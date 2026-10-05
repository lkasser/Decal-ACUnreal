using System;
using System.IO;
using AC.Dat;
using Setup.Common;
using Xunit;

namespace Setup.Tests
{
    /// <summary>The smaller parts: the registry in memory, folders never to delete, finding the Agent and Virindi Tank, shortcuts.</summary>
    public class SetupSupportTests
    {
        [Fact]
        public void TheRegistryInMemoryDeletesAKeyWithWhatIsUnderIt()
        {
            MemoryRegistry registry = new MemoryRegistry();
            registry.SetValue(@"Software\Decal Agent", "InstallDir", @"C:\X");
            registry.SetValue(@"Software\Decal Agent\Sub", "Value", 1);
            registry.SetValue(@"Software\Decal Agent Other", "Value", "kept");

            registry.DeleteKey(@"Software\Decal Agent");

            Assert.False(registry.KeyExists(@"Software\Decal Agent"));
            Assert.False(registry.KeyExists(@"Software\Decal Agent\Sub"));
            Assert.Equal("kept", registry.GetValue(@"Software\Decal Agent Other", "Value"));
            Assert.Throws<ArgumentException>(() => registry.SetValue("Key", "Name", 1.5));
        }

        [Fact]
        public void FoldersWindowsKeepsThingsInAreNeverDeletedWhole()
        {
            Assert.False(Paths.IsSafeToDeleteTree(@"C:\"));
            Assert.False(Paths.IsSafeToDeleteTree("relative"));
            Assert.False(Paths.IsSafeToDeleteTree(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            Assert.False(Paths.IsSafeToDeleteTree(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\"));
            Assert.False(Paths.IsSafeToDeleteTree(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")));
            Assert.False(Paths.IsSafeToDeleteTree(Path.GetTempPath()));

            Assert.True(Paths.IsSafeToDeleteTree(AgentFolder.DefaultDataFolder));
            Assert.True(Paths.IsSafeToDeleteTree(AgentFolder.DefaultFolder));
        }

        [Fact]
        public void PathsCompareAsWindowsComparesThem()
        {
            Assert.True(Paths.Same(@"C:\Games\Decal Agent\", @"c:\games\decal agent"));
            Assert.True(Paths.IsWithin(@"C:\Games\Decal Agent\DecalAgent.exe", @"C:\Games\Decal Agent"));
            Assert.False(Paths.IsWithin(@"C:\Games\Decal Agent 2\DecalAgent.exe", @"C:\Games\Decal Agent"));
        }

        [Fact]
        public void TheAgentIsFoundWhereItsSetupSaidOnlyIfItIsStillThere()
        {
            using TempFolder temp = new TempFolder();
            MemoryRegistry registry = new MemoryRegistry();
            Assert.Null(AgentFolder.Find(registry));
            Assert.Null(AgentFolder.Find(null));

            registry.SetValue(AgentFolder.RegistryKey, AgentFolder.InstallDirValue, temp.Path);
            Assert.Null(AgentFolder.Find(registry));

            File.WriteAllText(temp["DecalAgent.exe"], "agent");
            Assert.Equal(temp.Path, AgentFolder.Find(registry));
        }

        [Fact]
        public void VirindiTankIsFoundAsThePluginFindsItThroughDecalsRegistry()
        {
            using TempFolder temp = new TempFolder();
            FakeDecalRegistry decal = new FakeDecalRegistry();
            Assert.Null(VirindiTankInstall.FindRegistered(decal));

            decal.Plugins.Add(new DecalRegistryEntry("{642F1F48-16BE-48BF-B1D4-286652C4533E}", "Virindi Tank", temp.Path, "utank2-i.dll"));
            Assert.Null(VirindiTankInstall.FindRegistered(decal));

            File.WriteAllText(temp["utank2-i.dll"], "not really");
            Assert.Equal(temp["utank2-i.dll"], VirindiTankInstall.FindRegistered(decal));
        }

        [Fact]
        public void APluginFileIsTheAgentsWhenADllOfItsNameIsBesideTheAgent()
        {
            using TempFolder temp = new TempFolder();
            File.WriteAllText(temp["AC.Host.dll"], "host");

            Assert.True(VirindiTankInstall.IsSuppliedByAgent("AC.Host.dll", temp.Path));
            Assert.True(VirindiTankInstall.IsSuppliedByAgent("AC.Host.pdb", temp.Path));
            Assert.False(VirindiTankInstall.IsSuppliedByAgent("VirindiTank.Plugin.dll", temp.Path));
        }

        [Fact]
        public void TheShellMakesAShortcutThatStartsTheAgent()
        {
            using TempFolder temp = new TempFolder();
            string target = temp[@"Decal Agent\DecalAgent.exe"];
            Directory.CreateDirectory(temp["Decal Agent"]);
            File.WriteAllText(target, "agent");

            new ShellShortcuts().Create(new Shortcut { Path = temp[@"Start Menu\Decal Agent.lnk"], Target = target });

            Assert.True(File.Exists(temp[@"Start Menu\Decal Agent.lnk"]));
            Assert.True(Paths.Same(target, ShellShortcuts.ReadTarget(temp[@"Start Menu\Decal Agent.lnk"])));
        }

        [Fact]
        public void AProgramIsRunningFromAFolderOnlyIfItsFileIsThere()
        {
            string host = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            Assert.NotEmpty(RunningPrograms.In(Path.GetDirectoryName(Environment.ProcessPath), host));
            using TempFolder temp = new TempFolder();
            Assert.Empty(RunningPrograms.In(temp.Path, host));
            Assert.False(RunningPrograms.AgentRunningIn(temp.Path));
        }

        [Fact]
        public void TheVersionLeavesOutTheCommit()
        {
            Assert.DoesNotContain("+", SetupVersion.Of(typeof(SetupVersion).Assembly));
        }
    }
}
