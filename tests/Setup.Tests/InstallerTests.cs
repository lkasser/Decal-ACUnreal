using System;
using System.IO;
using System.Linq;
using Setup.Common;
using Xunit;

namespace Setup.Tests
{
    /// <summary>Installing Decal Agent and Virindi Tank into folders of the test's own, with a registry in memory.</summary>
    public class InstallerTests
    {
        private static InstallRequest AgentRequest(TempFolder temp, Payload payload, MemoryRegistry registry = null, FakeShortcuts shortcuts = null, bool desktop = false)
            => new InstallRequest
            {
                Product = SetupProduct.DecalAgent,
                Payload = payload,
                AgentFolder = temp["Decal Agent"],
                Version = "1.0.0",
                Registry = registry,
                Shortcuts = shortcuts,
                StartMenuFolder = shortcuts == null ? null : temp["Start Menu"],
                DesktopFolder = shortcuts != null && desktop ? temp["Desktop"] : null,
            };

        private static InstallRequest PluginRequest(TempFolder temp, Payload payload, MemoryRegistry registry = null)
        {
            string agent = temp["Decal Agent"];
            return new InstallRequest
            {
                Product = SetupProduct.VirindiTank,
                Payload = payload,
                AgentFolder = agent,
                Version = "1.0.0",
                Registry = registry,
                LeaveOut = file => VirindiTankInstall.IsSuppliedByAgent(file, agent),
            };
        }

        [Fact]
        public void InstallsTheAgentsFilesAndListsThem()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Agent();

            InstallResult result = Installer.Install(AgentRequest(temp, payload));

            Assert.Equal(payload.Files.Count, result.Installed);
            Assert.False(result.Replaced);
            Assert.Equal("agent 1", File.ReadAllText(temp[@"Decal Agent\DecalAgent.exe"]));
            Assert.Equal("sqlite", File.ReadAllText(temp[@"Decal Agent\plugins\Decal.Compat\native\sqlite3.dll"]));

            InstallManifest manifest = InstallManifest.Load(temp["Decal Agent"], SetupProduct.DecalAgent);
            Assert.NotNull(manifest);
            Assert.Equal("1.0.0", manifest.Version);
            Assert.Contains(@"plugins\Decal.Compat\Decal.Compat.dll", manifest.Files);
            Assert.Equal(payload.Files.Count, manifest.Files.Count);
            Assert.False(manifest.Registered);
        }

        [Fact]
        public void RegistersTheAgentInAppsAndSaysWhereItIs()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Agent();
            MemoryRegistry registry = new MemoryRegistry();

            InstallResult result = Installer.Install(AgentRequest(temp, payload, registry));

            string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DecalAgent";
            string folder = temp["Decal Agent"];
            Assert.True(result.Registered);
            Assert.Equal("Decal Agent", registry.GetValue(key, "DisplayName"));
            Assert.Equal("1.0.0", registry.GetValue(key, "DisplayVersion"));
            Assert.Equal(folder, registry.GetValue(key, "InstallLocation"));
            Assert.Equal("\"" + Path.Combine(folder, "Uninstall.exe") + "\"", registry.GetValue(key, "UninstallString"));
            Assert.Equal("\"" + Path.Combine(folder, "Uninstall.exe") + "\" /S", registry.GetValue(key, "QuietUninstallString"));
            Assert.Equal(1, registry.GetValue(key, "NoModify"));
            Assert.IsType<int>(registry.GetValue(key, "EstimatedSize"));
            Assert.Equal(folder, registry.GetValue(@"Software\Decal Agent", "InstallDir"));
            Assert.Equal(folder, AgentFolder.Find(registry));

            // Only ever HKEY_CURRENT_USER's own keys - nothing of Decal's.
            Assert.All(registry.Keys, written => Assert.DoesNotContain(@"Software\Decal\", written + "\\", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void MakesTheShortcutsAskedFor()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Agent();
            FakeShortcuts shortcuts = new FakeShortcuts();

            InstallResult result = Installer.Install(AgentRequest(temp, payload, shortcuts: shortcuts, desktop: true));

            Assert.Equal(2, shortcuts.Made.Count);
            Assert.All(shortcuts.Made, made => Assert.Equal(Path.Combine(temp["Decal Agent"], "DecalAgent.exe"), made.Target));
            Assert.True(File.Exists(temp[@"Start Menu\Decal Agent.lnk"]));
            Assert.True(File.Exists(temp[@"Desktop\Decal Agent.lnk"]));
            Assert.Equal(result.Shortcuts, InstallManifest.Load(temp["Decal Agent"], SetupProduct.DecalAgent).Shortcuts);
        }

        [Fact]
        public void AReinstallWithoutTheDesktopShortcutTakesItAway()
        {
            using TempFolder temp = new TempFolder();
            using (Payload first = Payloads.Agent())
                Installer.Install(AgentRequest(temp, first, shortcuts: new FakeShortcuts(), desktop: true));

            using (Payload second = Payloads.Agent())
                Installer.Install(AgentRequest(temp, second, shortcuts: new FakeShortcuts(), desktop: false));

            Assert.True(File.Exists(temp[@"Start Menu\Decal Agent.lnk"]));
            Assert.False(File.Exists(temp[@"Desktop\Decal Agent.lnk"]));
        }

        [Fact]
        public void WithoutShortcutsOrRegistryNothingIsWrittenOutsideTheFolder()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Agent();

            InstallResult result = Installer.Install(AgentRequest(temp, payload));

            Assert.Empty(result.Shortcuts);
            Assert.False(result.Registered);
            Assert.Equal(new[] { "Decal Agent" }, Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));
        }

        [Fact]
        public void AnUpgradeReplacesTheProgramAndDeletesWhatTheOldVersionAloneHad()
        {
            using TempFolder temp = new TempFolder();
            using (Payload first = Payloads.Of(("DecalAgent.exe", "v1"), ("Old.dll", "old"), ("plugins/Gone/Gone.dll", "gone"), ("Uninstall.exe", "u")))
                Installer.Install(AgentRequest(temp, first));

            // What the player added, and their settings in the data folder: neither the setup's.
            Directory.CreateDirectory(temp[@"Decal Agent\plugins\Mine"]);
            File.WriteAllText(temp[@"Decal Agent\plugins\Mine\Mine.dll"], "mine");
            Directory.CreateDirectory(temp["ACHost"]);
            File.WriteAllText(temp[@"ACHost\DecalAgent.json"], "{}");

            InstallResult result;
            using (Payload second = Payloads.Of(("DecalAgent.exe", "v2"), ("New.dll", "new"), ("Uninstall.exe", "u")))
                result = Installer.Install(AgentRequest(temp, second));

            Assert.True(result.Replaced);
            Assert.Equal(2, result.Removed);
            Assert.Equal("v2", File.ReadAllText(temp[@"Decal Agent\DecalAgent.exe"]));
            Assert.True(File.Exists(temp[@"Decal Agent\New.dll"]));
            Assert.False(File.Exists(temp[@"Decal Agent\Old.dll"]));
            Assert.False(Directory.Exists(temp[@"Decal Agent\plugins\Gone"]));
            Assert.True(File.Exists(temp[@"Decal Agent\plugins\Mine\Mine.dll"]));
            Assert.True(File.Exists(temp[@"ACHost\DecalAgent.json"]));
            Assert.DoesNotContain("Old.dll", InstallManifest.Load(temp["Decal Agent"], SetupProduct.DecalAgent).Files);
        }

        /// <summary>
        /// tools\update-agent.ps1 runs both setups between the Agent that stops and the one that
        /// starts. What the first left in the data folder for the second - the session it handed
        /// over, the copies its plugins ran from - is not the setups' business: every byte and every
        /// time on it is as it was.
        /// </summary>
        [Fact]
        public void AnUpdateLeavesWhatTheStoppedAgentLeftInTheDataFolderAlone()
        {
            using TempFolder temp = new TempFolder();
            using (Payload agent = Payloads.Agent("1"))
                Installer.Install(AgentRequest(temp, agent));
            using (Payload plugin = Payloads.VirindiTank("1"))
                Installer.Install(PluginRequest(temp, plugin));

            Directory.CreateDirectory(temp[@"ACHost\running\31256\VirindiTank.Plugin-b80736d26f2b"]);
            File.WriteAllText(temp[@"ACHost\running\31256\VirindiTank.Plugin-b80736d26f2b\VirindiTank.Plugin.dll"], "a copy");
            File.WriteAllBytes(temp[@"ACHost\handover-9100.bin"], new byte[] { 0x41, 0x43, 0x48, 0x41, 0x4E, 0x44, 0x4F, 0x56, 1, 0, 0, 0 });
            DateTime written = new DateTime(2026, 10, 5, 13, 46, 47, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(temp[@"ACHost\handover-9100.bin"], written);
            Directory.SetLastWriteTimeUtc(temp[@"ACHost\running"], written);
            Directory.SetLastWriteTimeUtc(temp["ACHost"], written);

            using (Payload agent = Payloads.Agent("2"))
                Installer.Install(AgentRequest(temp, agent));
            using (Payload plugin = Payloads.VirindiTank("2"))
                Installer.Install(PluginRequest(temp, plugin));

            Assert.Equal("agent 2", File.ReadAllText(temp[@"Decal Agent\DecalAgent.exe"]));
            Assert.Equal(new byte[] { 0x41, 0x43, 0x48, 0x41, 0x4E, 0x44, 0x4F, 0x56, 1, 0, 0, 0 }, File.ReadAllBytes(temp[@"ACHost\handover-9100.bin"]));
            Assert.Equal(written, File.GetLastWriteTimeUtc(temp[@"ACHost\handover-9100.bin"]));
            Assert.Equal(written, Directory.GetLastWriteTimeUtc(temp[@"ACHost\running"]));
            Assert.Equal(written, Directory.GetLastWriteTimeUtc(temp["ACHost"]));
            Assert.Equal("a copy", File.ReadAllText(temp[@"ACHost\running\31256\VirindiTank.Plugin-b80736d26f2b\VirindiTank.Plugin.dll"]));
            Assert.Equal(new[] { "handover-9100.bin", "running" }, Directory.GetFileSystemEntries(temp["ACHost"]).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void AFileInUseStopsTheInstallSayingSoAndTheListStillCoversEverything()
        {
            using TempFolder temp = new TempFolder();
            using (Payload first = Payloads.Of(("DecalAgent.exe", "v1"), ("Old.dll", "old")))
                Installer.Install(AgentRequest(temp, first));

            using FileStream held = new FileStream(temp[@"Decal Agent\DecalAgent.exe"], FileMode.Open, FileAccess.Read, FileShare.None);
            using Payload second = Payloads.Of(("DecalAgent.exe", "v2"), ("New.dll", "new"));

            SetupException refused = Assert.Throws<SetupException>(() => Installer.Install(AgentRequest(temp, second)));
            Assert.Equal(SetupExitCode.AgentRunning, refused.ExitCode);
            Assert.Contains("DecalAgent.exe", refused.Message);

            // The old and the new, so an uninstall after a half-done install misses nothing.
            InstallManifest manifest = InstallManifest.Load(temp["Decal Agent"], SetupProduct.DecalAgent);
            Assert.Contains("Old.dll", manifest.Files);
            Assert.Contains("New.dll", manifest.Files);
        }

        /// <summary>
        /// The overlay is loaded in the running game while the Agent is upgraded: the new one is
        /// written beside the old, which the next install deletes once the game has let it go.
        /// </summary>
        [Fact]
        public void AnUpgradeWhileTheGameHoldsTheOverlayPutsTheNewOneInPlace()
        {
            using TempFolder temp = new TempFolder();
            using (Payload first = Payloads.Of(("DecalAgent.exe", "v1"), ("ACUnrealOverlay.dll", "overlay 1"), ("Same.dll", "same")))
                Installer.Install(AgentRequest(temp, first));

            string overlay = temp[@"Decal Agent\ACUnrealOverlay.dll"];
            string aside = Payload.SetAsideName(overlay, 0);
            using (new FileStream(overlay, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (new FileStream(temp[@"Decal Agent\Same.dll"], FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (Payload second = Payloads.Of(("DecalAgent.exe", "v2"), ("ACUnrealOverlay.dll", "overlay 2"), ("Same.dll", "same")))
                Installer.Install(AgentRequest(temp, second));

            Assert.Equal("overlay 2", File.ReadAllText(overlay));
            Assert.Equal("overlay 1", File.ReadAllText(aside));
            Assert.Contains("ACUnrealOverlay.dll" + Payload.SetAsideSuffix, InstallManifest.Load(temp["Decal Agent"], SetupProduct.DecalAgent).Files);

            InstallResult third;
            using (Payload again = Payloads.Of(("DecalAgent.exe", "v3"), ("ACUnrealOverlay.dll", "overlay 2"), ("Same.dll", "same")))
                third = Installer.Install(AgentRequest(temp, again));

            Assert.False(File.Exists(aside));
            Assert.Equal(1, third.Removed);
            Assert.DoesNotContain("ACUnrealOverlay.dll" + Payload.SetAsideSuffix, InstallManifest.Load(temp["Decal Agent"], SetupProduct.DecalAgent).Files);
        }

        [Fact]
        public void AnOldFileThatCannotBeDeletedStaysOnTheList()
        {
            using TempFolder temp = new TempFolder();
            using (Payload first = Payloads.Of(("DecalAgent.exe", "v1"), ("Old.dll", "old")))
                Installer.Install(AgentRequest(temp, first));

            InstallResult result;
            using (new FileStream(temp[@"Decal Agent\Old.dll"], FileMode.Open, FileAccess.Read, FileShare.None))
            using (Payload second = Payloads.Of(("DecalAgent.exe", "v2")))
                result = Installer.Install(AgentRequest(temp, second));

            Assert.Equal(0, result.Removed);
            Assert.Contains("Old.dll", InstallManifest.Load(temp["Decal Agent"], SetupProduct.DecalAgent).Files);
        }

        [Fact]
        public void ReplacesAReadOnlyFile()
        {
            using TempFolder temp = new TempFolder();
            using (Payload first = Payloads.Of(("DecalAgent.exe", "v1")))
                Installer.Install(AgentRequest(temp, first));
            File.SetAttributes(temp[@"Decal Agent\DecalAgent.exe"], FileAttributes.ReadOnly);

            using (Payload second = Payloads.Of(("DecalAgent.exe", "v2")))
                Installer.Install(AgentRequest(temp, second));

            Assert.Equal("v2", File.ReadAllText(temp[@"Decal Agent\DecalAgent.exe"]));
        }

        [Fact]
        public void RefusesAFolderWindowsKeepsOtherThingsIn()
        {
            using Payload payload = Payloads.Agent();

            // %TEMP% itself, refused before anything is written there - as %LOCALAPPDATA%, the
            // desktop or a drive's root would be, which a test has no business trying.
            string temp = Path.GetTempPath();
            Assert.Throws<SetupException>(() => Installer.Install(new InstallRequest { Product = SetupProduct.DecalAgent, Payload = payload, AgentFolder = temp }));
            Assert.False(File.Exists(Path.Combine(temp, SetupProduct.DecalAgent.ManifestName)));
        }

        [Fact]
        public void WritesNoAppsEntryWithoutAnUninstallerToRunIt()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Of(("DecalAgent.exe", "v1"));
            MemoryRegistry registry = new MemoryRegistry();

            InstallResult result = Installer.Install(AgentRequest(temp, payload, registry));

            Assert.False(result.Registered);
            Assert.Empty(registry.Keys);
        }

        [Fact]
        public void VirindiTankGoesIntoThePluginsFolderWithoutTheHostsOwnFiles()
        {
            using TempFolder temp = new TempFolder();
            using (Payload agent = Payloads.Agent())
                Installer.Install(AgentRequest(temp, agent));

            using Payload plugin = Payloads.VirindiTank();
            InstallResult result = Installer.Install(PluginRequest(temp, plugin));

            Assert.Equal(temp[@"Decal Agent\plugins\VirindiTank"], result.ProductFolder);
            Assert.True(File.Exists(temp[@"Decal Agent\plugins\VirindiTank\VirindiTank.Plugin.dll"]));
            Assert.True(File.Exists(temp[@"Decal Agent\plugins\VirindiTank\VTClassic.dll"]));

            // AC.Host.dll is the Agent's; neither it nor its symbols go beside the plugin.
            Assert.Equal(new[] { "AC.Host.dll", "AC.Host.pdb" }, result.LeftOut.OrderBy(name => name));
            Assert.False(File.Exists(temp[@"Decal Agent\plugins\VirindiTank\AC.Host.dll"]));
            Assert.Equal("host 1", File.ReadAllText(temp[@"Decal Agent\AC.Host.dll"]));

            InstallManifest manifest = InstallManifest.Load(temp["Decal Agent"], SetupProduct.VirindiTank);
            Assert.Contains(@"plugins\VirindiTank\VirindiTank.Plugin.dll", manifest.Files);
            Assert.True(File.Exists(temp[@"Decal Agent\VirindiTank.install.json"]));
        }

        [Fact]
        public void VirindiTanksAppsEntryIsUninstalledByTheAgentsUninstaller()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = new MemoryRegistry();
            using (Payload agent = Payloads.Agent())
                Installer.Install(AgentRequest(temp, agent, registry));

            using Payload plugin = Payloads.VirindiTank();
            Installer.Install(PluginRequest(temp, plugin, registry));

            string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DecalAgent.VirindiTank";
            string folder = temp["Decal Agent"];
            Assert.Equal("Virindi Tank for Decal Agent", registry.GetValue(key, "DisplayName"));
            Assert.Equal(Path.Combine(folder, @"plugins\VirindiTank"), registry.GetValue(key, "InstallLocation"));
            Assert.Equal("\"" + Path.Combine(folder, "Uninstall.exe") + "\" /Product=VirindiTank", registry.GetValue(key, "UninstallString"));
            Assert.Equal(folder, registry.GetValue(@"Software\Decal Agent", "InstallDir"));
        }

        [Fact]
        public void ReinstallingTheAgentLeavesVirindiTankInPlace()
        {
            using TempFolder temp = new TempFolder();
            using (Payload agent = Payloads.Agent())
                Installer.Install(AgentRequest(temp, agent));
            using (Payload plugin = Payloads.VirindiTank())
                Installer.Install(PluginRequest(temp, plugin));

            using (Payload agent = Payloads.Agent("2"))
                Installer.Install(AgentRequest(temp, agent));

            Assert.Equal("agent 2", File.ReadAllText(temp[@"Decal Agent\DecalAgent.exe"]));
            Assert.Equal("vt 1", File.ReadAllText(temp[@"Decal Agent\plugins\VirindiTank\VirindiTank.Plugin.dll"]));
        }

        [Fact]
        public void ReportsProgressFileByFile()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Agent();
            RecordingProgress progress = new RecordingProgress();

            Installer.Install(AgentRequest(temp, payload), progress);

            Assert.Equal(payload.Files.Count + 1, progress.Reports.Count);
            Assert.Equal(new SetupProgress(payload.Files.Count, payload.Files.Count, null), progress.Reports[^1]);
        }

        private sealed class RecordingProgress : IProgress<SetupProgress>
        {
            public System.Collections.Generic.List<SetupProgress> Reports { get; } = new System.Collections.Generic.List<SetupProgress>();

            public void Report(SetupProgress value) => Reports.Add(value);
        }
    }
}
