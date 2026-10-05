using System;
using System.Collections.Generic;
using AC.Dat;

namespace AC.Host.Tests
{
    /// <summary>
    /// Decal's registry as a test says it is: plugins, services and COM servers in lists, and
    /// nothing read from the machine's own - so a test never depends on, or touches, what the
    /// player has installed.
    /// </summary>
    internal sealed class FakeDecalRegistry : IDecalRegistry
    {
        public List<DecalRegistryEntry> Plugins { get; } = new List<DecalRegistryEntry>();

        public List<DecalRegistryEntry> Services { get; } = new List<DecalRegistryEntry>();

        public Dictionary<string, string> ComServers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many times the plugin list was read, for tests of reading it again.</summary>
        public int PluginReads { get; private set; }

        /// <summary>Where Decal is installed, as its Agent key says; none unless a test says so.</summary>
        public string AgentPath { get; set; }

        public bool TryReadAgent(out string agentPath, out string portalPath)
        {
            agentPath = AgentPath;
            portalPath = null;
            return AgentPath != null;
        }

        public IReadOnlyList<DecalRegistryEntry> ReadPlugins()
        {
            PluginReads++;
            return Plugins.ToArray();
        }

        public IReadOnlyList<DecalRegistryEntry> ReadServices() => Services;

        public string ReadComServer(string clsid) => clsid != null && ComServers.TryGetValue(clsid, out string path) ? path : null;
    }
}
