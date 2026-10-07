# Xenonauts-2-Multiplayer

A 2-player co-op mod for **Xenonauts 2** — one shared campaign, host-authoritative:

- **Host** owns the authoritative simulation.
- **Guest** receives the campaign state (full save stream + live delta
  replication) and observes a read-only geoscape mirror: clock, funds,
  bases, aircraft, research — all tracking the host in real time.
- Guest-side save/load/new-game is suppressed while in-session; a mutation
  firewall guarantees the mirror stays read-only.
- Automatic + manual resync recovers the mirror after disconnects, host
  battles, or apply faults.
- A deterministic world hash (SHA-256 over canonicalized component state)
  verifies the guest mirror converges bit-for-bit with the host.

## Status

Working prototype over loopback/LAN (LiteNetLib, bundled with the game).
Phases 0–5 implemented: transport, handshake with mod/pack parity checks,
save streaming, mirror activation, guest clock freeze, save suppression,
firewall + UI pick gating, delta replication (~15 KB/s @ speed 3, ~24% of
the 64 KiB/s budget), per-op cascade enforcement, and hash-verified
convergence (6/6 matches incl. post-resync).

Remaining before release: latency/speed matrix, spec §8 acceptance run,
and cleanup/docs.

## Structure

- `src/` — mod source (net472, Unity Mono / game content-pack API)
  - `Net/` transport + wire codec, `Session/` host/client/session logic,
    `Mirror/` firewall + freeze + suppression, `Replication/` capture,
    delta pack/send, guest apply, world hash
- `tests/` — xunit suite for the game-free components
- `pack/manifest.json` — game content-pack manifest
- `specs/`, `plans/`, `reviews/` — design spec, implementation plan, reviews
- `docs/` — interaction scripts and notes
- `MODLOG.md` — engineering journal
- `tools/` — dev helpers (two-instance launcher)
- `deploy.ps1` — builds and deploys the pack into the game's Mods folder

## Requirements

- Xenonauts 2 (developed against 7.27.x, Mono backend)
- .NET Framework 4.7.2 SDK tooling (`dotnet build`)

## Safety

No anti-cheat involvement (none detected). Single-player co-op only —
never cheats against other players. No game files, assets, or decompiled
code are distributed here.
