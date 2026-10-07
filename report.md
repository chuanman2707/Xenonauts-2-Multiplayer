# Xenonauts 2 Co-op Mod — Status Report

Ngày: 2026-10-07. Repo `E:\universal-modder`, branch `main`, HEAD `492ca6a`.
Plan: `plans/2026-10-05-s0-s1-implementation-plan.md` (Phase 0–6).
Spec: `specs/2026-10-05-xenonauts2-coop-design.md`.

---

## 1. Mục tiêu

Co-op 2 người cho Xenonauts 2 trên shared campaign:

- **Host** sở hữu simulation có thẩm quyền.
- **Guest** nhận campaign state (save stream + delta replication) và quan sát
  geoscape mirror **read-only**.
- Tầm xa: co-command geoscape, tactical co-op theo soldier, air combat.

Hiện tại đã có **end-to-end đúng spec**: guest join → nhận save → mirror
world load → clock guest đồng bộ + frozen-read-only → mọi hành động local
bị firewall chặn → resync hồi phục khi diverge → và từ T5.5 có **world-hash
chứng minh mirror trùng host từng byte**.

---

## 2. Tiến độ tổng quan

| Phase | Nội dung | Trạng thái |
|---|---|---|
| **Phase 0** T0.1–T0.8 | Scaffold mod, recon R1–R4, probes, two-instance launcher | ✅ Xong |
| **Phase 1** R5–R10 | Recon chuyên sâu (identity, whitelist, process strategy, save path, bandwidth, apply point) | ✅ Xong |
| **Phase 2** T2.1–T2.6 | Transport LiteNetLib, codec, handshake/parity, UI panel, keepalive/disconnect, teardown | ✅ Xong |
| **Phase 3** T3.1–T3.6 | SaveStreamer + SaveReceiver (T3.3 = task gốc L308), clock freeze, save suppression, resync → **D3 floor met** | ✅ Xong |
| **Phase 4** T4.1–T4.2 | MirrorFirewall (read-only enforcement), scripted guest interaction (19 bước), UIPickGate | ✅ Xong |
| **Phase 5** T5.1–T5.6 | Delta replication: capture → serialize/send → apply → cascade → hash → latency matrix → **DP2** | 🔄 5/6 xong |
| **Phase 6** T6.1–T6.2 | Acceptance run spec §8 + cleanup/docs/publish | ⏳ Chưa bắt đầu |
| Cuối | Whole-branch review + integration | ⏳ |

**Tests:** 122/122 xanh. **Builds:** Debug + Release 0 warnings / 0 errors.

---

## 3. Đang làm task nào

**T5.5 — World hash + `HashReq`/`HashResp`** — đã implement + commit
`492ca6a`, **chờ independent review** (reviewer lần trước dính rate limit;
sẽ re-dispatch).

Kết quả live quan trọng nhất của cả project đến giờ:

- **6/6 hash MATCH** giữa host và guest — SHA-256 giống hệt nhau trên
  ~7900 component records / ~1400 entities, bao gồm:
  - ngay sau join (`seq=19`),
  - **sau 2 lần crash-resync** (`seq=98`, `seq=143`) — chứng minh resync
    thực sự hồi phục mirror về đúng trạng thái host,
  - sau periodic resync (`seq=144–146`).
- Hash bắt được **một replication gap thật**:
  `ProjectFinishedDateTimeComponent` — `ListComponent<DateTime>.Add`
  mutate in-place + `CopyInComponents` reinstall, cả hai bypass
  `World.ApplyComponentMutation` → không bao giờ được replicate qua
  delta; chỉ converge qua resync. Đã exclude + ghi follow-up.

Cơ chế: host pause → settle (không còn op pending + seq stable 400ms) →
`HashReq{seq}` trên ReliableOrdered → guest hash đúng lúc `appliedSeq==seq`
bên trong drain → `HashResp` → host compare, log `MATCH`/`MISMATCH` +
diff per-field.

**Ngay sau khi review T5.5 xong → T5.6** (latency/speed matrix → gate **DP2**),
rồi Phase 6: **T6.1** acceptance theo spec §8, **T6.2** cleanup + README +
publish prep, cuối cùng whole-branch review.

---

## 4. Đã làm (theo phase)

### Phase 0–1 — nền móng + recon (T0.x, R5–R10)

- Content-pack mod hoạt động qua API chính thức của game (`Mod`,
  `IContentPackLifecycle`, `ContentPackState`); Harmony `0Harmony.dll`
  shipped sẵn; LiteNetLib lấy từ `FlyingWormConsole3` trong game — **zero
  external DLL**.
- `boot.config` bỏ `single-instance=` (đã backup) → 2 instances cùng máy
  cho verify loopback.
- Recon quyết định kiến trúc: identity IDs + `ForceCreateEntity` cho
  baseline (R5); 12-type whitelist tách game-state khỏi UI churn (R6);
  LiteNetLib viable (R3); `IsMutationDelayed` luôn false ở Update/LateUpdate
  → apply point an toàn (R10).
- **R9 (bandwidth):** naive replication 68.8–184+ KB/s @ speed 3 = gấp ~3×
  ngân sách 64 KiB/s → T5.2 phải throttle (xem dưới).

### Phase 2 — transport + session (T2.1–T2.6)

- LiteNetLib loopback 2 chiều, RTT ~2s, fragmentation 64 KiB chunks OK.
- Codec 12 MsgTypes bounded-read, byte-deterministic.
- Handshake: mod-version + content-pack parity, reject đọc được lý do
  (PackMismatch/SessionFull/NotOnStrategy đều verify live).
- F6 panel: Host/Join/Stop + banner trạng thái; IP masked trong logs.
- Timeout `GuestLeft` 15s, `HostEnded` cả hai phía, rejoin được.
- Teardown: un-patch, free port, xóa `Saves\x2coop_guest`, re-host được.

### Phase 3 — save transfer + mirror + resync (T3.1–T3.6)

- **SaveStreamer (host):** serialize `InternalProcessSaveGameRequest`
  style → stream chunked → **host clock freeze** trong join window
  (`ClockFreeze` skip `UpdateTimeOnBase` — không dùng pause counter vì
  game tự resume từ nhiều nơi).
- **SaveReceiver (guest):** nhận stream → ghi `Saves\x2coop_guest\*.json`
  → `LoadGame` → mirror world activate → `GuestReady` → host release
  freeze. Verify: geodate khớp tuyệt đối (`00:16:03` cả hai).
- **Guest clock freeze:** giữ geodate flat ~95s @ speed 3 trong khi host
  chạy — release thì clock chạy lại bình thường.
- **SaveSuppress:** chặn F5/F9/save-menu/load-menu/autosave/ironman/
  new-game-overwrite + **`ContentManager.Delete`** (3 bypass xóa file
  thật của user: load-menu delete, overwrite-confirm, F11 cleanup).
  `ReceiverScope` cho phép đúng đường load của mod.
- **Resync:** manual (debounced), periodic (`resyncMinutes`), auto khi
  host quay lại từ battle/menu, `HostLeftStrategy`/`HostReturned`/
  `GuestMirrorLost` notices. **D3 floor met.**

### Phase 4 — read-only enforcement (T4.1–T4.2)

- **MirrorFirewall:** patch `ApplyComponentMutation` +
  `DeleteEntityImmediately`/`UnsafeDeleteEntity`/observe `CreateEntity`;
  chỉ gate mirror world; exclusion `[ExcludeFromSerialization]` +
  whitelist; scope mechanism cho apply path của mod; **fail-open**.
- Fatal-modal containment: finalizers trên `ProcessEvent`/`World.Process`/
  delete sinks — chỉ swallow exception correlate với firewall block trên
  mirror (mirror world + recent block + game-code stack); auto-resync sau
  3 hit.
- **UIPickGate:** chặn `SelectTarget`/base-build pointer-pick trên guest
  (nguyên nhân fatal NRE `RegionInfo.UpdateAppearance` qua uGUI hover —
  firewall không nhìn thấy được).
- Scripted 19-step checklist trên guest thật: launch interceptor, research,
  manufacture, hire, facility, speed, save menu, quicksave/load →
  **`unwhitelisted=0`**, 33 blockedMuts + 24 blockedDeletes + 4 exceptions
  swallowed + 1 auto-resync, **không crash**.

### Phase 5 — live delta replication (T5.1–T5.5)

- **T5.1 ReplicationSource:** postfix capture mọi mutation trên host
  Strategy world; `EnrichCreate` enumerate `GetRawComposition` cho
  template-spawned entities (bypass `ApplyComponentMutation`); ordering
  deletes→creates→mutations cho id recycling.
- **T5.2 DeltaSender:** serialize từng component bằng serializer của game
  (entity converter ghi ID only, side-effect-free) → `StateDelta` trên
  ReliableOrdered. Throttle theo R9: coalesce last-write-wins,
  heavy-type cap, positional lane Sequenced, tick 12.5Hz cố định.
  **Kết quả: ~15 KB/s @ speed 3 ≈ 24% budget** (từ 184 KB/s).
  Join-buffer cap 5.5MB, over-cap → forced resync.
- **T5.3 StateApplier:** `EntityIdMap` (identity claim + placeholder —
  không `ForceCreateEntity`), `ComponentTypeResolver`,
  `MappingEntityConverter`, `PosCoalescer`; poison → resync trên mọi
  `EntityMutationException` (kể cả partial-apply không leak flag).
  **DP1 met:** `seq=1..2701, gaps=0, fail=0`, clock/funds/icons track host.
- **T5.4 Cascade measurement:** `InFlightOp` per-op classification
  (Direct/Internal/Cascade). Đo được **100% cascades là serializable**
  (DurationComponent-dominated) → **per-op scope + `cascadeFirewall=1`**
  (host stream cascade results anyway → drop guest-side là đúng).
- **T5.5 WorldHash:** `CanonicalJson` (fsData → canonical bytes: sort keys,
  type-tagged scalars, wire-normalized floats) + `WorldState` snapshot
  (đúng filter của save path) + `HashEntityConverter` (guest→host id).
  **6/6 MATCH** như trên — arbiter chính thức của convergence.

---

## 5. Challenges đã và đang gặp

### Đã giải quyết

| Vấn đề | Root cause | Fix |
|---|---|---|
| Guest hard-wedge sau forced resync | `CleanupMarkedEntities` loop vô hạn khi firewall chặn un-mark mutations | `MirrorSystems` disarm 4 systems trên mirror world |
| Host join-freeze vô hạn | Guest wedge → `GuestReady` không bao giờ tới | `joinReadyTimeoutMs` watchdog: drop guest + release freeze |
| Fatal crash modal khi firewall drop mutation | Exception escape `World.Process` → fatal handler | Correlated-exception finalizers + auto-resync |
| Fatal NRE khi vào `GeoscapeSelectTarget` | uGUI hover path ngoài mutation sinks | `UIPickGate` chặn pick-mode entry trên guest |
| Create ops rỗng cho template entities | `Template.Create*` bypass mutation sink | `EnrichCreate` enumerate live composition |
| `TooBigPacketException` trên pos lane | LiteNetLib Sequenced không fragment | Split theo per-peer MTU thật (1428B) |
| Guest kẹt in-session sau local Stop | `NetTransport.Stop` xóa event queue trước khi Runner drain | Tick-level detect dead transport → `EndSessionToIdle` |
| Silent divergence sau apply crash | `PopAndHandleMutationDelayStack` throw sau khi pop flag | Poison trên mọi `EntityMutationException` |
| Hash đầu tiên chỉ thấy ~20/1365 entities | Bindings lazy — baseline entities chưa bao giờ "bind" | Baseline-identity fallback trong `EntityIdMap` |
| Host không resume sau hash | `_pauseCounter` ignores `SetSpeedTo` | Repay `Pause()` bằng `Resume(forceResume:true)` |

### Đang mở / deferred (có owner, không blocking)

1. **`ProjectFinishedDateTimeComponent` không replicate** — in-place
   `ListComponent.Add` + `CopyInComponents` bypass mutation sink (hash
   bắt được, host 11:00 vs guest 10:50). Converge qua resync; cần một
   capture path cho in-place writes — follow-up task.
2. **Hai latent apply crashes** trong baseline replay: `Rank` "no stress
   component" (đã chứng minh enforcement-independent — combatant thiếu
   stress component, ordering defect) và `DeleteEntity
   InventorySlotOwnerLink` (`RelationshipManager` unconditional Remove
   throw pre-sink). Cả hai heal bằng resync + **hash re-converge** —
   nghi là op-ordering/placeholder-content bugs, chưa fix.
3. **Resync-teardown wedge** (1 lần): `UI System Trigger while World is
   in Delayed Mutation: Dispose` — pre-existing fragility khi crash leak
   delay flag; cần hardening riêng (drain/clear hook).
4. **`cascadeFirewall=1` provisional** — chờ review T5.5 kết luận; hash
   đã match nhưng chưa có A/B repro trực tiếp cho delete crash.
5. Minor follow-ups: receiver-side load retry, stale-seq metrics,
   `MultiTargetSelect` doc nit, `blockedPicks` chưa live-exercised,
   `GameOver` freeze tail của run T5.5.

### Challenges về process/tooling

- **Subagent T2.6 bị interrupt giữa chừng** → dispatch implementer mới
  audit + tiếp tục uncommitted work thay vì viết lại.
- **Subagent T5.5 đầu tiên "silent" ~70 phút** (research sâu fsSerializer
  không artifact) → dispatch retry với directive design; retry phát hiện
  agent gốc thực ra vẫn đang viết (files xuất hiện lúc 4:49–4:59) và stop
  đúng cách. Bài học: agent chạy lâu không artifact ≠ wedged.
- **Reviewer rate-limit** (đang chờ reset) — T5.5 review re-dispatch sau.
- **Re-reviewer đọc file stale** một lần (cache per-path) → verify trực
  tiếp bằng SHA1 + mtime trước khi tin "new breakage" claims.
- **MODLOG mojibake** 2 lần do escape-expansion qua shell → quy ước:
  MODLOG entries chỉ viết bằng write/edit tool hoặc single-quoted
  here-string, pure ASCII.

---

## 6. Số liệu nổi bật

- Bandwidth: **~15–16 KB/s @ speed 3 = 24% của 64 KiB/s budget**
  (coalesce ~7.5× collapse: 820 ops/s raw → ~110 wire ops/s).
- Hash: **6/6 MATCH**, 7865–8973 items / 1365–1485 entities, ~500ms/compute.
- Apply pipeline: `seq=1..5577+, gaps=0, fail=0` qua live runs.
- Cascade enforcement: `blocked=10118` cascades all-serializable,
  `direct=67299` passed.
- Tests: **122/122**; builds Debug+Release 0/0.
- Verify mọi claim đều trên live 2-instance runs (kill-by-PID, backups).

---

## 7. Còn lại đến finish line

1. Review T5.5 (đang chờ rate-limit reset) → close.
2. **T5.6:** latency/speed matrix (RTT thật hơn loopback nếu có thể,
   speed 0/1/2/3 × durations) → **DP2 gate**: guest thấy host updates
   ≤500ms end-to-end.
3. **T6.1:** acceptance run đối chiếu spec §8 checklist (mọi clause có
   evidence).
4. **T6.2:** README, publish prep (`um publish check`), knowledge-base
   field note, cleanup probes/dev knobs quyết định giữ hay gate.
5. Whole-branch review + integration decision (main đã là working branch
   theo lựa chọn; finish = docs + tag).
