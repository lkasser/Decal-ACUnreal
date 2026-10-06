using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AC.Protocol;
using AC.Proxy;

namespace AC.Host.Handover
{
    /// <summary>
    /// What a host stopping while the game is connected leaves for the next host on the same
    /// ports, so that the next carries the session on and the player never notices: the messages
    /// the world was built from, the relay's numbering, and the little the host keeps besides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written to the host's data folder when the host stops properly - <c>achost ctl exit</c>, the
    /// Decal Agent's Exit, the Agent starting its host again - and read, and deleted, by the next
    /// host as it starts. That host takes it up only when the first traffic it relays is the same
    /// session going on, the snapshot was written for the same ports and server, and it is less
    /// than <see cref="MaxAge"/> old; a server forgets a client that has been silent much longer.
    /// </para>
    /// <para>
    /// A host that stops without saying so - a crash, the process killed - leaves nothing, and the
    /// next one says it has joined a session it knows nothing of.
    /// </para>
    /// </remarks>
    public sealed class HandoverSnapshot
    {
        /// <summary>How old a snapshot may be and still be carried on from.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACHANDOV");

        /// <summary>Format of <see cref="Write"/>; a reader refuses any other.</summary>
        private const int FormatVersion = 1;

        /// <summary>Far more than any one message the game sends; a length past it means the bytes are not a handover.</summary>
        private const int LargestMessage = 16 * 1024 * 1024;

        /// <summary>When the host stopped and wrote this.</summary>
        public DateTimeOffset WrittenAt { get; set; }

        /// <summary>The address the relay listened on.</summary>
        public string ListenAddress { get; set; } = string.Empty;

        public int ListenPort { get; set; }

        public string ServerHost { get; set; } = string.Empty;

        public int ServerPort { get; set; }

        public int PortCount { get; set; }

        /// <summary>Whether plugins were allowed to act when the host stopped.</summary>
        public bool ActionsAllowed { get; set; }

        /// <summary>Whether the host stopping had itself joined the session in the middle, knowing only what it had seen since.</summary>
        public bool JoinedMidSession { get; set; }

        /// <summary>Whether that host had told the player so in the game already.</summary>
        public bool ToldJoinedMidSession { get; set; }

        /// <summary>Whom "/r" answers.</summary>
        public string LastTellFrom { get; set; } = string.Empty;

        /// <summary>Whom "/rt" tells again.</summary>
        public string LastTellTo { get; set; } = string.Empty;

        /// <summary>The highest ordering sequence the client had used, which nothing sent may reuse.</summary>
        public uint LastActionSequence { get; set; }

        /// <summary>The messages the world was built from, oldest first.</summary>
        public List<JournalEntry> Journal { get; } = new List<JournalEntry>();

        /// <summary>The relay's state, or null when the host was not relaying.</summary>
        public RelayState Relay { get; set; }

        /// <summary>The name of the file a host listening on <paramref name="listenPort"/> hands over in.</summary>
        public static string PathIn(string dataDirectory, int listenPort)
            => Path.Combine(dataDirectory, "handover-" + listenPort.ToString(CultureInfo.InvariantCulture) + ".bin");
        /// <summary>Whether this was written by a relay with the same ports, to the same server.</summary>
        public bool IsFor(ProxyOptions options)
        {
            if (options == null)
                return false;

            return string.Equals(ListenAddress, options.ListenAddress?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && ListenPort == options.ListenPort
                && string.Equals(ServerHost, options.ServerHost ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && ServerPort == options.ServerPort
                && PortCount == options.PortCount;
        }

        /// <summary>Notes the relay's ports and server, which the next host must share.</summary>
        public void SetEndpoints(ProxyOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            ListenAddress = options.ListenAddress?.ToString() ?? string.Empty;
            ListenPort = options.ListenPort;
            ServerHost = options.ServerHost ?? string.Empty;
            ServerPort = options.ServerPort;
            PortCount = options.PortCount;
        }

        public void Write(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(WrittenAt.UtcTicks);
            writer.Write(ListenAddress ?? string.Empty);
            writer.Write(ListenPort);
            writer.Write(ServerHost ?? string.Empty);
            writer.Write(ServerPort);
            writer.Write(PortCount);
            writer.Write(ActionsAllowed);
            writer.Write(JoinedMidSession);
            writer.Write(ToldJoinedMidSession);
            writer.Write(LastTellFrom ?? string.Empty);
            writer.Write(LastTellTo ?? string.Empty);
            writer.Write(LastActionSequence);

            writer.Write(Journal.Count);
            foreach (JournalEntry entry in Journal)
            {
                writer.Write(entry.At.UtcTicks);
                writer.Write((byte)entry.Direction);
                writer.Write(entry.Opcode);
                writer.Write(entry.Pinned);
                writer.Write(entry.Payload.Length);
                writer.Write(entry.Payload);
            }

            writer.Write(Relay != null);
            Relay?.Write(writer);
        }

        /// <exception cref="InvalidDataException">The bytes are not a snapshot this host can read.</exception>
        public static HandoverSnapshot Read(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            try
            {
                using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                byte[] magic = reader.ReadBytes(Magic.Length);
                if (!magic.AsSpan().SequenceEqual(Magic))
                    throw new InvalidDataException("Not a host's handover (bad magic).");

                int version = reader.ReadInt32();
                if (version != FormatVersion)
                    throw new InvalidDataException($"A handover of format {version}; this host reads {FormatVersion}.");

                HandoverSnapshot snapshot = new HandoverSnapshot
                {
                    WrittenAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero),
                    ListenAddress = reader.ReadString(),
                    ListenPort = reader.ReadInt32(),
                    ServerHost = reader.ReadString(),
                    ServerPort = reader.ReadInt32(),
                    PortCount = reader.ReadInt32(),
                    ActionsAllowed = reader.ReadBoolean(),
                    JoinedMidSession = reader.ReadBoolean(),
                    ToldJoinedMidSession = reader.ReadBoolean(),
                    LastTellFrom = reader.ReadString(),
                    LastTellTo = reader.ReadString(),
                    LastActionSequence = reader.ReadUInt32(),
                };

                int entries = reader.ReadInt32();
                if (entries < 0)
                    throw new InvalidDataException($"A handover claims {entries} messages.");

                for (int i = 0; i < entries; i++)
                {
                    DateTimeOffset at = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                    PacketDirection direction = (PacketDirection)reader.ReadByte();
                    uint opcode = reader.ReadUInt32();
                    bool pinned = reader.ReadBoolean();
                    int length = reader.ReadInt32();
                    if (length < 0 || length > LargestMessage)
                        throw new InvalidDataException($"A message in a handover claims {length} bytes.");

                    byte[] payload = reader.ReadBytes(length);
                    if (payload.Length != length)
                        throw new InvalidDataException("A handover ends inside a message.");

                    snapshot.Journal.Add(new JournalEntry(at, direction, opcode, payload, pinned));
                }

                if (reader.ReadBoolean())
                    snapshot.Relay = RelayState.Read(reader);

                return snapshot;
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("A handover cut short.", ex);
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is OverflowException)
            {
                // Bytes that are not what they claim - a string's length, an address, a port - say
                // so in a way of their own; to whoever reads the file they all mean the same thing.
                throw new InvalidDataException($"A handover that does not read as one ({ex.Message}).", ex);
            }
        }

        /// <summary>Writes the snapshot whole, or not at all: to a file beside it first, then moved into place.</summary>
        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            string writing = path + ".writing";
            using (FileStream file = new FileStream(writing, FileMode.Create, FileAccess.Write, FileShare.None))
                Write(file);

            File.Move(writing, path, overwrite: true);
        }

        /// <summary>
        /// Reads the snapshot at <paramref name="path"/> and deletes it, used or not - one is for the
        /// next host only - and returns it if it can be carried on from: written for these ports and
        /// this server, less than <see cref="MaxAge"/> before <paramref name="now"/>. Otherwise null,
        /// with why in <paramref name="refusal"/>; both null when there was nothing there.
        /// </summary>
        /// <remarks>
        /// "Nothing there" is only ever the file not being found. The file is opened rather than
        /// asked after first: asking whether a file exists answers no for a file that is there but
        /// cannot be looked at, and a handover lost that way would look exactly like none written.
        /// Opened sharing everything, so that whatever else has it open - a scanner, an editor -
        /// does not stand in the way.
        /// </remarks>
        public static HandoverSnapshot Take(string path, ProxyOptions options, DateTimeOffset now, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrEmpty(path))
                return null;

            HandoverSnapshot snapshot;
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    snapshot = Read(file);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                refusal = $"The session handed over in {path} could not be read ({ex.Message}).";
                TryDelete(path);
                return null;
            }

            TryDelete(path);

            TimeSpan age = now - snapshot.WrittenAt;
            if (age > MaxAge || age < -MaxAge)
            {
                refusal = $"The session handed over in {path} is from {snapshot.WrittenAt.ToLocalTime():HH:mm:ss}, too long ago to carry on.";
                return null;
            }

            if (!snapshot.IsFor(options))
            {
                refusal = $"The session handed over in {path} was relayed from {snapshot.ListenAddress}:{snapshot.ListenPort} to {snapshot.ServerHost}:{snapshot.ServerPort}, not where this host relays, so it is not carried on.";
                return null;
            }

            return snapshot;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Read already, and it carries its own time: left behind, it goes stale by itself.
            }
        }
    }
}
