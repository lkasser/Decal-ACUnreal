# The plugin host — a Decal for clients that can't be injected

`src/AC.Host` plays the role Decal played for the retail client: it owns the
connection to the game, keeps a model of the world, and hands plugins a stable
API to read it and act on it. It does this **out of process**, on
top of the UDP relay, because AC:Unreal offers nothing to inject into.

Its first plugin, VirindiTank, is a repository of its own (VirindiTank-ACUnreal), which
builds against this one as a git submodule; this page names it often, as the plugin that
exercises the most of the host.

## Shape

```
  AC:Unreal  <-- UDP -->  ProxyTransport (relay)  <-- UDP -->  ACE / GDLE
                               |
                               |  AcMessage (opcode + payload), per direction
                               v
                        GameHost game thread
                               |
                    MessageDecoder.Apply(message, world)
                               |
                          WorldState  --events-->  plugins (IPlugin)
```

- **Transport** (`IGameTransport`): where messages come from. `ProxyTransport`
  is the live relay; `CaptureTransport` replays a recorded session through the
  identical pipeline. Plugins cannot tell which they are on.
- **Decoders** (`MessageDecoder`): turn server-to-client messages into changes
  to `WorldState`. Every layout is transcribed from ACE's message writers.
- **World** (`WorldState`, `WorldObject`, `CharacterState`): what is known.
  Mutated only on the game thread; raises an event per kind of change.
- **Host** (`GameHost` implements `IHost`): one game thread, a queue, plugin
  lifecycle, event fan-out with exception isolation.
- **Plugins** (`IPlugin`): loaded from `plugins/<Name>/<Name>.dll` by
  `PluginLoader`, each in its own load context with the contract assemblies
  shared from the host.

## The contract a plugin sees

```csharp
public interface IPlugin
{
    string Name { get; }
    void Startup(IHost host);
    void Shutdown();
}

public interface IHost
{
    IWorldView World { get; }          // objects, containers, server name
    ICharacterView Character { get; }  // level, skills (with base formula), attributes,
                                       // vitals, position, motion
    IGameActions Actions { get; }      // appraise, use, move items, drop, speak,
                                       // walk, turn, stop, cast, stance, query health,
                                       // attack, shoot, cancel an attack, wield
    IGameInput Input { get; }          // the game's movement keys, held down through
                                       // the overlay - how a plugin actually walks;
                                       // Input.Unheeded: held 3 s, no word from the client
    GameWindowState GameWindow { get; }
                                       // minimized, drawing, parked off-screen in place
                                       // of minimized - as the overlay says
    IPluginLog Log { get; }
    ChatCommandOutcome RunChatCommand(string text, IPlugin from);
                                       // a line as though typed: other plugins'
                                       // commands, then the client's own ("/f", "/ls")
    string GetSetting(IPlugin plugin, string key);      // "--set Name:Key=Value"
    string GetDataDirectory(IPlugin plugin);            // %LOCALAPPDATA%\ACHost\plugins\Name
    void RunOnGameThread(Action action);

    event EventHandler<TimeSpan> Tick;                  // ~100 ms
    event EventHandler<string> ServerConnected;
    event EventHandler<uint> PlayerIdentified;
    event EventHandler<string> LoggedOff;               // the character left the world, and why;
                                                        // the next PlayerIdentified is a new login
    event EventHandler<WorldObject> ObjectCreated;
    event EventHandler<WorldObject> ObjectUpdated;
    event EventHandler<WorldObject> ObjectAppraised;
    event EventHandler<uint> ObjectRemoved;
    event EventHandler<ContainerContents> ContainerViewed;
    event EventHandler<ChatMessage> ChatReceived;       // ChatLines.Format writes it as the
                                                        // retail chat window did
    event EventHandler CharacterUpdated;
    event EventHandler<uint> UseFinished;               // an action finished; 0 = it worked
    event EventHandler<uint> ContainerClosed;           // a container on the ground closed
    event EventHandler<MoveRefusal> MoveRefused;        // the server would not move an item
                                                        // (InventoryServerSaveFailed); its error
                                                        // is often 0
    event EventHandler<uint> AttackFinished;            // an attack or one swing of it ended
    event EventHandler<DamageDealt> DamageDealt;        // a blow the character struck landed
    event EventHandler<string> TargetEvaded;            // ...or was evaded, by this creature
    event EventHandler<bool> PortalSpaceChanged;        // into (true) or out of portal space
    event EventHandler<uint> VendorChanged;             // a vendor's window opened, or 0: closed
    event EventHandler<string> Died;                    // the server's death message
    event EventHandler<GameMessageEventArgs> MessageSeen;
                                                        // every message either way, as opcode
                                                        // and bytes: what Decal plugins'
                                                        // ServerDispatch is made from
}
```

**`UseFinished` is not a convenience.** The server processes one action at a time
and drops the rest without saying so, so anything that sends more than one action
has to wait for this between them. It carries the server's error, or zero.

An attack is the exception: the server answers it with `AttackFinished`, never
`UseFinished` - zero between the swings of an attack it is repeating by itself,
`ActionCancelled` when the attack is over.

**Threading, in one sentence:** every callback arrives on the one game thread,
in order, after the world has been updated — so a plugin never sees a torn
world and needs no locks, and a plugin that blocks stalls everyone.

**Isolation:** a handler that throws is logged and that event skipped for that
plugin. The plugin stays loaded; the host never lets the exception out.

**Transport independence is deliberate.** Nothing in `IHost` mentions packets,
ports or ISAAC. If AC:Unreal ever ships a native API, the host gets a new
`IGameTransport` and every plugin keeps working unchanged.

## What the world model holds

A `WorldObject` has two layers, and the distinction matters:

| Layer | Source | Present for |
|---|---|---|
| Typed creation fields (`Name`, `ItemType`, `Value`, `Burden`, `ContainerId`, `StackSize`, `Workmanship`, `Location`, palettes...) | `ObjectCreate` / `UpdateObject` | every visible object |
| Property dictionaries keyed by **server property id** (`Ints`, `Floats`, `Strings`, `Bools`, `Int64s`, `DataIds`, `InstanceIds`) | identify responses, property updates | after an appraisal, or as the server pushes them |
| Appraisal profiles (weapon, armour, armour levels, creature) and `SpellIds` | identify response | after an appraisal |

The server's property ids are the same numbers Decal used for its low-range
value keys. That is why the VirindiTank adapter can pass a Decal key through as
a dictionary lookup, and only Decal's *computed* keys (≥ `0x0D000000`) need
hand-mapping to typed fields.

`CharacterState` carries skills as the server describes them (ranks,
advancement class, starting bonus) and computes each skill's base value with
the game's attribute formula table.

## When objects go

The world holds what the client holds, which Decal plugins saw as the client's own
object list. `ObjectRemoved` (Decal's `ReleaseObject`) is raised for each of these:

- **Deleted**: `ObjectDelete` - a creature's corpse appearing, a spell exploding.
- **Picked up by someone else**: a `PickupEvent` for an item on the ground that nobody
  the host knows holds. ACE takes it out of the world and forgets that this client knew
  it. The character's own pickup is not this: its container is set first, and the item
  stays, off the landscape.
- **Out of view for 25 seconds**: ACE sends nothing when an object goes out of sight. It
  forgets it 25 seconds later (its `ObjectMaint` destruction queue, the client's own
  rule), and creates it afresh if it comes back. In view: outdoors, the character's
  landblock and the eight around it; indoors, the cell's own list of visible cells, and
  the landblocks around if the cell sees outside - read from `client_cell_1.dat` beside
  `client_portal.dat` (without it, indoors keeps everything within a landblock). A
  teleport is the same: whatever cannot be seen from the destination goes 25 seconds
  later. The character, what it carries and wears, the contents of containers and what
  creatures hold never go this way; a creature's weapon goes with the creature.
- **The character leaving the world**: everything, after `LoggedOff`.

## What is decoded today

Server-to-client:
`ObjectCreate`, `UpdateObject`, `ObjDescEvent`, `ObjectDelete`, `PlayerCreate`,
`ServerName`, `InventoryRemoveObject`, `SetStackSize`, `PickupEvent`,
`UpdatePosition`, private/public `UpdatePosition`, all fourteen private/public
property updates (int, int64, bool, float, string, data id, instance id),
`PrivateUpdateSkill`, `PrivateUpdateAttribute`, private/public `UpdateVital`,
`PrivateUpdateAttribute2ndLevel`, `HearSpeech`, `HearRangedSpeech`,
`ServerMessage`, `EmoteText`, `PlayEffect`, `Sound`, `VectorUpdate` (velocity, on
the object), `SetState` (physics state, on the object), `ParentEvent` (what is wielded
by whom).

Inside `GameEvent`:
`PlayerDescription` (properties, attributes, vitals, skills), `IdentifyObjectResponse`
(every table and profile), `ViewContents`, `InventoryPutObjInContainer`,
`WieldObject`, `UpdateHealth`, `Tell`, `CommunicationTransientString`,
`WeenieError`, `WeenieErrorWithString`, `UseDone`, `CloseGroundContainer`,
`MagicUpdateEnchantment`, `MagicRemoveEnchantment`, `DefenderNotification`,
`EvasionDefenderNotification`, `KillerNotification`, `SetTurbineChatChannels`,
`InventoryPutObjectIn3D`, and - from ACE's writers, not yet from traffic - `AttackDone`,
`AttackerNotification`, `EvasionAttackerNotification` and `CombatCommenceAttack`.

Client-to-server — only movement, because the server does not echo a player's own
movement back to them, so the client's account is the only one there is:
`AutonomousPosition` (where the client says it is) and `MoveToState` (what its body
is doing). Everything else the client sends is its own business, and the server's
reply says what came of it.

Everything else is counted and reported as *ignored* by `achost`, most frequent
first — that list is the worklist. A decoder that rejects real bytes is counted
as *malformed* by opcode, which is the other worklist, and the capture that
produced it is the test case.

Three of these share a single opcode, so an opcode count says nothing about what
is being missed. `achost` counts them separately: ignored game events, and client
actions sent.

## Finding out what an id means

Two problems, two tools.

*What is this id?* The ACE server on the same machine carries the enums, so
`tools/enumdump` reads them rather than trusting memory:

```sh
dotnet run --project tools/enumdump -- "C:\ACE\Server" GameEventType
dotnet run --project tools/enumdump -- "C:\ACE\Server" GameActionType
dotnet run --project tools/enumdump -- "C:\ACE\Server" GameMessageOpcode
```

*What is its layout?* `--dump-action <hex>` and `--dump-event <hex>` print the
bytes of the first twenty of one type, which is enough to read a layout off real
traffic instead of inventing one:

```sh
achost replay session.acap --no-plugins --dump-action 0xF753
achost replay session.acap --no-plugins --dump-event 0x01C7
```

Both were used for everything decoded here, which is why the tests carry captured
hex strings rather than bytes from a builder. A test builder that shares the
decoder's assumptions agrees with it and proves nothing: four layout errors in the
motion decoder survived a full set of passing tests that way.

## What is not there yet, stated plainly

- **Injected movement toward the server.** The message is right — a composed walk
  is byte for byte what the client sends — but in Asheron's Call the client decides
  where its own character is and tells the server, and the next position it
  reports undoes anything the server was told otherwise. So plugins walk with
  `IHost.Input` instead: the overlay presses the game's own movement keys (see
  "Walking" below). `IGameActions.WalkForwardAsync` and friends remain for what
  they can do - show a motion to others - and `--test-move` for asking the question
  directly.
- **Typed plugin commands.** AC:Unreal's executable says a line it does not handle
  itself goes to the server as Talk ("Server commands continue through Talk"), and ACE
  says aloud any Talk not starting with `@`. So the relay takes such a Talk out of the
  client's packet when its first word is a plugin's (`IChatCommands.CommandWords`, plus
  the setting `Decal:CommandWords`), renumbering the fragments after it down by one - the
  mirror of injecting one - and the host gives the line to the plugins
  (`ClientStreamRewriter.Withhold`, `ITypedCommandSource`). No capture has a chat line in
  it yet, so whether AC:Unreal keeps the `/` or turns it into `@` is unseen; both are
  caught. A withheld line is not in the capture file either.
- **Buffed skill values.** Enchantments are decoded and tracked, but not yet
  *applied*: a buffed skill still reads as its base value. (An item's `ActiveSpellCount` is the
  spells its appraisal lists as on it now.) Applying them needs the stat-modifier key numbering,
  and only three keys have been observed - not enough to map.
- **Attacks the character makes, as seen on the wire.** The recorded sessions are of
  a character that fights with spells, so none has a melee or missile attack, a wield,
  an untargeted cast, or the events that answer an attack. Those are composed and
  decoded in ACE's handlers' and writers' order - the server's actual code, and the
  defender's notification, which was captured, matches its writer word for word - but
  the first session with a sword or a bow in it is the test they are still owed.
- **A control vocabulary on the wire.** Panels are tables of strings. Real uTank2 pages
  are checkboxes, sliders, dropdowns and edit boxes, and none of those can cross the
  contract yet. Matching those pages is a contract change, not a styling one.

## Enchantments and combat, and how their layouts were settled

Neither was decoded until a session contained some, and then both were read off the
bytes rather than out of a document. What made each convincing is worth recording,
because it is the standard the rest should meet.

**Enchantments.** The caster field lands on the character's own id for a self-buff
and on a creature's for a debuff. The degrade limit lands on -666, the sentinel the
retail client uses - a number with no business appearing anywhere else. And the
spell ids resolve in the client's own table: 5206 is "Surge of Protection", 1443 is
"Bafflement Other V", and the latter arrived from a creature with a -30 modifier
lasting 180 seconds, which is what a monster debuffing someone looks like. A third
id has no name in that table; every other field in its record is sound, so that is
recorded as a fact about the data rather than smoothed over.

**Combat.** A creature called "Inferno" reports damage type 0x10, which is fire.
The body part is between 0 and 9 in all thirteen samples, exactly the range the
server numbers them over. The one blow with the critical word set did 15 damage
where the rest did between 1 and 4, and the percentage tracked the amount at a
constant ratio throughout - which is what a fraction of one character's health
looks like, and puts that character's health near 371.

Neither of those is proof. Both are the kind of cross-check that catches a field in
the wrong place, which a passing test against a hand-built message is not.

**Deaths and stances.** Every kill in a recorded session went the same way: the
creature's health reported as nothing, the death message ("You slay Auroch
Yearling..."), a motion three or four messages later whose forward command is 0x11 -
the low half of ACE's `MotionCommand.Dead` - and the creature's removal as its corpse
appeared. Eleven kills, eleven such motions, and that command on nothing alive. The
character's own stance arrives as property 40, ACE's `PropertyInt.CombatMode`: in one
session it went 8, 1, 8, 1, each time the stance the client had just asked for - magic,
then peace.

## The ordering sequence, and what cannot be fixed from outside the client

Every client action carries an ordering sequence, and the client owns that counter.
An injected action cannot ask it for a number.

In a real session the client used 1 and 3 for `LoginComplete` while the host's own
appraisals used 1, 2 and 3. Duplicates, which ACE tolerated for a whole session —
luck rather than design. Injected actions now take a number above anything the
client has been seen to use, which is the most that can be done from out here. Doing
it properly would mean rewriting the client's subsequent sequences on the way past,
the way `ClientStreamRewriter` already rewrites fragment sequences; that is a real
option and it is not done yet.

## Client data

`--dat` points the host at `client_portal.dat`; without it, one is looked for in
the usual places. It is optional and never fatal - the host decodes everything
either way - but the protocol sends ids where a player sees words, so without it
a spell is a number and a palette slot is a number:

| From the DAT | Used for |
|---|---|
| Spell table (`0x0E00000E`) | `MySpell.Name`, so loot rules on spell names can match |
| Skill table (`0x0E000004`) | The game's own attribute formulas for skill values |
| Palettes (`0x04......`) | A representative colour per palette slot, for colour rules |

The archive is a block-allocated store indexed by a B-tree, so a file is a chain
of blocks whose first four bytes point at the next, and the index is stored the
same way. `AC.Dat` reads it whole at open - about 80,000 files on the portal
archive - and caches palettes on demand.

One honest caveat: a palette slot covers a run of colours and the host samples
the middle of it. That is stable and avoids the near-black and near-white ends
of a shading ramp, but it is not known to be the same sample the retail plugins
took, so a colour rule tuned against retail may not agree.

## The window, and what it cannot be

Decal's panels were drawn inside the game, by injecting into the client's own
rendering. That route does not exist here: AC:Unreal exposes no plugin API and loads
no Decal, so there is no way to put a panel in the game's own frame. Anyone expecting
the VirindiTank window to appear over the game the way it used to is expecting
something this approach cannot deliver, and saying so plainly is better than
implying otherwise.

What can be done is a window of our own, kept on top of the game: `src/AC.Host.Ui`,
built as `VirindiTankWindow.exe`. It runs the same host and loads the same plugins
from the same `plugins\` directory; only the reporting differs. Tabs for status,
loot decisions, combat, buffs, chat and the log.

Host events arrive on the game thread, so every one of them is marshalled onto the
UI thread before touching a control. The loot plugin's decisions are picked up
through reflection on its `DecisionMade` event, so the UI has no compile-time
dependency on VirindiTank - a second plugin exposing the same event would be
reported the same way.

### Text in the game's own chat

There is one thing that *does* appear inside the game: a line of chat. The injection
that carries actions to the server works just as well pointed the other way, because
nothing in it is direction-specific - it recovers the checksum key from whatever
packet it is given and renumbers that stream's fragments. So the same rewriter runs
on the server-to-client path, and `IHost.ShowInGame(text, chatType)` puts a line in the
game's chat window as a `ServerMessage`: the text exactly as given, in the chat type named -
which is what Decal's `AddChatText` called a colour, so a Decal plugin's
`AddChatText("[VGI] ...", 5)` arrives as written, in colour 5. Decal's target window cannot
be named in a `ServerMessage`; the line goes wherever the player's chat settings send its type.

It arrives as though the server had sent it, which it did not. So it says whose it is
rather than being disguised - Virindi Tank's lines "[VTank] ", a Decal plugin's its own,
and the host's, through `IHost.ShowInGame(text)`, "[Decal] " - and nothing should use it to
imitate the server or another player: the point is to report, and a report that cannot be
told from the game's own voice is worse than no report.

VirindiTank uses it for decisions that keep something and for what it actually took.
`NoLoot` decisions are not worth a line. `VirindiTank:Echo=false` turns it off.

It needs `--enable-actions`, because it is the same machinery; without that the host
cannot reach the client at all and `ShowInGame` returns false rather than pretending.

## The injected overlay, and the shape Decal had

The window beside the game is one answer; the other is drawing inside it. `native/`
holds a C++ DLL that is injected into `ACUnreal.exe`, hooks its D3D12 swap chain, and
draws with Dear ImGui in the client's own frame. How that works, and what was read off
the client rather than assumed, is in `native/docs/d3d12-overlay-design.md`.

What it draws is the shape Decal had, which took three attempts to get right: **a bar
with one entry per plugin, and a window per plugin**. The host's own furniture - status,
the staleness warning, the notice - is in the host's window; VirindiTank's panels are in
VirindiTank's. The bar folds, and the two things that must never be hidden - a non-zero
malformed count and a stale snapshot - sit outside the fold.

The contract between the host and the DLL is `native/ACUnrealOverlay/overlay_state.h`,
kept deliberately narrow: a declared window per loaded plugin carrying that plugin's
controls, generic panels of strings, and commands back - every one of them stamped with
the plugin it belongs to. The DLL knows nothing about loot, spells or the wire, and names
no plugin: VirindiTank's switches are drawn because VirindiTank declared them, the same
way any other plugin's would be. That is what lets it survive changes on the host side
untouched. The colours were sampled from VirindiViewService's own theme images on this
machine; none of Virindi's artwork is copied.

### Panels in, clicks out

A plugin puts tables on screen by implementing `IOverlayPanels` and returning them when
asked - several times a second, on the game thread, so it holds no display state. A
plugin hears clicks by implementing `IOverlayCommands`. The host stamps every panel with
the plugin that produced it and routes every command to the plugin whose window it came
from and no other; a plugin never sees another plugin's clicks, and a click nobody
answers is counted and said rather than lost.

Every loaded plugin gets a window and a place on the bar, whether or not it has anything
to show - Decal listed every plugin it had loaded, not only the busy ones. A plugin whose
window needs switches implements `IOverlayControls` and returns them the same way, built
from `OverlayControl`'s factories:

```csharp
public IReadOnlyList<OverlayControl> GetControls() => new[]
{
    OverlayControl.Toggle("loot", "Pick loot up", _taker != null),
    OverlayControl.Slider("range", "Range", _range, min: 1, max: 40, step: 0.5),
    OverlayControl.Choice("mode", "Mode", _mode, new[] { "Melee", "Missile", "Magic" }),
    OverlayControl.Text("name", "Name", _name),
    OverlayControl.Button("reload", "Reload profile") with { Tooltip = "Read the profile again." },
};
```

A change comes back through `IOverlayCommands` as `"set"` with the control's id in
`ControlId` and the new value in `Value` (`OverlayControl.ParseToggle` and
`TryParseSlider` read it), or `"press"` for a button. The overlay keeps none of it: each
control shows what the plugin last said, so a change the plugin refuses shows as the
control springing back. A slider or text box sends once, when the player lets go, rather
than for every value it passes through. A plugin that throws while declaring its
controls stays on the bar with its name in red, and the host log says why.

The host publishes on its tick, and only when something changed - with a heartbeat once
a second when nothing has, because an idle host that sends nothing is indistinguishable
from a dead one, and the overlay once declared a healthy host stale on exactly that
basis.

### Decal views, in the Decal theme

A plugin can go further than controls and panels and ship a **Decal view** - the same view
XML a Decal plugin ships - by implementing `IOverlayView`. The host parses it into named,
typed controls (`DecalView.Parse`; `view.Checkbox("cEnableLooting")`, `view.List(...)`, and
so on, each with the events Decal.Adapter's wrappers had), publishes it with its current
state, and routes the overlay's clicks back to the control's event on the game thread. The
overlay draws it as Virindi View Service's **Decal theme** drew it: parchment, black Times
New Roman, lamp checkboxes, wood tabs, the client's own scrollbars and sliders. The theme's
every image id, colour and metric was read out of `VirindiViewService.dll`'s own
`Decal_Theme` class.

VirindiTank uses this to show **Virindi Tank's own window**: it reads uTank2's
`mainView.xml` out of the `utank2-i.dll` Decal has registered on the machine and shows it
page for page. "Enable Looting" drives the looter; every other setting is remembered across
sessions, and a feature not built yet says so in the log the first time it is used.
`VirindiTank:ViewSource` points at a different `utank2-i.dll`.

The artwork is never copied into this repository. The host finds Decal, Virindi View
Service and the client from Decal's own registry entries (`DecalInstall`), resolves each
image by key (`ImageCatalog`: `portal:0600126F` from the client's portal.dat, `vvs:...` from
VVS's embedded theme images, `decal:...` from Decal's folder, and `bar:open|closed|faulted`,
Decal's bar switches made from its own bitmaps), and sends each image to the overlay once
per connection. It says at startup what it found; a missing image draws as a plain stand-in
and is named in the log.

### Settings that persist

`IHost.LoadSettings<T>` gives a plugin one typed settings object, kept as
`settings.json` in its own directory. Change it and call `Save`; the host saves again at
shutdown regardless. An old file missing a new property leaves it at its default; a file
that is not JSON is set aside rather than overwritten. `--set` still overrides any of it
for one run. VirindiTank keeps looting, echo and its profile there, which is what makes
the overlay's switches mean the same thing next time.

### Decal's window

The first window on the bar is Decal's own: the host's, not a plugin's, so it is always
there and knows nothing about any particular plugin. Its Plugins page lists every plugin
installed, each with a lamp to switch it on or off and a Reload that picks up a rebuilt
copy; a plugin switched off stays off next time (`%LOCALAPPDATA%\ACHost\plugins.json`).
Its Options page holds **Let plugins act in the game**, the switch that decides whether
any plugin may act at all, and the session's server, character, position and traffic.

Plugins run from a copy of their folder, in a load context that can be unloaded, so the
folder itself is never locked: rebuild a plugin, press Reload, and the new one is running
with the session still connected. The old copy is shut down and cut loose from every host
event even if it forgot to unsubscribe (the log says how many handlers it left behind).

Beneath DecalCompat the page lists Decal's own plugins, one row each as DenAgent listed them:
the name Decal's registry gives it, its lamp, its version and a status - running, off,
replaced by this host, cannot run here, failed. Click a row and the line under the list says
why in full. "Find New" also reads Decal's registry again.

### Decal's own plugins, from Decal's registry

DecalCompat (`src/Decal.Compat`) finds Decal plugins where Decal did: every entry under
`HKLM\SOFTWARE\Decal\Plugins` (the 32-bit view, WOW6432Node, since Decal is 32-bit), with its
Enabled tick, Path, Assembly and Object. It also still runs any plugin put in its own folder
(`plugins\DecalCompat\Decal Plugins` in the data folder, or `DecalCompat:Folder`), with no registry
entry: each DLL there - `<name>\<name>.dll`, every DLL of a folder not laid out so, or one lying in
the folder itself - is read as metadata, and one with a class derived from Decal's PluginBase or
FilterBase is a Decal plugin (`DecalPluginCatalog.FindInFolder`). Such a plugin runs where it is,
but its DLLs are treated as a registered plugin's working copy has them (below) - its calls of the
player's folders, the XML serializer, Decal.dll and the registry pointed at this host's - and one
whose calls that changes runs from a treated copy (`DecalPluginLoadContext`). Mag-Filter is installed
there (below). `DecalCompat:Registry=false` leaves the registry alone; `DecalCompat:Skip` names
plugins to leave out.

Every registered entry is listed; which are loaded, and why not:

| Entry | What happens |
|---|---|
| Virindi Tank, Virindi HUDs, Virindi Hotkey System, Decal Hotkey System | **Replaced by this host**, never loaded: the host's VirindiTank plugin, the HUDs it hosts, and the two hotkey windows in `HotkeyWindows.cs` do their jobs. Known by class id, assembly file or (for the native DHS) name (`Replacements.cs`). Their lamp shows Decal's tick and cannot be switched. |
| A native (COM) plugin, a missing file, a file that is not .NET or holds no Decal plugin | **Cannot run here**, with the reason; DenAgent's version comes from the COM server's file. |
| The same assembly as one in DecalCompat's folder, or a second entry for the same file | Not loaded twice: the folder's copy, or the first entry, runs. |
| Anything else | Loaded when ticked, and its status says whether it started and, if not, exactly why. |

**The player's choice is DecalCompat's, not Decal's.** A plugin follows Decal's own tick until
it is ticked or unticked in the Decal window or the Agent; then the choice is kept in
`plugins\DecalCompat\settings.json` by class id, and only while it differs from Decal's.
Decal's registry is only ever read.

**The install is only read, by the host and by the plugin.** A registered plugin runs from a
working copy of its install folder (`plugins\DecalCompat\Registered\<folder>-<hash>`,
`WorkingCopy.cs`), and is told that is its folder: its DLLs are brought up to date from the
install each time it loads, with the x86-only mark cleared and 32-bit address arithmetic widened
(`PointerRewrite.cs`: the System.Data.SQLite 1.0.61 Virindi's tools ship read every blob through
`IntPtr.ToInt32`, which overflows in this 64-bit process), and its other files are copied the
first time only, so what it writes - settings, rules, error logs - stays in the copy. Archives,
files over 32 MB and large subfolders are left behind (Mag-Tools is registered in a downloads
folder beside the client's installers). Its calls this 64-bit .NET host answers otherwise than
Decal did go to stand-ins in `Decal.Adapter.Hosting` (`CallRewrite.cs`): `Environment.GetFolderPath`
to `PluginFolders` - the player's own Documents, as under Decal, unless `DecalCompat:UserFolders`
names a folder to stand in for them, as the tests do; `new XmlSerializer(type)` to `PluginXml`,
which can serialize a `List<>` of the plugin's own type (.NET's own writes that code where nothing
may name a type that can be unloaded); and a P/Invoke of Decal.dll's `DispatchOnChatCommand` to
`DecalNative`.

**Why a plugin is unwell** is gathered as it happens and shown as its status and reason
(`DecalFailure.cs`):

- a failure to load or start is read as what it needed - an assembly (`Decal.Interop.*`,
  Managed DirectX beyond its maths, `VTClassic`), a type or member the stand-ins lack, a
  32-bit native DLL,
  a registry read through the 64-bit view (now rare: a registered plugin's working copy has its
  HKLM reads pointed at the 32-bit view, `RegistryRewrite`);
- a handler that throws later (`DecalRuntime.Faulted`), an exception the plugin catches and
  reports in chat (`DecalRuntime.ChatShown`), and a native DLL it was refused
  (`DecalPluginLoadContext.NativeProblems`, such as a 32-bit `sqlite3.dll`) make it "running, with
  errors", with the first three reasons;
- a **message box** a plugin shows on the game thread (Virindi's plugins report their own
  exceptions that way) is read, logged, moved off screen and answered with its least harmful
  button at once (`PluginDialogs.cs`), instead of stopping the game thread until someone finds it
  behind the game. `DecalCompat:ShowMessageBoxes=true` lets them show.

**A plugin switched on while the host runs** - ticked in Decal's window mid-session, the way
a player turns one on here - is told what the others were told as they started
(`DecalRuntime.CatchUp`): FilterInitComplete, ServiceInitComplete and PluginInitComplete, then,
with the character in the world, its Login and LoginComplete, to its own handlers alone. Decal
loaded plugins as the client started, so every plugin heard these, and one that misses them waits
for ever: Mag-Tools makes its window on PluginInitComplete and starts its timers, hotkeys and
status rows on LoginComplete.

**Stand-ins for Virindi's other assemblies.** Besides Decal.Adapter, VVS, FileService,
Decal.Interop.Core and Managed DirectX's maths, a Decal plugin that names one of these is given:

- `VirindiHotkeySystem` (`src/VirindiHotkeySystem`): a hotkey a plugin adds is one of the host's,
  listed in the hotkey windows as "vhs/<owner>/<name>" with the key it was made with, and a press
  raises its `Fired2` (Decal Compat is an `IOverlayHotkeys`). A stopped plugin's go with it.
- `VirindiHUDs` (`src/VirindiHUDs`): `StatusModel.UpdateEntry` puts a row on the Status HUD Virindi
  Tank shows, through `IHost.UpdateStatusRow` and `IStatusRows`. Loaded only for a plugin that names
  it, since Virindi Reporter and Sense feed it whenever they find it, and Virindi Tank shows
  Reporter's rows itself.
- `uTank2` (`src/uTank2`): Virindi Tank's API for other plugins, answering as Virindi Tank did with
  no loot profile loaded - no item needs an ID, no rule keeps anything, its world tracker has
  nothing - with the loot types forwarded to the host's own (`UTank2.Abstractions`). Loaded only
  when asked for, since Integrator2 and Item Tool look for it among the loaded assemblies.

**Decal's window.** `Decal.Hwnd` is a message-only window of the host's own (`ClientWindow`), not
the client's, which is another process's and not a plugin's to move, take the frame off or close.
Keys a plugin posts to it - WM_KEYDOWN and WM_KEYUP - are pressed in the game by their virtual-key
codes through the overlay (`IGameInput.HoldKey`), in order, each as it arrives, while plugins may
act; changes posted together go 50 ms apart (`ClientWindow.Apart`) so the game sees each, on the
host's timer between ticks. "/mt jumpw 100" holds space about 150 ms, where one change a tick held
it 300. Mouse clicks, at places on the old client's panels, and a request to close it are dropped
and said once. Two things are read for what they meant instead:

- **A line typed into the chat** - Enter, keys that type (letters, digits, space, the punctuation
  keys, Shift), and Enter, its closing Enter waited for up to 20 ticks - is run as the chat box ran
  a line (`HooksWrapper.InvokeChatParser`): offered to the Decal plugins, then the host's plugins'
  commands, the game's commands and speech. Its keys are never pressed, so it works with the game
  minimized. Enter alone, and keys with no Enter after them in time, are still keys.
- **The old client's character select, clicked**, with the session at the character list: a click
  in its list of characters (x 42 to 202, y 209 to 532, as the retail client had it) chooses the
  character on that row - the list's height shared among the account's slots
  (`IWorldView.CharacterSlots`, Decal's `CharacterFilter.CharacterSlots`), the characters counted by
  name as the old client and Mag-Filter count them - and a click on its Enter (239,289, 211 by 211)
  enters the world as the one chosen through the host, `IGameActions.EnterWorldAsync`, which `achost
  ctl login` uses. With no row clicked, the one chosen is the character the client last asked to
  enter as, else the last in the world, as the retail select had it. Enter clicked again within 50
  ticks asks nothing more: a plugin retrying a login clicks it five times a second. No password is
  involved: the account is already at its character list.

Lines plugins put in the chat go as a message from the server. A link the old client drew -
`<Tell:IIDString:id:name>text<\Tell>`, round Mag-Tools' and Virindi Tank's item lines - goes as its
text alone (`ChatMarkup`): AC:Unreal's chat has no links, so it cannot be clicked.

#### Mag-Tools

Mag-Tools (2.1.6, `MagTools.dll` in the Mag-Tools install folder) is registered with Decal and off in its list;
ticked on in Decal's window here it starts, shows its window, binds its hotkeys (One Touch Heal,
Maximize and Minimize Chat), puts its fourteen Status HUD rows up and says "Plugin now online".
Its settings, logs and inventory file stay where Decal's copy of it kept them, in
`Documents\Decal Plugins\Mag-Tools`. "/mt" is a known command word, so a line the player types for
it is kept from the server. Its status reads "running, with errors" for one reason, which it says
in chat at login: its inventory packer could not load `VTClassic` (below).

Before, it failed four ways: switched on mid-session it never heard PluginInitComplete or its
login, so it made no window and started nothing; its login handler needed the real Virindi Hotkey
System; its HUD the real Virindi HUDs; and its inventory logger's `XmlSerializer` of
`List<MyWorldObject>` threw at every login. Its looter - on by default for chests and corpses -
asked the real Virindi Tank about every open container, ten times a second, into its error log and
the chat, for as long as the host ran.

The player's metas send it 588 commands (the Virindi Tank repository's `MetaCorpusTests`). Each, tested end to end against the
real DLL in `MagToolsTests` where it can be:

| Command | Uses | Here |
|---|---|---|
| `use`, `usep`, `usel`, `useip` | 156, 90, 36, 24 | Works: the item by name in the packs or nearby (`closestnpc`, `closestportal` too), used; "X on Y" uses X on Y, the plugin's selection standing for the use |
| `face` | 89 | Works: the game's turn keys, through the overlay, to within 5 degrees (`Facing`) - Decal turned the client itself, to the degree |
| `fellow` | 54 | `recruit`, `disband`, `quit` work: ACE's fellowship actions. `create` (3) does not: it is a script of the old client's keys and mouse clicks on its fellowship panel |
| `send` | 39 | The keys - `enter`, `f4`, `f12`, `msg` letters - are pressed in the game, in order; what each does is AC:Unreal's. `enter`, `msg` and `enter` sent together are a line typed into the chat, and run as one (Decal's window, above) |
| `combatstate` | 22 | Works |
| `give`, `givep` | 19, 10 | Works: the whole stack |
| `jump`, `jumpw`, `sjump`, `sjumpw` | 1, 13, 1, 10 | Works: space held for the time, let go with W (and shift) down, through the overlay |
| `click` | 5 | Does not: mouse clicks at the old client's dialog, which the overlay cannot make |
| `castp` | 4 | Works: the spell by part of its name from FileService, cast at nothing if it takes no target, else at the character |
| `logout` | 4 | Works |
| `loot`, `lootp` | 4, 2 | Works: in the chest or corpse last opened (`Hooks.OpenedContainer`) |
| `autopack` | 3 | Not here: the packer - and its Pack Inventory hotkey - needs `VTClassic` from Virindi Tank's folder, which the host keeps inside its own Virindi Tank; Mag-Tools says so once at login, and the line goes on to the server, which knows no "mt". The player has no AutoPack profile, so it did nothing under Decal either |
| `drop` | 1 | Works |
| `selectp` | 1 | The client's selection cannot be set; the plugin's choice stands for what it uses next |

Its "Show Item Info On Ident" (Misc options, on by default) prints a line for each item the player
selects and has appraised: Decal raised `ItemSelected` for the client's own selection, and
`IdentReceived` for every appraisal, so it printed only what was appraised within 10 s of the player
selecting it. The host's own appraisals - Virindi Tank's mana upkeep of the worn gear - go on in the
client's packets, and the relay marks them as the host's (`ClientStreamRewriter.IsOurs`): they are
never the player's selection, and their answers are kept from the client. Before, every one of them
printed, and threw for armour, whose appraisal lists the spells on it with the top bit set, which
Decal gave as `ActiveSpell` and not as `Spell`.

The turns, keys and jumps need the overlay attached and "Let plugins act" on; without them they
are dropped and said once. What cannot work, and why: anything that moves, resizes or reframes the
client's window (Mag-Tools' window options), mouse clicks into it, and the client's own panels
(Hooks.UIElementRegion, Maximize/Minimize Chat) - all the old client's memory and windows; Virindi
Tank's loot rules for its looter, item info and packer, which this host's Virindi Tank does not
offer to Decal plugins (its looter still empties the character's own corpse, which asks no rule);
and its vendor and trade commands, whose hooks the host does not have.

#### Mag-Filter

Mag-Filter is Mag-nus's Decal network filter from Mag-Plugins: commands that choose the character to
enter the world as next, or by default, and that queue lines to type once a character has entered.
It is not installed on this machine; the player's metas send it fourteen lines. So it is built from
its own sources, unchanged, against these stand-ins (`third_party\Mag-Filter`: the sources are
fetched at a pinned commit, not kept here), and installed as a Decal plugin in Decal Compat's own
folder, with no registry entry and nothing written to the registry:

```powershell
tools\install-plugin.ps1 -Plugin MagFilter   # fetch if need be, build, and install into
                                             # %LOCALAPPDATA%\ACHost\plugins\DecalCompat\Decal Plugins\MagFilter
                                             # -Data for another data folder, -Notify to tell a running host
```

Its licence is Mag-Plugins' own, the **GNU LGPL 2.1**, not MIT; `license.md` is installed beside it
as `LICENSE.md`, with `third_party\Mag-Filter\README.md`, which names the commit. **It handles no
password**: at that commit it stores, reads and sends none - its logins only choose a character at
the character list the account is already at - and its build stops on any source that mentions a
password or credentials, so none can come in unread.

It starts as the filter it is ("Mag-Filter", running, from the folder), "/mf" is a known command
word, and it keeps its settings (`Mag-Filter.xml`) and error log in `Documents\Decal Plugins\Mag-Filter`,
as under Decal - its call of the player's Documents treated as a registered plugin's is. What it does
here, tested end to end against the built DLL in `MagFilterTests`:

| Command | Metas | Here |
|---|---|---|
| `lncbi set <n>`, `lnc set <name>` | 10 (`lncbi set 1` to `10`, StipendsIB.met) | Works. When the server next lists the account's characters - after a logout, as the meta's `/mt logout` asks - Mag-Filter clicks that character's row and Enter on the old client's character select a second later, and the host enters the world as it through `IGameActions.EnterWorldAsync`, `ctl login`'s path, which waits for its own logout to be done first. Counted from 0 by name, as Mag-Filter and the old client count them. Entering needs the overlay attached and the game not minimized (parked is fine) |
| `lmq add <line>`, `lcmq add`, `alcmq add`, `olcmq add`, and their `clear` | 4 (`lmq clear`, `lmq add /vt start`, `/vt meta load StipendsIB`, `/vt opt set enablemeta true`) | Works. Once the client has entered (its LoginComplete), Mag-Filter types each line - Enter, the line, Enter - and each is run as the chat box runs a line, reaching Virindi Tank's commands, minimized or not. As Mag-Filter types them: letters lower case, digits as spaces - "/vt meta load stipendsib", whose file is found all the same |
| `alcmq wait set`, `olcwait set`, `clear` | - | Works: the wait before the after-login lines |
| `dlc set`, `dlcbi set <n>`, `sdlcbi set <n>`, and their `clear` | - | Works: kept in its settings, by server and account; the character is entered through the host when the client first connects (the server's 0xF7EA), after two clicks that skipped the old client's movies, which are said and dropped |
| `cssmfps <n>` | - | Taken and kept, but there is no character-select frame to slow: it would only slow the host's own tick at the character list. Leave it 0 |

Also: when the server says a character is still in the world (CharacterError 13), Mag-Filter clicks
OK and Enter five times a second until the client asks to enter - the host enters as the character
the client had named, once, not again for 50 ticks. Its Esc at the login screen (FastQuit) has no
window messages to hear, and its OK and Yes clicks on the old client's dialogs are dropped and said.

What the player's registered plugins do here, as measured on this machine, is in
the Virindi Tank repository's `docs/virindi-parity-code.md` section 5.

### Driving a running host: `achost ctl`

Everything Decal's window does, from another command line, over a named pipe:

```bash
achost ctl status              # server, character, acting, and every plugin's state,
                               # Decal's plugins listed beneath DecalCompat; a "session"
                               # line when the host started with the game connected
achost ctl act on              # let plugins act; "act off" to stop them
achost ctl reload VirindiTank  # rebuild first, then this: no reconnect
achost ctl disable VirindiTank # and "enable"; remembered like the window's lamps
achost ctl disable "Virindi Sense"  # a Decal plugin, by the name Decal lists it under
achost ctl rescan              # load plugins installed since the host started, and
                               # read Decal's registry again
achost ctl windows             # every window, by the name view/set/press take
achost ctl view VirindiTank cEnable  # a window's controls and values, filtered
achost ctl set VirindiTank cOn true  # and press / click / page: as the player's clicks
achost ctl find Auroch         # objects by name: where each is, who holds it
achost ctl chat 20             # the game's last chat lines, as the host read them
achost ctl say /vt help        # a line typed as the player types one
achost ctl hotkey VirindiTank Toggle_MiniRemote  # a plugin's hotkey, as if pressed
achost ctl window              # the game window as the overlay says it - minimized,
                               # drawing, parked - and any keys held that it ignores
achost ctl window keep on      # minimizing the game parks it off-screen instead, where
                               # it goes on taking keys; Decal's Options page has it too
achost ctl characters          # the account's characters, numbered as login takes them
achost ctl logout              # log out to the character list, and say how it went
achost ctl login "Testchar I"  # enter the world as one: by name, 0x id or number;
                               # a last word keys or messages chooses the way
achost ctl exit                # exit as the tray icon's Exit does: settings saved,
                               # the session handed over to the next host
```

### Going to the character list and back: `ctl logout`, `ctl login`

Plugins (`IGameActions.LogOutAsync`, `EnterWorldAsync`), Decal's `Hooks.Logout` and `achost ctl`
can log the character out to the character list - with the game minimized or not - and enter the
world again, as the same character or another of the account's, with the game shown or parked off
screen. **No password is involved**: logging
out to the character list keeps the account connected, and entering the world names only the
character. A session that has ended - the client disconnected or closed, the account booted -
needs a full login, with the password, and **nothing here ever does one**: `login` refuses, saying
so. `SessionControl` (`src/AC.Host/Actions`) does both, and checks afterwards that both ends agree.

**Which way, as AC:Unreal allows it** (live, 2026-10-05: the Virindi Tank repository's `docs/live-tests/live-2026-10-05-b.md`,
section 7). *A logout always goes by the message*: CharacterLogOff (0xF653, empty), put into the
client's stream by the relay. AC:Unreal takes it unasked - it logs `CharacterLogOff (server)` and
`Logged off — returned to character select`, shows its character select, and **stays connected**,
so entering again needs no password. There is no safe key: AC:Unreal's Log Out (LOGOUT) is bound
only with a modifier - the retail keymap's Ctrl+Q and Alt+X, the player's own keymap Shift+Esc -
and a modifier posted by the overlay does not reach its keymap as one. Ctrl+Q came to it as Q, its
autorun (a toggle), and ran the character 22 m into a fence. So `ctl logout keys` goes by the
message too, and says why in the log; before the message goes, every key held for plugins is let
go, and nothing is pressed to stop a movement of the client's own. *Entering always goes by the
character select's own Enter, clicked* through the overlay - the character's row, then the Enter
button - and the client sends its CharacterEnterWorldRequest and CharacterEnterWorld itself.
Entering by messages, put to the client unasked, does not work and is not done: the server's login
reached AC:Unreal - its log has the PlayerDescription and PlayerCreate, and it even sent
LoginComplete - but its character select stayed up, since only its own Enter moves its screens to
the world (no `EnterWorld place`, no `UIFlow mode -> 6`). Nor does its character select take the
Enter key: two presses did nothing. `ctl login <character> messages` goes by the click and says why.
Entering needs the overlay attached and the game not minimized (parked off-screen with `ctl window
keep on` is fine); otherwise it is refused, saying so. Both need acting to be allowed.

**The click.** AC:Unreal draws its character select from the retail layout charactermanagement
(0x21000004, in its `Plugins\ACEClient\Docs\UI\Resolved`): 800 by 600, unscaled and centred, black
around it - at 560,240 of the live 1920 by 1080 window. The host asks the overlay for a click at
layout points - the character's row, 122,220 for the first and 16 pixels lower for each next, in the
server's order; then the Enter button's middle, 344,394 - and the overlay places the layout in the
window's client area (shrunk to fit only in a smaller window), brings the pointer there, posts the
move, the button's press and its release a moment apart, then the next point, and puts the pointer
back where the player had it. The pointer is moved as well because Unreal counts a click only over
the button its idea of the pointer is over, and it reads that from the real pointer. The messages
are marked as the overlay's, so its own windows - Virindi Tank's covers the character list - never
take them. The click rides in the overlay's input frame (`OverlayInput.Click`) with an id the
overlay makes it once by; the overlay's log (`ACUnrealOverlay.log`, beside it) says where it
clicked.

**The client's Desktop UI Scale.** Since release 94 AC:Unreal has a "Desktop UI Scale" option,
which it keeps as `DesktopUIScale` under `[ACE.Presentation]` in `Saved\Config\Windows\
GameUserSettings.ini` (100% to 300% in quarter steps; 100% when the line is missing). It is
Unreal's DPI scale for the whole viewport, so the character select is drawn that much larger - but
never larger than the largest quarter step at which 800 by 600 fits the window: 200% in a 1920 by
1080 window is 175%, the layout 1400 by 1050 at 260,15. The host reads the option
(`ClientSettingsWatcher`, read-only) and the click carries it (`ui_scale`); the overlay, which knows
the window, applies the client's limit (`native/ACUnrealOverlay/client_ui.h`, worked out from
ACUnreal.exe release 96, as its own tests state it: 200% at 3840 by 2160, 175% at 1920 by 1080, 100%
at 800 by 600). The overlay's own drawing does not change with it: the option scales Unreal's
widgets, not the window or its swap chain. `ctl status` shows the scale (`ui scale`). Not yet seen
live at anything but 100%.

**What is known**, from the captures and from AC:Unreal's own log (`Saved\Logs\ACUnreal.log`):

- The client logs off with CharacterLogOff, empty, sent again every two seconds until answered
  (3 or 4 times); ACE answers about six seconds later - the logging-out motion - with
  CharacterLogOff, the character list and its name, the three in one packet. The client logs
  `[ACE] CharacterLogOff (0xF653) sent`, `[ACE] CharacterLogOff (server)`, `[ACE] Logged off —
  returned to character select` and `UIFlow mode -> 3 layout=0x21000004`.
- The client enters with CharacterEnterWorldRequest - logging `EnterWorld request character=…` and
  `UIFlow mode -> 0` - ACE answers the ready, the client names the character, and the login
  follows; the client logs `PlayerCreate` and `LoginComplete (exited portal space)`, sends
  LoginComplete (0x00A1), and about a second later `UIFlow mode -> 6 layout=0x21000005`. Its log
  names its screens: 2 the login screen, 3 the character select, 6 the game, 0 between.
- ACE handles CharacterLogOff only for a character in the world, and the two entering messages only
  at the character list; it turns a character down with CharacterError (0xF659) - 13 for one still
  in the world, which can happen just after a logoff: the report says to try again in a few seconds.
- Every captured logoff the player made was followed, 1.0-1.6 s later, by the client closing the
  session: that was the player, at the character select. A logoff by the message is not: live, the
  client stayed connected at its character select for as long as it was watched.

**What is assumed**, until a live test says otherwise (the Virindi Tank repository's `docs/live-tests/relog-plan.md`): that the
click on the character select's row and Enter, posted with the pointer brought there, enters the
world as a player's click does, the game window shown or behind another one.

**The checks, and what is done when one fails.** A logout is done when the server has answered and
the client, watched for 3 s more, is at its character select by its log - or, with no log to read,
has not acted in the world since (a weak sign: an idle client seldom acts). A client still showing
the world has no key pressed for it: the player is asked, in a warning, to log out in the game, and
the client's own CharacterLogOff - which ACE, at the character list already, drops - is answered
with the server's own CharacterLogOff, list and name, the very bytes; after 60 s with none, it is
reported. A client that went on to its login screen, or a session that ended, is reported as the
end of the session. Entering is done once the server has created the character and the client
shows the game by its log (`UIFlow mode -> 6`) - its LoginComplete alone is not enough where its
log can be read, since AC:Unreal sent one while it stayed at its character select; where the log
cannot be read, LoginComplete is. A client that does not ask to enter within 5 s of the click is
reported, with the screen its log says; one the server created the character for that still shows
its character select 30 s later has the character logged off again, so both ends are back at the
character list and the character is not left standing in the world unplayed. The client's log is
read where the running client writes it (`ClientLog`, `src/AC.Host.Runtime`), opened and closed at
each read so the client can always rename it; a host relaying a capture or a test transport reads
none.

Plugins hear what a player's own relog tells them: `LoggingOff` once the logoff is asked for
(Decal's Logoff, Requested, on which Virindi Tank stops its macro), `LoggedOff` once it is done
(Logoff, Authorized), the account's characters (`IWorldView.AccountCharacters`, Decal's
`CharacterFilter.Characters` and `AccountName`), then a login. `IWorldView.Phase` says where the
session stands. Virindi Tank has no meta action or command of its own for logging out or in.

**Entering asked for while a logout is being finished** - the server has the character out, and the
client is being watched for its 3 s - is not refused: it waits, and starts once the logout is done,
or ends with it if the logout does not finish (`Stage.AfterLogOut`; `ctl status` shows it as "; then
enter the world as ..."). Mag-Filter asks a second after the character list comes, which is then.
Asked for before the server has answered the logout, it is refused as ever.

`ctl logout` and `ctl login` wait up to 25 s and answer how it ended - "Logged Testchar I
(0x50000006) out to the character list: the client shows its character select." - or that it is
still under way. `ctl status` adds `phase` (where the session stands), `relog` (the one under way,
or how the last ended) and, with the client's log read, `client` (its screen). A host that joined a
session with nothing handed over can put itself right: `ctl logout`, then `ctl login 1`.

### Restarting the host with the game connected

The relay can be stopped and started again under a player who stays in the world - the Decal
Agent updated by `tools\update-agent.ps1`, its Options changed, `achost run` stopped with Ctrl+C
and run again - and nothing is asked of the player. The new relay binds the same ports, so the
server's session (it is named after the relay's own endpoint, "127.0.0.1:9100") and the client
both simply carry on. What the new host would miss is everything the server says only at login:
the character, its skills, spells and enchantments, its inventory, what is around it, the
server's name. So the host stopping hands the session over, and the one starting carries it on:

- **Handing over.** Every host keeps the messages its world was built from, in order
  (`AC.Host.Handover.SessionJournal`). When it stops properly - `ctl exit`, the tray icon's Exit,
  the Agent restarting its host, Windows shutting down - it writes them to
  `handover-<listen port>.bin` in its data folder, with the relay's state: whom each port relays
  for, both rewriters' numbering (every line put into the server's stream and every action put
  into the client's moved the numbers after it; a relay that forgot would give the client
  numbers it already has, and the session would go quiet), the fragments they held back, and the
  halves of messages still being assembled. Acting on or off, "/r" and "/rt", and the client's
  highest action sequence go with them.
- **Raw messages, not the world they made.** The next host is as often as not a newer one, and
  replaying the messages through its own decoders gives it the world it would have had from the
  start - with anything a newer decoder reads that the old one skipped - and nothing to keep in
  step between two versions of the world's shape. The journal stays small by keeping only the
  latest of what each later message replaces whole: an object's position, motion and velocity,
  the client's own position reports and selection. A 16-hour capture of 1.2 million messages
  (111 MB) keeps 6,200 of them, 640 KB. Leaving the world forgets all but the server's name; the
  session ending forgets everything; past 64 MB the journal gives up and says so. The one thing
  the world does that no message says - letting go, on the tick, of an object out of view for 25
  seconds - goes into the journal as the ObjectDelete it amounts to.
- **Carrying on.** A host starting reads the file and deletes it. If it was written for the same
  ports and server less than two minutes ago, the relay takes up its numbering and its client
  before relaying anything - so the server's first packet, which after a restart often comes
  first, reaches the client. If the first packet relayed is the session going on (no login, no
  handshake; `ISessionStarts`), the host replays the messages into the world before applying the
  first one, saying nothing to plugins while it does and with the world's clock reading when each
  was first heard - so what went out of view more than 25 seconds ago is let go of before anything
  is said of it. It ages every enchantment by the time since the server sent it, puts acting back
  as the player left it, and then tells plugins what a login
  tells them, in a login's order: `ServerConnected`, `PlayerIdentified` (before the character's
  object is described, as at a login), `ObjectCreated` for the character, then what it carries
  and wears, then everything around it, `EnchantmentChanged` for each enchantment, and
  `CharacterUpdated`. Decal's plugins hear Login - once the character's object is told of, so
  that the character filter reads as a login's description left it: id, name, level, experience
  - every CreateObject, the login's messages, and LoginComplete on the client's word that it had
  entered, retold after them. A new login instead
  lets the file go; the numbering starts over at its handshake.
- **Nothing handed over.** A host that crashed or was killed leaves nothing, and neither does a
  host older than this. The next one knows it joined in the middle: it says so in its log, `ctl
  status` shows `session    joined while you were in the world: ...`, and once a character is
  seen in the world it says, once, in the game: "Decal Agent started while you were in the world:
  log out to character select and enter the world again so it can see your character." Entering
  the world shows it everything again (the server's name excepted, which comes only with a new
  login). Its relay starts with no numbering either: if lines had been put into the server's
  stream or answers taken out of it - or actions put into the client's - before the crash, the
  numbers are off by as many, and the far end misses that many messages, or waits for ones that
  never come, until the player logs in afresh.

`ctl status` says `session    carried on from the host that stopped at 13:42:05` when a host has
carried a session on.

Every handover says where it went, whether or not it happened. The host stopping logs the whole
path it wrote ("Handed the session over in C:\...\handover-9100.bin (7245 message(s), written at
05:46:47)"), or that it wrote nothing there and why. The host starting always logs, at Info, the
whole path it looked at and what it found: the session taken, with the same path; why a file there
was not taken (too old, another relay's, unreadable - and deleted all the same); or "No session
was handed over: there is nothing at C:\...\handover-9100.bin", followed by what that folder does
hold - so that, beside the stopping host's line, a file written where the new host does not see it
can be told from one never written. Only a file not found counts as nothing there: the file is
opened, not asked after, since asking answers no for a file that is there and cannot be looked at;
and nothing in it - a garbled file included - stops the host from starting. `ctl status` adds a
line with the same story and its end: `handover   none at C:\...\handover-9100.bin`;
`handover   taken from C:\...\handover-9100.bin, written at 05:46:47 (7245 message(s)): carried on`
(or `to be carried on once the game is heard from`, or `let go:` and why); or
`handover   not taken: ...`.
A host stopped again before the game was heard from - the update run twice in a row - hands on
what it was handed, as it came and as old as it was, rather than losing it between two restarts.
`tools\update-agent.ps1` follows it end to end: it prints the file the Agent that stopped left, the
new Agent's handover line, and a warning naming the file and the log to read when the new Agent
left a file there untaken.

Start and update the Agent from a shell of your own. A shell inside a packaged (MSIX) app - the
Claude desktop app's, for one - may have its writes to `%LOCALAPPDATA%` redirected into that app's
private copy (`%LOCALAPPDATA%\Packages\<app>\LocalCache\Local`). An Agent started from there keeps
its settings, its plugins' settings and the session it hands over in that copy, where an Agent
started normally never looks: that is how a handover was once left untaken. `tools\start-game.ps1`,
`tools\update-agent.ps1` and `tools\install-plugin.ps1` (into an Agent under `%LOCALAPPDATA%`)
test for it with `tools\PackageCheck.ps1` - a probe file written and looked for under the
packages' copies - and stop, saying so.

Not carried over: the plugins' own state (each starts as at a login),
actions and lines still queued to go out, `ctl chat`'s history, and Decal plugins' view of the
login messages themselves - ServerDispatch is not given them again. `HostRuntimeOptions.NoHandover`
turns all of it off; a replay never hands over.

### Decal Agent: Decal as a program of its own

`src/Decal.Agent` builds `DecalAgent.exe`, the counterpart of Decal's DenAgent.exe: a small
window laid out from DenAgent 2.9.8.3's own dialog template (read out of its resources, as
are its icon and its plugin list's pictures), with the plugin list - a box, a version and a
status for each plugin, and beneath DecalCompat every plugin Decal's registry lists, as the
Decal window lists them (`PluginList.Build` gives both the same rows) - and Update, Options,
Add, Remove, Export, Refresh List and Close down the side. The box is DenAgent's: ticked while
it runs, empty while off, a red cross when wanted and unable to run; a row's tooltip, and "Why?"
on its menu, give the reason in full. Clicking the box of a plugin the host replaces says why it
has no switch. Refresh List reads Decal's registry again; Export writes each row's reason too.
Close hides it; the icon in the notification area brings it back, switches acting, and exits.

It runs the host in its own process: `AC.Host.Runtime`'s `HostRuntime`, which is exactly what
`achost run --overlay` runs, relay, plugins, overlay pipe and control pipe included - so
`achost ctl` drives it too. Exiting it, or its restarting the host when Options change the
ports, hands the session over, so an Agent restarted while the player is in the world - as
`tools\update-agent.ps1` restarts it - carries the session on (see "Restarting the host with
the game connected" above). It watches for AC:Unreal (an `ACUnreal` process owning a window
titled "AC:Unreal...") and puts `ACUnrealOverlay.dll` into it once per client process, a few
seconds after its window appears; a failure is reported rather than retried, and the icon's
menu asks again by hand.

Where the buttons differ from Decal's: **Update** has nothing to download, so it reloads every
running plugin from its folder; **Add** installs a plugin assembly (with what it needs from
beside it) or a whole plugin folder into `plugins\`, and starts it; **Remove** switches one off
and deletes it, keeping its settings. The Agent ships only Decal's own plugin, DecalCompat. Other
plugins are installed into it separately:

```powershell
# in the Virindi Tank repository
tools\install-plugin.ps1 -Plugin VirindiTank            # into the Agent built in its submodule
tools\install-plugin.ps1 -Plugin VirindiTank -Agent "$env:LOCALAPPDATA\Programs\Decal Agent" -Notify
```

For players, `tools\build-installers.ps1` builds `DecalAgentSetup.exe`, which installs the Agent
self-contained and per user; the Virindi Tank repository builds `VirindiTankSetup.exe`, which
installs Virindi Tank into it. See [installers.md](installers.md).

Its settings are `DecalAgent.json` and its log `logs\DecalAgent-<date>.log`, both in the host's
data folder. Its command-line options (`--listen-port`, `--control-pipe`, `--overlay-pipe`,
`--data`, `--plugins`, `--no-inject`, `--tray`) change one run without saving anything, for
starting a second one beside the player's.

### Walking

The client, not the server, decides where an Asheron's Call character is: the server is
told the client's position several dozen times a second and believes it. So a plugin
walks the way a player does - `IHost.Input.Hold(GameKey.Forward, true)` - and the overlay,
which runs inside the client, presses the key. The host sends the whole set of held keys
each time (never a change, so nothing can be left stuck), repeats it every 300 ms while
anything is held, and the overlay releases everything when it has heard nothing for 1.2
seconds, when the pipe drops, or when it unloads. Switching acting off releases every key
on the next tick. The keys default to the retail client's movement keys and can be
rebound: `--set Decal:Key.Forward=W`, `Decal:Key.TurnLeft=A`, a letter or a virtual-key
number. Where the character went comes back as it always has: the client's own position
reports, read into `ICharacterView.Location`.

### Hot reload of the overlay

`acinject --unload` asks the injected DLL to remove itself and waits for it to go, which
frees the file to rebuild. The removal drains every entry point - the render detours and
the window procedure both - before the trampolines are freed, because the first version
did not, and unmapping code the message thread was still inside crashed the client. It
has since been reloaded into a running client repeatedly.

## Taking loot

Deciding is watching. Taking reaches into someone's game, so it waits to be asked
for: `--set VirindiTank:Loot=true`, and `--enable-actions` as well. Without both,
the plugin reports what it *would* do. With the setting on but actions off, it
says so rather than quietly doing nothing. With Virindi Tank's own window, its
Enable Looting option is what asks - as the character's Virindi Tank settings have
it, with the player's changes in the window kept in the plugin's settings - and
`--set VirindiTank:Loot` sets that option for the run.

The difficulty is not choosing what to take, it is pacing. The server processes one
action at a time and silently drops the rest, so sending a take for every item in a
corpse loses all but the first — and looks like it worked. So the taker keeps
exactly one action in flight and waits for its answer: the item arriving in the
packs, or the server refusing the move (InventoryServerSaveFailed, which plugins hear
as `IHost.MoveRefused`). A refused take is tried again after the 0.75 s item-use
lock, up to Virindi Tank's CorpseLootItemMaxAttempts (20), then left. A timeout
gives up on an item if no answer arrives, because a queue that stalls forever is
worse than one that loses an item, and a container that closes drops whatever was
still queued for it.

## Acting, and why it works this way

`IGameActions` is live whenever the player allows it: from the start with
`--enable-actions`, or at any time from Decal's window or `achost ctl act on`. The
machinery below is always fitted and costs nothing while nothing is queued. Two
properties of the protocol shaped the implementation, and neither is obvious.

**Keys cannot be borrowed.** A live packet's checksum is masked with a key from
an ISAAC stream shared by both ends. The server tolerates keys arriving out of
order by walking its stream forward to find whichever key it is shown
(`CryptoSystem.Search`, up to 256 keys). So spending a key the real client still
needs does not merely waste it — the client's next packet becomes unverifiable
and the window fills with garbage. Sending our own packets is therefore not an
option.

What *is* possible: an encrypted checksum is `headerHash + (key ^ payloadHash)`,
and every term but the key is computable from the bytes in hand. **Observing a
packet yields the key that produced it.** So the host alters the client's own
packet and re-stamps it with the same key. The server sees one packet using that
key, exactly as it expected.

**Fragment numbers must be unbroken.** The server accepts a fragment only when
it is exactly one past the last it took, buffering anything early
(`NetworkSession.cs`). Inserting one therefore shifts every later fragment for
the life of the connection, so `ClientStreamRewriter` renumbers the client's
fragments by a running offset — and remembers assignments, because a
retransmission resends original numbers and recomputing would open a gap the
server would wait on forever.

The consequence for callers: a message leaves on the client's *next* packet, not
immediately. On a live session that is a few hundred milliseconds — the client
is always sending acks, echoes and movement. Anything that will not fit waits
for a roomier packet rather than overflowing the 1024-byte limit.

Actions are off by default. Relaying is observation; acting on a live character
is a decision to make deliberately, not one to acquire by starting a proxy - which
is why the switch is the player's, in the game, and starts off.

## Running it

```bash
# live, against a self-hosted ACE on 9000/9001, with whatever plugins are installed
dotnet run --project src/AC.Host.Cli -- run --server 127.0.0.1 --listen-port 9100 --capture session.acap --objects

# the same, but letting plugins act in the game
dotnet run --project src/AC.Host.Cli -- run --server 127.0.0.1 --listen-port 9100 --enable-actions

# the same host and plugins over a recorded session, no client or server needed
dotnet run --project src/AC.Host.Cli -- replay session.acap --objects --chat

# walk forward for one second and say how far the character moved
achost run --server 127.0.0.1 --enable-actions --test-move

# let VirindiTank pick things up, not just decide about them
achost run --server 127.0.0.1 --enable-actions --set VirindiTank:Loot=true

# point VirindiTank at a profile
dotnet run --project src/AC.Host.Cli -- run --server 127.0.0.1 --set VirindiTank:Profile="C:\profiles\mine.utl"
```

Building `AC.Host.Cli` lays Decal's own plugin, Decal.Compat, out under
`bin/.../plugins/Decal.Compat/` automatically, which is where `achost` looks.
Other plugins go beside it: a directory named for the plugin containing its DLL
and its private dependencies. The Virindi Tank repository's `tools\install-plugin.ps1
-Plugin VirindiTank -Destination <achost's folder>\plugins` puts VirindiTank there. Do not ship `AC.Host.dll` inside a plugin
directory — the loader resolves the contract to the host's copy on purpose.

## Writing a plugin

Reference `AC.Host` (with `Private=false` so it is not copied), implement
`IPlugin`, subscribe in `Startup`, unsubscribe in `Shutdown`. `tests/plugins/Counting.Plugin`
is the smallest example. The Virindi Tank repository's `src/VirindiTank.Plugin` is the worked
example: `VirindiTankPlugin.cs` is the lifecycle and decision loop,
`WorldObjectItemInfo.cs` adapts the world model to an older API, and
`HostGameStateProvider.cs` answers character questions from `ICharacterView`.

On the question of a **Decal.Adapter-compatible facade** — so that existing
community plugin source (Mag-Tools, GoArrow, the VVS ports) could be recompiled
against this host: the machinery is the same clean-room technique used for the
loot engine, and the world model already holds what `WorldFilter` and
`CharacterFilter` exposed. It has not been built, deliberately: it should be
scoped from the call sites of the first real plugin someone wants ported, not
speculatively.
