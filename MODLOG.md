# Xenonauts 2 -- co-op multiplayer mod -- MODLOG

## Goal (as agreed with user)
- 2-player co-op campaign (host + guest), one shared campaign.
- Co-commanders on the Geoscape: either player can run strategy actions, actions sync.
- Tactical battles: per-soldier ownership -- each player controls ~half the dropship squad,
  both act during the same player turn.
- Reference model: Kenshi Online (nexusmods.com/kenshi/mods/2183) -- but adapted: X2 has no
  shared open world, so host-authoritative state sync instead of parallel-world overlay.

## Recon -- 2026-10-05
- Install: `E:\GOG Games\Xenonauts 2` (GOG build, `gog productID 1412583633`).
- Engine: Unity 2022.3.62f2, **Mono backend**, x64. `Xenonauts2_Data\Managed\Assembly-CSharp.dll`
  fully decompilable (ilspycmd 8.2.0.7535 works with `DOTNET_ROLL_FORWARD=Major`; latest
  ilspycmd package is broken -- use that version).
- No anti-cheat. No loaders installed.
- Saves/profile: `C:\Users\chuan\AppData\LocalLow\Goldhawk Interactive\Xenonauts 2\`
  (`Saves`, `Mods`, `Settings`, `Logs`, `Player.log`). Mods folder exists but empty.
- Game ships `0Harmony.dll` + `Common.Modding.Mod` class -> official mod API exists;
  mods can ship C# assemblies + Harmony patches. BepInEx 5 also viable (Mono).
- **`FlyingWormConsole3.LiteNetLib.*` is bundled** (full LiteNetLib incl. `NatPunchModule`,
  `NetSerializer`) inside the shipped ConsolePro debug console -- usable transport, or
  bundle our own copy.
- Architecture (from type dump, ~8700 classes):
  - Custom **ECS**: `Common.Commands` (`CreateEntityCommand`, `AddComponentCommand`,
    `ActionCommand`), `Common.CommandHistory` (`CommandStack`, `ExecuteCommand` event,
    `UndoCommand`/`RedoCommand`, `BasicCommandHistorySystem`) -- player actions appear to
    flow through a command stack = the interception point for multiplayer.
  - Layers: `Strategy.*` (geoscape), `GroundCombat.*` / `Xenonauts.GroundCombat.*`
    (tactical), `Xenonauts.AirCombat.*` / `AirCombat.*` (interception minigame).
  - Faction/controller model: `Common.Mechanics.Factions` -- `PlayersGroup`, `TeamsGroup`,
    `ControllerLink`, `ControllingLink`, `IsControlledByFactionMemberAttribute`;
    `Xenonauts.GroundCombat.TurnOwnerPlayerTag` -- engine already models "which player
    controls this unit / owns this turn" -> per-soldier ownership can map onto this.
  - **Serialization**: `Common.Serialization` (FullSerializer `fsDataFormatter`,
    `JsonStringPrinter`, `SaveGameSystem`, versioned save processors) -- campaign and
    battle state are JSON-serializable -> initial sync + resync = ship the save.
  - AI: `GCAISystem`, `ACAISystem`, `AITurn*` events -- AI turns are event-marked
    (guest's sim must let host drive these).
  - Turn flow: `Common.Boards.System.TurnAct`, `CombatantTurnStatusSystem`,
    `AITurnStart/EndEvent`.
- `um scan` does not auto-detect GOG installs -- pass the folder path.
- KB: no Xenonauts notes -- we are first; write a field note at the end.

## Open questions / risks
- Exact boundary between `Common.*`/`Strategy.*` vs `Xenonauts.*` namespaces (partial
  refactor in flight upstream?). Hook points need decompile-level verification.
- Air combat (real-time interception minigame) -- separate sim, its own sync path; likely
  a later slice or solo-host-sim + guest watches.
- GOG Galaxy SDK (`Galaxy64.dll`) could give lobby/NAT traversal, but adds SDK complexity;
  start with direct UDP (LiteNetLib NatPunch / manual port-forward / Hamachi).
- Guest "puppet mode" feasibility = the core technical risk: can guest's sim be driven
  purely by replayed host commands/events? Verify early with a decompile read of the
  command stack + one slice.

## Decisions locked with user (2026-10-05)
- Scope: **shared single campaign** (user confirmed twice -- Kenshi/RWT are technical
  references only, NOT the parallel-worlds gameplay model).
- Approach A: host-authoritative command relay; guest's world is a mirror.
- Slices: S0 lab+harness - S1 connect+save transfer+read-only geoscape mirror -
  S2 tactical co-op (own-soldiers, same player turn) - S3 geoscape co-command -
  S4 air combat (risk-flagged, may degrade to host-only) + publish.
- Spec for S0+S1: `specs/2026-10-05-xenonauts2-coop-design.md`.
- 2 players only; friend needs own GOG copy; connect via IP (LAN broadcast / manual).

## Next step
User reviews spec -> writing-plans -> implement S0+S1.

## Spec review -- 2026-10-05
- Full decompile at `C:\Users\chuan\xenonauts2-decomp\full\` (ilspycmd -p).
- Verdict **REVISE** -> `reviews/2026-10-05-spec-review.md`. Code loading confirmed (ContentPackState.Patch:
  Assembly.Load + Harmony.PatchAll + IContentPackLifecycle). Blockers: per-tick whole-world snapshot infeasible
  (ticks every 16-25 ms; deserialize = DeleteAllEntities); strategy actions are direct [GameAPI] calls, not events;
  boot.config `single-instance=`; join/transition/save-transfer protocol gaps; criteria 4/5 not measurable.
- Plan for S0+S1 deferred until spec revised.
- Spec v2 written (same file): snapshot feed -> component-delta replication at
  `World.ApplyComponentMutation` (D1->D2->D3 degrade path); mirror mode = clock freeze
  (UpdateTimeOnBase) + mutation firewall + save suppression; join consistency via
  host clock pause + baselineSeq; new protocol msgs (sessionId, GuestReady, Resync,
  HostLeftStrategy/Returned); guest saves -> `Saves\x2coop_guest\`; boot.config
  single-instance dev workaround; single-assembly rule; no log4net ERROR (PostHog);
  S3 premise flagged (actions are [GameAPI] calls, not events).

## Spec re-review (r2) + plan -- 2026-10-05
- r2 verdict **PASS** -> `reviews/2026-10-05-spec-review-r2.md` (B1-B5 resolved; concerns C1-C7 carried
  into plan). New finding: `Artitas...EntityConverter` deserializes refs via `ForceCreateEntity(id)` ->
  guest must decode deltas with own fsSerializer (`CommonSerializationSystem.SetupSerializer(s, null)` +
  mapping converter).
- Plan: `plans/2026-10-05-s0-s1-implementation-plan.md` (Phases 0-6, D3 floor at end of Phase 3,
  Phase 5 replication spike time-boxed 5 days with DP1/DP2 degrade points; ~25 dev days).

## Implementation log -- 2026-10-05

### T0.1 Project scaffold -- done
- `src/X2Coop.csproj` (SDK-style, net472, `LangVersion=latest`, `DebugType=portable`),
  `src/Properties/AssemblyInfo.cs` (0.1.0 single source), `src/ModVersion.cs`
  (`X2Coop.ModInfo.ModVersion` const), `.gitignore` (`bin/ obj/ .dev/ .vs/`).
- `GameDir` <- env `X2_DIR`, default `E:\GOG Games\Xenonauts 2`; refs via `$(ManagedDir)`.
- **fsSerializer lives in `Assembly-CSharp-firstpass.dll`** -- `grep "class fsSerializer"` in the
  decompile only finds *uses* (`using FullSerializer;` in e.g.
  `Common.Content.Managers/ContentManager.cs`); `ilspycmd -l c` on the Managed DLLs shows
  `Class FullSerializer.fsSerializer` under firstpass. Already in the brief's reference list.
- Gotcha: shipped `0Harmony` 2.3.6 targets **net48**, so RAR silently dropped it -- and
  `Assembly-CSharp` too (indirect dep) -- from a net472 compile (MSB3274/MSB3275). Types were
  unresolvable despite a green build. Fixed with
  `ResolveAssemblyReferenceIgnoreTargetFrameworkAttributeVersionMismatch=true`; harmless under
  Unity Mono. Verified with a throwaway `typeof(HarmonyLib.Harmony)` / `fsSerializer` /
  `Common.Modding.Mod` probe file (deleted after).
- Verify: `dotnet build -c Debug` clean (0 warn/0 err); `GetReferencedAssemblies()` -> `mscorlib`
  only (csc strips unused refs); `bin/Debug/net472/` = `X2Coop.dll` + `X2Coop.pdb` only.

### T0.2 RECON R1 -- pack manifest + `assembly/` sub-path -- done
- **`assembly/` sub-path answer: `assembly/common/`** -- and it is scanned *recursively*.
  Decompile chain: `XenoAssetConfigPrototypes.cs:59-62` maps `Assembly`->prefix `"assembly"`
  (postfix `""`), `.dll`->`Assembly`, `AssemblyPDB`->same prefix + `.pdb`;
  `Common.Content/AssetConfigPrototypes.cs:97` registers `AddScreenMapping(null, "common")`
  (null screen -> `common` infix); `AssetPath.cs:239` `ConstructPrefixPath` builds
  `prefix/screenOffset[/postfix]` -> `assembly/common`. The actual scan is
  `ContentPackState.Patch` (`Common.Content.DataStructures/ContentPackState.cs:544`):
  `AssetReference.Directory<Assembly>("", ..., SearchOption.AllDirectories, screen: null, ...)`
  -> `Directory.GetFiles(<pack>/assembly/common, "*", AllDirectories)` filtered to `.dll`
  (`FileHandle.List`, `Common.Content/FileHandle.cs:203`). `assembly/<file>.dll` at the root
  is NOT valid -- the segment after `assembly/` must be a screen offset
  (`AssetDirectoryDescriptor.Construct`, `Common.Content/AssetDirectoryDescriptor.cs:216`),
  and `Patch()` only queries the `common` screen. Other screen dirs
  (`assembly/strategy/`, `assembly/groundcombat/`...) are addressable but never scanned.
  `.pdb` next to the `.dll` is auto-loaded as debug symbols (`AssemblyLoadProcessor.cs:24,44-50`).
  Each loaded assembly gets `Harmony.PatchAll` + `IContentPackLifecycle` scan
  (`ContentPackState.cs:554-572`).
- **Pack layout produced by in-game authoring:** `$PROF\Mods\<guid-lowercase>\` containing
  exactly `manifest.json` + `preview.png` (256x256 placeholder) -- no `assembly/` dir is
  created. Folder name == manifest `UID` (`ContentPackAuthoring.CreateLocalPack`,
  `Common.Content.Authoring/ContentPackAuthoring.cs:16-27`). Discovery =
  `FileSystemContentPackDiscoverer` over `$PROF\Mods\`, manifest must sit exactly one level
  deep (`FileSystemContentPackDiscoverer.cs:36-73`).
- **manifest.json** (committed at `pack/manifest.json`, verbatim game output):
  `{ "version": "0.1.0", "asset": { Source:"Manual", UID:"6e6bc764-960e-4370-a3ff-e0d5d66828ce",
  WorkshopID:"", Name:"X2 Co-op", Description:"", Visibility:"Private", Author:"", Website:"",
  Credits:"", Tags:[], Dependencies:[{UID:"7409d306-8813-4ae2-b2e2-8e94d4dc39f1",
  Version:{"&v":"7.27.11"}, DisplayName:"Xenonauts"}], LoadBefore/LoadAfter/IncompatibleWith/
  RemoveFiles:[], SupportedLanguages:["en-US"], Version:"0.1.0", "$t":"14" }, "$t":"15" }`.
  FullSerializer-typed JSON -- `$t`/`&v` tags left untouched; `Name` set to `X2 Co-op` at
  creation (typed into the in-game dialog). Core-pack dependency UID
  `7409d306-...39f1` = the game's `core` pack (contentpacks.json).
- **UI path:** main menu MODS -> MOD MANAGER -> DEVELOPER MODE toggle (confirm YES -- it gates
  `createButton`, `ContentPackMenuElement.cs:721`) -> CREATE MOD -> name input -> ACCEPT
  (`CreateMod` -> `CreateLocalPack`, `ContentPackMenuElement.cs:391-421`). Pack immediately
  listed in the manager (screenshot `.dev/evidence/r1-11-packcreated.png`).
- **Deploy note for T0.3:** pack dir name should be the manifest UID --
  deploy to `Mods\6e6bc764-960e-4370-a3ff-e0d5d66828ce\` (already exists with
  manifest+preview; drop `assembly/common/X2Coop.dll` in).
- **Gotcha: launch CWD matters.** `um win launch` (cmd `start`) starts the exe with the
  caller's working dir; the game reads `contentpacks.json` via a *relative* path
  (`AssetConfigPrototypes.cs:19` -> `ContentPackRegistry.Load`) -> null registry ->
  TypeInitializationException, hangs at 0% loading. Fix: launch with
  `-WorkingDirectory "E:\GOG Games\Xenonauts 2"` (Start-Process). Same for any later tasks.
- Game exited cleanly after capture (external quit -- `OnApplicationQuit` in r1-game.log:45);
  benign `ContentRuntimeException ... AssetReference.ContentManager` on the quit path.
  Evidence + tree: `.dev/evidence/`; profile snapshot `um backup xenonauts2-profile`.

### T0.3 deploy.ps1 -- done
- `deploy.ps1` (PS 5.1-compatible): guard -> `dotnet build -c Debug` -> copy `pack\*` +
  `bin\Debug\net472\X2Coop.{dll,pdb}` -> dedup -> print file list with sizes.
- **Deploy target: `$PROF\Mods\x2coop\`** (controller ruling, plan-literal), NOT the
  UID-named folder R1 suggested. Safe because discovery is manifest-driven, not
  folder-name-driven: `FileSystemContentPackDiscoverer.Discover` accepts any
  `*.json` exactly one level deep under `Mods\`
  (`FileSystemContentPackDiscoverer.cs:36-73`); identity = manifest `asset.UID`.
  DLL lands at `x2coop\assembly\common\X2Coop.dll` (recursive scan, R1).
- **Dedup:** after copying, reads our UID from `pack\manifest.json` at runtime and
  removes any OTHER `Mods\*` folder whose manifest carries the same UID -- a
  duplicate UID would double-list/conflict the pack in the menu. Removes the
  game-authored `6e6bc764-...` folder (manifest+preview) on first run. Folders with
  missing/unparseable `asset.UID` are skipped, never deleted.
- **Guard:** aborts exit 2 with the PID list if `Xenonauts2` process exists (DLL
  lock; running game wouldn't reload anyway). Never kills.
- `-Clean` wipes `x2coop` first; `-Configuration`/`-ModsRoot` overridable.
- Verified: run 1 deploys 3 files + dedups the UID folder; run 2 hash-identical
  (idempotent); abort path tested with a renamed `ping -t` as fake Xenonauts2 ->
  reported PID, exit 2, process untouched; `-Clean` + mixed-UID/malformed-manifest
  folders behave (dup removed, others kept).

### T0.4 Hello-world lifecycle + logger -- done
- `src/X2CoopLifecycle.cs : Common.Content.Lifecycle.IContentPackLifecycle` (decompile-verified
  signatures: `Create(ContentPackState, Harmony)`, `Destroy()`,
  `OnWorldCreate/OnWorldDispose(Section, WeakReference<Artitas.World>)`,
  `IEnumerable<Common.Content.Descriptor> GetRequiredAssets(Section)`; `Section` is a nested
  enum of the interface). Every body try/catch -> own `Log` -- escaping exceptions hit
  log4net ERROR = PostHog (r2 C5). Stores `pack.path` (never `Assembly.Location` -- the
  loader uses `Assembly.Load(byte[])`), `patcher.Id`, the `Harmony` handle.
- `src/Log.cs` (`X2Coop.Log.Info/Warn/Error/Exception`): each line -> `Debug.Log` + file
  `x2coop-<pid>.log` in `Constants.Folders.Logs()` ($PROF\Logs, self-creating) + **a third
  channel**: `UnityLogHandler.OriginalLogHandler.LogFormat(LogType.Log,...)`.
- **Big engine finding:** the game installs `Xenonauts.Common.Logging.UnityLogHandler` as
  `Debug.unityLogger.logHandler`, routing every `Debug.Log` into log4net -- whose root level
  is ERROR (`Assets/Configuration/log4net.xml`). So INFO `Debug.Log` NEVER reaches `-logFile`,
  `Player.log`, or `Logs\output.log`; only ERROR+ does -- and ERROR also hits
  `AnalyticsLogAppender` -> PostHog `$exception` + `BugReportSystem`. `Debug.LogError` is
  therefore BANNED for routine logging. The saved `UnityLogHandler.OriginalLogHandler`
  (Unity's stock handler, public property) writes straight to `-logFile` at INFO -- same
  trick the game's own `UnityLogAppender` uses -- zero analytics exposure. First build
  proved it: `Create` was in `x2coop-<pid>.log` but absent from `-logFile` until the third
  channel was added.
- **Pack enablement:** `Settings\contentpacks.json` still had `Path` = old UID folder, but
  `ContentManager.ReconcilePreferences` re-binds by UID and rewrites `Path` -- pack stayed
  enabled and patched at boot; no MOD MANAGER trip needed.
- **Verify (real game):** `-startScreen=Strategy` boots directly into a new campaign
  (XenonautsMain.cs:529-541 -- no save existed, menu world doesn't fire lifecycle events;
  `OnWorldCreate` only fires for Strategy/GroundCombat/AirCombat/AirCombatSimulation
  screens). Results, PID 15424: `Create path=.../Mods/x2coop uid=6e6bc764-... asm=-2123915240
  harmony=6e6bc764-...@.../x2coop ver=0.1.0 logfile=.../x2coop-15424.log` then
  `OnWorldCreate section=Strategy world=789366336` (+11 s) -- present in BOTH
  `-logFile` (t04-game.log:38,43) and `x2coop-15424.log`. `OnWorldDispose section=Strategy`
  (same world hash) fired on clean Alt+F4 quit. `asm` differed across runs/copies.
- **net472 HELD** -- no TypeLoadException; no fallback needed.
- `Destroy` is not called on app quit (game skips `Unpatch()` at shutdown).
- Evidence: `.dev/evidence/t04-*.png`; backup `xenonauts2-profile` snapshot before first
  modded launch. All three launches used `Start-Process -WorkingDirectory <gamedir>` (the
  contentpacks.json-CWD hang rule).

### T0.5 RECON R2 -- enabled by default? restart needed? -- done
- **Answers: NOT enabled by default; a RESTART prompt appears after APPLY but
  hot-patch/unpatch already happened in-session (restart is advisory, not required for
  `Create`/`Destroy`).**
- Preference file: `$PROF\Settings\contentpacks.json` -- plain JSON
  `{ "Packs": [ { UID, Path, Enabled } ] }` (serialized `ContentPackSettings.Packs`,
  `Xenonauts.Common.Settings/ContentPackSettings.cs:11`).
- **Enabled-by-default = NO.** `ContentManager.ReconcilePreferences`
  (`Common.Content.Managers/ContentManager.cs:1494-1509`): discovered packs missing from
  preferences are appended with `Enabled = (item8.type != ContentPackType.Mod)` -- Mod
  packs default **false** (Core/DLC types default true). `DiscoverContentPacks` also
  `config.AddMod(text, active: false)` for new packs (ContentManager.cs:1391).
  **Verified live (PID 12744):** deleted our pack's entry from `contentpacks.json`,
  launched -- the game *rewrote the file at boot*, re-adding our pack with
  `"Enabled": false`. No `x2coop-12744.log` was created, no `Create` in `-logFile`;
  MOD MANAGER listed `X2 Co-op` with an unchecked box (`.dev/evidence/t05-02`).
- **Restart prompt = YES, after APPLY, but conditional on "changed since boot".**
  `SAVE & CLOSE` -> `Apply` -> `DoApply` -> `RefreshContentPacks` live-applies changes:
  newly-enabled packs run `CreateIndex->Patch->ParsePatches` in-session
  (`ContentManager.cs:1616-1632`; `Patch` invokes `IContentPackLifecycle.Create`),
  disabled packs get `Unpatch()`->`lifecycle.Destroy()` + `Harmony.UnpatchAll`
  (`ContentPackState.cs:608-654`) or `Disable()` (`:122-133`). Then
  `ContentPacksChangedSinceLoad` (active UID+path set vs the boot signature captured by
  `MarkContentPacksLoaded`, `ContentManager.cs:161-171,1642-1644`) gates
  `PromptContentPackRestart` (`ContentPackMenuElement.cs:343-371`): dialog "RESTART
  REQUIRED -- Mods or content packs have changed. Restart Xenonauts 2 for changes to
  take full effect." with QUIT TO DESKTOP (=ExitGame) and CANCEL.
- **Verified live:** same boot (PID 12744) -- enable+APPLY: `Create` logged
  14:51:14 in `x2coop-12744.log`, restart prompt shown (`.dev/evidence/t05-05`);
  disable+APPLY: `Destroy` logged 14:54:09 and **no restart prompt** -- the active set
  matched the boot signature again (`ContentPacksChangedSinceLoad=false`);
  re-enable+APPLY: `Create` at 14:56:54, prompt shown again
  (`.dev/evidence/t05-11`). So hot toggling works both ways without restart; the
  dialog is advisory ("for full effect"). Starting a NEW GAME after changing packs
  also warns -- separate dialog with CONTINUE ANYWAY
  (`NewGameParametersElement.cs:309-335`).
- **Side finding -- "MISSING DEPENDENCIES / Version Mismatch" on every enable.**
  Enabling X2 Co-op always prompts "dependencies not present. Enable anyway?
  Version Mismatch: - Xenonauts 2 Core" (YES enables fine -- warning severity only,
  `ContentPackMenuElement.cs:1234`). Cause: our game-authored manifest's core
  dependency is stamped with the *app* version `7.27.11`
  (`ContentPackAuthoring.CreateLocalPack` -> `XenonautsConstants.Versioning.VERSION`),
  but the core pack's own manifest declares `Version: 7.27.10`
  (`E:\GOG Games\Xenonauts 2\Assets\Assets\xenonauts\manifest.json`), and the check is
  minimum-version `installed >= required`
  (`ContentPackDependencyResolver.MeetsMinimumVersion`, `ContentPackDependencyResolver.cs:283-294`)
  -> always mismatches on this build. Candidate fix: hand-edit `pack/manifest.json`
  dependency `Version` to `7.27.10` -- flagged, NOT done (file is game-authored
  verbatim; would need re-checking after every game update).
- **Install-notes draft for the Phase-6 README -> `docs/install-notes.md`.**
- State restored: pack re-enabled via menu; `contentpacks.json` byte-identical to
  pre-test backup (kept at `.dev/contentpacks.json.bak-t05`); relaunch (PID 22204)
  showed `Create` at boot 14:58:58 -- same as T0.4 behavior. Game exited cleanly both
  times via EXIT GAME -> YES.
- **Post-review correction (2026-10-05):** a review pass flagged the live
  `Settings\contentpacks.json` as containing only the core entry. Re-verified same
  day: the x2coop entry IS present with `Enabled:true`, SHA1-identical to the backup
  (`971A7A0D...`, file last written 14:58:58 by the verification-run boot; game closed
  since). The review likely read the game-dir registry file
  `E:\GOG Games\Xenonauts 2\contentpacks.json` (a different file -- `{"core":...,"dlcs":[]}`,
  only ever lists the core pack). No corrective edit was needed; end state = pack
  enabled.

### T0.7 Probe patches -- done
- `src/Probes/ProbePatches.cs` -- whole file under `#if X2COOP_PROBES`; the symbol is
  defined only for Debug via `<DefineConstants>` in the csproj (`deploy.ps1` builds
  Debug; `-c Release` verified compiling clean with probes stripped).
- Patches are applied MANUALLY in `X2CoopLifecycle.Create` via
  `patcher.Patch(original, postfix: new HarmonyMethod(...))` -- not `[HarmonyPatch]`
  attributes -- so we get explicit `probes: patch-applied`/`method not found` log
  lines. (The game's own `PatchAll(assembly)` runs just before `Create`,
  `ContentPackState.cs:554`; attributes would work too, manual is more observable.)
- Targets (decompile-verified): `Xenonauts.Strategy.Systems.TimeTransitionSystem
  .UpdateTimeOnBase(DeltaTimeEvent)` -- public `[Subscriber]` (TimeTransitionSystem.cs:117);
  `Artitas.World.ApplyComponentMutation(int,int,IComponent,IComponent,bool)` -- private
  (World.cs:213), resolved via `AccessTools.Method(typeof(World),"ApplyComponentMutation")`.
  Mutation counter keyed by `World.Name` -- exists, `public string Name { get; set; }`
  (World.cs:71).
- Each postfix logs `patch-hit <Type.Method>` once on first call; a `probe-summary`
  line fires every 5 s of wall clock (Stopwatch inside the postfix -- lazy cadence,
  only emitted when a patched method runs). `time`=all UpdateTimeOnBase calls;
  `timeActive`=calls where the public `[GameAPI] IsPaused()` (TimeTransitionSystem.cs:172)
  returns false; `mutations`=ApplyComponentMutation calls, split per `World.Name`.
- **Verified live (PID 32248 tutorial campaign + PID 29340 no-tutorial campaign):**
  both `patch-applied` lines at `Create`; `patch-hit` on `ApplyComponentMutation`
  ~1 s after boot (menu bootstrap) and on `UpdateTimeOnBase` when the Strategy world
  came up -- NEITHER METHOD IS INLINED. Running geoscape (slowest speed):
  `mutations` ~+870/s, `timeActive` ~+120/s. ESC -> GAME MENU pause: `mutations`
  FROZEN EXACTLY (90367 constant for 20+ s; world Process fully suspended -- the
  ~120 mut/s seen during modal dialogs is modal UI churn, not the floor).
  RESUME -> counts climb again. Second frozen window under the quit dialog
  (128807 constant, 20 s), then `OnWorldDispose` + clean exit to desktop.
- Bonus: air-combat interception (tutorial run) produced `AirCombatWorld` counts
  separate from `StrategyWorld` (+~4300/s during live combat) -- per-`World.Name`
  demux proven. `MainMenuWorld`/`LoadingWorld` also appear; a handful of mutations
  land under `<unnamed>` (worlds before `Name` is set).
- Gotcha: game's tutorial interact-block (`StrategyTutorialSystem.BlockInputByDefault`
  gates `pause_menu` + all non-tagged buttons) made the scripted pause test easier
  with a NON-tutorial campaign; `um win` client coords = screenshot_px / 1.25
  (125 % DPI, 2560x1440 capture <-> 2048x1152 client).
- Evidence: `.dev/evidence/t07-*.png`; logs `.dev/logs/t07-game.log`,
  `t07b-game.log`, `x2coop-32248.log`, `x2coop-29340.log` (copied from $PROF\Logs).

### T0.6 RECON R3 -- Assembly.Load(byte[]) "neither" context side effects -- done
- `src/Probes/ReconProbes.cs` (whole file `#if X2COOP_PROBES`): `Run()` from `Create`
  logs one `recon r3-N` line per check, plus an attribute-style Harmony patch
  (`R3AttrProbePatch`, `[HarmonyPatch(typeof(World),"ApplyComponentMutation")]` +
  `[HarmonyPostfix]`) that only the game's own `PatchAll` (ContentPackState.cs:554)
  can have applied. Live run PID 9476 (`-logFile` -> `.dev/logs/r3-game.log`,
  own log `.dev/logs/r3-x2coop-9476.log`).

| # | Check | Result (PID 9476) |
|---|-------|-------------------|
| 1 | `Assembly.Location == ""` | **PASS** -- `recon r3-1 asm-location=""` on both `Create`s. `pack.path` is the only filesystem truth. |
| 2 | Duplicate load on MOD MANAGER off->on | **NO second copy** -- `Destroy` 16:21:25, `Create` 16:22:16 with the **SAME** `asm=596646860` and `recon r3-2 x2coop-copies=1`. The pack's Assembly asset stays cached in the content manager (`cm.IsLoaded` guard, ContentPackState.cs:546-551), so re-enable reuses the same Assembly object. Same-copy corollary: probe statics persisted across the toggle (`patch-hit attr` did NOT re-log; `_hit` was already true). *Teardown guidance unchanged but inverted:* Destroy must still destroy everything (statics/GameObjects survive as the SAME objects), and a true second copy remains possible if the asset cache is ever dropped (then `fsTypeCache` name lookups resolve to the FIRST-loaded copy -- scan order, see below). |
| 3 | Harmony `[HarmonyPatch]` via game's `PatchAll` | **PASS** -- `recon r3-3 patch-hit attr Artitas.World.ApplyComponentMutation` at 16:18:49 (~1 s after Create, menu bootstrap) -- no manual `patcher.Patch` call exists for that class. Attribute patching works on a byte[]-loaded assembly (alongside T0.7's manual-patch proof on the same target). |
| 4 | Game-asm refs resolve w/o `AssemblyResolve` | **PASS** -- `recon r3-4` resolved `Artitas.World`, `FullSerializer.fsSerializer`, `HarmonyLib.Harmony` at JIT time (Locations = real Managed\*.dll paths); nothing threw; we install no hook. |
| 5 | `.pdb` next to dll -> line numbers | **PASS** -- deliberate `InvalidOperationException` logged with `at X2Coop.Probes.ReconProbes.CheckStackTraceLines () [0x00002] in E:\universal-modder\...\ReconProbes.cs:145` (both own log and `-logFile`; also note the leaf frame `ThrowForStackTrace` was JIT-inlined -- only the callsite frame shows). |
| 6 | `fsSerializer` instantiates our types | **PASS** both paths -- r3-6a explicit storage type: `TrySerialize(typeof(ReconDto),...)`/`TryDeserialize(...,typeof(ReconDto),...)` round-trips `{"IntValue":42,"StrValue":"x2coop-neither","Nums":[7,8,9]}`, `fields-match=True`. r3-6b object storage type emits `"$type":"X2Coop.Probes.ReconDto"` and resolves it **by name** -- `fsTypeCache` indexes `AppDomain.GetAssemblies()` + `AssemblyLoad` events, which include neither-context assemblies. `resolved-same-copy=True` here; on a real duplicate load the first-loaded copy's type would win the name scan -- never rely on name-based lookup for cross-copy data (T5.3: `AddConverter` + explicit types only). |

- **Save seeded:** `Saves\purple_knight_0db3fda4\user_r3seedday1-1.json` (267 KB) --
  in-game name typed as "r3seedday1" (save field strips hyphens), Soldier
  difficulty, no tutorial, base placed in Africa, Day-1 geoscape.
- Evidence: `.dev/evidence/t06-*.png` (menu -> mod-manager toggle -> geoscape ->
  save dialog). Game exited via GAME MENU -> QUIT GAME -> DESKTOP,
  `OnWorldDispose section=Strategy` logged, no errors.
- Note: opening MOD MANAGER itself raised the "MISSING DEPENDENCIES / Version
  Mismatch" dialog once (same core-pack 7.27.11-vs-7.27.10 check T0.5 found);
  NO dismissed it without state change.

### T0.8 RECON R4 + two-instance launcher -- done
- `tools/two-instance.ps1` (PS5.1): `-Patch` backs up
  `$GameDir\Xenonauts2_Data\boot.config` -> `boot.config.x2coop.bak` (first run
  only, never overwritten) and removes the `single-instance=` line byte-exactly
  (file is UTF-8 no-BOM, LF endings); `-Restore` copies the backup back and
  SHA256-verifies; `-Launch` starts host then guest with
  `-logFile .dev\logs\{host,guest}.log` + `-screen-fullscreen 0 -screen-width
  1280 -screen-height 720`, 10 s stagger, prints both PIDs; `-GameDir` default
  = env `X2_DIR` else `E:\GOG Games\Xenonauts 2`. Warns if `single-instance=`
  still present or instances already running. User pre-approved the patch.
- **R4 answer: with `single-instance=` present, instance 2 exits.** PID 38712
  terminated ~2 s after launch, **ExitCode 0** -- Unity's Force-Single-Instance
  handoff fires before the `-logFile` is even opened (`r4-second.log` was
  never created). Instance 1 (PID 22604) kept running as the sole
  Xenonauts2 process/window. So: hand-off + clean exit, no error dialog.
- **Patched run: both instances reach the main menu simultaneously.** Host
  PID 6184 + guest PID 4384 ran side by side ~5 min. `host.log`/`guest.log`
  each contain `[X2Coop] Create` + `patch-hit` (own per-PID logs
  `x2coop-6184.log`/`x2coop-4384.log` in $PROF\Logs too) -- the pack loads in
  BOTH processes. Gotcha: both windows came up 2048x1152 @ (0,0) -- the game
  overrides `-screen-width/-height` with its saved display settings; move
  windows via SetWindowPos/`um win drive` when non-overlap is needed (they
  fully overlap by default).
- **GoG/Galaxy in instance 2: no errors -- and none expected.** Zero
  GoG/Galaxy lines and zero log4net ERRORs in `guest.log` (noise profile
  identical to `host.log`; PostHog queue stayed empty). Caveat:
  `GoGCorePlatformModule` logs init/signin failures at **Warn**, which log4net
  root=ERROR suppresses entirely -- a soft Galaxy failure would be log-silent
  (State=Failed -> module no-ops; menu still reached). GOG Galaxy client IS
  installed+running here (GalaxyClient 8784, GalaxyCommunication 16604), so
  `Init`/`SignInGalaxy` plausibly succeeded in both. Non-blocking either way:
  workshop/achievements only.
- **Player.log isolation confirmed.** `-logFile` redirects the Unity player
  log completely -- `$PROF\Player.log` untouched across all three launches
  (mtime 06:51, SHA1 A39D7E23..., 16040 B from a 6 AM run).
- **Restore verified byte-for-byte:** after `-Restore`, `boot.config` SHA256 =
  `4480DAFA3C2A...` = pre-patch hash, `single-instance=` back. Re-patched after --
  **final state: PATCHED** (two-instance needed for all later dev). Backup
  `boot.config.x2coop.bak` stays in the game dir, untracked, never committed.
- `xenonauts2-profile` snapshot taken before first launch. Both instances
  killed by exact PID (`um win kill`) after evidence capture.
- Evidence: `.dev/evidence/t08-*.png` -- host/guest menu shots are
  pixel-identical (static title screen, 0/16761 samples differ >30); the
  minimized-window capture timed out proving gfxcapture is truly per-hwnd, so
  the guest shot is the guest's own window at menu. Logs: `.dev/logs/host.log`,
  `guest.log`, `r4-first.log` (instance-1 baseline).

### R5 Entity identity + converter -- done, decision **(a)**

**DECISION (a): identity IDs + `ForceCreateEntity` for new host entities.** The
guest-local entity create rate on a live geoscape is **zero** -- measured on the
true allocation chokepoint (`EntityRecyclingFactory.Obtain(World)`), not just
`World.CreateEntity` -- over ~4 minutes of steady-state across three runs,
including ~50 s of active mouse panning. No explicit host->guest map needed.

- **ID allocation (EntityManager.cs, `EntityRecyclingFactory`):** `nextId`
  bumps monotonically; `Free(id)` pushes freed IDs onto a `limbo` Deque
  (:43-47); `Obtain(world)` reuses `limbo.RemoveFromFront()` unless limbo is
  empty or `world.IsMutationDelayed()` (:78-95) -- **yes, there is a free-ID
  pool and IDs are reused after deletes**; `generations[id]` is bumped on
  `MarkAsRecycling` (:37-41) so stale-ID detection exists if we ever need
  (id,generation) refs. `Obtain(world,id)` (the ForceCreateEntity path,
  :54-76) grows `nextId` to `id+1` and pushes every gap ID into limbo marked
  recycled -- sparse host IDs are safe to resurrect.
- **Wire form confirmed** (`Artitas.Serialization.Converters/EntityConverter.cs`):
  `TrySerialize` writes `new fsData(entity.ID)` -- bare int (:43); `CreateInstance`
  accepts `{$content:id}` or a number (:22-33), returns the live entity or calls
  `ForceCreateEntity(id)` (:34-38). `TryDeserialize` is a no-op -- all work is in
  CreateInstance. `internal ForceCreateEntity` (EntityManager.cs:185) ->
  `Obtain(world, entityID)`.
- **UID-keyed alt converter** (`Common.UID.Systems/UIDSystem.cs:22-57`): writes
  `entity.UID().value` (int, not int-ID); `TryDeserialize` resolves via
  `GetEntityWith(uid)` -- a linear `FirstOrDefault` scan over the UID family
  (:102-122), returns **null** on miss (NO resurrection). It's a separate
  fsSerializer on `UIDSystem.Serializer` (:81-92) used by `Common.Commands`
  (undo/command snapshots), not the save path.
- **UID coverage on StrategyWorld = 0% (measured).** UIDSystem auto-assigns
  UIDs only to `Any<Transformation,RegionGroup>` entities (NeedsUID,
  CommonArchetypes.cs:180) -- and StrategyWorld has **zero** such entities
  (geoscape positioning lives in GeoscapePositioningSystem, not map
  Transformation). StrategyScreen registers `GuidManagerSystem` + `ScopedGuidSystem`
  (StrategyScreen.cs:223,303), **not** UIDSystem. Census: `u=0 g=221 t=0
  a=1722` -- 0 UID, 221 GuidComponent carriers, 0 eligible, 1722 alive.
  The save's gzip worldData blob corroborates (no UID-component data).
- **Probe:** `ProbePatches` gained `World.CreateEntity`, the true chokepoint
  `EntityRecyclingFactory.Obtain(World)` (catches the Template factory path --
  Template.cs:26421/26456/26479 bypasses `World.CreateEntity` via
  `world._entityManager.CreateEntity()`), `EntityManager.ForceCreateEntity`,
  and a per-world alive/UID/Guid/Transform census in the 5 s summary.

| Run | Window | World.CreateEntity | Obtain(World) | ForceCreateEntity | uid census (StrategyWorld) |
|-----|--------|--------------------|---------------|-------------------|-----------------------------|
| r5 PID 18064 | load -> ~160 s (75 s idle + ~48 s hover/pan drags) | 372 (4 unnamed, 3 MainMenu, 365 Strategy) | n/a (probe added later) | 1364 (all during save load) | u=0 g=221 t=0 a=1722 |
| r5b PID 3216 | load -> ~90 s idle | 372 (identical) | n/a | 1364 | u=0 g=221 t=0 a=1722 |
| r5c PID 10424 | load -> ~85 s idle | 372 (identical) | **376** (4 unnamed, 3 MainMenu, 369 Strategy) -- frozen after load | 1364 | u=0 g=221 t=0 a=1722 |

- Steady-state deltas after load finished (last summary during load ~16:58:20 ->
  +85 s): **creates +0, obtains +0, forced +0** on every world, while
  `mutations` kept ~870/s and `timeActive` ~120/s -- the world was unpaused and
  live; the create/forced counters simply never moved. Totals are
  bit-identical across three processes -> load-time creation is deterministic
  (same on host and guest from the same save).
- Caveats for S1+: (i) day-1 geoscape only -- later-game entity churn
  (missions spawning UFOs, craft, notifications) is *host* strategy logic the
  mirror replicates, not guest-local creation, but re-verify on a richer save;
  (ii) if a guest-side UI path ever allocates (e.g. a transient
  template-instantiated toast), the design should still tolerate/log a stray
  guest-local obtain -- cheap defense, not required by the measurement;
  (iii) `Obtain(world,id)`'s gap-fill puts every skipped ID into limbo -- a
  ForceCreateEntity(2000) on the guest makes IDs <2000 recycled-reusable, so
  the limbo pool stays host-consistent only if host and guest share the same
  obtain history (they do: same save + mirrored deltas).
- Evidence: `.dev/logs/r5-game.log`, `r5b-game.log`, `r5c-game.log`;
  `.dev/evidence/r5-0{1,2,3,4,5}-*.png`. Game killed by PID (18064/3216/10424).

### R6 Whitelist discovery - guest-local UI/camera mutations - done, **12 serializable types**

**Whitelist written to `src/Mirror/Whitelist.cs`** (`HashSet<Type>` via `typeof()`;
all 12 observed types are public in Assembly-CSharp, `TypeNames` string fallback
left empty). `Whitelist.Active = true` kept on for Debug probe builds - the probe
now logs `whitelist-miss <type>` for any paused serializable mutation outside the
set; Release strips probes entirely (`#if X2COOP_PROBES`), shipping is unaffected.

**Method (probe changes, `ProbePatches.cs`):**
- `MutationPostfix` now binds `entityID`/`componentID` by name to the original
  args; on `StrategyWorld` only, resolves the type via the public reverse map
  `World.ComponentManager.FindComponentType(componentID)`
  (`ComponentManager.cs:52` -> `ComponentLookup._idToType`,
  `ComponentLookup.cs:20-23`). Forward map `FindComponentId` NOT used for the
  exclusion-id lookup: its `DynamicComponentLookup` override REGISTERS a missing
  type as a side effect (`DynamicComponentLookup.cs:9-18`), so
  `ExcludeFromSerializationComponent`'s id is read from `Lookup.TypeToId`
  (a copy - no side effects) and cached per world.
- Flags per type: `X` = `[ExcludeFromSerialization]` on the component class
  (type never serialized, WorldState.cs:77 - can never be a whitelist
  candidate); `E` = seen at least once on an entity carrying
  `ExcludeFromSerializationComponent` (entity skipped, WorldState.cs:131).
- Pause predicate: the stored `TimeTransitionSystem` instance's own
  `[GameAPI] IsPaused()` queried at mutation time - covers the `_pauseCounter`
  path (ESC menu, modals, select-target, non-geoscape screens) AND the
  not-in-`StrategyStates.Turn` path. Unknown -> counted unpaused (conservative:
  misses only under-count paused, never over-count).
- Emits every 5 s: `probe-types paused=N n=M types={Full.Name=total/paused[!flags]}`
  (sorted for diffing) and `probe-whitelist active=N types=N miss={...}`.

**Pause semantics (decompile, drives session design):** there is NO
"speed 0 = pause" - `SetSpeedTo(0)` is the SLOW speed
(`StrategyConstants.ADVANCE_TIME_RATE_PER_TICK_SPEEDS[0]` = 1 s game / 20 ms
real). `TimeControlCommand.Pause()` (`_pauseCounter++`) is sent by:
`GeoscapeMainElement.OnExit` (:416, i.e. EVERY screen off the geoscape -
base/research/engineering/soldiers/stores/armory/aircraft/xenopedia - runs
paused by construction; `OnEnter` resumes + `SetSpeedTo(0)`),
`GeoscapeBaseBuildElement`/`GeoscapeSelectTargetElement`/
`GeoscapeSelectGeoRegionElement` (paused while the map stays interactive),
all modal elements, Settings/Save/Load menus, `PauseMenuElement` (ESC).
ESC GAME MENU additionally suspends world Process entirely: `mutations` frozen
at exactly 194281 for 20+ s (even the timer below stops) - so under ESC there is
nothing to discover; the pause-counter pauses are where the data lives.

**Session (PID 20636, user_r3seedday1-1.json, ~9 min):** select-target mode
(paused): camera drags (map visibly panned), wheel zooms, hovers over base
marker + map points; every top-bar screen; MANAGE SCIENTISTS/ENGINEERS panels;
RECRUIT SOLDIERS incl. checkbox selects + column sorts; CASUALTY LIST; armory
soldier-select + ARMOUR/LOADOUT dropdowns; xenopedia category expands; base
grid empty-plot select + building-list hovers; ESC menu. Nothing confirmed.

**Result (discovery run; `paused` = mutations while IsPaused()):**

| Type | total/paused | flags | Why it mutates paused |
|---|---|---|---|
| `Xenonauts.Common.Components.CampaignPlayTimeComponent` | 65541/42205 | | wall-clock playtime timer, ~120/s - ticks through pause-counter pauses (stops only under ESC). Timer-driven, NOT interaction-driven - flagged for T4.1 |
| `Artitas.Components.EntityListenerComponent` | 802/524 | | UI listener (re)registration churn on paused screens |
| `Xenonauts.Strategy.Components.MainBase.CreationTimeComponent` | 409/52 | !E | base-screen creation/countdown bookkeeping |
| `Common.Components.GuidComponent` | 47/47 | | GUID assign/churn on screen-state entities (all observed paused) |
| `Common.Components.RotationComponent` | 46/46 | | 3D preview spin (aircraft/armory viewers; all observed paused) |
| `Xenonauts.Strategy.Components.StrategyUIStateComponent` | 52/38 | !E | which strategy screen/state is open |
| `Common.Components.Generic.ReadComponent` | 13/13 | | "marked read" flags (xenopedia browse). Borderline gameplay state - flagged for T4.1 review |
| `Artitas.Components.ExcludeFromSerializationComponent` | 364/3 | !E | marker add/remove churn on already-excluded entities |
| `Common.Components.GameObjectComponent` | 360/3 | !E | also `IOptionalSerialization` (per-instance opt-out) |
| `Strategy.UI.Components.UI.GeoscapeScreenComponent` | 354/3 | !E | geoscape screen targets |
| `Xenonauts.Common.Components.Biome` | 1/1 | | biome lookup on hover (component form of the enum; disambiguated from `Artitas.Biome` data enum) |
| `Xenonauts.Strategy.Components.GeoRegionComponent` | 1/1 | | region hover/select bookkeeping |

Paused mutations seen on types already non-serialized (`!X`, NOT whitelisted -
can never appear in host state): `UIControllersComponent` 357/3 (!XE),
`GeoscapeIconParentLink` 354/3 (!XE), `GeoscapeIconsLink` 118/1 (!X),
`GeoscapePositionAnchor` 354/3 (!XE).

**Negative-space sanity:** every game-time component showed ZERO paused
mutations - `GeoscapeDateTimeComponent` 8503/0, `DurationComponent` 68190/0,
`GeoscapeClockStepsComponent` 14/0, `PositionComponent` 34012/0,
`VelocityComponent` 17006/0. The sim-clock set is clean; paused mutations are
exclusively UI-side.

**Notable:**
- Camera pan/zoom while paused produced ZERO `PositionComponent`/
  `VelocityComponent` mutations - camera transform does not flow through
  `ApplyComponentMutation` while the world is not processing (the unpaused
  position churn is radar/animation entities, not the camera).
- **+3 creates / +52 obtains during paused screen browsing** (428 obtains vs
  375 creates - template-path allocations bypass `World.CreateEntity`, mostly
  limbo-pool re-obtains of UI state entities). R5's "idle guest never creates"
  stands, but UI browsing DOES churn pooled entities - flag for T4.1: the
  whitelist covers component types, not entity churn; the mirror still needs
  the tolerate-and-log defense for stray guest-local obtains.
- `E`-flagged entries (`StrategyUIStateComponent`, `CreationTimeComponent`,
  `GameObjectComponent`, `GeoscapeScreenComponent`,
  `ExcludeFromSerializationComponent`) were observed on excluded entities -
  whitelisted anyway as belt-and-braces for any serialized carrier.

**Verify (PID 31148, `Whitelist.Active = true`, condensed re-run):** select-target
camera pan/zoom/hover, all eight top-bar screens, manage/recruit panels, column
sorts, checkbox select, xenopedia categories, ESC menu -> **ZERO `whitelist-miss`
lines; `probe-whitelist ... miss={}` in every 5 s summary.** Whitelist is
complete for the measured interaction set.

Evidence: `.dev/logs/r6-game.log`, `r6-x2coop-20636.log`, `r6-verify-game.log`,
`r6-verify-x2coop-31148.log`; `.dev/evidence/r6-*.png`, `r6v-*.png`.
Games killed by PID (20636/31148). `Whitelist.Active` stays `true` in Debug so
whitelist-miss keeps guarding; reset to `false` if probe noise is ever unwanted.

### R7 `ProcessStrategy` concrete type -- done, observed **`DebugProcessStrategy`**

- `World.ProcessStrategy` is a public property of abstract type
  `Artitas.ProcessStrategy` (ProcessStrategy.cs:7); backing field
  `_processStrategy` (World.cs:45), property World.cs:79-90, assigned from
  `config.ProcessStrategy` in the ctor (World.cs:102). The decompile default is
  `DefaultProcessStrategy` (`Artitas/DefaultProcessStrategy.cs:9`;
  `WorldConfiguration.cs:110` lazily news one).
- **Observed live: `strategy-process type=DebugProcessStrategy` -- NOT the
  expected `DefaultProcessStrategy`.** `StrategyScreen.cs:209` picks
  `new DebugProcessStrategy()` whenever `Artitas.Event.ENABLE_PROFILING` is true
  at world-build time -- and it is true during EVERY LoadScreen-driven setup:
  `LoadScreen` wraps the setup/promise `Update` calls in
  `using (ProfilerOptions.Set(true,true,true[,true]))`
  (`Common.Screens.DataStructures/LoadScreen.cs:239,247,341,420,444`), which sets
  `Event.ENABLE_PROFILING` for the duration (`Artitas.Core.Utils/ProfilerOptions.cs:41`,
  restored on Dispose). `StrategyScreen.InitializeWorld` builds the world AND
  fires `ContentManager.OnWorldCreate` inside that scope (:207-212), so the
  shipped game's Strategy world always gets `DebugProcessStrategy`. (The other
  setter, `ProfilerOverride` in XenonautsMain.cs:377-384/1161-1168, is
  `Constants.IsEditor`-gated; `DebugSystem.cs:581-583` can also swap it in at
  runtime when ENABLE_PROFILING -- same outcome.)
- `DebugProcessStrategy` (`Artitas/DebugProcessStrategy.cs:11`) subclasses
  `DefaultProcessStrategy`: same subscriber-dispatch loop, plus
  `ArtitasLogger.RecordEvent(queuedEvent)` on every event, indent-tracked
  Info/Debug delegation tracing (silenced -- `ArtitasLogger.cs:355` forces the
  `ProcessStrategy` logger to Warn), an `OnProcessDebug` profiler callback after
  each `Process()`, and an `IgnoreProcess` flag that makes `Process()` skip the
  world entirely -- a potential single-flag freeze lever worth noting for S3.
- Probe: one `#if X2COOP_PROBES` block in `X2CoopLifecycle.OnWorldCreate`
  resolves the `WeakReference<World>` and logs `ProcessStrategy.GetType().Name`
  when `section == Strategy`. S1 does not patch ProcessStrategy; recorded for S3.
- Verify (PID 2672, `Saves\purple_knight_0db3fda4\user_r3seedday1-1.json` via
  main menu LOAD GAME -> row -> LOAD SAVE): `OnWorldCreate section=Strategy
  world=76897376` + `strategy-process type=DebugProcessStrategy` at 17:39:01,
  present in BOTH `r7-game.log` (-logFile) and `x2coop-2672.log`.
- Evidence: `.dev/logs/r7-game.log`, `r7-x2coop-2672.log`;
  `.dev/evidence/r7-0{1,2,3,4}-*.png`. Game killed by PID (2672).

## Recon R8 - 2026-10-05 - custom save write + load path (feeds T3.2/T3.3)
Answers to the four recon questions (decompile: C:\Users\chuan\xenonauts2-decomp\full):

1. **Can `ContentManager.Save(asset, new FileDescriptor(path, typeof(SaveGame)),
   JsonPrinterSaveParameters(compress:true), overwrite:true, cacheAsset:false)` write
   outside `Constants.Folders.Save()`?** YES - proven live. `FileDescriptor` ctor
   stores the path verbatim (Common.Content.Descriptors/FileDescriptor.cs:17-21, no
   validation); `FileDescriptorResolver` (:10-18) passes rooted paths straight to
   `FileHandle.CreateFileSystemFileHandle(..., Path.IsPathRooted(text))`. Save root
   is a convention (Constants.cs:374-382), not a sandbox. Live: probe wrote
   `$PROF\x2coop\tmp\probe.json` (215,786 B, AssetState.Loaded).
2. **Does `SaveGameSystem.LoadGame(FileDescriptor, bool)` accept a descriptor under
   `Saves\x2coop_guest\`?** YES - proven live. LoadGame (SaveGameSystem.cs:740-783)
   only requires `ContentManager.Exists`, which is `descriptor.FileHandle.Exists()`
   (ContentManager.cs:1087-1101) - any resolvable path. Live: `Exists=True` on
   `Saves\x2coop_guest\x2coop_probe-1.json`, LoadGame -> RequestMoveTo ->
   `OnWorldCreate section=Strategy world=-1029289872` (new world, load landed).
3. **`SaveGameRequest.state` = `Optional<WorldState>`** (SaveGameSystem.cs). Plain
   `new SerializationRequestCommand()` works: `State` unset = None, and
   `ArtitasSerializationSystem.HandleSerialization` falls back to
   `CopyWorldIntoWorldState` on None. No SaveGameRequest needed.
4. **`PreSaveGameReport`/`PostSaveGameReport` have NO subscribers** - repo-wide
   decompile grep finds only the two event classes and the two dispatch sites
   (SaveGameSystem.cs:405,565). No achievements/cloud-sync side effects. We still
   fire them (harmless, keeps parity with the real path).

**GOTCHA (the hard part):** `SaveGameSystem.SAVE_PROFILER` must have a LIVE
section before `ContentManager.Save`. `SaveGameConverter.DoSerialize`
(Common.Content.DataStructures/SaveGameConverter.cs:57-102) calls
`SAVE_PROFILER.Mark()`, and `Profiler.Mark` -> `Stop()` THROWS on an empty
section stack ("No sections started"; Artitas.Core.Utils/Profiler.cs:177-194).
`AssetTask.Process` (Common.Content.Tasks/AssetTask.cs:74-86) swallows the
exception into `TaskStatus.Failed`, so `Save()` returns `AssetState.Failure`
with nothing thrown - first run failed exactly this way. Fix: mirror the real
path - `SAVE_PROFILER.Reset(); SAVE_PROFILER.Start(...)` before the save block,
`Stop()`/`StopAll()` after (SaveGameSystem.cs:403-404,566).

Probe: `src/Probes/SavePathProbe.cs` (`#if X2COOP_PROBES`), F8 polled inside a
postfix on `TimeTransitionSystem.UpdateTimeOnBase` (same tick context as queued
saves); mirrors InternalProcessSaveGameRequest minus screenshot + group-driven
naming. `SerializationRequestCommand` -> `SaveGame` wrapper (USER group +
GameScreens=Strategy + 4 metadata Prefix + ResolveIdentity) -> two
`ContentManager.Save` calls under one `EnterSaveSerialization`, then
`SaveGameSystem.LoadGame(guestFd,false)`.

Live verify (PID 6164, loaded `user_r3seedday1-1.json`):
- `r8 save begin: geodate=2020-01-01 00:49:24` ; serialize ok
- `outside=Loaded exists=true len=215786` ; `guest=Loaded exists=true len=215786`
- `Exists(guestDescriptor)=True` -> LoadGame -> `r8 verify: geodate-before=
  2020-01-01 00:49:24 geodate-after=2020-01-01 00:49:30` (world resumed and
  ticked ~6 real seconds between world-create and first observed tick; the
  file's own GeoscapeDateTimeInformationSegment reads 00:49:24 = exact restore).
- Saves\ tree diff (SHA-256, before/after): ONLY
  `x2coop_guest/x2coop_probe-1.json` ADDED; seed save byte-identical
  (448D7135...). No host save touched, nothing outside x2coop_guest changed.
- Evidence: .dev/logs/r8b-game.log (run-2 success), r8-game.log (run-1 failure
  showing the swallowed profiler exception), .dev/r8-saves-{before,after}.json,
  .dev/shots/r8b-*.png. Game killed by PID 6164.
- Full report: .superpowers\sdd\2026-10-05-s0-s1-implementation-plan\task-R8-report.md

## Recon R9 - 2026-10-05 - replication volume per time speed (feeds T5.2)
Measured mutations/frame + serialized bytes (one sample per type) on a
host-style StrategyWorld session (single-player = host emission), 60 s per
speed 0-3, using the game\'s own `ArtitasSerializationSystem.Serializer`
(`fsSerializer` + `EntityConverter`/`WorldStateConverter`/`ValueComponentConverter`).

Probe: `src/Probes/ReplicationVolumeProbe.cs` (`#if X2COOP_PROBES`), F7
start/abort (F8=ToggleLogging, F9=QuickLoad - both taken). Postfixes:
`World.ApplyComponentMutation` (op counting + first-seen per-type `Copy()`
sample, never mutates world) and `TimeTransitionSystem.UpdateTimeOnBase`
(frame boundary via `Time.frameCount`, `IsPaused()` tracking, F7 poll,
window advance via `TimeControlCommand.SetSpeedTo` + `Resume(true)`).
Serialization: `TrySerialize` -> `fsJsonPrinter.CompressedJson` -> UTF-8 bytes;
bytes/frame = sampleBytes x muts/type/frame; envelope +16 B/op.

Results (60 s windows; paused = modal/defeat ticks):
- Speed 0 CLEAN x3: 36,485 / 36,542 / 47,314 B/s payload, env 50.4-63.7 KB/s
  (within/at 64 KiB edge). ops/f p50/p95 = 1/16-19, B/f p95 701-878.
- Speed 1: CLEAN (run C) = **165,803 B/s, env 182.5 KB/s - 2.5x over budget.**
  Runs A/B windows were 86-90% paused by day-1 modals (lower bounds only).
- Speed 2 CLEAN (run A) = 52,063 B/s payload (62,674 muts), env **68,775 B/s
  - 1.05x over**; `[ExcludeFromSerialization]` types filtered ~284 ops.
- Speed 3 = **182,875 / 184,000 B/s while 89% PAUSED** (event modals + ATLAS
  defeat cascade); replicated within 0.6% across runs. True unpaused rate is
  higher -> >=2.8x over budget.
- Top bytes: `ActivityStateMachineComponent` 6,743 B x ~1,000-1,500 muts =
  72-93% of window bytes (per-10-min update batches); `DurationComponent`
  59 B x ~24-33k; `CampaignPlayTimeComponent` 18 B x 7,196 (every frame, incl.
  paused heartbeat); `PositionComponent` 39 B x 12k.
- Serialization safety: `serializer=ok`, serfail=0, nosample=0, <=29 ms/window.

**Verdict: budget EXCEEDED** at speed >= 1. T5.2 brief gained a type-level
throttling addendum: coalesce per (entity,type) to latest-state per send tick,
rate-cap >1 KB types, 1-2 Hz unreliable channel for client-derivable
timers/positions, fixed 10-15 Hz flush instead of every LateUpdate.

Concerns: day-1 save has scripted modals (Cleaners 11:00, ATLAS 16:53 ->
Game-Over at day-4 16:53); speed-3 cannot get a fully-clean window on this
save - reported value is a pause-suppressed lower bound. Bytes are
sample-size x count estimates. Evidence: `Logs\x2coop-{35720,27356,12760}.log`,
`.dev\logs\r9-game{3,4,5,6}.log`, `.dev\shots\r9g{3,4,5,6}-*.png`.
- Full report: .superpowers\sdd\2026-10-05-s0-s1-implementation-plan\task-R9-report.md

### T2.1 NetTransport + main-thread runner - done (2026-10-05)

**Files:** `src/Net/Protocol.cs`, `src/Net/NetTransport.cs`, `src/Runtime/Runner.cs`;
`X2CoopLifecycle.Create` -> `Runner.EnsureStarted()`, `Destroy` -> `Runner.Shutdown()`;
`tools/two-instance.ps1` gained `-CoopAutoStart` (adds `-x2coop-host` to host args,
`-x2coop-join` to guest - probe/Debug-only dev triggers parsed on first Runner.Update;
H/J/K hotkeys are the fallback: H=host :27960, J=join 127.0.0.1, K=stop).

**API surface (T2.2 codec / T2.3 session consume):**
- `NetTransport`: `StartHost(port)` / `StartGuest()` / `Connect(ip,port)` (guest auto-
  starts ephemeral socket), `Send(peerId, channel, data, off, len, DeliveryMethod)` +
  `Broadcast`, `Disconnect(peerId)`, `PollEvents()`, `TryDequeue(out NetEvent)`, `Stop()`;
  `Role/IsRunning/PeerCount/LocalPort/ServerPeerId/PeerIds/IsConnected`.
- `NetEvent{Kind: Connected|Disconnected|Message, PeerId, Data[], Delivery, Reason}` -
  single main-thread queue; Data is a copied buffer (AutoRecycle=false ->
  `reader.Recycle()` after copy).
- `Protocol`: `Version=1`, `ConnectKey="x2coop/1"` (static-readonly built from Version -
  cannot drift), `DefaultPort=27960`, `MaxPayloadSize=64KiB`,
  `MaxMessageSize=64KiB+256B` envelope, `ChannelControl=0` / `ChannelSave=1`
  (`ChannelsCount=4`; save stream gets its own channel so a transfer can't
  head-of-line block control/deltas).
- `NetManager` config: `DisconnectTimeout=15000`, `UnsyncedEvents=false` (events queue
  for main-thread PollEvents), `AutoRecycle=false`, `NetDebug.Logger` rerouted to
  `Log.Warn` (`lnl ...`) - LNL errors otherwise die silently inside Debug.Log.
- Runner seams: `Update`->`PollEvents`+drain->`DispatchNetEvent`->`NetEventReceived` event;
  `LateUpdate`->`FlushReplication()` (T5.2); `OnGUI`->`DrawCoopPanel()` (T2.4).

**Fragmentation finding (bundled FlyingWormConsole3.LiteNetLib ~0.9.x):**
`NetPeer.SendInternal` AUTO-FRAGMENTS ReliableOrdered + ReliableUnordered payloads over
MTU - (mtu-10)-byte fragments, up to 65535 fragments (~60-90 MiB theoretical);
Unreliable/Sequenced/ReliableSequenced throw `TooBigPacketException` past MTU.
Reassembly in `NetPeer.AddReliablePacket` (IsFragmented) delivers ONE message.
**64 KiB ReliableOrdered works as-is** (~47-65 datagrams at MTU 1024-1432).
Chosen cap `MaxMessageSize=65792` (64 KiB + 256 B envelope): bounds per-message
reassembly buffers and fragment-burst resend stalls; send-side warns+drops above it.

**Verify (PIDs 11048 host / 35392 guest, `-CoopAutoStart`, menu, ~80 s session):**
- host `net host-start port=27960 key=x2coop/1`; guest `net guest-start port=54997`,
  `net connecting peer=0`, `net connected peer=0 role=Guest`; host `net connected peer=0
  role=Host` - "connected peer=<id>" on BOTH sides, ~0.4 s handshake on loopback.
- two-way data proof: `dev: sent devping` + `net msg peer=0 len=14 delivery=
  ReliableOrdered` on both sides. MTU discovery observed climbing 1024->1432.
- `net rtt peer=0 rtt=0ms mtu=1432` every 2 s on both for the whole session (rtt climbs
  honestly to ~1 s as the killed peer's pings go unanswered - smoothed ping*2).
- disconnect path: guest `um win kill` -> host `net disconnected peer=0 reason=Timeout`
  ~15.5 s later. Zero lnl/WARN/ERROR lines on either side.

**Windows Firewall outcome: NO new prompt - already allowed.** Inbound Allow rules for
`xenonauts2.exe` (one TCP, one UDP, `Query User{guid}` consent rules, Private+Public)
pre-existed - the classic signature of a Windows Security Alert answered Allow at some
earlier date. The UDP :27960 bind succeeded first attempt; no dialog was visible in
`um win ps` during the run and no agent action was needed. (Loopback bypasses WFP
regardless - LAN/WAN joins ride on that existing consent.)

**Gotcha noted:** `Application.runInBackground` - Runner sets it true when false
(logs `runner: runInBackground was false -> set true`); neither instance logged it, so
the game ships true - but an unfocused instance would otherwise stop pumping
PollEvents and eat the 15 s timeout.

Evidence: `.dev\logs\host.log`, `.dev\logs\guest.log`,
`.dev\logs\x2coop-{11048,35392}.log`, `.dev\evidence\t21-host-connected.png`.
Both instances killed by exact PID. Report: task-T2.1-report.md.

## Recon R10 - 2026-10-05 - mutation-delay context at the apply seam (feeds T5.x)

Question (r2 C3): when Runner.Update/LateUpdate execute - the future
StateApplier apply points - is the Strategy world mid-mutation
(World.IsMutationDelayed()) or mid-tick (inside World.Process())?

**Answer: NEITHER, ever.** Sampled every frame on both Update and LateUpdate
over a full menu -> load -> unpaused geoscape -> ESC pause-menu -> resume
session (PID 13644, save x2coop_guest/x2coop_probe-1.json, day-1 geoscape):

- `r10 update ... n=73980 delayedN=0 inprocN=0 stratInprocN=0 paused=15210
  unpaused=51378 ...` (log EOF 20:48:12; late shows identical counts) -
  world-bound = paused+unpaused = 66,588 samples per phase (~133k across
  Update+LateUpdate); ZERO delayed, ZERO in-process, ZERO transitions
  (trans=0).
- `r10 process n=95400 strat=88023 reentry=21462 delayedAtEntry=0
  delayedAtExit=0 maxDepth=2 tid=1 otherTid=0 updateTid=1` (EOF) -
  World.Process() runs on ManagedThreadId 1 = the same thread as
  Runner.Update (tidU=1); never observed on another thread (otherTid=0),
  never entered/exited with mutation delay held (delayedAtEntry/Exit=0).
- Reentrancy confirmed: 21,462 reentrant Process calls (maxDepth=2) - the
  inner `base.World.Process()` drain inside TimeTransitionSystem.UpdateTimeOnBase
  (TimeTransitionSystem.cs:156) plus the PhasedFSMSystem loop; reentry froze
  at 20,034 for the whole pause-menu window (UpdateTimeOnBase early-returns
  when paused) and resumed on unpause - mechanism attribution confirmed.
- World still ticks under the ESC pause menu: strat Process calls keep
  ~1/frame while paused (IsPaused()=true via _pauseCounter>0 from
  PauseMenuElement.OnEnter -> TimeControlCommand.Pause()).

**Implication for StateApplier (T5.x):** apply deltas from Runner.Update or
LateUpdate directly - the world is never mid-cascade there, no queue-drain
trick needed. IsMutationDelayed() is only true inside a DelayedMutationStack
Push/Pop scope (mutation application inside event dispatch inside Process) -
all on the same main thread, so Update can never interleave. The in-process
flag was deliberately a shared static (not ThreadStatic) so a hypothetical
off-thread Process would still have been caught; none was. Phase-1 exit:
nothing in R10 contradicts the T5.x apply-point design; no revision needed.

Probe: `src/Probes/MutationDelayProbe.cs` (`#if X2COOP_PROBES`) - Harmony
prefix+postfix on `Artitas.World.Process()` (non-generic, parameterless,
World.cs:561) maintaining depth counters + entry/exit delayed samples; the
Runner Update/LateUpdate call `SampleUpdate()`/`SampleLateUpdate()`. World
bind: X2CoopLifecycle.OnWorldCreate(Strategy) -> NoteStrategyWorld (same path
R7 used), Process-prefix Name=="StrategyWorld" as rebind, OnWorldDispose ->
ForgetStrategyWorld. Pause split via cached TimeTransitionSystem.IsPaused()
(GetSystem resolves lazily - systems register after OnWorldCreate; first
attempt returned null, fixed by per-sample retry).

Verify (PID 13644, x2coop-13644.log): `r10 probes: patch-applied
Artitas.World.Process (prefix+postfix)` at 20:37:43.825 (l.18);
`patch-hit Artitas.World.Process tid=1` at 20:37:45.123 (l.23);
`OnWorldCreate section=Strategy world=434985856` + `strategy-process
type=DebugProcessStrategy` + `r10 strategy-world-bound hash=434985856
src=lifecycle tid=1` + `r10 time-sys-resolved hash=434985856`, all at
20:38:50.18x-22x (l.291-294); strategy-process=DebugProcessStrategy is the
R7 result replicated. Sample counters above from x2coop-13644.log
20:37-20:48.
- Caveats: 6 sample errors (err=6) during the load-transition window
  (half-built world), zero after; `paused?` bucket = menu/noWorld samples
  where no TimeTransitionSystem exists - expected, not a defect.
- Evidence: `.dev\logs\r10-game2.log`, `.dev\logs\x2coop-13644.log`,
  `.dev\shots\r10-{20..26}-*.png`. Game killed by PID (13644).
- Full report: .superpowers\sdd\2026-10-05-s0-s1-implementation-plan\task-R10-report.md

### T2.2 Message codec - done (2026-10-05)

Wire format (protocol v1, little-endian via `NetDataWriter`): every message is
`[MsgType:1][sessionId:16][payload]`; payloads use `int32`-length-prefixed
UTF8 strings/byte blobs (the same layout `NetDataWriter.Put(string)`/
`PutBytesWithLength` produce) with per-field caps checked BEFORE allocation.
`MsgType` values are explicit (1-10 + 20-21 for the T5.5 `HashReq{seq}`/
`HashResp{seq,hash,count}` pair, implemented now so later protocol additions
never shift meanings). `sessionId` is a 16-byte Guid: Empty is the
pre-session sentinel; the codec lets `Hello{Empty}` through at any time and
`HelloAck` while the receiver expects Empty (its envelope carries the host's
assignment - guest adopts it). Everything else must equal the expected id.
Drop reasons: ShortBuffer/Oversize/UnknownType/SessionMismatch/Malformed/
TrailingBytes - all logged `codec drop: ...`, never thrown, never logged
with payload contents (playerName travels inside Hello but is never logged).

Channel policy lives in the codec (`MsgCodec.ChannelFor`): SaveOffer+SaveChunk
on `ChannelSave` (offer must stay ordered ahead of its chunk stream),
everything else on `ChannelControl`; all ReliableOrdered.

Runner wiring (T2.2 scope): every inbound buffer decodes through
`MsgCodec.TryDecode(evt.Data, _sessionId, ...)`; decoded messages go to a new
`MessageDecoded(int, NetMessage)` event for T2.3. Ping/Pong is the real 2 s
keepalive now (PumpKeepAlive -> Ping, Ping -> Pong echo, Pong logs rtt; the
probe-only "x2coop-devping" byte string is replaced by a codec Ping on
Connected). HelloAck-accept adopts the session id; disconnect/HostEnded
clears it. `B` hotkey (probes only) runs `CodecSelfTest.Run()` and logs
`codec ok n/n` - the in-game fallback path exists but was NOT needed.

Verify path chosen: **xunit** (`tests/X2Coop.Tests`, net472). Gate question
was whether `NetDataWriter`/`NetDataReader` load outside Unity - decompile
shows pure managed code (System.* only; no static ctor, no UnityEngine refs),
so the test project links the codec sources directly and references
`Assembly-CSharp.dll` (Private=true) only. A `X2Coop.Log` stub captures log
lines for assertions; UnityEngine is NOT referenced.

Evidence:
- `dotnet build src\X2Coop.csproj -c Debug` green (0 warn/0 err); `-c Release`
  green (probes strip cleanly).
- `dotnet test tests\X2Coop.Tests`: **16/16 passed** (78 ms). Covers: all 12
  registered MsgTypes round-trip byte-identical (encode -> decode -> encode),
  `codec ok 12/12` + `codec drops ok 4/4` via CodecSelfTest, field-level
  Hello checks incl. UTF8 player name + null campaignId, drop+warn on
  foreign sessionId / unknown type 200 / truncation / mutated enum byte /
  trailing bytes / corrupted string length, pre-session rules (Hello{Empty}
  and HelloAck adoption pass; Ping{foreign} dropped), encode rejects a
  64KiB+1 chunk and an over-cap string, an exactly-64KiB SaveChunk
  round-trips at 65,565 B total (< MaxMessageSize 65,792), channel map.

Wire notes for T2.3+/T5.x: seq fields are int64 (headroom over int32 frame
counters); SaveOffer.ByteCount/SaveChunk.Offset int64; sha256 fixed 32 B;
entityID int32 (Artitas `Entity.ID` is int); component type names are
length-prefixed UTF8 <= 256 B; delta ops capped at 4096/batch and 32 KiB/op;
SaveChunk data <= 64 KiB; encode never truncates - an over-cap field fails
the whole encode with a warn (silent truncation would corrupt pack uids /
type names).
- Full report: .superpowers\sdd\2026-10-05-s0-s1-implementation-plan\task-T2.2-report.md

### T2.3 Handshake + parity - done (2026-10-05)

Session layer wired on the Runner's decoded/event streams (main thread only):
guest sends Hello on transport Connected; host answers HelloAck accept or
reject+human-readable Detail. HelloAck gained a bounded (256 B) Detail
string so the guest can print the host's reasons verbatim.

Hello fields (sources mirror SaveGameSystem.InternalProcessSaveGameRequest,
SaveGameSystem.cs:497-506): protocolVer=Protocol.Version,
modVer=ModInfo.ModVersion, gameVer=XenonautsConstants.Versioning
.VERSION (observed 7.27.11), commitHashes[4]=Commits.Instance
{Artitas,Assets,Common,X2Core} in enumerator order, playerName (GoG persona
via PlatformManager.Core as GoGCorePlatformModule -> x2coop.playerName ->
"player"; transported, NEVER logged), activePackStamps via
ContentPackDependencyResolver.BuildStamps(GetConfig().GetAllActiveContentPacks()
.Select(p => p.Manifest.Get())) -> uid@version pairs.

Host reject order: SessionFull (1 guest max) -> parity mismatch
(VersionMismatch / PackMismatch, detail names field + both values) ->
NotOnStrategy (StrategyPresence seam: live, undisposed "StrategyWorld" bound
by lifecycle OnWorldCreate/OnWorldDispose; T3.1 ScreenGuard hardens it into a
screen check). Parity outranks screen state deliberately - a version mismatch
is not fixable by waiting. Rejected peers stay CONNECTED (no kick) so a retry
can succeed later; their in-session traffic would SessionMismatch-drop anyway.
Accept order: Runner.SetSessionId(sid) FIRST (guest keepalive Pings carry
sid - would drop otherwise), MirrorState.HostJoinFreeze = true (spec 6
host-pause seam; T3.2 owns lifecycle, T3.4 owns the clock patch - T2.3 sets
the flag only), then HelloAck{sid}. Re-Hello from the accepted peer re-sends
the SAME sid (idempotent; rekeying would desync).

x2coop.* dev settings (src/DebugSettings.cs): env x2coop.<key> or cmdline
-x2coop.<key>=<v> / -x2coop.<key> <v>; cmdline wins; active overrides logged
once (PII keys masked). Knobs: fakeModVer, fakePackStamp (appends uid@ver),
playerName, joinRetryMs (guest Hello retry cadence after reject; 0=off).
two-instance.ps1 gained -HostArgs/-GuestArgs for per-instance args.

Verify (live, loopback, host PID 43684 loaded user_r3seedday1-1.json via
CONTINUE, guests under .dev/logs\):
- Matched builds: guest "session guest: HelloAck accept sid=9a31fa18"
  (guest.log); host "session host: HelloAck accept peer=0 sid=9a31fa18
  modVer=0.1.0 gameVer=7.27.11 packs=2" (host.log 21:54:44).
- -x2coop.fakeModVer=0.0.0 on guest: host reject VersionMismatch +
  detail="mod version 0.0.0 != 0.1.0"; guest prints "join rejected:
  mod version 0.0.0 != 0.1.0" (guest-fakever2.log 21:57:34).
- -x2coop.fakePackStamp=x2coop-fakepack@9.9.9: PackMismatch,
  "packs differ: host-only=[] guest-only=[x2coop-fakepack@9.9.9]"
  (guest-fakepack.log 21:59:01). NOTE: real one-side-only pack toggling is
  infeasible on ONE machine - both instances share the profile's
  contentpacks.json - so the injected stamp stands in for an extra pack.
- Incidental coverage: NotOnStrategy rejects at main menu (host.log
  21:50:09-21:50:37 x8, guest retried via joinRetryMs=4000 until the save
  loaded), SessionFull reject at 21:56:08 (a new Hello arrived before the
  killed guest's 15 s timeout cleared the slot).
- dotnet test tests\X2Coop.Tests: 27/27 green (11 new: HelloAck.Detail
  round-trip, parity compare order/details, DebugSettings arg parse).

Known benign: one "codec drop: Pong session=none" right after accept - the
guest's in-flight Empty-sid keepalive races the sid assignment; transport-level
LNL pings keep the link up regardless.

### T2.4 CoopUI IMGUI panel - done (2026-10-05)

`src/UI/CoopPanel.cs` (Unity-side IMGUI) + `src/UI/CoopPanelState.cs`
(game-free banner/status state machine, linked into the test project like
SessionParity). Runner: attach/detach in Awake/OnDestroy, `PollInput()` in
Update (GetKeyDown is unreliable inside OnGUI's multi-pass pump), `Draw()` via
the existing OnGUI -> DrawCoopPanel seam, plus `LastRttMs` (Pong-measured,
reset when the last peer leaves) surfaced in the status line.

Hotkey: **F6**. Decompile grep of `KeyCode.` over xenonauts2-decomp\full:
game binds F1-F5 (screen shortcuts + quick_save F5), F7 (FPSTest),
F8 (CommonDebugSystem), F9 (quick_load), F10 (DebugCheatPanelFragment),
F11 (BugReporterOverlayElement + BugReportFileSystem). F6/F12-F15 appear ONLY
in `Common.InputHandling/KeyBinding.cs` GET_CODE_STRING_OVERRIDE - a
display-name map for the rebind UI, not real bindings. F12 skipped anyway
(de-facto screenshot key in Steam/overlays). Mod probes use H/J/K/B + F7/F8.

Drawn surfaces (all IMGUI, default skin):
- status strip (always on screen): "X2-Coop <role/state> sid=<short>
  rtt=<n>ms | <last notice> [F6]" - screenshot-safe by construction.
- banner (guest-only, centered, top): "Spectating host" while in-session,
  "Host busy" (amber) on SessionNotice.HostLeftStrategy until HostReturned,
  "Resyncing..." (blue) from ResyncReq-sent / inbound SaveOffer until
  SessionNotice.Resynced. Priority Resyncing > HostBusy > Spectating.
  "Spectating" is wired to in-session for now (S1 guests are spectators by
  definition); T3.3 may tighten it to MirrorState.Active once the guest
  mirror actually loads. HostLeftStrategy/HostReturned have no sender until
  T3.1 ScreenGuard - the banner side is live and unit-tested.
- F6 window: status repeat, Host button (disabled while running), Stop,
  "IP:" field + Join (connected guest relabels it "Retry Hello" ->
  SessionClient.SendHello - the T2.3 no-kick retry path). Field accepts
  "host[:port]" (default 27960; empty -> 127.0.0.1). Title-bar drag only, so
  field clicks are not swallowed.

IP privacy: the field exists only inside the toggled panel AND renders a
fixed "********" mask whenever unfocused (real text only while editing - the
length is hidden too). Never logged: Join logs port + address length only;
NetTransport logs peer ids, never endpoints. Verified live: typed
203.0.113.44 into the guest's field, defocused, screenshot shows ********
(t24-06) and no log line contains it.

New dev knob: `x2coop.panel=open|true|1` starts the panel visible (scripted
two-instance screenshot verifies; F6 still toggles).

Verify (live loopback, host PID 41184 / guest PID 8524, Debug deploy,
`-x2coop-host/-x2coop-join` + `-x2coop.panel=open` on both): host loaded
user_r3seedday1-1.json via CONTINUE click; guest auto-connected, HelloAck
accept sid=ccf5efed both sides (host.log/guest.log 22:34:18). Screenshots
`.dev/evidence/`: t24-01 host menu (panel open, "hosting :27960 (no
guest)"), t24-03 host geoscape ("hosting :27960 sid=ccf5efed guest=peer0
join-freeze rtt=25ms | connected" - Host greyed out while running, Join
greyed in host role), t24-04 guest ("guest in-session sid=ccf5efed rtt=33ms
| join accepted" + centered "Spectating host" banner + "Retry Hello"
button), t24-05 panel crops, t24-06 IP mask proof. `dotnet test
tests\X2Coop.Tests`: 38/38 green (11 new PanelStateTests: banner mapping,
priority, reject detail, resync seam, disconnect/teardown resets).
`dotnet build` Debug + Release: 0 warn/0 err (Release strips probes; the
panel is not probe-gated). csproj gained UnityEngine.TextRenderingModule
(GUIStyle.fontStyle/alignment pull FontStyle/TextAnchor).

Known benign/limitations: while the IP field is focused for editing, letters
typed are still visible to the game's own keybind systems (Unity Input is
process-wide; IMGUI cannot consume them) - only digits/dots/colon matter for
an address, and those map to strategy panel hotkeys at worst; documented
rather than engineered around. GUI.DragWindow is limited to the title strip
for the same reason. Banner "Host busy"/"Resyncing" are wired but have no
in-game trigger until T3.1/T5.x send the notices/requests (transitions
unit-tested).
### T2.5 Keepalive + disconnect - done (2026-10-05)

Session teardown paths, all on the Runner main-thread streams:

- Guest gone (timeout or close): SessionHost.OnNetEvent sees the peer's
  Disconnected event, BroadcastNotice(GuestLeft) while the sid is still live
  (S1: no other peers - forward path for multi-guest), then ClearGuest drops
  the slot, clears the sid AND MirrorState.HostJoinFreeze, and logs
  "session host: GuestLeft peer=N reason=X". A rejoin gets a fresh sid
  immediately. Runner.DispatchNetEvent now only clears the sid for the GUEST
  role - a stray non-guest peer disconnect can no longer wipe a live host
  session.
- Host ends session (panel Stop / dev K / x2coop.stopHostAfterMs):
  SessionHost.RequestStop sends SessionNotice.HostEnded FIRST, then stops the
  transport ~300 ms later from the new SessionHost.Tick (LiteNetLib's Stop()
  flushes only its own disconnect packet - a same-frame stop drops the
  reliable notice). Non-host callers fall back to an immediate Stop.
- Guest on HostEnded: logs "session guest: HostEnded - host closed the
  session", drops to Idle and gracefully disconnects the peer itself (no
  15 s timeout wait). The "Host ended session" banner (new CoopBanner
  member, top priority, red) is a latch that SURVIVES the trailing
  transport disconnect - NoteDisconnected no longer erases it; it clears on
  a fresh dial (NoteConnected) or a re-sent Hello (NoteHelloSent). Banner
  draw is hard-gated to guest role.
- Guest robustness: AwaitingAck is now bounded by x2coop.ackTimeoutMs
  (default 10000, 0=off) - a never-answered Hello can no longer wedge the
  guest in "joining".
- New dev knob: x2coop.stopHostAfterMs=<ms> auto-ends the host session N ms
  after the listener comes up (scripted teardown verifies; 0=off).
- Out of scope per plan: no guest clock freeze (T3.4), no save-stream pause
  (T3.2), no HostLeftStrategy senders (T3.1) - HostJoinFreeze stays a flag
  seam only and is cleared on guest-leave/host-stop.

Verify (live loopback, Debug deploy, host PID 23792 / guest1 PID 28428 /
guest2 PID 4368, host via -startScreen=Strategy fresh campaign):
- kill guest1 (um win kill 28428 at 23:21:07.810) -> host logs
  "net disconnected peer=0 reason=Timeout" + "net session cleared" +
  "session host: GuestLeft peer=0 reason=Timeout" at 23:21:22.855 =
  15.045 s (DisconnectTimeout=15000). Host clock/tick pipeline uninterrupted:
  probe-summary time= rises 9974->12378 and mutations= 20152->22556 across
  the window (the fresh campaign's own pre-base placement pause is
  game-owned, paused=1 before AND after - teardown introduced no new pause).
  Host stays listening - proven by the immediate rejoin.
- rejoin: guest2 connects 23:25:11 -> "HelloAck accept peer=0 sid=c7230fbe"
  both sides; NEW sid (was ae61f8d5) = host freed the slot cleanly.
- host end: dev K at 23:26:00.214 -> "session host: HostEnded -> peer=0",
  "graceful stop pending (300ms)", "stopping transport (session ended)"
  +307 ms, "net stop role=Host". Guest logs "net notice peer=0
  kind=HostEnded" + "session guest: HostEnded - host closed the session"
  23:26:00.229 and gracefully disconnects (reason=DisconnectPeerCalled,
  no timeout). Screenshot .dev/evidence/t25-guest-hostended.png shows the
  red "Host ended session" banner + status strip (banner survives the
  trailing disconnect by design).
- dotnet test tests\X2Coop.Tests: 40/40 green (3 new HostEnded state tests:
  survives teardown, outranks all, clears on reconnect/re-hello).
  dotnet build Debug + Release: 0 warn/0 err.

Known benign: on host-end the guest's own graceful disconnect can land
before the host's 300 ms deferred stop - host then logs a second
"GuestLeft reason=RemoteConnectionClose" line (the broadcast is a no-op
with one peer; ClearGuest is idempotent).

### T2.6 Teardown - done (2026-10-06)

Centralized teardown in `src/Runtime/Teardown.cs` (`Teardown.Run()`), invoked
from `X2CoopLifecycle.Destroy` (pack hot-toggle off; the game's
ContentPackState.Unpatch then does Harmony.UnpatchAll itself) and from
`Runner.OnApplicationQuit` (the game skips lifecycle Destroy at quit, so the
runner runs the same teardown on the way out).

Order inside Teardown.Run:
  1. `Runner.Shutdown` -> `Runner.TeardownNow()` SYNCHRONOUSLY (the
     Object.Destroy in Shutdown only schedules the GameObject death for
     end-of-frame; the socket must be released before Destroy returns, so the
     real work happens inline). TeardownNow is guarded by `_tornDown` and
     re-entered harmlessly by OnDestroy later.
     - Session layer first: `SessionHost.Detach` sends HostEnded while the
       socket is still alive. PACK-TEARDOWN DECISION: if the transport is a
       host WITH a guest, TeardownNow blocks the main thread for
       `SessionHost.GracefulStopDelayMs` (300 ms, now a public const) so
       LiteNetLib's logic thread can flush the reliable notice - the usual
       deferred stop via SessionHost.Tick can never fire because the runner
       dies inside the frame. 300 ms on a menu Apply / process exit is fine;
       teardown is not a hot path. No guest or not hosting -> no sleep.
     - Then `SessionClient.Detach`, `CoopPanel.Detach` (now wipes ALL panel
       statics incl. the HostEnded banner latch and typed IP - a re-enabled
       pack must start clean), `Transport.Stop`, `Runner.Instance` cleared.
  2. Detached-from-runner statics: `MirrorState.Reset()` (HostJoinFreeze +
     Active), `DebugSettings.Reset()` (argv cache + logged flag), and under
     X2COOP_PROBES: `MutationDelayProbe`, `SavePathProbe`,
     `ReplicationVolumeProbe`, `ProbePatches.Reset()` - each drops its strong
     game-object refs (World/TimeTransitionSystem/fsSerializer) while keeping
     recon counters. ProbePatches was the missed one: `_timeSys` rebinds only
     when null, so a stale system would have poisoned IsPausedSafe forever
     after a world dispose while disabled; `WorldsSeen`/`ExclIdByWorld` held
     World refs.
  3. `CleanupProfileDirs`: deletes `Folders.Save()\x2coop_guest` and
     `Folders.Local()\x2coop\tmp` (the x2coop parent dir stays). SAFETY: each
     delete runs only if the GetFullPath-normalized target resolves strictly
     inside its guard root AND ends with the expected leaf - guard root is
     saveRoot for the guest dir (correct under a -gameSaveFolder override that
     points outside $PROF), profRoot for tmp. Refusals log REFUSED, missing
     dirs log "absent".

Kept statics (deliberate, see Teardown.cs class doc): StrategyPresence
WeakReference (fed only by world create/dispose; a disposed world reports
IsOnStrategy=false via World.Disposed, so a stale ref still refuses joins
correctly - clearing it would reject legit joins after a mid-campaign
hot-toggle), Log writer (process-lifetime file), NetDebug.Logger, panel drag
position, probe counters.

Runner.EnsureStarted also got hardened for the same-frame re-enable corner:
an Instance that is mid-teardown, or a stale X2CoopRunner GameObject found
awaiting end-of-frame destroy, is dropped and replaced - a torn-down
component is never handed out again.

Verify (live, Debug deploy, PID 5820 then 35048, pack toggled via the
in-game MOD MANAGER checkbox + SAVE & CLOSE apply):

- Toggle OFF (applied 06:32:56): "Destroy path=... harmony=..." ->
  "session host detached"/"session client detached"/"ui: panel detached" ->
  "net stop role=Host" -> "runner: stopped" -> "teardown: ... absent" ->
  "teardown: complete". netstat -ano: port 27960 entries for PID 5820 gone
  (were: UDP 0.0.0.0:27960 + [::]:27960). The on-screen X2-Coop strip + panel
  vanished between screenshots - the runner GameObject is gone.
- Toggle ON: an earlier accidental cycle at 06:19:47-48 ran the full
  Destroy->teardown->Create->re-host inside one apply: "teardown: deleted
  Saves\x2coop_guest", "deleted x2coop\tmp", then Create with the SAME
  asm=-1780675200 (R3 same-assembly copy confirmed live again), "runner:
  started go=X2CoopRunner" exactly once (no duplicate-destroy warnings,
  recon r3-2 x2coop-copies=1), and "net host-start port=27960" with no bind
  error - the re-enabled pack re-hosted on the same port instantly. The
  06:32 cycle correctly logged "absent" for both dirs (idempotent).
- Game exit (PID 35048, hosting at quit): WM_CLOSE -> OnApplicationQuit ->
  Teardown.Run -> detach lines -> "net stop role=Host" -> "runner: stopped"
  -> "teardown: deleted Saves\x2coop_guest" + "deleted x2coop\tmp" ->
  "teardown: complete" -> process exits; netstat clean. (A pack that is
  already disabled at quit has no runner, so OnApplicationQuit fires nothing
  - by design.)
- contentpacks.json persisted Enabled=false after the disable-apply; restored
  to true afterwards (file edited while game off).
- dotnet build Debug + Release: 0/0; dotnet test tests\X2Coop.Tests: 41/41
  (new CoopPanelState.Reset test covers wiping the HostEnded latch).

UI-drive lesson (recorded for future verifies): X2's pointer position does
NOT follow SetCursorPos or PostMessage WM_MOUSEMOVE - clicks then land at
the game's stale cursor spot. SendInput with MOUSEEVENTF_MOVE|ABSOLUTE in a
short jitter sequence DOES register (see .dev/tools/raw-click.ps1), though
intermittently - and a latched tooltip can overlay the checkbox target.

### T3.1 ScreenGuard - done (2026-10-06)

StrategyPresence renamed + hardened into Session/ScreenGuard.cs: the game
already hands the lifecycle seam a WeakReference<World>
(IContentPackLifecycle.OnWorldCreate/OnWorldDispose, StrategyScreen.cs:211-212
sets World.Name="StrategyWorld" then raises Section.Strategy; :429 disposes it
from OnExit - every leave path: battle, debrief, quit-to-menu, save reload).
Bind filter: section==Strategy AND resolved world non-null AND
Name=="StrategyWorld" - a stray or renamed world can never fake presence
(ground combat worlds ride Section.GroundCombat anyway). Liveness =
weakref resolves AND !World.Disposed AND name matches; any exception -> false
(fail-closed: the join gate rejects rather than allows).

Transition-driven notices: Note() fires SessionHost.NotifyStrategyPresence(true)
-> HostReturned on a real off->on; Forget() fires (false) -> HostLeftStrategy
on on->off. Transition-only (wasOn guard) so an unmatched dispose or duplicate
create cannot double-send, and NotifyStrategyPresence returns early unless
runner+transport are live, role==Host and a guest holds the slot - no notices
are queued for rejected-but-connected peers (they would codec-drop them
anyway). Join gate unchanged in order: SessionFull -> parity -> NotOnStrategy.

Bug found + fixed along the way (Runner.cs DrainNetEvents): the panel Stop
button (and any other local transport Stop) drops the queued Disconnected
event silently, so the guest kept its envelope session id. The next HelloAck
- accept OR reject - then hit "codec drop: session=none expected=<old>" and a
rejoin was impossible until restart. The drain now clears _sessionId whenever
the socket is not running (superset of the disconnect-event path; the host's
own ClearGuest clear is idempotent).

Verify (live loopback, Debug deploy; host PID 7160 -> guest PID 26772, later
host PID 3548 -> guest PID 5188; logs .dev\logs\host|host2|guest|guest2.log):

- Join while on geoscape: accept sid=6714e231 (host.log 07:09:33) - existing
  T2.3 path intact.
- REAL battle entry: geoscape time at F4 spawned the ATLAS-base mission;
  host.log 07:26:03.243 "OnWorldDispose section=Strategy" -> "screen-guard
  unbound world=-1529173968 onStrategy=False" -> "session host:
  HostLeftStrategy -> peer=0"; 07:26:09 "OnWorldCreate section=GroundCombat"
  proves a true combat transition, not a menu hop. guest.log 07:26:03.255
  "net notice peer=0 kind=HostLeftStrategy"; screenshot t31-20 shows the
  "Host busy" banner with "host left strategy" in the status strip.
- Join during host battle: fresh guest connection rejected -
  "HelloAck reject peer=1 reason=NotOnStrategy detail=""host is not on the
  Strategy screen""" (host.log 07:28:32; also 07:33:39 peer=0). Note: a
  RE-Hello from the still-accepted peer re-acks the same sid by design
  (re-handshake path), so the reject test used fresh transports.
- Stale-sid regression proof then fix proof: first fresh join (old build)
  logged "codec drop: HelloAck session=none expected=6714e231" then a 10 s
  ack timeout; with the Runner fix the same flow logs "net join rejected
  reason=NotOnStrategy" + "session guest: join rejected: host is not on the
  Strategy screen" (guest2.log 07:33:39) and the strip shows the reason.
- Full leave->return notice pair with an in-session guest (host2 07:51:41):
  Esc -> QUIT GAME -> MAIN MENU: "screen-guard unbound" + "HostLeftStrategy
  -> peer=0"; guest banner "Host busy" while still in-session
  (t31-53). LOAD GAME -> "Day 1: Before Mission (Auto)": 07:52:42.274
  "screen-guard bound world=-774297248 name=StrategyWorld" + "session host:
  HostReturned -> peer=0"; guest2.log 07:52:42.281 kind=HostReturned;
  t31-57 shows "Spectating host" + strip "host returned".
- No-guest no-spam confirmed implicitly: returns at 07:37:53 and 07:41:29
  (no guest slot) logged only bound/unbound, no notice lines.

dotnet build Debug + Release 0/0; dotnet test 41/41. ScreenGuard itself is
live-verified only - the test assembly deliberately has no game refs.

UI-drive lesson #2 (recorded for future verifies): the game's virtual cursor
tracks RAW input deltas, not the OS pointer - SetCursorPos alone does not
move it, so SetCursorPos+button clicks landed at stale positions. Injecting
MOUSEEVENTF_MOVE|ABSOLUTE before the button pair fixes hover+click
(drive-pid.ps1 Click now does this); MOUSEEVENTF_MOVE relative deltas plus a
corner-pin (-RelX/-RelY + -ClickHere) give deterministic positioning.
ALSO: clicks follow Z-order, keys follow focus - with two overlapping
instances a "focused" host still misses clicks while the guest window is on
top; re-focus before clicking.

T3.2 - SaveStreamer (host) + ClockFreeze
========================================
Host side of spec section 6's accept->freeze->snapshot->stream. On HelloAck
accept (SessionHost.HandleHello): sid assigned first, MirrorState.
HostJoinFreeze latched, ack sent, then SaveStreamer.QueueOnAccept snapshots
baselineSeq = MirrorState.LastDeltaSeq (0 placeholder until T5.1 exists -
"first live delta = baselineSeq + 1" falls out for free; SaveOffer.baselineSeq
already on the wire) and queues the send ~150ms later (x2coop.offerDelayMs) -
channels do not order across each other and the guest codec would drop a
SaveOffer whose sid it has not adopted via HelloAck yet (T3.3 owns the real
fix: buffer-until-sid or sid-less offer).

World-readiness gate (found in the FIRST live run): ScreenGuard binds the
StrategyWorld at OnWorldCreate, but a save LOAD keeps streaming entities in
over the next frames - the join-retry landed inside that window and the
serialization produced a 17,682-byte near-empty save (geodate=?,
campaignId=none, playTime=0, diff=unknown). The deferred offer now waits for
WorldReady (CampaignTag entity + non-default GeoscapeDateTime) up to
x2coop.offerReadyTimeoutMs (15s default), logs "world not populated yet -
holding offer", and aborts on timeout (unfreezes; guest disconnect/rejoin
retries). Second live run logged the hold at 08:35:19.8 then serialized the
populated world at 08:35:22.6.

SendOffer mirrors SaveGameSystem.InternalProcessSaveGameRequest
(SaveGameSystem.cs:401-570): SAVE_PROFILER Reset/Start -> PreSaveGameReport
-> SerializationRequestCommand (State unset: HandleSerialization calls
CopyWorldIntoWorldState itself, R8-proven) inside AssetDescriptor.
EnterSaveSerialization() -> SaveGame with name "x2coop-<short-sid>", the
campaign USER SaveGameGroup cloned via copy-ctor + Identity|Tag "x2coop"
suffix + GameScreens=Strategy segment + the same four metadata prefixes +
ResolveIdentity(injection table); displayInject copied; version/
versionName/versionDate from XenonautsConstants.Versioning; created from
_globalVariableSystem.Get<VersionComponent>(CREATED_VERSION); 4 commit
hashes from Commits.Instance; activeContentPacks via
ContentPackDependencyResolver.BuildStamps; worldData = cmd.Data. Private
fields (_globalVariableSystem, difficultyConfigSystem, campaignLogicSystem)
read via AccessTools.Field with a world.GetSystem<T>() fallback. Writes via
ContentManager.Save(JsonPrinterSaveParameters compress:true, overwrite:true,
cacheAsset:false) to $PROF\x2coop\tmp\<sid-N>.json - OUTSIDE Folders.Save(),
so the SaveGameLimit eviction loop, Ironman hidden copy and screenshot are
all inapplicable and intentionally excluded (screenshot/map/gcParameters
stay Optional.None exactly as the real path leaves them on Strategy).

SPEC DEVIATION (zlib): spec section 6 says "chunk[64 KiB zlib]" - the
JsonPrinterSaveParameters(compress:true) output is ALREADY zlib-compressed,
so SaveChunk carries the raw file bytes with NO second compression layer
(re-zipping a zlib stream buys nothing and T3.3 would have to unwrap twice
before ContentManager could read it). Chunks go out at exactly 64 KiB on
ChannelSave/ReliableOrdered after one SaveOffer (byteCount + sha256 +
baselineSeq + campaignId + OfferSessionId = envelope sid for codec
validation).

Freeze: Mirror/ClockFreeze.cs prefixes TimeTransitionSystem.
UpdateTimeOnBase(DeltaTimeEvent) - returns false while HostJoinFreeze is
latched, so the accumulator never grows and the geoscape clock cannot
advance even if the player clicks Play (deliberately NOT the pause counter:
Resume() fires from many places; a skipped tick cannot be un-paused).
Fails OPEN on exception (logged). Ships in Release, NOT probe-gated; T3.4
extends the same prefix for the guest mirror world. Release exactly once:
GuestReady (any status - a broken guest must never wedge the host clock),
abort, disconnect or teardown via SessionHost.ClearGuest + MirrorState.
Reset. Tick logs "join-freeze geodate=... (clock held)" every
x2coop.joinFreezeLogMs while latched.

Guest side: SaveReceiver stub (T3.3 replaces with real reassembly + load) -
logs offer header, counts chunk bytes, and ONLY behind x2coop.guestReadyMs
sends GuestReady{Ready} N ms after the last chunk (never by default - the
stub must not claim "save loaded" for real joins). SessionHost answers
ResyncReq with a log-only line (T3.6 owns re-stream).

New dev knobs: x2coop.forceSpeedOnJoinFreeze=0..3 (scripted "host clicks
Play mid-join" - fires the same TimeControlCommand.SetSpeedTo the geoscape
speed buttons send), x2coop.offerDelayMs, x2coop.offerReadyTimeoutMs,
x2coop.joinFreezeLogMs, x2coop.guestReadyMs, x2coop.joinRetryMs (T2.3
existed, now documented for verify use).

Verify (live loopback, Debug deploy, PIDs host 32576 / guest 17204 /
guest2 10740; host loaded user_r3seedday1-1.json via main-menu LOAD GAME;
logs .dev\logs\host|guest|guest2.log, saves diffs .dev\t32-saves-*.txt):

- Run A (mid-load race found): accept at 08:30:30.672 -> save bytes=17676
  sha=f82e17bc... ms=57 chunks=1 - 17 KB because the Hello retry accepted
  while the load was still populating the world. FIXED with the
  WorldReady gate above (not shipped broken).
- Run B (fixed, sid=1fbed29b): "world not populated yet - holding offer"
  08:35:19.8 -> serialize ok 08:35:22.6 geodate=2020-01-01 00:16:03
  playTime=00:02:28 diff=Soldier ironman=false -> "save bytes=213843
  sha=8de61d4a... ms=489 chunks=4". Guest: offer + 4 chunks
  (65536+65536+65536+17235) = 213843/213843 complete 08:35:22.767.
- Freeze with Play armed: forceSpeedOnJoinFreeze=3 fired at accept; four
  heartbeats "join-freeze geodate=2020-01-01 00:16:03 (clock held)"
  08:35:22.7-28.7 flat; GuestReady 08:35:28.777 "join-freeze released";
  geoscape then showed 00:32:15 (+16 game-min at the armed speed-3 -
  resume proven; screenshot t32-24).
- Abort path (sid=900745c8, guest with guestReadyMs=0): save
  bytes=215499 sha=6a7d89fb... ms=608 chunks=4 at 08:36:47.9; clock held
  flat at 01:21:18 for ~75s of heartbeats; killed guest process ->
  "join-freeze released (GuestLeft peer=0 reason=Timeout)" 08:38:02.7;
  clock then resumed (11:00:34 + a scheduled event popup, t32-25).
- Saves tree before/after byte-identical (3 entries, FullName+Length+
  LastWriteTime) - no normal save created, none evicted (tmp files live
  under $PROF\x2coop\tmp: 3 streamed saves on disk, matching byte counts).
- campaignId=none on the wire: Entity.Guid() is empty on this save's
  campaign entity - the save format has no campaign-Guid concept; the
  campaign NAME (logged) is the practical parity key. T3.3 decides whether
  OfferSessionId/name matching suffices.

dotnet build Debug + Release 0/0; dotnet test 41/41.

UI-drive lesson #3 (recorded): injected clicks only reach the FOREGROUND
window - when neither instance was foreground the buttons showed hover
(via earlier real input) but presses landed nowhere; re-focus with
drive-pid -ProcId (alt-tap/title-bar fallback) before raw-click. ALSO:
main-menu LOAD GAME dialog buttons sit at ~0.37/0.50/0.63 client-x, not
the evenly-spread guess - measure off a real screenshot, and ESC backs out
of CONFIRM DELETE safely.
T3.2 review fixes (post-commit)
===============================
1) HostJoinFreeze could leak into a NEW StrategyWorld: leaving Strategy
   mid-join (load screen, GC entry, quit-to-menu) disposed the serialized
   world without clearing the flag, so the next StrategyWorld ticked frozen
   until GuestReady/disconnect - indefinitely with a never-ready guest.
   Fixed in SessionHost.NotifyStrategyPresence: on the off-strategy
   transition, HostJoinFreeze releases + SaveStreamer.Reset("strategy-left")
   drops the queued stream + "join-freeze released (strategy-left)". The
   guest story is already coherent: HostLeftStrategy ("Host busy") ->
   HostReturned -> T3.6 ResyncReq -> fresh offer (ResyncReq handler is the
   T3.6 stub today). Verified live: freeze held 00:16:03 across the ESC
   menus (menu is not a world dispose), quit-to-menu logged
   "join-freeze released (strategy-left)" beside "screen-guard unbound",
   reload bound a new world -> HostReturned -> clock advancing (00:33:51,
   no sticky freeze).
2) campaignId=none fallback: Campaign archetype has no GuidComponent so
   CampaignId is Guid.Empty on real saves - spec's
   Saves\x2coop_guest\<campaignId>-<n>.json would collide across every
   campaign. WIRE CHANGE (v1 never shipped): SaveOfferMsg gains
   CampaignName (bounded 128 B via MaxCampaignNameBytes), populated from
   SaveGameSystem.GetCampaignName() - "Purple Knight" observed on the wire.
   Decided T3.3 filename rule (documented in SaveReceiver): CampaignId
   when non-empty, else sanitized CampaignName, else OfferSessionId.
   Codec sample + round-trip cover the field.
3) GetDisplayName()="" : displayInject non-empty wins over name, but the
   streamed group carries no Display-flagged segment -> "". displayInject
   now stays EMPTY so GetDisplayName falls back to "x2coop-<sid>".
4) Post-abort same-peer re-Hello dead-end: the idempotent re-ack resent
   the sid but never re-queued the stream. Re-handshake now calls
   SaveStreamer.QueueOnAccept when !StreamCompleted (new flag set after
   the chunk loop finishes; cleared by Reset/QueueOnAccept). A completed
   stream is not re-sent (ReliableOrdered delivers or the peer dies;
   deeper recovery is T3.6 ResyncReq).

dotnet build Debug + Release 0/0; dotnet test 41/41.
T3.3 - guest SaveReceiver (2026-10-06)
======================================
Replaced the T3.2 counting stub with the real receiver. SaveOffer opens a
session-scoped reassembly buffer (byteCount bounded at MaxOfferBytes=256MB,
out-of-bounds offer -> GuestReady{LoadFailed} so the host freeze still
releases). SaveChunk validates offset/length against byteCount and drops
strays; stream completes at received==byteCount -> SHA-256 over the exact
written bytes vs offer hash -> mismatch: ResyncReq{SaveLoadFailed} (one
retry, then GuestReady{LoadFailed}); match: write
Saves\x2coop_guest\<stem>-<n>.json, MirrorMode=PendingActivate, call
SaveGameSystem.LoadGame(new FileDescriptor(path, typeof(SaveGame))).
Filename stem (new game-free SaveFileNamer, unit-tested): CampaignId when
non-empty, else sanitized CampaignName (path chars trimmed, 64-char cap),
else OfferSessionId - campaignId is Guid.Empty on real saves (no
GuidComponent on the Campaign archetype), so CampaignName is the
effective stem; live run produced "Purple Knight-1.json".

Load completion is async: X2CoopLifecycle.OnWorldCreate(Strategy) hands
SaveReceiver the new world ONLY while Mode=PendingActivate (name must be
"StrategyWorld"), then Tick waits for world.Initialized + a real
GeoscapeClockTag date plus one extra frame -> MirrorMode.Active, log
"mirror active: geodate=...", send GuestReady{Ready}. The pending gate is
what keeps an ordinary local campaign load from ever masquerading as the
mirror. A re-streamed offer clears Mode too, so a stale in-flight load
can't activate on the next stream's world. Load timeouts /
world-died-before-ready -> ResyncReq{SaveLoadFailed} x1 then
GuestReady{WorldInitFailed}. Reset (disconnect/HostEnded) clears Mode and
deletes Saves\x2coop_guest\ unless a load is still in flight (the game's
DeserializeSaveGameCommand re-reads the file; teardown still cleans it).
Teardown.DeleteGuestSaveDir factored so guest session end and pack
teardown share it. Host side: minimal ResyncReq handler re-queues the
stream via SaveStreamer.QueueOnAccept (full resync is T3.6).

Live verify (two instances, Debug deploy): host loaded user_r3seedday1-1
(Purple Knight), serialized 216200 B sha=f24a8028... chunks=4 with
join-freeze holding geodate=2020-01-01 00:16:03; guest reassembled
216200/216200, sha ok, wrote Purple Knight-1.json, mirror world
2129484192 populated and activated - guest screenshot shows the geoscape
with "Spectating host"/"mirror loaded" at 01 JAN 2020; host logged
"GuestReady peer=0 status=Ready - join-freeze released" 7ms later.
Geodate equality: the save's GeoscapeDateTimeInformationSegment stores
2020-01-01T00:16:03.0000000 (host's frozen date, byte-identical via sha);
guest clock read 00:16:11 at activation because the mirror clock
free-runs during load+init - guest-side freeze is T3.4 by design. Host
Stop (K) -> HostEnded -> guest logged "teardown: deleted
Saves\x2coop_guest" and the dir is gone from disk; guest stays on the
geoscape as local play with a "Host ended session" banner.

dotnet build Debug + Release 0/0; dotnet test 46/46 (SaveFileNamer stem/
sanitize/increment tests added; csproj links the new file).

T3.4 - guest clock freeze gate (2026-10-06)
===========================================
Mirror/ClockFreeze.cs prefix now also returns false when MirrorState.Active
AND the TimeTransitionSystem's World IS the guest's bound mirror world
(SaveReceiver.MirrorWorld - resolves only while Mode==Active, so a
guest-local campaign or a post-session world can never be frozen). Same
prefix-skip mechanism as the host join freeze - deliberately NOT the pause
counter (r2 C4: Resume() fires from many places; a skipped subscriber
cannot be un-paused). The per-system identity compare matters: the patch
fires for every world's UpdateTimeOnBase, so gating on the flag alone
would freeze the host's world too; `ReferenceEquals(__instance.World,
mirror)` is the spec's `(MirrorMode.Active && __instance.World ==
guestStrategyWorld) || HostJoinFreeze` verbatim.

Verify seams added (all cheap, all shipped except the probe):
- Guest heartbeat in SaveReceiver.Tick: "mirror-freeze geodate=...
  role=guest (held)" every x2coop.mirrorFreezeLogMs (default 5 s, 0=off)
  while Mode==Active.
- Host heartbeat in SaveStreamer.Tick: same tag with "role=host (running)"
  while a guest holds the session slot - shares x2coop.joinFreezeLogMs;
  makes the frozen-vs-advancing divergence greppable from the two logs.
- Release capture: the Active->Off transitions (session-end Reset, fresh
  offer) keep a weak ref to the outgoing mirror world (_releasedWorld) and
  the heartbeat keeps sampling it ~30 s as "released - clock ungated" so
  the freed clock's resume is provable from the guest log.
- Dev knob x2coop.mirrorForceSpeed=0..3 fires the geoscape UI's own
  Resume(forceResume:true)+SetSpeedTo pair on the mirror world at
  activation - scripted "guest sets speed N" (the freeze must hold anyway;
  the force-resume also clears any serialized pause so the released clock
  resumes at the armed speed).
- New Debug-only probe Probes/MirrorMutProbe.cs: postfix on
  World.ApplyComponentMutation counting mutations that land on the mirror
  world; the heartbeat reports "mirror-muts n=<delta> total=<n>". Reference
  data for the T4.1 firewall - a frozen clock does NOT mean a frozen world.

Verify (live loopback, Debug deploy; host PID 6532, guest PID 38948; host
loaded user_r3seedday1-1.json via main-menu LOAD GAME; guest args
-x2coop-join -x2coop.joinRetryMs=2000 -x2coop.mirrorForceSpeed=3
-x2coop.panel=open; host arg -x2coop.forceSpeedOnJoinFreeze=3; logs
.dev\logs\host|guest.log, screenshots .dev\evidence\t34-01..05):

- accept 10:04:41 sid=c7a1e18e -> host join-freeze held 00:16:03 across the
  not-yet-populated wait -> serialize ok, 215252 B sha=e621f862 chunks=4;
  guest wrote Purple Knight-1.json -> mirror world 1422484496 -> "mirror
  active: geodate=2020-01-01 00:16:11" + "dev mirrorForceSpeed=3 fired" +
  GuestReady{Ready} -> host "join-freeze released" 3 ms later.
- Frozen window ~95 s (>60 required): guest heartbeats flat at 00:16:11
  from 10:05:04.9 to 10:06:55.1 while armed at speed 3; host heartbeats
  advanced 00:17:16 -> 01:53:07 over the same span (t34-03 vs t34-04 show
  00:16:11 frozen guest vs 00:49:20 running host). NOTE: the host's armed
  SetSpeedTo(3) fired during the still-populating window and the finishing
  deserialize appears to have restored the save's own speed - observed host
  rate ~50 game-s/s = speed-0 rate; divergence proven regardless.
- UI stays live while held: the mirror world kept mutating through the
  non-time pipeline - mirror-muts ~137-140 per 5 s idle, up to ~600/5 s
  with UI hover churn (total 5414 by release). A frozen guest world is NOT
  a quiet world - exactly the reference data T4.1 needs.
- Release on HostEnded (host K at 10:06:57.7): guest logged "mirror-freeze
  released (session end) geodate=00:16:11" -> MirrorMode=Off -> the freed
  clock raced at armed speed 3 (~10 game-min per 16 ms accumulator) to
  11:00:00 inside ~2 s, where the game's own scheduled-event modal ("THE
  CLEANERS" intel report, t34-05) game-paused it - funds ticked $2,500,000
  -> $2,502,750 in the same span; Saves\x2coop_guest deleted. The modal
  pause is the GAME's pause (muts n=0 after) - our gate is gone.
- Non-mirror safety: guest at menu pre-join and post-session local world
  are Mode=Off -> MirrorWorld resolves null -> never frozen (the released
  world ticking to 11:00:00 is direct proof).

dotnet build Debug + Release 0/0; dotnet test 46/46 (no new testable
surface - all paths need live game types).

T3.5 - guest save suppression (2026-10-06)
==========================================
Harmony prefixes on the abstract SaveGameSystem (none overridden by the
concrete Strategy/GroundCombat/MainMenu systems) block every save/load entry
point while MirrorState.Mode != Off - PendingActivate OR Active, widened from
the brief's Active-only per the T3.3 review (a local load inside the pending
window would let OnWorldCreate claim a foreign StrategyWorld as the mirror).
Each block logs "blocked <method>" and returns Result.Fail("x2coop: guest")
where the signature returns Result. New file src/Mirror/SaveSuppress.cs +
src/Mirror/ReceiverScope.cs; applied from X2CoopLifecycle.Create (ships in
Release, not probe-gated).

Patched (resolved via AccessTools.Method, exact overloads for LoadGame):
  QueueSaveGameRequest(SaveGameRequest)   - enqueue end of every queued save
  ProcessSaveGameRequest(SaveGameRequest) - direct GameAPI save -> Result.Fail
  ProcessSaveGameRequests()               - protected drain; blocked AND queue
                                            cleared so the game's saving-
                                            overlay state machine settles and
                                            pre-session queued saves drop
  RequestAutoSave(group, bool, bool)      - new-month/intercept/GC autosaves
  FlushSaveQueue(ProcessSaveGameRequestsCommand) - save menu's drain command
  HandleQuickLoadShortcut(InputReport)    - F9; fires per-frame, so the log
                                            line only emits when the report
                                            is the actual quickload trigger
  LoadGame(LoadGameCommand)               - load-menu confirm / Continue
  LoadGame(FileDescriptor, bool) static   - THE funnel for every load
  IronManHiddenCopy(FileDescriptor, string) - private static ironman shadow
                                            copy (patched per spec; unreachable
                                            while saves are suppressed)

ReceiverScope carve-out: LoadGame(FileDescriptor,bool) is the funnel for ALL
loads incl. our own - SaveReceiver.FinishStream wraps its call in
ReceiverScope.InScope = true + try/finally, so only the receiver's mirror load
passes the gate while Mode != Off. Plain bool (main-thread only); Teardown
clears it defensively.

SaveStreamer.QueueOnAccept now also re-latches HostJoinFreeze on EVERY
re-queue (fold of the T3.2 deferral): a ResyncReq re-stream after an abort
released the freeze would otherwise snapshot a running clock. Same
exactly-once release (GuestReady / abort / disconnect / teardown);
SessionHost comment updated.

Verify (live 2-instance, Debug deploy, host PID 39116 / guest PID 36236,
-x2coop-host / -x2coop-join -x2coop.probeSaveSuppress=1; host driven into
user_r3seedday1-1 via LOAD GAME -> row -> LOAD SAVE using the new
.dev/tools/ocr.ps1 WinRT-OCR blind-drive helper; logs .dev/logs/, saves
diffs .dev/t35-saves-*.txt, shots .dev/evidence/t35-*):

- Stream: HelloAck accept sid=7f4e9616 -> 215440 B sha=30b676aa... in 4
  chunks -> wrote Saves\x2coop_guest\Purple Knight-1.json -> PendingActivate
  -> ReceiverScope'd LoadGame (no "blocked" line for it).
- First attempt hit the 120 s mirrorLoadTimeoutMs on a COLD guest process
  (no StrategyWorld create - not a suppression stall: zero blocked lines in
  the window, and attempt 2 under identical gates created the world in ~7 s
  warm) -> ResyncReq{SaveLoadFailed} -> re-stream 214372 B -> Purple
  Knight-2.json -> mirror world 1930274944 -> "mirror active: geodate=
  2020-01-01 00:25:33" + GuestReady{Ready}. Scope exception proven: the
  PendingActivate static LoadGame went through while the gate was armed.
- DevProbe at activation fired every suppressed entry point once:
  blocked RequestAutoSave / ProcessSaveGameRequest (-> Result.Fail,
  probe-confirmed failed=True msg="x2coop: guest") / QueueSaveGameRequest /
  FlushSaveQueue / LoadGame via LoadGameCommand / LoadGame via FileDescriptor
  / ProcessSaveGameRequests / HandleQuickLoadShortcut - 8 paths, one line
  each. IronManHiddenCopy deliberately not probed (unblocked fallback
  creates+deletes hidden ironman files).
- REAL INPUT while Active: F5 quicksave -> blocked QueueSaveGameRequest;
  F9 quickload -> blocked HandleQuickLoadShortcut; ESC -> SAVE GAME -> SAVE
  GAME click -> blocked QueueSaveGameRequest + FlushSaveQueue; ESC -> LOAD
  GAME -> LOAD SAVE on the streamed save -> blocked LoadGame via
  LoadGameCommand (path=Purple Knight-2.json). All silent - no toasts/modals.
- Autosave equivalent: DevProbe's RequestAutoSave call is the exact GameAPI
  the ClockStepReport month-boundary code invokes - the new-month wait is
  impractical live; the entry point is identical.
- Saves\ tree: scripted recursive diff (FullName|Length|LastWriteTimeUtc) -
  in-session adds ONLY x2coop_guest\Purple Knight-{1,2}.json (the receiver's
  own mechanism writes); after host kill (PID 39116) -> guest Timeout ->
  MirrorMode=Off -> dir deleted -> tree BYTE-IDENTICAL to baseline (6 entries).

dotnet build Debug + Release 0/0; dotnet test 46/46.

UI-drive lesson #4 (recorded): win32 SetForegroundWindow alone fails for
these windows - run drive-pid's focus (alt-tap + title-bar fallback) before
raw-click; shots are 2560x1440 vs client 2048x1152 so client=shot/1.25; a
hover tooltip without activation means the click raced the hover - click
twice. OCR (ocr.ps1) turns blind driving into text-anchored clicks.

T3.5-r1 review fix - Saves\ deletion bypass (2026-10-06)
========================================================
Review finding (Important): suppression covered save/load but not DELETE.
Three ordinary UI paths called ContentManager.Delete(Descriptor) directly,
invisible to the SaveGameSystem prefixes:
  AbstractLoadMenuElement.DeleteSaveGame      - load-menu row delete
  AbstractSaveMenuElement overwrite-confirm   - deletes the target BEFORE
      raising the (blocked) SaveGameRequest: file gone, no save replaces it
  BugReportFileSystem.CleanupUserF11BugSave   - F11 bug-report cleanup

Narrowest correct gate = ONE prefix on the funnel:
Common.Content.Managers.ContentManager.Delete(Descriptor), in SaveSuppress
(ships in Release like the rest). Gate = MirrorState.Mode != Off AND the
descriptor's path normalizes strictly inside Constants.Folders.Save()
(separator-boundary StartsWith, OrdinalIgnoreCase - Savesx\ style prefixes
don't match, -gameSaveFolder roots resolve). Fails OPEN on exception.
DescriptorPath prefers FileDescriptor.path, falls back to
FileHandle.AbsolutePath. No carve-out needed for our own cleanup:
Teardown.DeleteGuestSaveDir uses raw Directory.Delete AND runs after
MirrorState resets to Off.

DevProbe grew a sacrificial-file check: writes Saves\x2coop-suppress-probe-
delete.json, calls the REAL AssetReference.ContentManager.Delete, logs
stillExists= (raw File.Delete cleanup keeps the Saves\ diff clean either
way).

Verify (live 2-instance, host PID 28836 / guest PID 28872, guest mirror
Active sid=23c0a803, geodate held 2020-01-01 04:11:55):
- DevProbe at activation: all 8 prior blocked lines + "blocked
  ContentManager.Delete (guest in-session, mode=Active,
  path=...Saves\x2coop-suppress-probe-delete.json)" + stillExists=True.
- REAL INPUT: guest ESC -> LOAD GAME -> selected x2coop-23c0a803 row (Purple
  Knight-1.json) -> DELETE SAVE -> CONFIRM DELETE YES -> "blocked
  ContentManager.Delete ... Saves\x2coop_guest\Purple Knight-1.json"
  (11:47:58). File survived; row stayed in the list. This exercises the
  exact AbstractLoadMenuElement.DeleteSaveGame -> ContentManager.Delete
  funnel the review flagged.
- Session end (host K): teardown still deleted Saves\x2coop_guest - the
  guard can't deadlock our own cleanup (raw Directory.Delete + Mode=Off).
- Saves\ recursive diff before/after: BYTE-IDENTICAL.

dotnet build Debug + Release 0/0; dotnet test 46/46.
UI-drive lesson #5: CONFIRM DELETE stacks YES above NO (not side-by-side);
OCR the zoomed crop when a small word misses.

T3.6 - resync + D3 floor checkpoint (2026-10-06)
==================================================
Wire/protocol: ResyncReason.ManualRequest=5 (bound 1..5),
SessionNoticeKind.GuestMirrorLost=7 (bound 1..7). MirrorMode gains
Receiving and Detached: the lifecycle is now
Off -> Receiving (armed AT SaveOffer, closing the T3.5 race where the
guest stayed Off while chunks streamed) -> PendingActivate -> Active,
with failure or mid-session mirror disposal -> Detached. SaveSuppress
therefore covers every non-Off mode; suppression stays armed while
Detached so a dead mirror still cannot be mutated locally.

Guest (SessionClient/SaveReceiver):
- RequestResync(reason, detail) funnel drives the panel Resync button
  (ManualRequest), checksum/load-failure paths (HashMismatch /
  SaveLoadFailed), HostReturned (HostRequested, deferred via
  _resyncOnActivate when a stream is mid-flight), and the periodic
  timer (x2coop.resyncMinutes float, default 0 = off, verified live at
  0.25). _hostBusy (HostLeftStrategy/HostReturned) pauses the periodic
  clock so no request is sent while the host has no strategy world.
- Load-failure retry budget raised 1->2 (cold-load headroom);
  persistent failure still reports GuestReady{LoadFailed} /
  WorldInitFailed so the host freeze always releases.
- X2CoopLifecycle.OnWorldDispose -> SaveReceiver: a disposed mirror
  world mid-session goes Detached and SessionClient sends
  SessionNotice{GuestMirrorLost}; the host logs 'guest mirror lost -
  awaiting explicit resync'. Resync button is the recovery path.

Host (SessionHost/SaveStreamer): every ResyncReq re-runs the T3.2 path
- SaveStreamer.QueueOnAccept re-latches MirrorState.HostJoinFreeze on
each queue (verified 'join-freeze re-latched (stream re-queue)'),
reserializes the authoritative Strategy world and streams it on a new
baselineSeq (still LastDeltaSeq-based = 0; live deltas are T5). A
successful GuestReady{Ready} releases the freeze and sends
SessionNotice.Resynced back so the guest banner drops to spectating.

SaveSuppress also gates
NewGameParametersElement.TryAndStartNewGame1stPass (review item: the
new-campaign path runs a raw Directory.Delete(campaignDir, true) on the
selected folder BEFORE any SaveGameSystem call - invisible to the
existing funnel prefixes).

Live verification (host PID 7308 / guest PID 13228, sid=c76e26ab,
resyncMinutes=0.25, ~217KB save):
- Join/initial sync: host serialize geodate=2020-01-01 00:16:03 ->
  guest 'mirror active' 00:16:03; SessionNotice.Resynced received.
- Periodic resync: ~27s cadence observed repeatedly - guest
  ResyncReq{ManualRequest}('periodic resyncMinutes=0.25') -> host
  re-stream -> GuestReady{Ready} -> freeze released; clock match held
  (host 2020-01-04 16:10:00 = guest 16:10:00 on the last cycles).
- Host-left transition: host campaign ended (ATLAS mission window
  lapsed during freeze-cadence fast-forward) -> VIEW CAMPAIGN STATS ->
  CONTINUE -> OnWorldDispose Strategy -> 'HostLeftStrategy -> peer=0'
  -> guest 'Host busy' banner (zoomed-OCR confirmed) and periodic
  resync paused (no requests while host at main menu). The dispose ->
  notice seam is destination-agnostic (identical for ground combat,
  verified against the real battle path in T3.1).
- Host return: LOAD GAME -> latest save -> OnWorldCreate Strategy ->
  'HostReturned -> peer=0' -> guest auto ResyncReq{HostRequested}
  ('host returned to strategy') -> fresh stream -> 'mirror active'
  geodate=2020-01-04 16:10:00 == host serialized clock -> Resynced ->
  'Spectating host' banner restored.
- Manual resync: guest panel Resync button ->
  ResyncReq{ManualRequest}('manual resync button') 12:56:54.419 ->
  Resynced 12:57:05.651 = ~11.2s end-to-end. Bound AMENDED in review
  (r1): <=10s -> <=15s pending a Release-build re-measure - the
  decomposition (~0.4s wire+write, ~4.7s LoadGame->world create, ~6.0s
  populate wait) is ~10.7s of the GAME's own load pipeline under a
  Debug build with probes active, not mod overhead.
- Guest quit-to-menu: ESC -> QUIT GAME -> MAIN MENU -> mirror world
  disposed mid-session -> Detached (suppression armed) ->
  SessionNotice{GuestMirrorLost} -> host 'guest mirror lost ... -
  awaiting explicit resync'; guest strip shows detached + 'mirror lost
  - Resync to rejoin' at the main menu; Resync button -> full stream ->
  mirror active -> 'Spectating host'. Recovery loop closed.

Concerns / carried items:
1. HostJoinFreeze re-latch on re-queue: VERIFIED live (log line above).
2. Offer-time suppression: armed at SaveOffer (Mode=Receiving) - closed.
3. 120s cold-load ceiling: retry budget 2 gives a second window; the
   timeout itself was not hit this run (loads ~5s on 217KB).
4. New-campaign Directory.Delete: gated in SaveSuppress as above.
5. Guest quit/menu/new-campaign while Active: verified end-to-end
   (Detached + GuestMirrorLost + Resync recovery). NOTE: at aggressive
   periodic cadence (0.25min) an in-flight stream's LoadGame can
   pre-empt the quit dialog - quit succeeded between cycles; with the
   default off cadence this does not arise. Candidate: suppress
   stream-start while the guest is navigating away.
6. x2coop\tmp staging files accumulate across resyncs (~10 observed) -
   FIXED in T3.6-r1: SaveStreamer.Reset deletes <sid>.json (Teardown's
   dir sweep remains the crash path).
7. Panel LastNotice may read 'resynced' while HostBusy holds when a
   Resynced notice lands after HostLeftStrategy - cosmetic ordering;
   the banner itself stays correct.

dotnet build Debug + Release 0/0; dotnet test 50/50.

D3 floor checkpoint (criteria numbered per spec section 2 success
criteria; the D3 tier itself is spec section 5):
1. Install + content-pack enable + UnityEngine.Debug logging: PASS
   (unchanged; both instances pack-enabled, [X2Coop] lines flowing).
2. Two instances, own -logFile: PASS (PIDs 7308/13228, host.log /
   guest.log).
3. Handshake parity (protocol/build/mod/packs) with readable reject:
   PASS (unchanged T2.x path; codec suite covers).
4. Live read-only geoscape <=500ms: N/A at D3 floor by design (no live
   deltas) - the required slice, clock equality at resync: PASS
   (host serialize 16:10:00 == guest activated 16:10:00).
6. Host leaves Strategy -> guest notified + 'Host busy' -> host returns
   -> full save resync -> guest resumes mirroring: PASS (live sequence
   above, including GuestMirrorLost + manual recovery).

D3 verdict: FLOOR MET. D1-D2 live-delta replication remains deferred to
T5; D3 is the shipping-safe degrade.


T3.6-r1 review fixes - resync debounce/single-flight + bound disposition (2026-10-06)
=====================================================================================
Review I1 (Important): the Resync button stayed enabled through the whole
Resyncing window and SessionClient.RequestResync had no in-flight guard -
every click put another ResyncReq on the wire; each host-side re-queue
restarts SaveStreamer's _pendingTimer (starves the offer) and a mid-stream
offer resets the guest receive buffer. Fixed in BOTH layers:

- UI (CoopPanel): the button greys while a resync is latched in-flight -
  State.Resyncing AND (SessionClient.ResyncRequestInFlight OR Mode is
  Receiving/PendingActivate). The second clause matters: the stale-release
  clears the latch without touching the banner, so a silently-dropped
  request can never wedge the recovery button behind a stuck Resyncing.
- Logic (SessionClient.RequestResync guard stack):
    _hostBusy            -> deferred to _resyncOnReturn (HostReturned fires
                            it once - collapses the manual-while-busy +
                            auto-resync double stream)
    Receiving/PendingAct -> skipped (the in-flight stream IS the answer)
    _resyncOutstanding   -> merged (a request is already on the wire)
    < resyncReqMinMs     -> debounced (default 1500ms post-completion)
  _resyncOutstanding latches inside SaveReceiver.SendResyncReq via the new
  NoteResyncSent() seam, so internal failure-retries latch it too; it
  clears on the answering SaveOffer, on HostReturned (pre-departure
  requests are stale by definition - the host queue either streamed or
  timed out), on disconnect/HostEnded/Detach, and on the
  resyncReqStaleMs timeout (default 30000 = offerReadyTimeoutMs 15s +
  slack) in case the host silently drops the queued offer.

Review I2 (Important) - bound disposition: the <=10s manual-resync target
is AMENDED to <=15s pending a Release-build re-measure. Observed 11.2s
end-to-end decomposes to ~0.4s wire+write + ~4.7s LoadGame->world create
+ ~6.0s populate wait - ~10.7s of the game's OWN load pipeline under a
Debug build with probes live, not mod overhead. No faked number.

Also fixed (cheap items):
- Item 4: _resyncOnActivate (and _resyncOnReturn) now clear on any
  ->Detached/->Off transition - a deferred resync latched mid-stream can
  no longer fire a redundant second request off the next manual resync's
  activation.
- Item 5: SaveStreamer.Reset deletes /x2coop/tmp/<sid>.json
  (session-keyed staging file) - safe mid-session because a completed
  stream already holds its bytes in memory and a re-queue rewrites the
  same name; Teardown's whole-dir sweep remains the crash path.
- Item 3: CodecTests pins the new wire values - ResyncReq
  ManualRequest=5 round-trips with the literal byte checked and 6
  rejected as Malformed; SessionNotice GuestMirrorLost=7 likewise
  (8 rejected). A bound regression now fails tests.
- Item 6: D3 checkpoint header notes criteria are spec section 2 success
  criteria; the D3 tier is spec section 5.

New dev knobs documented in DebugSettings: x2coop.resyncReqMinMs (1500),
x2coop.resyncReqStaleMs (30000).

dotnet build Debug + Release 0/0; dotnet test 52/52 (two new codec pins).
Live re-verify (host 29604 / guest 33740, sid=78c9bdfe, Debug deploy):
- Click-spam: 6 rapid Resync clicks in ~1.8s -> exactly ONE 'ui: resync
  clicked' -> ONE ResyncReq -> ONE host re-queue (button greyed on the
  Resyncing latch, so further clicks never reach the handler); a second
  2-click burst post-activation behaved identically. Single-flight holds.
- tmp cleanup: 'save streamer: tmp deleted' fired on each re-queue AND
  on guest disconnect; the session file febdc978...json (written 13:38)
  was gone from x2coop\tmp after the 13:39 RemoteConnectionClose sweep.
- Graceful leave: guest panel Stop -> host 'net disconnected ...
  RemoteConnectionClose' -> GuestLeft, no wedge.

## 2026-10-06 - T4.1 guest mutation firewall (MirrorFirewall + StateApplier stub)

src/Mirror/MirrorFirewall.cs + src/Replication/StateApplier.cs (scope stub
placed in Replication, not the brief's src/Mirror - T5.3's real applier owns
that namespace; InScope/Scope() contract unchanged). Wired:
X2CoopLifecycle.Apply (ships in Release, not probe-gated), Runner.Update
Tick seam, Teardown.Shutdown (detach logMessageReceived tap), SaveReceiver
.Reset (per-session counters/maps). x2coop.firewallLogOnly=1 degrade knob
documented in DebugSettings (count+log but allow - the D3 floor).

Patches (all fail-open): prefix World.ApplyComponentMutation (private; drop
iff MirrorState.Active && instance==SaveReceiver.MirrorWorld &&
!StateApplier.InScope && type resolves && serializable && !Whitelist &&
entity lacks ExcludeFromSerializationComponent); prefixes on
DeleteEntityImmediately + UnsafeDeleteEntity (same gate minus type terms);
postfix on World.CreateEntity (never blocked - callers would NRE; id is
recorded in GuestCreatedIds + logged rate-limited). Blocks log
type+entity+8 frames at 1 line/key/5s; counters always increment.
Unresolvable componentIDs fail open (counted as untyped).

Exception wrap - TWO layers (the brief's logMessageReceived tap alone
proved insufficient live): (a) Harmony FINALIZERS on
DefaultProcessStrategy.ProcessEvent + DebugProcessStrategy.ProcessEvent
(the common dispatch for queued World.Process and immediate
World.HandleEvent<T> - the latter is generic, unpatchable), World.Process
(1-per-tick cap: WorldManagedScreen.Update retries !Process() in-frame -
an uncapped swallow would hang, not crash), the two delete sinks, and
World.Dispose. A finalizer swallows ONLY a correlated exception
(Mirrorish world - MirrorWorld OR the _mirrorSeen released-world ref,
because SaveReceiver nulls _mirrorWorld at SaveOffer so dispose-time
faults still match; recent block <=10s; Artitas./Xenonauts. frames or a
recently-blocked type named in the text), counts it, and lets Tick decide.
(b) Application.logMessageReceived tap counts correlated
LogType.Exceptions escaping outside the world pump (reentrancy-guarded,
never logs inside the handler). Tick flushes pending hits to the log and
auto-requests SessionClient.RequestResync(ManualRequest) at 3 cumulative
hits (and every +3).

Whitelist review (required by brief): ReadComponent + CampaignPlayTime
-Component KEPT whitelisted. Both are presentation/read-model (read-flag
markers + wall-clock playtime); blocking ReadComponent risks read-back UI
exceptions for zero integrity gain since resync overwrites them anyway.

Verified live (two-instance, Debug deploy; host 32860 / guest 31396,
sid=f6a9225e - SECOND run after the finalizer layer was added):
- Run 1 (prefixes only, pre-finalizer build): Soldiers->Recruit->hire =
  8 blocked mutations (AssignedPersonnelOwnerLink/Cash/OwnsLink/
  ControllingLink/AssignedPersonnelLink/AssignedLoadoutProfileLink/
  InventoryTemplateComponent) with caller stacks, then the game's
  XenonautsMain.Update catch turned the downstream 'TargetInventory is
  missing' into the FATAL CRASH modal + bug zip at the FIRST correlated
  hit - proof a 3-strike counter fed only by the log tap is unreachable;
  also 46+ blocked deletes over the UI tour, 1 tolerated guest-local
  create (id=1825), paused tour produced zero qualifying blocks.
- Run 2 (finalizers live): 3 hire attempts = 21 blocked mutations + 3
  'swallowed@ProcessEvent' hits (all three 'Exception: The TargetInventory is missing.'),
  NO crash modal, guest stayed responsive; hits=3 ->
  'ResyncReq{ManualRequest} sent ("firewall: 3 guest exception(s) after
  dropped mutations")' -> SaveOffer -> stream ok. During the old mirror
  world's Dispose the swallowed hire's ORPHANED delayed mutation
  (AssignedActorsLink add against half-deleted entities) threw an
  EntityMutationException inside DeleteEntityImmediately's
  PopAndHandleMutationDelayStack - caught by the new delete finalizer
  ('swallowed@delete', hits=4), dispose completed, new mirror activated
  (world=777446480, GuestReady{Ready}, Resynced), geodate held frozen,
  Saves\ untouched except x2coop_guest/ stream files. In run 1 the same
  orphan had produced the second crash - now the verification arc passes
  end-to-end.

Known residual gap (documented for T5.x): correlated exceptions escaping
OUTSIDE the five covered sinks (e.g., a UI entry point that mutates the
world directly without the event pump) still reach the game's fatal
handler; the log tap counts them but cannot un-show the modal. True
prevention needs command-level guest-input gating. logOnly mode remains
the stable degrade if enforced blocking proves too aggressive.

dotnet build Debug + Release: 0 warnings/0 errors; dotnet test 52/52.

## 2026-10-06 - T4.2 scripted guest-interaction run (RUN BEGIN/END marker)

The measurable half of spec criterion 5: a fixed ~19-step guest checklist
(docs/guest-interaction-script.md) driven live on a two-instance session,
bracketed by a new run-marker hotkey so the firewall counters have clean
begin/end log lines.

- Marker key = Home (KeyCode.Home, VK 0x24). Decompile survey
  (xenonauts2-decomp\full, KeyCode.*): F1-F4 time speeds, F5/F9
  quicksave/quickload, F7 FPSTest, F8 CommonDebugSystem, F10
  DebugCheatPanel, F11 BugReporter, Esc/Space/Return/Tab, arrows,
  WASDQEXFR C I, Alpha1-9, PageUp/PageDown, Mouse0-2. Mod already owns
  F6/F7/F8 + H/J/K/B. Home/End/Insert have ZERO references; Home types no
  text (safe mid-checklist). drive-pid.ps1 needed a KEYEVENTF_EXTENDEDKEY
  fix for the nav cluster - without it 0x24 arrived as numpad-7's scan
  and Unity read Keypad7 (silent miss).
- Runner.PollRunMarker (src/Runtime/Runner.cs): first Home press logs
  "run: RUN BEGIN ... baseline firewall summary: ...", second logs
  "run: RUN END ... firewall summary: muts=N dels=N creates=N hits=N
  unwhitelisted=N ..." from MirrorFirewall.Summary(). Not probe-gated -
  a log-only marker is harmless in Release.

Verified live (Debug deploy via deploy.ps1; `um backup` profile snapshot
20261006-154046 first; host 38984 / guest 41136, sid=d799eeeb, 1280x720
windows, guest driven by PID via .dev/tools/drive-pid.ps1 +
raw-click.ps1 -raw):

- RUN BEGIN 15:48:39.819 baseline: unwhitelisted=0 blockedMuts=0
  blockedDeletes=0 guestCreates=0 untyped=0 exceptions=0 swallowed=0
  autoResyncs=0 logOnly=0 whitelist=12.
- Geoscape camera pan + F1/F2 time-speed + Space + map clicks +
  right-clicks: presentation only, zero blocked lines, geodate heartbeat
  stayed flat (clock freeze holds). Launch pick-mode ("SELECT TARGET
  FOR MISSION LAUNCH") produced one tolerated guest-local create
  (id=1825, the waypoint marker) - 15:50:36.
- Base view -> build STOREROOM on an empty grid cell (16:04:20): the
  guest tried to CREATE a build-site entity 1573 - 10 component adds
  blocked in a burst (PositionComponent, BuildSiteLink,
  ActionCurveComponent, BuildingPoints, BuildingParcelComponent,
  BuildingSpriteComponent, PowerUsage, PrerequisitesComponent,
  ProgressPoints, BuildingStateMachineComponent) + deletes blocked on the
  placement ghost/rollback (dels 1->24 incl. the earlier interact-state
  block on entity 1163 and ghosts 1541/1543). muts=12 dels=24.
- Soldiers -> Recruit -> 3 hires (16:13:23 / 16:13:47 / 16:13:54): each
  = 7 blocked mutations (AssignedPersonnelOwnerLink remove, Cash,
  OwnsLink, ControllingLink, AssignedPersonnelLink,
  AssignedLoadoutProfileLink, InventoryTemplateComponent) + 1
  'swallowed@ProcessEvent Exception: The TargetInventory is missing.'
  hits=3 -> 'ResyncReq{ManualRequest} sent ("firewall: 3 guest
  exception(s) after dropped mutations")' + 'auto-resync requested'
  16:13:54.423 -> host SaveOffer 16:13:54.883 -> orphaned delayed
  mutation during old-world Dispose caught by the delete finalizer
  ('swallowed@delete EntityMutationException: A crash occurred during
  delayed entity mutation', hits=4, 16:13:55.277) -> GuestReady{Ready}
  16:14:05.876 -> Resynced 16:14:05.942 - the guest cycled through
  'Resyncing'/'Spectating host' exactly as scripted, then resumed.
- Save menu: Escape -> GAME MENU -> SAVE GAME -> slot + SAVE GAME ->
  CONFIRM OVERWRITE -> YES: dialog dismissed, NO file written (latest
  guest save stayed Purple Knight-8.json 15:42:58 - the host-streamed
  one); the request never reached a SaveGameSystem sink so no 'blocked'
  line - suppression at the queue layer below the UI.
- F5 (15:59:15.360): 'blocked QueueSaveGameRequest (guest in-session,
  mode=Active)' - quicksave suppressed.
- F9: sent VK 0x79 in error = F10 (DebugCheatPanel), no visible effect;
  quickload coverage came from the menu path instead - LOAD GAME -> pick
  r3seedday1 -> LOAD SAVE -> 'blocked LoadGame (guest in-session,
  mode=Active, via LoadGameCommand path=.../user_r3seedday1-1.json)'
  16:00:40.230. DELETE SAVE -> CONFIRM DELETE -> YES: the real file
  user_r3seedday1-1.json (shared profile dir!) still exists afterwards -
  nothing deleted.
- Research screen: THE CLEANERS shown in progress, no available projects,
  START RESEARCH disabled - UI-level refusal, no mutation issued.
  HIRE SCIENTISTS panel: research capacity already 5/5 -> count/button
  inert. Engineering screen: ANGEL INTERCEPTOR selected -> 'EMPTY HANGAR'
  red (2/2 hangars) -> START PRODUCTION disabled; click no-op.
- FINDING (honest report, spec asks to surface not hide): at 16:19:21 a
  FATAL CRASH modal appeared on the guest. bug_report zip stack:
  NullReferenceException at UnityEngine.Component.get_gameObject <-
  RegionInfo.UpdateAppearance (RegionInfo.cs:111) <- FocusRegion <-
  GeoscapeElement.SetupGeoscapeView pointer-enter handler
  (PointerListener.OnPointerEnter, uGUI ExecuteEvents) right after
  'Enter State:GeoscapeSelectTarget' (the LAUNCH AIRCRAFT pick-mode).
  This is the T4.1 residual gap made concrete: the escape is in the
  game's UI hover path, NOT a mutation sink - nothing the firewall
  patches could see or swallow it, and it produced zero mutations before
  dying. The Unity Component it touched was likely a disposed/mirror-
  rebuilt UI object referenced across the resync. Guest process stayed
  alive + connected (pump + rtt running behind the modal) and RUN END
  still logged on Home press; EXIT GAME closed PID 41136 cleanly. Host
  (38984) unaffected throughout.
- RUN END 16:24:09.397 verbatim:
  'run: RUN END (role=Guest mode=Active) - firewall summary:
  unwhitelisted=0 blockedMuts=33 blockedDeletes=24 guestCreates=3
  untyped=0 exceptions=4 swallowed=4 autoResyncs=1 logOnly=0
  whitelist=12'

Result: every mutating action blocked or UI-gated, unwhitelisted=0 (no
replicated-state leak), geodate frozen, no applied mutations, host
untouched. Criterion 5's measurable half PASSES on counters; the one
crash modal is a real uncovered-sink escape -> documented as the T5.x
concern (command-level guest-input gating, or a region-hover/SelectTarget
guard), not hidden.

dotnet build Debug + Release: 0 warnings/0 errors; dotnet test 52/52.

### T4.2-r1 fix round - geoscape pick-mode gate (2026-10-06)

Fixes review finding I-1: the T4.2 fatal modal was an orphaned-hover NRE
(RegionInfo.UpdateAppearance <- FocusRegion <- uGUI pointer-enter) the
frame after 'Enter State:GeoscapeSelectTarget' - the LAUNCH AIRCRAFT
pick mode entered post-resync with a region listener whose uGUI object
had most plausibly been rebuilt away (orphan mechanism inferred, not
proven). The firewall cannot see uGUI hover, so the fix is
to keep a mirrored guest out of geoscape pointer-pick states entirely.

- New file `src/Mirror/UIPickGate.cs` (registered in
  X2CoopLifecycle.Create): prefix on the NON-GENERIC
  `StrategyUISystem.TriggerEventOnFSM(TriggerCommand<StrategyUITriggers>)`
  - the single funnel every strategy-UI FSM command passes through.
  While `MirrorState.Mode != Off` the prefix returns false (drops the
  command) for:
  * StrategyUITriggers.SelectTarget - PermitDynamic fans this out to all
    three geoscape pick states by parameter type
    (GeoscapeSelectTargetElement.Parameters -> launch/retarget picks;
    GeoscapeSelectGeoRegionElement.Parameters -> region pick;
    GeoBaseSelectionElement.Parameters -> base pick). One gate covers
    LAUNCH AIRCRAFT plus the Air* element retarget buttons.
  * StrategyUITriggers.Build ONLY when param[0] is
    GeoscapeBaseBuildElement.Parameters - the new-base placement pick
    (same Picking2DReport hover surface, same orphaned-hover risk).
    Other Build params (ConstructBuilding/AircraftRequisition) are menu
    flows, not pointer-picks, and pass through.
- Deliberately NOT gated: Select (menu GeoBaseSelectionElement list
  panels - research/engineering/stores/soldiers/aircraft transfers have
  no geoscape hover surface), ShowModal/MoveTo/Inspect/Hide (navigation
  + read-only; MultiTargetSelect is unreachable now anyway). SaveSuppress
  gates LoadGameCommand/FileDescriptor loads the same way for consistency
  (Mode != Off = Receiving/PendingActivate/Active/Detached).
- Logging: `blocked <key> (guest in-session, mode=<m>, params=<P>) -
  geoscape pick-mode entry refused (picks=N)`, rate-limited 1/key/5 s,
  BlockedPickEntries session counter (reset in SaveReceiver.Reset and
  Teardown.Run).
  Fail-open: any throw in the prefix logs + proceeds.
- Verified live (2-instance loopback; host 11804 / guest 33964,
  sid=7ac0af3c, r3seedday1 campaign after the host's save swap forced a
  full receiver-resync cycle):
  * `pick-gate: patch-applied ... TriggerEventOnFSM (prefix)` on BOTH
    instances (host 17:00:18, guest 17:11:37).
  * Guest clicked LAUNCH AIRCRAFT (geoscape top bar): 17:15:33.574
    `blocked SelectTarget (guest in-session, mode=Active,
    params=GeoscapeSelectTargetElement.Parameters) - geoscape pick-mode
    entry refused (picks=1)`; repeat click 17:16:20.177 -> `(picks=2)`.
  * ZERO `Enter State:GeoscapeSelectTarget` lines on the guest; no
    "SELECT TARGET FOR MISSION LAUNCH" banner; no crash modal; guest
    stayed in-session (rtt ~25-40 ms, "resynced" strip).
  * Region hover afterwards (EUROPE label appeared on pointer-over): no
    NRE, no modal - the FocusRegion path ran on the live mirror UI.
  * Resync exercised end-to-end IN this session: guest's first mirror
    LoadGame produced no world-create (silent no-op) -> 120 s timeout ->
    `ResyncReq{SaveLoadFailed}` 17:13:38.921 -> host SaveOffer
    17:13:39.371 -> `mirror active geodate=2020-01-01 02:52:49`
    17:13:53.081 -> Resynced notice + status strip. The gate did not
    interfere: it only drops SelectTarget/Build-pick triggers.
  * Host untouched: zero `blocked`/`pick-gate` hits host-side (Mode=Off
    pass-through).
- Field notes from the session: CONTINUE had loaded a mid-battle
  GroundCombat save, so the host was walked back to LOAD GAME ->
  `auto_strategy_before_combat-1.json` and then `user_r3seedday1-1.json`
  (the T4.2 save); guest relaunched with `-x2coop.joinRetryMs=2000` and
  `-WorkingDirectory` set (a bare Start-Process inherits the caller's CWD
  and `ContentPackRegistry.Load("contentpacks.json")` returns null ->
  boot-crash - same trap MODLOG already records for `um win launch`).
  drive-pid.ps1 -Click (SetCursorPos + one raw MOVE + down/up) lands
  uGUI button clicks where raw-click.ps1's jitter approach only hovered
  (kept for region hover/move-only).
- `docs/guest-interaction-script.md` step 13b documents the expected
  `blocked SelectTarget` outcome for LAUNCH AIRCRAFT.

dotnet build Debug + Release: 0 warnings/0 errors; dotnet test 52/52.

## 2026-10-06 - T5.1 host ReplicationSource capture

Host-side half of the D1 pipeline (spec section 4 ReplicationSource):
postfixes that capture every world mutation + entity op on the host's
live StrategyWorld while a guest holds the session, coalesced per frame,
filtered at flush by the same exclusion rules save serialization uses.
T5.1 captures + counts only; nothing is serialized or sent yet (T5.2
consumes ReplicationSource.FrameOps from the same Runner.LateUpdate ->
FlushReplication seam).

- Patches (X2CoopLifecycle.Create, ships in Release - NOT probe-gated):
  World.ApplyComponentMutation(int,int,IComponent,IComponent,bool) - the
  non-generic add/remove/update sink; World.CreateEntity() (return value)
  + EntityManager.EntityRecyclingFactory.Obtain(World) - R5 proved Obtain
  is the true chokepoint (Template factory bypasses CreateEntity), both
  patched with per-frame id dedup; World.DeleteEntityImmediately(int) +
  World.UnsafeDeleteEntity(int). ForceCreateEntity deliberately not
  captured (host-side resurrection only fires inside a save reload, which
  is the resync path, not deltas).
- Record shape: _muts dict keyed (entityID<<32|componentID) ->
  (added, nextC), LAST WRITE WINS per frame. nextC held by reference, no
  Copy() - r2 verified components are replaced not mutated in place and
  each record dies at end-of-frame flush anyway. A delete drops the
  entity's pending mutations + cancels a same-frame create (never reached
  the wire); a delete whose id was ALSO re-created (limbo pool reuse)
  keeps both ops and FrameOps orders deletes->creates->mutations so id
  recycling replays correctly.
- Gate = SessionHost.HasGuest AND __instance IS the live
  ScreenGuard.StrategyWorld (WeakReference fast-path, ScreenGuard slow-path
  re-validates Name+Disposed, re-binds across world recreate). HasGuest
  covers the join-freeze window on purpose: spec section 6 buffers deltas
  > baselineSeq until GuestReady and baselineSeq snapshots at
  QueueOnAccept, so capture must already be live during the freeze or a
  host command mid-join never produces a buffered delta. No parallel
  Mirroring enum added - the existing session state expresses the gate.
- Flush (main thread, per rendered frame): drains pending ops through
  MirrorFirewall's exclusion helpers (promoted private->internal, single
  slot cache re-resolves per world - the gated host/guest worlds never
  coexist in one process): [ExcludeFromSerialization] type ->
  FilteredType; ExcludeFromSerializationComponent on entity ->
  FilteredEntity; IOptionalSerialization.IsSerializable==false ->
  FilteredOptOut (R9's !O bucket, GameObjectComponent). Unresolvable
  componentIDs fail OPEN as `untyped` (kept - cannot classify). Deletes
  of already-gone entities pass through kept (idempotent no-op for T5.3).
- Reset paths: SessionHost.ClearGuest, Teardown.Run, plus the lazy
  session-end path inside FlushFrame (guest gone or world gone ->
  Reset within a frame, no live IComponent/World refs survive session
  end - the assembly stays loaded across hot re-enable, R3).
- Verify line while gated, one INFO per x2coop.captureLogMs (default
  1000, 0=off): `capture ops=N muts=M creates=C deletes=D filt=F
  untyped=U types=A:n,...` - muts is the raw ApplyComponentMutation call
  count (R9-comparable), ops is post-filter+coalesce emitted count, the
  explicit ops=0 line is the paused verify.
- NOT filtered (documented T5.2/T5.3 decisions): guest Whitelist types
  (whitelist gates what the guest ACCEPTS, not what the host emits),
  template-stripped Equals() components (WorldState.cs:94 - needs sparse
  merge context), link scrubbing toward excluded entities
  (WorldState.cs:141-184 - T5.3 maps host refs).

Live verify (Debug deploy, profile backup 20261006-173342.zip first;
host PID 2604 / guest PID 1404 sid=b52a4b6c, user_r3seedday1-1.json via
LOAD GAME; guest auto-joined through the NotOnStrategy rejects once the
world bound):

- patch-applied x5 on BOTH instances (firewall prefix + capture postfix
  coexist on the same methods - disjoint world gates):
  `capture: patch-applied Artitas.World.ApplyComponentMutation (postfix)`
  `capture: patch-applied Artitas.World.CreateEntity (postfix)`
  `capture: patch-applied Artitas.Managers.EntityManager+EntityRecyclingFactory.Obtain (postfix)`
  `capture: patch-applied Artitas.World.DeleteEntityImmediately (postfix)`
  `capture: patch-applied Artitas.World.UnsafeDeleteEntity (postfix)`
- `capture: bound host StrategyWorld hash=-408538096` at world create;
  the stream-serialize second shows the filter working:
  `capture ops=64 muts=3667 creates=0 deletes=5 filt=3443 untyped=0
  types=EntityListenerComponent:34,MutationDelayedItemComponent`2:11,DynamicGroupComponent:11,GameObjectComponent:1,SeedComponent:1`
  (filt=3443 - the serialize-path churn is almost entirely excluded
  entities/types; a mid-session host save will produce the same burst).
- PAUSED (ESC pause menu - world Process suspended): 13+ consecutive
  `capture ops=0 muts=0 creates=0 deletes=0 filt=0 untyped=0 types=-`
  seconds (17:38:58 - 17:39:10). Matches R9's "mutations frozen at
  exactly N under ESC" - zero ops, zero heartbeat.
- PAUSED (pause-counter path - day-1 THE CLEANERS intel modal):
  `capture ops=120 muts=120 ... types=CampaignPlayTimeComponent:120`
  every second - the world still ticks under _pauseCounter and the
  2 Hz wall-clock playtime timer is the ONLY churn, exactly the paused
  heartbeat R9 measured (CampaignPlayTimeComponent 65541/42205 paused).
- SPEED 1 (post-load default): `capture ops=771 muts=873 ...
  types=DurationComponent:400,CampaignPlayTimeComponent:120,
  PositionComponent:100,VelocityComponent:100,GeoscapeDateTimeComponent:50`.
- SPEED 3 (F3, ~20 s sustained window 17:40:27-46, geodate raced
  Jan-1 16:20 -> Jan-3 13:15):
  `capture ops=906 muts=1067 creates=0 deletes=0 filt=0 untyped=0
  types=DurationComponent:440,CampaignPlayTimeComponent:120,PositionComponent:80,VelocityComponent:80,ActivityStateMachineComponent:60`
  steady state ~823 ops/s / ~981 muts/s; modal-pop second showed
  `ops=602 muts=1023 deletes=95` (pooled-entity churn).
  ActivityStateMachineComponent IS in the top-5 as required (60 ops/s -
  R9 found it dominates BYTES at ~6.7 KB/sample; op counts do not show
  the byte weight - T5.2 sees that). ops<muts by ~16%: the per-frame
  last-write-wins coalescing is measurably collapsing repeat writes.
- R9 comparison: muts ~981/s speed-3 / ~873/s speed-1 vs R7/R9's
  ~870/s probe ballpark - same order. Dominant ops are the per-real-tick
  timer set (DurationComponent ~360-440/s, CampaignPlayTimeComponent
  120/s) whose rate barely changes with game speed - consistent with
  R9's observation that paused-byte volume is dominated by the same
  heartbeat types.
- Guest side: same 5 capture patch-applied lines (17:34:13) but ZERO
  `capture ops=` lines all session - the world-identity gate rejected
  the guest mirror world every frame. Mirror stayed Active, geodate
  held 00:16:03 while the host raced to Jan-3, firewall whitelist
  probe active types=12 - host capture + guest firewall coexist.
- Session boundary: `GuestLeft peer=0 reason=Timeout` 17:41:38.220 ->
  ZERO further capture lines (HasGuest false -> lazy Reset). Host then
  killed by exact PID 2604; guest already killed by exact PID 1404.

Deferred to T5.2/T5.3 (documented, not hidden):

- ops/s is not bytes/s: ActivityStateMachineComponent ~60 ops/s at
  ~6.7 KB/sample (R9) would be ~400 KB/s serialized naively - T5.2's
  addendum caps big types + drops derivable timer/position types to a
  lower-frequency/unreliable channel. The 64 KiB/s budget CANNOT carry
  D1 raw even after coalescing (R9 speed-3 envelope ~183 KB/s was
  itself 89% paused).
- Day-1 modal cascade means "speed 3" is majority pause-counter-paused
  in practice (the same caveat R9's 89% window had) - sustained unpaused
  speed-3 sizing needs a later-campaign save or modal-free window.
- Deletes of already-dead entities can't see ExcludeFromSerializationComponent
  (entity already gone at postfix time) - pass-through kept, idempotent
  no-op for T5.3; counted if it ever matters.
- Guest geoscape click produced no blocked/guest-local line (a plain
  click is not a mutation attempt) - firewall coexistence here rests on
  zero guest capture lines + the live T4.2-verified whitelist probe,
  not a full T4.2 re-run.

dotnet build Debug + Release: 0 warnings/0 errors; dotnet test 52/52.

### T5.1-r1 fix round (2026-10-06, review of 8102fea: 2 Important + 4 Minor)

- I1 hollow template creates FIXED: Template.Create/CreateUnsafe and
  Entity.CopyTo install components via ComponentManager.AddComponents /
  CopyInComponents + FamilyManager.SetComposition, bypassing
  ApplyComponentMutation - a bare create reached the guest with ZERO
  components. DrainFrame now enriches each emitted create with the
  entity's live serializable snapshot: FamilyManager.GetRawComposition
  -> IterateIndices -> ComponentManager.GetComponent, filtered by the
  same IsTypeExcluded / IOptionalSerialization rules as the mutation
  path, emitted as Mutation ops flagged CapturedOp.FromCreate
  immediately after their Create op. A (entity,component) pair already
  pending in _muts is skipped (the standalone op replays last and
  wins); a dead-at-flush id resolves to composition 0, which stays a
  hollow create - still the correct replay. Same-frame marker adds are
  covered because the snapshot runs at flush time.
- I1-adjacent reconciliation: standalone mutation records now
  reconcile against live matrix state at flush (ComponentManager
  .GetComponent) - a ComponentManager-direct bypass write that flipped
  presence flips the emitted op; divergences counted via Reconciled.
- I2 lifetime contract now documented on CapturedOp/FrameOps: the list
  and every Next reference point at LIVE world state and are valid
  only until the next FlushFrame - T5.2 MUST consume synchronously
  inside the Runner.LateUpdate -> FlushReplication seam on the main
  thread; never defer to send/serialization threads, queues or later
  frames.
- I2 caveats documented (class header): a component mutated PURELY IN
  PLACE emits zero ops; direct ComponentManager add/remove calls
  bypass the sink. Residual gap: Entity.CopyTo/Clone-target writes
  onto an entity NOT created this frame stay silent - observed call
  sites are transient scratch usage; a missed overwrite heals on the
  next touching mutation or resync.
- M1: `_loggedHash >= 0` sentinel was dead code - GetHashCode CAN be
  negative (r0 run bound hash=-408538096), so `capture: released`
  never logged. Bound state is a real bool now (_boundLogged);
  _loggedHash only de-dupes the bound line.
- M2: delete ops no longer consult the exclusion marker - the original
  delete strips the entity's components before the postfix runs and a
  same-frame-recycled id resolves to the NEW incarnation, so the
  marker could never answer "was the DELETED entity excluded?".
  Deletes always pass kept (idempotent no-op for T5.3; a wrongly
  dropped delete is a divergence). EntityManager.IsAlive at flush only
  counts RecycledDeletes - visibility for limbo-pool id re-issue.
- M3: Log.Exception has NO rate limiting - postfix/flush faults now go
  through Fault() which logs once per tag per session (_faultLogged
  cleared by Reset); the "logs once" doc claim is now true.
- M4: verify line is `capture ops=N muts=M creates=C deletes=D enr=E
  filt=F untyped=U types=A:n,...` - muts is the GATED
  ApplyComponentMutation postfix count (R9 probe-window comparable,
  NOT a process-wide raw total), ops is post-filter emitted, enr the
  create-enrichment subset. Corrected live figures: speed 1 ~873
  muts/s, speed 3 ~981-1067 muts/s.
- New dev knob x2coop.captureCreateDump=1: per-create detail line
  `capture: create+enrich eid=N comps=M [types,...]` (first 12 kept
  type names).

Live re-verify (Debug deploy; profile backup 20261006-182059.zip
first; host PID 31244 / guest PID 16572 sid=0b04378b,
user_r3seedday1-1.json via LOAD GAME; guest auto-joined through the
NotOnStrategy rejects once the world bound):

- `capture: bound host StrategyWorld hash=1830775488` at world bind.
- First creates at bind (eid=4, eid=9) logged comps=0 - they carried
  only excluded types (GameObjectComponent/DynamicTagComponent/
  ExcludeFromSerializationComponent): correct filtering, not a bug.
- Notification template spawn 18:32:37:
  `capture: create+enrich eid=1540 comps=6 [ComposableFormatterComponent,
  ColorComponent,DescriptionComponent,DurationComponent,NotificationGroup,
  NotificationID]`; same second `creates=1 enr=6`.
- ATLAS BASE tactical-mission cascade 18:33:44 (the day's scripted
  mission + its soldiers/items/marker, all template-created):
  `capture ops=3300 muts=2171 creates=120 deletes=0 enr=2158 filt=147
  untyped=0` - 120 creates carrying 2158 enriched component ops in one
  window. Dump evidence: soldier actors eid=1739/1636 comps=63
  (HearingRange, NightVisionIntensity, UnmodifiedHitPoints,
  VisionRange, PreferredStrikeTeamSlotComponent,
  InventoryTemplateComponent,...), geoscape mission marker eid=1640
  comps=8 (PositionComponent, RotationComponent, ActivityIconComponent,
  DetectionLayer, DetectionStateComponent), item/inventory templates
  comps=28/29 (AleniumComponent, Weight, ItemsGroup,...).
- Speed-3 sustained window 18:35:30-41 (geodate racing):
  `ops=~823-904 muts=~981-1068 filt=0 untyped=0
  types=DurationComponent:~360-440,CampaignPlayTimeComponent:120,
  PositionComponent:80,VelocityComponent:80,
  ActivityStateMachineComponent:60` - same envelope as r0.
- Pause-counter heartbeat unchanged: `ops=120 muts=120
  types=CampaignPlayTimeComponent:120` under the intel/mission modals.
- Guest killed by exact PID -> 18:36:24
  `capture: released host StrategyWorld (emitted=99130)` +
  `session host: GuestLeft peer=0 reason=Timeout` - the release log
  now fires via the bool bound flag, and capture stops on session end.
- Guest side: all 5 capture patch-applied lines but ZERO capture
  ops/bound/enrich/released lines all session - the world-identity
  gate rejected the guest mirror world every frame.
- Host then killed by exact PID 31244; guest already killed by exact
  PID 16572.

dotnet build Debug + Release: 0 warnings/0 errors; dotnet test 52/52.

## 2026-10-06 - T5.2 delta serialize + send

Host side of the D1 live-delta pipeline (spec section 5): consumes
ReplicationSource.FrameOps SYNCHRONOUSLY at the Runner.LateUpdate ->
FlushReplication seam (CapturedOp.Next holds live component refs that
die at the next FlushFrame), snapshots/serializes through the game's own
ArtitasSerializationSystem.Serializer (TrySerialize -> CompressedJson ->
UTF8, identical to the save/probe path; entity conversion writes IDs
only, no side effects), then packs and sends on two fixed-rate lanes.
Guest side: DeltaSink does sequence accounting only - T5.3 owns
decode+apply.

- New Replication/: DeltaQueue (cross-tick coalesce map keyed
  (entityId<<32|componentId), structural-op dedup, dead-id set so a
  mutation captured before a delete never emits, join-freeze buffer +
  resync latch), DeltaPacker (exact codec wire model; greedy
  order-preserving Split at Protocol.MaxMessageSize=65792 incl.
  envelope; WireLimits.MaxDeltaOps=4096 op cap; a lone oversized op goes
  out alone so the codec's deterministic reject only burns that seq),
  DeltaSender (fold, serializer binding, both send lanes, join-buffer
  flush, stats), DeltaSink (guest rx: StateDelta.Seq must be last+1 -
  any gap is a resync trigger for T5.3; PosDelta.Seq is a SEPARATE
  counter, sequenced drops counted, never alarming).
- Wire: Protocol.ChannelPos=2 (latest-state), MsgType.PosDelta=11,
  DeltaOpKind.RemoveComponent=4, StateDeltaMsg.HostSendTicks (host send
  clock for guest-side timing), PosDeltaMsg (own seq space), MsgCodec
  routes PosDelta to ChannelPos/Sequenced; StateDelta rides
  ChannelControl/ReliableOrdered (corrected in T5.2-r1 - this line
  earlier claimed ChannelSave; only SaveOffer/SaveChunk use channel 1).
  CodecSelfTest covers the new shapes.
- Lanes/cadence (R9 addendum): reliable StateDelta emitted on a fixed
  tick (x2coop.deltaSendMs, default 80ms = 12.5Hz, clamped 20-1000) -
  structural add/remove/link + all non-positional state; latest-state
  PosDelta lane (x2coop.deltaPosMs, default 500ms = 2Hz, clamped
  250-5000) carries Position/Velocity/CampaignPlayTime/
  GeoscapeDateTime - Sequenced, so drops are free. Per-rendered-frame
  sending is gone entirely.
- Throttling: component payloads > x2coop.deltaHeavyBytes (1024) are
  rate-capped to one snapshot per entity per x2coop.deltaHeavyMs
  (1000ms) - covers R9's big four (ActivityStateMachine ~6.7KB,
  UIControllers ~3.3KB, TemplateRelationships ~1.9KB,
  NotificationStateMachine ~1.5KB); the timer trio rides the pos lane
  instead of the reliable stream. Serializer-unbound records stay
  pending (retry next drain); a real serialize failure drops the op.
- Join window: deltas with seq > baselineSeq buffer encoded as whole
  wire messages (DeltaQueue.OfferJoin) under cap x2coop.deltaJoinCapKB
  (default 5520KB = 184KB/s * 30s, the R9 load window); GuestReady{Ready}
  flushes the buffer then live ops; GuestReady{failed} drops it;
  overflow latches ResyncNeeded, drops everything and forces a resync -
  a partial flush would be a gap-corrupt stream.
- Lifecycle: every reset seam clears T5.2 state - SessionHost.ClearGuest,
  NotifyStrategyPresence(strategy-left), SaveStreamer abort,
  SessionClient.Detach, SaveReceiver.Reset, Teardown - no world,
  component, peer or wire refs survive. Seq counters stay monotonic
  across a reset (rewinding would silently diverge the mirror).
- Logging: "delta seq=.. ops=.. bytes=.." per emitted delta
  (x2coop.deltaLogEach), "delta-stats bps=.. posBps=.." per-second host
  line (x2coop.deltaStatsMs), "delta-rx" guest line - paired evidence
  for the two-instance bandwidth check.
- Tests: tests/X2Coop.Tests/DeltaTests.cs + csproj links (game-free
  sources only). Covers coalesce last-write-wins, structural dedup,
  dead-id suppression, split at byte boundary AND op cap AND
  lone-oversized, join buffer offer/flush/overflow-resync, codec
  round-trips (HostSendTicks, RemoveComponent kind-byte pinned at 4,
  PosDelta own-seq), generic FullName <= MaxComponentTypeBytes. One
  harness fix: the boundary test's 13-byte DeleteEntity op made fit=5058
  exceed MaxDeltaOps (cap bound first, not bytes) - now a true 20-byte
  op so the byte boundary binds. DeltaQueue.PendingOp
  Snapshot/Payload/SerFailed are auto-properties (CS0649 in the
  test-linked build).

dotnet build Debug + Release: 0 warnings/0 errors; dotnet test 77/77.

Live verify (2026-10-06, host 38164 / guest 19440, save r3seedday1,
campaign "Debug"/"Purple Knight", Soldier diff):
- um backup taken before modded launch; deploy.ps1 Debug build.
- Mirror active on guest (world=1233974752 class), zero reliable gaps
  (delta-rx gaps=0 through session end).
- Bandwidth, delta-stats 1s windows:
    paused:   bps=0      posBps=0    -> 0 B/s      (0% of 64 KiB/s)
    speed 1:  bps=12636-13334 posBps=1042 -> ~13.7-14.4 KB/s (~21-22%)
    speed 3:  bps=14160-14759 posBps=1042 -> ~15.2-15.8 KB/s (~24-25%)
  All states comfortably inside the R9 64 KiB/s budget.
- Throttling evidence at speed 3: raw capture ~820-835 ops/s
  (~920-937 mutations/s, mostly CampaignPlayTime/GeoscapeDateTime)
  collapses to ~108-110 reliable ops/s + ~12 pos ops/s on the wire -
  roughly 7.5x reduction from coalescing + heavy-type rate caps.
- Positional lane: delta-pos seq=.. ops=6 bytes=521 at ~2 Hz on the
  Sequenced channel (Position/Velocity/clock types), independent of
  the 12.5 Hz reliable tick.
- Join buffering: offer queued baselineSeq=0; delta seq=1 (351B) and
  seq=2 (6630B) logged "buffered joinKB=6" during the guest's save
  load; GuestReady{Ready} released the freeze and the buffer flushed.
- Over-cap resync: production MinJoinCap is 1 MiB so the cap test ran
  with a temporary MinJoinCap=1024 patch + deltaJoinCapKB=5
  (cap=5120B). Host 29192 / guest 12316: "delta: join buffer over
  cap=5120 (dropped ~7KB) - resync forced at GuestReady", then at
  GuestReady "join deltas overflowed - forcing resync", join-freeze
  re-latched, save streamer re-offered baselineSeq=2 and produced a
  fresh 237,716-byte save. The overflow->drop->latch->force-resync
  machinery works end to end; unit test
  JoinBuffer_OverCap_DropsAll_AndLatchesResync covers the same path.
- KNOWN ISSUE (RESOLVED in T5.2-r1): the forced-resync re-offer
  serialized and the sender logged success, but the guest never decoded
  the second SaveOffer/SaveChunks on ChannelSave. Host channel-0
  traffic (pings, rtt=0ms) stayed live throughout; guest-side probe
  heartbeats that ride Runner.Update stopped at mirror-activation time.
  Diagnosed in T5.2-r1: NOT a stalled pump - the streamed save carried
  MarkedForCleanupGroup entities, the guest's CleanupSystem entered its
  unbounded "while family nonempty" rescan ~10ms after activation, the
  firewall blocked the marker-leave mutations, and the guest hard-wedged
  inside one LateUpdateEvent dispatch. See the T5.2-r1 section for the
  fix set (MirrorSystems disarm + join watchdog + barrier + MTU split).
- Teardown: guest connection close produced host "delta: reset
  (GuestLeft peer=0 reason=RemoteConnectionClose)" + "session host:
  stopping transport"; guest logged "net notice kind=HostEnded",
  "mirror-freeze released (session end)", "MirrorMode=Off",
  "delta-rx: reset (session end)", "save receiver: reset" - no stale
  world/peer references retained.
- The temporary MinJoinCap=1024 test patch was reverted (now 1 MiB
  floor again), Debug+Release rebuild clean, 77/77 tests, production
  DLL redeployed. boot.config restored via two-instance.ps1 -Restore.
## 2026-10-06 - T5.2-r1 fix round (review of a582026)

Independent review of commit a582026 ("T5.2: host delta serialize+send")
scored 2 critical / 2 important / 6 minor. Root-cause correction first:
the earlier "guest Update pump stalled" hypothesis was WRONG. Decompiling
Xenonauts.Common.Cleanup.CleanupSystem showed the real wedge - its
CleanupAtEndOfLateUpdate subscriber runs CleanupMarkedEntities, whose
loop is literally "while (t.Count > 0) { foreach item: CleanupEntity,
LeaveMarkedForCleanup, JoinMarkedForDeletion; t.Clear();
t.AddRange(_family) }". A streamed save CAN carry MarkedForCleanupGroup
entities (they serialize like anything else); once the guest mirror hit
Active, the firewall (correctly) blocked those marker-leave mutations,
the family never emptied, and the guest hard-wedged inside a single
LateUpdateEvent dispatch. LiteNetLib's socket thread kept answering
pings - which is why host channel-0 traffic looked alive.

Fixes:

- C1 MirrorSystems (new src/Mirror/MirrorSystems.cs): Harmony prefixes,
  installed from X2CoopLifecycle.Create (Release too, not probe-gated),
  that skip four passes when (MirrorState.Active AND __instance.World
  IS the bound SaveReceiver.MirrorWorld): CleanupSystem
  .CleanupMarkedEntities (private - patched on the abstract base so
  Strategy/GC/AC subclasses all inherit; THE wedge site),
  EntityDeletionSystem.DeleteMarkedEntities (bounded but re-queued
  blocked deletes every pass), SignpostSystem.Resolve() and
  StrategyXenopediaSystem.Resolve() (same while(flag) convergence shape
  driven by firewall-blocked TriggerUnlockedStateMachine writes). The
  marked entities simply stay inert zombies on the guest - host
  lifecycle is authoritative anyway (T5.3 applies host deletes). Gates
  fail OPEN; first-skip logs once per site per session; per-site skip
  counters ride the firewall summary line (mirrorSysSkips=). Audited
  and deliberately NOT patched: ProjectSystem/ItemRequestSystem/
  TimelineSystem/TimeTransitionSystem/PhasedFSM/WorldManagedScreen and
  the GC/AC loops - bounded, self-bounding, or unreachable under
  ClockFreeze (audit list is in the MirrorSystems class doc).
- C1b evidence: SaveReceiver.Activate logs markedCleanup=/markedDeletion=
  counts (entities the streamed save carried in) next to "mirror
  active"; dev knob x2coop.debugMarkCleanup=N injects N empty
  marker-carrying entities into the host world right before
  serialization (odd: cleanup; even: cleanup+deletion) so the repro is
  deterministic. Host's own cleanup self-disposes them next LateUpdate.
- C2 join watchdog: x2coop.joinReadyTimeoutMs (default 60000, 0=off)
  arms at SaveStreamer.QueueOnAccept on every stream incl. forced
  re-offers, gated on HostJoinFreeze so GuestReady/abort/strategy-left/
  disconnect end it implicitly; on timeout SessionHost.DropStalledGuest
  sends HostEnded, Disconnect()s the peer (a WEDGED guest never hits
  LiteNetLib's own 15 s silence timeout - it keeps answering pings),
  and ClearGuest releases the freeze + resets streamer/delta/capture.
- I1 PosDelta MTU: LiteNetLib decompile proved Sequenced (and
  ReliableSequenced/Unreliable) NEVER fragment - over-MTU throws
  TooBigPacketException inside NetPeer.Send, so the old
  Protocol.MaxMessageSize split budget meant every pos packet carrying
  >~1400B died on the floor. NetTransport.MaxSinglePacketSize(peer,
  method) wraps GetMaxSinglePacketSize; PumpPosTick splits to that real
  per-peer budget (bails, ops stay pending, when the query fails), burns
  one seq per emit attempt as before, and now counts _secSendFail on
  send=false plus _secEncFail on encode=null (encode fails were
  mislabeled sendFail before; stats line gained encFail=). Bytes/ops/
  msgs/PosSent count only on successful Send.
- I2 save-serialize barrier: DeltaSender.OnSaveSerialized() called from
  SendOffer after CopyWorldIntoWorldState succeeds, before the offer is
  built - drops pending ops + join buffer + resync latch captured
  BEFORE the snapshot (they could double-apply or regress the guest)
  and re-snapshots BaselineSeq so the first delta the guest sees is the
  first post-snapshot seq. Offer.BaselineSeq picks the new value up.
- M2 liveness: Consume + FoldFrameOps check World.Disposed (a disposed
  host world is not null - queue mutations for a dead world used to
  linger). M3: DeltaQueue.ResyncDroppedBytes is cumulative across the
  latched window (was: last-attempt-only). M4: Pos/Heavy sets match
  Type.FullName (was: Name - namespace collisions/renames misrouted).
  Docs: this MODLOG's channel line corrected (StateDelta =
  ChannelControl/ReliableOrdered, not ChannelSave); the T5.2 task
  report's known-issue section rewritten with the real diagnosis.
- Teardown: MirrorSystems.Reset() wired into Teardown.Run +
  SaveReceiver.Reset (counters + first-skip log set are session-scoped;
  no external hooks exist to detach).

Tests (tests/X2Coop.Tests/DeltaTests.cs, +2 = 79): join-buffer dropped
bytes accumulate across overflow+refusals and reset per window;
packer split under a worst-case 496B sequenced budget encodes every
chunk under budget.

dotnet build Debug + Release: 0 warnings/0 errors; dotnet test 79/79.

Live verify (2026-10-06, host/guest two-instance, save r3seedday1,
dev knobs deltaJoinCapKB=5 + debugMarkCleanup=8 on the host):

- Marked-save activation (the C1 wedge repro): host injected
  `entities=8 markedCleanup=8 markedDeletion=4` pre-serialize; guest
  activated three times across runs logging `mirror active ...
  markedCleanup=12 markedDeletion=4` (in-game 12: the 8 injected + 4
  the save already carried) then `GuestReady{Ready} sent` plus all
  four `mirror-systems: <x> pass disarmed on the mirror world` lines
  (cleanup/delete/signpost-resolve/xenopedia-resolve). No wedge; the
  mirror kept logging deltas/heartbeats >90 s afterward.
- Save-serialize barrier (I2): `delta: save-serialize barrier cleared
  pend=0 joinMsgs=1 baselineSeq=1` on the first offer and
  `pend=1 joinMsgs=0 baselineSeq=2` on the forced re-offer - real
  pre-snapshot traffic dropped live.
- Forced resync end-to-end (run with the 5 KB dev cap): `delta: join
  buffer over cap=5120 (dropped ~6KB) - resync forced at GuestReady`;
  at GuestReady#1 `join deltas overflowed - forcing resync` ->
  join-freeze re-latch -> re-offer baselineSeq=2 -> fresh 238,792B
  stream -> guest second activation (`mirror active ...
  markedCleanup=8 markedDeletion=4` from Debug-2.json) ->
  `GuestReady{Ready}` #2 -> guest logs `net notice kind=Resynced` ->
  host `join-freeze released`. Whole cycle ~14 s.
- Watchdog (C2): fired three times on genuinely stalled joins
  (15 s/45 s/120 s knob values across runs): `join-watchdog: no
  GuestReady after <ms> - dropping stalled guest (host clock
  resumes)` -> delta reset -> freeze release -> DisconnectPeerCalled
  -> guest logs RemoteConnectionClose + receiver reset.
- Guest kill mid-session: Stop-Process on the guest PID -> host saw
  `net disconnected reason=Timeout` ~14 s later -> `delta: reset
  (GuestLeft)` + `capture: released (emitted=12584)` + `net session
  cleared` + `GuestLeft`. Host world kept running unharmed.
- Teardown/rejoin: clean `teardown: complete` (guest dir + tmp
  deleted) on every app quit observed; after a watchdog drop the
  guest reconnected via the panel Join button, streamed again and
  reached GuestReady{Ready} in ~15 s (sid=8d688b49) -> freeze
  released. Rejoin after drop verified.
- PosDelta MTU (I1): `delta-pos seq=151 ops=1 bytes=119
  budget=1428` - packets now carry the real per-peer
  GetMaxSinglePacketSize budget, no send failures logged
  (sendFail=0 encFail=0 all session).
- NEW EDGE CASE (observed, not this round's bug): a streamed save
  landing ~2-4 s after the guest's menu boot stalls the load -
  LoadGame returns fine but no StrategyWorld is ever created
  ("load in flight" until the session drops it). Menu-settled guests
  load in 7-15 s every time. The host watchdog covered it; a
  receiver-side LoadGame retry when no world binds within a few
  seconds would close the gap - follow-up candidate.

## 2026-10-07 - T5.3 guest-side StateApplier (delta apply pipeline)

The guest half of the replication loop: DeltaSink keeps the wire
accounting, StateApplier owns the world mutation. Pipeline:

    Runner.Update -> transport PollEvents -> codec -> DeltaSink
      -> StateApplier.Enqueue/EnqueuePos -> SessionClient.Tick
      -> StateApplier.Tick -> mirror-world mutation inside Scope()

Files: src/Replication/{EntityIdMap,ComponentTypeResolver,
MappingEntityConverter,PosCoalescer,StateApplier}.cs +
tests/X2Coop.Tests/ApplierTests.cs.

- EntityIdMap: host->guest id map with identity claims for baseline
  entities, placeholder CreateEntity() for unknown host ids (NEVER
  ForceCreateEntity - the game-side converter did that and spawned
  twin entities colliding with runtime ids), tombstones on delete so
  a stale pos/set can't resurrect a dead twin, BaselineAllocated bound
  captured at first active drain so identity claims can't steal
  post-snapshot runtime entities.
- ComponentTypeResolver: resolve component types via the world's
  registered type table first, then Type.GetType, then assembly scan
  incl. generic FullName arity parsing.
- MappingEntityConverter (fsConverter): every serialized entity ref
  routes through EntityIdMap (resolve-or-placeholder) - replaces the
  game's own converter for applier deserializes only; the shared
  serializer is bound per world via
  CommonSerializationSystem.SetupSerializer(new fsSerializer(),
  world: null) then disposed on rebind/reset.
- PosCoalescer: latest-state-per-(entity,component) keyed buffer;
  stale/out-of-order PosDelta seqs die unapplied, pos ops never
  create placeholders (no ghost resurrections), cleared on rebind,
  poison and reset.
- StateApplier.Tick (main thread only): resolves SaveReceiver.
  MirrorWorld, rebinds map+serializer+resolver whenever the world
  instance swaps, drains only under MirrorMode.Active AND
  !World.IsMutationDelayed() (the r10 probe proved in-process frames
  expose the flag), all ops inside Scope() so the T4.1 firewall
  passes them. Counters + per-msg "apply seq=.. ops=ok/n
  latencyMs=.." (x2coop.applyLogEach) + rolling "apply-stats"
  (x2coop.applyStatsMs). Pending cap x2coop.applyPendingCap trips to
  poison (a backlog the drain can't catch is as lost as a wire gap).
- Mutation-crash safety (found live): World.MutateComponentOnEntity
  pushes the mutation-delay stack and pops it ONLY on success - a
  listener/family callback throw leaves IsMutationDelayed()==true
  forever and the world corrupts (r3 crash: GeoscapeDateTimeComponent
  replace inside StrategyUISystem, then World.Process NRE'd every
  frame). ApplyOp now logs op kind/entity/type + inner exception +
  top inner frame, checks IsMutationDelayed() after a throw and
  poisons the stream on a leaked flag; a stuck flag from any other
  cause trips the x2coop.applyDelayPoisonMs watchdog (default 30 s).
  Poison freezes the mirror and re-nudges the guarded resync path on
  a ~2.5 s cadence until a fresh baseline lands.
- Baseline watermark (resync bug #1, found live): first live delta =
  SaveOffer.BaselineSeq + 1 (Messages.cs contract) but the guest used
  to keep its old watermark, so every post-resync delta read as a
  one-seq gap and resync looped forever. SaveReceiver now calls
  DeltaSink.NoteBaselineSeq + StateApplier.NoteBaselineSeq when the
  stream is armed.
- Pending-across-rebind (resync bug #2): StateApplier.Rebind used to
  drop ALL queued deltas - including valid post-snapshot deltas that
  arrived while the replacement world was still loading. Rebind now
  trims pending <= baselineSeq (snapshot already covers them),
  retains seq > baselineSeq, and seeds _lastAppliedSeq = baselineSeq.
- Session-end on LOCAL Stop (bug #3, found live): NetTransport.Stop
  clears the event queue before Runner drains it, so no Disconnected
  NetEvent reaches SessionClient - sid got cleared by the Runner
  backstop but MirrorMode stayed Active, the mirror stayed frozen and
  SaveSuppress kept LoadGame blocked forever (guest could never
  load/new-game again on that process). SessionClient.Tick now
  detects a dead/absent guest transport while state|mode is live and
  runs the shared EndSessionToIdle() (extracted; used by the
  Disconnected, HostEnded and Detach paths too) -> SaveReceiver.Reset
  -> MirrorMode=Off + firewall/pick-gate/mirror-systems/DeltaSink/
  StateApplier resets; the abandoned mirror world thaws into a normal
  local world (clock ungated, playable).

Tests (tests/X2Coop.Tests/ApplierTests.cs + csproj links for the
game-free files): EntityIdMap claim/placeholder/tombstone/reuse,
BaselineAllocated bound, PosCoalescer latest-wins/stale-die/
clear-on-reset, resolver generic-name handling; 98/98 green.

dotnet build Debug + Release: 0 warnings/0 errors; dotnet test 98/98.

Live verify (2026-10-07, two-instance, host loads save r3seedday1,
run A host=12096/guest=17620, run B host=8028/guest=40116):

- Join->stream->activate: HelloAck accept sid=... -> offer
  bytes~216K baselineSeq=0 -> stream ok 4 chunks ->
  MirrorMode=PendingActivate+LoadGame -> screen-guard bound
  StrategyWorld -> mirror active markedCleanup=0 markedDeletion=0 ->
  apply: bound world types=907 -> baseline captured allocated=1825 ->
  apply seq=1..N; apply-stats fail=0 ph=0; delta-rx gaps=0 to
  lastSeq=2701 over ~5 min. Guest clock tracks host (geodate
  01:17->04:36 held->released), funds identical $2,500,250 both
  sides, "Spectating host" banner, pos ops applying (apply-pos
  seq=N ops=6/6).
- Mid-session manual resync (run A): ResyncReq{ManualRequest} ->
  host re-streams baselineSeq=1980 -> guest rebind world=null ->
  load -> mirror active world=225815312 -> FIRST post-baseline delta
  seq=1981 applied (latencyMs=11148 - queued through the ~11 s
  reload, retained by the rebind fix) -> gaps=0 continues, no false
  gap, no stream-broken. Run B repeats with baselineSeq=513 ->
  apply seq=514 latencyMs=11234, gaps=0 lastSeq=658.
- Guest-local mutation firewall: zero "firewall: blocked" lines all
  session (spectating UI never attempts a mutation); mirror-freeze
  held geodate all session so only applied deltas advance it; F2 on
  the guest produced no mutation attempts and no effect.
- Local Stop (run B, the new reset): "net session cleared (transport
  stopped)" -> NEW "session guest: transport stopped (state=InSession
  mode=Active) - session end reset" -> MirrorMode=Off -> apply: reset
  -> save receiver: reset -> "mirror-freeze ... (released - clock
  ungated)" + mirror-muts (released) - abandoned mirror becomes a
  live local world, loads unblocked. Host logs GuestLeft +
  delta: reset and keeps serving.
- Health: no EntityMutationException / mutation-delay leak / FSM
  state-loss / fatal crash in either run (the r3 crash signature is
  gone - probe delayedN=0 inprocN=0 all session both worlds).

### T5.3-r1 review fix
- Reviewer Important: op-crash poison only covered the leaked-flag subset;
  EntityMutationException can escape with the flag INTACT when
  PopAndHandleMutationDelayStack throws after popping (world lands in
  _illegalState, mutation half-applied -> silent divergence). Fix: poison on
  `w.IsMutationDelayed() OR ex is EntityMutationException`; log carries
  flagLeaked=0/1. StateApplier.cs ApplyOp catch.
- Deferred minors (T5.5): stale/dup deltas count as gaps= (forward-jump-only
  resync already correct; cosmetic metric pollution); fail= op drops are
  accepted silent divergence pending hash check; negative type cache never
  rescues late-registered types; BaselineAllocated capture window; cosmetic
  nits.

## 2026-10-07 - T5.4 cascade measurement + per-op firewall scope (r2 C3)

While StateApplier drains a host op inside Scope(), the game's listener
systems fire LOCAL ApplyComponentMutation calls of their own on the guest
mirror - "cascades". If those touch serializable types they double-apply
(the host ran the same listeners and streams its own cascade results as
ops), so the T5.3 batch/time-window carve-out (InScope = everything passes)
had to be measured and possibly tightened to per-op scope.

Files: src/Replication/InFlightOp.cs (new), src/Replication/StateApplier.cs,
src/Mirror/MirrorFirewall.cs, src/DebugSettings.cs,
tests/X2Coop.Tests/ApplierTests.cs (+csproj link).

- InFlightOp (game-free: ints+enums only so xunit drives the truth table):
  Begin(kind) marks the in-flight op (Create/Delete/Set/Remove) around the
  op's apply body; Arm(entityId,componentId) marks the op's terminal world
  call. Both tokens save+restore the FULL tuple so nesting/leaks can't
  bleed a stale key in. ClassifyMutation: Set/Remove arm a consume-once
  (entity,component) key - first match is Direct, a second same-key call is
  a listener re-mutation = Cascade; Delete arms an entity-wildcard key that
  matches repeatably (delete fan-out removes per-component rows on the same
  entity); unarmed in-op mutations are Internal (applier bookkeeping:
  resolve/deserialize/placeholder-create); Kind.None or non-matching =
  Cascade. IsDirectDelete covers DeleteEntityImmediately ->
  UnsafeDeleteEntity re-entry on the armed entity; ClassifyCreate splits
  World.CreateEntity into InternalCreates (unarmed bookkeeping) vs
  CascadeCreates (a listener spawning an entity mid-mutation).
- StateApplier: ApplyOp wraps every op in InFlightOp.Begin(kind); the three
  terminal calls (entity.Add / entity.Remove / DeleteEntityImmediately) run
  inside Arm(); Reset() drops the tracker too (session end, resync rebind,
  teardown all flow through it).
- MirrorFirewall: the InScope branch now classifies first instead of
  blanket-passing. Direct + Internal pass silently; cascades count into
  Cascade{Whitelisted,Serializable,ExcludedType,ExcludedEntity,Untyped,
  Deletes,Creates} + per-type tallies (bounded dict, top-5 in the summary).
  Serializable non-whitelisted cascades drop via the normal Block() path
  ("firewall: blocked cascade <Type> entity=N") only when
  x2coop.cascadeFirewall=1 (default 1, see below). Counters are public
  statics, zeroed in Reset(). Firewall stats line gained cascades=/
  cascadeSer=/cascadeBlocked=/cascadeFw=.
- Summary line every x2coop.cascadeLogMs (default 5000, 0=off), emitted
  only when totals moved:
    cascade ops=N direct=N internal=N wl=N ser=N exclT=N exclE=N
    untyped=N dels=N creates=N intCreates=N blocked=N enforce=0|1
    types=A=n,B=n sertypes=...

MEASUREMENT (live, Purple Knight save, speed 3, enforce=0 measure-only):
- Steady state ~3-4 min into the run:
    cascade ops=8909 direct=57280 internal=0 wl=0 ser=8909 exclT=0 exclE=0
    untyped=0 dels=0 creates=0 intCreates=0 blocked=0 enforce=0
    types=DurationComponent=8678,AirCombatAmmoComponent=190,
    AirCombatArmorComponent=41
- Final before the session-ending crash (below): ops=9197 direct=59153
  internal=121 ser=9197.
- 100% of cascades were serializable non-whitelisted writes - every single
  one a double-apply candidate the host stream resends anyway. DECISION:
  per-op scope, x2coop.cascadeFirewall default 1. (A batch window would
  pass them all; a narrower type-list would whitelist future types blind.)

VERIFY under enforcement (same evening, fresh pair host=35328
guest=11300, Day-1 "ATLAS Base" save of campaign Purple Knight, speed 3,
~9 min session):
- Join -> SaveOffer -> activate -> apply seq resumed; final counters
    cascade ops=10118 direct=67299 internal=121 wl=0 ser=10118
    blocked=10118 enforce=1 intCreates=121
    types=DurationComponent=10030,DynamicGroupComponent=22,
    InventorySlotOwnerLink=21,CarrierLink=10,MissionControllerLink=9
  i.e. every in-scope non-op write was a serializable cascade and was
  dropped at the sink; every op-own write passed (direct=67299).
- Post-resync window: apply seq 108..5577, gaps=0, fail=0, latencyMs
  ~8-60; rate-limited "firewall: blocked cascade DurationComponent
  entity=499" lines show the live drop path. Guest UI: "Spectating host",
  clock 23:11 tracking host, funds $2,508,750, doomsday 30% - the
  presentation stream is NOT starved by the drops.
- Graceful K teardown: HostEnded -> guest mirror-freeze released
  (geodate=23:39:38 tracked) -> MirrorMode=Off -> full reset chain; host
  GuestLeft + capture released emitted=434812.

CONCERNS observed live (honest - feed T5.5):
- Two apply crashes during baseline replay, both recovered via resync:
  apply op DeleteEntity e=1608 threw EntityException "Cannot remove
  InventorySlotOwnerLink from Entity 1573 as it does not contain
  Component" (02:51:20), and apply op SetComponent e=1544 Rank threw
  EntityMutationException "No stress component found on combatant"
  (02:56:36). The Rank crash also occurred in the measure-mode run
  (02:21:35, enforce=0) so THAT one is proven enforcement-independent:
  placeholder combatant 1544 lacks its stress component when the Rank op
  lands - an op-ordering/placeholder-content latent bug. Whether per-op
  drops contributed to the DeleteEntity crash is UNPROVEN (it appeared only
  in the enforce run; a guest-side link-add cascade we dropped could in
  principle be what the fan-out was probing - flagged for the T5.5 hash
  check and a possible wider delete arm key).
- Flaky resync recovery path: in the measure-mode run the Rank crash ->
  resync -> baseline adopted, but mirror activation then wedged on
  "UI System Trigger while World is in Delayed Mutation: Dispose" (the
  crashed op's leaked delay flag bleeds into the reload teardown) and the
  host watchdog dropped the guest. In the enforce run the same swallowed
  UI exception appeared in firewall correlation lines yet activation
  completed and ran clean 9 min. Recovery works but isn't deterministic.
- Bootstrapping note: the guest's unhandled-exception zip left
  BugReports/crash_info.json behind, which pops PreviousCrashElement on
  BOTH instances at next boot (shared profile); deleted crash_info.json +
  crash_info.html to unblock the menus. Also: CopyFromScreen grabs of this
  game can return a stale composited surface (menu invisible) -
  `um win shot --hwnd` (gfxcapture) gets the real frame.

Hash verification of mirror-vs-host state is T5.5 - explicitly deferred;
until then fail=/divergence is measured by crashes+gaps, not hashes.

Tests: ApplierTests +8 = 106/106 green (InFlightOp truth table: arm
consume-once, delete wildcard repeatable, internal vs cascade phases,
create classification, nested Begin restore, Reset).
dotnet build Debug + Release: 0 warnings/0 errors.

## 2026-10-07 - T5.5 world hash (r2 C7) - deterministic host/guest state equality

Hash pipeline: WorldState snapshot (save-path filter: alive entities minus
ExcludeFromSerializationComponent carriers, components minus
[ExcludeFromSerialization] types and IOptionalSerialization opt-outs) ->
fsData per component (guest uses HashEntityConverter to write host ids back;
unmapped refs emit a {$x2u:<gid>} marker and are counted) -> R6 whitelist
excluded on both sides -> CanonicalJson canonicalization (ordinal key sort,
type-tagged scalars, wire-normalized numerics, deterministic escapes) ->
sorted by (hostEntityId, typeFullName) -> SHA-256 over canonical lines +
per-item FNV-1a fingerprints. Type/member exclusions via
x2coop.hashSkipTypes / hashSkipMembers.

Protocol: reserved HashReq{seq}/HashResp{seq,hash,count} on the reliable
ordered channel. Host: HashNow (P key / hashOnReadyMs post-GuestReady /
scripted) -> optional TimeControlCommand.Pause (hashPause, default 1) ->
settle gate (DeltaSender.HashVisiblePending == 0 AND LastDeltaSeq quiet for
hashQuietMs, bounded by hashSettleMs) -> hash + send req -> compare only a
resp whose Seq == the requested seq. Guest: queues the req, hashes only
when StateApplier's applied watermark hits the seq exactly; a req arriving
after the watermark already passed is dropped stale; reqs time out
(hashReqTimeoutMs). Mismatch logs both digests, item/entity counts, serFail,
skipLocal/skipWl, unmapped refs, first divergent (entity,type) pairs and
member-path field diffs from level-2 dumps; hashDump writes
x2coop-hashdump-{role}-seq{N}.txt next to the x2coop log.

Fixes the live run surfaced:
- EntityIdMap identity fallback: bindings are minted lazily per applied op,
  so a fresh mirror had ~20 bindings for ~1365 entities and the hash
  skipped 1345 baseline twins as "guest-local". HostId/HostIdLive now fall
  back to guestId==hostId for in-baseline ids that are unbound, not
  tombstoned, not placeholder, and not claimed by another guest id. Plus
  _deadHostIds so a dangling ref to a deleted twin still canonicalizes to
  the host's bare-id form.
- hashPause was paired with SetSpeedTo but pause is a COUNTER
  (TimeTransitionSystem._pauseCounter): SetSpeedTo never unpauses, so the
  world stayed frozen and every checkpoint hashed identical state.
  MaybeResumeHostSpeed now repays the increment with Resume(forceResume:
  true) when - and only when - the hash issued the pause.

Live two-instance verification (host 9724 / guest 42128, save
purple_knight_0db3fda4/user_r3seedday1-1.json, hashOnReadyMs=3000,
hashResumeSpeed=3, resyncMinutes=2 on the guest, hashDumpLevel=2,
hashSkipTypes=ProjectFinishedDateTimeComponent on both sides):
- post-activation MATCH seq=19 (7865 items / 1365 ents, identical SHA-256
  58a2178f on both sides);
- sim ran hard: geoscape clock Jan-01 11:00 -> Jan-04 17:40 (~78 game-hours
  in ~40s wall at speed 3), live entity churn 1365 -> 1485 -> 1393;
- two MissedDelta resyncs (guest apply crashes op=SetComponent e=1544,
  op=DeleteEntity e=1608) each re-converged: MATCH at seq=98 and seq=143 -
  the hash itself is what proves the resync restored equality;
- periodic resyncs at +2/+4/+6 min each re-activated and re-hashed clean:
  MATCH seq=144, 145, 146 (identical digest 180dad5e - the campaign reached
  GameOver at Jan-04 17:40, which freezes the world legitimately;
  StrategyStateComponent.state="GameOver" in the dumps);
- guest counters on every hash: skipLocal=0, serFail=0, unmapped=0.

First real catch (documented exclusion): the unmasked run reported a
one-field divergence -
Xenonauts.Strategy.Components.ProjectFinishedDateTimeComponent e=299 [0]:
host 2020-01-01T11:00:00 vs guest 10:50:00. Root cause: the project-task
FSM mutates it via ListComponent<DateTime>.Add (in-place list write), and
the component is re-installed via the project/CopyInComponents path - both
bypass World.ApplyComponentMutation, so the write is invisible to
ReplicationSource and never delta-replicated (the same bypass class the
captured-op docs already flag for project queues). It only re-converges on
resync, which re-baselines the whole component. This is a T4.x replication
coverage gap the hash exists to catch - kept out of the hash via the
hashSkipTypes knob in the verification run so the other 7865 items get a
clean gate; the divergence + convergence-by-resync behavior is documented
in task-T5.5-report.md. Caveat: hashSkipTypes must be identical on both
ends (each side applies its own exclusion set).

Tests: HashTests added (canonical scalar tagging, object key order, array
order, string escapes, Int64/double/float wire normalization via a
WireReparse shim - the shipped fsJsonParser hits a Unity-Mono-only
System.ReadOnlySpan IL ref that net472 cannot load - item sort, digest
determinism, digest change on value/item diff, member-path diff). Full
suite 122/122 green; Debug + Release 0/0.

Known limitation: the campaign GameOver at Jan-04 froze the tail of the
run, so the last ~4 min hashed a static world; the live-play evidence is
the earlier churn window plus two crash-resync cycles, each of which
converged. HashNow via hotkey-P shares RequestNow with the auto-hash path
but was exercised through the scripted arm, not a physical keypress.
