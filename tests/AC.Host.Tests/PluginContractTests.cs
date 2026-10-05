using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AC.Host.Plugins;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// The contract gate. A plugin built against a host that no longer exists loads
    /// perfectly well and then throws the first time the host calls something that was
    /// added since - and for a host that injects into a running game, that happens in the
    /// worst place at the worst moment. So the check happens before anything is
    /// instantiated, and these tests use real assemblies built to fail it rather than
    /// mocks, because the thing being tested is what the loader reads out of a file.
    /// </summary>
    public class PluginContractTests
    {
        /// <summary>
        /// Lays out a plugin directory the way the loader expects to find one, copying a
        /// built assembly and whatever it needs beside it.
        /// </summary>
        private static string StagePlugin(string assemblyName)
        {
            string root = Path.Combine(Path.GetTempPath(), "achost-contract-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, assemblyName);
            Directory.CreateDirectory(directory);

            string source = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            Assert.True(
                File.Exists(source),
                $"{assemblyName}.dll is not beside the tests. It should arrive as a project reference.");

            File.Copy(source, Path.Combine(directory, assemblyName + ".dll"));

            string deps = Path.Combine(AppContext.BaseDirectory, assemblyName + ".deps.json");
            if (File.Exists(deps))
                File.Copy(deps, Path.Combine(directory, assemblyName + ".deps.json"));

            return root;
        }

        [Fact]
        public void APluginThatDeclaresNoContractVersionIsRefusedAndToldWhatToAdd()
        {
            string root = StagePlugin("Unversioned.Plugin");
            ListLog log = new ListLog();

            IReadOnlyList<LoadedPlugin> loaded = PluginLoader.LoadFrom(root, log);

            Assert.Empty(loaded);

            string complaint = Assert.Single(log.Lines.Where(l => l.StartsWith("ERROR")));
            Assert.Contains("does not declare", complaint);

            // The message has to carry the fix, not just the fault: an author reading it
            // should not have to go looking for the attribute's name.
            Assert.Contains("PluginApi", complaint);
        }

        [Fact]
        public void APluginNewerThanTheHostIsRefusedAndTheHostIsBlamed()
        {
            string root = StagePlugin("TooNew.Plugin");
            ListLog log = new ListLog();

            IReadOnlyList<LoadedPlugin> loaded = PluginLoader.LoadFrom(root, log);

            Assert.Empty(loaded);

            string complaint = Assert.Single(log.Lines.Where(l => l.StartsWith("ERROR")));
            Assert.Contains("999", complaint);
            Assert.Contains("update the host", complaint);
        }

        /// <remarks>
        /// The Virindi Tank repository runs the same check against its plugin, which carries
        /// dependencies of its own beside it.
        /// </remarks>
        [Fact]
        public void APluginBuiltAgainstThisHostLoadsAndReportsItsVersion()
        {
            string root = StagePlugin("Counting.Plugin");

            ListLog log = new ListLog();
            IReadOnlyList<LoadedPlugin> loaded = PluginLoader.LoadFrom(root, log);

            LoadedPlugin plugin = Assert.Single(loaded);
            Assert.Equal("Counting", plugin.Plugin.Name);
            Assert.Equal(HostApi.Version, plugin.ApiVersion);
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("ERROR"));
        }

        /// <summary>
        /// A plugin directory is mostly dependencies. Complaining about each one that
        /// happens not to contain a plugin would bury the one message that matters.
        /// </summary>
        [Fact]
        public void AnAssemblyWithNoPluginInItIsPassedOverInSilence()
        {
            string root = Path.Combine(Path.GetTempPath(), "achost-contract-" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "Quiet");
            Directory.CreateDirectory(directory);

            // AC.Protocol has no plugin in it and declares no contract version, which is
            // exactly the shape of an ordinary dependency.
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "AC.Protocol.dll"),
                Path.Combine(directory, "AC.Protocol.dll"));

            ListLog log = new ListLog();
            IReadOnlyList<LoadedPlugin> loaded = PluginLoader.LoadFrom(root, log);

            Assert.Empty(loaded);
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("ERROR"));
        }

        [Fact]
        public void TheHostsOwnMinimumIsNotAboveItsCurrentVersion()
        {
            // A guard against a careless bump: if the minimum ever exceeded the current
            // version, every plugin would be refused and the message would blame them.
            Assert.True(
                HostApi.MinimumSupported <= HostApi.Version,
                "HostApi.MinimumSupported must not be greater than HostApi.Version.");
        }
    }
}
