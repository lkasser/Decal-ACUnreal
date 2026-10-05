using System;
using System.IO;
using System.Linq;
using Decal.Adapter.Hosting;
using Decal.Compat;
using Microsoft.Win32;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// A registered Decal plugin's reads of HKEY_LOCAL_MACHINE, pointed at the registry 32-bit
    /// Decal showed it (<see cref="RegistryRewrite"/>, <see cref="PluginRegistry"/>).
    /// </summary>
    public class DecalRegistryRewriteTests
    {
        private static string Troubled => Path.Combine(AppContext.BaseDirectory, "decal-registered", "TroubledPlugin", "TroubledPlugin.dll");

        /// <summary>
        /// The troubled plugin reads its Path as Integrator2 does, through Registry.LocalMachine:
        /// the field load and both registry calls become calls into the shim, named through its own
        /// reference to Decal.Adapter, and nothing else in it changes.
        /// </summary>
        [Fact]
        public void APluginsRegistryReadsAreRedirectedToTheShim()
        {
            byte[] original = File.ReadAllBytes(Troubled);
            byte[] rewritten = RegistryRewrite.Rewrite(original, out bool changed, out string problem);

            Assert.True(changed);
            Assert.Null(problem);

            using ModuleDefinition before = ModuleDefinition.ReadModule(new MemoryStream(original));
            using ModuleDefinition after = ModuleDefinition.ReadModule(new MemoryStream(rewritten));
            Instruction[] code = after.Types.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();

            Assert.DoesNotContain(code, i => i.Operand is FieldReference f && f.Name == "LocalMachine");
            Assert.Contains(code, i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m
                                       && m.DeclaringType.FullName == "Decal.Adapter.Hosting.PluginRegistry" && m.Name == "LocalMachine");
            Assert.Contains(code, i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m
                                       && m.DeclaringType.FullName == "Decal.Adapter.Hosting.PluginRegistry" && m.Name == "GetValue" && m.Parameters.Count == 2);
            Assert.Equal(before.AssemblyReferences.Select(a => a.Name).OrderBy(n => n), after.AssemblyReferences.Select(a => a.Name).OrderBy(n => n));
        }

        [Fact]
        public void AnAssemblyThatNeverTouchesTheRegistryOrIsNativeIsLeftAsItIs()
        {
            byte[] plain = File.ReadAllBytes(typeof(Assert).Assembly.Location);
            Assert.Same(plain, RegistryRewrite.Rewrite(plain, out bool changed, out string problem));
            Assert.False(changed);
            Assert.Null(problem);

            string native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
            if (File.Exists(native))
            {
                byte[] image = File.ReadAllBytes(native);
                Assert.Same(image, RegistryRewrite.Rewrite(image, out changed, out problem));
                Assert.Null(problem);
            }
        }

        /// <summary>HKEY_LOCAL_MACHINE is the 32-bit view, as Decal's process saw it.</summary>
        [Fact]
        public void LocalMachineIsTheThirtyTwoBitView()
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
                return;

            using RegistryKey root = PluginRegistry.LocalMachine();
            using RegistryKey current = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion");
            object programs = PluginRegistry.GetValue(current, "ProgramFilesDir");
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), programs);
            Assert.Equal(programs, PluginRegistry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion", "ProgramFilesDir", null));
        }

        /// <summary>
        /// A registered plugin's own Path is its working copy, written as the installer wrote it -
        /// with the closing backslash or without - and other values and keys are as read.
        /// </summary>
        [Fact]
        public void APluginsOwnPathIsItsWorkingCopy()
        {
            if (!OperatingSystem.IsWindows())
                return;

            using RegistryKey root = PluginRegistry.LocalMachine();
            using RegistryKey plugins = root.OpenSubKey(@"SOFTWARE\Decal\Plugins");
            string clsid = plugins?.GetSubKeyNames().FirstOrDefault(n => plugins.OpenSubKey(n)?.GetValue("Path") is string p && p.Length > 0);
            if (clsid == null)
                return;     // no Decal on this machine

            using RegistryKey own = plugins.OpenSubKey(clsid);
            string written = (string)own.GetValue("Path");
            object answered = PluginRegistry.Answer(own, "Path", written, id => string.Equals(id, clsid, StringComparison.OrdinalIgnoreCase) ? @"C:\Working\Copy" : null);
            Assert.Equal(written.EndsWith("\\", StringComparison.Ordinal) ? @"C:\Working\Copy\" : @"C:\Working\Copy", answered);

            Assert.Equal(written, PluginRegistry.Answer(own, "Path", written, _ => null));
            Assert.Equal("x", PluginRegistry.Answer(own, "File", "x", _ => @"C:\Working\Copy"));
        }
    }
}
