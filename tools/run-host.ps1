<#
.SYNOPSIS
    Stops any running host, rebuilds, and starts a fresh one.

.DESCRIPTION
    Restarting by hand has three steps that must happen in order, and getting one
    wrong fails quietly: the old host keeps port 9100, so the new one refuses to
    start, the client connects to the old one, and everything looks like it worked
    apart from the features that are missing. This does the three steps and says
    what it did.

    Run it from anywhere; paths are worked out from the script's own location.

.EXAMPLE
    tools\run-host.ps1
    Watch only. Plugins decide but cannot act.

.EXAMPLE
    tools\run-host.ps1 -EnableActions -Loot
    Let VirindiTank pick things up, with the Virindi Tank plugin installed into the host's plugins
    folder (its repository's tools\install-plugin.ps1 -Destination src\AC.Host.Cli\bin\Debug\net10.0\plugins).

.EXAMPLE
    tools\run-host.ps1 -EnableActions -TestMove
    Walk forward for a second once logged in, and report how far the character moved.
#>
[CmdletBinding()]
param(
    # Let plugins act in the game at all. Everything below needs it.
    [switch] $EnableActions,

    # Let VirindiTank pick up what its rules keep, rather than only deciding.
    [switch] $Loot,

    # Once logged in, walk forward for one second and report the distance moved.
    [switch] $TestMove,

    # Print objects, damage, buffs and loot decisions as they happen.
    [switch] $Verbose_,

    # Publish to the injected overlay, so the panels appear inside the game itself.
    [switch] $Overlay,

    [string] $Server = "127.0.0.1",
    [int]    $ServerPort = 9000,
    [int]    $ListenPort = 9100,

    # Where to record the session. Defaults to a new file each day.
    [string] $Capture,

    # A .utl profile for this run. Without one, VirindiTank uses the profile last chosen in its
    # window, or Virindi Tank's own choice for the character once one logs in.
    [string] $ProfilePath
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repo "src\AC.Host.Cli"

if (-not $Capture) {
    $Capture = Join-Path $repo ("session-" + (Get-Date -Format "yyyyMMdd-HHmm") + ".acap")
}

# No profile named: VirindiTank uses the one last chosen in its window, or Virindi Tank's
# own choice for the character once one logs in, or its starter profile.

# --- 1. Stop the old host ----------------------------------------------------
# Without this the new one cannot bind, and the client happily keeps talking to
# the old one - which is the failure this script exists to prevent.
$running = Get-Process achost -ErrorAction SilentlyContinue
if ($running) {
    foreach ($p in $running) {
        Write-Host ("Stopping the host already running (pid {0}, started {1:HH:mm})." -f $p.Id, $p.StartTime)
    }

    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}
else {
    Write-Host "No host was running."
}

# --- 2. Wait for the port, so the build does not race the old process's locks -
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $held = @(Get-NetUDPEndpoint -LocalPort $ListenPort -ErrorAction SilentlyContinue)
    if ($held.Count -eq 0) { break }
    Start-Sleep -Milliseconds 300
}

$stillHeld = @(Get-NetUDPEndpoint -LocalPort $ListenPort -ErrorAction SilentlyContinue)
if ($stillHeld.Count -gt 0) {
    Write-Warning ("Port {0} is still held by pid {1}. Close whatever owns it, or pass a different -ListenPort." -f $ListenPort, $stillHeld[0].OwningProcess)
    exit 1
}

# --- 3. Build, then run ------------------------------------------------------
Write-Host "Building."
dotnet build $cli --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Warning "The build failed, so the old host is stopped and no new one is running."
    exit $LASTEXITCODE
}

$arguments = @(
    "run", "--server", $Server,
    "--server-port", $ServerPort,
    "--listen-port", $ListenPort,
    "--capture", $Capture
)

if ($ProfilePath) { $arguments += @("--set", ("VirindiTank:Profile=" + $ProfilePath)) }

if ($EnableActions) { $arguments += "--enable-actions" }
if ($TestMove) { $arguments += "--test-move" }
if ($Verbose_) { $arguments += @("--objects", "--chat") }
if ($Loot) { $arguments += @("--set", "VirindiTank:Loot=true") }
if ($Overlay) { $arguments += "--overlay" }

if ($Loot -and -not $EnableActions) {
    Write-Warning "-Loot takes nothing until plugins may act: tick 'Let plugins act' in Decal's window, or run 'achost ctl act on'."
}

if ($TestMove -and -not $EnableActions) {
    Write-Warning "-TestMove does nothing without -EnableActions."
}

Write-Host ""
Write-Host ("Recording to {0}" -f $Capture)
Write-Host ("Profile      {0}" -f $(if ($ProfilePath) { $ProfilePath } else { "Virindi Tank's choice for the character, or the last one chosen" }))
Write-Host ("Acting       {0}" -f $(if ($EnableActions) { "yes" } else { "off for now - tick 'Let plugins act' in Decal's window, or 'achost ctl act on'" }))
Write-Host ("Looting      {0}" -f $(if ($Loot -and $EnableActions) { "yes - things will be picked up" } else { "no" }))
Write-Host ("Overlay      {0}" -f $(if ($Overlay) { "yes - inject native\build\ACUnrealOverlay.dll into the client with acinject" } else { "no" }))
Write-Host ""
Write-Host "In the client, connect to $Server`:$ListenPort. Ctrl+C here to stop."
Write-Host "From another window: 'achost ctl status', 'achost ctl reload VirindiTank' after a rebuild."
Write-Host ""

& (Join-Path $cli "bin\Debug\net10.0\achost.exe") @arguments
