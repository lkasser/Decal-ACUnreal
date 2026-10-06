using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AC.Dat;
using AC.Host.Plugins;
using Decal.Adapter;
using Decal.Adapter.Hosting;

[assembly: PluginApi(HostApi.Version)]

namespace Decal.Compat
{
    /// <summary>One Decal plugin, loaded or not, as the compatibility plugin lists it.</summary>
    public sealed class DecalPluginEntry
    {
        internal DecalPluginEntry(DecalPluginCandidate candidate)
        {
            AssemblyPath = candidate.AssemblyPath;
            Source = candidate.Source;
            Name = candidate.Name;
            Version = candidate.Version ?? string.Empty;
            Clsid = candidate.Registered?.Clsid;
            RegisteredEnabled = candidate.Registered?.Enabled ?? true;
            ReplacedBy = candidate.ReplacedBy;
            CannotRun = candidate.CannotRun;
            CannotRunDetail = candidate.CannotRunDetail;
            Facts = candidate.Facts;
        }

        /// <summary>
        /// What it is listed as: the name Decal's registry gives it, or - for one from the folder -
        /// its friendly name once it has loaded, and its file's until then.
        /// </summary>
        public string Name { get; internal set; }

        /// <summary>Its assembly, where it is installed; null for a registered plugin that names none, a native one.</summary>
        public string AssemblyPath { get; }

        /// <summary>
        /// For a registered plugin, the copy of its install folder it runs from, once it has
        /// been loaded (<see cref="WorkingCopy"/>); null for one from the folder, which runs in place.
        /// </summary>
        public string WorkingDirectory { get; internal set; }

        /// <summary>Where it was found: "folder" or "registry".</summary>
        public string Source { get; }

        /// <summary>Whether Decal's registry lists it, rather than the compatibility plugin's own folder holding it.</summary>
        public bool IsRegistered => Source == DecalPluginCatalog.RegistrySource;

        /// <summary>The class id Decal's registry keeps it under; null for one from the folder.</summary>
        public string Clsid { get; }

        /// <summary>Whether it is ticked in Decal's own list - what it follows here until the player chooses otherwise.</summary>
        public bool RegisteredEnabled { get; }

        /// <summary>Why the host does not load it because it does its job itself; null when it does not.</summary>
        public string ReplacedBy { get; }

        /// <summary>Why it cannot be tried in this host at all, in a few words; null when it can.</summary>
        public string CannotRun { get; }

        /// <summary>Whether it is ever loaded here: neither replaced nor impossible.</summary>
        public bool CanRun => ReplacedBy == null && CannotRun == null;

        public string Version { get; internal set; } = string.Empty;

        /// <summary>Whether the player wants it running in this host. Remembered between sessions.</summary>
        public bool Enabled { get; internal set; } = true;

        /// <summary>Running, off, or what went wrong, in a few words.</summary>
        public string Status { get; internal set; } = "not loaded";

        /// <summary>The whole reason for <see cref="Status"/>: why it runs, why it does not, what would change that.</summary>
        public string Detail { get; internal set; } = string.Empty;

        public bool IsRunning => Extensions.Count > 0;

        /// <summary>Where it stands, for the picture a list gives it.</summary>
        public HostedPluginState State
            => ReplacedBy != null ? HostedPluginState.Replaced
                : CannotRun != null ? HostedPluginState.CannotRun
                : IsRunning ? HostedPluginState.Running
                : !Enabled ? HostedPluginState.Off
                : HostedPluginState.Failed;

        internal string CannotRunDetail { get; }

        internal PluginFacts Facts { get; }

        /// <summary>The plugins and filters it holds that are running, in the order they started.</summary>
        internal List<Extension> Extensions { get; } = new List<Extension>();

        /// <summary>What it holds that has been found and not yet started.</summary>
        internal List<Type> Pending { get; } = new List<Type>();

        internal DecalPluginLoadContext Context { get; set; }

        internal string CopyDirectory { get; set; }

        /// <summary>What has gone wrong in it since it was loaded - a handler that threw, a message box - the first few.</summary>
        internal List<string> Faults { get; } = new List<string>();

        /// <summary>The native DLLs it was refused that its faults already say.</summary>
        internal HashSet<string> NativeNoted { get; } = new HashSet<string>(StringComparer.Ordinal);

        public override string ToString() => $"{Name} ({Status})";
    }

    /// <summary>What the compatibility plugin remembers between sessions.</summary>
    public sealed class DecalCompatSettings
    {
        /// <summary>The Decal plugins from its own folder the player has switched off, by name.</summary>
        public List<string> Disabled { get; set; } = new List<string>();

        /// <summary>
        /// The player's choice for each plugin in Decal's registry that differs from Decal's own
        /// tick, by class id: true to run one Decal has off, false to leave out one it has on. A
        /// plugin not here follows Decal's tick. Decal's registry itself is never written.
        /// </summary>
        public Dictionary<string, bool> Registered { get; set; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs Decal's .NET plugins in the host: finds them, loads each in a context of its own
    /// against the Decal.Adapter and Virindi View Service stand-ins, starts them as Decal did,
    /// and puts their windows in the overlay beside every other plugin's.
    /// </summary>
    /// <remarks>
    /// A separate plugin rather than part of Decal.Adapter, because the stand-in's only job is
    /// to be Decal.Adapter, and an assembly that is also a host plugin would have to be both
    /// the thing Decal plugins bind to and the thing that binds them. Kept apart, the stand-ins
    /// are what the plugins see and this is what the host sees.
    ///
    /// <para>
    /// Plugins are found where Decal found them - every entry under Decal's Plugins key, each
    /// ticked or not as in Decal's own list and loaded from where it is installed - and in a
    /// folder of this plugin's own: "Decal Plugins" in its data directory unless the setting
    /// DecalCompat:Folder names another, laid out as the host's plugins are, where a plugin needs
    /// no registry entry and is treated as a registered one's working copy is
    /// (<see cref="DecalPluginLoadContext"/>) - Mag-Filter, built from its sources, goes there.
    /// DecalCompat:Registry=false leaves Decal's registry alone; DecalCompat:Skip names plugins
    /// to leave out; DecalCompat:VirindiViewService=false hides VVS, so plugins fall back to
    /// Decal's own views; DecalCompat:UserFolders names a folder to stand in for the player's
    /// Documents and the rest, where plugins such as Mag-Tools keep their files - the real ones
    /// otherwise.
    /// </para>
    ///
    /// <para>
    /// Not everything registered is loaded. Virindi Tank, Virindi HUDs and the two hotkey systems
    /// are this host's own work and are listed as replaced (<see cref="Replacements"/>); a native
    /// plugin or a missing file cannot run here and says so. Everything else that is ticked is
    /// tried, and one that does not start says exactly why.
    /// </para>
    ///
    /// <para>
    /// Each Decal plugin can be switched off, on or reloaded while the host runs, as the host's
    /// own plugins can, and the choice is remembered in this plugin's own settings - for a
    /// registered one, only where it differs from Decal's tick, and never in Decal's registry.
    /// One switched on while the host runs is told what the others were told as they started:
    /// that everything is up and, in the world, that the character has logged in.
    /// Everything happens on the game thread, as it does for every plugin.
    /// </para>
    /// </remarks>
    public sealed class DecalCompatPlugin : IPlugin, IOverlayViews, IHostedPlugins, IChatCommands, IOverlayHotkeys
    {
        /// <summary>The name it goes by; "Decal" is the host's own window.</summary>
        public const string PluginName = "DecalCompat";

        private readonly List<DecalPluginEntry> _entries = new List<DecalPluginEntry>();
        private readonly IDecalRegistry _registry;
        private IHost _host;
        private DecalRuntime _runtime;
        private PluginSettings<DecalCompatSettings> _settings;
        private Dictionary<string, Func<Assembly>> _shims;
        private string _copyRoot;
        private string _workingRoot;

        /// <summary>
        /// Native DLLs for this process given to plugins in place of their own: the "native" folder
        /// beside Decal Compat, which ships a 64-bit sqlite3.dll for Virindi's tools, then the one
        /// in its data, where a player may put others.
        /// </summary>
        private IReadOnlyList<string> _nativeFolders = Array.Empty<string>();
        private IReadOnlyList<OverlayViewWindow> _windows;
        private bool _readRegistry;
        private HashSet<string> _skip;
        private PluginDialogs _dialogs;
        private readonly HashSet<System.Threading.Timer> _timers = new HashSet<System.Threading.Timer>();

        /// <summary>How many faults an entry keeps for its status: enough to say what is wrong, not a log.</summary>
        private const int FaultsKept = 3;

        /// <summary>Reads Decal's plugins from the real registry, as the host loads it.</summary>
        public DecalCompatPlugin()
            : this(null)
        {
        }

        /// <param name="registry">Where Decal's plugin list is read from; the real registry when null.</param>
        public DecalCompatPlugin(IDecalRegistry registry)
        {
            _registry = registry ?? new WindowsDecalRegistry();
        }

        public string Name => PluginName;

        /// <summary>Every Decal plugin found: Decal's registered ones in its order, then the folder's.</summary>
        public IReadOnlyList<DecalPluginEntry> Entries => _entries;

        /// <summary>The running Decal, while this plugin is running.</summary>
        public DecalRuntime Runtime => _runtime;

        /// <summary>The folder Decal plugins are looked for in.</summary>
        public string Folder { get; private set; }

        /// <summary>Raised when the list or any entry in it changes.</summary>
        public event EventHandler Changed;

        /// <summary>Every open Decal window, each by its owner's name.</summary>
        public IReadOnlyList<OverlayViewWindow> Views
        {
            get
            {
                if (_runtime == null)
                    return Array.Empty<OverlayViewWindow>();

                return _windows ??= _runtime.Views.Select(v => new OverlayViewWindow(v.Key, v.View, startsClosed: !v.Visible)).ToList();
            }
        }

        public void Startup(IHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _settings = host.LoadSettings<DecalCompatSettings>(this);
            _settings.Value.Disabled ??= new List<string>();
            _settings.Value.Registered = new Dictionary<string, bool>(_settings.Value.Registered ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);

            _runtime = new DecalRuntime(host)
            {
                VirindiViewServiceRunning = !IsFalse(host.GetSetting(this, "VirindiViewService")),
                Owner = this,
            };
            _runtime.ViewsChanged += (_, _) => _windows = null;
            _runtime.Tick += OnTick;
            _runtime.Later = Later;
            _runtime.Faulted += OnFaulted;
            _runtime.ChatShown += OnChatShown;

            // What plugins' ServerDispatch and ClientDispatch read messages against: Decal's own
            // messages.xml if it is a later revision than the copy built in, as Decal's Update
            // button would have fetched; said once, since a field read wrong starts here.
            _runtime.Messages = MessageSchema.Choose(InstalledMessagesFile(), out string schemaWhy);
            host.Log.Info($"Decal: network messages are read against {schemaWhy}.");

            // Startup runs on the game thread, which is the thread whose message boxes these are.
            if (!IsTrue(host.GetSetting(this, "ShowMessageBoxes")))
                _dialogs = PluginDialogs.Install(OnDialog);

            // Plugins look for Virindi View Service among the assemblies already loaded before
            // they touch it, so it has to be loaded before they start.
            RuntimeHelpers.RunClassConstructor(typeof(VirindiViewService.Service).TypeHandle);
            VirindiHotkeySystem.VHotkeySystem.InstanceReal.HotkeyListChanged += OnHotkeysChanged;
            _hotkeys = null;

            _shims = new Dictionary<string, Func<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Decal.Adapter"] = () => typeof(PluginBase).Assembly,
                ["VirindiViewService"] = () => typeof(VirindiViewService.Service).Assembly,
                ["Decal.FileService"] = () => typeof(Decal.Filters.FileService).Assembly,
                ["Decal.Interop.Core"] = () => typeof(Decal.Interop.Core.IPluginSite2).Assembly,
                // Virindi Hotkey System, whose job the host's hotkey windows do: Mag-Tools binds
                // its hotkeys through it at login (IOverlayHotkeys, below).
                ["VirindiHotkeySystem"] = () => typeof(VirindiHotkeySystem.VHotkeySystem).Assembly,
                // Virindi HUDs' Status HUD, which Virindi Tank draws: Mag-Tools puts its rows on it.
                // Loaded only for a plugin that names it, since Virindi Reporter and Sense feed it
                // whenever they find it loaded, and Virindi Tank already shows Reporter's rows.
                ["VirindiHUDs"] = StatusHud,
                // Virindi Tank's API for other plugins, answering as it did with no loot profile:
                // Mag-Tools' looter asks it about every open container. Loaded only when asked for,
                // for Integrator2 and Item Tool look for it among the loaded assemblies.
                ["uTank2"] = TankApi,
                // Managed DirectX's maths, which plugins that draw for themselves - Integrator2's
                // map - work out their transforms with, and VVS's DxTexture takes.
                ["Microsoft.DirectX"] = () => typeof(Microsoft.DirectX.Matrix).Assembly,
            };

            string data = host.GetDataDirectory(this);
            _copyRoot = Path.Combine(data, "running", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            _workingRoot = Path.Combine(data, "Registered");
            // A registered plugin reading its own Path from Decal's registry is told its working copy.
            Decal.Adapter.Hosting.PluginRegistry.FolderFor = WorkingFolderFor;

            // The player's Documents and the rest, where plugins keep what is the player's - the
            // real ones, unless the host was given a folder to stand in for them.
            Decal.Adapter.Hosting.PluginFolders.Root = host.GetSetting(this, "UserFolders");

            _nativeFolders = new[]
            {
                Path.Combine(Path.GetDirectoryName(typeof(DecalCompatPlugin).Assembly.Location) ?? string.Empty, "native"),
                Path.Combine(data, "native"),
            };
            ClearOldCopies(Path.Combine(data, "running"));

            Folder = host.GetSetting(this, "Folder") ?? Path.Combine(data, "Decal Plugins");
            TryCreate(Folder);

            _readRegistry = !IsFalse(host.GetSetting(this, "Registry"));
            _skip = new HashSet<string>(
                (host.GetSetting(this, "Skip") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            // Decal's own FileService, which Decal ran as a filter before any plugin, and which
            // plugins reach through Core.FileService for spell names and icons.
            try
            {
                _runtime.Start(new Decal.Filters.FileService(), AppContext.BaseDirectory);
            }
            catch (Exception ex)
            {
                host.Log.Error("Decal's FileService did not start; plugins asking it for spells will find none.", ex);
            }

            StartAll(Discover());
            _runtime.CompleteStartup();

            int registered = _entries.Count(e => e.IsRegistered);
            host.Log.Info(_entries.Count == 0
                ? $"Decal: no Decal plugins found - none registered with Decal{(_readRegistry ? string.Empty : " (not read: DecalCompat:Registry=false)")}, and none in {Folder}."
                : $"Decal: {_entries.Count(e => e.IsRunning)} of {_entries.Count} Decal plugin(s) running - {registered} registered with Decal"
                    + $" ({_entries.Count(e => e.ReplacedBy != null)} replaced by this host, {_entries.Count(e => e.CannotRun != null)} unable to run here,"
                    + $" {_entries.Count(e => e.IsRegistered && e.CanRun && !e.Enabled)} off), {_entries.Count - registered} from {Folder}.");
            OnChanged();
        }

        public void Shutdown()
        {
            foreach (DecalPluginEntry entry in _entries.Where(e => e.IsRunning).Reverse().ToList())
                Stop(entry, "stopped");

            _runtime?.Dispose();
            _runtime = null;
            _windows = null;
            VirindiHotkeySystem.VHotkeySystem.InstanceReal.HotkeyListChanged -= OnHotkeysChanged;
            VirindiHotkeySystem.VHotkeySystem.InstanceReal.Release(_ => true);
            _hotkeys = null;
            lock (_timers)
            {
                foreach (System.Threading.Timer timer in _timers)
                    timer.Dispose();
                _timers.Clear();
            }
            _dialogs?.Dispose();
            _dialogs = null;
            Decal.Adapter.Hosting.PluginRegistry.FolderFor = null;
            Decal.Adapter.Hosting.PluginFolders.Root = null;
            TryDelete(_copyRoot);
        }

        /// <summary>
        /// Switches a Decal plugin on or off in this host, and remembers the choice. A plugin the
        /// host replaces cannot be switched; one that cannot run here keeps the choice for when it can.
        /// </summary>
        public void SetEnabled(DecalPluginEntry entry, bool enabled)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (entry.ReplacedBy != null)
            {
                _host.Log.Info($"Decal plugin {entry.Name} is not switched: {entry.ReplacedBy}");
                OnChanged();
                return;
            }

            entry.Enabled = enabled;
            if (!entry.CanRun)
                SettleUnloaded(entry);
            else if (enabled && !entry.IsRunning)
                Load(entry);
            else if (!enabled && entry.IsRunning)
                Stop(entry, "off");
            else if (!enabled)
                SettleUnloaded(entry);

            Remember(entry);
            _host.Log.Info($"Decal plugin {entry.Name} switched {(enabled ? "on" : "off")} in this host."
                + (entry.IsRegistered ? $" Decal's own list still has it {(entry.RegisteredEnabled ? "on" : "off")}." : string.Empty));
            OnChanged();
        }

        /// <summary>Shuts a Decal plugin down and loads it again, picking up a rebuilt copy.</summary>
        public void Reload(DecalPluginEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (!entry.CanRun)
                return;

            if (entry.IsRunning)
                Stop(entry, "reloading");

            if (entry.Enabled)
                Load(entry);

            OnChanged();
        }

        /// <summary>A Decal plugin by the name it is listed as, its file's name, or its class id.</summary>
        public DecalPluginEntry Find(string name)
            => _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? _entries.FirstOrDefault(e => e.AssemblyPath != null && string.Equals(Path.GetFileNameWithoutExtension(e.AssemblyPath), name, StringComparison.OrdinalIgnoreCase))
                ?? _entries.FirstOrDefault(e => Replacements.SameClsid(e.Clsid, name));

        /// <summary>The Decal plugins, for Decal's window to list under this one. Game thread only.</summary>
        public IReadOnlyList<HostedPluginInfo> HostedPlugins
        {
            get
            {
                // A native DLL is asked for when a plugin first calls into it, which may be long
                // after it started: what it was refused since the last look goes into its status now.
                foreach (DecalPluginEntry entry in _entries.Where(e => e.IsRunning))
                    NoteNativeProblems(entry);

                return _entries.Select(e => new HostedPluginInfo(e.Name, e.Version, e.Enabled, e.Status, e.Detail, e.State, canSwitch: e.ReplacedBy == null)).ToList();
            }
        }

        public void SetHostedEnabled(string name, bool enabled)
        {
            DecalPluginEntry entry = Find(name);
            if (entry != null && (entry.Enabled != enabled || entry.ReplacedBy != null))
                SetEnabled(entry, enabled);
        }

        public void ReloadHosted(string name)
        {
            DecalPluginEntry entry = Find(name);
            if (entry != null)
                Reload(entry);
        }

        /// <summary>
        /// Reads Decal's registry and the folder again, as DenAgent's Refresh List did, and starts
        /// whatever is new and ticked.
        /// </summary>
        public void RescanHosted()
        {
            if (_runtime == null)
                return;

            List<DecalPluginEntry> found = Discover();
            StartAll(found);
            if (found.Count > 0)
                _host.Log.Info($"Decal: found {found.Count} more Decal plugin(s).");
            OnChanged();
        }

        /// <summary>
        /// Offers a line the player typed to the Decal plugins, as Decal did - their /commands.
        /// True when one of them claimed it.
        /// </summary>
        public bool InvokeCommandLine(string text) => _runtime != null && _runtime.InvokeCommandLine(text);

        /// <summary>A command from elsewhere in the host, offered to the Decal plugins as though typed.</summary>
        public bool TryCommand(string text) => InvokeCommandLine(text);

        /// <summary>
        /// Decal plugins' command words this host knows, by the plugin's class id or assembly file.
        /// A Decal plugin never says which words are its own, so a line typed for one went to the
        /// server - "/mt face 90" said aloud - unless the player listed the word in
        /// "Decal:CommandWords"; these need no listing.
        /// </summary>
        private static readonly (string Clsid, string AssemblyFileName, string[] Words)[] KnownWords =
        {
            ("{959D5CA6-0BD5-48A2-9AD2-F95F94DCDC3E}", "MagTools.dll", new[] { "mt" }),
            // Mag-Filter, a network filter: Decal listed filters apart from its plugins, so it is
            // known by its file alone - as third_party\Mag-Filter builds it, into the folder.
            (null, "MagFilter.dll", new[] { "mf" }),
        };

        /// <summary>
        /// The command words of the Decal plugins listed that are known to have some (<see cref="KnownWords"/>)
        /// and can run here, switched on or not: a line typed for one is kept from the server, and
        /// said to be untaken if its plugin is off.
        /// </summary>
        public IReadOnlyCollection<string> CommandWords
            => _entries.Where(e => e.CanRun)
                .SelectMany(e => KnownWords.Where(k => Replacements.SameClsid(e.Clsid, k.Clsid)
                                                       || (e.AssemblyPath != null && string.Equals(Path.GetFileName(e.AssemblyPath), k.AssemblyFileName, StringComparison.OrdinalIgnoreCase)))
                                           .SelectMany(k => k.Words))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        // ------------------------------------------------------------------- hotkeys

        private HotkeyDefinition[] _hotkeys;

        /// <summary>
        /// The hotkeys Decal plugins added to Virindi Hotkey System, which the host's hotkey windows
        /// list and bind as they do every plugin's own, under the name each was added with:
        /// "vhs/Mag-Tools/Pack Inventory". The key suggested is the one it was made with.
        /// </summary>
        public IReadOnlyList<HotkeyDefinition> Hotkeys
            => _hotkeys ??= VirindiHotkeySystem.VHotkeySystem.InstanceReal.AllHotkeys
                .Select(h => new HotkeyDefinition(HotkeyId(h), h.Description, DefaultKeys(h), h.HotkeyName, h.AssemblyName))
                .ToArray();

        /// <summary>A key bound to one of a Decal plugin's hotkeys was pressed: its handlers are told, as VHS told them.</summary>
        public void HotkeyPressed(string id)
        {
            VirindiHotkeySystem.VHotkeyInfo hotkey = VirindiHotkeySystem.VHotkeySystem.InstanceReal.AllHotkeys.FirstOrDefault(h => HotkeyId(h) == id);
            if (hotkey == null || !hotkey.Enabled || _runtime == null)
                return;

            try
            {
                VirindiHotkeySystem.VHotkeySystem.InstanceReal.Press(hotkey);
            }
            catch (Exception ex)
            {
                DecalPluginEntry entry = EntryOf(hotkey.Owner);
                DecalFailure failure = entry == null ? null : DecalFailure.Explain(ex, entry.Facts, entry.Context?.NativeProblems, Path.GetDirectoryName(entry.AssemblyPath));
                _host.Log.Error($"Decal plugin {entry?.Name ?? hotkey.AssemblyName}'s hotkey \"{hotkey.HotkeyName}\" failed.", DecalFailure.Unwrap(ex));
                if (entry != null)
                    AddFault(entry, $"{When(entry)}its hotkey \"{hotkey.HotkeyName}\" failed: {failure?.Detail ?? "it threw."}");
            }
        }

        private static string HotkeyId(VirindiHotkeySystem.VHotkeyInfo hotkey) => "vhs/" + hotkey.AssemblyName + "/" + hotkey.HotkeyName;

        /// <summary>The key a hotkey was made with, as the host writes keys; none for a mouse button or no key.</summary>
        private static string DefaultKeys(VirindiHotkeySystem.VHotkeyInfo hotkey)
            => hotkey.VirtualKey is >= 1 and <= 254 ? new KeyChord(hotkey.VirtualKey, hotkey.ControlState, hotkey.ShiftState, hotkey.AltState).ToString() : string.Empty;

        private void OnHotkeysChanged(object sender, EventArgs e) => _hotkeys = null;

        // ------------------------------------------------------------------- finding

        /// <summary>
        /// Everything registered or in the folder not already listed, added to the list in that
        /// order, each marked on or off - by the player's choice, else Decal's tick - with the
        /// reason for any that cannot run, and each registered one said in the log.
        /// </summary>
        private List<DecalPluginEntry> Discover()
        {
            IReadOnlyList<DecalPluginCandidate> folder = DecalPluginCatalog.FindInFolder(Folder);
            IReadOnlyList<DecalPluginCandidate> registered = _readRegistry
                ? DecalPluginCatalog.ReadRegistered(_registry, _skip, folder)
                : Array.Empty<DecalPluginCandidate>();

            List<DecalPluginEntry> added = new List<DecalPluginEntry>();
            foreach (DecalPluginCandidate candidate in registered.Concat(folder))
            {
                if (Known(candidate))
                    continue;

                // One put in the folder by hand is held to the same rule: nothing the host does
                // itself runs twice.
                if (!candidate.Source.Equals(DecalPluginCatalog.RegistrySource, StringComparison.Ordinal))
                    candidate.ReplacedBy = Replacements.ReasonForFile(candidate.AssemblyPath);

                DecalPluginEntry entry = new DecalPluginEntry(candidate);
                entry.Enabled = ChosenEnabled(entry);
                _entries.Add(entry);
                added.Add(entry);

                if (!entry.CanRun || !entry.Enabled)
                    SettleUnloaded(entry);

                if (entry.IsRegistered)
                    _host.Log.Info($"Decal's registry lists {entry.Name} {entry.Clsid}{(entry.AssemblyPath != null ? " at " + entry.AssemblyPath : " (native)")}, "
                        + (entry.RegisteredEnabled ? "ticked" : "not ticked") + " in Decal's list: "
                        + (!entry.CanRun ? entry.Detail : entry.Enabled ? "loading it." : entry.Detail));
            }

            return added;
        }

        private bool Known(DecalPluginCandidate candidate)
        {
            if (candidate.Registered != null)
                return _entries.Any(e => e.IsRegistered && Replacements.SameClsid(e.Clsid, candidate.Registered.Clsid));

            return _entries.Any(e => !e.IsRegistered && string.Equals(e.AssemblyPath, candidate.AssemblyPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Whether the player wants it running: their choice here if they made one, else Decal's tick.</summary>
        private bool ChosenEnabled(DecalPluginEntry entry)
        {
            if (entry.IsRegistered)
                return _settings.Value.Registered.TryGetValue(Replacements.NormaliseClsid(entry.Clsid), out bool chosen) ? chosen : entry.RegisteredEnabled;

            return !IsDisabled(entry.Name);
        }

        // ------------------------------------------------------------------- loading

        /// <summary>
        /// Every assembly loaded first, then every filter started, then every plugin: Decal
        /// started its filters before any plugin, and a plugin's Startup may ask for one.
        /// </summary>
        private void StartAll(IEnumerable<DecalPluginEntry> entries)
        {
            List<DecalPluginEntry> loaded = new List<DecalPluginEntry>();
            foreach (DecalPluginEntry entry in entries)
            {
                if (!entry.CanRun || !entry.Enabled)
                    continue;

                if (!Prepare(entry))
                    continue;

                // A folder plugin's friendly name is known only once it has loaded.
                if (!entry.IsRegistered && IsDisabled(entry.Name))
                {
                    entry.Enabled = false;
                    Unload(entry);
                    SettleUnloaded(entry);
                    continue;
                }

                loaded.Add(entry);
            }

            foreach (DecalPluginEntry entry in loaded)
                StartPrepared(entry, filters: true);

            foreach (DecalPluginEntry entry in loaded)
                StartPrepared(entry, filters: false);

            foreach (DecalPluginEntry entry in loaded)
            {
                if (Settle(entry))
                    _runtime.CatchUp(entry.Extensions.ToArray());
            }
        }

        /// <summary>
        /// Loads and starts one Decal plugin assembly, filters first, and - since the others were
        /// told long ago - tells it that everything is up and, in the world, that the character
        /// has logged in (<see cref="DecalRuntime.CatchUp"/>).
        /// </summary>
        private bool Load(DecalPluginEntry entry)
        {
            if (!Prepare(entry))
                return false;

            StartPrepared(entry, filters: true);
            StartPrepared(entry, filters: false);
            if (!Settle(entry))
                return false;

            _runtime.CatchUp(entry.Extensions.ToArray());
            return true;
        }

        /// <summary>
        /// Loads the assembly in a context of its own and finds what in it can be started,
        /// starting nothing yet.
        /// </summary>
        private bool Prepare(DecalPluginEntry entry)
        {
            string install = Path.GetDirectoryName(entry.AssemblyPath);
            string load = entry.AssemblyPath;
            if (entry.IsRegistered)
            {
                // Never from the player's install: from a copy of it, which is where it will find
                // its files and keep what it writes.
                if (!RefreshWorkingCopy(entry, install))
                    return false;

                load = Path.Combine(entry.WorkingDirectory, Path.GetFileName(entry.AssemblyPath));
            }

            // A registered plugin's working copy is treated already; one from the folder is treated
            // as it loads, from a corrected copy when that changes anything.
            string copy = Path.Combine(_copyRoot, Path.GetFileNameWithoutExtension(entry.AssemblyPath) + "-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            DecalPluginLoadContext context = new DecalPluginLoadContext(entry.Name, _shims, Path.GetDirectoryName(load), copy, RunningPluginAssembly, _nativeFolders,
                                                                        treat: !entry.IsRegistered);

            Assembly assembly;
            try
            {
                assembly = context.LoadPluginAssembly(load);
            }
            catch (Exception ex)
            {
                DecalFailure failure = DecalFailure.ExplainLoad(ex, entry.Facts, context.NativeProblems, install);
                return Fail(entry, context, copy, failure, $"Decal plugin {entry.Name} could not be loaded from {load}: {failure.Detail}", ex);
            }

            entry.Version = assembly.GetName().Version?.ToString() ?? string.Empty;

            if (context.CopiedForX86)
                _host.Log.Info($"Decal plugin {entry.AssemblyPath} was built for x86 only, so it runs from a corrected copy; files it writes beside itself are lost when it stops.");
            if (context.CopiedTreated)
                _host.Log.Info($"Decal plugin {entry.AssemblyPath} runs from a copy with its calls of the player's folders, the XML serializer, Decal.dll and the registry pointed at this host's, as a registered plugin's working copy has them; files it writes beside itself are lost when it stops.");
            foreach (string problem in context.TreatProblems)
                _host.Log.Warn($"Decal plugin {entry.Name}: {problem}.");

            List<Type> types = new List<Type>();
            try
            {
                types.AddRange(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Some of its types need something missing - a Decal COM interop assembly,
                // usually. The rest may still run; say what is missing, once.
                types.AddRange(ex.Types.Where(t => t != null));
                string missing = string.Join("; ", ex.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct().Take(3));
                _host.Log.Warn($"Decal plugin {entry.Name}: some of its types could not be loaded ({missing}).");
            }

            List<Type> extensions = types
                .Where(t => !t.IsAbstract && (t.IsSubclassOf(typeof(PluginBase)) || t.IsSubclassOf(typeof(FilterBase))) && t.GetConstructor(Type.EmptyTypes) != null)
                .ToList();

            if (extensions.Count == 0)
                return Fail(entry, context, copy, null, $"{entry.AssemblyPath} holds no Decal plugin that can be made.", null);

            if (!entry.IsRegistered)
                entry.Name = DecalRuntime.FriendlyNameOf(extensions.FirstOrDefault(t => t.IsSubclassOf(typeof(PluginBase))) ?? extensions[0]);

            entry.Context = context;
            entry.CopyDirectory = copy;
            entry.Pending.AddRange(extensions);
            entry.Faults.Clear();
            entry.NativeNoted.Clear();
            return true;
        }

        /// <summary>The folder a registered plugin runs from, by its class id; null for one not running here.</summary>
        private string WorkingFolderFor(string clsid)
        {
            foreach (DecalPluginEntry entry in _entries.ToArray())
            {
                if (entry.IsRegistered && entry.WorkingDirectory != null && Replacements.SameClsid(entry.Clsid, clsid))
                    return entry.WorkingDirectory;
            }

            return null;
        }

        /// <summary>
        /// Brings a registered plugin's working copy up to date from its install, saying the first
        /// time where it is and what was left behind.
        /// </summary>
        private bool RefreshWorkingCopy(DecalPluginEntry entry, string install)
        {
            string working = WorkingCopy.DirectoryFor(_workingRoot, install);
            bool first = !Directory.Exists(working);

            IReadOnlyList<string> problems;
            try
            {
                problems = WorkingCopy.Refresh(install, working);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                entry.Status = "failed: no working copy";
                entry.Detail = $"Its install folder, {install}, could not be copied to {working} to run from there: {ex.Message}";
                _host.Log.Error($"Decal plugin {entry.Name}: {entry.Detail}");
                return false;
            }

            entry.WorkingDirectory = working;

            // The host's own install falling short is said on its own, as a warning, every start
            // until it is put right: the plugin runs, but not as it should.
            List<string> incomplete = problems.Where(p => p.Contains(WorkingCopy.HostAssemblyMissing, StringComparison.Ordinal)).ToList();
            problems = problems.Except(incomplete).ToList();
            foreach (string line in incomplete)
                _host.Log.Warn($"Decal plugin {entry.Name}'s working copy: {line}");

            if (first)
                _host.Log.Info($"Decal plugin {entry.Name} runs from a working copy of {install}, in {working}: it finds its files there and keeps what it writes there, and the install is only read."
                    + (problems.Count == 0 ? string.Empty : " " + string.Join(" ", problems)));
            else if (problems.Any(p => !p.Contains("left behind", StringComparison.Ordinal)))
                _host.Log.Info($"Decal plugin {entry.Name}'s working copy: {string.Join(" ", problems.Where(p => !p.Contains("left behind", StringComparison.Ordinal)))}");

            return true;
        }

        /// <summary>Another loaded Decal plugin's own assembly, by name, for plugins that reference each other.</summary>
        private Assembly RunningPluginAssembly(string name)
            => _entries.Select(e => e.Context?.MainAssembly).FirstOrDefault(a => a != null && string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Starts the filters, or the plugins, a prepared assembly holds.</summary>
        private void StartPrepared(DecalPluginEntry entry, bool filters)
        {
            // Its folder, as Decal told a plugin: the working copy, for a registered one.
            string directory = entry.WorkingDirectory ?? Path.GetDirectoryName(entry.AssemblyPath);

            foreach (Type type in entry.Pending.Where(t => t.IsSubclassOf(typeof(FilterBase)) == filters).ToList())
            {
                entry.Pending.Remove(type);

                // A registered plugin goes by its registered name, in the log and as its windows'
                // owner: every Virindi plugin's class is called PluginCore.
                string name = entry.IsRegistered && type.IsSubclassOf(typeof(PluginBase)) ? entry.Name : DecalRuntime.FriendlyNameOf(type);

                try
                {
                    Extension extension = (Extension)Activator.CreateInstance(type);
                    _runtime.Start(extension, directory, name);
                    entry.Extensions.Add(extension);
                    _host.Log.Info($"Decal plugin {name} {entry.Version} started, from {entry.AssemblyPath}"
                        + (entry.IsRegistered ? $" (as registered with Decal; running in {entry.WorkingDirectory})." : "."));
                }
                catch (Exception ex)
                {
                    DecalFailure failure = DecalFailure.Explain(ex, entry.Facts, entry.Context?.NativeProblems, Path.GetDirectoryName(entry.AssemblyPath));
                    _host.Log.Error($"Decal plugin {name} failed to start: {failure.Detail}", DecalFailure.Unwrap(ex));
                    entry.Status = "failed: " + failure.Status;
                    entry.Detail = $"Tried, and did not start. {failure.Detail}";
                }
            }
        }

        /// <summary>Running if anything in it started; otherwise unloaded, keeping the reason.</summary>
        private bool Settle(DecalPluginEntry entry)
        {
            entry.Pending.Clear();

            if (entry.IsRunning)
            {
                NoteNativeProblems(entry);
                ShowRunning(entry);
                return true;
            }

            string status = entry.Status;
            string detail = entry.Detail;
            Unload(entry);
            entry.Status = status;
            entry.Detail = detail;
            return false;
        }

        /// <summary>The status of one running: where from, and what has gone wrong in it since, if anything has.</summary>
        private static void ShowRunning(DecalPluginEntry entry)
        {
            entry.Status = entry.Faults.Count == 0 ? "running" : "running, with errors";
            entry.Detail = (entry.IsRegistered
                    ? $"Running, installed at {entry.AssemblyPath}, from its working copy in {entry.WorkingDirectory}: it finds its files and keeps what it writes there, so the install itself is never changed."
                    : $"Running, from {entry.AssemblyPath}.")
                + (entry.Faults.Count == 0 ? string.Empty : " " + string.Join(" ", entry.Faults))
                + (entry.Facts?.DecalKeyRead != null && entry.Faults.Any(DecalFailure.IsNullFault)
                    ? $" It finds its folder by reading HKLM\\{entry.Facts.DecalKeyRead} through the registry's 64-bit view, where that key is not - Decal is 32-bit and wrote its keys under WOW6432Node - so what it reads there is nothing, which is the likely cause."
                    : string.Empty);
        }

        // ------------------------------------------------------------------- what goes wrong later

        /// <summary>A handler of a Decal plugin threw and was skipped: the runtime has logged it; this says whose it was.</summary>
        private void OnFaulted(object sender, DecalFaultEventArgs e)
        {
            DecalPluginEntry entry = EntryOf(e.Assembly);
            if (entry == null)
                return;

            Exception cause = e.Exception == null ? null : DecalFailure.Unwrap(e.Exception);
            DecalFailure failure = cause == null ? null : DecalFailure.Explain(cause, entry.Facts, entry.Context?.NativeProblems, Path.GetDirectoryName(entry.AssemblyPath));
            AddFault(entry, $"{When(entry)}its {e.What} handler failed: {failure?.Detail ?? "it threw."}");
        }

        /// <summary>A message box a Decal plugin showed on the game thread, answered at once so the thread goes on.</summary>
        private void OnDialog(string caption, string text)
        {
            DecalPluginEntry entry = EntryOfCaller();
            string who = entry?.Name ?? "A Decal plugin";
            _host.Log.Warn($"{who} showed a message box{(string.IsNullOrWhiteSpace(caption) ? string.Empty : " \"" + caption + "\"")}, answered at once so the game thread goes on: {FirstLines(text)}");

            if (entry != null)
                AddFault(entry, $"{When(entry)}it showed a message box: {DecalFailure.ExplainText(text)}");
        }

        /// <summary>
        /// A line a Decal plugin put in chat: a fault, when it reads as one of its own exceptions -
        /// the fault of the plugin whose code began the call, not of one it printed through:
        /// Mag-Tools puts its lines in the chat through Virindi Chat System, whose code is the
        /// nearer on the stack, and its exceptions are its own.
        /// </summary>
        private void OnChatShown(object sender, string text)
        {
            if (!DecalFailure.LooksLikeFault(text))
                return;

            DecalPluginEntry entry = EntryOfCaller(outermost: true);
            if (entry != null)
                AddFault(entry, $"{When(entry)}it reported in chat that {DecalFailure.ExplainText(text)}");
        }

        /// <summary>"While starting, " for a fault before its Startup returned; "Since it started, " after.</summary>
        private static string When(DecalPluginEntry entry) => entry.IsRunning && entry.Pending.Count == 0 ? "Since it started, " : "While starting, ";

        /// <summary>
        /// Says, as a fault, each native DLL the plugin asked for and could not have: a plugin that
        /// catches the failure itself - as Virindi Chat System does its SQLite database's - runs
        /// on without whatever needed it, and this is the only sign.
        /// </summary>
        private void NoteNativeProblems(DecalPluginEntry entry)
        {
            foreach (string problem in entry.Context?.NativeProblems ?? Array.Empty<string>())
            {
                if (entry.NativeNoted.Add(problem))
                    AddFault(entry, $"It asked for {problem}, so whatever of it needs that does not work.");
            }
        }

        private void AddFault(DecalPluginEntry entry, string fault)
        {
            if (entry.Faults.Count >= FaultsKept || entry.Faults.Contains(fault))
                return;

            entry.Faults.Add(fault);

            // While it is starting, Settle says it; once running, say it now.
            if (entry.IsRunning && entry.Pending.Count == 0)
            {
                ShowRunning(entry);
                OnChanged();
            }
        }

        /// <summary>
        /// The entry whose code is on the stack: the innermost, or with <paramref name="outermost"/>
        /// the one whose code the call began in.
        /// </summary>
        private DecalPluginEntry EntryOfCaller(bool outermost = false)
        {
            DecalPluginEntry found = null;
            foreach (StackFrame frame in new StackTrace(false).GetFrames())
            {
                DecalPluginEntry entry = EntryOf(frame.GetMethod()?.DeclaringType?.Assembly);
                if (entry == null)
                    continue;

                found = entry;
                if (!outermost)
                    break;
            }

            return found;
        }

        /// <summary>The entry that loaded an assembly, its own or one of its dependencies.</summary>
        private DecalPluginEntry EntryOf(Assembly assembly)
        {
            if (assembly == null)
                return null;

            foreach (DecalPluginEntry entry in _entries)
            {
                if (entry.Context != null && ReferenceEquals(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly), entry.Context))
                    return entry;
            }

            return null;
        }

        /// <summary>A message box's text, up to its first two lines - an exception's message and where it was thrown.</summary>
        private static string FirstLines(string text)
        {
            string[] lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string first = string.Join(" ", lines.Take(2));
            return first.Length > 300 ? first.Substring(0, 300) + "..." : first;
        }

        /// <summary>The status of one not loaded: replaced, impossible, or off, and why.</summary>
        private void SettleUnloaded(DecalPluginEntry entry)
        {
            if (entry.ReplacedBy != null)
            {
                entry.Status = "replaced by this host";
                entry.Detail = entry.ReplacedBy;
            }
            else if (entry.CannotRun != null)
            {
                entry.Status = entry.CannotRun;
                entry.Detail = entry.CannotRunDetail + (entry.Enabled ? string.Empty : " It is switched off in any case.");
            }
            else if (!entry.Enabled)
            {
                entry.Status = "off";
                entry.Detail = !entry.IsRegistered ? "Switched off. Tick it to run it."
                    : entry.RegisteredEnabled ? "Switched off in this host; Decal's own list has it on, and is left so."
                    : _settings.Value.Registered.ContainsKey(Replacements.NormaliseClsid(entry.Clsid)) ? "Switched off in this host, as in Decal's own list."
                    : "Off in Decal's own list, so not loaded. Tick it to run it in this host; Decal's own list is left as it is.";
            }
        }

        private void Stop(DecalPluginEntry entry, string status)
        {
            for (int i = entry.Extensions.Count - 1; i >= 0; i--)
            {
                Extension extension = entry.Extensions[i];
                try
                {
                    _runtime.Stop(extension);
                }
                catch (Exception ex)
                {
                    _host.Log.Error($"Decal plugin {entry.Name} failed to shut down.", ex);
                }
            }

            Unload(entry);
            entry.Status = status;
            entry.Detail = status == "off" ? string.Empty : status;
            if (status == "off")
                SettleUnloaded(entry);
        }

        private void Unload(DecalPluginEntry entry)
        {
            entry.Extensions.Clear();
            entry.Pending.Clear();

            // Its hotkeys go with it: they hold its handlers, and with them its code.
            DecalPluginLoadContext context = entry.Context;
            if (context != null)
                VirindiHotkeySystem.VHotkeySystem.InstanceReal.Release(a => ReferenceEquals(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(a), context));

            try
            {
                entry.Context?.Unload();
            }
            catch (InvalidOperationException)
            {
                // Already unloading.
            }

            entry.Context = null;
            TryDelete(entry.CopyDirectory);
            entry.CopyDirectory = null;
        }

        private bool Fail(DecalPluginEntry entry, DecalPluginLoadContext context, string copy, DecalFailure failure, string message, Exception ex)
        {
            entry.Status = failure?.Status ?? "no plugin in it";
            entry.Detail = failure?.Detail ?? message;
            _host.Log.Error(message, ex == null ? null : DecalFailure.Unwrap(ex));

            try
            {
                context.Unload();
            }
            catch (InvalidOperationException)
            {
            }

            TryDelete(copy);
            return false;
        }

        // ------------------------------------------------------------------- the tick

        /// <summary>
        /// Runs something on the game thread after a delay - a turn key let go between ticks. The
        /// timers are kept until they fire, since one nobody holds can be collected unfired, and
        /// one that fires after Decal has stopped does nothing.
        /// </summary>
        private void Later(TimeSpan delay, Action action)
        {
            IHost host = _host;
            DecalRuntime runtime = _runtime;
            System.Threading.Timer timer = null;
            timer = new System.Threading.Timer(_ =>
            {
                lock (_timers)
                    _timers.Remove(timer);
                timer?.Dispose();
                host.RunOnGameThread(() =>
                {
                    if (ReferenceEquals(_runtime, runtime))
                        action();
                });
            });

            lock (_timers)
                _timers.Add(timer);
            timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, System.Threading.Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Lets the game thread's window messages through, for Decal plugins that brought Windows
        /// Forms timers with them: those tick only when their thread's messages are dispatched,
        /// which in the client they always were.
        /// </summary>
        private void OnTick(object sender, TimeSpan elapsed)
        {
            if (DecalPluginLoadContext.WindowsFormsLoaded)
                GameThreadMessages.Pump();
        }

        // ------------------------------------------------------------------- remembering

        private bool IsDisabled(string name) => _settings.Value.Disabled.Contains(name, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Keeps the player's choice: a folder plugin's in the list of those switched off, a
        /// registered one's only where it differs from Decal's tick - so one set back to agree
        /// with Decal follows Decal again.
        /// </summary>
        private void Remember(DecalPluginEntry entry)
        {
            if (entry.IsRegistered)
            {
                string clsid = Replacements.NormaliseClsid(entry.Clsid);
                if (entry.Enabled == entry.RegisteredEnabled)
                    _settings.Value.Registered.Remove(clsid);
                else
                    _settings.Value.Registered[clsid] = entry.Enabled;
            }
            else
            {
                // Those switched off now, and those remembered as off that are not in the folder
                // this time: the choice outlives a plugin taken out and put back.
                _settings.Value.Disabled = _entries.Where(e => !e.IsRegistered && !e.Enabled).Select(e => e.Name)
                    .Concat(_settings.Value.Disabled.Where(n => !_entries.Any(e => !e.IsRegistered && string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase))))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            _settings.Save();
        }

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

        // ------------------------------------------------------------------- housekeeping

        /// <summary>The Virindi HUDs stand-in, loaded here and nowhere sooner.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Assembly StatusHud() => typeof(VirindiHUDs.UIs.StatusModel).Assembly;

        /// <summary>The uTank2 stand-in, loaded here and nowhere sooner.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Assembly TankApi() => typeof(uTank2.PluginCore).Assembly;

        /// <summary>The messages.xml in Decal's install directory, as its registry names it; null with no install.</summary>
        private string InstalledMessagesFile()
        {
            try
            {
                return _registry.TryReadAgent(out string agentPath, out _) && !string.IsNullOrWhiteSpace(agentPath)
                    ? Path.Combine(agentPath.Trim(), "messages.xml")
                    : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is ArgumentException)
            {
                return null;
            }
        }

        private static bool IsTrue(string value)
            => value != null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("on", StringComparison.OrdinalIgnoreCase));

        private static bool IsFalse(string value)
            => value != null && (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" || value.Equals("off", StringComparison.OrdinalIgnoreCase));

        private static void TryCreate(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A folder that cannot be made is a folder with no plugins in it.
            }
        }

        private static void TryDelete(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Still held by a context that has not finished unloading; the next start clears it.
            }
        }

        /// <summary>Removes the copies left by hosts that are no longer running.</summary>
        private static void ClearOldCopies(string runningRoot)
        {
            if (!Directory.Exists(runningRoot))
                return;

            foreach (string directory in Directory.GetDirectories(runningRoot))
            {
                if (int.TryParse(Path.GetFileName(directory), out int pid) && pid != Environment.ProcessId && !IsRunning(pid))
                    TryDelete(directory);
            }
        }

        private static bool IsRunning(int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
