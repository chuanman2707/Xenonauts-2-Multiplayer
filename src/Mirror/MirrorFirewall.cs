using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using Artitas;
using Artitas.Components;
using HarmonyLib;
using UnityEngine;

namespace X2Coop.Mirror
{
    /// <summary>
    /// T4.1 guest mutation firewall (spec section 4 "MirrorMode", item 2).
    /// While the guest's mirror world is live -
    /// <see cref="MirrorState.Active"/> AND __instance IS
    /// <see cref="Session.SaveReceiver.MirrorWorld"/> AND NOT inside a
    /// <see cref="Replication.StateApplier"/> scope (T5.3 carve-out, plan
    /// ruling 9; T5.4 refines the carve-out to the in-flight op's own armed
    /// mutation key - inside a scope, non-matching mirror-world mutations are
    /// "cascades", counted per type and dropped when serializable +
    /// non-whitelisted under x2coop.cascadeFirewall - default ON after the
    /// T5.4 measurement showed every cascade was a serializable
    /// double-apply) - guest-local writes into replicated state are
    /// dropped at the mutation sink:
    ///
    ///   - PREFIX World.ApplyComponentMutation(int,int,IComponent,IComponent,
    ///     bool) - private, non-generic; EVERY component add/remove/update
    ///     funnels here (immediate via MutateComponentOnEntity, delayed via
    ///     ApplyDelayedMutation). A mutation is dropped (prefix returns
    ///     false) iff ALL of:
    ///       * componentID resolves to a Type (world.ComponentManager
    ///         .FindComponentType - the R6 reverse map;
    ///         ComponentManager.cs:52 -> ComponentLookup._idToType),
    ///       * the type is serializable (no [ExcludeFromSerialization] on the
    ///         class - WorldState.cs:77 never writes it to host state, so a
    ///         guest-local change to it can never desync),
    ///       * the type is NOT in <see cref="Whitelist"/> (R6's measured
    ///         presentation/read-model set - UI listeners, camera, playtime
    ///         timer, screen-state bookkeeping),
    ///       * the entity does NOT carry ExcludeFromSerializationComponent
    ///         (WorldState.cs:131 skips those entities wholesale - they are
    ///         already guest-local by construction).
    ///     Unresolvable componentIDs fail OPEN (counted, allowed) - blocking
    ///     a mutation we cannot classify is riskier than letting it through.
    ///
    ///   - PREFIXES World.DeleteEntityImmediately(int) + World.UnsafeDeleteEntity
    ///     (int) - the two delete sinks (World.DeleteEntity's delayed path
    ///     funnels into DeleteEntityImmediately via ApplyDelayedMutation, so
    ///     it needs no patch of its own). Same rule minus the type terms:
    ///     an entity carrying the exclusion marker is guest-local and its
    ///     delete is allowed through; anything else is dropped. Both are
    ///     void-returning so a false prefix is a clean skip - no __result.
    ///
    ///   - World.CreateEntity() is NOT blocked (a false prefix would NRE the
    ///     caller). A postfix instead records the guest-local entity id in
    ///     <see cref="GuestCreatedIds"/> and logs it - the tolerate-and-log
    ///     defense for pooled/crafted guest entities the whitelist cannot
    ///     cover (R6 saw +3 creates/+52 obtains while paused).
    ///
    ///   - Exception wrap, two layers:
    ///     a) FINALIZERS on ProcessStrategy.ProcessEvent (Default + Debug
    ///        overrides - the common dispatch for BOTH World.Process's queued
    ///        drain and World.HandleEvent's immediate path) and on
    ///        World.Process itself. Live verify showed why the tap alone is
    ///        not enough: XenonautsMain.Update treats the FIRST exception
    ///        escaping the screen pump as fatal (bug-report zip + crash
    ///        modal), so a 3-strike counter fed only by logMessageReceived
    ///        can never accumulate. A finalizer that swallows a CORRELATED
    ///        exception (mirror world, block within the window, game-code
    ///        stack) turns that fatal hit into a counted one: the failed
    ///        subscriber's event is already dequeued, the queue keeps
    ///        draining, and the Tick pump can accumulate hits toward the
    ///        resync threshold. Uncorrelated exceptions are passed through
    ///        untouched; logOnly mode never swallows.
    ///     b) Application.logMessageReceived tap (unchanged) - catches
    ///        correlated LogType.Exception reports that escape via channels
    ///        outside the world pump (UI entry points, engine callbacks).
    ///     At <see cref="ExceptionResyncThreshold"/> cumulative hits (and
    ///     every +threshold after) the Tick pump auto-requests a resync via
    ///     SessionClient.RequestResync - the host re-streams and the mirror
    ///     heals instead of drifting on a half-applied action. The
    ///     logMessageReceived handler NEVER logs or touches UnityEngine APIs
    ///     (it runs inside Unity's log dispatch - a Debug.Log there would
    ///     recurse), it only counts; all reporting happens in
    ///     <see cref="Tick"/> on the Runner pump.
    ///
    /// Logging: every drop logs "firewall: blocked &lt;kind&gt; &lt;type&gt; ..."
    /// plus the top 8 frames of new StackTrace(2, false) - rate-limited to 1
    /// line per key per 5 s (key = component FullName for mutations, the
    /// method name for deletes, "create" for creates). Counters ALWAYS
    /// increment regardless of log rate.
    ///
    /// Degrade path: x2coop.firewallLogOnly=1 turns enforcement into
    /// count+log only (prefix returns true) - the D3-fallback escape hatch
    /// if the firewall itself proves unstable (spec section 5).
    ///
    /// Fails OPEN everywhere: a throw inside any prefix/postfix/handler logs
    /// once and lets the mutation through (a stuck prefix would break the
    /// host's world too if our gate logic ever faulted).
    /// Not probe-gated: ships in Release.
    /// </summary>
    public static class MirrorFirewall
    {
        /// <summary>Rate limit on the "blocked" log line: 1 per key per 5 s.</summary>
        private const int LogRateLimitMs = 5000;

        /// <summary>An exception counts as firewall-caused only if a block landed within this window.</summary>
        private const int ExceptionCorrelationMs = 10000;

        /// <summary>Cumulative correlated-exception hits that trigger an auto ResyncReq.</summary>
        private const int ExceptionResyncThreshold = 3;

        /// <summary>Bound on the recent-blocked-types ring used for exception correlation.</summary>
        private const int MaxRecentBlocks = 64;

        /// <summary>Bound on the guest-local created-id set (diagnostic; ids recycle anyway).</summary>
        private const int MaxTrackedCreates = 8192;

        /// <summary>Frames logged per blocked line.</summary>
        private const int MaxFrames = 8;

        // ---- counters (T4.2's scripted-run summary reads these) -------------
        /// <summary>ApplyComponentMutation calls dropped by the firewall.</summary>
        public static long BlockedMutations;
        /// <summary>DeleteEntityImmediately/UnsafeDeleteEntity calls dropped.</summary>
        public static long BlockedDeletes;
        /// <summary>Rule-matched mutations/deletes ALLOWED through (logOnly mode or fault path). Spec criterion 5's leak counter - 0 while enforcing.</summary>
        public static long LeakedUnwhitelisted;
        /// <summary>World.CreateEntity calls observed on the mirror world.</summary>
        public static long GuestLocalCreates;
        /// <summary>Guest exceptions correlated with dropped mutations.</summary>
        public static int ExceptionHits;
        /// <summary>Subset of <see cref="ExceptionHits"/> swallowed by the ProcessEvent/Process finalizers (the rest arrived via the log tap).</summary>
        public static int SwallowedExceptions;
        /// <summary>Auto resyncs requested by the exception wrap.</summary>
        public static int AutoResyncs;
        /// <summary>Mirror-world mutations whose componentID did not resolve to a Type (allowed - cannot classify).</summary>
        public static long UntypedMutations;

        // ---- T5.4 in-scope cascade accounting (r2 C3) ------------------------
        // While StateApplier.InScope the firewall classifies every mirror-world
        // ApplyComponentMutation against the in-flight op (Replication.
        // InFlightOp): Direct = the op's own terminal mutation, Internal =
        // applier bookkeeping (resolve/deserialize/placeholder-create phase),
        // Cascade = anything else (listener writes, delayed-stack drain,
        // between-ops strays). Cascades split by class so the per-op-vs-window
        // decision is data-driven: CascadeSerializable is THE number - those
        // are guest-side writes to types the host also serializes (its own
        // cascade results arrive as ops later), i.e. double-apply candidates.
        /// <summary>In-scope mutations matching the in-flight op's armed key.</summary>
        public static long DirectMutations;
        /// <summary>In-scope mutations during an op's unarmed bookkeeping phase.</summary>
        public static long InternalMutations;
        /// <summary>In-scope mutations matching nothing - listener cascades.</summary>
        public static long CascadeMutations;
        /// <summary>Cascades on R6 whitelist types (always allowed - presentation state).</summary>
        public static long CascadeWhitelisted;
        /// <summary>Cascades on [ExcludeFromSerialization] types (guest-local by construction).</summary>
        public static long CascadeExcludedType;
        /// <summary>Cascades on entities carrying ExcludeFromSerializationComponent.</summary>
        public static long CascadeExcludedEntity;
        /// <summary>Cascades on serializable non-whitelisted types - what per-op scope drops.</summary>
        public static long CascadeSerializable;
        /// <summary>Cascades whose componentID did not resolve to a Type (allowed, counted).</summary>
        public static long CascadeUntyped;
        /// <summary>Op-own deletes that matched the armed delete key.</summary>
        public static long DirectDeletes;
        /// <summary>In-scope deletes on other entities - listener cascades.</summary>
        public static long CascadeDeletes;
        /// <summary>Applier-internal CreateEntity calls (EntityIdMap placeholders etc).</summary>
        public static long InternalCreates;
        /// <summary>Listener-spawned CreateEntity inside an armed op mutation.</summary>
        public static long CascadeCreates;
        /// <summary>Serializable cascades actually dropped (cascadeFirewall=1 only).</summary>
        public static long CascadeBlockedMutations;
        /// <summary>Cascade deletes actually dropped (cascadeFirewall=1 only).</summary>
        public static long CascadeBlockedDeletes;

        // ---- per-session state ------------------------------------------------
        private static readonly Dictionary<string, int> LastLogTick = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, bool> ExclAttrByType = new Dictionary<Type, bool>();
        private static readonly HashSet<int> _guestCreatedIds = new HashSet<int>();
        private static readonly List<string> _recentBlockTypes = new List<string>(MaxRecentBlocks);
        private static readonly List<int> _recentBlockTicks = new List<int>(MaxRecentBlocks);
        private static World _exclWorld;          // marker-id cache, single slot (only the mirror world is ever gated)
        private static int _exclCompId = -2;      // -2 = unresolved, -1 = not registered on _exclWorld
        private static World _mirrorSeen;         // last world that passed Gated() - survives release so teardown-time exceptions still recognise it
        private static int _createsSinceLog;
        private static bool _logOnly;
        private static int _logOnlyCachedTick;

        // ---- T5.4 cascade per-type tallies + summary emit --------------------
        private const int MaxCascadeTypes = 256;   // bound on each tally dict
        private const int CascadeTopN = 5;         // types shown per summary line
        private const int DefaultCascadeLogMs = 5000;   // x2coop.cascadeLogMs (0 = off)
        // T5.4 live measurement decided per-op enforcement (MODLOG T5.4): the
        // r2 C3 run saw 100% of cascades land on serializable non-whitelisted
        // types (DurationComponent ~98%, AirCombatAmmo/Armor), i.e. every
        // cascade is a double-apply candidate the host stream resends anyway.
        private const int DefaultCascadeEnforce = 1;    // x2coop.cascadeFirewall (1 = per-op drops)
        private static readonly Dictionary<string, long> _cascadeTypes = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> _cascadeSerTypes = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly System.Diagnostics.Stopwatch _cascadeWatch = new System.Diagnostics.Stopwatch();
        private static long _cascadeLastTotal;
        private static bool _casEnforce;
        private static int _casEnforceCachedTick;

        // ---- exception-wrap state (written inside logMessageReceived) --------
        private static bool _logHookInstalled;
        private static bool _inLogHandler;      // reentrancy guard: a Log.* inside the handler would recurse through Debug.Log
        private static int _mainThreadId;
        private static volatile int _lastBlockTick; // Environment.TickCount of the most recent block
        private static int _pendingHits;         // correlated exceptions not yet reported by Tick
        private static string _pendingSample;    // first line of the newest correlated exception
        private static int _hitsAtLastResync;
        private static int _processSwallowTick = -1; // last tick a Process-level swallow fired (drain-loop bound)

        /// <summary>Ids of entities created on the mirror world while Active (guest-local). Exposed for T5.x.</summary>
        public static IReadOnlyCollection<int> GuestCreatedIds => _guestCreatedIds;

        /// <summary>
        /// One-line dump for the T4.2 scripted-run END marker:
        /// "firewall summary: unwhitelisted=&lt;leaks&gt; blocked=...".
        /// `unwhitelisted` is the LEAK count (rule-matched mutations that were
        /// applied anyway) - spec criterion 5 wants 0.
        /// </summary>
        public static string Summary()
        {
            return "firewall summary: unwhitelisted=" + LeakedUnwhitelisted
                + " blockedMuts=" + BlockedMutations
                + " blockedDeletes=" + BlockedDeletes
                + " guestCreates=" + GuestLocalCreates
                + " untyped=" + UntypedMutations
                + " exceptions=" + ExceptionHits
                + " swallowed=" + SwallowedExceptions
                + " autoResyncs=" + AutoResyncs
                + " blockedPicks=" + UIPickGate.BlockedPickEntries
                + " mirrorSysSkips=" + MirrorSystems.SkippedTotal
                + " cascades=" + CascadeMutations
                + " cascadeSer=" + CascadeSerializable
                + " cascadeBlocked=" + (CascadeBlockedMutations + CascadeBlockedDeletes)
                + " cascadeFw=" + (CascadeEnforce() ? 1 : 0)
                + " logOnly=" + (LogOnly() ? 1 : 0)
                + " whitelist=" + Whitelist.Count;
        }

        /// <summary>Resolve targets + install the exception tap. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("firewall: Apply called with null patcher");
                return;
            }
            PatchOn(patcher, typeof(World), "ApplyComponentMutation",
                new[] { typeof(int), typeof(int), typeof(IComponent), typeof(IComponent), typeof(bool) },
                prefixName: nameof(MutationPrefix));
            PatchOn(patcher, typeof(World), "DeleteEntityImmediately", new[] { typeof(int) },
                prefixName: nameof(DeleteImmediatelyPrefix), finalizerName: nameof(DeleteFaultFinalizer));
            PatchOn(patcher, typeof(World), "UnsafeDeleteEntity", new[] { typeof(int) },
                prefixName: nameof(UnsafeDeletePrefix), finalizerName: nameof(DeleteFaultFinalizer));
            PatchOn(patcher, typeof(World), "CreateEntity", null,
                postfixName: nameof(CreatePostfix));
            PatchOn(patcher, typeof(World), "Process", Type.EmptyTypes,
                finalizerName: nameof(ProcessFinalizer));
            PatchOn(patcher, typeof(World), "Dispose",
                new[] { typeof(Func<IEnumerable<Entity>, IEnumerable<Entity>>) },
                finalizerName: nameof(DisposeFinalizer));
            // ProcessEvent is abstract on ProcessStrategy; each concrete
            // override needs its own finalizer (the mirror world runs the
            // Debug variant, other worlds the Default one).
            PatchOn(patcher, typeof(DefaultProcessStrategy), "ProcessEvent",
                new[] { typeof(IEvent) }, finalizerName: nameof(EventFinalizer));
            PatchOn(patcher, typeof(DebugProcessStrategy), "ProcessEvent",
                new[] { typeof(IEvent) }, finalizerName: nameof(EventFinalizer));
            InstallLogHook();
        }

        private static void PatchOn(Harmony patcher, Type targetType, string name,
            Type[] args, string prefixName = null, string postfixName = null,
            string finalizerName = null)
        {
            string tag = targetType.FullName + "." + name;
            try
            {
                MethodInfo original = args != null
                    ? AccessTools.Method(targetType, name, args)
                    : AccessTools.Method(targetType, name);
                if (original == null && args != null)
                {
                    // Fallback: overload resolution failed - try name-only.
                    original = AccessTools.Method(targetType, name);
                }
                if (original == null)
                {
                    Log.Warn("firewall: method not found: " + tag);
                    return;
                }
                var kinds = new List<string>(3);
                HarmonyMethod prefix = null, postfix = null, finalizer = null;
                if (prefixName != null)
                {
                    MethodInfo h = AccessTools.Method(typeof(MirrorFirewall), prefixName);
                    if (h == null) { Log.Warn("firewall: handler not found: " + prefixName); return; }
                    prefix = new HarmonyMethod(h);
                    kinds.Add("prefix");
                }
                if (postfixName != null)
                {
                    MethodInfo h = AccessTools.Method(typeof(MirrorFirewall), postfixName);
                    if (h == null) { Log.Warn("firewall: handler not found: " + postfixName); return; }
                    postfix = new HarmonyMethod(h);
                    kinds.Add("postfix");
                }
                if (finalizerName != null)
                {
                    MethodInfo h = AccessTools.Method(typeof(MirrorFirewall), finalizerName);
                    if (h == null) { Log.Warn("firewall: handler not found: " + finalizerName); return; }
                    finalizer = new HarmonyMethod(h);
                    kinds.Add("finalizer");
                }
                patcher.Patch(original, prefix: prefix, postfix: postfix, finalizer: finalizer);
                Log.Info("firewall: patch-applied " + tag + " (" + string.Join("+", kinds.ToArray()) + ")");
            }
            catch (Exception ex)
            {
                Log.Exception("firewall patch " + tag, ex);
            }
        }

        /// <summary>The exception tap - one subscribe per process; survives pack re-enable (idempotent).</summary>
        private static void InstallLogHook()
        {
            try
            {
                if (_logHookInstalled) { return; }
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                Application.logMessageReceived += OnUnityLog;
                _logHookInstalled = true;
                Log.Info("firewall: logMessageReceived tap installed");
            }
            catch (Exception ex)
            {
                Log.Exception("firewall log-hook install", ex);
            }
        }

        /// <summary>
        /// Per-session reset (SaveReceiver.Reset on session end / fresh join):
        /// counters, rate-limit map, created-id set, exception-wrap state.
        /// The log hook itself stays installed (Shutdown owns that).
        /// </summary>
        public static void Reset()
        {
            BlockedMutations = 0;
            BlockedDeletes = 0;
            LeakedUnwhitelisted = 0;
            GuestLocalCreates = 0;
            ExceptionHits = 0;
            SwallowedExceptions = 0;
            AutoResyncs = 0;
            UntypedMutations = 0;
            DirectMutations = InternalMutations = CascadeMutations = 0;
            CascadeWhitelisted = CascadeSerializable = 0;
            CascadeExcludedType = CascadeExcludedEntity = CascadeUntyped = 0;
            DirectDeletes = CascadeDeletes = InternalCreates = CascadeCreates = 0;
            CascadeBlockedMutations = CascadeBlockedDeletes = 0;
            _cascadeTypes.Clear();
            _cascadeSerTypes.Clear();
            _cascadeWatch.Reset();
            _cascadeLastTotal = 0;
            _casEnforceCachedTick = 0;
            LastLogTick.Clear();
            _guestCreatedIds.Clear();
            _recentBlockTypes.Clear();
            _recentBlockTicks.Clear();
            _exclWorld = null;
            _exclCompId = -2;
            _mirrorSeen = null;
            _createsSinceLog = 0;
            _lastBlockTick = 0;
            _pendingHits = 0;
            _pendingSample = null;
            _hitsAtLastResync = 0;
            _processSwallowTick = -1;
        }

        /// <summary>
        /// Pack teardown (T2.6 Teardown.Run): detach the Unity log tap - a
        /// dead pack must never leave a subscriber on a process-wide event -
        /// then reset session state. Harmony unpatching is the game's own
        /// UnpatchAll after Destroy, never ours.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                if (_logHookInstalled)
                {
                    Application.logMessageReceived -= OnUnityLog;
                    _logHookInstalled = false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("firewall log-hook detach", ex);
            }
            Reset();
        }

        /// <summary>
        /// The firewall's kill-switch (x2coop.firewallLogOnly=1): rule-matched
        /// mutations are counted + logged but let through - the D3-floor
        /// degrade if enforcement proves unstable. Cached ~1 s so the hot
        /// path doesn't re-read settings per mutation.
        /// </summary>
        private static bool LogOnly()
        {
            int now = Environment.TickCount;
            if (unchecked(now - _logOnlyCachedTick) < 1000 && _logOnlyCachedTick != 0)
            {
                return _logOnly;
            }
            _logOnlyCachedTick = now;
            _logOnly = DebugSettings.GetInt("firewallLogOnly", 0) != 0;
            return _logOnly;
        }

        /// <summary>
        /// The guest-mirror gate shared by all four patches: true only while
        /// __instance is the bound mirror world AND MirrorMode.Active AND not
        /// inside a StateApplier scope. MirrorWorld also resolves under
        /// PendingActivate - the explicit Active check keeps the populate
        /// phase (deserialize mutations) untouched.
        /// </summary>
        private static bool Gated(World world)
        {
            if (!MirrorState.Active) { return false; }
            if (Replication.StateApplier.InScope) { return false; }
            if (!ReferenceEquals(world, Session.SaveReceiver.MirrorWorld)) { return false; }
            _mirrorSeen = world;
            return true;
        }

        /// <summary>
        /// The finalizer gate - wider than <see cref="Gated"/>: a mirror-world
        /// exception is firewall-relevant at ANY mode (verified live: an
        /// orphaned delayed-mutation NRE'd inside the OLD mirror world's
        /// Dispose while Mode was already Receiving - SaveReceiver drops
        /// _mirrorWorld on a fresh offer, so the just-released world is
        /// matched through <see cref="_mirrorSeen"/>). The correlation window
        /// + game-code check still apply, so ambient faults pass through.
        /// </summary>
        private static bool Mirrorish(World world)
        {
            if (Replication.StateApplier.InScope) { return false; }
            return ReferenceEquals(world, Session.SaveReceiver.MirrorWorld)
                || ReferenceEquals(world, _mirrorSeen);
        }

        // ------------------------------------------------------------------
        // prefixes / postfix - all fail OPEN (a throwing Harmony patch would
        // corrupt the game's own mutation pipeline)
        // ------------------------------------------------------------------

        /// <summary>World.ApplyComponentMutation - the component add/remove/update sink.</summary>
        private static bool MutationPrefix(World __instance, int entityID,
            int componentID, IComponent previousC, IComponent nextC, bool added)
        {
            try
            {
                if (Replication.StateApplier.InScope)
                {
                    // T5.4 per-op scope: inside an apply drain the only free
                    // mutation is the in-flight op's OWN terminal write. Every
                    // other mirror-world mutation is classified against
                    // InFlightOp's armed key:
                    //   Direct   -> the op itself - pass silently.
                    //   Internal -> the applier's bookkeeping phase (resolve /
                    //               deserialize / placeholder-create) - pass,
                    //               counted apart so the cascade numbers stay
                    //               honest (blocking a factory add could leave
                    //               a placeholder malformed).
                    //   Cascade  -> a listener write triggered by the apply -
                    //               counted per type + class; when
                    //               x2coop.cascadeFirewall=1 the serializable
                    //               non-whitelisted ones drop here (the host
                    //               streams its own cascade results as ops).
                    if (!ReferenceEquals(__instance, Session.SaveReceiver.MirrorWorld))
                    {
                        return true; // not our world - old pass-through
                    }
                    _mirrorSeen = __instance; // same liveness marker Gated() sets
                    switch (Replication.InFlightOp.ClassifyMutation(entityID, componentID))
                    {
                        case Replication.InFlightOp.Class.Direct:
                            DirectMutations++;
                            return true;
                        case Replication.InFlightOp.Class.Internal:
                            InternalMutations++;
                            return true;
                        default:
                            return Cascade(__instance, entityID, componentID,
                                added, nextC);
                    }
                }
                if (!Gated(__instance)) { return true; }
                Type type = null;
                try
                {
                    type = __instance.ComponentManager?.FindComponentType(componentID);
                }
                catch
                {
                }
                if (type == null)
                {
                    // Unclassifiable - fail open but count it (probe data says ~0).
                    UntypedMutations++;
                    return true;
                }
                if (Whitelist.Contains(type)) { return true; }
                if (IsTypeExcluded(type)) { return true; }
                if (EntityExcluded(__instance, entityID)) { return true; }
                return Block("mutation", type.FullName ?? type.Name,
                    "entity=" + entityID + " added=" + added
                    + (nextC == null ? " (remove)" : string.Empty),
                    ref BlockedMutations);
            }
            catch (Exception ex)
            {
                Log.Exception("firewall mutation-prefix", ex);
            }
            return true;
        }

        /// <summary>World.DeleteEntityImmediately(int) - entity delete sink.</summary>
        private static bool DeleteImmediatelyPrefix(World __instance, int entityID)
        {
            try
            {
                if (Replication.StateApplier.InScope)
                {
                    return InScopeDelete(__instance, entityID, "DeleteEntityImmediately");
                }
                if (!Gated(__instance)) { return true; }
                if (EntityExcluded(__instance, entityID)) { return true; }
                return Block("delete", "DeleteEntityImmediately",
                    "entity=" + entityID, ref BlockedDeletes);
            }
            catch (Exception ex)
            {
                Log.Exception("firewall delete-prefix", ex);
            }
            return true;
        }

        /// <summary>World.UnsafeDeleteEntity(int) - the bypass-the-delay-stack delete.</summary>
        private static bool UnsafeDeletePrefix(World __instance, int entityID)
        {
            try
            {
                if (Replication.StateApplier.InScope)
                {
                    return InScopeDelete(__instance, entityID, "UnsafeDeleteEntity");
                }
                if (!Gated(__instance)) { return true; }
                if (EntityExcluded(__instance, entityID)) { return true; }
                return Block("delete", "UnsafeDeleteEntity",
                    "entity=" + entityID, ref BlockedDeletes);
            }
            catch (Exception ex)
            {
                Log.Exception("firewall unsafe-delete-prefix", ex);
            }
            return true;
        }

        /// <summary>
        /// T5.4 in-scope delete: the in-flight Delete op's own call (entity
        /// match, incl. internal re-entry like DeleteEntityImmediately ->
        /// UnsafeDeleteEntity on the same entity) passes; any OTHER entity's
        /// delete mid-apply is a listener cascade - counted, and gated like a
        /// cascade mutation when x2coop.cascadeFirewall=1.
        /// </summary>
        private static bool InScopeDelete(World world, int entityID, string sink)
        {
            if (!ReferenceEquals(world, Session.SaveReceiver.MirrorWorld))
            {
                return true; // not our world - old pass-through
            }
            if (Replication.InFlightOp.IsDirectDelete(entityID))
            {
                DirectDeletes++;
                return true;
            }
            CascadeDeletes++;
            if (EntityExcluded(world, entityID)) { return true; }
            if (!CascadeEnforce()) { return true; }
            return Block("cascade-delete", sink, "entity=" + entityID,
                ref CascadeBlockedDeletes);
        }

        /// <summary>
        /// World.CreateEntity postfix - never blocked (callers would NRE).
        /// Records the guest-local id and logs (rate-limited, with the
        /// suppressed-burst count folded into the next emitted line).
        /// </summary>
        private static void CreatePostfix(World __instance, Entity __result)
        {
            try
            {
                if (Replication.StateApplier.InScope)
                {
                    // T5.4: creates inside an apply scope are either applier
                    // bookkeeping (EntityIdMap placeholder / converter resolve
                    // - unarmed phase) or a listener spawning an entity inside
                    // an armed op mutation (cascade). Never blocked - a false
                    // prefix would NRE the caller anyway - just classified.
                    if (!ReferenceEquals(__instance, Session.SaveReceiver.MirrorWorld)
                        || __result == null)
                    {
                        return;
                    }
                    if (Replication.InFlightOp.ClassifyCreate()
                        == Replication.InFlightOp.Class.Cascade)
                    {
                        CascadeCreates++;
                        if (RateLimit("cascade-create"))
                        {
                            Log.Warn("cascade: listener create inside apply id="
                                + __result.ID + " (total=" + CascadeCreates
                                + ") - the host streams its own twin as a Create op"
                                + "\n" + TopFrames(new System.Diagnostics.StackTrace(3, false)));
                        }
                    }
                    else
                    {
                        InternalCreates++;
                    }
                    return;
                }
                if (!Gated(__instance) || __result == null) { return; }
                GuestLocalCreates++;
                _createsSinceLog++;
                if (_guestCreatedIds.Count < MaxTrackedCreates)
                {
                    _guestCreatedIds.Add(__result.ID);
                }
                if (RateLimit("create"))
                {
                    Log.Warn("firewall: guest-local create on mirror world id=" + __result.ID
                        + " (total=" + GuestLocalCreates + ", burst=" + _createsSinceLog + ")"
                        + "\n" + TopFrames(new System.Diagnostics.StackTrace(2, false)));
                    _createsSinceLog = 0;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("firewall create-postfix", ex);
            }
        }

        // ------------------------------------------------------------------
        // T5.4 cascade classification (inside StateApplier.Scope)
        // ------------------------------------------------------------------

        /// <summary>
        /// An in-scope ApplyComponentMutation that is NOT the in-flight op's
        /// direct write: a listener cascade (or between-ops stray). Counted
        /// always; the classification drives both the summary line and the
        /// optional per-op drop:
        ///   whitelisted   -> pass (R6 set - presentation/read-model types are
        ///                    free to churn; per-client state by definition)
        ///   type-excluded -> pass ([ExcludeFromSerialization] never reaches
        ///                    host state - a guest-only write can't desync)
        ///   entity-excluded -> pass (entity skipped wholesale by WorldState)
        ///   untyped       -> pass (fail open - cannot classify, do not block)
        ///   serializable  -> THE decision number. With x2coop.
        ///                    cascadeFirewall=1 these drop via Block() just
        ///                    like out-of-scope writes (kind "cascade"): the
        ///                    host ran the same listener and its result arrives
        ///                    as an op on the reliable stream.
        /// </summary>
        private static bool Cascade(World world, int entityID, int componentID,
            bool added, IComponent nextC)
        {
            CascadeMutations++;
            Type type = null;
            try
            {
                type = world.ComponentManager?.FindComponentType(componentID);
            }
            catch
            {
            }
            string key = type != null
                ? (type.FullName ?? type.Name)
                : ("cid#" + componentID);
            CountType(_cascadeTypes, key);
            if (type == null) { CascadeUntyped++; return true; }
            if (Whitelist.Contains(type)) { CascadeWhitelisted++; return true; }
            if (IsTypeExcluded(type)) { CascadeExcludedType++; return true; }
            if (EntityExcluded(world, entityID)) { CascadeExcludedEntity++; return true; }
            CascadeSerializable++;
            CountType(_cascadeSerTypes, key);
            if (!CascadeEnforce()) { return true; }
            return Block("cascade", key,
                "entity=" + entityID + " added=" + added
                + (nextC == null ? " (remove)" : string.Empty),
                ref CascadeBlockedMutations);
        }

        /// <summary>
        /// Per-op enforcement switch (x2coop.cascadeFirewall): 0 = measure
        /// only (cascades counted + passed - the T5.4 measurement mode), 1 =
        /// drop serializable non-whitelisted cascades at the mutation sink.
        /// Default set from the live measurement (MODLOG T5.4). Cached ~1 s
        /// like <see cref="LogOnly"/> - the prefix is a hot path.
        /// </summary>
        private static bool CascadeEnforce()
        {
            int now = Environment.TickCount;
            if (unchecked(now - _casEnforceCachedTick) < 1000 && _casEnforceCachedTick != 0)
            {
                return _casEnforce;
            }
            _casEnforceCachedTick = now;
            _casEnforce = DebugSettings.GetInt("cascadeFirewall", DefaultCascadeEnforce) != 0;
            return _casEnforce;
        }

        /// <summary>Per-type cascade tally (bounded - a full dict just lumps into "other").</summary>
        private static void CountType(Dictionary<string, long> dict, string key)
        {
            long n;
            if (dict.TryGetValue(key, out n))
            {
                dict[key] = n + 1;
                return;
            }
            if (dict.Count < MaxCascadeTypes)
            {
                dict[key] = 1;
            }
            else
            {
                dict.TryGetValue("other", out n);
                dict["other"] = n + 1;
            }
        }

        /// <summary>"A=31,B=7,C=2" - the top CascadeTopN entries of a tally dict, or "-".</summary>
        private static string TopTypes(Dictionary<string, long> dict)
        {
            if (dict.Count == 0) { return "-"; }
            var items = new List<KeyValuePair<string, long>>(dict);
            items.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            int n = Math.Min(CascadeTopN, items.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) { sb.Append(','); }
                string name = items[i].Key;
                int dot = name.LastIndexOf('.');
                sb.Append(dot >= 0 ? name.Substring(dot + 1) : name)
                    .Append('=').Append(items[i].Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Periodic "cascade ..." summary (x2coop.cascadeLogMs, default 5000,
        /// 0 = off) - the T5.4 measurement evidence. Emitted from
        /// <see cref="Tick"/> only when the totals moved since the last line.
        /// </summary>
        private static void PumpCascade()
        {
            int ms = DebugSettings.GetInt("cascadeLogMs", DefaultCascadeLogMs);
            if (ms <= 0)
            {
                if (_cascadeWatch.IsRunning) { _cascadeWatch.Reset(); }
                return;
            }
            if (!_cascadeWatch.IsRunning) { _cascadeWatch.Restart(); return; }
            if (_cascadeWatch.ElapsedMilliseconds < ms) { return; }
            _cascadeWatch.Restart();
            long total = DirectMutations + InternalMutations + CascadeMutations
                + DirectDeletes + CascadeDeletes + InternalCreates + CascadeCreates;
            if (total == _cascadeLastTotal) { return; }
            _cascadeLastTotal = total;
            Log.Info("cascade ops=" + CascadeMutations
                + " direct=" + DirectMutations
                + " internal=" + InternalMutations
                + " wl=" + CascadeWhitelisted
                + " ser=" + CascadeSerializable
                + " exclT=" + CascadeExcludedType
                + " exclE=" + CascadeExcludedEntity
                + " untyped=" + CascadeUntyped
                + " dels=" + CascadeDeletes
                + " creates=" + CascadeCreates
                + " intCreates=" + InternalCreates
                + " blocked=" + (CascadeBlockedMutations + CascadeBlockedDeletes)
                + " enforce=" + (CascadeEnforce() ? 1 : 0)
                + " types=" + TopTypes(_cascadeTypes)
                + " sertypes=" + TopTypes(_cascadeSerTypes));
        }

        // ------------------------------------------------------------------
        // the block decision
        // ------------------------------------------------------------------

        /// <summary>
        /// Record + (rate-limited) log a drop; return false to skip the
        /// original, or true when logOnly degrades enforcement to a leak
        /// counter. StackTrace(2,false) is captured in the PREFIX (spec text)
        /// so frame 0 of the formatted list is the game's caller
        /// (MutateComponentOnEntity / ApplyDelayedMutation / the delete path).
        /// </summary>
        private static bool Block(string kind, string key, string detail, ref long counter)
        {
            bool enforcing = !LogOnly();
            if (enforcing)
            {
                counter++;
            }
            else
            {
                LeakedUnwhitelisted++;
            }
            _lastBlockTick = Environment.TickCount;
            RememberBlockedType(key);
            if (RateLimit(key))
            {
                // new StackTrace(2,false) HERE would add this helper's frame;
                // capture in the prefix instead? - we are inside the prefix's
                // call chain either way; +1 frame for Block. The top frame is
                // still a game frame (the prefix trampoline is skipped by 2
                // only when built inside the prefix) - so build it here with
                // skip=3: Block -> the prefix -> trampoline -> caller.
                Log.Warn("firewall: blocked " + kind + " " + key + " " + detail
                    + (enforcing ? string.Empty : " [LOG-ONLY - applied]")
                    + " (blocks muts=" + BlockedMutations + " dels=" + BlockedDeletes + ")"
                    + "\n" + TopFrames(new System.Diagnostics.StackTrace(3, false)));
            }
            return !enforcing;
        }

        /// <summary>[ExcludeFromSerialization] on the component class - the whole type never serializes (WorldState.cs:77). Cached per Type. Internal: T5.1 ReplicationSource reuses the same exclusion rules host-side (the gated worlds never coexist - host capture world vs guest mirror world).</summary>
        internal static bool IsTypeExcluded(Type type)
        {
            if (ExclAttrByType.TryGetValue(type, out bool excl))
            {
                return excl;
            }
            try
            {
                excl = type.GetCustomAttributes(typeof(ExcludeFromSerializationAttribute), false).Length > 0;
            }
            catch
            {
                excl = false;
            }
            ExclAttrByType[type] = excl;
            return excl;
        }

        /// <summary>
        /// The entity carries ExcludeFromSerializationComponent - WorldState
        /// skips it wholesale (WorldState.cs:131), so every mutation on it is
        /// already guest-local. Component-id resolved via Lookup.TypeToId
        /// (NOT FindComponentId - DynamicComponentLookup REGISTERS missing
        /// types as a side effect, DynamicComponentLookup.cs:9-18); cached
        /// for the one gated world. Internal: T5.1 ReplicationSource calls it
        /// host-side too (the single-slot cache re-resolves per world
        /// switch; the two gated worlds never coexist in one process).
        /// </summary>
        internal static bool EntityExcluded(World world, int entityID)
        {
            if (!ReferenceEquals(world, _exclWorld))
            {
                _exclWorld = world;
                _exclCompId = -1;
                try
                {
                    Dictionary<Type, int> map = world.ComponentManager?.Lookup?.TypeToId;
                    if (map != null
                        && map.TryGetValue(typeof(ExcludeFromSerializationComponent), out int id))
                    {
                        _exclCompId = id;
                    }
                }
                catch
                {
                }
            }
            if (_exclCompId < 0) { return false; }
            try
            {
                Entity e = world.EntityManager?.GetEntity(entityID);
                return e != null && e.Has(_exclCompId);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>1 log line per key per LogRateLimitMs; Environment.TickCount wraparound-safe diff.</summary>
        private static bool RateLimit(string key)
        {
            int now = Environment.TickCount;
            if (LastLogTick.TryGetValue(key, out int last)
                && unchecked(now - last) < LogRateLimitMs)
            {
                return false;
            }
            LastLogTick[key] = now;
            return true;
        }

        /// <summary>Keep the bounded (type,tick) ring for exception correlation.</summary>
        private static void RememberBlockedType(string typeName)
        {
            _recentBlockTypes.Add(typeName);
            _recentBlockTicks.Add(Environment.TickCount);
            if (_recentBlockTypes.Count > MaxRecentBlocks)
            {
                _recentBlockTypes.RemoveAt(0);
                _recentBlockTicks.RemoveAt(0);
            }
        }

        /// <summary>"at A.B() <- C.D() ..." - the top MaxFrames frames of the passed trace.</summary>
        private static string TopFrames(System.Diagnostics.StackTrace st)
        {
            var sb = new StringBuilder("   stack:");
            int n = Math.Min(MaxFrames, st.FrameCount);
            for (int i = 0; i < n; i++)
            {
                MethodBase m = null;
                try { m = st.GetFrame(i)?.GetMethod(); } catch { }
                if (m == null) { continue; }
                sb.Append(i == 0 ? " at " : " <- ")
                    .Append(m.DeclaringType != null ? m.DeclaringType.Name : "?")
                    .Append('.')
                    .Append(m.Name);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // exception wrap layer A: finalizers on the world's event pump
        // ------------------------------------------------------------------

        /// <summary>
        /// Finalizer on both concrete ProcessStrategy.ProcessEvent overrides.
        /// __instance is the strategy; the gate resolves its World. Covers the
        /// queued drain (World.Process) AND the immediate dispatch
        /// (World.HandleEvent&lt;T&gt; - generic, unpatchable; its body calls
        /// straight into ProcessEvent).
        /// </summary>
        private static Exception EventFinalizer(ProcessStrategy __instance,
            Exception __exception)
        {
            return SwallowIfOurs(__instance != null ? __instance.World : null,
                __exception, "ProcessEvent");
        }

        /// <summary>
        /// Finalizer on World.Process() - outer belt for throws that escape
        /// the strategy layer itself (rare; ProcessEvent catches almost
        /// everything). Same-tick cap: WorldManagedScreen.Update loops
        /// `while (!Process())` - a swallowed Process() returns false and is
        /// retried IN THE SAME FRAME, so a persistent Process-level thrower
        /// would hang instead of crash. One swallow per Environment.TickCount
        /// tick keeps the drain bounded (the second throw propagates).
        /// </summary>
        private static Exception ProcessFinalizer(World __instance, Exception __exception)
        {
            try
            {
                if (__exception == null) { return null; }
                int now = Environment.TickCount;
                if (_processSwallowTick == now) { return __exception; }
                Exception result = SwallowIfOurs(__instance, __exception, "Process");
                if (result == null)
                {
                    _processSwallowTick = now;
                }
                return result;
            }
            catch
            {
                return __exception;
            }
        }

        /// <summary>
        /// Finalizer shared by the two delete sinks. Teardown path: the mirror
        /// world's Dispose deletes entities one by one, and each
        /// DeleteEntityImmediately drains the delayed-mutation stack - a stack
        /// orphaned by a swallowed command exception (verified: the blocked
        /// Hire left an AssignedActorsLink add that NRE'd against half-deleted
        /// entities) throws here, NOT inside ProcessEvent. Swallowing lets the
        /// Dispose loop continue past the poisoned entry; the world is dying
        /// anyway.
        /// </summary>
        private static Exception DeleteFaultFinalizer(World __instance, Exception __exception)
        {
            return SwallowIfOurs(__instance, __exception, "delete");
        }

        /// <summary>Finalizer on World.Dispose(...) - backstop for teardown faults outside the delete path.</summary>
        private static Exception DisposeFinalizer(World __instance, Exception __exception)
        {
            return SwallowIfOurs(__instance, __exception, "Dispose");
        }

        /// <summary>
        /// The swallow decision shared by both finalizers: return null to
        /// swallow ONLY when the exception lands on the gated mirror world,
        /// enforcement is on (logOnly must never hide faults), a block landed
        /// within the correlation window, and the trace looks like game code.
        /// Counted through the same hit pipeline the log tap feeds; all
        /// reporting + the resync still happen in <see cref="Tick"/>.
        /// Fails OPEN: any fault here rethrows the original exception.
        /// </summary>
        private static Exception SwallowIfOurs(World world, Exception ex, string where)
        {
            try
            {
                if (ex == null) { return null; }
                if (!Mirrorish(world)) { return ex; }
                if (LogOnly()) { return ex; }
                int last = _lastBlockTick;
                if (last == 0
                    || unchecked(Environment.TickCount - last) > ExceptionCorrelationMs)
                {
                    return ex;
                }
                if (!LooksLikeGameFault(null, ex.ToString())) { return ex; }
                ExceptionHits++;
                SwallowedExceptions++;
                _pendingHits++;
                if (_pendingSample == null)
                {
                    string s = "swallowed@" + where + " " + ex.GetType().Name + ": " + ex.Message;
                    _pendingSample = s.Length > 200 ? s.Substring(0, 200) : s;
                }
                return null;
            }
            catch
            {
                return ex;
            }
        }

        // ------------------------------------------------------------------
        // exception wrap layer B: logMessageReceived -> count -> Tick -> ResyncReq
        // ------------------------------------------------------------------

        /// <summary>
        /// Unity log tap. Runs INSIDE Unity's log dispatch on the logging
        /// thread - must never throw and must never call Debug.Log/Log.*
        /// (a Log call re-enters this handler; _inLogHandler is the belt).
        /// All real reporting + the resync happen in <see cref="Tick"/>.
        ///
        /// A hit requires ALL of:
        ///   - LogType.Exception (log4net Fatal-with-exception arrives via
        ///     UnityLogAppender -> OriginalLogHandler.LogException -> native
        ///     emit -> this tap; handled ExceptionReport errors don't),
        ///   - MirrorMode.Active (guest mirror live; host/local faults are
        ///     not ours),
        ///   - main thread (the mutation pipeline is main-thread only, R10),
        ///   - a firewall block within ExceptionCorrelationMs, AND
        ///   - the text/stack looks like game code (Artitas./Xenonauts.
        ///     frames) OR names a recently-blocked component type.
        /// It's a heuristic - documented; a false positive costs one cheap
        /// (single-flight/debounced) ResyncReq, a false negative just means
        /// the mirror keeps the corruption until the next resync driver.
        /// </summary>
        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (_inLogHandler) { return; }
            try
            {
                if (type != LogType.Exception) { return; }
                if (!MirrorState.Active) { return; }
                if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) { return; }
                int last = _lastBlockTick;
                if (last == 0
                    || unchecked(Environment.TickCount - last) > ExceptionCorrelationMs)
                {
                    return;
                }
                if (!LooksLikeGameFault(condition, stackTrace)) { return; }
                _inLogHandler = true;
                try
                {
                    ExceptionHits++;
                    _pendingHits++;
                    if (_pendingSample == null && !string.IsNullOrEmpty(condition))
                    {
                        int nl = condition.IndexOf('\n');
                        _pendingSample = (nl > 0 ? condition.Substring(0, nl) : condition);
                        if (_pendingSample.Length > 200)
                        {
                            _pendingSample = _pendingSample.Substring(0, 200);
                        }
                    }
                }
                finally
                {
                    _inLogHandler = false;
                }
            }
            catch
            {
                // Inside the engine's log dispatch - swallow absolutely.
            }
        }

        /// <summary>Game-stack / blocked-type-name correlation for the exception heuristic.</summary>
        private static bool LooksLikeGameFault(string condition, string stackTrace)
        {
            string text = (condition ?? string.Empty) + "\n" + (stackTrace ?? string.Empty);
            for (int i = 0; i < _recentBlockTypes.Count; i++)
            {
                if (unchecked(Environment.TickCount - _recentBlockTicks[i]) > ExceptionCorrelationMs)
                {
                    continue;
                }
                string tn = _recentBlockTypes[i];
                int dot = tn.LastIndexOf('.');
                string shortName = dot >= 0 ? tn.Substring(dot + 1) : tn;
                if (shortName.Length >= 6 && text.IndexOf(shortName, StringComparison.Ordinal) >= 0)
                {
                    return true; // the exception literally names a type we just dropped
                }
            }
            return text.IndexOf("Artitas.", StringComparison.Ordinal) >= 0
                || text.IndexOf("Xenonauts.", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Runner.Update pump seam (main thread): flush the correlated-hit
        /// report the log handler only counted, and fire the auto-resync
        /// every ExceptionResyncThreshold cumulative hits. RequestResync's
        /// own guards (in-session, host-busy deferral, stream-in-flight,
        /// single-flight, 1.5 s debounce) keep repeated triggers cheap.
        /// </summary>
        internal static void Tick()
        {
            try
            {
                PumpCascade();
            }
            catch (Exception ex)
            {
                Log.Exception("firewall cascade-tick", ex);
            }
            try
            {
                int pending = _pendingHits;
                if (pending <= 0) { return; }
                _pendingHits = 0;
                Log.Warn("firewall: " + pending + " guest exception(s) correlated with dropped mutations"
                    + " (hits=" + ExceptionHits + ")"
                    + (_pendingSample != null ? " first=\"" + _pendingSample + "\"" : string.Empty));
                _pendingSample = null;
                if (ExceptionHits >= ExceptionResyncThreshold
                    && ExceptionHits - _hitsAtLastResync >= ExceptionResyncThreshold)
                {
                    _hitsAtLastResync = ExceptionHits;
                    if (Session.SessionClient.RequestResync(Net.ResyncReason.ManualRequest,
                        "firewall: " + ExceptionHits + " guest exception(s) after dropped mutations"))
                    {
                        AutoResyncs++;
                        Log.Warn("firewall: auto-resync requested (exception hits=" + ExceptionHits + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("firewall tick", ex);
            }
        }
    }
}
