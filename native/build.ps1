<#
.SYNOPSIS
    Builds the injected overlay DLL.

.DESCRIPTION
    Deliberately a script rather than a CMake project. The whole native side is one
    DLL with three vendored dependencies and no configuration to speak of, and a
    script that prints the exact compiler command is easier to debug than a generator
    that hides it. If this grows a second target, reconsider.

    MSVC is located through vswhere rather than a hardcoded path, because the version
    number in that path changes with every Visual Studio update.

.EXAMPLE
    native\build.ps1
    Builds native\build\ACUnrealOverlay.dll.

.EXAMPLE
    native\build.ps1 -Clean -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [switch] $Clean,

    # Somewhere other than native\build - to check a change compiles while the game still
    # has the built DLL loaded, and so locked.
    [string] $OutputDirectory
)

$ErrorActionPreference = "Stop"

$native = $PSScriptRoot
$third = Join-Path $native "third_party"
$out = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $native "build" }
$obj = Join-Path $out "obj"

# --- Locate the toolchain ----------------------------------------------------
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio Build Tools with the C++ workload."
}

$install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $install) {
    # Fall back to any install: a Build Tools install can report its components
    # differently, and failing here when a usable cl.exe exists would be unhelpful.
    $install = & $vswhere -latest -products * -property installationPath
}

if (-not $install) {
    throw "No Visual Studio installation found. The C++ workload is required."
}

$vcvars = Join-Path $install "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) {
    throw "Found Visual Studio at $install but no vcvars64.bat. The x64 C++ toolset is not installed."
}

Write-Host "Toolchain: $install"

# --- Sources -----------------------------------------------------------------
# Listed rather than globbed, so a half-written file cannot silently join the build
# and so a missing one is named plainly.
$ourSources = @(
    "ACUnrealOverlay\dllmain.cpp",
    "ACUnrealOverlay\hooks.cpp",
    "ACUnrealOverlay\log.cpp",
    "ACUnrealOverlay\overlay_ui.cpp",
    "ACUnrealOverlay\overlay_ipc.cpp",
    "ACUnrealOverlay\decal_view.cpp",
    "ACUnrealOverlay\gdi_font.cpp",
    "ACUnrealOverlay\textures.cpp",
    "ACUnrealOverlay\input.cpp"
)

$imguiSources = @(
    "third_party\imgui\imgui.cpp",
    "third_party\imgui\imgui_draw.cpp",
    "third_party\imgui\imgui_tables.cpp",
    "third_party\imgui\imgui_widgets.cpp",
    "third_party\imgui\backends\imgui_impl_dx12.cpp",
    "third_party\imgui\backends\imgui_impl_win32.cpp"
)

# MinHook is C, not C++, and is compiled as such below.
$minhookSources = @(
    "third_party\minhook\src\buffer.c",
    "third_party\minhook\src\hook.c",
    "third_party\minhook\src\trampoline.c",
    "third_party\minhook\src\hde\hde64.c"
)

$missing = @()
foreach ($rel in ($ourSources + $imguiSources + $minhookSources)) {
    if (-not (Test-Path (Join-Path $native $rel))) { $missing += $rel }
}

if ($missing.Count -gt 0) {
    Write-Warning "Not built. These sources are missing:"
    $missing | ForEach-Object { Write-Warning "  $_" }
    Write-Warning "Run native\fetch-deps.ps1 if the third_party ones are absent."
    exit 1
}

# --- Directories -------------------------------------------------------------
if ($Clean -and (Test-Path $out)) {
    Remove-Item $out -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $obj | Out-Null

# --- Flags -------------------------------------------------------------------
$includes = @(
    "/I`"$third\imgui`"",
    "/I`"$third\imgui\backends`"",
    "/I`"$third\minhook\include`"",
    "/I`"$third\nlohmann\single_include`"",
    "/I`"$native\ACUnrealOverlay`""
) -join " "

# /MT so the DLL carries its own runtime: it is loaded into a process whose runtime
# version is not ours to assume, and a missing redistributable would show up as an
# injection that silently does nothing.
$common = "/nologo /std:c++20 /EHsc /W4 /MT /DWIN32 /D_WINDOWS /DUNICODE /D_UNICODE"

if ($Configuration -eq "Release") {
    $common += " /O2 /DNDEBUG /GL"
    $linkExtra = "/LTCG /OPT:REF /OPT:ICF"
}
else {
    $common += " /Od /Zi /DDEBUG /Fd`"$obj\overlay.pdb`""
    $linkExtra = "/DEBUG"
}

$libs = "d3d12.lib dxgi.lib d3dcompiler.lib user32.lib gdi32.lib dwmapi.lib"

# --- Compile and link --------------------------------------------------------
# One cmd invocation so the vcvars environment survives across the compiler calls.
$cppList = (($ourSources + $imguiSources) | ForEach-Object { "`"$native\$_`"" }) -join " "
$cList = ($minhookSources | ForEach-Object { "`"$native\$_`"" }) -join " "

$dll = Join-Path $out "ACUnrealOverlay.dll"

$script = @"
call "$vcvars" >nul || exit /b 1
cd /d "$obj" || exit /b 1
cl $common $includes /c $cppList || exit /b 1
cl /nologo /W3 /MT /O2 /c $cList || exit /b 1
link /nologo /DLL $linkExtra /OUT:"$dll" *.obj $libs || exit /b 1
"@

$batch = Join-Path $env:TEMP ("overlay-build-" + [guid]::NewGuid().ToString("N") + ".bat")
Set-Content -Path $batch -Value $script -Encoding ASCII

try {
    & cmd /c "`"$batch`""
    $code = $LASTEXITCODE
}
finally {
    Remove-Item $batch -ErrorAction SilentlyContinue
}

if ($code -ne 0) {
    Write-Warning "The build failed with exit code $code."
    exit $code
}

Write-Host ""
Write-Host "Built $dll"
Write-Host ("  {0:N0} bytes, {1}" -f (Get-Item $dll).Length, $Configuration)
Write-Host ""
Write-Host "Inject it with:  dotnet run --project src\AC.Injector -- --process ACUnreal --dll `"$dll`""
