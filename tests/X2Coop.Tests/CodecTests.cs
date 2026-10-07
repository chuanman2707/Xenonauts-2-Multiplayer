using System;
using System.Collections.Generic;
using Xunit;
using X2Coop;
using X2Coop.Net;

namespace X2Coop.Tests
{
    /// <summary>
    /// T2.2 codec verification: every MsgType round-trips byte-identically,
    /// the envelope sessionId gate drops foreign traffic, and malformed input
    /// is rejected with a reason (never an exception). CodecSelfTest.Run() is
    /// the same code the in-game "B" hotkey executes.
    /// </summary>
    public class CodecTests
    {
        private static readonly Guid Sid = new Guid(new byte[]
        {
            0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE,
            0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        });

        private static byte[] Round(NetMessage m, Guid expected)
        {
            byte[] wire = MsgCodec.Encode(m);
            Assert.NotNull(wire);
            Assert.True(wire.Length >= MsgCodec.EnvelopeSize);
            Assert.True(wire.Length <= Protocol.MaxMessageSize);
            Assert.Equal((byte)m.Type, wire[0]);
            Assert.True(MsgCodec.TryDecode(wire, 0, wire.Length, expected,
                out NetMessage back, out MsgCodec.DropReason drop));
            Assert.Equal(m.Type, back.Type);
            Assert.Equal(m.SessionId, back.SessionId);
            // Re-encode must reproduce the identical bytes (deterministic wire).
            byte[] wire2 = MsgCodec.Encode(back);
            Assert.Equal(wire, wire2);
            return wire;
        }

        [Fact]
        public void AllRegisteredTypes_RoundTrip_ByteIdentical()
        {
            Log.Clear();
            NetMessage[] samples = CodecSelfTest.Samples(Sid);
            Assert.Equal(MsgCodec.RegisteredCount, samples.Length);
            foreach (NetMessage m in samples)
            {
                Guid expected = m.Type == MsgType.Hello ? Guid.Empty : Sid;
                Round(m, expected);
            }
            // No warnings during a clean pass - anything logged here is a smell.
            Assert.Empty(Log.Lines);
        }

        [Fact]
        public void SelfTest_LogsCodecOk()
        {
            Log.Clear();
            List<string> failures = CodecSelfTest.Run();
            Assert.Empty(failures);
            Assert.Contains(Log.Lines, l => l.Contains("codec ok 13/13"));
            Assert.Contains(Log.Lines, l => l.Contains("codec drops ok 4/4"));
        }

        [Fact]
        public void Hello_FieldsRoundTrip()
        {
            Log.Clear();
            var hello = new HelloMsg
            {
                ProtocolVersion = 1, ModVersion = "0.1.0", GameVersion = "4.28.0-GE",
                CommitHashes = new[] { "aa", "bb", "cc", "dd" },
                PlayerName = "Name Wi"+'é'+ "th-utf8",
                Packs = new[] { new PackStamp { Uid = "u", Version = "v" } },
                CampaignId = Sid,
            };
            byte[] wire = MsgCodec.Encode(hello);
            Assert.NotNull(wire);
            Assert.True(MsgCodec.TryDecode(wire, Guid.Empty, out NetMessage back));
            var h = (HelloMsg)back;
            Assert.Equal(Guid.Empty, h.SessionId);
            Assert.Equal(1, h.ProtocolVersion);
            Assert.Equal("0.1.0", h.ModVersion);
            Assert.Equal("4.28.0-GE", h.GameVersion);
            Assert.Equal(new[] { "aa", "bb", "cc", "dd" }, h.CommitHashes);
            Assert.Equal(hello.PlayerName, h.PlayerName);
            Assert.Single(h.Packs);
            Assert.Equal("u", h.Packs[0].Uid);
            Assert.Equal(Sid, h.CampaignId);
        }

        [Fact]
        public void Hello_NullCampaignId_RoundTripsAsAbsent()
        {
            Log.Clear();
            var hello = new HelloMsg { ProtocolVersion = 1, CampaignId = null };
            byte[] wire = MsgCodec.Encode(hello);
            Assert.True(MsgCodec.TryDecode(wire, Guid.Empty, out NetMessage back));
            Assert.Null(((HelloMsg)back).CampaignId);
        }

        [Fact]
        public void ForeignSessionId_DroppedAndLogged()
        {
            Log.Clear();
            byte[] wire = MsgCodec.Encode(new PingMsg { SessionId = Guid.NewGuid(), Ticks = 1 });
            Assert.False(MsgCodec.TryDecode(wire, 0, wire.Length, Sid,
                out _, out MsgCodec.DropReason drop));
            Assert.Equal(MsgCodec.DropReason.SessionMismatch, drop);
            Assert.Contains(Log.Lines, l => l.Contains("codec drop") && l.Contains("session"));
        }

        [Fact]
        public void UnknownType_DroppedAndLogged()
        {
            Log.Clear();
            byte[] wire = MsgCodec.Encode(new PingMsg { SessionId = Sid, Ticks = 1 });
            wire[0] = 0xC8; // not a registered MsgType
            Assert.False(MsgCodec.TryDecode(wire, 0, wire.Length, Sid,
                out _, out MsgCodec.DropReason drop));
            Assert.Equal(MsgCodec.DropReason.UnknownType, drop);
            Assert.Contains(Log.Lines, l => l.Contains("unknown msg-type 200"));
        }

        [Fact]
        public void TruncatedMessage_DroppedAndLogged()
        {
            Log.Clear();
            byte[] wire = MsgCodec.Encode(new PingMsg { SessionId = Sid, Ticks = 1 });
            byte[] trunc = new byte[wire.Length - 3];
            Array.Copy(wire, trunc, trunc.Length);
            Assert.False(MsgCodec.TryDecode(trunc, 0, trunc.Length, Sid,
                out _, out MsgCodec.DropReason drop));
            Assert.Equal(MsgCodec.DropReason.Malformed, drop);
        }

        [Fact]
        public void MutatedEnumByte_DroppedAndLogged()
        {
            Log.Clear();
            byte[] wire = MsgCodec.Encode(new SessionNoticeMsg
            {
                SessionId = Sid, Notice = SessionNoticeKind.GuestJoined,
            });
            wire[wire.Length - 1] = 0xFE; // notice kind 254 is not defined
            Assert.False(MsgCodec.TryDecode(wire, 0, wire.Length, Sid,
                out _, out MsgCodec.DropReason drop));
            Assert.Equal(MsgCodec.DropReason.Malformed, drop);
            Assert.Contains(Log.Lines, l => l.Contains("codec drop"));
        }

        [Fact]
        public void TrailingBytes_DroppedAndLogged()
        {
            Log.Clear();
            byte[] wire = MsgCodec.Encode(new PingMsg { SessionId = Sid, Ticks = 1 });
            byte[] longer = new byte[wire.Length + 4];
            Array.Copy(wire, longer, wire.Length);
            Assert.False(MsgCodec.TryDecode(longer, 0, longer.Length, Sid,
                out _, out MsgCodec.DropReason drop));
            Assert.Equal(MsgCodec.DropReason.TrailingBytes, drop);
        }

        [Fact]
        public void CorruptStringLength_DroppedNotAllocated()
        {
            Log.Clear();
            // Hello layout: [1 type][16 sid][4 protocolVer][4 modVerLen]...
            // Inflate modVer length to 1 GiB -> bound violation, no big alloc.
            var hello = new HelloMsg { ProtocolVersion = 1, ModVersion = "abc" };
            byte[] wire = MsgCodec.Encode(hello);
            int lenOffset = MsgCodec.EnvelopeSize + 4;
            wire[lenOffset] = 0xFF; wire[lenOffset + 1] = 0xFF;
            wire[lenOffset + 2] = 0xFF; wire[lenOffset + 3] = 0x3F;
            Assert.False(MsgCodec.TryDecode(wire, 0, wire.Length, Guid.Empty,
                out _, out MsgCodec.DropReason drop));
            Assert.Equal(MsgCodec.DropReason.Malformed, drop);
        }

        [Fact]
        public void PreSession_HelloAllowed_ButPingForeignNot()
        {
            Log.Clear();
            // Pre-session (expected = Empty): Hello{Empty} decodes.
            byte[] hello = MsgCodec.Encode(new HelloMsg { ProtocolVersion = 1 });
            Assert.True(MsgCodec.TryDecode(hello, Guid.Empty, out NetMessage m));
            Assert.IsType<HelloMsg>(m);
            // But a Ping{Empty} also decodes (matches expected) while a
            // Ping{foreign} does not.
            byte[] pingEmpty = MsgCodec.Encode(new PingMsg { SessionId = Guid.Empty, Ticks = 5 });
            Assert.True(MsgCodec.TryDecode(pingEmpty, Guid.Empty, out _));
            byte[] pingOther = MsgCodec.Encode(new PingMsg { SessionId = Sid, Ticks = 5 });
            Assert.False(MsgCodec.TryDecode(pingOther, Guid.Empty, out _));
        }

        [Fact]
        public void HelloAck_CarriesAssignedSession_PreSession()
        {
            Log.Clear();
            // Guest expecting Empty may decode an accept carrying the new id.
            byte[] ack = MsgCodec.Encode(new HelloAckMsg
            {
                SessionId = Sid, Accepted = true,
            });
            Assert.True(MsgCodec.TryDecode(ack, Guid.Empty, out NetMessage m));
            Assert.Equal(Sid, m.SessionId);
            Assert.True(((HelloAckMsg)m).Accepted);
        }

        [Fact]
        public void Encode_OversizeChunk_Fails()
        {
            Log.Clear();
            var big = new SaveChunkMsg
            {
                SessionId = Sid, Offset = 0,
                Data = new byte[WireLimits.MaxChunkBytes + 1], // 64 KiB + 1
            };
            Assert.Null(MsgCodec.Encode(big));
            Assert.Contains(Log.Lines, l => l.Contains("codec encode failed"));
        }

        [Fact]
        public void Encode_OversizeString_Fails()
        {
            Log.Clear();
            var hello = new HelloMsg
            {
                ProtocolVersion = 1,
                PlayerName = new string('x', WireLimits.MaxPlayerNameBytes + 1),
            };
            Assert.Null(MsgCodec.Encode(hello));
        }

        [Fact]
        public void MaxChunk_RoundTrips_UnderMessageCap()
        {
            Log.Clear();
            var chunk = new SaveChunkMsg
            {
                SessionId = Sid, Offset = 0,
                Data = new byte[WireLimits.MaxChunkBytes], // exactly 64 KiB
            };
            new Random(1234).NextBytes(chunk.Data);
            byte[] wire = Round(chunk, Sid);
            Assert.True(wire.Length <= Protocol.MaxMessageSize);
            Assert.True(MsgCodec.TryDecode(wire, Sid, out NetMessage back));
            Assert.Equal(chunk.Data, ((SaveChunkMsg)back).Data);
        }

        [Fact]
        public void ChannelMap_SaveOnChannel1_RestOnControl()
        {
            Assert.Equal(Protocol.ChannelSave, MsgCodec.ChannelFor(MsgType.SaveOffer));
            Assert.Equal(Protocol.ChannelSave, MsgCodec.ChannelFor(MsgType.SaveChunk));
            Assert.Equal(Protocol.ChannelControl, MsgCodec.ChannelFor(MsgType.StateDelta));
            Assert.Equal(Protocol.ChannelControl, MsgCodec.ChannelFor(MsgType.Ping));
            // T5.2: PosDelta is the only non-control/non-save channel - the
            // latest-state lane (Protocol.ChannelPos, Sequenced delivery).
            Assert.Equal(Protocol.ChannelPos, MsgCodec.ChannelFor(MsgType.PosDelta));
            Assert.Equal(FlyingWormConsole3.LiteNetLib.DeliveryMethod.Sequenced,
                MsgCodec.DeliveryFor(MsgType.PosDelta));
            foreach (MsgType t in MsgCodec.RegisteredTypes)
            {
                if (t == MsgType.PosDelta) { continue; }
                Assert.Equal(FlyingWormConsole3.LiteNetLib.DeliveryMethod.ReliableOrdered,
                    MsgCodec.DeliveryFor(t));
            }
        }

        [Fact]
        public void ResyncReq_ManualRequest_RoundTrips_PinWireValue()
        {
            Log.Clear();
            // T3.6 enum extension: ManualRequest = 5. Pin the literal wire
            // byte so a bound/value regression fails here, not in-game.
            var req = new ResyncReqMsg
            {
                SessionId = Sid,
                Reason = ResyncReason.ManualRequest,
                Detail = "periodic resyncMinutes=0.25",
            };
            byte[] wire = Round(req, Sid);
            Assert.Equal(5, wire[MsgCodec.EnvelopeSize]); // body[0] = reason
            Assert.True(MsgCodec.TryDecode(wire, 0, wire.Length, Sid,
                out NetMessage back, out _));
            var decoded = (ResyncReqMsg)back;
            Assert.Equal(ResyncReason.ManualRequest, decoded.Reason);
            Assert.Equal(req.Detail, decoded.Detail);
            // And the upper bound itself: 6 must not silently decode.
            wire[MsgCodec.EnvelopeSize] = 6;
            Assert.False(MsgCodec.TryDecode(wire, 0, wire.Length, Sid,
                out _, out MsgCodec.DropReason drop));
            Assert.Equal(MsgCodec.DropReason.Malformed, drop);
        }

        [Fact]
        public void SessionNotice_GuestMirrorLost_RoundTrips_PinWireValue()
        {
            Log.Clear();
            // T3.6 enum extension: GuestMirrorLost = 7 (mirror world died
            // mid-session). Same pin: literal byte + bound regression.
            var msg = new SessionNoticeMsg
            {
                SessionId = Sid, Notice = SessionNoticeKind.GuestMirrorLost,
            };
            byte[] wire = Round(msg, Sid);
            Assert.Equal(7, wire[MsgCodec.EnvelopeSize]); // body[0] = kind
            Assert.True(MsgCodec.TryDecode(wire, 0, wire.Length, Sid,
                out NetMessage back, out _));
            Assert.Equal(SessionNoticeKind.GuestMirrorLost,
                ((SessionNoticeMsg)back).Notice);
            wire[MsgCodec.EnvelopeSize] = 8;
            Assert.False(MsgCodec.TryDecode(wire, 0, wire.Length, Sid,
                out _, out MsgCodec.DropReason drop));
            Assert.Equal(MsgCodec.DropReason.Malformed, drop);
        }
    }
}
