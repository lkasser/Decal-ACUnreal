using System;
using System.Text;
using System.Windows.Forms;
using Setup.Common;

namespace Decal.Setup
{
    /// <summary>Decal Agent's setup wizard: where to install it, which shortcuts, and whether to start it at the end.</summary>
    internal sealed class DecalSetupWizard : SetupWizard
    {
        private readonly DecalSetup _setup;
        private readonly CheckBox _startMenu;
        private readonly CheckBox _desktop;
        private readonly CheckBox _start;

        public DecalSetupWizard(DecalSetup setup)
            : base(SetupProduct.DecalAgent, setup.Log)
        {
            _setup = setup;

            SetWelcome("Welcome to Decal Agent Setup", "Decal for AC:Unreal, version " + setup.Version + ".",
                "This installs Decal Agent: Decal for AC:Unreal. It runs the plugins - Decal's own, and Virindi Tank once it is installed - "
                + "and puts Decal's overlay into the game." + Environment.NewLine + Environment.NewLine
                + "It installs for you alone and needs no administrator. Nothing has to be installed first: the .NET runtime it runs on comes with it."
                + Environment.NewLine + Environment.NewLine
                + "If Decal Agent is running, exit it before going on: Exit, on its icon's menu in the notification area."
                + Environment.NewLine + Environment.NewLine
                + "Press Next to go on.");

            SetFolderStep("Choose Install Location", "Choose the folder to install Decal Agent in.",
                "Setup will install Decal Agent in this folder. To install it somewhere else, press Browse and choose another folder.",
                setup.Folder, "The folder to install Decal Agent in");

            _startMenu = AddFolderOption("Add Decal Agent to the &Start menu", !setup.NoShortcuts);
            _desktop = AddFolderOption("Put a shortcut on the &desktop", false);
            _startMenu.Enabled = _desktop.Enabled = !setup.NoShortcuts;

            StringBuilder note = new StringBuilder();
            note.Append($"It needs about {setup.Megabytes} MB. Your settings, in {AgentFolder.DefaultDataFolder}, are kept when Decal Agent is reinstalled or uninstalled.");
            if (setup.NoRegistry || setup.NoShortcuts)
            {
                note.Append(" Trial run: ");
                note.Append(setup.NoRegistry && setup.NoShortcuts ? "nothing is written to the registry, and no shortcuts are made."
                    : setup.NoRegistry ? "nothing is written to the registry." : "no shortcuts are made.");
            }

            SetFolderNote(note.ToString());

            SetInstallingStep("Installing", "Please wait while Setup installs Decal Agent.", "Copying Decal Agent's files...");
            SetFinishStep("Decal Agent is installed", "Setup has finished.");

            // A trial run starts nothing on its own: an Agent started from it would take the
            // ports and pipes of the one the player is using.
            _start = AddFinishOption("S&tart Decal Agent now", !setup.NoRegistry);
            EndSetUp();
        }

        protected override bool ReadyToInstall(string folder)
        {
            while (DecalSetup.AgentRunningIn(folder))
            {
                if (!AskToRetry($"Decal Agent is running from {folder}, so its files cannot be replaced. Exit it - Exit, on its icon's menu in the notification area - and press Retry."))
                    return false;
            }

            if (DecalSetup.HoldsOtherThings(folder)
                && !Ask($"{folder} already has other things in it. Install Decal Agent there anyway?", MessageBoxIcon.Warning))
                return false;

            return true;
        }

        protected override InstallResult Install(string folder, IProgress<SetupProgress> progress)
            => _setup.Install(folder, _startMenu.Checked, _desktop.Checked, progress);

        protected override string DescribeResult(InstallResult result)
        {
            StringBuilder text = new StringBuilder();
            text.Append($"Decal Agent {_setup.Version} is installed in {result.AgentFolder}.");
            if (result.Replaced)
                text.Append(" It replaced the copy that was there; your settings are as you left them.");

            text.AppendLine().AppendLine();
            text.Append(result.Shortcuts.Count > 0 ? "Start it from the Start menu. " : "Start it from DecalAgent.exe in that folder. ");
            text.Append("While it runs its icon sits in the notification area. Point AC:Unreal at 127.0.0.1, port 9100; Options, in Decal Agent's window, shows the port and changes it.");
            text.AppendLine().AppendLine();
            text.Append("To add Virindi Tank, run VirindiTankSetup.exe. To uninstall Decal Agent, use Installed apps in Windows' Settings, or Uninstall.exe in its folder.");
            return text.ToString();
        }

        protected override void Finished()
        {
            if (_start.Checked && Result != null)
                _setup.StartAgent(Result.AgentFolder);
        }
    }
}
