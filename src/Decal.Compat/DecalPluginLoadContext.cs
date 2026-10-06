using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Decal.Compat
{
    /// <summary>
    /// The load context one Decal plugin runs in: its own assemblies from its folder, and
    /// Decal's - whichever version and key it was built against - from the stand-ins.
    /// </summary>
    /// <remarks>
    /// A Decal plugin names "Decal.Adapter, Version=2.9.x, PublicKeyToken=bd1c8ce002ce221e",
    /// and Virindi View Service at whatever version it was built with. Every such request is
    /// answered with the one stand-in already loaded, whatever the version asked for, so that
    /// every plugin's PluginBase is the runtime's PluginBase. .NET does not check strong-name
    /// signatures, so nothing objects.
    ///
    /// <para>
    /// Many Decal plugins were built for x86 only, which says nothing about their code - it is
    /// all IL - but stops a 64-bit process loading them. Those are loaded from a copy with the
    /// flag cleared; everything else from where it is, since that is where a Decal plugin looks
    /// for its own files. A plugin from Decal's registry is loaded from its working copy
    /// (<see cref="WorkingCopy"/>), where the flag is already cleared, so never from the
    /// player's install. One from Decal Compat's own folder has no working copy, so its DLLs are
    /// treated here as a working copy's are, and one whose calls that changes is loaded from a
    /// treated copy too: Mag-Filter, built from its sources, keeps its settings in the player's
    /// Documents, which must be the stand-in for them when the host is given one.
    /// </para>
    ///
    /// <para>
    /// The context is collectible, so a plugin switched off can be unloaded; and it has to be,
    /// because the stand-ins it binds to live in the Decal compatibility plugin's own context,
    /// which the host loads collectibly, and nothing may bind to a collectible context from one
    /// that is not.
    /// </para>
    /// </remarks>
    internal sealed class DecalPluginLoadContext : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, Func<Assembly>> _shims;
        private readonly string _sourceDirectory;
        private readonly string _copyDirectory;
        private readonly Func<string, Assembly> _otherPlugins;
        private readonly IReadOnlyList<string> _nativeFolders;
        private readonly List<string> _nativeProblems = new List<string>();

        /// <param name="shims">
        /// The stand-ins by assembly name, each loaded only when a plugin first asks for it: one a
        /// plugin finds among the loaded assemblies is one it uses, so the Virindi HUDs stand-in is
        /// there only once a plugin that names it has asked.
        /// </param>
        /// <param name="otherPlugins">
        /// Finds another running Decal plugin's assembly by name. Decal ran every plugin in one
        /// domain, and plugins that work together - Mag-Tools with Virindi's, Virindi's with each
        /// other - reference each other directly; each is given the one already running.
        /// </param>
        /// <param name="nativeFolders">
        /// Folders of native DLLs built for this process, used in place of a plugin's own when
        /// that is built for another processor or missing - a 64-bit sqlite3.dll for the 32-bit one
        /// Virindi's tools ship.
        /// </param>
        /// <param name="treat">
        /// Whether its DLLs are treated as a registered plugin's working copy has them treated
        /// (<see cref="WorkingCopy.Treat"/>) - its calls of the player's folders, the XML serializer
        /// and Decal.dll pointed at this host's, its registry reads and address arithmetic put
        /// right - and loaded from a treated copy when that changes anything: for a plugin from
        /// Decal Compat's own folder, which runs where it is and has no working copy.
        /// </param>
        internal DecalPluginLoadContext(string name, IReadOnlyDictionary<string, Func<Assembly>> shims, string sourceDirectory, string copyDirectory,
                                        Func<string, Assembly> otherPlugins = null, IReadOnlyList<string> nativeFolders = null, bool treat = false)
            : base("Decal: " + name, isCollectible: true)
        {
            _shims = shims;
            _sourceDirectory = sourceDirectory;
            _copyDirectory = copyDirectory;
            _otherPlugins = otherPlugins;
            _nativeFolders = nativeFolders ?? Array.Empty<string>();
            _treat = treat;
        }

        private readonly bool _treat;

        /// <summary>The plugin's own assembly, once loaded.</summary>
        internal Assembly MainAssembly { get; private set; }

        /// <summary>
        /// Native DLLs the plugin asked for that could not be given it, and why - a 32-bit DLL, in
        /// this 64-bit process - for explaining the failure that follows. A copy: a plugin's
        /// native calls are resolved on whichever thread first makes them.
        /// </summary>
        internal IReadOnlyList<string> NativeProblems
        {
            get
            {
                lock (_nativeProblems)
                    return _nativeProblems.ToArray();
            }
        }

        /// <summary>
        /// Whether any Decal plugin has brought Windows Forms in, so its timers need the game
        /// thread's messages pumped. Set on the game thread, where plugins are loaded; read there.
        /// </summary>
        internal static bool WindowsFormsLoaded { get; private set; }

        /// <summary>
        /// Loads an assembly from the plugin's folder: from where it is, or from a corrected
        /// copy when it was built for x86 only.
        /// </summary>
        internal Assembly LoadPluginAssembly(string path)
        {
            Assembly assembly = LoadFromAssemblyPath(PathForLoading(path));
            MainAssembly ??= assembly;
            return assembly;
        }

        protected override Assembly Load(AssemblyName name)
        {
            if (name.Name != null && _shims.TryGetValue(name.Name, out Func<Assembly> shim))
                return shim();

            // Before the host's own: .NET's System.Drawing is only the colours and rectangles,
            // and a plugin that draws bitmaps needs the whole of it. Both forward the colours to
            // the same assembly, so the stand-ins' Color is still the plugin's.
            Assembly desktop = WindowsDesktop.Resolve(this, name);
            if (desktop != null)
                return desktop;

            // Anything the host itself has loaded - the framework, the host's own assemblies -
            // is the host's copy.
            foreach (Assembly loaded in Default.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            string beside = Path.Combine(_sourceDirectory, name.Name + ".dll");
            if (File.Exists(beside))
                return LoadFromAssemblyPath(PathForLoading(beside));

            return name.Name != null ? _otherPlugins?.Invoke(name.Name) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string path = ResolveNative(unmanagedDllName, _sourceDirectory, _nativeFolders, out string problem);
            if (problem != null)
            {
                lock (_nativeProblems)
                {
                    if (!_nativeProblems.Contains(problem))
                        _nativeProblems.Add(problem);
                }
            }

            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }

        /// <summary>
        /// Which native DLL a plugin's request is given: the one beside the plugin when it is built
        /// for this process; otherwise one of that name in the native folders; otherwise none, and
        /// the search goes on as the runtime's own. <paramref name="problem"/> says why a plugin's
        /// own DLL was passed over, when it was and nothing took its place.
        /// </summary>
        internal static string ResolveNative(string unmanagedDllName, string sourceDirectory, IReadOnlyList<string> nativeFolders, out string problem)
        {
            problem = null;
            string file = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : unmanagedDllName + ".dll";
            string beside = Path.Combine(sourceDirectory, file);

            // A DLL for another processor would only fail to load, with a message that says
            // nothing about why.
            string wrong = File.Exists(beside) ? WrongMachine(beside) : null;
            if (File.Exists(beside) && wrong == null)
                return beside;

            foreach (string folder in nativeFolders ?? Array.Empty<string>())
            {
                string replacement = Path.Combine(folder, file);
                if (File.Exists(replacement) && WrongMachine(replacement) == null)
                    return replacement;
            }

            if (wrong != null)
            {
                string bits = Environment.Is64BitProcess ? "64" : "32";
                problem = $"{file}, a {wrong} DLL, which this {bits}-bit host cannot load";
                if (nativeFolders != null && nativeFolders.Count > 0)
                    problem += $"; a {bits}-bit {file} in {nativeFolders[0]} would be used instead";
            }

            return null;
        }

        /// <summary>
        /// "32-bit" or "64-bit" when a native DLL is built for a processor other than this
        /// process's; null when it is this process's, or cannot be told.
        /// </summary>
        internal static string WrongMachine(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using PEReader reader = new PEReader(stream);
                Machine machine = reader.PEHeaders.CoffHeader.Machine;
                bool is64 = machine == Machine.Amd64 || machine == Machine.Arm64 || machine == Machine.IA64;
                bool is32 = machine == Machine.I386 || machine == Machine.Arm || machine == Machine.ArmThumb2;
                if (Environment.Is64BitProcess && is32)
                    return "32-bit";
                if (!Environment.Is64BitProcess && is64)
                    return "64-bit";
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is BadImageFormatException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Where to load an assembly from: where it is, unless it is marked x86-only or - for a
        /// plugin from the folder - makes calls this host answers otherwise, in which case a
        /// corrected copy.
        /// </summary>
        /// <remarks>
        /// From where it is by preference, because Decal plugins find their own files - an ini,
        /// a layout, their settings - beside their assembly, and write them back there. A copy is
        /// made only when it must be, and then the plugin folder's other files are copied beside
        /// it so that what it reads is there; what it writes there is lost when it is unloaded,
        /// which is said in the log.
        /// </remarks>
        private string PathForLoading(string path)
        {
            byte[] image = File.ReadAllBytes(path);
            bool x86 = ClearRequires32Bit(image);

            bool treated = false;
            if (_treat)
            {
                byte[] before = image;
                foreach (string problem in WorkingCopy.Treat(ref image))
                    _treatProblems.Add($"{Path.GetFileName(path)}: {problem}");
                treated = !ReferenceEquals(before, image);
            }

            if (!x86 && !treated)
                return path;

            Directory.CreateDirectory(_copyDirectory);
            string copy = Path.Combine(_copyDirectory, Path.GetFileName(path));
            File.WriteAllBytes(copy, image);

            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(path)))
            {
                string beside = Path.Combine(_copyDirectory, Path.GetFileName(file));
                if (!File.Exists(beside))
                    File.Copy(file, beside);
            }

            CopiedForX86 |= x86;
            CopiedTreated |= treated;
            return copy;
        }

        private readonly List<string> _treatProblems = new List<string>();

        /// <summary>Whether anything had to be loaded from a copy with its x86-only mark cleared.</summary>
        internal bool CopiedForX86 { get; private set; }

        /// <summary>Whether anything of a folder plugin's had to be loaded from a copy with its calls pointed at this host's.</summary>
        internal bool CopiedTreated { get; private set; }

        /// <summary>What could not be treated in a folder plugin's DLLs, and why, one line each.</summary>
        internal IReadOnlyList<string> TreatProblems => _treatProblems;

        /// <summary>
        /// Clears COMIMAGE_FLAGS_32BITREQUIRED in an IL-only image, in place. An image with
        /// native code keeps it: that really is 32-bit, and loading it would fail either way.
        /// </summary>
        internal static bool ClearRequires32Bit(byte[] image)
        {
            const int Requires32Bit = 0x2;
            const int FlagsOffset = 16;

            using PEReader reader = new PEReader(new MemoryStream(image, writable: false));
            CorHeader cor = reader.PEHeaders.CorHeader;
            if (cor == null || (cor.Flags & CorFlags.ILOnly) == 0 || (cor.Flags & CorFlags.Requires32Bit) == 0)
                return false;

            int offset = reader.PEHeaders.CorHeaderStartOffset + FlagsOffset;
            int flags = BitConverter.ToInt32(image, offset);
            BitConverter.GetBytes(flags & ~Requires32Bit).CopyTo(image, offset);
            return true;
        }

        /// <summary>The same, for a file that may not be an image at all: one that is not is left alone.</summary>
        internal static bool TryClearRequires32Bit(byte[] image)
        {
            try
            {
                return ClearRequires32Bit(image);
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// Windows Forms and the parts of System.Drawing that go with it, for plugins built
        /// against the .NET Framework, which had them in every process. On .NET they are a
        /// separate shared framework the host does not run on, so they are loaded from it by
        /// path when a plugin asks - its timers and message boxes then work as long as someone
        /// pumps the game thread's messages, which the compatibility plugin does.
        /// </summary>
        /// <remarks>
        /// Loaded once per process, into a context of their own that every Decal plugin shares
        /// and nothing unloads. Two copies of Windows Forms in one process register the same
        /// window class names, and the second copy's first timer fails with "class already
        /// exists" - as it would on a plugin's reload, the old copy's classes outliving it. The
        /// context is a plain one kept in the process's own data, so it survives the Decal
        /// compatibility plugin itself being reloaded, and pins none of its code: what Windows
        /// Forms needs beyond .NET itself is loaded into it up front, so it never has to ask.
        /// </remarks>
        private static class WindowsDesktop
        {
            private const string SharedKey = "Decal.Compat.WindowsDesktop";

            private static readonly object Gate = new object();

            private static readonly string Directory = FindDirectory();

            private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System.Windows.Forms",
                "System.Windows.Forms.Primitives",
                "System.Drawing.Common",
                "System.Drawing",
                "Microsoft.Win32.SystemEvents",
                "System.Private.Windows.Core",
                "System.Private.Windows.GdiPlus",
                "Accessibility",
            };

            public static Assembly Resolve(AssemblyLoadContext requester, AssemblyName name)
            {
                if (name.Name == null || !Names.Contains(name.Name))
                    return null;

                // A host that is itself a Windows Forms program - the Decal Agent - already has
                // Windows Forms, and a second copy beside its own is exactly the trouble the shared
                // context exists to avoid. Its copies are the ones every plugin gets. Only then:
                // a plain .NET host has a System.Drawing of its own too, but it is only the
                // colours and rectangles.
                if (HostIsWindowsDesktop)
                    return Note(Default.LoadFromAssemblyName(new AssemblyName(name.Name)));

                if (Directory == null)
                    return null;

                lock (Gate)
                {
                    AssemblyLoadContext shared = Shared();
                    foreach (Assembly loaded in shared.Assemblies)
                    {
                        if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                            return Note(loaded);
                    }

                    string path = Path.Combine(Directory, name.Name + ".dll");
                    return File.Exists(path) ? Note(shared.LoadFromAssemblyPath(path)) : null;
                }
            }

            /// <summary>Whether the host runs on the Windows Desktop runtime itself, as a Windows Forms program does.</summary>
            private static readonly bool HostIsWindowsDesktop =
                ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Any(path => string.Equals(Path.GetFileNameWithoutExtension(path), "System.Windows.Forms", StringComparison.OrdinalIgnoreCase));

            private static Assembly Note(Assembly assembly)
            {
                if (string.Equals(assembly.GetName().Name, "System.Windows.Forms", StringComparison.OrdinalIgnoreCase))
                    WindowsFormsLoaded = true;

                return assembly;
            }

            /// <summary>The process's one Windows Forms context, made - and filled - on first use.</summary>
            private static AssemblyLoadContext Shared()
            {
                if (AppDomain.CurrentDomain.GetData(SharedKey) is AssemblyLoadContext existing)
                    return existing;

                AssemblyLoadContext context = new AssemblyLoadContext("Decal: Windows Forms", isCollectible: false);
                foreach (string path in Closure())
                    context.LoadFromAssemblyPath(path);

                AppDomain.CurrentDomain.SetData(SharedKey, context);
                return context;
            }

            /// <summary>
            /// Windows Forms and everything it references, however indirectly, that is in the
            /// Windows Desktop runtime and not in .NET's own - read from metadata, loading nothing.
            /// </summary>
            private static IEnumerable<string> Closure()
            {
                HashSet<string> platform = new HashSet<string>(
                    ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
                        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                        .Select(Path.GetFileNameWithoutExtension),
                    StringComparer.OrdinalIgnoreCase);

                HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Queue<string> pending = new Queue<string>(new[] { "System.Windows.Forms", "System.Drawing" });

                while (pending.Count > 0)
                {
                    string name = pending.Dequeue();
                    string path = Path.Combine(Directory, name + ".dll");
                    if (platform.Contains(name) || !File.Exists(path) || !found.Add(name))
                        continue;

                    using FileStream stream = File.OpenRead(path);
                    using PEReader pe = new PEReader(stream);
                    MetadataReader reader = pe.GetMetadataReader();
                    foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
                        pending.Enqueue(reader.GetString(reader.GetAssemblyReference(handle).Name));
                }

                return found.Select(name => Path.Combine(Directory, name + ".dll")).ToList();
            }

            /// <summary>The Windows Desktop runtime matching the .NET the host runs on, or null.</summary>
            private static string FindDirectory()
            {
                if (!OperatingSystem.IsWindows())
                    return null;

                string core = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
                string version = Path.GetFileName(core);
                string shared = Path.GetDirectoryName(Path.GetDirectoryName(core));
                if (shared == null)
                    return null;

                string desktop = Path.Combine(shared, "Microsoft.WindowsDesktop.App", version);
                if (System.IO.Directory.Exists(desktop))
                    return desktop;

                // A different patch of the same major version is the next best thing.
                string root = Path.Combine(shared, "Microsoft.WindowsDesktop.App");
                if (!System.IO.Directory.Exists(root))
                    return null;

                string major = version.Split('.')[0] + ".";
                string best = null;
                foreach (string candidate in System.IO.Directory.GetDirectories(root))
                {
                    if (Path.GetFileName(candidate).StartsWith(major, StringComparison.Ordinal))
                        best = candidate;
                }

                return best;
            }
        }
    }
}
