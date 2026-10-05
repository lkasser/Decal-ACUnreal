using System.Diagnostics;
using System.IO;
using System.Linq;
using Setup.Common;
using Xunit;

namespace Setup.Tests
{
    /// <summary>Uninstalling by the lists the installs left, from folders of the test's own.</summary>
    public class UninstallerTests
    {
        private const string AgentKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DecalAgent";
        private const string PluginKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DecalAgent.VirindiTank";

        /// <summary>The Agent and Virindi Tank installed, registered, with a Start menu shortcut and some settings.</summary>
        private static MemoryRegistry InstallBoth(TempFolder temp, bool plugin = true)
        {
            MemoryRegistry registry = new MemoryRegistry();
            string agent = temp["Decal Agent"];
            using (Payload payload = Payloads.Agent())
            {
                Installer.Install(new InstallRequest
                {
                    Product = SetupProduct.DecalAgent,
                    Payload = payload,
                    AgentFolder = agent,
                    Version = "1.0.0",
                    Registry = registry,
                    Shortcuts = new FakeShortcuts(),
                    StartMenuFolder = temp["Start Menu"],
                });
            }

            if (plugin)
            {
                using Payload payload = Payloads.VirindiTank();
                Installer.Install(new InstallRequest
                {
                    Product = SetupProduct.VirindiTank,
                    Payload = payload,
                    AgentFolder = agent,
                    Version = "1.0.0",
                    Registry = registry,
                    LeaveOut = file => VirindiTankInstall.IsSuppliedByAgent(file, agent),
                });
            }

            Directory.CreateDirectory(temp[@"ACHost\plugins\VirindiTank"]);
            File.WriteAllText(temp[@"ACHost\DecalAgent.json"], "{}");
            File.WriteAllText(temp[@"ACHost\plugins\VirindiTank\settings.json"], "{}");
            return registry;
        }

        private static UninstallResult Uninstall(TempFolder temp, SetupProduct product, MemoryRegistry registry, bool deleteSettings = false)
            => Uninstaller.Uninstall(new UninstallRequest
            {
                Product = product,
                AgentFolder = temp["Decal Agent"],
                Registry = registry,
                DeleteSettings = deleteSettings,
                DataFolder = temp["ACHost"],
            });

        [Fact]
        public void UninstallingTheAgentTakesEverythingItsSetupsPutThereAndKeepsTheSettings()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = InstallBoth(temp);

            UninstallResult result = Uninstall(temp, SetupProduct.DecalAgent, registry);

            Assert.Equal(new[] { SetupProduct.VirindiTank, SetupProduct.DecalAgent }, result.Uninstalled);
            Assert.Empty(result.InUse);
            Assert.Empty(result.LeftBehind);
            Assert.False(Directory.Exists(temp["Decal Agent"]));
            Assert.False(File.Exists(temp[@"Start Menu\Decal Agent.lnk"]));
            Assert.False(registry.KeyExists(AgentKey));
            Assert.False(registry.KeyExists(PluginKey));
            Assert.False(registry.KeyExists(@"Software\Decal Agent"));

            Assert.False(result.SettingsDeleted);
            Assert.True(File.Exists(temp[@"ACHost\DecalAgent.json"]));
            Assert.True(File.Exists(temp[@"ACHost\plugins\VirindiTank\settings.json"]));
        }

        [Fact]
        public void AskedToTheSettingsGoToo()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = InstallBoth(temp);

            UninstallResult result = Uninstall(temp, SetupProduct.DecalAgent, registry, deleteSettings: true);

            Assert.True(result.SettingsDeleted);
            Assert.Equal(temp["ACHost"], result.SettingsFolder);
            Assert.False(Directory.Exists(temp["ACHost"]));
        }

        [Fact]
        public void APluginThePlayerAddedIsLeftWithTheFolderAroundIt()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = InstallBoth(temp);
            Directory.CreateDirectory(temp[@"Decal Agent\plugins\Mine"]);
            File.WriteAllText(temp[@"Decal Agent\plugins\Mine\Mine.dll"], "mine");

            UninstallResult result = Uninstall(temp, SetupProduct.DecalAgent, registry);

            Assert.Equal(new[] { "plugins" }, result.LeftBehind);
            Assert.True(File.Exists(temp[@"Decal Agent\plugins\Mine\Mine.dll"]));
            Assert.False(Directory.Exists(temp[@"Decal Agent\plugins\VirindiTank"]));
            Assert.False(Directory.Exists(temp[@"Decal Agent\plugins\Decal.Compat"]));
            Assert.False(File.Exists(temp[@"Decal Agent\DecalAgent.exe"]));
        }

        [Fact]
        public void UninstallingVirindiTankLeavesTheAgent()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = InstallBoth(temp);

            UninstallResult result = Uninstall(temp, SetupProduct.VirindiTank, registry);

            Assert.Equal(new[] { SetupProduct.VirindiTank }, result.Uninstalled);
            Assert.Empty(result.LeftBehind);
            Assert.Empty(result.InUse);
            Assert.False(Directory.Exists(temp[@"Decal Agent\plugins\VirindiTank"]));
            Assert.False(File.Exists(temp[@"Decal Agent\VirindiTank.install.json"]));
            Assert.True(File.Exists(temp[@"Decal Agent\DecalAgent.exe"]));
            Assert.True(File.Exists(temp[@"Decal Agent\plugins\Decal.Compat\Decal.Compat.dll"]));
            Assert.True(File.Exists(temp[@"Start Menu\Decal Agent.lnk"]));
            Assert.False(registry.KeyExists(PluginKey));
            Assert.True(registry.KeyExists(AgentKey));
            Assert.Equal(temp["Decal Agent"], registry.GetValue(@"Software\Decal Agent", "InstallDir"));
        }

        [Fact]
        public void WhatThePlayerPutInVirindiTanksFolderIsNamedAndKept()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = InstallBoth(temp);
            File.WriteAllText(temp[@"Decal Agent\plugins\VirindiTank\notes.txt"], "mine");

            UninstallResult result = Uninstall(temp, SetupProduct.VirindiTank, registry);

            Assert.Equal(new[] { "notes.txt" }, result.LeftBehind);
            Assert.True(File.Exists(temp[@"Decal Agent\plugins\VirindiTank\notes.txt"]));
            Assert.False(File.Exists(temp[@"Decal Agent\plugins\VirindiTank\VirindiTank.Plugin.dll"]));
        }

        [Fact]
        public void VirindiTanksSettingsAreItsOwnFolderOfTheAgents()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = InstallBoth(temp);

            UninstallResult result = Uninstall(temp, SetupProduct.VirindiTank, registry, deleteSettings: true);

            Assert.True(result.SettingsDeleted);
            Assert.False(Directory.Exists(temp[@"ACHost\plugins\VirindiTank"]));
            Assert.True(File.Exists(temp[@"ACHost\DecalAgent.json"]));
        }

        [Fact]
        public void AnAppsEntryForAnotherFolderIsLeftAlone()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = InstallBoth(temp, plugin: false);

            // The real install, elsewhere, recorded after this trial one.
            string elsewhere = temp["Elsewhere"];
            registry.SetValue(AgentKey, "InstallLocation", elsewhere);
            registry.SetValue(@"Software\Decal Agent", "InstallDir", elsewhere);

            Uninstall(temp, SetupProduct.DecalAgent, registry);

            Assert.Equal(elsewhere, registry.GetValue(AgentKey, "InstallLocation"));
            Assert.Equal(elsewhere, registry.GetValue(@"Software\Decal Agent", "InstallDir"));
        }

        [Fact]
        public void WithoutARegistryTheEntriesStay()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = InstallBoth(temp, plugin: false);

            Uninstall(temp, SetupProduct.DecalAgent, registry: null);

            Assert.True(registry.KeyExists(AgentKey));
            Assert.False(Directory.Exists(temp["Decal Agent"]));
        }

        [Fact]
        public void FilesInUseAreLeftForLaterAndThenGoWithTheirFolders()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = InstallBoth(temp);
            string uninstaller = temp[@"Decal Agent\Uninstall.exe"];
            string native = temp[@"Decal Agent\plugins\Decal.Compat\native\sqlite3.dll"];

            UninstallResult result;
            using (new FileStream(uninstaller, FileMode.Open, FileAccess.Read, FileShare.None))
            using (new FileStream(native, FileMode.Open, FileAccess.Read, FileShare.None))
                result = Uninstall(temp, SetupProduct.DecalAgent, registry);

            Assert.Contains(uninstaller, result.InUse);
            Assert.Contains(native, result.InUse);

            // The list goes last, after what it lists.
            Assert.Equal(temp[@"Decal Agent\DecalAgent.install.json"], result.InUse[^1]);
            Assert.Equal(
                new[] { temp[@"Decal Agent\plugins\Decal.Compat\native"], temp[@"Decal Agent\plugins\Decal.Compat"], temp[@"Decal Agent\plugins"], temp["Decal Agent"] },
                result.FoldersToRemove);
            Assert.Empty(result.LeftBehind);
            Assert.False(File.Exists(temp[@"Decal Agent\DecalAgent.exe"]));

            // What the uninstaller hands on once it has exited.
            string script = DeferredDelete.Write(result.InUse, result.FoldersToRemove, temp.Path);
            using (Process run = DeferredDelete.Start(script))
                Assert.True(run.WaitForExit(30000));

            Assert.False(Directory.Exists(temp["Decal Agent"]));
            Assert.False(File.Exists(script));
        }

        [Fact]
        public void NothingListedNothingDeleted()
        {
            using TempFolder temp = new TempFolder();
            Directory.CreateDirectory(temp["Decal Agent"]);
            File.WriteAllText(temp[@"Decal Agent\DecalAgent.exe"], "someone else's");

            UninstallResult result = Uninstall(temp, SetupProduct.DecalAgent, new MemoryRegistry());

            Assert.Equal(0, result.Removed);
            Assert.True(File.Exists(temp[@"Decal Agent\DecalAgent.exe"]));
            Assert.Equal(new[] { "DecalAgent.exe" }, result.LeftBehind);
        }

        [Fact]
        public void KnowsWhatIsInstalledInAFolder()
        {
            using TempFolder temp = new TempFolder();
            InstallBoth(temp);

            Assert.Equal(new[] { SetupProduct.VirindiTank, SetupProduct.DecalAgent }, Uninstaller.InstalledIn(temp["Decal Agent"]).ToArray());
            Assert.Empty(Uninstaller.InstalledIn(temp.Path));
        }
    }
}
