using AC.Host.Plugins;

// A version no host will ever be. The loader must refuse this and say that the plugin
// is newer than the host, which is a different problem from an unversioned one.
[assembly: PluginApi(999)]

namespace TooNew.Plugin
{
    public sealed class FromTheFuturePlugin : IPlugin
    {
        public string Name => "TooNew";

        public void Startup(IHost host)
        {
        }

        public void Shutdown()
        {
        }
    }
}
