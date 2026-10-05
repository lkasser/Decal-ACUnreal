using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decal;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Adding a plugin to the plugin folder and taking one out again, as the Decal Agent's Add
    /// and Remove buttons do, and binding the movement keys from outside the game.
    /// </summary>
    public class PluginInstallTests
    {
        private sealed class QuietTransport : IGameTransport
        {
            public string Description => "quiet test transport";

            public event EventHandler<GameMessageEventArgs> MessageReceived { add { } remove { } }

            public event EventHandler Ended { add { } remove { } }

            public bool CanSend => true;

            public bool CanShowInGame => false;

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "achost-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "plugins"));
            return root;
        }

        /// <summary>Where the host keeps its own files: apart from the plugin folder, as it is beside a real host.</summary>
        private static string Data(string root) => Path.Combine(root, "data");

        /// <summary>A folder somewhere else holding Counting.Plugin, as a build output or a download would.</summary>
        private static string Elsewhere(string root, string name = "download")
        {
            string folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Counting.Plugin.dll"), Path.Combine(folder, "Counting.Plugin.dll"));
            return folder;
        }

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

        private static int Read(IPlugin plugin, string property)
            => (int)plugin.GetType().GetProperty(property).GetValue(plugin);

        [Fact]
        public async Task AnAssemblyIsInstalledIntoAFolderOfItsOwnAndStarted()
        {
            string root = NewRoot();
            string plugins = Path.Combine(root, "plugins");
            string source = Elsewhere(root);
            await using GameHost host = new GameHost(new QuietTransport(), new ListLog(), dataRoot: Data(root));
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), Data(root));
            manager.LoadAll();
            await host.StartAsync();

            PluginEntry entry = await OnGameThread(host, () => manager.Install(Path.Combine(source, "Counting.Plugin.dll")));

            Assert.NotNull(entry);
            Assert.Equal("Counting", entry.Name);
            Assert.Equal("running", entry.Status);
            Assert.True(File.Exists(Path.Combine(plugins, "Counting.Plugin", "Counting.Plugin.dll")));
            Assert.Contains(host.Plugins, p => p.Name == "Counting");

            // Its source is untouched: it was copied, not moved.
            Assert.True(File.Exists(Path.Combine(source, "Counting.Plugin.dll")));
        }

        [Fact]
        public async Task InstallingWhatIsAlreadyInstalledReplacesItAndReloadsIt()
        {
            string root = NewRoot();
            string plugins = Path.Combine(root, "plugins");
            string installed = Path.Combine(plugins, "Counting");
            Directory.CreateDirectory(installed);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Counting.Plugin.dll"), Path.Combine(installed, "Counting.Plugin.dll"));
            string source = Elsewhere(root);

            await using GameHost host = new GameHost(new QuietTransport(), new ListLog(), dataRoot: Data(root));
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), Data(root));
            manager.LoadAll();
            await host.StartAsync();
            IPlugin before = manager.Entries[0].Plugin;

            PluginEntry entry = await OnGameThread(host, () => manager.Install(Path.Combine(source, "Counting.Plugin.dll")));

            // Still one of it, where it was, and the running one is a fresh load.
            Assert.Same(manager.Entries.Single(), entry);
            Assert.Equal(Path.Combine(installed, "Counting.Plugin.dll"), entry.AssemblyPath);
            Assert.False(Directory.Exists(Path.Combine(plugins, "Counting.Plugin")));
            Assert.NotSame(before, entry.Plugin);
            Assert.Equal(1, Read(before, "Shutdowns"));
            Assert.Single(host.Plugins, p => p.Name == "Counting");
        }

        [Fact]
        public async Task AFolderIsInstalledWholeWithItsDataFiles()
        {
            string root = NewRoot();
            string plugins = Path.Combine(root, "plugins");
            string source = Elsewhere(root, "Counter Pack");
            File.WriteAllText(Path.Combine(source, "rules.xml"), "<rules />");
            Directory.CreateDirectory(Path.Combine(source, "profiles"));
            File.WriteAllText(Path.Combine(source, "profiles", "default.utl"), "profile");

            GameHost host = new GameHost(new QuietTransport(), new ListLog(), dataRoot: Data(root));
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), Data(root));

            // Before the host starts it is simply listed, and starts with the rest.
            PluginEntry entry = manager.Install(source);

            Assert.Equal("Counting", entry.Name);
            Assert.True(File.Exists(Path.Combine(plugins, "Counter Pack", "rules.xml")));
            Assert.True(File.Exists(Path.Combine(plugins, "Counter Pack", "profiles", "default.utl")));

            await host.StartAsync();
            Assert.Equal(1, Read(entry.Plugin, "Startups"));
            await host.DisposeAsync();
        }

        [Fact]
        public async Task WhatIsNotAPluginForThisHostIsRefusedAndNothingIsCopied()
        {
            string root = NewRoot();
            string plugins = Path.Combine(root, "plugins");
            string source = Path.Combine(root, "download");
            Directory.CreateDirectory(source);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "AC.Protocol.dll"), Path.Combine(source, "AC.Protocol.dll"));
            File.WriteAllText(Path.Combine(source, "readme.txt"), "not a plugin");

            await using GameHost host = new GameHost(new QuietTransport(), new ListLog(), dataRoot: Data(root));
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), Data(root));

            InvalidOperationException assembly = Assert.Throws<InvalidOperationException>(() => manager.Install(Path.Combine(source, "AC.Protocol.dll")));
            Assert.Contains("not a plugin for this host", assembly.Message);
            InvalidOperationException folder = Assert.Throws<InvalidOperationException>(() => manager.Install(source));
            Assert.Contains("no plugin for this host", folder.Message);
            Assert.Throws<FileNotFoundException>(() => manager.Install(Path.Combine(source, "missing.dll")));

            Assert.Empty(Directory.GetFileSystemEntries(plugins));
            Assert.Empty(manager.Entries);
        }

        [Fact]
        public async Task SomethingAlreadyInThePluginFolderIsNotInstalledOverItself()
        {
            string root = NewRoot();
            string plugins = Path.Combine(root, "plugins");
            string inside = Elsewhere(plugins, "Counting");

            await using GameHost host = new GameHost(new QuietTransport(), new ListLog(), dataRoot: Data(root));
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), Data(root));

            Assert.Throws<InvalidOperationException>(() => manager.Install(Path.Combine(inside, "Counting.Plugin.dll")));
        }

        [Fact]
        public async Task RemovingAPluginStopsItAndDeletesItsFolderButNotItsData()
        {
            string root = NewRoot();
            string plugins = Path.Combine(root, "plugins");
            string folder = Elsewhere(plugins, "Counting");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "installed with it");

            await using GameHost host = new GameHost(new QuietTransport(), new ListLog(), dataRoot: Data(root));
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), Data(root));
            manager.LoadAll();
            await host.StartAsync();

            PluginEntry entry = manager.Entries.Single();
            IPlugin plugin = entry.Plugin;
            string data = host.GetDataDirectory(plugin);
            int changes = 0;
            manager.Changed += (_, _) => changes++;

            await OnGameThread(host, () =>
            {
                manager.Uninstall(entry);
                return true;
            });

            Assert.Empty(manager.Entries);
            Assert.Equal("removed", entry.Status);
            Assert.False(entry.IsRunning);
            Assert.Equal(1, Read(plugin, "Shutdowns"));
            Assert.DoesNotContain(host.Plugins, p => p.Name == "Counting");
            Assert.False(Directory.Exists(folder));
            Assert.True(Directory.Exists(plugins));
            Assert.True(Directory.Exists(data));
            Assert.True(changes > 0);

            // Gone for good: a fresh look finds nothing.
            await OnGameThread(host, () =>
            {
                manager.Rescan();
                return true;
            });
            Assert.Empty(manager.Entries);
        }

        [Fact]
        public async Task RemovingALooseAssemblyDeletesOnlyItAndWhatGoesWithIt()
        {
            string root = NewRoot();
            string plugins = Path.Combine(root, "plugins");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Counting.Plugin.dll"), Path.Combine(plugins, "Counting.Plugin.dll"));
            File.WriteAllText(Path.Combine(plugins, "Counting.Plugin.xml"), "<doc />");
            File.WriteAllText(Path.Combine(plugins, "Someone Else.txt"), "not this plugin's");

            GameHost host = new GameHost(new QuietTransport(), new ListLog(), dataRoot: Data(root));
            PluginManager manager = new PluginManager(host, plugins, new ListLog(), Data(root));
            manager.LoadAll();

            manager.Uninstall(manager.Entries.Single());

            Assert.False(File.Exists(Path.Combine(plugins, "Counting.Plugin.dll")));
            Assert.False(File.Exists(Path.Combine(plugins, "Counting.Plugin.xml")));
            Assert.True(File.Exists(Path.Combine(plugins, "Someone Else.txt")));
            await host.DisposeAsync();
        }

        // ------------------------------------------------------------------- movement keys

        [Fact]
        public async Task MovementKeysCanBeBoundFromOutsideTheGameAndAreRemembered()
        {
            string root = NewRoot();
            GameHost host = new GameHost(new QuietTransport(), new ListLog(), dataRoot: Data(root));
            DecalAgent agent = new DecalAgent(host, null);
            host.AddPlugin(agent);

            Assert.Throws<InvalidOperationException>(() => agent.BindKey(GameKey.Forward, "i"));
            await host.StartAsync();

            Assert.Equal(new[] { GameKey.Forward, GameKey.Backward, GameKey.TurnLeft, GameKey.TurnRight }, DecalAgent.MovementKeys);
            Assert.True(await OnGameThread(host, () => agent.BindKey(GameKey.Forward, "i")));
            Assert.False(await OnGameThread(host, () => agent.BindKey(GameKey.TurnLeft, "nonsense")));
            Assert.Equal('I', host.InputKeys.VirtualKey(GameKey.Forward));
            Assert.Equal('A', host.InputKeys.VirtualKey(GameKey.TurnLeft));
            await host.DisposeAsync();

            GameHost next = new GameHost(new QuietTransport(), new ListLog(), dataRoot: Data(root));
            next.AddPlugin(new DecalAgent(next, null));
            await next.StartAsync();
            Assert.Equal('I', next.InputKeys.VirtualKey(GameKey.Forward));
            await next.DisposeAsync();
        }
    }
}
