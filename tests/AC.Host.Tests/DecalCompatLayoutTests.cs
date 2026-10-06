using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Decal.Compat is a plugin, loaded by path, so every assembly it runs on that the host does
    /// not supply must be laid out beside it - by the Decal Agent's build, which the installer
    /// publishes, and by achost's. The live install lacked Mono.Cecil.Rocks, and Global
    /// Inventory's SQLite was never widened.
    /// </summary>
    public class DecalCompatLayoutTests
    {
        /// <summary>The repository's root, found from where the tests run; null when they run elsewhere.</summary>
        private static string RepositoryRoot()
        {
            for (DirectoryInfo folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            {
                if (File.Exists(Path.Combine(folder.FullName, "VirindiTankUnreal.slnx")))
                    return folder.FullName;
            }

            return null;
        }

        /// <summary>
        /// Every runtime assembly of Decal.Compat's and of what it depends on, by file name, as its
        /// deps.json lists them - but the host's own AC.* assemblies, of which a plugin is always
        /// given the host's copy.
        /// </summary>
        private static List<string> RuntimeAssemblies(string depsJson)
        {
            using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(depsJson));
            JsonElement target = deps.RootElement.GetProperty("targets").EnumerateObject().First().Value;

            List<string> files = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<string> libraries = new Queue<string>(new[] { target.EnumerateObject().First(l => l.Name.StartsWith("Decal.Compat/", StringComparison.Ordinal)).Name });
            while (libraries.Count > 0)
            {
                string library = libraries.Dequeue();
                if (!seen.Add(library) || !target.TryGetProperty(library, out JsonElement entry))
                    continue;

                if (entry.TryGetProperty("runtime", out JsonElement runtime))
                {
                    foreach (JsonProperty asset in runtime.EnumerateObject())
                    {
                        string file = Path.GetFileName(asset.Name);
                        if (!file.StartsWith("AC.", StringComparison.Ordinal))
                            files.Add(file);
                    }
                }

                if (entry.TryGetProperty("dependencies", out JsonElement dependencies))
                {
                    foreach (JsonProperty dependency in dependencies.EnumerateObject())
                        libraries.Enqueue(dependency.Name + "/" + dependency.Value.GetString());
                }
            }

            return files;
        }

        /// <summary>The file names an item's Include patterns lay out - the folder dropped, wildcards kept - from a project file.</summary>
        private static List<Regex> LaidOut(string project, string item)
            => XDocument.Load(project).Descendants(item)
                .SelectMany(e => ((string)e.Attribute("Include") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(pattern => Path.GetFileName(pattern.Replace("$(DecalCompatFolder)", string.Empty, StringComparison.Ordinal)))
                .Select(name => new Regex("^" + Regex.Escape(name).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase))
                .ToList();

        [SkippableFact]
        public void EveryAssemblyDecalCompatRunsOnIsLaidOutBesideIt()
        {
            string root = RepositoryRoot();
            Skip.If(root == null, "The tests are not running from inside the repository.");

            // Decal.Compat as these tests were built against it: the same configuration, its own folder.
            DirectoryInfo output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            string compat = Path.Combine(root, "src", "Decal.Compat", "bin", output.Parent.Name, output.Name);
            string depsJson = Path.Combine(compat, "Decal.Compat.deps.json");
            Skip.IfNot(File.Exists(depsJson), $"Decal.Compat was not built to {compat}.");

            List<string> needed = RuntimeAssemblies(depsJson);
            Assert.Contains("Decal.Compat.dll", needed);
            Assert.Contains("Mono.Cecil.dll", needed);
            Assert.Contains("Mono.Cecil.Rocks.dll", needed);

            List<Regex> agent = LaidOut(Path.Combine(root, "src", "Decal.Agent", "Decal.Agent.csproj"), "DecalCompatFile");
            List<Regex> achost = LaidOut(Path.Combine(root, "src", "AC.Host.Cli", "AC.Host.Cli.csproj"), "DecalCompatFiles");
            foreach (string file in needed)
            {
                Assert.True(File.Exists(Path.Combine(compat, file)), $"Decal.Compat's build has no {file} to lay out.");
                Assert.True(agent.Any(pattern => pattern.IsMatch(file)), $"The Decal Agent's build does not lay out {file} beside Decal.Compat.");
                Assert.True(achost.Any(pattern => pattern.IsMatch(file)), $"achost's build does not lay out {file} beside Decal.Compat.");
            }

            // The installer checks what it publishes against the same list.
            string installers = File.ReadAllText(Path.Combine(root, "tools", "build-installers.ps1"));
            Assert.Contains("Decal.Compat.deps.json", installers);
            Assert.Contains(@"plugins\Decal.Compat\Mono.Cecil.Rocks.dll", installers);
        }
    }
}
