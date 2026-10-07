#if X2COOP_PROBES
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Artitas;
using Artitas.Components;
using Artitas.Events;
using Code.Screens.Common.Systems;
using Common;
using Common.Components;
using Common.Content;
using Common.Content.DataStructures;
using Common.Content.Descriptors;
using Common.Content.Descriptors.SaveParameters;
using Common.Util;
using HarmonyLib;
using UnityEngine;
using Xenonauts;
using Xenonauts.Common;
using Xenonauts.Common.Events;
using Xenonauts.Common.GlobalVariables;
using Xenonauts.Common.Systems;
using Xenonauts.Strategy.Components.MainBase;
using Xenonauts.Strategy.Systems;

namespace X2Coop.Probes
{
    /// <summary>
    /// R8 recon probe - custom save write + load path. Debug builds only.
    ///
    /// Hotkey: F8 (polled via <see cref="Input.GetKey"/> inside a postfix on
    /// Xenonauts.Strategy.Systems.TimeTransitionSystem.UpdateTimeOnBase - the
    /// same world-thread tick subscriber ProbePatches already patches, so no
    /// probe MonoBehaviour/GameObject is needed. Held-key edge detection via
    /// _wasDown because GetKeyDown would fire once per postfix call in the same
    /// frame (~2 ticks/frame). Caveat: under the ESC GAME MENU the world does
    /// not process at all, so the hotkey is dead there - press it on the live
    /// geoscape. Same call context as the game's own queued-save processing
    /// (ProcessSaveGameRequestsAtEndOfATick fires inside subscriber dispatch).
    ///
    /// On F8, mirrors SaveGameSystem.InternalProcessSaveGameRequest
    /// (SaveGameSystem.cs:401-572) minus the group-driven descriptor/naming and
    /// the save-limit enforcement:
    ///   1. log geoscape datetime (GeoscapeClockTag -> GeoscapeDateTime.value)
    ///   2. World.HandleEvent(new PreSaveGameReport())
    ///   3. new SerializationRequestCommand() (State left unset = same as a
    ///      request built via `new SaveGameRequest(group)`; ArtitasSerializationSystem
    ///      .HandleSerialization then calls CopyWorldIntoWorldState itself,
    ///      ArtitasSerializationSystem.cs:98) inside EnterSaveSerialization
    ///   4. build the SaveGame asset (same metadata fields as :492-511, minus
    ///      the screenshot - not needed for load, avoids ImageConversionModule)
    ///   5. ContentManager.Save(asset, FileDescriptor, JsonPrinterSaveParameters(
    ///      compress:true), overwrite:true, cacheAsset:false) - once to a path
    ///      OUTSIDE Constants.Folders.Save() ($PROF\x2coop\tmp\probe.json) and
    ///      once to Saves\x2coop_guest\x2coop_probe-1.json
    ///   6. World.HandleEvent(new PostSaveGameReport())
    ///   7. SaveGameSystem.LoadGame(new FileDescriptor(guestPath,...), false)
    ///      (SaveGameSystem.cs:740 - Exists -> partial load -> group needs a
    ///      GameScreensInformationSegment -> RequestMoveTo(Strategy) ->
    ///      DeserializeSaveGameCommand on the fresh world)
    ///   8. verify: first UpdateTimeOnBase on the NEW StrategyWorld logs the
    ///      post-load geoscape datetime for comparison with step 1.
    /// </summary>
    internal static class SavePathProbe
    {
        private static readonly KeyCode TriggerKey = KeyCode.F8;

        private static bool _wasDown;
        private static bool _running;

        // Verify-after-load state: the load replaces the world, so the first
        // tick on a DIFFERENT World instance is post-load. _verifyDeadline
        // bounds the wait so a failed load doesn't leave the flag stuck.
        private static World _savedWorld;
        private static DateTime _savedGeoDate;
        private static bool _pendingVerify;
        private static readonly Stopwatch VerifyTimer = new Stopwatch();

        /// <summary>
        /// T2.6 pack teardown: drop the verify-after-load latch and the strong
        /// world ref (the world it points at may be disposed while the pack
        /// is off). Probe counters/history are untouched.
        /// </summary>
        public static void Reset()
        {
            _savedWorld = null;
            _savedGeoDate = default(DateTime);
            _pendingVerify = false;
            VerifyTimer.Reset();
            _wasDown = false;
            _running = false;
        }

        /// <summary>Patch UpdateTimeOnBase with our own postfix. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("r8 probes: Apply called with null patcher");
                return;
            }
            try
            {
                var original = AccessTools.Method(typeof(TimeTransitionSystem), "UpdateTimeOnBase");
                var postfix = AccessTools.Method(typeof(SavePathProbe), nameof(KeyPollPostfix));
                if (original == null || postfix == null)
                {
                    Log.Warn("r8 probes: method/postfix not found (UpdateTimeOnBase/KeyPollPostfix)");
                    return;
                }
                patcher.Patch(original, postfix: new HarmonyMethod(postfix));
                Log.Info("r8 probes: patch-applied Xenonauts.Strategy.Systems.TimeTransitionSystem.UpdateTimeOnBase (hotkey=F8)");
            }
            catch (Exception ex)
            {
                Log.Exception("r8 probes apply", ex);
            }
        }

        /// <summary>Postfix on UpdateTimeOnBase: poll the hotkey + run post-load verify.</summary>
        private static void KeyPollPostfix(TimeTransitionSystem __instance)
        {
            try
            {
                World w = null;
                try
                {
                    w = __instance?.World; // BaseSystem.World is public (BaseSystem.cs:19)
                }
                catch
                {
                }
                MaybeVerify(w);
                bool down = SafeGetKey(TriggerKey);
                if (down && !_wasDown && !_running)
                {
                    RunProbe(w);
                }
                _wasDown = down;
            }
            catch (Exception ex)
            {
                Log.Exception("r8 KeyPollPostfix", ex);
            }
        }

        private static bool SafeGetKey(KeyCode key)
        {
            try
            {
                return Input.GetKey(key);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The probe body. Runs inside world event dispatch (same as the real save path).</summary>
        private static void RunProbe(World world)
        {
            _running = true;
            try
            {
                if (world == null || world.Name != "StrategyWorld")
                {
                    Log.Warn("r8 probe: skipped - not on StrategyWorld (world=" + (world == null ? "null" : world.Name ?? "<unnamed>") + ")");
                    return;
                }
                SaveGameSystem saveSys = world.GetSystem<SaveGameSystem>();
                if (saveSys == null)
                {
                    Log.Warn("r8 probe: skipped - no SaveGameSystem on world");
                    return;
                }

                // --- pre-save state -------------------------------------------------
                DateTime geoDate = ReadGeoscapeDateTime(world, out bool geoOk);
                Entity campaign = SafeFindTagged<CampaignTag>(world);
                string campaignName = SafeString(() => campaign?.Name()?.value, "?");
                TimeSpan playTime = SafeTimeSpan(() => campaign?.CampaignPlayTime()?.value ?? TimeSpan.Zero);
                CampaignLogicSystem logic = world.GetSystem<CampaignLogicSystem>();
                Log.Info("r8 save begin: geodate=" + FmtDate(geoDate) + " geoOk=" + geoOk
                    + " campaign=" + campaignName + " playTime=" + playTime
                    + " ironman=" + SafeString(() => logic.IsIronManMode.ToString(), "?")
                    + " world=" + world.GetHashCode());

                // --- mirror InternalProcessSaveGameRequest --------------------------
                // SAVE_PROFILER must already have a live section: SaveGameConverter.
                // DoSerialize (reached via the VersionedAssetSerializer stage inside
                // ContentManager.Save) calls SAVE_PROFILER.Mark(), and Profiler.Mark
                // -> Stop() throws "No sections started" on an empty stack. AssetTask
                // .Process swallows that into TaskStatus.Failed, so without this the
                // Save() returns AssetState.Failure with no exception on our side.
                // Real path does Reset()+Start() at SaveGameSystem.cs:403-404.
                SaveGameSystem.SAVE_PROFILER.Reset();
                SaveGameSystem.SAVE_PROFILER.Start("r8 Handle Pre Save Report");
                world.HandleEvent(new PreSaveGameReport());
                SaveGameSystem.SAVE_PROFILER.Mark("r8 Serialize World");

                SerializationRequestCommand cmd = new SerializationRequestCommand();
                // State intentionally left default(Optional<WorldState>) - identical to a
                // SaveGameRequest built without serializeWorldNow; HandleSerialization
                // then calls CopyWorldIntoWorldState itself.
                using (AssetDescriptor.EnterSaveSerialization())
                {
                    world.HandleEvent(cmd);
                }
                bool serOk = cmd.Result.Succeeded;
                Log.Info("r8 serialize: ok=" + serOk
                    + " dataIsNull=" + (cmd.Data == null || cmd.Data.IsNull)
                    + (serOk ? string.Empty : " err=" + cmd.Result.FormattedMessages));
                if (!serOk)
                {
                    return;
                }

                Dictionary<string, string> inject = SafeInjectTable(saveSys);
                string diffName = SafeString(() =>
                {
                    var diffSys = world.GetSystem<DifficultyConfigSystem>();
                    return diffSys?.DifficultyConfig?.Name()?.value;
                }, "unknown");
                Semver.SemVersion created = SafeSemVersion(() =>
                    world.GetSystem<GlobalVariableSystem>()?.Get<VersionComponent>(XenonautsConstants.GlobalVariables.CREATED_VERSION));

                // Group mirrors the real path: USER group + GameScreens segment
                // (required by LoadGame - SaveGameSystem.cs:765), then the same
                // four metadata Prefix() calls as :444, then ResolveIdentity.
                SaveGameGroup grp = new SaveGameGroup(XenonautsConstants.SaveGameGroups.USER)
                    .Suffix(new SaveGameGroup.GameScreensInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None, GameScreens.Strategy));
                SaveGameGroup grpFull = grp
                    .Prefix(new SaveGameGroup.SystemDateTimeInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None, DateTime.Now))
                    .Prefix(new SaveGameGroup.GeoscapeDateTimeInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None, geoDate))
                    .Prefix(new SaveGameGroup.PlayTimeInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None, playTime))
                    .Prefix(new SaveGameGroup.DifficultyInformationSegment(
                        SaveGameGroup.InformationSegment.InformationType.None, diffName));
                SaveGameGroup resolved = grpFull.ResolveIdentity(inject);

                SaveGame asset = new SaveGame
                {
                    name = "x2coop-probe",
                    group = resolved,
                    displayInject = new Dictionary<string, string>(),
                    version = XenonautsConstants.Versioning.VERSION,
                    versionName = XenonautsConstants.Versioning.VERSION_NAME,
                    versionDate = XenonautsConstants.Versioning.VERSION_DATE,
                    created = created,
                    artitasCommitHash = Commits.Instance.ArtitasCommitHash,
                    assetsCommitHash = Commits.Instance.AssetsCommitHash,
                    commonCommitHash = Commits.Instance.CommonCommitHash,
                    x2CoreCommitHash = Commits.Instance.X2CoreCommitHash,
                    activeContentPacks = ContentPackDependencyResolver.BuildStamps(
                        AssetReference.ContentManager.GetConfig().GetAllActiveContentPacks()
                            .Select(p => p.Manifest.Get())),
                    worldData = cmd.Data
                };

                // --- the two writes --------------------------------------------------
                string outsidePath = PathUtility.Combine(Constants.Folders.Local(), "x2coop/tmp/probe.json");
                string guestPath = PathUtility.Combine(Constants.Folders.Save(), "x2coop_guest/x2coop_probe-1.json");
                Log.Info("r8 save targets: outside=" + outsidePath + " guest=" + guestPath);

                AssetState st1 = AssetState.Failure;
                AssetState st2 = AssetState.Failure;
                string err1 = null;
                string err2 = null;
                try
                {
                    SaveGameSystem.SAVE_PROFILER.Mark("r8 Save Asset");
                    using (AssetDescriptor.EnterSaveSerialization())
                    {
                        st1 = SafeSave(asset, outsidePath, ref err1);
                        st2 = SafeSave(asset, guestPath, ref err2);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception("r8 ContentManager.Save", ex);
                }

                SaveGameSystem.SAVE_PROFILER.Mark("r8 Handle Post Save Report");
                world.HandleEvent(new PostSaveGameReport());
                SaveGameSystem.SAVE_PROFILER.Stop();

                // --- outcome ---------------------------------------------------------
                Log.Info("r8 save results: outside=" + st1 + " exists=" + FileExistsLen(outsidePath)
                    + (err1 == null ? string.Empty : " err=" + err1));
                Log.Info("r8 save results: guest=" + st2 + " exists=" + FileExistsLen(guestPath)
                    + (err2 == null ? string.Empty : " err=" + err2));

                // --- load the guest-path save -----------------------------------------
                var guestDescriptor = new FileDescriptor(guestPath, typeof(SaveGame));
                bool exists = false;
                try
                {
                    exists = AssetReference.ContentManager.Exists(guestDescriptor);
                }
                catch (Exception ex)
                {
                    Log.Exception("r8 ContentManager.Exists(guest)", ex);
                }
                Log.Info("r8 load: ContentManager.Exists(guestDescriptor)=" + exists
                    + " -> calling SaveGameSystem.LoadGame(fd, resetRNGSeed:false)");
                if (exists)
                {
                    _savedWorld = world;
                    _savedGeoDate = geoDate;
                    _pendingVerify = true;
                    VerifyTimer.Restart();
                    SaveGameSystem.LoadGame(guestDescriptor, false);
                    Log.Info("r8 load: LoadGame returned (RequestMoveTo is queued; transition is async)");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("r8 RunProbe", ex);
            }
            finally
            {
                try
                {
                    // Never leave a stale section on the shared profiler stack;
                    // the next real save does Reset() anyway, but keep it clean.
                    SaveGameSystem.SAVE_PROFILER.StopAll();
                }
                catch
                {
                }
                _running = false;
            }
        }

        /// <summary>
        /// ContentManager.Save with the exact real-path arguments
        /// (JsonPrinterSaveParameters compress:true, overwrite:true, cacheAsset:false).
        /// </summary>
        private static AssetState SafeSave(SaveGame asset, string path, ref string err)
        {
            try
            {
                return AssetReference.ContentManager.Save(asset,
                    new FileDescriptor(path, typeof(SaveGame)),
                    new JsonPrinterSaveParameters(compress: true),
                    overwrite: true,
                    cacheAsset: false);
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
                return AssetState.Failure;
            }
        }

        /// <summary>
        /// Post-load verify: the load disposes the old world and builds a new
        /// "StrategyWorld" (StrategyScreen.InitializeWorld -> StartLoadSaveGame ->
        /// DeserializeSaveGameCommand). First tick on a different world whose
        /// geoscape clock is present = load completed; compare dates.
        /// </summary>
        private static void MaybeVerify(World w)
        {
            if (!_pendingVerify)
            {
                return;
            }
            if (w == null || ReferenceEquals(w, _savedWorld) || w.Name != "StrategyWorld")
            {
                if (VerifyTimer.IsRunning && VerifyTimer.Elapsed.TotalSeconds > 90)
                {
                    _pendingVerify = false;
                    VerifyTimer.Stop();
                    Log.Warn("r8 verify: TIMEOUT - no new StrategyWorld tick within 90s of LoadGame");
                }
                return;
            }
            DateTime after = ReadGeoscapeDateTime(w, out bool geoOk);
            if (!geoOk)
            {
                return; // new world still initializing; keep waiting
            }
            _pendingVerify = false;
            VerifyTimer.Stop();
            Log.Info("r8 verify: geodate-before=" + FmtDate(_savedGeoDate)
                + " geodate-after=" + FmtDate(after)
                + " match=" + (after == _savedGeoDate)
                + " world=" + w.GetHashCode()
                + " (savedWorld=" + (_savedWorld == null ? "null" : _savedWorld.GetHashCode().ToString()) + ")");
        }

        private static DateTime ReadGeoscapeDateTime(World w, out bool ok)
        {
            ok = false;
            try
            {
                Entity clock = w?.FindTagged<GeoscapeClockTag>();
                if (clock == null)
                {
                    return default(DateTime);
                }
                // XenonautsArchetypes.Clock = All<GeoscapeClockTag>().All<GeoscapeDateTimeComponent>
                DateTime v = clock.GeoscapeDateTime().value;
                ok = true;
                return v;
            }
            catch
            {
                return default(DateTime);
            }
        }

        private static Entity SafeFindTagged<T>(World w) where T : class, ITagComponent
        {
            try
            {
                return w?.FindTagged<T>();
            }
            catch
            {
                return null;
            }
        }

        private static string SafeString(Func<string> f, string fallback)
        {
            try
            {
                return f() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static TimeSpan SafeTimeSpan(Func<TimeSpan> f)
        {
            try
            {
                return f();
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        private static Semver.SemVersion SafeSemVersion(Func<Semver.SemVersion> f)
        {
            try
            {
                return f() ?? new Semver.SemVersion(0);
            }
            catch
            {
                return new Semver.SemVersion(0);
            }
        }

        private static Dictionary<string, string> SafeInjectTable(SaveGameSystem sys)
        {
            try
            {
                return sys.GetSaveGameInjectionTable() ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private static string FileExistsLen(string path)
        {
            try
            {
                return File.Exists(path) ? "true len=" + new FileInfo(path).Length : "false";
            }
            catch (Exception ex)
            {
                return "err " + ex.GetType().Name;
            }
        }

        private static string FmtDate(DateTime dt)
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
#endif
