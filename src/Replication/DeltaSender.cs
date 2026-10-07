using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Artitas;
using Artitas.Components;
using Artitas.Systems;
using Common.Content.Descriptors;
using FlyingWormConsole3.LiteNetLib;
using FullSerializer;
using X2Coop.Mirror;
using X2Coop.Net;
using X2Coop.Runtime;
using X2Coop.Session;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.2 DeltaSender (host side): consumes <see cref="ReplicationSource.FrameOps"/>
    /// synchronously inside the Runner.LateUpdate -&gt; FlushReplication seam
    /// (the ONLY place live component references are still valid), snapshots /
    /// serializes them with the world's own ArtitasSerializationSystem
    /// .Serializer, coalesces across the send window in <see cref="DeltaQueue"/>,
    /// packs the result into StateDelta messages at a fixed emit tick, and
    /// streams positional/client-derivable state on the unreliable PosDelta
    /// lane. Nothing it holds between frames is a live game reference - only
    /// owned component copies (IComponent.Clone) and serialized byte[].
    ///
    /// R9 addendum (bandwidth, task-R9-report.md - target &lt;64 KiB/s):
    ///   - cross-frame coalescing: DeltaQueue's last-write-wins map already
    ///     collapses the per-second ~1k mutations into ~one op per
    ///     (entity, component) per send tick;
    ///   - heavy-type cap: ops whose serialized payload exceeds
    ///     x2coop.deltaHeavyBytes (default 1024), plus the four measured
    ///     state-machine types (ActivityStateMachine ~6.7 KB, UIControllers
    ///     ~3.3 KB, TemplateRelationships ~1.9 KB, NotificationStateMachine
    ///     ~1.5 KB), emit at most once per entity per x2coop.deltaHeavyMs
    ///     (default 1000 ms);
    ///   - latest-state lane: Position/Velocity/CampaignPlayTime/
    ///     GeoscapeDateTime component updates ride PosDeltaMsg on
    ///     Protocol.ChannelPos / Sequenced at x2coop.deltaPosMs cadence
    ///     (default 500 ms) - timer spam (~120/s on the modal heartbeat) and
    ///     positional churn never touch the reliable stream;
    ///   - fixed emit tick x2coop.deltaSendMs (default 80 = 12.5 Hz) instead
    ///     of per-rendered-frame sends.
    ///
    /// Join window (spec section 6 + T5.2 addendum): while
    /// <see cref="MirrorState.HostJoinFreeze"/> is latched, encoded StateDelta
    /// wire bytes buffer inside <see cref="DeltaQueue.OfferJoin"/> (every
    /// buffered delta already has seq &gt; BaselineSeq - the baseline snapshot
    /// happens inside the same frame the freeze latches, before any tick can
    /// emit). On GuestReady, <see cref="OnGuestReady"/> flushes the buffer
    /// ordered ahead of the live stream; an over-cap buffer is dropped and
    /// returns "forced resync" instead. A non-Ready status drops the buffer
    /// and releases the latch.
    ///
    /// Sequence integrity: a seq is BURNED on every emit attempt (encode/send
    /// failure included) - the guest's gap detection converts a lost delta
    /// into a resync; rewinding seqs would silently diverge instead.
    ///
    /// Lifetime hygiene (spec T5.2): <see cref="Reset"/> drops the queue
    /// (component snapshots + serialized buffers), the serializer binding
    /// (world weak-ref only - never pinned), the join buffer, sequence
    /// counters and every watchdog. Called from SessionHost.ClearGuest,
    /// strategy-left transitions, pack teardown and the lazy no-guest path.
    /// </summary>
    public static class DeltaSender
    {
        // ---- knobs (x2coop.delta*) ------------------------------------------
        /// <summary>Reliable emit tick (default 80 ms = 12.5 Hz; spec band 10-15 Hz).</summary>
        private const int DefaultSendMs = 80;
        /// <summary>Latest-state emit cadence (default 500 ms = 2 Hz; spec band 1-2 Hz).</summary>
        private const int DefaultPosMs = 500;
        /// <summary>Payload size above which a component is rate-capped (default 1 KiB).</summary>
        private const int DefaultHeavyBytes = 1024;
        /// <summary>Min interval between heavy snapshots per (entity,component) (default 1 s).</summary>
        private const int DefaultHeavyMs = 1000;
        /// <summary>Join buffer cap: R9 speed-3 lower bound (~184 KB/s) x 30 s ~= 5.5 MB.</summary>
        private const long DefaultJoinCapBytes = 184L * 1024 * 30; // 5,652,480
        /// <summary>"delta-stats" line interval (default 1000 ms, 0 = off).</summary>
        private const int DefaultStatsMs = 1000;

        private const int MinSendMs = 20, MaxSendMs = 1000;
        private const int MinPosMs = 250, MaxPosMs = 5000;
        private const int MinHeavyMs = 50, MaxHeavyMs = 60000;
        private const long MinJoinCap = 1024L * 1024, MaxJoinCap = 64L * 1024 * 1024;

        /// <summary>
        /// R9 addendum: client-derivable/positional lane. T5.2-r1 (review
        /// M4): keys are Type.FullName, not Type.Name - simple names collide
        /// across namespaces (a modded "Foo.PositionComponent" would wrongly
        /// ride the unreliable lane, and a renamed namespace would silently
        /// re-reliable a positional type).
        /// </summary>
        private static readonly HashSet<string> PosTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Common.Components.PositionComponent",
            "Common.Components.VelocityComponent",
            "Xenonauts.Common.Components.CampaignPlayTimeComponent",
            "Xenonauts.Strategy.Components.MainBase.GeoscapeDateTimeComponent",
        };

        /// <summary>R9 addendum: measured-big types - always capped, size or not (FullName keys, M4).</summary>
        private static readonly HashSet<string> HeavyTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Xenonauts.Strategy.Components.ActivityStateMachineComponent",
            "Common.Components.UIControllersComponent",
            "Artitas.Components.TemplateRelationshipsComponent",
            "Xenonauts.Strategy.Components.NotificationStateMachineComponent",
        };

        private static readonly DeltaQueue _q = new DeltaQueue();
        /// <summary>(entity,component) key -> wall-ms of the last emitted heavy op.</summary>
        private static readonly Dictionary<long, long> _heavyLast = new Dictionary<long, long>();
        private static readonly List<DeltaOp> _emitOps = new List<DeltaOp>(256);
        private static readonly List<long> _emittedKeys = new List<long>(256);

        /// <summary>Serializer of the CURRENT host world - weak ref, re-resolved on world change, never pinned.</summary>
        private static WeakReference<World> _serWorld;
        private static fsSerializer _serializer;
        private static bool _serWarned;

        /// <summary>Latest-state seq counter - its own stream (never gap-flagged against StateDelta).</summary>
        private static long _posSeq;

        private static readonly Stopwatch _mono = Stopwatch.StartNew();
        private static readonly Stopwatch _tickWatch = new Stopwatch();   // reliable emit cadence
        private static readonly Stopwatch _posWatch = new Stopwatch();    // pos emit cadence
        private static readonly Stopwatch _statsWatch = new Stopwatch();  // stats line cadence
        private static readonly HashSet<string> _faultLogged = new HashSet<string>(StringComparer.Ordinal);

        // ---- per-second stats window + session counters ----------------------
        private static long _secRelBytes, _secRelOps, _secRelMsgs;
        private static long _secPosBytes, _secPosOps, _secPosMsgs;
        private static long _secSerCalls, _secSerFail, _secSerTicks;
        private static long _secDeferred, _secDroppedDead, _secSendFail, _secEncFail;

        /// <summary>Reliable StateDelta messages sent/buffered this session.</summary>
        public static long DeltasSent;
        /// <summary>Component/entity ops carried by those messages.</summary>
        public static long DeltaOpsSent;
        /// <summary>Wire bytes carried by those messages.</summary>
        public static long DeltaBytesSent;
        /// <summary>PosDelta messages sent this session.</summary>
        public static long PosSent;
        /// <summary>Components that could not be serialized (dropped at emit).</summary>
        public static long SerFailures;

        /// <summary>
        /// T5.5 WorldHash settle probe: pending work that could still change
        /// hash-visible world state. Counts every queued structural op plus
        /// pending component records whose type would be hashed - i.e. NOT
        /// whitelisted (guest-local presentation state), not
        /// [ExcludeFromSerialization], and not an IOptionalSerialization
        /// snapshot that opted out. Positional-lane ops count too: the pos
        /// tick flushes them on the same Consume pass. A hash can only be
        /// taken faithfully once this reads 0 (and stays 0 for hashQuietMs).
        /// </summary>
        internal static int HashVisiblePending
        {
            get
            {
                int n = _q.Structural.Count;
                foreach (KeyValuePair<long, DeltaQueue.PendingOp> kv in _q.PendingMap)
                {
                    DeltaQueue.PendingOp rec = kv.Value;
                    if (rec.CompType != null)
                    {
                        if (Whitelist.Contains(rec.CompType)) { continue; }
                        if (MirrorFirewall.IsTypeExcluded(rec.CompType)) { continue; }
                        IOptionalSerialization opt = rec.Snapshot as IOptionalSerialization;
                        if (opt != null && !opt.IsSerializable) { continue; }
                    }
                    n++;
                }
                return n;
            }
        }

        // ------------------------------------------------------------------
        // Runner.LateUpdate seam
        // ------------------------------------------------------------------

        /// <summary>
        /// Called from Runner.FlushReplication EVERY rendered frame, after
        /// ReplicationSource.FlushFrame has drained the capture. Folds the
        /// frame's ops into <see cref="DeltaQueue"/> (snapshotting components
        /// while the references are still live) and pumps the emit ticks.
        /// </summary>
        internal static void Consume(Runner runner)
        {
            try
            {
                if (runner == null || !SessionHost.HasGuest)
                {
                    LazySessionReset();
                    return;
                }
                World w = HostWorld();
                // T5.2-r1 (review M2): a DISPOSED world is not a null world -
                // the weak-ref guard below cannot see it. Queue mutations on
                // captured ops from a dead world would linger forever (the
                // dead-id ban is the only stale filter and it knows nothing
                // about world liveness), so treat disposed like absent.
                if (w == null || w.Disposed)
                {
                    LazyWorldReset();
                    return;
                }
                EnsureSerializer(w);
                FoldFrameOps(w);
                long nowMs = _mono.ElapsedMilliseconds;
                PumpEmitTick(runner, nowMs);
                PumpPosTick(runner, nowMs);
                PumpStats();
            }
            catch (Exception ex)
            {
                Fault("consume", ex);
            }
        }

        /// <summary>
        /// SessionHost.HandleGuestReady seam. Ready: flush the join buffer
        /// ordered ahead of the live stream - unless it overflowed, in which
        /// case return true and let the caller force a resync instead. Any
        /// other status drops the buffer (the guest will resync itself).
        /// </summary>
        internal static bool OnGuestReady(Runner runner, int peerId, GuestReadyStatus status)
        {
            try
            {
                if (status != GuestReadyStatus.Ready)
                {
                    int dropped = _q.DropJoin();
                    _q.ClearResync();
                    if (dropped > 0)
                    {
                        Log.Info("delta: join buffer dropped msgs=" + dropped
                            + " (guest status=" + status + ")");
                    }
                    return false;
                }
                if (_q.ResyncNeeded)
                {
                    _q.ClearResync();
                    _q.DropJoin();
                    Log.Warn("delta: join buffer overflowed cap=" + JoinCapBytes()
                        + " - forcing resync (guest's load is pre-overflow)");
                    return true;
                }
                FlushJoin(runner);
                return false;
            }
            catch (Exception ex)
            {
                Fault("guest-ready", ex);
                return false;
            }
        }

        /// <summary>
        /// T5.2-r1 (review I-2) save-serialization barrier. Called by
        /// SaveStreamer.SendOffer AFTER CopyWorldIntoWorldState has snapshotted
        /// the host world and BEFORE the SaveOffer goes out. The save bytes are
        /// now the guest's ground truth: every op captured BEFORE this call -
        /// including everything folded during the queue-to-serialize window
        /// (offer delay + world-ready wait can span seconds) - describes
        /// PRE-snapshot state. Flushing those ops at GuestReady would re-apply
        /// mutations the save already contains (double-apply) or revert state
        /// the snapshot captured (regression). So: drop pending ops, the join
        /// buffer and any resync latch, and re-snapshot BaselineSeq so the
        /// first delta the guest ever sees is the first POST-snapshot seq.
        /// Post-snapshot ops (PostSaveGameReport handlers onward) stay queued.
        /// </summary>
        internal static void OnSaveSerialized()
        {
            try
            {
                int pend = _q.PendingCount;
                int join = _q.JoinMsgCount;
                long seqAtBarrier = MirrorState.LastDeltaSeq;
                _q.ClearOps();
                _heavyLast.Clear();
                _q.DropJoin();
                _q.ClearResync();
                MirrorState.BaselineSeq = seqAtBarrier;
                if (pend > 0 || join > 0)
                {
                    Log.Info("delta: save-serialize barrier cleared pend=" + pend
                        + " joinMsgs=" + join
                        + " baselineSeq=" + MirrorState.BaselineSeq
                        + " (pre-snapshot ops can never regress the guest)");
                }
            }
            catch (Exception ex)
            {
                Fault("save-barrier", ex);
            }
        }

        /// <summary>
        /// SaveStreamer.QueueOnAccept seam: a new baseline is about to be
        /// snapshot + streamed, so any delta buffered for the PREVIOUS window
        /// is stale - drop it and clear an overflow latch (the resync that was
        /// coming IS this stream).
        /// </summary>
        internal static void OnStreamQueued()
        {
            try
            {
                int dropped = _q.DropJoin();
                _q.ClearResync();
                if (dropped > 0)
                {
                    Log.Info("delta: join buffer dropped msgs=" + dropped
                        + " (new baseline queued)");
                }
            }
            catch (Exception ex)
            {
                Fault("stream-queued", ex);
            }
        }

        /// <summary>
        /// Full session-end reset: SessionHost.ClearGuest, pack teardown and
        /// the lazy no-guest path. Drops EVERYTHING the sender holds -
        /// pending ops (owned component copies die with the queue), the join
        /// buffer, the serializer binding, sequence counters and the fault
        /// set. No world/component reference may survive session end (R3:
        /// the same assembly keeps these statics across a re-enable).
        /// </summary>
        internal static void Reset(string why)
        {
            bool had = _q.PendingCount > 0 || _q.JoinMsgCount > 0
                || _q.ResyncNeeded || _serializer != null
                || MirrorState.LastDeltaSeq != 0 || _posSeq != 0;
            _q.Reset();
            _heavyLast.Clear();
            _serWorld = null;
            _serializer = null;
            _serWarned = false;
            _posSeq = 0;
            MirrorState.LastDeltaSeq = 0;
            MirrorState.BaselineSeq = 0;
            _tickWatch.Reset();
            _posWatch.Reset();
            _statsWatch.Reset();
            _faultLogged.Clear();
            DeltasSent = 0;
            DeltaOpsSent = 0;
            DeltaBytesSent = 0;
            PosSent = 0;
            SerFailures = 0;
            if (had)
            {
                Log.Info("delta: reset (" + (why ?? "?") + ")");
            }
        }

        // ------------------------------------------------------------------
        // frame fold: FrameOps -> DeltaQueue (live refs die after this call)
        // ------------------------------------------------------------------

        private static void FoldFrameOps(World w)
        {
            // M2 belt-and-braces: never mutate the queue for a world that
            // died between Consume's check and the fold (component refs in
            // the frame ops are exactly as dead as the world).
            if (w == null || w.Disposed)
            {
                return;
            }
            List<ReplicationSource.CapturedOp> ops = ReplicationSource.FrameOps;
            for (int i = 0; i < ops.Count; i++)
            {
                ReplicationSource.CapturedOp op = ops[i];
                switch (op.Kind)
                {
                    case ReplicationSource.OpKind.Create:
                        _q.RecordCreate(op.EntityId);
                        break;
                    case ReplicationSource.OpKind.Delete:
                        _q.RecordDelete(op.EntityId);
                        break;
                    default:
                        FoldMutation(w, op);
                        break;
                }
            }
        }

        /// <summary>
        /// Fold one captured mutation into the send window. The component
        /// reference is copied NOW (IComponent.Copy - the same clone
        /// WorldState.cs:108 takes for save snapshots) so the queue never
        /// holds a live ref; if Copy throws, the live ref is serialized
        /// IMMEDIATELY (still inside its valid frame) and only the wire bytes
        /// are kept. A double failure drops the op.
        /// </summary>
        private static void FoldMutation(World w, ReplicationSource.CapturedOp op)
        {
            Type type = null;
            try { type = w.ComponentManager?.FindComponentType(op.ComponentId); }
            catch { }
            if (type == null && op.Next != null)
            {
                // Unresolvable component-id (T5.1 fails open): the runtime type
                // still serializes + names the op - same FullName either way.
                type = op.Next.GetType();
            }
            var rec = new DeltaQueue.PendingOp
            {
                EntityId = op.EntityId,
                ComponentId = op.ComponentId,
                CompType = type,
                Remove = !op.Added,
                FromCreate = op.FromCreate,
            };
            if (!rec.Remove)
            {
                string name = type != null ? type.FullName : string.Empty;
                rec.Positional = !rec.FromCreate && PosTypeNames.Contains(name);
                IComponent snap = null;
                try { snap = op.Next != null ? op.Next.Copy() : null; }
                catch { }
                if (snap != null)
                {
                    rec.Snapshot = snap;
                }
                else
                {
                    // Clone failed (or the op carried a null instance): serialize
                    // the live reference right now, before the frame dies.
                    using (AssetDescriptor.EnterSaveSerialization())
                    {
                        rec.Payload = SerializeNow(type, op.Next);
                    }
                    if (rec.Payload == null)
                    {
                        _secSerFail++;
                        SerFailures++;
                        return; // unsendable - drop, never queue
                    }
                }
            }
            if (!_q.Put(rec))
            {
                _secDroppedDead++;
            }
        }

        // ------------------------------------------------------------------
        // reliable emit tick (10-15 Hz): structural + component ops
        // ------------------------------------------------------------------

        private static void PumpEmitTick(Runner runner, long nowMs)
        {
            int sendMs = ClampInt(DebugSettings.GetInt("deltaSendMs", DefaultSendMs),
                MinSendMs, MaxSendMs);
            if (!_tickWatch.IsRunning) { _tickWatch.Restart(); }
            if (_tickWatch.ElapsedMilliseconds < sendMs) { return; }
            _tickWatch.Restart();

            if (_q.ResyncNeeded)
            {
                // Buffer overflowed: pending ops are pre-resync garbage.
                _q.ClearOps();
                _heavyLast.Clear();
                return;
            }

            bool frozen = MirrorState.HostJoinFreeze;
            if (!frozen && _q.JoinMsgCount > 0)
            {
                FlushJoin(runner); // backstop (OnGuestReady flushes first)
            }

            List<DeltaOp> ops = _emitOps;
            ops.Clear();
            _emittedKeys.Clear();

            foreach (DeltaQueue.StructOp s in _q.Structural)
            {
                ops.Add(new DeltaOp
                {
                    EntityId = s.EntityId,
                    Kind = s.Kind == DeltaQueue.StructKind.Create
                        ? DeltaOpKind.CreateEntity
                        : DeltaOpKind.DeleteEntity,
                });
            }
            _q.CommitStructural();

            int heavyBytes = Math.Max(256, DebugSettings.GetInt("deltaHeavyBytes", DefaultHeavyBytes));
            long heavyMs = ClampLong(DebugSettings.GetInt("deltaHeavyMs", DefaultHeavyMs),
                MinHeavyMs, MaxHeavyMs);

            using (AssetDescriptor.EnterSaveSerialization())
            {
                foreach (KeyValuePair<long, DeltaQueue.PendingOp> kv in _q.PendingMap)
                {
                    DeltaQueue.PendingOp rec = kv.Value;
                    if (rec.Positional) { continue; } // the pos lane owns it
                    if (!rec.Remove)
                    {
                        if (rec.Payload == null)
                        {
                            if (rec.SerFailed)
                            {
                                _secSerFail++;
                                _emittedKeys.Add(kv.Key); // unsendable - drop it
                                continue;
                            }
                            if (_serializer == null)
                            {
                                continue;   // serializer not bound yet - stays pending
                            }
                            long t0 = _mono.ElapsedTicks;
                            rec.Payload = SerializeNow(rec.CompType, rec.Snapshot);
                            _secSerTicks += _mono.ElapsedTicks - t0;
                            _secSerCalls++;
                            rec.Snapshot = null; // owned copy spent either way
                            if (rec.Payload == null)
                            {
                                rec.SerFailed = true;
                                _secSerFail++;
                                SerFailures++;
                                _emittedKeys.Add(kv.Key); // unsendable - drop it
                                continue;
                            }
                        }
                        bool heavy = rec.Payload.Length > heavyBytes
                            || (rec.CompType != null && HeavyTypeNames.Contains(rec.CompType.FullName));
                        if (heavy)
                        {
                            long last;
                            if (_heavyLast.TryGetValue(kv.Key, out last)
                                && nowMs - last < heavyMs)
                            {
                                _secDeferred++;
                                continue; // rate-capped: stays pending
                            }
                            _heavyLast[kv.Key] = nowMs;
                        }
                    }
                    ops.Add(new DeltaOp
                    {
                        EntityId = rec.EntityId,
                        Kind = rec.Remove ? DeltaOpKind.RemoveComponent : DeltaOpKind.SetComponent,
                        ComponentType = rec.CompType != null ? rec.CompType.FullName : string.Empty,
                        Payload = rec.Payload,
                    });
                    _emittedKeys.Add(kv.Key);
                }
            }
            for (int i = 0; i < _emittedKeys.Count; i++)
            {
                _q.PendingMap.Remove(_emittedKeys[i]);
            }
            if (ops.Count == 0) { return; }
            EmitReliable(runner, ops, frozen);
        }

        // ------------------------------------------------------------------
        // latest-state lane (PosDelta, ~2 Hz, Sequenced)
        // ------------------------------------------------------------------

        private static void PumpPosTick(Runner runner, long nowMs)
        {
            if (MirrorState.HostJoinFreeze) { return; } // values are stale by definition; keep newest pending
            if (_q.ResyncNeeded) { return; }
            int posMs = ClampInt(DebugSettings.GetInt("deltaPosMs", DefaultPosMs),
                MinPosMs, MaxPosMs);
            if (!_posWatch.IsRunning) { _posWatch.Restart(); }
            if (_posWatch.ElapsedMilliseconds < posMs) { return; }
            _posWatch.Restart();

            List<DeltaOp> ops = _emitOps;
            ops.Clear();
            _emittedKeys.Clear();
            using (AssetDescriptor.EnterSaveSerialization())
            {
                foreach (KeyValuePair<long, DeltaQueue.PendingOp> kv in _q.PendingMap)
                {
                    DeltaQueue.PendingOp rec = kv.Value;
                    if (!rec.Positional) { continue; }
                    if (rec.Payload == null)
                    {
                        if (rec.SerFailed)
                        {
                            _secSerFail++;
                            _emittedKeys.Add(kv.Key); // unsendable - drop it
                            continue;
                        }
                        if (_serializer == null)
                        {
                            continue;   // serializer not bound yet - stays pending
                        }
                        long t0 = _mono.ElapsedTicks;
                        rec.Payload = SerializeNow(rec.CompType, rec.Snapshot);
                        _secSerTicks += _mono.ElapsedTicks - t0;
                        _secSerCalls++;
                        rec.Snapshot = null;
                        if (rec.Payload == null)
                        {
                            rec.SerFailed = true;
                            _secSerFail++;
                            SerFailures++;
                            _emittedKeys.Add(kv.Key);
                            continue;
                        }
                    }
                    ops.Add(new DeltaOp
                    {
                        EntityId = rec.EntityId,
                        Kind = DeltaOpKind.SetComponent,
                        ComponentType = rec.CompType != null ? rec.CompType.FullName : string.Empty,
                        Payload = rec.Payload,
                    });
                    _emittedKeys.Add(kv.Key);
                }
            }
            for (int i = 0; i < _emittedKeys.Count; i++)
            {
                _q.PendingMap.Remove(_emittedKeys[i]);
            }
            if (ops.Count == 0) { return; }

            NetTransport t = runner.Transport;
            int peer = SessionHost.GuestPeerId;
            if (t == null || !t.IsRunning || peer < 0) { return; }

            // T5.2-r1 (review I-1): PosDelta rides Sequenced - LiteNetLib does
            // NOT fragment it (only ReliableOrdered/ReliableUnordered do), so
            // Protocol.MaxMessageSize was a fantasy budget: a >MTU packet
            // throws TooBigPacketException inside NetPeer.Send and every pos
            // tick dropped on the floor. Split to the peer's REAL
            // single-packet budget instead (MTU minus the sequenced header).
            int budget = t.MaxSinglePacketSize(peer, MsgCodec.DeliveryFor(MsgType.PosDelta));
            if (budget <= DeltaPacker.FixedBytes)
            {
                // Peer vanished between IsConnected and the query (or a
                // degenerate MTU) - keep the ops pending; the next tick
                // retries. Never guess a smaller budget: an over-MTU send
                // counts as a send-fail, not a silent re-split.
                return;
            }
            List<DeltaOp[]> chunks = DeltaPacker.Split(ops, budget);
            for (int i = 0; i < chunks.Count; i++)
            {
                var msg = new PosDeltaMsg
                {
                    SessionId = runner.SessionId,
                    Seq = ++_posSeq,   // burned per emit attempt - same rule as StateDelta
                    HostSendTicks = DateTime.UtcNow.Ticks,
                    Ops = chunks[i],
                };
                byte[] wire = MsgCodec.Encode(msg);
                if (wire == null)
                {
                    _secEncFail++; // encode failure - never hit the socket
                    continue;
                }
                if (!t.Send(peer, MsgCodec.ChannelFor(MsgType.PosDelta),
                    wire, 0, wire.Length, MsgCodec.DeliveryFor(MsgType.PosDelta)))
                {
                    _secSendFail++; // T5.2-r1: failed sends were invisible before
                    if (_faultLogged.Add("pos-send-fail"))
                    {
                        Log.Warn("delta-pos seq=" + msg.Seq
                            + " send failed bytes=" + wire.Length
                            + " budget=" + budget + " (peer gone?)");
                    }
                    continue;
                }
                _secPosBytes += wire.Length;
                _secPosOps += chunks[i].Length;
                _secPosMsgs++;
                PosSent++;
                if (LogEach())
                {
                    Log.Info("delta-pos seq=" + msg.Seq + " ops=" + chunks[i].Length
                        + " bytes=" + wire.Length + " budget=" + budget);
                }
            }
        }

        // ------------------------------------------------------------------
        // emit + join buffer
        // ------------------------------------------------------------------

        /// <summary>
        /// Split + seq-stamp + encode + send/buffer one op batch. Seq is
        /// incremented (burned) before encode - see class doc. While frozen
        /// the encoded bytes buffer in DeltaQueue; otherwise the join buffer
        /// drains first (backstop) and the packet goes out ReliableOrdered on
        /// the control channel.
        /// </summary>
        private static void EmitReliable(Runner runner, List<DeltaOp> ops, bool frozen)
        {
            NetTransport t = runner.Transport;
            int peer = SessionHost.GuestPeerId;
            Guid sid = runner.SessionId;
            if (t == null || !t.IsRunning || peer < 0 || sid == Guid.Empty) { return; }

            List<DeltaOp[]> chunks = DeltaPacker.Split(ops, Protocol.MaxMessageSize);
            long cap = JoinCapBytes();
            bool wasResync = _q.ResyncNeeded;
            for (int i = 0; i < chunks.Count; i++)
            {
                var msg = new StateDeltaMsg
                {
                    SessionId = sid,
                    Seq = ++MirrorState.LastDeltaSeq,
                    HostSendTicks = DateTime.UtcNow.Ticks,
                    Ops = chunks[i],
                };
                byte[] wire = MsgCodec.Encode(msg);
                if (wire == null) { _secEncFail++; continue; } // encode fail - seq still burned on purpose
                DeltasSent++;
                DeltaOpsSent += chunks[i].Length;
                DeltaBytesSent += wire.Length;
                if (frozen)
                {
                    _q.OfferJoin(wire, cap);
                    if (LogEach())
                    {
                        Log.Info("delta seq=" + msg.Seq + " ops=" + chunks[i].Length
                            + " bytes=" + wire.Length
                            + " buffered joinKB=" + (_q.JoinBytes / 1024));
                    }
                }
                else
                {
                    _secRelBytes += wire.Length;
                    _secRelOps += chunks[i].Length;
                    _secRelMsgs++;
                    if (!t.Send(peer, MsgCodec.ChannelFor(MsgType.StateDelta),
                        wire, 0, wire.Length, MsgCodec.DeliveryFor(MsgType.StateDelta)))
                    {
                        _secSendFail++;
                        if (_faultLogged.Add("send-fail"))
                        {
                            Log.Warn("delta seq=" + msg.Seq + " send failed (peer gone?)");
                        }
                    }
                    if (LogEach())
                    {
                        Log.Info("delta seq=" + msg.Seq + " ops=" + chunks[i].Length
                            + " bytes=" + wire.Length);
                    }
                }
            }
            if (frozen && !wasResync && _q.ResyncNeeded)
            {
                Log.Warn("delta: join buffer over cap=" + cap
                    + " (dropped ~" + (_q.ResyncDroppedBytes / 1024) + "KB)"
                    + " - resync forced at GuestReady");
            }
        }

        /// <summary>Send every buffered join-window message, ordered, on the control channel.</summary>
        private static void FlushJoin(Runner runner)
        {
            List<byte[]> buffered = _q.DrainJoin();
            if (buffered.Count == 0) { return; }
            NetTransport t = runner != null ? runner.Transport : null;
            int peer = SessionHost.GuestPeerId;
            long bytes = 0;
            int sent = 0;
            for (int i = 0; i < buffered.Count; i++)
            {
                byte[] wire = buffered[i];
                bytes += wire.Length;
                if (t == null || !t.IsRunning || peer < 0) { continue; }
                if (t.Send(peer, MsgCodec.ChannelFor(MsgType.StateDelta),
                    wire, 0, wire.Length, MsgCodec.DeliveryFor(MsgType.StateDelta)))
                {
                    sent++;
                }
            }
            _secRelMsgs += sent;
            _secRelBytes += bytes;
            Log.Info("delta join-flush msgs=" + buffered.Count
                + " bytes=" + bytes + (sent < buffered.Count
                    ? " (sent=" + sent + ")" : string.Empty));
        }

        // ------------------------------------------------------------------
        // serialization (game pipeline: world's own fsSerializer)
        // ------------------------------------------------------------------

        /// <summary>
        /// Resolve (once per world) the StrategyWorld's ArtitasSerializationSystem
        /// .Serializer - the exact fsSerializer the save path uses. Re-resolves
        /// automatically when the world instance changes; the weak ref never
        /// pins a dead world.
        /// </summary>
        private static void EnsureSerializer(World w)
        {
            World cur = null;
            WeakReference<World> wr = _serWorld;
            if (wr != null)
            {
                try { wr.TryGetTarget(out cur); } catch { }
            }
            if (ReferenceEquals(cur, w) && _serializer != null) { return; }
            _serializer = null;
            try { _serializer = w.GetSystem<ArtitasSerializationSystem>()?.Serializer; }
            catch (Exception ex) { Fault("serializer-resolve", ex); }
            _serWorld = new WeakReference<World>(w);
            if (_serializer == null && !_serWarned)
            {
                _serWarned = true;
                Log.Warn("delta: no ArtitasSerializationSystem serializer on host world - ops stay queued");
            }
        }

        /// <summary>
        /// Serialize one component with the game serializer (the same
        /// TrySerialize -&gt; CompressedJson -&gt; UTF8 path the R9 probe
        /// measured). SIDE-EFFECT FREE: the instance is always our owned
        /// snapshot (or a doomed-live ref at fold time) and the Artitas
        /// EntityConverter writes entity IDs only - nothing here mutates the
        /// host world. Callers wrap in AssetDescriptor.EnterSaveSerialization
        /// (the same scope the save path runs under) except the fold-time
        /// fallback, which wraps per-call.
        /// </summary>
        private static byte[] SerializeNow(Type type, object instance)
        {
            if (_serializer == null || type == null || instance == null) { return null; }
            try
            {
                fsResult r = _serializer.TrySerialize(type, instance, out fsData data);
                if (r.Failed || data == null) { return null; }
                string json = fsJsonPrinter.CompressedJson(data);
                return json == null ? null : Encoding.UTF8.GetBytes(json);
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // stats + resets + helpers
        // ------------------------------------------------------------------

        /// <summary>"delta-stats" per-second line - the R9 bandwidth evidence.</summary>
        private static void PumpStats()
        {
            int ms = DebugSettings.GetInt("deltaStatsMs", DefaultStatsMs);
            if (ms <= 0)
            {
                if (_statsWatch.IsRunning) { _statsWatch.Reset(); }
                return;
            }
            if (!_statsWatch.IsRunning) { _statsWatch.Restart(); return; }
            if (_statsWatch.ElapsedMilliseconds < ms) { return; }
            _statsWatch.Restart();
            Log.Info("delta-stats bps=" + _secRelBytes
                + " msgs=" + _secRelMsgs
                + " ops=" + _secRelOps
                + " posBps=" + _secPosBytes
                + " posOps=" + _secPosOps
                + " ser=" + _secSerCalls
                + " serMs=" + (_secSerTicks / TimeSpan.TicksPerMillisecond)
                + " serFail=" + _secSerFail
                + " defer=" + _secDeferred
                + " dead=" + _secDroppedDead
                + " sendFail=" + _secSendFail
                + " encFail=" + _secEncFail
                + " pend=" + _q.PendingCount
                + " joinKB=" + (_q.JoinBytes / 1024));
            _secRelBytes = _secRelOps = _secRelMsgs = 0;
            _secPosBytes = _secPosOps = _secPosMsgs = 0;
            _secSerCalls = _secSerFail = _secSerTicks = 0;
            _secDeferred = _secDroppedDead = _secSendFail = _secEncFail = 0;
        }

        /// <summary>Lazy full reset when no guest holds the slot (backstop for ClearGuest).</summary>
        private static void LazySessionReset()
        {
            Reset("no guest");
        }

        /// <summary>
        /// Mid-session world loss (host left Strategy): drop pending ops,
        /// join buffer and the serializer binding - they all reference the
        /// dead world. Sequence counters stay monotonic; the guest's resync
        /// on HostReturned re-baselines.
        /// </summary>
        private static void LazyWorldReset()
        {
            bool had = _q.PendingCount > 0 || _q.JoinMsgCount > 0
                || _q.ResyncNeeded || _serializer != null;
            _q.Reset();
            _heavyLast.Clear();
            _serWorld = null;
            _serializer = null;
            _serWarned = false;
            if (had)
            {
                Log.Info("delta: cleared (host left strategy - pending state was dead-world)");
            }
        }

        private static World HostWorld()
        {
            try { return ScreenGuard.StrategyWorld; }
            catch { return null; }
        }

        private static long JoinCapBytes()
        {
            return ClampLong(DebugSettings.GetInt("deltaJoinCapKB",
                (int)(DefaultJoinCapBytes / 1024)) * 1024L, MinJoinCap, MaxJoinCap);
        }

        private static bool LogEach()
        {
            return DebugSettings.GetInt("deltaLogEach", 1) != 0;
        }

        private static void Fault(string tag, Exception ex)
        {
            if (_faultLogged.Add(tag))
            {
                Log.Exception("delta " + tag, ex);
            }
        }

        private static int ClampInt(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static long ClampLong(long v, long lo, long hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
