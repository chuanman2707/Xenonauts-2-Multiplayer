# X2 Co-op — install notes (draft for the Phase-6 README)

Evidence: MODLOG T0.5 entry (2026-10-05), verified on GOG build 7.27.11.

## Install

1. Copy the pack folder into the game's mods directory:
   `%LOCALAPPDATA%Low\Goldhawk Interactive\Xenonauts 2\Mods\x2coop\`
   (the folder name is arbitrary — discovery is manifest-driven — but keep `x2coop`
   for consistency with `deploy.ps1`). The layout must be:

   ```
   Mods\x2coop\
     manifest.json
     preview.png
     assembly\common\X2Coop.dll
   ```

2. **The mod is NOT enabled automatically.** Xenonauts 2 discovers new packs but adds
   them disabled (`Enabled=false` in `Settings\contentpacks.json`). You must enable it
   once by hand:
   - Main menu → **MODS** (MOD MANAGER).
   - Tick the checkbox next to **X2 Co-op**.
   - A **MISSING DEPENDENCIES — "Version Mismatch: Xenonauts 2 Core"** dialog appears
     on every enable (cosmetic engine quirk: the manifest's core-dependency version was
     stamped with the app version 7.27.11 while the core pack reports 7.27.10).
     Click **YES** — it is only a warning.
   - Click **SAVE & CLOSE**.

3. The game then shows **RESTART REQUIRED — "Mods or content packs have changed.
   Restart Xenonauts 2 for changes to take full effect."**
   - The mod is already loaded at this point (its `Create` lifecycle ran the moment you
     clicked SAVE & CLOSE — check `Logs\x2coop-<pid>.log` under the profile folder).
   - Still, choose **QUIT TO DESKTOP** and relaunch — recommended so all content
     indexes and the new-campaign flow see a clean state. (CANCEL also works; starting
     a NEW CAMPAIGN without restarting pops a second warning with a CONTINUE ANYWAY
     option.)

4. From then on the mod loads automatically at every launch — the enabled state is
   persisted in `Settings\contentpacks.json` and `Create` fires during boot, before
   the main menu.

## Uninstall / disable

- MOD MANAGER → untick **X2 Co-op** → SAVE & CLOSE. The mod's `Destroy` lifecycle and
  Harmony unpatch run immediately; a restart is again advised.
- Or delete the `Mods\x2coop` folder — the preference entry is cleaned up on next
  boot (the game rewrites `contentpacks.json` at launch).
