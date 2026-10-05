using AC.Host.Plugins;

namespace Unversioned.Plugin
{
    /// <summary>
    /// A plugin with no <c>PluginApi</c> attribute. Deliberately: the loader must refuse
    /// it, and that refusal is worth a test against something real.
    /// </summary>
    public sealed class ForgetfulPlugin : IPlugin
    {
        public string Name => "Unversioned";

        public void Startup(IHost host)
        {
        }

        public void Shutdown()
        {
        }
    }
}
