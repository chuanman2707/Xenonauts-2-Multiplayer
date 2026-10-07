# T4.2 scripted guest-interaction run

Fixed checklist for spec criterion 5 ("read-only holds measurably"): drive the
GUEST instance through a spectator's natural clicks on the mirrored geoscape and
prove the mutation firewall blocks every state change. The run is bounded by the
RUN BEGIN / RUN END marker hotkey; the END line dumps
`firewall summary: unwhitelisted=<n>` which must read 0.

## Marker hotkey: Home (VK 0x24)

`Runner.PollRunMarker` (src/Runtime/Runner.cs) toggles on `KeyCode.Home`:

- 1st press: `run: RUN BEGIN (key=Home role=<r> mode=<m>) - baseline firewall summary: ...`
- 2nd press: `run: RUN END (role=<r> mode=<m>) - firewall summary: unwhitelisted=0 blockedMuts=<n> blockedDeletes=<n> guestCreates=<n> untyped=<n> exceptions=<n> swallowed=<n> autoResyncs=<n> blockedPicks=<n> logOnly=0 whitelist=12`

Counters are session-scoped (`MirrorFirewall.Reset` runs only on session
end / fresh join via `SaveReceiver.Reset`), so a mid-run auto-resync does NOT
zero them - the END summary covers the whole session; the BEGIN line is the
baseline for the diff.

Why Home: the decompile (`xenonauts2-decomp\full`, `KeyCode.*` grep) binds
F1-F4 (time speeds), F5/F9 (quick save/load), F7 (FPSTest), F8
(CommonDebugSystem), F10 (GC DebugCheatPanel), F11 (BugReporter),
Escape/Space/Return/Tab, arrows + WASDQEXFR CI, Alpha1-9, +/-,
PageUp/PageDown, Mouse0-2. Our mod owns F6 (panel), F7/F8 (probes), H/J/K/B
(dev). F12 is the de-facto screenshot key (avoided since T2.4); letters fire
while typing in text fields; Delete is hazardous near the save menu; Insert
risks overtype toggles. `Home`/`End`/`Insert` have ZERO references in the
whole decompile - Home types no text and is present on every keyboard.

## Setup (once per run)

1. Deploy the Debug build: `deploy.ps1` (aborts if Xenonauts2 is running).
2. `tools/two-instance.ps1 -Launch` - 1280x720 windows, logs at
   `.dev\logs\host.log` / `guest.log`; note both PIDs.
3. Host: click CONTINUE (loads the latest campaign save) -> geoscape ->
   F6 -> Host (or `-x2coop-host` auto-listen via `-CoopAutoStart`).
4. Guest: F6 -> Join (defaults to 127.0.0.1:27960; or `-x2coop-join` +
   `-x2coop.joinRetryMs=2000` auto-dial via `-CoopAutoStart -GuestArgs`).
   Wait for `mirror active: geodate=` in the guest's
   `$PROF\Logs\x2coop-<guestpid>.log` and the "Spectating host" banner.
5. Drive the guest by PID only (two same-name processes):
   keys `.\.dev\tools\drive-pid.ps1 -ProcId <guestPid> -Key 0xNN`;
   clicks `.\.dev\tools\raw-click.ps1 -ProcId <guestPid> -X <x> -Y <y>`
   (client coords; aim with `um win shot --hwnd <guestHwnd>` +
   `.\.dev\tools\ocr.ps1`; screenshots land in `.dev\evidence\t42-*`).
   VK map: Home 0x24, Esc 0x1B, Space 0x20, F1-F4 0x70-0x73, F5 0x74,
   F9 0x78, digits 1-9 = 0x31-0x39, W 0x57 A 0x41 S 0x53 D 0x44.

## Expected per-action outcomes

- Screen navigation / camera pan / hover / selection: presentation traffic -
  whitelisted or excluded-entity mutations; at most tolerated `guest-local
  create` lines. A `blocked` line here means a whitelist GAP - investigate
  (spec: type missing from Whitelist or a gate bypass) and report it; do not
  hide it.
- Orders with real state (hire, research start, manufacture queue, facility
  build, interceptor launch, save/delete ops): `firewall: blocked ...` lines.
  Rate limit is 1 line per component type per 5 s - the counters always
  increment even when the line is suppressed.
- Save/load attempts do NOT go through the firewall: they are blocked earlier
  by SaveSuppress (`blocked QueueSaveGameRequest`, `blocked
  HandleQuickLoadShortcut`, `blocked LoadGame...`) - also expected.
- A blocked action can leave the game's command handler half-run: the
  correlated exception is swallowed by the firewall finalizers
  (`guest exception(s) correlated with dropped mutations`), and at 3 hits the
  firewall auto-requests a resync (`auto-resync requested`) - a short
  "Resyncing" / "Spectating host" cycle mid-run is EXPECTED, not a failure.
  Wait for `mirror active: geodate=` again, then continue the checklist.

## The checklist (guest window only)

1. Press Home -> `run: RUN BEGIN` + baseline summary.
2. Camera pan: hold A then D ~0.5 s each -> whitelisted presentation only.
3. Time speed: press F2 then F1 -> attempt to change speed; the
   `mirror-freeze geodate=` heartbeat must stay flat; any time-control
   mutations show as `blocked`.
4. Space (panic button) once -> same expectation as step 3.
5. Left-click a base icon on the geoscape, then left-click an empty map
   point (select + click-to-order attempt).
6. Right-click the geoscape once (tooltip-lock / cancel-pick path).
7. Soldiers screen (key 5): open Recruit, click a recruit row, click the
   hire/confirm button. Expect `blocked mutation` lines
   (AssignedPersonnelOwnerLink / Cash / OwnsLink / ControllingLink /
   AssignedPersonnelLink / AssignedLoadoutProfileLink /
   InventoryTemplateComponent) and a swallowed
   `TargetInventory is missing` exception. Repeat the hire attempt up to 3x
   total until `auto-resync requested` fires; wait for the mirror to come
   back (`mirror active: geodate=`), then continue.
8. Research screen (key 3): select a project in the list, click the
   start/begin button -> `blocked` lines expected.
9. Engineering screen (key 4): select a project, click queue/build ->
   `blocked` lines expected.
10. Base screen (key 2): click an empty facility slot, pick any facility,
    confirm -> `blocked` lines expected (or a plain UI refusal if the game
    rejects the click before issuing a command - record which).
11. Stores screen (key 6): open, click an item row once -> browse only.
12. Armoury screen (key 7): open, click a soldier then a piece of equipment
    (equip attempt) -> any mutation shows as `blocked`.
13. Aircraft screen (key 8): select an interceptor and click its
    launch/order control -> `blocked` lines expected. Fallback if no target
    exists: back on the geoscape (key 1), click the interceptor icon, click
    a destination, confirm the move order.
13b. LAUNCH AIRCRAFT (geoscape top-bar button): since T4.2-r1 the click is
    refused EARLY by the pick gate (`src/Mirror/UIPickGate.cs`, prefix on
    `StrategyUISystem.TriggerEventOnFSM`) while the guest is in-session:
    expect `blocked SelectTarget (guest in-session, mode=Active,
    params=GeoscapeSelectTargetElement.Parameters) - geoscape pick-mode
    entry refused (picks=N)`. The pick mode is NEVER entered - no "Enter
    State:GeoscapeSelectTarget" line, no "SELECT TARGET FOR MISSION LAUNCH"
    banner - and clicking is safe to repeat. Region/base-selection and
    base-placement pick triggers are refused the same way; the T4.1
    firewall still owns the mutation sinks underneath.
14. Xenopedia (key 9) + the funding/diplomacy report (geoscape top-bar
    button): open, click one entry, close -> browse only.
15. Escape -> pause menu -> SAVE GAME -> type a name -> click save ->
    `blocked QueueSaveGameRequest` (+`FlushSaveQueue`); Escape out.
16. F5 (quick save) -> `blocked QueueSaveGameRequest`.
17. F9 (quick load) -> `blocked HandleQuickLoadShortcut`.
18. Escape -> pause menu -> LOAD GAME -> click a save row -> click load ->
    `blocked LoadGame` (LoadGameCommand funnel); Escape out.
19. Press Home -> `run: RUN END` + final `firewall summary:` line.

## Pass criteria (spec criterion 5)

- `firewall summary: unwhitelisted=0` at RUN END (n = 0 leaks).
- Every attempted mutating action shows `firewall: blocked` (or a
  SaveSuppress `blocked <method>`) in the guest log; no applied mutation
  (`[LOG-ONLY - applied]` must not appear with `logOnly=0`).
- geodate stays frozen for the whole run (except the sanctioned mirror
  reload during an auto-resync).
- No crash modal on the guest; `Saves\` gains only `x2coop_guest\` stream
  files.
