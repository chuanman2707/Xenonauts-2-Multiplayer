#Requires -Version 5.1
<#
.SYNOPSIS
    Build + deploy the X2 Co-op mod into the Xenonauts 2 profile Mods folder.

.DESCRIPTION
    1. Aborts (exit 2) if Xenonauts2.exe is running - the game locks the loaded
       DLL and would not reload it anyway. Reports the PID(s); never kills.
    2. dotnet build src\X2Coop.csproj (-Configuration, default Debug).
    3. Copies pack\* -> <ModsRoot>\x2coop\ and the built X2Coop.dll/.pdb into
       <ModsRoot>\x2coop\assembly\common\ (R1 recon: the loader scans
       <pack>\assembly\common\ recursively for .dll + .pdb).
    4. Dedup: reads our pack UID from pack\manifest.json at runtime, then removes
       any OTHER folder under <ModsRoot> whose manifest.json carries the same
       UID - a duplicate UID would double-list/conflict the pack in the menu.
       (Discovery is "manifest.json exactly one level deep under Mods\", so the
       folder name is cosmetic; x2coop works.)
    5. Prints the deployed file list with sizes. Re-running is idempotent.

.PARAMETER Clean
    Remove <ModsRoot>\x2coop before copying (fresh deploy).

.PARAMETER Configuration
    MSBuild configuration. Default: Debug.

.PARAMETER ModsRoot
    Profile Mods dir. Default: %USERPROFILE%\AppData\LocalLow\Goldhawk
    Interactive\Xenonauts 2\Mods ($PROF\Mods).
#>
[CmdletBinding()]
param(
    [switch]$Clean,
    [ValidateNotNullOrEmpty()][string]$Configuration = 'Debug',
    [ValidateNotNullOrEmpty()][string]$ModsRoot = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Goldhawk Interactive\Xenonauts 2\Mods')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modDir    = $PSScriptRoot
$packDir   = Join-Path $modDir 'pack'
$csproj    = Join-Path $modDir 'src\X2Coop.csproj'
$deployDir = Join-Path $ModsRoot 'x2coop'
$asmDir    = Join-Path $deployDir 'assembly\common'

function Log([string]$msg) { Write-Host "[deploy] $msg" }
function Fail([string]$msg, [int]$code = 1) {
    Write-Host "[deploy] ERROR: $msg" -ForegroundColor Red
    exit $code
}

# --- 1. game-running guard --------------------------------------------------
$x2 = Get-Process -Name 'Xenonauts2' -ErrorAction SilentlyContinue
if ($x2) {
    $pids = ($x2 | ForEach-Object { $_.Id }) -join ', '
    Fail "Xenonauts2.exe is running (PID $pids). Close the game first - deploy refuses to touch a locked/running install." 2
}

# --- 2. build -----------------------------------------------------------------
Log "dotnet build -c $Configuration"
& dotnet build $csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed (exit $LASTEXITCODE)." }

$binDir = Join-Path $modDir "src\bin\$Configuration\net472"
$dll = Join-Path $binDir 'X2Coop.dll'
$pdb = Join-Path $binDir 'X2Coop.pdb'
foreach ($f in @($dll, $pdb)) {
    if (-not (Test-Path $f)) { Fail "expected build output missing: $f" }
}

# --- 3. read pack UID (runtime, not hardcoded) ----------------------------------
$packManifestPath = Join-Path $packDir 'manifest.json'
if (-not (Test-Path $packManifestPath)) { Fail "pack manifest not found: $packManifestPath" }
$uid = $null
try { $uid = (Get-Content $packManifestPath -Raw | ConvertFrom-Json).asset.UID } catch { }
if ([string]::IsNullOrWhiteSpace($uid)) { Fail "could not read asset.UID from $packManifestPath" }
Log "pack UID = $uid"

# --- 4. optional clean -----------------------------------------------------------
if ($Clean -and (Test-Path $deployDir)) {
    Log "-Clean: removing $deployDir"
    Remove-Item $deployDir -Recurse -Force
}

# --- 5. copy pack + built assembly -------------------------------------------------
New-Item -ItemType Directory -Force -Path $asmDir | Out-Null
Copy-Item (Join-Path $packDir '*') $deployDir -Recurse -Force
Copy-Item $dll, $pdb $asmDir -Force
Log "copied pack\* + X2Coop.dll/.pdb -> $deployDir (dll under assembly\common\)"

# --- 6. dedup: remove other folders whose manifest has the same UID -----------------
foreach ($dir in (Get-ChildItem $ModsRoot -Directory)) {
    if ($dir.Name -ieq 'x2coop') { continue }
    $mf = Join-Path $dir.FullName 'manifest.json'
    if (-not (Test-Path $mf)) { continue }
    $otherUid = $null
    try { $otherUid = (Get-Content $mf -Raw | ConvertFrom-Json).asset.UID } catch { }
    if ([string]::IsNullOrWhiteSpace($otherUid)) {
        Log "skip $($dir.Name): manifest has no readable asset.UID"
        continue
    }
    if ($otherUid.Trim() -ieq $uid.Trim()) {
        Log "removing duplicate pack folder '$($dir.Name)' (same UID $otherUid)"
        Remove-Item $dir.FullName -Recurse -Force
    }
}

# --- 7. report ----------------------------------------------------------------------
Log "deployed files under ${deployDir}:"
Get-ChildItem $deployDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($deployDir.Length).TrimStart('\', '/')
    Write-Host ("  {0,-45} {1,10:N0} bytes" -f $rel, $_.Length)
}
exit 0
