using System;
using System.Collections.Generic;
using System.Linq;

namespace AC.Host.Plugins
{
    /// <summary>
    /// One line of a plugin list - Decal's window's in the game, or the Decal Agent's - as
    /// DenAgent's list had them: a name, a box, a version, and here a status saying why.
    /// </summary>
    /// <param name="Hosted">A plugin another plugin runs - a Decal plugin, run by DecalCompat - listed beneath it.</param>
    /// <param name="HostName">For a hosted row, the plugin that runs it.</param>
    /// <param name="AssemblyPath">For one of the host's own plugins, where it is installed.</param>
    public sealed record PluginListRow(
        bool Hosted,
        string HostName,
        string Name,
        string Version,
        bool Enabled,
        string Status,
        string Detail,
        HostedPluginState State,
        bool CanSwitch,
        string AssemblyPath)
    {
        /// <summary>What stays the same about a row while its state changes, for keeping a selection.</summary>
        public string Key => (Hosted ? HostName + "/" + Name : AssemblyPath ?? Name) ?? string.Empty;
    }

    /// <summary>The rows a plugin list shows, in its order: each plugin, then the plugins it runs.</summary>
    public static class PluginList
    {
        /// <summary>
        /// The host's own plugins, each followed by the plugins it runs, if it runs any; then the
        /// plugins run by plugins that were added to the host directly rather than found in its
        /// folder. Game thread only.
        /// </summary>
        /// <param name="entries">The plugin folder's plugins; null for a host without a folder.</param>
        /// <param name="plugins">Every plugin the host is running.</param>
        /// <param name="listingFailed">Told of a plugin that threw while listing what it runs; that plugin's rows are left out.</param>
        public static IReadOnlyList<PluginListRow> Build(IEnumerable<PluginEntry> entries, IEnumerable<IPlugin> plugins, Action<IPlugin, Exception> listingFailed = null)
        {
            List<PluginListRow> rows = new List<PluginListRow>();
            HashSet<IPlugin> listed = new HashSet<IPlugin>();

            foreach (PluginEntry entry in entries ?? Enumerable.Empty<PluginEntry>())
            {
                rows.Add(Row(entry));
                if (entry.Plugin is IHostedPlugins hosting && listed.Add(entry.Plugin))
                    AddHosted(rows, entry.Name, hosting, entry.Plugin, listingFailed);
            }

            foreach (IPlugin plugin in plugins ?? Enumerable.Empty<IPlugin>())
            {
                if (plugin is IHostedPlugins hosting && listed.Add(plugin))
                    AddHosted(rows, plugin.Name, hosting, plugin, listingFailed);
            }

            return rows;
        }

        /// <summary>A row for one of the host's own plugins.</summary>
        public static PluginListRow Row(PluginEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            HostedPluginState state = entry.IsRunning ? HostedPluginState.Running : !entry.Enabled ? HostedPluginState.Off : HostedPluginState.Failed;
            string said = state switch
            {
                HostedPluginState.Running => "Running",
                HostedPluginState.Off => "Switched off",
                _ => Capitalised(entry.Status),
            };

            return new PluginListRow(false, null, entry.Name, entry.Version, entry.Enabled, entry.Status,
                $"{said}. One of this host's own plugins, from {entry.AssemblyPath}.", state, true, entry.AssemblyPath);
        }

        private static void AddHosted(List<PluginListRow> rows, string hostName, IHostedPlugins hosting, IPlugin plugin, Action<IPlugin, Exception> listingFailed)
        {
            IReadOnlyList<HostedPluginInfo> hosted;
            try
            {
                hosted = hosting.HostedPlugins;
            }
            catch (Exception ex)
            {
                listingFailed?.Invoke(plugin, ex);
                return;
            }

            foreach (HostedPluginInfo info in hosted ?? Array.Empty<HostedPluginInfo>())
                rows.Add(new PluginListRow(true, hostName, info.Name, info.Version, info.Enabled, info.Status, info.Detail, info.State, info.CanSwitch, null));
        }

        private static string Capitalised(string text)
            => string.IsNullOrEmpty(text) ? "Not loaded" : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
