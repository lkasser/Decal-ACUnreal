using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Protocol;
using AC.Host.Plugins;
using AC.Host.Runtime;
using AC.Host.Transport;
using AC.Host.World;
using AC.Proxy;

namespace AC.Host.Cli
{
    internal static class Program
    {
        private const string Usage = @"achost - plugin host for Asheron's Call clients

  achost run --server <host> [options]      Host plugins over a live relay
  achost replay <capture> [options]         Host plugins over a recorded session
  achost ctl <command>                      Drive the running host: status, act on|off,
                                            reload [plugin], enable|disable <plugin>, rescan

run options:
  --server <host>        Real server host or address (required)
  --server-port <n>      First real server port                 [9000]
  --listen <address>     Local address to listen on             [127.0.0.1]
  --listen-port <n>      First local port                       [9100]
  --ports <n>            Consecutive ports to relay             [2]
  --capture <file>       Record the session to <file>

  --enable-actions       Let plugins act in the game (see below)
  --test-action          Once logged in, appraise your own character once, to
                         prove injection works end to end. Needs --enable-actions.
  --test-move            Once logged in, walk forward for one second and stop,
                         then report how far the character actually moved. Moves
                         your character. Needs --enable-actions.
  --overlay              Publish what the plugins are showing to an injected
                         overlay, so it appears over the game itself.
  --overlay-pipe <name>  The pipe to publish on. Only needed to run two hosts
                         at once, and then the overlay must be told the same.

common options:
  --dat <file|dir>       client_portal.dat, or a folder holding it. Gives spell
                         names, palette colours and the game's own skill
                         formulas. Found automatically if not given.
  --no-dat               Run without client data
  --plugins <dir>        Plugin directory                       [<exe dir>/plugins]
  --no-plugins           Run with no plugins, just the world model
  --set Plugin:Key=Val   A setting a plugin can read (repeatable)
  --data <dir>           Where plugins keep their files         [%LOCALAPPDATA%/ACHost]
  --chat                 Print chat as it arrives
  --objects              Print objects as they are created and appraised
  --dump-action <hex>    Print the bytes of the first 20 client actions of this
                         type, for working out a layout (e.g. 0xF753)
  --dump-event <hex>     The same for inbound game events (e.g. 0x01C7)
  --dump-opcode <hex>    The same for inbound messages by opcode (e.g. 0xF74E)
  --quiet                No periodic status line

Point the client at --listen : --listen-port.

Without --enable-actions the host starts out only watching: plugins can read the
world but every action reports failure, until ""Let plugins act"" is ticked in
Decal's window in the game (or `achost ctl act on`). With it, plugins can make your
character act from the start - appraise, use, move items, speak - by weaving
messages into the client's own packets. Turn it on deliberately.";

        /// <summary>How many of one type the dumps print: 20, or ACHOST_DUMP_LIMIT.</summary>
        private static readonly int DumpLimit = int.TryParse(Environment.GetEnvironmentVariable("ACHOST_DUMP_LIMIT"), out int limit) && limit > 0 ? limit : 20;

        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            try
            {
                return args[0] switch
                {
                    "run" => await RunAsync(args.Skip(1).ToArray(), live: true).ConfigureAwait(false),
                    "replay" => await RunAsync(args.Skip(1).ToArray(), live: false).ConfigureAwait(false),
                    "ctl" => await ControlAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                    _ => Unknown(args[0]),
                };
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        /// <summary>Sends a command to the running host - what Decal's window does, from a script.</summary>
        private static async Task<int> ControlAsync(string[] args)
        {
            string command = args.Length == 0 ? "status" : string.Join(' ', args);
            try
            {
                string reply = await ControlPipe.SendAsync(command, ControlPipe.ConfiguredName).ConfigureAwait(false);
                Console.Write(reply);
                if (!reply.EndsWith('\n'))
                    Console.WriteLine();
                return 0;
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("No host is running (nothing answered on the control pipe).");
                return 1;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("The host did not answer: " + ex.Message);
                return 1;
            }
        }

        private static int Unknown(string command)
        {
            Console.Error.WriteLine($"Unknown command '{command}'.");
            Console.Error.WriteLine(Usage);
            return 1;
        }

        private static async Task<int> RunAsync(string[] args, bool live)
        {
            // What runs is the runtime's to decide, the same one the Decal Agent starts; this
            // only reads the command line into it, and adds what a console is good for - the
            // dumps, the tests and the status line.
            HostRuntimeOptions options = new HostRuntimeOptions();
            ProxyOptions proxyOptions = options.Proxy;
            string capturePath = null;
            bool testAction = false;
            bool testMove = false;
            bool printChat = false;
            bool printObjects = false;
            int dumpAction = -1;
            int dumpEvent = -1;
            int dumpOpcode = -1;
            bool quiet = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--server": proxyOptions.ServerHost = Next(args, ref i); break;
                    case "--server-port": proxyOptions.ServerPort = ParseInt(Next(args, ref i), "--server-port"); break;
                    case "--listen": proxyOptions.ListenAddress = IPAddress.Parse(Next(args, ref i)); break;
                    case "--listen-port": proxyOptions.ListenPort = ParseInt(Next(args, ref i), "--listen-port"); break;
                    case "--ports": proxyOptions.PortCount = ParseInt(Next(args, ref i), "--ports"); break;
                    case "--capture": proxyOptions.CapturePath = Next(args, ref i); break;
                    case "--plugins": options.PluginDirectory = Next(args, ref i); break;
                    case "--no-plugins": options.NoPlugins = true; break;
                    case "--data": options.DataDirectory = Next(args, ref i); break;
                    case "--enable-actions": options.EnableActions = true; break;
                    case "--dat": options.DatPath = Next(args, ref i); break;
                    case "--no-dat": options.NoDat = true; break;
                    case "--test-action": testAction = true; break;
                    case "--test-move": testMove = true; break;
                    case "--overlay": options.Overlay = true; break;
                    case "--overlay-pipe": options.OverlayPipeName = Next(args, ref i); options.Overlay = true; break;
                    case "--control-pipe": options.ControlPipeName = Next(args, ref i); break;
                    case "--chat": printChat = true; break;
                    case "--objects": printObjects = true; break;
                    case "--dump-action": dumpAction = (int)ParseOpcode(Next(args, ref i), "--dump-action"); break;
                    case "--dump-event": dumpEvent = (int)ParseOpcode(Next(args, ref i), "--dump-event"); break;
                    case "--dump-opcode": dumpOpcode = (int)ParseOpcode(Next(args, ref i), "--dump-opcode"); break;
                    case "--quiet": quiet = true; break;
                    case "--set":
                    {
                        string pair = Next(args, ref i);
                        int eq = pair.IndexOf('=');
                        if (eq <= 0) throw new ArgumentException($"--set needs Plugin:Key=Value, not '{pair}'.");
                        options.Settings[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                        break;
                    }
                    default:
                        if (!live && capturePath == null && !args[i].StartsWith("--", StringComparison.Ordinal))
                        {
                            capturePath = args[i];
                            break;
                        }

                        throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            ConsoleLog log = new ConsoleLog();

            if (live)
            {
                if (proxyOptions.ServerHost == null)
                    throw new ArgumentException("--server is required.");
            }
            else
            {
                if (capturePath == null)
                    throw new ArgumentException("replay needs a capture file.");

                options.ReplayPath = capturePath;
            }

            using CancellationTokenSource stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            await using HostRuntime runtime = new HostRuntime(options, log);

            // `achost ctl exit` from another window: as Ctrl+C here.
            if (runtime.Control != null)
                runtime.Control.ExitRequested += (_, _) => stop.Cancel();

            return await HostAsync(runtime, log, live, testAction, testMove, printChat, printObjects, dumpAction, dumpEvent, dumpOpcode, quiet, stop);
        }

        private static async Task<int> HostAsync(
            HostRuntime runtime,
            ConsoleLog log,
            bool live,
            bool testAction,
            bool testMove,
            bool printChat,
            bool printObjects,
            int dumpAction,
            int dumpEvent,
            int dumpOpcode,
            bool quiet,
            CancellationTokenSource stop)
        {
            GameHost host = runtime.Host;
            IGameTransport transport = runtime.Transport;

            if (printObjects)
            {
                host.DamageTaken += (_, d) => Console.WriteLine($"  damage   {d}");
                host.AttackEvaded += (_, who) => Console.WriteLine($"  evaded   {who}");
                host.EnchantmentChanged += (_, e) => Console.WriteLine($"  buff     {e}");
            }

            if (printChat)
                host.ChatReceived += (_, chat) => Console.WriteLine($"  chat  {chat}");

            if (printObjects)
            {
                host.ObjectCreated += (_, obj) => Console.WriteLine($"  create   {obj}{Placement(obj)}");
                host.ObjectAppraised += (_, obj) => Console.WriteLine($"  appraise {obj}{Placement(obj)} ints={obj.Ints.Count} floats={obj.Floats.Count} spells={obj.SpellIds.Count}");
            }

            if (dumpEvent >= 0)
            {
                // Game events share one opcode too, and the interesting ones - a use
                // finishing, a blow landing - are all in here.
                uint wantedEvent = (uint)dumpEvent;
                int shownEvents = 0;

                transport.MessageReceived += (_, e) =>
                {
                    if (e.Direction != PacketDirection.Inbound) return;
                    if (!MessageDecoder.TryReadGameEventType(e.Message, out uint type) || type != wantedEvent) return;
                    if (shownEvents++ >= DumpLimit) return;

                    ReadOnlySpan<byte> payload = e.Message.Payload.Span;
                    Console.WriteLine($"  event 0x{type:X4} len={payload.Length}  {Convert.ToHexString(payload)}");
                };
            }

            if (dumpOpcode >= 0)
            {
                // Top-level inbound messages: the ones the ignored-opcodes report names
                // and the other two dumps cannot reach.
                uint wantedOpcode = (uint)dumpOpcode;
                int shownMessages = 0;

                transport.MessageReceived += (_, e) =>
                {
                    if (e.Direction != PacketDirection.Inbound) return;
                    if (e.Message.Opcode != wantedOpcode) return;
                    if (shownMessages++ >= DumpLimit) return;

                    ReadOnlySpan<byte> payload = e.Message.Payload.Span;
                    Console.WriteLine($"  opcode 0x{wantedOpcode:X4} len={payload.Length}  {Convert.ToHexString(payload)}");
                };
            }

            if (dumpAction >= 0)
            {
                // Every client action shares one opcode, so the only way to learn a
                // layout is to look at the bytes of one type at a time.
                uint wanted = (uint)dumpAction;
                int shown = 0;

                transport.MessageReceived += (_, e) =>
                {
                    if (e.Direction != PacketDirection.Outbound) return;
                    if (!MessageDecoder.TryReadActionType(e.Message, out uint type) || type != wanted) return;
                    if (shown++ >= DumpLimit) return;

                    ReadOnlySpan<byte> payload = e.Message.Payload.Span;
                    Console.WriteLine($"  action 0x{type:X4} len={payload.Length}  {Convert.ToHexString(payload)}");
                };
            }

            if (testMove)
            {
                if (!transport.CanSend)
                {
                    log.Warn("--test-move needs --enable-actions; skipping it.");
                }
                else
                {
                    // Movement is the one action whose success cannot be read from a
                    // reply, because the server does not echo a player's own movement
                    // back to them. What it does do is keep sending the client position
                    // updates, and the client keeps reporting where it thinks it is - so
                    // the honest test is whether the character is somewhere else
                    // afterwards.
                    bool started = false;

                    host.CharacterUpdated += (_, __) =>
                    {
                        if (started || !host.Character.Location.HasValue)
                            return;

                        started = true;
                        Location from = host.Character.Location.Value;

                        log.Info($"[test] Walking forward for one second from {from}.");

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                if (!await host.Actions.WalkForwardAsync().ConfigureAwait(false))
                                {
                                    log.Warn("[test] The walk could not be queued.");
                                    return;
                                }

                                await Task.Delay(1000).ConfigureAwait(false);
                                await host.Actions.StopAsync().ConfigureAwait(false);

                                // Give the client time to report where it ended up.
                                await Task.Delay(1500).ConfigureAwait(false);

                                if (!host.Character.Location.HasValue)
                                {
                                    log.Warn("[test] No position reported after the walk.");
                                    return;
                                }

                                Location to = host.Character.Location.Value;
                                double dx = to.X - from.X;
                                double dy = to.Y - from.Y;
                                double moved = Math.Sqrt((dx * dx) + (dy * dy));

                                if (moved > 0.5)
                                    log.Info($"[test] SUCCESS - the character moved {moved:F1} units to {to}.");
                                else
                                    log.Warn($"[test] The character did not move (was {from}, now {to}). The message was accepted but had no effect.");
                            }
                            catch (Exception ex)
                            {
                                log.Error("[test] The walk failed.", ex);
                            }
                        });
                    };
                }
            }

            if (testAction)
            {
                if (!transport.CanSend)
                {
                    log.Warn("--test-action needs --enable-actions; skipping it.");
                }
                else
                {
                    // Appraising your own character is the smallest thing that proves
                    // the whole path: a message we composed is woven into the client's
                    // packet, renumbered, re-stamped, accepted by the server, and
                    // answered. It changes nothing in the game.
                    bool sent = false;

                    host.PlayerIdentified += (_, id) =>
                    {
                        if (sent)
                            return;

                        sent = true;
                        log.Info($"[test] Injecting one appraisal for your own character (0x{id:X8}).");
                        log.Info("[test] It leaves on the client's next packet; watch for the reply.");

                        _ = host.Actions.AppraiseAsync(id).ContinueWith(t =>
                            log.Info(t.Status == TaskStatus.RanToCompletion && t.Result
                                ? "[test] Queued for the next outgoing packet."
                                : "[test] Could not queue it."));
                    };

                    host.ObjectAppraised += (_, obj) =>
                    {
                        if (obj.Id == host.Character.Id)
                            log.Info($"[test] SUCCESS - the server answered our injected appraisal for \"{obj.Name}\".");
                    };
                }
            }

            await runtime.StartAsync(stop.Token).ConfigureAwait(false);

            if (live)
                log.Info("Ctrl+C to stop.");

            if (live)
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false);
                        if (!quiet)
                            Console.WriteLine(StatusLine(host));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }
            else
            {
                await host.Ended.ConfigureAwait(false);
            }

            Console.WriteLine();
            Console.WriteLine(Report(host));
            return 0;
        }

        /// <summary>
        /// Where an object is, as far as the server has said: the difference between
        /// loot on the ground, something in a pack, and something worn.
        /// </summary>
        private static string Placement(WorldObject obj)
        {
            if (obj.WielderId.HasValue) return $" wielded-by=0x{obj.WielderId.Value:X8}";
            if (obj.ContainerId.HasValue) return $" in=0x{obj.ContainerId.Value:X8}";
            if (obj.Location.HasValue) return $" at={obj.Location.Value}";
            return " placement=unknown";
        }

        private static string StatusLine(GameHost host)
        {
            HostStatistics s = host.Statistics;
            return $"in {s.MessagesInbound,6}  out {s.MessagesOutbound,6}  applied {s.Applied,6}  ignored {s.Ignored,5}  malformed {s.Malformed,3}  objects {host.World.ObjectCount,5}";
        }

        private static string Report(GameHost host)
        {
            HostStatistics s = host.Statistics;
            List<string> lines = new List<string>
            {
                "Session:",
                $"  server            {host.World.ServerName ?? "(never announced)"}",
                $"  player            {(host.Character.Id == 0 ? "(unknown)" : $"0x{host.Character.Id:X8} \"{host.Character.Name}\" level {host.Character.Level}")}",
                $"  messages          {s.MessagesInbound} in, {s.MessagesOutbound} out",
                $"  applied           {s.Applied}",
                $"  ignored           {s.Ignored}",
                $"  malformed         {s.Malformed}",
                $"  plugin exceptions {s.PluginExceptions}",
                $"  objects known     {host.World.ObjectCount}",
                $"  actions           {(host.Actions.IsAvailable ? "enabled" : "disabled")}",
                $"  client data       {(host.GameData.IsAvailable ? "loaded" : "not loaded")}",
                $"  skills known      {host.Character.Skills.Count}",
            };

            if (s.MalformedOpcodes.Count > 0)
            {
                lines.Add("  malformed by opcode (decoder bugs to fix from this capture):");
                foreach (KeyValuePair<uint, long> pair in s.MalformedOpcodes.OrderByDescending(p => p.Value))
                    lines.Add($"    {pair.Value,7}  0x{pair.Key:X4}");
            }

            if (host.Character.Location.HasValue)
            {
                lines.Add($"  position          {host.Character.Location.Value}");
                lines.Add($"  movement          {host.Character.Motion?.ToString() ?? "not seen"}");
                lines.Add($"  sequences         {host.Character.Sequences}");
            }

            if (host.Character.Enchantments.Count > 0)
            {
                lines.Add("  enchantments still active:");
                foreach (AC.Host.World.Enchantment e in host.Character.Enchantments.Values)
                {
                    string name = host.GameData?.GetSpellName(e.SpellId);
                    lines.Add($"    {(string.IsNullOrEmpty(name) ? "spell " + e.SpellId : name),-28} {e}");
                }
            }

            if (s.OutboundActions.Count > 0)
            {
                lines.Add("  client actions sent, most frequent first:");
                foreach (KeyValuePair<uint, long> pair in s.OutboundActions.OrderByDescending(p => p.Value).Take(15))
                    lines.Add($"    {pair.Value,7}  0x{pair.Key:X4}");
            }

            if (s.IgnoredGameEvents.Count > 0)
            {
                lines.Add("  ignored game events (no decoder yet), most frequent first:");
                foreach (KeyValuePair<uint, long> pair in s.IgnoredGameEvents.OrderByDescending(p => p.Value).Take(15))
                    lines.Add($"    {pair.Value,7}  0x{pair.Key:X4}");
            }

            if (s.IgnoredOpcodes.Count > 0)
            {
                lines.Add("  ignored inbound opcodes (no decoder yet), most frequent first:");
                foreach (KeyValuePair<uint, long> pair in s.IgnoredOpcodes.OrderByDescending(p => p.Value).Take(15))
                    lines.Add($"    {pair.Value,7}  0x{pair.Key:X4}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"{args[i]} needs a value.");

            return args[++i];
        }

        /// <summary>An opcode or action type, written the way the protocol writes it.</summary>
        private static uint ParseOpcode(string value, string option)
        {
            string text = value ?? string.Empty;
            bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (hex) text = text.Substring(2);

            if (uint.TryParse(text, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out uint result))
                return result;

            // Bare hex without the prefix is the common way these are written down.
            if (!hex && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result))
                return result;

            throw new ArgumentException($"{option} needs an opcode like 0xF753, not '{value}'.");
        }

        private static int ParseInt(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                throw new ArgumentException($"{option} needs a number, not '{value}'.");

            return result;
        }

        private sealed class ConsoleLog : IPluginLog
        {
            public void Info(string message) => Console.WriteLine($"{Stamp()} {message}");

            public void Warn(string message) => Console.WriteLine($"{Stamp()} WARN {message}");

            public void Error(string message, Exception exception = null)
            {
                Console.Error.WriteLine($"{Stamp()} ERROR {message}");
                if (exception != null)
                    Console.Error.WriteLine($"    {exception.GetType().Name}: {exception.Message}");
            }

            private static string Stamp() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
