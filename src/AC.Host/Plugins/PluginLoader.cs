using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace AC.Host.Plugins
{
    /// <summary>A plugin found on disk, with where it came from.</summary>
    public sealed class LoadedPlugin
    {
        internal LoadedPlugin(IPlugin plugin, string assemblyPath, int apiVersion)
        {
            Plugin = plugin;
            AssemblyPath = assemblyPath;
            ApiVersion = apiVersion;
        }

        public IPlugin Plugin { get; }

        public string AssemblyPath { get; }

        /// <summary>The contract version the assembly declared.</summary>
        public int ApiVersion { get; }
    }

    /// <summary>
    /// Finds and instantiates plugins from a directory.
    /// </summary>
    /// <remarks>
    /// Each plugin assembly gets its own load context so plugins may carry their own
    /// dependencies at whatever versions they like. The one thing that must NOT be
    /// duplicated is the contract: a plugin's <c>IPlugin</c> has to be the host's
    /// <c>IPlugin</c>, or the type check fails and the plugin is invisible. So any
    /// assembly the host has already loaded is resolved to the host's copy, and only
    /// what the host does not have comes from the plugin's directory.
    ///
    /// Layout: <c>plugins/&lt;name&gt;/&lt;name&gt;.dll</c> plus its dependencies, or
    /// a bare <c>plugins/&lt;name&gt;.dll</c>.
    /// </remarks>
    public static class PluginLoader
    {
        public static IReadOnlyList<LoadedPlugin> LoadFrom(string directory, IPluginLog log)
        {
            List<LoadedPlugin> loaded = new List<LoadedPlugin>();

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return loaded;

            foreach (string candidate in EnumerateCandidates(directory))
            {
                try
                {
                    foreach (LoadedPlugin plugin in LoadAssembly(candidate, log))
                        loaded.Add(plugin);
                }
                catch (Exception ex)
                {
                    // One bad plugin must not stop the others from loading.
                    log?.Error($"Could not load plugin assembly {candidate}", ex);
                }
            }

            return loaded;
        }

        internal static IEnumerable<string> EnumerateCandidates(string directory)
        {
            foreach (string sub in Directory.GetDirectories(directory))
            {
                string named = Path.Combine(sub, Path.GetFileName(sub) + ".dll");
                if (File.Exists(named))
                {
                    yield return named;
                    continue;
                }

                foreach (string dll in Directory.GetFiles(sub, "*.dll"))
                    yield return dll;
            }

            foreach (string dll in Directory.GetFiles(directory, "*.dll"))
                yield return dll;
        }

        private static IEnumerable<LoadedPlugin> LoadAssembly(string path, IPluginLog log)
            => LoadAssembly(path, log, collectible: false, out _);

        /// <summary>
        /// Loads one assembly's plugins in a context of their own. A collectible context can
        /// be unloaded again, which is what lets a plugin be switched off or reloaded while the
        /// host runs; <paramref name="context"/> is that context, or null if nothing loaded.
        /// </summary>
        internal static IReadOnlyList<LoadedPlugin> LoadAssembly(string path, IPluginLog log, bool collectible, out AssemblyLoadContext context)
        {
            PluginLoadContext loadContext = new PluginLoadContext(path, collectible);
            context = loadContext;
            Assembly assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(path));

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            List<LoadedPlugin> plugins = new List<LoadedPlugin>();
            List<Type> candidates = new List<Type>();

            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || type.IsInterface)
                    continue;

                if (!typeof(IPlugin).IsAssignableFrom(type))
                    continue;

                if (type.GetConstructor(Type.EmptyTypes) == null)
                    continue;

                candidates.Add(type);
            }

            // Most files in a plugin directory are its dependencies, not plugins. Only
            // one that actually offers a plugin is worth saying anything about.
            if (candidates.Count == 0)
                return plugins;

            if (!IsCompatible(assembly, log, out int apiVersion))
                return plugins;

            foreach (Type type in candidates)
                plugins.Add(new LoadedPlugin((IPlugin)Activator.CreateInstance(type), path, apiVersion));

            return plugins;
        }

        /// <summary>
        /// Decides whether a plugin assembly was built against a contract this host can
        /// honour, and says why not when it was not.
        /// </summary>
        /// <remarks>
        /// Checked before anything is instantiated. A plugin compiled against an older
        /// host loads perfectly and then throws the first time the host calls something
        /// that did not exist - which for a host that injects into a running game happens
        /// in the worst place at the worst moment. Refusing costs an author one line;
        /// the alternative costs them a session.
        /// </remarks>
        private static bool IsCompatible(Assembly assembly, IPluginLog log, out int apiVersion)
        {
            apiVersion = 0;

            PluginApiAttribute declared = assembly.GetCustomAttribute<PluginApiAttribute>();
            string name = assembly.GetName().Name;

            if (declared == null)
            {
                log?.Error(
                    $"Plugin {name} does not declare which host contract it was built against, so it is not loaded. "
                    + "Add [assembly: AC.Host.Plugins.PluginApi(AC.Host.Plugins.HostApi.Version)] to it.");
                return false;
            }

            apiVersion = declared.Version;

            if (declared.Version > HostApi.Version)
            {
                log?.Error(
                    $"Plugin {name} was built against host contract {declared.Version}, and this host is {HostApi.Version}. "
                    + "The plugin is newer than the host; update the host.");
                return false;
            }

            if (declared.Version < HostApi.MinimumSupported)
            {
                log?.Error(
                    $"Plugin {name} was built against host contract {declared.Version}, and this host no longer supports anything below {HostApi.MinimumSupported}. "
                    + "Rebuild the plugin against the current host.");
                return false;
            }

            return true;
        }

        private sealed class PluginLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;
            private readonly string _directory;

            internal PluginLoadContext(string pluginPath, bool collectible = false)
                : base(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: collectible)
            {
                _resolver = new AssemblyDependencyResolver(pluginPath);
                _directory = Path.GetDirectoryName(pluginPath);
            }

            protected override Assembly Load(AssemblyName name)
            {
                // The host's copy wins for anything it already has, so contract types
                // unify. Returning null defers to the default context.
                foreach (Assembly hostAssembly in Default.Assemblies)
                {
                    if (string.Equals(hostAssembly.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                        return null;
                }

                string resolved = _resolver.ResolveAssemblyToPath(name);
                if (resolved != null)
                    return LoadFromAssemblyPath(resolved);

                // A plugin published without a deps.json still has its dependencies
                // sitting beside it.
                string beside = Path.Combine(_directory, name.Name + ".dll");
                if (File.Exists(beside))
                    return LoadFromAssemblyPath(beside);

                return null;
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                string resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                return resolved != null ? LoadUnmanagedDllFromPath(resolved) : IntPtr.Zero;
            }
        }
    }
}
