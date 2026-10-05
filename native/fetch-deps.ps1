<#
.SYNOPSIS
    Fetches the native dependencies at the exact commits this code was written against.

.DESCRIPTION
    Pinned, not floating. Dear ImGui in particular changes its backend
    initialisation signatures between releases - the DX12 backend has been rewritten
    more than once - so "latest" would break the overlay at some unpredictable future
    date with a compile error that looks like our bug.

    The dependencies are not committed to this repository: they are large, alive, and
    unmodified. What is committed is this script and the commits it pins.

.EXAMPLE
    native\fetch-deps.ps1
#>
[CmdletBinding()]
param(
    # Re-clone even if a directory is already there.
    [switch] $Force
)

$ErrorActionPreference = "Stop"

$third = Join-Path $PSScriptRoot "third_party"
New-Item -ItemType Directory -Force -Path $third | Out-Null

# name, repository, commit, what it is for
$deps = @(
    @{
        Name = "imgui"
        Url = "https://github.com/ocornut/imgui.git"
        Commit = "580c00c316285de8104a8fa0701265557fb78a62"
        Why = "the overlay's widgets, and its D3D12 and Win32 backends"
    },
    @{
        Name = "minhook"
        Url = "https://github.com/TsudaKageyu/minhook.git"
        Commit = "8af6b4acae5a9388fd742b56fa79ece89d96f823"
        Why = "hooking the client's Present and ExecuteCommandLists"
    },
    @{
        Name = "nlohmann"
        Url = "https://github.com/nlohmann/json.git"
        Commit = "9cca280a4d0ccf0c08f47a99aa71d1b0e52f8d03"
        Why = "parsing the state the .NET host publishes"
    }
)

foreach ($dep in $deps) {
    $path = Join-Path $third $dep.Name

    if ((Test-Path $path) -and -not $Force) {
        $have = (& git -C $path rev-parse HEAD 2>$null)
        if ($have -eq $dep.Commit) {
            Write-Host ("{0,-10} already at {1}" -f $dep.Name, $dep.Commit.Substring(0, 12))
            continue
        }

        Write-Host ("{0,-10} is at {1}, wanted {2} - refetching" -f $dep.Name, $have.Substring(0, 12), $dep.Commit.Substring(0, 12))
        Remove-Item $path -Recurse -Force
    }
    elseif (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }

    Write-Host ("{0,-10} fetching  ({1})" -f $dep.Name, $dep.Why)

    # A full clone, because a shallow one cannot be checked out at an arbitrary commit.
    & git clone --quiet $dep.Url $path
    if ($LASTEXITCODE -ne 0) { throw "Could not clone $($dep.Url)." }

    & git -C $path checkout --quiet $dep.Commit
    if ($LASTEXITCODE -ne 0) { throw "Could not check out $($dep.Commit) of $($dep.Name)." }
}

Write-Host ""
Write-Host "Licences, all permissive and all requiring attribution:"
Write-Host "  Dear ImGui  MIT       third_party\imgui\LICENSE.txt"
Write-Host "  MinHook     BSD 2     third_party\minhook\LICENSE.txt"
Write-Host "  nlohmann    MIT       third_party\nlohmann\LICENSE.MIT"
Write-Host ""
Write-Host "Now run native\build.ps1"
