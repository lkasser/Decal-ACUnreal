using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using AC.Dat;
using Decal.Adapter.Hosting;
using Decal.Compat;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// A registered Decal plugin's calls this 64-bit .NET host answers otherwise than Decal did -
    /// the player's folders, an XmlSerializer over its own types, Decal.dll's DispatchOnChatCommand
    /// - pointed at the stand-ins in its working copy (<see cref="CallRewrite"/>), as Mag-Tools
    /// makes all three.
    /// </summary>
    [Collection(DecalCollection.Name)]
    public sealed class DecalCallRewriteTests : IDisposable
    {
        private const string TroubledClsid = "{0B5C2E1D-7A4F-4C3B-9E21-5D6F7A8B9C0D}";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "achost-calls-" + Guid.NewGuid().ToString("N"));

        private static string Troubled => Path.Combine(AppContext.BaseDirectory, "decal-registered", "TroubledPlugin", "TroubledPlugin.dll");

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A copy still held by a context that has not finished unloading; temp is temp.
            }
        }

        /// <summary>
        /// The troubled plugin's LegacyCalls make the three calls as Mag-Tools does: each becomes a
        /// call into a stand-in - the serializer's constructor a call of the same size - named through
        /// its own reference to Decal.Adapter, and nothing else in it changes.
        /// </summary>
        [Fact]
        public void APluginsFolderSerializerAndDecalCallsGoToTheStandIns()
        {
            byte[] original = File.ReadAllBytes(Troubled);
            byte[] rewritten = CallRewrite.Rewrite(original, out int redirected, out string problem);

            Assert.Equal(3, redirected);
            Assert.Null(problem);

            using ModuleDefinition before = ModuleDefinition.ReadModule(new MemoryStream(original));
            using ModuleDefinition after = ModuleDefinition.ReadModule(new MemoryStream(rewritten));
            Instruction[] code = after.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();

            Assert.Contains(code, i => Calls(i, "PluginFolders", "GetFolderPath"));
            Assert.Contains(code, i => Calls(i, "PluginXml", "Serializer"));
            Assert.Contains(code, i => Calls(i, "DecalNative", "DispatchOnChatCommand"));
            Assert.DoesNotContain(code, i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m && m.DeclaringType.Name == "XmlSerializer");
            Assert.DoesNotContain(code, i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.Environment");
            Assert.Equal(before.AssemblyReferences.Select(a => a.Name).OrderBy(n => n), after.AssemblyReferences.Select(a => a.Name).OrderBy(n => n));
        }

        [Fact]
        public void AnAssemblyThatMakesNoneOfThemIsLeftAsItIs()
        {
            byte[] plain = File.ReadAllBytes(typeof(Assert).Assembly.Location);
            Assert.Same(plain, CallRewrite.Rewrite(plain, out int redirected, out string problem));
            Assert.Equal(0, redirected);
            Assert.Null(problem);
        }

        /// <summary>
        /// Run from its working copy, the plugin finds its Documents in the folder the host was given
        /// to stand in for the player's, keeps a list of its own type through XmlSerializer and back,
        /// and offers a line to the plugins without a Decal.dll.
        /// </summary>
        [Fact]
        public void RunFromItsWorkingCopyThePluginReachesTheStandIns()
        {
            MacroTestHost host = new MacroTestHost(dataRoot: Path.Combine(_root, "data"));
            host.Settings["DecalCompat:Folder"] = Path.Combine(_root, "folder");
            host.Settings["DecalCompat:UserFolders"] = Path.Combine(_root, "user");
            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new DecalRegistryEntry(TroubledClsid, "Troubled Plugin", Path.GetDirectoryName(Troubled), "TroubledPlugin.dll"));

            DecalCompatPlugin decal = new DecalCompatPlugin(registry);
            decal.Startup(host);
            try
            {
                List<string> offered = new List<string>();
                decal.Runtime.Core.CommandLineText += (_, e) =>
                {
                    offered.Add(e.Text);
                    e.Eat = e.Text == "/tp mine";
                };

                Type calls = decal.Find("Troubled Plugin").Context.MainAssembly.GetType("TroubledPlugin.LegacyCalls", throwOnError: true);
                Assert.Equal(Path.Combine(_root, "user", "Documents"), Call(calls, "Documents"));
                Assert.Equal("Pyreal Mote", Call(calls, "RoundTrip", "Pyreal Mote"));
                Assert.Equal(1, Call(calls, "Dispatch", "/tp mine"));
                Assert.Equal(0, Call(calls, "Dispatch", "/nobody"));
                Assert.Equal(new[] { "/tp mine", "/nobody" }, offered);
            }
            finally
            {
                decal.Shutdown();
            }

            // With nothing given to stand in for them, the folders are the player's own.
            Assert.Null(PluginFolders.Root);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Personal), PluginFolders.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        }

        /// <summary>
        /// Why: .NET writes a serializer's code where its type's assembly lives, and a List of a
        /// plugin's type lives in .NET's own, where nothing may name a type that can be unloaded -
        /// Mag-Tools' inventory logger threw at every login. Anchored on the plugin's type, the
        /// code goes beside it.
        /// </summary>
        [Fact]
        public void AListOfAnUnloadablePluginsTypeIsSerializedBesideIt()
        {
            AssemblyLoadContext context = new AssemblyLoadContext("serializer test", isCollectible: true);
            try
            {
                Assembly plugin = context.LoadFromAssemblyPath(Troubled);
                Type item = plugin.GetType("TroubledPlugin.LegacyCalls+Item", throwOnError: true);
                Type list = typeof(List<>).MakeGenericType(item);

                TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(() => Call(plugin.GetType("TroubledPlugin.LegacyCalls"), "RoundTrip", "x"));
                Assert.IsType<NotSupportedException>(thrown.InnerException);

                Assert.Same(item, PluginXml.CollectiblePart(list));
                System.Xml.Serialization.XmlSerializer serializer = PluginXml.Serializer(list);
                object items = Activator.CreateInstance(list);
                object one = Activator.CreateInstance(item);
                item.GetField("Name").SetValue(one, "Mana Stone");
                list.GetMethod("Add").Invoke(items, new[] { one });

                using StringWriter writer = new StringWriter();
                serializer.Serialize(writer, items);
                Assert.Contains("<Name>Mana Stone</Name>", writer.ToString());
            }
            finally
            {
                context.Unload();
            }
        }

        private static bool Calls(Instruction instruction, string type, string method)
            => instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference m
               && m.DeclaringType.FullName == "Decal.Adapter.Hosting." + type && m.Name == method;

        private static object Call(Type type, string method, params object[] arguments)
            => type.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, arguments);
    }
}
