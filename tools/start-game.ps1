<#
.SYNOPSIS
    Starts Decal Agent and AC:Unreal, so all that is left is logging in.

.DESCRIPTION
    Starts the installed Decal Agent in the notification area unless one is running already,
    waits until its host answers, then starts AC:Unreal unless it is running. The Agent puts the
    overlay into the game by itself once the game is up. Logging in is the player's: nothing here
    knows or types a password.

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

$agentExe = Join-Path $Agent "DecalAgent.exe"
if (-not (Test-Path $agentExe)) {
    throw "Decal Agent is not installed at $Agent. Run DecalAgentSetup.exe first."
}

if (Get-Process DecalAgent -ErrorAction SilentlyContinue) {
    Write-Host "Decal Agent is running already."
} else {
    Write-Host "Starting Decal Agent..."
    Start-Process -FilePath $agentExe -ArgumentList "--tray" | Out-Null

    # Its control pipe answers once the host is up; the game started before that would find
    # nothing listening on its port.
    $deadline = (Get-Date).AddSeconds(60)
    $pipe = "\\.\pipe\achost-control"
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
