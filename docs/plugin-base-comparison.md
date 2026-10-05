# A plugin base of comparable strength: Chorizite, Decal, and what we have

> This note was written when Decal and Virindi Tank were one repository. Paths under
> `src/VTClassic`, `src/UTank2.Abstractions`, `src/VirindiTank.Plugin` and `upstream/` are now in
> the Virindi Tank repository (VirindiTank-ACUnreal), which builds against this one as a git
> submodule; the rest are here.

This note answers a concrete question — the owner wants "a strong plugin base like
Chorizite and Decal", so what does that actually consist of, and where does
`src/AC.Host/Plugins/IPlugin.cs` fall short?

Everything below is grounded in something read. Chorizite was read from its own
repositories at the commit current on 2026-09-28 (default branch `master`, not
`main` — which is why `/tree/main/` URLs 404). For Decal there is something better
than its documentation to hand: a pristine checkout of `virindi_public` (MIT; how to
check one out is in the Virindi Tank repository's `UPSTREAM.md`, into `upstream-virindi/`),
including three Decal example plugins,
four ported community plugins, VirindiReporter, and Virindi's own view-system
abstraction. That is first-hand evidence of the contract Decal plugins were
written against, and it is cited by repository path. It is also only plugin-side
evidence, which is why there is a section below on what it cannot settle. Where
something could not be established, it says so.

## What Chorizite does

Chorizite describes itself as "an open source plugin manager for Asheron's Call.
Includes core plugins for html/css based UI, Dat reading, and lua scripting"
(the repository description at <https://api.github.com/repos/Chorizite/Chorizite>).
The important structural fact is that almost nothing is in the core: DAT reading,
the UI, Lua and the client integration are all *plugins*, each in its own
repository (<https://api.github.com/orgs/Chorizite/repos>: `LuaPlugin`,
`RmlUiPlugin`, `ACPlugin`, `LauncherPlugin`, `PluginManagerUIPlugin`,
`CoreTestPlugin`). The core is a manifest reader, a loader registry, a
dependency-ordered starter, an Autofac container and a reload loop. That
division is the thing worth copying.

### A plugin is a directory with a manifest

Discovery is a single directory scan, one level deep, looking for
`manifest.json` in each subdirectory — `PluginManager.LoadPluginManifests()` at
<https://raw.githubusercontent.com/Chorizite/Chorizite/master/Chorizite.Core/Plugins/PluginManager.cs>:

```csharp
foreach (var file in Directory.EnumerateDirectories(PluginDirectory)) {
    var manifestFile = Path.GetFullPath(Path.Combine(file, "manifest.json"));
```

There is no attribute, no naming convention and no assembly scan at discovery
time. A directory without a readable `manifest.json` is warned about and skipped.
The manifest is the plugin's identity. Here is a real one, the whole file,
from <https://raw.githubusercontent.com/Chorizite/RmlUiPlugin/master/manifest.json>:

```json
{
  "id": "RmlUi",
  "name": "RmlUi",
  "description": "Adds rmlui interfaces with html/css/lua scripting",
  "version": "0.0.0-dev",
  "entryfile": "RmlUi.dll",
  "author": "Chorizite",
  "icon": "icon.png",
  "dependencies": [ "Lua@0.0.13" ],
  "environments": [ "Launcher", "Client" ]
}
```

The fields are defined by `PluginManifest`
(<https://raw.githubusercontent.com/Chorizite/Chorizite/master/Chorizite.Core/Plugins/PluginManifest.cs>):
`Id`, `Name`, `Author`, `Version`, `Description`, `Repo`, `Icon`,
`List<string> Dependencies`, `ChoriziteEnvironment Environments`, `EntryFile`,
plus two `[JsonIgnore]` computed members, `ManifestFile` and
`BaseDirectory => Path.GetDirectoryName(ManifestFile)`. `TryLoadManifest<T>`
returns false with a human-readable `errorString` rather than throwing, which is
the right shape for refusing a plugin cleanly. `Validate` itself is thin: it
checks that `Name` is non-empty, and its version check is guarded by
`if (string.IsNullOrWhiteSpace(Version))` before attempting `new Version(Version)`
— so as written it only tries to parse a version that is blank, and never
validates a supplied one. The same shape is published as a JSON schema for editor completion at
<https://raw.githubusercontent.com/Chorizite/plugin-index/master/PluginIndexBuilder/schemas/plugin-manifest.json>,
where `id`, `author`, `entryfile`, `version`, `description` and `environments`
are `required`.

Two smaller pieces are worth noting. `manifest.dev.json` is a *separate*,
developer-only manifest with just `Source` and `Bin` paths
(`PluginDevManifest.cs`, same directory), loaded if present and used to watch a
source tree rather than the deployed one. And an MSBuild task package
(<https://raw.githubusercontent.com/Chorizite/Chorizite.Plugins.MSBuildTasks/master/README.md>)
makes the manifest the single source of truth for the build: it sets `Version`,
`AssemblyVersion`, `PackageVersion`, `Description`, `Title`, `Product`,
`PackageId`, `Authors` and `Company` from the manifest's fields, copies the
plugin into the Chorizite install directory after build, and generates the
`manifest.dev.json` that enables hot reload. The manifest is not a chore
maintained alongside the assembly; it is what the assembly's metadata is
generated from.

### The loader is itself an extension point

`IPluginManager` (same directory) holds `List<IPluginLoader> PluginLoaders` with
`RegisterPluginLoader` / `UnregisterPluginLoader`, and `IPluginLoader` is two
methods:

```csharp
bool CanLoadPlugin(PluginManifest manifest);
bool LoadPluginInstance(PluginManifest manifest, out PluginInstance? instance);
```

`AssemblyPluginLoader.CanLoadPlugin` returns
`Path.GetExtension(manifest?.EntryFile ?? "") == ".dll"`. That is the whole test.
The consequence is the neatest thing in the design: the Lua plugin is an ordinary
assembly plugin that registers a second loader claiming `.lua` entry files
(<https://raw.githubusercontent.com/Chorizite/LuaPlugin/master/LuaPluginLoader.cs>:
`manifest.EntryFile?.EndsWith(".lua") == true`), and from then on a Lua script
directory with a `manifest.json` is a first-class plugin with a version,
dependencies and an environment list, indistinguishable from a compiled one to
the manager. Scripting support is not a feature of the host; it is a plugin that
adds a loader.

### Isolation, dependencies and load order

Each assembly plugin gets a collectible `AssemblyLoadContext`
(`AssemblyPluginLoadContext : AssemblyLoadContext`, constructed
`base(pluginPath, true)` — the `true` is `isCollectible`), with an
`AssemblyDependencyResolver` over the entry DLL and a fallback that copies native
libraries to a temp directory before loading so the originals are not locked.
Managed assemblies are read into a `MemoryStream` and loaded with
`LoadFromStream` for the same reason (`LoadAssemblyWithoutLocking`). Assemblies
the resolver cannot place fall back to
`AppDomain.CurrentDomain.GetAssemblies().LastOrDefault(...)`, with an honest
comment in the source:

```csharp
// TODO: this is used for plugins that reference other plugins. this should
// only look through plugins that this plugin depends on
```

Dependencies are strings of the form `Id@Version`, with a trailing `?` marking
the dependency optional. `PluginManager.StartPlugin` parses them and recurses
depth-first before starting the plugin itself, so load order is derived from the
declared graph rather than configured. A missing non-optional dependency is a
clean refusal — `Failed to start plugin {Name}: Dependency {depId} not found` —
and so is a dependency that fails to start. `Environments` is checked first:
a plugin whose `Environments` flag does not include the running
`ChoriziteEnvironment` (`Launcher`, `Client`, `Inspector`, or `DocGen` which is
`Launcher | Client`) is silently not started.

Two caveats, both read in the source rather than inferred. The version half of a
dependency is only ever a warning, and the comparison looks wrong: it compares
the *dependent's* own version against the required version, not the dependency's
—

```csharp
if (new Version(manifest.Version.Split('-').First()) < depVersion) {
    _log?.LogWarning($"Plugin {manifest.Name}: Dependency {depId} version {depVersion} was less than the loaded version of {depPlugin.Version}");
}
```

— so a plugin declaring `Lua@0.0.13` will load against any `Lua` that is
present. And there is no declared *host* API version anywhere: not in
`PluginManifest`, not in the JSON schema, and not in the plugin-index models
(`ReleaseModel`, `PluginListingModel`, `PluginDetailsModel` in
`Chorizite.Core/Plugins/Models/` carry `Version`, `DownloadUrl`, `Sha256`,
`Dependencies`, `Environments` and download counts, but nothing constraining the
Chorizite core version). A plugin built against an older Chorizite is not
refused with a clear message; it fails however it fails. This is worth stating
plainly because it means the manifest idea is worth copying but the versioning
story is *not* something to copy from Chorizite — it is a gap there too.

### What the host hands a plugin

There is a DI container: Autofac. `Chorizite<TBackend>` builds it and a child
`ILifetimeScope`
(<https://raw.githubusercontent.com/Chorizite/Chorizite/master/Chorizite.Core/Chorizite.cs>),
registering the backend, `IRenderer`, `IInputManager`, the scope itself,
`IPluginManager`, `AssemblyPluginLoader`, `ILogger<>` via a factory, and
conditionally `IDatReaderInterface`, `IClientBackend` + `NetworkParser` in client
environments and `ILauncherBackend` in the launcher. A client environment that
fails to provide an `IClientBackend` throws at construction:
`"Client environments must provide an IClientBackend"`.

Plugins receive services by *constructor injection*, resolved by reflection.
`AssemblyPluginInstance.InstantiatePlugin` finds the plugin type
(`typeof(IPluginCore).IsAssignableFrom(t) && !t.IsAbstract`), sorts its
constructors by parameter count descending, and resolves each parameter through
`ResolveParameter`, which special-cases the plugin's own `PluginManifest`,
`ILogger`, `IClientBackend`, `ILauncherBackend`, and — the important one — any
parameter assignable to `IPluginCore`, which is resolved to *another loaded
plugin's instance*. Everything else goes to `_serviceProvider.Resolve`. That is
how a plugin consumes another plugin, and it is why declared dependencies and
load order matter. The real thing looks like this
(<https://raw.githubusercontent.com/Chorizite/ACPlugin/master/ACPlugin.cs>):

```csharp
protected ACPlugin(AssemblyPluginManifest manifest, IChoriziteBackend choriziteBackend,
    IClientBackend clientBackend, IPluginManager pluginManager, NetworkParser net,
    RmlUiPlugin rmlUi, IDatReaderInterface dat, ILogger log) : base(manifest) {
```

`IPluginCore` itself is small — `Manifest`, `Services`, `AssemblyDirectory`,
`DataDirectory` (which is `StorageDirectory` joined with `Manifest.Id`), and two
protected abstract methods `Initialize()` and `Dispose()`. It is an abstract
*class*, not an interface, and it carries a doc comment that matters for the
lifecycle: "Use Initialize to initialize the plugin instead of the constructor.
If your plugin is using state/settings/views, these wont be ready to setup until
Initizalize is called."

The event model is not centralised. There is no host-wide event bus; events live
on whichever service owns them, and the shape is consistently a `WeakEvent<T>`
from `Chorizite.Common` exposed through explicit `add`/`remove`. The surfaces
read: `IChoriziteBackend` (`Renderer`, `Input`, `Environment`, `OnLogMessage`,
`PlaySound`, `SetCursorDid`, clipboard, `Invoke(Action)` to marshal onto the game
thread); `IClientBackend` (`GameScreen`, `SelectedObjectId`, `OnC2SData`,
`OnS2CData`, `OnChatInput`, `OnChatTextAdded`, `OnObjectSelected`, `UIBackend`,
`EnterGame`, `LogOff`, `Exit`, `AddChatText`, `InvokeChat`,
`SendProtoUIMessage`); `IClientUIBackend` (`OnScreenChanged`, drag-drop start and
end, tooltip show and hide, root-element show and hide, `OnUILockChanged`,
`GetUIElementPosition`, `ToggleRootElement`); and on the manager itself
`OnPluginsLoaded` and `OnBeforePluginsUnloaded`. Anything richer — a world object
model, a character — is in `ACPlugin`'s own `API/` namespace (`Game`, `World`,
`WorldObjectManager`, `WorldObject` and a class per weenie type,
`Character`, `Actions`, `Enchantment`, `SkillFormula`, and so on), i.e. a plugin,
not the core.

### Unload and reload are supported, and cost something

This is the part of Chorizite that is genuinely ahead. `IPluginManager` has
`LoadPlugins(bool isReloading)`, `UnloadPlugins(bool isReloading)`,
`ReloadPlugins()` and `Update()`, and `Update()` is called once per frame from
`IRenderer.OnBeforeRender3D`. `ReloadPlugins()` only sets a flag; the actual
reload happens on the next frame, which avoids unloading an assembly from inside
its own call stack:

```csharp
public void Update() {
    if (_wantsReload || Plugins.Any(p => p.IsLoaded && p.WantsReload)) {
        _wantsReload = false;
        ReloadPluginsInternal();
    }
}
```

A plugin asks to be reloaded by setting `WantsReload`, and the base
`PluginInstance` constructor sets it automatically from a `FileWatcher` over
`Manifest.BaseDirectory`/`Manifest.EntryFile`, with `LiveReload` defaulting to
`true`. Unloading walks dependents first (`UnloadPluginAndDependents`), then
`GC.Collect()`/`WaitForPendingFinalizers()` up to fifty times, then checks
whether each plugin's assembly is still in `AppDomain.CurrentDomain` and logs
`Failed to unload plugins: ...` if so. `AssemblyPluginInstance.Unload` calls
`Dispose` on the plugin by reflection, unloads and disposes the load context, and
then clears `System.Text.Json`'s internal type cache by reflecting on
`JsonSerializerOptionsUpdateHandler.ClearCache` — the source calls it "a hack",
which is the honest label for what collectible-ALC reloading costs in practice.

Because an unload destroys the plugin object, Chorizite gives plugins two ways to
survive it, both opt-in generic interfaces in
`Chorizite.Core/Plugins/AssemblyLoader/`. `ISerializeState<T>` is for reload
continuity: `SerializeBeforeUnload()` returns a `T`, `DeserializeAfterLoad(T?)`
receives it back (null on a first load), and `TypeInfo` supplies the
source-generated `JsonTypeInfo<T>` so the serialisation does not need reflection
across the boundary. `ISerializeSettings<T>` is the same pattern pointed at disk:
`AssemblyPluginInstance.TrySerializeSettings` writes to
`Path.Combine(PluginInstance.DataDirectory, "settings.json")` and
`TryDeserializeSettings` reads it back. So the settings store *is* the
reload-survival mechanism, with a typed object per plugin, persisted as JSON in
the plugin's own data directory, saved on unload and restored on load. There is
no key-value API and no change notification; a plugin declares one settings type
and owns it.

### The UI story

The UI is HTML and CSS because it is RmlUi — the C++ library at
<https://github.com/mikke89/RmlUi> — driven through the org's own C# bindings
(`RmlUi.Net`, <https://api.github.com/orgs/Chorizite/repos>) and rendered into
the client's own Direct3D 9 device by `Render/DX9RenderInterface.cs` in
`Chorizite.NativeClientBootstrapper` (or OpenGL in the launcher,
`Chorizite.Launcher/Render/OpenGLRenderer.cs`). It is a plugin:
`RmlUiPlugin`, whose README says it "adds RmlUi view that support
html/css/lua" (<https://raw.githubusercontent.com/Chorizite/RmlUiPlugin/master/README.md>).

A plugin does **not** declare its UI in the manifest. There is no UI field in
`PluginManifest` and none in the published schema. Instead a plugin declares a
dependency on `RmlUi`, takes `RmlUiPlugin` as a constructor parameter, and calls
an API at runtime:
`Panel CreatePanel(string name, string rmlFilePath, Action<UIDocument>? init = null)`,
`CreatePanelFromString`, `CreatePanelFromSource`, `DestroyPanel`,
`RegisterScreen(string name, string rmlFilePath)`, `RegisterTemplate`,
`ToggleDebugger` (`RmlUiPlugin.cs` and `Lib/PanelManager.cs` in that repository).
`Panel` carries the chrome concepts: `ShowInBar` ("Show in the plugin bar. This
is where plugins can be minimized to"), `WantsAttention`, `IsGhost` ("ghost
panels let click/mouse events pass through"), `PullToFront`. The content is an
`.rml` file shipped as an asset beside the DLL — `ACPlugin` ships
`assets/panels/{DragDropOverlay,Indicators,Logs,Tooltip}.rml` and
`assets/screens/{CharSelect,DatPatch}.rml`. A panel is HTML with a `<style>`
block, templates via `<link type="text/template">`, and behaviour in Lua via
`<script src="todo.lua" />`
(<https://raw.githubusercontent.com/Chorizite/RmlUiPlugin/master/assets/panels/Test.rml>).
There is also a virtual-DOM layer (`Lib/RmlUi/VDom/`) for reactive updates.

So the mechanism is: UI is a capability provided by one plugin to others, coupled
at compile time to that plugin's types, and the declaration is an asset file plus
a runtime call. Not a manifest contribution, and not framework-free.

### Lua

A Lua plugin is a manifest whose `entryfile` is a `.lua` file. `LuaPluginInstance.Load`
reads the file and runs it wrapped in a managed coroutine:

```csharp
_moduleRet = LuaPluginCore.Instance.Context.DoString(
    $"""coroutine.create_managed(function() {source} end, "Document")""", $"{luaEntry}");
```

(<https://raw.githubusercontent.com/Chorizite/LuaPlugin/master/LuaPluginInstance.cs>.)
The runtime is XLua, vendored into the plugin (`XLua/` plus
`runtimes/win-x86/native/xlua.dll`). What a script sees is a set of named modules
that `LuaPluginCore.Initialize` registers — `Backend`, `Renderer`,
`InputManager`, `PluginManager` unconditionally, and `DatReader`,
`NetworkParser`, `ClientBackend`, `LauncherBackend` if those resolve from the
container (`RegisterOptionalLuaModule<T>` guards with `Scope.TryResolve`)
(<https://raw.githubusercontent.com/Chorizite/LuaPlugin/master/LuaPlugin.cs>).
`RegisterLuaModule(string name, object module)` is public, so any plugin can add
to the Lua surface. Two attributes shape what gets exposed:
`LuaModuleNamespaceAttribute(params string[] ns)` and `HideScriptingAttribute`.
A prelude (`LuaScripts/init.lua`) adds `async`, `await`, `sleep` and
`coroutine.create_managed` on top of C# `Task`, so a script can await .NET work
without blocking the frame. Separately, `Chorizite.DocGen.LuaDefs` generates Lua
type definitions from the .NET surface and `Chorizite.VSCode` consumes them for
intellisense.

### Chorizite.NativeClientBootstrapper

This is the piece that has no analogue here, and understanding why is the point.
It is the in-process host for the *retail* client: `StandaloneLoader.Init` is an
entry point matching the `(IntPtr, int)` signature that
`Chorizite.Injector` — "Injects a dotnet host into a process and loads
Chorizite", itself "Based on
<https://github.com/Reloaded-Project/Reloaded.Core.Bootstrap>" — calls after
loading a .NET runtime into `acclient.exe`. `Init` installs five hook sets
(`DirectXHooks`, `NetHooks`, `ACClientHooks`, `ChatHooks`, `UIHooks`) and
`Startup` then constructs `Chorizite<ACChoriziteBackend>` with
`ChoriziteEnvironment.Client`. Hooks are created through `Reloaded.Hooks`, either
at a literal address or at an address found by signature scan
(`Hooks/HookBase.cs`, `Lib/SigScanner.cs`, `Lib/SigScanAttribute.cs`), and the
client's internals are re-declared in C# under `AcClient/` — `Physics.cs`,
`Movement.cs`, `Inventory.cs`, `Magic.cs`, `Net.cs`, `ChatInterface.cs`,
`UIFlow.cs` and forty more files — against a symbol map, `Chorizite.Core/acclient.map`,
with a sibling repository of "ida scripts for porting info from pdb client to eor
client".

That is the cost of being inside the client: a hand-maintained map of a specific
binary. It is also what buys Chorizite everything we cannot have — panels drawn
in the game's own frame, real UI element positions, direct calls into the client's
inventory and physics. The relevant conclusion for us is not that we should do
this; it is that Chorizite's *plugin* layer is carefully independent of it. The
backend is an abstraction (`IChoriziteBackend`, with `ACChoriziteBackend` for the
client and `LauncherChoriziteBackend` for the launcher), environments are
declared, and services are registered conditionally. The same core loads plugins
in a launcher with an OpenGL renderer and no game at all. That separation is
directly copyable and is the same bet `IGameTransport` already makes here.

## What Decal did

Decal was COM at the bottom and a .NET adapter on top, injected into the retail
client. The .NET surface is what plugins were actually written against, and this
repository contains plugins written against it.

**A plugin was a .NET class deriving from `Decal.Adapter.PluginBase`, marked with
attributes, registered in the Windows registry as a COM component.** The
canonical shape, from `upstream-virindi/VVSPorts/SSSort-VVS/SSSort/PluginCore.cs`:

```csharp
[FriendlyName("SSSort")]
[MyClasses.MetaViewWrappers.MVView("SSSort.SSSort.xml")]
[WireUpBaseEvents]
public class PluginCore : PluginBase
```

`[FriendlyName]` and `[WireUpBaseEvents]` are Decal's own — neither is defined
anywhere in this tree and the file's usings are `using Decal.Adapter; using
Decal.Adapter.Wrappers; using Decal.Filters;` — while `[MVView]` is Virindi's,
discussed below. `[FriendlyName]` appears on three of the four ported community
plugins here and `[WireUpBaseEvents]` on two; `SkunkVision` simply derives
`Decal.Adapter.PluginBase` with no attributes at all
(`upstream-virindi/VVSPorts/SkunkVision-VVS/SkunkVision_CSharp/Plugin.cs:47`), so
the attributes were optional decoration on a base class that was not.
`PluginBase` provided `protected override void Startup()` and `Shutdown()`, a
`Host` property of type `Decal.Adapter.Wrappers.PluginHost`
(`upstream-virindi/Examples/AutoWireupExamplePlugin/AutoWireupExamplePlugin/PluginCore.cs`)
and a `Core` property of type `CoreManager` — visible where one plugin passes both
to a helper, `ClassGroup = new cClassGroup(Host, Core);` with the helper's field
declared `public CoreManager Core;`
(`upstream-virindi/VirindiReporter/VirindiReporter/PluginCore.cs:52,79`).
Registration was per-plugin COM registration with a GUID: VTClassic reads its own
installed profile path out of
`HKLM\SOFTWARE\Decal\Plugins\{642F1F48-16BE-48BF-B1D4-286652C4533E}`, value
`ProfilePath` (`src/VTClassic/Rules/ColorXML.cs:72`, guarded in our port and
described in `UPSTREAM.md`). So discovery was the registry, not a manifest file,
and identity was a GUID, not a string id.

**The service surface was a set of "filters" reached through a static
singleton.** `Decal.Adapter.CoreManager.Current` was the root, and the members
exercised by the code here are `CharacterFilter`, `WorldFilter`, `HotkeySystem`,
`ChatBoxMessage`, `CommandLineText`, `PluginInitComplete` and
`PluginTermComplete`. From the plugin side a filter behaved as a stateful,
live-updated model derived from the network stream, which the plugin queried and
subscribed to — not something the plugin inserted itself into. Whether filters
were *also* an ordered chain that a third party could add a link to is a
different question, and one this evidence cannot settle; see below. Reading them
looked like this
(`upstream-virindi/VirindiTankLootPlugins/VTClassic Shared/LootRules.cs`, and the
pristine copy at `upstream/VTClassic-r162/VTClassic Shared/LootRules.cs:1317`):

```csharp
foreach (Decal.Adapter.Wrappers.WorldObject wo in Decal.Adapter.CoreManager.Current.WorldFilter.GetByContainer(Decal.Adapter.CoreManager.Current.CharacterFilter.Id))
{
    if (wo.ObjectClass == Decal.Adapter.Wrappers.ObjectClass.Container) continue;
    if (wo.Values(Decal.Adapter.Wrappers.LongValueKey.EquippedSlots, 0) > 0) continue;
```

and subscribing looked like this
(`upstream-virindi/VVSPorts/GoArrow-VVS/GoArrow/PluginCore.cs`, lines 101-113 and 441):

```csharp
Core.CharacterFilter.LoginComplete += new EventHandler(CharacterFilter_LoginComplete);
Core.CharacterFilter.Logoff += new EventHandler<LogoffEventArgs>(CharacterFilter_Logoff);
Core.ChatBoxMessage += new EventHandler<ChatTextInterceptEventArgs>(ChatLinkHandler);
Core.CommandLineText += new EventHandler<ChatParserInterceptEventArgs>(ChatCommandHandler);
Core.CharacterFilter.SpellCast += new EventHandler<SpellCastEventArgs>(CharacterFilter_SpellCast);
Core.CharacterFilter.Death += new EventHandler<DeathEventArgs>(CharacterFilter_Death);
Core.WorldFilter.ChangeObject += new EventHandler<ChangeObjectEventArgs>(WorldFilter_ChangeObject);
Core.WorldFilter.CreateObject += new EventHandler<CreateObjectEventArgs>(WorldFilter_CreateObject);
Core.CharacterFilter.ChangePortalMode += new EventHandler<ChangePortalModeEventArgs>(CharacterFilter_ChangePortalMode);
Core.HotkeySystem.Hotkey += new EventHandler<HotkeyEventArgs>(HotkeySystem_Hotkey);
```

Actions went the other way through `Host.Actions`, and the verbs used by plugins
in this tree are `AddChatText`, `InvokeChatParser`, `SelectItem`,
`CurrentSelection`, `RequestId`, `MoveItem`, `FaceHeading`, `SetAutorun`,
`HeadingRadians`, `Landcell`, `LocationX`, `Region`, `RegionWindow`
(`upstream-virindi/VVSPorts/GoArrow-VVS/GoArrow/PluginCore.cs`,
`upstream-virindi/VVSPorts/SSSort-VVS/SSSort/PluginCore.cs`,
`.../GoArrow/Huds/ArrowHud.cs`, `.../Util.cs`).

There was no host tick. A per-frame callback existed but only down in COM, and
Virindi built its own timer abstraction on it — `iMyTimer` with `Tick`, `Start`
and `Stop` (`upstream-virindi/SharedCode/IMyTimer.cs`), implemented by counting
frames off the client's render hook
(`upstream-virindi/SharedCode/MyTimer.cs:65`):

```csharp
pHost.Underlying.Hooks.RenderPreUI += new Decal.Interop.Core.IACHooksEvents_RenderPreUIEventHandler(hooks_RenderPreUI);
CoreManager.Current.PluginTermComplete += new EventHandler<EventArgs>(Current_PluginTermComplete);
```

That line is the clearest single view of the layering: a managed `PluginHost`
whose `.Underlying` is the COM object, whose `Hooks` member raises a COM event
whose handler delegate is named for the connection-point interface,
`IACHooksEvents`. `PluginTermComplete` on `CoreManager.Current` was the global
teardown signal that static helpers hung their cleanup on.

The .NET wrappers sat over COM and it showed. `Decal.Interop.Filters`,
`Decal.Interop.Core`, `Decal.Interop.Inject`, `Decal.Interop.Net` and
`Decal.Interop.Input` are the type libraries (named in
`docs/ac-unreal-integration.md:24`, and used directly where the wrapper was
insufficient); there was also a managed `Decal.Filters` namespace, imported
alongside `Decal.Adapter` by the community plugins here and distinct from
`Decal.Interop.Filters`. When a wrapper did not expose what a plugin needed it reached
through `.Underlying` to the COM object and released it by hand — for buffed
skill values, VTClassic does exactly that
(`upstream/VTClassic-r162/VTClassic Shared/LootRules.cs:1244`, and again at 1743):

```csharp
Decal.Interop.Filters.SkillInfo skillinfo = null;
try {
    skillinfo = Decal.Adapter.CoreManager.Current.CharacterFilter.Underlying.get_Skill((Decal.Interop.Filters.eSkillID)(int)vk);
    return (skillinfo.Buffed >= keyval);
} finally {
    if (skillinfo != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(skillinfo);
}
```

**UI was XML view definitions loaded by the host.** A view was a document
shipped as an embedded resource and handed to Decal by name;
`upstream-virindi/ViewServiceConnector/Wrapper_Decal.cs:54` shows the two calls
that existed:

```csharp
public void Initialize(Decal.Adapter.Wrappers.PluginHost p, string pXML) {
    myView = p.LoadViewResource(pXML);   // by resource name
}
public void InitializeRawXML(Decal.Adapter.Wrappers.PluginHost p, string pXML) {
    myView = p.LoadView(pXML);           // by XML string
}
```

A real view, complete, from
`upstream-virindi/ViewServiceConnector/ExamplePlugin/ExamplePlugin/ViewXML/testlayout.xml`:

```xml
<?xml version="1.0"?>
<view icon="26075" title="Example Plugin" width="246" height="217">
    <control progid="DecalControls.FixedLayout" clipped="">
        <control progid="DecalControls.PushButton" name="bTest" left="128" top="128" width="104" height="32" text="Test Button"/>
        <control progid="DecalControls.Edit" name="txtTest" left="16" top="24" width="200" height="16" imageportalsrc="4726" text="Sample text"/>
        <control progid="DecalControls.Slider" name="sldTest" left="16" top="64" width="216" height="16" minimum="0" maximum="100" textcolor="0" vertical="0"/>
    </control>
</view>
```

The vocabulary is a COM ProgID per control, and counting them across every view
XML in `upstream-virindi/` gives the whole set actually used:
`DecalControls.StaticText` (112 uses), `Checkbox` (59), `PushButton` (57),
`TextColumn` (50), `FixedLayout` (36), `IconColumn` (26), `Edit` (25), `List`
(22), `Choice` (20), `CheckColumn` (19), `Button` (11), `Notebook` (9), `Slider`
(3), `Progress` (2). Nesting expressed layout — a `Notebook` containing `page`
elements containing a `FixedLayout` containing positioned controls
(`upstream-virindi/Examples/AutoWireupExamplePlugin/AutoWireupExamplePlugin/Views/MainView.xml`).
Controls were reached from code as `Decal.Adapter.Wrappers.ViewWrapper` plus a
typed wrapper per control (`PushButtonWrapper`, `CheckBoxWrapper`,
`TextBoxWrapper`, `ChoiceWrapper`, `SliderWrapper`, `ListWrapper`,
`StaticWrapper`, `NotebookWrapper`, `ProgressWrapper`, `ButtonWrapper`, all
enumerated in `Wrapper_Decal.cs:179`), and each wrapper's `.Underlying` was a
`Decal.Interop.Inject.ILayer` from which a `tagRECT` position could be read
(`Wrapper_Decal.cs:289`).

The attribute-driven wireup that made this pleasant was **not Decal's**. It is
Virindi's `MyClasses.MetaViewWrappers`, defined in this tree
(`upstream-virindi/ViewServiceConnector/Wrapper_WireupHelper.cs`) as
`MVViewAttribute`, `MVControlReferenceAttribute`,
`MVControlReferenceArrayAttribute`, `MVControlEventAttribute` and
`MVWireUpControlEventsAttribute`, and used like this
(`.../Examples/AutoWireupExamplePlugin/.../Views/MainView.cs`):

```csharp
[MVView("AutoWireupExamplePlugin.Views.MainView.xml")]
class MainView : IDisposable {
    public MainView() { MVWireupHelper.WireupStart(this, PluginCore.host); }
    [MVControlReference("lstObjects")] public IList lstObjects;
    [MVControlEvent("lstObjects", "Selected")]
    private void lstObjects_Selected(object sender, MVListSelectEventArgs e) { ... }
```

The whole reason that layer exists is that there was more than one view system —
`ViewSystemSelector.eViewSystem` is `{ DecalInject, VirindiViewService }` and the
tree also carries `VHS_Connector.cs`, `VHUDs_Connector.cs`, `VCS_Connector.cs`,
`Wrapper_MyHuds.cs` — and a plugin wanted to target whichever was installed.

**Versioning and isolation were the weak parts, and the code here is the
evidence.** Decal did not declare an API version to a plugin. To find out what
it was talking to, a plugin walked the loaded assemblies and read
`Decal.Adapter`'s version, then checked for individual members by reflection:

```csharp
foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
    AssemblyName nmm = a.GetName();
    if (nmm.Name == "Decal.Adapter") { iCachedDecalVersion = nmm.Version; ... }
}
...
MethodInfo call = DecalType_HooksWrapper.GetMethod("UIElementMove", new Type[] { DecalType_UIElementType, typeof(int), typeof(int) });
if (call == null) return;
call.Invoke(hooks, new object[] { e_decal, x, y });
```

(`upstream-virindi/SharedCode/DecalProxy.cs`.) Inter-plugin communication was the
same technique aimed at another plugin. There was no service registry and no
message bus; a plugin that wanted to call VirindiTank scanned for the `uTank2`
assembly at a minimum version and then went through a static singleton, keeping
the typed call in a separate method so the JIT would not need the absent type
unless the check had passed:

```csharp
public static bool IsVTankPresent(Version minimumversion) { ... if ((nmm.Name == "uTank2") && (nmm.Version >= minimumversion)) { ... return Curtain_IsVTankPresent(); } ... }
static bool Curtain_IsVTankPresent() { return uTank2.PluginCore.PC != null; }
```

(`upstream-virindi/Examples/VTankFreeMethodsTest/VTankFreeMethodsTest/VirindiTank_FreeConnector.cs`;
`ViewSystemSelector.cs` does the same for `VirindiViewService`.) The `Curtain_`
naming convention appears throughout. Plugins ran in the client's process; all
the evidence here is of one AppDomain shared by Decal, the adapter and every
plugin, which is why assembly-scanning worked at all.

### What could not be established about Decal

The material above is all *plugin-side*. Four things it cannot answer, and they
should not be guessed at:

The COM interface a plugin object itself implemented. Everything here derives
`Decal.Adapter.PluginBase` and lets the adapter deal with COM; no file in this
tree implements a Decal COM interface directly, so whether there was an
`IPluginSite`-style interface a plugin had to satisfy, and what it was called, is
not established from this evidence. What *is* established, from the names the
adapter leaks, is that `IACHooks`/`IACHooksEvents` existed in
`Decal.Interop.Core` (`upstream-virindi/SharedCode/MyTimer.cs:65`) and that
`Decal.Interop.Inject.ILayer` was the drawing primitive behind a control
(`upstream-virindi/ViewServiceConnector/Wrapper_Decal.cs:289`).

The registry layout. One key is observed first-hand —
`HKLM\SOFTWARE\Decal\Plugins\{GUID}` with a `ProfilePath` value — which shows
plugins were registered per-GUID under `SOFTWARE\Decal\Plugins` and could keep
their own values there. What the full set of values was, and whether registration
was pure COM registration or something Decal-specific in addition, is not
established here.

A filter as seen by its *author*. Every filter in this tree is consumed. What a
third party had to implement and register to add a filter, and whether filters
formed an ordered chain over the network stream that could modify or suppress
traffic, is not established from this material. The hint that ordering mattered
is that `virindi_public` ships a `DecalPluginOrderer/` tool
(`UPSTREAM.md:41`) — but what it ordered, plugins or filters, and by what
mechanism, was not read.

Whether a single plugin could be unloaded without restarting the client. Nothing
here attempts it, and every presence check in this tree assumes assemblies that
arrive and stay.

VirindiTank's own loot-plugin contract is worth noting as a second data point,
because it shows what a *host* interface looked like when a Decal plugin was
itself hosting plugins. `src/UTank2.Abstractions/LootPluginBase.cs` — a
clean-room reconstruction from r162's call sites — is an abstract base class with
`Startup()` returning a `LootPluginInfo` that declares the file extension the
plugin owns, `Shutdown()`, profile load/unload, editor open/close, and two
decision methods; host services arrive as settable properties (`Host`,
`GameState`) assigned before `Startup`; and optional behaviour is declared by
implementing marker interfaces —
`ILootPluginCapability_SalvageCombineDecision2`,
`ILootPluginCapability_GetExtraOptions`, with the latter returning an
`[Flags]` `eLootPluginExtraOption`. That is capability negotiation by interface
probe, and it is the pattern a host with no manifest reaches for.

## What we have

`src/AC.Host/Plugins/IPlugin.cs` is three members:

```csharp
public interface IPlugin {
    string Name { get; }
    void Startup(IHost host);
    void Shutdown();
}
```

and `IHost` is one object handed to `Startup`: `World`, `Character`, `Actions`,
`GameData`, `Log`, `Settings`, `GetSetting`, `GetDataDirectory`,
`RunOnGameThread`, `ShowInGame`, and seventeen events. Two design decisions in it
are good and should be preserved by anything added: every callback arrives on one
game thread in order, so plugins need no locking (`GameHost`'s summary comment,
and `IPlugin`'s remarks); and nothing in the contract mentions packets, ports or
ISAAC, so the transport can be replaced (`docs/plugin-host.md`, "Transport
independence is deliberate"). Event fan-out catches per-handler exceptions and
counts them in `Statistics.PluginExceptions` (`GameHost.Raise<T>` and
`GameHost.RaisePlain`), so one bad plugin does not take down the others. Citations
into `GameHost.cs` name members rather than lines below, because the file is being
edited as this is written and its line numbers have already moved.

Discovery is a directory scan for assemblies and a reflection pass for types
(`src/AC.Host/Plugins/PluginLoader.cs`). `EnumerateCandidates` prefers
`plugins/<name>/<name>.dll` but otherwise yields *every* `*.dll` in each
subdirectory and in the root, and `LoadAssembly` constructs a new load context
per candidate and instantiates every non-abstract `IPlugin` with a parameterless
constructor. There is no manifest, no id, no version, no declared dependency and
no declared capability. A plugin's identity is `Name`, a string used for its
settings prefix and data directory.

Each plugin does get its own `AssemblyLoadContext` with an
`AssemblyDependencyResolver`, and the contract assemblies are unified to the
host's copy by returning null from `Load` for anything already in the default
context — that part is equivalent to Chorizite's. But the context is constructed
`isCollectible: false`, so nothing can ever be unloaded.

Settings are a read-only `Dictionary<string, string>` built from `--set
Name:Key=Value` at startup (`GameHost._settings`, `GameHost.Settings`,
`GameHost.GetSetting`). Access is
`host.GetSetting(this, key)` returning a string or null, so plugins parse and
compare by hand — `!string.Equals(host.GetSetting(this, "Echo"), "false",
StringComparison.OrdinalIgnoreCase)` and
`string.Equals(host.GetSetting(this, "Loot"), "true", ...)` in
`VirindiTankPlugin.Startup` (`src/VirindiTank.Plugin/VirindiTankPlugin.cs`).
Nothing is written back and
nothing persists; a setting changed in the UI cannot be saved.

Plugins are registered by `GameHost.AddPlugin` and it throws if called after
`StartAsync` ("Plugins are added before the host starts.").
Startup order is whatever order the loader produced, which is directory
enumeration order. There is no way for one plugin to find another: `IHost`
exposes no plugin list and no registry. The consequence is already visible in our
own code — `src/AC.Host.Ui/MainForm.cs:292` reaches a plugin's decisions by
reflecting on an event *by name*:

```csharp
System.Reflection.EventInfo made = plugin.GetType().GetEvent("DecisionMade");
```

and then reads `Name`, `Id`, `Kind` and `RuleName` off the event argument by
property name (lines 321-328). That is the `Curtain_` pattern from
`DecalProxy.cs` reproduced fifteen years later, in the host itself, for want of a
registry.

UI, as committed, is a separate window (`src/AC.Host.Ui`) with a fixed set of tabs
the host knows about. A plugin cannot contribute a tab; the window knows about
VirindiTank specifically, by reflection.

The overlay changes that, and it is worth being precise about its state because it
moved while this note was being written. `native/ACUnrealOverlay/overlay_state.h`
defines one vocabulary for the injected C++ side — `Row` (a
`vector<string> cells` plus an `int tone` documented as "0 normal, 1 good (kept,
buffed), 2 bad (refused, debuffed), 3 muted (ignored)"), `Panel` (`title`,
`columns`, `rows`), `Status`, and `State` (a `Status`, a `vector<Panel>` and a
`revision` counter) — with `Command { name, value }` coming back, and a comment
that rows are "deliberately generic: the host decides what the columns mean, so a
new panel needs no change on this side of the pipe." As of this writing the
matching managed side exists in the working tree but is **not yet committed**
(`git status` shows `src/AC.Host/Plugins/OverlayPanels.cs`,
`src/AC.Host.Overlay/`, `src/AC.Injector/`,
`src/VirindiTank.Plugin/VirindiTankPanels.cs`,
`native/ACUnrealOverlay/overlay_ipc.cpp` as untracked and `GameHost.cs` as
modified), so what follows describes work in progress rather than the committed
contract.

The shape of it is the right one. `IOverlayPanels` is a separate optional
interface — "Optional, and separate from `IPlugin` so that a plugin with nothing
to show carries no UI concepts at all" — with a single method
`IReadOnlyList<OverlayPanel> GetPanels()`, and `OverlayPanel` is a title, column
headings and rows of `string` cells with an `OverlayTone`. It is pull rather than
push: `GameHost.CollectPanels()` type-tests each plugin, calls `GetPanels()` on
the game thread several times a second, and catches and counts a plugin that
throws exactly as event fan-out does, so "a broken panel costs the overlay one
table; it must not cost the session." `src/AC.Host.Overlay/` carries the named-pipe
server, a length-prefixed JSON codec and the state builder. `VirindiTankPlugin`
already declares `: IPlugin, IOverlayPanels`.

Two things are not there yet. The return path stops at the host: `OverlayServer`
raises `CommandReceived` on the pipe's own thread, and nothing in `IHost` or any
plugin-side interface lets a plugin receive an `OverlayCommand` — so a plugin can
report but cannot yet be operated. And `src/AC.Host.Ui` has not been moved onto
the same panels, so the reflection in `MainForm.cs` remains.

Scripting: none. A plugin is a compiled `net10.0` assembly referencing `AC.Host`.

## The gaps, in priority order

Priority here is by ratio of consequence to cost, and consequence is judged
against one question: what stops a second person writing a plugin for this host
without reading its source? Item 5 is listed in place for completeness but is
already most of the way built in the working tree, so the live worklist is 1, 2,
3, then 4, then the small remainder of 5.

**1. A manifest with an id, a version and a declared host API version.**
*Problem.* Today a plugin is any DLL in a directory. There is no id (so no stable
identity for settings, dependencies or a registry), no version (so nothing to
report or compare), and — worst — no way for the host to tell that a plugin was
built against an older `AC.Host`. Such a plugin fails at whatever point the
missing member is touched: a `TypeLoadException` or `MissingMethodException` at
use, or — if `GetTypes()` throws — a plugin that never appears at all, since
`LoadAssembly` falls back to `ex.Types` and skips the nulls while `LoadFrom` logs
the failure and moves on. Note that Chorizite does
*not* solve this either — there is no core-version field in its manifest or its
index models — so this is a place to go beyond both references rather than copy
one. The mechanism is small: a constant `HostApi.Version` in `AC.Host`, a
`"hostApi"` range in the manifest, and a refusal with a message naming both
versions.
*Cost.* Low. A `manifest.json` record, a JSON schema for editors, a loader that
reads the manifest before touching the assembly, and a rule that a directory
without a manifest is reported rather than scanned. Perhaps 250 lines with tests.
It also fixes the loader's current willingness to treat every dependency DLL in a
plugin folder as a candidate plugin, which is a real hazard once plugins ship
dependencies.

**2. A settings store with typed access, defaults and persistence.**
*Problem.* `--set Plugin:Key=Value` as the only channel means every setting is a
string parsed at each use, no setting has a declared default or type, nothing can
be changed at runtime, and nothing survives a restart. The UI has switches it
cannot save. This is also the cheapest thing that makes the host feel finished to
someone using it rather than building it.
*Cost.* Low. Chorizite's answer is worth copying almost exactly and it is barely
any code: one settings type per plugin, serialised to
`<dataDirectory>/settings.json` — it already has `GetDataDirectory` — loaded
before `Startup` and saved on `Shutdown`, with source-generated
`JsonTypeInfo<T>` so it survives trimming. Command-line `--set` stays as an
override on top. Add a change notification only if something needs it.

**3. A shared service registry, which is also how plugins find each other.**
*Problem.* `IHost` is a fixed set of properties. A plugin that offers something
to other plugins — a navigation service, a combat state machine, a UI host — has
no way to publish it, and a plugin that wants one has no way to ask. The proof
that this hurts is that our own UI already reflects on a plugin's event by name
(`src/AC.Host.Ui/MainForm.cs:292`), which is precisely the failure mode Decal
plugins lived with (`DecalProxy.cs`, `VirindiTank_FreeConnector.cs`). It will
hurt more immediately than it looks: nav, combat and HUD are all planned as
separate concerns over the same world model.
*Cost.* Low if kept to `bool TryGetService<T>(out T)` / `void
RegisterService<T>(T)` on `IHost` plus a dictionary in `GameHost`, with
registration during `Startup` and resolution allowed from `Startup` onwards.
Resist a DI container: Chorizite needs Autofac because it resolves plugin
constructors, and full constructor injection is what forces its reflection-heavy
`ResolveParameter`. A registry over a one-thread host does not need that.
Recommend *against* a general publish/subscribe message bus: with one game thread
and a service registry, typed events on a resolved service are simpler, are
discoverable at compile time, and do not invent a second event model beside
`IHost`'s.

**4. Dependencies and load order, once there is a manifest and a registry.**
*Problem.* Alone, this is not urgent: with one plugin there is no ordering
problem. It becomes necessary the moment (3) exists, because a plugin resolving
another's service needs the provider started first, and "started" has to be
ordered by something other than directory enumeration. Chorizite's syntax is
worth taking as-is — `"dependencies": [ "Nav@0.2.0" ]`, with a trailing `?` for
optional — along with the depth-first recursion and the clean refusal for a
missing non-optional dependency. Do not copy its version check, which compares
the wrong operand and only warns; compare the provider's version and refuse.
*Cost.* Low, and mostly already paid once the manifest exists: a field, a
topological start, cycle detection, and refusal messages. Fifty lines plus tests.

**5. A UI declaration that does not link against a UI framework — largely done,
with the return path missing.**
*Problem.* The overlay is a separate injected C++ process and the window is
WinForms; a plugin must not depend on either. Chorizite's answer does not
transfer: there, UI is a plugin that other plugins reference at compile time
(`RmlUiPlugin rmlUi` in `ACPlugin`'s constructor, `.rml` assets on disk), which
works because RmlUi is in-process. Ours has a process boundary.
*State.* The uncommitted work described above already does the hard half the right
way: `IOverlayPanels` as an optional interface off `IPlugin`, panels as titles,
column headings and rows of string cells with a tone, pulled on the game thread
with per-plugin exception isolation. Nothing in it mentions a window, a frame or
ImGui. The remaining work is the return path — a plugin-side way to receive the
`OverlayCommand` that `OverlayServer.CommandReceived` already delivers to the
host, marshalled onto the game thread like every other callback — and moving
`src/AC.Host.Ui` onto the same panels, which is what would delete the reflection
in `MainForm.cs`.
*Honest limitation, worth writing into the contract's own documentation:* rows and
cells cannot express a graph, a map, or an editable form, and stretching them to
try would produce an awkward middle layer. `.utl` profile editing stays a separate
window.
*Cost.* The contract side is small and mostly paid. The overlay process, the pipe
and injection into a UE5 client are substantial, but that work is independent of
the plugin contract, which is the argument for having defined the data shape
first.

**6. Unload and reload without restarting the host.**
*Problem.* Today `tools/run-host.ps1` exists because restarting by hand has three
ordered steps that fail quietly (`README.md`). Reload would remove that. But the
cost is real and Chorizite's source documents it honestly: `isCollectible: true`
contexts, unloading dependents first, up to fifty
`GC.Collect()`/`WaitForPendingFinalizers()` rounds, a check that still sometimes
logs `Failed to unload plugins`, and a reflection hack to clear
`System.Text.Json`'s type cache. On top of that, every plugin must give up its
object identity on reload, which is why `ISerializeState<T>` exists there.
*Recommendation.* Do it, but not third, and in two stages. First make the
lifecycle honest: permit plugins to be added and removed while running (today
`AddPlugin` throws after start), drain the game-thread queue, unsubscribe on
`Shutdown`. That much makes a *stop, rebuild, start* cycle work in-process
without a new host, which is most of the practical benefit of the script. Only
then switch the load contexts to collectible and accept the GC dance. Note the
sequencing dependency: reload is far less painful once (2) exists, because
settings already round-trip through disk, and a plugin that has no in-memory
state to preserve does not need `ISerializeState<T>` at all.
*Cost.* Medium to high. The collectible-ALC half is the kind of work that looks
finished and is not; budget for a test that asserts the context is actually
collected, because without one the failure is a slow leak.

**7. Scripting for plugins that are not compiled assemblies.**
*Problem.* A loot profile is already data, so the obvious scripting audience is
the next tier up: "when my health drops below 40%, quaff" — logic too specific to
ship and too small to justify a project file and a build. Chorizite's structure
here is excellent and is the part to copy even if the feature is deferred: the
loader is an extension point, and Lua is a plugin that registers a loader
claiming `.lua` entry files, with the API exposed as named modules
(`RegisterLuaModule("PluginManager", ...)`) and generated type definitions for
editor completion.
*Recommendation.* Adopt the *shape* now — an `IPluginLoader`-style abstraction
selected by the manifest's entry file, so the assembly loader is one
implementation rather than the only path — and defer the script runtime itself.
It is the lowest-value item on this list against the effort: a Lua or C#-script
runtime, a binding layer, sandboxing decisions, and the documentation to make any
of it usable. Chorizite's Lua plugin vendors an entire XLua tree plus a native
DLL to do this. Do not start it before 1-5 are done.

### One thing deliberately not recommended

A `Decal.Adapter`-compatible facade. `docs/plugin-host.md` already argues this
should be scoped from the call sites of the first plugin someone actually wants
ported rather than built speculatively, and the evidence gathered here
strengthens that: the surface reached by real plugins in `upstream-virindi/` is
not just `CoreManager.Current.WorldFilter` and `CharacterFilter` but
`.Underlying` COM objects with manual `ReleaseComObject`, `Decal.Interop.Inject.ILayer`
positions, `HooksWrapper`, the `DecalControls.*` view vocabulary and
`HotkeySystem`. Reproducing enough of that to compile an arbitrary plugin is a
much larger job than reproducing enough to compile a chosen one, and nothing
above depends on it.
