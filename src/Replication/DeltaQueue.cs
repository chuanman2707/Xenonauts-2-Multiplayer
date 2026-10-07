using System;
using System.Collections.Generic;
using X2Coop.Net;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.2 send-tick accumulator - deliberately game-free (DeltaSender owns
    /// it; the xunit suite links this file directly and never touches Unity or
    /// Artitas). Frame-folded <see cref="ReplicationSource.FrameOps"/> land
    /// here across a whole send window (the fixed ~12.5 Hz emit tick), which
    /// is where the R9 addendum's cross-frame coalescing lives:
    ///
    ///   - Component mutations go into a last-write-wins map keyed
    ///     (entityId, componentId) - one record per key per window,
    ///     the newest replacing the oldest wholesale (latest state wins).
    ///   - Entity create/delete ops go into an ordered structural list with
    ///     per-entity dedup: a create+delete pair inside one window cancels
    ///     (the entity never reached the wire); a delete+create pair keeps
    ///     both (id recycling replays correctly on the guest).
    ///   - Dead entity ids are tracked for the whole session
    ///     (<see cref="_dead"/>): a mutation captured for an already-deleted
    ///     id (delayed-mutation drain, same-window bypass write) can never
    ///     leak a zombie entity onto the guest; a later create for the id
    ///     lifts the ban.
    ///
    /// The join-freeze window buffers ENCODED wire bytes (<see cref="OfferJoin"/>)
    /// so the ~5.5 MB cap counts exactly what would have hit the socket;
    /// overflowing latches <see cref="ResyncNeeded"/>, drops the buffer and
    /// the forced-resync path in DeltaSender/SessionHost takes over.
    /// </summary>
    internal sealed class DeltaQueue
    {
        internal enum StructKind : byte { Create = 1, Delete = 2 }

        /// <summary>One queued structural op (entity lifecycle).</summary>
        internal struct StructOp
        {
            public int EntityId;
            public StructKind Kind;
        }

        /// <summary>
        /// One pending component record. <see cref="Snapshot"/> is the owned
        /// copy (IComponent.Clone taken at fold time - live refs never live
        /// here); <see cref="Payload"/> is the serialized wire bytes once the
        /// sender has produced them (either lane).
        /// </summary>
        internal sealed class PendingOp
        {
            public int EntityId;
            public int ComponentId;
            public Type CompType;            // canonical type (registered or runtime); null = unresolvable
            public bool Remove;              // true = component removal (no payload)
            public bool FromCreate;          // create-enrichment op - always rides the reliable lane
            public bool Positional;          // true = latest-state lane (PosDelta/Sequenced)
            public object Snapshot { get; set; }   // owned component copy (null once serialized or on remove)
            public byte[] Payload { get; set; }    // serialized wire bytes once produced
            public bool SerFailed { get; set; }    // payload can never be produced - drop at drain
        }

        private readonly Dictionary<long, PendingOp> _pend =
            new Dictionary<long, PendingOp>();
        private readonly List<StructOp> _structural = new List<StructOp>();
        private readonly Dictionary<int, StructKind> _lastStruct =
            new Dictionary<int, StructKind>();
        private readonly HashSet<int> _dead = new HashSet<int>();
        private readonly List<long> _prune = new List<long>();

        private readonly List<byte[]> _joinBuf = new List<byte[]>();
        private long _joinBytes;
        private bool _resyncNeeded;
        private long _resyncDroppedBytes;

        /// <summary>Map key: (entityId &lt;&lt; 32) | componentId - mirrors the capture dict.</summary>
        internal static long Key(int entityId, int componentId)
        {
            return ((long)entityId << 32) | (uint)componentId;
        }

        internal int PendingCount => _pend.Count;
        internal IReadOnlyList<StructOp> Structural => _structural;
        internal Dictionary<long, PendingOp> PendingMap => _pend;

        internal long JoinBytes => _joinBytes;
        internal int JoinMsgCount => _joinBuf.Count;
        internal bool ResyncNeeded => _resyncNeeded;
        internal long ResyncDroppedBytes => _resyncDroppedBytes;

        /// <summary>
        /// Entity creation: lifts the dead-ban (the id is alive again) and
        /// appends a structural op. A second create for an already-live id is
        /// a no-op (id uniqueness; defensive - Obtain dedups per frame).
        /// </summary>
        internal void RecordCreate(int entityId)
        {
            _dead.Remove(entityId);
            StructKind last;
            if (_lastStruct.TryGetValue(entityId, out last) && last == StructKind.Create)
            {
                return; // duplicate create this window - defensive dedup
            }
            _structural.Add(new StructOp { EntityId = entityId, Kind = StructKind.Create });
            _lastStruct[entityId] = StructKind.Create;
        }

        /// <summary>
        /// Entity deletion: drops every pending component record for the id
        /// (moot bytes - the guest applies the delete either way), bans later
        /// mutations for it, and emits a structural delete - EXCEPT when the
        /// only pending structural op for the id is a create this window
        /// (create+delete cancel: the entity never reached the wire).
        /// delete;create;delete replays as one net delete.
        /// </summary>
        internal void RecordDelete(int entityId)
        {
            DropPendingFor(entityId);
            _dead.Add(entityId);
            StructKind last;
            if (_lastStruct.TryGetValue(entityId, out last))
            {
                if (last == StructKind.Create)
                {
                    RemoveLastCreate(entityId);
                    RescanLastStruct(entityId);
                }
                // last == Delete: second delete this window - dedup.
                return;
            }
            _structural.Add(new StructOp { EntityId = entityId, Kind = StructKind.Delete });
            _lastStruct[entityId] = StructKind.Delete;
        }

        /// <summary>
        /// Last-write-wins insert/replace of a component record. Returns false
        /// (dropped) when the entity id is dead this session. A replacement
        /// keeps a previous <see cref="PendingOp.FromCreate"/> flag so a
        /// create-window write can never fall onto the unreliable lane.
        /// </summary>
        internal bool Put(PendingOp p)
        {
            if (p == null || _dead.Contains(p.EntityId)) { return false; }
            long key = Key(p.EntityId, p.ComponentId);
            PendingOp old;
            if (_pend.TryGetValue(key, out old) && old != null && old.FromCreate)
            {
                p.FromCreate = true;
                p.Positional = false;
            }
            _pend[key] = p;
            return true;
        }

        /// <summary>
        /// Called after a tick's structural ops have been emitted into a
        /// StateDelta - clears the ordered list + dedup map. Dead tracking
        /// persists (it is session truth, not window truth).
        /// </summary>
        internal void CommitStructural()
        {
            _structural.Clear();
            _lastStruct.Clear();
        }

        /// <summary>
        /// Drop pending component records + structural ops (the overflow path:
        /// everything pending is pre-resync garbage once the buffer is gone).
        /// Join buffer + dead set are NOT touched here.
        /// </summary>
        internal void ClearOps()
        {
            _pend.Clear();
            CommitStructural();
        }

        /// <summary>All pending records for an entity id (delete path).</summary>
        private void DropPendingFor(int entityId)
        {
            if (_pend.Count == 0) { return; }
            _prune.Clear();
            foreach (KeyValuePair<long, PendingOp> kv in _pend)
            {
                if (kv.Value.EntityId == entityId) { _prune.Add(kv.Key); }
            }
            for (int i = 0; i < _prune.Count; i++) { _pend.Remove(_prune[i]); }
        }

        /// <summary>Remove the most recent pending create op for the id.</summary>
        private void RemoveLastCreate(int entityId)
        {
            for (int i = _structural.Count - 1; i >= 0; i--)
            {
                if (_structural[i].EntityId == entityId
                    && _structural[i].Kind == StructKind.Create)
                {
                    _structural.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>Recompute <see cref="_lastStruct"/>[entityId] after a removal.</summary>
        private void RescanLastStruct(int entityId)
        {
            for (int i = _structural.Count - 1; i >= 0; i--)
            {
                if (_structural[i].EntityId == entityId)
                {
                    _lastStruct[entityId] = _structural[i].Kind;
                    return;
                }
            }
            _lastStruct.Remove(entityId);
        }

        // ------------------------------------------------------------------
        // join-freeze buffer (encoded wire bytes; cap = R9 30 s budget)
        // ------------------------------------------------------------------

        /// <summary>
        /// Buffer one encoded StateDelta for the join window. Returns false
        /// when the cap is exceeded: the WHOLE buffer is dropped (a partial
        /// flush would be a gap-corrupt stream) and <see cref="ResyncNeeded"/>
        /// latches - the caller forces a resync at GuestReady.
        /// T5.2-r1 (review M3): <see cref="ResyncDroppedBytes"/> ACCUMULATES
        /// the buffer contents + every refused wire across the whole latched
        /// window (earlier it recorded only the first attempt's total, so the
        /// logged "dropped ~NKB" under-reported every retry).
        /// </summary>
        internal bool OfferJoin(byte[] wire, long capBytes)
        {
            if (wire == null) { return false; }
            if (_resyncNeeded)
            {
                _resyncDroppedBytes += wire.Length; // still refusing into the dropped window
                return false;
            }
            if (_joinBytes + wire.Length > capBytes)
            {
                _resyncDroppedBytes += _joinBytes + wire.Length;
                _joinBuf.Clear();
                _joinBytes = 0;
                _resyncNeeded = true;
                return false;
            }
            _joinBuf.Add(wire);
            _joinBytes += wire.Length;
            return true;
        }

        /// <summary>Take the buffered messages (ordered) out; leaves the buffer empty.</summary>
        internal List<byte[]> DrainJoin()
        {
            var list = new List<byte[]>(_joinBuf);
            _joinBuf.Clear();
            _joinBytes = 0;
            return list;
        }

        /// <summary>Drop the buffered messages without sending. Returns the count.</summary>
        internal int DropJoin()
        {
            int n = _joinBuf.Count;
            _joinBuf.Clear();
            _joinBytes = 0;
            return n;
        }

        /// <summary>Clear the overflow latch after the resync has been queued.</summary>
        internal void ClearResync() { _resyncNeeded = false; _resyncDroppedBytes = 0; }

        /// <summary>
        /// Full session teardown: every pending op, structural list, dead-id
        /// set, join buffer and the resync latch. Serialized payloads are the
        /// only bytes held - no live component/world refs ever land here.
        /// </summary>
        internal void Reset()
        {
            _pend.Clear();
            _structural.Clear();
            _lastStruct.Clear();
            _dead.Clear();
            _joinBuf.Clear();
            _joinBytes = 0;
            _resyncNeeded = false;
            _resyncDroppedBytes = 0;
        }
    }
}
