# Xenonauts 2 Co-op — S0 + S1 implementation plan

Date: 2026-10-05 · Spec: `specs/2026-10-05-xenonauts2-coop-design.md` (v2) · Reviews:
`reviews/2026-10-05-spec-review.md` (r1), `reviews/2026-10-05-spec-review-r2.md` (r2 PASS, concerns C1–C7)

Decompile reference: `C:\Users\chuan\xenonauts2-decomp\full\` (paths below are relative to it).
Game: `E:\GOG Games\Xenonauts 2\` (`$X2`). Profile: `%USERPROFILE%\AppData\LocalLow\Goldhawk Interactive\Xenonauts 2\` (`$PROF`).

Sizing: **S** ≤ half a day · **M** ≈ 1 day · **L** 2–3 days (each includes in-game verification).
Every task logs through the mod's own logger (`[X2Coop]` prefix, `UnityEngine.Debug.Log` + own file),
**never** log4net ERROR, never IPs/player names (spec §3 Logging). "Verify" lines name the exact
evidence. Record each recon result and decision point in `MODLOG.md`.

Ground rules (apply to every task):
- Harmony targets are non-generic and resolved with `AccessTools.Method(typeof(T), "Name")`. Every patch
  logs its first hit once (`[X2Coop] patch-hit <Type.Method>`), which proves it isn't inlined.
- Every `IContentPackLifecycle` method body is wrapped in try/catch → own log (r2 C5). `ContentPackState`
  logs escaping exceptions at `Log.Error`, which feeds PostHog.
- `um backup` the Saves folder before the first modded launch of each phase.
- Commit after each task. Never commit game DLLs, decompiled code or generated game assets.

---

## Phase 0 — Dev loop (S0)

### T0.1 Project scaffold — S
- **Goal:** a buildable single-assembly mod that references only game DLLs.
- **Detail:** `src/X2Coop.csproj`, SDK style, `TargetFramework=net472`, `LangVersion=latest`,
  `<GameDir>` from env `X2_DIR` (default `E:\GOG Games\Xenonauts 2`). Add `<Reference Private="false">` for
  `Xenonauts2_Data\Managed\{Assembly-CSharp, Assembly-CSharp-firstpass, 0Harmony, UnityEngine,
  UnityEngine.CoreModule, UnityEngine.IMGUIModule, log4net}.dll`. Find which DLL defines `fsSerializer`
  (`grep -rn "class fsSerializer"` gives the namespace; confirm the assembly with `ilspycmd -l c`) and
  add it. Set `DebugType=portable`. No NuGet runtime packages. Add `Microsoft.NETFramework.ReferenceAssemblies`
  for build only. `.gitignore` covers `bin/ obj/ .dev/`.
- **Files:** `src/X2Coop.csproj`, `src/Properties/AssemblyInfo.cs` (version = modVer), `.gitignore`.
- **Verify:** `dotnet build -c Debug` succeeds. A PowerShell check
  `[Reflection.Assembly]::LoadFile(...).GetReferencedAssemblies()` lists only game/BCL assemblies.
  `bin/` contains only `X2Coop.dll` + `.pdb` (no copied game DLLs).

### T0.2 RECON R1 — manifest and `assembly/` sub-path — M
- **Question:** what folder layout and `manifest.json` does a pack the game itself creates have, and
  where under `assembly/` does the loader look for DLLs?
- **Method:** (1) Read `Xenonauts/XenoAssetConfigPrototypes.cs` around lines 50–70
  (`AddTypeMapping<Assembly>("assembly")`, `MapExtensionToType<Assembly>(".dll")`, `AssemblyPDB`) and
  `Xenonauts.Common.Assets.ContentManager.Processors.Load/AssemblyLoadProcessor.cs` for the path rule.
  (2) In game, open the content-pack menu (`Xenonauts.Strategy.UI.Elements/ContentPackMenuElement.cs`)
  and create a local pack. That calls `Common.Content.Authoring/ContentPackAuthoring.CreateLocalPack(displayName,
  modsRootFolder, gameVersion, lang, coreUid)`. (3) Inspect the folder it produced in `$PROF\Mods\`.
- **Output:** `pack/manifest.json` (copied from the generated one, display name set to "X2 Co-op"),
  the documented sub-path (e.g. `assembly/common/`), and a MODLOG entry. Never hand-edit `$t` tags. Re-generate instead.
- **Verify:** the generated pack is listed in the in-game content-pack menu (screenshot via `um win`).

### T0.3 `deploy.ps1` — S
- **Goal:** one command builds the mod and deploys it to `$PROF\Mods\x2coop\`.
- **Detail:** `dotnet build`, then copy `pack/*` and `X2Coop.dll` + `.pdb` into `<pack>\<R1 sub-path>\`.
  `-Clean` removes the target first. Abort if `Xenonauts2.exe` is running (DLL lock, and a running
  game wouldn't reload the DLL anyway). Find it by process query and report its PID. Never kill by name.
- **Files:** `deploy.ps1`.
- **Verify:** the script prints the deployed file list with sizes. A second run is idempotent (same hashes).

### T0.4 Hello-world lifecycle + logger — S
- **Goal:** prove code loads, and settle the net472 question.
- **Detail:** `src/X2CoopLifecycle.cs : Common.Content.Lifecycle.IContentPackLifecycle`.
  - `Create(ContentPackState pack, Harmony patcher)` stores `pack.path` (r1: no `Assembly.Location`) and
    `patcher.Id`, then logs `[X2Coop] Create path=<pack.path> uid=<pack.UID> asm=<GetHashCode>`.
  - `OnWorldCreate/OnWorldDispose` log the section.
  - `GetRequiredAssets` returns empty.
  - `src/Log.cs`: `Debug.Log` + an append-only file at `Constants.Folders`' Logs dir
    (`Common/Constants.cs:386`) named `x2coop-<pid>.log`, so two instances never share a file.
- **Verify:** after deploy and enabling the pack, the instance's `-logFile` and `x2coop-<pid>.log`
  contain the `Create` line, and `OnWorldCreate Strategy` appears after loading a save. If net472 fails
  to load (TypeLoadException in the log), switch to `netstandard2.1` and record the switch in MODLOG.

### T0.5 RECON R2 — enabled by default? Restart needed? — S
- **Method:** read `Common.Content.Managers/ContentManager.cs` `ReconcilePreferences` (line ~1432) and
  `Xenonauts.Common.Settings/ContentPackSettings.cs`. Then test: delete the pack's preference entry in
  `$PROF\Settings\`, start the game, and record whether `Create` fires without a menu toggle. Toggle it off
  and on in the menu and record whether a restart prompt appears.
- **Output:** an install note in the README (to be written in Phase 6) and in MODLOG.
- **Verify:** the log shows (or doesn't show) `Create` on a fresh discovery.

### T0.6 RECON R3 — side effects of the `Assembly.Load(byte[])` "neither" context — M
- **Questions and checks (each logged):**
  1. `typeof(X2CoopLifecycle).Assembly.Location == ""` → confirms no `Location`. All paths come from
     `pack.path`.
  2. **Duplicate load on re-enable:** toggle the pack off → on without a restart. Does `Create` log a
     *different* `asm=` hash? If yes, statics from the old copy survive. `Destroy` must therefore tear
     down everything (NetManager, runner GameObject, static singletons), and the new copy must not find
     stale `DontDestroyOnLoad` objects (look them up by name, destroy, then recreate).
  3. Harmony attributes and `PatchAll` against a byte-loaded assembly work. A probe patch hits (T0.7).
  4. Game-assembly references resolve without an `AppDomain.AssemblyResolve` hook (they are already loaded).
  5. Exception stack traces carry line numbers when the `.pdb` sits next to the DLL (`AssemblyPDB` mapping).
  6. `fsSerializer` can create instances of types defined in our assembly. This matters for the custom
     converter in T5.3. Register the converter explicitly with `AddConverter`, and never rely on
     fsSerializer's type scanning to find our types.
- **Output:** MODLOG table, one row per question with the result.

### T0.7 Probe patches — S
- **Detail:** `src/Probes/ProbePatches.cs`, compiled only under `#if X2COOP_PROBES`:
  - A postfix on `Xenonauts.Strategy.Systems.TimeTransitionSystem.UpdateTimeOnBase(DeltaTimeEvent)` counts calls.
  - A postfix on `Artitas.World.ApplyComponentMutation` (private; via `AccessTools.Method`) counts calls
    per `World.Name`.
  - A summary line every 5 s of real time.
- **Verify:** the log shows `patch-hit` for both. Counts rise while the geoscape runs and freeze when paused.

### T0.8 RECON R4 + two-instance launcher — M
- **Goal:** two instances run side by side with separate logs (spec criterion 2).
- **Detail:** `tools/two-instance.ps1`:
  - `-Patch` backs up `$X2\Xenonauts2_Data\boot.config` → `boot.config.x2coop.bak` (only if no backup
    exists yet) and removes the `single-instance=` line.
  - `-Restore` restores the backup byte-for-byte.
  - `-Launch` starts `Xenonauts2.exe -logFile <repo>\.dev\logs\host.log -screen-fullscreen 0
    -screen-width 1280 -screen-height 720`, then the same with `guest.log`, and prints both PIDs.
  - **Ask the user before the first `-Patch`.** It edits a file in the install folder (dev-only,
    reversible, never shipped).
- **Recon checks:**
  - With the line present, does instance 2 exit or focus instance 1? (This confirms R4.)
  - With the line removed, do both run?
  - Does `Xenonauts.Common.Platform.GoG/GoGCorePlatformModule.cs` fail in instance 2? Grep `guest.log`
    for Galaxy errors and check whether the game still reaches the main menu.
  - Does `Player.log` stay untouched when `-logFile` is used?
- **Verify:** both windows reach the main menu. `host.log` and `guest.log` each contain `[X2Coop] Create`.
  `two-instance.ps1 -Restore` leaves `boot.config` with the same hash as before.
- **Escalation:** if instance 2 still refuses, stop and ask the user (spec §8 circuit breaker (c)). The
  last resort is testing on two PCs.

**S0 exit gate:** T0.4, T0.7 and T0.8 verified, plus R1–R4 recorded in MODLOG. Stop here if code doesn't
load or two instances can't run, and report to the user.

---

## Phase 1 — S1 recon (decompile reading + probe runs; no feature code)

### R5 Entity identity and the converter (r2 C1) — M
- **Known:** `Artitas.Serialization.Converters/EntityConverter.cs`
  - Serializes an Entity as a bare int ID (or `{"$content": id}`).
  - On deserialize it calls `EntityManager.ForceCreateEntity(id)` when that ID is not alive.
  - `Common.UID.Systems/UIDSystem.cs` has an alternative `EntityConverter` keyed on the `UID` component.
- **Open questions:**
  - How does `Artitas.Managers/EntityManager.cs` allocate IDs (`nextId`, lines 21–86)? Is there a free
    pool, so that IDs get reused after deletes?
  - Does the guest create entities after load (UI, camera, notifications)? Measure by logging
    `World.CreateEntity` on an idle guest-style session for 60 s. Note: a local single-player session
    is equivalent here.
  - What share of strategy entities carry `UID`?
- **Decision output:**
  - **(a)** If the guest-local entity creation rate is zero, use identity IDs and `ForceCreateEntity` for
    new host entities.
  - **(b)** Otherwise use an explicit host→guest map, which is the default assumption.
  - Record the decision in MODLOG.

### R6 Whitelist discovery: guest-local UI/camera mutations — M
- **Method:**
  - Extend the T0.7 probe to log each `ApplyComponentMutation` on the Strategy world, grouped by
    component type. Resolve each type via `World.ComponentManager` (`FindComponentId(Type)` is the forward
    map; recon the reverse map in `Artitas/ComponentLookup.cs`/`DynamicComponentLookup.cs`).
  - Flag each type for `ExcludeFromSerializationAttribute` and the entity for
    `ExcludeFromSerializationComponent`.
  - Session (single player, **paused**): pan and zoom the camera, open every geoscape panel and every base
    screen, hover icons, open research, hire and manufacture screens, but confirm nothing.
- **Output:** `src/Mirror/Whitelist.cs`, containing the serializable types mutated by pure UI/camera
  interaction while paused (paused means no simulation, so every such mutation is presentation state).
  Record the list in MODLOG.
- **Verify:** a re-run with the whitelist loaded shows zero un-whitelisted mutations while paused.

### R7 `ProcessStrategy` concrete type — S
- Log `world.ProcessStrategy.GetType()` in `OnWorldCreate(Strategy)`. The expected value is
  `DefaultProcessStrategy` (`Xenonauts.Strategy/StrategyScreen.cs:209`, unless
  `Artitas.Event.ENABLE_PROFILING`). S1 doesn't patch it. This is recorded for S3.

### R8 Custom save write and load path — M
- **Questions:**
  - Can `AssetReference.ContentManager.Save(asset, new FileDescriptor(path, typeof(SaveGame)),
    new JsonPrinterSaveParameters(compress: true), overwrite: true, cacheAsset: false)` write outside
    `Constants.Folders.Save()`? (Check the `FileDescriptor` ctor at `Common.Content.Descriptors/FileDescriptor.cs:17`.)
  - Will `SaveGameSystem.LoadGame(FileDescriptor, bool)` accept a descriptor under `Saves\x2coop_guest\`?
    It requires `ContentManager.Exists`.
  - What is `SaveGameRequest.state`, and how do you construct a `SerializationRequestCommand` without the
    request (`SaveGameSystem.cs:401–415`)?
  - Do `PreSaveGameReport` and `PostSaveGameReport` have subscribers with side effects (achievements,
    cloud sync)? Grep the `[Subscriber]` handlers.
- **Method:** a probe hotkey writes a save to `$PROF\x2coop\tmp\probe.json` and to `Saves\x2coop_guest\`,
  then loads the second one.
- **Verify:** the load lands on the geoscape at the same date. The `Saves\` listing outside
  `x2coop_guest\` is unchanged (scripted diff before and after).

### R9 Replication volume — S
- On a host-style session, log mutations per frame and the serialized byte size of one sample per type
  at each time speed (0–3) for 60 s, using the game's `ArtitasSerializationSystem.Serializer`.
- **Output:** p50/p95 bytes per frame. Budget: under 64 KiB/s on loopback is comfortable. If it is
  exceeded, add type-level throttling to the T5.2 design before coding.

### R10 Mutation-delay context (r2 C3) — S
- In the runner's `Update` and `LateUpdate` (Phase 2), log `world.IsMutationDelayed()` and whether we are
  inside `World.Process()` (a flag set by a prefix/postfix on `World.Process`, if it's non-generic).
- **Expected:** not delayed. If it is delayed, deltas must be applied from a different point. Record it.

**Phase 1 exit:** R5–R10 recorded. Revise T5.x details here if a finding contradicts them, before any
Phase 5 code.

---

## Phase 2 — Transport and session (no world sync)

### T2.1 `NetTransport` + main-thread runner — M
- **Detail:** `src/Net/NetTransport.cs` wraps `FlyingWormConsole3.LiteNetLib.NetManager` with
  `EventBasedNetListener`.
  - Host: `Start(27960)`. Guest: `Start()` (ephemeral) + `Connect(ip, 27960, "x2coop/<protocolVer>")`.
  - `src/Runtime/Runner.cs` is a MonoBehaviour on a `DontDestroyOnLoad` GameObject named `X2CoopRunner`.
    Its `Update()` calls `PollEvents()` (spec §4: all world access on the main thread).
  - Set `DisconnectTimeout = 15000`.
  - Delivery: control, save chunks and deltas go on `DeliveryMethod.ReliableOrdered`. Only idempotent
    values (clock) may go on `Sequenced`.
  - Check that the bundled LiteNetLib supports fragmentation for large ReliableOrdered packets
    (`NetPeer.cs:489/888` `IsFragmented`, `TooBigPacketException`) and pick a safe max message size.
- **Verify:** two instances on loopback log `connected peer=<id>` on both sides. RTT is logged every 2 s.
  The first host bind triggers the Windows Firewall prompt (document it).

### T2.2 Message codec — S
- **Detail:** `src/Net/Protocol.cs`, messages per spec §6. Each message is: a `MsgType` byte, a 16-byte
  `sessionId`, then the payload, written via `NetDataWriter`/`NetDataReader`. Unknown or mismatched
  `sessionId` → drop and log.
- **Files:** `tests/X2Coop.Tests` (net472, xunit) round-trips every message. This works only if
  `NetDataWriter` loads outside Unity. If it doesn't, replace it with an in-game self-test hotkey that
  round-trips and logs `codec ok`.
- **Verify:** tests are green, or the in-game self-test logs `codec ok 9/9`.

### T2.3 Handshake + parity — M
- **Detail:** `src/Session/SessionHost.cs`, `SessionClient.cs`. `Hello` carries:
  - `protocolVer`, `modVer`
  - `XenonautsConstants.Versioning.VERSION`
  - `Commits.Instance.{ArtitasCommitHash, AssetsCommitHash, CommonCommitHash, X2CoreCommitHash}`
  - `playerName`
  - pack stamps from `ContentPackDependencyResolver.BuildStamps(AssetReference.ContentManager.GetConfig()
    .GetAllActiveContentPacks().Select(p => p.Manifest.Get()))`. This is the same call
    `SaveGameSystem.InternalProcessSaveGameRequest` uses.

  The host rejects with a reason on any mismatch, when not on Strategy (T3.1 state), or when the session
  is full.
- **Verify:**
  - With matched builds, the log shows `HelloAck accept`.
  - The debug setting `x2coop.fakeModVer=0.0.0` makes the guest show `rejected: mod version 0.0.0 ≠ x.y.z`.
  - Enabling an extra pack on one side only gives a pack-mismatch reject.

### T2.4 `CoopUI` IMGUI panel — M
- **Detail:** `src/UI/CoopPanel.cs`, drawn in `Runner.OnGUI`. A hotkey toggles it. Pick an unused key:
  grep the game's input bindings first.
  - Host button, Join button + IP field, status line.
  - Guest banners: "Spectating host", "Host busy", "Resyncing…".
  - Never draw the IP into screenshots or logs.
- **Verify:** an in-game screenshot of both instances showing their status.

### T2.5 Keepalive + disconnect — S
- **Verify:** kill the guest by PID (`um win kill`). The host logs `GuestLeft` within 15 s and its clock
  keeps running. Ending the host session makes the guest log `HostEnded` and show the banner.

### T2.6 Teardown — S
- **Detail:** `Destroy()` stops the NetManager, destroys the runner, clears statics, and deletes
  `Saves\x2coop_guest\` and `$PROF\x2coop\tmp\`. `Unpatch` is done by the game.
- **Verify:**
  - After a pack toggle off/on (if R2 allows it without a restart) or a game exit, `netstat -ano` shows
    no listener on 27960 from that PID.
  - Re-hosting works (no "address in use").
  - `Saves\x2coop_guest\` is gone.

---

## Phase 3 — Save transfer + mirror mode → reaches **D3 floor**

### T3.1 `ScreenGuard` — S
- **Detail:** track the Strategy world from `OnWorldCreate/OnWorldDispose(Section.Strategy)`
  (`StrategyScreen.cs:212/429`) as a `WeakReference<World>`. The host sends `HostLeftStrategy` on dispose
  and `HostReturned` on create. Joins are only accepted while the Strategy world is live.
- **Verify:** host enters ground combat → guest log `HostLeftStrategy` and the "Host busy" banner appears.
  Joining while the host is in a battle is rejected.

### T3.2 `SaveStreamer` (host) — L
- **Detail:** `src/Session/SaveStreamer.cs`.
  - On accept: set `HostJoinFreeze = true` (the T3.4 gate, r2 C4) and snapshot `baselineSeq`.
  - Mirror `SaveGameSystem.InternalProcessSaveGameRequest` (`SaveGameSystem.cs:401–570`) minus the
    unwanted parts:
    1. `World.HandleEvent(new SerializationRequestCommand{State=…})` inside
       `AssetDescriptor.EnterSaveSerialization()`.
    2. Build a `SaveGame` with the same fields: `version`, `versionName`, `versionDate`, `created`,
       commit hashes, `activeContentPacks`, `worldData`, and a `group` cloned from the campaign's group.
    3. `ContentManager.Save` to `$PROF\x2coop\tmp\<sessionId>.json` (the R8 path).
  - Leave out the `SaveGameLimit` loop, the Ironman copy and the screenshot.
  - Include `PreSaveGameReport`/`PostSaveGameReport` only if R8 shows they're needed or harmless.
  - Read the bytes, compute SHA-256, send `SaveOffer` then `SaveChunk`s (64 KiB). The file is already
    compressed by `JsonPrinterSaveParameters(compress:true)`, so don't zlib it again. This is a spec
    deviation; note it in MODLOG.
  - Private fields such as `_globalVariableSystem`, `difficultyConfigSystem` and `campaignLogicSystem`
    are read via `AccessTools.Field`.
- **Verify:**
  - Host log: `save bytes=N sha=… ms=…`.
  - A scripted check that the `Saves\` tree listing is identical before and after (no eviction, no new
    file).
  - The host clock does not advance between `SaveOffer` and `GuestReady`, even when the host clicks Play
    mid-join.

### T3.3 `SaveReceiver` (guest) — L
- **Detail:**
  - Reassemble the chunks and verify the SHA. On mismatch → `ResyncReq`.
  - Write to `Saves\x2coop_guest\<campaignId>-<n>.json` and set `MirrorMode.PendingActivate`.
  - Call `SaveGameSystem.LoadGame(new FileDescriptor(path, typeof(SaveGame)))`.
  - On `OnWorldCreate(Strategy)` + one frame → `MirrorMode.Active = true` and send `GuestReady`.
  - On `GuestReady` the host flushes buffered deltas `> baselineSeq` (Phase 5; in D3 there are none) and
    clears `HostJoinFreeze`.
- **Verify:**
  - The guest lands on the geoscape. The guest log shows `GeoscapeDateTime` equal to the host's frozen
    date: log the `GeoscapeClockTag` entity's `GeoscapeDateTime` on both sides.
  - After the session ends, `Saves\x2coop_guest\` is deleted.

### T3.4 Clock freeze gate — S
- **Detail:** `src/Mirror/ClockFreeze.cs` is a prefix on
  `TimeTransitionSystem.UpdateTimeOnBase(DeltaTimeEvent)`. It returns `false` when
  `(MirrorMode.Active && __instance.World == guestStrategyWorld) || HostJoinFreeze`. It does not use the
  pause counter (r2 C4: the game fires `TimeControlCommand.Resume()` from many places).
- **Verify:** at speed 3 on the guest, its clock stays static for 60 s while the host advances. The
  firewall (T4.1) is not yet active, so log any guest `ApplyComponentMutation` counts for reference.

### T3.5 Save suppression (guest) — S
- **Detail:** prefixes that return `false` while `MirrorMode.Active` on `Code.Screens.Common.Systems.SaveGameSystem`:
  `QueueSaveGameRequest`, `ProcessSaveGameRequest`, `RequestAutoSave`, `FlushSaveQueue`,
  `HandleQuickLoadShortcut` (r2 C6), `LoadGame(LoadGameCommand)` (instance), and the private static
  `IronManHiddenCopy`. Return `Result.Fail("x2coop: guest")` where the method has a return value. Each
  block logs `blocked <method>`.
  - **Exception:** our own `SaveReceiver` calls the static `LoadGame(FileDescriptor,bool)` through a
    `ReceiverScope` flag, so that path must stay unblocked.
- **Verify:** on the guest, press the quicksave and quickload keys, open the save menu and try to save,
  and wait for an autosave trigger (a new month). The log shows `blocked …`, and a scripted diff of
  `Saves\` shows no changes.

### T3.6 Resync — M
- **Detail:** the guest's "Resync" button and checksum failures send `ResyncReq`. `HostReturned` and
  `ResyncReq` make the host run T3.2 again (with a new `baselineSeq`). A periodic resync timer
  (`x2coop.resyncMinutes`, default off; on in D2/D3) is added.
- **Verify:** host → ground combat → finish → back on geoscape: the guest goes through "Host busy" →
  "Resyncing…" → spectating, and the clock matches. A manual Resync logs `Resynced` within 10 s for a
  typical save.

**Checkpoint D3 (floor):** the spec §5 D3 experience works end-to-end (spec criteria 1, 2, 3 and 6, plus
criterion 4 for the clock-at-resync only). Record it in MODLOG. Everything after this point improves on
D3.

---

## Phase 4 — Mutation firewall (guest)

### T4.1 `MirrorFirewall` — M
- **Detail:** `src/Mirror/MirrorFirewall.cs`.
  - **Prefix on private `World.ApplyComponentMutation`.** Active only when `__instance` is the guest
    Strategy world, `MirrorMode.Active` is set and `!StateApplier.InScope`.
    - Resolve the component type from `componentID` (R6 reverse map).
    - If the type is serializable (no `ExcludeFromSerializationAttribute`), the entity has no
      `ExcludeFromSerializationComponent`, and the type is not in `Whitelist`: return `false`, increment a
      counter, and log the type plus the top 8 frames of `new StackTrace(2,false)`. Rate-limit to 1 line
      per type per 5 s.
  - **Prefixes on `World.DeleteEntityImmediately` and `World.UnsafeDeleteEntity`.** Same rule (r2 C2).
  - **`World.CreateEntity` is not blocked**, because callers would NRE. A postfix records guest-local
    entity IDs and logs them (r2 C2).
  - **Wrap the guest's exceptions.** If a game system throws because a firewall-dropped mutation is
    missing, the runner catches it via `Application.logMessageReceived` filtering, counts it, and
    auto-requests a resync after 3 hits.
- **Verify:**
  - A paused UI tour produces zero blocks (R6 whitelist).
  - An unpaused tour produces blocks only from attempted actions.
  - The guest never crashes.

### T4.2 Scripted guest interaction run — S
- **Detail:** `docs/guest-interaction-script.md` is a fixed checklist with ~15 steps:
  - try to launch an interceptor, start research, queue manufacture, hire, build a facility;
  - change the time speed, open the save menu, quicksave and quickload.

  A runner hotkey logs `RUN BEGIN/END` markers and dumps the firewall counters at END.
- **Verify:** `firewall summary: unwhitelisted=<n>` at END. Spec criterion 5 needs n = 0 for
  *un-whitelisted leaks*. Every attempted action must show as **blocked**, and none as applied.

---

## Phase 5 — Replication spike (D1), time-boxed to **5 dev days**

### T5.1 `ReplicationSource` capture (host) — M
- **Detail:** `src/Replication/ReplicationSource.cs`.
  - Postfix on `World.ApplyComponentMutation`, for the host Strategy world while the session is
    `Mirroring`. Record `(entityID, componentID) → (added, nextC)` in a per-frame dictionary (last write
    wins). Holding `nextC` by reference is safe because components are replaced, not mutated in place
    (r2 verified: `AddGeoscapeDateTime`, `GeoscapeSchedulerComponent.Copy()`→`Add`,
    `GeoscapeMovementSystem.AddPosition`).
  - Postfixes on `CreateEntity` (return value), `DeleteEntityImmediately` and `UnsafeDeleteEntity` record
    entity ops.
  - Filter at flush time: `ExcludeFromSerialization*` types and entities, plus any extra exclusions from R9.
- **Verify:** a per-second log line `capture ops=… types=top5`. Paused → 0 ops. Speed 3 → roughly the R9
  numbers.

### T5.2 Delta serialize + send — M
- **Detail:**
  - Flush in `Runner.LateUpdate`. Serialize each component with the game's
    `ArtitasSerializationSystem.Serializer`. Host-side serialization is side-effect free: the Entity
    converter writes IDs only.
  - Pack ops into a `StateDelta{seq, hostSendTicks, ops[]}` and send on ReliableOrdered. Split at the
    T2.1 size limit.
  - During join (seq > baselineSeq, before `GuestReady`), buffer in memory instead of sending. Cap the
    buffer at R9 × 30 s; over the cap → drop the buffer and force a resync on `GuestReady`.
- **Verify:** log `delta seq=… ops=… bytes=…`. Bytes/s per speed is within the R9 budget.

### T5.3 `StateApplier` (guest) — L
- **Detail:** `src/Replication/StateApplier.cs`.
  - Apply in `Runner.Update`, after `PollEvents`, and only when `!world.IsMutationDelayed()` (R10, r2 C3).
  - Inside `using (StateApplier.Scope())`, so the firewall lets the mutations through.
  - **Decoding uses our own `fsSerializer`,** built by `Common.Serialization.CommonSerializationSystem.
    SetupSerializer(new fsSerializer(), world: null)`. Passing `null` skips the game's `EntityConverter`
    and its `ForceCreateEntity` side effect (r2 C1). Then `AddConverter(new MappingEntityConverter(map))`
    resolves host IDs through `EntityIdMap`, following R5's decision (identity or explicit map). An
    unknown host ID → create a placeholder via `World.CreateEntity()`, map it, and log it.
  - Apply add/replace with `Entity.Add(IComponent, componentId)` (`Artitas/Entity.cs:33452`), where
    `componentId = World.ComponentManager.FindComponentId(type)`. Apply removes and deletes through the
    corresponding `World` APIs.
  - Check `seq` continuity. A gap → `ResyncReq`.
- **Verify:**
  - The guest clock and aircraft/UFO icons move live.
  - Log `apply seq=… latencyMs=(now - hostSendTicks)`. Both instances share a machine clock on loopback,
    so the latency is direct.
  - Zero exceptions over a 5-minute run at speed 3.

### T5.4 Cascade measurement (r2 C3) — M
- **Detail:** while in scope, count `ApplyComponentMutation` calls that are not the op being applied.
  These are listener cascades.
- **Decision:**
  - If cascades touch serializable types, make the scope **per-op**, keyed on `(entityID, componentID)`,
    so that nested serializable mutations are dropped by the firewall. The host will send their results
    anyway.
  - Otherwise keep the scope as a time window.
  - Record the choice in MODLOG.
- **Verify:** after the change, the hash check (T5.5) still passes.

### T5.5 World hash (r2 C7) — M
- **Detail:**
  - `src/Replication/WorldHash.cs` covers every alive entity without `ExcludeFromSerializationComponent`,
    and on it every component of a serializable type outside the whitelist.
  - Each component is serialized with a canonicalizing serializer that writes Entity refs as **host IDs**
    (the guest maps back through `EntityIdMap`).
  - Sort by `(hostEntityId, typeFullName)` and SHA-256 the result.
  - The protocol adds `HashReq{seq}`/`HashResp{seq, hash, count}`. Both sides compute at the same applied
    `seq` while the host is paused. This is a protocol addition beyond spec §6; note it in MODLOG.
- **Verify:**
  - Hashes match **immediately after a resync** (baseline sanity check). If they don't, find the fields
    that aren't deterministic and exclude them, with the reason logged.
  - Hashes still match after a 5-minute live run at speed 3.

### T5.6 Latency/speed matrix — S
- **Detail:** for speeds 0–3, run 60 s each. Log p50/p95/max apply latency per speed and the hash at the
  end.
- **Verify:** p95 ≤ 500 ms at every speed (spec criterion 4) and the hash matches.

### Decision points (spec §5 degrade path)
- **DP1 — at the end of T5.3, or on day 3 of the time-box, whichever comes first:**
  - If capture and apply work but T5.5 hashes diverge in ways that can't be fixed within the time-box,
    or exceptions persist → **degrade to D2**.
  - **D2:** replicate only an allow-list:
    - `GeoscapeDateTimeComponent` on the `GeoscapeClockTag` entity;
    - `PositionComponent` and `VelocityComponent` on aircraft and UFO entities (identify the tags in R6);
    - the base and research summary types chosen from R6/R9.

    Everything else syncs through the periodic resync (`x2coop.resyncMinutes=5`). Hash checks become
    "matches after resync" only.
- **DP2 — D2 still fails** (e.g. new aircraft entities can't be mapped reliably) → **degrade to D3.**
  Deltas are off, the periodic resync stays on, and live replication becomes S1.5.
- **Spike end (day 5):** whatever level is achieved is frozen. Write it to MODLOG with the evidence
  (latency table, hash results, exception counts). No open-ended debugging past the time-box without
  the user's approval.

---

## Phase 6 — S1 acceptance and wrap-up

### T6.1 Acceptance run — M
- **Detail:** follow spec §8 with both instances.
  - **Host oracle sequence:** pause → speed step → start a research → launch a squadron → resume. A host
    hotkey logs `ORACLE <step> clock=… hash=…` at each step.
  - **Guest:** the T4.2 interaction run during the sequence.
  - **Then:** host → ground combat → return.
- **Evidence:** both logs plus screenshots (`um win`) captured into `.dev/evidence/<date>/`, with a
  summary table for criteria 1–6 in MODLOG. Mark each criterion met, met-at-D2/D3, or failed.
- **Verify:** every criterion has a log line or screenshot reference.

### T6.2 Cleanup + docs — S
- **Detail:**
  - Run `two-instance.ps1 -Restore` (boot.config hash matches the original).
  - Write `README.md` covering install (R2 result), Firewall prompt, LAN/Hamachi, the known limits
    (modals not mirrored, guest read-only), and that this is a dev-only boot.config edit.
  - Run `um publish check .`.
  - Draft a KB field note (`um kb new`). **Ask the user before** any `um kb pr` or publishing.
- **Verify:** `um publish check` passes. `git status` shows no game files or decompiled code.

---

## Stop criteria (global)
1. **S0 gate fails:** the pack never logs `Create`, or two instances can't run after R4. Stop and report.
   Do not start S1.
2. **A patch silently doesn't fire for 3 runs** (no `patch-hit`). Stop that task, dump
   `Harmony.GetPatchInfo` + method IL size, and choose a different hook (spec §8 b).
3. **Firewall-induced guest instability:** more than 1 auto-resync per minute in T4.2 that can't be fixed
   by the whitelist. Reduce the firewall to log-only, keep D3 read-only through clock freeze + save
   suppression, and record the downgrade in MODLOG.
4. **Phase 5 time-box (5 dev days) exhausted.** Freeze at the achieved D-level (DP1/DP2).
5. **Any save-integrity risk** (the host's `Saves\` changes unexpectedly, or a guest file appears outside
   `x2coop_guest\`). Stop immediately, restore from `um backup`, and fix before continuing.

## Task order and sizing summary

| Phase | Tasks | Size |
|---|---|---|
| 0 Dev loop (S0) | T0.1 S · R1 M · T0.3 S · T0.4 S · R2 S · R3 M · T0.7 S · R4/T0.8 M | ~4–5 days |
| 1 Recon | R5 M · R6 M · R7 S · R8 M · R9 S · R10 S | ~4 days |
| 2 Transport | T2.1 M · T2.2 S · T2.3 M · T2.4 M · T2.5 S · T2.6 S | ~4 days |
| 3 Save + mirror (→ D3) | T3.1 S · T3.2 L · T3.3 L · T3.4 S · T3.5 S · T3.6 M | ~6–7 days |
| 4 Firewall | T4.1 M · T4.2 S | ~1.5 days |
| 5 Replication spike (D1/D2) | T5.1 M · T5.2 M · T5.3 L · T5.4 M · T5.5 M · T5.6 S (time-box 5 days) | ≤ 5 days |
| 6 Acceptance | T6.1 M · T6.2 S | ~1.5 days |

Honest total: **~25 dev days** for S0+S1. The D3 floor is reachable in ~19 days, independent of the
replication spike. Phase 1 recon may run in parallel with Phase 2 once S0 passes.
