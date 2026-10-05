using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decal;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using AC.Host.Transport;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Decal's side of the host: the switch that lets plugins act, and the plugin list that
    /// switches plugins on, off and reloads them while the host runs.
    /// </summary>
    public class DecalHostTests
    {
        /// <summary>A live-like transport: it can send, and it never ends by itself.</summary>
        private sealed class LiveTransport : IGameTransport
        {
            public List<AcMessage> Sent { get; } = new List<AcMessage>();

            public string Description => "live test";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public bool CanSend => true;

            public bool CanShowInGame => true;

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default)
            {
                lock (Sent)
                    Sent.Add(message);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                MessageReceived = null;
                Ended?.Invoke(this, EventArgs.Empty);
                Ended = null;
                return ValueTask.CompletedTask;
            }
        }

        private static string NewRoot() => Path.Combine(Path.GetTempPath(), "achost-decal-" + Guid.NewGuid().ToString("N"));

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

        private static Task OnGameThread(GameHost host, Action work)
            => OnGameThread(host, () =>
            {
                work();
                return true;
            });

        // ------------------------------------------------------------------- acting

        [Fact]
        public async Task ActingCanBeSwitchedOffAndOnWhileTheHostRuns()
        {
            LiveTransport transport = new LiveTransport();
            ListLog log = new ListLog();
            await using GameHost host = new GameHost(transport, log, dataRoot: NewRoot());
            await host.StartAsync();

            host.ActionsAllowed = false;
            Assert.False(host.Actions.IsAvailable);
            Assert.False(await host.Actions.UseAsync(0x80000001));
            Assert.Empty(transport.Sent);

            host.ActionsAllowed = true;
            Assert.True(host.Actions.IsAvailable);
            Assert.True(await host.Actions.UseAsync(0x80000001));
            Assert.Single(transport.Sent);

            Assert.Contains(log.Lines, l => l.Contains("Acting is now off"));
            Assert.Contains(log.Lines, l => l.Contains("Acting is now ALLOWED"));
        }

        [Fact]
        public async Task ShowingALineInTheGameIsNotActingAndWorksWhileActingIsOff()
        {
            LiveTransport transport = new LiveTransport();
            await using GameHost host = new GameHost(transport, new ListLog(), dataRoot: NewRoot());
            host.ActionsAllowed = false;

            Assert.True(host.ShowInGame("hello"));
        }

        // ------------------------------------------------------------------- movement keys

        private static (GameInput Input, List<int[]> Sent) NewInput(bool attached = true, bool allowed = true)
        {
            List<int[]> sent = new List<int[]>();
            GameInput input = new GameInput(new ListLog())
            {
                Publish = held => sent.Add(held.OrderBy(k => k).ToArray()),
                Attached = () => attached,
                Allowed = () => allowed,
            };
            return (input, sent);
        }

        [Fact]
        public void HoldingAKeySendsTheWholeSetByVirtualKey()
        {
            (GameInput input, List<int[]> sent) = NewInput();

            Assert.True(input.Hold(GameKey.Forward, true));
            Assert.True(input.Hold(GameKey.TurnLeft, true));
            Assert.True(input.Hold(GameKey.TurnLeft, false));

            Assert.Equal(new[] { new[] { 'W' + 0 }, new[] { 'A' + 0, 'W' + 0 }, new[] { 'W' + 0 } }, sent);
            Assert.Equal(new[] { GameKey.Forward }, input.Held);
        }

        [Fact]
        public void HeldKeysAreRepeatedSoTheOverlayKnowsTheHostIsStillThere()
        {
            (GameInput input, List<int[]> sent) = NewInput();
            input.Hold(GameKey.Forward, true);
            sent.Clear();

            input.Tick(TimeSpan.FromMilliseconds(100));
            input.Tick(TimeSpan.FromMilliseconds(100));
            Assert.Empty(sent);

            input.Tick(TimeSpan.FromMilliseconds(100));
            Assert.Single(sent);

            // Nothing held, nothing repeated.
            input.ReleaseAll();
            sent.Clear();
            input.Tick(TimeSpan.FromSeconds(1));
            Assert.Empty(sent);
        }

        [Fact]
        public void NoKeyIsPressedWhileActingIsOffOrTheOverlayIsAway()
        {
            (GameInput notAllowed, List<int[]> none) = NewInput(allowed: false);
            Assert.False(notAllowed.Hold(GameKey.Forward, true));
            Assert.Empty(none);

            (GameInput detached, List<int[]> nothing) = NewInput(attached: false);
            Assert.False(detached.Hold(GameKey.Forward, true));
            Assert.Empty(nothing);
        }

        [Fact]
        public void SwitchingActingOffLetsGoOfEverythingOnTheNextTick()
        {
            bool allowed = true;
            List<int[]> sent = new List<int[]>();
            GameInput input = new GameInput(new ListLog()) { Publish = held => sent.Add(held.ToArray()), Attached = () => true, Allowed = () => allowed };

            input.Hold(GameKey.Forward, true);
            allowed = false;
            input.Tick(TimeSpan.FromMilliseconds(100));

            Assert.Empty(input.Held);
            Assert.Empty(sent[^1]);
        }

        [Fact]
        public void KeysCanBeReboundFromSettings()
        {
            GameInput input = new GameInput(new ListLog(), new Dictionary<string, string>
            {
                ["Decal:Key.Forward"] = "i",
                ["Decal:Key.Jump"] = "0x20",
                ["Decal:Key.TurnLeft"] = "37",
                ["Decal:Key.TurnRight"] = "nonsense",
            });

            Assert.Equal('I', input.VirtualKey(GameKey.Forward));
            Assert.Equal(0x20, input.VirtualKey(GameKey.Jump));
            Assert.Equal(37, input.VirtualKey(GameKey.TurnLeft));
            Assert.Equal('D', input.VirtualKey(GameKey.TurnRight));
        }

        [Fact]
        public async Task AHostWithoutAnOverlayCannotHoldKeys()
        {
            await using GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: NewRoot());

            Assert.False(((IHost)host).Input.IsAvailable);
            Assert.False(((IHost)host).Input.Hold(GameKey.Forward, true));
        }

        // ------------------------------------------------------------------- the plugin list

        /// <summary>Lays out Counting.Plugin in a plugin folder of its own, as the host expects to find it.</summary>
        private static string StageCountingPlugin(string root)
        {
            string plugins = Path.Combine(root, "plugins");
            string folder = Path.Combine(plugins, "Counting");
            Directory.CreateDirectory(folder);

            string source = Path.Combine(AppContext.BaseDirectory, "Counting.Plugin.dll");
            Assert.True(File.Exists(source), "Counting.Plugin.dll is not beside the tests. It should arrive as a project reference.");
            File.Copy(source, Path.Combine(folder, "Counting.Plugin.dll"));

            // A dependency beside it that is not a plugin, as a real plugin folder has.
            File.Copy(Path.Combine(AppContext.BaseDirectory, "AC.Protocol.dll"), Path.Combine(folder, "AC.Protocol.dll"));
            return plugins;
        }

        private static int Read(IPlugin plugin, string property)
            => (int)plugin.GetType().GetProperty(property).GetValue(plugin);

        [Fact]
        public async Task InstalledPluginsAreFoundAndOnlyRealOnesListed()
        {
            string root = NewRoot();
            string plugins = StageCountingPlugin(root);
            await using GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: root);
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), root);

            manager.LoadAll();

            PluginEntry entry = Assert.Single(manager.Entries);
            Assert.Equal("Counting", entry.Name);
            Assert.Equal("running", entry.Status);
            Assert.True(entry.Enabled);
            Assert.Contains(host.Plugins, p => p.Name == "Counting");

            // It runs from a copy, so the installed file is free to be rebuilt.
            Assert.NotEqual(Path.GetFullPath(entry.AssemblyPath), Path.GetFullPath(entry.Plugin.GetType().Assembly.Location));
            using (File.Open(entry.AssemblyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }

        [Fact]
        public async Task APluginSwitchedOffIsShutDownCutLooseAndStaysOffNextTime()
        {
            string root = NewRoot();
            string plugins = StageCountingPlugin(root);
            ListLog log = new ListLog();
            GameHost host = new GameHost(new LiveTransport(), log, dataRoot: root);
            PluginManager manager = new PluginManager(host, plugins, log, root);
            manager.LoadAll();
            await host.StartAsync();

            PluginEntry entry = manager.Entries[0];
            IPlugin plugin = entry.Plugin;

            await OnGameThread(host, () => manager.SetEnabled(entry, false));

            Assert.False(entry.IsRunning);
            Assert.Equal("off", entry.Status);
            Assert.DoesNotContain(host.Plugins, p => p.Name == "Counting");
            Assert.Equal(1, Read(plugin, "Shutdowns"));

            // It never unsubscribed from the tick; the host did it for it.
            Assert.Contains(log.Lines, l => l.Contains("left 1 event handler"));
            int ticks = Read(plugin, "Ticks");
            await Task.Delay(350);
            Assert.Equal(ticks, Read(plugin, "Ticks"));

            await host.DisposeAsync();

            // Remembered: a fresh host lists it, off, without starting it.
            await using GameHost next = new GameHost(new LiveTransport(), new ListLog(), dataRoot: root);
            PluginManager again = new PluginManager(next, plugins, new ListLog(), root);
            again.LoadAll();

            PluginEntry listed = Assert.Single(again.Entries);
            Assert.Equal("Counting", listed.Name);
            Assert.False(listed.Enabled);
            Assert.Equal("off", listed.Status);
            Assert.DoesNotContain(next.Plugins, p => p.Name == "Counting");
        }

        [Fact]
        public async Task SwitchingOnAgainStartsAFreshCopy()
        {
            string root = NewRoot();
            string plugins = StageCountingPlugin(root);
            await using GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: root);
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), root);
            manager.LoadAll();
            await host.StartAsync();

            PluginEntry entry = manager.Entries[0];
            await OnGameThread(host, () => manager.SetEnabled(entry, false));
            await OnGameThread(host, () => manager.SetEnabled(entry, true));

            Assert.True(entry.IsRunning);
            Assert.Equal(1, Read(entry.Plugin, "Startups"));
            Assert.Single(host.Plugins, p => p.Name == "Counting");
        }

        [Fact]
        public async Task ReloadingSwapsTheRunningPluginForANewLoadOfTheFile()
        {
            string root = NewRoot();
            string plugins = StageCountingPlugin(root);
            await using GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: root);
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), root);
            manager.LoadAll();
            await host.StartAsync();

            PluginEntry entry = manager.Entries[0];
            IPlugin before = entry.Plugin;
            Assembly beforeAssembly = before.GetType().Assembly;

            await OnGameThread(host, () => manager.Reload(entry));

            Assert.True(entry.IsRunning);
            Assert.NotSame(before, entry.Plugin);
            Assert.NotSame(beforeAssembly, entry.Plugin.GetType().Assembly);
            Assert.Equal(1, Read(before, "Shutdowns"));
            Assert.Single(host.Plugins, p => p.Name == "Counting");
        }

        [Fact]
        public async Task AddingAndRemovingPluginsIsForTheGameThread()
        {
            await using GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: NewRoot());
            await host.StartAsync();

            Assert.Throws<InvalidOperationException>(() => host.Attach(new RecordingPlugin()));
        }

        // ------------------------------------------------------------------- the window

        [Fact]
        public void DecalsWindowIsADecalViewWithThePluginListAndTheActingSwitch()
        {
            DecalView view = DecalAgent.LoadView();

            Assert.Equal("Decal", view.Title);
            Assert.Equal(new[] { "Plugins", "Options", "Hotkeys", "About" }, view.Notebook("tabs").Pages.Select(p => p.Label));
            Assert.True(view.TryGet("lstPlugins", out List list));
            Assert.Equal(5, list.Columns.Count);
            Assert.True(view.TryGet("chkAct", out Checkbox _));
            Assert.Empty(view.Warnings);
        }

        [Fact]
        public async Task TheWindowListsPluginsAndItsLampsSwitchThem()
        {
            string root = NewRoot();
            string plugins = StageCountingPlugin(root);
            GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: root);
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), root);
            DecalAgent agent = new DecalAgent(host, manager);
            host.AddPlugin(agent);
            manager.LoadAll();
            await host.StartAsync();

            DecalView view = await OnGameThread(host, () => agent.View);
            Assert.True(view.TryGet("lstPlugins", out List list));
            Assert.Equal(1, list.RowCount);
            Assert.Equal("Counting", list[0][1].Text);
            Assert.True(list[0][0].Checked);
            Assert.Equal("running", list[0][3].Text);

            // A click on the lamp, as the overlay sends it.
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("click", "0", rowId: "0", controlId: "lstPlugins"));
            await OnGameThread(host, () => true);

            Assert.False(manager.Entries[0].IsRunning);
            view = await OnGameThread(host, () => agent.View);
            Assert.Equal("off", list[0][3].Text);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task MovementKeysReboundInTheWindowAreUsedAndRemembered()
        {
            string root = NewRoot();
            GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: root);
            DecalAgent agent = new DecalAgent(host, null);
            host.AddPlugin(agent);
            await host.StartAsync();

            DecalView view = await OnGameThread(host, () => agent.View);
            Assert.True(view.TryGet("txtKeyForward", out Edit forward));
            Assert.Equal("W", forward.Text);

            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("set", "i", controlId: "txtKeyForward"));
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("set", "nonsense", controlId: "txtKeyTurnLeft"));
            await OnGameThread(host, () => true);

            Assert.Equal('I', host.InputKeys.VirtualKey(GameKey.Forward));
            Assert.Equal("I", forward.Text);
            Assert.Equal('A', host.InputKeys.VirtualKey(GameKey.TurnLeft));
            Assert.True(view.TryGet("txtKeyTurnLeft", out Edit left));
            Assert.Equal("A", left.Text);
            await host.DisposeAsync();

            // The next session binds it again before anything moves.
            GameHost next = new GameHost(new LiveTransport(), new ListLog(), dataRoot: root);
            next.AddPlugin(new DecalAgent(next, null));
            await next.StartAsync();
            Assert.Equal('I', next.InputKeys.VirtualKey(GameKey.Forward));
            await next.DisposeAsync();
        }

        [Fact]
        public async Task TheWalkTestHoldsForwardForASecondAndSaysHowFarTheCharacterWent()
        {
            GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: NewRoot());
            List<int[]> sent = new List<int[]>();
            host.InputKeys.Attached = () => true;
            host.InputKeys.Publish = held => { lock (sent) sent.Add(held.ToArray()); };
            DecalAgent agent = new DecalAgent(host, null);
            host.AddPlugin(agent);
            await host.StartAsync();

            await OnGameThread(host, () => host.WorldState.SetClientPosition(MacroTestHost.At(0x2B110020, 100, 100), default));
            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("press", controlId: "btnTestWalk"));
            await OnGameThread(host, () => true);
            Assert.Contains(GameKey.Forward, host.InputKeys.Held);

            // The client walks, and says so.
            await OnGameThread(host, () => host.WorldState.SetClientPosition(MacroTestHost.At(0x2B110020, 100, 104.5f), default));
            await Task.Delay(2200);

            DecalView view = await OnGameThread(host, () => agent.View);
            Assert.True(view.TryGet("lblTestWalk", out StaticText said));
            Assert.Equal("Moved 4.5 m forward: the keys work.", said.Text);
            Assert.Empty(host.InputKeys.Held);
            lock (sent)
                Assert.Empty(sent[^1]);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task TheWalkTestSaysWhyItCannotRun()
        {
            GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: NewRoot());
            DecalAgent agent = new DecalAgent(host, null);
            host.AddPlugin(agent);
            await host.StartAsync();

            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("press", controlId: "btnTestWalk"));
            DecalView view = await OnGameThread(host, () => agent.View);
            Assert.True(view.TryGet("lblTestWalk", out StaticText said));
            Assert.Equal("The overlay is not attached.", said.Text);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task TheActingSwitchInTheWindowThrowsTheHostsSwitch()
        {
            GameHost host = new GameHost(new LiveTransport(), new ListLog(), dataRoot: NewRoot());
            DecalAgent agent = new DecalAgent(host, null);
            host.AddPlugin(agent);
            host.ActionsAllowed = false;
            await host.StartAsync();

            DecalView view = await OnGameThread(host, () => agent.View);
            Assert.True(view.TryGet("chkAct", out Checkbox act));
            Assert.False(act.Checked);

            host.DispatchCommand(DecalAgent.PluginName, new OverlayCommand("set", "true", controlId: "chkAct"));
            await OnGameThread(host, () => true);

            Assert.True(host.ActionsAllowed);
            await host.DisposeAsync();
        }
    }
}
