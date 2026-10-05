using System;
using System.Linq;
using System.Windows.Forms;
using Setup.Common;

namespace Decal.Setup
{
    /// <summary>
    /// DecalAgentSetup.exe: installs Decal Agent - the Agent, the .NET runtime it runs on,
    /// Decal.Compat, the overlay and acinject - for the player running it, by a wizard or, with
    /// /S, without asking anything.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            SetupCommandLine line;
            try
            {
                line = SetupCommandLine.Parse(args);
            }
            catch (ArgumentException ex)
            {
                if (!args.Any(arg => string.Equals(arg, "/S", StringComparison.OrdinalIgnoreCase)))
                    MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + "DecalAgentSetup [options]" + Environment.NewLine + Environment.NewLine + SetupCommandLine.SetupUsage,
                        "Decal Agent Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return SetupExitCode.BadCommandLine;
            }

            SetupLog log = new SetupLog(line.LogPath);
            using Payload payload = Payload.FromAssembly(typeof(Program).Assembly);
            if (payload == null)
            {
                const string Missing = "This setup was built without Decal Agent's files, so it has nothing to install. Build it with tools\\build-installers.ps1.";
                log.Error(Missing);
                if (!line.Silent)
                    MessageBox.Show(Missing, "Decal Agent Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return SetupExitCode.NoPayload;
            }

            DecalSetup setup = new DecalSetup(line, payload, log);
            if (line.Silent)
                return setup.RunSilent();

            using DecalSetupWizard wizard = new DecalSetupWizard(setup);
            Application.Run(wizard);
            return wizard.Installed ? SetupExitCode.Success : SetupExitCode.Failed;
        }
    }
}
