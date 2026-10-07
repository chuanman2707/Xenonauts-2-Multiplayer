# Spec re-review (r2) — Xenonauts 2 Co-op design (S0 + S1)

Date: 2026-10-05 · Reviewed: `specs/2026-10-05-xenonauts2-coop-design.md` (draft v2) against
`reviews/2026-10-05-spec-review.md` (r1, REVISE). Spot-checked against the decompile at
`C:\Users\chuan\xenonauts2-decomp\full\`.

## Blocking issues

| # | r1 issue | Status | Notes |
|---|---|---|---|
| B1 | Per-tick snapshot feed infeasible | **Resolved** | §4 `ReplicationSource`/`StateApplier` replaces it with component-delta capture at `World.ApplyComponentMutation` + `CreateEntity`/`DeleteEntityImmediately`, an `ExcludeFromSerialization*` filter, per-frame coalescing, ReliableOrdered + `seq`, and full save resync as the backstop. §5 time-boxes this as a spike and defines the D1→D2→D3 degrade path, recorded in MODLOG. Re-verified: `World.cs:213` is private and non-generic. The clock, scheduler and movement code all use copy-and-replace (`AddGeoscapeDateTime`, `GeoscapeSchedulerComponent.Copy()`→`Add`, `GeoscapeMovementSystem.AddPosition/AddVelocity`), so mutations do pass through the sink. |
| B2 | "Every action is an event" | **Resolved** | §3 corrects the premise. MirrorMode now has layered guards: clock freeze, mutation firewall with whitelist and stack logging, and save suppression, with UI blocking cosmetic. §9 flags the S3 premise. |
| B3 | Two instances / shared profile | **Resolved** | `single-instance=` dev workaround (back up and restore), `-logFile` per instance, `Saves\x2coop_guest\`, shared `Settings\` documented, GOG module check in instance 2 (§3, §7, §8). |
| B4 | Session lifecycle / protocol | **Resolved** | Join pause + `baselineSeq`, join only on Strategy, `HostLeftStrategy`/`HostReturned` + resync, custom-`FileDescriptor` save path that bypasses `SaveGameLimit` and Ironman, guest save suppression, pack/build parity in `Hello`, `sessionId`/`GuestReady`/`ResyncReq`, no deltas on Sequenced, main-thread `PollEvents`. |
| B5 | Criteria 4/5 not measurable | **Resolved** | ≤500 ms on loopback/LAN at every speed, with modals explicitly out of scope. Criterion 5 is now zero un-whitelisted firewall hits plus a world-hash match after resync. |

## Non-blocking corrections from r1

| Item | Status |
|---|---|
| Pack layout `assembly/` folder, sub-path recon | Done (§3). Re-verified `XenoAssetConfigPrototypes.cs:59/61`. |
| Manifest generated in-game, default-enable recon | Done (§3, §7) |
| Single assembly, no `Location`, `ContentPackState.path` | Done (§7) |
| net472 + `Private=false` | Done (§7) |
| No log4net ERROR (PostHog) | Done (§1, §3) |
| Non-generic patch targets, `ProcessStrategy` concrete type | Mostly done. See C5. |
| `Destroy` tears down NetManager/MonoBehaviours | Done (§7) |
| Ephemeral guest port, firewall prompt | Done (§4) |
| Risk register reordered | Done (§9) |
| `%USERPROFILE%\AppData\LocalLow` path | Done (§1) |

## Remaining concerns (non-blocking — the plan must handle them)

- **C1 — Entity converter side effect (new, important).** `Artitas.Serialization.Converters.EntityConverter`
  writes an Entity as a bare int ID (or `{"$content": id}`). On deserialize it calls
  `EntityManager.ForceCreateEntity(id)` when that ID is not alive. If the guest decodes deltas
  with the game's own `Serializer`, it will create phantom entities at host IDs, which can collide with
  guest-local IDs. The `StateApplier` must decode with its **own** `fsSerializer` that uses a
  mapping Entity converter. `Common.UID.Systems.UIDSystem.EntityConverter` (stable `UID` component) is
  a possible identity key for UID-carrying entities. The wire-form recon item is partly answered. What
  remains is the guest's ID allocation behaviour.
- **C2 — The firewall cannot "drop" `CreateEntity`.** It returns an `Entity`, so callers would NRE.
  Allow guest-local creation, firewall only component mutations and deletes, and track and log orphan
  entities. `UnsafeDeleteEntity` bypasses `DeleteEntityImmediately` and must also be hooked.
- **C3 — Applier scope vs. the delayed-mutation stack.** Mutations queued while
  `IsMutationDelayed()` apply later through `ApplyDelayedMutation`, possibly outside the scope. Family-listener
  cascades that run *inside* the scope are allowed and may duplicate host-derived state. Apply
  deltas only from the main-thread poll, outside `World.Process()`, and assert
  `!IsMutationDelayed()`. The spike must measure cascades.
- **C4 — Join pause is fragile.** Many paths fire `TimeControlCommand.Resume()`
  (`StrategyUISystem:641`, `GeoscapeMainElement:362`, `Notifications:475` delayed 3 s, mission
  effects), and the host player can click play. During join, freeze the host with the same
  `UpdateTimeOnBase` prefix gate instead of the pause counter. `baselineSeq` buffering keeps the join
  correct even if time leaks.
- **C5 — Exceptions in lifecycle callbacks** are logged by `ContentPackState` at `Log.Error`, which goes to
  PostHog. Wrap every `IContentPackLifecycle` method body in try/catch → own log. `ProcessStrategy` is
  `DefaultProcessStrategy` unless `Artitas.Event.ENABLE_PROFILING` (`StrategyScreen.cs:209`).
  S1 does not need to patch it.
- **C6 — Guest quick-load.** `SaveGameSystem.HandleQuickLoadShortcut` and the direct
  `ProcessSaveGameRequest` call must also be suppressed on the guest. `ScreenGuard` should key off
  `OnWorldCreate/OnWorldDispose(Section.Strategy)` (`StrategyScreen.cs:212/429`).
- **C7 — The world hash is undefined.** The plan must define it: canonical serialized persistent
  components, sorted, under the host-ID mapping.

## Verdict

**PASS.** B1–B5 are resolved. C1–C7 are implementation-level and are carried into the plan
(`plans/2026-10-05-s0-s1-implementation-plan.md`) as recon tasks or task requirements.
