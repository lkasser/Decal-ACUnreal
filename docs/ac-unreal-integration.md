# Integrating with AC:Unreal — what the shipped client actually offers

Findings from inspecting the installed build. Everything here is observation, not
speculation; where something is unknown it says so.

## The client that was examined

| | |
|---|---|
| Product | AC:Unreal / AC:VR, release 84 (`2026.09.28.84`) |
| Install | `%LOCALAPPDATA%\Programs\ACUnreal` |
| Engine | Unreal Engine `5.8.0-55116800+++UE5+Release-5.8` |
| Build config | **Development** (per `LogCsvProfiler: Metadata set : config="Development"`) |
| Platform | Win64, D3D12, packaged (`global.utoc` + `ACUnreal-Windows.pak/.utoc`, 622 packages) |
| Game module | one project plugin, `ACEClient` |
| Protocols | ACE and GDLE login; GDLE verified only through character selection |

## The blocking finding: there is no plugin surface

Virindi Tank was a **Decal** plugin. Decal injected itself into the retail
`acclient.exe`, read the client's memory through its filters, and handed managed
plugins a live object model (`Decal.Adapter.CoreManager.Current`). Every Virindi
component in `virindi_public` is built against that: `Decal.Adapter`,
`Decal.Interop.Core/Filters/Net/Input/Inject`, plus `utank2-i.dll` for the Tank
plugin API itself.

AC:Unreal is a different process with none of that. Specifically:

- **The client says so.** `README-WINDOWS.txt`, verbatim:
  "Unreal Editor, ThwargLauncher, and Decal are not required."
- **No third-party plugin directory.** `ACUnreal/Plugins/` contains exactly one
  entry, `ACEClient`, and the only loose files under it are UI documentation
  JSON (`Docs/UI/Resolved/0x21......json`). Plugin content is cooked into the
  `.pak`/`.utoc`; nothing is designed to be dropped in beside it.
- **No remote-control or scripting surface.** Searching the 349 MB
  `ACUnreal.exe` for UTF-16 strings (UE `TCHAR`) finds **zero** occurrences of
  `RemoteControl`, `WebRemoteControl`, `AutomationAPI`, or `ScriptEngine`. The
  engine plugins actually mounted at runtime (per `Saved/Logs/ACUnreal.log`) do
  not include `RemoteControl`, `WebRemoteControl`, `PythonScriptPlugin`, or
  anything comparable.
- **No mention of Virindi or Tank.** The single UTF-16 hit for `Virindi` in the
  executable is the in-game character title "Virindi Informer" — the AC race, not
  the plugin suite. `VirindiTank` appears zero times. The 37 ASCII hits for
  `Decal` are Unreal's own decal rendering.
- **No published source.** Thwargle's 18 public GitHub repositories (ACE,
  ThwargLauncher, Mag-Plugins, DerethMaps, …) include no Unreal Engine or
  AC:Unreal client repository, so adding a plugin API in-tree is not an option
  from outside.
- **Release notes never mention plugins.** Release 84's notes cover movement,
  camera, world loading, inventory, interface and updating. No automation,
  scripting, or extension surface appears in them.

One detail cuts slightly the other way: the build is **Development**, not
Shipping, so the UE console and console variables are live and `-ExecCmds` exists
in the binary. That is a startup-time and local-keyboard surface, though — there
is no transport for another process to drive it, and no evidence `ACEClient`
registers gameplay commands worth driving.

**Conclusion: the Virindi plugins cannot be retargeted onto AC:Unreal by
recompiling them.** There is no host to load them and no API to call. Whatever
"upgrade for Unreal 5" ends up meaning, it is not a port of the Decal integration
layer.

## What was portable anyway, and is now ported

The Decal coupling in the public repository is far shallower than it looks. The
loot engine — the part of Virindi Tank that actually encodes users' decisions, and
the only part of Tank whose source is public — touches the client in exactly
**five** places (character level twice, buffed skill, base skill, and main-pack
free-slot counting), all in `LootRules.cs`. Everything else is pure logic over an
item-property model.

So the engine is now ported and building on .NET 10 against a clean-room host
contract, with those five call sites moved onto `IGameStateProvider`. That port lives
in the Virindi Tank repository (VirindiTank-ACUnreal): see its README and `UPSTREAM.md`.
This is client-agnostic: it is the piece any of the routes below would need, and it is
done.

## Routes to a working AC:Unreal integration

Ordered by how much depends on someone else.

### A. Ask for a plugin API (cheapest, not in our hands)

AC:Unreal is actively developed — release 84 shipped the same day this was
written, with an in-launcher updater. A host-side extension point (even just a
local WebSocket or named pipe exposing object/character state plus an action
channel) would make everything else straightforward. This is a Discord
conversation with the author, not an engineering task.

### B. Network proxy between client and server (chosen; in progress)

AC:Unreal speaks the AC wire protocol to ACE/GDLE servers, and the server address
is user-configured in the login screen (`Saved/Login/Servers.xml`). A local UDP
proxy can therefore sit in the path with no client cooperation at all:

- **Read** — decode the server's object-creation, property-update, inventory, and
  chat messages into exactly the item and character model the ported engine
  already consumes. `GameItemInfo` and `IGameStateProvider` are implementable
  from this stream.
- **Act** — inject client-sequenced messages for the actions a looter needs (use
  object, appraise, move item, give, sell).
- **Known cost** — the outbound direction is the hard half: correct sequence
  numbering, fragmentation and CRC handling, and keeping the proxy's injected
  traffic consistent with the real client's own. ACE is open source, which makes
  the protocol legible rather than guesswork.
- **Reusable** — the same proxy would serve the retail client, OpenAC, or any
  future client, because it depends on the protocol rather than on any client.

**Status.** Framing (`src/AC.Protocol`), the relay (`src/AC.Proxy`, `acproxy`)
and the plugin host (`src/AC.Host`, `achost`) are built and tested. The relay
forwards every datagram byte-for-byte before anything interprets it, so a
decoder gap can never break the game, and writes captures so decoders are
developed against real sessions replayed offline. The read direction is done:
objects, inventory, appraisals, character state and chat are decoded into a
world model, and VirindiTank runs against it as a plugin in dry-run mode. See
`docs/plugin-host.md`. Injection is next.

**Self-hosted server.** The target server is ACE 1.76.4751 on this machine
(`C:\ACE\Server`, `0.0.0.0:9000-9001`). That settles the server-policy question
below for development, gives the proxy a loopback peer on both sides - the case
where telling client from server is hardest, and which the classifier is tested
against - and means ACE's source is the exact protocol dialect in play.

### C. In-process injection into `ACUnreal.exe` (not recommended)

Technically what Decal did, but against a 349 MB stripped UE5 Development build
with no symbols, cooked assets, and an auto-updater that replaces the binary. Every
offset would break on each release. Reserve this as evidence that route B is the
right one.

### D. Input synthesis plus screen reading (weakest)

OS-level clicks and keystrokes against the UI, with OCR or pixel matching for
state. No protocol work, but no reliable item properties either, which is exactly
what loot rules are made of.

## Server-policy caveat, stated plainly

Whichever route is taken, this is automation of a live game client against
community servers. Individual ACE/GDLE server operators set their own rules about
automation, and some ban it. That is a question for the servers actually being
played on, and it is worth settling before building route B rather than after.

## Open questions worth answering before committing

1. Would the AC:Unreal author add a plugin/automation API, or accept a patch that
   does? (Route A collapses everything else.)
2. Which server — ACE or GDLE, which build — is the target? It decides the exact
   protocol dialect route B must speak.
3. Is the goal the full Tank feature set (nav, combat, macro engine, fellowship),
   or just looting? Only the loot engine's source is public; nav and combat would
   be new work, not a port.
