<#
.SYNOPSIS
    Stops a tools\ script whose writes to %LOCALAPPDATA% are redirected into a packaged app's
    private copy.

.DESCRIPTION
    Windows gives a packaged (MSIX) app - and, depending on how it starts them, the programs it
    starts - a private copy of %LOCALAPPDATA%: what such a program creates there lands in
    %LOCALAPPDATA%\Packages\<app>\LocalCache\Local instead. A shell inside the Claude desktop app
    can be in that state without any package identity of its own. A Decal Agent started from such
    a shell keeps its settings, its plugins' settings and the session it hands over where an Agent
    started normally never looks, and a plugin installed from one is invisible to the running Agent.

    So this tests what matters, the redirection itself: it writes a small file to %LOCALAPPDATA%
    and looks for it under any package's LocalCache. Dot-source this and call Assert-NotInPackage
    before starting the Agent or writing its files.
#>

function Test-InPackage {
    $name = "decal-tools-probe-" + [Guid]::NewGuid().ToString("N") + ".tmp"
    $probe = Join-Path $env:LOCALAPPDATA $name
    try {
        Set-Content -LiteralPath $probe -Value "probe" -Encoding ascii
        $redirected = @(Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA "Packages\*\LocalCache\Local\$name") -ErrorAction SilentlyContinue)
        return $redirected.Count -gt 0
    }
    catch {
        return $false
    }
    finally {
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    }
}

function Assert-NotInPackage {
    if (Test-InPackage) {
        throw ("This PowerShell's writes to %LOCALAPPDATA% are redirected into a packaged app's private copy " +
            "(it is running inside an app such as the Claude desktop app): a Decal Agent started or updated " +
            "from here would keep its settings and its session handover where a normally started Agent " +
            "cannot see them. Run this from your own PowerShell window (Start menu, or Windows Terminal) instead.")
    }
}
