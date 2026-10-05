using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Decal.Agent
{
    /// <summary>
    /// What the command line changes for one run, over the saved settings. Never saved: it exists
    /// so a second Agent can be started beside the player's without touching anything of theirs -
    /// other ports, other pipes, another data folder, and hands off the client.
    /// </summary>
    internal sealed class AgentCommandLine
    {
        public const string Usage = @"DecalAgent - Decal for AC:Unreal

  DecalAgent [options]

  --tray                 Start in the notification area, without the window
  --server <host>        The game server, for this run only
  --server-port <n>      Its first port, for this run only
  --listen-port <n>      The first port the client connects to, for this run only
  --control-pipe <name>  The pipe `achost ctl` talks to (also ACHOST_CONTROL_PIPE)
  --overlay-pipe <name>  The pipe the overlay is published on
  --data <dir>           Where the host keeps its files     [%LOCALAPPDATA%\ACHost]
  --plugins <dir>        Where plugins are installed         [plugins beside DecalAgent.exe]
  --no-dat               Run without client data
  --no-inject            Never put the overlay into the client unasked, whatever Options say
  --set Plugin:Key=Val   A setting a plugin can read (repeatable)";

        public bool StartInTray { get; private set; }

        public string ServerHost { get; private set; }

        public int? ServerPort { get; private set; }

        public int? ListenPort { get; private set; }

        public string ControlPipe { get; private set; }

        public string OverlayPipe { get; private set; }

        public string DataDirectory { get; private set; }

        public string PluginDirectory { get; private set; }

        public bool NoDat { get; private set; }

        public bool NoInject { get; private set; }

        public Dictionary<string, string> Settings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads the command line.</summary>
        /// <exception cref="ArgumentException">Something on it is not understood, in words fit to show.</exception>
        public static AgentCommandLine Parse(string[] args)
        {
            AgentCommandLine line = new AgentCommandLine();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--tray": line.StartInTray = true; break;
                    case "--server": line.ServerHost = Next(args, ref i); break;
                    case "--server-port": line.ServerPort = Port(Next(args, ref i), "--server-port"); break;
                    case "--listen-port": line.ListenPort = Port(Next(args, ref i), "--listen-port"); break;
                    case "--control-pipe": line.ControlPipe = Next(args, ref i); break;
                    case "--overlay-pipe": line.OverlayPipe = Next(args, ref i); break;
                    case "--data": line.DataDirectory = Path.GetFullPath(Next(args, ref i)); break;
                    case "--plugins": line.PluginDirectory = Path.GetFullPath(Next(args, ref i)); break;
                    case "--no-dat": line.NoDat = true; break;
                    case "--no-inject": line.NoInject = true; break;
                    case "--set":
                    {
                        string pair = Next(args, ref i);
                        int eq = pair.IndexOf('=');
                        if (eq <= 0)
                            throw new ArgumentException($"--set needs Plugin:Key=Value, not '{pair}'.");
                        line.Settings[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                        break;
                    }

                    default:
                        throw new ArgumentException($"'{args[i]}' is not something Decal Agent understands.");
                }
            }

            return line;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"{args[i]} needs a value.");

            return args[++i];
        }

        private static int Port(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65534)
                throw new ArgumentException($"{option} needs a port number, not '{value}'.");

            return port;
        }
    }
}
