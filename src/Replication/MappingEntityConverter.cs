using System;
using Artitas;
using FullSerializer;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.3 (R5): fsConverter registered on the guest's OWN delta
    /// deserializer in place of the game's EntityConverter. Host deltas
    /// carry entity references as HOST ids; the game's converter would
    /// ForceCreateEntity(hostId) - resurrecting by host id is exactly the
    /// identity-preserving resurrection that breaks the guest's own id
    /// space, so <see cref="StateApplier"/> builds its serializer with
    /// <c>CommonSerializationSystem.SetupSerializer(new fsSerializer(),
    /// world: null)</c> (EntityConverter skipped) and registers this one
    /// instead: every serialized Entity resolves through the session's
    /// <see cref="EntityIdMap"/> - baseline ids claim their identity twin,
    /// post-baseline/unknown ids land on fresh placeholders.
    ///
    /// Same shape as Artitas.Serialization.Converters.EntityConverter
    /// (Entity.cs decompile): a serialized entity is a bare id (or the
    /// wrapped {"$content": id} form); CreateInstance returns the mapped
    /// live Entity and TryDeserialize is a no-op - the game never walks
    /// entity members through fs.
    ///
    /// A resolved entity REF that maps to nothing (a host id the guest
    /// truly lacks, e.g. a link into a not-yet-created entity) resolves
    /// forWrite=true like op targets do - a placeholder keeps the
    /// reference non-null, matching the game's ForceCreateEntity semantics
    /// for dangling refs; the owning entity's real create op fills it in.
    /// </summary>
    internal sealed class MappingEntityConverter : fsConverter, IDisposable
    {
        private EntityIdMap _map;
        private World _world;

        internal MappingEntityConverter(EntityIdMap map, World world)
        {
            _map = map;
            _world = world;
        }

        public override object CreateInstance(fsData data, Type storageType)
        {
            int hostId;
            if (data.IsDictionary)
            {
                hostId = data.AsDictionary["$content"].AsAnyInt;
            }
            else if (data.IsInt64 || data.IsDouble || data.IsFloat)
            {
                hostId = data.AsAnyInt;
            }
            else
            {
                throw new Exception(
                    "MappingEntityConverter: serialized entity ref carries no id");
            }
            int guestId = -1;
            EntityIdMap.Result r = _map != null
                ? _map.Resolve(hostId, forWrite: true, out guestId)
                : EntityIdMap.Result.Missing;
            if (r == EntityIdMap.Result.Missing || guestId < 0)
            {
                // The placeholder factory itself failed - leave the ref null
                // rather than throwing inside a component deserialize (the
                // owning op still applies its own payload).
                return null;
            }
            return _world.EntityManager.GetEntity(guestId);
        }

        /// <summary>
        /// Host-id write-back for the canonicalizing serializer T5.5's hash
        /// check needs: a guest Entity serializes as its mapped HOST id
        /// (identity bindings write themselves). Unmapped guest-local ids
        /// fall through as their own id - same escape the game converter
        /// takes for entities it never bound.
        /// </summary>
        public override fsResult TrySerialize(object instance, out fsData serialized,
            Type instanceType, Type storageType)
        {
            Entity e = (Entity)instance;
            int id = e != null ? e.ID : -1;
            int hostId = _map != null ? _map.HostId(id) : -1;
            serialized = new fsData(hostId >= 0 ? hostId : id);
            return fsResult.Success;
        }

        public override fsResult TryDeserialize(fsData data, ref object instance,
            Type storageType)
        {
            return fsResult.Success;
        }

        public override bool CanProcess(Type type)
        {
            return typeof(Entity).IsAssignableFrom(type);
        }

        public void Dispose()
        {
            _map = null;
            _world = null;
        }
    }
}
