using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Runtime;
using AC.Host.Transport;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// The whole host as achost and the Decal Agent both start it: plugins found and started,
    /// the control pipe on the name it was given, the relay on its ports, and all of it let go
    /// of again.
    /// </summary>
    /// <remarks>
    /// Every name and port here is made up for the test. A player may be logged in through a
    /// host on the real ones while these run, and a test that took them would disconnect them.
    /// </remarks>
    public class HostRuntimeTests
    {
        /// <summary>A transport that behaves as a live one - it can send - and binds nothing.</summary>
        private sealed class QuietTransport : IGameTransport
        {
            public string Description => "quiet test transport";

            public event EventHandler<GameMessageEventArgs> MessageReceived { add { } remove { } }

            public event EventHandler Ended;

            public bool CanSend => true;

            public bool CanShowInGame => false;

            public bool Disposed { get; private set; }

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                Ended?.Invoke(this, EventArgs.Empty);
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// A relay stand-in the test drives: messages from the server, the client closing the
        /// session, and how many answers it kept from the client.
        /// </summary>
        private sealed class SessionTransport : IGameTransport, ISessionBoundaries, IClientboundFilter
        {
            public string Description => "session test transport";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public event EventHandler<SessionEnd> SessionEnded;

            public bool CanSend => true;

            public bool CanShowInGame => true;

            public Func<uint, ReadOnlyMemory<byte>, bool> WithholdFromClient { get; set; }

            public bool CanWithholdFromClient => true;

            public int MessagesWithheldFromClient { get; set; }

            public int SplitMessagesWithheldFromClient { get; set; }

            public void FromServer(WireWriter message)
                => MessageReceived?.Invoke(this, new GameMessageEventArgs(PacketDirection.Inbound, message.ToMessage()));

            public void ClientCloses() => SessionEnded?.Invoke(this, SessionEnd.ClientClosed);

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                Ended?.Invoke(this, EventArgs.Empty);
                return ValueTask.CompletedTask;
            }
        }

        private static string NewRoot() => Path.Combine(Path.GetTempPath(), "achost-runtime-" + Guid.NewGuid().ToString("N"));

        private static string NewPipeName(string what) => "achost-test-" + what + "-" + Guid.NewGuid().ToString("N");

        /// <summary>Counting.Plugin in a plugin folder of its own, as an installed plugin is.</summary>
        private static string StageCountingPlugin(string root)
        {
            string plugins = Path.Combine(root, "plugins");
            string folder = Path.Combine(plugins, "Counting");
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Counting.Plugin.dll"), Path.Combine(folder, "Counting.Plugin.dll"));
            return plugins;
        }

        /// <summary>Options for a host that touches nothing a real one uses.</summary>
        private static HostRuntimeOptions Isolated(string root, IGameTransport transport = null)
        {
            HostRuntimeOptions options = new HostRuntimeOptions
            {
                Transport = transport,
                PluginDirectory = Path.Combine(root, "plugins"),
                DataDirectory = Path.Combine(root, "data"),
                NoDat = true,
                ControlPipeName = NewPipeName("control"),
                OverlayPipeName = NewPipeName("overlay"),
            };

            return options;
        }

        private static int Read(IPlugin plugin, string property)
            => (int)plugin.GetType().GetProperty(property).GetValue(plugin);

        [Fact]
        public async Task ItFindsAndStartsTheInstalledPluginsBehindDecalsWindowAndShutsThemDown()
        {
            string root = NewRoot();
            StageCountingPlugin(root);
            QuietTransport transport = new QuietTransport();
            HostRuntime runtime = new HostRuntime(Isolated(root, transport), new ListLog());

            // Assembled but not started: the plugins are found, and nothing has run yet.
            PluginEntry counting = Assert.Single(runtime.Plugins.Entries);
            Assert.Equal("Counting", counting.Name);
            Assert.Equal(new[] { "Decal", "Counting" }, runtime.Host.Plugins.Select(p => p.Name));
            Assert.Same(runtime.Decal, runtime.Host.Plugins[0]);
            Assert.Equal(0, Read(counting.Plugin, "Startups"));

            await runtime.StartAsync();
            Assert.Equal(1, Read(counting.Plugin, "Startups"));
            Assert.True(counting.IsRunning);

            IPlugin plugin = counting.Plugin;
            await runtime.DisposeAsync();

            Assert.Equal(1, Read(plugin, "Shutdowns"));
            Assert.True(transport.Disposed);

            // Twice is harmless.
            await runtime.DisposeAsync();
        }

        [Fact]
        public async Task TheControlPipeAnswersOnTheNameItWasGiven()
        {
            string root = NewRoot();
            StageCountingPlugin(root);
            HostRuntimeOptions options = Isolated(root, new QuietTransport());
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            Assert.Equal(options.ControlPipeName, runtime.Control.Name);

            string status = await ControlPipe.SendAsync("status", options.ControlPipeName);
            Assert.Contains("acting     off", status);
            Assert.Contains("Counting", status);

            // And it drives the host it belongs to.
            string acting = await ControlPipe.SendAsync("act on", options.ControlPipeName);
            Assert.Equal("acting allowed", acting.Trim());
            Assert.True(runtime.Host.ActionsAllowed);
        }

        /// <summary>
        /// What `achost ctl status` tells whoever is watching a live session: who is in the world -
        /// and, once the client has closed the session, nobody, with the log saying who left and
        /// why - and how many of the host's own appraisals were kept from the client.
        /// </summary>
        [Fact]
        public async Task StatusSaysWhoIsInTheWorldUntilTheyLeaveAndCountsTheAnswersKeptFromTheClient()
        {
            SessionTransport transport = new SessionTransport { MessagesWithheldFromClient = 3, SplitMessagesWithheldFromClient = 1 };
            HostRuntimeOptions options = Isolated(NewRoot(), transport);
            ListLog log = new ListLog();
            await using HostRuntime runtime = new HostRuntime(options, log);
            await runtime.StartAsync();

            transport.FromServer(new WireWriter(Opcodes.ServerName).I32(1).I32(128).String16L("Example Server"));
            transport.FromServer(new WireWriter(Opcodes.PlayerCreate).U32(0x50000006));
            transport.FromServer(WireWriter.ObjectCreate(0x50000006, "Testchar I", 1, ItemTypes.Creature, 0));

            string before = await ControlPipe.SendAsync("status", options.ControlPipeName);
            Assert.Contains("server     Example Server", before);
            Assert.Contains("character  Testchar I", before);
            Assert.Contains("appraisals 3 withheld from the client (1 in several fragments)", before);

            transport.ClientCloses();

            string after = await ControlPipe.SendAsync("status", options.ControlPipeName);
            Assert.Contains("server     (not connected)", after);
            Assert.Contains("character  (not logged in)", after);
            lock (log.Lines)
                Assert.Contains("INFO Testchar I (0x50000006) has left the world: the client closed the session.", log.Lines);
        }

        /// <summary>
        /// `ctl status` says which of AC:Unreal's own client plugins are enabled - UCM can play the
        /// character, and whether it is running the client does not say - and its Desktop UI Scale,
        /// read from the client's Saved folder: here a made-up one of the client's shape.
        /// </summary>
        [Fact]
        public async Task StatusSaysWhichClientPluginsAreEnabledAndTheClientsUiScale()
        {
            string root = NewRoot();
            string saved = Path.Combine(root, "Saved");
            Directory.CreateDirectory(Path.Combine(saved, "ClientPlugins"));
            Directory.CreateDirectory(Path.Combine(saved, "Config", "Windows"));
            File.WriteAllText(ClientSettingsWatcher.PluginSettingsPath(saved), "{ \"ucm\": \"enabled:cast,combat\", \"waypoint\": \"disabled\" }");
            File.WriteAllText(ClientSettingsWatcher.GameUserSettingsPath(saved), "[ACE.Presentation]\r\nDesktopUIScale=1.25\r\n");

            HostRuntimeOptions options = Isolated(root, new QuietTransport());
            options.ClientSettingsFolder = saved;
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            string status = string.Empty;
            for (int i = 0; i < 50 && !status.Contains("ui scale"); i++)
            {
                status = await ControlPipe.SendAsync("status", options.ControlPipeName);
                if (!status.Contains("ui scale"))
                    await Task.Delay(100);
            }

            Assert.Contains("ac plugins enabled: UCM (cast, combat) - UCM can play the character: enabled, though whether it is running the client does not say", status);
            Assert.Contains("ui scale   125%, the client's Desktop UI Scale; its plugin bar at 8,80 62x214 (where the client starts it)", status);
            Assert.True(runtime.Host.ClientPlugins.UcmEnabled);
            Assert.Equal(1.25, runtime.Host.ClientUiScale);
        }

        /// <summary>A host that is not relaying a live game reads no client's settings: status says they are not known.</summary>
        [Fact]
        public async Task AHostNotRelayingAGameReadsNoClientSettings()
        {
            HostRuntimeOptions options = Isolated(NewRoot(), new QuietTransport());
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            Assert.Null(runtime.ClientSettings);
            string status = await ControlPipe.SendAsync("status", options.ControlPipeName);
            Assert.Contains("ac plugins not known (the client's settings were not read)", status);
            Assert.DoesNotContain("ui scale", status);
        }

        /// <summary>
        /// A window a plugin hosts is reached by its overlay name, "Plugin/key", as the Decal
        /// Agent's hotkey windows are: `windows` lists them, and view and press work on them.
        /// </summary>
        [Fact]
        public async Task TheControlPipeReachesTheWindowsAPluginHosts()
        {
            HostRuntimeOptions options = Isolated(NewRoot(), new QuietTransport());
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            string windows = await ControlPipe.SendAsync("windows", options.ControlPipeName);
            Assert.Contains("Decal/vhs", windows);
            Assert.Contains("Virindi Hotkey System", windows);

            string view = await ControlPipe.SendAsync("view decal/vhs cmdAddVvs", options.ControlPipeName);
            Assert.Contains("(Add window)", view);

            string sent = await ControlPipe.SendAsync("press decal/vhs cmdAddVvs", options.ControlPipeName);
            Assert.Contains("to Decal/vhs", sent);
            Assert.Contains("no window named", await ControlPipe.SendAsync("view Decal/nothing", options.ControlPipeName));
        }

        /// <summary>
        /// A window whose name has spaces in it - a Decal plugin's, "DecalCompat/Virindi Reporter" -
        /// is taken whole: the longest name that begins the line, then the words after it.
        /// </summary>
        [Fact]
        public void TheControlPipeTakesAWindowNameWithSpacesWhole()
        {
            string[] names = { "Decal", "DecalCompat", "DecalCompat/Virindi Reporter", "DecalCompat/Virindi Item Tool" };

            Assert.Equal(new[] { "DecalCompat/Virindi Reporter", "cmdReset" }, ControlPipe.SplitWindow("DecalCompat/Virindi Reporter cmdReset", 2, names));
            Assert.Equal(new[] { "decalcompat/virindi item tool", "txtName", "a b c" }, ControlPipe.SplitWindow("decalcompat/virindi item tool txtName a b c", 2, names));
            Assert.Equal(new[] { "DecalCompat/Virindi Reporter" }, ControlPipe.SplitWindow("DecalCompat/Virindi Reporter", 1, names));
            Assert.Equal(new[] { "Decal", "lstPlugins", "0", "1" }, ControlPipe.SplitWindow("Decal lstPlugins 0 1", 3, names));
            Assert.Equal(new[] { "Unknown", "x" }, ControlPipe.SplitWindow("Unknown x", 2, names));
            Assert.Empty(ControlPipe.SplitWindow("  ", 2, names));
        }

        /// <summary>
        /// One asker still waiting for its answer - a `ctl logout` waits up to 25 seconds - does not
        /// keep the next out: a position poll beside it is answered, not told no host is running.
        /// A second host on the same name still serves nothing while the first holds it.
        /// </summary>
        [Fact]
        public async Task TheControlPipeAnswersOneAskerWhileAnotherWaits()
        {
            HostRuntimeOptions options = Isolated(NewRoot(), new QuietTransport());
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            // Connected, and its line not yet sent: the pipe is busy with it, as with a logout.
            using NamedPipeClientStream waiting = new NamedPipeClientStream(".", options.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await waiting.ConnectAsync(5000);

            string status = await ControlPipe.SendAsync("status", options.ControlPipeName).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("acting", status);

            // The first is answered in its turn.
            await waiting.WriteAsync(Encoding.UTF8.GetBytes("act" + Environment.NewLine));
            using (StreamReader reader = new StreamReader(waiting, Encoding.UTF8, false, 1024, leaveOpen: true))
                Assert.Equal("acting off", (await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10))).Trim());

            // A second host given the same name leaves it to the first.
            HostRuntimeOptions second = Isolated(NewRoot(), new QuietTransport());
            second.ControlPipeName = options.ControlPipeName;
            second.EnableActions = true;
            await using HostRuntime other = new HostRuntime(second, new ListLog());
            await other.StartAsync();
            Assert.True(other.Host.ActionsAllowed);
            for (int i = 0; i < 5; i++)
                Assert.Contains("acting     off", await ControlPipe.SendAsync("status", options.ControlPipeName));
        }

        /// <summary>
        /// A live tester presses a hotkey and types a line through the pipe: the hotkey goes to its
        /// plugin as the overlay sends one, and the line takes the typed line's route.
        /// </summary>
        [Fact]
        public async Task TheControlPipePressesHotkeysAndSaysLines()
        {
            HostRuntimeOptions options = Isolated(NewRoot(), new QuietTransport());
            options.EnableActions = true;
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            // Decal's own hotkey turns acting over.
            Assert.True(runtime.Host.ActionsAllowed);
            Assert.Contains("pressed Decal's hotkey ToggleActing", await ControlPipe.SendAsync("hotkey decal ToggleActing", options.ControlPipeName));
            await ControlPipe.SendAsync("status", options.ControlPipeName);
            Assert.False(runtime.Host.ActionsAllowed);
            Assert.Contains("no plugin named", await ControlPipe.SendAsync("hotkey Nobody X", options.ControlPipeName));

            // A line said is answered with what became of it.
            Assert.Equal("say what?", (await ControlPipe.SendAsync("say", options.ControlPipeName)).Trim());
            string said = (await ControlPipe.SendAsync("say /nosuchcommand", options.ControlPipeName)).Trim();
            Assert.True(Enum.TryParse(said, out ChatCommandOutcome _), said);
        }

        /// <summary>
        /// The overlay says what the game window is doing and the host keeps it, for plugins and for
        /// `ctl status` and `ctl window`; `ctl window keep on` switches parking the game in place of
        /// minimizing it, which the overlay reads from the next snapshot.
        /// </summary>
        [Fact]
        public async Task TheOverlaySaysWhatTheGameWindowDoesAndTheControlPipeSwitchesKeepingOn()
        {
            HostRuntimeOptions options = Isolated(NewRoot(), new QuietTransport());
            options.Overlay = true;
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            Assert.Contains("window     not known (no overlay); keep playing while minimized off", await ControlPipe.SendAsync("status", options.ControlPipeName));

            using NamedPipeClientStream pipe = new NamedPipeClientStream(".", options.OverlayPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);

            // As the overlay frames a command: four bytes of length, then the JSON.
            byte[] json = Encoding.UTF8.GetBytes("{\"name\":\"game-window\",\"value\":\"1,0,0\",\"row_id\":\"\",\"owner\":\"\",\"control_id\":\"\"}");
            byte[] frame = new byte[4 + json.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
            json.CopyTo(frame, 4);
            await pipe.WriteAsync(frame);
            await pipe.FlushAsync();

            Assert.True(await Eventually(() => runtime.Host.GameWindow.Minimized));
            Assert.Contains("minimized, no frames; keep playing while minimized off", await ControlPipe.SendAsync("window", options.ControlPipeName));

            Assert.Contains("keep playing while minimized on", await ControlPipe.SendAsync("window keep on", options.ControlPipeName));
            Assert.True(runtime.Host.KeepPlayingMinimized);
            Assert.Contains("usage", await ControlPipe.SendAsync("window keep maybe", options.ControlPipeName));

            // The overlay hears of it in the snapshots that follow.
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            byte[] prefix = new byte[4];
            bool told = false;
            while (!told)
            {
                await pipe.ReadExactlyAsync(prefix, timeout.Token);
                byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
                await pipe.ReadExactlyAsync(body, timeout.Token);
                using JsonDocument document = JsonDocument.Parse(body);
                told = document.RootElement.TryGetProperty("keep_playing_minimized", out JsonElement keep) && keep.GetBoolean();
            }
        }

        /// <summary>
        /// `ctl exit` answers first and then asks whoever started the host to stop it; a host that
        /// nobody can stop that way says so.
        /// </summary>
        [Fact]
        public async Task TheControlPipeAsksTheHostsOwnerToExit()
        {
            HostRuntimeOptions options = Isolated(NewRoot(), new QuietTransport());
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            Assert.Contains("cannot be told to exit", await ControlPipe.SendAsync("exit", options.ControlPipeName));

            TaskCompletionSource asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Control.ExitRequested += (_, _) => asked.TrySetResult();
            Assert.Equal("exiting", (await ControlPipe.SendAsync("exit", options.ControlPipeName)).Trim());
            await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PluginsMayActFromTheStartOnlyWhenAskedTo()
        {
            await using HostRuntime watching = new HostRuntime(Isolated(NewRoot(), new QuietTransport()), new ListLog());
            Assert.False(watching.Host.ActionsAllowed);

            HostRuntimeOptions options = Isolated(NewRoot(), new QuietTransport());
            options.EnableActions = true;
            await using HostRuntime acting = new HostRuntime(options, new ListLog());
            Assert.True(acting.Host.ActionsAllowed);
        }

        [Fact]
        public async Task WithoutPluginsThereIsNoPluginListButStillAControlPipe()
        {
            HostRuntimeOptions options = Isolated(NewRoot(), new QuietTransport());
            options.NoPlugins = true;
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            Assert.Null(runtime.Plugins);
            Assert.Null(runtime.Decal);
            Assert.Empty(runtime.Host.Plugins);
            Assert.Contains("this host was started without plugins", await ControlPipe.SendAsync("reload x", options.ControlPipeName));
        }

        [Fact]
        public async Task SettingsGivenToTheRuntimeReachThePlugins()
        {
            HostRuntimeOptions options = Isolated(NewRoot(), new QuietTransport());
            options.Settings["Counting:Colour"] = "blue";
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());

            Assert.Equal("blue", runtime.Host.Settings["Counting:Colour"]);
        }

        [Fact]
        public void ALiveHostNeedsAServerToRelayTo()
        {
            HostRuntimeOptions options = Isolated(NewRoot());
            Assert.Throws<ArgumentException>(() => new HostRuntime(options, new ListLog()));
        }

        // ------------------------------------------------------------------- the real relay

        /// <summary>
        /// The first of two consecutive UDP ports on loopback that nothing holds, from the
        /// ephemeral range, which no game server or host is configured on.
        /// </summary>
        internal static int FreePortPair()
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                int first;
                using (UdpClient probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    first = ((IPEndPoint)probe.Client.LocalEndPoint).Port;

                if (first >= 65535 || !IsFree(first) || !IsFree(first + 1))
                    continue;

                return first;
            }

            throw new InvalidOperationException("No two consecutive UDP ports were free.");
        }

        private static bool IsFree(int port)
        {
            try
            {
                using UdpClient client = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static HostRuntimeOptions Relaying(string root, int listenPort)
        {
            HostRuntimeOptions options = Isolated(root);
            options.Proxy.ServerHost = "127.0.0.1";
            options.Proxy.ListenPort = listenPort;

            // Nothing answers there; the relay only has to be able to forward to it.
            int serverPort = FreePortPair();
            while (Math.Abs(serverPort - listenPort) < 2)
                serverPort = FreePortPair();
            options.Proxy.ServerPort = serverPort;
            return options;
        }

        [Fact]
        public async Task TheRelayHoldsItsPortsWhileRunningAndLetsThemGo()
        {
            string root = NewRoot();
            StageCountingPlugin(root);
            int port = FreePortPair();
            HostRuntimeOptions options = Relaying(root, port);
            options.Overlay = true;

            HostRuntime runtime = new HostRuntime(options, new ListLog());
            Assert.True(runtime.IsLive);
            Assert.IsType<ProxyTransport>(runtime.Transport);
            await runtime.StartAsync();

            Assert.False(IsFree(port));
            Assert.False(IsFree(port + 1));

            // The overlay's pipe is served too, and what it sends first is Decal's window: the
            // same snapshot achost run --overlay sends. Counting has nothing to show, so it has
            // no window of its own on the bar.
            // Attached is asked while the stand-in overlay is still on the pipe: once it lets go, the
            // server may notice before the question is put.
            await WithFirstSnapshotAsync(options.OverlayPipeName, async state =>
            {
                Assert.Contains(state.Windows, w => w.Owner == "Decal");
                Assert.DoesNotContain(state.Windows, w => w.Owner == "Counting");
                Assert.True(runtime.OverlayAttached || await Eventually(() => runtime.OverlayAttached));
            });

            await runtime.DisposeAsync();

            Assert.True(IsFree(port));
            Assert.True(IsFree(port + 1));
        }

        [Fact]
        public async Task AHostThatCannotHaveItsPortsSaysSoAndHoldsNoPipe()
        {
            string root = NewRoot();
            int port = FreePortPair();
            HostRuntimeOptions options = Relaying(root, port);

            using UdpClient squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            HostRuntime runtime = new HostRuntime(options, new ListLog());

            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync());
            Assert.Contains("already in use", refused.Message);

            // Nothing is listening on the pipe: a failed host must not hold the name.
            await Assert.ThrowsAsync<TimeoutException>(() => ControlPipe.SendAsync("status", options.ControlPipeName, timeoutMs: 300));
            await runtime.DisposeAsync();
        }

        private static async Task<bool> Eventually(Func<bool> condition)
        {
            for (int i = 0; i < 40 && !condition(); i++)
                await Task.Delay(50);
            return condition();
        }

        /// <summary>Connects as the injected overlay would, and reads frames until a snapshot arrives.</summary>
        /// <summary>Connects as the overlay does, reads the first full snapshot, and checks it while still connected.</summary>
        private static async Task WithFirstSnapshotAsync(string pipeName, Func<AC.Host.Overlay.OverlayState, Task> check)
        {
            using NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);

            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            byte[] prefix = new byte[4];
            while (true)
            {
                await pipe.ReadExactlyAsync(prefix, timeout.Token);
                byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
                await pipe.ReadExactlyAsync(body, timeout.Token);

                string json = Encoding.UTF8.GetString(body);
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("windows", out _))
                {
                    await check(AC.Host.Overlay.OverlayJson.ReadState(json));
                    return;
                }
            }
        }
    }
}
