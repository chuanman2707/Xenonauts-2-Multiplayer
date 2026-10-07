using System;
using System.Collections.Generic;

namespace X2Coop
{
    /// <summary>
    /// Dev/debug settings reader for `x2coop.*` keys. Two sources, command line
    /// wins over environment:
    ///
    ///   command line:   -x2coop.&lt;key&gt;=&lt;value&gt;   or   -x2coop.&lt;key&gt; &lt;value&gt;
    ///                   (a bare -x2coop.&lt;key&gt; with no '=' and no following
    ///                   value reads as "true")
    ///   environment:    variable named  x2coop.&lt;key&gt;
    ///                   (Windows env names are case-insensitive; dots are legal)
    ///
    /// Deliberately NOT the game's SettingsManager: these are dev overrides that
    /// must apply before any profile/settings file is loaded, must differ per
    /// process (two-instance testing sets per-instance command-line args), and
    /// must never be persisted into the player's settings.
    ///
    /// Known keys (all optional):
    ///   x2coop.fakeModVer=&lt;semver&gt;       Hello reports this instead of
    ///                                       ModInfo.ModVersion (verify: forced
    ///                                       version-mismatch reject).
    ///   x2coop.fakePackStamp=&lt;uid[@ver]&gt;  Appends a synthetic pack stamp to
    ///                                       Hello's stamp list (verify: forced
    ///                                       pack-mismatch reject).
    ///   x2coop.playerName=&lt;name&gt;          Overrides the Hello playerName
    ///                                       (never logged).
    ///   x2coop.joinRetryMs=&lt;ms&gt;           Guest re-sends Hello every N ms
    ///                                       after a reject while still
    ///                                       connected (0 = off; lets a dev
    ///                                       guest wait out "not on Strategy").
    ///   x2coop.ackTimeoutMs=&lt;ms&gt;            Guest AwaitingAck bound: no HelloAck
    ///                                       within N ms drops back to Idle
    ///                                       (default 10000, 0 = off). T2.5.
    ///   x2coop.stopHostAfterMs=&lt;ms&gt;         Host auto-ends the session N ms
    ///                                       after the listener comes up via the
    ///                                       graceful path (HostEnded, then
    ///                                       transport stop; 0 = off). T2.5
    ///                                       scripted-teardown verify.
    ///   x2coop.panel=open|true|1          T2.4 co-op panel starts visible
    ///                                       (F6 still toggles). For scripted
    ///                                       two-instance screenshot verifies.
    ///   x2coop.offerDelayMs=&lt;ms&gt;            T3.2 delay between HelloAck-accept
    ///                                       and the SaveOffer send (default
    ///                                       150; the ack rides a different
    ///                                       channel and must land first).
    ///   x2coop.joinFreezeLogMs=&lt;ms&gt;         T3.2 join-freeze "geodate="
    ///                                       heartbeat interval (default
    ///                                       5000, 0 = off) - proof the host
    ///                                       clock is held.
    ///   x2coop.forceSpeedOnJoinFreeze=&lt;0-3&gt; T3.2 scripted "host clicks Play
    ///                                       mid-join": fires the geoscape
    ///                                       TimeControlCommand.SetSpeedTo
    ///                                       right after the freeze latches
    ///                                       (-1 = off). Verify knob only.
    ///   x2coop.mirrorFreezeLogMs=&lt;ms&gt;       T3.4 guest-side "mirror-freeze
    ///                                       geodate=" heartbeat interval
    ///                                       while the mirror is Active (and
    ///                                       ~30 s of post-release resume
    ///                                       sampling). Default 5000, 0 = off.
    ///                                       The host's in-session
    ///                                       "role=host" heartbeat shares the
    ///                                       joinFreezeLogMs cadence.
    ///   x2coop.mirrorForceSpeed=&lt;0-3&gt;       T3.4 scripted "guest clicks a
    ///                                       geoscape speed button" fired at
    ///                                       mirror activation (Resume(true)
    ///                                       + SetSpeedTo - the UI's own
    ///                                       pair). -1 = off. Verify knob:
    ///                                       the frozen clock must ignore it.
    ///   x2coop.probeSaveSuppress=1        T3.5 verify knob: at mirror
    ///                                       activation the guest fires every
    ///                                       suppressed SaveGameSystem entry
    ///                                       point once (autosave request,
    ///                                       direct save, queued save, flush
    ///                                       command, drain, LoadGameCommand,
    ///                                       static LoadGame, F9 shortcut);
    ///                                       each must log "blocked &lt;method&gt;"
    ///                                       and touch nothing on disk; plus a
    ///                                       ContentManager.Delete probe on a
    ///                                       sacrificial probe file under
    ///                                       Saves\ (must survive). The
    ///                                       ironman hidden copy is patched
    ///                                       but deliberately not probed (its
    ///                                       unblocked fallback deletes
    ///                                       files).
    ///   x2coop.mirrorLoadTimeoutMs=&lt;ms&gt;     T3.3 bound on the guest mirror
    ///                                       load: LoadGame -&gt; new
    ///                                       StrategyWorld created -&gt; populated
    ///                                       (clock entity) -&gt; GuestReady must
    ///                                       complete within N ms or the
    ///                                       receiver fails the load
    ///                                       (ResyncReq, then GuestReady
    ///                                       {LoadFailed} after the resync
    ///                                       budget). Default 120000, 0 = off.
    ///   x2coop.resyncMinutes=&lt;min&gt;          T3.6 periodic resync: while the
    ///                                       guest is in-session with an
    ///                                       Active mirror and the host is on
    ///                                       strategy, send ResyncReq every N
    ///                                       minutes (fractions allowed, e.g.
    ///                                       0.1 = 6 s). Default 0 = OFF. The
    ///                                       clock restarts on every mirror
    ///                                       activation, so a resync in flight
    ///                                       can never stack requests. D2/D3
    ///                                       verify knob; off in normal play.
    ///   x2coop.resyncReqMinMs=&lt;ms&gt;          T3.6-r1 debounce floor between
    ///                                       external ResyncReq sends (button,
    ///                                       timer, HostReturned). Default
    ///                                       1500. Single-flight guards
    ///                                       (in-flight stream + outstanding
    ///                                       request) sit ABOVE this and can
    ///                                       never be relaxed by the knob.
    ///   x2coop.firewallLogOnly=1        T4.1 degrade switch (spec's D3
    ///                                       floor): the mirror firewall still
    ///                                       counts + logs rule-matched
    ///                                       mutations (leaks counted as
    ///                                       "unwhitelisted") but lets them
    ///                                       apply - use if enforced blocking
    ///                                       destabilizes the guest beyond
    ///                                       the auto-resync rate.
    ///   x2coop.resyncReqStaleMs=&lt;ms&gt;        T3.6-r1: an outstanding ResyncReq
    ///                                       with no answering SaveOffer after
    ///                                       N ms releases the single-flight
    ///                                       latch (host silently aborted the
    ///                                       queued offer). Default 30000 =
    ///                                       offerReadyTimeoutMs (15 s) +
    ///                                       transit slack.
    ///   x2coop.guestReadyMs=&lt;ms&gt;            REMOVED (T3.3): the T3.2 stub used
    ///                                       it to fake GuestReady without a
    ///                                       loader; the real receiver sends
    ///                                       GuestReady on mirror activation
    ///                                       and the knob is ignored.
    ///   x2coop.captureLogMs=&lt;ms&gt;            T5.1 host capture verify line
    ///                                       interval ("capture ops=..."
    ///                                       while a guest holds the session
    ///                                       and the host is on Strategy).
    ///                                       Default 1000, 0 = off.
    ///   x2coop.captureCreateDump=1          T5.1-r1: per-create detail line
    ///                                       ("capture: create+enrich eid=N
    ///                                       comps=M [types,...]") for the
    ///                                       template-create enrichment fix.
    ///                                       Default 0 = off (per-second
    ///                                       enr= counter still runs).
    ///   x2coop.deltaSendMs=&lt;ms&gt;             T5.2 reliable-delta emit tick
    ///                                       (default 80 = 12.5 Hz, clamped
    ///                                       20..1000 - the spec's fixed
    ///                                       10-15 Hz band).
    ///   x2coop.deltaPosMs=&lt;ms&gt;              T5.2 latest-state PosDelta lane
    ///                                       cadence (default 500 = 2 Hz,
    ///                                       clamped 250..5000 - the spec's
    ///                                       1-2 Hz band).
    ///   x2coop.deltaHeavyBytes=&lt;n&gt;          T5.2 component payloads larger
    ///                                       than N bytes are rate-capped to
    ///                                       one snapshot per entity per
    ///                                       deltaHeavyMs (default 1024).
    ///   x2coop.deltaHeavyMs=&lt;ms&gt;            T5.2 heavy-type rate cap
    ///                                       interval (default 1000, clamped
    ///                                       50..60000).
    ///   x2coop.deltaJoinCapKB=&lt;KB&gt;          T5.2 join-window delta buffer
    ///                                       cap (default 5520 ~= R9 30 s
    ///                                       speed-3 budget; 1024..65536).
    ///                                       Overflow drops the buffer and
    ///                                       forces a resync at GuestReady.
    ///   x2coop.deltaStatsMs=&lt;ms&gt;            T5.2 "delta-stats bps=..." host
    ///                                       verify line interval (default
    ///                                       1000, 0 = off) - the live
    ///                                       bandwidth evidence.
    ///   x2coop.deltaLogEach=0               T5.2 per-delta "delta seq=...
    ///                                       ops=... bytes=..." line (default
    ///                                       1 = on; the required verify
    ///                                       evidence).
    ///   x2coop.applyLogEach=&lt;0|1&gt;            T5.3 per-delta "apply seq=...
    ///                                       ops=... latencyMs=..." and
    ///                                       "apply-pos ..." guest apply
    ///                                       lines (default 1 = on).
    ///   x2coop.applyOpsPerFrame=&lt;n&gt;          T5.3 reliable-lane op budget
    ///                                       per frame drain (default 4096;
    ///                                       the join-flush backlog spreads
    ///                                       across frames instead of
    ///                                       stalling one).
    ///   x2coop.applyPendingCap=&lt;n&gt;           T5.3 queued StateDelta
    ///                                       messages held while no Active
    ///                                       mirror exists (default 4096;
    ///                                       over-cap poisons the stream
    ///                                       into a resync).
    ///   x2coop.applyStatsMs=&lt;ms&gt;             T5.3 "apply-stats ops=..."
    ///                                       guest verify line interval
    ///                                       (default 1000, 0 = off).
    ///   x2coop.applyDelayPoisonMs=&lt;ms&gt;       T5.3 watchdog: a mirror world
    ///                                       stuck IsMutationDelayed longer
    ///                                       than this poisons the stream
    ///                                       into resync (default 30000; a
    ///                                       crashed mutation leaks the flag
    ///                                       forever). An op that leaks the
    ///                                       flag itself poisons instantly.
    ///   x2coop.cascadeLogMs=&lt;ms&gt;             T5.4 guest cascade-measurement
    ///                                       line interval ("cascade ops=...
    ///                                       ser=... types=..."): inside an
    ///                                       apply scope, every mirror-world
    ///                                       mutation that is not the
    ///                                       in-flight op's own write is a
    ///                                       listener cascade - counted by
    ///                                       class and component type.
    ///                                       Default 5000, 0 = off.
    ///   x2coop.cascadeFirewall=&lt;0|1&gt;         T5.4 per-op scope enforcement:
    ///                                       0 = measure only (cascades pass),
    ///                                       1 = drop serializable
    ///                                       non-whitelisted cascades + other
    ///                                       entities' deletes inside the
    ///                                       apply scope (the host streams
    ///                                       its own cascade results anyway).
    ///                                       Default 1 - the T5.4 measurement
    ///                                       saw 100% serializable cascades
    ///                                       (MODLOG T5.4); 0 = measure only.
    ///   x2coop.joinReadyTimeoutMs=&lt;ms&gt;      T5.2-r1 host join-freeze
    ///                                       watchdog: no GuestReady within
    ///                                       N ms of a (re-)stream drops the
    ///                                       stalled guest and releases the
    ///                                       host clock. Default 60000,
    ///                                       0 = off. Re-arms per stream.
    ///   x2coop.debugMarkCleanup=&lt;n&gt;         T5.2-r1 reproduction knob:
    ///                                       before serializing the streamed
    ///                                       save, create n empty entities
    ///                                       carrying MarkedForCleanupGroup
    ///                                       (even n: +MarkedForDeletionGroup)
    ///                                       so the guest's mirror activation
    ///                                       provably survives marker
    ///                                       entities. Off by default.
    ///   x2coop.hashPause=&lt;0|1&gt;               T5.5 HashNow pauses the host
    ///                                       world first via
    ///                                       TimeControlCommand.Pause (the
    ///                                       guest mirror's clock is frozen
    ///                                       whenever Active, so pausing the
    ///                                       host is what makes the pair
    ///                                       quiesced). Default 1.
    ///   x2coop.hashQuietMs=&lt;ms&gt;             T5.5 settle window: fire once no
    ///                                       hash-visible delta is pending
    ///                                       AND LastDeltaSeq has been stable
    ///                                       this long (default 400).
    ///   x2coop.hashSettleMs=&lt;ms&gt;            T5.5 settle timeout - hash anyway
    ///                                       with a warn (default 15000).
    ///   x2coop.hashRespTimeoutMs=&lt;ms&gt;       T5.5 host bound on awaiting the
    ///                                       guest's HashResp (default 30000).
    ///   x2coop.hashReqTimeoutMs=&lt;ms&gt;        T5.5 guest bound on reaching the
    ///                                       requested applied seq (default
    ///                                       30000; a req whose seq never
    ///                                       arrives drops).
    ///   x2coop.hashOnReadyMs=&lt;ms&gt;           T5.5 scripted verify: the host
    ///                                       auto-issues a HashNow N ms after
    ///                                       every GuestReady (covers join AND
    ///                                       resync). Default 0 = off.
    ///   x2coop.hashDump=&lt;0|1|2&gt;             T5.5 item dump writes
    ///                                       x2coop-hashdump-{role}-seq{N}.txt
    ///                                       next to the x2coop log: 0 = off,
    ///                                       1 = guest-on-respond + host-on-
    ///                                       mismatch (the pair-diff inputs),
    ///                                       2 = always. Default 1.
    ///   x2coop.hashDumpLevel=&lt;1|2&gt;          T5.5 dump detail: 1 = entityId +
    ///                                       type + item fingerprint per line;
    ///                                       2 = + full canonical text (enables
    ///                                       member-path diff output).
    ///                                       Default 1.
    ///   x2coop.hashMaxDiffs=&lt;n&gt;             T5.5 cap on logged divergent
    ///                                       (entity,type) pairs per mismatch
    ///                                       (default 16).
    ///   x2coop.hashSkipTypes=&lt;csv&gt;          T5.5 type FullNames excluded from
    ///                                       the hash (comma/semicolon list) -
    ///                                       for types a real mismatch proves
    ///                                       nondeterministic; each needs a
    ///                                       documented reason in the report.
    ///   x2coop.hashSkipMembers=&lt;csv&gt;        T5.5 "Type.FullName:member.path"
    ///                                       entries stripped from the fsData
    ///                                       tree before canonicalization -
    ///                                       for member fields proven
    ///                                       nondeterministic.
    ///   x2coop.hashResumeSpeed=&lt;0-3&gt;         T5.5 paired with hashPause: set
    ///                                       geoscape speed (SetSpeedTo) once
    ///                                       the host hash fired, so scripted
    ///                                       runs pause for the snapshot and
    ///                                       resume without input. -1 = off
    ///                                       (default; game stays paused).
    ///   x2coop.autoLoadSave=&lt;name|path&gt;     T5.5 scripted-verify knob
    ///                                       (X2COOP_PROBES/Debug builds):
    ///                                       LoadGame on first reaching the
    ///                                       main menu - the host needs a
    ///                                       live Strategy world with zero
    ///                                       input driving. Bare name =
    ///                                       Constants.Folders.Save()+name;
    ///                                       rooted path used verbatim.
    ///
    /// <see cref="LogActive"/> logs which keys are SET - values are masked for
    /// keys that can carry PII (anything containing "name").
    /// </summary>
    public static class DebugSettings
    {
        private const string Prefix = "x2coop.";
        private const string ArgPrefix = "-" + Prefix;

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _args;   // null until first read
        private static bool _logged;

        /// <summary>Setting value or null when unset. Command line beats env.</summary>
        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) { return null; }
            string v;
            lock (Gate)
            {
                if (_args == null) { _args = ParseArgs(Environment.GetCommandLineArgs()); }
                if (_args.TryGetValue(key, out v)) { return v; }
            }
            try
            {
                v = Environment.GetEnvironmentVariable(Prefix + key);
            }
            catch
            {
                v = null;
            }
            return string.IsNullOrEmpty(v) ? null : v;
        }

        /// <summary>Int setting or <paramref name="defaultValue"/> when unset/unparsable.</summary>
        public static int GetInt(string key, int defaultValue)
        {
            string v = Get(key);
            int n;
            if (v != null && int.TryParse(v, out n)) { return n; }
            return defaultValue;
        }

        /// <summary>Float setting (invariant culture) or <paramref name="defaultValue"/> when unset/unparsable.</summary>
        public static float GetFloat(string key, float defaultValue)
        {
            string v = Get(key);
            float n;
            if (v != null && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out n)) { return n; }
            return defaultValue;
        }

        /// <summary>
        /// Log every x2coop.* override found in BOTH sources, once per process.
        /// Values are masked for keys containing "name" (PII-safe).
        /// </summary>
        public static void LogActive()
        {
            try
            {
                lock (Gate)
                {
                    if (_logged) { return; }
                    _logged = true;
                }
                var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> args;
                lock (Gate)
                {
                    if (_args == null) { _args = ParseArgs(Environment.GetCommandLineArgs()); }
                    args = _args;
                }
                foreach (KeyValuePair<string, string> kv in args) { seen[kv.Key] = kv.Value; }
                try
                {
                    foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
                    {
                        string name = de.Key as string;
                        if (name != null && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                            && name.Length > Prefix.Length)
                        {
                            string k = name.Substring(Prefix.Length);
                            if (!seen.ContainsKey(k))
                            {
                                seen[k] = de.Value as string;
                            }
                        }
                    }
                }
                catch
                {
                    // Environment block unreadable - command line still reported.
                }
                foreach (KeyValuePair<string, string> kv in seen)
                {
                    Log.Info("dev setting " + Prefix + kv.Key + "=" + SafeValue(kv.Key, kv.Value));
                }
            }
            catch (Exception ex)
            {
                Log.Exception("DebugSettings.LogActive", ex);
            }
        }

        /// <summary>
        /// T2.6 pack teardown: drop the parsed command-line cache and the
        /// logged-once flag. Both rebuild lazily - a re-enabled pack re-reads
        /// its args (identical per process) and re-logs the active keys.
        /// </summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _args = null;
                _logged = false;
            }
        }

        /// <summary>Masks values of keys that can carry PII.</summary>
        internal static string SafeValue(string key, string value)
        {
            if (key != null && key.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return value == null ? null : "<set>";
            }
            return value;
        }

        /// <summary>
        /// Parse an argv array into a key-&gt;value map (keys without the
        /// x2coop. prefix are ignored). Internal for tests.
        /// </summary>
        internal static Dictionary<string, string> ParseArgs(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args == null) { return map; }
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null || !a.StartsWith(ArgPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string rest = a.Substring(ArgPrefix.Length);
                int eq = rest.IndexOf('=');
                string key;
                string value;
                if (eq >= 0)
                {
                    key = rest.Substring(0, eq);
                    value = rest.Substring(eq + 1);
                }
                else if (i + 1 < args.Length && args[i + 1] != null
                    && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    key = rest;
                    value = args[i + 1];
                    i++;
                }
                else
                {
                    key = rest;
                    value = "true";
                }
                if (key.Length > 0) { map[key] = value; }
            }
            return map;
        }
    }
}
