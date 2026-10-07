using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Artitas;
using Code.Screens.Common.Systems;
using Common;
using Common.Content.DataStructures;
using Common.Content.Descriptors;
using Common.Content.Lifecycle;
using Common.Util;
using FlyingWormConsole3.LiteNetLib;
using Xenonauts.Strategy.Components.MainBase;
using Xenonauts.Strategy.Systems;
using X2Coop.Mirror;
using X2Coop.Net;
using X2Coop.Runtime;

namespace X2Coop.Session
{
    /// <summary>
    /// T3.3 guest-side save receiver (spec section 6): reassembles the host's
    /// SaveOffer + SaveChunk stream, verifies SHA-256, writes the file under
    /// Saves\x2coop_guest\ and loads it via the game's own load path.
    ///
    /// Flow:
    ///   1. <see cref="OnSaveOffer"/> arms a byte[ByteCount] buffer. Chunks
    ///      arrive ReliableOrdered on ChannelSave in offset order; each is
    ///      bounds-checked and BlockCopy'd into place. Completion = received
    ///      bytes reaching ByteCount.
    ///   2. SHA-256 over the reassembled bytes must equal SaveOffer.Sha256.
    ///      On mismatch -&gt; ResyncReq{SaveLoadFailed} (the host's minimal
    ///      handler re-streams; budget-capped, see FailLoad). Bytes are
    ///      ALREADY zlib-compressed (host's JsonPrinterSaveParameters
    ///      compress:true, T3.2 deviation note) - written to disk verbatim.
    ///   3. Write to Saves\x2coop_guest\&lt;stem&gt;-&lt;n&gt;.json (stem rule
    ///      in SaveFileNamer; n = per-session counter). MirrorMode =
    ///      PendingActivate, then
    ///      SaveGameSystem.LoadGame(new FileDescriptor(path, typeof(SaveGame)),
    ///      resetRNGSeed:false) - R8-proven: any resolvable path loads; false
    ///      keeps the serialized RNG state identical to the host's (the UI
    ///      load path uses Constants.ReseedSaveOnLoad - deliberately not
    ///      mirrored).
    ///   4. LoadGame queues RequestMoveTo(Strategy) -&gt; the load screen
    ///      builds a NEW "StrategyWorld" -&gt; lifecycle OnWorldCreate fires
    ///      (StrategyScreen.cs:212) while entities stream in over the next
    ///      frames (DeserializeSaveGameCommand is queued behind world
    ///      Initialize). <see cref="OnWorldCreate"/> binds that world ONLY
    ///      while MirrorMode == PendingActivate - the pending flag is the
    ///      "this create is our mirror load" discriminator vs a guest-local
    ///      campaign load.
    ///   5. <see cref="Tick"/> waits one frame past create AND until the world
    ///      is populated (Initialized + GeoscapeClockTag entity with a real
    ///      GeoscapeDateTime - the host-side WorldReady equivalent), then sets
    ///      MirrorMode.Active, logs "mirror active: geodate=..." (the verify
    ///      pairs this with the host's "join-freeze geodate=" line), and sends
    ///      GuestReady{Ready} - the host's freeze-release trigger (and its
    ///      SessionNotice{Resynced} reply boundary, T3.6).
    ///
    /// Failure policy (bounded): any post-write failure (write threw, load
    /// timeout, world died pre-activation) sends a ResyncReq{SaveLoadFailed}
    /// -&gt; the host re-streams and the whole flow retries, up to
    /// MaxResyncAttempts per activation cycle. Past the budget,
    /// GuestReady{LoadFailed|WorldInitFailed} goes out so the host's join
    /// freeze still releases (a broken guest must never wedge the host
    /// clock); the guest stays in-session in MirrorMode.Detached and a
    /// Resync press / disconnect-rejoin restarts the flow.
    ///
    /// T3.6 resync flow: SessionClient.RequestResync (Resync button,
    /// HostReturned, resyncMinutes timer) -&gt; ResyncReq -&gt; the host
    /// re-runs its streamer with a new baselineSeq -&gt; a fresh SaveOffer
    /// re-enters at step 1 with MirrorMode.Receiving (suppression armed for
    /// the whole receive window) -&gt; GuestReady -&gt; SessionNotice{Resynced}.
    ///
    /// Session end (<see cref="Reset"/>, guest disconnect / HostEnded / pack
    /// teardown): drops the buffer, Mode -&gt; Off (the mirror world becomes a
    /// local sandbox - its clock resumes; the T3.4 ClockFreeze gate only
    /// holds while Mode == Active and Tick keeps sampling the freed world's
    /// geodate for a few seconds as resume evidence), and
    /// deletes Saves\x2coop_guest\ per spec "deleted on session end" - EXCEPT
    /// while a load is still in flight, because DeserializeSaveGameCommand
    /// re-reads the file at deserialize time (SaveGameSystem.cs:148-160);
    /// Teardown at quit still covers that case.
    /// </summary>
    internal static class SaveReceiver
    {
        /// <summary>Max accepted offer size - real compressed saves are ~200 KB; pure headroom vs a hostile/broken ByteCount alloc.</summary>
        private const long MaxOfferBytes = 256L * 1024 * 1024;

        /// <summary>
        /// ResyncReq sends per activation cycle on stream/load failure before
        /// giving up with GuestReady{LoadFailed}. T3.6 raised 1 -&gt; 2: the
        /// observed cold-process failure mode (first mirror load exceeding
        /// the 120 s mirrorLoadTimeoutMs) recovered on the very next try -
        /// one retry was one cold-start short of the budget. The counter
        /// resets on <see cref="Activate"/> (each successful mirror gets a
        /// fresh budget) and on externally-requested resyncs via
        /// <see cref="NoteExternalResync"/>; failure-driven retries never
        /// reset it, so a persistently-broken stream still bounds at 2.
        /// </summary>
        private const int MaxResyncAttempts = 2;

        /// <summary>LoadGame -> activated bound (x2coop.mirrorLoadTimeoutMs). Generous: load screens are slow.</summary>
        private const int DefaultLoadTimeoutMs = 120000;

        /// <summary>Mirror-freeze geodate heartbeat interval (x2coop.mirrorFreezeLogMs, T3.4).</summary>
        private const int DefaultMirrorFreezeLogMs = 5000;

        /// <summary>
        /// How long (ms) the heartbeat keeps sampling the RELEASED mirror
        /// world after Mode drops from Active (T3.4 release evidence: the
        /// un-gated clock visibly advancing in the guest log). The world may
        /// die sooner (dispose on quit-to-menu) - that ends it too.
        /// </summary>
        private const int ReleasedHeartbeatMs = 30000;

        private enum Phase : byte
        {
            Idle = 0,
            AwaitWorld = 1,   // LoadGame called; waiting for the new StrategyWorld create
            AwaitReady = 2,   // world created; >=1 frame + populated before GuestReady
        }

        private static long _expectBytes = -1;
        private static byte[] _buf;
        private static long _received;
        private static int _chunks;
        private static byte[] _sha;
        private static Guid _campaignId;
        private static string _campaignName;
        private static Guid _offerSid;
        private static int _fileCounter;
        private static int _resyncsSent;
        private static Phase _phase = Phase.Idle;
        private static readonly Stopwatch _phaseTimer = new Stopwatch();
        private static WeakReference<World> _mirrorWorld;
        private static int _framesLeft;
        private static bool _wroteFile;
        private static string _lastFile;
        // T3.4 heartbeat state: _releasedWorld keeps a weak ref to the world
        // that JUST stopped being the mirror (session end / fresh offer) so
        // Tick can keep sampling its geodate for ReleasedHeartbeatMs - proof
        // the un-gated clock resumes.
        private static WeakReference<World> _releasedWorld;
        private static readonly Stopwatch _releasedWatch = new Stopwatch();
        private static readonly Stopwatch _hbWatch = new Stopwatch();

        /// <summary>
        /// The live mirror world, or null. T3.4's clock-freeze prefix compares
        /// TimeTransitionSystem.World against this to scope the guest-side
        /// freeze.
        ///
        /// Resolves under PendingActivate as well as Active (T3.6): _mirrorWorld
        /// is null until OnWorldCreate binds the new load, so this never
        /// freezes the OUTGOING world - it only stops the fresh mirror's clock
        /// drifting during the create-&gt;populate window (observed: ~8 s of
        /// geodate gained while awaiting activation). The Active-&gt;populate
        /// boundary is exactly the T3.3 design; widening the freeze one mode
        /// earlier just makes "clocks match at resync" exact.
        /// </summary>
        public static World MirrorWorld
        {
            get
            {
                try
                {
                    return (MirrorState.Mode == MirrorMode.Active
                        || MirrorState.Mode == MirrorMode.PendingActivate)
                        ? Resolve(_mirrorWorld) : null;
                }
                catch { return null; }
            }
        }

        /// <summary>Guest saw a SaveOffer: log + arm the reassembly buffer.</summary>
        public static void OnSaveOffer(Runner runner, SaveOfferMsg offer)
        {
            NetTransport t = runner != null ? runner.Transport : null;
            if (t == null || t.CurrentRole != NetTransport.Role.Guest || offer == null) { return; }

            // A fresh offer restarts the flow - drop a half-received stream or
            // a pending mirror load (the new stream supersedes both; the
            // orphaned file is cleaned at session end).
            _expectBytes = offer.ByteCount;
            _received = 0;
            _chunks = 0;
            _sha = offer.Sha256;
            _campaignId = offer.CampaignId;
            _campaignName = offer.CampaignName;
            _offerSid = offer.OfferSessionId;
            _phase = Phase.Idle;
            // T3.4: a fresh offer while a mirror is Active releases the freeze
            // on the OLD world (the new load will replace it) - capture it for
            // the post-release heartbeat before the refs drop.
            if (MirrorState.Mode == MirrorMode.Active && _mirrorWorld != null)
            {
                _releasedWorld = _mirrorWorld;
                _releasedWatch.Restart();
                Log.Info("mirror-freeze released (new save offer) geodate="
                    + FmtDate(Geodate(Resolve(_releasedWorld))));
            }
            _mirrorWorld = null;
            _framesLeft = 0;
            _phaseTimer.Reset();
            // T3.6 (r2 of the T3.5 review): arm suppression the moment the
            // offer lands - MirrorMode.Receiving != Off keeps every
            // save/load/delete path blocked during the whole receive window
            // (previously Mode stayed Off until FinishStream, leaving a gap
            // where a guest-local load could start inside the window). The
            // bind gate still reads PendingActivate, so even a world-create
            // from a load started BEFORE the offer can never be claimed as
            // the mirror (that residual window is documented in MODLOG).
            MirrorState.Mode = MirrorMode.Receiving;
            string sha8 = offer.Sha256 != null && offer.Sha256.Length >= 8
                ? BitConverter.ToString(offer.Sha256, 0, 8).Replace("-", string.Empty).ToLowerInvariant()
                : "?";
            Log.Info("save receiver: offer bytes=" + offer.ByteCount
                + " sha=" + sha8 + "..."
                + " baselineSeq=" + offer.BaselineSeq
                + " campaignId=" + MsgCodec.ShortId(offer.CampaignId)
                + " campaign=\"" + (offer.CampaignName ?? string.Empty) + "\""
                + " sid=" + MsgCodec.ShortId(offer.OfferSessionId));
            if (_expectBytes <= 0 || _expectBytes > MaxOfferBytes)
            {
                Log.Warn("save receiver: offer byteCount=" + _expectBytes
                    + " out of bounds (max " + MaxOfferBytes + ") - refusing stream");
                _expectBytes = -1;
                _buf = null;
                MirrorState.Mode = MirrorMode.Detached; // stream refused; Resync rejoins
                // A re-stream resends the identical offer - pointless retry;
                // report failure directly so the host freeze still releases.
                SendGuestReady(runner, GuestReadyStatus.LoadFailed);
                return;
            }
            try
            {
                _buf = new byte[_expectBytes];
                // T5.3-r2: the baseline snapshot covers every seq <=
                // baselineSeq ("first live delta = baselineSeq + 1",
                // Messages.cs:410). Adopt the watermark now that the stream
                // is armed - without it the first post-baseline delta reads
                // as a one-seq gap and resync loops forever; and the
                // applier keeps post-snapshot deltas buffered mid-load
                // instead of dropping them at the activation rebind.
                Replication.DeltaSink.NoteBaselineSeq(offer.BaselineSeq);
                Replication.StateApplier.NoteBaselineSeq(offer.BaselineSeq);
            }
            catch (Exception ex)
            {
                Log.Exception("save receiver alloc", ex);
                _expectBytes = -1;
                MirrorState.Mode = MirrorMode.Detached;
                SendGuestReady(runner, GuestReadyStatus.LoadFailed);
            }
        }

        /// <summary>Reassemble one chunk; on completion verify SHA-256 then write + load.</summary>
        public static void OnSaveChunk(Runner runner, SaveChunkMsg chunk)
        {
            if (chunk == null || chunk.Data == null) { return; }
            if (_buf == null || _expectBytes <= 0)
            {
                Log.Warn("save receiver: chunk with no open offer (off=" + chunk.Offset
                    + " len=" + chunk.Data.Length + ") - dropped");
                return;
            }
            if (chunk.Offset < 0 || chunk.Offset + chunk.Data.Length > _expectBytes)
            {
                Log.Warn("save receiver: chunk out of bounds off=" + chunk.Offset
                    + " len=" + chunk.Data.Length + " expected=" + _expectBytes + " - dropped");
                return;
            }
            Buffer.BlockCopy(chunk.Data, 0, _buf, (int)chunk.Offset, chunk.Data.Length);
            _received += chunk.Data.Length;
            _chunks++;
            bool last = _received >= _expectBytes;
            if (_chunks == 1 || last || (_chunks % 32) == 0)
            {
                Log.Info("save receiver: chunk off=" + chunk.Offset
                    + " len=" + chunk.Data.Length
                    + " received=" + _received + "/" + _expectBytes);
            }
            if (last)
            {
                FinishStream(runner);
            }
        }

        /// <summary>
        /// Lifecycle hook (X2CoopLifecycle.OnWorldCreate, after ScreenGuard):
        /// while MirrorMode.PendingActivate, the next Section.Strategy world
        /// named "StrategyWorld" is the one our LoadGame spawned - bind it and
        /// start the populate wait. Every other mode refuses the bind, so a
        /// foreign create (a local load that snuck in before suppression
        /// armed, an autosave corner) can never be claimed as the mirror.
        /// </summary>
        public static void OnWorldCreate(IContentPackLifecycle.Section section, WeakReference<World> world)
        {
            try
            {
                if (section != IContentPackLifecycle.Section.Strategy) { return; }
                if (MirrorState.Mode != MirrorMode.PendingActivate) { return; }
                World w = Resolve(world);
                if (w == null || w.Name != "StrategyWorld")
                {
                    Log.Warn("save receiver: pending-activate saw unexpected strategy world name="
                        + (w != null ? (w.Name ?? "<null>") : "<dead>") + " - still waiting");
                    return;
                }
                _mirrorWorld = new WeakReference<World>(w);
                _framesLeft = 1; // spec: activate at create + one frame
                _phase = Phase.AwaitReady;
                _phaseTimer.Restart();
                Log.Info("save receiver: mirror world created world=" + w.GetHashCode()
                    + " - awaiting populate + 1 frame");
            }
            catch (Exception ex)
            {
                Log.Exception("save receiver world-create", ex);
            }
        }

        /// <summary>
        /// Per-frame pump (SessionClient.Tick &lt;- Runner.Update): drives the
        /// AwaitWorld/AwaitReady phases and their timeout.
        /// </summary>
        public static void Tick(Runner runner)
        {
            try
            {
                // T3.4: mirror-freeze geodate heartbeat - runs in EVERY phase
                // (incl. Idle): while Mode==Active the held clock is sampled,
                // and for ~ReleasedHeartbeatMs after release the freed world's
                // resume is sampled. Own cadence watch; near-free per frame.
                MirrorHeartbeat();
                // T3.6 belt-and-braces: the ACTIVE mirror world dying should
                // detach via OnWorldDispose; cover a missed event (a dispose
                // path that skipped the lifecycle hook) so a dead mirror can
                // never linger as Active.
                if (_phase == Phase.Idle && MirrorState.Mode == MirrorMode.Active)
                {
                    World mw = Resolve(_mirrorWorld);
                    if (mw == null || mw.Disposed)
                    {
                        DetachMirror("mirror world gone without a dispose event");
                    }
                }
                if (_phase == Phase.AwaitWorld)
                {
                    CheckLoadTimeout(runner, "no StrategyWorld create after LoadGame",
                        GuestReadyStatus.LoadFailed);
                    return;
                }
                if (_phase != Phase.AwaitReady) { return; }

                World w = Resolve(_mirrorWorld);
                if (w == null || w.Disposed)
                {
                    FailLoad(runner, GuestReadyStatus.WorldInitFailed,
                        "mirror world died before activation");
                    return;
                }
                if (_framesLeft > 0) { _framesLeft--; }
                if (_framesLeft == 0 && MirrorWorldReady(w))
                {
                    Activate(runner, w);
                    return;
                }
                CheckLoadTimeout(runner, "mirror world not populated after create",
                    GuestReadyStatus.WorldInitFailed);
            }
            catch (Exception ex)
            {
                Log.Exception("save receiver tick", ex);
            }
        }

        /// <summary>
        /// Drop all tracking (disconnect / HostEnded / detach). Mode -&gt; Off
        /// even mid-mirror so a loaded world reverts to local play (its clock
        /// resumes; the T3.4 freeze only gates on Active). Deletes
        /// Saves\x2coop_guest\ when we wrote a file - spec's "deleted on
        /// session end" - but never while a load is still in flight
        /// (DeserializeSaveGameCommand re-reads the file at deserialize time).
        /// </summary>
        public static void Reset()
        {
            bool had = _buf != null || _phase != Phase.Idle || MirrorState.Mode != MirrorMode.Off;
            bool loadInFlight = _phase != Phase.Idle;
            // T3.4: capture the outgoing mirror world BEFORE the refs drop -
            // the Tick heartbeat keeps sampling its geodate ~ReleasedHeartbeatMs
            // so the un-gated clock's resume is provable from the guest log.
            if (MirrorState.Mode == MirrorMode.Active && _mirrorWorld != null)
            {
                _releasedWorld = _mirrorWorld;
                _releasedWatch.Restart();
                Log.Info("mirror-freeze released (session end) geodate="
                    + FmtDate(Geodate(Resolve(_releasedWorld))));
            }
            _expectBytes = -1;
            _buf = null;
            _received = 0;
            _chunks = 0;
            _sha = null;
            _campaignId = Guid.Empty;
            _campaignName = null;
            _offerSid = Guid.Empty;
            _fileCounter = 0;
            _resyncsSent = 0;
            _phase = Phase.Idle;
            _phaseTimer.Reset();
            _mirrorWorld = null;
            _framesLeft = 0;
            _lastFile = null;
            if (MirrorState.Mode != MirrorMode.Off)
            {
                MirrorState.Mode = MirrorMode.Off;
                Log.Info("save receiver: MirrorMode=Off (session end)");
            }
            if (_wroteFile)
            {
                _wroteFile = false;
                if (loadInFlight)
                {
                    // The in-flight DeserializeSaveGameCommand would read a
                    // deleted file -> game error path. Leave it; process-level
                    // teardown still cleans the dir at quit / next session end.
                    Log.Info("save receiver: guest dir kept (load in flight; teardown cleans it)");
                }
                else
                {
                    Teardown.DeleteGuestSaveDir();
                }
            }
            // T4.1: per-session firewall state (block counters, rate-limit
            // map, guest-created ids, exception-wrap) is session-scoped like
            // the guest save dir. The log tap itself survives - Shutdown owns
            // it (pack teardown).
            Mirror.MirrorFirewall.Reset();
            // T4.2-r1: pick-gate counters are session-scoped the same way.
            Mirror.UIPickGate.Reset();
            // T5.2-r1: cleanup/convergence disarm counters + first-skip log
            // set are session-scoped too (a fresh session re-arms the
            // "disarmed" evidence lines).
            Mirror.MirrorSystems.Reset();
            // T5.2: delta rx counters/seq watermarks are session-scoped too.
            Replication.DeltaSink.Reset();
            // T5.3: the apply pipeline's session state (pending deltas, the
            // EntityIdMap binding, serializer, poison latch) dies with it.
            Replication.StateApplier.Reset();
            // T5.5: pending HashReqs + the guest hash serializer binding die
            // with the session too (any response owed is moot - the host
            // re-requests after the next activation anyway).
            Replication.WorldHash.Reset("session end");
            if (had) { Log.Info("save receiver: reset"); }
        }

        // ---------------------------------------------------------------

        /// <summary>Stream complete: sha256 verify, write Saves\x2coop_guest\&lt;stem&gt;-&lt;n&gt;.json, LoadGame.</summary>
        private static void FinishStream(Runner runner)
        {
            byte[] buf = _buf;
            _buf = null; // the stream is closed either way
            _expectBytes = -1;

            byte[] got;
            using (SHA256 h = SHA256.Create()) { got = h.ComputeHash(buf); }
            if (!BytesEqual(got, _sha))
            {
                string gotHex = Hex8(got);
                string wantHex = Hex8(_sha);
                Log.Warn("save receiver: sha256 mismatch got=" + gotHex
                    + "... want=" + wantHex + "...");
                FailLoad(runner, GuestReadyStatus.LoadFailed,
                    "sha256 mismatch got=" + gotHex + " want=" + wantHex);
                return;
            }
            Log.Info("save receiver: stream ok bytes=" + _received
                + " chunks=" + _chunks + " sha=" + Hex8(got) + "...");

            string path;
            try
            {
                string dir = PathUtility.Combine(Constants.Folders.Save(), "x2coop_guest");
                Directory.CreateDirectory(dir);
                string stem = SaveFileNamer.Stem(_campaignId, _campaignName, _offerSid);
                int n = _fileCounter + 1;
                do
                {
                    path = PathUtility.Combine(dir, stem + "-" + n + ".json");
                    n++;
                }
                while (File.Exists(path));
                _fileCounter = n - 1;
                File.WriteAllBytes(path, buf);
                _wroteFile = true;
                _lastFile = path;
            }
            catch (Exception ex)
            {
                Log.Exception("save receiver write", ex);
                FailLoad(runner, GuestReadyStatus.LoadFailed, "write failed: " + ex.GetType().Name);
                return;
            }

            Log.Info("save receiver: wrote " + path
                + " -> MirrorMode=PendingActivate, LoadGame");
            MirrorState.Mode = MirrorMode.PendingActivate;
            _phase = Phase.AwaitWorld;
            _phaseTimer.Restart();
            try
            {
                // T3.5: the static LoadGame is the funnel for EVERY load and is
                // prefix-blocked while Mode != Off (guest-local loads would
                // spawn a foreign StrategyWorld inside the pending window).
                // ReceiverScope marks THIS call as the receiver's own mirror
                // load - the only one allowed through.
                ReceiverScope.InScope = true;
                try
                {
                    SaveGameSystem.LoadGame(new FileDescriptor(path, typeof(SaveGame)), false);
                }
                finally
                {
                    ReceiverScope.InScope = false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save receiver LoadGame", ex);
                FailLoad(runner, GuestReadyStatus.LoadFailed, "LoadGame threw " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// World is bound, past the one-frame minimum and populated: flip
        /// MirrorMode.Active, log the clock (geodate-equality evidence vs the
        /// host's "join-freeze geodate=" lines) and tell the host we're ready.
        /// </summary>
        private static void Activate(Runner runner, World w)
        {
            MirrorState.Mode = MirrorMode.Active;
            _phase = Phase.Idle;
            _phaseTimer.Reset();
            _resyncsSent = 0;
            _releasedWorld = null; // a fresh mirror supersedes any old release window
            _hbWatch.Restart();    // mirror-freeze heartbeat cadence starts now
            DateTime geo = Geodate(w);
            // T5.2-r1 (C1) evidence: count the marker-family members the
            // streamed save carried in. On the pre-fix build these entities
            // (MarkedForCleanupGroup/MarkedForDeletionGroup) wedged
            // CleanupSystem's unbounded rescan ~10ms after this line; the
            // counts prove the reproduction input was present while the
            // continuing Runner pump proves the MirrorSystems disarm held.
            int markedCleanup = CountMarked<Xenonauts.Common.Cleanup.MarkedForCleanupGroup>(w);
            int markedDeletion = CountMarked<Artitas.Systems.MarkedForDeletionGroup>(w);
            Log.Info("mirror active: geodate=" + FmtDate(geo)
                + " world=" + (w != null ? w.GetHashCode().ToString() : "null")
                + " markedCleanup=" + markedCleanup
                + " markedDeletion=" + markedDeletion
                + " file=" + (_lastFile ?? "?")
                + " sid=" + MsgCodec.ShortId(runner != null ? runner.SessionId : Guid.Empty));
            // Dev knob (x2coop.mirrorForceSpeed=0..3): scripted "guest clicks a
            // speed button" right at activation - the same Resume(force)+
            // SetSpeedTo pair the geoscape UI sends. The T3.4 ClockFreeze gate
            // holds the clock regardless; the heartbeat proves it.
            int spd = DebugSettings.GetInt("mirrorForceSpeed", -1);
            if (spd >= 0)
            {
                FireMirrorSpeed(w, spd);
            }
            // Dev knob (x2coop.probeSaveSuppress=1): scripted T3.5 verify -
            // fires every suppressed SaveGameSystem entry point once against
            // the fresh mirror world; each must log "blocked <method>" and
            // write nothing to Saves\.
            if (DebugSettings.GetInt("probeSaveSuppress", 0) != 0)
            {
                SaveSuppress.DevProbe(w);
            }
            SendGuestReady(runner, GuestReadyStatus.Ready);
            try { UI.CoopPanel.NoteSaveLoaded(); }
            catch (Exception ex) { Log.Exception("save receiver ui-note", ex); }
        }

        /// <summary>
        /// T3.4 guest heartbeat (pumped from <see cref="Tick"/> every frame in
        /// every phase): while Mode==Active logs "mirror-freeze geodate=...
        /// role=guest (held)" every x2coop.mirrorFreezeLogMs (default 5 s,
        /// 0 = off) - the frozen-clock evidence, paired with the host's
        /// "mirror-freeze geodate=... role=host" line. For ~30 s after the
        /// Active->Off transition the RELEASED world is still sampled so the
        /// un-gated clock's resume is provable too. Probe builds also emit a
        /// "mirror-muts n=..." line - ApplyComponentMutation count on the
        /// mirror world since the last heartbeat (reference data for the T4.1
        /// firewall; non-zero is expected - only UpdateTimeOnBase is gated,
        /// the rest of the event pipeline still runs).
        /// </summary>
        private static void MirrorHeartbeat()
        {
            int every = DebugSettings.GetInt("mirrorFreezeLogMs", DefaultMirrorFreezeLogMs);
            if (every <= 0) { return; }
            if (!_hbWatch.IsRunning) { _hbWatch.Restart(); }
            World w = MirrorWorld; // non-null while Active or (T3.6) PendingActivate-with-bound-world
            bool released = false;
            if (w == null)
            {
                w = Resolve(_releasedWorld);
                if (w == null || w.Disposed
                    || !_releasedWatch.IsRunning
                    || _releasedWatch.ElapsedMilliseconds > ReleasedHeartbeatMs)
                {
                    _releasedWorld = null;
                    return;
                }
                released = true;
            }
            else
            {
                _releasedWorld = null;
            }
            if (_hbWatch.ElapsedMilliseconds < every) { return; }
            _hbWatch.Restart();
            Log.Info("mirror-freeze geodate=" + FmtDate(Geodate(w))
                + (released ? " role=guest (released - clock ungated)"
                            : " role=guest (held)"));
#if X2COOP_PROBES
            Log.Info("mirror-muts n=" + Probes.MirrorMutProbe.TakeDelta()
                + " total=" + Probes.MirrorMutProbe.Total
                + (released ? " (released)" : string.Empty));
#endif
        }

        /// <summary>Fire the geoscape speed commands the UI sends (x2coop.mirrorForceSpeed verify knob).</summary>
        private static void FireMirrorSpeed(World w, int speed)
        {
            try
            {
                if (w == null || speed < 0) { return; }
                if (speed > 3) { speed = 3; }
                // Same pair the geoscape speed buttons send (the R9 probe's
                // window-open sequence, GeoscapeMainElement.cs:755) - the
                // force-resume also drops any serialized pause state, so a
                // released clock resumes at the armed speed.
                w.HandleEvent(TimeControlCommand.Resume(forceResume: true));
                w.HandleEvent(TimeControlCommand.SetSpeedTo(speed));
                Log.Info("save receiver: dev mirrorForceSpeed=" + speed
                    + " fired (mirror clock must stay frozen)");
            }
            catch (Exception ex)
            {
                Log.Exception("save receiver mirror-speed", ex);
            }
        }

        /// <summary>
        /// Failure path: up to <see cref="MaxResyncAttempts"/> ResyncReq
        /// retries, then GuestReady{status} so the host's join freeze still
        /// releases. Mode drops to Detached (T3.6, not Off): suppression
        /// stays armed for the life of the session, a half-loaded world is
        /// never promoted to "the mirror", and recovery is an explicit
        /// resync (Resync button / reconnect) rather than a silent retry.
        /// </summary>
        private static void FailLoad(Runner runner, GuestReadyStatus status, string why)
        {
            Log.Warn("save receiver: load failed - " + why);
            MirrorState.Mode = MirrorMode.Detached;
            _mirrorWorld = null;
            _phase = Phase.Idle;
            _phaseTimer.Reset();
            if (_resyncsSent < MaxResyncAttempts)
            {
                _resyncsSent++;
                SendResyncReq(runner, ResyncReason.SaveLoadFailed, why);
            }
            else
            {
                Log.Warn("save receiver: resync budget spent - reporting " + status
                    + " (Resync button or disconnect/rejoin retries it)");
                SendGuestReady(runner, status);
            }
        }

        /// <summary>
        /// T3.6: a Section.Strategy world was disposed. If the ACTIVE mirror
        /// world itself died (guest quit-to-menu, a local transition that
        /// replaced it), leave Active so the firewall/gates drop with it -
        /// Mode -&gt; Detached keeps suppression armed but unbinds the dead
        /// world, and the host is told via SessionNotice{GuestMirrorLost}.
        /// Ignored while Receiving / PendingActivate: our own LoadGame
        /// transition disposes the old mirror in exactly those modes - that
        /// is not a detach.
        /// </summary>
        public static void OnWorldDispose(IContentPackLifecycle.Section section, WeakReference<World> wr)
        {
            try
            {
                if (section != IContentPackLifecycle.Section.Strategy || wr == null) { return; }
                if (MirrorState.Mode != MirrorMode.Active) { return; }
                World ours = Resolve(_mirrorWorld);
                World w = Resolve(wr);
                if (ours == null || w == null || !ReferenceEquals(w, ours))
                {
                    return; // some other Strategy world ended (e.g. a stale local one)
                }
                DetachMirror("mirror world disposed mid-session");
            }
            catch (Exception ex)
            {
                // Fail-open on the detach path: a broken event must not throw
                // into game dispatch - worst case the stale world ref dies on
                // the next Tick liveness check.
                Log.Exception("SaveReceiver.OnWorldDispose", ex);
            }
        }

        /// <summary>Shared detach path (dispose hook + Tick's missed-event check).</summary>
        private static void DetachMirror(string why)
        {
            _releasedWorld = _mirrorWorld;
            _releasedWatch.Restart();
            _mirrorWorld = null;
            _phase = Phase.Idle;
            _phaseTimer.Reset();
            _framesLeft = 0;
            MirrorState.Mode = MirrorMode.Detached;
            Log.Warn("save receiver: " + why + " -> detached (suppression stays armed, host notified)");
            SessionClient.NotifyMirrorLost();
        }

        /// <summary>
        /// T3.6: an externally-requested resync (Resync button, HostReturned,
        /// resyncMinutes timer, deferred fire) gets a fresh failure-retry
        /// budget. The counter only auto-increments inside FailLoad and is
        /// never reset by a failure retry, so a persistently-broken stream
        /// still bounds at MaxResyncAttempts instead of looping forever.
        /// </summary>
        internal static void NoteExternalResync()
        {
            _resyncsSent = 0;
        }

        private static void CheckLoadTimeout(Runner runner, string why, GuestReadyStatus status)
        {
            int ms = DebugSettings.GetInt("mirrorLoadTimeoutMs", DefaultLoadTimeoutMs);
            if (ms <= 0 || _phaseTimer.ElapsedMilliseconds < ms) { return; }
            FailLoad(runner, status, why + " (timeout " + ms + "ms)");
        }

        /// <summary>
        /// T5.2-r1: count alive entities carrying marker component
        /// <typeparamref name="T"/> on the mirror world (-1 on failure).
        /// One O(entities) walk per activation - cheap evidence, never a hot
        /// path.
        /// </summary>
        private static int CountMarked<T>(World w) where T : Artitas.IComponent
        {
            try
            {
                if (w == null) { return -1; }
                int n = 0;
                foreach (Entity e in w.FindAllEntitiesMatching(Matcher.All<T>()))
                {
                    n++;
                }
                return n;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Populated = Initialized + a real clock geodate (host WorldReady equivalent).</summary>
        private static bool MirrorWorldReady(World w)
        {
            try
            {
                return w != null && w.Initialized && Geodate(w) != default(DateTime);
            }
            catch
            {
                return false;
            }
        }

        private static void SendGuestReady(Runner runner, GuestReadyStatus status)
        {
            NetTransport t = runner != null ? runner.Transport : null;
            if (t == null || t.CurrentRole != NetTransport.Role.Guest || t.ServerPeerId < 0)
            {
                Log.Warn("save receiver: cannot send GuestReady{" + status + "} - not connected");
                return;
            }
            byte[] wire = MsgCodec.Encode(new GuestReadyMsg
            {
                SessionId = runner.SessionId,
                Status = status,
            });
            if (wire != null
                && t.Send(t.ServerPeerId, MsgCodec.ChannelFor(MsgType.GuestReady),
                    wire, 0, wire.Length, DeliveryMethod.ReliableOrdered))
            {
                Log.Info("save receiver: GuestReady{" + status + "} sent sid="
                    + MsgCodec.ShortId(runner.SessionId));
            }
            else
            {
                Log.Warn("save receiver: GuestReady{" + status + "} send failed");
            }
        }

        /// <summary>
        /// Send ResyncReq + flip the panel to Resyncing. Internal so
        /// SessionClient.RequestResync (button / HostReturned / timer) uses
        /// the identical wire path as failure-driven retries. Returns true
        /// when the request actually went on the wire (T3.6-r1: the send
        /// latches SessionClient's single-flight flag via NoteResyncSent).
        /// </summary>
        internal static bool SendResyncReq(Runner runner, ResyncReason reason, string detail)
        {
            NetTransport t = runner != null ? runner.Transport : null;
            if (t == null || t.CurrentRole != NetTransport.Role.Guest || t.ServerPeerId < 0)
            {
                Log.Warn("save receiver: cannot send ResyncReq - not connected");
                return false;
            }
            byte[] wire = MsgCodec.Encode(new ResyncReqMsg
            {
                SessionId = runner.SessionId,
                Reason = reason,
                Detail = detail ?? string.Empty,
            });
            if (wire != null
                && t.Send(t.ServerPeerId, MsgCodec.ChannelFor(MsgType.ResyncReq),
                    wire, 0, wire.Length, DeliveryMethod.ReliableOrdered))
            {
                Log.Info("save receiver: ResyncReq{" + reason + "} sent (\"" + detail + "\")");
                try { UI.CoopPanel.NoteResyncSent(); }
                catch (Exception ex) { Log.Exception("save receiver ui-note", ex); }
                SessionClient.NoteResyncSent(); // T3.6-r1 single-flight latch
                return true;
            }
            Log.Warn("save receiver: ResyncReq send failed");
            return false;
        }

        private static World Resolve(WeakReference<World> wr)
        {
            try
            {
                if (wr != null && wr.TryGetTarget(out World w)) { return w; }
            }
            catch
            {
            }
            return null;
        }

        private static DateTime Geodate(World w)
        {
            try
            {
                Entity clock = w != null ? w.FindTagged<GeoscapeClockTag>() : null;
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

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) { return false; }
            int diff = 0;
            for (int i = 0; i < a.Length; i++) { diff |= a[i] ^ b[i]; }
            return diff == 0;
        }

        private static string Hex8(byte[] hash)
        {
            if (hash == null || hash.Length < 8) { return "?"; }
            return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string FmtDate(DateTime dt)
        {
            return dt == default(DateTime) ? "?" : dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
