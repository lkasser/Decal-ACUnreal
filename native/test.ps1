<#
.SYNOPSIS
    Builds and runs the overlay's headless self-test.

.DESCRIPTION
    Compiles overlay_ui.cpp against real ImGui with asserts live and the test-engine
    hooks on, then drives it through clicks, drags, typing and hostile snapshots and
    checks the commands that come out. Nothing here touches a device or the game.

    Separate from build.ps1 because the flags differ in the ways that matter: the DLL is
    built with NDEBUG, which compiles every IM_ASSERT away, and this is built without it
    precisely so they fire.

.EXAMPLE
    native\test.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$native = $PSScriptRoot
$third = Join-Path $native "third_party"
$out = Join-Path $native "build\tests"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio Build Tools with the C++ workload."
}

$install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $install) {
    $install = & $vswhere -latest -products * -property installationPath
}

$vcvars = Join-Path $install "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) {
    throw "No vcvars64.bat under $install. The x64 C++ toolset is not installed."
}

$sources = @(
    "tests\ui_selftest.cpp",
    "ACUnrealOverlay\overlay_ui.cpp",
    "ACUnrealOverlay\decal_view.cpp",
    "ACUnrealOverlay\gdi_font.cpp",
    "ACUnrealOverlay\textures.cpp",
    "ACUnrealOverlay\input.cpp",
    "third_party\imgui\imgui.cpp",
    "third_party\imgui\imgui_draw.cpp",
    "third_party\imgui\imgui_tables.cpp",
    "third_party\imgui\imgui_widgets.cpp"
)

$missing = @($sources | Where-Object { -not (Test-Path (Join-Path $native $_)) })
if ($missing.Count -gt 0) {
    Write-Warning "Not built. These sources are missing:"
    $missing | ForEach-Object { Write-Warning "  $_" }
    Write-Warning "Run native\fetch-deps.ps1 if the third_party ones are absent."
    exit 1
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

$exe = Join-Path $out "ui_selftest.exe"
$list = ($sources | ForEach-Object { "`"$native\$_`"" }) -join " "

# No /DNDEBUG, on purpose: it is the whole point of this build.
$script = @"
call "$vcvars" >nul || exit /b 1
cd /d "$out" || exit /b 1
cl /nologo /std:c++20 /EHsc /W4 /MT /DIMGUI_ENABLE_TEST_ENGINE /I"$third\imgui" /I"$native\ACUnrealOverlay" $list /Fe:"$exe" /link user32.lib imm32.lib gdi32.lib || exit /b 1
"@

$batch = Join-Path $env:TEMP ("overlay-test-" + [guid]::NewGuid().ToString("N") + ".bat")
Set-Content -Path $batch -Value $script -Encoding ASCII

try {
    & cmd /c "`"$batch`"" | Where-Object { $_ -match "error|warning" }
    $code = $LASTEXITCODE
}
finally {
    Remove-Item $batch -ErrorAction SilentlyContinue
}

if ($code -ne 0) {
    Write-Warning "The self-test did not build (exit code $code)."
    exit $code
}

& $exe
exit $LASTEXITCODE
