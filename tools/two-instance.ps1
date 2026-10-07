#Requires -Version 5.1
<#
.SYNOPSIS
    Run two Xenonauts 2 instances side by side for X2 Co-op development.

.DESCRIPTION
    Xenonauts 2 ships `single-instance=` in Xenonauts2_Data\boot.config (Unity
    Force Single Instance): a second process hands off to the first and exits.
    This tool removes that line (dev-only, reversible) and launches a host +
    guest pair with separate -logFile outputs.

    -Patch    backs up boot.config -> boot.config.x2coop.bak (only if no backup
              exists yet) and removes the `single-instance=` line. Edits a file
              in the install folder - ask the user before the first run.
    -Restore  copies the backup back byte-for-byte (SHA256-verified).
    -Launch   starts Xenonauts2.exe twice with the dev window args and
              -logFile <mod>\.dev\logs\host.log / guest.log, then prints both
              PIDs. Kill them by exact PID (um win kill <pid> / Stop-Process).
    -CoopAutoStart  (with -Launch) adds -x2coop-host to the host instance and
              -x2coop-join to the guest: T2.1 dev triggers (X2COOP_PROBES /
              Debug builds only) auto-listen on 27960 and auto-connect to
              127.0.0.1:27960 at first Runner.Update — no input driving needed.
              Release builds ignore the args silently.

    Boot.config on this install is UTF-8 no-BOM, LF endings; -Patch removes the
    line's bytes only, so -Restore is byte-identical.

    PowerShell 5.1 compatible. Never commit boot.config.x2coop.bak (it lives in
    the game dir, not the repo).

.PARAMETER GameDir
    Xenonauts 2 install dir. Default: env X2_DIR, else 'E:\GOG Games\Xenonauts 2'.

.PARAMETER StaggerSeconds
    Pause between the host and guest launches (lets the host grab the profile /
    window first). Default: 10.

.EXAMPLE
    .\tools\two-instance.ps1 -Patch
    .\tools\two-instance.ps1 -Launch
    .\tools\two-instance.ps1 -Restore
#>
[CmdletBinding()]
param(
    [switch]$Patch,
    [switch]$Restore,
    [switch]$Launch,
    [switch]$CoopAutoStart,
    [ValidateNotNullOrEmpty()][string]$GameDir = $(if ($env:X2_DIR) { $env:X2_DIR } else { 'E:\GOG Games\Xenonauts 2' }),
    [int]$StaggerSeconds = 10,
    # Extra raw command-line args appended to one instance's launch (dev/debug
    # knobs like -x2coop.fakeModVer=0.0.0 for the T2.3 reject-path verify).
    [string]$HostArgs = '',
    [string]$GuestArgs = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ($Patch -or $Restore -or $Launch)) {
    Write-Host 'usage: two-instance.ps1 -Patch | -Restore | -Launch  (see Get-Help)'
    exit 1
}

$toolsDir = $PSScriptRoot
$modDir   = Split-Path $toolsDir -Parent
$exe      = Join-Path $GameDir 'Xenonauts2.exe'
$bootCfg  = Join-Path $GameDir 'Xenonauts2_Data\boot.config'
$bootBak  = "$bootCfg.x2coop.bak"
$logDir   = Join-Path $modDir '.dev\logs'

function Log([string]$msg) { Write-Host "[two-instance] $msg" }
function Fail([string]$msg, [int]$code = 1) {
    Write-Host "[two-instance] ERROR: $msg" -ForegroundColor Red
    exit $code
}
function Sha256([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash }
function Test-SingleInstancePresent {
    $t = [System.IO.File]::ReadAllText($bootCfg)
    return ($t -match '(?m)^single-instance=')
}

foreach ($f in @($exe, $bootCfg)) {
    if (-not (Test-Path $f)) { Fail "not found: $f (check -GameDir / X2_DIR)" }
}

# --- -Patch ---------------------------------------------------------------------
if ($Patch) {
    if (-not (Test-Path $bootBak)) {
        Copy-Item $bootCfg $bootBak
        Log "backup created: $bootBak (sha256 $(Sha256 $bootBak))"
    } else {
        Log "backup already exists, not overwriting: $bootBak"
    }

    if (Test-SingleInstancePresent) {
        $text = [System.IO.File]::ReadAllText($bootCfg)
        # Remove the `single-instance=<value>` line plus its line terminator,
        # leaving every other byte (LF endings, no BOM) untouched.
        $newText = $text -replace 'single-instance=[^\r\n]*(\r\n|\r|\n)?', ''
        [System.IO.File]::WriteAllText($bootCfg, $newText)
        if (Test-SingleInstancePresent) { Fail "patch failed: single-instance= still present in $bootCfg" }
        Log "patched: removed single-instance= from $bootCfg (sha256 now $(Sha256 $bootCfg))"
    } else {
        Log "already patched: no single-instance= line in $bootCfg"
    }
}

# --- -Restore ---------------------------------------------------------------------
if ($Restore) {
    if (-not (Test-Path $bootBak)) { Fail "no backup at $bootBak - nothing to restore" }
    Copy-Item $bootBak $bootCfg -Force
    if (Sha256 $bootCfg -eq Sha256 $bootBak) {
        Log "restored byte-for-byte: $bootCfg (sha256 $(Sha256 $bootCfg))"
    } else {
        Fail "restore failed: sha256 mismatch between $bootCfg and $bootBak" 3
    }
}

# --- -Launch ---------------------------------------------------------------------
if ($Launch) {
    if (Test-SingleInstancePresent) {
        Log "WARNING: single-instance= still present in boot.config - the guest will hand off to the host and exit. Run -Patch first."
    }
    $running = @(Get-Process -Name 'Xenonauts2' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $pids = ($running | ForEach-Object { $_.Id }) -join ', '
        Log "WARNING: $($running.Count) Xenonauts2 process(es) already running (PID $pids) - launching anyway."
    }

    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $hostLog  = Join-Path $logDir 'host.log'
    $guestLog = Join-Path $logDir 'guest.log'
    # Fresh logs each launch so [X2Coop] Create checks read this run only.
    foreach ($f in @($hostLog, $guestLog)) {
        if (Test-Path $f) { Remove-Item $f -Force; Log "removed stale log $f" }
    }

    $winArgs = '-screen-fullscreen 0 -screen-width 1280 -screen-height 720'
    $hostCoop = ''
    $guestCoop = ''
    if ($CoopAutoStart) {
        # T2.1 dev triggers (probe/Debug builds): host listens :27960, guest
        # dials 127.0.0.1:27960 from the first Runner.Update. No effect on
        # Release builds.
        $hostCoop = ' -x2coop-host'
        $guestCoop = ' -x2coop-join'
        Log "CoopAutoStart: host gets -x2coop-host, guest gets -x2coop-join"
    }
    # -WorkingDirectory <gamedir> is REQUIRED: the game resolves contentpacks.json
    # via a relative path and hangs at 0% loading if started elsewhere (T0.2).
    $hostProc = Start-Process -FilePath $exe `
        -ArgumentList "-logFile `"$hostLog`" $winArgs$hostCoop $HostArgs" `
        -WorkingDirectory $GameDir -PassThru
    Log "host  PID $($hostProc.Id)  log: $hostLog"

    if ($StaggerSeconds -gt 0) { Start-Sleep -Seconds $StaggerSeconds }

    $guestProc = Start-Process -FilePath $exe `
        -ArgumentList "-logFile `"$guestLog`" $winArgs$guestCoop $GuestArgs" `
        -WorkingDirectory $GameDir -PassThru
    Log "guest PID $($guestProc.Id)  log: $guestLog"

    Log "both launched. Verify: Select-String '\[X2Coop\] Create' $hostLog,$guestLog"
    Log "kill by exact PID: um win kill $($hostProc.Id) ; um win kill $($guestProc.Id)"
}
exit 0
