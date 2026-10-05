using System;
using System.Collections.Generic;
using System.IO;
using AC.Host.Transport;
using AC.Proxy;

namespace AC.Host.Runtime
{
    /// <summary>
    /// Everything that decides what a host runs: where the game is relayed to and from, where
    /// plugins and their files are kept, and whether the overlay and the control pipe are served.
    /// </summary>
    /// <remarks>
    /// The same object whether it was filled from <c>achost run</c>'s command line or from the
    /// Decal Agent's Options dialog, which is the point: the two programs differ in how they are
    /// told what to run, never in what running it means.
    /// </remarks>
    public sealed class HostRuntimeOptions
    {
        /// <summary>Where the relay listens and where it forwards to. Listens on 9100 unless told otherwise.</summary>
        public ProxyOptions Proxy { get; set; } = new ProxyOptions { ListenPort = 9100 };

        /// <summary>A recorded session to replay instead of relaying a live one; null for live.</summary>
        public string ReplayPath { get; set; }

        /// <summary>
        /// A transport to host over instead of the relay or a replay. For tests, which want a
        /// host that behaves as a live one without binding ports a player may be using.
        /// </summary>
        public IGameTransport Transport { get; set; }

        /// <summary>Where installed plugins are found: <c>plugins</c> beside the program by default.</summary>
        public string PluginDirectory { get; set; } = DefaultPluginDirectory;

        /// <summary>The <c>plugins</c> folder beside the running program.</summary>
        public static string DefaultPluginDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");

        /// <summary>Runs with no plugins and no Decal window, just the world model.</summary>
        public bool NoPlugins { get; set; }

        /// <summary>Where plugins keep their files and the host its state; %LOCALAPPDATA%\ACHost when null.</summary>
        public string DataDirectory { get; set; }

        /// <summary>Whether plugins may act from the start, rather than only once the player allows it.</summary>
        public bool EnableActions { get; set; }

        /// <summary>Whether to publish to the injected overlay.</summary>
        public bool Overlay { get; set; }

        /// <summary>The pipe the overlay is published on. Only changed to run two hosts at once.</summary>
        public string OverlayPipeName { get; set; } = AC.Host.Overlay.OverlayServer.DefaultPipeName;

        /// <summary>
        /// The pipe <c>achost ctl</c> talks to, or null for none. ACHOST_CONTROL_PIPE, or
        /// "achost-control", unless told otherwise. A replay never serves one.
        /// </summary>
        public string ControlPipeName { get; set; } = ControlPipe.ConfiguredName;

        /// <summary>client_portal.dat, or a folder holding it; looked for in the usual places when null.</summary>
        public string DatPath { get; set; }

        /// <summary>Runs without client data at all.</summary>
        public bool NoDat { get; set; }

        /// <summary>
        /// Neither takes up a session the host before this one left on the same ports, nor leaves
        /// one for the next. A live host does both unless told not to; a replay never does.
        /// </summary>
        public bool NoHandover { get; set; }

        /// <summary>Settings a plugin can read, keyed "Plugin:Key", as <c>--set</c> gives them.</summary>
        public Dictionary<string, string> Settings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when replaying a recording rather than relaying a live game.</summary>
        public bool IsReplay => ReplayPath != null && Transport == null;
    }
}
