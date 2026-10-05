<#
.SYNOPSIS
    Updates the installed Decal Agent from dist\, and any plugin setups beside it, with the Agent restarted.

.DESCRIPTION
    Asks the running Agent to exit (`achost ctl exit`, as Exit on its tray icon's menu does,
    saving every plugin's settings), runs dist\DecalAgentSetup.exe silently over the install - and
    dist\VirindiTankSetup.exe after it if it is there (built by the Virindi Tank repository's
    tools\build-installers.ps1 and copied in) - and starts the Agent again with tools\start-game.ps1.

    The game may stay running: the setups leave the overlay the game holds alone when it has not
    changed, and set it aside when it has. The player may stay in the world too: the Agent asked
    to exit hands its session over, and the Agent started again carries it on within two minutes,
    so plugins start as at a login and nothing is asked of the player. An Agent too old to hand
    over, or one that had to be stopped, leaves nothing; the new one then asks the player in the
    game to log out to character select and enter the world again. Nothing here knows or types a
    password.

    An Agent too old to know `ctl exit` is told to reload its plugins first, which saves their
    settings, and is then stopped.

.EXAMPLE
    tools\build-installers.ps1; tools\update-agent.ps1
#>
[CmdletBinding()]
param(
    # The folder holding DecalAgent.exe; the installed one unless given.
    [string] $Agent = (Join-Path $env:LOCALAPPDATA "Programs\Decal Agent"),

    # Where the setups are.
    [string] $Dist = (Join-Path (Split-Path -Parent $PSScriptRoot) "dist"),

    # Update the Agent only, not Virindi Tank, even if its setup is in dist\.
    [switch] $AgentOnly
)

$ErrorActionPreference = "Stop"

$agentSetup = Join-Path $Dist "DecalAgentSetup.exe"
$tankSetup = Join-Path $Dist "VirindiTankSetup.exe"
if (-not (Test-Path $agentSetup)) { throw "$agentSetup is not there; run tools\build-installers.ps1 first." }
if (-not (Test-Path $tankSetup)) { $AgentOnly = $true }

function Send-HostCommand([string] $command) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "achost-control", [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(3000)
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($command + [Environment]::NewLine)
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        return (New-Object System.IO.StreamReader($pipe)).ReadToEnd()
    }
    finally {
        $pipe.Dispose()
    }
}

# Only the Agent installed in that folder is stopped: a developer's build running elsewhere
# is not this script's business.
$agentExe = [System.IO.Path]::GetFullPath((Join-Path $Agent "DecalAgent.exe"))
$running = @(Get-Process DecalAgent -ErrorAction SilentlyContinue | Where-Object {
    try { [string]::Equals($_.Path, $agentExe, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
})

if ($running.Count -gt 0) {
    Write-Host "Asking Decal Agent to exit..."
    $answer = try { Send-HostCommand "exit" } catch { "" }
    if ($answer -notmatch "exiting") {
        # Too old for `exit`: a reload saves every plugin's settings, then it is stopped.
        Write-Host "It does not know 'exit'; saving its plugins' settings and stopping it."
        try { Send-HostCommand "reload" | Out-Null } catch { }
        $running | Stop-Process -Force
    }

    $deadline = (Get-Date).AddSeconds(60)
    while ($running | Where-Object { -not $_.HasExited }) {
        if ((Get-Date) -gt $deadline) {
            Write-Host "It has not exited within a minute; stopping it."
            $running | Where-Object { -not $_.HasExited } | Stop-Process -Force
            break
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Decal Agent has exited."
}

function Invoke-Setup([string] $setup, [string] $log) {
    Write-Host "Running $(Split-Path -Leaf $setup) silently..."
    $process = Start-Process -FilePath $setup -ArgumentList @("/S", "/D=$Agent", "/Log=$log") -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "$(Split-Path -Leaf $setup) failed with exit code $($process.ExitCode); see $log." }
}

$logs = Join-Path $env:TEMP "decal-agent-update"
New-Item -ItemType Directory -Force -Path $logs | Out-Null
Invoke-Setup $agentSetup (Join-Path $logs "DecalAgentSetup.log")
if (-not $AgentOnly) {
    Invoke-Setup $tankSetup (Join-Path $logs "VirindiTankSetup.log")
}

& (Join-Path $PSScriptRoot "start-game.ps1") -Agent $Agent
Write-Host "Updated. The Agent carries on the session it was handed; if the game says to, log out to character select and back in."
