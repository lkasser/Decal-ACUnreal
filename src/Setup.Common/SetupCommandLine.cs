using System;
using System.Collections.Generic;
using System.IO;

namespace Setup.Common
{
    /// <summary>
    /// The setups' and the uninstaller's command line, in the manner installers have long used:
    /// /S for silent, and /D=folder last, taking the rest of the line, spaces and all.
    /// </summary>
    public sealed class SetupCommandLine
    {
        public const string SetupUsage = @"  /S             Install without asking anything
  /D=<folder>    The folder to install into (last; may contain spaces unquoted)
  /Desktop       With /S, also put a shortcut on the desktop
  /Start         With /S, start Decal Agent when done
  /NoShortcuts   Make no shortcuts
  /NoRegistry    Write nothing to the registry: no Apps entry, no record of the folder
  /Log=<file>    Write what was done to this file";

        public const string UninstallUsage = @"  /S               Uninstall without asking anything
  /Product=<id>    DecalAgent (the default) or VirindiTank
  /D=<folder>      The Decal Agent folder (the uninstaller's own, unless given)
  /DeleteSettings  With /S, also delete the settings folder
  /Data=<folder>   The settings folder       [%LOCALAPPDATA%\ACHost]
  /NoRegistry      Leave the registry alone
  /Log=<file>      Write what was done to this file";

        public bool Silent { get; private set; }

        /// <summary>The folder from /D=, made full; null when not given.</summary>
        public string Folder { get; private set; }

        public bool NoRegistry { get; private set; }

        public bool NoShortcuts { get; private set; }

        public bool Desktop { get; private set; }

        public bool Start { get; private set; }

        public string LogPath { get; private set; }

        /// <summary>The uninstaller's /Product=; null when not given.</summary>
        public string Product { get; private set; }

        public bool DeleteSettings { get; private set; }

        /// <summary>The uninstaller's /Data=, made full; null when not given.</summary>
        public string DataFolder { get; private set; }

        /// <summary>Reads the command line.</summary>
        /// <exception cref="ArgumentException">Something on it is not understood, in words fit to show.</exception>
        public static SetupCommandLine Parse(IReadOnlyList<string> args)
        {
            SetupCommandLine line = new SetupCommandLine();
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (arg.Length < 2 || (arg[0] != '/' && arg[0] != '-'))
                    throw new ArgumentException($"'{arg}' is not something this setup understands.");

                string word = arg.Substring(1);
                int eq = word.IndexOf('=');
                string name = eq < 0 ? word : word.Substring(0, eq);
                string value = eq < 0 ? null : word.Substring(eq + 1);

                switch (name.ToUpperInvariant())
                {
                    case "S": line.Silent = true; break;
                    case "NOREGISTRY": line.NoRegistry = true; break;
                    case "NOSHORTCUTS": line.NoShortcuts = true; break;
                    case "DESKTOP": line.Desktop = true; break;
                    case "START": line.Start = true; break;
                    case "DELETESETTINGS": line.DeleteSettings = true; break;

                    case "D":
                    {
                        // The rest of the line, as an unquoted folder with spaces arrives in pieces;
                        // up to the next switch, so a quoted one may come before others too.
                        List<string> parts = new List<string> { Value(arg, value) };
                        while (i + 1 < args.Count && !args[i + 1].StartsWith("/", StringComparison.Ordinal))
                            parts.Add(args[++i]);
                        line.Folder = Full(string.Join(" ", parts), "/D");
                        break;
                    }

                    case "LOG": line.LogPath = Full(Value(arg, value), "/Log"); break;
                    case "PRODUCT": line.Product = Value(arg, value); break;
                    case "DATA": line.DataFolder = Full(Value(arg, value), "/Data"); break;

                    default:
                        throw new ArgumentException($"'{arg}' is not something this setup understands.");
                }
            }

            return line;
        }

        private static string Value(string arg, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{arg.TrimEnd('=')} needs a value after '='.");

            return value.Trim().Trim('"');
        }

        private static string Full(string path, string option)
        {
            try
            {
                return Paths.Normalize(path.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new ArgumentException($"{option} needs a folder, not '{path}'.");
            }
        }
    }
}
