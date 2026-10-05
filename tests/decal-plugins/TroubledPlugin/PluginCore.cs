using System;
using System.Runtime.InteropServices;
using Decal.Adapter;
using Microsoft.Win32;

namespace TroubledPlugin
{
    /// <summary>
    /// Starts, as Virindi's plugins start here, and goes wrong as they go wrong: see the project
    /// file. Every fault is its own and caught, as theirs were, except the login handler's.
    /// </summary>
    [FriendlyName("Troubled")]
    public sealed class PluginCore : PluginBase
    {
        /// <summary>Its own entry in Decal's registry, made up; never there in the 64-bit view.</summary>
        private const string OwnKey = @"SOFTWARE\Decal\Plugins\{0B5C2E1D-7A4F-4C3B-9E21-5D6F7A8B9C0D}";

        protected override void Startup()
        {
            try
            {
                // As Virindi Integrator2 and Global Inventory find their folder.
                string folder = Registry.LocalMachine.OpenSubKey(OwnKey).GetValue("Path").ToString();
                Host.Actions.AddChatText("[TP] Found my folder: " + folder, 5);
            }
            catch (Exception ex)
            {
                // As they reported it.
                MessageBoxW(IntPtr.Zero, ex.ToString(), string.Empty, 0);
            }

            // As Virindi Item Tool reported a stand-in's missing type.
            Host.Actions.AddChatText("[TP] System.TypeLoadException: Could not load type 'VirindiViewService.Controls.HudNothing' from assembly 'VirindiViewService, Version=1.0.0.47, Culture=neutral, PublicKeyToken=null'.", 5);

            Core.CharacterFilter.Login += OnLogin;
        }

        protected override void Shutdown()
        {
            Core.CharacterFilter.Login -= OnLogin;
        }

        private void OnLogin(object sender, EventArgs e) => throw new InvalidOperationException("Troubled at login.");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    }
}
