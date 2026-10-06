<#
.SYNOPSIS
    Builds and runs the overlay's render test: its real D3D12 path through minimizing,
    restoring and resizing.

.DESCRIPTION
    Compiles hooks.cpp, the ImGui backends and the drawing code into tests\render_test.cpp,
    which hooks its own swap chain as the DLL hooks the game's, then minimizes, restores and
    resizes it as Unreal does and counts the pixels the overlay drew after each step. Runs on
    the default adapter, then on WARP.

    The build names its own pipe (OVERLAY_TEST_PIPE), so it never reaches a Decal Agent that
    is running for the game, and its window is off every screen and never activated.

    Where AC:Unreal is installed, its Agility SDK (D3D12Core.dll and d3d12SDKLayers.dll) is
    copied beside the test, which turns the D3D12 debug layer on; without it the test runs
    without the layer and says so.

.EXAMPLE
    native\test-render.ps1 -OutputDirectory $env:TEMP\overlay-render-test
#>
[CmdletBinding()]
param(
    # Somewhere other than native\build\render-test, to keep out of a folder the game may hold.
    [string] $OutputDirectory,

    # Where AC:Unreal keeps its Agility SDK; only read from.
    [string] $AgilitySdk = (Join-Path $env:LOCALAPPDATA "Programs\ACUnreal\ACUnreal\Binaries\Win64\D3D12\x64")
)

$ErrorActionPreference = "Stop"

$native = $PSScriptRoot
$third = Join-Path $native "third_party"
$out = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $native "build\render-test" }

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
    "tests\render_test.cpp",
    "ACUnrealOverlay\hooks.cpp",
    "ACUnrealOverlay\log.cpp",
    "ACUnrealOverlay\overlay_ui.cpp",
    "ACUnrealOverlay\overlay_ipc.cpp",
    "ACUnrealOverlay\decal_view.cpp",
    "ACUnrealOverlay\gdi_font.cpp",
    "ACUnrealOverlay\textures.cpp",
    "ACUnrealOverlay\input.cpp",
    "third_party\imgui\imgui.cpp",
    "third_party\imgui\imgui_draw.cpp",
    "third_party\imgui\imgui_tables.cpp",
    "third_party\imgui\imgui_widgets.cpp",
    "third_party\imgui\backends\imgui_impl_dx12.cpp",
    "third_party\imgui\backends\imgui_impl_win32.cpp"
)

$minhook = @(
    "third_party\minhook\src\buffer.c",
    "third_party\minhook\src\hook.c",
    "third_party\minhook\src\trampoline.c",
    "third_party\minhook\src\hde\hde64.c"
)

$missing = @(($sources + $minhook) | Where-Object { -not (Test-Path (Join-Path $native $_)) })
if ($missing.Count -gt 0) {
    Write-Warning "Not built. These sources are missing:"
    $missing | ForEach-Object { Write-Warning "  $_" }
    Write-Warning "Run native\fetch-deps.ps1 if the third_party ones are absent."
    exit 1
}

$obj = Join-Path $out "obj"
New-Item -ItemType Directory -Force -Path $obj | Out-Null

$exe = Join-Path $out "render_test.exe"
$cppList = ($sources | ForEach-Object { "`"$native\$_`"" }) -join " "
$cList = ($minhook | ForEach-Object { "`"$native\$_`"" }) -join " "
$includes = "/I`"$third\imgui`" /I`"$third\imgui\backends`" /I`"$third\minhook\include`" /I`"$third\nlohmann\single_include`" /I`"$native\ACUnrealOverlay`""

# As the DLL is built, but with asserts live, and with the test's own pipe.
$script = @"
call "$vcvars" >nul || exit /b 1
cd /d "$obj" || exit /b 1
cl /nologo /std:c++20 /EHsc /W4 /MT /O2 /DWIN32 /D_WINDOWS /DUNICODE /D_UNICODE /DOVERLAY_TEST_PIPE $includes /c $cppList || exit /b 1
cl /nologo /W3 /MT /O2 /c $cList || exit /b 1
link /nologo /OUT:"$exe" *.obj d3d12.lib dxgi.lib d3dcompiler.lib user32.lib gdi32.lib dwmapi.lib imm32.lib || exit /b 1
"@

$batch = Join-Path $env:TEMP ("overlay-render-test-" + [guid]::NewGuid().ToString("N") + ".bat")
Set-Content -Path $batch -Value $script -Encoding ASCII

try {
    & cmd /c "`"$batch`"" | Where-Object { $_ -match "error|warning" }
    $code = $LASTEXITCODE
}
finally {
    Remove-Item $batch -ErrorAction SilentlyContinue
}

if ($code -ne 0) {
    Write-Warning "The render test did not build (exit code $code)."
    exit $code
}

# The game's Agility SDK, for the debug layer: copied, never touched where it is.
$sdk = Join-Path $out "D3D12"
if ((Test-Path (Join-Path $AgilitySdk "D3D12Core.dll")) -and (Test-Path (Join-Path $AgilitySdk "d3d12SDKLayers.dll"))) {
    New-Item -ItemType Directory -Force -Path $sdk | Out-Null
    Copy-Item (Join-Path $AgilitySdk "D3D12Core.dll"), (Join-Path $AgilitySdk "d3d12SDKLayers.dll") $sdk -Force
}

$failed = 0
foreach ($adapter in @(@(), @("--warp"))) {
    Push-Location $out
    try {
        & $exe @adapter
        if ($LASTEXITCODE -ne 0) { $failed = $LASTEXITCODE }
    }
    finally {
        Pop-Location
    }
    Write-Host ""
}

exit $failed
