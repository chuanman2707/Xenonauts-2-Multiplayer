using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Artitas;
using FullSerializer;
using X2Coop.Mirror;
using X2Coop.Net;
using X2Coop.Session;

namespace X2Coop.Replication
{
    /// <summary>
    /// T4.1 scope flag + T5.3 StateApplier (guest side): decodes the host's
    /// StateDelta/PosDelta ops and mutates the mirror world so it tracks the
    /// authoritative host.
    ///
    /// Scope contract (unchangeable without re-reviewing the firewall):
    ///  - <see cref="InScope"/> is true ONLY inside <see cref="Scope"/>'s
    ///    using-block, and only on the thread that opened it - all world
    ///    access is Unity main thread (spec section 4, R10), so a plain
    ///    static bool is enough; no ThreadStatic, no locking.
    ///  - While InScope, MirrorFirewall lets ApplyComponentMutation /
    ///    DeleteEntityImmediately / UnsafeDeleteEntity / CreateEntity
    ///    through (host-authoritative state landing on the guest mirror).
    ///    T5.4 refines the carve-out to PER-OP: <see cref="InFlightOp"/>
    ///    marks which op is mid-apply and arms its terminal mutation key, so
    ///    the firewall passes exactly the op's own write and classifies the
    ///    rest - Direct (the op), Internal (applier bookkeeping phase) or
    ///    Cascade (listener writes). Serializable non-whitelisted cascades
    ///    are dropped when x2coop.cascadeFirewall=1 (the host streams its
    ///    own cascade results as ops anyway); the measurement that decided
    ///    this is in MODLOG T5.4.
    ///  - Scope() nests: each Dispose restores the PREVIOUS flag value, so
    ///    an inner using can't accidentally unlatch an outer scope. If a
    ///    scope ever leaks (no Dispose), the firewall stays open - every
    ///    mutation below runs inside a `using` so a throw still closes it.
    ///
    /// Apply pipeline (T5.3):
    ///  - <see cref="Enqueue"/>/<see cref="EnqueuePos"/>: DeltaSink feeds
    ///    decoded, session-gated, continuity-checked messages here from
    ///    inside Runner.DrainNetEvents (already on the main thread).
    ///  - <see cref="Tick"/>: SessionClient.Tick calls this every frame -
    ///    inside Runner.Update, AFTER Transport.PollEvents + DrainNetEvents
    ///    (the literal task seam). Drains the pending queue into the mirror
    ///    world when and only when MirrorMode == Active, the world is live,
    ///    and !world.IsMutationDelayed() (R10's defensive gate - it never
    ///    fires at this seam, and a wrong-time apply would corrupt anyway).
    ///  - Ops apply in wire order inside ONE Scope per drain call:
    ///    CreateEntity/DeleteEntity resolve through <see cref="EntityIdMap"/>
    ///    (identity bind for baseline ids, placeholder via
    ///    World.CreateEntity() for anything else - never ForceCreateEntity);
    ///    SetComponent resolves the FullName through
    ///    <see cref="ComponentTypeResolver"/> (world registry first, then
    ///    Type.GetType, then an assembly scan with generic-arity parsing),
    ///    deserializes the fsData payload with our own serializer, and calls
    ///    entity.Add(component, componentId) - Add already passes the live
    ///    component as `previous` to MutateComponentOnEntity (Entity.cs:
    ///    33452), so replace-in-place needs NO remove-then-add;
    ///    RemoveComponent resolves the type in the world's registry (no
    ///    registration side effects - an unregistered-on-guest type cannot
    ///    exist on the entity) and calls entity.Remove(componentId).
    ///  - The deserializer is the guest's own fsSerializer:
    ///    CommonSerializationSystem.SetupSerializer(new fsSerializer(),
    ///    world: null) skips the game's EntityConverter (whose CreateInstance
    ///    ForceCreateEntity(hostId)s), then <see cref=
    ///    "MappingEntityConverter"/> maps host entity ids through the map.
    ///    The host serialized under AssetDescriptor.EnterSaveSerialization -
    ///    that flag only rewrites emitted $next tokens, so the guest needs
    ///    no matching scope to read them.
    ///  - Sequence integrity is DeltaSink's job (it owns the watermark and
    ///    the gap counter); a forward jump calls <see cref="NoteStreamBroken"/>
    ///    which poisons the stream: pending + incoming deltas drop until a
    ///    fresh baseline lands (world rebind clears the latch), because
    ///    applying post-gap ops would spread the missing delta's divergence.
    ///    DeltaSink escalates the gap to SessionClient.RequestResync;
    ///    while poisoned, Tick keeps nudging the guarded resync path on a
    ///    ~2.5 s cadence (single-flight/debounce absorb repeats) so a
    ///    silently-dropped request cannot wedge the mirror forever.
    ///  - PosDelta ops ride through <see cref="PosCoalescer"/>: latest
    ///    seq-per-key wins, stale packets die unapplied, and pos ops never
    ///    spawn placeholders (a position write for an entity the guest
    ///    lacks is a no-op - resurrecting it would leak a ghost).
    ///
    /// Fail-open everywhere: an op-level exception is counted + logged
    /// (first few per tag), the op drops, the delta continues - UNLESS the
    /// throw leaked the world's mutation-delay flag (a crash inside
    /// World.MutateComponentOnEntity skips the Pop that clears it; seen
    /// live: one EntityMutationException left the mirror delayed for good).
    /// A leaked flag poisons the stream into resync immediately; a stuck
    /// flag from any other cause trips the applyDelayPoisonMs watchdog
    /// (default 30 s) for the same recovery. A Tick-level fault logs once
    /// per tag per session. Counters feed the "apply-stats"
    /// line (x2coop.applyStatsMs) and the per-message "apply seq=...
    /// latencyMs=..." line (x2coop.applyLogEach, default on).
    /// </summary>
    internal static class StateApplier
    {
        /// <summary>True while a StateApplier apply is running on this thread's call stack.</summary>
        public static bool InScope { get; private set; }

        /// <summary>Open an apply scope. Always pair with `using`.</summary>
        public static IDisposable Scope()
        {
            return new ScopeToken();
        }

        private sealed class ScopeToken : IDisposable
        {
            private readonly bool _previous;
            private bool _disposed;

            public ScopeToken()
            {
                _previous = InScope;
                InScope = true;
            }

            public void Dispose()
            {
                if (_disposed) { return; }
                _disposed = true;
                InScope = _previous;
            }
        }

        // ---- T5.3 knobs ----------------------------------------------------
        private const int DefaultOpsPerFrame = 4096;   // x2coop.applyOpsPerFrame
        private const int DefaultPendingCap = 4096;    // x2coop.applyPendingCap (queued msgs)
        private const int DefaultStatsMs = 1000;       // x2coop.applyStatsMs (0 = off)
        private const int DefaultDelayPoisonMs = 30000;// x2coop.applyDelayPoisonMs (stuck flag watchdog)
        private const long ResyncNudgeMs = 2500;       // poisoned-stream resync re-request cadence
        private const int MaxFailLogs = 8;             // per-tag WARN lines before silence

        // ---- pending state (main thread only) ------------------------------
        private static readonly Queue<StateDeltaMsg> _pending = new Queue<StateDeltaMsg>();
        private static readonly PosCoalescer _pos = new PosCoalescer();

        private static WeakReference<World> _world;
        private static EntityIdMap _map;
        private static fsSerializer _serializer;
        private static MappingEntityConverter _converter;
        private static ComponentTypeResolver _types;
        private static bool _baselineCaptured;

        private static bool _poisoned;
        private static string _poisonDetail;
        private static readonly Stopwatch _poisonNudge = new Stopwatch();
        private static readonly Stopwatch _statsWatch = new Stopwatch();
        private static readonly HashSet<string> _faultLogged = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _failLogged = new Dictionary<string, int>(StringComparer.Ordinal);
        private static long _lastAppliedSeq;
        private static long _baselineSeq;              // accepted baseline's seq watermark (post-snapshot ops are > this)
        private static long _delayedSinceMs;           // wall ms the delayed gate first held (0 = clear)

        // ---- session counters (reset on session end) ------------------------
        internal static long AppliedDeltas;      // reliable StateDelta messages drained
        internal static long AppliedOps;         // component/entity ops that mutated the world
        internal static long AppliedCreates;
        internal static long AppliedDeletes;
        internal static long AppliedRemoves;
        internal static long AppliedSets;
        internal static long PosMsgs;            // PosDelta messages drained
        internal static long PosOpsApplied;
        internal static long NoopOps;            // idempotent no-ops (dead entity, absent component)
        internal static long FailedOps;          // deserialize/type/apply failures (dropped)
        internal static long DroppedPoisoned;    // ops dropped while the stream is broken
        internal static long DeferredFrames;     // ticks skipped on IsMutationDelayed
        internal static long PendingOverflows;   // times the pending cap tripped
        internal static long Faults;             // caught exceptions anywhere in this class

        // stats-window accumulators
        private static long _winOps, _winDeltas, _winPosOps, _winPosMsgs, _winFail, _winNoop;

        /// <summary>Queued reliable messages waiting for a live mirror.</summary>
        internal static int PendingMsgs { get { return _pending.Count; } }

        /// <summary>
        /// T5.5 seams for <see cref="WorldHash"/>: the entity-id map (host-id
        /// write-back + guest-local detection) and the reliable applied-seq
        /// watermark the guest hashes at.
        /// </summary>
        internal static EntityIdMap Map { get { return _map; } }
        internal static long LastAppliedSeq { get { return _lastAppliedSeq; } }

        // ------------------------------------------------------------------
        // feed (DeltaSink, inside DrainNetEvents - main thread)
        // ------------------------------------------------------------------

        /// <summary>
        /// Queue a decoded, continuity-checked StateDelta for application.
        /// DeltaSink calls this only for strictly-new seqs; a poisoned stream
        /// drops at the door (applying post-gap ops spreads the divergence -
        /// the gap-triggered resync re-baselines instead).
        /// </summary>
        internal static void Enqueue(StateDeltaMsg msg)
        {
            try
            {
                if (msg == null) { return; }
                if (_poisoned)
                {
                    DroppedPoisoned += msg.Ops != null ? msg.Ops.Length : 1;
                    return;
                }
                _pending.Enqueue(msg);
            }
            catch (Exception ex) { Fault("enqueue", ex); }
        }

        /// <summary>Queue a PosDelta's ops into the latest-state coalescer.</summary>
        internal static void EnqueuePos(PosDeltaMsg msg)
        {
            try
            {
                if (msg == null) { return; }
                if (_poisoned)
                {
                    DroppedPoisoned += msg.Ops != null ? msg.Ops.Length : 1;
                    return;
                }
                _pos.Offer(msg);
            }
            catch (Exception ex) { Fault("enqueue-pos", ex); }
        }

        /// <summary>
        /// Reliable-stream continuity broke (DeltaSink saw seq jump forward).
        /// Latch the poisoned state: everything still arriving is post-gap
        /// state computed against a world we diverged from - drop it until a
        /// fresh baseline rebinds.
        /// </summary>
        internal static void NoteStreamBroken(string detail)
        {
            if (_poisoned) { return; }
            _poisoned = true;
            _poisonDetail = detail;
            _poisonNudge.Restart();
            int d = _pending.Count;
            _pending.Clear();
            _pos.Clear();
            Log.Warn("apply: delta stream broken (" + (detail ?? "?") + ")"
                + (d > 0 ? " dropped pending=" + d : string.Empty)
                + " - mirror frozen until resync");
        }

        /// <summary>
        /// A SaveOffer was accepted (SaveReceiver armed the stream): record
        /// its seq watermark so rebind keeps post-snapshot deltas
        /// (seq &gt; baselineSeq - the snapshot does NOT contain them)
        /// instead of dropping them with the stale tail, and reopen a
        /// poisoned queue - the baseline is the recovery the poison was
        /// waiting for. The sink's watermark moves at the same time.
        /// </summary>
        internal static void NoteBaselineSeq(long baselineSeq)
        {
            try
            {
                _baselineSeq = baselineSeq;
                int trimmed = TrimPendingToBaseline();
                if (_poisoned)
                {
                    _poisoned = false;
                    _poisonDetail = null;
                    _poisonNudge.Reset();
                    Log.Info("apply: baseline seq=" + baselineSeq
                        + " adopted - poison cleared, stream reopened"
                        + (trimmed > 0 ? " (trimmed stale=" + trimmed + ")" : string.Empty));
                }
                else if (trimmed > 0)
                {
                    Log.Info("apply: baseline seq=" + baselineSeq
                        + " adopted - trimmed stale pending=" + trimmed);
                }
            }
            catch (Exception ex) { Fault("baseline-seq", ex); }
        }

        // ------------------------------------------------------------------
        // pump (SessionClient.Tick, inside Runner.Update after PollEvents)
        // ------------------------------------------------------------------

        /// <summary>
        /// Per-frame guest-side apply pump. Resolves the live mirror world
        /// (rebinding the map/serializer whenever the world instance swaps -
        /// a resync baseline IS a new world), gates on MirrorMode.Active +
        /// !IsMutationDelayed (R10), and drains inside one Scope.
        /// </summary>
        internal static void Tick()
        {
            try
            {
                if (MirrorState.Mode == MirrorMode.Off)
                {
                    LazyReset("mode off");
                    return;
                }
                World w = SaveReceiver.MirrorWorld;
                World cur = ResolveWorld();
                if (!ReferenceEquals(cur, w))
                {
                    Rebind(w);
                    cur = w;
                }
                if (_poisoned)
                {
                    // Frozen until the new baseline lands. Re-nudge the
                    // guarded resync path on a slow cadence - its
                    // single-flight/debounce/stream-in-flight checks absorb
                    // repeats, so this can never stack requests.
                    DropPending();
                    if (!_poisonNudge.IsRunning || _poisonNudge.ElapsedMilliseconds > ResyncNudgeMs)
                    {
                        _poisonNudge.Restart();
                        SessionClient.RequestResync(ResyncReason.MissedDelta,
                            _poisonDetail ?? "delta stream broken");
                    }
                    return;
                }
                // The pending cap applies in every mode - an apply backlog
                // the drain can never catch up on is as lost as a wire gap.
                HoldCheck();
                if (_poisoned) { return; } // the cap just poisoned us
                if (w == null || w.Disposed
                    || MirrorState.Mode != MirrorMode.Active)
                {
                    // Mid-stream / pending-activate / detached: hold ops for
                    // the upcoming (or just-binding) world, bounded by the cap.
                    PumpStats();
                    return;
                }
                try
                {
                    if (w.IsMutationDelayed())
                    {
                        DeferredFrames++;
                        // The flag is a latch, not a window marker: a crash
                        // inside MutateComponentOnEntity skips the Pop that
                        // clears it, leaving the world mutation-delayed
                        // forever (seen live, r3 verify). Apply must not
                        // starve silently - bound the stall and resync.
                        if (_delayedSinceMs == 0)
                        {
                            _delayedSinceMs = Environment.TickCount;
                        }
                        else if (Environment.TickCount - _delayedSinceMs
                            > DebugSettings.GetInt("applyDelayPoisonMs", DefaultDelayPoisonMs))
                        {
                            NoteStreamBroken("world stuck mutation-delayed >"
                                + DebugSettings.GetInt("applyDelayPoisonMs", DefaultDelayPoisonMs)
                                + "ms (leaked flag)");
                        }
                        return;
                    }
                }
                catch (Exception ex) { Fault("delayed-check", ex); return; }
                _delayedSinceMs = 0;
                EnsureBound(w);
                if (_map == null || _serializer == null) { return; }
                if (!_baselineCaptured)
                {
                    _baselineCaptured = true;
                    try
                    {
                        _map.BaselineAllocated = w.EntityManager.AllocatedEntitiesCount();
                    }
                    catch (Exception ex) { Fault("baseline", ex); }
                    Log.Info("apply: baseline captured allocated="
                        + (_map.BaselineAllocated == int.MaxValue ? -1 : _map.BaselineAllocated)
                        + " world=" + WorldTag(w));
                }
                Drain(w);
                PumpStats();
            }
            catch (Exception ex) { Fault("tick", ex); }
        }

        // ------------------------------------------------------------------
        // drain + per-op apply (inside Scope - firewall passes these)
        // ------------------------------------------------------------------

        private static void Drain(World w)
        {
            int budget = DebugSettings.GetInt("applyOpsPerFrame", DefaultOpsPerFrame);
            if (budget < 1) { budget = 1; }
            int used = 0;
            using (Scope())
            {
                while (_pending.Count > 0 && used < budget)
                {
                    StateDeltaMsg msg = _pending.Dequeue();
                    if (msg.Seq <= _lastAppliedSeq)
                    {
                        NoopOps++; // strictly-new is the sink's contract; this is belt
                        continue;
                    }
                    _lastAppliedSeq = msg.Seq;
                    int ok = 0, noop = 0, fail = 0;
                    int n = msg.Ops != null ? msg.Ops.Length : 0;
                    for (int i = 0; i < n; i++)
                    {
                        OpResult r = ApplyOp(w, msg.Ops[i], posLane: false);
                        if (r == OpResult.Applied) { ok++; }
                        else if (r == OpResult.Noop) { noop++; }
                        else { fail++; }
                        if (_poisoned) { return; } // op-level corruption: stop mutating
                    }
                    used += n;
                    AppliedDeltas++;
                    AppliedOps += ok;
                    _winDeltas++;
                    _winOps += ok;
                    _winNoop += noop;
                    _winFail += fail;
                    if (LogEach())
                    {
                        Log.Info("apply seq=" + msg.Seq
                            + " ops=" + ok + "/" + n
                            + (noop > 0 ? " noop=" + noop : string.Empty)
                            + (fail > 0 ? " FAIL=" + fail : string.Empty)
                            + " latencyMs=" + LatencyMs(msg.HostSendTicks));
                    }
                    // T5.5: a pending HashReq for exactly this seq fires its
                    // guest hash NOW - inside the drain, between messages -
                    // so "state at seq N" is captured before msg N+1 applies
                    // even when the wire already queued it. The call is
                    // self-guarded (no pending req = one empty check).
                    WorldHash.GuestWatermark(msg.Seq);
                }
                // latest-state lane: reliable ops first (a pos op may target
                // an entity this drain just created).
                List<DeltaOp> posOps = _pos.Drain();
                if (posOps.Count > 0)
                {
                    int ok = 0, noop = 0, fail = 0;
                    for (int i = 0; i < posOps.Count; i++)
                    {
                        OpResult r = ApplyOp(w, posOps[i], posLane: true);
                        if (r == OpResult.Applied) { ok++; }
                        else if (r == OpResult.Noop) { noop++; }
                        else { fail++; }
                        if (_poisoned) { return; } // op-level corruption: stop mutating
                    }
                    PosMsgs++;
                    PosOpsApplied += ok;
                    _winPosMsgs++;
                    _winPosOps += ok;
                    _winNoop += noop;
                    _winFail += fail;
                    if (LogEach())
                    {
                        Log.Info("apply-pos seq=" + _pos.LastMsgSeq
                            + " ops=" + ok + "/" + posOps.Count
                            + (noop > 0 ? " noop=" + noop : string.Empty)
                            + (fail > 0 ? " FAIL=" + fail : string.Empty)
                            + " latencyMs=" + LatencyMs(_pos.LastMsgTicks));
                    }
                }
            }
        }

        private enum OpResult : byte { Applied, Noop, Failed }

        private static OpResult ApplyOp(World w, DeltaOp op, bool posLane)
        {
            try
            {
                // T5.4: mark which op is in flight so the firewall can tell
                // the op's OWN terminal mutation (armed below) apart from
                // listener cascades. Kind covers the whole apply body; the
                // (entity,component) key arms only around the terminal call.
                InFlightOp.Kind kind;
                switch (op.Kind)
                {
                    case DeltaOpKind.CreateEntity: kind = InFlightOp.Kind.Create; break;
                    case DeltaOpKind.DeleteEntity: kind = InFlightOp.Kind.Delete; break;
                    case DeltaOpKind.SetComponent: kind = InFlightOp.Kind.Set; break;
                    case DeltaOpKind.RemoveComponent: kind = InFlightOp.Kind.Remove; break;
                    default: kind = InFlightOp.Kind.None; break;
                }
                using (InFlightOp.Begin(kind))
                {
                    switch (op.Kind)
                    {
                        case DeltaOpKind.CreateEntity:
                            return ApplyCreate(w, op);
                        case DeltaOpKind.DeleteEntity:
                            return ApplyDelete(w, op);
                        case DeltaOpKind.SetComponent:
                            return ApplySet(w, op, posLane);
                        case DeltaOpKind.RemoveComponent:
                            return ApplyRemove(w, op);
                        default:
                            FailedOps++;
                            return OpResult.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                FailedOps++;
                // The wrapper (EntityMutationException) only names the
                // mutation; the crashing listener lives in InnerException -
                // log its type + top frame so the next failure identifies
                // the system instead of the plumbing.
                Exception ie = ex.InnerException != null ? ex.InnerException : ex;
                string innerTop = null;
                try
                {
                    string st = ie.StackTrace;
                    if (st != null)
                    {
                        int nl = st.IndexOf('\n');
                        innerTop = (nl > 0 ? st.Substring(0, nl) : st).Trim();
                    }
                }
                catch { }
                FailLog("op-" + op.Kind, null,
                    "apply op " + op.Kind + " e=" + op.EntityId
                    + " t=" + (op.ComponentType ?? "-") + " threw "
                    + ex.GetType().Name + ": " + ex.Message
                    + (ie != ex ? " | inner " + ie.GetType().Name + ": " + ie.Message : string.Empty)
                    + (innerTop != null ? " | at " + innerTop : string.Empty));
                try
                {
                    // We only run ops while !IsMutationDelayed, so a true
                    // flag here means THIS op's mutation crash leaked it
                    // (World.MutateComponentOnEntity skips its Pop on a
                    // throw). An EntityMutationException can also escape
                    // with the flag intact when PopAndHandleMutationDelayStack
                    // itself threw after popping - the world still lands in
                    // _illegalState with the mutation half-applied. Both
                    // cases are corrupt - resync is the only honest
                    // recovery; a stuck mirror is worse.
                    if (w != null
                        && (w.IsMutationDelayed() || ex is EntityMutationException))
                    {
                        NoteStreamBroken("mutation crash (flagLeaked="
                            + (w.IsMutationDelayed() ? "1" : "0")
                            + " op=" + op.Kind + " e=" + op.EntityId + ")");
                    }
                }
                catch { }
                return OpResult.Failed;
            }
        }

        /// <summary>
        /// CreateEntity: ensure a live guest entity is bound to the host id.
        /// An already-live binding means an earlier op (or the baseline)
        /// produced the twin - keep it; the entity's component ops follow on
        /// the wire and write the real state.
        /// </summary>
        private static OpResult ApplyCreate(World w, DeltaOp op)
        {
            int gid;
            EntityIdMap.Result r = _map.Resolve(op.EntityId, forWrite: true, out gid);
            if (r == EntityIdMap.Result.Missing || gid < 0)
            {
                FailedOps++;
                FailLog("create-null", null, "apply create e=" + op.EntityId + " - placeholder factory failed");
                return OpResult.Failed;
            }
            AppliedCreates++;
            if (r == EntityIdMap.Result.Placeholder)
            {
                FailLog("ph", null, "apply: placeholder guest=" + gid
                    + " for host e=" + op.EntityId + " (no baseline twin)");
            }
            return OpResult.Applied;
        }

        /// <summary>
        /// DeleteEntity: resolve-only (a delete for a twin we never had is an
        /// idempotent no-op - T5.1 emits deletes it cannot classify). Tombstone
        /// the guest slot so a recycled host id can never re-claim it.
        /// </summary>
        private static OpResult ApplyDelete(World w, DeltaOp op)
        {
            int gid;
            EntityIdMap.Result r = _map.Resolve(op.EntityId, forWrite: false, out gid);
            if (r == EntityIdMap.Result.Missing || gid < 0)
            {
                NoopOps++;
                return OpResult.Noop; // host deleted something the mirror never had
            }
            // Arm the entity-wildcard key (componentId=-1): the delete plus
            // any per-component removals it fans out into are the op's
            // direct effect; a listener mutating ANOTHER entity is a cascade.
            using (InFlightOp.Arm(gid, -1))
            {
                w.DeleteEntityImmediately(gid);
            }
            _map.Tombstone(op.EntityId, gid);
            AppliedDeletes++;
            return OpResult.Applied;
        }

        /// <summary>
        /// SetComponent: resolve type (world registry -> Type.GetType ->
        /// assembly scan), deserialize the payload with the session
        /// serializer (entity refs map through EntityIdMap), then
        /// entity.Add(component, componentId). Add passes the live component
        /// as `previous` to MutateComponentOnEntity, so an existing slot is
        /// replaced in place - no remove-then-add.
        /// </summary>
        private static OpResult ApplySet(World w, DeltaOp op, bool posLane)
        {
            Type t = _types.Resolve(op.ComponentType, WorldTypes(w));
            if (t == null || !typeof(IComponent).IsAssignableFrom(t))
            {
                FailedOps++;
                FailLog("type", null, "apply set e=" + op.EntityId
                    + " - unresolvable/non-component type " + (op.ComponentType ?? "<null>"));
                return OpResult.Failed;
            }
            int gid;
            EntityIdMap.Result r = _map.Resolve(op.EntityId, forWrite: !posLane, out gid);
            if (r == EntityIdMap.Result.Missing || gid < 0)
            {
                // posLane: never resurrect (a position for a dead twin is a
                // stale write, not a create). reliable lane: forWrite=true
                // already spawned a placeholder unless the factory itself
                // failed (then Missing).
                if (posLane) { NoopOps++; return OpResult.Noop; }
                FailedOps++;
                FailLog("set-noent", null, "apply set e=" + op.EntityId
                    + " t=" + op.ComponentType + " - no guest entity");
                return OpResult.Failed;
            }
            IComponent c = Deserialize(t, op.Payload);
            if (c == null)
            {
                FailedOps++;
                FailLog("deser", null, "apply set e=" + op.EntityId
                    + " t=" + op.ComponentType + " - payload failed deserialize");
                return OpResult.Failed;
            }
            int cid;
            try { cid = w.ComponentManager.FindComponentId(t); }
            catch (Exception ex)
            {
                FailedOps++;
                FailLog("cid", ex, "apply set e=" + op.EntityId
                    + " t=" + op.ComponentType + " - FindComponentId threw");
                return OpResult.Failed;
            }
            Entity e = w.EntityManager.GetEntity(gid);
            // The op's ONE direct ApplyComponentMutation is (gid, cid) -
            // consume-once, so even a listener re-mutating the same key
            // counts as a cascade. Everything else that fires inside Add
            // (listener subs, the delayed-mutation-stack drain) is a cascade.
            using (InFlightOp.Arm(gid, cid))
            {
                e.Add(c, cid);
            }
            AppliedSets++;
            return OpResult.Applied;
        }

        /// <summary>
        /// RemoveComponent: the type must already be registered on the guest
        /// (an unregistered type cannot sit on any entity - resolve is kept
        /// side-effect-free so a remove never mints a component id). Missing
        /// entity/component = idempotent no-op.
        /// </summary>
        private static OpResult ApplyRemove(World w, DeltaOp op)
        {
            Type t = _types.Resolve(op.ComponentType, WorldTypes(w));
            if (t == null)
            {
                NoopOps++;
                FailLog("rm-type", null, "apply remove e=" + op.EntityId
                    + " - unresolvable type " + (op.ComponentType ?? "<null>"));
                return OpResult.Noop;
            }
            int cid;
            try
            {
                Dictionary<Type, int> lut = w.ComponentManager.Lookup.TypeToId;
                if (!lut.TryGetValue(t, out cid)) { NoopOps++; return OpResult.Noop; }
            }
            catch { NoopOps++; return OpResult.Noop; }
            int gid;
            EntityIdMap.Result r = _map.Resolve(op.EntityId, forWrite: false, out gid);
            if (r == EntityIdMap.Result.Missing || gid < 0)
            {
                NoopOps++;
                return OpResult.Noop;
            }
            Entity e = w.EntityManager.GetEntity(gid);
            if (!e.Has(cid)) { NoopOps++; return OpResult.Noop; }
            using (InFlightOp.Arm(gid, cid))
            {
                e.Remove(cid);
            }
            AppliedRemoves++;
            return OpResult.Applied;
        }

        // ------------------------------------------------------------------
        // serialization / world plumbing
        // ------------------------------------------------------------------

        /// <summary>
        /// Payload -> component instance with the session serializer. Empty
        /// payloads and any failure return null (the op drops + counts - the
        /// stream itself is not trustworthy enough to keep on a bad decode).
        /// </summary>
        private static IComponent Deserialize(Type t, byte[] payload)
        {
            if (_serializer == null || payload == null || payload.Length == 0)
            {
                return null;
            }
            try
            {
                string json = Encoding.UTF8.GetString(payload);
                fsData data;
                fsResult pr = fsJsonParser.Parse(json, out data);
                if (pr.Failed || data == null) { return null; }
                object inst = null;
                fsResult r = _serializer.TryDeserialize(data, t, ref inst);
                if (r.Failed || inst == null) { return null; }
                return inst as IComponent;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>World's registered component types (resolver merge source).</summary>
        private static IEnumerable<Type> WorldTypes(World w)
        {
            try
            {
                Artitas.ComponentLookup lut = w != null ? w.ComponentManager.Lookup : null;
                return lut != null ? lut.IdToType : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Bind the map + serializer + converter + resolver to a fresh mirror
        /// world. Called lazily on the first Active drain and on every rebind.
        /// </summary>
        private static void EnsureBound(World w)
        {
            if (_map != null && _serializer != null) { return; }
            try
            {
                var map = new EntityIdMap(
                    id => SafeIsAlive(w, id),
                    () => w.CreateEntity().ID);
                fsSerializer ser = Common.Serialization.CommonSerializationSystem
                    .SetupSerializer(new fsSerializer(), world: null);
                var conv = new MappingEntityConverter(map, w);
                ser.AddConverter(conv);
                var resolver = new ComponentTypeResolver();
                resolver.MergeWorldTypes(WorldTypes(w));
                _map = map;
                _converter = conv;
                _serializer = ser;
                _types = resolver;
                Log.Info("apply: bound world=" + WorldTag(w)
                    + " types=" + resolver.CachedCount);
            }
            catch (Exception ex)
            {
                _map = null;
                _converter = null;
                _serializer = null;
                Fault("bind", ex);
            }
        }

        private static bool SafeIsAlive(World w, int id)
        {
            try { return w != null && !w.Disposed && w.EntityManager.IsAlive(id); }
            catch { return false; }
        }

        /// <summary>
        /// Rebind bookkeeping: swap world ref, drop stale bindings + the pos
        /// coalescer, and trim the pending queue to post-snapshot seqs.
        /// Deltas buffered mid-load with seq &gt; <see cref="_baselineSeq"/>
        /// carry state the fresh world lacks - they must survive the rebind
        /// and drain after activation (previously cleared wholesale, a
        /// silent ops loss on every resync).
        /// </summary>
        private static void Rebind(World w)
        {
            int dropped = TrimPendingToBaseline();
            int stalePos = _pos.PendingCount;
            bool hadBinding = _map != null || _poisoned;
            _pos.Clear();
            _map = null;
            _converter = null;
            try { _serializer?.Dispose(); }   // disposes our MappingEntityConverter too
            catch { }
            _serializer = null;
            _types = null;
            _baselineCaptured = false;
            _lastAppliedSeq = _baselineSeq; // the snapshot already covers <= baselineSeq
            _poisoned = false;
            _poisonDetail = null;
            _poisonNudge.Reset();
            _delayedSinceMs = 0;
            _world = w != null ? new WeakReference<World>(w) : null;
            if (hadBinding || dropped > 0 || stalePos > 0)
            {
                Log.Info("apply: rebind world=" + WorldTag(w)
                    + (dropped > 0 || stalePos > 0
                        ? " dropped pend=" + dropped + " pos=" + stalePos
                        : string.Empty)
                    + " (new baseline supersedes the stream)");
            }
        }

        /// <summary>
        /// Drop pending msgs already inside the accepted baseline
        /// (seq &lt;= <see cref="_baselineSeq"/>). Post-snapshot deltas
        /// (seq &gt;) still carry state the new world lacks. Returns the
        /// dropped count.
        /// </summary>
        private static int TrimPendingToBaseline()
        {
            int dropped = 0;
            int n = _pending.Count;
            for (int i = 0; i < n; i++)
            {
                StateDeltaMsg m = _pending.Dequeue();
                if (m != null && m.Seq <= _baselineSeq) { dropped++; }
                else if (m != null) { _pending.Enqueue(m); }
            }
            return dropped;
        }

        private static World ResolveWorld()
        {
            try
            {
                WeakReference<World> wr = _world;
                World w;
                if (wr != null && wr.TryGetTarget(out w) && w != null && !w.Disposed)
                {
                    return w;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Drop the queues without unbinding (poisoned stream).</summary>
        private static void DropPending()
        {
            if (_pending.Count > 0)
            {
                DroppedPoisoned += _pending.Count;
                _pending.Clear();
            }
            if (_pos.PendingCount > 0)
            {
                DroppedPoisoned += _pos.PendingCount;
                _pos.Clear();
            }
        }

        /// <summary>
        /// Holding pattern while no Active mirror exists (stream in flight /
        /// detached): deltas keep queueing until the cap trips - then the
        /// backlog is declared lost and the stream poisons into a resync.
        /// </summary>
        private static void HoldCheck()
        {
            int cap = DebugSettings.GetInt("applyPendingCap", DefaultPendingCap);
            if (cap < 64) { cap = 64; }
            if (_pending.Count <= cap) { return; }
            PendingOverflows++;
            NoteStreamBroken("pending overflow msgs=" + _pending.Count
                + " cap=" + cap + " (stream stuck without an active mirror)");
        }

        // ------------------------------------------------------------------
        // stats + resets + helpers
        // ------------------------------------------------------------------

        private static void PumpStats()
        {
            int ms = DebugSettings.GetInt("applyStatsMs", DefaultStatsMs);
            if (ms <= 0)
            {
                if (_statsWatch.IsRunning) { _statsWatch.Reset(); }
                return;
            }
            if (!_statsWatch.IsRunning) { _statsWatch.Restart(); return; }
            if (_statsWatch.ElapsedMilliseconds < ms) { return; }
            _statsWatch.Restart();
            if (_winDeltas == 0 && _winOps == 0 && _winPosMsgs == 0
                && _winFail == 0 && _winNoop == 0)
            {
                return;
            }
            Log.Info("apply-stats ops=" + _winOps
                + " deltas=" + _winDeltas
                + " pos=" + _winPosOps
                + " noop=" + _winNoop
                + " fail=" + _winFail
                + " ph=" + (_map != null ? _map.PlaceholderCreates : 0)
                + " map=" + (_map != null ? _map.Count : 0)
                + " pend=" + _pending.Count
                + (_poisoned ? " POISONED" : string.Empty));
            _winOps = _winDeltas = _winPosOps = _winPosMsgs = _winFail = _winNoop = 0;
        }

        private static bool LogEach()
        {
            return DebugSettings.GetInt("applyLogEach", 1) != 0;
        }

        private static long LatencyMs(long hostSendTicks)
        {
            if (hostSendTicks <= 0) { return -1; }
            return (DateTime.UtcNow.Ticks - hostSendTicks) / TimeSpan.TicksPerMillisecond;
        }

        /// <summary>Once-per-tag exception logger (fail-open pattern, no spam).</summary>
        private static void Fault(string tag, Exception ex)
        {
            Faults++;
            if (_faultLogged.Add(tag))
            {
                Log.Exception("apply " + tag, ex);
            }
        }

        /// <summary>Limited WARN lines per tag (first MaxFailLogs), counted always.</summary>
        private static void FailLog(string tag, Exception ex, string message)
        {
            int n;
            _failLogged.TryGetValue(tag, out n);
            if (n < MaxFailLogs)
            {
                _failLogged[tag] = n + 1;
                if (ex != null) { Log.Warn(message + " (" + ex.GetType().Name + ": " + ex.Message + ")"); }
                else { Log.Warn(message); }
            }
        }

        private static string WorldTag(World w)
        {
            try { return w != null ? w.GetHashCode().ToString() : "null"; }
            catch { return "?"; }
        }

        /// <summary>
        /// Session end / teardown / off-mode lazy reset: drop the queues,
        /// world binding (map, serializer, converter, resolver), poison
        /// latch, counters, and the T4.1 scope flag. Called from
        /// SaveReceiver.Reset, Teardown, and the Off-mode guard in Tick.
        /// </summary>
        public static void Reset()
        {
            bool had = _map != null || _pending.Count > 0 || _poisoned
                || AppliedDeltas != 0 || InScope;
            InScope = false;
            InFlightOp.Reset();
            _pending.Clear();
            _pos.Clear();
            _world = null;
            _map = null;
            _converter = null;
            try { _serializer?.Dispose(); }
            catch { }
            _serializer = null;
            _types = null;
            _baselineCaptured = false;
            _poisoned = false;
            _poisonDetail = null;
            _poisonNudge.Reset();
            _delayedSinceMs = 0;
            _statsWatch.Reset();
            _faultLogged.Clear();
            _failLogged.Clear();
            _lastAppliedSeq = 0;
            _baselineSeq = 0;
            AppliedDeltas = AppliedOps = AppliedCreates = AppliedDeletes = 0;
            AppliedRemoves = AppliedSets = PosMsgs = PosOpsApplied = 0;
            NoopOps = FailedOps = DroppedPoisoned = DeferredFrames = 0;
            PendingOverflows = Faults = 0;
            _winOps = _winDeltas = _winPosOps = _winPosMsgs = _winFail = _winNoop = 0;
            if (had)
            {
                Log.Info("apply: reset");
            }
        }

        private static void LazyReset(string why)
        {
            if (_map == null && _pending.Count == 0 && !_poisoned
                && AppliedDeltas == 0 && !InScope)
            {
                return; // nothing held - stay silent
            }
            Log.Info("apply: lazy reset (" + why + ")");
            Reset();
        }
    }
}
