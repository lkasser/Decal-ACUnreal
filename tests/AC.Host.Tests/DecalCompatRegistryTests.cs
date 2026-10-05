using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using AC.Dat;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using Decal.Adapter.Hosting;
using Decal.Compat;
using Decal.Filters;
using VirindiViewService;
using VirindiViewService.Controls;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Decal's own plugin list: the plugins Decal's registry lists, which of them the host runs,
    /// which it does itself, which cannot run here and why, the player's choice kept apart from
    /// Decal's, and the rows the Decal window and the Decal Agent show for them. Every registry
    /// here is a stand-in; every "install" is a copy in a temporary folder, only ever read.
    /// </summary>
    public partial class DecalCompatTests
    {
        private const string TankClsid = "{642F1F48-16BE-48BF-B1D4-286652C4533E}";
        private const string HudsClsid = "{C6B1DF06-FF20-459E-8302-AA346CBFDA01}";
        private const string VhsClsid = "{ED7CC818-7159-461F-A833-4CA49E1C85B6}";
        private const string DhsClsid = "{6B6B9FA8-37DE-4FA3-8C60-52BD6A2F9855}";
        private const string ExampleClsid = "{11111111-2222-3333-4444-555555555555}";
        private const string TroubledClsid = "{0B5C2E1D-7A4F-4C3B-9E21-5D6F7A8B9C0D}";
        private const string ExampleName = "AutoWireup Example";

        /// <summary>Where the test build puts the Decal plugin tests install through a stand-in registry.</summary>
        private static string RegisteredFixtureFolder => Path.Combine(AppContext.BaseDirectory, "decal-registered");

        private static string ExampleAssembly => Path.Combine(FixtureFolder, "AutoWireupExamplePlugin", "AutoWireupExamplePlugin.dll");

        // ------------------------------------------------------------------- what the host replaces

        [Fact]
        public void WhatTheHostDoesItselfIsKnownByClassIdFileOrName()
        {
            Assert.Contains("Virindi Tank", Replacements.ReasonFor(new DecalRegistryEntry(TankClsid, "Virindi Tank", @"D:\VT\", "utank2-i.dll")));
            Assert.Contains("Virindi Tank", Replacements.ReasonFor(new DecalRegistryEntry(TankClsid.ToLowerInvariant(), "renamed", @"D:\VT\", "other.dll")));
            Assert.Contains("Virindi Tank", Replacements.ReasonFor(new DecalRegistryEntry("{00000000-0000-0000-0000-000000000001}", "VT", @"D:\VT\", "UTANK2-I.DLL")));
            Assert.Contains("HUDs", Replacements.ReasonFor(new DecalRegistryEntry(HudsClsid, "Virindi HUDs", @"D:\H\", "VirindiHUDs.dll")));
            Assert.Contains("VVS's bar", Replacements.ReasonFor(new DecalRegistryEntry("{00000000-0000-0000-0000-000000000002}", "Hotkeys", @"D:\V\", "VirindiHotkeySystem.dll")));
            Assert.Contains("Decal's bar", Replacements.ReasonFor(new DecalRegistryEntry(DhsClsid, "Decal Hotkey System")));
            Assert.Contains("Decal's bar", Replacements.ReasonFor(new DecalRegistryEntry("{00000000-0000-0000-0000-000000000003}", "Decal Hotkey System")));

            // Anything else is not the host's to do; nor is a .NET plugin that happens to share
            // the native one's name.
            Assert.Null(Replacements.ReasonFor(new DecalRegistryEntry("{0A7B25AC-79A9-4F6A-9751-419B87A7BB05}", "Virindi Chat System 5", @"D:\VCS\", "VCS5.dll")));
            Assert.Null(Replacements.ReasonFor(new DecalRegistryEntry("{00000000-0000-0000-0000-000000000004}", "Some Native Plugin")));
            Assert.Null(Replacements.ReasonFor(new DecalRegistryEntry("{00000000-0000-0000-0000-000000000005}", "Decal Hotkey System", @"D:\X\", "X.dll")));

            // One put in DecalCompat's own folder by hand is held to the same rule.
            Assert.NotNull(Replacements.ReasonForFile(@"C:\Decal Plugins\utank2-i\utank2-i.dll"));
            Assert.Null(Replacements.ReasonForFile(@"C:\Decal Plugins\DHS\DHS.dll"));
        }

        [Fact]
        public void EveryRegisteredPluginIsListedWithItsReasonBeforeAnythingLoads()
        {
            string installs = Path.Combine(_dataRoot, "installs");
            string good = Install(installs, "Good", ExampleAssembly);
            string text = Path.Combine(installs, "Text");
            Directory.CreateDirectory(text);
            File.WriteAllText(Path.Combine(text, "Text.dll"), "not a program");
            string noPlugin = Install(installs, "NoPlugin", typeof(DecalInstall).Assembly.Location);
            string skipped = Install(installs, "Skipped", ExampleAssembly);

            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new DecalRegistryEntry(ExampleClsid, "Good One", good, "AutoWireupExamplePlugin.dll", enabled: false, objectType: "AutoWireupExamplePlugin.PluginCore"));
            registry.Plugins.Add(new DecalRegistryEntry(TankClsid, "Virindi Tank", Path.Combine(installs, "VirindiTank"), "utank2-i.dll"));
            registry.Plugins.Add(new DecalRegistryEntry("{A0000000-0000-0000-0000-00000000000A}", "Native One"));
            registry.Plugins.Add(new DecalRegistryEntry("{B0000000-0000-0000-0000-00000000000B}", "Gone", Path.Combine(installs, "Gone"), "Gone.dll"));
            registry.Plugins.Add(new DecalRegistryEntry("{C0000000-0000-0000-0000-00000000000C}", "Text", text, "Text.dll"));
            registry.Plugins.Add(new DecalRegistryEntry("{D0000000-0000-0000-0000-00000000000D}", "No Plugin", noPlugin, Path.GetFileName(typeof(DecalInstall).Assembly.Location)));
            registry.Plugins.Add(new DecalRegistryEntry("{E0000000-0000-0000-0000-00000000000E}", "Skipped", skipped, "AutoWireupExamplePlugin.dll"));
            registry.Plugins.Add(new DecalRegistryEntry("{F0000000-0000-0000-0000-00000000000F}", "Good Twice", good + Path.DirectorySeparatorChar, "AutoWireupExamplePlugin.dll"));
            registry.ComServers["{A0000000-0000-0000-0000-00000000000A}"] = @"C:\Decal\NativeOne.dll";

            IReadOnlyList<DecalPluginCandidate> found = DecalPluginCatalog.ReadRegistered(registry, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Skipped" }, Array.Empty<DecalPluginCandidate>());

            Assert.Equal(new[] { "Good One", "Virindi Tank", "Native One", "Gone", "Text", "No Plugin", "Skipped", "Good Twice" }, found.Select(c => c.Name));
            DecalPluginCandidate first = found[0];
            Assert.Null(first.ReplacedBy);
            Assert.Null(first.CannotRun);
            Assert.False(first.Registered.Enabled);
            Assert.Equal(AssemblyName.GetAssemblyName(ExampleAssembly).Version.ToString(), first.Version);

            Assert.NotNull(found[1].ReplacedBy);
            Assert.Equal("cannot run: native", found[2].CannotRun);
            Assert.Contains(@"C:\Decal\NativeOne.dll", found[2].CannotRunDetail);
            Assert.Null(found[2].AssemblyPath);
            Assert.Equal("cannot run: file missing", found[3].CannotRun);
            Assert.Equal("cannot run: not .NET", found[4].CannotRun);
            Assert.Equal("cannot run: no plugin", found[5].CannotRun);
            Assert.Equal("skipped", found[6].CannotRun);
            Assert.Equal("listed twice", found[7].CannotRun);
            Assert.Contains("Good One", found[7].CannotRunDetail);
        }

        [Fact]
        public void TheSameAssemblyInDecalCompatsFolderRunsInsteadOfTheRegisteredCopy()
        {
            string good = Install(Path.Combine(_dataRoot, "installs"), "Good", ExampleAssembly);
            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new DecalRegistryEntry(ExampleClsid, ExampleName, good, "AutoWireupExamplePlugin.dll"));

            IReadOnlyList<DecalPluginCandidate> folder = DecalPluginCatalog.FindInFolder(FixtureFolder);
            DecalPluginCandidate registered = Assert.Single(DecalPluginCatalog.ReadRegistered(registry, new HashSet<string>(), folder));

            Assert.Equal("runs from the folder", registered.CannotRun);
            Assert.Contains(folder[0].AssemblyPath, registered.CannotRunDetail);
        }

        // ------------------------------------------------------------------- running them

        [Fact]
        public async Task ARegisteredPluginRunsFromAWorkingCopyAndItsInstallIsOnlyRead()
        {
            string install = Install(Path.Combine(_dataRoot, "installs"), "AutoWireup Install", ExampleAssembly);
            File.WriteAllText(Path.Combine(install, "settings.ini"), "as installed");
            Dictionary<string, string> before = Snapshot(install);

            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new DecalRegistryEntry(ExampleClsid, ExampleName, install + Path.DirectorySeparatorChar, "AutoWireupExamplePlugin.dll"));
            (GameHost host, DecalCompatPlugin decal, ListLog log) = await StartWithRegistryAsync(registry);

            DecalPluginEntry entry = Assert.Single(decal.Entries);
            Assert.Equal(ExampleName, entry.Name);
            Assert.True(entry.IsRegistered);
            Assert.Equal("running", entry.Status);
            Assert.Equal(HostedPluginState.Running, entry.State);

            // It runs from a copy in DecalCompat's data folder, which it is told is its folder,
            // and which holds its files.
            string working = entry.WorkingDirectory;
            Assert.StartsWith(Path.Combine(_dataRoot, "plugins", DecalCompatPlugin.PluginName, "Registered"), working);
            Assert.Equal("as installed", File.ReadAllText(Path.Combine(working, "settings.ini")));
            global::Decal.Adapter.Extension extension = await OnGameThreadAsync(host, () => decal.Runtime.Extensions.Single(e => e is global::Decal.Adapter.PluginBase));
            Assert.Equal(working, extension.Path);
            Assert.StartsWith(working, extension.GetType().Assembly.Location);
            Assert.Contains("working copy", entry.Detail);

            // Its window goes by the name Decal lists it under.
            Assert.Contains(await OnGameThreadAsync(host, host.CollectWindows), w => w.Owner == OverlayViewWindow.OwnerFor(DecalCompatPlugin.PluginName, ExampleName));
            Assert.Contains(log.Lines, l => l.Contains($"Decal's registry lists {ExampleName}") && l.Contains("loading it"));

            await host.DisposeAsync();
            Assert.Equal(before, Snapshot(install));
        }

        [Fact]
        public async Task DecalsTickIsFollowedUntilThePlayerChoosesAndTheChoiceIsKeptOutOfDecalsRegistry()
        {
            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new DecalRegistryEntry(ExampleClsid, ExampleName, Path.GetDirectoryName(ExampleAssembly), "AutoWireupExamplePlugin.dll", enabled: false));

            (GameHost host, DecalCompatPlugin decal, _) = await StartWithRegistryAsync(registry);
            DecalPluginEntry entry = decal.Find(ExampleName);
            Assert.False(entry.Enabled);
            Assert.Equal("off", entry.Status);
            Assert.Contains("Off in Decal's own list", entry.Detail);
            Assert.False(entry.IsRunning);

            // Ticked in this host: it runs, the choice is DecalCompat's, and Decal's is as it was.
            await OnGameThreadAsync(host, () =>
            {
                decal.SetHostedEnabled(ExampleName, true);
                return 0;
            });
            Assert.Equal("running", entry.Status);
            Assert.False(registry.Plugins[0].Enabled);
            Assert.Equal(new Dictionary<string, bool> { [ExampleClsid] = true }, ReadRegisteredChoices());
            await host.DisposeAsync();

            // Next time, it still runs.
            (host, decal, _) = await StartWithRegistryAsync(registry);
            entry = decal.Find(ExampleName);
            Assert.True(entry.Enabled);
            Assert.Equal("running", entry.Status);

            // Set back to agree with Decal, the choice is dropped: it follows Decal's tick again.
            await OnGameThreadAsync(host, () =>
            {
                decal.SetHostedEnabled(ExampleName, false);
                return 0;
            });
            Assert.Equal("off", entry.Status);
            Assert.Empty(ReadRegisteredChoices());
            await host.DisposeAsync();
        }

        [Fact]
        public async Task WhatTheHostReplacesIsListedNotLoadedAndCannotBeSwitched()
        {
            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new DecalRegistryEntry(TankClsid, "Virindi Tank", Path.GetDirectoryName(ExampleAssembly), "AutoWireupExamplePlugin.dll"));
            registry.Plugins.Add(new DecalRegistryEntry(HudsClsid, "Virindi HUDs", Path.Combine(_dataRoot, "nowhere"), "VirindiHUDs.dll"));
            registry.Plugins.Add(new DecalRegistryEntry(VhsClsid, "Virindi Hotkey System", Path.Combine(_dataRoot, "nowhere"), "VirindiHotkeySystem.dll", enabled: false));
            registry.Plugins.Add(new DecalRegistryEntry(DhsClsid, "Decal Hotkey System"));

            (GameHost host, DecalCompatPlugin decal, ListLog log) = await StartWithRegistryAsync(registry);

            // Even one whose file is a perfectly good Decal plugin: Virindi Tank's class id is enough.
            Assert.Equal(4, decal.Entries.Count);
            Assert.All(decal.Entries, e =>
            {
                Assert.Equal(HostedPluginState.Replaced, e.State);
                Assert.Equal("replaced by this host", e.Status);
                Assert.StartsWith("Replaced by this host", e.Detail);
                Assert.False(e.IsRunning);
            });
            Assert.All(decal.HostedPlugins, p => Assert.False(p.CanSwitch));
            Assert.Equal(new[] { true, true, false, true }, decal.HostedPlugins.Select(p => p.Enabled));
            Assert.Empty(await OnGameThreadAsync(host, host.CollectWindows));

            await OnGameThreadAsync(host, () =>
            {
                decal.SetHostedEnabled("Virindi Tank", false);
                return 0;
            });
            Assert.True(decal.Find("Virindi Tank").Enabled);
            Assert.Contains(log.Lines, l => l.Contains("Decal plugin Virindi Tank is not switched: Replaced by this host"));
            Assert.Contains(log.Lines, l => l.Contains("Decal's registry lists Decal Hotkey System") && l.Contains("(native)"));
            Assert.Empty(ReadRegisteredChoices());

            await host.DisposeAsync();
        }

        [Fact]
        public async Task APluginThatGoesWrongSaysWhyInItsStatus()
        {
            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new DecalRegistryEntry(TroubledClsid, "Troubled Plugin", Path.Combine(RegisteredFixtureFolder, "TroubledPlugin"), "TroubledPlugin.dll"));

            // Its Startup shows a message box on the game thread; were it not answered, starting
            // would never finish.
            (GameHost host, DecalCompatPlugin decal, ListLog log) = await StartWithRegistryAsync(registry, TimeSpan.FromSeconds(30));
            DecalPluginEntry entry = decal.Find("Troubled Plugin");
            Assert.Equal("1.2.3.4", entry.Version);
            Assert.True(entry.IsRunning);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN Troubled Plugin showed a message box, answered at once", StringComparison.Ordinal));

            // Then the character logs in, and its login handler throws.
            await OnGameThreadAsync(host, () =>
            {
                host.WorldState.SetPlayerId(0x50000001);
                return 0;
            });

            Assert.Equal("running, with errors", entry.Status);
            Assert.Equal(HostedPluginState.Running, entry.State);
            Assert.Contains("While starting, it showed a message box: System.NullReferenceException", entry.Detail);
            Assert.Contains("it uses VirindiViewService.Controls.HudNothing from VirindiViewService, which this host's stand-in does not have yet", entry.Detail);
            Assert.Contains("its Login handler failed: InvalidOperationException: Troubled at login.", entry.Detail);
            Assert.Contains(@"HKLM\SOFTWARE\Decal\Plugins\" + TroubledClsid, entry.Detail);
            Assert.Contains("WOW6432Node", entry.Detail);

            HostedPluginInfo info = Assert.Single(decal.HostedPlugins);
            Assert.Equal(entry.Detail, info.Detail);
            Assert.True(info.CanSwitch);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task RefreshListReadsDecalsRegistryAgainAndStartsWhatIsNew()
        {
            FakeDecalRegistry registry = new FakeDecalRegistry();
            (GameHost host, DecalCompatPlugin decal, _) = await StartWithRegistryAsync(registry);
            Assert.Empty(decal.Entries);

            registry.Plugins.Add(new DecalRegistryEntry(ExampleClsid, ExampleName, Path.GetDirectoryName(ExampleAssembly), "AutoWireupExamplePlugin.dll"));
            await OnGameThreadAsync(host, () =>
            {
                decal.RescanHosted();
                decal.RescanHosted();
                return 0;
            });

            DecalPluginEntry entry = Assert.Single(decal.Entries);
            Assert.Equal("running", entry.Status);
            Assert.Equal(3, registry.PluginReads);

            await host.DisposeAsync();
        }

        // ------------------------------------------------------------------- the lists

        [Fact]
        public void PluginListRowsPutThePluginsAPluginRunsBeneathIt()
        {
            PluginEntry plain = new PluginEntry(@"C:\plugins\Plain\Plain.dll") { Plugin = new NamedPlugin("Plain"), Version = "1.0" };
            PluginEntry hosting = new PluginEntry(@"C:\plugins\Runner\Runner.dll") { Plugin = new HostingPlugin("Runner", new HostedPluginInfo("Tank", "2.0", true, "replaced by this host", "Replaced, because.", HostedPluginState.Replaced, canSwitch: false), new HostedPluginInfo("Chat", "5.0", true, "running")) };
            PluginEntry off = new PluginEntry(@"C:\plugins\Off\Off.dll") { Enabled = false, Status = "off" };
            HostingPlugin added = new HostingPlugin("Added", new HostedPluginInfo("Extra", string.Empty, false, "off"));
            HostingPlugin broken = new HostingPlugin("Broken") { Throws = true };
            List<string> failures = new List<string>();

            IReadOnlyList<PluginListRow> rows = PluginList.Build(new[] { plain, hosting, off }, new IPlugin[] { plain.Plugin, hosting.Plugin, added, broken }, (p, ex) => failures.Add(p.Name + ": " + ex.Message));

            Assert.Equal(new[] { "Plain", "Runner", "Tank", "Chat", "Off", "Extra" }, rows.Select(r => r.Name));
            Assert.Equal(new[] { false, false, true, true, false, true }, rows.Select(r => r.Hosted));
            Assert.Equal(new[] { null, null, "Runner", "Runner", null, "Added" }, rows.Select(r => r.HostName));
            Assert.Equal(new[] { HostedPluginState.Running, HostedPluginState.Running, HostedPluginState.Replaced, HostedPluginState.Running, HostedPluginState.Off, HostedPluginState.Off }, rows.Select(r => r.State));
            Assert.False(rows[2].CanSwitch);
            Assert.Equal("Replaced, because.", rows[2].Detail);
            Assert.Equal("running", rows[3].Detail);
            Assert.Contains(@"C:\plugins\Off\Off.dll", rows[4].Detail);
            Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());
            Assert.Equal(new[] { "Broken: listing failed" }, failures);
        }

        [Fact]
        public async Task DecalsWindowListsDecalsRegisteredPluginsWithTheirStatusAndWhy()
        {
            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new DecalRegistryEntry(TankClsid, "Virindi Tank", Path.Combine(_dataRoot, "nowhere"), "utank2-i.dll"));
            registry.Plugins.Add(new DecalRegistryEntry(ExampleClsid, ExampleName, Path.GetDirectoryName(ExampleAssembly), "AutoWireupExamplePlugin.dll"));
            registry.Plugins.Add(new DecalRegistryEntry("{A0000000-0000-0000-0000-00000000000A}", "Native One"));

            ListLog log = new ListLog();
            GameHost host = new GameHost(new LingeringTransport(), log, new Dictionary<string, string> { ["DecalCompat:Folder"] = Path.Combine(_dataRoot, "empty") }, _dataRoot);
            AC.Host.Decal.DecalAgent agent = new AC.Host.Decal.DecalAgent(host, null);
            DecalCompatPlugin decal = new DecalCompatPlugin(registry);
            host.AddPlugin(agent);
            host.AddPlugin(decal);
            await host.StartAsync();

            List list = await OnGameThreadAsync(host, () => agent.View.List("lstPlugins"));
            Assert.Equal(new[] { "   Virindi Tank", "   " + ExampleName, "   Native One" }, Enumerable.Range(0, list.RowCount).Select(r => list[r][1].Text));
            Assert.Equal(new[] { "replaced by this host", "running", "cannot run: native" }, Enumerable.Range(0, list.RowCount).Select(r => list[r][3].Text));
            Assert.Equal(new[] { string.Empty, "Reload", string.Empty }, Enumerable.Range(0, list.RowCount).Select(r => list[r][4].Text));
            Assert.Equal(new[] { true, true, true }, Enumerable.Range(0, list.RowCount).Select(r => list[r][0].Checked));

            // A click on a row says why it stands as it does.
            host.DispatchCommand(AC.Host.Decal.DecalAgent.PluginName, new OverlayCommand(ViewVerbs.Click, "1", rowId: "0", controlId: "lstPlugins"));
            await SettleAsync(host);
            string detail = await OnGameThreadAsync(host, () => agent.View.StaticText("lblPluginDetail").Text);
            Assert.StartsWith("Virindi Tank: Replaced by this host", detail);

            // Virindi Tank's lamp springs back: the host does its job, and it has no switch here.
            host.DispatchCommand(AC.Host.Decal.DecalAgent.PluginName, new OverlayCommand(ViewVerbs.Click, "0", rowId: "0", controlId: "lstPlugins"));
            await SettleAsync(host);
            Assert.True((await OnGameThreadAsync(host, () => agent.View.List("lstPlugins")))[0][0].Checked);

            // The example's switches off.
            host.DispatchCommand(AC.Host.Decal.DecalAgent.PluginName, new OverlayCommand(ViewVerbs.Click, "0", rowId: "1", controlId: "lstPlugins"));
            await SettleAsync(host);
            Assert.Equal("off", decal.Find(ExampleName).Status);
            List after = await OnGameThreadAsync(host, () => agent.View.List("lstPlugins"));
            Assert.False(after[1][0].Checked);
            Assert.Equal("off", after[1][3].Text);

            await host.DisposeAsync();
        }

        // ------------------------------------------------------------------- saying why

        [Fact]
        public void FailuresAreReadAsWhatThePluginNeeded()
        {
            DecalFailure missing = DecalFailure.Explain(
                new System.Reflection.TargetInvocationException(new FileNotFoundException(
                    "Could not load file or assembly 'Decal.Interop.Input, Version=2.9.6.0, Culture=neutral, PublicKeyToken=481f17d392f1fb65'.",
                    "Decal.Interop.Input, Version=2.9.6.0, Culture=neutral, PublicKeyToken=481f17d392f1fb65")),
                null, null, null);
            Assert.Equal("needs Decal.Interop.Input", missing.Status);
            Assert.Contains("native (COM) layer", missing.Detail);

            DecalFailure type = DecalFailure.Explain(new TypeLoadException("Could not load type 'VirindiViewService.Controls.HudDetachedTabView' from assembly 'VirindiViewService, Version=1.0.0.47, Culture=neutral, PublicKeyToken=null'."), null, null, null);
            Assert.Equal("needs HudDetachedTabView", type.Status);
            Assert.Contains("this host's stand-in does not have yet", type.Detail);

            // Managed DirectX's maths is a stand-in; its Direct3D is not there at all.
            DecalFailure maths = DecalFailure.Explain(new TypeLoadException("Could not load type 'Microsoft.DirectX.Quaternion' from assembly 'Microsoft.DirectX, Version=1.0.2902.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35'."), null, null, null);
            Assert.Contains("this host's stand-in does not have yet", maths.Detail);
            DecalFailure direct3D = DecalFailure.Explain(new FileNotFoundException(
                "Could not load file or assembly 'Microsoft.DirectX.Direct3D, Version=1.0.2902.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35'.",
                "Microsoft.DirectX.Direct3D, Version=1.0.2902.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"), null, null, null);
            Assert.Equal("needs Microsoft.DirectX.Direct3D", direct3D.Status);
            Assert.Contains("Managed DirectX other than its maths", direct3D.Detail);

            DecalFailure member = DecalFailure.Explain(new MissingMethodException("Method not found: 'Void VirindiViewService.ACImage..ctor(System.Drawing.Bitmap)'."), null, null, null);
            Assert.Equal("needs ACImage(Bitmap)", member.Status);

            DecalFailure native = DecalFailure.Explain(new DllNotFoundException("Unable to load DLL 'sqlite3' or one of its dependencies."), null,
                new[] { "sqlite3.dll, a 32-bit DLL, which this 64-bit host cannot load" }, null);
            Assert.Equal("32-bit sqlite3.dll", native.Status);
            Assert.Equal("It needs sqlite3.dll, a 32-bit DLL, which this 64-bit host cannot load.", native.Detail);

            PluginFacts troubled = PluginFacts.Read(Path.Combine(RegisteredFixtureFolder, "TroubledPlugin", "TroubledPlugin.dll"));
            DecalFailure registry = DecalFailure.Explain(new NullReferenceException(), troubled, null, null);
            Assert.Equal("reads 64-bit registry", registry.Status);
            Assert.Contains("WOW6432Node", registry.Detail);

            DecalFailure plain = DecalFailure.Explain(new NullReferenceException(), null, null, null);
            Assert.Equal("NullReferenceException", plain.Status);
        }

        [Fact]
        public void ReportedExceptionsAreReadTheSameWay()
        {
            Assert.True(DecalFailure.LooksLikeFault("[VI] Exception \"Object reference not set to an instance of an object.\""));
            Assert.True(DecalFailure.LooksLikeFault("System.TypeLoadException: Could not load type 'X'"));
            Assert.False(DecalFailure.LooksLikeFault("You say, \"hello\""));

            Assert.Equal("it needs the assembly VirindiHUDs, the real Virindi HUDs, which this host replaces with its own.",
                DecalFailure.ExplainText("Startup Error: HUD failed to bind: Could not load file or assembly 'VirindiHUDs, Version=1.0.0.18, Culture=neutral, PublicKeyToken=null'."));
            Assert.Contains("which .NET cannot do for a plugin loaded so that it can be unloaded",
                DecalFailure.ExplainText("Exception caught: A non-collectible assembly may not reference a collectible assembly."));
            Assert.Equal(@"it tried to write c:\vi2-error.txt, which Windows does not let it - while reporting an error of its own.",
                DecalFailure.ExplainText("Exception in exception handler: System.UnauthorizedAccessException: Access to the path 'c:\\vi2-error.txt' is denied."));
            Assert.Equal("Error [NullReferenceException]: see errors.txt.",
                DecalFailure.ExplainText("Error [NullReferenceException]: see <Tell:IIDString:1:GoArrow_ChatCommand_ErrorsTxt>errors.txt<\\Tell>"));
            Assert.True(DecalFailure.IsNullFault("System.NullReferenceException: Object reference not set"));
        }

        [Fact]
        public void APluginsMetadataSaysWhatItNeedsWithoutLoadingIt()
        {
            PluginFacts troubled = PluginFacts.Read(Path.Combine(RegisteredFixtureFolder, "TroubledPlugin", "TroubledPlugin.dll"));
            Assert.True(troubled.IsDecalExtension);
            Assert.Equal("TroubledPlugin", troubled.AssemblyName);
            Assert.Equal("1.2.3.4", troubled.Version);
            Assert.Contains("user32.dll", troubled.NativeImports);
            Assert.Contains("Decal.Adapter", troubled.References);
            Assert.Equal(@"SOFTWARE\Decal\Plugins\" + TroubledClsid, troubled.DecalKeyRead);

            PluginFacts example = PluginFacts.Read(ExampleAssembly);
            Assert.True(example.IsDecalExtension);
            Assert.Null(example.DecalKeyRead);
            Assert.False(PluginFacts.Read(typeof(DecalInstall).Assembly.Location).IsDecalExtension);

            string text = Path.Combine(_dataRoot, "text.dll");
            Directory.CreateDirectory(_dataRoot);
            File.WriteAllText(text, "not a program");
            Assert.Null(PluginFacts.Read(text));
        }

        // ------------------------------------------------------------------- the working copy

        [Fact]
        public void TheWorkingCopyTakesCodeEachTimeAndDataOnlyOnce()
        {
            string install = Path.Combine(_dataRoot, "install", "Plugin");
            Directory.CreateDirectory(Path.Combine(install, "maps"));
            Directory.CreateDirectory(Path.Combine(install, "client"));

            // The plugin, marked x86-only as many Decal plugins are.
            byte[] image = File.ReadAllBytes(ExampleAssembly);
            using (System.Reflection.PortableExecutable.PEReader reader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(image)))
            {
                int offset = reader.PEHeaders.CorHeaderStartOffset + 16;
                BitConverter.GetBytes(BitConverter.ToInt32(image, offset) | 0x2).CopyTo(image, offset);
            }

            File.WriteAllBytes(Path.Combine(install, "Plugin.dll"), image);
            File.WriteAllText(Path.Combine(install, "settings.ini"), "installed");
            File.WriteAllText(Path.Combine(install, "maps", "map.txt"), "a map");
            File.WriteAllText(Path.Combine(install, "installer.zip"), "an archive");
            for (int i = 0; i < 501; i++)
                File.WriteAllText(Path.Combine(install, "client", i + ".dat"), "x");
            Dictionary<string, string> before = Snapshot(install);

            string working = WorkingCopy.DirectoryFor(Path.Combine(_dataRoot, "working"), install);
            Assert.Equal(working, WorkingCopy.DirectoryFor(Path.Combine(_dataRoot, "working"), install.ToUpperInvariant() + Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(working, WorkingCopy.DirectoryFor(Path.Combine(_dataRoot, "working"), Path.Combine(_dataRoot, "elsewhere", "Plugin")));
            Assert.StartsWith("Plugin-", Path.GetFileName(working));

            IReadOnlyList<string> problems = WorkingCopy.Refresh(install, working);

            Assert.False(DecalPluginLoadContext.ClearRequires32Bit(File.ReadAllBytes(Path.Combine(working, "Plugin.dll"))));
            Assert.Equal("installed", File.ReadAllText(Path.Combine(working, "settings.ini")));
            Assert.Equal("a map", File.ReadAllText(Path.Combine(working, "maps", "map.txt")));
            Assert.False(File.Exists(Path.Combine(working, "installer.zip")));
            Assert.False(Directory.Exists(Path.Combine(working, "client")));
            Assert.Contains(problems, p => p.StartsWith("installer.zip", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.Contains("client"));

            // What the plugin writes is its own; a new build of it is taken.
            File.WriteAllText(Path.Combine(working, "settings.ini"), "changed by the plugin");
            File.WriteAllText(Path.Combine(install, "settings.ini"), "reinstalled");
            File.SetLastWriteTimeUtc(Path.Combine(install, "Plugin.dll"), DateTime.UtcNow.AddMinutes(-5));
            before = Snapshot(install);

            WorkingCopy.Refresh(install, working);

            Assert.Equal("changed by the plugin", File.ReadAllText(Path.Combine(working, "settings.ini")));
            Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(install, "Plugin.dll")), File.GetLastWriteTimeUtc(Path.Combine(working, "Plugin.dll")));
            Assert.Equal(before, Snapshot(install));
        }

        // ------------------------------------------------------------------- the stand-ins

        [Fact]
        public void AnImageStackInAListIsTheImageOnTop()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            HudView view = new HudView("Stacks", 200, 100, new ACImage(0x1234));
            HudFixedLayout layout = new HudFixedLayout();
            view.Controls.HeadControl = layout;
            HudList list = new HudList();
            layout.AddControl(list, new System.Drawing.Rectangle(0, 0, 200, 100));
            list.AddColumn(typeof(HudImageStack), 16, "cIcon");
            list.AddColumn(typeof(HudStaticText), 100, "cName");

            // As Virindi Item Tool fills its comps list: underlay, then icon.
            HudImageStack stack = (HudImageStack)list.AddRow()[0];
            stack.Add(new System.Drawing.Rectangle(0, 0, 16, 16), 0x6000000 | 0x1111);
            stack.Add(new System.Drawing.Rectangle(0, 0, 16, 16), 0x6000000 | 0x2222);
            ((HudStaticText)list[0][1]).Text = "Prismatic Taper";

            List drawn = Assert.Single(runtime.Views).View.List(list.Name);
            Assert.Equal(new[] { ListColumnKind.Icon, ListColumnKind.Text }, drawn.Columns.Select(c => c.Kind));
            Assert.Equal("portal:06002222", drawn[0][0].ImageKey);
            Assert.Equal(2, stack.Count);
            Assert.Equal(typeof(HudImageStack), list.GetColumnInfo(0).ControlType);

            stack.SetValue(1, new ACImage());
            Assert.Equal("portal:06001111", drawn[0][0].ImageKey);
            stack.Clear();
            Assert.Equal(string.Empty, drawn[0][0].ImageKey);
            Assert.Throws<Exception>(() => stack.SetValue(0, new ACImage(5)));
        }

        [Fact]
        public void AChatBoxShowsWhatPluginsSendIt()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            HudView view = new HudView("Chat", 300, 200, new ACImage(0x1234));
            HudFixedLayout layout = new HudFixedLayout();
            view.Controls.HeadControl = layout;
            HudChatbox chat = new HudChatbox();
            layout.AddControl(chat, new System.Drawing.Rectangle(0, 0, 300, 200));
            List drawn = Assert.Single(runtime.Views).View.List(chat.Name);

            HudChatbox.SendChatText("<Tell:IIDString:1:Bob>Bob<\\Tell> says hello", eConsoleColorClass.OtherTell);
            Assert.Equal(new[] { "Bob says hello" }, Enumerable.Range(0, drawn.RowCount).Select(r => drawn[r][0].Text));

            chat.FilterClearAll();
            chat.FilterAddEnabled(eConsoleColorClass.PluginMessage);
            HudChatbox.SendChatText("not for this box", eConsoleColorClass.OpenChat);
            HudChatbox.SendChatText("a plugin's line", HudConsole.eACTextColor.Green0, eConsoleColorClass.PluginMessage);
            chat.WriteLine("red", System.Drawing.Color.Red);
            Assert.Equal(new[] { "Bob says hello", "a plugin's line", "red" }, Enumerable.Range(0, drawn.RowCount).Select(r => drawn[r][0].Text));
            Assert.Equal(unchecked((uint)System.Drawing.Color.Red.ToArgb()), drawn[2][0].Color);

            chat.Dispose();
            HudChatbox.SendChatText("after", eConsoleColorClass.PluginMessage);
            Assert.Equal(3, drawn.RowCount);
        }

        [Fact]
        public void ComponentTypesAreDecalsSeven()
        {
            Assert.Equal("Taper", ComponentType.GetById(5).Name);
            Assert.Equal(0, ComponentType.GetByName("scarab").Id);
            Assert.Null(ComponentType.GetById(7));
            Assert.Same(ComponentType.GetById(6), ComponentType.GetByName("Pea"));
        }

        [Fact]
        public void ARegistryEntryWithoutAnEnabledValueCountsAsTicked()
        {
            DecalRegistryEntry entry = new DecalRegistryEntry("{A}", "Plugin", @"C:\P\", "P.dll");
            Assert.True(entry.Enabled);
            Assert.Null(entry.ObjectType);
            Assert.Null(((IDecalRegistry)new NoComServers()).ReadComServer("{A}"));
        }

        // ------------------------------------------------------------------- helpers

        private async Task<(GameHost host, DecalCompatPlugin decal, ListLog log)> StartWithRegistryAsync(FakeDecalRegistry registry, TimeSpan? timeout = null)
        {
            ListLog log = new ListLog();
            Dictionary<string, string> settings = new Dictionary<string, string> { ["DecalCompat:Folder"] = Path.Combine(_dataRoot, "empty") };
            GameHost host = new GameHost(new LingeringTransport(), log, settings, _dataRoot);
            DecalCompatPlugin decal = new DecalCompatPlugin(registry);
            host.AddPlugin(decal);
            await host.StartAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(60));
            return (host, decal, log);
        }

        /// <summary>DecalCompat's remembered choices for registered plugins, read from its settings file.</summary>
        private Dictionary<string, bool> ReadRegisteredChoices()
        {
            string path = Path.Combine(_dataRoot, "plugins", DecalCompatPlugin.PluginName, "settings.json");
            if (!File.Exists(path))
                return new Dictionary<string, bool>();

            DecalCompatSettings settings = JsonSerializer.Deserialize<DecalCompatSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return new Dictionary<string, bool>(settings.Registered);
        }

        /// <summary>A copy of an assembly in an install folder of its own, as an installer would leave it.</summary>
        private static string Install(string root, string name, string assembly)
        {
            string folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            File.Copy(assembly, Path.Combine(folder, Path.GetFileName(assembly)), overwrite: true);
            return folder;
        }

        /// <summary>Every file under a folder, with its size and time: to tell that nothing was written there.</summary>
        private static Dictionary<string, string> Snapshot(string folder)
            => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, f => new FileInfo(f).Length + " " + File.GetLastWriteTimeUtc(f).Ticks);

        private sealed class NamedPlugin : IPlugin
        {
            public NamedPlugin(string name) => Name = name;

            public string Name { get; }

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }
        }

        private sealed class HostingPlugin : IPlugin, IHostedPlugins
        {
            private readonly HostedPluginInfo[] _hosted;

            public HostingPlugin(string name, params HostedPluginInfo[] hosted)
            {
                Name = name;
                _hosted = hosted;
            }

            public string Name { get; }

            public bool Throws { get; set; }

            public IReadOnlyList<HostedPluginInfo> HostedPlugins => Throws ? throw new InvalidOperationException("listing failed") : _hosted;

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }

            public void SetHostedEnabled(string name, bool enabled)
            {
            }

            public void ReloadHosted(string name)
            {
            }
        }

        private sealed class NoComServers : IDecalRegistry
        {
            public bool TryReadAgent(out string agentPath, out string portalPath)
            {
                agentPath = null;
                portalPath = null;
                return false;
            }

            public IReadOnlyList<DecalRegistryEntry> ReadPlugins() => Array.Empty<DecalRegistryEntry>();

            public IReadOnlyList<DecalRegistryEntry> ReadServices() => Array.Empty<DecalRegistryEntry>();
        }
    }
}
