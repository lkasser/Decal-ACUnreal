using System.Collections.Generic;

namespace AC.Host.Plugins
{
    /// <summary>
    /// Implemented by a plugin that runs plugins of its own - the Decal compatibility layer
    /// runs Decal's - so that Decal's window can list them under it, and switch them on and off
    /// and reload them, as it does the host's own.
    /// </summary>
    /// <remarks>
    /// Called on the game thread, like everything else that touches a plugin. Names are the
    /// hosted plugins' own, as <see cref="HostedPlugins"/> gives them.
    /// </remarks>
    public interface IHostedPlugins
    {
        IReadOnlyList<HostedPluginInfo> HostedPlugins { get; }

        void SetHostedEnabled(string name, bool enabled);

        void ReloadHosted(string name);

        /// <summary>
        /// Looks again for plugins to host - installed or registered since the last look - and
        /// starts the ones switched on: what Refresh List and Find New ask of the host's own.
        /// </summary>
        void RescanHosted()
        {
        }
    }

    /// <summary>Where a hosted plugin stands, for the picture or colour a list gives it.</summary>
    public enum HostedPluginState
    {
        /// <summary>Loaded and started.</summary>
        Running,

        /// <summary>Switched off, here or in Decal's own list.</summary>
        Off,

        /// <summary>Switched on, and tried, and it did not start.</summary>
        Failed,

        /// <summary>Not loaded, because the host does its job itself.</summary>
        Replaced,

        /// <summary>Not tried, because it cannot run in this host at all - a native plugin, a missing file.</summary>
        CannotRun,
    }

    /// <summary>One plugin a plugin hosts, as Decal's window shows it.</summary>
    public sealed class HostedPluginInfo
    {
        public HostedPluginInfo(string name, string version, bool enabled, string status)
            : this(name, version, enabled, status, null, enabled ? (status == "running" ? HostedPluginState.Running : HostedPluginState.Failed) : HostedPluginState.Off)
        {
        }

        /// <param name="detail">Why it stands as it does, in a sentence or two; the status when null.</param>
        /// <param name="canSwitch">Whether its box does anything: false for a plugin the host replaces.</param>
        public HostedPluginInfo(string name, string version, bool enabled, string status, string detail, HostedPluginState state, bool canSwitch = true)
        {
            Name = name ?? string.Empty;
            Version = version ?? string.Empty;
            Enabled = enabled;
            Status = status ?? string.Empty;
            Detail = string.IsNullOrEmpty(detail) ? Status : detail;
            State = state;
            CanSwitch = canSwitch;
        }

        public string Name { get; }

        public string Version { get; }

        public bool Enabled { get; }

        /// <summary>Running, off, or what went wrong, in a few words.</summary>
        public string Status { get; }

        /// <summary>The whole reason: why it runs, why it does not, and what would change that.</summary>
        public string Detail { get; }

        public HostedPluginState State { get; }

        /// <summary>
        /// Whether ticking its box switches it: a plugin the host does the job of instead has a
        /// box that shows Decal's own tick and does nothing.
        /// </summary>
        public bool CanSwitch { get; }
    }
}
