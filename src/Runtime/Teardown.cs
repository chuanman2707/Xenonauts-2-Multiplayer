using System;
using System.IO;

namespace X2Coop.Runtime
{
    /// <summary>
    /// T2.6 centralized pack teardown. Runs from X2CoopLifecycle.Destroy
    /// (content-pack hot-toggle: the game calls Destroy + Harmony.UnpatchAll
    /// itself - we never unpatch) and from Runner.OnApplicationQuit (the game
    /// skips lifecycle methods at shutdown - MODLOG T2.1 note).
    ///
    /// Order:
    ///   1. Runner.Shutdown - session detach sends HostEnded while the socket
    ///      is still alive, a ~300 ms flush sleep (T2.5 rule; the deferred
    ///      SessionHost.Tick stop can never fire - the runner dies inside the
    ///      frame), then NetManager.Stop, runner GameObject destroy and
    ///      Runner.Instance cleared.
    ///   2. Static session/UI/mirror state reset. R3 proved the pack assembly
    ///      stays cached on re-enable (same Assembly object, same statics) -
    ///      without this sweep a re-enabled pack would inherit a dead session.
    ///   3. Profile scratch dirs deleted (guarded - see CleanupProfileDirs).
    ///
    /// Static-state audit (kept deliberately, with reasons):
    ///   - Session.ScreenGuard._strategy: WeakReference to the game's
    ///     StrategyWorld, fed ONLY by OnWorldCreate/OnWorldDispose - which fire
    ///     on world create/dispose, not on pack toggle. Clearing it would
    ///     reject every join until the next campaign load after a hot-toggle
    ///     mid-campaign. Self-heals via World.Disposed + lifecycle Forget.
    ///   - Log._writer: the per-PID append log is process state, not pack
    ///     state; the file belongs to this process for its whole lifetime.
    ///   - NetDebug.Logger (our LiteNetLib sink): the assembly stays loaded
    ///     either way; re-enable installs it again via the new NetTransport.
    ///   - CoopPanel._winRect: window drag position, cosmetic persistence.
    ///   - Probe accumulators keep their recon history; the strong refs to
    ///     game objects inside them (incl. ProbePatches' _timeSys / WorldsSeen
    ///     / ExclIdByWorld - the R5/R6 census probe, and
    ///     ReplicationVolumeProbe's TypeRecs/FrameRecs/FrameSet/
    ///     _curFrameCounts - IComponent samples) are reset below (Debug builds
    ///     only).
    ///   - R3AttrProbePatch._hit (ReconProbes.cs): plain-bool once-latch for
    ///     the r3-3 attribute-patch log line - recon history, no object refs;
    ///     kept so re-enable does not re-log the line.
    /// </summary>
    public static class Teardown
    {
        /// <summary>
        /// Full pack teardown. Idempotent: every step is guarded and the
        /// runner's TeardownNow runs at most once per component.
        /// </summary>
        public static void Run()
        {
            // 1. Runner + transport + every session/panel static that hangs
            //    off the Runner's event streams (SessionHost/SessionClient/
            //    CoopPanel Detach clear their own fields).
            try { Runner.Shutdown(); }
            catch (Exception ex) { Log.Exception("teardown runner", ex); }

            // 2. Detached-from-runner statics.
            try { Mirror.MirrorState.Reset(); }
            catch (Exception ex) { Log.Exception("teardown mirrorstate", ex); }
            try { Mirror.ReceiverScope.InScope = false; } // T3.5: paranoia - the finally can't leak it, but never trust a torn-down thread
            catch (Exception ex) { Log.Exception("teardown receiverscope", ex); }
            try { Replication.StateApplier.Reset(); } // T4.1/T5.3: same paranoia for the applier scope
            catch (Exception ex) { Log.Exception("teardown stateapplier", ex); }
            try { Replication.ReplicationSource.Reset(); } // T5.1: drop pending capture ops + bound-world cache (live IComponent refs)
            catch (Exception ex) { Log.Exception("teardown replicationsource", ex); }
            try { Replication.DeltaSender.Reset("teardown"); } // T5.2 host: pending ops/join buffer/serializer binding die here
            catch (Exception ex) { Log.Exception("teardown deltasender", ex); }
            try { Replication.DeltaSink.Reset(); } // T5.2 guest: rx counters + seq watermarks
            catch (Exception ex) { Log.Exception("teardown deltasink", ex); }
            try { Replication.WorldHash.Reset("teardown"); } // T5.5: in-flight reqs + guest hash serializer
            catch (Exception ex) { Log.Exception("teardown worldhash", ex); }
            try { Mirror.MirrorFirewall.Shutdown(); } // T4.1: detach the logMessageReceived tap + reset counters
            catch (Exception ex) { Log.Exception("teardown mirrorfirewall", ex); }
            try { Mirror.MirrorSystems.Reset(); } // T5.2-r1: disarm counters + first-skip log set (no hooks to detach)
            catch (Exception ex) { Log.Exception("teardown mirrorsystems", ex); }
            try { Mirror.UIPickGate.Reset(); } // T4.2-r1: session counters - no external hooks to detach
            catch (Exception ex) { Log.Exception("teardown uipickgate", ex); }
            try { DebugSettings.Reset(); }
            catch (Exception ex) { Log.Exception("teardown devsettings", ex); }
#if X2COOP_PROBES
            try { Probes.MutationDelayProbe.Reset(); }
            catch (Exception ex) { Log.Exception("teardown r10-probe", ex); }
            try { Probes.SavePathProbe.Reset(); }
            catch (Exception ex) { Log.Exception("teardown r8-probe", ex); }
            try { Probes.ReplicationVolumeProbe.Reset(); }
            catch (Exception ex) { Log.Exception("teardown r9-probe", ex); }
            try { Probes.ProbePatches.Reset(); }
            catch (Exception ex) { Log.Exception("teardown r5-probe", ex); }
            try { Probes.MirrorMutProbe.Reset(); }
            catch (Exception ex) { Log.Exception("teardown mirror-muts", ex); }
#endif

            // 3. Profile scratch dirs.
            try { CleanupProfileDirs(); }
            catch (Exception ex) { Log.Exception("teardown dirs", ex); }

            Log.Info("teardown: complete");
        }

        /// <summary>
        /// Delete the co-op scratch dirs (spec T2.6):
        ///   Saves\x2coop_guest\   - guest-side save-stream target (R8 path)
        ///   $PROF\x2coop\tmp\     - session tmp files (T3.2 writes here;
        ///                         the x2coop parent dir itself is kept)
        /// Paths come from the game's own resolvers - Folders.Save() =
        /// $PROF\Saves and Folders.Local() = $PROF - so a -localGameFolder /
        /// -gameSaveFolder override is honoured automatically.
        /// SAFETY: a delete runs only when the GetFullPath-normalized target
        /// resolves STRICTLY inside its normalized guard root (saveRoot for
        /// x2coop_guest, profRoot for x2coop\tmp) AND ends with the expected
        /// leaf - a broken folder resolver can never escalate into deleting
        /// an unrelated directory.
        /// </summary>
        private static void CleanupProfileDirs()
        {
            string profRoot = SafeFolder("Local");
            if (string.IsNullOrEmpty(profRoot))
            {
                Log.Warn("teardown: profile root unresolved - dir cleanup skipped");
                return;
            }
            DeleteGuestSaveDir();
            DeleteDirInside(Path.Combine(profRoot, "x2coop", "tmp"), profRoot,
                "x2coop" + Path.DirectorySeparatorChar + "tmp", "x2coop\\tmp");
        }

        /// <summary>
        /// Delete Saves\x2coop_guest\ alone (T3.3: also called by
        /// SaveReceiver.Reset on guest session end - spec: the guest dir is
        /// session-scoped). Same guard as CleanupProfileDirs: the save root
        /// (Folders.Save) can sit OUTSIDE the profile root under a
        /// -gameSaveFolder override, so the target is guarded against
        /// saveRoot, not profRoot.
        /// </summary>
        internal static void DeleteGuestSaveDir()
        {
            string saveRoot = SafeFolder("Save");
            if (string.IsNullOrEmpty(saveRoot))
            {
                string profRoot = SafeFolder("Local");
                if (string.IsNullOrEmpty(profRoot))
                {
                    Log.Warn("teardown: save/profile root unresolved - guest-dir cleanup skipped");
                    return;
                }
                saveRoot = Path.Combine(profRoot, "Saves");
            }
            DeleteDirInside(Path.Combine(saveRoot, "x2coop_guest"), saveRoot,
                "x2coop_guest", "Saves\\x2coop_guest");
        }

        private static string SafeFolder(string which)
        {
            try
            {
                return which == "Save"
                    ? global::Common.Constants.Folders.Save()
                    : global::Common.Constants.Folders.Local();
            }
            catch (Exception ex)
            {
                Log.Exception("teardown Folders." + which, ex);
                return null;
            }
        }

        private static void DeleteDirInside(string dir, string guardRoot,
            string expectedTail, string label)
        {
            try
            {
                string full = Path.GetFullPath(dir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string root = Path.GetFullPath(guardRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                bool inside = full.Length > root.Length
                    && full.StartsWith(root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
                bool tailOk = full.EndsWith(
                    Path.DirectorySeparatorChar + expectedTail,
                    StringComparison.OrdinalIgnoreCase);
                if (!inside || !tailOk)
                {
                    Log.Warn("teardown: REFUSED delete " + label + " - \"" + full
                        + "\" fails guard (root=\"" + root + "\" tail=\"" + expectedTail + "\")");
                    return;
                }
                if (!Directory.Exists(full))
                {
                    Log.Info("teardown: " + label + " absent");
                    return;
                }
                Directory.Delete(full, recursive: true);
                Log.Info("teardown: deleted " + label + " path=" + full);
            }
            catch (Exception ex)
            {
                Log.Exception("teardown delete " + label, ex);
            }
        }
    }
}
