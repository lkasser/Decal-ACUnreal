using System;
using Decal.Interop.Core;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// The COM plugin site Decal.Adapter handed out as PluginHost.Underlying, reduced to what
    /// plugins used of it: the client hooks, and their RenderPreUI event.
    /// </summary>
    /// <remarks>
    /// RenderPreUI was raised every frame, before the interface was drawn, and Virindi's
    /// plugins drove their timers and window updates from it. The host has no frames, so it is
    /// raised on the host's tick, as <see cref="CoreManager.RenderFrame"/> is.
    /// </remarks>
    internal sealed class DecalPluginSite : IPluginSite2
    {
        private readonly ClientHooks _hooks;

        internal DecalPluginSite(DecalRuntime runtime)
        {
            _hooks = new ClientHooks(runtime);
        }

        public ACHooks Hooks => _hooks;

        public object Object => null;

        public object PluginSite => null;

        public void Unload()
        {
        }

        public void RegisterSinks(object pPlugin)
        {
        }

        internal void RaiseRenderPreUI() => _hooks.Raise();

        private sealed class ClientHooks : ACHooks
        {
            private readonly DecalRuntime _runtime;

            internal ClientHooks(DecalRuntime runtime)
            {
                _runtime = runtime;
            }

            public event IACHooksEvents_RenderPreUIEventHandler RenderPreUI;

#pragma warning disable CS0067
            public event IACHooksEvents_StatusTextInterceptEventHandler StatusTextIntercept;
#pragma warning restore CS0067

            public float ObjectHeight(int lObjectID) => 0;

            internal void Raise()
            {
                IACHooksEvents_RenderPreUIEventHandler handler = RenderPreUI;
                if (handler == null)
                    return;

                foreach (Delegate subscriber in handler.GetInvocationList())
                    _runtime.Guard("A RenderPreUI handler", () => ((IACHooksEvents_RenderPreUIEventHandler)subscriber)(), subscriber.Method.DeclaringType?.Assembly);
            }
        }
    }
}
