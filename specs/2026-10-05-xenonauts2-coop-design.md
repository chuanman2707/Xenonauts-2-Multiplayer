# Xenonauts 2 Co-op — Design Spec

Date: 2026-10-05 · Status: draft v2 (revised after spec review) · Scope: slices S0 + S1
Review: `reviews/2026-10-05-spec-review.md` (verdict was REVISE — B1–B5 addressed here)

## 1. Vision (context, not all in this spec)

A 2-player co-op multiplayer mod for Xenonauts 2 (GOG, Unity 2022.3.62f2, Mono/x64).
Two commanders share **one** campaign hosted on one player's machine:

- Shared Geoscape — either player can issue strategy actions (co-commanders).
- Shared tactical battles — each player owns ~half the dropship squad via the engine's
  existing faction/controller model (`ControllerLink`, `PlayersGroup`, `TurnOwnerPlayerTag`);
  both act inside the same player turn. AI turns always run on the host.
- Host-authoritative: the host's `Artitas.World` is the single source of truth; the guest's
  game is a mirror that replays host state. Guest disconnect never harms the campaign —
  the host simply continues solo.

Reference implementations studied: Kenshi Online (injected client + dedicated server,
server-authoritative) and RimWorld-Together (Harmony mod + standalone server, world-state
sync). We adopt their technical pattern (mod + authoritative host + explicit sync layer)
but **not** their parallel-worlds scope — X2 co-op shares a single simulation.

Delivery slices:

| Slice | Deliverable | Spec |
|---|---|---|
| S0 | Content-pack mod loads; Harmony applies; 2 game instances run side by side | this doc |
| S1 | Guest connects, receives campaign save, sees host's geoscape mirrored read-only | this doc |
| S2 | Tactical co-op: per-soldier ownership, both act in same player turn | future |
| S3 | Geoscape co-command: guest actions relay to host | future — **premise revised**, see §9 |
| S4 | Air combat co-op; reconnect/resync polish; publish | future |

## 2. Scope of this spec

**In:** mod skeleton loading via the official content-pack system; host/join session over
LiteNetLib; campaign save transfer host→guest; guest "mirror mode" — frozen simulation,
host-fed component replication, read-only; in-game Co-op menu (host/join + status).

**Out:** guest-originated strategy actions (S3), tactical battle sync (S2), unit
ownership, air combat (S4), matchmaking/dedicated server, reconnect mid-battle,
>2 players, GOG Galaxy integration, mirroring of geoscape modals/popups (event-driven,
not component state — cosmetic, out of scope).

**Success criteria (verifiable in the real game):**
1. Mod installs by copying a folder into
   `%USERPROFILE%\AppData\LocalLow\Goldhawk Interactive\Xenonauts 2\Mods`; appears in the
   game's content-pack menu; enables without crash; writes log lines via
   `UnityEngine.Debug` into its own log output (never log4net ERROR — that feeds
   Goldhawk's analytics, see §7).
2. Two instances of the game run simultaneously on one PC, each with its own
   `-logFile` path.
3. Host loads a campaign → "Host session"; guest → "Join" by IP → handshake verifies
   protocol version, game build (`XenonautsConstants.Versioning.VERSION` + `Commits`),
   mod version, and the active content-pack stamp list; mismatches are rejected with a
   human-readable reason.
4. Guest loads the transferred campaign and lands on a **live read-only geoscape**:
   host clock, aircraft/UFO icon movement, base and research changes appear on the
   guest's screen within ≤500 ms on loopback/LAN at every time speed. Event-driven
   modals (Ink dialogs, popups) are not mirrored (out of scope).
5. Read-only holds measurably: during a scripted guest interaction run the mutation
   firewall (§4) logs zero un-whitelisted mutations of replicated component types on
   the guest, and the guest world hash matches the host's after a resync.
6. If the host leaves the Strategy screen (enters ground combat / air combat / loads a
   save), the guest is notified and freezes on a "host busy" screen; when the host
   returns to Strategy, a full save resync runs and the guest resumes mirroring.

## 3. Verified recon facts (from full decompile, `C:\Users\chuan\xenonauts2-decomp\full\`)

**Mod loading — confirmed, lower risk than assumed:**
- `ContentPackState.Patch()` loads every `Assembly` asset in a pack
  (`Assembly.Load(byte[])` — no `Location`), runs `_harmony.PatchAll(assembly)` with id
  `"{UID}@{path}"`, then instantiates every non-abstract `IContentPackLifecycle` and
  calls `Create(this, harmony)`. `Unpatch()` calls `Destroy()` + `UnpatchAll` on disable.
- `IContentPackLifecycle`: `Create`, `Destroy`, `OnWorldCreate(Section, WeakRef<World>)`,
  `OnWorldDispose`, `GetRequiredAssets`. `Section` = AirCombat, AirCombatSimulation,
  GroundCombat, Strategy — `OnWorldCreate(Strategy, …)` is the clean entry to the
  geoscape `World`.
- Packs are discovered one folder deep under `Mods\` via `*.json` manifests
  (`FileSystemContentPackDiscoverer`). Base pack lives at `Assets\Assets\xenonauts\`
  per `contentpacks.json`. Manifest is FullSerializer JSON with `$t` tags — generate it
  via the in-game pack-authoring path (`ContentPackAuthoring.CreateLocalPack`), never
  hand-write it. Assemblies are folder-typed: DLL goes under `<pack>\assembly\…`
  (exact sub-path is a recon item); `.pdb` alongside gives line numbers in stack traces.

**Strategy time source — the guest sim freeze is a single hook:**
- All geoscape time flows through
  `Xenonauts.Strategy.Systems.TimeTransitionSystem.UpdateTimeOnBase(DeltaTimeEvent)`
  (`[Subscriber(Priority = int.MaxValue)]`): advances `GeoscapeClockTag`/
  `GeoscapeDateTime`, fires `ClockStepReport`, runs `GeoscapeSchedulerComponent` tasks.
  Returns early when `IsPaused()` (pause is a counter — release exactly once).
  Other `DeltaTimeEvent` subscribers only recompute derived state; none are time sources.
  One Harmony prefix freezing this method freezes the guest's simulation.
- Pause/resume API: `TimeControlCommand.Pause()/Resume()/SetSpeedTo(int)`;
  tick events `PreGeoscapeTickReport`, `PostGeoscapeTickReport`, `ClockStepReport`.
- Tick rate: `StrategyConstants.ADVANCE_TIME_RATE_PER_TICK_SPEEDS` ≈ one tick every
  16–25 ms real time (40–60 ticks/s) — **rules out any per-tick state snapshot**.

**Mutation sink — the replication choke point (replaces the EventBus premise):**
- `Artitas.World.ApplyComponentMutation(entityID, componentID, previousC, nextC, added)`
  — private, non-generic: every component add/remove (immediate or delayed) ends here.
  Deletes end in `World.DeleteEntityImmediately`; creation in `World.CreateEntity`.
- Player strategy actions are **not** events: ~238 `[GameAPI]` methods on Strategy
  systems mutate entities in place (e.g. `XenonautSquadronSystem.CreateSquadronAndLaunch`
  → `MoveTo`), vs ~20 `*Command` event classes (mostly UI/camera/time). Event dispatch
  is `World.HandleEvent<T>`/`QueueEvent<T>` → `ProcessStrategy.ProcessEvent(IEvent)`
  (generic — do not patch generics on Mono; patch `ProcessEvent` if needed).
  `Artitas.Utils.EventBus` is a helper used by only two classes — **not** the game's bus.

**Serialization / save:**
- `Artitas.Systems.ArtitasSerializationSystem` (`Serializer` = fsSerializer with Artitas
  converters, `CopyWorldIntoWorldState`, `HandleSerialization`, `HandleDeSerialization`).
  Serialize = whole world only; deserialize = `DeleteAllEntities()` then rebuild — only
  usable behind a screen transition. Entity IDs survive save/load (component matrix is
  ID-indexed).
- `ExcludeFromSerializationAttribute` / `ExcludeFromSerializationComponent` mark
  local/presentation state — the filter rule `WorldState` itself uses.
- `SaveGameSystem`: `static LoadGame(FileDescriptor, bool)`, `ProcessSaveGameRequest`,
  `QueueSaveGameRequest`, `RequestAutoSave`. `InternalProcessSaveGameRequest` writes
  under `Folders.Save()`, enforces `SaveGameLimit` (evicts oldest saves in the group),
  fires `PostSaveGameReport`, makes hidden copies in Ironman — so the co-op transfer must
  **not** reuse the normal save path.
- `SaveGame.activeContentPacks` is recorded but **not enforced** on load
  (`CanLoad()` only checks `MIN_SAVE_VERSION`) — handshake must check pack parity.

**Transport:** `FlyingWormConsole3.LiteNetLib.*` lives **inside Assembly-CSharp** as
public types (`NetManager`, `NetDataWriter`, `NatPunchModule`) — reference directly, no
embedded copy needed.

**Two-instance hazards:** `Xenonauts2_Data\boot.config` contains `single-instance=`
(Unity Force Single Instance — second launch likely focuses the first window and exits);
both instances share the profile dir — `Player.log` rotates on startup (use `-logFile`),
`Saves\` collides (guest writes to `Saves\x2coop_guest\`), `Settings\` is shared;
`GoGCorePlatformModule` may fail to init in the second instance (S0 gate check).

**Logging:** log4net root level is ERROR and feeds `AnalyticsLogAppender` → PostHog.
Mod logs via `UnityEngine.Debug` + own file sink; never at ERROR, never with IPs/player
names.

## 4. Architecture

```
┌── host instance ─────────────────────────────────┐  ┌── guest instance ────────────────────────────────┐
│ X2 game (authoritative Artitas.World)             │  │ X2 game (mirror-mode Artitas.World)             │
│   ↑                                               │  │   ↑                                             │
│ X2Coop.dll (single assembly, content pack)        │  │ X2Coop.dll (same build)                         │
│  ├─ SessionHost: listen, handshake, pack parity   │  │  ├─ SessionClient: connect, handshake           │
│  ├─ ReplicationSource: postfix ApplyComponent-    │  │  ├─ MirrorMode:                                 │
│  │   Mutation / Create / Delete → coalesced       │  │  │   a) freeze TimeTransitionSystem            │
│  │   per-frame deltas on ReliableOrdered          │  │  │   b) mutation firewall (see below)          │
│  ├─ SaveStreamer: serialize world → chunk stream  │  │  │   c) suppress all save paths                │
│  ├─ ScreenGuard: notice when leaving Strategy     │  │  ├─ StateApplier: apply deltas in applier-      │
│  └─ NetTransport (LiteNetLib)                     │◄─┼──┤   scope; ID-map new entities                 │
└───────────────────────────────────────────────────┘  │  └─ NetTransport (LiteNetLib)                   │
             UDP :27960 (host) / ephemeral (guest)     └─────────────────────────────────────────────────┘
```

Components:

- **`NetTransport`** — LiteNetLib wrapper around the game's bundled `NetManager`.
  Channels: `ReliableOrdered` for control, save chunks and **deltas** (never Sequenced
  for deltas — it drops out-of-order packets); `Sequenced` only for idempotent full
  values (e.g. clock). Host binds 27960 (Windows Firewall prompt documented); guest
  binds ephemeral. `NetManager.PollEvents()` driven from the Unity main thread
  (MonoBehaviour `Update` or a `DeltaTimeEvent` subscriber) — all world access is
  single-threaded.
- **`SessionHost`/`SessionClient`** — handshake, save transfer, session notices,
  Co-op panel UI.
- **`ReplicationSource`** (host) — Harmony **postfixes** on `World.ApplyComponentMutation`,
  `World.CreateEntity`, `World.DeleteEntityImmediately` (non-generic, Mono-safe),
  active only for the Strategy world while a session lives. Filters out
  `ExcludeFromSerialization*` components/entities; coalesces per frame (last write wins
  per `(entityID, componentType)`); serializes each component via the game's own
  `ArtitasSerializationSystem.Serializer`; emits `StateDelta` batches with monotonic `seq`.
- **`MirrorMode`** (guest):
  1. **Clock freeze** — prefix on `TimeTransitionSystem.UpdateTimeOnBase` (no-op while
     guest session active). Freezes time; derived-state systems stay harmlessly idle.
  2. **Mutation firewall** — prefix on `World.ApplyComponentMutation` (and the
     create/delete sinks): outside `StateApplier` scope, drop mutations of
     serializable component types and log the call stack via `UnityEngine.Debug`.
     Whitelist guest-local presentation state (camera, UI components) discovered during
     recon — this is the mechanism that makes "read-only" real despite 238 direct
     `[GameAPI]` mutators.
  3. **Save suppression** — prefix out `SaveGameSystem.QueueSaveGameRequest`,
     `RequestAutoSave`, Ironman hidden copies while mirroring; guest never writes saves.
- **`StateApplier`** (guest) — applies `StateDelta` inside an explicit applier scope
  (flag the firewall honors). Entity identity: IDs match after the initial same-save
  load; entities created later go through a host→guest ID map; component-internal
  entity references are remapped — the Entity converter's wire form is a recon item.
  Family listeners then update UI exactly as in normal play.
- **`SaveStreamer`/`SaveReceiver`** — host serializes the world via
  `SerializationRequestCommand` and builds a `SaveGame` to a custom `FileDescriptor`
  (mirroring `InternalProcessSaveGameRequest` but bypassing `SaveGameLimit` eviction
  and Ironman copies). Guest stores it under `Saves\x2coop_guest\` (visible to
  `GetSaveGameDescriptors`' AllDirectories scan; deleted on session end) and enters via
  `SaveGameSystem.LoadGame` behind the normal load-screen transition.
- **`ScreenGuard`** — host posts `HostLeftStrategy`/`HostReturned` on screen
  transitions; on return, triggers the full save resync.
- **`CoopUI`** — minimal IMGUI panel: Host/Join, IP field, status, guest "spectating"
  banner, "host busy" screen.

## 5. Replication model (S1) — time-boxed spike with degrade path

The S1 spike: prove host-side mutation capture + guest-side apply renders a coherent
live geoscape. If full component replication proves too broad (ID-mapping gaps,
converter edge cases), degrade in this order and record the choice in MODLOG:

1. **D1 (target):** all serializable Strategy components replicated (design above).
2. **D2:** replicate only clock (`GeoscapeDateTime`), aircraft/UFO position components
   and base/research summary components; everything else via periodic full save resync
   (e.g. every N minutes + on demand).
3. **D3 (floor):** no live deltas — guest mirrors via periodic save resync only
   (a "spectate that refreshes" experience). Still proves transport + save pipeline +
   freeze + firewall; live replication becomes S1.5.

## 6. Protocol (v1, little-endian via `NetDataWriter`; every msg prefixed with sessionId)

| Msg | Dir | Payload |
|---|---|---|
| `Hello` | G→H | protocolVer, modVer, gameVer+commits, playerName, activePackStamps[uid+ver], campaignId? |
| `HelloAck` | H→G | accept / reject+reason (version/pack mismatch, not on Strategy screen, session full) |
| `SaveOffer` | H→G | sessionId, campaignId, baselineSeq, byteCount, sha256 |
| `SaveChunk` | H→G | offset, chunk[64 KiB zlib] |
| `SaveAck`/`GuestReady` | G→H | save loaded, world initialized, ready for deltas |
| `StateDelta` | H→G | seq (starts baselineSeq+1), entityID→component ops[] |
| `ResyncReq` | G→H | reason — host answers with new `SaveOffer` |
| `SessionNotice` | both | GuestJoined/GuestLeft/HostEnded/HostLeftStrategy/HostReturned/Resynced |
| `Ping/Pong` | both | 2 s keepalive; 15 s silence = disconnect |

Join consistency: on accept, host pauses the clock (`TimeControlCommand.Pause()` —
counter released exactly once at `GuestReady` or abort), snapshots `baselineSeq`,
serializes, streams; guest acks → buffered deltas `> baselineSeq` flush → resume.
Join is refused unless the host is on the Strategy screen.

## 7. Implementation notes

- Layout in this repo: `mods/xenonauts2-coop/` — `src/X2Coop.csproj` (single assembly),
  `pack/` content-pack template (manifest generated in-game, committed), `deploy.ps1`
  → `%USERPROFILE%\AppData\LocalLow\Goldhawk Interactive\Xenonauts 2\Mods\x2coop\`.
- Target framework: prefer `net472` with `<Reference Private="false">` against
  `Xenonauts2_Data\Managed\*.dll` (game assemblies are .NET Framework 4.x-profile Mono);
  `netstandard2.1` is the fallback if S0 shows otherwise.
- **Single assembly only** — `Assembly.Load(byte[])` has no `Location`; a second DLL
  won't resolve. Reference only game DLLs (never shipped). No `Assembly.Location`-
  relative paths — resolve the pack folder from `ContentPackState.path` in
  `IContentPackLifecycle.Create`.
- Patch only non-generic methods; verify each patch fires (no inlining) via the log.
- `IContentPackLifecycle.Destroy` must stop `NetManager`, remove MonoBehaviours, and let
  `Unpatch` clean Harmony state.
- Two-instance dev setup (S0 gate, dev-only, never shipped): back up `boot.config`,
  remove `single-instance=`; launch each instance with `-logFile <path>`; verify GOG
  Galaxy module failure modes in instance 2.
- Manifest discovery → enabled-by-default vs. menu toggle + restart requirement is an
  S0 recon item (`ContentManager.ReconcilePreferences`/`ContentPackSettings`).
- Decompiled reference: `C:\Users\chuan\xenonauts2-decomp\full\` (ilspycmd 8.2.0.7535
  `-p`, `DOTNET_ROLL_FORWARD=Major`). Journal: `MODLOG.md`.

## 8. Testing / verification

- S0 exit: pack appears in content-pack menu, enables, `Player.log`/own log shows
  `IContentPackLifecycle.Create`; a probe Harmony patch on a non-generic method fires;
  two instances run with separate log files (post `boot.config` edit).
- S1 exit: scripted oracle — host pauses, performs a fixed sequence (advance one speed
  step, start a research, move a squadron), resumes; guest mirror must show identical
  clock/icon/base state ≤500 ms; firewall log shows zero un-whitelisted replicated
  mutations during the guest's scripted clicking; host→battle→return triggers
  `HostLeftStrategy`/freeze/resync correctly.
- Circuit breakers: (a) replication spike → degrade D1→D2→D3 per §5, record in MODLOG;
  (b) any patch silently failing 3 runs → stop, dump method identity/inlining state,
  pick a different hook; (c) `single-instance` workaround failing → escalate to user
  (two-PC testing is the last resort).

## 9. Explicit non-goals, flag for S3, risk register

- No game files, decompiled code, or extracted assets are shipped — mod + docs only.
- Guest needs own X2 copy + host IP; no NAT relay (Hamachi/ZeroTier for WAN).
- Air combat host-only until S4.
- **S3 premise revision (from review B2):** "commands are events" is false — S3 needs
  either a curated list of relayed `[GameAPI]` strategy methods or host-side replay of
  UI intents. The S3 spec must start from that, not from EventBus interception.
- Risk register (ordered): live replication fidelity (D1→D3 degrade) → guest mutation
  firewall coverage (whitelist gaps leak guest-local mutations — mitigated by hash
  check + resync) → two-instance/shared-profile quirks (dev-only workaround) →
  session-lifecycle edge cases (transitions, rejoin) → code loading (low; verified).
