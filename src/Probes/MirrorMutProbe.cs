#if X2COOP_PROBES
using System;
using Artitas;
using Artitas.Components;
using HarmonyLib;
using X2Coop.Mirror;

namespace X2Coop.Probes
{
    /// <summary>
    /// T3.4 mirror-mutation counter - Debug builds only (X2COOP_PROBES).
    ///
    /// The T4.1 mutation firewall is not yet active, so this probe measures
    /// how much traffic the guest's mirror world still generates while its
    /// clock is frozen: a postfix on World.ApplyComponentMutation counts
    /// every mutation that lands on the bound mirror world (resolves via
    /// SaveReceiver.MirrorWorld - under MirrorMode.Active, or PendingActivate
    /// once the world is bound (T3.6 - populate mutations count too).
    /// SaveReceiver's "mirror-muts n=..." heartbeat line reports the delta
    /// since the last heartbeat plus the running total.
    ///
    /// Expected non-zero: the ClockFreeze prefix only skips
    /// TimeTransitionSystem.UpdateTimeOnBase - the rest of the DeltaTimeEvent
    /// pipeline, UI-driven commands and other systems still mutate the world.
    /// </summary>
    internal static class MirrorMutProbe
    {
        private static long _delta;
        private static long _total;

        /// <summary>Total mutations counted on mirror worlds since Reset().</summary>
        public static long Total => _total;

        /// <summary>Mutations since the previous TakeDelta call (heartbeat window).</summary>
        public static long TakeDelta()
        {
            long d = _delta;
            _delta = 0;
            return d;
        }

        /// <summary>T2.6 pack teardown: drop the counters (no object refs held).</summary>
        public static void Reset()
        {
            _delta = 0;
            _total = 0;
        }

        /// <summary>Postfix World.ApplyComponentMutation. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("mirror-muts: Apply called with null patcher");
                return;
            }
            try
            {
                var original = AccessTools.Method(typeof(World), "ApplyComponentMutation",
                    new[] { typeof(int), typeof(int), typeof(IComponent), typeof(IComponent), typeof(bool) });
                var postfix = AccessTools.Method(typeof(MirrorMutProbe), nameof(MutPostfix));
                if (original == null || postfix == null)
                {
                    Log.Warn("mirror-muts: method/postfix not found (World.ApplyComponentMutation)");
                    return;
                }
                patcher.Patch(original, postfix: new HarmonyMethod(postfix));
                Log.Info("mirror-muts: patch-applied Artitas.World.ApplyComponentMutation");
            }
            catch (Exception ex)
            {
                Log.Exception("mirror-muts apply", ex);
            }
        }

        /// <summary>
        /// Count a mutation iff it lands on the guest's bound mirror world.
        /// Cheap: two flag checks + a WeakReference resolve per call (R9's
        /// MutPostfix on the same method already tolerates this cadence).
        /// </summary>
        private static void MutPostfix(World __instance)
        {
            try
            {
                if (__instance == null || !MirrorState.Active) { return; }
                if (!ReferenceEquals(__instance, Session.SaveReceiver.MirrorWorld)) { return; }
                _delta++;
                _total++;
            }
            catch (Exception ex)
            {
                Log.Exception("mirror-muts postfix", ex);
            }
        }
    }
}
#endif
