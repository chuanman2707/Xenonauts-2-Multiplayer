using System;
using System.Collections.Generic;

namespace X2Coop.Mirror
{
    /// <summary>
    /// R6 whitelist: SERIALIZABLE component types observed mutating on the
    /// Strategy world purely from local UI/camera interaction while the
    /// simulation is paused (TimeTransitionSystem.IsPaused() == true).
    ///
    /// Semantics (Artitas.Systems/WorldState.cs):
    ///  - A component TYPE whose class carries [ExcludeFromSerialization] is
    ///    never serialized (WorldState.cs:77) - such types can never appear in
    ///    host state, so they are NOT whitelist candidates (they are already
    ///    guest-local by construction). The R6 session saw paused mutations of
    ///    four such types (UIControllersComponent, GeoscapeIconParentLink,
    ///    GeoscapeIconsLink, GeoscapePositionAnchor) - logged but not listed.
    ///  - An ENTITY carrying ExcludeFromSerializationComponent is skipped
    ///    wholesale (WorldState.cs:34,:131) - but its component types still
    ///    serialize on other entities, so a type seen on an excluded entity
    ///    is not automatically safe.
    ///
    /// Therefore the whitelist lists serializable TYPES that presentation-only
    /// paths mutate: a guest must not treat these mutations as desync, and a
    /// host-side mirror must not rebroadcast them (they are per-client
    /// presentation state - camera, selection, hover, open-screen bookkeeping).
    ///
    /// Membership is compile-time-safe: every entry is a typeof() reference -
    /// all 12 observed types are public Assembly-CSharp types, so the
    /// <see cref="TypeNames"/> string fallback is currently empty (kept for
    /// types that are awkward to reference: internal, nested-private, or in
    /// assemblies we do not reference). Contains() checks both.
    ///
    /// Measured 2026-10-05 (session detail + per-type counts in MODLOG R6
    /// entry): single-player on user_r3seedday1-1.json, interactions performed
    /// only while IsPaused() reported true (geoscape select-target mode, every
    /// base/research/engineering/soldiers/stores/armory/aircraft/xenopedia
    /// screen, recruit/manage panels, ESC game menu). Nothing confirmed.
    /// </summary>
    internal static class Whitelist
    {
        /// <summary>
        /// When true, the R6 probe (Debug builds) treats the whitelist as
        /// authoritative: any mutation of a serializable component type that
        /// is NOT in this set while the sim is paused logs "whitelist-miss".
        /// Kept as a const so the check compiles to nothing in callers that
        /// short-circuit on it; set true while verifying, false for shipping.
        /// </summary>
        internal const bool Active = true;

        /// <summary>
        /// Discovered whitelist types (typeof()-referenced). paused-count and
        /// !E flag (seen on an entity carrying ExcludeFromSerializationComponent)
        /// are noted per entry from the measurement run.
        /// </summary>
        private static readonly HashSet<Type> Types = new HashSet<Type>
        {
            // --- per-frame / timer churn while paused -------------------------
            typeof(global::Artitas.Components.EntityListenerComponent),            // 524 paused: UI listener (re)registration, ~120/s on paused screens
            typeof(global::Xenonauts.Common.Components.CampaignPlayTimeComponent), // 42205 paused: wall-clock playtime timer, ticks through pause-counter pauses (not under ESC menu)

            // --- UI-state bookkeeping (all-observed-paused or !E) -------------
            typeof(global::Common.Components.GuidComponent),                       // 47/47 paused: GUID assign/churn on screen-state entities
            typeof(global::Common.Components.RotationComponent),                   // 46/46 paused: 3D preview spin (aircraft/armory viewers)
            typeof(global::Common.Components.Generic.ReadComponent),               // 13/13 paused: "marked read" flags (xenopedia/notification browse). Borderline gameplay state - flagged for T4.1 review
            typeof(global::Xenonauts.Strategy.Components.StrategyUIStateComponent),// 38 paused, !E: which strategy screen/state is open
            typeof(global::Xenonauts.Strategy.Components.MainBase.CreationTimeComponent), // 52 paused, !E: base-screen creation/countdown bookkeeping

            // --- one-off / rare paused mutations ------------------------------
            typeof(global::Artitas.Components.ExcludeFromSerializationComponent),  // 3 paused, !E: marker add/remove churn on excluded entities
            typeof(global::Common.Components.GameObjectComponent),                 // 3 paused, !E: IOptionalSerialization - per-instance opt-out too
            typeof(global::Strategy.UI.Components.UI.GeoscapeScreenComponent),     // 3 paused, !E: geoscape screen targets
            typeof(global::Xenonauts.Common.Components.Biome),                     // 1 paused: biome lookup on hover (component form of the enum)
            typeof(global::Xenonauts.Strategy.Components.GeoRegionComponent),      // 1 paused: region hover/select bookkeeping
        };

        /// <summary>
        /// Fallback by FullName for types we cannot reference at compile time.
        /// Kept separate so the choice is explicit per type (see class doc).
        /// Empty after R6: all observed types are public.
        /// </summary>
        private static readonly HashSet<string> TypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
        };

        public static int Count => Types.Count + TypeNames.Count;

        public static bool Contains(Type type)
        {
            if (type == null)
            {
                return false;
            }
            return Types.Contains(type) || TypeNames.Contains(type.FullName);
        }

        public static bool Contains(string fullName)
        {
            return fullName != null && TypeNames.Contains(fullName);
        }
    }
}
