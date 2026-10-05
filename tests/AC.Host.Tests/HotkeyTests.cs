using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AC.Host.Decal;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using AC.Host.Transport;
using AC.Proxy;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>Keys that trigger a plugin's actions, as Virindi's hotkey system gave Decal plugins.</summary>
    public class HotkeyTests
    {
        [Theory]
        [InlineData("Ctrl+Shift+F12", 0x7B, true, true, false, "Ctrl+Shift+F12")]
        [InlineData("alt+1", '1', false, false, true, "Alt+1")]
        [InlineData("F5", 0x74, false, false, false, "F5")]
        [InlineData(" ctrl + m ", 'M', true, false, false, "Ctrl+M")]
        [InlineData("Shift+Home", 0x24, false, true, false, "Shift+Home")]
        [InlineData("Ctrl+Numpad5", 0x65, true, false, false, "Ctrl+Numpad5")]
        public void KeysAreReadAndWrittenAsThePlayerTypesThem(string text, int key, bool ctrl, bool shift, bool alt, string written)
        {
            Assert.True(KeyChord.TryParse(text, out KeyChord chord));
            Assert.Equal(new KeyChord(key, ctrl, shift, alt), chord);
            Assert.Equal(written, chord.ToString());
        }

        [Theory]
        [InlineData("Ctrl+Nonsense")]
        [InlineData("Ctrl+Shift")]
        [InlineData("A+B")]
        public void WhatIsNotAKeyIsRefused(string text) => Assert.False(KeyChord.TryParse(text, out _));

        [Fact]
        public void NothingTypedIsNoKey()
        {
            Assert.True(KeyChord.TryParse("  ", out KeyChord chord));
            Assert.True(chord.IsEmpty);
            Assert.Equal(string.Empty, chord.ToString());
        }

        private sealed class HotkeyPlugin : IPlugin, IOverlayHotkeys
        {
            private readonly string _name;
            private readonly HotkeyDefinition[] _hotkeys;

            public HotkeyPlugin(string name, params HotkeyDefinition[] hotkeys)
            {
                _name = name;
                _hotkeys = hotkeys;
            }

            public string Name => _name;

            public List<string> Pressed { get; } = new List<string>();

            public IReadOnlyList<HotkeyDefinition> Hotkeys => _hotkeys;

            public void HotkeyPressed(string id) => Pressed.Add(id);

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }
        }

        private static GameHost NewHost(string root = null)
            => new GameHost(new CaptureTransport(Array.Empty<CapturedDatagram>()), new ListLog(), dataRoot: root ?? Path.Combine(Path.GetTempPath(), "achost-hotkeys-" + Guid.NewGuid().ToString("N")));

        /// <summary>A transport that stays up until disposed, so commands sent after the host starts are run.</summary>
        private sealed class OpenTransport : IGameTransport
        {
            public string Description => "open";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public bool CanSend => true;

            public bool CanShowInGame => true;

            public Task ShowInGameAsync(AC.Protocol.AcMessage message, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task StartAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AC.Protocol.AcMessage message, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                MessageReceived = null;
                Ended?.Invoke(this, EventArgs.Empty);
                Ended = null;
                return ValueTask.CompletedTask;
            }
        }

        private static GameHost NewLiveHost(string root = null)
            => new GameHost(new OpenTransport(), new ListLog(), dataRoot: root ?? Path.Combine(Path.GetTempPath(), "achost-hotkeys-" + Guid.NewGuid().ToString("N")));

        /// <summary>Runs something on the host's game thread and waits for it.</summary>
        private static Task<T> OnGameThread<T>(GameHost host, Func<T> work)
        {
            TaskCompletionSource<T> done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
            return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task EachHotkeyIsBoundToItsSuggestedKeyUnlessThePlayerChoseAnother()
        {
            await using GameHost host = NewHost();
            host.AddPlugin(new HotkeyPlugin("Tank",
                new HotkeyDefinition("Macro", "Run the macro", "Ctrl+F12"),
                new HotkeyDefinition("Loot", "Loot", "")));

            IReadOnlyList<BoundHotkey> hotkeys = host.CollectHotkeys();
            Assert.Equal(new KeyChord(0x7B, ctrl: true), hotkeys[0].Keys);
            Assert.True(hotkeys[1].Keys.IsEmpty);

            host.HotkeyBindings["Tank/Macro"] = new KeyChord('M', alt: true);
            host.HotkeyBindings["tank/loot"] = new KeyChord('L', alt: true);
            hotkeys = host.CollectHotkeys();
            Assert.Equal("Alt+M", hotkeys[0].Keys.ToString());
            Assert.Equal("Alt+L", hotkeys[1].Keys.ToString());
        }

        [Fact]
        public async Task OneKeyNeverTriggersTwoActions()
        {
            ListLog log = new ListLog();
            await using GameHost host = new GameHost(new CaptureTransport(Array.Empty<CapturedDatagram>()), log, dataRoot: Path.Combine(Path.GetTempPath(), "achost-hotkeys-" + Guid.NewGuid().ToString("N")));
            host.AddPlugin(new HotkeyPlugin("First", new HotkeyDefinition("A", "A", "Ctrl+F1")));
            host.AddPlugin(new HotkeyPlugin("Second", new HotkeyDefinition("B", "B", "Ctrl+F1")));

            IReadOnlyList<BoundHotkey> hotkeys = host.CollectHotkeys();
            Assert.Equal("Ctrl+F1", hotkeys[0].Keys.ToString());
            Assert.True(hotkeys[1].Keys.IsEmpty);
            Assert.Single(log.Lines, l => l.Contains("already bound"));

            host.CollectHotkeys();
            Assert.Single(log.Lines, l => l.Contains("already bound"));
        }

        [Fact]
        public async Task AHotkeyPressReachesThePluginThatOfferedIt()
        {
            await using GameHost host = NewLiveHost();
            HotkeyPlugin tank = new HotkeyPlugin("Tank", new HotkeyDefinition("Macro", "Run the macro", "Ctrl+F12"));
            host.AddPlugin(tank);
            await host.StartAsync();

            host.DispatchCommand("Tank", new OverlayCommand("hotkey", "Macro"));
            await OnGameThread(host, () => true);

            Assert.Equal(new[] { "Macro" }, tank.Pressed);
        }

        [Fact]
        public void TheOverlayIsToldEveryBoundKey()
        {
            AC.Host.Overlay.OverlayState state = new AC.Host.Overlay.OverlayState();
            state.Hotkeys.Add(new AC.Host.Overlay.OverlayHotkey { Owner = "Tank", Id = "Macro", Key = 0x7B, Ctrl = true });

            string json = AC.Host.Overlay.OverlayJson.ToJson(state);

            Assert.Contains("\"hotkeys\":[{\"owner\":\"Tank\",\"id\":\"Macro\",\"key\":123,\"ctrl\":true,\"shift\":false,\"alt\":false}]", json);
        }

        /// <summary>A plugin drawn by VVS, as Virindi Tank is, whose hotkeys were VHS's.</summary>
        private sealed class VvsHotkeyPlugin : IPlugin, IOverlayHotkeys, IOverlayView
        {
            private readonly HotkeyDefinition[] _hotkeys;

            public VvsHotkeyPlugin(params HotkeyDefinition[] hotkeys)
            {
                _hotkeys = hotkeys;
                View = DecalView.Parse("<view icon=\"0\" title=\"Tank\" width=\"100\" height=\"50\"><control progid=\"DecalControls.FixedLayout\"/></view>");
                View.Bar = ViewBar.Vvs;
            }

            public string Name => "VirindiTank";

            public DecalView View { get; }

            public List<string> Pressed { get; } = new List<string>();

            public IReadOnlyList<HotkeyDefinition> Hotkeys => _hotkeys;

            public void HotkeyPressed(string id) => Pressed.Add(id);

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }
        }

        [Fact]
        public async Task VirindiTanksHotkeysAreInVhsAndDecalsInDhsAndAKeyIsSetByPressingIt()
        {
            GameHost host = NewLiveHost();
            DecalAgent agent = new DecalAgent(host, null);
            VvsHotkeyPlugin tank = new VvsHotkeyPlugin(new HotkeyDefinition("StartStop", "Starts or stops the macro.", "Pause", "Start/Stop"));
            host.AddPlugin(agent);
            host.AddPlugin(tank);
            await host.StartAsync();

            IReadOnlyList<OverlayViewWindow> windows = await OnGameThread(host, () => agent.Views);
            DecalView dhs = windows.Single(w => w.Key == "dhs").View;
            DecalView vhs = windows.Single(w => w.Key == "vhs").View;

            // Where they sit: DHS on Decal's bar, VHS on VVS's; Decal's own window on neither.
            Assert.Equal(ViewBar.Decal, dhs.Bar);
            Assert.Equal(ViewBar.Vvs, vhs.Bar);
            DecalView own = await OnGameThread(host, () => agent.View);
            Assert.False(own.ShowInBar);
            Assert.True(own.OpensFromBarGrip);

            List keys = vhs.Get<List>("lstKeys");
            Assert.Equal(1, keys.RowCount);
            Assert.Equal(("pause", "VTank", "Start/Stop", "Starts or stops the macro."), (keys[0][2].Text, keys[0][3].Text, keys[0][4].Text, keys[0][5].Text));
            Assert.Equal("Decal: ToggleActing", dhs.Get<List>("lstMain")[0][1].Text);

            // Choose the row, Set, and the overlay is asked for the next key; it sends Ctrl+F12.
            host.DispatchCommand("Decal/vhs", new OverlayCommand("click", "4", rowId: "0", controlId: "lstKeys"));
            host.DispatchCommand("Decal/vhs", new OverlayCommand("press", controlId: "cmdSetKey"));
            Assert.Equal(DecalAgent.PluginName, await OnGameThread(host, () => host.KeyCaptureOwner));
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand(HotkeyWindows.KeyCapturedCommand, "123,1,0,0"));
            Assert.Null(await OnGameThread(host, () => host.KeyCaptureOwner));
            Assert.Equal("Ctrl+F12", (await OnGameThread(host, () => host.CollectHotkeys())).Single(h => h.Owner == "VirindiTank").Keys.ToString());

            // Escape cancels: the key stays.
            host.DispatchCommand("Decal/vhs", new OverlayCommand("press", controlId: "cmdSetKey"));
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand(HotkeyWindows.KeyCapturedCommand, "cancel"));
            Assert.Equal("Ctrl+F12", (await OnGameThread(host, () => host.CollectHotkeys())).Single(h => h.Owner == "VirindiTank").Keys.ToString());

            // The lamp switches it off: its key goes to the game, and a press is not passed on.
            host.DispatchCommand("Decal/vhs", new OverlayCommand("click", "0", rowId: "0", controlId: "lstKeys"));
            Assert.True(await OnGameThread(host, () => host.DisabledHotkeys.Contains("VirindiTank/StartStop")));
            host.DispatchCommand("VirindiTank", new OverlayCommand("hotkey", "StartStop"));
            await OnGameThread(host, () => 0);
            Assert.Empty(tank.Pressed);

            // Reset gives it back its own key.
            host.DispatchCommand("Decal/vhs", new OverlayCommand("press", controlId: "cmdResetKey"));
            Assert.Equal("Pause", (await OnGameThread(host, () => host.CollectHotkeys())).Single(h => h.Owner == "VirindiTank").Keys.ToString());
            await host.DisposeAsync();
        }

        [Fact]
        public async Task AddVvsGivesAVvsWindowAKeyThatShowsAndHidesIt()
        {
            string root = Path.Combine(Path.GetTempPath(), "achost-addvvs-" + Guid.NewGuid().ToString("N"));
            GameHost host = NewLiveHost(root);
            DecalAgent agent = new DecalAgent(host, null);
            VvsHotkeyPlugin tank = new VvsHotkeyPlugin(new HotkeyDefinition("StartStop", "Starts or stops the macro.", "Pause", "Start/Stop"));
            host.AddPlugin(agent);
            host.AddPlugin(tank);
            await host.StartAsync();

            // Add VVS opens VHS's window over the VVS windows on the bar: the tank's and VHS's own.
            host.DispatchCommand("Decal/vhs", new OverlayCommand("press", controlId: "cmdAddVvs"));
            IReadOnlyList<OverlayViewWindow> windows = await OnGameThread(host, () => agent.Views);
            DecalView addVvs = windows.Single(w => w.Key == "addvvs").View;
            Assert.Equal(1, addVvs.OpenRequests);
            Assert.False(addVvs.ShowInBar);
            Assert.False(addVvs.Minimizable);
            List views = addVvs.Get<List>("lstViews");
            Assert.Equal(new[] { "Tank", "Virindi Hotkey System" }, Enumerable.Range(0, views.RowCount).Select(i => views[i][1].Text).OrderBy(t => t));
            int tankRow = Enumerable.Range(0, views.RowCount).Single(i => views[i][1].Text == "Tank");
            Assert.False(views[tankRow][0].Checked);

            // Its lamp adds VVS's "Toggle:Tank" to VHS's list, with no key yet.
            host.DispatchCommand("Decal/addvvs", new OverlayCommand("click", "0", rowId: tankRow.ToString(System.Globalization.CultureInfo.InvariantCulture), controlId: "lstViews"));
            windows = await OnGameThread(host, () => agent.Views);
            Assert.True(views[tankRow][0].Checked);
            List keys = windows.Single(w => w.Key == "vhs").View.Get<List>("lstKeys");
            int toggleRow = Enumerable.Range(0, keys.RowCount).Single(i => keys[i][4].Text == "Toggle:Tank");
            Assert.Equal(("", "VVS", "Toggle window: Tank"), (keys[toggleRow][2].Text, keys[toggleRow][3].Text, keys[toggleRow][5].Text));

            // Its key turns the window over, once for each press.
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("hotkey", "Toggle:Tank"));
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("hotkey", "Toggle:Tank"));
            Assert.Equal(2, await OnGameThread(host, () => tank.View.ToggleRequests));

            // Close closes the window.
            host.DispatchCommand("Decal/addvvs", new OverlayCommand("press", controlId: "cmdClose"));
            Assert.Equal(1, await OnGameThread(host, () => addVvs.CloseRequests));
            await host.DisposeAsync();

            // It is kept, as VHS kept it.
            GameHost next = NewHost(root);
            next.AddPlugin(new DecalAgent(next, null));
            await next.StartAsync();
            await next.Ended.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(next.CollectHotkeys(), h => h.Owner == DecalAgent.PluginName && h.Definition.Id == "Toggle:Tank");
            await next.DisposeAsync();
        }

        [Fact]
        public async Task DecalsWindowBindsAHotkeyAndRemembersIt()
        {
            string root = Path.Combine(Path.GetTempPath(), "achost-hotkeys-" + Guid.NewGuid().ToString("N"));
            GameHost host = NewLiveHost(root);
            DecalAgent agent = new DecalAgent(host, null);
            host.AddPlugin(agent);
            host.AddPlugin(new HotkeyPlugin("Tank", new HotkeyDefinition("Macro", "Run the macro", "Ctrl+F12")));
            await host.StartAsync();

            // As the overlay sends them: pick the second row, type a key, press Set.
            await OnGameThread(host, () => agent.View);
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("click", "0", rowId: "1", controlId: "lstHotkeys"));
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("set", "Alt+M", controlId: "txtHotkeyKeys"));
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("press", controlId: "btnSetHotkey"));
            DecalView view = await OnGameThread(host, () => agent.View);

            Assert.True(view.TryGet("lstHotkeys", out List list));
            Assert.Equal(2, list.RowCount);
            Assert.Equal("Tank", list[1][0].Text);
            Assert.Equal("Alt+M", list[1][2].Text);
            Assert.Equal("Alt+M", host.CollectHotkeys().Single(h => h.Owner == "Tank").Keys.ToString());
            await host.DisposeAsync();

            GameHost next = NewHost(root);
            next.AddPlugin(new DecalAgent(next, null));
            next.AddPlugin(new HotkeyPlugin("Tank", new HotkeyDefinition("Macro", "Run the macro", "Ctrl+F12")));
            await next.StartAsync();
            await next.Ended.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Alt+M", next.CollectHotkeys().Single(h => h.Owner == "Tank").Keys.ToString());
            await next.DisposeAsync();
        }
    }
}
