using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Artitas;
using Code.Screens.Common.Systems;
using Common;
using Common.Content;
using Common.Content.DataStructures;
using Common.Content.Descriptors;
using Common.Content.Managers;
using Common.DataStructures;
using Common.Input;
using Common.Util;
using HarmonyLib;
using UnityEngine;
using Xenonauts;
using Xenonauts.Common.Events;
using Xenonauts.Strategy;

namespace X2Coop.Mirror
{
    /// <summary>
    /// T3.5 save suppression (guest). While the guest is inside a co-op mirror
    /// session - <see cref="MirrorState.Mode"/> != Off, i.e. PendingActivate OR
    /// Active - every SaveGameSystem save/load entry point is prefix-blocked
    /// (return false) and logged "blocked &lt;method&gt;".
    ///
    /// Gate rationale: the brief says Active, but PendingActivate is the same
    /// hazard window - a save mid-load would serialize the dying world into
    /// Saves\ (breaking the "Saves\ diff shows no changes" invariant), and a
    /// LOCAL load landing inside the pending window is the T3.3-review bug:
    /// its StrategyWorld create would be claimed by OnWorldCreate as the mirror
    /// world -> silent divergence. Mode != Off covers both. The host's Mode is
    /// always Off, so nothing here ever gates the host's own saves.
    ///
    /// Patched methods (all on the abstract base SaveGameSystem - none are
    /// overridden by the concrete Strategy/GroundCombat/MainMenu systems):
    ///   - QueueSaveGameRequest(SaveGameRequest)        [Subscriber] - the
    ///     enqueue end of EVERY queued save (quicksave key, save menu,
    ///     autosave queue path).
    ///   - ProcessSaveGameRequest(SaveGameRequest)      [GameAPI] - the direct
    ///     save; returns Result.Fail("x2coop: guest").
    ///   - ProcessSaveGameRequests()                    (protected drain) -
    ///     covers saves queued before the session started; the queue is
    ///     CLEARED on block so the game's overlay state machine still
    ///     completes (stuck "saving" overlay would be the failure otherwise).
    ///   - RequestAutoSave(SaveGameGroup, bool, bool)   [GameAPI] - new-month /
    ///     intercept / GC autosaves.
    ///   - FlushSaveQueue(ProcessSaveGameRequestsCommand) [Subscriber] - the
    ///     save menu's drain command; blocked + queue cleared (same reason).
    ///   - HandleQuickLoadShortcut(InputReport)         (protected) - F9. Fires
    ///     on EVERY InputReport incl. the per-frame MouseHoverReport, so the
    ///     "blocked" line is logged only when the report is actually a
    ///     quickload trigger (XenonautsInputMappings.QUICK_LOAD.TestAction).
    ///   - LoadGame(LoadGameCommand)                    [Subscriber] - the
    ///     load-command path (load menu row confirm, main-menu Continue,
    ///     quickload's inner dispatch).
    ///   - LoadGame(FileDescriptor, bool)               [GameAPI] static - THE
    ///     funnel every load converges on (incl. XenonautsMain's boot
    ///     direct-load). The single exception is SaveReceiver's own mirror
    ///     load, wrapped in <see cref="ReceiverScope.InScope"/>.
    ///   - IronManHiddenCopy(FileDescriptor, string)    private static - the
    ///     ironman shadow copy (unreachable while suppressed anyway; patched
    ///     per spec - also covers a pre-latch async call still in flight).
    ///   - ContentManager.Delete(Descriptor)            [r1 review] - the
    ///     funnel for UI-driven save DELETION (load-menu delete, save-menu
    ///     overwrite confirm, F11 bug-save cleanup); gated to descriptors
    ///     under Folders.Save() while Mode != Off so suppression can't be
    ///     bypassed into destroying pre-session saves.
    ///   - NewGameParametersElement.TryAndStartNewGame1stPass() [T3.6] - the
    ///     NEW GAME start path: its overwrite-confirm ACCEPT does a RAW
    ///     Directory.Delete(campaignDir, recursive) (decompiled
    ///     NewGameParametersElement.cs:~394) that bypasses the
    ///     ContentManager.Delete funnel entirely, and the pass ends by
    ///     starting a LOCAL campaign while the session is still live.
    ///     Blocking the whole entry point while Mode != Off covers both
    ///     hazards at once; the guest's NEW GAME button simply does nothing
    ///     in-session (quit the session first).
    ///
    /// UX: silent-ish by design - no toasts, no modals; the game UI just does
    /// nothing (quicksave/menu presses consume normally, the load menu simply
    /// doesn't transition). Result.Fail only on the GameAPI path whose caller
    /// already handles a failed save.
    /// </summary>
    public static class SaveSuppress
    {
        /// <summary>Resolve + prefix every entry point. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("save-suppress: Apply called with null patcher");
                return;
            }
            Patch(patcher, "QueueSaveGameRequest", null, nameof(QueueSaveGameRequestPrefix));
            Patch(patcher, "ProcessSaveGameRequest", null, nameof(ProcessSaveGameRequestPrefix));
            Patch(patcher, "ProcessSaveGameRequests", null, nameof(ProcessSaveGameRequestsPrefix));
            Patch(patcher, "RequestAutoSave", null, nameof(RequestAutoSavePrefix));
            Patch(patcher, "FlushSaveQueue", null, nameof(FlushSaveQueuePrefix));
            Patch(patcher, "HandleQuickLoadShortcut", null, nameof(HandleQuickLoadShortcutPrefix));
            Patch(patcher, "LoadGame", new[] { typeof(LoadGameCommand) }, nameof(LoadGameCommandPrefix));
            Patch(patcher, "LoadGame", new[] { typeof(FileDescriptor), typeof(bool) }, nameof(LoadGameFilePrefix));
            Patch(patcher, "IronManHiddenCopy", null, nameof(IronManHiddenCopyPrefix));
            // T3.5-r1 (review): Saves\ DELETION also bypassed suppression -
            // AbstractLoadMenuElement.DeleteSaveGame (load-menu delete),
            // AbstractSaveMenuElement's overwrite-confirm (deletes the target
            // BEFORE raising the blocked SaveGameRequest) and
            // BugReportFileSystem.CleanupUserF11BugSave all call
            // ContentManager.Delete(Descriptor) directly. Gate that funnel to
            // Saves\ paths while Mode != Off - covers every caller at once.
            try
            {
                Type cm = typeof(ContentManager);
                MethodInfo del = AccessTools.Method(cm, "Delete", new[] { typeof(Descriptor) });
                MethodInfo pre = AccessTools.Method(typeof(SaveSuppress), nameof(ContentManagerDeletePrefix));
                if (del == null || pre == null)
                {
                    Log.Warn("save-suppress: method/prefix not found (ContentManager.Delete)");
                }
                else
                {
                    patcher.Patch(del, prefix: new HarmonyMethod(pre));
                    Log.Info("save-suppress: patch-applied " + cm.FullName + ".Delete(Descriptor)");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress apply ContentManager.Delete", ex);
            }
            // T3.6 (r4 of the T3.5 review): the new-game overwrite confirm
            // deletes the existing campaign dir via a RAW recursive
            // Directory.Delete - invisible to the ContentManager.Delete gate -
            // and the pass then starts a local campaign mid-session. Gate the
            // entry point itself while Mode != Off. Resolved by name (the
            // element lives in the menu UI namespace; a type rename in a game
            // update degrades to a warn, not a crash).
            try
            {
                Type ng = AccessTools.TypeByName("Xenonauts.MainMenu.UI.Elements.NewGameParametersElement");
                MethodInfo start = ng != null
                    ? AccessTools.Method(ng, "TryAndStartNewGame1stPass")
                    : null;
                MethodInfo pre = AccessTools.Method(typeof(SaveSuppress), nameof(NewGameStartPrefix));
                if (start == null || pre == null)
                {
                    Log.Warn("save-suppress: method/prefix not found (NewGameParametersElement.TryAndStartNewGame1stPass)");
                }
                else
                {
                    patcher.Patch(start, prefix: new HarmonyMethod(pre));
                    Log.Info("save-suppress: patch-applied " + ng.FullName + ".TryAndStartNewGame1stPass");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress apply NewGameStart", ex);
            }
        }

        private static void Patch(Harmony patcher, string name, Type[] args, string prefixName)
        {
            try
            {
                Type t = typeof(SaveGameSystem);
                MethodInfo original = args != null
                    ? AccessTools.Method(t, name, args)
                    : AccessTools.Method(t, name);
                MethodInfo prefix = AccessTools.Method(typeof(SaveSuppress), prefixName);
                if (original == null || prefix == null)
                {
                    Log.Warn("save-suppress: method/prefix not found (" + name + ")");
                    return;
                }
                patcher.Patch(original, prefix: new HarmonyMethod(prefix));
                string sig = name + "("
                    + (original.GetParameters().Length == 0 ? string.Empty : "...")
                    + ")";
                Log.Info("save-suppress: patch-applied " + t.FullName + "." + sig);
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress apply " + name, ex);
            }
        }

        // ------------------------------------------------------------------
        // prefixes - all fail OPEN on exception (a throwing prefix would break
        // the game's save pipeline for the host/local player too)
        // ------------------------------------------------------------------

        /// <summary>Enqueue end of every queued save (quicksave key, save menu, queued autosave).</summary>
        private static bool QueueSaveGameRequestPrefix()
        {
            try
            {
                if (Blocked("QueueSaveGameRequest")) { return false; }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress queue-request", ex);
            }
            return true;
        }

        /// <summary>The direct GameAPI save - returns Result.Fail("x2coop: guest").</summary>
        private static bool ProcessSaveGameRequestPrefix(ref Result __result)
        {
            try
            {
                if (MirrorState.Mode != MirrorMode.Off)
                {
                    __result = Result.Fail("x2coop: guest");
                    Log.Info("blocked ProcessSaveGameRequest (guest in-session, mode="
                        + MirrorState.Mode + ") -> Result.Fail");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress process-request", ex);
            }
            return true;
        }

        /// <summary>
        /// The queue drain (UpdateSaveGameRequestQueue + FlushSaveQueue funnel).
        /// Blocked AND cleared: a request queued before Mode left Off must not
        /// write mid-session, and an empty queue lets the game's
        /// processingQueuedRequests/SavingOverlay state machine settle.
        /// </summary>
        private static bool ProcessSaveGameRequestsPrefix(SaveGameSystem __instance)
        {
            try
            {
                if (MirrorState.Mode != MirrorMode.Off)
                {
                    int dropped = ClearQueue(__instance);
                    Log.Info("blocked ProcessSaveGameRequests (guest in-session, mode="
                        + MirrorState.Mode + ")"
                        + (dropped > 0 ? " - dropped " + dropped + " queued" : string.Empty));
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress drain", ex);
            }
            return true;
        }

        /// <summary>[GameAPI] autosave (new-month / intercept / GC wrappers).</summary>
        private static bool RequestAutoSavePrefix()
        {
            try
            {
                if (Blocked("RequestAutoSave")) { return false; }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress autosave", ex);
            }
            return true;
        }

        /// <summary>The save menu's "drain now" command - drop, don't drain.</summary>
        private static bool FlushSaveQueuePrefix(SaveGameSystem __instance)
        {
            try
            {
                if (MirrorState.Mode != MirrorMode.Off)
                {
                    int dropped = ClearQueue(__instance);
                    Log.Info("blocked FlushSaveQueue (guest in-session, mode="
                        + MirrorState.Mode + ")"
                        + (dropped > 0 ? " - dropped " + dropped + " queued" : string.Empty));
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress flush", ex);
            }
            return true;
        }

        /// <summary>
        /// F9 quickload. Fires per InputReport (incl. per-frame hover), so the
        /// block is silent unless the report is actually the quickload trigger.
        /// </summary>
        private static bool HandleQuickLoadShortcutPrefix(InputReport report)
        {
            try
            {
                if (MirrorState.Mode != MirrorMode.Off)
                {
                    if (XenonautsInputMappings.QUICK_LOAD != null
                        && XenonautsInputMappings.QUICK_LOAD.TestAction(report))
                    {
                        Log.Info("blocked HandleQuickLoadShortcut (guest in-session, mode="
                            + MirrorState.Mode + ")");
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress quickload", ex);
            }
            return true;
        }

        /// <summary>Instance [Subscriber] LoadGameCommand -> static LoadGame; cut it here.</summary>
        private static bool LoadGameCommandPrefix(LoadGameCommand command)
        {
            try
            {
                if (MirrorState.Mode != MirrorMode.Off)
                {
                    Log.Info("blocked LoadGame (guest in-session, mode=" + MirrorState.Mode
                        + ", via LoadGameCommand path="
                        + (command != null && command.SaveGameDescriptor != null
                            ? command.SaveGameDescriptor.path : "?") + ")");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress loadgame-cmd", ex);
            }
            return true;
        }

        /// <summary>
        /// Static LoadGame(FileDescriptor, bool) - the funnel for ALL loads.
        /// The ReceiverScope flag lets SaveReceiver's own mirror load through;
        /// every other caller while Mode != Off is a guest-local load that must
        /// not spawn a foreign StrategyWorld inside the session window.
        /// </summary>
        private static bool LoadGameFilePrefix(FileDescriptor saveGameDescriptor, bool resetRNGSeed)
        {
            try
            {
                if (ReceiverScope.InScope)
                {
                    return true;
                }
                if (MirrorState.Mode != MirrorMode.Off)
                {
                    Log.Info("blocked LoadGame (guest in-session, mode=" + MirrorState.Mode
                        + ", via FileDescriptor path="
                        + (saveGameDescriptor != null ? saveGameDescriptor.path : "?") + ")");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress loadgame-file", ex);
            }
            return true;
        }

        /// <summary>Private static ironman shadow copy (AsyncFireAndForget thread).</summary>
        private static bool IronManHiddenCopyPrefix()
        {
            try
            {
                if (Blocked("IronManHiddenCopy")) { return false; }
            }
            catch (Exception ex)
            {
                // Reads MirrorState from a worker thread - still just a static read.
                Log.Exception("save-suppress ironman", ex);
            }
            return true;
        }

        /// <summary>
        /// The funnel for UI-driven save DELETION: the load-menu delete path
        /// (AbstractLoadMenuElement.DeleteSaveGame), the save-menu overwrite
        /// confirm (deletes the target before raising the blocked
        /// SaveGameRequest - the nastiest: the file is gone and no save ever
        /// replaces it) and CleanupUserF11BugSave all call
        /// ContentManager.Delete(Descriptor) directly, which none of the
        /// SaveGameSystem prefixes see.
        ///
        /// Scoped narrowly: only fires while Mode != Off AND the descriptor's
        /// path resolves strictly inside Folders.Save(); every other delete
        /// (asset refs, non-save files, host/local play) is untouched. Our own
        /// cleanup never goes through here (Teardown.DeleteGuestSaveDir uses
        /// Directory.Delete, and it runs after Mode has already dropped to
        /// Off anyway) - no carve-out needed.
        /// </summary>
        private static bool ContentManagerDeletePrefix(Descriptor descriptor)
        {
            try
            {
                if (MirrorState.Mode == MirrorMode.Off) { return true; }
                string path = DescriptorPath(descriptor);
                if (path == null || !UnderSaveRoot(path)) { return true; }
                Log.Info("blocked ContentManager.Delete (guest in-session, mode="
                    + MirrorState.Mode + ", path=" + path + ")");
                return false;
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress cm-delete", ex);
            }
            return true;
        }

        /// <summary>
        /// T3.6: the NEW GAME start pass - while the guest is in-session
        /// (Mode != Off, incl. Detached) it would (a) raw-delete the named
        /// campaign dir on overwrite-confirm, bypassing the ContentManager
        /// funnel, and (b) start a local campaign the mirror machinery would
        /// then fight. Block silently-ish like every other entry point.
        /// </summary>
        private static bool NewGameStartPrefix()
        {
            try
            {
                if (MirrorState.Mode != MirrorMode.Off)
                {
                    Log.Info("blocked TryAndStartNewGame1stPass (guest in-session, mode="
                        + MirrorState.Mode + " - raw campaign-delete path)");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress new-game", ex);
            }
            return true;
        }

        /// <summary>Best-effort absolute path for a Descriptor (FileDescriptor.path, else resolved FileHandle).</summary>
        private static string DescriptorPath(Descriptor d)
        {
            try
            {
                FileDescriptor fd = d as FileDescriptor;
                if (fd != null) { return fd.path; }
                FileHandle fh = d != null ? d.FileHandle : null;
                return fh != null ? fh.AbsolutePath : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when path normalizes strictly inside Folders.Save() (same guard shape as Teardown.DeleteDirInside).</summary>
        private static bool UnderSaveRoot(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) { return false; }
                string full = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string root = Path.GetFullPath(Constants.Folders.Save())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return full.Length > root.Length
                    && full.StartsWith(root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false; // unparseable path is not a saves-file delete - allow
            }
        }

        // ------------------------------------------------------------------

        /// <summary>Gate + log. Shared by every save-side prefix.</summary>
        private static bool Blocked(string method)
        {
            if (MirrorState.Mode == MirrorMode.Off) { return false; }
            Log.Info("blocked " + method + " (guest in-session, mode=" + MirrorState.Mode + ")");
            return true;
        }

        /// <summary>
        /// Drop the queued save requests without running the drain; returns
        /// how many were dropped (-1 when the field could not be reached -
        /// still blocked, just not cleared).
        /// </summary>
        private static int ClearQueue(SaveGameSystem sys)
        {
            try
            {
                if (_requestsRef == null)
                {
                    _requestsRef = AccessTools.FieldRefAccess<SaveGameSystem, List<SaveGameRequest>>("requests");
                }
                List<SaveGameRequest> q = _requestsRef(sys);
                if (q == null) { return -1; }
                int n = q.Count;
                q.Clear();
                return n;
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress clear-queue", ex);
                return -1;
            }
        }

        private static AccessTools.FieldRef<SaveGameSystem, List<SaveGameRequest>> _requestsRef;

        // ------------------------------------------------------------------
        // Dev verify knob: -x2coop.probeSaveSuppress=1 fires every suppressed
        // entry point once against the mirror world's SaveGameSystem (called
        // from SaveReceiver.Activate, right after Mode=Active). Each call must
        // land in its prefix -> one "blocked <method>" line per path, and the
        // ProcessSaveGameRequest call must come back Result.Fail. The fake
        // FileDescriptor keeps the load probes harmless even if a patch were
        // missing (LoadGame on a nonexistent path just warns + returns); the
        // save probes would write a real file on a regression - exactly what
        // the verify's Saves\ diff exists to catch. IronManHiddenCopy is the
        // one path NOT probed (its fallback would create the hidden ironman
        // dir and run a delete-limit pass); it is patched but unreachable
        // while saves are suppressed.
        // ------------------------------------------------------------------

        /// <summary>Fire every suppressed entry point once. Dev-only (x2coop.probeSaveSuppress).</summary>
        internal static void DevProbe(World w)
        {
            try
            {
                if (w == null) { Log.Warn("save-suppress probe: null world"); return; }
                SaveGameSystem sys = w.GetSystem<SaveGameSystem>();
                if (sys == null)
                {
                    Log.Warn("save-suppress probe: no SaveGameSystem on world");
                    return;
                }
                Log.Info("save-suppress probe: firing every suppressed path (expect blocked lines)");

                var fake = new FileDescriptor("x2coop-suppress-probe-nonexistent.json",
                    typeof(SaveGame));

                try { sys.RequestAutoSave(StrategyConstants.SaveGameGroups.AUTO_USER); }
                catch (Exception ex) { Log.Exception("probe RequestAutoSave", ex); }

                try
                {
                    Result r = sys.ProcessSaveGameRequest(
                        new SaveGameRequest(StrategyConstants.SaveGameGroups.AUTO_USER));
                    Log.Info("save-suppress probe: ProcessSaveGameRequest -> failed="
                        + r.Failed + " msg=\"" + r.FormattedMessages + "\"");
                }
                catch (Exception ex) { Log.Exception("probe ProcessSaveGameRequest", ex); }

                try { w.HandleEvent(new SaveGameRequest(StrategyConstants.SaveGameGroups.QUICK)); }
                catch (Exception ex) { Log.Exception("probe SaveGameRequest", ex); }

                try { w.HandleEvent(new ProcessSaveGameRequestsCommand()); }
                catch (Exception ex) { Log.Exception("probe FlushSaveQueue", ex); }

                try { w.HandleEvent(new LoadGameCommand(fake, true)); }
                catch (Exception ex) { Log.Exception("probe LoadGameCommand", ex); }

                try { SaveGameSystem.LoadGame(fake, false); }
                catch (Exception ex) { Log.Exception("probe LoadGame(FileDescriptor)", ex); }

                // The drain is protected - invoke via reflection; the prefix
                // still applies (covers pre-session queued saves).
                try
                {
                    MethodInfo drain = AccessTools.Method(typeof(SaveGameSystem), "ProcessSaveGameRequests");
                    if (drain != null) { drain.Invoke(sys, null); }
                }
                catch (Exception ex) { Log.Exception("probe ProcessSaveGameRequests", ex); }

                // HandleQuickLoadShortcut is protected - same trick with a
                // synthetic F9-down report (a real quickload keypress, minus
                // the keyboard).
                try
                {
                    MethodInfo hql = AccessTools.Method(typeof(SaveGameSystem), "HandleQuickLoadShortcut");
                    if (hql != null)
                    {
                        var rep = new KeyboardCodeStateReport(Vector2.zero,
                            Common.Input.DataStructures.KeyState.Down,
                            KeyCode.F9, Common.Input.DataStructures.Modifiers.None);
                        hql.Invoke(sys, new object[] { rep });
                    }
                }
                catch (Exception ex) { Log.Exception("probe HandleQuickLoadShortcut", ex); }

                // IronManHiddenCopy is deliberately NOT probed: even with the
                // fake path it would (on a regression) still create the hidden
                // ironman dir and run LimitIronManSaves - a file-DELETE path.
                // Its prefix exists for spec completeness; unreachable while
                // the save paths are blocked.

                // T3.5-r1: ContentManager.Delete guard - a sacrificial file
                // under Saves\ sent through the REAL delete entry point must
                // survive (the UI delete paths all converge on it). The probe
                // creates + removes the file itself via raw File IO (not
                // gated), so the Saves\ diff stays clean either way; a
                // regression shows up as stillExists=False.
                string probeFile = null;
                try
                {
                    probeFile = PathUtility.Combine(Constants.Folders.Save(),
                        "x2coop-suppress-probe-delete.json");
                    File.WriteAllText(probeFile, "{}");
                    AssetReference.ContentManager.Delete(
                        new FileDescriptor(probeFile, typeof(SaveGame)));
                    Log.Info("save-suppress probe: ContentManager.Delete -> stillExists="
                        + File.Exists(probeFile));
                }
                catch (Exception ex) { Log.Exception("probe ContentManager.Delete", ex); }
                finally
                {
                    if (probeFile != null)
                    {
                        try { File.Delete(probeFile); } catch { }
                    }
                }

                Log.Info("save-suppress probe: done");
            }
            catch (Exception ex)
            {
                Log.Exception("save-suppress probe", ex);
            }
        }
    }
}
