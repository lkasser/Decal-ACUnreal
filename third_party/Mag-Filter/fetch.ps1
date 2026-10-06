<#
.SYNOPSIS
    Fetches Mag-Plugins, whose Mag-Filter MagFilter.csproj builds, at the commit it pins.

.DESCRIPTION
    Mag-Filter's sources are not committed to this repository: they are Mag-nus's, unmodified,
    and fetched here - into third_party\Mag-Filter\upstream, which git ignores - at the one commit
    MagFilter.csproj names (MagPluginsCommit), so a later upstream change cannot alter what is
    built without being read first. Only that commit is fetched, with no history.

    Run it from anywhere; paths are worked out from the script's own location.

.EXAMPLE
    third_party\Mag-Filter\fetch.ps1
    Fetches https://github.com/Mag-nus/Mag-Plugins at the pinned commit, unless it is already there.

.EXAMPLE
    third_party\Mag-Filter\fetch.ps1 -From C:\src\Mag-Plugins
    Takes the commit from a clone already on this machine instead of from GitHub.
#>
[CmdletBinding()]
param(
    # Where to fetch from: GitHub unless given - a URL, or a clone on this machine that has the commit.
    [string] $From = "https://github.com/Mag-nus/Mag-Plugins.git",

    # Where to put it. upstream\ beside this script unless given, which is where MagFilter.csproj looks.
    [string] $Destination,

    # Fetch again even if it is already there at the commit.
    [switch] $Force
)

$ErrorActionPreference = "Stop"

if (-not $Destination) {
    $Destination = Join-Path $PSScriptRoot "upstream"
}

$Destination = [System.IO.Path]::GetFullPath($Destination)

# The commit, from the one place it is written: the project that builds it.
[xml] $project = Get-Content -Raw -Path (Join-Path $PSScriptRoot "MagFilter.csproj")
$commit = ($project.Project.PropertyGroup | ForEach-Object { $_.MagPluginsCommit } | Where-Object { $_ } | Select-Object -First 1).Trim()
if (-not $commit) {
    throw "MagFilter.csproj names no MagPluginsCommit."
}

if ((Test-Path (Join-Path $Destination ".git")) -and -not $Force) {
    $have = (& git -C $Destination rev-parse HEAD 2>$null)
    if ($have -eq $commit) {
        Write-Host ("Mag-Plugins is already at {0} in {1}." -f $commit.Substring(0, 12), $Destination)
        exit 0
    }

    Write-Host ("Mag-Plugins in {0} is at {1}, wanted {2} - fetching it again." -f $Destination, $have, $commit.Substring(0, 12))
}

if (Test-Path $Destination) {
    Remove-Item -Path $Destination -Recurse -Force
}

Write-Host ("Fetching Mag-Plugins {0} from {1}." -f $commit.Substring(0, 12), $From)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# The one commit, and none of the history before it: GitHub, and a local clone that has the
# commit as a branch's tip, both give a commit asked for by its id.
& git -C $Destination init --quiet
if ($LASTEXITCODE -ne 0) { throw "git init failed in $Destination." }

& git -C $Destination fetch --quiet --depth 1 $From $commit
if ($LASTEXITCODE -ne 0) {
    # A server that will not give a commit by its id: the whole history, then the commit.
    Write-Host "That source would not give the commit by its id; fetching its history instead."
    & git -C $Destination fetch --quiet $From
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch $From." }
}

& git -C $Destination -c advice.detachedHead=false checkout --quiet $commit
if ($LASTEXITCODE -ne 0) { throw "Could not check out $commit of Mag-Plugins." }

Write-Host ("Mag-Plugins {0} is in {1}." -f $commit.Substring(0, 12), $Destination)
Write-Host "Its licence is its license.md: the GNU Lesser General Public License 2.1. Installing Mag-Filter puts a copy beside it."
