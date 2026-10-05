using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace AC.Injector
{
    /// <summary>
    /// The command line around <see cref="DllInjector"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately small: injecting into the wrong process, or twice into the right one,
    /// is the kind of mistake that closes a game mid-session, so there is no default
    /// process, no search for something that looks like the client, and no retry. Both
    /// arguments are given or nothing happens.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    internal static class Program
    {
        private const int Success = 0;
        private const int Failed = 1;
        private const int Misused = 2;

        private static int Main(string[] args)
        {
            string process = null;
            string dll = null;
            bool list = false;
            int pid = 0;
            bool unload = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--process":
                    case "-p":
                        if (!TryTake(args, ref i, out process))
                            return Complain("--process needs the name of a running process.");
                        break;

                    case "--dll":
                    case "-d":
                        if (!TryTake(args, ref i, out dll))
                            return Complain("--dll needs the absolute path of a DLL.");
                        break;

                    case "--pid":
                        // A name is not always enough. This client runs a launcher shim
                        // and the game under the same executable name, so "ACUnreal"
                        // can match three processes, and injecting into the wrong one
                        // either does nothing or disturbs a session someone is using.
                        if (!TryTake(args, ref i, out string pidText))
                            return Complain("--pid needs a process id.");

                        if (!int.TryParse(pidText, out pid) || pid <= 0)
                            return Complain($"--pid needs a process id, not '{pidText}'.");

                        break;

                    case "--unload":
                        // Iterating on an injected DLL otherwise means restarting the
                        // game for every change, because the file stays locked while it
                        // is loaded and so cannot even be rebuilt.
                        unload = true;
                        break;

                    case "--list":
                    case "-l":
                        list = true;
                        break;

                    case "--help":
                    case "-h":
                        Usage(Console.Out);
                        return Success;

                    default:
                        return Complain($"'{args[i]}' is not an argument acinject knows.");
                }
            }

            if (list)
            {
                IReadOnlyList<InjectionTarget> candidates = DllInjector.Candidates(process);

                if (candidates.Count == 0)
                {
                    Console.Error.WriteLine(process == null
                        ? "No process with a window was found, which should not happen."
                        : $"Nothing called '{process}' is running.");
                    return Failed;
                }

                foreach (InjectionTarget candidate in candidates)
                    Console.WriteLine(candidate);

                return Success;
            }

            if (dll == null)
                return Complain("--dll is needed.");

            if (pid == 0 && process == null)
                return Complain("Either --pid or --process is needed.");

            if (unload && pid == 0)
                return Complain("--unload needs --pid, because it acts on one named process.");

            InjectionResult result;

            if (unload)
            {
                result = DllUnloader.Unload(pid, dll);

                if (result.Success)
                {
                    // The export only asks; the DLL frees itself on its own thread
                    // afterwards. Waiting for the module to actually go is what tells the
                    // caller the file on disk is free to rebuild.
                    bool gone = DllUnloader.WaitUntilGone(pid, System.IO.Path.GetFileName(dll), TimeSpan.FromSeconds(10));

                    Console.WriteLine(gone
                        ? result.Message
                        : result.Message + " It has not gone yet, so the file may still be locked.");

                    return gone ? 0 : 1;
                }
            }
            else
            {
                result = pid != 0
                    ? DllInjector.InjectInto(pid, dll)
                    : DllInjector.Inject(process, dll);
            }
            (result.Success ? Console.Out : Console.Error).WriteLine(result.Message);
            return result.Success ? Success : Failed;
        }

        private static bool TryTake(string[] args, ref int index, out string value)
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                value = null;
                return false;
            }

            value = args[++index];
            return true;
        }

        private static int Complain(string problem)
        {
            Console.Error.WriteLine(problem);
            Usage(Console.Error);
            return Misused;
        }

        private static void Usage(System.IO.TextWriter writer)
        {
            writer.WriteLine("acinject - loads a DLL into a running process.");
            writer.WriteLine();
            writer.WriteLine("  acinject --process <name> --dll <absolute path>");
            writer.WriteLine("  acinject --pid <id> --dll <absolute path>");
            writer.WriteLine("  acinject --pid <id> --dll <absolute path> --unload");
            writer.WriteLine("  acinject --list [--process <name>]");
            writer.WriteLine();
            writer.WriteLine("  --process, -p  The client's executable name, with or without the .exe.");
            writer.WriteLine("  --pid          A process id, for when a name matches more than one.");
            writer.WriteLine("  --unload       Ask an already-injected DLL to remove itself, and wait for it.");
            writer.WriteLine("  --dll, -d      Absolute path to the DLL. It is resolved inside the target,");
            writer.WriteLine("                 so a relative path is refused rather than guessed at.");
            writer.WriteLine("  --list, -l     Print the processes that could be injected into.");
            writer.WriteLine();
            writer.WriteLine("acinject must run at the same elevation and architecture as the client.");
        }
    }
}
