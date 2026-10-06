using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AC.Host.Actions;
using AC.Host.Plugins;
using AC.Host.Runtime;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// AC:Unreal's own client plugins - its Unattended Combat Manager among them - and its Desktop
    /// UI Scale, as the host reads them from the client's settings, read-only.
    /// </summary>
    /// <remarks>
    /// Every settings file here is made up for the test, in the shape release 96 writes - never the
    /// player's own - and lives in a temporary folder of its own.
    /// </remarks>
    public class ClientPluginsTests
    {
        /// <summary>The client's settings.json with UCM enabled and granted all eight permissions, as UCM's manifest asks.</summary>
        private const string UcmEnabled = @"{
	""_window_positions"":
	{
		""ucm"": { ""x"": 300, ""y"": 200 },
		""ucm.size"": { ""x"": 640, ""y"": 480 }
	},
	""ucm.profile"": ""Default"",
	""ucm.folder"": ""C:/Plugins/VirindiTank"",
	""ucm"": ""enabled:cast,combat,navigation,inventory,loot,say,fellowship,confirm"",
	""waypoint"": ""disabled"",
	""looteditor"": ""disabled""
}";

        /// <summary>The client's settings.json after the plugin list was opened and nothing was enabled: window positions, a profile, two plugins off.</summary>
        private const string NothingEnabled = @"{
	""_window_positions"":
	{
		""waypoint"": { ""x"": 121, ""y"": 382 },
		""waypoint.map.size"": { ""x"": 780, ""y"": 699 }
	},
	""waypoint.profile"": ""Default"",
	""waypoint"": ""disabled"",
	""looteditor"": ""disabled""
}";

        private static string NewRoot() => Path.Combine(Path.GetTempPath(), "achost-client-plugins-" + Guid.NewGuid().ToString("N"));

        // ------------------------------------------------------------------- settings.json

        [Fact]
        public void AnEnabledUcmIsReadWithEveryPermissionItWasGranted()
        {
            ClientPluginsState state = ClientPluginsState.Parse(UcmEnabled);

            Assert.True(state.Known);
            Assert.True(state.UcmEnabled);
            Assert.True(state.AutomationEnabled);
            ClientPluginGrant ucm = Assert.Single(state.Enabled);
            Assert.Equal("ucm", ucm.Id);
            Assert.Equal("UCM", ucm.Name);
            Assert.Equal(new[] { "cast", "combat", "navigation", "inventory", "loot", "say", "fellowship", "confirm" }, ucm.Permissions);
            Assert.Equal(new[] { "cast", "combat", "navigation", "inventory", "loot" }, ucm.AutomationGranted);
            Assert.Same(ucm, Assert.Single(state.Automating));
            Assert.Equal(new[] { "waypoint", "looteditor" }, state.Disabled);
            Assert.Equal("enabled: UCM (cast, combat, navigation, inventory, loot, say, fellowship, confirm)", state.Describe());
        }

        /// <summary>Window positions, a profile's name and a folder sit beside the grants, and none of them is a plugin.</summary>
        [Fact]
        public void NothingButTheGrantsIsTakenForAPlugin()
        {
            ClientPluginsState state = ClientPluginsState.Parse(NothingEnabled);

            Assert.True(state.Known);
            Assert.Empty(state.Enabled);
            Assert.False(state.UcmEnabled);
            Assert.False(state.AutomationEnabled);
            Assert.Equal(new[] { "waypoint", "looteditor" }, state.Disabled);
            Assert.Equal("none enabled (waypoint, looteditor switched off)", state.Describe());
        }

        /// <summary>
        /// Waypoint and the loot editor ask for no permissions; a third-party plugin may ask for any.
        /// Only cast, combat, navigation, inventory and loot can play the character.
        /// </summary>
        [Fact]
        public void OnlyAPluginHoldingAnAutomationPermissionCountsAsAutomation()
        {
            ClientPluginsState state = ClientPluginsState.Parse(@"{
                ""waypoint"": ""enabled:"",
                ""monitor"": ""enabled:say, confirm"",
                ""farmer"": ""enabled:loot,inventory""
            }");

            Assert.Equal(new[] { "waypoint", "monitor", "farmer" }, state.Enabled.Select(p => p.Id));
            Assert.Empty(state.Enabled[0].Permissions);
            Assert.Equal(new[] { "say", "confirm" }, state.Enabled[1].Permissions);
            Assert.False(state.UcmEnabled);
            Assert.True(state.AutomationEnabled);
            Assert.Equal(new[] { "farmer" }, state.Automating.Select(p => p.Id));
            Assert.Equal("enabled: waypoint (no permissions); monitor (say, confirm); farmer (loot, inventory)", state.Describe());
        }

        [Fact]
        public void SettingsThatAreNotAnObjectAreNotKnown()
        {
            Assert.False(ClientPluginsState.Parse("{ not json").Known);
            Assert.False(ClientPluginsState.Parse("[]").Known);
            Assert.Equal(ClientPluginsState.NoneEnabled, ClientPluginsState.Parse(""));
            Assert.Equal("not known (the client's settings were not read)", ClientPluginsState.Unknown.Describe());
        }

        [Fact]
        public void TwoReadsOfTheSameSettingsAreEqual()
        {
            Assert.Equal(ClientPluginsState.Parse(UcmEnabled), ClientPluginsState.Parse(UcmEnabled));
            Assert.NotEqual(ClientPluginsState.Parse(UcmEnabled), ClientPluginsState.Parse(NothingEnabled));
        }

        // ------------------------------------------------------------------- the plugin bar

        [Fact]
        public void ThePluginBarStartsWhereTheClientStartsIt()
        {
            Assert.Equal(ClientPluginBar.Default, ClientPluginBar.Read(NothingEnabled));
            Assert.Equal(ClientPluginBar.Default, ClientPluginBar.Read(null));
            Assert.Equal(ClientPluginBar.Default, ClientPluginBar.Read("{ not json"));
            Assert.Equal(new ClientPluginBar(8, 80, 62, 214, false), ClientPluginBar.Default);
            Assert.Equal("8,80 62x214 (where the client starts it)", ClientPluginBar.Default.Describe());
        }

        [Fact]
        public void ThePluginBarIsWhereThePlayerLeftIt()
        {
            ClientPluginBar bar = ClientPluginBar.Read(@"{ ""_window_positions"": { ""__bar"": { ""x"": 400, ""y"": 6 }, ""__bar.size"": { ""x"": 62, ""y"": 200 } } }");

            Assert.Equal(new ClientPluginBar(400, 6, 62, 200, true), bar);
            Assert.Equal("400,6 62x200 (where the player left it)", bar.Describe());
        }

        // ------------------------------------------------------------------- Desktop UI Scale

        private static string Ini(string presentation) => $@";METADATA=(Diff=true, UseCommands=true)
[/Script/Engine.GameUserSettings]
FullscreenMode=2
DesktopUIScale=2

[ACEClient]
ShowVitalNumbers=True

[ACE.Presentation]
MasterVolume=0.101769909
{presentation}
CursorScale=1

[ACE.Camera]
InvertMouseX=False
";

        [Theory]
        [InlineData("DesktopUIScale=1", 1.0)]
        [InlineData("DesktopUIScale=1.75", 1.75)]
        [InlineData("DesktopUIScale=2.1", 2.0)]
        [InlineData("DesktopUIScale=2.2", 2.25)]
        [InlineData("DesktopUIScale=5", 3.0)]
        [InlineData("DesktopUIScale=0.5", 1.0)]
        [InlineData("DesktopUIScale=big", 1.0)]
        [InlineData("", 1.0)]
        public void TheDesktopUiScaleIsReadAsTheClientTakesIt(string line, double expected)
        {
            Assert.Equal(expected, ClientDisplay.ReadDesktopUiScale(Ini(line)));
        }

        [Fact]
        public void NoSettingsIsTheLayoutsOwnSize()
        {
            Assert.Equal(1.0, ClientDisplay.ReadDesktopUiScale(null));
            Assert.Equal("175%", ClientDisplay.Percent(1.75));
        }

        // ------------------------------------------------------------------- the watcher

        /// <summary>A Saved folder of the client's shape, in a temporary folder, with what a test puts in it.</summary>
        private static string StageSaved(string settingsJson, string presentation)
        {
            string saved = Path.Combine(NewRoot(), "Saved");
            if (settingsJson != null)
            {
                Directory.CreateDirectory(Path.Combine(saved, "ClientPlugins"));
                File.WriteAllText(ClientSettingsWatcher.PluginSettingsPath(saved), settingsJson);
            }

            if (presentation != null)
            {
                Directory.CreateDirectory(Path.Combine(saved, "Config", "Windows"));
                File.WriteAllText(ClientSettingsWatcher.GameUserSettingsPath(saved), Ini(presentation));
            }

            Directory.CreateDirectory(saved);
            return saved;
        }

        [Fact]
        public void TheWatcherHandsOnWhatTheSettingsSayAndAgainOnlyWhenTheyChange()
        {
            string saved = StageSaved(NothingEnabled, "DesktopUIScale=1.5");
            int handed = 0;
            (ClientPluginsState Plugins, double Scale, ClientPluginBar Bar) last = default;
            ClientSettingsWatcher watcher = new ClientSettingsWatcher(() => saved, (p, s, b) => { handed++; last = (p, s, b); });

            watcher.Poll();
            Assert.Equal(1, handed);
            Assert.False(last.Plugins.UcmEnabled);
            Assert.True(last.Plugins.Known);
            Assert.Equal(1.5, last.Scale);
            Assert.Equal(ClientPluginBar.Default, last.Bar);
            Assert.Equal(saved, watcher.SavedFolder);

            watcher.Poll();
            Assert.Equal(1, handed);

            // The player enables UCM: the file is written again, longer.
            File.WriteAllText(ClientSettingsWatcher.PluginSettingsPath(saved), UcmEnabled);
            watcher.Poll();
            Assert.Equal(2, handed);
            Assert.True(last.Plugins.UcmEnabled);
            Assert.Equal(1.5, last.Scale);
        }

        /// <summary>A client whose plugin list was never opened has no settings.json: nothing is enabled, and the bar is where it starts.</summary>
        [Fact]
        public void NoPluginSettingsYetIsNothingEnabled()
        {
            string saved = StageSaved(null, null);
            ClientPluginsState plugins = null;
            double scale = 0;
            ClientPluginBar bar = null;
            new ClientSettingsWatcher(() => saved, (p, s, b) => (plugins, scale, bar) = (p, s, b)).Poll();

            Assert.Equal(ClientPluginsState.NoneEnabled, plugins);
            Assert.Equal(1.0, scale);
            Assert.Equal(ClientPluginBar.Default, bar);
        }

        [Fact]
        public void NoClientIsNotKnown()
        {
            int handed = 0;
            ClientPluginsState plugins = null;
            ClientPluginBar bar = ClientPluginBar.Default;
            ClientSettingsWatcher watcher = new ClientSettingsWatcher(() => null, (p, _, b) => { handed++; (plugins, bar) = (p, b); });

            watcher.Poll();
            watcher.Poll();

            Assert.Equal(1, handed);
            Assert.False(plugins.Known);
            Assert.Null(bar);
            Assert.Null(watcher.SavedFolder);
        }

        /// <summary>The Saved folder is beside the log the running client writes, when that is known.</summary>
        [Fact]
        public void TheSavedFolderIsBesideTheClientsLog()
        {
            string saved = StageSaved(NothingEnabled, null);
            Directory.CreateDirectory(Path.Combine(saved, "Logs"));

            Assert.Equal(saved, ClientSettingsWatcher.FindSavedFolder(Path.Combine(saved, "Logs", "ACUnreal.log")));
        }

        // ------------------------------------------------------------------- the host

        [Fact]
        public async Task TheHostKeepsWhatTheClientsSettingsSayAndWarnsOfAnEnabledUcm()
        {
            ListLog log = new ListLog();
            await using GameHost host = new GameHost(new RigTransport(), log, dataRoot: NewRoot());
            Assert.False(((IHost)host).ClientPlugins.Known);
            Assert.Null(host.ClientPluginBar);

            host.NoteClientSettings(ClientPluginsState.Parse(NothingEnabled), 1.0, ClientPluginBar.Default);
            Assert.Contains("INFO AC:Unreal's own client plugins: none enabled (waypoint, looteditor switched off).", log.Lines);
            Assert.DoesNotContain(log.Lines, l => l.Contains("Desktop UI Scale"));

            ClientPluginsState ucm = ClientPluginsState.Parse(UcmEnabled);
            ClientPluginsState heard = null;
            host.ClientPluginsChanged += (_, state) => heard = state;
            host.NoteClientSettings(ucm, 1.75, ClientPluginBar.Default);
            host.NoteClientSettings(ClientPluginsState.Parse(UcmEnabled), 1.75, ClientPluginBar.Default);

            Assert.Equal(ucm, ((IHost)host).ClientPlugins);
            Assert.Same(ucm, heard);
            Assert.Equal(1.75, host.ClientUiScale);
            Assert.Single(log.Lines, l => l.StartsWith("WARN AC:Unreal's own client plugins: enabled: UCM (cast, combat", StringComparison.Ordinal)
                                          && l.Contains("UCM can play the character beside Virindi Tank's macro")
                                          && l.Contains("Enabled is not running"));
            Assert.Single(log.Lines, l => l == "INFO AC:Unreal's Desktop UI Scale is 175%: its character select is clicked where it draws it at that scale, as far as the window lets it.");
        }
    }
}
