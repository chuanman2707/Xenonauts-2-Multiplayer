using System;
using System.Reflection;
using Common.FSM.Events;
using HarmonyLib;
using Xenonauts.Strategy.Systems;

namespace X2Coop.Mirror
{
    /// <summary>
    /// T4.2-r1 (review I-1): guest geoscape pointer-pick gate.
    ///
    /// The T4.2 run produced a FATAL crash modal on the guest at 16:19:21:
    /// NullReferenceException at UnityEngine.Component.get_gameObject inside
    /// RegionInfo.UpdateAppearance, reached from RegionInfo.FocusRegion via
    /// the pointer-enter delegate GeoscapeElement.SetupGeoscapeView wires on
    /// every region listener (decompile GeoscapeElement.cs:134-137), right
    /// after 'Enter State:GeoscapeSelectTarget' - the LAUNCH AIRCRAFT pick
    /// mode. The hovered element was orphaned across the resync's mirror-UI
    /// rebuild; the firewall could not see it (a uGUI hover path, not a
    /// mutation sink).
    ///
    /// The cheapest real mitigation (the one T4.2's own report deferred to):
    /// a guest on a read-only mirror must never enter a geoscape pointer-pick
    /// state at all - every such mode is futile anyway (any pick outcome is
    /// a mutation the firewall would drop).
    ///
    /// Patch site (single, narrow): PREFIX on
    /// Xenonauts.Strategy.Systems.StrategyUISystem.TriggerEventOnFSM
    /// (TriggerCommand&lt;StrategyUITriggers&gt;) - protected override,
    /// NON-GENERIC, the single funnel every strategy-UI FSM trigger command
    /// passes through (queued QueueTrigger events and immediate
    /// HandleTrigger calls both land in FSMSystem.OnHandle which calls the
    /// virtual TriggerEventOnFSM; the ShowModal/MoveTo branches forward to
    /// the same base helper). Dropping the trigger here means the pick
    /// state's OnEnter never runs: no SetState(Pick), no cursor swap, no
    /// canvas-group toggles, no reachable orphaned hover.
    ///
    /// Blocked while MirrorState.Mode != Off (guest in-session, matching the
    /// SaveSuppress gate):
    ///   - StrategyUITriggers.SelectTarget - the PermitDynamic on
    ///     GeoscapeMain fans out to ALL THREE geoscape pick states by
    ///     parameter type: GeoscapeSelectTargetElement.Parameters ->
    ///     GeoscapeSelectTarget (LAUNCH AIRCRAFT toggle +
    ///     squadron-retarget buttons on the Air* elements),
    ///     GeoscapeSelectGeoRegionElement.Parameters ->
    ///     GeoscapeSelectGeoRegion (strategy-ability region pick),
    ///     GeoBaseSelectionElement.Parameters -> GeoscapeSelectGeoBase
    ///     (ground-mission base pick - a list panel, but a futile guest
    ///     pick regardless). Blocking the trigger covers all three.
    ///   - StrategyUITriggers.Build ONLY when Parameters[0] is
    ///     GeoscapeBaseBuildElement.Parameters -> GeoscapeBaseBuild, the
    ///     new-base placement pick mode (Picking2DReport hover + click,
    ///     same orphaned-hover surface). Other Build parameters
    ///     (ConstructBuildingElement, AircraftRequisitionElement) are
    ///     menu-driven flows, not geoscape pointer-picks, and pass -
    ///     their mutation attempts are the T4.1 firewall's domain.
    ///
    /// Deliberately NOT gated:
    ///   - StrategyUITriggers.Select -> the menu *SelectGeoBase states
    ///     (Research/Engineering/BaseStores/Soldiers/Aircraft transfers)
    ///     all use GeoBaseSelectionElement, a plain list panel - no
    ///     geoscape pointer-pick, no region-hover surface.
    ///   - ShowModal / MoveTo / Inspect / Hide - MOVE_TO and
    ///     GEOSCAPE_MODALS lists contain no pick states; Inspect callers
    ///     name read-only info/editor states; the guest must keep Hide to
    ///     leave any state.
    ///   - MultiTargetSelect is a ShowModal reached only from inside a
    ///     pick flow that can no longer be entered.
    ///
    /// Fails OPEN everywhere: a throw inside the prefix logs once and the
    /// trigger proceeds (a stuck prefix would eat Hide/ShowModal
    /// navigation on the host too if the gate logic ever faulted - and
    /// Mode is always Off host-side anyway). Rate-limited to 1 blocked
    /// line per trigger-key per 5 s; <see cref="BlockedPickEntries"/>
    /// always increments. Not probe-gated: ships in Release.
    /// </summary>
    public static class UIPickGate
    {
        /// <summary>Rate limit on the "blocked" log line: 1 per key per 5 s.</summary>
        private const int LogRateLimitMs = 5000;

        /// <summary>Pick-mode trigger entries dropped while the guest was in-session.</summary>
        public static long BlockedPickEntries;

        private static readonly System.Collections.Generic.Dictionary<string, int> LastLogTick
            = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Cached Xenonauts.Strategy.UI.GeoscapeBaseBuildElement+Parameters (resolved in Apply; null degrades the Build half of the gate to pass-through).</summary>
        private static Type _baseBuildParams;

        /// <summary>Resolve targets + install the prefix. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("pick-gate: Apply called with null patcher");
                return;
            }
            try
            {
                _baseBuildParams = AccessTools.TypeByName(
                    "Xenonauts.Strategy.UI.GeoscapeBaseBuildElement+Parameters");
                if (_baseBuildParams == null)
                {
                    Log.Warn("pick-gate: GeoscapeBaseBuildElement.Parameters not found - the base-placement half of the gate degrades to pass-through");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("pick-gate params resolve", ex);
            }

            string tag = typeof(StrategyUISystem).FullName + ".TriggerEventOnFSM";
            try
            {
                MethodInfo original = AccessTools.DeclaredMethod(typeof(StrategyUISystem),
                    "TriggerEventOnFSM", new[] { typeof(TriggerCommand<StrategyUITriggers>) })
                    ?? AccessTools.Method(typeof(StrategyUISystem), "TriggerEventOnFSM");
                MethodInfo prefix = AccessTools.Method(typeof(UIPickGate), nameof(TriggerEventOnFSMPrefix));
                if (original == null || prefix == null)
                {
                    Log.Warn("pick-gate: method/prefix not found (" + tag + ")");
                    return;
                }
                patcher.Patch(original, prefix: new HarmonyMethod(prefix));
                Log.Info("pick-gate: patch-applied " + tag
                    + "(TriggerCommand<StrategyUITriggers>) (prefix)");
            }
            catch (Exception ex)
            {
                Log.Exception("pick-gate patch " + tag, ex);
            }
        }

        /// <summary>
        /// Per-session reset (SaveReceiver.Reset on session end / fresh join):
        /// counter + rate-limit map are session-scoped like the firewall's.
        /// </summary>
        public static void Reset()
        {
            BlockedPickEntries = 0;
            LastLogTick.Clear();
        }

        /// <summary>
        /// Prefix on StrategyUISystem.TriggerEventOnFSM - return false to drop
        /// the trigger command before the UI FSM sees it.
        /// </summary>
        private static bool TriggerEventOnFSMPrefix(TriggerCommand<StrategyUITriggers> trigger)
        {
            try
            {
                if (MirrorState.Mode == MirrorMode.Off) { return true; }
                if (trigger == null) { return true; }
                StrategyUITriggers t = trigger.Trigger;
                if (t == StrategyUITriggers.SelectTarget)
                {
                    return Block("SelectTarget", "params=" + ParamName(trigger));
                }
                if (t == StrategyUITriggers.Build && IsBaseBuildPick(trigger))
                {
                    return Block("Build->GeoscapeBaseBuild", "params=" + ParamName(trigger));
                }
            }
            catch (Exception ex)
            {
                Log.Exception("pick-gate trigger-prefix", ex);
            }
            return true;
        }

        /// <summary>True when the Build trigger carries GeoscapeBaseBuildElement.Parameters (the geoscape base-placement pick).</summary>
        private static bool IsBaseBuildPick(TriggerCommand<StrategyUITriggers> trigger)
        {
            if (_baseBuildParams == null) { return false; }
            object p = FirstParam(trigger);
            return p != null && _baseBuildParams.IsInstanceOfType(p);
        }

        private static object FirstParam(TriggerCommand<StrategyUITriggers> trigger)
        {
            object[] p = (trigger as IParameterizedTriggerCommand)?.Parameters;
            return (p != null && p.Length > 0) ? p[0] : null;
        }

        /// <summary>"GeoscapeSelectTargetElement.Parameters" style name for the first trigger parameter (nested type -> DeclaringType.Name + "." + Name).</summary>
        private static string ParamName(TriggerCommand<StrategyUITriggers> trigger)
        {
            object p = FirstParam(trigger);
            if (p == null) { return "none"; }
            Type t = p.GetType();
            return (t.DeclaringType != null ? t.DeclaringType.Name + "." : string.Empty) + t.Name;
        }

        /// <summary>Count + (rate-limited) log the drop; always returns false (the trigger is refused, not degraded).</summary>
        private static bool Block(string key, string detail)
        {
            BlockedPickEntries++;
            if (RateLimit(key))
            {
                Log.Info("blocked " + key + " (guest in-session, mode="
                    + MirrorState.Mode + ", " + detail
                    + ") - geoscape pick-mode entry refused (picks="
                    + BlockedPickEntries + ")");
            }
            return false;
        }

        /// <summary>1 log line per key per LogRateLimitMs; Environment.TickCount wraparound-safe diff.</summary>
        private static bool RateLimit(string key)
        {
            int now = Environment.TickCount;
            if (LastLogTick.TryGetValue(key, out int last)
                && unchecked(now - last) < LogRateLimitMs)
            {
                return false;
            }
            LastLogTick[key] = now;
            return true;
        }
    }
}
