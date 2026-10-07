using System;
using Artitas;
using Artitas.Events;
using HarmonyLib;
using Xenonauts.Strategy.Systems;

namespace X2Coop.Mirror
{
    /// <summary>
    /// Clock freeze (T3.2 shipped the host half; T3.4 added the guest half) - a
    /// PREFIX on
    /// Xenonauts.Strategy.Systems.TimeTransitionSystem.UpdateTimeOnBase(
    /// DeltaTimeEvent), the [Subscriber(Priority=int.MaxValue)] that alone
    /// advances GeoscapeDateTime and drives the per-tick World.Process loop
    /// (TimeTransitionSystem.cs:117-169).
    ///
    /// Returning false skips the whole method: the real-time accumulator never
    /// grows, so the geoscape clock cannot advance and its schedulers/tick
    /// reports never fire - while the frame-level world pipeline that
    /// dispatched DeltaTimeEvent keeps running (UI stays responsive). This is
    /// deliberately NOT the game's pause counter (spec r2 C4:
    /// TimeControlCommand.Resume() fires from many places and could un-pause
    /// behind our back; a skipped UpdateTimeOnBase cannot). A host clicking
    /// Play mid-join therefore sets a speed the tick loop simply never sees
    /// until the freeze lifts.
    ///
    /// Gates:
    ///   - <see cref="MirrorState.HostJoinFreeze"/> (host): set on HelloAck
    ///     accept, released exactly once at GuestReady or abort/disconnect
    ///     (T3.2 SaveStreamer lifecycle).
    ///   - T3.4 (guest): MirrorState.Active AND __instance.World is the
    ///     guest's bound mirror Strategy world. T3.6 widened the resolve to
    ///     PendingActivate too (the mirror world is bound at OnWorldCreate but
    ///     activates only after populate - freezing it at bind time removes
    ///     the load-window drift so the resync clock matches the serialized
    ///     geodate exactly). The outgoing world is never the bound mirror, so
    ///     a guest-local campaign (Mode == Off) and any non-mirror world are
    ///     never frozen; on session end Mode -> Off re-opens the gate and the
    ///     (now local) clock resumes on its own.
    ///
    /// Coexists with the probe postfixes on the same method (ProbePatches
    /// TimePostfix, SavePathProbe KeyPollPostfix - Harmony runs postfixes even
    /// when a prefix skips the original, which is harmless for probes). Not
    /// probe-gated: this ships in Release too.
    /// </summary>
    public static class ClockFreeze
    {
        /// <summary>Resolve + prefix UpdateTimeOnBase. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("clock-freeze: Apply called with null patcher");
                return;
            }
            try
            {
                var original = AccessTools.Method(typeof(TimeTransitionSystem), "UpdateTimeOnBase");
                var prefix = AccessTools.Method(typeof(ClockFreeze), nameof(UpdateTimeOnBasePrefix));
                if (original == null || prefix == null)
                {
                    Log.Warn("clock-freeze: method/prefix not found (UpdateTimeOnBase)");
                    return;
                }
                patcher.Patch(original, prefix: new HarmonyMethod(prefix));
                Log.Info("clock-freeze: patch-applied Xenonauts.Strategy.Systems.TimeTransitionSystem.UpdateTimeOnBase");
            }
            catch (Exception ex)
            {
                Log.Exception("clock-freeze apply", ex);
            }
        }

        /// <summary>
        /// Prefix: skip the geoscape tick while the host is mid-join
        /// (<see cref="MirrorState.HostJoinFreeze"/>) or while this
        /// TimeTransitionSystem belongs to the guest's bound mirror world
        /// (<see cref="Session.SaveReceiver.MirrorWorld"/> - resolves under
        /// MirrorMode.Active or PendingActivate-with-bound-world, T3.6).
        /// Per-system gating: the patch fires for
        /// every world's UpdateTimeOnBase, so the guest half MUST compare
        /// world identity - anything else would freeze the host's world too.
        /// </summary>
        private static bool UpdateTimeOnBasePrefix(TimeTransitionSystem __instance, DeltaTimeEvent __0)
        {
            try
            {
                if (MirrorState.HostJoinFreeze)
                {
                    return false;
                }
                World mirror = Session.SaveReceiver.MirrorWorld;
                if (mirror != null && ReferenceEquals(__instance.World, mirror))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                // A throw inside a Harmony prefix propagates into the game's
                // event dispatch - never let it (freeze fails OPEN, logged).
                Log.Exception("clock-freeze prefix", ex);
            }
            return true;
        }
    }
}
