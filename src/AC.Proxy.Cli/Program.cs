using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AC.Protocol;
using AC.Proxy;

namespace AC.Proxy.Cli
{
    internal static class Program
    {
        private const string Usage = @"acproxy - relay an Asheron's Call session and watch it go by

  acproxy run --server <host> [options]     Relay a live session
  acproxy replay <capture> [--verbose]      Run a capture back through the parser

run options:
  --server <host>        Real server host or address (required)
  --server-port <n>      First real server port                 [9000]
  --listen <address>     Local address to listen on             [127.0.0.1]
  --listen-port <n>      First local port                       [9100]
  --ports <n>            Consecutive ports to relay             [2]
  --capture <file>       Record every datagram to <file>
  --verbose              Print each packet as it passes

Point the client at --listen : --listen-port. ACE uses its port and the next one,
which is why two ports are relayed by default. The listen ports must differ from
the server ports when both run on this machine.";

        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            try
            {
                switch (args[0])
                {
                    case "run":
                        return await RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
                    case "replay":
                        return Replay(args.Skip(1).ToArray());
                    default:
                        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                        Console.Error.WriteLine(Usage);
                        return 1;
                }
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

        private static async Task<int> RunAsync(string[] args)
        {
            ProxyOptions options = new ProxyOptions { ListenPort = 9100 };
            bool verbose = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--server":
                        options.ServerHost = Next(args, ref i);
                        break;
                    case "--server-port":
                        options.ServerPort = ParseInt(Next(args, ref i), "--server-port");
                        break;
                    case "--listen":
                        options.ListenAddress = IPAddress.Parse(Next(args, ref i));
                        break;
                    case "--listen-port":
                        options.ListenPort = ParseInt(Next(args, ref i), "--listen-port");
                        break;
                    case "--ports":
                        options.PortCount = ParseInt(Next(args, ref i), "--ports");
                        break;
                    case "--capture":
                        options.CapturePath = Next(args, ref i);
                        break;
                    case "--verbose":
                        verbose = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            if (options.ServerHost == null)
                throw new ArgumentException("--server is required.");

            using CancellationTokenSource stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            CaptureWriter capture = options.CapturePath != null ? new CaptureWriter(options.CapturePath) : null;
            SessionStatistics stats = new SessionStatistics();

            await using AcProxy proxy = new AcProxy(options);

            proxy.PacketObserved += (_, observed) =>
            {
                capture?.Write(observed);
                stats.Record(observed);

                if (verbose)
                    Console.WriteLine(Describe(observed));
            };

            await proxy.StartAsync(stop.Token).ConfigureAwait(false);

            Console.WriteLine($"Relaying to {options.ServerHost}:{options.ServerPort}-{options.ServerPort + options.PortCount - 1}");
            Console.WriteLine("Listening on " + string.Join(", ", proxy.ListeningOn));
            Console.WriteLine();
            Console.WriteLine($"In the client, add a custom server with host {options.ListenAddress} and port {options.ListenPort}.");
            if (capture != null)
                Console.WriteLine($"Capturing to {options.CapturePath}");
            Console.WriteLine("Ctrl+C to stop.");
            Console.WriteLine();

            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false);

                    if (!verbose)
                        Console.WriteLine(stats.Summary());

                    if (proxy.LoopedBackDatagrams > 0)
                        Console.WriteLine($"  WARNING: {proxy.LoopedBackDatagrams} datagrams came back from this proxy's own port. It is talking to itself.");

                    if (proxy.DatagramsFromClient == 0)
                        Console.WriteLine($"  Nothing from the client yet. Point it at {options.ListenAddress}:{options.ListenPort}.");
                    else if (proxy.DatagramsFromServer == 0)
                        Console.WriteLine($"  Client is sending, but {options.ServerHost}:{options.ServerPort} has not answered. Is the server up on that port?");
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C.
            }
            finally
            {
                capture?.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine("Session totals:");
            Console.WriteLine(stats.Report());

            return 0;
        }

        private static int Replay(string[] args)
        {
            string path = null;
            bool verbose = false;

            foreach (string arg in args)
            {
                if (arg == "--verbose")
                    verbose = true;
                else if (path == null)
                    path = arg;
                else
                    throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            if (path == null)
                throw new ArgumentException("replay needs a capture file.");

            SessionStatistics stats = new SessionStatistics();

            foreach (CapturedDatagram datagram in CaptureReader.Read(path))
            {
                AcPacket.TryParse(datagram.Bytes, out AcPacket packet);

                PacketObservedEventArgs observed = new PacketObservedEventArgs(
                    datagram.Direction,
                    datagram.LocalPort,
                    datagram.Bytes,
                    packet);

                stats.Record(observed);

                if (verbose)
                    Console.WriteLine(Describe(observed));
            }

            Console.WriteLine(stats.Report());
            return 0;
        }

        private static string Describe(PacketObservedEventArgs observed)
        {
            string arrow = observed.Direction == PacketDirection.Outbound ? "C->S" : "S->C";
            string time = observed.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            if (observed.Packet == null)
                return $"{time} {arrow} :{observed.LocalPort} {observed.Datagram.Length,5}B  (did not parse)";

            return $"{time} {arrow} :{observed.LocalPort} {observed.Datagram.Length,5}B  {observed.Packet}";
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"{args[i]} needs a value.");

            return args[++i];
        }

        private static int ParseInt(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                throw new ArgumentException($"{option} needs a number, not '{value}'.");

            return result;
        }
    }

    /// <summary>
    /// What a session looked like at the protocol level: how much of it the parser
    /// understood, and which messages it carried.
    /// </summary>
    /// <remarks>
    /// The opcode histogram is the first thing to look at after a real session, since
    /// it says which message decoders would pay off first.
    /// </remarks>
    internal sealed class SessionStatistics
    {
        private readonly MessageAssembler _clientToServer = new MessageAssembler();
        private readonly MessageAssembler _serverToClient = new MessageAssembler();
        private readonly Dictionary<uint, int> _outboundOpcodes = new Dictionary<uint, int>();
        private readonly Dictionary<uint, int> _inboundOpcodes = new Dictionary<uint, int>();
        private readonly Dictionary<PacketHeaderFlags, int> _flagCombinations = new Dictionary<PacketHeaderFlags, int>();

        private long _outbound;
        private long _inbound;
        private long _unparsed;
        private long _bytes;
        private long _fragments;
        private long _messages;
        private long _plainChecksumChecked;
        private long _plainChecksumBad;

        public void Record(PacketObservedEventArgs observed)
        {
            _bytes += observed.Datagram.Length;

            if (observed.Direction == PacketDirection.Outbound)
                _outbound++;
            else
                _inbound++;

            AcPacket packet = observed.Packet;
            if (packet == null)
            {
                _unparsed++;
                return;
            }

            _flagCombinations[packet.Header.Flags] = _flagCombinations.GetValueOrDefault(packet.Header.Flags) + 1;

            if (!packet.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
            {
                _plainChecksumChecked++;
                if (!packet.HasValidPlainChecksum())
                    _plainChecksumBad++;
            }

            _fragments += packet.Fragments.Count;

            MessageAssembler assembler = observed.Direction == PacketDirection.Outbound ? _clientToServer : _serverToClient;
            Dictionary<uint, int> opcodes = observed.Direction == PacketDirection.Outbound ? _outboundOpcodes : _inboundOpcodes;

            foreach (AcMessage message in assembler.Accept(packet))
            {
                _messages++;
                opcodes[message.Opcode] = opcodes.GetValueOrDefault(message.Opcode) + 1;
            }
        }

        public string Summary()
            => $"C->S {_outbound,6}  S->C {_inbound,6}  messages {_messages,6}  unparsed {_unparsed,4}  {_bytes / 1024,7} KB";

        public string Report()
        {
            List<string> lines = new List<string>
            {
                $"  datagrams        {_outbound + _inbound} ({_outbound} client->server, {_inbound} server->client)",
                $"  bytes            {_bytes}",
                $"  did not parse    {_unparsed}",
                $"  fragments        {_fragments}",
                $"  messages         {_messages}",
                $"  partial pending  {_clientToServer.PendingMessages + _serverToClient.PendingMessages}",
                $"  abandoned        {_clientToServer.AbandonedMessages + _serverToClient.AbandonedMessages}",
                $"  duplicates       {_clientToServer.DuplicateFragments + _serverToClient.DuplicateFragments}",
                $"  rejected frags   {_clientToServer.RejectedFragments + _serverToClient.RejectedFragments}",
                $"  plain checksums  {_plainChecksumChecked} checked, {_plainChecksumBad} bad",
            };

            lines.Add("  header flag combinations:");
            foreach (KeyValuePair<PacketHeaderFlags, int> pair in _flagCombinations.OrderByDescending(p => p.Value).Take(12))
                lines.Add($"    {pair.Value,7}  {pair.Key}");

            lines.Add("  top opcodes, server->client:");
            foreach (KeyValuePair<uint, int> pair in _inboundOpcodes.OrderByDescending(p => p.Value).Take(15))
                lines.Add($"    {pair.Value,7}  0x{pair.Key:X4}");

            lines.Add("  top opcodes, client->server:");
            foreach (KeyValuePair<uint, int> pair in _outboundOpcodes.OrderByDescending(p => p.Value).Take(15))
                lines.Add($"    {pair.Value,7}  0x{pair.Key:X4}");

            return string.Join(Environment.NewLine, lines);
        }
    }
}
