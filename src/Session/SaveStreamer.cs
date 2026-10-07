using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Artitas;
using Artitas.Events;
using Code.Screens.Common.Systems;
using Common;
using Common.Components;
using Common.Content;
using Common.Content.DataStructures;
using Common.Content.Descriptors;
using Common.Content.Descriptors.SaveParameters;
using Common.Util;
using FlyingWormConsole3.LiteNetLib;
using HarmonyLib;
using Xenonauts;
using Xenonauts.Common;
using Xenonauts.Common.Components;
using Xenonauts.Common.Events;
using Xenonauts.Common.GlobalVariables;
using Xenonauts.Common.Systems;
using Xenonauts.Strategy.Components.MainBase;
using Xenonauts.Strategy.Systems;
using X2Coop.Mirror;
using X2Coop.Net;
using X2Coop.Runtime;

namespace X2Coop.Session
{
    /// <summary>
    /// T3.2 SaveStreamer (host side, spec section 6 "accept -> freeze ->
    /// snapshot -> stream"). Serializes the live StrategyWorld into a SaveGame
    /// and streams it to the joining guest as SaveOffer + SaveChunk messages.
    ///
    /// Flow:
    ///   1. SessionHost.HandleHello accept calls <see cref="QueueOnAccept"/>:
    ///      HostJoinFreeze is already latched (SessionHost), we snapshot
    ///      <see cref="MirrorState.BaselineSeq"/> = MirrorState.LastDeltaSeq
    ///      (0 until T5.1 exists - "first live delta = baselineSeq + 1"), and
    ///      queue the send for <see cref="OfferDelayMs"/> later. The delay
    ///      lets the HelloAck leave the wire first: channels do not order
    ///      across each other, and the guest codec drops a SaveOffer whose
    ///      sessionId it has not adopted yet (HelloAck carries the sid).
    ///   2. <see cref="Tick"/> (pumped from SessionHost.Tick -> Runner.Update,
    ///      main thread - same thread the world ticks on) fires
    ///      <see cref="SendOffer"/>, a mirror of
    ///      SaveGameSystem.InternalProcessSaveGameRequest
    ///      (SaveGameSystem.cs:401-570):
    ///      SAVE_PROFILER Reset/Start -> PreSaveGameReport ->
    ///      SerializationRequestCommand inside AssetDescriptor.
    ///      EnterSaveSerialization() (State left unset: HandleSerialization
    ///      calls CopyWorldIntoWorldState itself, R8-proven) -> SaveGame with
    ///      the same fields (version/versionName/versionDate/created, 4 commit
    ///      hashes, activeContentPacks, worldData, group) ->
    ///      ContentManager.Save(JsonPrinterSaveParameters compress:true,
    ///      overwrite:true, cacheAsset:false) to
    ///      $PROF\x2coop\tmp\&lt;sessionId-N&gt;.json (OUTSIDE Folders.Save() -
    ///      the Saves tree is untouched) -> PostSaveGameReport.
    ///   3. Left out on purpose: the SaveGameLimit eviction loop (no files are
    ///      written under Saves\, so nothing can be evicted - the T3.2 verify
    ///      diff proves it), the Ironman hidden copy, and the screenshot
    ///      (SaveGame.screenshot stays Optional.None - not needed for load,
    ///      avoids ImageConversionModule; map/gcParameters stay None exactly
    ///      as the real path leaves them on Strategy).
    ///   4. The file bytes are read back, SHA-256'd, and sent: one SaveOffer
    ///      then SaveChunks of &lt;=64 KiB on ChannelSave/ReliableOrdered.
    ///      SPEC DEVIATION (MODLOG): spec section 6 lists "chunk[64 KiB zlib]"
    ///      - the JsonPrinterSaveParameters(compress:true) output is already
    ///      zlib-compressed, so we stream the raw file bytes and do NOT
    ///      compress again (re-zipping a zlib stream buys nothing and T3.3
    ///      would have to unwrap twice before ContentManager could read it).
    ///   5. Freeze lifecycle: HostJoinFreeze releases exactly once - on
    ///      GuestReady (SessionHost.OnMessage) or on abort/disconnect/
    ///      teardown (SessionHost.ClearGuest). While latched, Tick logs a
    ///      "join-freeze geodate=..." heartbeat so the frozen clock is
    ///      provable from the host log.
    ///
    /// Private-field mirrors: the real path reads saveSys._globalVariableSystem
    /// (created), .difficultyConfigSystem (difficulty segment) and
    /// .campaignLogicSystem (ironman flag, logged only) - protected/private
    /// instance fields read here via AccessTools.Field with a
    /// world.GetSystem&lt;T&gt;() fallback (SaveGameSystem.Initialize binds them
    /// from the same world, so the fallback is equivalent).
    ///
    /// Failure policy: any abort unfreezes the clock and logs WARN, but keeps
    /// the session slot - the guest peer stays connected (its un-synced
    /// messages codec-drop) and a disconnect/rejoin retries the whole stream.
    /// </summary>
    public static class SaveStreamer
    {
        /// <summary>Chunk payload cap = wire max (envelope keeps 64KiB + ~29B under MaxMessageSize=65792).</summary>
        private const int ChunkBytes = 64 * 1024;

        /// <summary>
        /// Delay between HelloAck and SaveOffer (ms). The two ride different
        /// LiteNetLib channels, so there is no cross-channel ordering; this
        /// gives the ack a head start so the guest adopts the session id
        /// before the offer lands. T3.3 owns the real fix (buffer-until-sid
        /// or a sid-less offer); the delay shrinks the race to ~zero on
        /// loopback. Dev knob: x2coop.offerDelayMs.
        /// </summary>
        private const int DefaultOfferDelayMs = 150;

        /// <summary>Join-freeze geodate heartbeat interval (x2coop.joinFreezeLogMs).</summary>
        private const int DefaultFreezeLogMs = 5000;

        /// <summary>
        /// Max wait for the Strategy world to finish populating after accept
        /// (x2coop.offerReadyTimeoutMs). ScreenGuard goes on-strategy at
        /// OnWorldCreate, but a load keeps streaming entities in over the next
        /// frames - serializing inside that window produced a 17 KB save with
        /// no campaign/clock entities in the T3.2 live verify. The deferred
        /// offer waits for <see cref="WorldReady"/> and aborts on timeout.
        /// </summary>
        private const int DefaultReadyTimeoutMs = 15000;

        // SaveGameSystem private/protected fields the mirrored path consumes.
        private static readonly FieldInfo F_Globals =
            AccessTools.Field(typeof(SaveGameSystem), "_globalVariableSystem");
        private static readonly FieldInfo F_Diff =
            AccessTools.Field(typeof(SaveGameSystem), "difficultyConfigSystem");
        private static readonly FieldInfo F_CampaignLogic =
            AccessTools.Field(typeof(SaveGameSystem), "campaignLogicSystem");

        /// <summary>
        /// T5.2-r1 (review C2) join-ready watchdog bound, ms
        /// (x2coop.joinReadyTimeoutMs, 0 = off). Arms at QueueOnAccept on
        /// EVERY stream (first offer AND every forced re-offer) and is gated
        /// on HostJoinFreeze itself, so GuestReady / abort / strategy-left /
        /// disconnect all end it implicitly. A guest that wedged mid-load
        /// (the T5.2 cleanup-marker bug) or died mid-stream used to hold the
        /// host's clock frozen forever; 60 s is comfortably past the worst
        /// observed load (~10 s) plus the guest's own resync retries.
        /// </summary>
        private const int DefaultJoinReadyTimeoutMs = 60000;

        private static Runner _runner;
        private static int _peerId = -1;
        private static Guid _sessionId;
        private static bool _pending;
        private static bool _waitedReady;
        private static bool _completed;
        private static readonly Stopwatch _pendingTimer = new Stopwatch();
        private static readonly Stopwatch _freezeLogTimer = new Stopwatch();
        private static readonly Stopwatch _joinWatch = new Stopwatch(); // C2 watchdog

        /// <summary>
        /// True once a full offer+chunk stream went out for the current
        /// session (cleared by <see cref="Reset"/>/<see cref="QueueOnAccept"/>).
        /// SessionHost's re-handshake path re-queues the stream only when this
        /// is false - a completed stream needs no re-send (ReliableOrdered
        /// delivers or the peer dies; deeper recovery is T3.6 ResyncReq).
        /// </summary>
        public static bool StreamCompleted => _completed;

        /// <summary>
        /// Called from SessionHost's accept path AFTER sid assignment, the
        /// freeze latch and the HelloAck send. Snapshots baselineSeq and
        /// queues the deferred stream.
        /// </summary>
        public static void QueueOnAccept(Runner runner, int peerId, Guid sid)
        {
            try
            {
                Reset("re-queue");
                // T3.5 (fold of the T3.2 deferred): re-latch the join freeze on
                // EVERY queue, not just the first accept. A re-Hello/ResyncReq
                // re-stream after an abort had released the freeze would
                // otherwise snapshot+stream while the host clock runs free -
                // the re-latch is released by the same exactly-once rule
                // (GuestReady or abort/disconnect/teardown), and post-Ready it
                // is bounded by the guest's mirrorLoadTimeoutMs.
                if (!MirrorState.HostJoinFreeze)
                {
                    MirrorState.HostJoinFreeze = true;
                    Log.Info("join-freeze re-latched (stream re-queue)");
                }
                MirrorState.BaselineSeq = MirrorState.LastDeltaSeq; // snapshot of the delta stream head
                // T5.2: a new baseline makes every previously buffered
                // join-window delta stale - drop it (and an overflow latch)
                // before this stream's deltas start collecting.
                try { Replication.DeltaSender.OnStreamQueued(); }
                catch (Exception ex) { Log.Exception("delta stream-queued", ex); }
                _runner = runner;
                _peerId = peerId;
                _sessionId = sid;
                _pending = true;
                _waitedReady = false;
                _completed = false;
                _pendingTimer.Restart();
                _freezeLogTimer.Restart();
                _joinWatch.Restart(); // C2: this stream's GuestReady deadline
                int delay = DebugSettings.GetInt("offerDelayMs", DefaultOfferDelayMs);
                Log.Info("save streamer: offer queued peer=" + peerId
                    + " sid=" + MsgCodec.ShortId(sid)
                    + " baselineSeq=" + MirrorState.BaselineSeq
                    + " delay=" + delay + "ms"
                    + " geodate=" + FmtDate(Geodate(ScreenGuard.StrategyWorld)));

                // Dev knob (x2coop.forceSpeedOnJoinFreeze=0..3): scripted
                // "host clicks Play mid-join" - fires the same
                // TimeControlCommand.SetSpeedTo the geoscape speed buttons
                // send. With the freeze latched the tick prefix must still
                // keep the clock static; the heartbeat proves it.
                int spd = DebugSettings.GetInt("forceSpeedOnJoinFreeze", -1);
                if (spd >= 0)
                {
                    FireSetSpeed(spd);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save streamer queue", ex);
            }
        }

        /// <summary>Drop any pending stream state (guest left / teardown / abort).</summary>
        public static void Reset(string why)
        {
            if (_pending)
            {
                Log.Info("save streamer: pending offer dropped (" + (why ?? "?") + ")");
            }
            _pending = false;
            _waitedReady = false;
            _completed = false;
            _runner = null;
            _peerId = -1;
            // T3.6-r1 (review item 5): delete this session's staging file so
            // x2coop\tmp can't accumulate per-session litter inside a process
            // lifetime. Safe mid-session: a completed stream already read the
            // bytes into memory, and a re-queue rewrites the same name in
            // SendOffer anyway. Teardown's dir sweep stays the crash path.
            DeleteTmpFile(_sessionId);
            _sessionId = Guid.Empty;
            _pendingTimer.Reset();
            _joinWatch.Reset();
        }

        /// <summary>Best-effort delete of $PROF\x2coop\tmp\&lt;sid&gt;.json.</summary>
        private static void DeleteTmpFile(Guid sid)
        {
            try
            {
                if (sid == Guid.Empty) { return; }
                string path = PathUtility.Combine(Constants.Folders.Local(),
                    "x2coop/tmp/" + sid.ToString("N") + ".json");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log.Info("save streamer: tmp deleted " + path);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save streamer tmp-delete", ex);
            }
        }

        /// <summary>
        /// Campaign entity + a real geodate = the load finished streaming
        /// entities into the Strategy world (both are absent mid-load).
        /// </summary>
        private static bool WorldReady(World w)
        {
            try
            {
                return w != null
                    && w.FindTagged<CampaignTag>() != null
                    && Geodate(w) != default(DateTime);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>SessionHost.Tick pump: fires the deferred offer + freeze heartbeat.</summary>
        public static void Tick()
        {
            try
            {
                if (_pending)
                {
                    int delay = DebugSettings.GetInt("offerDelayMs", DefaultOfferDelayMs);
                    if (_pendingTimer.ElapsedMilliseconds >= delay)
                    {
                        // The Hello retry can accept while a save is still
                        // loading its entities into the fresh StrategyWorld;
                        // wait for the campaign/clock entities (T3.2 verify:
                        // mid-load serialization -> 17 KB empty save).
                        if (WorldReady(ScreenGuard.StrategyWorld))
                        {
                            _pending = false;
                            SendOffer();
                        }
                        else
                        {
                            if (!_waitedReady)
                            {
                                _waitedReady = true;
                                Log.Info("save streamer: world not populated yet - holding offer (sid="
                                    + MsgCodec.ShortId(_sessionId) + ")");
                            }
                            int timeout = DebugSettings.GetInt("offerReadyTimeoutMs", DefaultReadyTimeoutMs);
                            if (_pendingTimer.ElapsedMilliseconds >= timeout)
                            {
                                _pending = false;
                                Abort("strategy world not populated after " + timeout + "ms", unfreeze: true);
                            }
                        }
                    }
                }
                if (MirrorState.HostJoinFreeze)
                {
                    // T5.2-r1 (review C2): a guest that never sends GuestReady
                    // (wedged mid-load, dead mid-stream, or a black-holed
                    // offer) must not hold the host clock forever. Drop the
                    // stalled peer - ClearGuest inside DropStalledGuest
                    // releases the freeze and resets streamer/delta state.
                    int wd = DebugSettings.GetInt("joinReadyTimeoutMs", DefaultJoinReadyTimeoutMs);
                    if (wd > 0 && _joinWatch.IsRunning
                        && _joinWatch.ElapsedMilliseconds >= wd)
                    {
                        Log.Warn("join-watchdog: no GuestReady after " + wd + "ms"
                            + " peer=" + _peerId
                            + " sid=" + MsgCodec.ShortId(_sessionId)
                            + " - dropping stalled guest (host clock resumes)");
                        SessionHost.DropStalledGuest("join-ready watchdog timeout (" + wd + "ms)");
                        return; // freeze released inside DropStalledGuest
                    }
                    int every = DebugSettings.GetInt("joinFreezeLogMs", DefaultFreezeLogMs);
                    if (every > 0 && _freezeLogTimer.ElapsedMilliseconds >= every)
                    {
                        _freezeLogTimer.Restart();
                        Log.Info("join-freeze geodate="
                            + FmtDate(Geodate(ScreenGuard.StrategyWorld))
                            + " (clock held)");
                    }
                }
                else if (SessionHost.HasGuest)
                {
                    // T3.4: post-release session heartbeat - the guest's
                    // mirror clock is held by ClockFreeze while the host's
                    // runs free; the paired "mirror-freeze geodate=" lines
                    // (role=host here, role=guest in SaveReceiver) make the
                    // divergence provable from the two logs alone. Same
                    // x2coop.joinFreezeLogMs cadence knob (0 = off).
                    int every = DebugSettings.GetInt("joinFreezeLogMs", DefaultFreezeLogMs);
                    if (every > 0 && _freezeLogTimer.ElapsedMilliseconds >= every)
                    {
                        _freezeLogTimer.Restart();
                        Log.Info("mirror-freeze geodate="
                            + FmtDate(Geodate(ScreenGuard.StrategyWorld))
                            + " role=host (running)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save streamer tick", ex);
            }
        }

        // ---------------------------------------------------------------

        /// <summary>The InternalProcessSaveGameRequest mirror + offer/chunk send.</summary>
        private static void SendOffer()
        {
            Stopwatch total = Stopwatch.StartNew();
            Runner runner = _runner;
            NetTransport t = runner != null ? runner.Transport : null;
            int peerId = _peerId;
            Guid sid = _sessionId;
            try
            {
                if (t == null || t.CurrentRole != NetTransport.Role.Host
                    || peerId < 0 || !t.IsConnected(peerId)
                    || runner.SessionId != sid || sid == Guid.Empty)
                {
                    Abort("stale session/peer at offer time", unfreeze: true);
                    return;
                }
                World world = ScreenGuard.StrategyWorld;
                if (world == null)
                {
                    Abort("no live StrategyWorld to serialize", unfreeze: true);
                    return;
                }
                SaveGameSystem saveSys = world.GetSystem<SaveGameSystem>();
                if (saveSys == null)
                {
                    Abort("no SaveGameSystem on StrategyWorld", unfreeze: true);
                    return;
                }
                // T5.2-r1 (review C1) reproduction knob: seed marker-carrying
                // entities into the host world so the save deterministically
                // streams MarkedForCleanupGroup/MarkedForDeletionGroup
                // members - the exact input that wedged the pre-fix guest.
                DebugMarkCleanup(world);

                // --- mirror :403-424 (profiler + Pre-report + serialize) -----
                SaveGameSystem.SAVE_PROFILER.Reset();
                SaveGameSystem.SAVE_PROFILER.Start("Handle Pre Save Report");
                world.HandleEvent(new PreSaveGameReport());
                SaveGameSystem.SAVE_PROFILER.Mark("Serialize World");
                var cmd = new SerializationRequestCommand(); // State unset -> CopyWorldIntoWorldState
                using (AssetDescriptor.EnterSaveSerialization())
                {
                    world.HandleEvent(cmd);
                }
                if (!cmd.Result.Succeeded || cmd.Data == null || cmd.Data.IsNull)
                {
                    Abort("serialize failed: " + (cmd.Result.Succeeded
                        ? "empty data"
                        : cmd.Result.FormattedMessages), unfreeze: true);
                    return;
                }
                // T5.2-r1 (review I-2): the snapshot just fixed the guest's
                // ground truth. Barrier out every delta captured BEFORE it
                // (offer-delay/world-ready ops were already folded) and
                // re-snapshot BaselineSeq so only post-snapshot seqs stream.
                try { Replication.DeltaSender.OnSaveSerialized(); }
                catch (Exception ex) { Log.Exception("delta save-barrier", ex); }

                // --- mirror :425-446 (metadata + group) ----------------------
                SaveGameSystem.SAVE_PROFILER.Mark("Gather SaveGame MetaData");
                Dictionary<string, string> inject = SafeInjectTable(saveSys);
                DateTime now = DateTime.Now;
                DateTime geoDate = Geodate(world);
                string diffName = DifficultyName(saveSys, world);
                TimeSpan playTime = PlayTime(world, out Entity campaign);
                Semver.SemVersion created = CreatedVersion(saveSys, world);
                Guid campaignId = CampaignGuid(campaign);
                CampaignLogicSystem logic = ResolveCampaignLogic(saveSys, world);
                // Entity.Guid() is empty on saves that lack the Guid
                // component (r3seedday1 verifies this); the campaign NAME is
                // the practical identity - it also rides the wire as
                // SaveOffer.CampaignName (T3.2 review fix: filename fallback).
                string campaignName = SafeString(() => saveSys.GetCampaignName(), string.Empty);
                Log.Info("save streamer: serialize ok geodate=" + FmtDate(geoDate)
                    + " campaignId=" + MsgCodec.ShortId(campaignId)
                    + " campaign=\"" + campaignName + "\""
                    + " playTime=" + playTime
                    + " ironman=" + SafeString(() => logic != null && logic.IsIronManMode ? "true" : "false", "?")
                    + " diff=" + diffName);

                // "group cloned from the campaign's group": the USER save group
                // (carries CampaignNameInformationSegment - resolved through the
                // injection table's CAMPAIGN_NAME, i.e. the campaign's own
                // directory) cloned via the copy ctor so the shared static is
                // never mutated, + the save-name tag + the GameScreens segment
                // LoadGame requires (SaveGameSystem.cs:765), then the same four
                // metadata prefixes as :444 and ResolveIdentity.
                SaveGameGroup grp = new SaveGameGroup(XenonautsConstants.SaveGameGroups.USER)
                    .Suffix(new SaveGameGroup.StringInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.Identity
                        | SaveGameGroup.InformationSegment.InformationType.Tag,
                        "x2coop"))
                    .Suffix(new SaveGameGroup.GameScreensInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None,
                        GameScreens.Strategy));
                SaveGameGroup resolved = grp
                    .Prefix(new SaveGameGroup.SystemDateTimeInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None, now))
                    .Prefix(new SaveGameGroup.GeoscapeDateTimeInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None, geoDate))
                    .Prefix(new SaveGameGroup.PlayTimeInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None, playTime))
                    .Prefix(new SaveGameGroup.DifficultyInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None, diffName))
                    .ResolveIdentity(inject);

                // --- mirror :491-511 (SaveGame asset; screenshot/map/gcParameters
                //     stay at their Optional.None defaults) ------------------
                SaveGameSystem.SAVE_PROFILER.Mark("Create SaveGame");
                var asset = new SaveGame
                {
                    name = "x2coop-" + MsgCodec.ShortId(sid),
                    group = resolved,
                    // displayInject left EMPTY on purpose (T3.2 review fix):
                    // GetDisplayName() prefers displayInject over name, and
                    // BuildLocalizedDisplay only renders Display-flagged
                    // segments - our group carries none (all metadata
                    // prefixes are InformationType.None), so a non-empty
                    // inject table produced "". Empty -> name wins.
                    displayInject = new Dictionary<string, string>(),
                    version = XenonautsConstants.Versioning.VERSION,
                    versionName = XenonautsConstants.Versioning.VERSION_NAME,
                    versionDate = XenonautsConstants.Versioning.VERSION_DATE,
                    created = created,
                    artitasCommitHash = Commits.Instance.ArtitasCommitHash,
                    assetsCommitHash = Commits.Instance.AssetsCommitHash,
                    commonCommitHash = Commits.Instance.CommonCommitHash,
                    x2CoreCommitHash = Commits.Instance.X2CoreCommitHash,
                    activeContentPacks = ContentPackDependencyResolver.BuildStamps(
                        AssetReference.ContentManager.GetConfig().GetAllActiveContentPacks()
                            .Select(p => p.Manifest.Get())),
                    worldData = cmd.Data,
                };

                // --- mirror :512-538 (write; custom FileDescriptor outside
                //     Saves\ so the SaveGameLimit eviction is N/A) ------------
                string path = PathUtility.Combine(Constants.Folders.Local(),
                    "x2coop/tmp/" + sid.ToString("N") + ".json");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                }
                catch (Exception ex)
                {
                    Abort("tmp dir create failed: " + ex.GetType().Name, unfreeze: true);
                    return;
                }
                AssetState st;
                SaveGameSystem.SAVE_PROFILER.Mark("Save Asset ");
                using (AssetDescriptor.EnterSaveSerialization())
                {
                    st = AssetReference.ContentManager.Save(asset,
                        new FileDescriptor(path, typeof(SaveGame)),
                        new JsonPrinterSaveParameters(compress: true),
                        overwrite: true,
                        cacheAsset: false);
                }
                if ((st & AssetState.Failure) != 0 || !File.Exists(path))
                {
                    Abort("ContentManager.Save -> " + st + " exists=" + File.Exists(path)
                        + " path=" + path, unfreeze: true);
                    return;
                }

                // --- mirror :564-566 (post report) ---------------------------
                SaveGameSystem.SAVE_PROFILER.Mark("Handle Post Save Report");
                world.HandleEvent(new PostSaveGameReport());
                SaveGameSystem.SAVE_PROFILER.Stop();

                // --- bytes -> sha256 -> SaveOffer + SaveChunks ---------------
                byte[] bytes = File.ReadAllBytes(path);
                byte[] sha;
                using (SHA256 h = SHA256.Create()) { sha = h.ComputeHash(bytes); }

                var offer = new SaveOfferMsg
                {
                    SessionId = sid,
                    OfferSessionId = sid,
                    CampaignId = campaignId,
                    CampaignName = campaignName ?? string.Empty,
                    BaselineSeq = MirrorState.BaselineSeq,
                    ByteCount = bytes.LongLength,
                    Sha256 = sha,
                };
                byte[] offerWire = MsgCodec.Encode(offer);
                if (offerWire == null
                    || !t.Send(peerId, MsgCodec.ChannelFor(MsgType.SaveOffer),
                        offerWire, 0, offerWire.Length, DeliveryMethod.ReliableOrdered))
                {
                    Abort("SaveOffer send failed", unfreeze: false); // send fail = peer dying; disconnect cleans up
                    return;
                }
                int chunks = 0;
                long off = 0;
                while (off < bytes.LongLength)
                {
                    int n = (int)Math.Min(ChunkBytes, bytes.LongLength - off);
                    var data = new byte[n];
                    Buffer.BlockCopy(bytes, (int)off, data, 0, n);
                    byte[] wire = MsgCodec.Encode(new SaveChunkMsg
                    {
                        SessionId = sid,
                        Offset = off,
                        Data = data,
                    });
                    if (wire == null
                        || !t.Send(peerId, MsgCodec.ChannelFor(MsgType.SaveChunk),
                            wire, 0, wire.Length, DeliveryMethod.ReliableOrdered))
                    {
                        Abort("SaveChunk send failed at off=" + off, unfreeze: false);
                        return;
                    }
                    off += n;
                    chunks++;
                }
                _completed = true; // whole stream enqueued reliable-ordered
                Log.Info("save streamer: save bytes=" + bytes.LongLength
                    + " sha=" + BitConverter.ToString(sha).Replace("-", string.Empty).ToLowerInvariant()
                    + " ms=" + total.ElapsedMilliseconds
                    + " chunks=" + chunks
                    + " sid=" + MsgCodec.ShortId(sid)
                    + " tmp=" + path);
            }
            catch (Exception ex)
            {
                Log.Exception("save streamer offer", ex);
                Abort("exception " + ex.GetType().Name, unfreeze: true);
            }
            finally
            {
                try { SaveGameSystem.SAVE_PROFILER.StopAll(); } catch { }
            }
        }

        /// <summary>Abandon the stream: log + (optionally) release the join freeze.</summary>
        private static void Abort(string why, bool unfreeze)
        {
            Log.Warn("save streamer: aborted - " + why
                + " (guest left in-session; disconnect/rejoin retries)");
            if (unfreeze && MirrorState.HostJoinFreeze)
            {
                MirrorState.HostJoinFreeze = false;
                Log.Info("join-freeze released (stream abort)");
            }
        }

        // --- field/system readers (AccessTools.Field first, GetSystem fallback) --

        private static object Field(FieldInfo f, SaveGameSystem sys)
        {
            try { return f != null && sys != null ? f.GetValue(sys) : null; }
            catch { return null; }
        }

        private static string DifficultyName(SaveGameSystem saveSys, World world)
        {
            return SafeString(() =>
            {
                var diff = Field(F_Diff, saveSys) as DifficultyConfigSystem;
                if (diff == null) { diff = world.GetSystem<DifficultyConfigSystem>(); }
                return diff?.DifficultyConfig?.Name()?.value;
            }, "unknown");
        }

        private static Semver.SemVersion CreatedVersion(SaveGameSystem saveSys, World world)
        {
            try
            {
                var gvs = Field(F_Globals, saveSys) as GlobalVariableSystem;
                if (gvs == null) { gvs = world.GetSystem<GlobalVariableSystem>(); }
                Semver.SemVersion v = gvs?.Get<VersionComponent>(
                    XenonautsConstants.GlobalVariables.CREATED_VERSION);
                return v ?? new Semver.SemVersion(0);
            }
            catch
            {
                return new Semver.SemVersion(0);
            }
        }

        private static CampaignLogicSystem ResolveCampaignLogic(SaveGameSystem saveSys, World world)
        {
            var logic = Field(F_CampaignLogic, saveSys) as CampaignLogicSystem;
            return logic ?? world.GetSystem<CampaignLogicSystem>();
        }

        private static TimeSpan PlayTime(World world, out Entity campaign)
        {
            campaign = null;
            try
            {
                campaign = world?.FindTagged<CampaignTag>();
                if (campaign == null) { return TimeSpan.Zero; }
                return campaign.CampaignPlayTime()?.value ?? TimeSpan.Zero;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        private static Guid CampaignGuid(Entity campaign)
        {
            try
            {
                return campaign?.Guid()?.value ?? Guid.Empty;
            }
            catch
            {
                return Guid.Empty;
            }
        }

        private static Dictionary<string, string> SafeInjectTable(SaveGameSystem sys)
        {
            try
            {
                return sys.GetSaveGameInjectionTable() ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private static DateTime Geodate(World w)
        {
            try
            {
                Entity clock = w?.FindTagged<GeoscapeClockTag>();
                if (clock != null)
                {
                    return clock.GeoscapeDateTime().value;
                }
            }
            catch
            {
            }
            return default(DateTime);
        }

        /// <summary>
        /// T5.2-r1 reproduction knob (x2coop.debugMarkCleanup=N): create N
        /// empty entities that carry ONLY marker groups - odd ones join
        /// MarkedForCleanupGroup, even ones join MarkedForCleanupGroup +
        /// MarkedForDeletionGroup. The groups serialize into the streamed
        /// save (the T5.2 live wedge proved they round-trip), MirrorSystems
        /// disarms the guest-side passes, and the host's own next cleanup
        /// tick self-disposes them (empty entities - no inventory/squadron/
        /// activity semantics to corrupt). Off by default; dev-only.
        /// </summary>
        private static void DebugMarkCleanup(World world)
        {
            try
            {
                int n = DebugSettings.GetInt("debugMarkCleanup", 0);
                if (n <= 0 || world == null) { return; }
                if (n > 64) { n = 64; } // dev knob - keep the payload sane
                int markedC = 0, markedD = 0;
                for (int i = 0; i < n; i++)
                {
                    Entity e = world.CreateEntity();
                    if (e == null) { continue; }
                    e.JoinMarkedForCleanup();
                    markedC++;
                    if ((i & 1) == 1)
                    {
                        e.JoinMarkedForDeletion();
                        markedD++;
                    }
                }
                Log.Info("save streamer: dev debugMarkCleanup entities=" + n
                    + " markedCleanup=" + markedC + " markedDeletion=" + markedD
                    + " (guest activation must not wedge)");
            }
            catch (Exception ex)
            {
                Log.Exception("save streamer debug-mark", ex);
            }
        }

        private static void FireSetSpeed(int speed)
        {
            try
            {
                World w = ScreenGuard.StrategyWorld;
                if (w == null) { return; }
                if (speed > 3) { speed = 3; }
                // Same event the geoscape speed buttons send
                // (TimeControlCommand.SetSpeedTo) - the scripted "Play click".
                w.HandleEvent(TimeControlCommand.SetSpeedTo(speed));
                Log.Info("save streamer: dev forceSpeedOnJoinFreeze=" + speed + " fired (clock must stay frozen)");
            }
            catch (Exception ex)
            {
                Log.Exception("save streamer force-speed", ex);
            }
        }

        private static string SafeString(Func<string> f, string fallback)
        {
            try { return f() ?? fallback; }
            catch { return fallback; }
        }

        private static string FmtDate(DateTime dt)
        {
            return dt == default(DateTime) ? "?" : dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
