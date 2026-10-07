using System;
using System.Collections.Generic;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.3 (R5) host -&gt; guest entity-id translation. The wire always
    /// speaks HOST entity ids; the guest mirror world has its own id space.
    /// Two bindings exist:
    ///
    ///   - identity: baseline entities were deserialized from the same save
    ///     on both sides, so host id N is the SAME entity as guest id N while
    ///     both are alive (the T3.3 baseline gives the guest the host's exact
    ///     id space). A claim is only ever taken when every guard passes:
    ///     the candidate is alive, inside the baseline allocation range, and
    ///     not already bound to another host id / a placeholder / a
    ///     tombstoned guest slot.
    ///   - placeholder: a host id with no identity claim (post-baseline host
    ///     create, recycled id, or an entity the guest never had) maps to a
    ///     fresh <c>World.CreateEntity()</c> guest id. The host's ops then
    ///     fill it in (a create's initial component set follows immediately
    ///     on the reliable lane).
    ///
    /// Lifecycle truth (mirrors the T5.1 capture rules):
    ///   - a resolve whose binding went dead (entity deleted since) evicts
    ///     the stale pair instead of returning a corpse;
    ///   - <see cref="Tombstone"/> marks a guest id as "we deleted its host
    ///     twin" - a tombstoned slot can never be identity-claimed by a
    ///     LATER host id that happens to equal it (recycled-id protection),
    ///     though the recycling pool may still reissue it as a placeholder;
    ///   - <see cref="BaselineAllocated"/> bounds identity claims to ids the
    ///     world had allocated at mirror activation - anything a guest-local
    ///     system or the applier itself created post-activation sits above
    ///     the bound and can never be claimed as a host twin.
    ///
    /// Game-free on purpose (world access arrives as delegates) so the
    /// xunit suite links + drives it directly. All calls are Unity main
    /// thread (spec section 4); no locking.
    /// </summary>
    internal sealed class EntityIdMap
    {
        /// <summary>Outcome of a <see cref="Resolve"/> call.</summary>
        internal enum Result : byte
        {
            /// <summary>No live binding and none could be made (forWrite=false).</summary>
            Missing = 0,
            /// <summary>Already bound to a live guest entity.</summary>
            Existing = 1,
            /// <summary>Unbound host id was alive inside the baseline range - bound identity.</summary>
            Identity = 2,
            /// <summary>A fresh guest entity was created and bound (forWrite=true).</summary>
            Placeholder = 3,
        }

        private readonly Func<int, bool> _isAlive;   // guest-world EntityManager.IsAlive
        private readonly Func<int> _create;          // guest-world CreateEntity().ID

        private readonly Dictionary<int, int> _h2g = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _g2h = new Dictionary<int, int>();
        private readonly HashSet<int> _placeholders = new HashSet<int>();
        private readonly HashSet<int> _tombstonedGuests = new HashSet<int>();
        // T5.5: guestId -> the host id its binding carried at tombstone time.
        // A host component may still serialize a ref to a now-dead entity
        // (the host emits its bare id regardless of liveness); keeping the
        // dead binding's host id lets HashEntityConverter emit that same id
        // instead of an unmapped marker. Cleared when the slot is rebound.
        private readonly Dictionary<int, int> _deadHostIds = new Dictionary<int, int>();

        /// <summary>
        /// Entity ids below this bound may be identity-claimed. Set by the
        /// applier at first active drain (AllocatedEntitiesCount at that
        /// instant = the baseline's own id space); MaxValue = "not captured
        /// yet" - nothing applies before capture anyway.
        /// </summary>
        internal int BaselineAllocated = int.MaxValue;

        internal long IdentityBinds;      // identity claims this session
        internal long PlaceholderCreates; // placeholder creates this session
        internal long StaleEvicts;        // dead bindings evicted on resolve
        internal long Tombstones;         // applied deletes tombstoned

        internal EntityIdMap(Func<int, bool> isAlive, Func<int> create)
        {
            _isAlive = isAlive;
            _create = create;
        }

        /// <summary>Live bindings held (host ids currently mapped).</summary>
        internal int Count { get { return _h2g.Count; } }

        /// <summary>
        /// Resolve a host entity id to a live guest entity id.
        /// <paramref name="forWrite"/>: true for ops/refs that need the entity
        /// to exist (create/set/refs - a miss spawns a placeholder); false
        /// for ops that are no-ops when the twin is absent (delete/remove -
        /// resurrecting a dead twin would leak a ghost).
        /// </summary>
        internal Result Resolve(int hostId, bool forWrite, out int guestId)
        {
            guestId = -1;
            int gid;
            if (_h2g.TryGetValue(hostId, out gid))
            {
                if (Alive(gid)) { guestId = gid; return Result.Existing; }
                Unbind(hostId, gid);
                StaleEvicts++;
                // Fall through: the twin died - identity rules re-check below
                // (a recycled live id may legally re-claim its own slot).
            }
            if (hostId >= 0 && hostId < BaselineAllocated
                && !_g2h.ContainsKey(hostId)
                && !_placeholders.Contains(hostId)
                && !_tombstonedGuests.Contains(hostId)
                && Alive(hostId))
            {
                Bind(hostId, hostId);
                guestId = hostId;
                IdentityBinds++;
                return Result.Identity;
            }
            if (!forWrite) { return Result.Missing; }
            try { guestId = _create(); }
            catch { guestId = -1; return Result.Missing; }
            _placeholders.Add(guestId);
            Bind(hostId, guestId);
            PlaceholderCreates++;
            return Result.Placeholder;
        }

        /// <summary>Look up the current binding without creating or claiming.</summary>
        internal int Lookup(int hostId)
        {
            int gid;
            return (_h2g.TryGetValue(hostId, out gid) && Alive(gid)) ? gid : -1;
        }

        /// <summary>
        /// Reverse map for serialization (T5.5 writes host ids back). Live
        /// bindings win; a tombstoned slot falls back to the host id its
        /// binding carried when the delete applied, so a dangling ref to a
        /// dead twin still canonicalizes to the host's bare-id form.
        /// </summary>
        internal int HostId(int guestId)
        {
            int hid;
            if (_g2h.TryGetValue(guestId, out hid)) { return hid; }
            if (_deadHostIds.TryGetValue(guestId, out hid)) { return hid; }
            // Identity: a baseline-range guest id that was never rebound IS
            // host id guestId (the baseline save restored identical id
            // spaces). Lazily-bound ops leave most entities unbound - hashing
            // would otherwise report them all guest-local. A tombstoned or
            // placeholder slot, or a host id already mapped elsewhere, can
            // never claim this fallback.
            return IdentityHostId(guestId);
        }

        /// <summary>
        /// Live bindings only - the name an ALIVE guest entity may hash under.
        /// Unlike <see cref="HostId"/> this never resurrects a dead binding:
        /// an entity recycled onto a tombstoned slot is guest-local, not the
        /// old host twin.
        /// </summary>
        internal int HostIdLive(int guestId)
        {
            int hid;
            if (_g2h.TryGetValue(guestId, out hid)) { return hid; }
            return IdentityHostId(guestId);
        }

        /// <summary>
        /// Baseline-identity fallback shared by <see cref="HostId"/> and
        /// <see cref="HostIdLive"/>: returns guestId unchanged when the slot
        /// is inside the baseline allocation, unbound both ways, and neither
        /// tombstoned nor placeholder-flagged. Never records a binding - a
        /// hash is a read, not a claim (Resolve owns minting).
        /// </summary>
        private int IdentityHostId(int guestId)
        {
            if (guestId < 0 || guestId >= BaselineAllocated) { return -1; }
            if (_tombstonedGuests.Contains(guestId)
                || _placeholders.Contains(guestId)
                || _h2g.ContainsKey(guestId))
            {
                return -1;
            }
            return guestId;
        }

        /// <summary>This guest id was bound to a host twin an applied delete removed.</summary>
        internal bool IsTombstoned(int guestId)
        {
            return _tombstonedGuests.Contains(guestId);
        }

        /// <summary>This guest id was minted by us as a placeholder (not a baseline twin).</summary>
        internal bool IsPlaceholder(int guestId)
        {
            return _placeholders.Contains(guestId);
        }

        /// <summary>
        /// The guest entity for <paramref name="hostId"/>/its guest id was
        /// deleted by an applied op: drop the binding and tombstone the guest
        /// slot so a later host id equal to it can never re-claim the recycled
        /// slot's future occupant.
        /// </summary>
        internal void Tombstone(int hostId, int guestId)
        {
            _h2g.Remove(hostId);
            _g2h.Remove(guestId);
            _placeholders.Remove(guestId);
            _tombstonedGuests.Add(guestId);
            if (hostId >= 0) { _deadHostIds[guestId] = hostId; }
            Tombstones++;
        }

        private bool Alive(int guestId)
        {
            try { return guestId >= 0 && _isAlive != null && _isAlive(guestId); }
            catch { return false; }
        }

        private void Bind(int hostId, int guestId)
        {
            _h2g[hostId] = guestId;
            _g2h[guestId] = hostId;
            _deadHostIds.Remove(guestId); // recycled slot: dead name superseded
        }

        private void Unbind(int hostId, int guestId)
        {
            _h2g.Remove(hostId);
            int bound;
            if (_g2h.TryGetValue(guestId, out bound) && bound == hostId)
            {
                _g2h.Remove(guestId);
            }
        }
    }
}
