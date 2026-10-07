using System;
using System.Collections.Generic;

namespace X2Coop.Net
{
    /// <summary>
    /// Codec self-test: encodes a deterministic sample of every registered
    /// MsgType, decodes it against the matching sessionId, re-encodes and
    /// requires BYTE-IDENTICAL output (proves the codec is deterministic and
    /// lossless, not just approximately right). Then exercises the drop paths
    /// (mutated payload byte, wrong session, unknown type, truncation).
    ///
    /// No Unity / LiteNetLib-transport dependency: runs inside the game (Runner
    /// hotkey B, X2COOP_PROBES builds) and inside the xunit suite. Results are
    /// both returned AND logged: "codec ok n/n" / "codec drops ok k/k" on a
    /// full pass, "codec FAIL &lt;case&gt;: &lt;why&gt;" + a summary line otherwise.
    /// </summary>
    public static class CodecSelfTest
    {
        /// <summary>
        /// Runs all cases. Returns the failure list - EMPTY means everything
        /// passed (each entry is "case: reason" for reporting).
        /// </summary>
        public static List<string> Run()
        {
            var failures = new List<string>();
            int roundTrips = 0;
            int drops = 0;
            try
            {
                roundTrips = RunRoundTrips(failures);
                drops = RunDropProbes(failures);
            }
            catch (Exception ex)
            {
                failures.Add("self-test threw: " + ex.GetType().Name + " " + ex.Message);
            }

            int rtTotal = MsgCodec.RegisteredCount;
            int dropTotal = 4;
            if (roundTrips == rtTotal && drops == dropTotal && failures.Count == 0)
            {
                Log.Info("codec ok " + roundTrips + "/" + rtTotal);
                Log.Info("codec drops ok " + drops + "/" + dropTotal);
            }
            else
            {
                foreach (string f in failures)
                {
                    Log.Warn("codec FAIL " + f);
                }
                Log.Error("codec failed: roundtrips " + roundTrips + "/" + rtTotal
                    + " drops " + drops + "/" + dropTotal);
            }
            return failures;
        }

        /// <summary>Deterministic sample for every registered MsgType.</summary>
        internal static NetMessage[] Samples(Guid sessionId)
        {
            byte[] sha = Pattern(WireLimits.Sha256Bytes, 0x5A);
            return new NetMessage[]
            {
                new HelloMsg
                {
                    SessionId = Guid.Empty, // pre-session by definition
                    ProtocolVersion = Protocol.Version,
                    ModVersion = "0.1.0",
                    GameVersion = "4.28.0-GE",
                    CommitHashes = new[] { "a1b2c3", "d4e5f6", "0a1b2c", "3d4e5f" },
                    PlayerName = "Guest-42",
                    Packs = new[]
                    {
                        new PackStamp { Uid = "x2coop", Version = "0.1.0" },
                        new PackStamp { Uid = "community.balance", Version = "2.3" },
                    },
                    CampaignId = sessionId,
                },
                new HelloAckMsg { SessionId = sessionId, Accepted = true, RejectReason = HelloRejectReason.None },
                new SaveOfferMsg
                {
                    SessionId = sessionId, OfferSessionId = sessionId,
                    CampaignId = sessionId, BaselineSeq = 1234L, ByteCount = 987654L, Sha256 = sha,
                    CampaignName = "Purple Knight",
                },
                new SaveChunkMsg { SessionId = sessionId, Offset = 65536L, Data = Pattern(5000, 0x11) },
                new GuestReadyMsg { SessionId = sessionId, Status = GuestReadyStatus.Ready },
                new StateDeltaMsg
                {
                    SessionId = sessionId, Seq = 1235L,
                    HostSendTicks = 638000000000010000L,
                    Ops = new[]
                    {
                        new DeltaOp
                        {
                            EntityId = 42, Kind = DeltaOpKind.SetComponent,
                            ComponentType = "Xenonauts.Strategy.Components.GeoscapeDateTimeComponent",
                            Payload = Pattern(24, 0x22),
                        },
                        new DeltaOp { EntityId = 7, Kind = DeltaOpKind.DeleteEntity },
                        new DeltaOp
                        {
                            EntityId = 9, Kind = DeltaOpKind.CreateEntity,
                            ComponentType = "Common.Components.PositionComponent",
                            Payload = Pattern(48, 0x33),
                        },
                        new DeltaOp
                        {
                            EntityId = 42, Kind = DeltaOpKind.RemoveComponent,
                            ComponentType = "Xenonauts.Strategy.Components.StaleFlagComponent",
                        },
                    },
                },
                new PosDeltaMsg
                {
                    SessionId = sessionId, Seq = 77L, HostSendTicks = 638000000000020000L,
                    Ops = new[]
                    {
                        new DeltaOp
                        {
                            EntityId = 42, Kind = DeltaOpKind.SetComponent,
                            ComponentType = "Xenonauts.Strategy.Components.GeoscapeDateTimeComponent",
                            Payload = Pattern(24, 0x44),
                        },
                        new DeltaOp
                        {
                            EntityId = 11, Kind = DeltaOpKind.SetComponent,
                            ComponentType = "Common.Components.PositionComponent",
                            Payload = Pattern(40, 0x55),
                        },
                    },
                },
                new ResyncReqMsg { SessionId = sessionId, Reason = ResyncReason.HashMismatch, Detail = "seq 1235" },
                new SessionNoticeMsg { SessionId = sessionId, Notice = SessionNoticeKind.HostLeftStrategy },
                new PingMsg { SessionId = sessionId, Ticks = 638000000000000000L },
                new PongMsg { SessionId = sessionId, Ticks = 638000000000000000L },
                new HashReqMsg { SessionId = sessionId, Seq = 1500L },
                new HashRespMsg { SessionId = sessionId, Seq = 1500L, Hash = sha, Count = 34567 },
            };
        }

        private static int RunRoundTrips(List<string> failures)
        {
            Guid sessionId = new Guid(Pattern(16, 0x7E));
            NetMessage[] samples = Samples(sessionId);
            if (samples.Length != MsgCodec.RegisteredCount)
            {
                failures.Add("samples=" + samples.Length + " != registered=" + MsgCodec.RegisteredCount);
            }
            int ok = 0;
            foreach (NetMessage sample in samples)
            {
                try
                {
                    byte[] wire = MsgCodec.Encode(sample);
                    if (wire == null)
                    {
                        failures.Add("roundtrip " + sample.Type + ": encode returned null");
                        continue;
                    }
                    // Hello is pre-session: decode with Empty expected.
                    Guid expected = sample.Type == MsgType.Hello ? Guid.Empty : sessionId;
                    if (!MsgCodec.TryDecode(wire, 0, wire.Length, expected, out NetMessage back, out MsgCodec.DropReason drop))
                    {
                        failures.Add("roundtrip " + sample.Type + ": decode dropped (" + drop + ")");
                        continue;
                    }
                    if (back == null || back.Type != sample.Type)
                    {
                        failures.Add("roundtrip " + sample.Type + ": decoded type mismatch");
                        continue;
                    }
                    byte[] wire2 = MsgCodec.Encode(back);
                    if (wire2 == null || !BytesEqual(wire, wire2))
                    {
                        failures.Add("roundtrip " + sample.Type + ": re-encode not byte-identical");
                        continue;
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    failures.Add("roundtrip " + sample.Type + ": threw " + ex.GetType().Name);
                }
            }
            return ok;
        }

        private static int RunDropProbes(List<string> failures)
        {
            Guid sessionId = new Guid(Pattern(16, 0x7E));
            int ok = 0;

            // 1. Mutated payload byte: HelloAck reject-reason 4 -> 200 (invalid enum).
            byte[] ack = MsgCodec.Encode(new HelloAckMsg
            {
                SessionId = sessionId, Accepted = false, RejectReason = HelloRejectReason.SessionFull,
            });
            if (ack != null && MustDrop(ack, ack.Length - 1, sessionId, "mutated-byte", failures)) { ok++; }

            // 2. Wrong session: Ping under a different sessionId.
            byte[] pingForeign = MsgCodec.Encode(new PingMsg
            {
                SessionId = Guid.NewGuid(), Ticks = 1L,
            });
            if (pingForeign != null
                && MustDrop(pingForeign, -1, sessionId, "foreign-session", failures)) { ok++; }

            // 3. Unknown MsgType byte (valid envelope, unregistered type).
            byte[] pong = MsgCodec.Encode(new PongMsg { SessionId = sessionId, Ticks = 1L });
            if (pong != null)
            {
                byte[] unknown = (byte[])pong.Clone();
                unknown[0] = 200;
                if (MustDrop(unknown, -1, sessionId, "unknown-type", failures)) { ok++; }
            }

            // 4. Truncated message: Ping minus its last 3 payload bytes.
            if (pong != null)
            {
                byte[] truncated = new byte[pong.Length - 3];
                Array.Copy(pong, truncated, truncated.Length);
                if (MustDrop(truncated, -1, sessionId, "truncated", failures)) { ok++; }
            }
            return ok;
        }

        /// <summary>
        /// Applies an optional byte mutation (<paramref name="mutateIndex"/> &lt; 0
        /// = none) and requires TryDecode to reject the result.
        /// </summary>
        private static bool MustDrop(byte[] wire, int mutateIndex, Guid expected,
            string name, List<string> failures)
        {
            try
            {
                if (mutateIndex >= 0 && mutateIndex < wire.Length)
                {
                    wire[mutateIndex] = unchecked((byte)(wire[mutateIndex] ^ 0xFF));
                }
                if (MsgCodec.TryDecode(wire, 0, wire.Length, expected, out _, out MsgCodec.DropReason drop))
                {
                    failures.Add("drop " + name + ": decoded anyway");
                    return false;
                }
                if (drop == MsgCodec.DropReason.None)
                {
                    failures.Add("drop " + name + ": dropped but reason=None");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                failures.Add("drop " + name + ": threw " + ex.GetType().Name);
                return false;
            }
        }

        private static byte[] Pattern(int n, byte seed)
        {
            byte[] b = new byte[n];
            for (int i = 0; i < n; i++) { b[i] = unchecked((byte)(seed + i)); }
            return b;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) { return false; }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) { return false; }
            }
            return true;
        }
    }
}
