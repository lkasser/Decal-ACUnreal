# AC:Unreal release 96: the client's plugin system, UCM, and our stack

I looked at the installed client on 2026-10-06, between 06:00 and 06:20 local time, while the
player was logged in through our proxy. Nothing was started, stopped or written under the client's
folders. Two labels are used throughout:

- **Seen** means read directly: in a file, a string in the binary, a packaged file or a log.
- **Inferred** means worked out from disassembly, the binary's automated-test names or reasoning.
  Nothing marked Inferred was run.

## The client that was examined

| | |
|---|---|
| Version | `2026.10.06.96`. Seen in `DefaultGame.ini` inside the pak and in the log line `Set ProjectVersion to 2026.10.06.96`. |
| Engine | `5.8.3`, `++UE5+Release-5.8-CL-58210709`. This is the same build release 93 used. `ac-unreal-integration.md` says 5.8.0 for release 84, and that changed between releases 87 and 93. |
| Before today | Release **93** (`2026.10.03.93`), in every log from 10-04 18:22 UTC to 10-06 13:51 UTC. Release 87 ran on 10-04 and release 86 on 09-29. **Releases 94 and 95 never ran here**: the plugin system arrived with this morning's update (`Saved/Updates/installer.log`, 05:52). |
| Release notes | `RELEASE-NOTES.md` covers release 96 only. Everything said here about release 94's features comes from the binary and the pak. |

**How the evidence was gathered:**
- Strings were taken from `ACUnreal.exe` (344 MB), both UTF-16 and ASCII.
- `ACUnreal-Windows.pak` was opened. It is pak version 12, compressed with Oodle. Its index was parsed, and the few files below were decompressed into a scratch folder outside the repository using `oo2core.dll` from the installed UE 5.8.
- The code that references the key strings was disassembled with capstone. The addresses quoted are for this build only and will move with each release.
- The client's `Saved/` files and three logs were read: the client's, our host's and the overlay's.

## 1. Summary

- **Since release 94 the client has a real plugin host.** It runs **sandboxed Lua 5.4.9** scripts:
  - Each plugin is a folder with `plugin.json` and `main.lua`.
  - `main.lua` returns `tick(snapshot, profile)`, and that function returns at most one *intent* (an action) per call.
  - Plugins have no .NET, no DLL loading, no file access, no sockets and no IPC.
  - UCM itself is such a plugin: `ClientMods/ucm/main.lua`, which ships inside the pak.
- **Third-party plugins look possible but are undocumented and untested.** The loader scans a second
  folder, `Saved/ClientPlugins/Installed/<id>/` (Inferred from disassembly). No SDK ships.
- **Decal plugins cannot run inside the client, and neither can our C# Virindi Tank.** A plugin
  gets a read-only snapshot, eight permission groups of actions and a generic settings window.
- **UCM is off by default and has never been enabled here.** Even once enabled it acts only after
  Start is pressed. The client's log says nothing about plugins, so from outside only the *enabled*
  state can be seen (in `Saved/ClientPlugins/settings.json`), not whether UCM is *running*.
- **The Virindi conversion is a clean-room re-implementation of Virindi Tank inside the client.**
  - It converts `.utl`/`.nav`/`.met`/`.usd` files to native JSON.
  - It covers nearly all of VT's meta engine and about 100 expression functions.
  - Its loot engine is based on VTClassic (the client ships VTClassic's MIT licence).
- **Nothing our stack depends on changed between releases 93 and 96.** That covers the log lines,
  the character select, the swap chain and logging out by message. Logging out by message was proven again on 96 at 06:15.

## 2. The plugin system

### 2.1 What ships

The following was **Seen** in the pak's directory index. All entries are under
`ACUnreal/Plugins/ACEClient/ClientMods/`:

| Entry | What it is |
|---|---|
| `ucm/plugin.json`, `ucm/default.json`, `ucm/main.lua` | Unattended Combat Manager, `"version": "0.7.0"`. `main.lua` is 145,614 bytes and 2,195 lines. |
| `waypoint/plugin.json`, `waypoint/default.json` | Waypoint (`"short_name":"NAV"`), with `"permissions":[]`: "No automated movement." |
| `looteditor/plugin.json`, `looteditor/default.json` | Loot Profile Editor (`"LOOT"`), with `"permissions":[]`: "This editor performs no game actions." |
| `LICENSE-Lua.txt`, `LICENSE-VTClassic.txt` | The second is the VirindiPlugins MIT notice. It is the same upstream as our `src/VTClassic`. |

Waypoint and the loot editor have no `main.lua`. Their windows are native C++. The source file names
are in the binary: `Mods\ACEPluginAutomation.cpp`, `ACEPluginDesktop.cpp`, `ACEPluginPanel.cpp`,
`ACEUCMPanel.cpp`, `ACEUCMLootPanel.inl`, `ACEUCMVendorPanel.inl`, `ACEVTImportPanel.inl`,
`ACEVTLegacyLootPanel.inl` and `ACEWaypointPanel.cpp`. The tests are named `ACEPluginTests`,
`ACEPluginRequestsTests`, `ACEUCMPolicy/Recovery/RestockTests` and
`ACEVTAdapters/Meta/Profile/SettingsTests`. The loader checks for the ids `looteditor` and
`waypoint` by name, so they are special cases (**Seen** as string references in the loader).

### 2.2 The manifest (`plugin.json`)

**Seen** in UCM's manifest:
- `api` (1), `id`, `name`, `short_name` (the label on the plugin bar), `version` and `description`.
- `permissions`.
- `settings`: a list of `{key, label, type: bool|number|choice, min, max, values}`.
- `tools`: UCM uses `"spells"` and `"route"`.

`default.json` beside it is the default profile.

**Seen** in the manifest parser (code from 0x149F5D112 to 0x149F5DCA4):
- The fields `id`, `api`, `name`, `version`, `description` and `permissions`.
- The **complete permission vocabulary**, checked one name at a time: `cast`, `combat`, `navigation`, `inventory`, `loot`, `say`, `fellowship`, `confirm`.
- Anything else is rejected with "Unsupported permissions: ". UCM asks for all eight.

### 2.3 The runtime: a sandboxed Lua 5.4

**Seen** in strings:
- `$LuaVersion: Lua 5.4.9`.
- "Plugin must return a tick(snapshot, profile) function".
- "Tick must return nil or an intent table".
- "Cannot read main.lua (maximum 256 KiB)".
- "Plugin exceeds source/memory limit".
- "Cannot read profile (maximum 4 MiB)".
- "Intent key exceeds limit", "Intent text exceeds limit" and "Non-finite intent value".

**Seen** in the sandbox tests:
- The test "Sandbox has no filesystem, native loader, process, debug or coroutine APIs" asserts
  `io`, `os`, `package`, `require`, `debug`, `coroutine`, `load`, `dofile` and `pcall` are all nil.
- `string.find` is removed. A literal-only `string.contains` replaces it.
- Endless loops are interrupted, both at load time and inside a callback.
- An oversized allocation fails without killing the client.

**Seen** in UCM's `main.lua`: the host provides the globals `workavailable()` (a cooperative
budget check), `regexmatch()`, `spellindex()` and `ucmcommand()`.

### 2.4 Where plugins are found, and third-party plugins

**Seen** as strings:
- "Reload installed plugins" and "Manage installed plugins".
- `ClientMods`, `Installed`, `plugin.json` and `main.lua`.
- A test fixture for a plugin that does not ship (`monitor`, "Example Monitor", `{"short_name":"MON"}`).

**Inferred** from disassembly of the reload routine. It starts at the reference to "Reloading
plugins", 0x149F5C996.
- It builds two roots in one array:
  - The `ACEClient` plugin's base directory (looked up through `FindPlugin`), with `ClientMods` appended (0x149F5CAAB). These are the shipped three, inside the pak.
  - The client's data folder, with `Installed` appended (0x149F5CBD0).
- The data folder comes from a helper (0x149F6E660). It appends `ClientPlugins` to the project's `Saved` directory, unless a test has overridden it.
- It then reads `plugin.json` in each subfolder (0x149F5CF7E).

So a third-party plugin would go in:

```
%LOCALAPPDATA%\Programs\ACUnreal\ACUnreal\Saved\ClientPlugins\Installed\<id>\plugin.json
                                                                        \<id>\main.lua
```

This is **not tested**: the folder does not exist, and nothing may be written under the client.
No signing or allow-list strings were found near the loader. No SDK, documentation or URL for
plugin authors ships: `README-WINDOWS.txt` is unchanged and the binary contains no plugin URL.
"Skipped invalid, duplicate, or incompatible plugin: " shows that discovery validates each manifest.

### 2.5 What a plugin can see, do and show

**The world** (**Seen**: the fields UCM's `main.lua` reads from the snapshot):

| Area | Fields |
|---|---|
| Player | `player`, `level`, `health`/`stamina`/`mana` and their maxima, `position`, `server_position`, `heading`, `combat_mode`, `busy`, `ready`, `jumping`, `portal_space`, `burden_percent` |
| Skills and spells | `skills`, `trained_skills`, `usable_skills`, `spells`, `known_spells`, `enchantments`, `harmful_enchantments`, `cooldowns`, `last_spell`, `components_required` |
| Character properties | `char_ints`, `char_strings`, and their quad, double and bool equivalents |
| Items and containers | `inventory` (items carry `wcid`, `object_class`, `int_properties`, `float_properties` and `identified`), `packs`, `pack_slots`, `main_pack_slots`, `container`, `contents`, `contents_ready` |
| Around the player | `targets` (monsters with `distance` and `line_of_sight`), `corpses`, `route_objects`, `selected`, `nearest` |
| Vendor | `vendor`, `vendor_stock` |
| Fellowship and pets | `fellowship`, `in_fellowship`, `owned_pet` |
| Events | `chat_events` (text, colour, serial), `combat_events`, `portal_events` |
| Bookkeeping | `action_serial`, `action_error`, `time` |

**Actions** are intents: a table naming the `action` (for example `cast`) and its arguments (for `cast`, the `spell` and the `target`). **Seen**: the action names
the permission check compares, in this order (0x149F60B93). **Inferred**: the grouping into
permissions, from that order.

| Permission | Actions |
|---|---|
| `navigation` | `move`, `face`, `jump`, `recall`, `use_world`, `select`, `logout` |
| `combat` | `attack`, `cancel_attack`, `combat_mode`, `attack_bar` |
| `inventory` | `store_item`, `give`, `apply_item`, `equip`, `use_item`, `identify`, `merge`, `buy`, `combine_salvage`, `salvage`, `sell`, `read` |
| `loot` | `loot`, `open_corpse`, `close_corpse` |
| `cast`, `say`, `fellowship`, `confirm` | one action each, of the same name |

- Some actions need no permission: `stop`, `notice`, `profile_load`, `buff_request_done` and `force_buff_done`.
- An intent can also carry a route (at most 2,048 points), which the client draws in the world.
- **Seen** test names: "Host rejects ungranted action" and "Login screen cannot issue game actions".

**Chat.** A plugin reads `chat_events`. It writes only through `say` (which goes to the server) and
`notice` (a local status line). It has no way to put arbitrary lines into the chat window.

**UI.** Seen strings describe what a plugin gets:
- A button on the client's plugin bar (dock), labelled with its `short_name`.
- A generic window with:
  - Start/Stop and a status line;
  - a settings form generated from the manifest's `settings`;
  - the manifest's `tools` (only `spells` and `route` are known);
  - profile save and load;
  - "Advanced profile and state rules (JSON)".
- Windows can be hidden while the plugin keeps running ("Hide window (plugin keeps running)").

There is no drawing API, no custom control and no hotkey.

**Lifecycle.** **Seen** test names and help text:
- "Plugins default to disabled" and "Disabled plugin cannot start".
- "Enabling never auto-starts" and "Start is manual each session".
- "Reload never resumes automation".
- "Changed permission set requires re-enabling".
- "Manual movement stops automation" and "Leaving world stops automation".
- "Another plugin started" (a reason for stopping).

From these, **Inferred**: only one automation plugin runs at a time.

**Inferred** from disassembly (0x149F69329):
- The grant is stored per plugin as `"enabled:<comma-separated permissions>"` or `"disabled"`.
- It is written through the routine that also references `settings.json` (0x149F67EE0), so it is
  kept in `Saved/ClientPlugins/settings.json`.
- The key is most likely the plugin id.

### 2.6 Could a Decal-like host run inside the client?

**No, not in Decal's sense:**
- Decal plugins are .NET and COM assemblies. Ours is a .NET host. The client loads neither DLLs nor .NET, only Lua text.
- The Lua sandbox has no files, no sockets and no IPC.
- A plugin inside the client therefore cannot reach our host, Decal.Compat or the player's real Decal plugins.

What *could* run inside is a **Lua port of a plugin's logic**. **Inferred** from §2.5, it would get:

| | Inside the client (Lua) | Our host today |
|---|---|---|
| World | One snapshot per tick, curated by the client. No raw messages and no event hooks beyond the three event lists. | The full message stream, decoded into a world model, plus Decal's object model. |
| Chat | Read with colours; write only by `say` or a status `notice`. | Read, filter, and inject lines in any colour. |
| Actions | One intent per tick, in eight permission groups, validated by the client. Movement is the client's own. | Every message the client can send; movement through the overlay's keys. |
| UI | A generic settings window and a plugin-bar button. | Decal's bar, real VVS and Decal views, HUDs and hotkeys. |
| Files | None at run time. The profile arrives as JSON (at most 4 MiB). | Reads Virindi Tank's files in place. |

The only channel between such a script and our host would run through the server:
- A `say`/tell from the plugin, which the proxy could intercept and drop.
- A chat line injected by the proxy, which the plugin would see in `chat_events`.

That channel is one intent per tick, it shows up as chat, and starting the plugin would stop UCM
(and the reverse). **Not recommended.**

## 3. UCM: its settings, idle behaviour, and the conflict

### 3.1 Where it keeps things

The paths are **Seen** as strings. The roles come from each string's own wording.

| Path under `ACUnreal/Saved/ClientPlugins/` | What |
|---|---|
| `settings.json` | Window positions (`_window_positions`), `<id>.profile` (the selected profile), `ucm.folder` (the import folder), and the enable grants (Inferred, §2.5). |
| `Profiles/ucm/*.json` | UCM setups. Each import writes a new copy ("Converted to Profiles/ucm/…"). Defaults come from the pak's `ClientMods/ucm/default.json`. |
| `LootProfiles/*.json` | Loot-only profiles ("Saved files are in Saved/ClientPlugins/LootProfiles"). |
| `LegacyLootProfiles/` | `.utl` copies ("Saved UTL copy: "). |
| `Imports/<name>.json` | Import and compatibility reports. |
| `ImportInbox/` | A drop folder for files to import. |
| `Installed/` | Third-party plugins (Inferred, §2.4). |

**State on this machine** (**Seen**):
- `settings.json` was written at 05:58:46 today. It holds only Waypoint window positions and `"waypoint.profile": "Default"`.
- `Profiles/` contains only `waypoint/Default.json`, written at 05:58:44.
- No `ucm` key, no `Profiles/ucm`, no `Imports`, no `LootProfiles` and no `Installed`.

**So the player opened Waypoint this morning, and UCM has never been enabled, configured or used
here.**

### 3.2 Off by default, and what it does while idle

- **Seen**: disabled by default. Even once enabled, it acts only after Start, `/ucm start` or
  Force Buff.
- **Seen**: Force Buff "If UCM is stopped, runs only buffs and recovery, then stops."
- **Seen**: there is one action that runs while UCM is stopped: "Recharge equipment while UCM is
  stopped". The option is `mana_charges_when_off`, mapped from Virindi Tank's `ManaChargesWhenOff`.
  It "Uses configured mana supplies only; does not start combat, buffs or navigation".
  **Inferred**: an *enabled* UCM with that option set uses mana stones without being started. It is
  not in `default.json`, and it is not in the player's current `.usd`.
- **Seen**: "Players can request role-based buffs by tell while UCM and Buff are running". This
  happens only while UCM is running.
- `default.json` (**Seen**) sets `buffing: true`, `combat: "off"`, `navigation: false` and
  `looting: false`. These apply only once UCM is started.

### 3.3 Telling from outside whether UCM is running

| Signal | Result |
|---|---|
| The client's log (**Seen**) | Today's `ACUnreal.log` has no plugin or UCM lines. The binary's UCM strings are UI text and test names, not log formats. Its own log categories are `LogACEClient` and `LogTemp`, with lines prefixed `ACE`/`[ACE]`. Nothing to read. |
| Files | Shows only *enabled*, through the `ucm` grant in `settings.json` (format Inferred). No running state is written anywhere that was found. |
| The client's UI | " (UCM running)" or " (UCM stopped)" in Waypoint's window titles, and the notice "UCM stopped.". Visible only on screen. |
| The wire | UCM's actions are the client's own ordinary messages. The proxy cannot tell them from the player's. |

### 3.4 Making sure only one automates

These facts help:
- UCM is off until the player grants it in `/plugins` and presses Start. It stops on leaving the world, on `/ucm stop`, and on manual movement ("Paused by manual movement; press Start to resume").
- Starting another client plugin stops it.

These facts cut the other way (**Inferred**):
- Our overlay's held movement keys reach the client as the player's input, so they would pause UCM.
- Our *injected* casts, attacks, uses and loot moves never pass through the client's input. UCM would carry on beside them and see their effects in its snapshot. The two engines would then fight over targets, buffs and corpses.
- Our lines in the game's chat (`[VTank] …`, `[Decal] …`) reach UCM's `chat_events`. They could fire an imported meta's ChatMessage rules.

So:

- **The player:** run one or the other in each session. Leave UCM disabled in `/plugins` while
  using our Virindi Tank. `/ucm stop` or any movement key stops a UCM that is running.
- **Our host (proposed here; built since, as below):**
  - Watch `Saved/ClientPlugins/settings.json` read-only, as the host already reads the client's log.
  - When `ucm` (or any plugin holding automation permissions) is `enabled:`, say so on Decal's bar and in the log.
  - Ask for confirmation before Virindi Tank's macro starts.
- **What was built** (see `ac-unreal-integration.md`, "Since release 94"):
  - The host polls the file read-only.
  - It says in its log and in `ctl status` which client plugins are enabled.
  - It gives plugins `IHost.ClientPlugins`.
  - Virindi Tank warns in chat as its macro starts beside an enabled UCM, rather than asking, and starts anyway.
  - The grant format is now **Seen**: the file was rewritten at 06:04 with `"waypoint": "disabled"` and `"looteditor": "disabled"`, and the binary has the test string `enabled:cast`.
- **What cannot be done from outside:** detecting a *running* UCM reliably. One heuristic: the
  client sending game actions while the overlay sees no player input. It is unproven and would
  give false alarms.

## 4. The Virindi file conversion (the competing approach)

**Seen** in strings unless marked otherwise.

**Inputs:**
- `.utl` loot rules, `.nav` routes (`uTank2 NAV 1.2`), `.met` metas and `.usd` settings.
- `GameInfoDB.UGD`/`gameinfodb.ugd` beside the source, for "Monster expression requires
  gameinfodb.ugd beside the source profile" and for pea splitting.
- For Waypoint, GoArrow's `locations.xml` ("GoArrow atlas imports without a DLL").

**Where it reads from:**
- A configured folder (`ucm.folder`). The default is `C:/Games/VirindiPlugins/VirindiTank`. That
  folder does **not** exist here; the player's files are in the Virindi Tank plugin folder, elsewhere.
- A single file path.
- `Saved/ClientPlugins/ImportInbox`.

**The rules it states:**
- "Originals are never changed."
- "Only fully supported conversions can be activated; unsupported data is retained in an import report."
- "Supported /vt commands become /ucm actions."
- "Each import saves a new native JSON setup under Profiles/ucm."
- "Decal plugins are not loaded."

**Coverage**, from UCM's `main.lua` (**Seen**):

| Area | Coverage |
|---|---|
| Meta conditions | Types 0–26 and 28. Type 27 is the one missing; in Virindi Tank's numbering that is ClientDialogPopup (**Inferred**). |
| Meta actions | Types 0–12. |
| Expression functions | About 100, including `getvar`/`setvar`, the `list*`, `stopwatch*`, `coordinate*`, `getchar*`, `getfellow*`, `wobjectfind*` and `actiontry*` families. |
| Commands | The `/vt` set: `opt set`, `mexec`, `jump`, `setattackbar`, `reverseroute`, and `meta`/`nav`/`loot`/`settings` `load`. |
| Buffing | Release 96 adopted "VT BuffController" opening order (the comment in `main.lua`). |

**What it reports as unsupported:**
- "Custom User action %d requires its original plugin", meaning a third-party plugin's loot actions.
- "Command needs an adapter: ", "Expression function needs an adapter: ", "Character setting needs an adapter: " and "Character table needs an adapter: ".
- Original Classic loot requirements that "need an adapter".
- Regular expressions: "Extended-mode regex is unsupported", "Lookbehind/conditional regex is not supported", comment groups, and numeric backreferences mixed with named groups.
- Colour requirements, which "cannot be activated in UCM yet".
- VT expressions in some fields, which are "preserved, not executed by UCM".
- "Unsupported meta table: ", "Unsupported monster damage mode: ", "Unsupported monster expression operator: ", "Unsupported recharge handler: ", "Unsupported pet damage mode: ", "Unsupported secondary vulnerability: ", "Unsupported assistance item: ", "Unsupported NAV header", "Unsupported salvage combine version" and "Unsupported copy modifier".
- "Unsupported active setting blocks whole import".
- For routes the player records: "Same landblock only; no automatic jumps or portals".

**How it differs from ours:**
- UCM converts once into its own JSON and runs its own engine, with the client's own movement and windows.
- Our Virindi Tank reads the player's files in place, shows Virindi Tank's own window, and runs beside the player's real Decal plugins.
- Both engines take the same source files. The player's Virindi Tank plugin folder holds 207 `.met`, 236 `.nav`, 252 `.usd`, 12 `.utl` and 1 `.ugd`.

## 5. Changes since release 84 that could affect our stack

| Area | Release 96 | Effect |
|---|---|---|
| Log lines `SessionControl` reads | **Seen** in the binary: the format string `ACE UIFlow mode -> %d layout=0x%08X`. Today's log has modes 2 (login), 3 (character select, `0x21000004`), 0 (between) and 6 (game, `0x21000005`), as before. `CharacterLogOff (0xF653) sent`, `CharacterLogOff (server)`, `Logged off …`, `EnterWorld request character=%d` and `LoginRequest sent to %s:%d as '%s'` are unchanged. Compared with release 93's logs, no new `[ACE]` line kinds appear. | None |
| Logout | The player's `acclient.keymap` (in `Documents\Asheron's Call`, last written 09-29) still binds `LOGOUT` to Shift+Escape. **Seen** live on 96 at 06:15:26: the host logged out by message, and the client logged `Logged off — returned to character select` and modes 2 then 3, staying connected, exactly as on release 93. | None |
| Keymaps | The binary has retail keymap import and export: `*.keymap` from `Documents/Asheron's Call`, `Saved/Keymaps` or beside the DAT files. Whether this is new since release 84 is unknown. | If the player loads another keymap, the keys our overlay presses (W, A, …) could change meaning. |
| Character select (800 × 600, centred) | `Docs/UI/Resolved/0x21000004.json` is still dated 2026-09-05. The window was 1920 × 1080 at start and 1924 × 1083 after the player resized it at 05:58. | None at 100%. The binary has a "Desktop UI Scale" option (currently `DesktopUIScale=1`). It scales the whole viewport, the character select included (**Inferred**, §8.1). The click now follows it. |
| Window and swap chain | Overlay log today: present through `Present`; hooks installed on attempt 1; 3 buffers, format 24. `ResizeBuffers` 1920×1080 → 1924×1083 was handled. Minimising gives `ResizeBuffers` to 8×8, then no frames, the same as on 10-05. The title is still `AC:Unreal (64-bit Development PCD3D_SM5)`. The engine build is unchanged since release 93. | None |
| New UI: plugin bar and plugin windows | UE widgets drawn inside the game's frame: a dock with one `short_name` button per plugin, and movable windows. Positions are saved in `settings.json`. **Inferred**: our overlay draws after them, at `Present`, so its windows cover theirs, and a click over one of our windows goes to the overlay. The dock starts at the top left, at 8,80 (**Inferred**, and **Seen** in the live test's screenshot, §8.2). It has no saved position here. Decal's bar is drawn "compact, top, 114 long" (host log), at y 10 to 33. | Our VVS bar overlaps it (**Seen**); Decal's bar along the top cannot (§8.2). |
| Chat commands | `/plugins` and `/ucm …` are parsed by the client. | None for us. |
| Protocol | The binary has strings for an opt-in VR-observer exchange: "Desktop world entry sends a capability request", "Desktop opts in only when server advertises receive-only support". Whether this is new since release 84 is unknown. The relay forwards bytes untouched. | None seen with ACE 1.76 |
| Our docs | `README.md` ("release 84 exposes no plugin, scripting, or remote-control API") and `docs/ac-unreal-integration.md` ("there is no plugin surface") are **out of date since release 94**. | Docs to update |

## 6. Our host on release 96 today

**Seen** in `ACHost\logs\DecalAgent-20261006.log`, `ACUnrealOverlay.log` and `ACUnreal.log`:

| Time | What happened |
|---|---|
| 05:53 | The host started. 7 of 15 Decal plugins ran. The overlay attached and drew. |
| 05:55:49–51 | Login through `127.0.0.1:9100`, the character select, then the world. Virindi Tank loaded its meta, settings and route. |
| 06:03:58 | The player logged out. The client resent CharacterLogOff every 2 s, four times. It closed the session 0.9 s after reaching the character select, which matches what `plugin-host.md` calls the player's own pattern. |
| 06:04:33 | The player logged in again. |
| 06:05 | The game was minimised ("no frames"). |
| 06:13 | The game was shown again. |
| 06:15 | Acting was allowed, the macro started, and the host logged out by message without problems. |

**Nothing in the three logs points to the update.** The repeated `[VI] Disconnected` lines were
already there yesterday (52 on 10-05).

## 7. Recommendation

1. **Keep the proxy, the overlay and Decal.Compat as the architecture.** The client's plugin API is a
   sandboxed Lua tick-to-intent interface: no DLLs, no .NET, no files and no IPC. It cannot host
   Decal plugins or our host, and a script inside it cannot talk to our host. "Move Decal into the
   client" is not possible on release 96.
2. **Treat UCM as a peer that must not run alongside our Virindi Tank.**
   - Add a read-only watch of `Saved/ClientPlugins/settings.json` to the host.
   - Warn on Decal's bar when UCM holds a grant, and confirm before Virindi Tank's macro starts.
   - Tell players to pick one engine per session.
3. **Revisit route A: asking upstream is now realistic**, because the author has built a plugin host.
   The useful requests, smallest first:
   - a log line when a plugin starts or stops;
   - an "external automation active" signal that UCM respects, such as a lock file under `Saved/`;
   - later, a documented local IPC permission for out-of-process hosts.
4. **Do not port to Lua now.** A port would drop the player's real Decal plugins and Virindi Tank's
   UI, and would only duplicate UCM. Reconsider only if upstream adds IPC.
5. **Update `README.md` and `docs/ac-unreal-integration.md`.** They should say that the client has
   had a sandboxed Lua plugin host since release 94, and link to this note.

## 8. Follow-up: the Desktop UI Scale and the plugin bar

This was looked at later on 2026-10-06, the same way as the rest:
- strings and disassembly of release 96's `ACUnreal.exe`;
- the client's `Saved/Config/Windows/GameUserSettings.ini` and `Saved/ClientPlugins/settings.json`.

Both were only read. The addresses are for this build only.

### 8.1 The Desktop UI Scale

**Where it is kept.**
- **Seen**: the option is `DesktopUIScale` under `[ACE.Presentation]` in `Saved/Config/Windows/GameUserSettings.ini`. The player's is `DesktopUIScale=1`.
- **Seen**: the client's settings table (0x151161CF0) gives it a default of 1, a least of 1 and a most of 3. The reader (0x1499DB490) clamps a finite value to that range.

**What it scales.**
- **Seen**: the help text reads "Scales the desktop interface in 25% steps. Limited to fit the window; VR uses its own panel scale."
- **Seen**: the test names include "UI scale offers nine clear quarter steps", "The actual viewport DPI doubles all child widgets", "Scaled viewport keeps the entire layout in bounds" and "Physical mouse coordinates still activate scaled UI controls".
- **Seen**: the pak's `DefaultEngine.ini` flattens Unreal's own DPI curve, so it is 1.0 at every size. The option is therefore the only scale.

**How large it draws.** **Inferred** from disassembly (0x1499D4760):
- In a W by H viewport, the client draws at the smaller of two values:
  - the largest quarter step at which an 800 by 600 layout still fits (the smaller of W over 800 and H over 600, rounded down to a quarter), but at least 1;
  - the chosen scale, rounded to the nearest quarter step.
- The client's own tests give the same answers at 200%: 2.0 at 3840 by 2160, 1.75 at 1920 by 1080, and 1.0 at 800 by 600.

**The character select.**
- **Inferred**: the scale is the whole viewport's DPI, so the 800 by 600 character select is drawn that much larger and centred. At 175% in 1920 by 1080 it is 1400 by 1050, at 260,15.
- **Not yet seen live.**

**What our stack does.**
- The host reads the option and the click carries it.
- The overlay applies the rule above (`native/ACUnrealOverlay/client_ui.h`).
- The overlay's own drawing does not change: the option changes neither the window, its swap chain nor Windows' DPI, and the overlay sizes itself at a fixed 1.5.

### 8.2 The plugin bar

**Where it starts.** **Inferred** from disassembly of `SACEPluginDesktop` (`ACEPluginDesktop.cpp`):
- The plugin desktop is an `SConstraintCanvas` over the viewport.
- The bar is an `SPluginFrame` holding a scrolling column of buttons.
- Its position is read through the routine that reads `_window_positions` (0x149F53220). The key is `__bar`, a static string set at 0x14136F990, and the default is (8, 80).
- The size passed beside it is (62, 120).
- Moving the bar saves its position under `__bar` (0x149F4F830).

These are interface units, which the UI scale multiplies.

**Seen** in the live test's screenshot of 2026-10-06 (`l6-01-after-login.png`, from the Virindi Tank repository's `docs/live-tests/live-2026-10-06.md`; half size, in the world at 100%):
- The bar is a column of four buttons: the plugin list's "+", "LOOT EDITOR", "NAV HIDDEN" and the map's globe.
- It runs from about x 8 to 70 and y 80 to 250 in the 1924 by 1083 window. So it starts where the code says, and grows with its buttons past the 120 it is made with.
- UCM, disabled, has no button.
- At the character select there is no bar (the same test's screenshot of the character select, not published: it shows the character's name).
- The host therefore takes the bar as 62 wide and 214 high, which is room for a fifth button: UCM's, once enabled.

**What it overlaps:**
- **Decal's bar along the top** cannot meet it at any scale. It starts at y 4, and 27 at the bottom, from x 158; the player's is at y 10 to 33.
- **VVS's bar** does overlap it.
  - VVS starts its bar at (0, 52), 20 wide, down the left edge. With one plugin's square it reaches y 80 and covers x 8 to 20 of the plugin bar.
  - The player's VVS bar is at (0, 15), 20 by 236, in the overlay's ini. **Seen** in the same screenshot: its icons are drawn over the left edge of all four of the client's buttons. Those buttons are still clickable to the right of x 20.
- **Decal's bar docked left** (not the player's choice) would overlap it too.

**What our stack does.**
- Two of our bars start 4 pixels below the plugin bar wherever they would overlap it:
  - VVS's bar, when it starts where VVS starts it;
  - Decal's bar down a side, when Decal's registry has no place for it.
- The host sends the bar's place and the scale in the snapshot (`client_ui`). It reads them from `__bar` if the player has moved the bar, and otherwise uses the default.
- Positions the player has saved are never moved, so the player's VVS bar stays at (0, 15) and still covers that strip. To clear it, the player can drag either bar: VVS's with left Ctrl held, or the client's own.

**Live checks still needed:**
1. With UCM enabled, the bar's height with five buttons: is 214 enough?
2. The Desktop UI Scale raised to 150% or 200% at the character select, then `ctl login`, to confirm the click lands. Put it back afterwards.
3. A fresh overlay ini, or a player without one, at 100%: the VVS bar should start at (0, 298), below the client's bar.
