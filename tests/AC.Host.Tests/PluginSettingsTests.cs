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
    /// Typed, persisted settings. What is worth testing is not that JSON round-trips -
    /// it does - but the edges that decide whether a player's settings survive: an old
    /// file missing a new property, a file that is not JSON at all, a save the plugin
    /// forgot to make, and a crash mid-write.
    /// </summary>
    public class PluginSettingsTests
    {
        private sealed class LootSettings
        {
            public bool PickUp { get; set; }

            public int KeepUpTo { get; set; } = 6;

            public string Profile { get; set; } = "Starter.utl";
        }

        /// <summary>A later version of the same settings, with a property the file predates.</summary>
        private sealed class LootSettingsV2
        {
            public bool PickUp { get; set; }

            public int KeepUpTo { get; set; } = 6;

            public string Profile { get; set; } = "Starter.utl";

            public bool Echo { get; set; } = true;
        }

        private sealed class SettingsPlugin : IPlugin
        {
            public string Name => "Settings";

            public PluginSettings<LootSettings> Settings { get; private set; }

            public void Startup(IHost host)
            {
                Settings = host.LoadSettings<LootSettings>(this);
            }

            public void Shutdown()
            {
            }
        }

        private static string TempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "achost-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static async Task<(GameHost host, SettingsPlugin plugin)> RunAsync(string root)
        {
            SettingsPlugin plugin = new SettingsPlugin();
            GameHost host = new GameHost(new CaptureTransport(Array.Empty<CapturedDatagram>()), new ListLog(), dataRoot: root);
            host.AddPlugin(plugin);
            await host.StartAsync();
            await host.Ended.WaitAsync(TimeSpan.FromSeconds(10));
            return (host, plugin);
        }

        [Fact]
        public async Task AFirstRunGetsTheDefaultsAndSaysSo()
        {
            string root = TempRoot();
            (GameHost host, SettingsPlugin plugin) = await RunAsync(root);

            Assert.False(plugin.Settings.LoadedFromDisk);
            Assert.False(plugin.Settings.Value.PickUp);
            Assert.Equal(6, plugin.Settings.Value.KeepUpTo);
            Assert.EndsWith(Path.Combine("plugins", "Settings", "settings.json"), plugin.Settings.Path);

            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }

        [Fact]
        public async Task WhatWasChangedComesBackNextTime()
        {
            string root = TempRoot();

            (GameHost first, SettingsPlugin one) = await RunAsync(root);
            one.Settings.Value.PickUp = true;
            one.Settings.Value.KeepUpTo = 3;
            one.Settings.Value.Profile = "LootSnobV4.utl";
            Assert.True(one.Settings.Save());
            await first.DisposeAsync();

            (GameHost second, SettingsPlugin two) = await RunAsync(root);

            Assert.True(two.Settings.LoadedFromDisk);
            Assert.True(two.Settings.Value.PickUp);
            Assert.Equal(3, two.Settings.Value.KeepUpTo);
            Assert.Equal("LootSnobV4.utl", two.Settings.Value.Profile);

            await second.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }

        /// <summary>
        /// A plugin that changes a setting and never calls Save has still changed it. Losing
        /// that on exit would make every switch in the overlay lie the next time round.
        /// </summary>
        [Fact]
        public async Task TheHostSavesAtShutdownEvenIfThePluginForgot()
        {
            string root = TempRoot();

            (GameHost first, SettingsPlugin one) = await RunAsync(root);
            one.Settings.Value.PickUp = true;
            await first.DisposeAsync();

            (GameHost second, SettingsPlugin two) = await RunAsync(root);
            Assert.True(two.Settings.Value.PickUp);

            await second.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }

        /// <summary>
        /// Adding a setting must not require migrating anything: an old file simply
        /// leaves the new property at the value the constructor gave it.
        /// </summary>
        [Fact]
        public void AnOldFileLeavesANewPropertyAtItsDefault()
        {
            string root = TempRoot();
            string path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, "{ \"PickUp\": true, \"KeepUpTo\": 2, \"Profile\": \"old.utl\" }");

            PluginSettings<LootSettingsV2> settings = new PluginSettings<LootSettingsV2>(path, new ListLog());

            Assert.True(settings.LoadedFromDisk);
            Assert.True(settings.Value.PickUp);
            Assert.Equal(2, settings.Value.KeepUpTo);
            Assert.True(settings.Value.Echo);

            Directory.Delete(root, recursive: true);
        }

        /// <summary>
        /// The player's file is worth more than a clean start. A file that is not JSON is
        /// set aside, not overwritten by the next save.
        /// </summary>
        [Fact]
        public void AFileThatIsNotJsonIsSetAsideRatherThanDestroyed()
        {
            string root = TempRoot();
            string path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, "{ this is not json");

            ListLog log = new ListLog();
            PluginSettings<LootSettings> settings = new PluginSettings<LootSettings>(path, log);

            Assert.False(settings.LoadedFromDisk);
            Assert.Equal(6, settings.Value.KeepUpTo);
            Assert.True(File.Exists(path + ".broken"));
            Assert.Equal("{ this is not json", File.ReadAllText(path + ".broken"));
            Assert.Contains(log.Lines, l => l.StartsWith("ERROR") && l.Contains("set aside"));

            // And the next save writes a good file without touching the copy.
            Assert.True(settings.Save());
            Assert.Equal("{ this is not json", File.ReadAllText(path + ".broken"));

            Directory.Delete(root, recursive: true);
        }

        [Fact]
        public void ASaveLeavesNoTemporaryFileBehind()
        {
            string root = TempRoot();
            string path = Path.Combine(root, "settings.json");

            PluginSettings<LootSettings> settings = new PluginSettings<LootSettings>(path, new ListLog());
            Assert.True(settings.Save());

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));

            Directory.Delete(root, recursive: true);
        }

        [Fact]
        public void ReloadDiscardsUnsavedChanges()
        {
            string root = TempRoot();
            string path = Path.Combine(root, "settings.json");

            PluginSettings<LootSettings> settings = new PluginSettings<LootSettings>(path, new ListLog());
            settings.Value.KeepUpTo = 1;
            settings.Save();

            settings.Value.KeepUpTo = 99;
            settings.Reload();

            Assert.Equal(1, settings.Value.KeepUpTo);

            Directory.Delete(root, recursive: true);
        }

        [Fact]
        public async Task ASaveToAnUnwritablePlaceIsLoggedNotThrown()
        {
            string root = TempRoot();
            (GameHost host, SettingsPlugin plugin) = await RunAsync(root);

            // Make the directory unwritable by putting a file where the settings
            // directory would need to be created.
            string blocker = Path.Combine(root, "plugins", "Settings", "settings.json");
            Directory.Delete(Path.GetDirectoryName(blocker), recursive: true);
            File.WriteAllText(Path.GetDirectoryName(blocker), "a file, not a directory");

            Assert.False(plugin.Settings.Save());

            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }
}
