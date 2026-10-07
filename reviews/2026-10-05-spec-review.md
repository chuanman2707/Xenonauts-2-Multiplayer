# Spec review — Xenonauts 2 Co-op design (S0 + S1)

Date: 2026-10-05 · Reviewed: `specs/2026-10-05-xenonauts2-coop-design.md`, `MODLOG.md`
Method: claims checked against a full decompile of `Assembly-CSharp.dll` (GOG build, release-7.27.x),
written to `C:\Users\chuan\xenonauts2-decomp\full\` (ilspycmd 8.2.0.7535, `-p`; 5,349 files), plus
the install dir and profile dir on disk. File references below are relative to that decompile folder.

**Verdict: REVISE.** The S0 half is sound, and code loading turned out to be lower-risk than the spec
says. The S1 half depends on two premises that the decompile disproves: (a) that
the guest can be fed "a whole-strategy-state JSON snapshot per tick" and "write it into its world",
and (b) that "every gameplay action is an event", so gating EventBus publication is the choke
point. The spec has to replace both mechanisms before an implementation plan can be honest about
them. No implementation plan was written (as instructed for a REVISE verdict).

---

## 1. What checks out (verified)

| Spec claim | Evidence | Status |
|---|---|---|
| Content packs can ship C# and Harmony patches | `Common.Content.DataStructures/ContentPackState.cs` `Patch()`: loads every `Assembly` asset in the pack, calls `_harmony.PatchAll(assembly)` with id `"{UID}@{path}"`, then instantiates every non-abstract `IContentPackLifecycle` and calls `Create(this, harmony)`. `Unpatch()` calls `lifecycle.Destroy()` + `UnpatchAll`. | **Confirmed, stronger than stated.** Harmony auto-applies `[HarmonyPatch]` classes, so the game's own `0Harmony.dll` is used. Do not ship a copy. |
| User packs live in `LocalLow\…\Xenonauts 2\Mods` | `Common/Constants.cs` `Folders.ExternalMods()` = `Local()/Mods/`; `Xenonauts/XenonautsMain.cs:481` registers `FileSystemContentPackDiscoverer` on it. Discovery takes `*.json` manifests exactly one folder deep. | Confirmed |
| `IContentPackLifecycle` exists | `Common.Content.Lifecycle/IContentPackLifecycle.cs`: `Create`, `Destroy`, `OnWorldCreate(Section, WeakReference<World>)`, `OnWorldDispose`, `GetRequiredAssets`. `Section` = AirCombat, AirCombatSimulation, GroundCombat, Strategy. | Confirmed. `OnWorldCreate(Strategy, …)` gives a clean hook to get the Strategy `World`. |
| JSON world serialization / save = world snapshot | `Code.Screens.Common.Systems/SaveGameSystem.cs`, `Artitas.Systems/ArtitasSerializationSystem.cs`, `Artitas.Systems/WorldState.cs`. Entity IDs are preserved through save/load (the component matrix is indexed by entity ID). | Confirmed |
| LiteNetLib is bundled | `FlyingWormConsole3.LiteNetLib.*` is **inside Assembly-CSharp** as public types (`NetManager`, `NetDataWriter`, NAT punch). | Confirmed. The mod can reference it directly, so no embedding is needed for S1. |
| No anti-cheat, Mono, Harmony shipped | `Managed/0Harmony.dll`; `ContentPackState` imports `HarmonyLib`. | Confirmed |
| Faction/controller types (S2) | Not re-verified here (out of S0/S1 scope). | — |

**Over-stated risk (good news):** "content-pack code-loading rules on GOG build (BepInEx 5 fallback)".
The loader is generic and already in the shipped code path (`XenonautsMain.InitializeGame` →
`DiscoverContentPacks` → `RefreshContentPacks`). BepInEx should drop to a "not expected" footnote.

**Over-stated risk (good news):** "guest Strategy-sim suppression". All geoscape time comes from a
single place: `Xenonauts.Strategy.Systems/TimeTransitionSystem.cs`
`UpdateTimeOnBase(DeltaTimeEvent)` (`[Subscriber(Priority = int.MaxValue)]`). It advances the
`GeoscapeClockTag` entity's `GeoscapeDateTime`, fires `ClockStepReport` (`ProcessClockSteps`) and
runs `GeoscapeSchedulerComponent` tasks (`ProcessSchedulers`). It returns early when `IsPaused()`. Other
`DeltaTimeEvent` subscribers in Strategy (`ProjectSystem.TryUnlockProjects`,
`BuildingsManagementSystem.UpdateAdjacencyEffects`, `BaseStoresSystem.GenerateStoresOverCapacityReport`,
`PersonnelManagementSystem.GenerateXenonautArrivedNotification`, `StrategySaveGameSystem`) only recompute
derived state or flush queues. They are not time sources. A single Harmony prefix on
`UpdateTimeOnBase` (or forcing `IsPaused()` true) freezes the guest's simulation. The spec's
3-attempt circuit breaker targets the wrong risk.

---

## 2. Blocking issues (must be fixed in the spec)

### B1 — The StrategyFeed / StateApplier mechanism is not feasible as written
Spec §4: *"host serializes strategy-layer state each geoscape tick … start with whole-strategy-state
snapshot per tick (the data set is small …)"; guest "writes it into its world".*

- **Tick rate.** `StrategyConstants.ADVANCE_TIME_RATE_PER_TICK_SPEEDS` = one tick every **16–25 ms**
  of real time at every speed (game time per tick is 1 s / 1 min / 5 min / 10 min). That is 40–60
  snapshots per second.
- **No partial serializer.** The only world serializer is whole-world:
  `ArtitasSerializationSystem.CopyWorldIntoWorldState()` → `SerializationRequestCommand`. It produces
  the full campaign (every soldier, item and template override), not a small strategy subset. "The data
  set is small" was never measured.
- **No in-place apply.** The only apply path is `DeSerializationRequestCommand`, whose promise chain
  calls `World.EntityManager.DeleteAllEntities()` and then rebuilds every entity. Running it per tick
  (or even every few seconds) without a screen transition would tear down every
  entity-bound GameObject and UI binding. The game itself only does this behind a screen load
  (`SaveGameSystem.LoadGame` → `ScreenManager.RequestMoveTo(..., LoadGameParameters)`).

Because of this, §8's success criterion 4 ("within one geoscape tick") is physically impossible (one tick ≈ 20 ms,
below network RTT plus serialization time).

**Required spec change.** Replace the snapshot feed with an explicit replication mechanism and
time-box it as the S1 spike. The decompile suggests a workable one:
1. **Capture on host at the ECS mutation sink.** `Artitas/World.cs`: every component add/remove,
   immediate or delayed, ends in the private `World.ApplyComponentMutation(entityID, componentID,
   previousC, nextC, added)`. Deletes end in `World.DeleteEntityImmediately(entityID)`, and creation
   in `World.CreateEntity()`. Postfix those (non-generic, so Harmony-safe), only for the Strategy
   world, only while a session is live.
2. **Filter** to persistent components: skip types with `ExcludeFromSerializationAttribute` and
   entities carrying `ExcludeFromSerializationComponent` (the same rule `WorldState` uses). Those are
   local, derived or presentation state.
3. **Coalesce per frame** (last write wins per `(entity, componentType)`), serialize each component
   with the game's `ArtitasSerializationSystem.Serializer` (fsSerializer with Artitas converters),
   and send on a **ReliableOrdered** channel with a monotonically increasing `seq`.
4. **Apply on guest** by adding/removing the component on the mapped entity inside an "applier
   scope". Family listeners then update the UI as they do in normal play, which keeps §4 duty 3
   valid in this model.
5. **Entity identity.** IDs match right after both sides load the same save. Entities created later
   need a host→guest ID map, and entity references inside components need remapping. How the
   fsSerializer Entity converter encodes references is unknown and must be a recon item.
6. **Baseline resync = save reload.** Keep full save transfer + `SaveGameSystem.LoadGame` as the
   correctness backstop (join, host returns from battle, manual "Resync").

The spec must also say what happens if the spike fails, with a concrete degrade path. For example:
replicate only the clock entity's `GeoscapeDateTime` plus aircraft/UFO position components, and
resync everything else by periodic save reload. Record that choice in MODLOG.

### B2 — "Every gameplay action is an event" is false for the Strategy layer
Spec §3/§4 make EventBus publication "the single choke point" and plan to "swallow every
guest-originating strategy command" there.

- Strategy player actions are mostly **direct `[GameAPI]` method calls on systems that mutate
  entities in place**. Example: `XenonautSquadronSystem.CreateSquadronAndLaunch(...)` creates the squadron
  entity and calls `MoveTo` directly. There are 238 `[GameAPI]` members across
  `Xenonauts.Strategy.Systems` / `Strategy.Systems`, against only 20 `*Command` event classes in
  the Strategy namespaces (mostly UI, camera and time: `TimeControlCommand`, `HighlightCommand`, …).
- The event dispatch path is `World.HandleEvent<T>` / `QueueEvent<T>` (generic) →
  `ProcessStrategy.ProcessEvent(IEvent)`. `Artitas.Utils.EventBus` is a helper used by only two
  classes, so it is not the game's event bus.

**Required spec change.** Re-specify MirrorMode duty 2 as layered guards:
(a) clock freeze (above); (b) a **mutation firewall** on the guest: a prefix on
`World.ApplyComponentMutation` that drops mutations of replicated (serializable) component types
outside the applier scope and logs the call stack, with a whitelist for guest-local UI and camera state
discovered during recon; (c) suppress guest saves (B4); (d) UI affordance blocking stays
best-effort cosmetic in S1. The spec must also say that the S3 "command relay" premise is
invalid as stated. S3 will need either a curated list of relayed `[GameAPI]` methods or host-side
replay of UI intents. Flag it now so S3 isn't designed on the same assumption.

### B3 — Two instances on one PC: the S0 fallback list is wrong and shared-profile hazards are missing
- `Xenonauts2_Data/boot.config` contains **`single-instance=`**, which is Unity's "Force Single
  Instance" player setting. The second launch will very likely just focus the first window and exit.
  Windowed mode or `-screen-width` does not change this. The direct dev-box fix is
  backing up `boot.config` and removing that line. This is a local edit to an install file, dev only, never shipped,
  and must be reversible. (Not executed during this review; verify in S0.)
- Both instances share one profile dir (`LocalLow\Goldhawk Interactive\Xenonauts 2\`):
  - `Player.log`: Unity rotates it to `Player-prev.log` on startup, so instance 2 clobbers instance 1's log.
    Launch each with `-logFile <path>`.
  - `Saves\`: the guest's received save lands in the host's Saves tree and campaign identity paths
    (`SaveGameGroup.Identity`) can collide. The guest must write to a dedicated subfolder (e.g.
    `Saves\x2coop_guest\`). `GetSaveGameDescriptors` scans `AllDirectories`, so it will still appear
    in the load lists. Delete it on session end.
  - `Settings\`: enabling the mod in one instance affects the other. That is fine, but document it.
- The GOG Galaxy platform module (`Xenonauts.Common.Platform.GoG/GoGCorePlatformModule.cs`) may fail
  to initialize in instance 2. Add this to the S0 gate checks.

### B4 — Session-lifecycle and protocol gaps
Missing from §5/§6 and needed for S1 to work at all:
1. **Join consistency.** The host keeps ticking while the save serializes, transfers and loads. The
   spec needs either "host clock is paused (`TimeControlCommand.Pause()` /
   `TimeControlCommand.Resume()`) from snapshot until `GuestReady`" or "snapshot is stamped with
   `baselineSeq`; host buffers deltas `> baselineSeq` until guest ack". Pausing is simpler. Note
   that `TimeTransitionSystem` uses a pause counter, so the pause must be released exactly once.
2. **Host screen transitions.** What does the guest do when the host enters ground combat, air combat, a
   base screen, or the main menu, or loads another save? Minimum for S1: `SessionNotice.HostLeftStrategy` →
   guest shows "host is in battle" (frozen). On the host's return to Strategy, do a full save resync.
   Joining is allowed only while the host is on the Strategy screen.
3. **Save-transfer method.** "Host invokes the game's own save path to a temp file" is not what the
   game's path can do. `InternalProcessSaveGameRequest` always writes under `Constants.Folders.Save()`,
   with a group-derived name. It also **enforces `SaveGameLimit` (deletes the oldest saves in that
   group)**, fires `PostSaveGameReport`, and in Ironman makes hidden copies. Specify instead: serialize
   via `SerializationRequestCommand` and build/save a `SaveGame` to a custom `FileDescriptor` (mirroring
   `InternalProcessSaveGameRequest`), or prove via recon that a dedicated co-op group cannot evict
   the player's saves.
4. **Guest must never save.** Guest autosave, quicksave and Ironman hidden copies
   (`SaveGameSystem.QueueSaveGameRequest`, `ProcessSaveGameRequest`, `RequestAutoSave`) must be
   suppressed while mirroring. Otherwise the guest builds its own divergent save history of a campaign
   it doesn't own.
5. **Content-pack parity in the handshake.** `SaveGame.activeContentPacks` is recorded
   (`ContentPackDependencyResolver.BuildStamps`) but **not enforced on load** (`SaveGame.CanLoad()` only
   checks `MIN_SAVE_VERSION`). A guest with different data mods would load silently and diverge.
   `Hello` must carry the active-pack stamp list (UID + version) and the game build
   (`XenonautsConstants.Versioning.VERSION` + `Commits` hashes). The host rejects on mismatch.
6. **Message gaps.** Add `sessionId` (and `campaignId`) to every state/save message, `GuestReady`
   (save loaded, world initialized), `Resync` request (G→H), `HostLeftStrategy`/`HostReturned`, and
   `seq`/`baselineSeq` on deltas. The **Sequenced** channel must not carry deltas (it drops
   out-of-order packets). Use it only for idempotent full values such as the clock.
7. **Threading.** LiteNetLib events must be polled on the Unity main thread
   (`NetManager.PollEvents()` from a MonoBehaviour `Update` or a `DeltaTimeEvent` subscriber), because
   all world access is single-threaded.

### B5 — Success criteria 4 and 5 must be restated measurably
- Criterion 4 → e.g. "host clock, aircraft/UFO icons, base and research changes visible on the guest within
  ≤ 500 ms (LAN/loopback) at every time speed; no geoscape modals on guest (cosmetic, out of scope)".
  Host-side Ink/popup modals are event-driven and won't mirror through component replication, so
  say so.
- Criterion 5 → "the mutation firewall logs zero un-whitelisted replicated-component mutations during the
  scripted guest interaction run, and the guest world hash matches the host's after resync". "Every
  interaction … produces a notice" isn't testable when actions are 238 direct API calls.

---

## 3. Non-blocking corrections (fold into the revision)

- **Pack layout.** Assemblies are typed by folder: `XenoAssetConfigPrototypes`
  `.AddTypeMapping<Assembly>("assembly").MapExtensionToType<Assembly>(".dll")` (and `.pdb` →
  `AssemblyPDB`, which gives line numbers in stack traces). The DLL goes under `<pack>/assembly/…`. The exact
  sub-path (likely `assembly/common/`) is a recon item. §7's `pack/` template must say so.
- **Manifest.** The manifest is FullSerializer JSON with `$t` type tags (see
  `Assets/Assets/xenonauts/manifest.json`). Don't hand-write it. Generate it with the in-game "create
  pack" path (`ContentPackAuthoring.CreateLocalPack`, reached from `ContentPackMenuElement`), then
  commit the produced `manifest.json` as the template. Find out whether a newly discovered pack is
  enabled by default (`ContentManager.ReconcilePreferences` / `ContentPackSettings`) or must be
  toggled in the mod menu, and whether that needs a restart.
- **Assembly loading.** `AssemblyLoadProcessor` uses `Assembly.Load(byte[])` (no `Location`, and the
  "neither" load context). A second DLL in the pack (e.g. our own LiteNetLib) may not resolve. Keep
  the mod a **single assembly** referencing only game DLLs. Don't use `Assembly.Location`-relative
  file paths; resolve the pack folder from `ContentPackState.path` in `IContentPackLifecycle.Create`.
- **Target framework.** `netstandard2.1` probably works, but the game assemblies are .NET Framework
  4.x-profile Mono. `net472` + `<Reference Private="false">` to `Managed\*.dll` avoids facade
  surprises. Decide in S0 by loading a hello-world.
- **Logging.** The game's log4net root is ERROR, and **ERROR goes to `AnalyticsLogAppender`
  (PostHog)** as well as Player.log (`Assets/Configuration/log4net.xml`). The mod must log through
  `UnityEngine.Debug.Log` / its own file. It must never log IPs, player names or co-op errors at log4net ERROR,
  so they don't leak into Goldhawk analytics.
- **Harmony choke points must be non-generic.** `World.HandleEvent<T>`/`QueueEvent<T>` are generic,
  and patching them on Mono is fragile. Prefer `ProcessStrategy.ProcessEvent(IEvent)` (note: the
  concrete type may be `DefaultProcessStrategy` or `DebugProcessStrategy`) and
  `World.ApplyComponentMutation`. Verify that each patched method isn't inlined (fires in the log).
- **Mod lifecycle.** `Unpatch()` runs on disable. `IContentPackLifecycle.Destroy` must shut down
  the `NetManager` and remove any MonoBehaviour.
- **Port/LAN on one box.** The guest must bind an ephemeral port (only the host binds 27960). The first
  listen triggers a Windows Firewall prompt for `Xenonauts2.exe`, so document it.
- **Risk register order** should become: state replication (B1) → guest action firewall (B2) →
  two-instance/shared-profile (B3) → join/transition lifecycle (B4) → code loading (low).
- **Spec says `Artitas.Utils.EventBus` + `[Subscriber]`.** Systems are wired via
  `World.EventsToSystems` / `ProcessStrategy`, and `[Subscriber]` is correct.
- **Minor:** the base pack lives at `Assets/Assets/xenonauts/` (per `contentpacks.json` `core.path`).
  §1 criterion 1 should say `%USERPROFILE%\AppData\LocalLow\…\Mods`, not `…\Xenonauts 2\Mods`
  (ambiguous with the install dir).

## 4. Facts for the revised spec (ready to paste)

- Time source: `Xenonauts.Strategy.Systems.TimeTransitionSystem.UpdateTimeOnBase(DeltaTimeEvent)`;
  pause: `IsPaused()`, `TimeControlCommand.Pause()/Resume()/SetSpeedTo(int)`; tick events
  `PreGeoscapeTickReport`, `PostGeoscapeTickReport`, `ClockStepReport`; clock entity tag
  `GeoscapeClockTag`, component `GeoscapeDateTime`.
- Mutation sink: `Artitas.World.ApplyComponentMutation` (private), `DeleteEntityImmediately`,
  `CreateEntity`, `UnsafeDeleteEntity`.
- Serialization: `Artitas.Systems.ArtitasSerializationSystem` (`Serializer`,
  `CopyWorldIntoWorldState`, `HandleSerialization`, `HandleDeSerialization`);
  `ExcludeFromSerializationAttribute` / `ExcludeFromSerializationComponent`.
- Save: `Code.Screens.Common.Systems.SaveGameSystem` (`static LoadGame(FileDescriptor, bool)`,
  `ProcessSaveGameRequest`, `QueueSaveGameRequest`, `RequestAutoSave`); `SaveGame` fields
  (`version`, `activeContentPacks`, `worldData`, `group`).
- Mod loader: `ContentPackState.Patch`, `IContentPackLifecycle`, `AssemblyLoadProcessor`,
  `FileSystemContentPackDiscoverer`, `ContentPackAuthoring.CreateLocalPack`.
- Transport: `FlyingWormConsole3.LiteNetLib.NetManager` (in Assembly-CSharp).

## 5. Verdict

**REVISE.** Fix B1–B5 in the spec: a replication mechanism with a spike and fallback, a guest
firewall that doesn't rely on events, two-instance and shared-profile handling, session
lifecycle/protocol additions, and measurable criteria. Then re-review and write the S0+S1 plan. S0
(pack loads, Harmony fires, two instances) is close to plan-ready already and could start in
parallel if the user wants. It doesn't depend on the B1/B2 redesign.
