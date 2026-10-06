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

    The handover is followed from end to end and said out loud: the file the Agent that stopped
    left, with its full path and time; what the Agent started again found, from its `ctl status`;
    and a warning, naming the file and the log to read, when the new Agent did not take a file the
    old one left.

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

    # The Agent's data folder, where it hands its session over: its default unless given.
    [string] $Data = (Join-Path $env:LOCALAPPDATA "ACHost"),

    # Update the Agent only, not Virindi Tank, even if its setup is in dist\.
    [switch] $AgentOnly
)

$ErrorActionPreference = "Stop"

# Started from inside a packaged app, the Agent would keep its files where a normal one never looks.
. (Join-Path $PSScriptRoot "PackageCheck.ps1")
Assert-NotInPackage

$agentSetup = Join-Path $Dist "DecalAgentSetup.exe"
$tankSetup = Join-Path $Dist "VirindiTankSetup.exe"
if (-not (Test-Path $agentSetup)) { throw "$agentSetup is not there; run tools\build-installers.ps1 first." }
if (-not (Test-Path $tankSetup)) { $AgentOnly = $true }

# The pipe `achost ctl` talks to, as the host names it: ACHOST_CONTROL_PIPE, else achost-control.
$controlPipe = if ($env:ACHOST_CONTROL_PIPE) { $env:ACHOST_CONTROL_PIPE } else { "achost-control" }

function Send-HostCommand([string] $command) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $controlPipe, [System.IO.Pipes.PipeDirection]::InOut)
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

# The session handed over: what is in the data folder from the moment the Agent is asked to go.
$askedAt = Get-Date
$handedOver = @()

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

            # Stopped is not yet gone: the setups must not start while it still holds its files.
            foreach ($process in $running) { $process.WaitForExit(15000) | Out-Null }
            break
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Decal Agent has exited."

    # Written as the host stopped, before the process ended; a second's grace for the clocks.
    $handedOver = @(Get-ChildItem -Path (Join-Path $Data "handover-*.bin") -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $askedAt.AddSeconds(-1) })
    if ($handedOver.Count -gt 0) {
        foreach ($file in $handedOver) {
            Write-Host ("It handed its session over in {0} ({1:N0} bytes, written at {2:HH:mm:ss})." -f $file.FullName, $file.Length, $file.LastWriteTime)
        }
    } else {
        Write-Host "It left no session in $Data to hand over: the game was not in the world, or the Agent was too old or had to be stopped."
    }
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

# The Agent that answers now should be the one just installed, not some other build on the pipe.
if (-not (Get-Process DecalAgent -ErrorAction SilentlyContinue | Where-Object {
    try { [string]::Equals($_.Path, $agentExe, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
})) {
    Write-Warning "The Decal Agent running is not the one installed in $Agent; the update takes effect once that one runs."
}

# What the new Agent found: it looks for the session before it serves its pipe, so this is final.
$status = try { Send-HostCommand "status" } catch { "" }
$handoverLine = @($status -split "`r?`n" | Where-Object { $_ -like "handover *" }) | Select-Object -First 1
if ($handoverLine) {
    Write-Host ("The new Agent's " + ($handoverLine -replace "^handover\s+", "handover: "))
} elseif ($status) {
    Write-Host "The new Agent's status says nothing of a handover: it is older than this script, or not relaying a live game."
}

$agentLog = Join-Path $Data ("logs\DecalAgent-" + (Get-Date -Format "yyyyMMdd") + ".log")
foreach ($file in $handedOver) {
    if (Test-Path -LiteralPath $file.FullName) {
        Write-Warning ("The new Agent did not take {0}, which is still there ({1:N0} s after it was written). Its log, {2}, says the path it looked at and what it found there." -f $file.FullName, ((Get-Date) - $file.LastWriteTime).TotalSeconds, $agentLog)
    }
}

if ($handedOver.Count -gt 0 -and ((Get-Date) - $askedAt).TotalSeconds -gt 120) {
    Write-Warning "The update took more than two minutes; a session handed over is not carried on after that."
}

Write-Host "Updated. The Agent carries on the session it was handed; if the game says to, log out to character select and back in."
