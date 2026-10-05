using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Proxy;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Clicks coming back from the overlay. What matters is routing and isolation: a
    /// command reaches the plugin whose window it came from and no other, a plugin that
    /// throws costs one click rather than the session, and a click nobody answers is
    /// counted rather than lost - because a click that silently does nothing is the
    /// hardest kind of bug to notice.
    /// </summary>
    public class OverlayCommandTests
    {
        private sealed class ListeningPlugin : IPlugin, IOverlayCommands
        {
            private readonly Func<OverlayCommand, bool> _handle;

            public ListeningPlugin(string name, Func<OverlayCommand, bool> handle)
            {
                Name = name;
                _handle = handle;
            }

            public string Name { get; }

            public List<OverlayCommand> Received { get; } = new List<OverlayCommand>();

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }

            public bool HandleCommand(OverlayCommand command)
            {
                Received.Add(command);
                return _handle(command);
            }
        }

        private sealed class DeafPlugin : IPlugin
        {
            public string Name => "Deaf";

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }
        }

        /// <summary>
        /// Stays open until disposed. A capture transport ends the moment it is empty,
        /// and a host that has ended drops anything queued for its game thread - which is
        /// correct, and which meant a command dispatched after the wait was never
        /// delivered and every test here timed out at exactly its ten-second limit.
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

        /// <summary>
        /// Dispatch queues onto the game thread; a dispose drains that queue, which is the
        /// simplest way to be sure the command has been delivered before asserting.
        /// </summary>
        private static async Task SettleAsync(GameHost host)
        {
            TaskCompletionSource done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() => done.SetResult());
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ACommandReachesThePluginWhoseWindowItCameFromAndNoOther()
        {
            ListeningPlugin tank = new ListeningPlugin("VirindiTank", _ => true);
            ListeningPlugin other = new ListeningPlugin("Other", _ => true);

            (GameHost host, _) = await RunAsync(tank, other);

            host.DispatchCommand("VirindiTank", new OverlayCommand("toggle-looting"));
            await SettleAsync(host);

            OverlayCommand received = Assert.Single(tank.Received);
            Assert.Equal("toggle-looting", received.Name);
            Assert.Empty(other.Received);
            Assert.Equal(1, host.Statistics.CommandsHandled);
            Assert.Equal(0, host.Statistics.CommandsUnhandled);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task TheOwnerIsMatchedWithoutRegardToCase()
        {
            ListeningPlugin tank = new ListeningPlugin("VirindiTank", _ => true);
            (GameHost host, _) = await RunAsync(tank);

            host.DispatchCommand("virinditank", new OverlayCommand("reload-profile"));
            await SettleAsync(host);

            Assert.Single(tank.Received);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task TheValueAndRowTravelWithTheCommand()
        {
            ListeningPlugin tank = new ListeningPlugin("VirindiTank", _ => true);
            (GameHost host, _) = await RunAsync(tank);

            host.DispatchCommand("VirindiTank", new OverlayCommand("appraise", "now", "0x80001234"));
            await SettleAsync(host);

            OverlayCommand received = Assert.Single(tank.Received);
            Assert.Equal("now", received.Value);
            Assert.Equal("0x80001234", received.RowId);

            await host.DisposeAsync();
        }

        /// <summary>
        /// The control id is the whole of how a plugin tells its switches apart: every
        /// toggle sends "set", so a dispatcher that dropped the id would deliver a click the
        /// plugin could only guess at.
        /// </summary>
        [Fact]
        public async Task TheControlIdTravelsWithTheCommand()
        {
            ListeningPlugin tank = new ListeningPlugin("VirindiTank", _ => true);
            (GameHost host, _) = await RunAsync(tank);

            host.DispatchCommand("VirindiTank", new OverlayCommand("set", "false", controlId: "echo"));
            await SettleAsync(host);

            OverlayCommand received = Assert.Single(tank.Received);
            Assert.Equal("set", received.Name);
            Assert.Equal("false", received.Value);
            Assert.Equal("echo", received.ControlId);
            Assert.Equal(string.Empty, received.RowId);

            await host.DisposeAsync();
        }

        [Fact]
        public async Task ACommandForNoLoadedPluginIsCountedAndSaidNotLost()
        {
            (GameHost host, ListLog log) = await RunAsync(new ListeningPlugin("VirindiTank", _ => true));

            host.DispatchCommand("Nobody", new OverlayCommand("anything"));
            await SettleAsync(host);

            Assert.Equal(0, host.Statistics.CommandsHandled);
            Assert.Equal(1, host.Statistics.CommandsUnhandled);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN") && l.Contains("Nobody"));

            await host.DisposeAsync();
        }

        [Fact]
        public async Task APluginThatShowsPanelsButTakesNoCommandsIsSaidSo()
        {
            (GameHost host, ListLog log) = await RunAsync(new DeafPlugin());

            host.DispatchCommand("Deaf", new OverlayCommand("toggle-looting"));
            await SettleAsync(host);

            Assert.Equal(1, host.Statistics.CommandsUnhandled);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN") && l.Contains("does not take commands"));

            await host.DisposeAsync();
        }

        [Fact]
        public async Task ACommandThePluginDoesNotRecogniseIsCountedAgainstIt()
        {
            ListeningPlugin tank = new ListeningPlugin("VirindiTank", _ => false);
            (GameHost host, ListLog log) = await RunAsync(tank);

            host.DispatchCommand("VirindiTank", new OverlayCommand("no-such-thing"));
            await SettleAsync(host);

            Assert.Single(tank.Received);
            Assert.Equal(0, host.Statistics.CommandsHandled);
            Assert.Equal(1, host.Statistics.CommandsUnhandled);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN") && l.Contains("did not recognise") && l.Contains("no-such-thing"));

            await host.DisposeAsync();
        }

        /// <summary>
        /// The same rule as every other callback: one bad handler costs one click, not the
        /// session, and the next click still arrives.
        /// </summary>
        [Fact]
        public async Task APluginThatThrowsOnAClickIsIsolatedAndStillGetsTheNext()
        {
            int calls = 0;
            ListeningPlugin flaky = new ListeningPlugin("Flaky", _ => ++calls == 1 ? throw new InvalidOperationException("no") : true);

            (GameHost host, ListLog log) = await RunAsync(flaky);

            host.DispatchCommand("Flaky", new OverlayCommand("first"));
            host.DispatchCommand("Flaky", new OverlayCommand("second"));
            await SettleAsync(host);

            Assert.Equal(2, flaky.Received.Count);
            Assert.Equal(1, host.Statistics.PluginExceptions);
            Assert.Equal(1, host.Statistics.CommandsHandled);
            Assert.Equal(1, host.Statistics.CommandsUnhandled);
            Assert.Contains(log.Lines, l => l.StartsWith("ERROR") && l.Contains("threw handling"));

            await host.DisposeAsync();
        }

        [Fact]
        public async Task ACommandAddressedToTheHostItselfIsReportedRatherThanSwallowed()
        {
            (GameHost host, ListLog log) = await RunAsync(new ListeningPlugin("VirindiTank", _ => true));

            host.DispatchCommand(string.Empty, new OverlayCommand("toggle-acting"));
            await SettleAsync(host);

            Assert.Equal(1, host.Statistics.CommandsUnhandled);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN") && l.Contains("host itself"));

            await host.DisposeAsync();
        }

        [Fact]
        public void ACommandReadsAsASentence()
        {
            Assert.Equal("toggle-looting", new OverlayCommand("toggle-looting").ToString());
            Assert.Equal("appraise (now) on 0x80001234", new OverlayCommand("appraise", "now", "0x80001234").ToString());
            Assert.Equal(string.Empty, new OverlayCommand(null).Name);
        }
    }
}
