using System;
using System.Collections.Generic;
using Xunit;
using X2Coop;
using X2Coop.Net;
using X2Coop.Replication;

namespace X2Coop.Tests
{
    /// <summary>
    /// T5.2 game-free verification: the send-tick accumulator (DeltaQueue)
    /// and packet packer (DeltaPacker) - coalescing, structural ordering,
    /// dead-entity suppression, join buffering + cap/resync, size splitting,
    /// and the new wire fields (HostSendTicks, RemoveComponent, PosDelta).
    /// </summary>
    public class DeltaTests
    {
        private static readonly Guid Sid = new Guid(new byte[]
        {
            0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE,
            0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        });

        private static DeltaQueue.PendingOp Mut(int eid, int cid,
            bool remove = false, bool fromCreate = false, bool positional = false)
        {
            return new DeltaQueue.PendingOp
            {
                EntityId = eid,
                ComponentId = cid,
                CompType = typeof(object),
                Remove = remove,
                FromCreate = fromCreate,
                Positional = positional,
            };
        }

        // ------------------------------------------------------------------
        // coalescing (R9 addendum: one latest-state op per key per tick)
        // ------------------------------------------------------------------

        [Fact]
        public void Coalesce_SameKey_LastWriteWins()
        {
            var q = new DeltaQueue();
            var first = Mut(7, 3);
            var latest = Mut(7, 3);
            Assert.True(q.Put(first));
            Assert.True(q.Put(latest));
            Assert.Equal(1, q.PendingCount);
            Assert.Same(latest, q.PendingMap[DeltaQueue.Key(7, 3)]);
        }

        [Fact]
        public void Coalesce_DistinctKeys_StayDistinct()
        {
            var q = new DeltaQueue();
            q.Put(Mut(7, 3));
            q.Put(Mut(7, 4));
            q.Put(Mut(8, 3));
            Assert.Equal(3, q.PendingCount);
        }

        [Fact]
        public void Coalesce_AddThenRemove_RemoveWins()
        {
            var q = new DeltaQueue();
            q.Put(Mut(5, 1));
            q.Put(Mut(5, 1, remove: true));
            Assert.Single(q.PendingMap);
            Assert.True(q.PendingMap[DeltaQueue.Key(5, 1)].Remove);
        }

        [Fact]
        public void Coalesce_FromCreateStaysReliable_WhenOverwritten()
        {
            var q = new DeltaQueue();
            q.Put(Mut(9, 2, fromCreate: true));
            // A later non-create write for the same key (even a pos-type)
            // inherits FromCreate so the initial set never leaves the wire.
            q.Put(Mut(9, 2, positional: true));
            var rec = q.PendingMap[DeltaQueue.Key(9, 2)];
            Assert.True(rec.FromCreate);
            Assert.False(rec.Positional);
        }

        // ------------------------------------------------------------------
        // structural ordering
        // ------------------------------------------------------------------

        [Fact]
        public void Structural_CreateThenDelete_CancelsPair()
        {
            var q = new DeltaQueue();
            q.RecordCreate(11);
            q.Put(Mut(11, 1));
            q.RecordDelete(11);
            Assert.Empty(q.Structural);       // the pair never reached the wire
            Assert.Equal(0, q.PendingCount);  // its muts die with it
        }

        [Fact]
        public void Structural_DeleteThenCreate_KeepsBothInOrder()
        {
            var q = new DeltaQueue();
            q.RecordDelete(11);   // guest-known entity dies
            q.RecordCreate(11);   // id recycled into a new entity
            Assert.Equal(2, q.Structural.Count);
            Assert.Equal(DeltaQueue.StructKind.Delete, q.Structural[0].Kind);
            Assert.Equal(DeltaQueue.StructKind.Create, q.Structural[1].Kind);
        }

        [Fact]
        public void Structural_CreateDeleteCreate_NetsOneCreate()
        {
            var q = new DeltaQueue();
            q.RecordCreate(11);
            q.RecordDelete(11);
            q.RecordCreate(11);
            Assert.Single(q.Structural);
            Assert.Equal(DeltaQueue.StructKind.Create, q.Structural[0].Kind);
        }

        [Fact]
        public void Structural_DeleteDeleteCreateDelete_NetsOneDelete()
        {
            var q = new DeltaQueue();
            q.RecordDelete(5);
            q.RecordDelete(5);   // duplicate delete - dedup
            q.RecordCreate(5);   // recycled
            q.RecordDelete(5);   // and deleted again -> net = one delete
            Assert.Single(q.Structural);
            Assert.Equal(DeltaQueue.StructKind.Delete, q.Structural[0].Kind);
        }

        [Fact]
        public void Structural_CommitClears_PendingSurvives()
        {
            var q = new DeltaQueue();
            q.RecordCreate(1);
            q.RecordDelete(2);
            q.Put(Mut(9, 9));
            q.CommitStructural();
            Assert.Empty(q.Structural);
            Assert.Equal(1, q.PendingCount);
        }

        // ------------------------------------------------------------------
        // dead-entity suppression (across send windows)
        // ------------------------------------------------------------------

        [Fact]
        public void Dead_MutationForDeletedId_Dropped()
        {
            var q = new DeltaQueue();
            q.RecordDelete(11);
            Assert.False(q.Put(Mut(11, 1)));
            Assert.Equal(0, q.PendingCount);
        }

        [Fact]
        public void Dead_BanPersistsAcrossDrain_UntilCreate()
        {
            var q = new DeltaQueue();
            q.RecordDelete(11);
            q.CommitStructural();          // delete emitted; ban is session truth
            Assert.False(q.Put(Mut(11, 1)));
            q.RecordCreate(11);            // id re-issued -> ban lifted
            Assert.True(q.Put(Mut(11, 1)));
        }

        [Fact]
        public void Dead_DeleteDropsPendingMutations()
        {
            var q = new DeltaQueue();
            q.Put(Mut(11, 1));
            q.Put(Mut(11, 2));
            q.Put(Mut(12, 1));
            q.RecordDelete(11);
            Assert.Equal(1, q.PendingCount); // only the other entity's record
            Assert.Single(q.Structural);
        }

        // ------------------------------------------------------------------
        // join buffer + overflow -> resync
        // ------------------------------------------------------------------

        [Fact]
        public void JoinBuffer_AccumulatesUnderCap_DrainsInOrder()
        {
            var q = new DeltaQueue();
            Assert.True(q.OfferJoin(new byte[100], 1000));
            Assert.True(q.OfferJoin(new byte[200], 1000));
            Assert.Equal(2, q.JoinMsgCount);
            Assert.Equal(300, q.JoinBytes);
            List<byte[]> drained = q.DrainJoin();
            Assert.Equal(2, drained.Count);
            Assert.Equal(100, drained[0].Length);
            Assert.Equal(200, drained[1].Length);
            Assert.Equal(0, q.JoinMsgCount);
            Assert.Equal(0, q.JoinBytes);
        }

        [Fact]
        public void JoinBuffer_OverCap_DropsAll_AndLatchesResync()
        {
            var q = new DeltaQueue();
            Assert.True(q.OfferJoin(new byte[700], 1000));
            Assert.False(q.OfferJoin(new byte[400], 1000)); // 700+400 > 1000
            Assert.True(q.ResyncNeeded);
            Assert.Equal(0, q.JoinMsgCount);   // whole buffer dropped
            Assert.Equal(0, q.JoinBytes);
            // Latched: nothing new buffers until cleared.
            Assert.False(q.OfferJoin(new byte[10], 1000));
            q.ClearResync();
            Assert.True(q.OfferJoin(new byte[10], 1000));
        }

        [Fact]
        public void JoinBuffer_OverCap_DroppedBytes_Accumulate()
        {
            // T5.2-r1 (review M3): the dropped-byte total used to record only
            // the FIRST overflow attempt's bytes - a re-buffer + re-overflow
            // under-reported. It must accumulate the buffer contents plus
            // every refused wire across the whole latched window.
            var q = new DeltaQueue();
            Assert.True(q.OfferJoin(new byte[700], 1000));
            Assert.False(q.OfferJoin(new byte[400], 1000));  // 700+400 over cap
            Assert.True(q.ResyncNeeded);
            Assert.Equal(1100, q.ResyncDroppedBytes);         // buffer + refusing wire
            Assert.False(q.OfferJoin(new byte[50], 1000));    // post-latch refusals count too
            Assert.False(q.OfferJoin(new byte[25], 1000));
            Assert.Equal(1175, q.ResyncDroppedBytes);
            q.ClearResync();                                  // next window starts at zero
            Assert.Equal(0, q.ResyncDroppedBytes);
            // A second overflow in a fresh window restarts the accumulation.
            Assert.True(q.OfferJoin(new byte[900], 1000));
            Assert.False(q.OfferJoin(new byte[200], 1000));
            Assert.Equal(1100, q.ResyncDroppedBytes);
        }

        [Fact]
        public void JoinBuffer_DropJoin_Discards()
        {
            var q = new DeltaQueue();
            q.OfferJoin(new byte[10], 1000);
            q.OfferJoin(new byte[10], 1000);
            Assert.Equal(2, q.DropJoin());
            Assert.Equal(0, q.JoinMsgCount);
        }

        [Fact]
        public void Reset_ClearsEverything()
        {
            var q = new DeltaQueue();
            q.RecordCreate(1);
            q.RecordDelete(2);
            q.Put(Mut(3, 4));
            q.OfferJoin(new byte[50], 100);
            var q2 = q; // same instance - Reset() must clear all of it
            q2.Reset();
            Assert.Equal(0, q2.PendingCount);
            Assert.Empty(q2.Structural);
            Assert.Equal(0, q2.JoinMsgCount);
            Assert.False(q2.ResyncNeeded);
            // Dead tracking cleared too: the id may legally reappear next session.
            Assert.True(q2.Put(Mut(2, 9)));
        }

        // ------------------------------------------------------------------
        // DeltaPacker: byte-model + splitting
        // ------------------------------------------------------------------

        private static DeltaOp Op(int eid, DeltaOpKind kind, string type, int payloadBytes)
        {
            return new DeltaOp
            {
                EntityId = eid,
                Kind = kind,
                ComponentType = type,
                Payload = payloadBytes > 0 ? new byte[payloadBytes] : null,
            };
        }

        [Fact]
        public void Packer_WireBytes_MatchesActualEncoding()
        {
            var ops = new[]
            {
                Op(1, DeltaOpKind.SetComponent, "Xenonauts.Strategy.Components.GeoscapeDateTimeComponent", 24),
                Op(2, DeltaOpKind.CreateEntity, null, 0),
                Op(2, DeltaOpKind.SetComponent, "Common.Components.PositionComponent", 40),
                Op(3, DeltaOpKind.RemoveComponent, "Xenonauts.Strategy.Components.StaleComponent", 0),
                Op(4, DeltaOpKind.DeleteEntity, null, 0),
            };
            var msg = new StateDeltaMsg { SessionId = Sid, Seq = 9, HostSendTicks = 12345, Ops = ops };
            byte[] wire = MsgCodec.Encode(msg);
            Assert.NotNull(wire);
            Assert.Equal(wire.Length, DeltaPacker.MsgWireBytes(ops));
        }

        [Fact]
        public void Packer_SplitsAtBudget_EachChunkUnderCap()
        {
            // 4+1+4+30+4+2000 = 2043 bytes/op -> ~32 ops/message at 65792.
            var ops = new List<DeltaOp>();
            for (int i = 0; i < 200; i++)
            {
                ops.Add(Op(i, DeltaOpKind.SetComponent, new string('t', 30), 2000));
            }
            List<DeltaOp[]> chunks = DeltaPacker.Split(ops, Protocol.MaxMessageSize);
            Assert.True(chunks.Count > 1);
            int total = 0;
            foreach (DeltaOp[] chunk in chunks)
            {
                Assert.True(DeltaPacker.MsgWireBytes(chunk) <= Protocol.MaxMessageSize);
                // ...and the codec must actually encode it (proof the model
                // is conservative, not just optimistic).
                var msg = new StateDeltaMsg { SessionId = Sid, Seq = 1, Ops = chunk };
                byte[] wire = MsgCodec.Encode(msg);
                Assert.NotNull(wire);
                Assert.Equal(DeltaPacker.MsgWireBytes(chunk), wire.Length);
                total += chunk.Length;
            }
            Assert.Equal(200, total);
        }

        [Fact]
        public void Packer_ExactBoundary_ThenSplits()
        {
            // 20-byte ops ("T" type=1B + payload=6 -> 4+1+4+1+4+6=20 wire)
            // -> fit=floor(65757/20)=3287 < MaxDeltaOps, so the BYTE boundary
            // binds before the op cap.
            int budget = Protocol.MaxMessageSize;
            int perOp = DeltaPacker.OpWireBytes(Op(0, DeltaOpKind.SetComponent, "T", 6));
            Assert.True(perOp == 20, "perOp=" + perOp);
            int fit = (budget - DeltaPacker.FixedBytes) / perOp;
            Assert.True(fit < WireLimits.MaxDeltaOps, "fit=" + fit);
            var ops = new List<DeltaOp>();
            for (int i = 0; i < fit; i++) { ops.Add(Op(i, DeltaOpKind.SetComponent, "T", 6)); }
            List<DeltaOp[]> chunks = DeltaPacker.Split(ops, budget);
            Assert.Single(chunks);
            Assert.Equal(fit, chunks[0].Length);
            ops.Add(Op(fit, DeltaOpKind.SetComponent, "T", 6)); // one too many
            chunks = DeltaPacker.Split(ops, budget);
            Assert.Equal(2, chunks.Count);
            Assert.Equal(fit, chunks[0].Length);
            Assert.Single(chunks[1]);
        }

        [Fact]
        public void Packer_OversizedOp_GoesOutAlone()
        {
            var ops = new List<DeltaOp>
            {
                Op(1, DeltaOpKind.SetComponent, "T", 100),
                Op(2, DeltaOpKind.SetComponent, "T", WireLimits.MaxOpPayloadBytes), // ~32 KiB > half the cap but under it
                Op(3, DeltaOpKind.SetComponent, "T", 100),
            };
            List<DeltaOp[]> chunks = DeltaPacker.Split(ops, 1000); // tiny budget
            // op0 alone (1000 fixed..), op1 alone (way over), op2 alone.
            Assert.Equal(3, chunks.Count);
            Assert.Single(chunks[1]);
            Assert.True(DeltaPacker.MsgWireBytes(chunks[1]) > 1000);
        }

        [Fact]
        public void Packer_SequencedBudget_SplitsUnderMtu()
        {
            // T5.2-r1 (review I-1): PosDelta rides Sequenced - LiteNetLib does
            // NOT fragment it, so the sender splits to the peer's real
            // single-packet budget (smallest MTU tier 508 - the worst case
            // GetMaxSinglePacketSize can return is ~496 after headers). Ops
            // of ~300B must split at 1-2 per packet and every chunk must
            // encode under the budget.
            int budget = 496; // worst-case sequenced single-packet budget
            var ops = new List<DeltaOp>();
            for (int i = 0; i < 20; i++)
            {
                ops.Add(Op(i, DeltaOpKind.SetComponent, "P", 280)); // ~296B/op
            }
            List<DeltaOp[]> chunks = DeltaPacker.Split(ops, budget);
            Assert.True(chunks.Count >= 10, "chunks=" + chunks.Count); // ~1-2 ops each
            int total = 0;
            foreach (DeltaOp[] chunk in chunks)
            {
                int model = DeltaPacker.MsgWireBytes(chunk);
                Assert.True(model <= budget, "chunk bytes=" + model);
                // The codec must really encode it at that size.
                var msg = new PosDeltaMsg { SessionId = Sid, Seq = 1, Ops = chunk };
                byte[] wire = MsgCodec.Encode(msg);
                Assert.NotNull(wire);
                Assert.Equal(model, wire.Length);
                Assert.True(wire.Length <= budget);
                total += chunk.Length;
            }
            Assert.Equal(20, total);
        }

        [Fact]
        public void Packer_RespectsMaxDeltaOps()
        {
            var ops = new List<DeltaOp>();
            for (int i = 0; i < WireLimits.MaxDeltaOps + 5; i++)
            {
                ops.Add(Op(i, DeltaOpKind.DeleteEntity, null, 0));
            }
            List<DeltaOp[]> chunks = DeltaPacker.Split(ops, Protocol.MaxMessageSize);
            Assert.Equal(2, chunks.Count);
            Assert.Equal(WireLimits.MaxDeltaOps, chunks[0].Length);
            Assert.Equal(5, chunks[1].Length);
        }

        // ------------------------------------------------------------------
        // wire: HostSendTicks, RemoveComponent, PosDelta
        // ------------------------------------------------------------------

        [Fact]
        public void StateDelta_HostSendTicks_RoundTrips()
        {
            var msg = new StateDeltaMsg
            {
                SessionId = Sid, Seq = 42, HostSendTicks = 638000000000099999L,
                Ops = new[] { Op(1, DeltaOpKind.DeleteEntity, null, 0) },
            };
            byte[] wire = MsgCodec.Encode(msg);
            Assert.True(MsgCodec.TryDecode(wire, Sid, out NetMessage back));
            var m = (StateDeltaMsg)back;
            Assert.Equal(42L, m.Seq);
            Assert.Equal(638000000000099999L, m.HostSendTicks);
            Assert.Single(m.Ops);
        }

        [Fact]
        public void DeltaOpKind_RemoveComponent_PinWireValue4()
        {
            // Pin the literal kind byte so a renumber can never drift the wire.
            Assert.Equal(4, (byte)DeltaOpKind.RemoveComponent);
            var msg = new StateDeltaMsg
            {
                SessionId = Sid, Seq = 1,
                Ops = new[]
                {
                    Op(9, DeltaOpKind.RemoveComponent, "Xenonauts.Strategy.Components.Stale", 0),
                },
            };
            byte[] wire = MsgCodec.Encode(msg);
            Assert.True(MsgCodec.TryDecode(wire, Sid, out NetMessage back));
            var m = (StateDeltaMsg)back;
            Assert.Equal(DeltaOpKind.RemoveComponent, m.Ops[0].Kind);
            // And the body layout: [seq:8][ticks:8][n:2][eid:4][kind:1] ->
            int kindOff = MsgCodec.EnvelopeSize + 8 + 8 + 2 + 4;
            Assert.Equal(4, wire[kindOff]);
        }

        [Fact]
        public void PosDelta_RoundTrips_OwnSeqSpace()
        {
            var msg = new PosDeltaMsg
            {
                SessionId = Sid, Seq = 7, HostSendTicks = 999,
                Ops = new[] { Op(4, DeltaOpKind.SetComponent, "Common.Components.PositionComponent", 40) },
            };
            byte[] wire = MsgCodec.Encode(msg);
            Assert.NotNull(wire);
            Assert.Equal((byte)MsgType.PosDelta, wire[0]);
            Assert.True(MsgCodec.TryDecode(wire, Sid, out NetMessage back));
            var m = (PosDeltaMsg)back;
            Assert.Equal(7L, m.Seq);
            Assert.Equal(999L, m.HostSendTicks);
            Assert.Single(m.Ops);
        }

        [Fact]
        public void LongGenericTypeName_FitsMaxComponentTypeBytes()
        {
            // MutationDelayedItemComponent`2<...> FullNames are the long tail.
            string longName = typeof(KeyValuePair<string, List<int>>).FullName;
            Assert.True(longName.Length <= WireLimits.MaxComponentTypeBytes);
            var msg = new StateDeltaMsg
            {
                SessionId = Sid, Seq = 1,
                Ops = new[] { Op(1, DeltaOpKind.SetComponent, longName, 8) },
            };
            byte[] wire = MsgCodec.Encode(msg);
            Assert.NotNull(wire);
            Assert.True(MsgCodec.TryDecode(wire, Sid, out NetMessage back));
            Assert.Equal(longName, ((StateDeltaMsg)back).Ops[0].ComponentType);
        }
    }
}

