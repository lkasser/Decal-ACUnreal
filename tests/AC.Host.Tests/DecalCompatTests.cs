using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using AC.Host.Transport;
using AC.Host.World;
using Decal.Adapter;
using Decal.Adapter.Hosting;
using Decal.Adapter.Wrappers;
using Decal.Compat;
using Decal.Filters;
using Decal.Interop.Core;
using Xunit;
using DecalListRow = Decal.Adapter.Wrappers.ListRow;
using DecalWorldObject = Decal.Adapter.Wrappers.WorldObject;
using HostWorldObject = AC.Host.World.WorldObject;

namespace AC.Host.Tests
{
    /// <summary>
    /// Decal plugins in the host: a real one, Virindi's AutoWireup example built unchanged
    /// against the stand-ins, loaded and run end to end through the host; and the stand-ins'
    /// own behaviour, through plugins written here.
    /// </summary>
    /// <remarks>
    /// One collection, because Decal is one per process: <see cref="CoreManager.Current"/> is
    /// static, as plugins expect it to be, and two tests each running a Decal would see each
    /// other's.
    /// </remarks>
    [Collection(DecalCollection.Name)]
    public partial class DecalCompatTests : IDisposable
    {
        private const uint Mote = 0x80000101;
        private const uint Stone = 0x80000102;

        private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "achost-decal-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A copy still held by a context that has not finished unloading; temp is temp.
            }
        }

        /// <summary>Where the test build puts the Decal plugins it built, one folder each.</summary>
        private static string FixtureFolder => Path.Combine(AppContext.BaseDirectory, "decal-plugins");

        private const string AutoWireup = "PluginCore";

        private static readonly string AutoWireupWindow = OverlayViewWindow.OwnerFor(DecalCompatPlugin.PluginName, AutoWireup);

        // ------------------------------------------------------------------- a real plugin, end to end

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TheAutoWireupExampleStartsAndItsViewIsAWindow(bool vvs)
        {
            (GameHost host, DecalCompatPlugin decal, ListLog log) = await StartAsync(vvs);

            DecalPluginEntry entry = Assert.Single(decal.Entries);
            Assert.Equal(AutoWireup, entry.Name);
            Assert.Equal("running", entry.Status);
            Assert.Contains(log.Lines, l => l.StartsWith("INFO") && l.Contains($"Decal plugin {AutoWireup}") && l.Contains("started"));

            IReadOnlyList<OverlayWindowInfo> windows = await OnGameThreadAsync(host, host.CollectWindows);
            OverlayWindowInfo window = Assert.Single(windows, w => w.Owner == AutoWireupWindow);
            DecalView view = window.View;

            // The plugin's own XML, parsed by the host: a notebook of two pages, the first
            // holding the object list with its icon and name columns.
            Assert.Equal("Autowireup Example Plugin", view.Title);
            Assert.Equal(392, view.Width);
            Assert.Equal("portal:060029AB", view.IconKey);
            Notebook notebook = view.Notebook("Notebook1");
            Assert.Equal(new[] { "Objects", "Other" }, notebook.Pages.Select(p => p.Label));
            List list = view.List("lstObjects");
            Assert.Equal(new[] { ListColumnKind.Icon, ListColumnKind.Text }, list.Columns.Select(c => c.Kind));
            Assert.Empty(view.Warnings);

            await host.DisposeAsync();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AnObjectCreatedInTheWorldReachesTheRealPluginsWorldFilterHandler(bool vvs)
        {
            (GameHost host, _, _) = await StartAsync(vvs);

            await OnGameThreadAsync(host, () => AddObject(host, Mote, "Pyreal Mote", 0x06001234));
            await OnGameThreadAsync(host, () => AddObject(host, Stone, "Mana Stone", 0x0600135C));

            // The plugin's ObjectTracker heard CreateObject, and its view code added a row for
            // each, through whichever of VVS's list or Decal's it chose.
            List list = await OnGameThreadAsync(host, () => WindowView(host).List("lstObjects"));
            Assert.Equal(2, list.RowCount);
            Assert.Equal("portal:06001234", list[0][0].ImageKey);
            Assert.Equal("Pyreal Mote", list[0][1].Text);
            Assert.Equal("Mana Stone", list[1][1].Text);

            await host.DisposeAsync();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AClickInItsWindowReachesTheRealPluginsHandler(bool vvs)
        {
            (GameHost host, _, ListLog log) = await StartAsync(vvs);
            await OnGameThreadAsync(host, () => AddObject(host, Mote, "Pyreal Mote", 0x06001234));
            await OnGameThreadAsync(host, () => AddObject(host, Stone, "Mana Stone", 0x0600135C));

            // A click on the second row, as the overlay sends it, routed by the window's owner.
            long handled = host.Statistics.CommandsHandled;
            host.DispatchCommand(AutoWireupWindow, new OverlayCommand(ViewVerbs.Click, "1", rowId: "1", controlId: "lstObjects"));
            await SettleAsync(host);

            // The plugin's handler looked the row's object up and asked Decal to select it -
            // which the host cannot do for the client, and says so, naming the object.
            Assert.Equal(handled + 1, host.Statistics.CommandsHandled);
            Assert.Contains(log.Lines, l => l.Contains("Actions.SelectItem") && l.Contains($"0x{Stone:X8}"));

            await host.DisposeAsync();
        }

        [Fact]
        public async Task AnObjectLeavingTheWorldTakesItsRowAndTheRealPluginSaysSoInChat()
        {
            (GameHost host, _, ListLog log) = await StartAsync(vvs: true);
            await OnGameThreadAsync(host, () => AddObject(host, Mote, "Pyreal Mote", 0x06001234));
            await OnGameThreadAsync(host, () => AddObject(host, Stone, "Mana Stone", 0x0600135C));

            await OnGameThreadAsync(host, () =>
            {
                host.WorldState.Remove(Mote);
                return 0;
            });

            List list = await OnGameThreadAsync(host, () => WindowView(host).List("lstObjects"));
            Assert.Equal(1, list.RowCount);
            Assert.Equal("Mana Stone", list[0][1].Text);
            Assert.Contains(log.Lines, l => l.Contains("[AWEP] Object deleted: Pyreal Mote"));

            await host.DisposeAsync();
        }

        [Fact]
        public async Task ADecalPluginSwitchedOffLosesItsWindowAndSwitchedOnGetsItBack()
        {
            (GameHost host, DecalCompatPlugin decal, _) = await StartAsync(vvs: true);
            DecalPluginEntry entry = decal.Find(AutoWireup);

            await OnGameThreadAsync(host, () =>
            {
                decal.SetEnabled(entry, false);
                return 0;
            });

            Assert.Equal("off", entry.Status);
            Assert.DoesNotContain(await OnGameThreadAsync(host, host.CollectWindows), w => w.Owner == AutoWireupWindow);

            await OnGameThreadAsync(host, () =>
            {
                decal.SetEnabled(entry, true);
                return 0;
            });

            Assert.Equal("running", entry.Status);
            Assert.Contains(await OnGameThreadAsync(host, host.CollectWindows), w => w.Owner == AutoWireupWindow);

            // And a fresh start: the reloaded plugin tracks what arrives from now on.
            await OnGameThreadAsync(host, () => AddObject(host, Mote, "Pyreal Mote", 0x06001234));
            Assert.Equal(1, (await OnGameThreadAsync(host, () => WindowView(host).List("lstObjects"))).RowCount);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task DecalsWindowListsTheDecalPluginsAndSwitchesThemOff()
        {
            ListLog log = new ListLog();
            GameHost host = new GameHost(new LingeringTransport(), log, new Dictionary<string, string> { ["DecalCompat:Folder"] = FixtureFolder }, _dataRoot);
            AC.Host.Decal.DecalAgent agent = new AC.Host.Decal.DecalAgent(host, null);
            DecalCompatPlugin decal = new DecalCompatPlugin(new FakeDecalRegistry());
            host.AddPlugin(agent);
            host.AddPlugin(decal);
            await host.StartAsync();

            List list = await OnGameThreadAsync(host, () => agent.View.List("lstPlugins"));
            Assert.Equal(1, list.RowCount);
            Assert.Equal("   " + AutoWireup, list[0][1].Text);
            Assert.Equal("running", list[0][3].Text);
            Assert.True(list[0][0].Checked);

            // The lamp, clicked, as the overlay sends it: the list toggles it, and the plugin goes off.
            host.DispatchCommand(AC.Host.Decal.DecalAgent.PluginName, new OverlayCommand(ViewVerbs.Click, "0", rowId: "0", controlId: "lstPlugins"));
            await SettleAsync(host);

            Assert.Equal("off", decal.Find(AutoWireup).Status);
            Assert.Equal("off", (await OnGameThreadAsync(host, () => agent.View.List("lstPlugins")))[0][3].Text);
            Assert.DoesNotContain(await OnGameThreadAsync(host, host.CollectWindows), w => w.Owner == AutoWireupWindow);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task ACommandForAHostedWindowThatIsNotThereIsReportedNotSwallowed()
        {
            (GameHost host, _, ListLog log) = await StartAsync(vvs: true);

            long unhandled = host.Statistics.CommandsUnhandled;
            host.DispatchCommand(OverlayViewWindow.OwnerFor(DecalCompatPlugin.PluginName, "Nobody"), new OverlayCommand(ViewVerbs.Press, controlId: "btn"));
            host.DispatchCommand(AutoWireupWindow, new OverlayCommand(ViewVerbs.Press, controlId: "btnNotInTheView"));
            await SettleAsync(host);

            Assert.Equal(unhandled + 2, host.Statistics.CommandsUnhandled);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN") && l.Contains("btnNotInTheView"));

            await host.DisposeAsync();
        }

        // ------------------------------------------------------------------- the loader

        [Fact]
        public void AnAssemblyMarkedThirtyTwoBitOnlyButAllILHasTheMarkCleared()
        {
            byte[] image = File.ReadAllBytes(Path.Combine(FixtureFolder, "AutoWireupExamplePlugin", "AutoWireupExamplePlugin.dll"));
            Assert.False(DecalPluginLoadContext.ClearRequires32Bit(image));

            // Set the flag as an x86-only build does - COR20 header, flags at offset 16 - and
            // it comes off again.
            using (System.Reflection.PortableExecutable.PEReader reader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(image)))
            {
                int offset = reader.PEHeaders.CorHeaderStartOffset + 16;
                BitConverter.GetBytes(BitConverter.ToInt32(image, offset) | 0x2).CopyTo(image, offset);
            }

            Assert.True(DecalPluginLoadContext.ClearRequires32Bit(image));
            Assert.False(DecalPluginLoadContext.ClearRequires32Bit(image));
        }

        [Fact]
        public void OnlyAssembliesHoldingADecalPluginAreTakenForOne()
        {
            Assert.True(DecalPluginCatalog.IsDecalExtension(Path.Combine(FixtureFolder, "AutoWireupExamplePlugin", "AutoWireupExamplePlugin.dll")));
            Assert.False(DecalPluginCatalog.IsDecalExtension(typeof(PluginBase).Assembly.Location));
            Assert.False(DecalPluginCatalog.IsDecalExtension(typeof(GameHost).Assembly.Location));
        }

        // ------------------------------------------------------------------- the stand-ins

        [Fact]
        public void AttributesLoadTheViewAndWireControlsAndBaseEventsBeforeStartup()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            WiredPlugin plugin = new WiredPlugin();

            runtime.Start(plugin, AppContext.BaseDirectory);

            // Startup already found its references set.
            Assert.True(plugin.ReferencesSetAtStartup);
            HostedView window = Assert.Single(runtime.Views);
            Assert.Equal("Wired", window.Owner);
            Assert.Equal("Decal Test", window.View.Title);

            // A press reaches the [ControlEvent] method with the button's own id.
            Assert.True(window.View.Apply(new OverlayCommand(ViewVerbs.Press, controlId: "btnGo")));
            Assert.Equal(new[] { "hit", "click" }, plugin.Presses);
            Assert.Equal(plugin.Go.Id, plugin.LastId);
            Assert.NotEqual(0, plugin.Go.Id);

            // So does a tick, through its wrapper's Change.
            Assert.True(window.View.Apply(new OverlayCommand(ViewVerbs.Set, "true", controlId: "chkOn")));
            Assert.True(plugin.Checked);

            // And the [BaseEvent] methods hear the world and the chat.
            host.AddObject(Mote, "Pyreal Mote");
            Assert.Equal(new[] { "Pyreal Mote" }, plugin.Created);

            host.WorldState.NotifyChat(new ChatMessage(ChatKind.Tell, "hello", "Bob", 0x50000002, 3));
            Assert.Equal("<Tell:IIDString:1342177282:Bob>Bob<\\Tell> tells you, \"hello\"\n", Assert.Single(plugin.Chat));
        }

        [Fact]
        public void ControlWrappersReadAndWriteTheViewTheOverlayDraws()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            WiredPlugin plugin = new WiredPlugin();
            runtime.Start(plugin, AppContext.BaseDirectory);
            DecalView view = runtime.Views[0].View;
            ViewControls controls = plugin.DefaultView.Controls;

            ((StaticWrapper)controls["lblStatus"]).Text = "busy";
            Assert.Equal("busy", view.StaticText("lblStatus").Text);

            ChoiceWrapper choice = (ChoiceWrapper)controls["cmbKind"];
            Assert.Equal("two", choice.Data[1]);
            choice.Add("Third", 3);
            choice.Text[0] = "Primero";
            Assert.Equal(new[] { "Primero", "Second", "Third" }, view.Choice("cmbKind").Options.Select(o => o.Text));
            Assert.Equal(3, choice.Data[2]);
            choice.Remove(1);
            Assert.Equal(3, choice.Data[1]);

            ListWrapper list = (ListWrapper)controls["lstThings"];
            DecalListRow row = list.Add();
            row[0][0] = true;
            row[1][1] = 0x1234;
            row[2][0] = "Pyreal Mote";
            row[2].Color = System.Drawing.Color.Red;
            Assert.True(view.List("lstThings")[0][0].Checked);
            Assert.Equal("portal:06001234", view.List("lstThings")[0][1].ImageKey);
            Assert.Equal("Pyreal Mote", view.List("lstThings")[0][2].Text);
            Assert.Equal(0x06001234, list[0][1][1]);
            Assert.Equal(unchecked((uint)System.Drawing.Color.Red.ToArgb()), view.List("lstThings")[0][2].Color);

            ((ProgressWrapper)controls["prgDone"]).Value = 40;
            Assert.Equal(40, view.Progress("prgDone").Value);

            ((NotebookWrapper)controls["nbMain"]).PageText[1] = "Extra";
            Assert.Equal("Extra", view.Notebook("nbMain").Pages[1].Label);

            Assert.Throws<KeyNotFoundException>(() => controls["nothing"]);
        }

        [Fact]
        public void WorldObjectsAnswerDecalsKeysFromTheHostsObject()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            host.AddObject(Mote, "Pyreal Mote", o =>
            {
                o.IconId = 0x06001234;
                o.WeenieClassId = 3042;
                o.ItemType = ItemTypes.Money;
                o.Value = 250;
                o.StackSize = 5;
                o.ContainerId = MacroTestHost.PlayerId;
                o.Ints[105] = 7;
                o.Strings[16] = "A small pyreal.";
            });

            DecalWorldObject mote = runtime.Core.WorldFilter[unchecked((int)Mote)];
            Assert.Equal("Pyreal Mote", mote.Name);
            Assert.Equal(0x1234, mote.Icon);
            Assert.Equal(3042, mote.Type);
            Assert.Equal(ObjectClass.Money, mote.ObjectClass);
            Assert.Equal(250, mote.Values(LongValueKey.Value));
            Assert.Equal(5, mote.Values(LongValueKey.StackCount));
            Assert.Equal(7, mote.Values(LongValueKey.Workmanship));
            Assert.Equal(unchecked((int)MacroTestHost.PlayerId), mote.Container);
            Assert.Equal("A small pyreal.", mote.Values(StringValueKey.FullDescription));
            Assert.False(mote.Exists(LongValueKey.SlotLegacy));
            Assert.Equal(-1, mote.Values(LongValueKey.RareId, -1));
            Assert.Null(mote.Coordinates());
            Assert.Null(runtime.Core.WorldFilter[0x12345]);

            Assert.Equal(new[] { "Pyreal Mote" }, runtime.Core.WorldFilter.GetInventory().Select(o => o.Name));
            Assert.Equal(new[] { "Pyreal Mote" }, runtime.Core.WorldFilter.GetByNameSubstring("real").Select(o => o.Name));
        }

        [Fact]
        public void CoordinatesAreTheRadarsNumbers()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            // Holtburg's meeting hall: landblock A9B4, about 42.1N 33.6E.
            host.AddCreature(0x50000009, "Town Crier", 0xA9B40031, 84f, 7.5f);

            CoordsObject where = runtime.Core.WorldFilter[0x50000009].Coordinates();
            Assert.Equal("42.1N, 33.6E", where.ToString());
            Assert.Equal(ObjectClass.Npc, runtime.Core.WorldFilter[0x50000009].ObjectClass);
        }

        [Fact]
        public void ActionsGoToTheHostsActions()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            HooksWrapper actions = runtime.Core.Actions;

            actions.UseItem(unchecked((int)Mote), 0);
            actions.CastSpell(1234, unchecked((int)Stone));
            actions.RequestId(unchecked((int)Stone));
            actions.MoveItem(unchecked((int)Mote), unchecked((int)MacroTestHost.PlayerId), 2, true);
            actions.SetCombatMode(CombatState.Magic);

            Assert.Equal(new[]
            {
                $"use 0x{Mote:X8}",
                $"cast 1234 0x{Stone:X8}",
                $"appraise 0x{Stone:X8}",
                $"move 0x{Mote:X8} 0x{MacroTestHost.PlayerId:X8} 2",
                "stance Magic",
            }, host.Actions.Calls);

            actions.AddChatText("Hello from Decal", 5);
            Assert.Contains(("Hello from Decal", 5), host.Shown);
        }

        /// <summary>
        /// A Decal plugin's line arrives as it wrote it - its own prefix, nothing of the host's -
        /// in the colour it named, which was the client's chat type under Decal too. The window
        /// cannot be named in what reaches the client, so every form shows the line once, in its
        /// colour; a closing newline is not part of the line.
        /// </summary>
        [Fact]
        public void APluginsChatTextArrivesUnchangedInItsColour()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            HooksWrapper actions = runtime.Core.Actions;

            actions.AddChatText("[VGI] This character is not selected for tracking.", 5);
            actions.AddChatText("[VI] Disconnected, retry in 15 seconds.", 0, 1);
            actions.AddChatTextRaw("[VTank] Warning: No ust found in inventory. Cannot salvage.", 6);
            actions.AddChatTextRaw("[VHS] Hotkey set.\n", 0, 3);

            Assert.Equal(new[]
            {
                ("[VGI] This character is not selected for tracking.", 5),
                ("[VI] Disconnected, retry in 15 seconds.", 0),
                ("[VTank] Warning: No ust found in inventory. Cannot salvage.", 6),
                ("[VHS] Hotkey set.", 0),
            }, host.Shown);
            Assert.Contains(host.Log.Lines, l => l.Contains("[Decal] [VGI] This character is not selected for tracking."));
        }

        [Fact]
        public void APluginThatThrowsInStartupLeavesNoWindowBehind()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            Assert.Throws<InvalidOperationException>(() => runtime.Start(new FailingPlugin(), AppContext.BaseDirectory));

            Assert.Empty(runtime.Views);
            Assert.Empty(runtime.Extensions);
        }

        [Fact]
        public void LoginIsRaisedForACharacterAlreadyInTheWorld()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            List<string> heard = new List<string>();
            runtime.Core.CharacterFilter.Login += (_, e) => heard.Add($"login {e.Id:X8}");
            runtime.Core.CharacterFilter.LoginComplete += (_, _) => heard.Add("complete");
            runtime.Core.PluginInitComplete += (_, _) => heard.Add("init");

            runtime.CompleteStartup();

            Assert.Equal(new[] { "init", $"login {MacroTestHost.PlayerId:X8}", "complete" }, heard);
            Assert.Equal("Tester", runtime.Core.CharacterFilter.Name);
        }

        /// <summary>
        /// Decal's Logoff, while the character filter still names who left; and the same character
        /// coming back is a login of its own - live, the second login of the same character raised
        /// nothing, so Decal's plugins never knew it had been away.
        /// </summary>
        [Fact]
        public void LogoffIsRaisedAndTheSameCharacterLogsInAgain()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            runtime.CompleteStartup();

            List<string> heard = new List<string>();
            runtime.Core.CharacterFilter.Login += (_, e) => heard.Add($"login {e.Id:X8}");
            runtime.Core.CharacterFilter.LoginComplete += (_, _) => heard.Add("complete");
            runtime.Core.CharacterFilter.Logoff += (_, e) => heard.Add($"logoff {e.Type} {runtime.Core.CharacterFilter.Name}");

            host.WorldState.LeaveWorld("the client closed the session");
            Assert.Equal(0, runtime.Core.CharacterFilter.LoginStatus);

            host.WorldState.GetOrAdd(MacroTestHost.PlayerId, out _).Name = "Tester";
            host.WorldState.SetPlayerId(MacroTestHost.PlayerId);

            Assert.Equal(new[] { "logoff Authorized Tester", $"login {MacroTestHost.PlayerId:X8}", "complete" }, heard);
        }

        [Fact]
        public void VirindiViewServiceViewsBindToTheHostsControls()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            VirindiViewService.XMLParsers.Decal3XMLParser parser = new VirindiViewService.XMLParsers.Decal3XMLParser();
            parser.ParseFromResource("AC.Host.Tests.Resources.decal-test-view.xml", out VirindiViewService.ViewProperties properties, out VirindiViewService.ControlGroup group);
            properties.Title = "Renamed";
            VirindiViewService.HudView view = new VirindiViewService.HudView(properties, group);
            DecalView drawn = Assert.Single(runtime.Views).View;

            Assert.True(VirindiViewService.Service.Running);
            Assert.Equal("Renamed", drawn.Title);

            ((VirindiViewService.Controls.HudStaticText)view["lblStatus"]).Text = "busy";
            Assert.Equal("busy", drawn.StaticText("lblStatus").Text);

            VirindiViewService.Controls.HudList list = (VirindiViewService.Controls.HudList)view["lstThings"];
            VirindiViewService.Controls.HudList.HudListRowAccessor row = list.AddRow();
            ((VirindiViewService.Controls.HudStaticText)row[2]).Text = "Pyreal Mote";
            ((VirindiViewService.Controls.HudPictureBox)row[1]).Image = 0x1234;
            Assert.Equal("Pyreal Mote", drawn.List("lstThings")[0][2].Text);
            Assert.Equal("portal:06001234", drawn.List("lstThings")[0][1].ImageKey);

            List<string> heard = new List<string>();
            view["btnGo"].MouseEvent += (_, e) => heard.Add(e.EventType.ToString());
            ((VirindiViewService.Controls.HudCombo)view["cmbKind"]).Change += (_, _) => heard.Add("combo");
            list.Click += (_, r, c) => heard.Add($"cell {r},{c}");

            drawn.Apply(new OverlayCommand(ViewVerbs.Press, controlId: "btnGo"));
            drawn.Apply(new OverlayCommand(ViewVerbs.Set, "1", controlId: "cmbKind"));
            drawn.Apply(new OverlayCommand(ViewVerbs.Click, "2", rowId: "0", controlId: "lstThings"));

            Assert.Equal(new[] { "MouseDown", "MouseUp", "MouseHit", "combo", "cell 0,2" }, heard);
            Assert.Equal(1, ((VirindiViewService.Controls.HudCombo)view["cmbKind"]).Current);

            view.Dispose();
            Assert.Empty(runtime.Views);
        }

        [Fact]
        public void VirindiViewServiceWindowsBuiltInCodeAreDrawnAndAnswerClicks()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            // Built as VVS plugins build them: controls made with new, filled in, and put in
            // layouts and tabs - some before the layout is in the window, some after.
            VirindiViewService.HudView view = new VirindiViewService.HudView("Built", 300, 200, new VirindiViewService.ACImage(0x1234));
            VirindiViewService.Controls.HudTabView tabs = new VirindiViewService.Controls.HudTabView();
            view.Controls.HeadControl = tabs;

            VirindiViewService.Controls.HudFixedLayout page = new VirindiViewService.Controls.HudFixedLayout();
            VirindiViewService.Controls.HudButton go = new VirindiViewService.Controls.HudButton { Text = "Go" };
            page.AddControl(go, new System.Drawing.Rectangle(4, 4, 60, 20));
            VirindiViewService.Controls.HudCombo kind = new VirindiViewService.Controls.HudCombo(view.Controls);
            kind.AddItem("First", null);
            kind.AddItem("Second", null);
            kind.Current = 1;
            page.AddControl(kind, new System.Drawing.Rectangle(4, 28, 100, 20));
            VirindiViewService.Controls.HudList list = new VirindiViewService.Controls.HudList();
            list.AddColumn(typeof(VirindiViewService.Controls.HudStaticText), 120, "Name");
            page.AddControl(list, new System.Drawing.Rectangle(4, 52, 200, 100));
            tabs.AddTab(page, "Main");

            VirindiViewService.Controls.HudStaticText later = new VirindiViewService.Controls.HudStaticText { Text = "added after" };
            page.AddControl(later, new System.Drawing.Rectangle(120, 4, 100, 16));
            ((VirindiViewService.Controls.HudStaticText)list.AddRow()[0]).Text = "Pyreal Mote";

            DecalView drawn = Assert.Single(runtime.Views).View;
            Assert.Equal("Built", drawn.Title);
            Notebook notebook = Assert.IsType<Notebook>(Assert.Single(((FixedLayout)drawn.Root).Children));
            Assert.Equal("Main", Assert.Single(notebook.Pages).Label);
            FixedLayout content = (FixedLayout)notebook.Pages[0].Content;
            Assert.Equal(new[] { "Go" }, content.Children.OfType<PushButton>().Select(b => b.Text));
            Assert.Equal(new[] { "First", "Second" }, content.Children.OfType<Choice>().Single().Options.Select(o => o.Text));
            Assert.Equal(1, content.Children.OfType<Choice>().Single().Selected);
            Assert.Equal("Pyreal Mote", content.Children.OfType<List>().Single()[0][0].Text);
            Assert.Equal("added after", content.Children.OfType<StaticText>().Single().Text);

            // Code-made controls were named, so the overlay's clicks find them.
            List<string> heard = new List<string>();
            go.Hit += (_, _) => heard.Add("hit");
            Assert.True(drawn.Apply(new OverlayCommand(ViewVerbs.Press, controlId: go.Name)));
            Assert.Equal(new[] { "hit" }, heard);
            Assert.NotEqual(0, go.XMLID);
        }

        /// <summary>
        /// A VVS dropdown's current entry follows VVS's rules, which plugins rely on: the first
        /// entry added becomes current - so Virindi Global Inventory's object class filter, filled
        /// in code and never chosen, reads as its first entry when Search looks it up - and the
        /// number then stays put as entries come and go, until the one it names is deleted. Only
        /// the player's choice raises Change.
        /// </summary>
        [Fact]
        public void AVirindiViewServiceDropdownsCurrentEntryFollowsVvsRules()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            VirindiViewService.HudView view = new VirindiViewService.HudView("Combo", 300, 200, new VirindiViewService.ACImage(0x1234));
            VirindiViewService.Controls.HudFixedLayout page = new VirindiViewService.Controls.HudFixedLayout();
            view.Controls.HeadControl = page;

            VirindiViewService.Controls.HudCombo combo = new VirindiViewService.Controls.HudCombo(view.Controls);
            int changes = 0;
            combo.Change += (_, _) => changes++;
            string Shown() => ((VirindiViewService.Controls.HudStaticText)combo[combo.Current])?.Text;

            // Empty: VVS's number started at 0, with nothing behind it, and refuses one with no entry.
            Assert.Equal(0, combo.Current);
            Assert.Null(combo[0]);
            combo.Current = 3;
            Assert.Equal(0, combo.Current);

            // The first entry added is current, before the dropdown is in the window and after.
            combo.AddItem("0: Any", null);
            Assert.Equal(0, combo.Current);
            page.AddControl(combo, new System.Drawing.Rectangle(4, 4, 100, 20));
            DecalView window = Assert.Single(runtime.Views).View;
            Choice drawn = Assert.Single(window.Controls.OfType<Choice>());
            Assert.Equal(0, drawn.Selected);
            combo.AddItem("1: Armor", null);
            combo.AddItem("2: Weapon", null);
            Assert.Equal("0: Any", Shown());

            // The number stays put, whatever goes in before it or comes out ahead of it.
            combo.Current = 2;
            combo.InsertItem(0, "None", null);
            Assert.Equal(2, combo.Current);
            Assert.Equal("1: Armor", Shown());
            Assert.Equal(2, drawn.Selected);
            combo.DeleteItem(0);
            Assert.Equal("2: Weapon", Shown());

            // Deleting the current entry leaves none; a number with no entry throws, as VVS's did.
            combo.DeleteItem(2);
            Assert.Equal(-1, combo.Current);
            Assert.Equal(-1, drawn.Selected);
            Assert.Throws<ArgumentException>(() => combo.DeleteItem(5));

            // Cleared one at a time from the first, so a current entry past the first leaves its
            // number behind; the next first entry is current again.
            combo.Current = 1;
            combo.Clear();
            Assert.Equal(0, combo.Count);
            Assert.Equal(1, combo.Current);
            combo.AddItem("Again", null);
            Assert.Equal(0, combo.Current);

            // Setting an entry puts one in there, rather than replacing it.
            combo[0] = new VirindiViewService.Controls.HudStaticText { Text = "Before" };
            Assert.Equal(new[] { "Before", "Again" }, drawn.Options.Select(o => o.Text));

            // None of that was the player's doing; this is.
            Assert.Equal(0, changes);
            Assert.True(window.Apply(new OverlayCommand(ViewVerbs.Set, "1", controlId: drawn.Name)));
            Assert.Equal(1, changes);
            Assert.Equal(1, combo.Current);
        }

        /// <summary>A VVS dropdown from a view's XML starts on its first option, as VVS added them one by one.</summary>
        [Fact]
        public void AVirindiViewServiceDropdownFromXmlStartsOnItsFirstOption()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            VirindiViewService.XMLParsers.Decal3XMLParser parser = new VirindiViewService.XMLParsers.Decal3XMLParser();
            parser.ParseFromResource("AC.Host.Tests.Resources.decal-test-view.xml", out VirindiViewService.ViewProperties properties, out VirindiViewService.ControlGroup group);
            VirindiViewService.HudView view = new VirindiViewService.HudView(properties, group);

            VirindiViewService.Controls.HudCombo kind = (VirindiViewService.Controls.HudCombo)view["cmbKind"];
            Assert.Equal(0, kind.Current);
            Assert.Equal(0, Assert.Single(runtime.Views).View.Get<Choice>("cmbKind").Selected);
            view.Dispose();
        }

        /// <summary>
        /// The VVS-edition Decal plugins installed here, run from a copy - installed plugins
        /// are only ever read. Passes without checking anything where none is installed.
        /// </summary>
        [Fact]
        public async Task TheDecalPluginsInstalledOnThisMachineStartAndShowTheirWindows()
        {
            string[] wanted = { "SSSort (VVS Edition)", "ChaosHelper" };
            AC.Dat.DecalInstall install = AC.Dat.DecalInstall.Detect();
            List<AC.Dat.DecalPlugin> found = wanted.Select(install.FindPlugin).Where(p => p?.FullPath != null && File.Exists(p.FullPath)).ToList();
            if (found.Count == 0)
                return;

            string folder = Path.Combine(_dataRoot, "installed");
            foreach (AC.Dat.DecalPlugin plugin in found)
            {
                string target = Path.Combine(folder, Path.GetFileNameWithoutExtension(plugin.FullPath));
                Directory.CreateDirectory(target);
                foreach (string file in Directory.GetFiles(plugin.Directory))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }

            ListLog log = new ListLog();
            GameHost host = new GameHost(new LingeringTransport(), log, new Dictionary<string, string> { ["DecalCompat:Folder"] = folder }, _dataRoot);
            DecalCompatPlugin decal = new DecalCompatPlugin(new FakeDecalRegistry());
            host.AddPlugin(decal);
            await host.StartAsync();

            Assert.Equal(found.Count, decal.Entries.Count);
            Assert.All(decal.Entries, e => Assert.Equal("running", e.Status));

            IReadOnlyList<OverlayWindowInfo> windows = await OnGameThreadAsync(host, host.CollectWindows);
            foreach (DecalPluginEntry entry in decal.Entries)
                Assert.Contains(windows, w => w.Owner == OverlayViewWindow.OwnerFor(DecalCompatPlugin.PluginName, entry.Name) && w.View.Controls.Count > 1);

            Assert.DoesNotContain(log.Lines, l => l.StartsWith("ERROR"));
            await host.DisposeAsync();
        }

        [Fact]
        public void ThePluginSitesHooksRaiseRenderPreUIOnTheTick()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            HookedPlugin plugin = new HookedPlugin();
            runtime.Start(plugin, AppContext.BaseDirectory);

            host.RaiseTick(TimeSpan.FromMilliseconds(100));
            host.RaiseTick(TimeSpan.FromMilliseconds(100));

            Assert.Equal(2, plugin.Frames);
        }

        [Fact]
        public void TheIdQueueAsksForOneObjectAtATimeAndSaysWhenItArrives()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            host.AddObject(Mote, "Pyreal Mote");
            host.AddObject(Stone, "Mana Stone");
            List<int> processed = new List<int>();
            runtime.Core.IDQueue.UserIDRequestProcessed += (_, e) => processed.Add(e.ObjectId);

            runtime.Core.IDQueue.AddToQueue(unchecked((int)Mote));
            runtime.Core.IDQueue.AddToQueue(unchecked((int)Stone));
            runtime.Core.IDQueue.AddToQueue(0x7FFFFFF0);

            host.RaiseTick(TimeSpan.FromMilliseconds(400));
            Assert.Equal(new[] { $"appraise 0x{Mote:X8}" }, host.Actions.Calls);

            // Its appraisal arrives, so it is done with; the next goes out on a later tick, and
            // the object that is not in the world never does.
            host.WorldState.NotifyAppraised(host.WorldState.Get(Mote));
            host.RaiseTick(TimeSpan.FromMilliseconds(100));
            host.RaiseTick(TimeSpan.FromMilliseconds(300));

            Assert.Equal(new[] { $"appraise 0x{Mote:X8}", $"appraise 0x{Stone:X8}" }, host.Actions.Calls);
            Assert.Equal(new[] { unchecked((int)Mote) }, processed);
        }

        [Fact]
        public async Task DecalsFileServiceIsStartedForPluginsToAskForSpells()
        {
            (GameHost host, DecalCompatPlugin decal, _) = await StartAsync(vvs: true);

            FileService files = await OnGameThreadAsync(host, () => decal.Runtime.Core.Filter<FileService>());
            Assert.Same(files, await OnGameThreadAsync(host, () => decal.Runtime.Core.FileService));

            // With a portal file on this machine, the spells are the client's; without one, none.
            SpellTable spells = await OnGameThreadAsync(host, () => files.SpellTable);
            if (spells.Length > 0)
            {
                Spell first = spells[0];
                Assert.Same(first, spells.GetById(first.Id));
                Assert.False(string.IsNullOrEmpty(first.Name));
            }

            await host.DisposeAsync();
        }

        [Fact]
        public void AChatCommandReachesTheDecalPluginThatClaimsIt()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            CommandPlugin plugin = new CommandPlugin();
            runtime.Start(plugin, AppContext.BaseDirectory);

            Assert.True(runtime.InvokeCommandLine("/probe hello"));
            Assert.False(runtime.InvokeCommandLine("/mt use dwennon"));
            Assert.Equal(new[] { "/probe hello", "/mt use dwennon" }, plugin.Heard);
        }

        [Fact]
        public async Task TheHostOffersAChatCommandToEachPluginThatTakesThemButNotItsSender()
        {
            ListLog log = new ListLog();
            GameHost host = new GameHost(new LingeringTransport(), log, dataRoot: _dataRoot);
            CommandTaker first = new CommandTaker("First", "/one");
            CommandTaker second = new CommandTaker("Second", "/two");
            host.AddPlugin(first);
            host.AddPlugin(second);
            await host.StartAsync();

            Assert.True(await OnGameThreadAsync(host, () => host.DispatchChatCommand("/two go")));
            Assert.False(await OnGameThreadAsync(host, () => host.DispatchChatCommand("/one go", from: first)));
            Assert.False(await OnGameThreadAsync(host, () => host.DispatchChatCommand("/three go")));
            Assert.Equal(new[] { "/two go", "/three go" }, first.Heard);
            Assert.Equal(new[] { "/two go", "/one go", "/three go" }, second.Heard);

            await host.DisposeAsync();
        }

        // ------------------------------------------------------------------- plugins written here

        [WireUpBaseEvents]
        private sealed class CommandPlugin : PluginBase
        {
            internal List<string> Heard { get; } = new List<string>();

            protected override void Startup()
            {
            }

            protected override void Shutdown()
            {
            }

            [BaseEvent("CommandLineText")]
            private void OnCommand(object sender, ChatParserInterceptEventArgs e)
            {
                Heard.Add(e.Text);
                if (e.Text.StartsWith("/probe", StringComparison.Ordinal))
                    e.Eat = true;
            }
        }

        private sealed class CommandTaker : IPlugin, IChatCommands
        {
            private readonly string _prefix;

            internal CommandTaker(string name, string prefix)
            {
                Name = name;
                _prefix = prefix;
            }

            public string Name { get; }

            internal List<string> Heard { get; } = new List<string>();

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }

            public bool TryCommand(string text)
            {
                Heard.Add(text);
                return text.StartsWith(_prefix, StringComparison.Ordinal);
            }
        }

        private sealed class HookedPlugin : PluginBase
        {
            internal int Frames;

            protected override void Startup()
            {
                // As Virindi's plugins took their heartbeat: through the COM plugin site.
                IACHooksEvents_Event hooks = Host.Underlying.Hooks;
                hooks.RenderPreUI += new IACHooksEvents_RenderPreUIEventHandler(OnFrame);
            }

            protected override void Shutdown()
            {
            }

            private void OnFrame() => Frames++;
        }

        [FriendlyName("Wired")]
        [View("AC.Host.Tests.Resources.decal-test-view.xml")]
        [WireUpControlEvents]
        [WireUpBaseEvents]
        private sealed class WiredPlugin : PluginBase
        {
            [ControlReference("btnGo")]
            internal PushButtonWrapper Go;

            [ControlReference("chkOn")]
            internal CheckBoxWrapper On;

            internal bool ReferencesSetAtStartup;
            internal bool Checked;
            internal int LastId;
            internal List<string> Presses { get; } = new List<string>();
            internal List<string> Created { get; } = new List<string>();
            internal List<string> Chat { get; } = new List<string>();

            protected override void Startup() => ReferencesSetAtStartup = Go != null && On != null;

            protected override void Shutdown()
            {
            }

            [ControlEvent("btnGo", "Hit")]
            private void GoHit(object sender, ControlEventArgs e) => Presses.Add("hit");

            [ControlEvent("btnGo", "Click")]
            private void GoClick(object sender, ControlEventArgs e)
            {
                Presses.Add("click");
                LastId = e.Id;
            }

            [ControlEvent("chkOn", "Change")]
            private void OnChanged(object sender, CheckBoxChangeEventArgs e) => Checked = e.Checked;

            [BaseEvent("CreateObject", "WorldFilter")]
            private void OnCreate(object sender, CreateObjectEventArgs e) => Created.Add(e.New.Name);

            [BaseEvent("ChatBoxMessage")]
            private void OnChat(object sender, ChatTextInterceptEventArgs e) => Chat.Add(e.Text);
        }

        [View("AC.Host.Tests.Resources.decal-test-view.xml")]
        private sealed class FailingPlugin : PluginBase
        {
            protected override void Startup() => throw new InvalidOperationException("no start today");

            protected override void Shutdown()
            {
            }
        }

        // ------------------------------------------------------------------- the host around them

        private async Task<(GameHost host, DecalCompatPlugin decal, ListLog log)> StartAsync(bool vvs)
        {
            ListLog log = new ListLog();
            Dictionary<string, string> settings = new Dictionary<string, string>
            {
                ["DecalCompat:Folder"] = FixtureFolder,
                ["DecalCompat:VirindiViewService"] = vvs ? "true" : "false",
            };

            GameHost host = new GameHost(new LingeringTransport(), log, settings, _dataRoot);
            DecalCompatPlugin decal = new DecalCompatPlugin(new FakeDecalRegistry());
            host.AddPlugin(decal);
            await host.StartAsync();
            return (host, decal, log);
        }

        private static int AddObject(GameHost host, uint id, string name, uint icon)
        {
            HostWorldObject obj = host.WorldState.GetOrAdd(id, out _);
            obj.Name = name;
            obj.IconId = icon;
            obj.ItemType = ItemTypes.Misc;
            host.WorldState.NotifyCreated(obj);
            return 0;
        }

        /// <summary>The AutoWireup plugin's window's view, as the host publishes it. Game thread only.</summary>
        private static DecalView WindowView(GameHost host)
            => host.CollectWindows().Single(w => w.Owner == AutoWireupWindow).View;

        private static async Task SettleAsync(GameHost host)
        {
            TaskCompletionSource done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() => done.SetResult());
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task<T> OnGameThreadAsync<T>(GameHost host, Func<T> read)
        {
            TaskCompletionSource<T> done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() =>
            {
                try
                {
                    done.SetResult(read());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>Stays open until disposed, so the host keeps running its game thread.</summary>
        private sealed class LingeringTransport : IGameTransport
        {
            public string Description => "lingering";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public bool CanSend => false;

            public bool CanShowInGame => false;

            public Task StartAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AC.Protocol.AcMessage message, System.Threading.CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task ShowInGameAsync(AC.Protocol.AcMessage message, System.Threading.CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                Ended?.Invoke(this, EventArgs.Empty);
                MessageReceived = null;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Everything that runs a Decal, which there is one of per process.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class DecalCollection
    {
        public const string Name = "Decal";
    }
}
