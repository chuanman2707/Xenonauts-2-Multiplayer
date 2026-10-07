using System;
using System.Collections.Generic;
using Artitas;
using FullSerializer;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.5 hash-time Entity converter (GUEST side only). The wire speaks host
    /// entity ids; while hashing, every serialized Entity reference must come
    /// back out as the HOST id so host and guest canonical bytes compare
    /// equal. Resolution is a pure <see cref="EntityIdMap.HostId"/> lookup -
    /// hashing MUST NOT mint placeholders or bind new pairs (unlike
    /// MappingEntityConverter's CreateInstance, which resolves forWrite=true
    /// during op deserialization).
    ///
    /// An Entity the map cannot name is emitted as a deterministic marker
    /// object {"$x2u": &lt;guestId&gt;} - never the bare guest id. Two refs to
    /// the same unmapped entity produce identical markers (stable within one
    /// hash), and the marker can never collide with a host bare-int form, so
    /// a guest-only reference surfaces as a real divergence instead of a
    /// silent id-space mixup. <see cref="Unmapped"/> counts them.
    /// </summary>
    internal sealed class HashEntityConverter : fsConverter
    {
        private readonly EntityIdMap _map;

        /// <summary>Refs hashed with no host binding this Compute call.</summary>
        internal int Unmapped;

        internal HashEntityConverter(EntityIdMap map)
        {
            _map = map;
        }

        public override fsResult TrySerialize(object instance, out fsData serialized,
            Type instanceType, Type storageType)
        {
            Entity e = (Entity)instance;
            int gid = e != null ? e.ID : -1;
            int hid = gid >= 0 && _map != null ? _map.HostId(gid) : -1;
            if (hid >= 0)
            {
                serialized = new fsData((long)hid);
            }
            else if (gid >= 0)
            {
                Unmapped++;
                var d = new Dictionary<string, fsData>(1);
                d["$x2u"] = new fsData((long)gid);
                serialized = new fsData(d);
            }
            else
            {
                serialized = new fsData(-1L);
            }
            return fsResult.Success;
        }

        public override fsResult TryDeserialize(fsData data, ref object instance,
            Type storageType)
        {
            // Hashing never deserializes.
            return fsResult.Success;
        }

        public override bool CanProcess(Type type)
        {
            return typeof(Entity).IsAssignableFrom(type);
        }
    }
}
