using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Artitas;
using Artitas.Components;
using Artitas.Systems;
using Artitas.Utils;
using Common;
using Common.Content.Descriptors;
using FullSerializer;
using Xenonauts.Strategy.Systems;
using X2Coop.Mirror;
using X2Coop.Net;
using X2Coop.Runtime;
using X2Coop.Session;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.5 (r2 C7) deterministic world hash - the divergence detector for the
    /// host-authoritative mirror. Both sides serialize the SAME view of their
    /// world through <see cref="CanonicalJson"/> and compare SHA-256 over the
    /// sorted record set at an agreed replication seq:
    ///
    ///   WHAT IS HASHED (identical filter on both sides):
    ///   - every alive entity NOT carrying ExcludeFromSerializationComponent;
    ///   - on it, every component the save path would serialize: the
    ///     [ExcludeFromSerialization] attribute and IOptionalSerialization
    ///     (IsSerializable==false) filters are inherited wholesale by hashing
    ///     through <see cref="WorldState"/>'s constructor - the exact
    ///     component snapshot a save takes (component copies, not live refs);
    ///   - MINUS the R6 whitelist (Mirror/Whitelist.cs): those types are
    ///     documented guest-local/presentation state the host never streams
    ///     (camera, hover, playtime timer...). Hashing them would report a
    ///     mismatch that is by design, not by desync - so "serializable" for
    ///     this task means "serializable AND non-whitelisted" on both sides.
    ///   - guest entities with no EntityIdMap binding are guest-local (a
    ///     mirror must never mint entities the host does not own): skipped,
    ///     counted in <see cref="HashResult.SkippedGuestLocal"/>.
    ///   - optional x2coop.hashSkipTypes / hashSkipMembers exclusions for
    ///     fields proven nondeterministic by a real mismatch.
    ///
    ///   Canonicalization (<see cref="CanonicalJson"/>): object keys sorted
    ///   ordinal, scalars type-tagged, floats/doubles normalized through the
    ///   wire ToString/Parse representation, Entity refs as HOST ids
    ///   (<see cref="HashEntityConverter"/> on the guest; the world's own
    ///   EntityConverter writes bare host ids on the host), items sorted by
    ///   (hostId, type.FullName), SHA-256 over "id|type|canon\n" lines.
    ///
    ///   SEQ SEMANTICS: the host settles first - it fires only after the
    ///   DeltaQueue holds no hash-visible op AND MirrorState.LastDeltaSeq
    ///   stayed stable for x2coop.hashQuietMs - then hashes and sends
    ///   HashReq{seq}. HashReq rides ChannelControl ReliableOrdered, ordered
    ///   AFTER the last delta (seq=LastDeltaSeq) and BEFORE any later delta:
    ///   the guest therefore applies every seq&lt;=N op before the req is
    ///   even decoded, and the per-msg watermark inside StateApplier.Drain
    ///   fires the guest hash at appliedSeq==reqSeq EXACTLY - "state at seq
    ///   N" on both sides. A req whose seq is already exceeded (host not
    ///   actually quiesced) is dropped with a warn rather than answered with
    ///   a known-stale hash; a req that never reaches its seq times out the
    ///   same way.
    ///
    ///   OPTIONAL host pause (x2coop.hashPause, default 1): HashNow sends
    ///   TimeControlCommand.Pause() first - the guest mirror's clock is
    ///   frozen whenever Mode==Active, so pausing the host is what makes the
    ///   pair truly quiesced (a running host keeps mutating hash-visible
    ///   components forever and would settle-timeout every time). Paired
    ///   knob x2coop.hashResumeSpeed restores a speed after the req is sent
    ///   so a scripted run resumes without input.
    ///
    ///   DIAGNOSTICS: mismatch logs seq, both digests, item counts, and the
    ///   first divergent (entity,type) pairs by merge-walking the host's
    ///   item list against the guest's dump file
    ///   (x2coop-hashdump-{role}-seq{N}.txt in Logs\ - both instances share
    ///   the profile dir). hashDumpLevel=2 adds full canonical text per item
    ///   so the diff can report the member path ("field-level diagnostics").
    ///
    ///   Cost is deliberately not optimized: it runs on a dev trigger during
    ///   a pause - new WorldState(w) pays the same deep-copy the save path
    ///   pays (~a resync's snapshot), plus one serialize pass per component.
    ///   Fail-open throughout: anything throwing logs and yields an error
    ///   result instead of destabilizing the session.
    /// </summary>
    internal static class WorldHash
    {
        // ---- knobs (x2coop.hash*) ------------------------------------------
        private const int DefaultQuietMs = 400;        // pending==0 && seq stable for this long
        private const int DefaultSettleMs = 15000;     // give up settling, hash anyway (warn)
        private const int DefaultRespTimeoutMs = 30000;
        private const int DefaultReqTimeoutMs = 30000;
        private const int DefaultMaxDiffs = 16;
        private const int MaxGuestReqs = 8;

        // ---- shared result --------------------------------------------------
        internal sealed class HashResult
        {
            internal bool Ok;
            internal string Error;
            internal long Seq = -1;
            internal byte[] Digest = new byte[WireLimits.Sha256Bytes];
            internal int Items;
            internal int Entities;
            internal int SkippedGuestLocal;
            internal int SkippedWhitelist;
            internal int SkippedTypes;
            internal int SerFailures;
            internal int UnmappedRefs;      // guest-only: entity refs with no host id
            internal long TookMs;
            internal List<HashItem> ItemList = new List<HashItem>();
        }

        // ---- host state machine ---------------------------------------------
        private enum HostPhase : byte { Idle = 0, Settle = 1, AwaitResp = 2 }

        private static readonly Stopwatch Mono = Stopwatch.StartNew();

        private static HostPhase _phase;
        private static Runner _runner;
        private static long _seqSeen = -1;
        private static long _quietStart;
        private static long _settleStart;
        private static int _lastPend;
        private static long _reqSeq = -1;
        private static HashResult _hostResult;
        private static readonly Stopwatch _respWatch = new Stopwatch();
        private static long _autoAtMs = -1;    // armed by NoteGuestReady when knob > 0
        private static bool _hashPauseIssued;  // we own one Pause() increment - Resume() must repay it

        // ---- guest side ------------------------------------------------------
        private struct GuestReq { internal long Seq; internal long AtMs; }
        private static readonly List<GuestReq> _reqs = new List<GuestReq>(MaxGuestReqs);
        private static Runner _guestRunner;
        private static fsSerializer _guestSer;
        private static HashEntityConverter _guestConv;
        private static EntityIdMap _guestSerMap;  // the map _guestSer was built for

        // ---- exclusion knobs (parsed once per process) ------------------------
        private static bool _skipInit;
        private static HashSet<string> _skipTypes;
        private static Dictionary<string, List<string[]>> _skipMembers;
        private static readonly HashSet<string> _serFailLogged = new HashSet<string>(StringComparer.Ordinal);

        // ======================================================================
        // canonical compute (shared host/guest)
        // ======================================================================

        /// <summary>
        /// Serialize the world's hash-visible state into a sorted canonical
        /// item set and digest it. <paramref name="map"/> is null on the host
        /// (entity ids are already host ids); on the guest every entity id is
        /// translated - unmapped (guest-local) entities are excluded, counted,
        /// and never hashed.
        /// </summary>
        internal static HashResult Compute(World world, EntityIdMap map, fsSerializer ser)
        {
            var res = new HashResult();
            if (world == null || world.Disposed)
            {
                res.Error = "world absent/disposed";
                return res;
            }
            if (ser == null)
            {
                res.Error = "no serializer";
                return res;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                EnsureSkipConfig();
                // WorldState's ctor is the save path's own filter: alive,
                // non-ExcludeFromSerializationComponent entities, components
                // minus [ExcludeFromSerialization] types and
                // IOptionalSerialization opt-outs - taken as COPIES, so the
                // serialization loop below reads an immutable snapshot.
                var ws = new WorldState(world);
                Type[] typeByBag = InvertComponentIds(ws);
                List<Bag<IComponent>> matrix = ws.ComponentMatrix;
                List<Entity> ents = ws.Entities;
                var items = res.ItemList;
                var sb = new StringBuilder(256);
                using (AssetDescriptor.EnterSaveSerialization())
                {
                    for (int ei = 0; ei < ents.Count; ei++)
                    {
                        int localId;
                        try { localId = ents[ei] != null ? ents[ei].ID : -1; }
                        catch { continue; }
                        if (localId < 0) { continue; }
                        int hostId;
                        if (map == null)
                        {
                            hostId = localId;
                        }
                        else
                        {
                            // HostIdLive (live bindings only): every entity
                            // here is alive in ws.Entities, so it must carry
                            // a live binding. HostId's dead-path exists for
                            // dangling REFS inside components, not for naming
                            // live entities - a guest-local entity recycled
                            // onto a tombstoned slot would otherwise be
                            // hashed under a dead host id.
                            hostId = map.HostIdLive(localId);
                            if (hostId < 0)
                            {
                                res.SkippedGuestLocal++;
                                continue;
                            }
                        }
                        res.Entities++;
                        for (int b = 0; b < matrix.Count; b++)
                        {
                            Bag<IComponent> bag = matrix[b];
                            if (bag == null || localId >= bag.Count) { continue; }
                            IComponent comp;
                            try { comp = bag[localId]; }
                            catch { continue; }
                            if (comp == null) { continue; }
                            Type type = b < typeByBag.Length && typeByBag[b] != null
                                ? typeByBag[b]
                                : comp.GetType();
                            if (Whitelist.Contains(type))
                            {
                                // Guest-local/presentation state the host never
                                // streams (Whitelist.cs: never replicated, so
                                // hashing it is a guaranteed false positive).
                                res.SkippedWhitelist++;
                                continue;
                            }
                            if (_skipTypes != null && _skipTypes.Contains(type.FullName))
                            {
                                res.SkippedTypes++;
                                continue;
                            }
                            fsData data = null;
                            try
                            {
                                fsResult r = ser.TrySerialize(type, comp, out data);
                                if (r.Failed || data == null)
                                {
                                    res.SerFailures++;
                                    SerFailOnce(type, null);
                                    continue;
                                }
                            }
                            catch (Exception ex)
                            {
                                res.SerFailures++;
                                SerFailOnce(type, ex);
                                continue;
                            }
                            ApplySkipMembers(type, data);
                            sb.Length = 0;
                            CanonicalJson.Write(data, sb);
                            items.Add(new HashItem
                            {
                                EntityId = hostId,
                                TypeName = type.FullName ?? type.Name,
                                Canon = sb.ToString(),
                            });
                        }
                    }
                }
                CanonicalJson.Sort(items);
                res.Digest = CanonicalJson.Digest(items);
                res.Items = items.Count;
                res.Ok = true;
            }
            catch (Exception ex)
            {
                res.Error = ex.ToString();
            }
            res.TookMs = sw.ElapsedMilliseconds;
            return res;
        }

        /// <summary>
        /// ComponentIDs maps registered type -> filtered bag index; invert it
        /// so each bag reports the type the wire/save actually writes.
        /// </summary>
        private static Type[] InvertComponentIds(WorldState ws)
        {
            var arr = new Type[ws.ComponentMatrix != null ? ws.ComponentMatrix.Count : 0];
            try
            {
                foreach (KeyValuePair<Type, int> kv in ws.ComponentIDs)
                {
                    if (kv.Value >= 0 && kv.Value < arr.Length) { arr[kv.Value] = kv.Key; }
                }
            }
            catch { }
            return arr;
        }

        // ======================================================================
        // exclusion knobs
        // ======================================================================

        /// <summary>
        /// x2coop.hashSkipTypes=CSV(type FullName) and
        /// x2coop.hashSkipMembers=CSV("Type.FullName:dot.member.path") -
        /// appended exclusions for fields a real mismatch run proves
        /// nondeterministic (each must land in the T5.5 report with a reason).
        /// </summary>
        private static void EnsureSkipConfig()
        {
            if (_skipInit) { return; }
            _skipInit = true;
            _skipTypes = new HashSet<string>(StringComparer.Ordinal);
            _skipMembers = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
            try
            {
                string t = DebugSettings.Get("hashSkipTypes");
                if (!string.IsNullOrEmpty(t))
                {
                    foreach (string s in t.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        _skipTypes.Add(s.Trim());
                    }
                }
                string m = DebugSettings.Get("hashSkipMembers");
                if (!string.IsNullOrEmpty(m))
                {
                    foreach (string s in m.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string e = s.Trim();
                        int c = e.IndexOf(':');
                        if (c <= 0 || c >= e.Length - 1) { continue; }
                        string tn = e.Substring(0, c);
                        string[] path = e.Substring(c + 1).Split('.');
                        List<string[]> list;
                        if (!_skipMembers.TryGetValue(tn, out list))
                        {
                            list = new List<string[]>();
                            _skipMembers[tn] = list;
                        }
                        list.Add(path);
                    }
                }
                if (_skipTypes.Count > 0 || _skipMembers.Count > 0)
                {
                    Log.Info("hash: skip config types=" + _skipTypes.Count
                        + " memberPaths=" + _skipMembers.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("hash skip-config", ex);
            }
        }

        /// <summary>Strip knob-excluded member paths out of a serialized tree.</summary>
        private static void ApplySkipMembers(Type type, fsData data)
        {
            List<string[]> paths;
            if (_skipMembers == null || type == null || data == null
                || !_skipMembers.TryGetValue(type.FullName, out paths))
            {
                return;
            }
            for (int p = 0; p < paths.Count; p++)
            {
                RemovePath(data, paths[p]);
            }
        }

        private static void RemovePath(fsData data, string[] path)
        {
            fsData cur = data;
            for (int i = 0; i < path.Length - 1 && cur != null && cur.IsDictionary; i++)
            {
                fsData next;
                cur = cur.AsDictionary.TryGetValue(path[i], out next) ? next : null;
            }
            if (cur != null && cur.IsDictionary)
            {
                cur.AsDictionary.Remove(path[path.Length - 1]);
            }
        }

        /// <summary>Per-type once-serialize-failure logging.</summary>
        private static void SerFailOnce(Type type, Exception ex)
        {
            string name = type != null ? type.FullName : "?";
            if (_serFailLogged.Add(name))
            {
                Log.Warn("hash: serialize failed t=" + name
                    + (ex != null ? " (" + ex.GetType().Name + ": " + ex.Message + ")" : string.Empty)
                    + " (once per type)");
            }
        }

        // ======================================================================
        // dump files + pair diff
        // ======================================================================

        /// <summary>Dump level: 0 = none, 1 = on mismatch/guest-respond, 2 = always.</summary>
        private static int DumpMode()
        {
            return DebugSettings.GetInt("hashDump", 1);
        }

        private static string DumpDir()
        {
            try
            {
                string f = Log.FilePath;
                if (!string.IsNullOrEmpty(f))
                {
                    return Path.GetDirectoryName(f);
                }
            }
            catch { }
            try { return Constants.Folders.Logs(); }
            catch { return null; }
        }

        private static string DumpPathFor(string role, long seq)
        {
            string dir = DumpDir();
            return dir == null ? null
                : Path.Combine(dir, "x2coop-hashdump-" + role + "-seq" + seq + ".txt");
        }

        /// <summary>
        /// Write the item dump (one "eid\ttype\tfnv16[\tcanon]" line per item,
        /// header '#' line first). Level knob x2coop.hashDumpLevel: 1 = id +
        /// type + fingerprint, 2 = + canonical text (needed for field-path
        /// diffs).
        /// </summary>
        private static void WriteDump(HashResult res, string role)
        {
            try
            {
                string path = DumpPathFor(role, res.Seq);
                if (path == null) { return; }
                int level = DebugSettings.GetInt("hashDumpLevel", 1);
                using (var w = new StreamWriter(path, false))
                {
                    w.WriteLine("# x2coop hashdump role=" + role
                        + " seq=" + res.Seq
                        + " digest=" + CanonicalJson.Hex(res.Digest)
                        + " items=" + res.Items + " ents=" + res.Entities
                        + " tookMs=" + res.TookMs
                        + " serFail=" + res.SerFailures
                        + " skipGuestLocal=" + res.SkippedGuestLocal
                        + " skipWl=" + res.SkippedWhitelist
                        + " skipTypes=" + res.SkippedTypes
                        + " unmapped=" + res.UnmappedRefs);
                    List<HashItem> items = res.ItemList;
                    for (int i = 0; i < items.Count; i++)
                    {
                        HashItem it = items[i];
                        w.Write(it.EntityId);
                        w.Write('\t');
                        w.Write(it.TypeName);
                        w.Write('\t');
                        w.Write(it.Fingerprint.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
                        if (level >= 2)
                        {
                            w.Write('\t');
                            w.Write(it.Canon);
                        }
                        w.Write('\n');
                    }
                }
                Log.Info("hash: dump " + path + " level=" + level);
            }
            catch (Exception ex)
            {
                Log.Exception("hash dump-write", ex);
            }
        }

        /// <summary>
        /// Host-side pair diff: merge-walk our sorted items against the guest's
        /// dump file, logging the first divergent (entity,type) pairs - plus a
        /// member path when both dumps carried canonical text.
        /// </summary>
        private static void LogPairDiff(HashResult host)
        {
            try
            {
                string path = DumpPathFor("guest", host.Seq);
                if (path == null || !File.Exists(path))
                {
                    Log.Warn("hash: guest dump not found (" + (path ?? "?")
                        + ") - run the guest with -x2coop.hashDump=1 for pair diffs");
                    return;
                }
                var gItems = new List<HashItem>(host.Items);
                bool gHasCanon = false;
                foreach (string line in File.ReadLines(path))
                {
                    if (line == null || line.Length == 0 || line[0] == '#') { continue; }
                    string[] f = line.Split('\t');
                    if (f.Length < 3) { continue; }
                    int eid;
                    if (!int.TryParse(f[0], out eid)) { continue; }
                    var it = new HashItem { EntityId = eid, TypeName = f[1] };
                    ulong fp;
                    it.Fingerprint = ulong.TryParse(f[2],
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out fp) ? fp : 0;
                    if (f.Length >= 4) { it.Canon = f[3]; gHasCanon = true; }
                    gItems.Add(it);
                }
                CanonicalJson.Sort(gItems);
                List<HashItem> hItems = host.ItemList;
                int max = Math.Max(1, DebugSettings.GetInt("hashMaxDiffs", DefaultMaxDiffs));
                // Value diffs are the actionable kind (same entity+type on
                // both sides but different content) - collect first, then
                // membership-only lines, so the log cap can never starve the
                // interesting diff class.
                var valLines = new List<string>(max);
                var memLines = new List<string>(max);
                int onlyH = 0, onlyG = 0, valDiff = 0;
                int i = 0, j = 0;
                while (i < hItems.Count || j < gItems.Count)
                {
                    int cmp;
                    if (i >= hItems.Count) { cmp = 1; }
                    else if (j >= gItems.Count) { cmp = -1; }
                    else
                    {
                        cmp = hItems[i].EntityId != gItems[j].EntityId
                            ? hItems[i].EntityId.CompareTo(gItems[j].EntityId)
                            : string.CompareOrdinal(hItems[i].TypeName, gItems[j].TypeName);
                    }
                    if (cmp < 0)
                    {
                        onlyH++;
                        if (memLines.Count < max)
                        {
                            memLines.Add("hash: diff e=" + hItems[i].EntityId
                                + " t=" + hItems[i].TypeName + " only-on-host");
                        }
                        i++;
                    }
                    else if (cmp > 0)
                    {
                        onlyG++;
                        if (memLines.Count < max)
                        {
                            memLines.Add("hash: diff e=" + gItems[j].EntityId
                                + " t=" + gItems[j].TypeName + " only-on-guest");
                        }
                        j++;
                    }
                    else
                    {
                        if (hItems[i].Fingerprint != gItems[j].Fingerprint)
                        {
                            valDiff++;
                            if (valLines.Count < max)
                            {
                                string detail = " value-differs (fnv)";
                                if (gHasCanon && hItems[i].Canon != null)
                                {
                                    string d = CanonicalJson.Diff(hItems[i].Canon, gItems[j].Canon);
                                    if (d != null) { detail = " field: " + d; }
                                }
                                valLines.Add("hash: diff e=" + hItems[i].EntityId
                                    + " t=" + hItems[i].TypeName + detail);
                            }
                        }
                        i++; j++;
                    }
                }
                for (int k = 0; k < valLines.Count; k++) { Log.Warn(valLines[k]); }
                for (int k = 0; k < memLines.Count; k++) { Log.Warn(memLines[k]); }
                int shown = valLines.Count + memLines.Count;
                Log.Warn("hash: diff totals onlyHost=" + onlyH
                    + " onlyGuest=" + onlyG + " valueDiff=" + valDiff
                    + " guestItems=" + gItems.Count
                    + (shown >= onlyH + onlyG + valDiff ? string.Empty
                        : " (" + (onlyH + onlyG + valDiff - shown) + " more not shown, cap=" + max + ")")
                    + (gHasCanon ? string.Empty
                        : " (guest dump lacks canon - set hashDumpLevel=2 for field paths)"));
            }
            catch (Exception ex)
            {
                Log.Exception("hash pair-diff", ex);
            }
        }

        private static void MaybeDump(HashResult res, string role, bool mismatch)
        {
            int mode = DumpMode();
            if (mode >= 2 || (mode == 1 && mismatch))
            {
                WriteDump(res, role);
            }
        }

        // ======================================================================
        // HOST: request -> settle -> hash -> HashReq -> compare HashResp
        // ======================================================================

        /// <summary>
        /// Developer trigger (probes hotkey) + auto-trigger. On the host this
        /// optionally pauses the world, waits for the delta queue to quiet,
        /// hashes, then asks the guest for its hash at the same seq. On a
        /// guest it computes a local hash at the current applied seq
        /// (dump/log only - comparison needs a host request).
        /// </summary>
        internal static void RequestNow(Runner r, string reason)
        {
            try
            {
                if (r == null || r.Transport == null) { return; }
                if (r.Transport.CurrentRole == NetTransport.Role.Guest)
                {
                    _guestRunner = r;
                    GuestLocalHash();
                    return;
                }
                if (r.Transport.CurrentRole != NetTransport.Role.Host) { return; }
                _runner = r;
                if (_phase != HostPhase.Idle)
                {
                    Log.Warn("hash: request ignored - already " + _phase);
                    return;
                }
                if (MirrorState.HostJoinFreeze)
                {
                    Log.Warn("hash: request ignored - join freeze active (mid-stream)");
                    return;
                }
                World w = ScreenGuard.StrategyWorld;
                if (w == null || w.Disposed)
                {
                    Log.Warn("hash: request ignored - no live host world");
                    return;
                }
                if (DebugSettings.GetInt("hashPause", 1) != 0)
                {
                    try
                    {
                        // TimeTransitionSystem pause is a COUNTER, not a flag:
                        // Pause() increments it and only Resume() decrements
                        // it - SetSpeedTo alone never unpauses. Record that
                        // this hash owns one increment so MaybeResumeHostSpeed
                        // repays exactly one Resume() (a user-side pause taken
                        // during the settle stays in effect).
                        w.HandleEvent(TimeControlCommand.Pause());
                        _hashPauseIssued = true;
                        Log.Info("hash: host pause issued (x2coop.hashPause=1)"
                            + " - resume via the geoscape UI");
                    }
                    catch (Exception ex) { Log.Exception("hash pause", ex); }
                }
                _phase = HostPhase.Settle;
                _settleStart = _quietStart = Mono.ElapsedMilliseconds;
                _seqSeen = -1;
                _lastPend = -1;
                Log.Info("hash: settle begin (" + (reason ?? "?") + ")"
                    + " quiet=" + QuietMs() + "ms timeout=" + SettleMs() + "ms"
                    + " seq=" + MirrorState.LastDeltaSeq);
            }
            catch (Exception ex)
            {
                Log.Exception("hash request", ex);
            }
        }

        /// <summary>
        /// Runner.Update hook (after SessionHost.Tick): auto-trigger timer,
        /// host response timeout, guest req sweep + timeouts.
        /// </summary>
        internal static void Tick(Runner r)
        {
            try
            {
                if (r == null || r.Transport == null) { return; }
                if (r.Transport.CurrentRole == NetTransport.Role.Host)
                {
                    _runner = r;
                    if (_autoAtMs >= 0 && Mono.ElapsedMilliseconds >= _autoAtMs)
                    {
                        _autoAtMs = -1;
                        if (_phase == HostPhase.Idle && SessionHost.HasGuest
                            && !MirrorState.HostJoinFreeze)
                        {
                            RequestNow(r, "auto x2coop.hashOnReadyMs");
                        }
                    }
                    if (_phase == HostPhase.AwaitResp
                        && _respWatch.ElapsedMilliseconds > RespTimeoutMs())
                    {
                        Log.Warn("hash: response timeout seq=" + _reqSeq
                            + " (> " + RespTimeoutMs() + "ms) - guest may be"
                            + " waiting for its applied seq or has dropped the req");
                        _phase = HostPhase.Idle;
                        _reqSeq = -1;
                        _hostResult = null;
                    }
                }
                else if (r.Transport.CurrentRole == NetTransport.Role.Guest)
                {
                    _guestRunner = r;
                    GuestSweep();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("hash tick", ex);
            }
        }

        /// <summary>
        /// Runner.LateUpdate hook at the END of FlushReplication (post-Consume,
        /// so the frame's ops are already folded into DeltaQueue). Pumps the
        /// settle phase: fire when nothing hash-visible is pending AND
        /// LastDeltaSeq has been stable for hashQuietMs.
        /// </summary>
        internal static void LateUpdate(Runner r)
        {
            try
            {
                if (_phase != HostPhase.Settle || r == null) { return; }
                long now = Mono.ElapsedMilliseconds;
                int pend = DeltaSender.HashVisiblePending;
                long seq = MirrorState.LastDeltaSeq;
                if (pend > 0 || seq != _seqSeen)
                {
                    if (pend != _lastPend || seq != _seqSeen)
                    {
                        _lastPend = pend;
                    }
                    _seqSeen = seq;
                    _quietStart = now;
                    return;
                }
                if (now - _quietStart >= QuietMs())
                {
                    FireHost(r, timedOut: false);
                }
                else if (now - _settleStart > SettleMs())
                {
                    Log.Warn("hash: settle timeout after " + (now - _settleStart)
                        + "ms (world not quiesced) - hashing anyway");
                    FireHost(r, timedOut: true);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("hash settle", ex);
                _phase = HostPhase.Idle;
            }
        }

        private static void FireHost(Runner r, bool timedOut)
        {
            World w = ScreenGuard.StrategyWorld;
            fsSerializer ser = null;
            try { ser = w != null ? w.GetSystem<ArtitasSerializationSystem>()?.Serializer : null; }
            catch { }
            HashResult res = Compute(w, null, ser);
            res.Seq = MirrorState.LastDeltaSeq;
            if (!res.Ok)
            {
                Log.Warn("hash: host compute failed seq=" + res.Seq + " - " + res.Error);
                _phase = HostPhase.Idle;
                MaybeResumeHostSpeed(w);
                return;
            }
            Log.Info("hash: host seq=" + res.Seq
                + " hash=" + CanonicalJson.Hex(res.Digest)
                + " items=" + res.Items + " ents=" + res.Entities
                + " tookMs=" + res.TookMs
                + " serFail=" + res.SerFailures
                + " skipWl=" + res.SkippedWhitelist
                + (timedOut ? " (settle-timeout)" : string.Empty));
            _hostResult = res;
            if (!SessionHost.HasGuest || r.Transport == null
                || SessionHost.GuestPeerId < 0)
            {
                Log.Info("hash: no guest - local hash only (dump with -x2coop.hashDump=2)");
                MaybeDump(res, "host", mismatch: false);
                _phase = HostPhase.Idle;
                _hostResult = null;
                MaybeResumeHostSpeed(w);
                return;
            }
            byte[] wire = MsgCodec.Encode(new HashReqMsg
            {
                SessionId = r.SessionId,
                Seq = res.Seq,
            });
            if (wire == null
                || !r.Transport.Send(SessionHost.GuestPeerId,
                    MsgCodec.ChannelFor(MsgType.HashReq), wire, 0, wire.Length,
                    MsgCodec.DeliveryFor(MsgType.HashReq)))
            {
                Log.Warn("hash: HashReq seq=" + res.Seq + " send failed");
                _phase = HostPhase.Idle;
                _hostResult = null;
                MaybeResumeHostSpeed(w);
                return;
            }
            _reqSeq = res.Seq;
            _respWatch.Restart();
            _phase = HostPhase.AwaitResp;
            Log.Info("hash: req sent seq=" + res.Seq);
            // The host snapshot is frozen in res already; the guest answers at
            // exactly req.Seq even if the world resumes mid-flight (its drain
            // fires the hash BETWEEN messages at that watermark).
            MaybeResumeHostSpeed(w);
        }

        /// <summary>SessionHost dispatch: guest's answer to our HashReq.</summary>
        internal static void OnHashResp(int peerId, HashRespMsg msg)
        {
            try
            {
                if (msg == null) { return; }
                if (peerId != SessionHost.GuestPeerId)
                {
                    Log.Warn("hash: resp from non-guest peer=" + peerId + " ignored");
                    return;
                }
                if (_phase != HostPhase.AwaitResp || msg.Seq != _reqSeq)
                {
                    Log.Warn("hash: unexpected resp seq=" + msg.Seq
                        + " (phase=" + _phase + " pending=" + _reqSeq + ")");
                    return;
                }
                _phase = HostPhase.Idle;
                _respWatch.Reset();
                HashResult host = _hostResult;
                _hostResult = null;
                _reqSeq = -1;
                if (host == null || host.Digest == null || msg.Hash == null)
                {
                    Log.Warn("hash: resp seq=" + msg.Seq + " but local result missing");
                    return;
                }
                bool match = true;
                for (int i = 0; i < WireLimits.Sha256Bytes; i++)
                {
                    if (host.Digest[i] != msg.Hash[i]) { match = false; break; }
                }
                if (match)
                {
                    Log.Info("hash: MATCH seq=" + msg.Seq
                        + " hash=" + CanonicalJson.Hex(host.Digest)
                        + " items h=" + host.Items + " g=" + msg.Count
                        + " ents=" + host.Entities);
                    MaybeDump(host, "host", mismatch: false);
                    return;
                }
                Log.Warn("hash: MISMATCH seq=" + msg.Seq
                    + " host=" + CanonicalJson.Hex(host.Digest)
                    + " guest=" + CanonicalJson.Hex(msg.Hash)
                    + " items h=" + host.Items + " g=" + msg.Count
                    + " ents=" + host.Entities
                    + " serFail=" + host.SerFailures);
                MaybeDump(host, "host", mismatch: true);
                LogPairDiff(host);
            }
            catch (Exception ex)
            {
                Log.Exception("hash resp", ex);
            }
        }

        /// <summary>
        /// SessionHost.HandleGuestReady seam: arm the scripted post-activation
        /// hash when x2coop.hashOnReadyMs &gt; 0 (fires once per GuestReady -
        /// covers the first join AND every resync).
        /// </summary>
        internal static void NoteGuestReady()
        {
            try
            {
                int ms = DebugSettings.GetInt("hashOnReadyMs", 0);
                if (ms <= 0) { return; }
                _autoAtMs = Mono.ElapsedMilliseconds + ms;
                Log.Info("hash: auto-hash armed +" + ms + "ms (post-GuestReady)");
            }
            catch (Exception ex) { Log.Exception("hash onready-arm", ex); }
        }

        // ======================================================================
        // GUEST: HashReq -> wait for applied seq -> hash -> HashResp
        // ======================================================================

        /// <summary>SessionClient dispatch: host asked for a hash at seq.</summary>
        internal static void OnHashReq(Runner r, HashReqMsg msg)
        {
            try
            {
                if (r == null || msg == null) { return; }
                _guestRunner = r;
                if (MirrorState.Mode != MirrorMode.Active)
                {
                    Log.Warn("hash: req seq=" + msg.Seq + " dropped - mirror not Active ("
                        + MirrorState.Mode + ")");
                    return;
                }
                if (_reqs.Count >= MaxGuestReqs)
                {
                    Log.Warn("hash: req seq=" + msg.Seq + " dropped - req queue full");
                    return;
                }
                _reqs.Add(new GuestReq { Seq = msg.Seq, AtMs = Mono.ElapsedMilliseconds });
                Log.Info("hash: req seq=" + msg.Seq
                    + " (applied=" + StateApplier.LastAppliedSeq
                    + " pend=" + StateApplier.PendingMsgs + ")");
                GuestWatermark(StateApplier.LastAppliedSeq);
            }
            catch (Exception ex)
            {
                Log.Exception("hash req", ex);
            }
        }

        /// <summary>
        /// Called with the guest's applied reliable-seq watermark: after every
        /// applied StateDelta inside StateApplier.Drain (fires BETWEEN msgs so
        /// the hash runs at exactly req.Seq even when later deltas queue behind
        /// it), at drain end, and from Tick. Reqs for seqs already passed are
        /// dropped - answering them would hash post-N state.
        /// </summary>
        internal static void GuestWatermark(long appliedSeq)
        {
            try
            {
                if (_reqs.Count == 0) { return; }
                for (int i = 0; i < _reqs.Count;)
                {
                    GuestReq req = _reqs[i];
                    if (req.Seq == appliedSeq)
                    {
                        _reqs.RemoveAt(i);
                        RespondGuest(req);
                        continue;
                    }
                    if (req.Seq < appliedSeq)
                    {
                        _reqs.RemoveAt(i);
                        Log.Warn("hash: req seq=" + req.Seq + " already past applied="
                            + appliedSeq + " - dropped (host world was not quiesced?)");
                        continue;
                    }
                    i++; // req.Seq > appliedSeq - wait for the deltas to arrive
                }
            }
            catch (Exception ex)
            {
                Log.Exception("hash watermark", ex);
            }
        }

        /// <summary>Tick-side guest housekeeping: watermark re-check + stale req timeout.</summary>
        private static void GuestSweep()
        {
            GuestWatermark(StateApplier.LastAppliedSeq);
            if (_reqs.Count == 0) { return; }
            long now = Mono.ElapsedMilliseconds;
            int timeout = ReqTimeoutMs();
            for (int i = _reqs.Count - 1; i >= 0; i--)
            {
                if (now - _reqs[i].AtMs > timeout)
                {
                    Log.Warn("hash: req seq=" + _reqs[i].Seq + " timed out after "
                        + timeout + "ms (applied=" + StateApplier.LastAppliedSeq
                        + ") - dropped");
                    _reqs.RemoveAt(i);
                }
            }
        }

        /// <summary>Compute the mirror hash at exactly req.Seq and answer the host.</summary>
        private static void RespondGuest(GuestReq req)
        {
            Runner r = _guestRunner;
            World w = SaveReceiver.MirrorWorld;
            EntityIdMap map = StateApplier.Map;
            fsSerializer ser = GuestSerializer(map);
            HashResult res = Compute(w, map, ser);
            res.Seq = req.Seq;
            if (_guestConv != null) { res.UnmappedRefs = _guestConv.Unmapped; }
            if (!res.Ok)
            {
                Log.Warn("hash: guest compute failed seq=" + req.Seq + " - " + res.Error
                    + " - no response sent");
                return;
            }
            Log.Info("hash: guest seq=" + req.Seq
                + " hash=" + CanonicalJson.Hex(res.Digest)
                + " items=" + res.Items + " ents=" + res.Entities
                + " tookMs=" + res.TookMs
                + " serFail=" + res.SerFailures
                + " skipLocal=" + res.SkippedGuestLocal
                + " skipWl=" + res.SkippedWhitelist
                + (res.UnmappedRefs > 0 ? " UNMAPPED=" + res.UnmappedRefs : string.Empty));
            // The guest's dump is the host's pair-diff source - always write it
            // when dumps are on at all (the guest cannot know the verdict yet).
            if (DumpMode() >= 1) { WriteDump(res, "guest"); }
            NetTransport t = r != null ? r.Transport : null;
            if (t == null || !t.IsRunning || t.ServerPeerId < 0)
            {
                Log.Warn("hash: cannot respond - transport down");
                return;
            }
            byte[] wire = MsgCodec.Encode(new HashRespMsg
            {
                SessionId = r.SessionId,
                Seq = req.Seq,
                Hash = res.Digest,
                Count = res.Items,
            });
            if (wire == null
                || !t.Send(t.ServerPeerId, MsgCodec.ChannelFor(MsgType.HashResp),
                    wire, 0, wire.Length, MsgCodec.DeliveryFor(MsgType.HashResp)))
            {
                Log.Warn("hash: HashResp seq=" + req.Seq + " send failed");
                return;
            }
            Log.Info("hash: resp sent seq=" + req.Seq);
        }

        /// <summary>Guest pressing the hotkey: local hash at the current watermark.</summary>
        private static void GuestLocalHash()
        {
            World w = SaveReceiver.MirrorWorld;
            EntityIdMap map = StateApplier.Map;
            if (w == null || w.Disposed || map == null)
            {
                Log.Warn("hash: guest local hash skipped - no bound mirror");
                return;
            }
            fsSerializer ser = GuestSerializer(map);
            HashResult res = Compute(w, map, ser);
            res.Seq = StateApplier.LastAppliedSeq;
            if (_guestConv != null) { res.UnmappedRefs = _guestConv.Unmapped; }
            if (!res.Ok)
            {
                Log.Warn("hash: guest local compute failed - " + res.Error);
                return;
            }
            Log.Info("hash: guest local seq=" + res.Seq
                + " hash=" + CanonicalJson.Hex(res.Digest)
                + " items=" + res.Items + " ents=" + res.Entities
                + " tookMs=" + res.TookMs
                + " skipLocal=" + res.SkippedGuestLocal
                + " skipWl=" + res.SkippedWhitelist);
            MaybeDump(res, "guest", mismatch: false);
        }

        /// <summary>
        /// Guest hash serializer: the guest's normal converter set
        /// (SetupSerializer(world:null) = game chain minus EntityConverter)
        /// plus HashEntityConverter for host-id write-back. Built once per
        /// EntityIdMap instance (a resync rebind builds a fresh one).
        /// </summary>
        private static fsSerializer GuestSerializer(EntityIdMap map)
        {
            if (_guestSer != null && ReferenceEquals(_guestSerMap, map)) { return _guestSer; }
            try { _guestSer?.Dispose(); } catch { }
            _guestSer = null;
            _guestConv = null;
            _guestSerMap = map;
            if (map == null) { return null; }
            try
            {
                fsSerializer ser = Common.Serialization.CommonSerializationSystem
                    .SetupSerializer(new fsSerializer(), world: null);
                _guestConv = new HashEntityConverter(map);
                ser.AddConverter(_guestConv);
                _guestSer = ser;
            }
            catch (Exception ex)
            {
                Log.Exception("hash guest-ser", ex);
            }
            return _guestSer;
        }

        // ======================================================================
        // reset + knob helpers
        // ======================================================================

        /// <summary>
        /// Session end (host ClearGuest / guest SaveReceiver.Reset / teardown):
        /// drop every in-flight request, pending guest req, the local result
        /// and the serializer binding. Never blocks - hashing is disposable.
        /// </summary>
        internal static void Reset(string why)
        {
            bool had = _phase != HostPhase.Idle || _reqs.Count > 0
                || _hostResult != null || _guestSer != null;
            _phase = HostPhase.Idle;
            _runner = null;
            _reqSeq = -1;
            _hostResult = null;
            _respWatch.Reset();
            _autoAtMs = -1;
            _hashPauseIssued = false;
            _reqs.Clear();
            _guestRunner = null;
            _guestSerMap = null;
            _guestConv = null;
            try { _guestSer?.Dispose(); } catch { }
            _guestSer = null;
            if (had)
            {
                Log.Info("hash: reset (" + (why ?? "?") + ")");
            }
        }

        /// <summary>
        /// x2coop.hashResumeSpeed (0-3, -1 = off): restore geoscape speed
        /// after a hash fired - paired with hashPause so a scripted run
        /// pauses for the snapshot, then resumes without input.
        /// </summary>
        private static void MaybeResumeHostSpeed(World w)
        {
            try
            {
                int speed = DebugSettings.GetInt("hashResumeSpeed", -1);
                if (speed < 0 || speed > 3 || w == null || w.Disposed) { return; }
                bool resumed = _hashPauseIssued;
                if (resumed)
                {
                    // Repay the pause this hash issued. forceResume:true on
                    // purpose: the pause counter is shared - a modal/
                    // notification Pause() (e.g. project-complete at the
                    // settle moment) can leave it >0 even after we repay our
                    // increment, and then a scripted run freezes forever with
                    // nobody to click the modal away. resumeSpeed is a
                    // scripted-run knob (default -1 = off), so "must actually
                    // run" is the requested semantic - same pattern as
                    // SaveReceiver's post-activation resume.
                    _hashPauseIssued = false;
                    w.HandleEvent(TimeControlCommand.Resume(forceResume: true));
                }
                w.HandleEvent(speed == 0
                    ? TimeControlCommand.Pause()
                    : TimeControlCommand.SetSpeedTo(speed));
                Log.Info("hash: host speed set to " + speed
                    + (resumed ? " +resumed" : string.Empty));
            }
            catch (Exception ex) { Log.Exception("hash resume", ex); }
        }

        private static int QuietMs()
        {
            int v = DebugSettings.GetInt("hashQuietMs", DefaultQuietMs);
            return v < 50 ? 50 : v;
        }

        private static int SettleMs()
        {
            int v = DebugSettings.GetInt("hashSettleMs", DefaultSettleMs);
            return v < 500 ? 500 : v;
        }

        private static int RespTimeoutMs()
        {
            int v = DebugSettings.GetInt("hashRespTimeoutMs", DefaultRespTimeoutMs);
            return v < 1000 ? 1000 : v;
        }

        private static int ReqTimeoutMs()
        {
            int v = DebugSettings.GetInt("hashReqTimeoutMs", DefaultReqTimeoutMs);
            return v < 1000 ? 1000 : v;
        }
    }
}
