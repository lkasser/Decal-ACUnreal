using System;
using System.Linq;
using AC.Host.Actions;
using AC.Host.Plugins;

namespace AC.Host
{
    /// <summary>
    /// What the game client's own settings say: which of its own plugins the player has enabled,
    /// its Desktop UI Scale, and where its plugin bar is. Read from its files by whatever runs the
    /// host - never written - and handed in through <see cref="NoteClientSettings"/>.
    /// </summary>
    public sealed partial class GameHost
    {
        /// <summary>
        /// AC:Unreal's own client plugins - its Unattended Combat Manager among them - as the
        /// client's settings say the player has switched them on. <see cref="ClientPluginsState.Unknown"/>
        /// until they have been read.
        /// </summary>
        public ClientPluginsState ClientPlugins { get; private set; } = ClientPluginsState.Unknown;

        /// <summary>Raised on the game thread when <see cref="ClientPlugins"/> changes.</summary>
        public event EventHandler<ClientPluginsState> ClientPluginsChanged;

        /// <summary>
        /// The Desktop UI Scale the player chose in AC:Unreal, from 1 to 3 in quarter steps, which
        /// the client draws its character select at as far as the window lets it; 1 until read.
        /// </summary>
        public double ClientUiScale { get; private set; } = ClientDisplay.DefaultUiScale;

        /// <summary>The client's plugin bar, in its interface units; null while the client's settings are not read.</summary>
        public ClientPluginBar ClientPluginBar { get; private set; }

        /// <summary>
        /// Takes in what the client's settings say now, and says in the log what changed - a
        /// warning when a client plugin that can play the character is enabled. Game thread only.
        /// </summary>
        /// <param name="plugins">Its plugins as its settings.json says; <see cref="ClientPluginsState.Unknown"/> when there is no client to read.</param>
        /// <param name="uiScale">Its Desktop UI Scale, as <see cref="ClientDisplay.ReadDesktopUiScale"/> read it.</param>
        /// <param name="bar">Its plugin bar; null when there is no client to read.</param>
        public void NoteClientSettings(ClientPluginsState plugins, double uiScale, ClientPluginBar bar)
        {
            plugins ??= ClientPluginsState.Unknown;
            uiScale = ClientDisplay.QuarterStep(uiScale);

            ClientPluginsState was = ClientPlugins;
            double wasScale = ClientUiScale;
            ClientPlugins = plugins;
            ClientUiScale = uiScale;
            ClientPluginBar = bar;

            if (uiScale != wasScale)
                _log.Info($"AC:Unreal's Desktop UI Scale is {ClientDisplay.Percent(uiScale)}: its character select is clicked where it draws it at that scale, as far as the window lets it.");

            if (plugins.Equals(was))
                return;

            if (plugins.Known)
            {
                string line = "AC:Unreal's own client plugins: " + plugins.Describe() + ".";
                if (plugins.AutomationEnabled)
                    _log.Warn(line + " " + AutomationWarning(plugins));
                else
                    _log.Info(line);
            }
            else if (was.Known)
            {
                _log.Info("AC:Unreal's own client plugins are no longer known: its settings cannot be read.");
            }

            ClientPluginsChanged?.Invoke(this, plugins);
        }

        /// <summary>What the log says of an enabled client plugin that can play the character.</summary>
        private static string AutomationWarning(ClientPluginsState plugins)
        {
            string names = string.Join(" and ", plugins.Automating.Select(p => p.Name));
            return $"{names} can play the character beside Virindi Tank's macro, and the two would fight over targets, buffs and corpses."
                + " Enabled is not running - a client plugin acts only once its Start is pressed - but switch it off in the client's plugin list (/plugins) while using Virindi Tank.";
        }
    }
}
