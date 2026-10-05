# Decal for AC:Unreal

A Decal for [AC:Unreal](https://www.thwargle.com/unreal/), the Unreal Engine 5 rebuild of the
Asheron's Call client. It lets plugins - Decal's own, and new ones written for it - run beside
the game again.

AC:Unreal has no plugin or scripting API and does not load Decal, so this does the job from
outside the game:

- **A UDP proxy** sits between AC:Unreal and the server. The client speaks the AC wire protocol
  to ACE and takes any host and port, so the relay sees the whole session without the client's
  help.
- **A plugin host** decodes that session into a world model (objects, inventory, appraisals,
  character, movement, chat) and gives plugins a stable API to read it and, when the player
  allows it, to act: appraise, use, move items, speak, walk, cast.
- **Decal compatibility.** Stand-ins for `Decal.Adapter`, `Decal.FileService`,
  `Decal.Interop.Core` and Virindi View Service let existing Decal plugins load and draw their
  windows.
- **An in-game overlay.** A small C++ DLL injected into the client hooks its D3D12 swap chain and
  draws Decal's bar and the plugins' windows with Dear ImGui in the game's own frame.
- **Decal Agent**, a tray program that runs all of this, and **DecalAgentSetup.exe**, its
  installer.

Virindi Tank, as a plugin for this host, is a separate repository:
[VirindiTank-ACUnreal](https://github.com/lkasser/VirindiTank-ACUnreal).

This is an independent reimplementation. It is not affiliated with or endorsed by Turbine,
Warner Bros., the authors of Decal, Virindi, or the authors of AC:Unreal.

## Requirements

- Windows 10 or 11, 64-bit.
- The [.NET 10 SDK](https://dotnet.microsoft.com/) to build. Players need nothing: the installer
  carries its own runtime.
- AC:Unreal.
- An ACE server - your own, or a community server whose rules allow automation. ACE binds its
  configured port and the next one (9000 and 9001 by default), so the host listens on 9100 and
  the client gets a `127.0.0.1:9100` server entry.
- For the overlay only: the Visual Studio C++ build tools (MSVC), and `native\fetch-deps.ps1`
  run once to fetch Dear ImGui, MinHook and nlohmann/json at pinned commits.

## What's here

```
src/AC.Protocol        AC wire framing: packet and fragment headers, optional sections, Hash32,
                       message reassembly, and a writer.
src/AC.Proxy           The UDP relay, and a capture format for replaying sessions offline.
src/AC.Proxy.Cli       `acproxy`: run the relay, or replay a capture through the parser.
src/AC.Dat             Reader for the client's data files (portal and cell DATs, spells, skills,
                       palettes, images) and for Virindi View Service's window store.
src/AC.Host            The plugin host: IPlugin/IHost contract, world model, decoders,
                       transports, loader, game thread, and the actions plugins can take.
src/AC.Host.Runtime    The whole host as achost and Decal Agent start it: relay, plugins,
                       overlay and control pipes.
src/AC.Host.Overlay    The named-pipe server that feeds the injected overlay.
src/AC.Host.Cli        `achost`: the host over a live relay or a capture, and `achost ctl`.
src/AC.Host.Ui         A plain window of its own over the same host.
src/AC.Injector        `acinject`: puts the overlay DLL into the client, and takes it out.
src/Decal.Adapter      Stand-in for Decal.Adapter, signed with Decal's public key so Decal
src/Decal.FileService    plugins bind to it by name; likewise Decal.FileService and
src/Decal.Interop.Core   Decal.Interop.Core.
src/Decal.Compat       Loads and runs Decal plugins (from Decal's registry or a folder).
src/VirindiViewService The VVS surface Decal plugins call, drawn by the overlay.
src/Microsoft.DirectX  A stub of the Managed DirectX types plugins name.
src/Decal.Agent        DecalAgent.exe, the tray program.
src/Setup.Common       What the setups share; src/Decal.Setup is DecalAgentSetup.exe and
src/Decal.Setup          src/Setup.Uninstall is Uninstall.exe.
src/Setup.Uninstall
native/                The C++ overlay DLL (D3D12 hook, Dear ImGui), its build and test scripts.
third_party/sqlite     A 64-bit sqlite3.dll for Decal plugins that ship a 32-bit one.
tests/                 xUnit tests, and small plugins built to be loaded by them.
tools/                 run-host.ps1, start-game.ps1, update-agent.ps1, build-installers.ps1,
                       and enumdump (reads protocol enums out of a local ACE server).
docs/                  How the host works, what AC:Unreal offers, the installer.
```

## Build and test

```powershell
dotnet build Decal-ACUnreal.slnx
dotnet test Decal-ACUnreal.slnx
```

About 820 tests: AC.Protocol 58, AC.Proxy 72, AC.Dat 29, AC.Overlay 58, Setup 63 and AC.Host 540.
Some read files that are never committed and skip cleanly without them: the AC.Dat tests that
read a real `client_portal.dat` (they look in `C:\ACE\Dats` and the usual Turbine folders), and
the AC.Host tests that replay session captures (`*.acap`), which hold account names and so are
never committed.

The overlay is built separately, and needs MSVC:

```powershell
native\fetch-deps.ps1   # pinned Dear ImGui, MinHook, nlohmann/json, into native\third_party
native\build.ps1        # native\build\ACUnrealOverlay.dll
native\test.ps1         # its self-test
```

## Running the host

With an ACE server on this machine:

```powershell
dotnet run --project src/AC.Host.Cli -- run --server 127.0.0.1 --server-port 9000 --listen-port 9100 --objects
```

Then, in AC:Unreal's login screen, add a custom server with host `127.0.0.1`, port `9100` and
type ACE, and log in through it. `achost` loads the plugins in `plugins\` beside it - Decal's own
compatibility plugin is laid out there by the build; others are installed there - prints
objects as they are created and appraised, and on Ctrl+C reports which messages it had no
decoder for. `--capture session.acap` records the session; the same host runs over a recording
with no client or server:

```powershell
dotnet run --project src/AC.Host.Cli -- replay session.acap --objects --chat
```

To restart the host by hand - stop the old one, wait for the port, rebuild, start - use:

```powershell
tools\run-host.ps1 -EnableActions -Loot -TestMove
```

It says what it turned on. `-EnableActions` is needed for the other two to mean anything.

**Letting plugins act** is deliberate: `--enable-actions` (or "Let plugins act" in Decal's
window) lets them act at all. Messages ride out inside the client's own packets;
[docs/plugin-host.md](docs/plugin-host.md) explains why, and what is proven live and what is
only built.

**Inside the game.** With the overlay built, `tools\run-host.ps1 -EnableActions -Overlay`, then
`dotnet run --project src\AC.Injector -- --process ACUnreal --dll native\build\ACUnrealOverlay.dll`
(`--pid` when the name matches more than one process; `--unload` to take it out again). Decal
Agent does this by itself once the game is up.

**Chat.** `IHost.ShowInGame(text, chatType)` puts a line in the game's own chat window. Lines are
prefixed, never disguised: a plugin's lines arrive as it wrote them ("[VTank] ...", "[VI] ..."),
and the host's own begin "[Decal] ".

**What an id means.** Nothing is decoded from memory: `tools/enumdump` reads enums out of a
local ACE server's own assemblies, and `achost replay --dump-event 0x01C7` prints the bytes of
one message type from a real session.

```powershell
dotnet run --project tools/enumdump -- "C:\ACE\Server" GameEventType
dotnet run --project src/AC.Host.Cli -- replay session.acap --no-plugins --dump-event 0x01C7
```

**The proxy alone**: `dotnet run --project src/AC.Proxy.Cli -- run --server 127.0.0.1
--server-port 9000 --listen-port 9100 --capture session.acap`, and `... replay session.acap`.

## Decal Agent and the installer

`src/Decal.Agent` is Decal as a program of its own: it runs the host from the notification area,
lists and manages plugins as Decal's agent did, and puts the overlay into the game.
`tools\start-game.ps1` starts an installed Agent and the game.

```powershell
tools\build-installers.ps1      # dist\DecalAgentSetup.exe
```

`DecalAgentSetup.exe` installs the Agent for the player alone, self-contained, with no
administrator and nothing installed first. Plugins install into its `plugins` folder with setups
of their own (Virindi Tank's repository builds `VirindiTankSetup.exe`).
`tools\update-agent.ps1` updates an installed Agent from `dist\`. See
[docs/installers.md](docs/installers.md).

## Writing a plugin

Reference `AC.Host` (with `Private=false`, so it is not copied), implement `IPlugin`, declare
`[assembly: PluginApi(HostApi.Version)]`, and put the built plugin in a folder of its own under
`plugins\`. `tests/plugins/Counting.Plugin` is the smallest example; the Virindi Tank repository
is the full one. A Decal plugin needs nothing: Decal.Compat loads it as Decal would.

## Documentation

- [docs/plugin-host.md](docs/plugin-host.md) - the host's design, what is decoded, how actions
  work, the overlay, Decal's window and Decal's plugins, `achost ctl`.
- [docs/ac-unreal-integration.md](docs/ac-unreal-integration.md) - what the shipped client
  offers, and the four routes to an integration.
- [docs/plugin-base-comparison.md](docs/plugin-base-comparison.md) - Chorizite, Decal, and this
  host compared.
- [docs/installers.md](docs/installers.md) - the installer and uninstaller.
- [native/docs/](native/docs/) - how the overlay's hooks were established, and its contract.

## Third-party

- **SQLite** (`third_party/sqlite`): public domain; see its README.
- **Decal's `messages.xml`** (`src/Decal.Adapter`): Decal's message schema, as Decal shipped it,
  under Decal's own terms. The stand-ins are signed with Decal's *public* keys
  (`*PublicKey.snk`, public halves only) so Decal plugins bind to them; no private key is here.
- **AutoWireupExamplePlugin** (`tests/decal-plugins`): Virindi's example Decal plugin from
  [virindi_public](http://www.virindi.net/repos/virindi_public/), MIT, Copyright (c) 2011
  VirindiPlugins; its notices are intact.
- **Dear ImGui**, **MinHook** and **nlohmann/json** are fetched by `native\fetch-deps.ps1` at
  pinned commits and not committed; they keep their own licences (MIT, BSD-2-Clause, MIT).
- The docs quote short excerpts, with links, from
  [Chorizite](https://github.com/Chorizite), [virindi_public](http://www.virindi.net/repos/virindi_public/)
  (MIT), [Dear ImGui](https://github.com/ocornut/imgui), [MinHook](https://github.com/TsudaKageyu/minhook)
  and [kiero](https://github.com/Rebzzel/kiero).
- The images in `src/AC.Dat/Images` and `src/Decal.Agent/Resources` were extracted from the
  installed Decal and Virindi programs, to draw their windows as they looked.

Virindi Tank, Virindi View Service, Virindi HUDs and Virindi Hotkey System are closed source and
are not included; the stand-ins here are written from their public behaviour.

## Licence

MIT - see [LICENSE](LICENSE). Copyright (c) 2026 lkasser. Third-party files keep their own terms,
as listed above.
