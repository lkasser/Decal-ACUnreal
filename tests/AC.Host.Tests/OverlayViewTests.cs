using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using AC.Host.Transport;
using Xunit;
using Wire = AC.Host.Overlay;

namespace AC.Host.Tests
{
    /// <summary>
    /// Decal views in the host and on the wire. What matters is that a view reaches the
    /// overlay as the plugin last left it, and that a click on one of its controls comes
    /// back to that control's event - on the game thread, counted - while anything the view
    /// has no control for still reaches the plugin's own command handler as before.
    /// </summary>
    public class OverlayViewTests
    {
        private sealed class ViewPlugin : IPlugin, IOverlayView, IOverlayCommands
        {
            private readonly Func<DecalView> _view;

            public ViewPlugin(string name, Func<DecalView> view)
            {
                Name = name;
                _view = view;
            }

            public string Name { get; }

            public DecalView View => _view();

            public int GameThreadId { get; private set; }

            public List<OverlayCommand> Received { get; } = new List<OverlayCommand>();

            public void Startup(IHost host)
            {
                // Startup runs on the game thread, so this is the thread every callback
                // should arrive on.
                GameThreadId = Environment.CurrentManagedThreadId;
            }

            public void Shutdown()
            {
            }

            public bool HandleCommand(OverlayCommand command)
            {
                Received.Add(command);
                return true;
            }
        }

        private sealed class ViewOnlyPlugin : IPlugin, IOverlayView
        {
            public string Name => "ViewOnly";

            public DecalView View { get; } = DecalView.Parse(SampleViews.Tank);

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }
        }

        private sealed class QuietPlugin : IPlugin, IOverlayPanels
        {
            public string Name => "Quiet";

            public IReadOnlyList<OverlayPanel> GetPanels() => Array.Empty<OverlayPanel>();

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }
        }

        /// <summary>
        /// Stays open until disposed, as in the command tests: a capture transport ends at
        /// once, and a host that has ended drops whatever is queued for its game thread.
        /// </summary>
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

        private static async Task<(GameHost host, ListLog log)> RunAsync(params IPlugin[] plugins)
        {
            ListLog log = new ListLog();
            GameHost host = new GameHost(
                new LingeringTransport(),
                log,
                dataRoot: Path.Combine(Path.GetTempPath(), "achost-tests"));

            foreach (IPlugin plugin in plugins)
                host.AddPlugin(plugin);

            await host.StartAsync();
            return (host, log);
        }

        /// <summary>Waits until everything queued for the game thread so far has run.</summary>
        private static async Task SettleAsync(GameHost host)
        {
            TaskCompletionSource done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() => done.SetResult());
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>Runs on the game thread, where windows and views may be read.</summary>
        private static async Task<T> OnGameThreadAsync<T>(GameHost host, Func<T> read)
        {
            TaskCompletionSource<T> done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() => done.SetResult(read()));
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static OverlayCommand Set(string control, string value) => new OverlayCommand(ViewVerbs.Set, value, controlId: control);

        // --- The host ------------------------------------------------------------------------

        [Fact]
        public async Task APluginWithAViewHasItInItsWindowAndOthersHaveNone()
        {
            DecalView view = DecalView.Parse(SampleViews.Tank);
            ViewPlugin tank = new ViewPlugin("VirindiTank", () => view);

            (GameHost host, _) = await RunAsync(tank, new QuietPlugin());

            IReadOnlyList<OverlayWindowInfo> windows = await OnGameThreadAsync(host, host.CollectWindows);

            Assert.Equal(new[] { "VirindiTank", "Quiet" }, windows.Select(w => w.Owner));
            Assert.Same(view, windows[0].View);
            Assert.True(windows[0].Enabled);
            Assert.Null(windows[1].View);
            Assert.True(windows[1].Enabled);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task APluginWhoseViewThrowsKeepsItsWindowMarkedDisabled()
        {
            ViewPlugin bad = new ViewPlugin("Bad", () => throw new InvalidOperationException("no view today"));
            (GameHost host, ListLog log) = await RunAsync(bad);

            long before = host.Statistics.PluginExceptions;
            IReadOnlyList<OverlayWindowInfo> windows = await OnGameThreadAsync(host, host.CollectWindows);

            OverlayWindowInfo window = Assert.Single(windows);
            Assert.Equal("Bad", window.Owner);
            Assert.False(window.Enabled);
            Assert.Null(window.View);
            Assert.Equal(before + 1, host.Statistics.PluginExceptions);
            Assert.Contains(log.Lines, l => l.StartsWith("ERROR") && l.Contains("giving its view") && l.Contains("no view today"));

            await host.DisposeAsync();
        }

        [Fact]
        public async Task ACommandNamingAViewControlReachesItsEventOnTheGameThread()
        {
            DecalView view = DecalView.Parse(SampleViews.Tank);
            ViewPlugin tank = new ViewPlugin("VirindiTank", () => view);

            List<(bool isChecked, int thread)> raised = new List<(bool, int)>();
            view.Checkbox("cExact").Changed += (_, e) => raised.Add((e.Checked, Environment.CurrentManagedThreadId));

            (GameHost host, _) = await RunAsync(tank);

            host.DispatchCommand("VirindiTank", Set("cExact", "false"));
            await SettleAsync(host);

            (bool isChecked, int thread) = Assert.Single(raised);
            Assert.False(isChecked);
            Assert.Equal(tank.GameThreadId, thread);
            Assert.False(view.Checkbox("cExact").Checked);

            Assert.Equal(1, host.Statistics.CommandsHandled);
            Assert.Equal(0, host.Statistics.CommandsUnhandled);

            // The view took it, so the plugin's general handler never saw it.
            Assert.Empty(tank.Received);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task ACommandTheViewHasNoControlForGoesToThePluginsHandler()
        {
            DecalView view = DecalView.Parse(SampleViews.Tank);
            ViewPlugin tank = new ViewPlugin("VirindiTank", () => view);
            (GameHost host, _) = await RunAsync(tank);

            host.DispatchCommand("VirindiTank", Set("echo", "true"));
            host.DispatchCommand("VirindiTank", new OverlayCommand("toggle-looting"));
            await SettleAsync(host);

            Assert.Equal(new[] { "echo", string.Empty }, tank.Received.Select(c => c.ControlId));
            Assert.Equal(2, host.Statistics.CommandsHandled);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task APluginWithNoViewThisTimeGetsEveryCommandItself()
        {
            ViewPlugin tank = new ViewPlugin("VirindiTank", () => null);
            (GameHost host, _) = await RunAsync(tank);

            host.DispatchCommand("VirindiTank", Set("cExact", "true"));
            await SettleAsync(host);

            Assert.Equal("cExact", Assert.Single(tank.Received).ControlId);
            Assert.Equal(1, host.Statistics.CommandsHandled);

            await host.DisposeAsync();
        }

        /// <summary>
        /// A value the view cannot take is the view's to refuse, not something to pass on to
        /// the plugin's handler as though the control were unknown - and the log says why.
        /// </summary>
        [Fact]
        public async Task ACommandTheViewRefusesIsCountedAndTheReasonLogged()
        {
            DecalView view = DecalView.Parse(SampleViews.Tank);
            ViewPlugin tank = new ViewPlugin("VirindiTank", () => view);
            (GameHost host, ListLog log) = await RunAsync(tank);

            host.DispatchCommand("VirindiTank", Set("cmbNavType", "7"));
            await SettleAsync(host);

            Assert.Equal(0, host.Statistics.CommandsHandled);
            Assert.Equal(1, host.Statistics.CommandsUnhandled);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN") && l.Contains("cmbNavType") && l.Contains("not an option"));
            Assert.Empty(tank.Received);
            Assert.Equal(1, view.Choice("cmbNavType").Selected);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task AHandlerThatThrowsCostsOneClickAndTheNextStillArrives()
        {
            DecalView view = DecalView.Parse(SampleViews.Tank);
            ViewPlugin tank = new ViewPlugin("VirindiTank", () => view);

            int calls = 0;
            view.PushButton("bForceBuff").Clicked += (_, _) =>
            {
                if (++calls == 1)
                    throw new InvalidOperationException("first press broke");
            };

            (GameHost host, ListLog log) = await RunAsync(tank);

            host.DispatchCommand("VirindiTank", new OverlayCommand(ViewVerbs.Press, controlId: "bForceBuff"));
            host.DispatchCommand("VirindiTank", new OverlayCommand(ViewVerbs.Press, controlId: "bForceBuff"));
            await SettleAsync(host);

            Assert.Equal(2, calls);
            Assert.Equal(1, host.Statistics.PluginExceptions);
            Assert.Equal(1, host.Statistics.CommandsHandled);
            Assert.Equal(1, host.Statistics.CommandsUnhandled);
            Assert.Contains(log.Lines, l => l.StartsWith("ERROR") && l.Contains("threw handling") && l.Contains("first press broke"));

            await host.DisposeAsync();
        }

        [Fact]
        public async Task APluginWithOnlyAViewStillSaysWhenACommandIsNotForIt()
        {
            ViewOnlyPlugin plugin = new ViewOnlyPlugin();
            int pressed = 0;
            plugin.View.PushButton("bForceBuff").Clicked += (_, _) => pressed++;

            (GameHost host, ListLog log) = await RunAsync(plugin);

            host.DispatchCommand("ViewOnly", new OverlayCommand(ViewVerbs.Press, controlId: "bForceBuff"));
            host.DispatchCommand("ViewOnly", new OverlayCommand(ViewVerbs.Press, controlId: "bNotInTheView"));
            await SettleAsync(host);

            Assert.Equal(1, pressed);
            Assert.Equal(1, host.Statistics.CommandsHandled);
            Assert.Equal(1, host.Statistics.CommandsUnhandled);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN") && l.Contains("does not take commands") && l.Contains("bNotInTheView"));

            await host.DisposeAsync();
        }

        // --- The wire ------------------------------------------------------------------------

        private static IEnumerable<Wire.OverlayViewControl> Flatten(Wire.OverlayViewControl control)
        {
            yield return control;

            foreach (Wire.OverlayViewPage page in control.Pages)
            {
                foreach (Wire.OverlayViewControl inner in Flatten(page.Content))
                    yield return inner;
            }

            foreach (Wire.OverlayViewControl child in control.Children)
            {
                foreach (Wire.OverlayViewControl inner in Flatten(child))
                    yield return inner;
            }
        }

        private static Wire.OverlayViewControl Named(Wire.OverlayView view, string name)
            => Flatten(view.Root).Single(c => c.Name == name);

        [Fact]
        public void TheViewMapsToTheWireTreeAsTheXmlDescribesIt()
        {
            DecalView view = DecalView.Parse(SampleViews.Tank);
            Wire.OverlayView dto = Wire.OverlayMapping.ToDto(view);

            Assert.Equal("uTank2", dto.Title);
            Assert.Equal("portal:06000064", dto.Icon);
            Assert.Equal((600, 220), (dto.Width, dto.Height));

            Wire.OverlayViewControl root = dto.Root;
            Assert.Equal(Wire.ViewControlTypes.Notebook, root.Type);
            Assert.Equal("MainTabs", root.Name);
            Assert.Equal(0, root.Selected);
            Assert.Equal(new[] { "Options", "Route", "Vitals" }, root.Pages.Select(p => p.Label));
            Assert.All(root.Pages, p => Assert.Equal(Wire.ViewControlTypes.Fixed, p.Content.Type));

            Assert.Equal(
                new[]
                {
                    Wire.ViewControlTypes.Static, Wire.ViewControlTypes.Edit, Wire.ViewControlTypes.PushButton,
                    Wire.ViewControlTypes.Checkbox, Wire.ViewControlTypes.Checkbox, Wire.ViewControlTypes.Progress,
                },
                root.Pages[0].Content.Children.Select(c => c.Type));

            Wire.OverlayViewControl bottom = Named(dto, "layoutBottom");
            Assert.Equal(Wire.ViewControlTypes.Fixed, bottom.Type);
            Assert.Equal((6, 112, 300, 20), (bottom.X, bottom.Y, bottom.W, bottom.H));
            Assert.Equal(new[] { "cmbNavType", "btnNavDown" }, bottom.Children.Select(c => c.Name));

            Wire.OverlayViewControl range = Named(dto, "txtRange");
            Assert.Equal((80, 6, 50, 16), (range.X, range.Y, range.W, range.H));
            Assert.Equal("35", range.Value);
            Assert.Equal("portal:06001276", range.Image);

            Wire.OverlayViewControl exact = Named(dto, "cExact");
            Assert.Equal("Exact", exact.Text);
            Assert.True(exact.Checked);
            Assert.False(Named(dto, "cEnableBuffing").Checked);

            Assert.Equal("Buff now", Named(dto, "bForceBuff").Text);
            Assert.Equal(Wire.ViewControlTypes.Button, Named(dto, "btnNavDown").Type);

            Wire.OverlayViewControl styled = Named(dto, "lblE");
            Assert.Equal("E", styled.Text);
            Assert.Equal(0xFFC00000L, styled.TextColor);
            Assert.Equal(16, styled.FontSize);
            Assert.True(styled.Bold);
            Assert.Equal("center", styled.Justify);

            Wire.OverlayViewControl plain = Named(dto, "Label12");
            Assert.Equal(-1, plain.TextColor);
            Assert.Equal(0, plain.FontSize);
            Assert.Equal(string.Empty, plain.Justify);

            Wire.OverlayViewControl nav = Named(dto, "cmbNavType");
            Assert.Equal(new[] { "Circular", "Linear" }, nav.Options);
            Assert.Equal(1, nav.Selected);

            Wire.OverlayViewControl hp = Named(dto, "slMyHP");
            Assert.Equal((10.0, 90.0), (hp.Min, hp.Max));
            Assert.Equal("10", hp.Value);
            Assert.False(hp.Vertical);

            Wire.OverlayViewControl buffs = Named(dto, "prgBuffs");
            Assert.Equal((0.0, 50.0), (buffs.Min, buffs.Max));
            Assert.Equal("25", buffs.Value);

            Wire.OverlayViewControl monsters = Named(dto, "lstMonsters");
            Assert.Equal(
                new[] { Wire.ViewColumnTypes.Check, Wire.ViewColumnTypes.Text, Wire.ViewColumnTypes.Icon, Wire.ViewColumnTypes.Text },
                monsters.Columns.Select(c => c.Type));
            Assert.Equal(new[] { 16, 200, 16, 0 }, monsters.Columns.Select(c => c.Width));
            Assert.Empty(monsters.Rows);

            Assert.All(Flatten(root), c => Assert.True(c.Enabled && c.Visible));
        }

        /// <summary>
        /// Every kind the wire names is one the host can produce: a type added to the wire
        /// without a mapping would otherwise sit unused, and the overlay would never be sent it.
        /// </summary>
        [Fact]
        public void EveryWireControlTypeIsProducedFromTheSample()
        {
            string[] wire = typeof(Wire.ViewControlTypes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (string)f.GetRawConstantValue())
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToArray();

            string[] produced = Flatten(Wire.OverlayMapping.ToDto(DecalView.Parse(SampleViews.Tank)).Root)
                .Select(c => c.Type)
                .Distinct()
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(wire, produced);
        }

        [Fact]
        public void WhatThePluginAndThePlayerChangedIsWhatGoesOver()
        {
            DecalView view = DecalView.Parse(SampleViews.Tank);

            view.Notebook("MainTabs").ActivePage = 2;
            view.Checkbox("cEnableBuffing").Checked = true;
            view.Checkbox("cExact").Enabled = false;
            view.StaticText("Label12").Visible = false;
            view.StaticText("Label12").TextColor = ViewColor.Rgb(0, 255, 0);
            view.ImageButton("btnNavDown").SetPortalImage(0x060011F8);
            view.Edit("txtRange").Text = null;
            Assert.True(view.Apply(new OverlayCommand(ViewVerbs.Set, "0", controlId: "cmbNavType")));

            List monsters = view.List("lstMonsters");
            ListRow drudge = monsters.Add();
            drudge[0].Checked = true;
            drudge[1].Text = "Drudge Skulker";
            drudge[1].Color = ViewColor.Rgb(255, 0, 0);
            drudge[2].SetPortalImage(0x060011F8);
            monsters.Add()[1].Text = "Mosswart";

            Wire.OverlayView dto = Wire.OverlayMapping.ToDto(view);

            Assert.Equal(2, dto.Root.Selected);
            Assert.True(Named(dto, "cEnableBuffing").Checked);
            Assert.False(Named(dto, "cExact").Enabled);
            Assert.False(Named(dto, "Label12").Visible);
            Assert.Equal(0xFF00FF00L, Named(dto, "Label12").TextColor);
            Assert.Equal("portal:060011F8", Named(dto, "btnNavDown").Image);
            Assert.Equal(string.Empty, Named(dto, "txtRange").Value);
            Assert.Equal(0, Named(dto, "cmbNavType").Selected);

            Wire.OverlayViewControl list = Named(dto, "lstMonsters");
            Assert.Equal(2, list.Rows.Count);
            Assert.All(list.Rows, r => Assert.Equal(4, r.Cells.Count));

            Wire.OverlayViewCell[] first = list.Rows[0].Cells.ToArray();
            Assert.True(first[0].Checked);
            Assert.Equal("Drudge Skulker", first[1].Text);
            Assert.Equal(0xFFFF0000L, first[1].Color);
            Assert.Equal("portal:060011F8", first[2].Image);
            Assert.Equal(-1, first[3].Color);

            Assert.Equal("Mosswart", list.Rows[1].Cells[1].Text);
            Assert.False(list.Rows[1].Cells[0].Checked);
        }

        [Fact]
        public void AnUnknownControlGoesOverWithNoTypeSoTheOverlaySkipsIt()
        {
            DecalView view = DecalView.Parse(@"<view><control progid=""DecalControls.FixedLayout"">
                <control progid=""DecalControls.Scroller"" name=""scr"" left=""1"" top=""2"" width=""3"" height=""4""/>
            </control></view>");

            Assert.Single(view.Warnings);

            Wire.OverlayViewControl unknown = Assert.Single(Wire.OverlayMapping.ToDto(view).Root.Children);
            Assert.Equal(string.Empty, unknown.Type);
            Assert.Equal("scr", unknown.Name);
            Assert.Equal((1, 2, 3, 4), (unknown.X, unknown.Y, unknown.W, unknown.H));
        }

        [Fact]
        public void NumbersGoOverInvariantWhateverTheThreadsCulture()
        {
            DecalView view = DecalView.Parse(SampleViews.Tank);
            view.Slider("slMyHP").Position = 42.5;
            view.Progress("prgBuffs").Value = 0.25;

            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                Wire.OverlayView dto = Wire.OverlayMapping.ToDto(view);
                Assert.Equal("42.5", Named(dto, "slMyHP").Value);
                Assert.Equal("0.25", Named(dto, "prgBuffs").Value);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        /// <summary>
        /// The window mapping the CLI did inline, now in one place: every field of every
        /// control as before, and the view alongside.
        /// </summary>
        [Fact]
        public void AWindowMapsItsControlsAsBeforeAndCarriesItsView()
        {
            DecalView view = DecalView.Parse(SampleViews.Flat);
            OverlayWindowInfo window = new OverlayWindowInfo(
                "VirindiTank",
                enabled: true,
                new[]
                {
                    OverlayControl.Toggle("loot", "Pick loot up", true) with { Tooltip = "Takes loot." },
                    OverlayControl.Slider("range", "Range", 12.5, 1, 40, step: 0.5),
                    OverlayControl.Choice("mode", "Mode", "Magic", new[] { "Melee", null, "Magic" }),
                },
                view);

            Wire.OverlayWindow dto = Wire.OverlayMapping.ToDto(window);

            Assert.Equal("VirindiTank", dto.Owner);
            Assert.True(dto.Enabled);
            Assert.Equal(string.Empty, dto.Title);

            Assert.Equal(new[] { "loot", "range", "mode" }, dto.Controls.Select(c => c.Id));

            Wire.OverlayControl loot = dto.Controls[0];
            Assert.Equal(Wire.ControlKinds.Toggle, loot.Kind);
            Assert.Equal("Pick loot up", loot.Label);
            Assert.Equal("true", loot.Value);
            Assert.Equal("Takes loot.", loot.Tooltip);

            Wire.OverlayControl range = dto.Controls[1];
            Assert.Equal(Wire.ControlKinds.Slider, range.Kind);
            Assert.Equal("12.5", range.Value);
            Assert.Equal((1.0, 40.0, 0.5), (range.Min, range.Max, range.Step));

            // A null option would be a JSON null, which the overlay's parser cannot read.
            Assert.Equal(new[] { "Melee", string.Empty, "Magic" }, dto.Controls[2].Options);

            Assert.NotNull(dto.View);
            Assert.Equal("Example Plugin", dto.View.Title);
            Assert.Equal(Wire.ViewControlTypes.Fixed, dto.View.Root.Type);

            Wire.OverlayWindow plain = Wire.OverlayMapping.ToDto(new OverlayWindowInfo("Quiet", false, null));
            Assert.False(plain.Enabled);
            Assert.Empty(plain.Controls);
            Assert.Null(plain.View);

            Assert.Null(Wire.OverlayMapping.ToDto((DecalView)null));
        }

        [Fact]
        public void TheJsonCarriesTheViewUnderTheNamesTheOverlayReads()
        {
            DecalView view = DecalView.Parse(SampleViews.Tank);
            view.List("lstMonsters").Add()[1].Text = "Drudge";

            Wire.OverlayState state = new Wire.OverlayState();
            state.Windows.Add(Wire.OverlayMapping.ToDto(new OverlayWindowInfo("VirindiTank", true, null, view)));

            string json = Wire.OverlayJson.ToJson(state);

            foreach (string key in new[] { "view", "root", "pages", "children", "columns", "rows", "text_color", "font_size" })
                Assert.Contains($"\"{key}\":", json);

            Wire.OverlayState back = Wire.OverlayJson.ReadState(json);
            Wire.OverlayView read = Assert.Single(back.Windows).View;
            Assert.Equal("uTank2", read.Title);
            Assert.Equal(3, read.Root.Pages.Count);
            Assert.Equal("Drudge", Named(read, "lstMonsters").Rows[0].Cells[1].Text);
            Assert.Equal(0xFFC00000L, Named(read, "lblE").TextColor);
        }
    }
}
