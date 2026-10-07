using System;
using Xunit;
using X2Coop;
using X2Coop.Net;
using X2Coop.Session;

namespace X2Coop.Tests
{
    /// <summary>
    /// T2.3 handshake verification: HelloAck carries the human-readable reject
    /// Detail, SessionParity.Compare produces the right reject code + detail
    /// text per mismatch field, and DebugSettings parses the -x2coop.* forms.
    /// </summary>
    public class SessionTests
    {
        private static readonly Guid Sid = new Guid(new byte[]
        {
            0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE,
            0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        });

        private static HelloMsg BaseHello()
        {
            return new HelloMsg
            {
                ProtocolVersion = Protocol.Version,
                ModVersion = "0.1.0",
                GameVersion = "4.28.0-GE",
                CommitHashes = new[] { "aa", "bb", "cc", "dd" },
                PlayerName = "p",
                Packs = new[]
                {
                    new PackStamp { Uid = "x2coop", Version = "0.1.0" },
                    new PackStamp { Uid = "core", Version = "1.0" },
                },
            };
        }

        [Fact]
        public void HelloAck_Detail_RoundTrips()
        {
            Log.Clear();
            var ack = new HelloAckMsg
            {
                SessionId = Guid.Empty,
                Accepted = false,
                RejectReason = HelloRejectReason.VersionMismatch,
                Detail = "mod version 0.0.0 != 0.1.0",
            };
            byte[] wire = MsgCodec.Encode(ack);
            Assert.NotNull(wire);
            Assert.True(MsgCodec.TryDecode(wire, Guid.Empty, out NetMessage back));
            var a = (HelloAckMsg)back;
            Assert.False(a.Accepted);
            Assert.Equal(HelloRejectReason.VersionMismatch, a.RejectReason);
            Assert.Equal("mod version 0.0.0 != 0.1.0", a.Detail);
        }

        [Fact]
        public void Parity_Match_ReturnsNone()
        {
            HelloRejectReason r = SessionParity.Compare(BaseHello(), BaseHello(), out string detail);
            Assert.Equal(HelloRejectReason.None, r);
            Assert.Equal(string.Empty, detail);
        }

        [Fact]
        public void Parity_ProtocolMismatch()
        {
            HelloMsg remote = BaseHello();
            remote.ProtocolVersion = 99;
            HelloRejectReason r = SessionParity.Compare(BaseHello(), remote, out string detail);
            Assert.Equal(HelloRejectReason.VersionMismatch, r);
            Assert.Equal("protocol version 99 != " + Protocol.Version, detail);
        }

        [Fact]
        public void Parity_ModVersionMismatch_NamesBothValues()
        {
            HelloMsg remote = BaseHello();
            remote.ModVersion = "0.0.0"; // the x2coop.fakeModVer verify case
            HelloRejectReason r = SessionParity.Compare(BaseHello(), remote, out string detail);
            Assert.Equal(HelloRejectReason.VersionMismatch, r);
            Assert.Equal("mod version 0.0.0 != 0.1.0", detail);
        }

        [Fact]
        public void Parity_GameVersionMismatch()
        {
            HelloMsg remote = BaseHello();
            remote.GameVersion = "4.27.0";
            HelloRejectReason r = SessionParity.Compare(BaseHello(), remote, out string detail);
            Assert.Equal(HelloRejectReason.VersionMismatch, r);
            Assert.Contains("game version", detail);
        }

        [Fact]
        public void Parity_CommitMismatch_NamesRepo()
        {
            HelloMsg remote = BaseHello();
            remote.CommitHashes = new[] { "aa", "XX", "cc", "dd" }; // slot 1 = x2-assets
            HelloRejectReason r = SessionParity.Compare(BaseHello(), remote, out string detail);
            Assert.Equal(HelloRejectReason.VersionMismatch, r);
            Assert.Equal("commit x2-assets XX != bb", detail);
        }

        [Fact]
        public void Parity_PackMismatch_ListsBothSides()
        {
            HelloMsg remote = BaseHello();
            remote.Packs = new[]
            {
                new PackStamp { Uid = "x2coop", Version = "0.1.0" },
                new PackStamp { Uid = "extra-pack", Version = "9.9" }, // guest-only pack
            };
            HelloRejectReason r = SessionParity.Compare(BaseHello(), remote, out string detail);
            Assert.Equal(HelloRejectReason.PackMismatch, r);
            Assert.Contains("guest-only=[extra-pack@9.9]", detail);
            Assert.Contains("host-only=[core@1.0]", detail);
        }

        [Fact]
        public void Parity_PackOrderInsensitive()
        {
            HelloMsg remote = BaseHello();
            var reversed = (PackStamp[])remote.Packs.Clone();
            Array.Reverse(reversed);
            remote.Packs = reversed;
            Assert.Equal(HelloRejectReason.None,
                SessionParity.Compare(BaseHello(), remote, out _));
        }

        [Fact]
        public void Parity_ModVerCheckedBeforePacks()
        {
            HelloMsg remote = BaseHello();
            remote.ModVersion = "0.0.0";
            remote.Packs = new PackStamp[0];
            Assert.Equal(HelloRejectReason.VersionMismatch,
                SessionParity.Compare(BaseHello(), remote, out _));
        }

        [Fact]
        public void DebugSettings_ParseArgs_EqualsForm()
        {
            var map = DebugSettings.ParseArgs(new[]
            {
                "game.exe", "-x2coop.fakeModVer=0.0.0", "-screen-fullscreen", "0",
            });
            Assert.Equal("0.0.0", map["fakemodver"]);
            Assert.False(map.ContainsKey("screen-fullscreen"));
        }

        [Fact]
        public void DebugSettings_ParseArgs_SpaceAndBareForms()
        {
            var map = DebugSettings.ParseArgs(new[]
            {
                "-x2coop.joinRetryMs", "5000", "-x2coop.verbose", "-other", "x",
            });
            Assert.Equal("5000", map["joinRetryMs"]);
            Assert.Equal("true", map["verbose"]);
            Assert.False(map.ContainsKey("other"));
        }

        // --- T3.3 SaveFileNamer: filename fallback chain --------------------

        [Fact]
        public void FileStem_CampaignIdWins()
        {
            var cid = new Guid(new byte[]
            {
                0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE,
                0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
            });
            Assert.Equal("campaign-10325476",
                SaveFileNamer.Stem(cid, "Purple Knight", Sid));
        }

        [Fact]
        public void FileStem_EmptyCampaignId_UsesSanitizedName()
        {
            Assert.Equal("Purple Knight",
                SaveFileNamer.Stem(Guid.Empty, "Purple Knight", Sid));
        }

        [Fact]
        public void FileStem_InvalidChars_Replaced()
        {
            // Windows-invalid filename chars -> '_' (each sanitized char kept).
            Assert.Equal("a_b_c",
                SaveFileNamer.Stem(Guid.Empty, "a/b\\c", Sid));
        }

        [Fact]
        public void FileStem_NullOrAllInvalid_FallsToOfferSid()
        {
            Assert.Equal("offer-10325476",
                SaveFileNamer.Stem(Guid.Empty, null, Sid));
            Assert.Equal("offer-10325476",
                SaveFileNamer.Stem(Guid.Empty, string.Empty, Sid));
            Assert.Equal("offer-10325476",
                SaveFileNamer.Stem(Guid.Empty, "   ", Sid));
        }

        [Fact]
        public void FileStem_LongName_Capped()
        {
            string longName = new string('x', 200);
            string stem = SaveFileNamer.Stem(Guid.Empty, longName, Sid);
            Assert.Equal(SaveFileNamer.MaxStemChars, stem.Length);
        }
    }
}
