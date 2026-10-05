using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using AC.Dat;
using Setup.Common;

namespace Setup.Tests
{
    /// <summary>
    /// A folder of a test's own under %TEMP%, deleted afterwards: where every install, shortcut
    /// and settings folder in these tests goes, so none reaches the machine's real ones.
    /// </summary>
    internal sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "setup-tests-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string this[string relative] => System.IO.Path.Combine(Path, relative);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Something still holds a file; %TEMP% will have it.
            }
        }
    }

    /// <summary>Payloads made in memory, as the build makes them on disk: a zip of the folder as installed.</summary>
    internal static class Payloads
    {
        public static Payload Of(params (string Name, string Content)[] files)
        {
            MemoryStream stream = new MemoryStream();
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string name, string content) in files)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(name);
                    using Stream written = entry.Open();
                    byte[] bytes = Encoding.UTF8.GetBytes(content);
                    written.Write(bytes, 0, bytes.Length);
                }
            }

            stream.Position = 0;
            return new Payload(stream);
        }

        /// <summary>A Decal Agent, in miniature: the program, a runtime file, its uninstaller and Decal.Compat.</summary>
        public static Payload Agent(string version = "1")
            => Of(
                ("DecalAgent.exe", "agent " + version),
                ("DecalAgent.dll", "agent " + version),
                ("AC.Host.dll", "host " + version),
                ("System.Runtime.dll", "runtime"),
                ("Uninstall.exe", "uninstaller"),
                ("ACUnrealOverlay.dll", "overlay"),
                ("acinject.exe", "injector"),
                ("plugins/Decal.Compat/Decal.Compat.dll", "compat " + version),
                ("plugins/Decal.Compat/native/sqlite3.dll", "sqlite"));

        /// <summary>The Virindi Tank plugin, in miniature, with a copy of the host's contract it must not install.</summary>
        public static Payload VirindiTank(string version = "1")
            => Of(
                ("VirindiTank.Plugin.dll", "vt " + version),
                ("VirindiTank.Plugin.deps.json", "{}"),
                ("VTClassic.dll", "vtclassic"),
                ("AC.Host.dll", "a second host"),
                ("AC.Host.pdb", "its symbols"));
    }

    /// <summary>Shortcuts as small files, so a test can see them made and deleted without the shell.</summary>
    internal sealed class FakeShortcuts : IShortcutMaker
    {
        public List<Shortcut> Made { get; } = new List<Shortcut>();

        public void Create(Shortcut shortcut)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(shortcut.Path));
            File.WriteAllText(shortcut.Path, shortcut.Target);
            Made.Add(shortcut);
        }
    }

    /// <summary>Decal's registry as a test says it is; nothing is read from the machine's.</summary>
    internal sealed class FakeDecalRegistry : IDecalRegistry
    {
        public List<DecalRegistryEntry> Plugins { get; } = new List<DecalRegistryEntry>();

        public bool TryReadAgent(out string agentPath, out string portalPath)
        {
            agentPath = null;
            portalPath = null;
            return false;
        }

        public IReadOnlyList<DecalRegistryEntry> ReadPlugins() => Plugins;

        public IReadOnlyList<DecalRegistryEntry> ReadServices() => Array.Empty<DecalRegistryEntry>();
    }
}
