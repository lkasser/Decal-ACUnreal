using System;
using System.IO;
using Decal.Compat;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// A Decal plugin's native DLL: its own when built for this process, else a replacement from
    /// Decal Compat's native folder - the 64-bit sqlite3.dll Virindi's tools need in place of the
    /// 32-bit one they ship.
    /// </summary>
    public class DecalNativeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "achost-native-" + Guid.NewGuid().ToString("N"));

        public DecalNativeTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "plugin"));
            Directory.CreateDirectory(Path.Combine(_root, "native"));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        // Windows' own version.dll, in both kinds, standing in for sqlite3.dll.
        private static string Native32 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "version.dll");

        private static string Native64 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");

        [Fact]
        public void APluginsWrongKindOfDllIsReplacedFromTheNativeFolder()
        {
            if (!Environment.Is64BitProcess || !File.Exists(Native32) || !File.Exists(Native64))
                return;

            string plugin = Path.Combine(_root, "plugin");
            string native = Path.Combine(_root, "native");
            File.Copy(Native32, Path.Combine(plugin, "sqlite3.dll"));

            // Nothing to put in its place: passed over, and said why.
            Assert.Null(DecalPluginLoadContext.ResolveNative("sqlite3", plugin, new[] { native }, out string problem));
            Assert.Contains("32-bit", problem);
            Assert.Contains(native, problem);

            // A 64-bit one in the native folder is used instead.
            File.Copy(Native64, Path.Combine(native, "sqlite3.dll"));
            Assert.Equal(Path.Combine(native, "sqlite3.dll"), DecalPluginLoadContext.ResolveNative("sqlite3", plugin, new[] { native }, out problem));
            Assert.Null(problem);

            // A plugin with none of its own - Global Inventory - is given it too.
            File.Delete(Path.Combine(plugin, "sqlite3.dll"));
            Assert.Equal(Path.Combine(native, "sqlite3.dll"), DecalPluginLoadContext.ResolveNative("sqlite3.dll", plugin, new[] { native }, out _));

            // Its own, when it is the right kind, comes first.
            File.Copy(Native64, Path.Combine(plugin, "sqlite3.dll"));
            Assert.Equal(Path.Combine(plugin, "sqlite3.dll"), DecalPluginLoadContext.ResolveNative("sqlite3", plugin, new[] { native }, out _));
        }
    }
}
