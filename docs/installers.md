# The installer — Decal Agent for players

`DecalAgentSetup.exe` installs Decal Agent. It installs for the player alone, needs no
administrator, and needs nothing installed first. It is this repository's own C# - Windows
Forms on .NET 10, like the Agent - with no installer toolchain behind it.

Plugins come with setups of their own that install into the Agent's `plugins` folder. The
Virindi Tank repository (VirindiTank-ACUnreal) builds `VirindiTankSetup.exe` on the same
shared code (`src/Setup.Common`, which it takes from this repository as a git submodule), and
the Agent's `Uninstall.exe` uninstalls it too; both are described here where the Agent is
concerned, and in that repository's `docs/installers.md` otherwise.

```
src/Setup.Common      What the setups share: the payload, install, uninstall, registry,
                      shortcuts, command line, and the wizard (Welcome, folder, Installing, Finish).
src/Decal.Setup       DecalAgentSetup.exe.
src/Setup.Uninstall   Uninstall.exe, which Decal Agent's setup leaves in its folder; it
                      uninstalls the Agent and Virindi Tank.
tests/Setup.Tests     The install logic against temporary folders and a registry in memory.
tools/build-installers.ps1   Builds it into dist\.
```

## Building it

```powershell
tools\build-installers.ps1                       # dist\DecalAgentSetup.exe
tools\build-installers.ps1 -OverlayDll C:\Builds\ACUnrealOverlay.dll   # no C++ toolchain here
tools\build-installers.ps1 -KeepStage            # keep what went into the zip, to look at
```

It needs the .NET 10 SDK, the win-x64 runtime packs (restored from NuGet like any package),
and for the overlay the Visual Studio C++ build tools and `native\third_party`
(`native\fetch-deps.ps1`). What it does:

1. Publishes `src\Decal.Agent` self-contained for win-x64. A publish carries
   `plugins\Decal.Compat` (the `PublishDecalCompat` target in `Decal.Agent.csproj` lays it
   out as the build does: Decal.Compat, the Decal.Adapter, Decal.FileService,
   Decal.Interop.Core and Virindi View Service stand-ins, Mono.Cecil with its Rocks, Pdb and Mdb,
   and `native\sqlite3.dll`) and `acinject.exe`. Every runtime assembly in
   `Decal.Compat.deps.json` is checked for in the published folder - beside it, or beside
   `DecalAgent.exe` for the host's own `AC.*` - since one left out fails only at its first use, as
   Mono.Cecil.Rocks did: Global Inventory's SQLite was never widened.
2. Publishes `src\Setup.Uninstall` into the same folder, for the same runtime: it runs on the
   runtime the Agent carries and adds only itself and `Setup.Common.dll`.
3. Builds `ACUnrealOverlay.dll` with `native\build.ps1 -OutputDirectory` into a folder under
   `%TEMP%` - never `native\build`, whose DLL a running game may hold - and puts it beside
   DecalAgent.exe, where the Agent looks first.
4. Checks the folder has what a working Agent needs, and no plugin but Decal.Compat.
5. Zips the folder and publishes the setup as one self-contained file with its zip embedded
   (`-p:SetupPayload=<zip>`, as the manifest resource `Setup.Payload.zip`), carrying the
   Agent's version (`-p:Version=`).

Everything is staged under `%TEMP%\decal-installers-*` and deleted afterwards. `dist\` is
ignored by git. A setup built without a payload - by a plain solution build - runs, and says it
has nothing to install.

Virindi Tank's setup is built the same way by the Virindi Tank repository's own
`tools\build-installers.ps1`: the plugin in Release, less any file whose name matches a DLL
beside DecalAgent.exe (the host's contract is the Agent's to supply).

### Sizes, and why self-contained

| | installed | setup |
|---|---|---|
| Decal Agent | 258 files, 119 MB | `DecalAgentSetup.exe` 97 MB (its own runtime, about 47 MB, and a 50 MB zip) |
| Virindi Tank (its own repository) | 8 files, 0.9 MB | `VirindiTankSetup.exe` 47 MB (nearly all of it the runtime it runs on) |

The Agent is published **self-contained**: a player installs no .NET runtime, which would
otherwise need an administrator (the .NET Desktop Runtime installs machine-wide), and the
setups install per user precisely so that nothing needs one. Plugins still load as they do in
a framework-dependent build: each plugin's collectible load context resolves the framework
from the default context, which a self-contained app fills from its own folder. This was run,
not assumed: a trial install of both setups, started on pipes and ports of its own, loaded
DecalCompat (with a real Decal plugin, the AutoWireup example, in its Decal plugin folder) and
VirindiTank - which showed Virindi Tank's own window from utank2-i.dll and read its game
database - and `acinject.exe` ran from the same folder on the same runtime.

What a self-contained Windows Forms publish leaves out is WPF (PresentationCore,
PresentationFramework, System.Xaml and their kind), which the shared Desktop Runtime has. No
Decal plugin installed on the development machine refers to any of it - checked by reading
their references - and Decal plugins draw with Decal's views or Windows Forms. If one ever
needs WPF, add `<UseWPF>true</UseWPF>` to `Decal.Agent.csproj` (not as `-p:` on the command
line, which would pass it to every library in the build too); that adds about 43 MB installed.

The price is the runtime twice in Decal Agent's setup - once to run the setup, once in what it
installs - and a Virindi Tank setup that is mostly runtime. A framework-dependent build would
make them 50 MB and under 1 MB, at the cost of the player installing .NET 10 as an
administrator first.

## What Decal Agent's setup does

The wizard: Welcome; the folder, with "Add Decal Agent to the Start menu" (ticked) and "Put a
shortcut on the desktop"; Installing; and Finish, with "Start Decal Agent now" ticked.

- **Where:** `%LOCALAPPDATA%\Programs\Decal Agent` unless the player chooses otherwise; a
  reinstall offers the folder the last install recorded. Refused: a folder Windows keeps other
  things in (the profile, `%LOCALAPPDATA%` itself, the desktop, a drive's root...). A folder
  with other things in it is asked about first.
- **Files:** the published Agent, as above. Their list goes into
  `DecalAgent.install.json` beside them: every file, every shortcut made, and whether the Apps
  entry was written. It is written before the files, listing old and new, so an install that
  stops half way can still be uninstalled.
- **Shortcuts:** `Decal Agent.lnk` in the player's Start menu (`%APPDATA%\Microsoft\Windows\Start
  Menu\Programs`), and on their desktop if asked for.
- **Registry, HKEY_CURRENT_USER only:**
  - `Software\Microsoft\Windows\CurrentVersion\Uninstall\DecalAgent` - the Apps entry:
    DisplayName, DisplayVersion, Publisher, InstallLocation, DisplayIcon, UninstallString
    (`"<folder>\Uninstall.exe"`), QuietUninstallString (`... /S`), InstallDate, EstimatedSize,
    NoModify, NoRepair.
  - `Software\Decal Agent` - `InstallDir` and `Version`: where Virindi Tank's setup finds the
    Agent.

  `IUserRegistry` can only name keys under HKEY_CURRENT_USER, so nothing a setup does can write
  HKEY_LOCAL_MACHINE. Decal's own keys (HKLM\SOFTWARE\Decal) are only ever read, by Virindi
  Tank's setup, the way the plugin itself reads them.
- **Running Agent:** if DecalAgent.exe (or acinject.exe) is running from that folder, its files
  are held open: the wizard asks the player to exit it (Exit, on its icon's menu) and press
  Retry; a silent install exits with 4. An Agent running from anywhere else is no concern of it.
- **Reinstall or upgrade:** the files are written over; files only the old version had are
  deleted, and the folders they leave empty; shortcuts not asked for again are taken away.
  Plugins the player added to `plugins\`, and Virindi Tank, are left alone. The settings are not
  in this folder at all - they are in `%LOCALAPPDATA%\ACHost` (DecalAgent.json, each plugin's
  settings, the logs) - and no install touches them.
- **The game still running:** the game holds the `ACUnrealOverlay.dll` the Agent put into it.
  - A file that already holds the same bytes is not written at all, so an upgrade that leaves the
    overlay alone goes through.
  - One that changes is renamed to `ACUnrealOverlay.dll.setup-old` (Windows lets a loaded DLL be
    renamed, though not overwritten), and the new one is written in its place for the game's next
    start.
  - The renamed file goes on the install's list, so the next install deletes it once the game has
    let it go, and so does the uninstaller.

## Virindi Tank's setup

`VirindiTankSetup.exe` (the Virindi Tank repository) finds the Agent by `/D=`, else by
`InstallDir` under `HKCU\Software\Decal Agent`, installs into `<Agent>\plugins\VirindiTank`,
lists its files in `<Agent>\VirindiTank.install.json`, and writes its own Apps entry,
`...\Uninstall\DecalAgent.VirindiTank`, which the Agent's `Uninstall.exe /Product=VirindiTank`
honours. The code for all of it is `src/Setup.Common` here (`VirindiTankInstall`,
`SetupProduct.VirindiTank`); what the setup does step by step is in that repository's
`docs/installers.md`.

## Uninstalling

From Apps (Windows' Settings), or `Uninstall.exe` in the Agent's folder.

- It asks first, with a box, clear by default: "Also delete my settings and logs" - the whole of
  `%LOCALAPPDATA%\ACHost` for the Agent, `%LOCALAPPDATA%\ACHost\plugins\VirindiTank` for
  Virindi Tank. Without the box (or `/DeleteSettings` with `/S`) the settings stay.
- It deletes what the lists name and nothing else: the files, the shortcuts, the folders they
  leave empty. A plugin the player added is left, with the folders around it, and named at the
  end. Uninstalling the Agent takes Virindi Tank with it, since it lives in the Agent's plugins
  folder and runs nowhere else; uninstalling Virindi Tank leaves the Agent.
- It takes out an Apps entry, and the `InstallDir` record, only if they point at the folder
  being uninstalled, so uninstalling one copy never orphans another.
- If Decal Agent is running from the folder, it asks for it to be exited first (silently: exit
  code 4). Virindi Tank can go from under a running Agent, which keeps running its copy until it
  is next started.
- It runs on the runtime in the folder it is deleting. It loads everything it will need before
  deleting anything; what it cannot delete while it runs - itself and the parts of the runtime it
  has loaded - it hands to a batch file in `%TEMP%`, which waits for them to come free, deletes
  them one by one, removes the folders with a plain `rd` (which refuses a folder with anything
  left in it), and deletes itself. Windows can delete a file at the next restart only for an
  administrator, and these setups never ask to be one.

## Command line

```
DecalAgentSetup.exe / VirindiTankSetup.exe
  /S             Install without asking anything
  /D=<folder>    The folder to install into - for Virindi Tank, the Agent's folder (last on
                 the line; it may contain spaces unquoted)
  /Desktop       With /S, also put a shortcut on the desktop
  /Start         With /S, start Decal Agent when done (a silent install never does otherwise)
  /NoShortcuts   Make no shortcuts
  /NoRegistry    Write nothing to the registry, and read nothing of an earlier install there
  /Log=<file>    Write what was done to this file

Uninstall.exe
  /S  /Product=DecalAgent|VirindiTank  /D=<Agent folder>  /DeleteSettings  /Data=<settings folder>
  /NoRegistry  /Log=<file>
```

Exit codes: 0 done; 1 failed or cancelled; 2 a command line not understood; 3 no Decal Agent to
install Virindi Tank into; 4 Decal Agent is running from the folder; 5 a setup built without its
files.

## Trying them on a developer's machine

A machine with a live Agent and a player's game on it is not one to install onto for real. The
setups take a trial run that writes nothing outside the folder given:

```powershell
dist\DecalAgentSetup.exe  /S /NoRegistry /NoShortcuts /Log=C:\Temp\trial\decal.log /D=C:\Temp\trial\Decal Agent
dist\VirindiTankSetup.exe /S /NoRegistry /Log=C:\Temp\trial\vt.log /D=C:\Temp\trial\Decal Agent
"C:\Temp\trial\Decal Agent\Uninstall.exe" /S /NoRegistry /Data=C:\Temp\trial\data /Log=C:\Temp\trial\uninstall.log
```

The wizard under `/NoRegistry` leaves "Start Decal Agent now" clear. Never start a trial Agent
bare - even `--help` starts a whole Agent on the player's ports and pipes. Start it beside
theirs, with everything its own:

```powershell
& "C:\Temp\trial\Decal Agent\DecalAgent.exe" --tray --control-pipe trial-1 --overlay-pipe trial-overlay-1 `
    --listen-port 39500 --server-port 39300 --data C:\Temp\trial\data --no-inject --set DecalCompat:Registry=false
```

(`DecalCompat:Registry=false` keeps it from running the Decal plugins registered on the machine
out of their real installs.) `achost ctl` on `trial-1` - or the pipe's `plugins` command - says
what it loaded.

The install logic itself is tested without any of that: `dotnet test tests/Setup.Tests` runs
installs, upgrades and uninstalls against folders under `%TEMP%`, with `MemoryRegistry` in place
of HKEY_CURRENT_USER and shortcuts as plain files (one test makes a real .lnk, in a temporary
folder, and reads it back through the shell).

## What a clean machine still has to show

- The wizard and the uninstaller's dialogs at a scaling other than 100%: the steps were drawn
  and checked at 96 DPI only.
- The Apps entry, the Start menu shortcut and a real uninstall from Settings, which were never
  run here - every one of them writes or deletes the developer's own HKCU and Start menu.
- SmartScreen: the setups are not signed, and Windows will say so the first time each is run.
- A first start of the installed Agent against the player's AC:Unreal and server, injection
  included, which a trial Agent beside the player's must not attempt.
