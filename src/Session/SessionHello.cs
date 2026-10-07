using System;
using System.Collections.Generic;
using System.Linq;
using Code.Screens.Common.Systems;
using Common.Content;
using Common.Content.DataStructures;
using Xenonauts;
using Xenonauts.Common.Platform.GoG;
using X2Coop.Net;

namespace X2Coop.Session
{
    /// <summary>
    /// Builds the local side of the Hello handshake: fills a HelloMsg with this
    /// process's real versions, commit hashes, player name and active-pack
    /// stamps. Both directions use it - the guest sends what it collects, the
    /// host collects its own view to compare against the guest's Hello
    /// (<see cref="SessionParity.Compare"/>).
    ///
    /// Field sources mirror SaveGameSystem.InternalProcessSaveGameRequest
    /// (SaveGameSystem.cs:497-506) exactly so a session accepts iff a save
    /// written now could be shared:
    ///   gameVer = XenonautsConstants.Versioning.VERSION
    ///   commits = Commits.Instance.{Artitas,Assets,Common,X2Core}CommitHash
    ///   packs   = ContentPackDependencyResolver.BuildStamps(
    ///               AssetReference.ContentManager.GetConfig()
    ///                 .GetAllActiveContentPacks().Select(p => p.Manifest.Get()))
    ///
    /// Debug overrides (DebugSettings): x2coop.fakeModVer replaces ModVersion,
    /// x2coop.fakePackStamp appends a synthetic "uid[@ver]" stamp - the forced
    /// -mismatch verify knobs. playerName resolution tries the platform
    /// identity (GoG persona name via the core module) then the
    /// x2coop.playerName override, then a neutral "player" placeholder. The
    /// name is transported but NEVER logged (Log.cs rule).
    /// </summary>
    public static class SessionHello
    {
        /// <summary>Fill every parity field of <paramref name="h"/> from live game state.</summary>
        public static void Collect(HelloMsg h)
        {
            if (h == null) { return; }
            h.ProtocolVersion = Protocol.Version;
            h.ModVersion = DebugSettings.Get("fakeModVer") ?? ModInfo.ModVersion;
            h.GameVersion = CollectGameVersion();
            h.CommitHashes = CollectCommits();
            h.PlayerName = CollectPlayerName();
            h.Packs = CollectPackStamps();
            // CampaignId: resume-lobby flow is post-S1; stays null for now.
        }

        private static string CollectGameVersion()
        {
            try
            {
                object v = XenonautsConstants.Versioning.VERSION;
                return v != null ? v.ToString() : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Exception("hello gameVer", ex);
                return string.Empty;
            }
        }

        private static string[] CollectCommits()
        {
            var hashes = new string[WireLimits.CommitHashCount];
            try
            {
                Commits c = Commits.Instance;
                if (c != null)
                {
                    // Wire order = Commits.GetEnumerator order:
                    // artitas, x2-assets, ghi-common, x2-core.
                    hashes[0] = c.ArtitasCommitHash ?? string.Empty;
                    hashes[1] = c.AssetsCommitHash ?? string.Empty;
                    hashes[2] = c.CommonCommitHash ?? string.Empty;
                    hashes[3] = c.X2CoreCommitHash ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("hello commits", ex);
            }
            for (int i = 0; i < hashes.Length; i++)
            {
                if (hashes[i] == null) { hashes[i] = string.Empty; }
            }
            return hashes;
        }

        /// <summary>
        /// Pack stamps via the SAME call InternalProcessSaveGameRequest makes.
        /// A stamp is uid+"@"+version-as-string; packs without a version stamp
        /// as "uid@" (matches BuildStamps' Optional-less semantics).
        /// </summary>
        private static PackStamp[] CollectPackStamps()
        {
            var list = new List<PackStamp>();
            try
            {
                if (AssetReference.IsContentManagerActive)
                {
                    List<ContentPackManifestDependency> stamps =
                        ContentPackDependencyResolver.BuildStamps(
                            AssetReference.ContentManager.GetConfig()
                                .GetAllActiveContentPacks()
                                .Select(p => p.Manifest.Get()));
                    foreach (ContentPackManifestDependency d in stamps)
                    {
                        if (d == null) { continue; }
                        string ver = string.Empty;
                        try
                        {
                            // BuildStamps stores manifest.Version inside an
                            // Optional<SemVersion>; unset -> "".
                            if (d.Version.IsSet && d.Version.Value != null)
                            {
                                ver = d.Version.Value.ToString();
                            }
                        }
                        catch
                        {
                        }
                        list.Add(new PackStamp { Uid = d.UID ?? string.Empty, Version = ver });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("hello packs", ex);
            }
            // Verify knob: inject a synthetic stamp one side can never match.
            string fake = DebugSettings.Get("fakePackStamp");
            if (!string.IsNullOrEmpty(fake))
            {
                string uid = fake;
                string ver = "0.0.0";
                int at = fake.IndexOf('@');
                if (at >= 0)
                {
                    uid = fake.Substring(0, at);
                    ver = fake.Substring(at + 1);
                }
                list.Add(new PackStamp { Uid = uid, Version = ver });
            }
            return list.ToArray();
        }

        /// <summary>
        /// Best-effort platform identity for the Hello playerName field.
        /// GoG exposes a persona name through the core module; other stores
        /// don't surface one on ICorePlatformModule, so the fallback chain is
        /// x2coop.playerName (dev override) then "player". NEVER logged.
        /// </summary>
        private static string CollectPlayerName()
        {
            try
            {
                XenonautsMain main = XenonautsMain.Instance;
                var gog = main != null && main.PlatformManager != null
                    ? main.PlatformManager.Core as GoGCorePlatformModule
                    : null;
                if (gog != null)
                {
                    string name = gog.GetMyUsername(silent: true);
                    if (!string.IsNullOrEmpty(name)) { return name; }
                }
            }
            catch
            {
                // Galaxy not signed in / module missing - fall through.
            }
            string over = DebugSettings.Get("playerName");
            if (!string.IsNullOrEmpty(over)) { return over; }
            return "player";
        }
    }
}
