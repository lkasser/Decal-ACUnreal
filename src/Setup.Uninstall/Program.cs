using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Setup.Common;

namespace Setup.Uninstall
{
    /// <summary>
    /// Uninstall.exe, in Decal Agent's folder: uninstalls Decal Agent - and Virindi Tank with it,
    /// if its setup put it in the Agent's plugins folder - or, with /Product=VirindiTank, only
    /// Virindi Tank. Apps runs it for either. The settings are kept unless the player ticks the
    /// box to delete them too.
    /// </summary>
    /// <remarks>
    /// It runs on the runtime in the folder it is deleting. So everything it will need is loaded
    /// before anything is deleted, and what it cannot delete while it runs - itself, and the
    /// parts of the runtime it has loaded - is left to <see cref="DeferredDelete"/>, which takes
    /// them out once it has exited.
    /// </remarks>
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
                    MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + "Uninstall [options]" + Environment.NewLine + Environment.NewLine + SetupCommandLine.UninstallUsage,
                        "Decal Agent Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return SetupExitCode.BadCommandLine;
            }

            SetupLog log = new SetupLog(line.LogPath);
            SetupProduct product = line.Product == null ? SetupProduct.DecalAgent : SetupProduct.Find(line.Product);
            if (product == null)
            {
                Say(line, log, $"There is no product called {line.Product}: DecalAgent or VirindiTank.", TaskDialogIcon.Warning);
                return SetupExitCode.BadCommandLine;
            }

            string folder = line.Folder ?? Paths.Normalize(AppContext.BaseDirectory);
            string caption = product.DisplayName + " Uninstall";
            IUserRegistry registry = line.NoRegistry ? null : new CurrentUserRegistry();
            bool listed = File.Exists(InstallManifest.PathIn(folder, product));
            if (!listed && (registry == null || !UninstallEntry.PointsAt(registry, product, folder)))
            {
                Say(line, log, $"{product.DisplayName} is not installed in {folder}: there is no list of what its setup installed there.", TaskDialogIcon.Warning);
                return SetupExitCode.Failed;
            }

            LoadEverythingNeeded();

            string data = line.DataFolder ?? AgentFolder.DefaultDataFolder;
            string settings = product.DataSubfolder.Length == 0 ? data : Path.Combine(data, product.DataSubfolder);
            bool deleteSettings = line.DeleteSettings;
            IReadOnlyList<SetupProduct> taken = product == SetupProduct.DecalAgent
                ? Uninstaller.InstalledIn(folder).Where(other => other != product).ToList()
                : Array.Empty<SetupProduct>();

            if (!line.Silent && !Confirm(product, folder, settings, taken, out deleteSettings))
                return SetupExitCode.Failed;

            // The Agent holds its own files open while it runs; a plugin's it holds a copy of, so
            // Virindi Tank can go from under a running Agent and stops when the Agent next starts.
            while (product == SetupProduct.DecalAgent && RunningPrograms.AgentRunningIn(folder))
            {
                string running = $"Decal Agent is running from {folder}. Exit it - Exit, on its icon's menu in the notification area - and then uninstall.";
                if (line.Silent)
                {
                    log.Error(running);
                    return SetupExitCode.AgentRunning;
                }

                if (!AskToRetry(caption, running))
                    return SetupExitCode.Failed;
            }

            log.Info($"Uninstalling {product.DisplayName} from {folder}" + (deleteSettings ? $", and deleting {settings}." : "."));
            UninstallResult result = Uninstaller.Uninstall(new UninstallRequest
            {
                Product = product,
                AgentFolder = folder,
                Registry = registry,
                DeleteSettings = deleteSettings,
                DataFolder = data,
            });

            if (result.InUse.Count > 0)
            {
                string script = DeferredDelete.Write(result.InUse, result.FoldersToRemove);
                DeferredDelete.Start(script)?.Dispose();
                log.Info($"{result.InUse.Count} files in use are deleted once this exits, by {script}.");
            }

            log.Info($"Uninstalled {string.Join(" and ", result.Uninstalled)}: {result.Removed} files deleted"
                + (result.LeftBehind.Count > 0 ? "; left in the folder, as no setup put them there: " + string.Join(", ", result.LeftBehind) : string.Empty)
                + (result.SettingsDeleted ? $"; {result.SettingsFolder} deleted." : "."));
            if (result.SettingsProblem != null)
                log.Warn("The settings were not deleted: " + result.SettingsProblem);

            if (!line.Silent)
                Done(product, folder, result);

            return SetupExitCode.Success;
        }

        /// <summary>
        /// Loads every assembly the uninstaller refers to, directly or not, before it deletes
        /// any: loaded, each is held open and survives; not yet loaded, it would be deleted out
        /// from under the uninstaller and fail it the moment it was first needed.
        /// </summary>
        private static void LoadEverythingNeeded()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<Assembly> waiting = new Queue<Assembly>();
            waiting.Enqueue(typeof(Program).Assembly);
            while (waiting.Count > 0)
            {
                foreach (AssemblyName name in waiting.Dequeue().GetReferencedAssemblies())
                {
                    if (!seen.Add(name.FullName))
                        continue;

                    try
                    {
                        waiting.Enqueue(Assembly.Load(name));
                    }
                    catch (Exception ex) when (ex is FileNotFoundException || ex is FileLoadException || ex is BadImageFormatException)
                    {
                        // Referred to but never used on this platform.
                    }
                }
            }
        }

        private static bool Confirm(SetupProduct product, string folder, string settings, IReadOnlyList<SetupProduct> taken, out bool deleteSettings)
        {
            StringBuilder text = new StringBuilder();
            text.Append($"This removes {product.DisplayName} from {folder}");
            if (taken.Count > 0)
                text.Append(", with " + string.Join(" and ", taken) + ", which is installed in its plugins folder");
            text.Append(product == SetupProduct.DecalAgent ? ", and its shortcuts and its entry in Apps." : ", and its entry in Apps.");
            text.AppendLine().AppendLine();
            text.Append($"Your settings, in {settings}, are kept unless you tick the box below.");

            TaskDialogButton uninstall = new TaskDialogButton("&Uninstall");
            TaskDialogButton cancel = TaskDialogButton.Cancel;
            TaskDialogPage page = new TaskDialogPage
            {
                Caption = product.DisplayName + " Uninstall",
                Heading = $"Uninstall {product.DisplayName}?",
                Text = text.ToString(),
                Icon = TaskDialogIcon.Warning,
                Verification = new TaskDialogVerificationCheckBox(product == SetupProduct.DecalAgent
                    ? "Also delete my settings and logs"
                    : "Also delete its settings here"),
                Buttons = { uninstall, cancel },
                DefaultButton = cancel,
            };

            bool chosen = TaskDialog.ShowDialog(page) == uninstall;
            deleteSettings = chosen && page.Verification.Checked;
            return chosen;
        }

        private static void Done(SetupProduct product, string folder, UninstallResult result)
        {
            StringBuilder text = new StringBuilder();
            text.Append($"{string.Join(" and ", result.Uninstalled)} {(result.Uninstalled.Count > 1 ? "were" : "was")} removed from {folder}.");
            if (result.LeftBehind.Count > 0)
            {
                text.AppendLine().AppendLine();
                text.Append("Left in the folder, as no setup put them there: " + string.Join(", ", result.LeftBehind) + ".");
            }

            if (product != SetupProduct.DecalAgent && RunningPrograms.In(folder, "DecalAgent").Count > 0)
            {
                text.AppendLine().AppendLine();
                text.Append("Decal Agent is running, and it keeps running its copy of " + product.DisplayName + " until it is next started.");
            }

            text.AppendLine().AppendLine();
            text.Append(result.SettingsDeleted
                ? $"Your settings, in {result.SettingsFolder}, were deleted."
                : result.SettingsProblem != null
                    ? $"Your settings, in {result.SettingsFolder}, could not be deleted: {result.SettingsProblem}"
                    : $"Your settings are kept in {result.SettingsFolder}.");

            TaskDialog.ShowDialog(new TaskDialogPage
            {
                Caption = product.DisplayName + " Uninstall",
                Heading = $"{product.DisplayName} is uninstalled",
                Text = text.ToString(),
                Icon = TaskDialogIcon.Information,
                Buttons = { TaskDialogButton.OK },
            });
        }

        private static bool AskToRetry(string caption, string text)
        {
            TaskDialogButton retry = TaskDialogButton.Retry;
            return TaskDialog.ShowDialog(new TaskDialogPage
            {
                Caption = caption,
                Text = text,
                Icon = TaskDialogIcon.Warning,
                Buttons = { retry, TaskDialogButton.Cancel },
            }) == retry;
        }

        /// <summary>Tells the player, or with /S only the log.</summary>
        private static void Say(SetupCommandLine line, SetupLog log, string text, TaskDialogIcon icon)
        {
            log.Error(text);
            if (!line.Silent)
                TaskDialog.ShowDialog(new TaskDialogPage { Caption = "Decal Agent Uninstall", Text = text, Icon = icon, Buttons = { TaskDialogButton.OK } });
        }
    }
}
