namespace Decal.Interop.Core
{
    // Decal's COM interop assembly, for the little of it plugins used directly. Decal.Adapter
    // handed plugins the underlying COM plugin site, and Virindi's plugins in particular went
    // through it to the client hooks' RenderPreUI event, their per-frame heartbeat. These are
    // those types by the same names, so such plugins load; Decal.Adapter implements them and
    // raises RenderPreUI on the host's tick. Everything else in the COM surface is absent, and a
    // plugin that reaches for it fails where it reaches, not when it loads.

    /// <summary>The client's hooks: here only what plugins were seen to use.</summary>
    public interface IACHooks
    {
        /// <summary>How tall an object stands. The host does not know an object's model, so 0.</summary>
        float ObjectHeight(int lObjectID);
    }

    // COM's event delegates take their method pointer as UIntPtr, where C# writes IntPtr, and
    // plugins built against Decal's interop call the UIntPtr constructor. The build patches
    // these delegates' constructors to match; see the csproj.

    public delegate void IACHooksEvents_RenderPreUIEventHandler();

    public delegate void IACHooksEvents_StatusTextInterceptEventHandler(string bstrText, ref bool bEat);

    /// <summary>The hooks' events, as COM's event interface for them.</summary>
    public interface IACHooksEvents_Event
    {
        /// <summary>Before the interface is drawn, every frame - here, every host tick.</summary>
        event IACHooksEvents_RenderPreUIEventHandler RenderPreUI;

        /// <summary>A line for the status bar. The host has none to intercept, so it is never raised.</summary>
        event IACHooksEvents_StatusTextInterceptEventHandler StatusTextIntercept;
    }

    /// <summary>The hooks as the plugin site hands them out.</summary>
    public interface ACHooks : IACHooks, IACHooksEvents_Event
    {
    }

    /// <summary>A plugin's site in Decal: here, the way to its hooks.</summary>
    public interface IPluginSite2
    {
        ACHooks Hooks { get; }

        object Object { get; }

        object PluginSite { get; }

        void Unload();

        void RegisterSinks(object pPlugin);
    }

    public struct tagRECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }
}
