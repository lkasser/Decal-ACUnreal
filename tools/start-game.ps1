<#
.SYNOPSIS
    Starts Decal Agent and AC:Unreal, so all that is left is logging in.

.DESCRIPTION
    Starts the installed Decal Agent in the notification area unless one is running already,
    waits until its host answers, then starts AC:Unreal unless it is running. The Agent puts the
    overlay into the game by itself once the game is up. Logging in is the player's: nothing here
    knows or types a password.

    The host answers on its control pipe only once it is up - which is after it has looked for,
    and taken, any session the Agent before it handed over - so when this returns, the new Agent's
    `ctl status` already says what became of a handover.

.EXAMPLE
    tools\start-game.ps1
#>
[CmdletBinding()]
param(
    # The folder holding DecalAgent.exe; the installed one unless given.
    [string] $Agent = (Join-Path $env:LOCALAPPDATA "Programs\Decal Agent"),

    # AC:Unreal's launcher.
    [string] $Game = (Join-Path $env:LOCALAPPDATA "Programs\ACUnreal\ACUnreal.exe")
)

$ErrorActionPreference = "Stop"

# Started from inside a packaged app, the Agent would keep its files where a normal one never looks.
. (Join-Path $PSScriptRoot "PackageCheck.ps1")
Assert-NotInPackage

$agentExe = Join-Path $Agent "DecalAgent.exe"
if (-not (Test-Path $agentExe)) {
    throw "Decal Agent is not installed at $Agent. Run DecalAgentSetup.exe first."
}

# The pipe `achost ctl` talks to, as the host names it: ACHOST_CONTROL_PIPE, else achost-control.
$controlPipe = if ($env:ACHOST_CONTROL_PIPE) { $env:ACHOST_CONTROL_PIPE } else { "achost-control" }

$already = @(Get-Process DecalAgent -ErrorAction SilentlyContinue)
if ($already.Count -gt 0) {
    # Whichever it is - this one, or a build from somewhere else - it holds the pipe and the ports,
    # and a second would only be turned away. Said by path, so that is not a surprise.
    foreach ($process in $already) {
        $path = try { $process.Path } catch { "(its path cannot be read)" }
        Write-Host "Decal Agent is running already: $path (process $($process.Id))."
    }
} else {
    Write-Host "Starting Decal Agent..."
    Start-Process -FilePath $agentExe -ArgumentList "--tray" | Out-Null

    # Its control pipe answers once the host is up; the game started before that would find
    # nothing listening on its port.
    $deadline = (Get-Date).AddSeconds(60)
    $pipe = "\\.\pipe\$controlPipe"
    while (-not (Test-Path $pipe)) {
        if ((Get-Date) -gt $deadline) { throw "Decal Agent did not come up within a minute; see its log in $env:LOCALAPPDATA\ACHost\logs." }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Decal Agent is up."
}

if (Get-Process ACUnreal -ErrorAction SilentlyContinue) {
    Write-Host "AC:Unreal is running already."
} elseif (Test-Path $Game) {
    Write-Host "Starting AC:Unreal - log in through your 127.0.0.1:9100 entry."
    Start-Process -FilePath $Game | Out-Null
} else {
    throw "AC:Unreal was not found at $Game; give its launcher with -Game."
}
