using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using AC.Proxy;
using Xunit;

namespace AC.Host.Tests
{
    internal sealed class ListLog : IPluginLog
    {
        public List<string> Lines { get; } = new List<string>();

        public void Info(string message) { lock (Lines) Lines.Add("INFO " + message); }

        public void Warn(string message) { lock (Lines) Lines.Add("WARN " + message); }

        public void Error(string message, Exception exception = null)
        {
            lock (Lines) Lines.Add("ERROR " + message + (exception != null ? " :: " + exception.Message : string.Empty));
        }
    }

    /// <summary>Records what the host tells it, and the thread it was told on.</summary>
    internal sealed class RecordingPlugin : IPlugin
    {
        private readonly string _name;

        public RecordingPlugin(string name = "Recorder")
        {
            _name = name;
        }

        public string Name => _name;

        public List<string> Events { get; } = new List<string>();

        public HashSet<int> ThreadIds { get; } = new HashSet<int>();

        public bool ThrowOnCreate { get; set; }

        public void Startup(IHost host)
        {
            Note("startup");
            host.ObjectCreated += (_, o) =>
            {
                Note("created " + o.Name);
                if (ThrowOnCreate) throw new InvalidOperationException("plugin bug");
            };
            host.ObjectAppraised += (_, o) => Note("appraised " + o.Name);
            host.ChatReceived += (_, c) => Note("chat " + c.Text);
            host.PlayerIdentified += (_, id) => Note($"player {id:X8}");
        }

        public void Shutdown() => Note("shutdown");

        private void Note(string what)
        {
            Events.Add(what);
            ThreadIds.Add(Environment.CurrentManagedThreadId);
        }
    }

    internal static class Capture
    {
        private static uint _sequence = 1;

        public static CapturedDatagram Inbound(WireWriter w)
        {
            byte[] bytes = w.ToArray();
            uint opcode = BitConverter.ToUInt32(bytes, 0);
            uint seq = _sequence++;

            byte[] datagram = PacketWriter.Build(
                new PacketHeader { Sequence = seq, Flags = PacketHeaderFlags.BlobFragments },
                ReadOnlySpan<byte>.Empty,
                PacketWriter.Fragment(opcode, bytes.AsSpan(4), seq));

            return new CapturedDatagram(DateTimeOffset.UtcNow, PacketDirection.Inbound, 9100, datagram);
        }
    }

    public class HostTests
    {
        private const uint Player = 0x50000001;
        private const uint Cap = 0x80000011;

        private static async Task<(GameHost host, ListLog log)> RunAsync(IEnumerable<CapturedDatagram> datagrams, params IPlugin[] plugins)
        {
            ListLog log = new ListLog();
            GameHost host = new GameHost(new CaptureTransport(datagrams), log, dataRoot: Path.Combine(Path.GetTempPath(), "achost-tests"));

            foreach (IPlugin plugin in plugins)
                host.AddPlugin(plugin);

            await host.StartAsync();
            await host.Ended.WaitAsync(TimeSpan.FromSeconds(10));
            return (host, log);
        }

        [Fact]
        public async Task MessagesFlowThroughToPluginsInOrderOnOneThread()
        {
            RecordingPlugin plugin = new RecordingPlugin();

            (GameHost host, _) = await RunAsync(new[]
            {
                Capture.Inbound(new WireWriter(Opcodes.PlayerCreate).U32(Player)),
                Capture.Inbound(WireWriter.ObjectCreate(Cap, "Leather Cap", 1, ItemTypes.Armor, 0)),
                Capture.Inbound(new WireWriter(Opcodes.HearSpeech).String16L("hi").String16L("Bob").U32(2).U32(2)),
            }, plugin);

            await host.DisposeAsync();

            Assert.Equal(new[] { "startup", "player 50000001", "created Leather Cap", "chat hi", "shutdown" }, plugin.Events);
            Assert.Single(plugin.ThreadIds);
            Assert.Equal(3, host.Statistics.Applied);
            Assert.Equal(0, host.Statistics.PluginExceptions);
        }

        /// <summary>
        /// The tick timer queues work from a thread-pool thread, so a throw here does
        /// not fail a test - it ends the process, and takes the rest of the run with it.
        /// This was happening: runs reported fewer tests than were discovered.
        /// </summary>
        [Fact]
        public async Task WorkQueuedAfterShutdownIsDroppedRatherThanThrown()
        {
            (GameHost host, _) = await RunAsync(new[]
            {
                Capture.Inbound(WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0)),
            });

            await host.DisposeAsync();

            bool ran = false;
            host.RunOnGameThread(() => ran = true);

            Assert.False(ran);
        }

        [Fact]
        public async Task DisposingTwiceIsHarmless()
        {
            (GameHost host, _) = await RunAsync(System.Array.Empty<CapturedDatagram>());

            await host.DisposeAsync();
            await host.DisposeAsync();
        }

        [Fact]
        public async Task AThrowingPluginIsLoggedAndDoesNotStopTheOthers()
        {
            RecordingPlugin bad = new RecordingPlugin("Bad") { ThrowOnCreate = true };
            RecordingPlugin good = new RecordingPlugin("Good");

            (GameHost host, ListLog log) = await RunAsync(new[]
            {
                Capture.Inbound(WireWriter.ObjectCreate(Cap, "Cap", 1, ItemTypes.Armor, 0)),
                Capture.Inbound(WireWriter.ObjectCreate(Cap + 1, "Hat", 1, ItemTypes.Armor, 0)),
            }, bad, good);

            await host.DisposeAsync();

            Assert.Contains("created Cap", good.Events);
            Assert.Contains("created Hat", good.Events);
            Assert.Contains("created Cap", bad.Events);
            Assert.Equal(2, host.Statistics.PluginExceptions);
            Assert.Contains(log.Lines, l => l.StartsWith("ERROR") && l.Contains("plugin bug"));
        }

        [Fact]
        public async Task SettingsAndDataDirectoriesArePerPlugin()
        {
            RecordingPlugin plugin = new RecordingPlugin("Recorder");
            string root = Path.Combine(Path.GetTempPath(), "achost-tests-" + Guid.NewGuid().ToString("N"));

            await using GameHost host = new GameHost(
                new CaptureTransport(Array.Empty<CapturedDatagram>()),
                new ListLog(),
                new Dictionary<string, string> { ["Recorder:Profile"] = @"C:\x.utl", ["Other:Profile"] = "no" },
                root);

            host.AddPlugin(plugin);
            await host.StartAsync();
            await host.Ended.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(@"C:\x.utl", host.GetSetting(plugin, "Profile"));
            Assert.Null(host.GetSetting(plugin, "Missing"));

            string dir = host.GetDataDirectory(plugin);
            Assert.True(Directory.Exists(dir));
            Assert.EndsWith(Path.Combine("plugins", "Recorder"), dir);

            Directory.Delete(root, recursive: true);
        }

        [Fact]
        public async Task ActionsReportUnavailableRatherThanThrowing()
        {
            await using GameHost host = new GameHost(new CaptureTransport(Array.Empty<CapturedDatagram>()), new ListLog());

            Assert.False(host.Actions.IsAvailable);
            Assert.False(await host.Actions.AppraiseAsync(1));
        }

        [Fact]
        public async Task UnknownInboundOpcodesAreCountedForTheReport()
        {
            (GameHost host, _) = await RunAsync(new[]
            {
                Capture.Inbound(new WireWriter(0xF7E5).U32(1).U32(2)),
                Capture.Inbound(new WireWriter(0xF7E5).U32(1).U32(2)),
            });

            await host.DisposeAsync();

            Assert.Equal(2, host.Statistics.Ignored);
            Assert.Equal(2, host.Statistics.IgnoredOpcodes[0xF7E5]);
        }
    }

    public class PluginLoaderTests
    {
        [Fact]
        public void AMissingDirectoryYieldsNothing()
        {
            Assert.Empty(PluginLoader.LoadFrom(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")), new ListLog()));
        }
    }
}
