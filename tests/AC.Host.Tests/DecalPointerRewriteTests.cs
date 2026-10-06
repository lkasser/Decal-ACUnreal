using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Decal.Compat;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// A registered Decal plugin's 32-bit address arithmetic, widened for this 64-bit host
    /// (<see cref="PointerRewrite"/>): the System.Data.SQLite 1.0.61 that Virindi's tools ship, whose
    /// every blob read overflowed here, so that Global Inventory's searches found nothing.
    /// </summary>
    public class DecalPointerRewriteTests
    {
        /// <summary>
        /// The System.Data.SQLite Global Inventory ships, as the player installed it, found through
        /// Decal's registry; read only. Null on a machine without it.
        /// </summary>
        private static string InstalledSqlite
            => AC.Dat.DecalInstall.Detect().Plugins
                .Select(p => p.Directory)
                .Where(d => !string.IsNullOrEmpty(d) && d.Contains("GlobalInventory", StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "System.Data.SQLite.dll"))
                .FirstOrDefault(File.Exists);

        /// <summary>An address above 4 GB, where a 64-bit process's memory is.</summary>
        private static readonly IntPtr High = new IntPtr(0x0000_0123_4567_8000);

        /// <summary>
        /// An assembly that does with addresses what System.Data.SQLite 1.0.61 does, compiled as its
        /// compiler compiled it:
        /// <list type="bullet">
        /// <item><c>At(blob, offset)</c>, <c>(IntPtr)(blob.ToInt32() + offset)</c>, as its GetBytes reads a blob;</item>
        /// <item><c>Before(blob, offset)</c>, <c>new IntPtr(blob.ToInt32() - offset)</c>, the constructor's way;</item>
        /// <item><c>Low(blob)</c>, <c>blob.ToInt32()</c> and nothing more, which is not an address made again.</item>
        /// </list>
        /// </summary>
        private static byte[] OldAddressArithmetic()
        {
            using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("OldAddressArithmetic", new Version(1, 0, 61, 0)), "OldAddressArithmetic", ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            TypeDefinition type = new TypeDefinition("OldAddressArithmetic", "Blob",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
            module.Types.Add(type);

            MethodReference toInt32 = module.ImportReference(typeof(IntPtr).GetMethod(nameof(IntPtr.ToInt32)));
            MethodReference fromInt = module.ImportReference(typeof(IntPtr).GetMethod("op_Explicit", new[] { typeof(int) }));
            MethodReference construct = module.ImportReference(typeof(IntPtr).GetConstructor(new[] { typeof(int) }));

            MethodDefinition at = Method(type, "At", module.TypeSystem.IntPtr, withOffset: true);
            ILProcessor il = at.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarga_S, at.Parameters[0]);
            il.Emit(OpCodes.Call, toInt32);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Call, fromInt);
            il.Emit(OpCodes.Ret);

            MethodDefinition before = Method(type, "Before", module.TypeSystem.IntPtr, withOffset: true);
            before.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
            il = before.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Ldarga_S, before.Parameters[0]);
            il.Emit(OpCodes.Call, toInt32);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Newobj, construct);
            il.Emit(OpCodes.Ret);

            MethodDefinition low = Method(type, "Low", module.TypeSystem.Int32, withOffset: false);
            il = low.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarga_S, low.Parameters[0]);
            il.Emit(OpCodes.Call, toInt32);
            il.Emit(OpCodes.Ret);

            using MemoryStream image = new MemoryStream();
            assembly.Write(image);
            return image.ToArray();
        }

        private static MethodDefinition Method(TypeDefinition type, string name, TypeReference returns, bool withOffset)
        {
            MethodDefinition method = new MethodDefinition(name, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig, returns);
            method.Parameters.Add(new ParameterDefinition("blob", Mono.Cecil.ParameterAttributes.None, type.Module.TypeSystem.IntPtr));
            if (withOffset)
                method.Parameters.Add(new ParameterDefinition("offset", Mono.Cecil.ParameterAttributes.None, type.Module.TypeSystem.Int32));
            type.Methods.Add(method);
            return method;
        }

        /// <summary>Calls one of the methods above in an image, loaded on its own and let go of after.</summary>
        private static object Call(byte[] image, string method, params object[] arguments)
        {
            AssemblyLoadContext context = new AssemblyLoadContext("pointer-rewrite-test", isCollectible: true);
            try
            {
                Assembly assembly = context.LoadFromStream(new MemoryStream(image));
                return assembly.GetType("OldAddressArithmetic.Blob").GetMethod(method).Invoke(null, arguments);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
            finally
            {
                context.Unload();
            }
        }

        /// <summary>
        /// The overflow as the plugin met it - an address above 2 GB taken as an int - and the same
        /// code, widened, reading where it meant to: the address plus or minus the offset. A
        /// pointer's ToInt32 for any other use is left alone.
        /// </summary>
        [Fact]
        public void AddressArithmeticDoneInThirtyTwoBitsIsWidened()
        {
            if (!Environment.Is64BitProcess)
                return;

            byte[] original = OldAddressArithmetic();
            Assert.Throws<OverflowException>(() => Call(original, "At", High, 16));

            byte[] rewritten = PointerRewrite.Rewrite(original, out int widened, out string problem);

            Assert.Null(problem);
            Assert.Equal(2, widened);
            Assert.Equal(High + 16, (IntPtr)Call(rewritten, "At", High, 16));
            Assert.Equal(High - 16, (IntPtr)Call(rewritten, "Before", High, 16));
            Assert.Equal(new IntPtr(0x1234 + 16), (IntPtr)Call(rewritten, "At", new IntPtr(0x1234), 16));
            Assert.Throws<OverflowException>(() => Call(rewritten, "Low", High));

            // The rewritten assembly names nothing it did not before, and needs nothing more done.
            using ModuleDefinition before = ModuleDefinition.ReadModule(new MemoryStream(original));
            using ModuleDefinition after = ModuleDefinition.ReadModule(new MemoryStream(rewritten));
            Assert.Equal(before.AssemblyReferences.Select(a => a.FullName).OrderBy(n => n), after.AssemblyReferences.Select(a => a.FullName).OrderBy(n => n));
            Assert.Same(rewritten, PointerRewrite.Rewrite(rewritten, out widened, out problem));
            Assert.Equal(0, widened);
        }

        /// <summary>
        /// Whichever registered plugin brings such an assembly, its working copy is widened - and a
        /// copy made before this was done is made again - while the install keeps its own as it was.
        /// </summary>
        [Fact]
        public void ARegisteredPluginsWorkingCopyIsWidenedAndAnOlderCopyMadeAgain()
        {
            if (!Environment.Is64BitProcess)
                return;

            string root = Path.Combine(Path.GetTempPath(), "achost-widen-" + Guid.NewGuid().ToString("N"));
            try
            {
                string install = Path.Combine(root, "install", "VirindiChatSystem5");
                Directory.CreateDirectory(install);
                byte[] original = OldAddressArithmetic();
                string installed = Path.Combine(install, "System.Data.SQLite.dll");
                File.WriteAllBytes(installed, original);

                string working = WorkingCopy.DirectoryFor(Path.Combine(root, "working"), install);
                string copy = Path.Combine(working, "System.Data.SQLite.dll");
                Assert.Empty(WorkingCopy.Refresh(install, working));
                Assert.Equal(High + 16, (IntPtr)Call(File.ReadAllBytes(copy), "At", High, 16));
                Assert.Equal(original, File.ReadAllBytes(installed));

                // A copy as the treatments before this one left it: the same source, not widened -
                // 2 from before the widening, 3 and 4 from an install that lacked Mono.Cecil.Rocks.
                FileInfo source = new FileInfo(installed);
                foreach (int treatment in new[] { 2, 3, 4 })
                {
                    File.WriteAllBytes(copy, original);
                    File.WriteAllText(copy + WorkingCopy.StampSuffix, $"{source.Length};{source.LastWriteTimeUtc.Ticks};{treatment}");
                    Assert.Empty(WorkingCopy.Refresh(install, working));
                    Assert.Equal(High + 16, (IntPtr)Call(File.ReadAllBytes(copy), "At", High, 16));
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>
        /// Decal.Compat loaded as the live install had it, without Mono.Cecil.Rocks beside it: the
        /// working copy says which of the host's own assemblies is missing, where, and what to do -
        /// a line the plugin host logs as a warning - and is left unstamped, so that once the file
        /// is there it is widened at the next start rather than kept unwidened for good.
        /// </summary>
        [Fact]
        public void AWorkingCopyTheHostCouldNotTreatNamesTheMissingAssemblyAndIsMadeAgain()
        {
            if (!Environment.Is64BitProcess)
                return;

            string root = Path.Combine(Path.GetTempPath(), "achost-norocks-" + Guid.NewGuid().ToString("N"));
            WithoutRocks context = new WithoutRocks();
            try
            {
                string install = Path.Combine(root, "install", "VirindiGlobalInventory");
                Directory.CreateDirectory(install);
                File.WriteAllBytes(Path.Combine(install, "System.Data.SQLite.dll"), OldAddressArithmetic());
                string working = WorkingCopy.DirectoryFor(Path.Combine(root, "working"), install);
                string copy = Path.Combine(working, "System.Data.SQLite.dll");

                Assembly compat = context.LoadFromAssemblyPath(typeof(WorkingCopy).Assembly.Location);
                MethodInfo refresh = compat.GetType("Decal.Compat.WorkingCopy").GetMethod(nameof(WorkingCopy.Refresh), BindingFlags.Public | BindingFlags.Static);
                IReadOnlyList<string> problems = (IReadOnlyList<string>)refresh.Invoke(null, new object[] { install, working });

                string line = Assert.Single(problems);
                Assert.StartsWith("System.Data.SQLite.dll: its 32-bit address arithmetic could not be widened for this 64-bit host: the Decal Agent's own Mono.Cecil.Rocks.dll (0.11.6.0) is not installed beside Decal.Compat in ", line);
                Assert.EndsWith("Reinstall the Decal Agent; the copy is made again at its next start.", line);
                Assert.Contains(WorkingCopy.HostAssemblyMissing, line);
                Assert.True(File.Exists(copy));
                Assert.False(File.Exists(copy + WorkingCopy.StampSuffix));
                Assert.Throws<OverflowException>(() => Call(File.ReadAllBytes(copy), "At", High, 16));

                // Installed whole, as after a reinstall: the next start widens it.
                Assert.Empty(WorkingCopy.Refresh(install, working));
                Assert.Equal(High + 16, (IntPtr)Call(File.ReadAllBytes(copy), "At", High, 16));
                Assert.True(File.Exists(copy + WorkingCopy.StampSuffix));
            }
            finally
            {
                context.Unload();
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>
        /// Decal.Compat as an install that lacks Mono.Cecil.Rocks loads it: everything else from the
        /// tests' own context, and Rocks not to be found, as the runtime says of a file not there.
        /// </summary>
        private sealed class WithoutRocks : AssemblyLoadContext
        {
            public WithoutRocks()
                : base("without-rocks", isCollectible: true)
            {
            }

            protected override Assembly Load(AssemblyName name)
            {
                if (name.Name == "Mono.Cecil.Rocks")
                    throw new FileNotFoundException($"Could not load file or assembly '{name.FullName}'. The system cannot find the file specified.", name.FullName);

                return null;
            }
        }

        [Theory]
        [InlineData("Mono.Cecil.Rocks, Version=0.11.6.0, Culture=neutral, PublicKeyToken=50cebf1cceb9d05e", "Mono.Cecil.Rocks.dll (0.11.6.0)")]
        [InlineData("Mono.Cecil", "Mono.Cecil.dll")]
        public void AMissingAssemblyOfTheHostsIsNamed(string fileName, string named)
        {
            string said = WorkingCopy.MissingHostAssembly(new FileNotFoundException("Could not load file or assembly.", fileName));
            Assert.StartsWith($"the Decal Agent's own {named} is not installed beside Decal.Compat in ", said);

            Assert.NotNull(WorkingCopy.MissingHostAssembly(new FileLoadException("Could not load.", fileName)));
            Assert.Null(WorkingCopy.MissingHostAssembly(new FileNotFoundException("No file name.")));
            Assert.Null(WorkingCopy.MissingHostAssembly(new BadImageFormatException("Not one.", fileName)));
        }

        [Fact]
        public void AnAssemblyWithoutAddressArithmeticOrThatIsNativeIsLeftAsItIs()
        {
            byte[] plain = File.ReadAllBytes(typeof(Assert).Assembly.Location);
            Assert.Same(plain, PointerRewrite.Rewrite(plain, out int widened, out string problem));
            Assert.Equal(0, widened);
            Assert.Null(problem);

            string native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
            if (File.Exists(native))
            {
                byte[] image = File.ReadAllBytes(native);
                Assert.Same(image, PointerRewrite.Rewrite(image, out widened, out problem));
                Assert.Null(problem);
            }
        }

        /// <summary>
        /// Global Inventory's own System.Data.SQLite, as the player installed it: the two places it
        /// reads a blob are widened and nothing else changes; and through it, with the host's
        /// 64-bit sqlite3.dll, a blob reads back whole in this 64-bit process - where the original,
        /// with SQLite's memory above 2 GB as it is here, throws OverflowException from
        /// IntPtr.ToInt32 on every row, as the live test found.
        /// </summary>
        [SkippableFact]
        public void GlobalInventorysSqliteReadsBlobsOnceWidened()
        {
            Skip.IfNot(File.Exists(InstalledSqlite), "Virindi Global Inventory is not installed on this machine.");
            Skip.IfNot(Environment.Is64BitProcess, "Only a 64-bit process has addresses that overflow an int.");
            string sqlite3 = Path.Combine(AppContext.BaseDirectory, "native", "sqlite3.dll");
            Skip.IfNot(File.Exists(sqlite3), "Decal Compat's 64-bit sqlite3.dll is not beside the tests.");

            byte[] original = File.ReadAllBytes(InstalledSqlite);
            byte[] rewritten = PointerRewrite.Rewrite(original, out int widened, out string problem);

            Assert.Null(problem);
            Assert.Equal(2, widened);
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(rewritten)))
            {
                TypeDefinition sqlite = module.GetType("System.Data.SQLite.SQLite3");
                foreach (string name in new[] { "GetBytes", "GetParamValueBytes" })
                {
                    Instruction[] code = sqlite.Methods.Single(m => m.Name == name).Body.Instructions.ToArray();
                    Assert.Contains(code, i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.IntPtr" && m.Name == "ToInt64");
                }

                Assert.DoesNotContain(module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions),
                    i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.IntPtr" && m.Name == "ToInt32");
                Assert.Equal("System.Data.SQLite, Version=1.0.61.0, Culture=neutral, PublicKeyToken=db937bc2d44ff139", module.Assembly.FullName);
            }

            string folder = Path.Combine(Path.GetTempPath(), "achost-sqlite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                byte[] blob = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
                string database = Path.Combine(folder, "items.db");

                Assert.Equal(blob, ReadBlobBack(rewritten, sqlite3, database, blob));

                // The original, as the plugin ran it. SQLite's memory lies above 2 GB in a 64-bit
                // process all but always - high-entropy address randomisation spreads it over a
                // terabyte - and should it ever lie lower there is nothing to reproduce.
                Exception thrown = Record.Exception(() => ReadBlobBack(original, sqlite3, Path.Combine(folder, "original.db"), blob));
                Skip.If(thrown == null, "SQLite's memory lay below 2 GB this time, so the original did not overflow.");
                Assert.IsType<OverflowException>(thrown);
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>
        /// Writes a blob into a new database through a System.Data.SQLite image and reads it back
        /// as Global Inventory reads an item, with GetBytes; the image is loaded on its own, with
        /// <paramref name="sqlite3"/> for its native SQLite, as a plugin's is.
        /// </summary>
        private static byte[] ReadBlobBack(byte[] image, string sqlite3, string database, byte[] blob)
        {
            SqliteContext context = new SqliteContext(sqlite3);
            try
            {
                Assembly assembly = context.LoadFromStream(new MemoryStream(image));
                using IDbConnection connection = (IDbConnection)Activator.CreateInstance(assembly.GetType("System.Data.SQLite.SQLiteConnection", throwOnError: true));
                connection.ConnectionString = "Data Source=" + database;
                connection.Open();

                using (IDbCommand create = connection.CreateCommand())
                {
                    create.CommandText = "create table ObjectData (Id integer, SerializedData blob)";
                    create.ExecuteNonQuery();
                }

                using (IDbCommand insert = connection.CreateCommand())
                {
                    insert.CommandText = "insert into ObjectData (Id, SerializedData) values (1, @b)";
                    IDbDataParameter parameter = insert.CreateParameter();
                    parameter.ParameterName = "@b";
                    parameter.DbType = DbType.Binary;
                    parameter.Value = blob;
                    insert.Parameters.Add(parameter);
                    insert.ExecuteNonQuery();
                }

                using IDbCommand select = connection.CreateCommand();
                select.CommandText = "select * from ObjectData";
                using IDataReader reader = select.ExecuteReader();
                Assert.True(reader.Read());
                byte[] read = new byte[10000];
                long length = reader.GetBytes(1, 0L, read, 0, read.Length);
                return read.Take((int)length).ToArray();
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
            finally
            {
                context.Unload();
            }
        }

        /// <summary>A plugin's context, as far as SQLite goes: its native sqlite3 is the host's 64-bit one.</summary>
        private sealed class SqliteContext : AssemblyLoadContext
        {
            private readonly string _sqlite3;

            public SqliteContext(string sqlite3)
                : base("sqlite-test", isCollectible: true)
            {
                _sqlite3 = sqlite3;
            }

            protected override Assembly Load(AssemblyName assemblyName) => null;

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
                => unmanagedDllName.StartsWith("sqlite3", StringComparison.OrdinalIgnoreCase) ? LoadUnmanagedDllFromPath(_sqlite3) : IntPtr.Zero;
        }
    }
}
