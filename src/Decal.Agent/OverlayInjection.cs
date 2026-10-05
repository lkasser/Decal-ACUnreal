using System;
using System.Diagnostics;
using System.IO;
using AC.Host.Runtime;
using AC.Injector;

namespace Decal.Agent
{
    /// <summary>
    /// Finds the overlay DLL and puts it into the client, through the same injector acinject uses
    /// and with all of its refusals: the wrong architecture, a relative path, a DLL already there.
    /// </summary>
    internal static class OverlayInjection
    {
        public const string DllName = "ACUnrealOverlay.dll";

        /// <summary>
        /// The overlay DLL to use: the one Options names; else one beside the Agent, where a
        /// packaged Decal keeps it; else the one the native build leaves in a checkout the Agent was
        /// built in. Null when there is none of them.
        /// </summary>
        public static string Resolve(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? Path.GetFullPath(configured) : null;

            string beside = Path.Combine(AppContext.BaseDirectory, DllName);
            if (File.Exists(beside))
                return beside;

            for (DirectoryInfo folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            {
                string built = Path.Combine(folder.FullName, "native", "build", DllName);
                if (File.Exists(built))
                    return built;
            }

            return null;
        }

        /// <summary>Puts the overlay into the client process <paramref name="processId"/>, or says why not.</summary>
        public static ClientInjection Inject(int processId, string configured)
        {
            string dll = Resolve(configured);
            if (dll == null)
            {
                return new ClientInjection(processId, false, string.IsNullOrWhiteSpace(configured)
                    ? $"{DllName} was not found beside Decal Agent or in a build folder; set where it is in Options."
                    : $"There is no overlay DLL at {configured}; check it in Options.");
            }

            // Already there - put in earlier by this Agent, another host or acinject - is what was
            // wanted, not a failure; the injector's own refusal would read as one.
            if (IsLoaded(processId, Path.GetFileName(dll)))
                return new ClientInjection(processId, true, "The overlay is already in AC:Unreal.");

            InjectionResult result = DllInjector.InjectInto(processId, dll);
            return new ClientInjection(processId, result.Success, result.Message);
        }

        /// <summary>
        /// Whether a module of this name is loaded in the process. False when the process cannot be
        /// read, which leaves the injector to decide - and it refuses rather than risk a second copy.
        /// </summary>
        private static bool IsLoaded(int processId, string module)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                foreach (ProcessModule loaded in process.Modules)
                {
                    if (string.Equals(loaded.ModuleName, module, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception)
            {
                // Gone, or not ours to read.
            }

            return false;
        }
    }
}
