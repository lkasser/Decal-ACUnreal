using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AC.Host.Runtime;

namespace Decal.Agent
{
    /// <summary>
    /// Decal Agent: Decal for AC:Unreal, as a program of its own. It runs the plugin host - the
    /// relay, the plugins, the overlay's pipe and <c>achost ctl</c>'s - in this process, lists and
    /// manages the plugins the way Decal's agent did, and puts the overlay into the client when the
    /// client starts. The icon in the notification area is Decal while the window is closed.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            AgentCommandLine commandLine;
            try
            {
                commandLine = AgentCommandLine.Parse(args);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + AgentCommandLine.Usage, "Decal Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }

            // One Agent per control pipe: a second on the same one could have none of its ports or
            // pipes, and would only confuse. One started for testing, on a pipe of its own, is fine.
            string controlPipe = commandLine.ControlPipe ?? ControlPipe.ConfiguredName;
            using Mutex single = new Mutex(true, @"Local\DecalAgent-" + controlPipe.Replace('\\', '-'), out bool first);
            if (!first)
            {
                MessageBox.Show("Decal Agent is already running. Its icon is in the notification area.", "Decal Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            string dataDirectory = commandLine.DataDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACHost");

            using FileLog log = new FileLog(Path.Combine(dataDirectory, "logs"));
            log.Info($"Decal Agent {typeof(Program).Assembly.GetName().Version} starting.");

            AgentSettings settings = AgentSettings.Load(dataDirectory, log.Warn);
            AgentHost host = new AgentHost(log);

            using AgentForm form = new AgentForm(host, log, settings, commandLine, dataDirectory);

            // A window handle even when starting hidden, so the host's threads can reach the window.
            _ = form.Handle;
            if (!commandLine.StartInTray)
                form.Show();

            // Started once the message loop runs, so the window paints while the host comes up.
            form.BeginInvoke(new Action(async () => await form.BeginAsync()));

            // No main form: closing the window only hides it. Exit, on the icon's menu, ends the loop.
            Application.Run(new ApplicationContext());
            log.Info("Decal Agent has stopped.");
            return 0;
        }
    }
}
