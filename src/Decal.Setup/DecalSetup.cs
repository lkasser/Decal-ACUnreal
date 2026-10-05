using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Setup.Common;

namespace Decal.Setup
{
    /// <summary>
    /// Installing Decal Agent, whichever way it is asked for: what the install is given, the
    /// check that the Agent is not running from the folder, starting it afterwards, and the whole
    /// of a silent run.
    /// </summary>
    internal sealed class DecalSetup
    {
        private readonly SetupCommandLine _line;
        private readonly Payload _payload;

        public DecalSetup(SetupCommandLine line, Payload payload, SetupLog log)
        {
            _line = line ?? throw new ArgumentNullException(nameof(line));
            _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            Log = log ?? SetupLog.None;
            Version = SetupVersion.Of(typeof(DecalSetup).Assembly);
        }

        public SetupLog Log { get; }

        public string Version { get; }

        /// <summary>The folder to offer: the one given with /D, else where the Agent was installed before, else the default.</summary>
        public string Folder => _line.Folder ?? AgentFolder.Find(ReadRegistry()) ?? AgentFolder.DefaultFolder;

        /// <summary>A trial run, writing nothing to the registry; it never starts the Agent unasked.</summary>
        public bool NoRegistry => _line.NoRegistry;

        public bool NoShortcuts => _line.NoShortcuts;

        /// <summary>What the files take up once installed, in megabytes.</summary>
        public long Megabytes => (_payload.TotalLength + 1024 * 1024 - 1) / (1024 * 1024);

        /// <summary>Whether Decal Agent is running from this folder, holding its files open.</summary>
        public static bool AgentRunningIn(string folder) => RunningPrograms.AgentRunningIn(folder);

        /// <summary>Whether the folder has things in it that are not an earlier Decal Agent.</summary>
        public static bool HoldsOtherThings(string folder)
            => Directory.Exists(folder)
               && !AgentFolder.IsAgentFolder(folder)
               && !File.Exists(InstallManifest.PathIn(folder, SetupProduct.DecalAgent))
               && Directory.EnumerateFileSystemEntries(folder).Any();

        public InstallResult Install(string folder, bool startMenu, bool desktop, IProgress<SetupProgress> progress)
        {
            InstallRequest request = new InstallRequest
            {
                Product = SetupProduct.DecalAgent,
                Payload = _payload,
                AgentFolder = folder,
                Version = Version,
                Registry = _line.NoRegistry ? null : new CurrentUserRegistry(),
                Shortcuts = _line.NoShortcuts ? null : new ShellShortcuts(),
                StartMenuFolder = startMenu ? Environment.GetFolderPath(Environment.SpecialFolder.Programs) : null,
                DesktopFolder = desktop ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : null,
            };

            InstallResult result = Installer.Install(request, progress);
            Log.Info($"Decal Agent {Version}: {result.Installed} files in {result.AgentFolder}"
                + (result.Removed > 0 ? $", {result.Removed} left over from the earlier version deleted" : string.Empty)
                + (result.Shortcuts.Count > 0 ? ", shortcuts: " + string.Join("; ", result.Shortcuts) : ", no shortcuts")
                + (result.Registered ? ", registered in Apps." : ", not registered in Apps."));
            return result;
        }

        /// <summary>Starts the installed Agent, as the player would from its shortcut.</summary>
        public void StartAgent(string folder)
        {
            string exe = Path.Combine(folder, AgentFolder.ExeName);
            Log.Info($"Starting {exe}.");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = folder })?.Dispose();
        }

        /// <summary>/S: installs into the folder given or the default, with a Start menu shortcut unless told otherwise.</summary>
        public int RunSilent()
        {
            string folder = Folder;
            Log.Info($"Decal Agent Setup {Version}, silent, into {folder}.");

            if (AgentRunningIn(folder))
            {
                Log.Error($"Decal Agent is running from {folder}. Exit it, then run the setup again.");
                return SetupExitCode.AgentRunning;
            }

            try
            {
                Install(folder, startMenu: true, desktop: _line.Desktop, progress: null);
            }
            catch (SetupException ex)
            {
                Log.Error(ex.Message);
                return ex.ExitCode;
            }

            if (_line.Start)
                StartAgent(Paths.Normalize(folder));

            return SetupExitCode.Success;
        }

        /// <summary>The registry to look for an earlier install in; none for a trial run, which knows nothing of the real one.</summary>
        private IUserRegistry ReadRegistry() => _line.NoRegistry ? null : new CurrentUserRegistry();
    }
}
