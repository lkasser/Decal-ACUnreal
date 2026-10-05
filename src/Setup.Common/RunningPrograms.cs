using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace Setup.Common
{
    /// <summary>
    /// Which programs are running from inside a folder. Decal Agent running from the folder being
    /// installed over holds its files open; one running from anywhere else - a developer's build,
    /// say - is no business of this setup's.
    /// </summary>
    public static class RunningPrograms
    {
        /// <summary>The process ids of programs by these names whose files are inside <paramref name="folder"/>.</summary>
        public static IReadOnlyList<int> In(string folder, params string[] programNames)
        {
            List<int> found = new List<int>();
            foreach (string name in programNames)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try
                        {
                            if (Paths.IsWithin(process.MainModule?.FileName, folder))
                                found.Add(process.Id);
                        }
                        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is NotSupportedException)
                        {
                            // Gone already, or not this account's to look at - in which case it is
                            // not running from a folder this account installed either.
                        }
                    }
                }
            }

            return found;
        }

        /// <summary>Whether Decal Agent, or acinject which it starts, is running from this folder.</summary>
        public static bool AgentRunningIn(string folder)
            => In(folder, "DecalAgent", "acinject").Count > 0;
    }
}
