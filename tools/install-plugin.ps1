<#
.SYNOPSIS
    Builds Mag-Filter and installs it where a Decal Agent's Decal Compat finds it.

.DESCRIPTION
    Decal Agent is Decal and nothing else: it builds, references and ships no plugin but
    Decal's own. The one plugin this repository builds besides is Mag-nus's Mag-Filter, a Decal
    network filter, and this installs it the way a player installs a Decal plugin - in a folder
    of its own - leaving out anything the host supplies itself. (Virindi Tank has a repository,
    and an install script, of its own.)

    Mag-Filter is built from its own sources (third_party\Mag-Filter): they are fetched first, at
    the commit the project pins, if they are not there yet, then built against this repository's
    Decal stand-ins. It is a Decal plugin, not one of the host's, so it goes where Decal Compat
    finds Decal plugins that no registry names: "plugins\DecalCompat\Decal Plugins\MagFilter" in
    the host's data folder (%LOCALAPPDATA%\ACHost unless -Data is given), with its licence and a
    README beside it. Nothing is written to the registry.

    A running Agent picks it up with Refresh List, and a new build of it with Update - or pass
    -Notify to have it told over the control pipe.

    Run it from anywhere; paths are worked out from the script's own location.

.EXAMPLE
    tools\install-plugin.ps1 -Plugin MagFilter
    Fetch Mag-Plugins if need be, build Mag-Filter, and put it in Decal Compat's folder in
    %LOCALAPPDATA%\ACHost, where the Agent's Decal finds it at its next start or Refresh List.

.EXAMPLE
    tools\install-plugin.ps1 -Plugin MagFilter -Notify
    ...and tell the running Agent to load it (or reload it, if it was already there).
#>
[CmdletBinding()]
param(
    # The plugin to install: the one this repository builds, Mag-Filter.
    [ValidateSet("MagFilter")]
    [string] $Plugin = "MagFilter",

    # Install into this folder of Decal plugins instead: the one Decal Compat reads (its setting
    # DecalCompat:Folder, where one is given).
    [string] $Destination,

    # The host's data folder, whose plugins\DecalCompat\Decal Plugins it goes in.
    # %LOCALAPPDATA%\ACHost unless given - the Agent's --data, where it is started with one.
    [string] $Data,

    # Where third_party\Mag-Filter\fetch.ps1 fetches Mag-Plugins from, if it must - GitHub unless
    # given; a clone on this machine that has the commit works too.
    [string] $MagPluginsFrom,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",

    # Tell the running host over its control pipe: rescan, then reload the plugin.
    [switch] $Notify,

    # The control pipe to tell. ACHOST_CONTROL_PIPE, or the default, unless given.
    [string] $ControlPipe
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot

# The plugin: its project, the folder it is installed as, and the name Decal Compat lists it
# under - a Decal plugin, which Decal Compat loads from its own folder rather than the host from
# its plugins.
$entry = @{ Project = "third_party\Mag-Filter\MagFilter.csproj"; Folder = "MagFilter"; Listed = "Mag-Filter" }
$project = Join-Path $repo $entry.Project

# --- Where it goes -----------------------------------------------------------
if (-not $Destination) {
    # Decal Compat's own folder of Decal plugins, in the host's data folder: a plugin there is
    # found with no registry entry (src\Decal.Compat\DecalPluginCatalog.cs).
    if (-not $Data) {
        $Data = Join-Path $env:LOCALAPPDATA "ACHost"
    }

    $Destination = Join-Path $Data "plugins\DecalCompat\Decal Plugins"
}

$Destination = [System.IO.Path]::GetFullPath($Destination)
$target = Join-Path $Destination $entry.Folder

# Installing into an Agent under %LOCALAPPDATA% from inside a packaged app would land in that app's
# private copy of the folder, where the Agent never looks.
if ([System.IO.Path]::GetFullPath($target).StartsWith([System.IO.Path]::GetFullPath($env:LOCALAPPDATA), [StringComparison]::OrdinalIgnoreCase)) {
    . (Join-Path $PSScriptRoot "PackageCheck.ps1")
    Assert-NotInPackage
}

# What the host beside the plugins folder supplies itself; a plugin must not carry its own copy.
$hostFolder = Split-Path -Parent $Destination
$supplied = @{}
if (Test-Path $hostFolder) {
    Get-ChildItem -Path $hostFolder -Filter *.dll -File | ForEach-Object { $supplied[$_.Name.ToLowerInvariant()] = $true }
}

# --- Sources -----------------------------------------------------------------
# Mag-Filter's are Mag-nus's, fetched at the commit its project pins rather than kept here.
$fetch = Join-Path $repo "third_party\Mag-Filter\fetch.ps1"
try {
    if ($MagPluginsFrom) {
        & $fetch -From $MagPluginsFrom
    }
    else {
        & $fetch
    }
}
catch {
    Write-Warning ("Mag-Plugins could not be fetched, so nothing was installed: {0}" -f $_.Exception.Message)
    exit 1
}

# --- Build -------------------------------------------------------------------
# Built to its own output folder, not with -o: that would put the whole reference graph, the
# host's contract included, in one folder with the plugin.
Write-Host ("Building {0} ({1})." -f $Plugin, $Configuration)
dotnet build $project -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Warning "The build failed; nothing was installed."
    exit $LASTEXITCODE
}

$output = (dotnet msbuild $project -nologo "-getProperty:TargetDir" "-p:Configuration=$Configuration").Trim()
if (-not $output -or -not (Test-Path $output)) {
    Write-Warning ("The build said its output is in '{0}', and it is not." -f $output)
    exit 1
}

# --- Install -----------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $target | Out-Null

$copied = 0
$skipped = @()

# A Decal plugin is its own assembly and symbols alone: Decal.Adapter, Virindi View Service and
# the rest are the stand-ins Decal Compat gives every Decal plugin, Windows Forms the runtime's.
$built = $output.TrimEnd('\')
$files = @(Get-ChildItem -Path $output -File -Recurse -Include *.dll, *.pdb, *.json, *.xml)
$files = @($files | Where-Object { $_.Directory.FullName -eq $built -and $_.BaseName -eq $Plugin -and $_.Extension -in ".dll", ".pdb" })

foreach ($file in $files) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    if ($supplied.ContainsKey(($stem + ".dll").ToLowerInvariant())) {
        $skipped += $file.Name
        continue
    }

    $relative = $file.FullName.Substring($built.Length + 1)
    $destinationFile = Join-Path $target $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationFile) | Out-Null
    Copy-Item -Path $file.FullName -Destination $destinationFile -Force
    $copied++
}

# Beside it, its licence - Mag-Plugins' own, the GNU LGPL 2.1 - and what it is, where its
# sources are and what it does here.
Copy-Item -Path (Join-Path $repo "third_party\Mag-Filter\upstream\license.md") -Destination (Join-Path $target "LICENSE.md") -Force
Copy-Item -Path (Join-Path $repo "third_party\Mag-Filter\README.md") -Destination (Join-Path $target "README.md") -Force
$copied += 2

Write-Host ("Installed {0}: {1} files into {2}." -f $Plugin, $copied, $target)
if ($skipped.Count -gt 0) {
    Write-Host ("Left out, because the host supplies them: {0}" -f ($skipped -join ", "))
}

# --- Tell the host -----------------------------------------------------------
function Send-HostCommand([string] $pipeName, [string] $command) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(3000)
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($command + [Environment]::NewLine)
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        $reader = New-Object System.IO.StreamReader($pipe)
        return $reader.ReadToEnd()
    }
    finally {
        $pipe.Dispose()
    }
}

if ($Notify) {
    if (-not $ControlPipe) {
        $ControlPipe = $env:ACHOST_CONTROL_PIPE
        if (-not $ControlPipe) { $ControlPipe = "achost-control" }
    }

    try {
        # A rescan finds it if it is new; a reload picks up the new build if it was running - a
        # Decal plugin by the name Decal Compat lists it under.
        Send-HostCommand $ControlPipe "rescan" | Out-Null
        Write-Host (Send-HostCommand $ControlPipe ("reload " + $entry.Listed))
    }
    catch [System.TimeoutException] {
        Write-Warning ("No host answered on the pipe '{0}'. Start Decal Agent, or press Refresh List (a new plugin) or Update (a new build) in it." -f $ControlPipe)
    }
}
else {
    Write-Host "In a running Decal Agent: Refresh List to load a new plugin, Update to load a new build of one."
}
