using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AC.Host.Plugins;
using Decal.Adapter.Hosting;
using Decal.Compat;
using VirindiHotkeySystem;
using VirindiHUDs.UIs;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Virindi Hotkey System and Virindi HUDs as Decal plugins reach them here - stand-ins, since
    /// the host does both jobs itself: a hotkey a plugin adds is one of the host's, and a status row
    /// it gives goes to the Status HUD a plugin of the host's shows.
    /// </summary>
    [Collection(DecalCollection.Name)]
    public sealed class VirindiStandInTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "achost-vstandins-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Mag-Tools' Pack Inventory, on Ctrl+P, is listed with the host's hotkeys under the name it
        /// was added with; the key pressed raises its Fired2; it is added once however often a login
        /// adds it, and goes when Decal stops.
        /// </summary>
        [Fact]
        public void AHotkeyAPluginAddsIsTheHostsToBindAndPress()
        {
            MacroTestHost host = Host();
            DecalCompatPlugin decal = new DecalCompatPlugin(new FakeDecalRegistry());
            decal.Startup(host);
            try
            {
                int fired = 0;
                VHotkeyInfo pack = new VHotkeyInfo("Mag-Tools", true, "Pack Inventory", "Triggers the Inventory Packer Macro", 80, false, true, false);
                pack.Fired2 += (_, e) =>
                {
                    fired++;
                    Assert.True(e.Eat);
                };
                VHotkeySystem.InstanceReal.AddHotkey(pack);
                VHotkeySystem.InstanceReal.AddHotkey(new VHotkeyInfo("Mag-Tools", true, "Pack Inventory", "Again, at the next login", 80, false, true, false));
                VHotkeySystem.InstanceReal.AddHotkey(new VHotkeyInfo("Mag-Tools", true, "One Touch Heal", "Triggers the One Touch Healing Macro", 0, false, false, false));

                HotkeyDefinition listed = Assert.Single(decal.Hotkeys, h => h.Name == "Pack Inventory");
                Assert.Equal(("vhs/Mag-Tools/Pack Inventory", "Again, at the next login", "Ctrl+P", "Mag-Tools"), (listed.Id, listed.Description, listed.DefaultKeys, listed.Source));
                Assert.Equal(string.Empty, Assert.Single(decal.Hotkeys, h => h.Name == "One Touch Heal").DefaultKeys);
                Assert.False(pack.IsInitialized);

                // The one added last stands, as VHS kept one hotkey of a name; pressing it fires it.
                VHotkeyInfo again = VHotkeySystem.InstanceReal.GetHotkeyByName("Mag-Tools", "Pack Inventory");
                Assert.Equal("C-p", again.KeyString);
                again.Fired2 += (_, _) => fired++;
                decal.HotkeyPressed("vhs/Mag-Tools/Pack Inventory");
                Assert.Equal(1, fired);

                // A hotkey switched off in VHS's own terms is not fired.
                again.Enabled = false;
                decal.HotkeyPressed("vhs/Mag-Tools/Pack Inventory");
                Assert.Equal(1, fired);
            }
            finally
            {
                decal.Shutdown();
            }

            Assert.Empty(VHotkeySystem.InstanceReal.AllHotkeys);
        }

        /// <summary>A row Mag-Tools gives Virindi HUDs' StatusModel reaches the host, and through it a plugin that shows a status HUD.</summary>
        [Fact]
        public async Task AStatusRowGoesToTheStatusHudAPluginShows()
        {
            MacroTestHost host = Host();
            using (DecalRuntime runtime = new DecalRuntime(host))
            {
                StatusModel.UpdateEntry("Mag-Tools", "Mana", "2h10m");
                StatusModel.UpdateEntry("Mag-Tools", "Pack Slots", null, System.Drawing.Color.Red);
                StatusModel.UpdateEntry(null, "Mana", "nobody's");
            }

            Assert.Equal(new Dictionary<string, string> { ["Mag-Tools - Mana"] = "2h10m", ["Mag-Tools - Pack Slots"] = string.Empty }, host.StatusRows);

            // The host hands each row to every plugin that shows them.
            ListLog log = new ListLog();
            GameHost game = new GameHost(new LingeringTransport(), log, dataRoot: Path.Combine(_root, "game"));
            RowShower shower = new RowShower();
            game.AddPlugin(shower);
            await game.StartAsync();
            game.UpdateStatusRow("Mag-Tools", "DPS Out 1m", "120", 0xFFFF0000);
            await game.DisposeAsync();

            Assert.Equal(new[] { ("Mag-Tools", "DPS Out 1m", "120", 0xFFFF0000L) }, shower.Rows);
        }

        /// <summary>
        /// Virindi Tank's API for other plugins answers as Virindi Tank did with no loot profile
        /// loaded: nothing needs an ID, no rule keeps anything, the world tracker has nothing - and
        /// its LootAction reads as the real one did.
        /// </summary>
        [Fact]
        public void VirindiTanksApiAnswersAsWithNoLootProfile()
        {
            const int Item = unchecked((int)0x80000201);
            uTank2.PluginCore tank = uTank2.PluginCore.PC;
            Assert.NotNull(tank);
            Assert.False(tank.FLootPluginQueryNeedsID(Item));

            uTank2.LootPlugins.LootAction decision = tank.FLootPluginClassifyImmediate(Item);
            Assert.Equal((true, false, false, false, false, 0), (decision.IsNoLoot, decision.IsKeep, decision.IsSalvage, decision.IsSell, decision.IsKeepUpTo, decision.Data1));

            List<(int, bool, bool)> told = new List<(int, bool, bool)>();
            tank.FLootPluginClassifyCallback(Item, (id, result, success) => told.Add((id, result.IsNoLoot, success)));
            Assert.Equal(new[] { (Item, true, true) }, told);

            Assert.Null(tank.FWorldTracker_GetWithID(Item));
            Assert.Empty(tank.FWorldTracker_GetInContainer(0x50000001));
            Assert.Equal((true, 5), (uTank2.LootPlugins.LootAction.GetKeepUpTo(5).IsKeepUpTo, uTank2.LootPlugins.LootAction.GetKeepUpTo(5).Data1));

            // Mag-Tools names the loot types in "uTank2": they are the host's own.
            Assert.Equal(typeof(uTank2.LootPlugins.LootAction).Assembly, typeof(uTank2.PluginCore).Assembly.GetType("uTank2.LootPlugins.LootAction", throwOnError: true).Assembly);
        }

        /// <summary>
        /// Mag-Tools never said "/mt" was its word, so a line the player typed for it went to the
        /// server and was said aloud; registered, its word is known here, on or off.
        /// </summary>
        [Fact]
        public void MagToolsWordIsKnownWhereverItIsRegistered()
        {
            MacroTestHost host = Host();
            host.Settings["DecalCompat:Registry"] = "true";
            FakeDecalRegistry registry = new FakeDecalRegistry();
            string example = Path.Combine(AppContext.BaseDirectory, "decal-plugins", "AutoWireupExamplePlugin");
            registry.Plugins.Add(new AC.Dat.DecalRegistryEntry("{959D5CA6-0BD5-48A2-9AD2-F95F94DCDC3E}", "Mag-Tools", example, "AutoWireupExamplePlugin.dll", enabled: false));

            DecalCompatPlugin decal = new DecalCompatPlugin(registry);
            decal.Startup(host);
            try
            {
                Assert.Equal(new[] { "mt" }, decal.CommandWords);
            }
            finally
            {
                decal.Shutdown();
            }

            DecalCompatPlugin none = new DecalCompatPlugin(new FakeDecalRegistry());
            none.Startup(Host());
            try
            {
                Assert.Empty(none.CommandWords);
            }
            finally
            {
                none.Shutdown();
            }
        }

        private MacroTestHost Host()
        {
            MacroTestHost host = new MacroTestHost(dataRoot: Path.Combine(_root, "data"));
            host.Settings["DecalCompat:Folder"] = Path.Combine(_root, "folder");
            host.Settings["DecalCompat:Registry"] = "false";
            return host;
        }

        private sealed class RowShower : IPlugin, IStatusRows
        {
            public List<(string, string, string, long)> Rows { get; } = new List<(string, string, string, long)>();

            public string Name => "Shower";

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }

            public void UpdateStatusRow(string plugin, string entry, string value, long colour) => Rows.Add((plugin, entry, value, colour));
        }

        /// <summary>Stays open until disposed, so the host keeps running its game thread.</summary>
        private sealed class LingeringTransport : AC.Host.Transport.IGameTransport
        {
            public string Description => "lingering";

#pragma warning disable CS0067
            public event EventHandler<AC.Host.Transport.GameMessageEventArgs> MessageReceived;
#pragma warning restore CS0067

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
                return ValueTask.CompletedTask;
            }
        }
    }
}
