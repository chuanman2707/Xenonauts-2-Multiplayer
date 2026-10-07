using System;
using System.Collections.Generic;
using System.Text;
using X2Coop.Net;

namespace X2Coop.Session
{
    /// <summary>
    /// Pure parity comparison between a local HelloMsg (built by
    /// <see cref="SessionHello.Collect"/> with the game's own field values) and
    /// the remote one a guest sent. Kept free of game references so the xunit
    /// suite can link and exercise it directly.
    ///
    /// Check order is fixed: protocolVer, modVer, gameVer, the four commit
    /// hashes (artitas / x2-assets / ghi-common / x2-core = Commits
    /// enumerator order), then the active-pack stamp set. The first mismatch
    /// wins and produces BOTH the HelloRejectReason and a human-readable
    /// detail string that names the field and both values - the guest cannot
    /// reconstruct the detail (it does not know the host's values), so it is
    /// transported inside HelloAck.Detail.
    /// </summary>
    public static class SessionParity
    {
        /// <summary>Commit-hash slot labels, in CommitHashes wire order.</summary>
        public static readonly string[] CommitLabels =
            { "artitas", "x2-assets", "ghi-common", "x2-core" };

        /// <summary>
        /// Compare local (host) vs remote (guest) handshake fields.
        /// Returns HelloRejectReason.None on full parity; else the reject code.
        /// <paramref name="detail"/> always gets the human-readable text.
        /// </summary>
        public static HelloRejectReason Compare(HelloMsg local, HelloMsg remote, out string detail)
        {
            if (local == null || remote == null)
            {
                detail = "internal error: missing hello fields";
                return HelloRejectReason.VersionMismatch;
            }
            if (remote.ProtocolVersion != local.ProtocolVersion)
            {
                detail = "protocol version " + remote.ProtocolVersion
                    + " != " + local.ProtocolVersion;
                return HelloRejectReason.VersionMismatch;
            }
            if (!StrEq(remote.ModVersion, local.ModVersion))
            {
                detail = "mod version " + Show(remote.ModVersion)
                    + " != " + Show(local.ModVersion);
                return HelloRejectReason.VersionMismatch;
            }
            if (!StrEq(remote.GameVersion, local.GameVersion))
            {
                detail = "game version " + Show(remote.GameVersion)
                    + " != " + Show(local.GameVersion);
                return HelloRejectReason.VersionMismatch;
            }
            for (int i = 0; i < WireLimits.CommitHashCount; i++)
            {
                string lr = At(local.CommitHashes, i);
                string rr = At(remote.CommitHashes, i);
                if (!StrEq(rr, lr))
                {
                    string label = i < CommitLabels.Length ? CommitLabels[i] : ("#" + i);
                    detail = "commit " + label + " " + Show(rr) + " != " + Show(lr);
                    return HelloRejectReason.VersionMismatch;
                }
            }
            string packDetail = ComparePacks(local.Packs, remote.Packs);
            if (packDetail != null)
            {
                detail = packDetail;
                return HelloRejectReason.PackMismatch;
            }
            detail = string.Empty;
            return HelloRejectReason.None;
        }

        /// <summary>
        /// Set-difference over (uid, version) stamp pairs. Order-insensitive
        /// (BuildStamps order is a config enumeration, not a contract).
        /// Returns null on equality, else "packs differ: host-only=[..] guest-only=[..]".
        /// </summary>
        public static string ComparePacks(PackStamp[] local, PackStamp[] remote)
        {
            var localSet = StampSet(local);
            var remoteSet = StampSet(remote);
            var hostOnly = new List<string>();
            var guestOnly = new List<string>();
            foreach (string s in localSet)
            {
                if (!remoteSet.Contains(s)) { hostOnly.Add(s); }
            }
            foreach (string s in remoteSet)
            {
                if (!localSet.Contains(s)) { guestOnly.Add(s); }
            }
            if (hostOnly.Count == 0 && guestOnly.Count == 0) { return null; }
            hostOnly.Sort(StringComparer.Ordinal);
            guestOnly.Sort(StringComparer.Ordinal);
            return "packs differ: host-only=[" + string.Join(",", hostOnly.ToArray())
                + "] guest-only=[" + string.Join(",", guestOnly.ToArray()) + "]";
        }

        private static HashSet<string> StampSet(PackStamp[] packs)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (packs != null)
            {
                foreach (PackStamp p in packs)
                {
                    set.Add((p.Uid ?? string.Empty) + "@" + (p.Version ?? string.Empty));
                }
            }
            return set;
        }

        private static string At(string[] arr, int i)
        {
            return arr != null && i < arr.Length ? arr[i] : null;
        }

        private static bool StrEq(string a, string b)
        {
            return string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);
        }

        private static string Show(string s)
        {
            return string.IsNullOrEmpty(s) ? "<none>" : s;
        }
    }
}
