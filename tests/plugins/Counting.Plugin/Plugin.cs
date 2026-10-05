using System;
using AC.Host.Plugins;

[assembly: PluginApi(HostApi.Version)]

namespace Counting.Plugin
{
    /// <summary>Counts ticks, and forgets to stop listening for them when shut down.</summary>
    public sealed class CountingPlugin : IPlugin
    {
        public string Name => "Counting";

        public int Ticks { get; private set; }

        public int Startups { get; private set; }

        public int Shutdowns { get; private set; }

        public void Startup(IHost host)
        {
            Startups++;
            host.Tick += (_, _) => Ticks++;
        }

        public void Shutdown()
        {
            Shutdowns++;
        }
    }
}
