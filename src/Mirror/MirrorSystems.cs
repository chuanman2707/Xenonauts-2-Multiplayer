using System;
using System.Collections.Generic;
using System.Reflection;
using Artitas;
using Artitas.Systems;
using HarmonyLib;
using Xenonauts.Common.Cleanup;
using Xenonauts.Strategy.Systems;
using Xenonauts.Strategy.Systems.Base;

namespace X2Coop.Mirror
{
    /// <summary>
    /// T5.2-r1 (review C1) - disarm guest-local cleanup/convergence passes on
    /// the LIVE mirror world. The T5.2 live verify showed the real wedge the
    /// earlier "stalled pump" theory never was: ~10 ms after "mirror active"
    /// the guest's strategy CleanupSystem entered CleanupMarkedEntities on a
    /// save that carried MarkedForCleanupGroup/MarkedForDeletionGroup members
    /// (entity 777 then 784/859/870), the firewall blocked the marker-leave /
    /// delete-joint mutations (serializable types, not whitelisted), the
    /// entity never left the family and the unbounded
    /// "while (t.Count > 0) { ...; t.Clear(); t.AddRange(_family); }" rescan
    /// (CleanupSystem.cs:45-88) never terminated - the Unity main thread
    /// wedged inside one LateUpdateEvent dispatch, starving Runner's net
    /// pump while LiteNetLib's socket thread kept answering pings.
    ///
    /// Fix = reviewer option (a): skip the pass on the mirror. While
    /// <see cref="MirrorState.Active"/> the mirror's world state is
    /// host-authoritative (T5.3 applies the host's deletes itself), so the
    /// guest-local cleanup/deletion passes have no semantics to preserve -
    /// the marked entities simply stay as inert zombies for the session's
    /// life instead of corrupting it. Everything fails OPEN: a gate throw
    /// logs once and lets the original run.
    ///
    /// Four prefixes, all gated "Active AND __instance.World IS the bound
    /// MirrorWorld" (PendingActivate stays UNSKIPPED on purpose: the firewall
    /// does not block there, so post-load convergence still runs - matching
    /// the host's own pre-save cleanup):
    ///
    ///   - CleanupSystem.CleanupMarkedEntities (private, abstract base -
    ///     covers StrategyCleanupSystem/GCCleanupSystem/ACCleanupSystem; only
    ///     the strategy mirror exists in practice). THE wedge site.
    ///   - EntityDeletionSystem.DeleteMarkedEntities (public) - bounded
    ///     foreach, NOT a wedge, but every pass re-queued blocked deletes;
    ///     skipping kills the churn too.
    ///   - SignpostSystem.Resolve() (private no-arg worker - covers the
    ///     DeltaTimeEvent + ClockStepReport subscribers): same wedge shape -
    ///     "while (_resolve) { do { flag } while (flag) }" drives
    ///     TriggerUnlockedStateMachine mutations the firewall blocks while
    ///     Active, so flag would stay true forever once a _resolve latch was
    ///     set (PostDeSerializationEvent arms it during populate; the latch
    ///     can also land post-activation via a host-applied prereq change in
    ///     T5.3). Unreached while Active = unreachable wedge.
    ///   - StrategyXenopediaSystem.Resolve() (private no-arg worker - covers
    ///     both subscribers + UnlockXenopediaEntries' do..while(flag)).
    ///
    ///   Audited and deliberately NOT patched (same sweep over the decompile):
    ///   ProjectSystem.ResolveProjects re-arms forceResolveProjects only via
    ///   successful family mutation callbacks - a blocked mutation cannot
    ///   re-arm the latch, so it exits after one pass; ItemRequestSystem's
    ///   while-loop refills "Except(t)" (processed entities never re-enter -
    ///   self-bounding); TimelineSystem's do..while(flag) only runs inside
    ///   ClockStepReport, which only fires inside UpdateTimeOnBase - already
    ///   skipped wholesale by ClockFreeze on the mirror (if that patch is
    ///   ever removed this audit re-opens); TimeTransitionSystem's
    ///   do..while(World.Process()) sits inside the same skipped method;
    ///   WorldManagedScreen.Update's outer drain is bounded by the
    ///   event-queue snapshot count (DefaultProcessStrategy.Process copies
    ///   the count); PhasedFSMSystem is time-budgeted, DelayedTaskSystem /
    ///   ObjectLabelSystem / MissionSystem / DialogSystem / TooltipSystem /
    ///   UpliftSystem / UIStateSystem / StrategyInventorySystem are bounded
    ///   index/chain/counter walks, RegionSystem's flood fill is set-bounded,
    ///   BugReport*/SaveGameSystem loops carry count/retry caps, and the
    ///   GC/AC while-loops live on worlds a strategy mirror never has.
    ///
    /// Counters + first-skip log lines are the live evidence ("mirror-systems:
    /// disarmed &lt;site&gt;" at first engagement, per-session counts in the
    /// firewall summary). Not probe-gated: ships in Release.
    /// </summary>
    public static class MirrorSystems
    {
        /// <summary>CleanupSystem.CleanupMarkedEntities passes skipped on the mirror this session.</summary>
        public static long SkippedCleanup;
        /// <summary>EntityDeletionSystem.DeleteMarkedEntities passes skipped.</summary>
        public static long SkippedDeletes;
        /// <summary>Signpost/Xenopedia Resolve() passes skipped.</summary>
        public static long SkippedResolves;

        /// <summary>Total skips - appended to MirrorFirewall.Summary() for scripted verify.</summary>
        public static long SkippedTotal => SkippedCleanup + SkippedDeletes + SkippedResolves;

        /// <summary>One "disarmed" log line per site per session (the counter keeps counting).</summary>
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Resolve targets + install. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("mirror-systems: Apply called with null patcher");
                return;
            }
            PatchOn(patcher, typeof(CleanupSystem), "CleanupMarkedEntities",
                null, nameof(CleanupPrefix));
            PatchOn(patcher, typeof(EntityDeletionSystem), "DeleteMarkedEntities",
                Type.EmptyTypes, nameof(DeletionPrefix));
            PatchOn(patcher, typeof(SignpostSystem), "Resolve",
                Type.EmptyTypes, nameof(SignpostResolvePrefix));
            PatchOn(patcher, typeof(StrategyXenopediaSystem), "Resolve",
                Type.EmptyTypes, nameof(XenopediaResolvePrefix));
        }

        private static void PatchOn(Harmony patcher, Type targetType, string name,
            Type[] args, string prefixName)
        {
            string tag = targetType.FullName + "." + name;
            try
            {
                MethodInfo original = args != null
                    ? AccessTools.Method(targetType, name, args)
                    : AccessTools.Method(targetType, name);
                if (original == null && args != null)
                {
                    original = AccessTools.Method(targetType, name);
                }
                if (original == null)
                {
                    Log.Warn("mirror-systems: method not found: " + tag);
                    return;
                }
                MethodInfo h = AccessTools.Method(typeof(MirrorSystems), prefixName);
                if (h == null)
                {
                    Log.Warn("mirror-systems: handler not found: " + prefixName);
                    return;
                }
                patcher.Patch(original, prefix: new HarmonyMethod(h));
                Log.Info("mirror-systems: patch-applied " + tag);
            }
            catch (Exception ex)
            {
                Log.Exception("mirror-systems patch " + tag, ex);
            }
        }

        /// <summary>
        /// Per-session reset (SaveReceiver.Reset) + pack teardown
        /// (Teardown.Run): counters and the first-skip log set. No external
        /// hooks to detach (unlike MirrorFirewall's logMessageReceived tap).
        /// </summary>
        public static void Reset()
        {
            SkippedCleanup = 0;
            SkippedDeletes = 0;
            SkippedResolves = 0;
            _logged.Clear();
        }

        /// <summary>
        /// The shared gate: true only while the mirror is Active AND
        /// __instance.World is the bound mirror world (the exact seam the
        /// firewall enforces - a skip under PendingActivate would suppress the
        /// populate phase's legitimate cleanup, and a stale non-mirror world
        /// must keep its own lifecycle).
        /// </summary>
        private static bool Gated(World world)
        {
            if (!MirrorState.Active) { return false; }
            if (!ReferenceEquals(world, Session.SaveReceiver.MirrorWorld)) { return false; }
            return true;
        }

        private static bool Skip(string tag, World world)
        {
            try
            {
                if (!Gated(world)) { return false; }
                if (tag == "cleanup")
                {
                    SkippedCleanup++;
                }
                else if (tag == "delete")
                {
                    SkippedDeletes++;
                }
                else
                {
                    SkippedResolves++;
                }
                if (_logged.Add(tag))
                {
                    Log.Info("mirror-systems: " + tag
                        + " pass disarmed on the mirror world (host-authoritative lifecycle)");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("mirror-systems " + tag, ex);
                return false;
            }
        }

        // Harmony prefixes - false = skip the original on the live mirror.
        // All fail OPEN (a throw inside the try is logged, we return true so
        // the world's own pass still runs).

        private static bool CleanupPrefix(CleanupSystem __instance)
        {
            return !Skip("cleanup", __instance != null ? __instance.World : null);
        }

        private static bool DeletionPrefix(EntityDeletionSystem __instance)
        {
            return !Skip("delete", __instance != null ? __instance.World : null);
        }

        private static bool SignpostResolvePrefix(SignpostSystem __instance)
        {
            return !Skip("signpost-resolve", __instance != null ? __instance.World : null);
        }

        private static bool XenopediaResolvePrefix(StrategyXenopediaSystem __instance)
        {
            return !Skip("xenopedia-resolve", __instance != null ? __instance.World : null);
        }
    }
}
