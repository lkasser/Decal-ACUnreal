<#
.SYNOPSIS
    Builds the installer a player runs: dist\DecalAgentSetup.exe.

.DESCRIPTION
    The setup is a .NET program that carries what it installs as a zip embedded in itself, and
    is published as one self-contained file, so a player needs nothing installed first. This
    builds what goes in the zip, zips it, and builds the setup around it:

      Decal Agent    The Agent published self-contained for win-x64 - the .NET runtime and
                     Windows Forms beside DecalAgent.exe - with plugins\Decal.Compat (its native
                     sqlite3.dll and Mono.Cecil's assemblies included, every assembly its
                     deps.json names checked for), acinject.exe, ACUnrealOverlay.dll from
                     native\build.ps1, and Uninstall.exe, which runs on the same runtime.

    Plugins have setups of their own: the Virindi Tank repository builds VirindiTankSetup.exe,
    which installs into the Agent this one installs.

    The overlay is built into a folder of its own under %TEMP%, never native\build: the DLL
    there may be loaded in a running game, and so locked. Everything else is staged under
    %TEMP% too, and deleted afterwards unless -KeepStage is given.

    Nothing here runs the Agent or the setups.

    See docs\installers.md for what the setups do with it all.

.EXAMPLE
    tools\build-installers.ps1
    Builds it into dist\.

.EXAMPLE
    tools\build-installers.ps1 -OverlayDll C:\Builds\ACUnrealOverlay.dll
    Uses an overlay DLL already built, on a machine without the C++ toolchain.

.EXAMPLE
    tools\build-installers.ps1 -KeepStage
    ...and keeps the staged folders and zips, to look at what went in.
#>
[CmdletBinding()]
param(
    # Where the setup goes. dist\ in the repository unless given.
    [string] $OutputDirectory,

    # An ACUnrealOverlay.dll to ship instead of building one with native\build.ps1.
    [string] $OverlayDll,

    # Keep the staging folder under %TEMP% afterwards, and say where it is.
    [switch] $KeepStage
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo "dist"
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$runtime = "win-x64"

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("decal-installers-" + [guid]::NewGuid().ToString("N").Substring(0, 12))
$agent = Join-Path $stage "DecalAgent"
$agentZip = Join-Path $stage "DecalAgent.zip"
New-Item -ItemType Directory -Force -Path $agent, $OutputDirectory | Out-Null

function Invoke-Dotnet([string] $what, [string[]] $arguments) {
    Write-Host $what
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed (dotnet exited with $LASTEXITCODE)."
    }
}

function Get-ProjectProperty([string] $project, [string] $property) {
    $value = (& dotnet msbuild $project -nologo "-getProperty:$property" "-p:Configuration=Release")
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read $property from $project."
    }

    return ($value | Out-String).Trim()
}

try {
    # --- Decal Agent -----------------------------------------------------------
    # Self-contained: a player installs no runtime, and the plugins it loads into their own
    # load contexts bind to the framework it carries exactly as to a shared one.
    $agentProject = Join-Path $repo "src\Decal.Agent\Decal.Agent.csproj"
    Invoke-Dotnet "Publishing Decal Agent, self-contained for $runtime." @(
        "publish", $agentProject, "-c", "Release", "-r", $runtime, "--self-contained",
        "-p:SatelliteResourceLanguages=en", "-o", $agent, "--nologo", "-v", "q")

    # Into the same folder, for the same runtime: it adds itself and nothing else.
    Invoke-Dotnet "Publishing the uninstaller beside it." @(
        "publish", (Join-Path $repo "src\Setup.Uninstall\Setup.Uninstall.csproj"), "-c", "Release", "-r", $runtime, "--self-contained",
        "-p:SatelliteResourceLanguages=en", "-o", $agent, "--nologo", "-v", "q")

    # --- The overlay -------------------------------------------------------------
    if ($OverlayDll) {
        if (-not (Test-Path $OverlayDll)) {
            throw "There is no overlay DLL at $OverlayDll."
        }

        Write-Host "Using the overlay DLL $OverlayDll."
        Copy-Item -Path $OverlayDll -Destination (Join-Path $agent "ACUnrealOverlay.dll") -Force
    }
    else {
        $overlayOut = Join-Path $stage "overlay"
        Write-Host "Building the overlay DLL into $overlayOut."

        # In a PowerShell of its own, and not stopped by what the compiler's environment says on
        # stderr: Windows PowerShell turns a native program's stderr into a terminating error
        # when this script's output is redirected, and vcvars64 always says something there.
        $shell = (Get-Process -Id $PID).Path
        $before = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            & $shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo "native\build.ps1") -Configuration Release -OutputDirectory $overlayOut
            $built = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $before
        }

        if ($built -ne 0 -or -not (Test-Path (Join-Path $overlayOut "ACUnrealOverlay.dll"))) {
            throw "The overlay DLL did not build. Run native\fetch-deps.ps1 if its third-party sources are missing, or pass -OverlayDll."
        }

        Copy-Item -Path (Join-Path $overlayOut "ACUnrealOverlay.dll") -Destination (Join-Path $agent "ACUnrealOverlay.dll") -Force
    }

    # --- What a working Agent folder must hold -------------------------------------
    $required = @(
        "DecalAgent.exe",
        "DecalAgent.dll",
        "acinject.exe",
        "ACUnrealOverlay.dll",
        "Uninstall.exe",
        "hostfxr.dll",
        "System.Windows.Forms.dll",
        "plugins\Decal.Compat\Decal.Compat.dll",
        "plugins\Decal.Compat\Decal.Adapter.dll",
        "plugins\Decal.Compat\VirindiViewService.dll",
        "plugins\Decal.Compat\Mono.Cecil.dll",
        "plugins\Decal.Compat\Mono.Cecil.Rocks.dll",
        "plugins\Decal.Compat\native\sqlite3.dll"
    )

    # And every assembly Decal.Compat runs on, as its deps.json lists them: beside it, or - for the
    # host's own AC.* assemblies, which a plugin is always given the host's copy of - beside
    # DecalAgent.exe. One left out fails only when first used, as Mono.Cecil.Rocks once did.
    $compat = Join-Path $agent "plugins\Decal.Compat"
    $deps = Get-Content -Raw -Path (Join-Path $compat "Decal.Compat.deps.json") | ConvertFrom-Json
    foreach ($target in $deps.targets.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            if (-not $library.Value.runtime) {
                continue
            }

            foreach ($asset in $library.Value.runtime.PSObject.Properties.Name) {
                $file = Split-Path -Leaf $asset
                $beside = Join-Path "plugins\Decal.Compat" $file
                if ($file -like "AC.*") {
                    $beside = $file
                }

                if ($required -notcontains $beside) {
                    $required += $beside
                }
            }
        }
    }

    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $agent $_)) })
    if ($missing.Count -gt 0) {
        throw ("The published Agent is missing: " + ($missing -join ", "))
    }

    # No plugin but Decal's own: other plugins, Virindi Tank among them, have setups of their own.
    $others = @(Get-ChildItem -Path (Join-Path $agent "plugins") -Directory | Where-Object { $_.Name -ne "Decal.Compat" })
    if ($others.Count -gt 0) {
        throw ("The published Agent has plugin folders other than Decal.Compat: " + (($others | ForEach-Object { $_.Name }) -join ", "))
    }

    # --- The payload -----------------------------------------------------------------
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Write-Host "Zipping the payload."
    [System.IO.Compression.ZipFile]::CreateFromDirectory($agent, $agentZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    # --- The setup -------------------------------------------------------------------
    # It carries the Agent's version, which it records in Apps.
    $agentVersion = Get-ProjectProperty $agentProject "Version"

    $setups = @(
        @{ Project = "src\Decal.Setup\Decal.Setup.csproj"; Exe = "DecalAgentSetup.exe"; Payload = $agentZip; Version = $agentVersion; Out = "setup-decal" }
    )

    foreach ($setup in $setups) {
        $out = Join-Path $stage $setup.Out
        Invoke-Dotnet ("Publishing {0} ({1})." -f $setup.Exe, $setup.Version) @(
            "publish", (Join-Path $repo $setup.Project), "-c", "Release", "-r", $runtime,
            ("-p:SetupPayload=" + $setup.Payload), ("-p:Version=" + $setup.Version), "-o", $out, "--nologo", "-v", "q")

        Copy-Item -Path (Join-Path $out $setup.Exe) -Destination (Join-Path $OutputDirectory $setup.Exe) -Force
    }

    # --- Done --------------------------------------------------------------------------
    Write-Host ""
    $agentFiles = @(Get-ChildItem -Path $agent -File -Recurse)
    Write-Host ("Decal Agent: {0} files, {1:N1} MB installed, {2:N1} MB zipped." -f $agentFiles.Count, (($agentFiles | Measure-Object Length -Sum).Sum / 1MB), ((Get-Item $agentZip).Length / 1MB))
    foreach ($setup in $setups) {
        $built = Get-Item (Join-Path $OutputDirectory $setup.Exe)
        Write-Host ("{0,-22} {1:N1} MB  {2}" -f $setup.Exe, ($built.Length / 1MB), $built.FullName)
    }
}
finally {
    if ($KeepStage) {
        Write-Host "Staged in $stage"
    }
    elseif (Test-Path $stage) {
        Remove-Item -Path $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
