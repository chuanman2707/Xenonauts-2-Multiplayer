using System;
using System.Collections.Generic;
using Xunit;
using X2Coop.Net;
using X2Coop.Replication;

namespace X2Coop.Tests
{
    /// <summary>
    /// T5.3 game-free verification: EntityIdMap (identity claim / placeholder
    /// / tombstone / baseline bound / guest-local protection), PosCoalescer
    /// (latest seq per key, stale drops) and ComponentTypeResolver (world map
    /// first, Type.GetType, assembly scan incl. generic FullNames).
    /// The world itself is simulated with delegate closures - no Artitas.
    /// </summary>
    public class ApplierTests
    {
        // ------------------------------------------------------------------
        // EntityIdMap
        // ------------------------------------------------------------------

        private sealed class FakeWorld
        {
            public readonly HashSet<int> Alive = new HashSet<int>();
            public int NextId = 0;
            public readonly Queue<int> Recycled = new Queue<int>();

            public int Create()
            {
                int id = Recycled.Count > 0 ? Recycled.Dequeue() : NextId++;
                Alive.Add(id);
                return id;
            }
        }

        private static EntityIdMap Map(FakeWorld w, int baseline)
        {
            var m = new EntityIdMap(w.Alive.Contains, w.Create);
            m.BaselineAllocated = baseline;
            return m;
        }

        [Fact]
        public void Map_BaselineId_ClaimsIdentity()
        {
            var w = new FakeWorld();
            w.NextId = 1000;
            w.Alive.Add(42);              // baseline entity
            var m = Map(w, baseline: 1000);
            int gid;
            EntityIdMap.Result r = m.Resolve(42, forWrite: true, out gid);
            Assert.Equal(EntityIdMap.Result.Identity, r);
            Assert.Equal(42, gid);
            Assert.Equal(42, m.HostId(42));
            Assert.Equal(1, m.IdentityBinds);
        }

        [Fact]
        public void Map_UnknownId_Placeholders_AndSticks()
        {
            var w = new FakeWorld();
            w.NextId = 500;
            var m = Map(w, baseline: 400);
            int gid;
            EntityIdMap.Result r = m.Resolve(7, forWrite: true, out gid);
            Assert.Equal(EntityIdMap.Result.Placeholder, r);
            Assert.Equal(500, gid);        // fresh guest id, NOT the host id
            int gid2;
            r = m.Resolve(7, forWrite: true, out gid2);
            Assert.Equal(EntityIdMap.Result.Existing, r);
            Assert.Equal(gid, gid2);
            Assert.Equal(7, m.HostId(gid));
        }

        [Fact]
        public void Map_Readonly_NeverCreates()
        {
            var w = new FakeWorld();
            w.NextId = 100;
            var m = Map(w, baseline: 50);
            int gid;
            EntityIdMap.Result r = m.Resolve(9, forWrite: false, out gid);
            Assert.Equal(EntityIdMap.Result.Missing, r);
            Assert.Equal(0, m.PlaceholderCreates);
            Assert.Equal(100, w.NextId);   // the factory was never invoked
        }

        [Fact]
        public void Map_GuestLocal_AboveBaseline_NeverClaimed()
        {
            var w = new FakeWorld();
            w.NextId = 1000;
            w.Alive.Add(1200);             // a guest-local entity past baseline
            var m = Map(w, baseline: 1000);
            int gid;
            EntityIdMap.Result r = m.Resolve(1200, forWrite: true, out gid);
            // host id 1200 maps to a placeholder, NOT the live guest-local 1200
            Assert.Equal(EntityIdMap.Result.Placeholder, r);
            Assert.Equal(1000, gid);
            Assert.NotEqual(1200, gid);
        }

        [Fact]
        public void Map_StaleBinding_EvictsAndRebinds()
        {
            var w = new FakeWorld();
            w.NextId = 10;
            var m = Map(w, baseline: 5);
            int gid;
            m.Resolve(3, forWrite: true, out gid);   // placeholder guest=10
            w.Alive.Remove(10);                    // twin died
            int gid2;
            EntityIdMap.Result r = m.Resolve(3, forWrite: true, out gid2);
            Assert.Equal(EntityIdMap.Result.Placeholder, r);
            Assert.NotEqual(gid, gid2);
            Assert.Equal(1, m.StaleEvicts);
        }

        [Fact]
        public void Map_Tombstone_BlocksIdentityReclaim()
        {
            var w = new FakeWorld();
            w.NextId = 100;
            w.Alive.Add(9);                // baseline twin
            var m = Map(w, baseline: 100);
            int gid;
            m.Resolve(9, forWrite: false, out gid);  // identity claim guest=9
            Assert.Equal(9, gid);
            w.Alive.Remove(9);
            m.Tombstone(9, 9);             // delete op applied
            // host recycles id 9 into a NEW entity; a guest-local entity also
            // respawns at slot 9 -> tombstone must block the reclaim.
            w.Alive.Add(9);
            int gid2;
            EntityIdMap.Result r = m.Resolve(9, forWrite: true, out gid2);
            Assert.Equal(EntityIdMap.Result.Placeholder, r);
            Assert.NotEqual(9, gid2);
        }

        [Fact]
        public void Map_DeleteOfUnknown_IsMissing()
        {
            var w = new FakeWorld();
            var m = Map(w, baseline: 10);
            int gid;
            EntityIdMap.Result r = m.Resolve(88, forWrite: false, out gid);
            Assert.Equal(EntityIdMap.Result.Missing, r);
        }

        [Fact]
        public void Map_TwoHostIds_NeverShareGuestId()
        {
            var w = new FakeWorld();
            w.NextId = 200;
            var m = Map(w, baseline: 100);
            int g1, g2;
            m.Resolve(1, forWrite: true, out g1);
            m.Resolve(2, forWrite: true, out g2);
            Assert.NotEqual(g1, g2);
            Assert.Equal(1, m.HostId(g1));
            Assert.Equal(2, m.HostId(g2));
            Assert.Equal(-1, m.HostId(9999));
        }

        [Fact]
        public void Map_Lookup_NonCreating()
        {
            var w = new FakeWorld();
            w.NextId = 50;
            var m = Map(w, baseline: 20);
            Assert.Equal(-1, m.Lookup(3));   // never creates, never claims
            Assert.Equal(0, m.PlaceholderCreates);
            int gid;
            m.Resolve(3, forWrite: true, out gid);
            Assert.Equal(gid, m.Lookup(3));
        }

        // ------------------------------------------------------------------
        // PosCoalescer
        // ------------------------------------------------------------------

        private static PosDeltaMsg Pos(long seq, params DeltaOp[] ops)
        {
            return new PosDeltaMsg { Seq = seq, HostSendTicks = seq * 100, Ops = ops };
        }

        private static DeltaOp Set(int eid, string type, int payload)
        {
            return new DeltaOp
            {
                EntityId = eid,
                Kind = DeltaOpKind.SetComponent,
                ComponentType = type,
                Payload = new byte[payload],
            };
        }

        [Fact]
        public void Pos_NewestSeqWins_PerKey()
        {
            var c = new PosCoalescer();
            c.Offer(Pos(5, Set(7, "P", 1)));
            c.Offer(Pos(9, Set(7, "P", 2)));   // newer op for the same key
            List<DeltaOp> d = c.Drain();
            Assert.Single(d);
            Assert.Equal(2, d[0].Payload.Length);
            Assert.Equal(9, c.LastMsgSeq);
        }

        [Fact]
        public void Pos_OlderPacket_Drops()
        {
            var c = new PosCoalescer();
            c.Offer(Pos(9, Set(7, "P", 2)));
            c.Offer(Pos(5, Set(7, "P", 1)));   // stale packet arrives late
            List<DeltaOp> d = c.Drain();
            Assert.Single(d);
            Assert.Equal(2, d[0].Payload.Length);
            Assert.Equal(1, c.StaleOps);
        }

        [Fact]
        public void Pos_DistinctKeys_AllDrain()
        {
            var c = new PosCoalescer();
            c.Offer(Pos(1, Set(7, "P", 1), Set(7, "V", 1), Set(8, "P", 1)));
            List<DeltaOp> d = c.Drain();
            Assert.Equal(3, d.Count);
            Assert.Equal(0, c.PendingCount);
        }

        [Fact]
        public void Pos_Clear_Resets()
        {
            var c = new PosCoalescer();
            c.Offer(Pos(4, Set(1, "P", 1)));
            c.Clear();
            Assert.Equal(0, c.PendingCount);
            Assert.Equal(0, c.LastMsgSeq);
            Assert.Empty(c.Drain());
        }

        // ------------------------------------------------------------------
        // ComponentTypeResolver
        // ------------------------------------------------------------------

        [Fact]
        public void Types_WorldMap_HitsFirst()
        {
            var r = new ComponentTypeResolver();
            var worldTypes = new List<Type> { typeof(string), typeof(int) };
            r.MergeWorldTypes(worldTypes);
            Assert.Same(typeof(string), r.Resolve("System.String", worldTypes));
        }

        [Fact]
        public void Types_PlainName_ViaGetType()
        {
            var r = new ComponentTypeResolver();
            Assert.Same(typeof(int), r.Resolve("System.Int32", null));
            Assert.Same(typeof(List<int>), r.Resolve(typeof(List<int>).FullName, null));
        }

        [Fact]
        public void Types_GenericFullName_Resolves()
        {
            var r = new ComponentTypeResolver();
            Type t = typeof(KeyValuePair<string, List<int>>);
            Assert.Same(t, r.Resolve(t.FullName, null));
        }

        [Fact]
        public void Types_GenericNoAssembly_ResolvesViaScan()
        {
            // A generic name with NO assembly qualifiers defeats naive
            // Type.GetType on some runtimes; the scan must still bind it.
            var r = new ComponentTypeResolver();
            string name = "System.Collections.Generic.Dictionary`2[[System.String],[System.Int32]]";
            Type t = r.Resolve(name, null);
            Assert.Same(typeof(Dictionary<string, int>), t);
        }

        [Fact]
        public void Types_NestedGeneric_Resolves()
        {
            var r = new ComponentTypeResolver();
            Type t = typeof(List<KeyValuePair<int, string>>);
            Assert.Same(t, r.Resolve(t.FullName, null));
        }

        [Fact]
        public void Types_Unknown_NegativeCached()
        {
            var r = new ComponentTypeResolver();
            Assert.Null(r.Resolve("No.Such.Type`99[[Nope]]", null));
            Assert.Null(r.Resolve("No.Such.Type`99[[Nope]]", null));
            Assert.Equal(1, r.UnresolvableCount);
        }

        // ------------------------------------------------------------------
        // InFlightOp (T5.4 cascade classifier: Direct / Internal / Cascade)
        // ------------------------------------------------------------------

        [Fact]
        public void InFlight_Set_MatchesOnce_ThenCascade()
        {
            InFlightOp.Reset();
            using (InFlightOp.Begin(InFlightOp.Kind.Set))
            {
                // unarmed phase: resolve/deserialize bookkeeping
                Assert.Equal(InFlightOp.Class.Internal,
                    InFlightOp.ClassifyMutation(10, 5));
                using (InFlightOp.Arm(10, 5))
                {
                    Assert.Equal(InFlightOp.Class.Direct,
                        InFlightOp.ClassifyMutation(10, 5));
                    // same key again -> a listener re-mutation = cascade
                    Assert.Equal(InFlightOp.Class.Cascade,
                        InFlightOp.ClassifyMutation(10, 5));
                    // different component / entity -> cascade
                    Assert.Equal(InFlightOp.Class.Cascade,
                        InFlightOp.ClassifyMutation(10, 6));
                    Assert.Equal(InFlightOp.Class.Cascade,
                        InFlightOp.ClassifyMutation(11, 5));
                }
                // arm unwound -> back to bookkeeping
                Assert.Equal(InFlightOp.Class.Internal,
                    InFlightOp.ClassifyMutation(10, 5));
            }
            // op ended -> between-ops strays are cascades
            Assert.Equal(InFlightOp.Class.Cascade,
                InFlightOp.ClassifyMutation(10, 5));
        }

        [Fact]
        public void InFlight_Remove_BehavesLikeSet()
        {
            InFlightOp.Reset();
            using (InFlightOp.Begin(InFlightOp.Kind.Remove))
            using (InFlightOp.Arm(7, 9))
            {
                Assert.Equal(InFlightOp.Class.Direct,
                    InFlightOp.ClassifyMutation(7, 9));
                Assert.Equal(InFlightOp.Class.Cascade,
                    InFlightOp.ClassifyMutation(7, 9));
            }
        }

        [Fact]
        public void InFlight_Delete_EntityWildcard_Repeatable()
        {
            InFlightOp.Reset();
            using (InFlightOp.Begin(InFlightOp.Kind.Delete))
            {
                Assert.False(InFlightOp.IsDirectDelete(10)); // not armed yet
                using (InFlightOp.Arm(10, -1))
                {
                    // per-component removals on the dying entity are all direct
                    Assert.Equal(InFlightOp.Class.Direct,
                        InFlightOp.ClassifyMutation(10, 1));
                    Assert.Equal(InFlightOp.Class.Direct,
                        InFlightOp.ClassifyMutation(10, 2));
                    Assert.True(InFlightOp.IsDirectDelete(10));
                    Assert.True(InFlightOp.IsDirectDelete(10)); // repeatable
                    // another entity = listener cascade
                    Assert.Equal(InFlightOp.Class.Cascade,
                        InFlightOp.ClassifyMutation(11, 1));
                    Assert.False(InFlightOp.IsDirectDelete(11));
                }
                Assert.False(InFlightOp.IsDirectDelete(10));
            }
        }

        [Fact]
        public void InFlight_Create_AllInternal()
        {
            InFlightOp.Reset();
            using (InFlightOp.Begin(InFlightOp.Kind.Create))
            {
                Assert.Equal(InFlightOp.Class.Internal,
                    InFlightOp.ClassifyMutation(3, 1));
                Assert.Equal(InFlightOp.Class.Internal,
                    InFlightOp.ClassifyCreate());
            }
        }

        [Fact]
        public void InFlight_CreateDuringArm_IsCascade()
        {
            InFlightOp.Reset();
            using (InFlightOp.Begin(InFlightOp.Kind.Set))
            {
                Assert.Equal(InFlightOp.Class.Internal,
                    InFlightOp.ClassifyCreate());     // placeholder resolve
                using (InFlightOp.Arm(10, 5))
                {
                    Assert.Equal(InFlightOp.Class.Cascade,
                        InFlightOp.ClassifyCreate()); // listener spawned entity
                }
            }
        }

        [Fact]
        public void InFlight_Begin_RestoresOuterState()
        {
            InFlightOp.Reset();
            using (InFlightOp.Begin(InFlightOp.Kind.Delete))
            using (InFlightOp.Arm(9, -1))
            {
                using (InFlightOp.Begin(InFlightOp.Kind.Set))
                using (InFlightOp.Arm(1, 2))
                {
                    Assert.Equal(InFlightOp.Class.Direct,
                        InFlightOp.ClassifyMutation(1, 2));
                    Assert.False(InFlightOp.IsDirectDelete(9)); // inner scope owns classification
                }
                // outer delete state fully restored
                Assert.Equal(InFlightOp.Kind.Delete, InFlightOp.Current);
                Assert.True(InFlightOp.Armed);
                Assert.True(InFlightOp.IsDirectDelete(9));
                Assert.Equal(InFlightOp.Class.Direct,
                    InFlightOp.ClassifyMutation(9, 4));
            }
            Assert.Equal(InFlightOp.Kind.None, InFlightOp.Current);
            Assert.False(InFlightOp.Armed);
        }

        [Fact]
        public void InFlight_None_Kind_IsCascade()
        {
            InFlightOp.Reset();
            Assert.Equal(InFlightOp.Kind.None, InFlightOp.Current);
            Assert.Equal(InFlightOp.Class.Cascade,
                InFlightOp.ClassifyMutation(4, 4));
            Assert.False(InFlightOp.IsDirectDelete(4));
            Assert.Equal(InFlightOp.Class.Internal,
                InFlightOp.ClassifyCreate()); // unarmed
        }

        [Fact]
        public void InFlight_Reset_ClearsEverything()
        {
            InFlightOp.Reset();
            using (InFlightOp.Begin(InFlightOp.Kind.Set))
            using (InFlightOp.Arm(1, 2))
            {
                InFlightOp.Reset();
                Assert.Equal(InFlightOp.Class.Cascade,
                    InFlightOp.ClassifyMutation(1, 2));
            }
            Assert.Equal(InFlightOp.Kind.None, InFlightOp.Current);
        }
    }
}
