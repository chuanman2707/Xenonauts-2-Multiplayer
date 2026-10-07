#if X2COOP_PROBES
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Artitas;
using Artitas.Managers;
using Common.Components;
using Common.Regions.Components;
using Common.UID.Components;
using HarmonyLib;
using Xenonauts.Strategy.Systems;

namespace X2Coop.Probes
{
    /// <summary>
    /// T0.7 probe patches - Debug builds only (X2COOP_PROBES, see csproj).
    ///
    /// Two postfixes, applied manually from X2CoopLifecycle.Create via
    /// <see cref="Apply"/>. Manual patching (not [HarmonyPatch] attributes) so we
    /// control + log the exact moment each patch lands; the game's loader would
    /// have already run its own PatchAll(assembly) before Create
    /// (ContentPackState.cs:554) and we want explicit applied/failed lines.
    ///
    /// Targets:
    ///  - Xenonauts.Strategy.Systems.TimeTransitionSystem.UpdateTimeOnBase(DeltaTimeEvent)
    ///    public [Subscriber] (TimeTransitionSystem.cs:118). Counts invocations
    ///    plus "active" calls where IsPaused() reports false - while paused the
    ///    method still gets invoked (it early-returns internally), so
    ///    timeCalls keeps rising but timeActive freezes.
    ///  - Artitas.World.ApplyComponentMutation(int,int,IComponent,IComponent,bool)
    ///    private (World.cs:213) - resolved via AccessTools.Method. Counts calls
    ///    per World.Name (World.cs:71, "public string Name { get; set; }").
    ///
    /// T-R5 additions (entity-identity recon):
    ///  - Artitas.World.CreateEntity() public (World.cs:110). Counts per
    ///    World.Name - "does an idle (guest-style) session create entities?"
    ///    drives the identity-IDs-vs-explicit-map decision.
    ///  - Artitas.Managers.EntityManager.EntityRecyclingFactory.Obtain(World)
    ///    public (EntityManager.cs:78) - the TRUE create chokepoint: covers
    ///    World.CreateEntity AND the Template factory path, which bypasses
    ///    World.CreateEntity via world._entityManager.CreateEntity()
    ///    (Template.cs:26421/26456/26479) incl. limbo-pool ID re-obtains.
    ///  - Artitas.Managers.EntityManager.ForceCreateEntity(int) internal
    ///    (EntityManager.cs:185) - the EntityConverter resurrection path;
    ///    counted separately so save-load deserialisation spikes are visible.
    ///  - uid={w=u=<uid> g=<guid> t=<transform|region> a=<alive>} per seen world:
    ///    UIDComponent carriers (NeedsUID: Any<Transformation,RegionGroup>,
    ///    CommonArchetypes.cs:180), GuidComponent carriers (GuidManagerSystem),
    ///    and the NeedsUID-eligible population.
    ///
    /// T-R6 additions (whitelist discovery):
    ///  - MutationPostfix now binds entityID/componentID by name and, on
    ///    StrategyWorld only, resolves componentID -> Type via the public
    ///    reverse map ComponentManager.FindComponentType (ComponentManager.cs:52
    ///    -> ComponentLookup._idToType). Per-type stats: total, paused (via the
    ///    stored TimeTransitionSystem's IsPaused()), [ExcludeFromSerialization]
    ///    on the type, ExcludeFromSerializationComponent on the entity
    ///    (resolved via Lookup.TypeToId - NOT FindComponentId, which registers
    ///    missing types as a side effect on DynamicComponentLookup).
    ///  - Every 5 s summary also emits "probe-types" (sorted per-type census,
    ///    Full.Name=total/paused!flags) and, when Mirror.Whitelist.Active, a
    ///    "probe-whitelist" line + immediate "whitelist-miss &lt;type&gt;" lines
    ///    for paused serializable mutations outside the set.
    ///
    /// Every postfix logs "patch-hit &lt;Type.Method&gt;" once on first invocation
    /// (proof the target wasn't inlined). A "probe-summary" line is emitted every
    /// 5 s of wall-clock time (Stopwatch inside the postfix - no Unity timer).
    /// Postfixes never throw; all work is under one lock (world mutations are
    /// effectively single-threaded, the lock is uncontended and cheap).
    /// </summary>
    internal static class ProbePatches
    {
        private const double SummarySeconds = 5.0;
        private const string StrategyWorldName = "StrategyWorld";

        private static readonly object Gate = new object();
        private static readonly Stopwatch SinceSummary = new Stopwatch();
        private static readonly Dictionary<string, long> MutationsByWorld = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> CreatesByWorld = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> ObtainsByWorld = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> ForcedByWorld = new Dictionary<string, long>();
        private static readonly Dictionary<string, World> WorldsSeen = new Dictionary<string, World>();

        // --- R6: per-type mutation census on the Strategy world -------------
        // Per-type counters + exclusion flags for the whitelist-discovery set.
        private sealed class TypeStat
        {
            public long Total;
            public long Paused;
            public bool TypeExcluded;        // [ExcludeFromSerialization] on the component class
            public bool EntityExcludedSeen;  // seen on an entity carrying ExcludeFromSerializationComponent
        }

        private static readonly Dictionary<string, TypeStat> TypeStats = new Dictionary<string, TypeStat>();
        // Cache: component Type -> [ExcludeFromSerialization] present?
        private static readonly Dictionary<Type, bool> ExclAttrCache = new Dictionary<Type, bool>();
        // Cache: world -> componentID of ExcludeFromSerializationComponent (-1 = not registered there)
        private static readonly Dictionary<World, int> ExclIdByWorld = new Dictionary<World, int>();
        // Whitelist verify (Whitelist.Active): per-type miss counts + dedupe for the log line
        private static readonly Dictionary<string, long> WhitelistMisses = new Dictionary<string, long>();
        private static readonly HashSet<string> WhitelistMissLogged = new HashSet<string>();
        // Pause predicate source: the live TimeTransitionSystem (set in TimePostfix).
        private static TimeTransitionSystem _timeSys;

        private static bool _timeHit;
        private static bool _mutationHit;
        private static bool _createHit;
        private static bool _obtainHit;
        private static bool _forcedHit;
        private static long _timeCalls;
        private static long _timeActive;
        private static long _mutations;
        private static long _creates;
        private static long _obtains;
        private static long _forced;

        /// <summary>
        /// T2.6 pack teardown: drop the strong refs to game objects. _timeSys
        /// rebinds only when null (TimePostfix) - a stale TimeTransitionSystem
        /// surviving a world dispose while the pack was off would poison
        /// IsPausedSafe forever. WorldsSeen values and ExclIdByWorld keys are
        /// Worlds; keeping them past teardown is a leak (ExclIdByWorld would
        /// also keep answering for a dead world). The string/Type-keyed
        /// counters and caches keep their recon history.
        /// </summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _timeSys = null;
                WorldsSeen.Clear();
                ExclIdByWorld.Clear();
            }
        }

        /// <summary>Resolve + patch both probe targets. Called from Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("probes: Apply called with null patcher - nothing patched");
                return;
            }
            Patch(patcher, typeof(TimeTransitionSystem), "UpdateTimeOnBase", nameof(TimePostfix));
            Patch(patcher, typeof(World), "ApplyComponentMutation", nameof(MutationPostfix));
            Patch(patcher, typeof(World), "CreateEntity", nameof(CreatePostfix));
            // Obtain has two overloads - patch Obtain(World) only; Obtain(World,int)
            // is the ForceCreateEntity resurrection path, counted separately.
            PatchOverload(patcher, typeof(EntityManager.EntityRecyclingFactory), "Obtain",
                new[] { typeof(World) }, nameof(ObtainPostfix));
            Patch(patcher, typeof(EntityManager), "ForceCreateEntity", nameof(ForcedPostfix));
            lock (Gate)
            {
                SinceSummary.Restart();
            }
        }

        private static void Patch(Harmony patcher, Type targetType, string methodName, string postfixName)
        {
            PatchOverload(patcher, targetType, methodName, null, postfixName);
        }

        private static void PatchOverload(Harmony patcher, Type targetType, string methodName,
            Type[] argTypes, string postfixName)
        {
            string tag = targetType.FullName + "." + methodName;
            try
            {
                MethodInfo original = argTypes == null
                    ? AccessTools.Method(targetType, methodName)
                    : AccessTools.Method(targetType, methodName, argTypes);
                if (original == null)
                {
                    Log.Warn("probes: method not found: " + tag);
                    return;
                }
                MethodInfo postfix = AccessTools.Method(typeof(ProbePatches), postfixName);
                if (postfix == null)
                {
                    Log.Warn("probes: postfix not found: ProbePatches." + postfixName);
                    return;
                }
                patcher.Patch(original, postfix: new HarmonyMethod(postfix));
                Log.Info("probes: patch-applied " + tag + " (owner=" + patcher.Id + ")");
            }
            catch (Exception ex)
            {
                Log.Exception("probes patch " + tag, ex);
            }
        }

        /// <summary>Postfix: TimeTransitionSystem.UpdateTimeOnBase(DeltaTimeEvent).</summary>
        private static void TimePostfix(TimeTransitionSystem __instance)
        {
            try
            {
                bool active = false;
                try
                {
                    // [GameAPI] public bool IsPaused() - can NRE before the system
                    // initializes (_strategyLogicSystem null); count those as paused.
                    active = __instance != null && !__instance.IsPaused();
                }
                catch
                {
                }
                lock (Gate)
                {
                    // R6: stash the instance so MutationPostfix can ask IsPaused()
                    // at mutation time (the game's own predicate - covers both the
                    // pause-counter path and the not-in-Turn state).
                    if (_timeSys == null && __instance != null)
                    {
                        _timeSys = __instance;
                    }
                    if (!_timeHit)
                    {
                        _timeHit = true;
                        Log.Info("patch-hit Xenonauts.Strategy.Systems.TimeTransitionSystem.UpdateTimeOnBase");
                    }
                    _timeCalls++;
                    if (active)
                    {
                        _timeActive++;
                    }
                    MaybeSummarize();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("probe TimePostfix", ex);
            }
        }

        /// <summary>
        /// Postfix: World.ApplyComponentMutation(int,int,IComponent,IComponent,bool).
        /// R6: entityID/componentID bind by name to the original args; on the
        /// Strategy world the componentID is resolved to its Type via
        /// ComponentManager.FindComponentType (the reverse map, ComponentLookup
        /// ._idToType - public since ComponentManager.cs:52) and counted in
        /// TypeStats with paused/exclusion flags.
        /// </summary>
        private static void MutationPostfix(World __instance, int entityID, int componentID)
        {
            try
            {
                string name = SafeName(__instance);
                lock (Gate)
                {
                    if (!_mutationHit)
                    {
                        _mutationHit = true;
                        Log.Info("patch-hit Artitas.World.ApplyComponentMutation");
                    }
                    _mutations++;
                    MutationsByWorld.TryGetValue(name, out long count);
                    MutationsByWorld[name] = count + 1;
                    if (__instance != null)
                    {
                        WorldsSeen[name] = __instance;
                        if (name == StrategyWorldName && componentID >= 0)
                        {
                            RecordTypeStat(__instance, entityID, componentID);
                        }
                    }
                    MaybeSummarize();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("probe MutationPostfix", ex);
            }
        }

        /// <summary>
        /// R6: per-type census for one Strategy-world mutation. Caller holds
        /// Gate. Never throws into the postfix - every game-API touch is
        /// individually guarded because a KeyNotFound/IndexOutOfRange here must
        /// not void the whole stat line.
        /// </summary>
        private static void RecordTypeStat(World world, int entityID, int componentID)
        {
            Type type = null;
            try
            {
                type = world.ComponentManager?.FindComponentType(componentID);
            }
            catch
            {
            }
            if (type == null)
            {
                return;
            }
            string key = type.FullName ?? type.Name;
            if (!TypeStats.TryGetValue(key, out TypeStat st))
            {
                st = new TypeStat();
                TypeStats[key] = st;
            }
            st.Total++;

            // Type-level exclusion: [ExcludeFromSerialization] means the whole
            // component type is skipped by WorldState serialization (decomp
            // WorldState.cs:77) - never a whitelist candidate.
            if (!st.TypeExcluded)
            {
                if (!ExclAttrCache.TryGetValue(type, out bool typeExcl))
                {
                    try
                    {
                        typeExcl = type.GetCustomAttributes(
                            typeof(Artitas.Components.ExcludeFromSerializationAttribute), false).Length > 0;
                    }
                    catch
                    {
                        typeExcl = false;
                    }
                    ExclAttrCache[type] = typeExcl;
                }
                st.TypeExcluded = typeExcl;
            }

            bool paused = IsPausedSafe();
            if (paused)
            {
                st.Paused++;
            }

            // Entity-level exclusion: the entity carries
            // ExcludeFromSerializationComponent -> the whole entity is skipped
            // (WorldState.cs:131). Recorded once per type (flag only).
            if (!st.EntityExcludedSeen)
            {
                int exclId = ExclIdFor(world);
                if (exclId >= 0)
                {
                    try
                    {
                        Entity e = world.EntityManager?.GetEntity(entityID);
                        if (e != null && e.Has(exclId))
                        {
                            st.EntityExcludedSeen = true;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            // R6 verify: whitelist active + paused + type is serializable
            // (no type-level exclusion) + not whitelisted -> whitelist-miss.
            if (paused && Mirror.Whitelist.Active
                && !ExclAttrCache[type] && !Mirror.Whitelist.Contains(type))
            {
                WhitelistMisses.TryGetValue(key, out long miss);
                WhitelistMisses[key] = miss + 1;
                if (WhitelistMissLogged.Add(key))
                {
                    Log.Info("whitelist-miss " + key);
                }
            }
        }

        /// <summary>
        /// componentID of ExcludeFromSerializationComponent in this world, or
        /// -1 when not registered. Resolved through Lookup.TypeToId (a copy) -
        /// NOT FindComponentId, which on DynamicComponentLookup would REGISTER
        /// the type as a side effect (DynamicComponentLookup.cs:9-18).
        /// </summary>
        private static int ExclIdFor(World world)
        {
            if (ExclIdByWorld.TryGetValue(world, out int id))
            {
                return id;
            }
            id = -1;
            try
            {
                Dictionary<Type, int> map = world.ComponentManager?.Lookup?.TypeToId;
                if (map != null
                    && map.TryGetValue(typeof(Artitas.Components.ExcludeFromSerializationComponent), out int v))
                {
                    id = v;
                }
            }
            catch
            {
            }
            ExclIdByWorld[world] = id;
            return id;
        }

        /// <summary>The game's own pause predicate; false when unknown/uninit.</summary>
        private static bool IsPausedSafe()
        {
            try
            {
                return _timeSys != null && _timeSys.IsPaused();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Postfix: World.CreateEntity() - R5 guest-local create rate.</summary>
        private static void CreatePostfix(World __instance)
        {
            try
            {
                string name = SafeName(__instance);
                lock (Gate)
                {
                    if (!_createHit)
                    {
                        _createHit = true;
                        Log.Info("patch-hit Artitas.World.CreateEntity");
                    }
                    _creates++;
                    CreatesByWorld.TryGetValue(name, out long count);
                    CreatesByWorld[name] = count + 1;
                    if (__instance != null)
                    {
                        WorldsSeen[name] = __instance;
                    }
                    MaybeSummarize();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("probe CreatePostfix", ex);
            }
        }

        /// <summary>
        /// Postfix: EntityRecyclingFactory.Obtain(World) - every locally-allocated
        /// entity ID (fresh nextId++ AND limbo-pool re-obtains) funnels through here,
        /// from both World.CreateEntity and the Template factory path.
        /// </summary>
        private static void ObtainPostfix(World __0)
        {
            try
            {
                string name = SafeName(__0);
                lock (Gate)
                {
                    if (!_obtainHit)
                    {
                        _obtainHit = true;
                        Log.Info("patch-hit Artitas.Managers.EntityManager+EntityRecyclingFactory.Obtain(World)");
                    }
                    _obtains++;
                    ObtainsByWorld.TryGetValue(name, out long count);
                    ObtainsByWorld[name] = count + 1;
                    if (__0 != null)
                    {
                        WorldsSeen[name] = __0;
                    }
                    MaybeSummarize();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("probe ObtainPostfix", ex);
            }
        }

        /// <summary>Postfix: EntityManager.ForceCreateEntity(int) - converter resurrection.</summary>
        private static void ForcedPostfix(EntityManager __instance)
        {
            try
            {
                World w = null;
                try
                {
                    w = __instance?.World;
                }
                catch
                {
                }
                string name = SafeName(w);
                lock (Gate)
                {
                    if (!_forcedHit)
                    {
                        _forcedHit = true;
                        Log.Info("patch-hit Artitas.Managers.EntityManager.ForceCreateEntity");
                    }
                    _forced++;
                    ForcedByWorld.TryGetValue(name, out long count);
                    ForcedByWorld[name] = count + 1;
                    if (w != null)
                    {
                        WorldsSeen[name] = w;
                    }
                    MaybeSummarize();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("probe ForcedPostfix", ex);
            }
        }

        private static string SafeName(World w)
        {
            string name = null;
            try
            {
                name = w?.Name;
            }
            catch
            {
            }
            return string.IsNullOrEmpty(name) ? "<unnamed>" : name;
        }

        /// <summary>
        /// R5: per-world identity census, "u=&lt;uid&gt; g=&lt;guid&gt; t=&lt;transform|region&gt;
        /// a=&lt;alive&gt;": how many alive entities carry UIDComponent (NeedsUID
        /// auto-assigns to Any&lt;Transformation,RegionGroup&gt;, CommonArchetypes.cs:180),
        /// GuidComponent (strategy-side stable identity, GuidManagerSystem), and the
        /// NeedsUID-eligible population (Transformation OR RegionGroup). Has&lt;T&gt;
        /// throws KeyNotFoundException if the component type is not in the world's
        /// ComponentLookup -&gt; counted separately per type so one miss can't void
        /// the rest. Runs on the world thread inside a postfix.
        /// </summary>
        private static string UidFraction(World w)
        {
            try
            {
                if (w?.EntityManager == null)
                {
                    return "n/a";
                }
                int alive = 0;
                int uid = 0;
                int guid = 0;
                int transformOrRegion = 0;
                foreach (Entity e in w.EntityManager.FindAliveEntities())
                {
                    if (e == null)
                    {
                        continue;
                    }
                    alive++;
                    try
                    {
                        if (e.Has<UIDComponent>())
                        {
                            uid++;
                        }
                    }
                    catch
                    {
                        uid = -1;
                    }
                    try
                    {
                        if (e.Has<GuidComponent>())
                        {
                            guid++;
                        }
                    }
                    catch
                    {
                        guid = -1;
                    }
                    try
                    {
                        if (e.Has<Transformation>() || e.Has<RegionGroup>())
                        {
                            transformOrRegion++;
                        }
                    }
                    catch
                    {
                        transformOrRegion = -1;
                    }
                }
                return "u=" + uid + " g=" + guid + " t=" + transformOrRegion + " a=" + alive;
            }
            catch (Exception ex)
            {
                return "<err:" + ex.GetType().Name + ">";
            }
        }

        /// <summary>Emit the 5 s summary line. Caller holds Gate.</summary>
        private static void MaybeSummarize()
        {
            if (!SinceSummary.IsRunning || SinceSummary.Elapsed.TotalSeconds < SummarySeconds)
            {
                return;
            }
            var worlds = new List<string>();
            foreach (KeyValuePair<string, long> kv in MutationsByWorld)
            {
                worlds.Add(kv.Key + "=" + kv.Value);
            }
            var creates = new List<string>();
            foreach (KeyValuePair<string, long> kv in CreatesByWorld)
            {
                creates.Add(kv.Key + "=" + kv.Value);
            }
            var obtains = new List<string>();
            foreach (KeyValuePair<string, long> kv in ObtainsByWorld)
            {
                obtains.Add(kv.Key + "=" + kv.Value);
            }
            var forced = new List<string>();
            foreach (KeyValuePair<string, long> kv in ForcedByWorld)
            {
                forced.Add(kv.Key + "=" + kv.Value);
            }
            var uids = new List<string>();
            foreach (KeyValuePair<string, World> kv in WorldsSeen)
            {
                uids.Add(kv.Key + "=" + UidFraction(kv.Value));
            }
            Log.Info("probe-summary time=" + _timeCalls
                + " timeActive=" + _timeActive
                + " mutations=" + _mutations
                + " creates=" + _creates
                + " obtains=" + _obtains
                + " forced=" + _forced
                + " byWorld={" + string.Join(",", worlds.ToArray()) + "}"
                + " createByWorld={" + string.Join(",", creates.ToArray()) + "}"
                + " obtainByWorld={" + string.Join(",", obtains.ToArray()) + "}"
                + " forcedByWorld={" + string.Join(",", forced.ToArray()) + "}"
                + " uid={" + string.Join(",", uids.ToArray()) + "}");
            EmitTypeStats();
            SinceSummary.Restart();
        }

        /// <summary>
        /// R6: dump the Strategy-world per-type census every summary tick.
        /// Format: Full.Type.Name=total/paused[!flags] where flags are
        ///   X = [ExcludeFromSerialization] on the component class
        ///   E = seen on an entity carrying ExcludeFromSerializationComponent
        /// Sorted for stable diffs between pre/post-whitelist runs. A second
        /// line reports whitelist-verify misses whenever the whitelist is
        /// active (or any miss has been counted).
        /// </summary>
        private static void EmitTypeStats()
        {
            var keys = new List<string>(TypeStats.Keys);
            keys.Sort(StringComparer.Ordinal);
            var entries = new List<string>(keys.Count);
            foreach (string key in keys)
            {
                TypeStat st = TypeStats[key];
                string flags = (st.TypeExcluded ? "X" : string.Empty)
                    + (st.EntityExcludedSeen ? "E" : string.Empty);
                entries.Add(key + "=" + st.Total + "/" + st.Paused
                    + (flags.Length > 0 ? "!" + flags : string.Empty));
            }
            Log.Info("probe-types paused=" + (IsPausedSafe() ? 1 : 0)
                + " n=" + keys.Count
                + " types={" + string.Join(",", entries.ToArray()) + "}");
            if (Mirror.Whitelist.Active || WhitelistMisses.Count > 0)
            {
                var miss = new List<string>();
                foreach (KeyValuePair<string, long> kv in WhitelistMisses)
                {
                    miss.Add(kv.Key + "=" + kv.Value);
                }
                miss.Sort(StringComparer.Ordinal);
                Log.Info("probe-whitelist active=" + (Mirror.Whitelist.Active ? 1 : 0)
                    + " types=" + Mirror.Whitelist.Count
                    + " miss={" + string.Join(",", miss.ToArray()) + "}");
            }
        }
    }
}
#endif
