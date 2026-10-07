using System;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.4 in-flight op identity (r2 C3). While <see cref="StateApplier.InScope"/>
    /// is true, every ApplyComponentMutation that lands on the mirror world is
    /// either the op's OWN terminal write or something else. This tracker is how
    /// <see cref="Mirror.MirrorFirewall"/> tells them apart:
    /// <see cref="StateApplier"/> calls <see cref="Begin"/> around each op's
    /// apply body and <see cref="Arm"/> around the one world call that IS the
    /// op (entity.Add / entity.Remove / World.DeleteEntityImmediately); the
    /// firewall classifies every in-scope mutation via
    /// <see cref="ClassifyMutation"/>.
    ///
    /// Classification contract (the T5.4 definition - see MODLOG):
    ///  - Direct: the op's own mutation. Set/Remove arm a (guestEntity,
    ///    componentId) key that matches EXACTLY ONE call - consumed on first
    ///    hit, so a listener re-mutating the same key is a cascade, not the
    ///    op. Delete arms an entity-only key (component wildcard) that matches
    ///    repeatably: DeleteEntityImmediately may fan out into per-component
    ///    removals on the dying entity, all of which are the op's direct
    ///    effect.
    ///  - Internal: mutations while an op is in flight but NOT armed - the
    ///    applier bookkeeping phase (entity resolve, payload deserialize,
    ///    placeholder CreateEntity via EntityIdMap/MappingEntityConverter and
    ///    any factory-side adds it triggers). These are our own machinery,
    ///    counted separately so they never pollute the cascade numbers; the
    ///    firewall lets them through under both modes (blocking a factory
    ///    add could leave a placeholder entity malformed).
    ///  - Cascade: everything else - game listeners reacting to the mutation
    ///    we just applied (incl. the delayed-mutation-stack drain inside the
    ///    same call), mutations on other entities, and anything that lands
    ///    between ops (Kind.None). Under x2coop.cascadeFirewall=1 serializable
    ///    non-whitelisted cascades are dropped by the firewall: the host runs
    ///    the same listeners and streams their results as ops anyway.
    ///
    /// Game-free on purpose (ints + enums only) so the xunit suite drives the
    /// whole truth table. Main-thread only like everything else here - plain
    /// statics, no locking.
    /// </summary>
    internal static class InFlightOp
    {
        /// <summary>Which delta op is mid-apply.</summary>
        internal enum Kind : byte
        {
            /// <summary>No op in flight (inside a scope but between ops, or no scope at all).</summary>
            None = 0,
            /// <summary>CreateEntity op - never arms: its effect is the create itself, seen via the CreatePostfix.</summary>
            Create = 1,
            /// <summary>DeleteEntity op - arms an entity-wildcard key around DeleteEntityImmediately.</summary>
            Delete = 2,
            /// <summary>SetComponent op - arms (entity,component) around entity.Add.</summary>
            Set = 3,
            /// <summary>RemoveComponent op - arms (entity,component) around entity.Remove.</summary>
            Remove = 4,
        }

        /// <summary>How an in-scope mirror-world mutation relates to the in-flight op.</summary>
        internal enum Class : byte
        {
            /// <summary>The op's own terminal mutation - pass, never count as cascade.</summary>
            Direct = 0,
            /// <summary>Applier bookkeeping phase (unarmed) - pass, counted as internal.</summary>
            Internal = 1,
            /// <summary>Listener cascade / unrelated write - counted; gated when cascadeFirewall=1.</summary>
            Cascade = 2,
        }

        /// <summary>Op kind currently in flight (None between ops / outside ops).</summary>
        internal static Kind Current { get; private set; }

        /// <summary>An op's terminal mutation key is armed (the world call is on the stack).</summary>
        internal static bool Armed { get; private set; }

        /// <summary>Armed key: guest entity id of the op's terminal mutation (-1 = none).</summary>
        internal static int EntityId { get; private set; }

        /// <summary>Armed key: component id, or -1 = wildcard (Delete matches any component on the entity).</summary>
        internal static int ComponentId { get; private set; }

        /// <summary>Set/Remove keys are consume-once - true after the direct mutation matched.</summary>
        private static bool _used;

        /// <summary>Open an op's apply region. Always pair with `using`.</summary>
        internal static IDisposable Begin(Kind kind)
        {
            return new BeginToken(kind);
        }

        /// <summary>Arm the direct-mutation key around the terminal world call.</summary>
        internal static IDisposable Arm(int entityId, int componentId)
        {
            return new ArmToken(entityId, componentId);
        }

        /// <summary>
        /// Classify an ApplyComponentMutation(entityID, componentID) observed
        /// while <see cref="StateApplier.InScope"/>. Callers guarantee the scope
        /// is open; Kind.None still classifies (as Cascade - a mutation between
        /// ops is nobody's direct write).
        /// </summary>
        internal static Class ClassifyMutation(int entityID, int componentID)
        {
            switch (Current)
            {
                case Kind.Set:
                case Kind.Remove:
                    if (!Armed) { return Class.Internal; }
                    if (!_used && entityID == EntityId && componentID == ComponentId)
                    {
                        _used = true;   // the ONE direct call consumed the key
                        return Class.Direct;
                    }
                    return Class.Cascade;
                case Kind.Delete:
                    if (!Armed) { return Class.Internal; }
                    // Component-wildcard entity match, repeatable: the delete
                    // may fan out into per-component removals on the entity.
                    return entityID == EntityId ? Class.Direct : Class.Cascade;
                case Kind.Create:
                    // CreateEntity's terminal call is the create itself; any
                    // mutation here is factory-internal bookkeeping.
                    return Class.Internal;
                default:
                    return Class.Cascade;
            }
        }

        /// <summary>
        /// Is this DeleteEntityImmediately/UnsafeDeleteEntity call the in-flight
        /// delete op's own call? (Matches entity only - internal re-entry on the
        /// same entity, e.g. DeleteEntityImmediately -> UnsafeDeleteEntity,
        /// stays direct.)
        /// </summary>
        internal static bool IsDirectDelete(int entityID)
        {
            return Current == Kind.Delete && Armed && entityID == EntityId;
        }

        /// <summary>
        /// Classify a World.CreateEntity observed in scope. Unarmed phases are
        /// applier bookkeeping (EntityIdMap placeholder / converter resolve);
        /// a create while a terminal mutation is armed is a listener spawning
        /// an entity mid-apply - a cascade.
        /// </summary>
        internal static Class ClassifyCreate()
        {
            return Armed ? Class.Cascade : Class.Internal;
        }

        /// <summary>Session reset / world rebind: drop all in-flight state.</summary>
        internal static void Reset()
        {
            Current = Kind.None;
            Armed = false;
            EntityId = -1;
            ComponentId = -1;
            _used = false;
        }

        private sealed class BeginToken : IDisposable
        {
            private readonly Kind _prevKind;
            private readonly bool _prevArmed;
            private readonly int _prevEntity;
            private readonly int _prevComponent;
            private readonly bool _prevUsed;
            private bool _disposed;

            internal BeginToken(Kind kind)
            {
                // Hermetic: save + clear the whole tuple, so a leaked Arm from
                // an earlier op can never bleed a stale key into this one.
                _prevKind = Current;
                _prevArmed = Armed;
                _prevEntity = EntityId;
                _prevComponent = ComponentId;
                _prevUsed = _used;
                Current = kind;
                Armed = false;
                EntityId = -1;
                ComponentId = -1;
                _used = false;
            }

            public void Dispose()
            {
                if (_disposed) { return; }
                _disposed = true;
                Current = _prevKind;
                Armed = _prevArmed;
                EntityId = _prevEntity;
                ComponentId = _prevComponent;
                _used = _prevUsed;
            }
        }

        private sealed class ArmToken : IDisposable
        {
            private readonly bool _prevArmed;
            private readonly int _prevEntity;
            private readonly int _prevComponent;
            private readonly bool _prevUsed;
            private bool _disposed;

            internal ArmToken(int entityId, int componentId)
            {
                _prevArmed = Armed;
                _prevEntity = EntityId;
                _prevComponent = ComponentId;
                _prevUsed = _used;
                Armed = true;
                EntityId = entityId;
                ComponentId = componentId;
                _used = false;
            }

            public void Dispose()
            {
                if (_disposed) { return; }
                _disposed = true;
                Armed = _prevArmed;
                EntityId = _prevEntity;
                ComponentId = _prevComponent;
                _used = _prevUsed;
            }
        }
    }
}
