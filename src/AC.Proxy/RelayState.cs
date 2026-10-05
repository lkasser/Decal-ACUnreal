using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using AC.Protocol;

namespace AC.Proxy
{
    /// <summary>
    /// Everything a relay carries over so that a session can go on through another relay on the
    /// same ports: whom each port relays for, how each rewriter has renumbered its stream, and the
    /// halves of messages the host was still putting together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relay that stops while the game is connected leaves both ends mid-conversation. The
    /// server keeps the session - it is named after the relay's own endpoint, which the next relay
    /// binds again - and the client never learns anything happened. What the next relay must not
    /// lose is the numbering: every line put into the server's stream moved every later fragment
    /// up one for the client, every action put into the client's stream did the same for the
    /// server, and every message taken out moved them down. A relay that forgot would hand the
    /// client numbers it already has, which it discards as repeats, and the server numbers it has
    /// already seen - and the session would go quiet with nothing to say why.
    /// </para>
    /// <para>
    /// Messages still queued to go out are not carried: they were the stopped host's, and the host
    /// starting has its own to send. Everything else in a <see cref="ClientStreamRewriter"/> is.
    /// </para>
    /// </remarks>
    public sealed class RelayState
    {
        /// <summary>Format of <see cref="Write"/>; a reader refuses any other.</summary>
        private const int FormatVersion = 1;

        /// <summary>The client each relayed port has heard from, by local port.</summary>
        public Dictionary<int, IPEndPoint> Clients { get; } = new Dictionary<int, IPEndPoint>();

        /// <summary>The client's stream to the server, as rewritten; null when nothing rewrote it.</summary>
        public RewriterState Outbound { get; set; }

        /// <summary>The server's stream to the client, as rewritten; null when nothing rewrote it.</summary>
        public RewriterState Inbound { get; set; }

        /// <summary>Fragments of messages from the server not yet all arrived, as the host's assembler held them.</summary>
        public List<AcFragment> InboundPartials { get; } = new List<AcFragment>();

        /// <summary>Fragments of messages from the client not yet all arrived.</summary>
        public List<AcFragment> OutboundPartials { get; } = new List<AcFragment>();

        public void Write(BinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            writer.Write(FormatVersion);
            writer.Write(Clients.Count);
            foreach (KeyValuePair<int, IPEndPoint> client in Clients)
            {
                writer.Write(client.Key);
                writer.Write(client.Value.Address.ToString());
                writer.Write(client.Value.Port);
            }

            RewriterState.WriteOptional(writer, Outbound);
            RewriterState.WriteOptional(writer, Inbound);
            Fragments.WriteList(writer, InboundPartials);
            Fragments.WriteList(writer, OutboundPartials);
        }

        /// <exception cref="InvalidDataException">The bytes are not a relay's state this version can read.</exception>
        public static RelayState Read(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            int version = reader.ReadInt32();
            if (version != FormatVersion)
                throw new InvalidDataException($"A relay's state of format {version}; this relay reads {FormatVersion}.");

            RelayState state = new RelayState();
            int clients = Fragments.ReadCount(reader);
            for (int i = 0; i < clients; i++)
            {
                int port = reader.ReadInt32();
                IPAddress address = IPAddress.Parse(reader.ReadString());
                state.Clients[port] = new IPEndPoint(address, reader.ReadInt32());
            }

            state.Outbound = RewriterState.ReadOptional(reader);
            state.Inbound = RewriterState.ReadOptional(reader);
            state.InboundPartials.AddRange(Fragments.ReadList(reader));
            state.OutboundPartials.AddRange(Fragments.ReadList(reader));
            return state;
        }
    }

    /// <summary>
    /// A <see cref="ClientStreamRewriter"/>'s numbering, and the fragments it was holding, as
    /// <see cref="ClientStreamRewriter.SaveState"/> takes them and
    /// <see cref="ClientStreamRewriter.RestoreState"/> puts them back.
    /// </summary>
    public sealed class RewriterState
    {
        public int Offset { get; set; }

        public bool Renumbered { get; set; }

        public uint LastEmittedSequence { get; set; }

        public uint LastOriginalSequence { get; set; }

        /// <summary>Each change to the numbering still kept apart, oldest first.</summary>
        public List<(uint Anchor, int Delta)> Shifts { get; } = new List<(uint, int)>();

        /// <summary>The numbers of messages taken out, oldest first.</summary>
        public List<uint> Withheld { get; } = new List<uint>();

        /// <summary>Messages being taken out whose fragments have not all arrived: what has.</summary>
        public List<TakenMessageState> Taking { get; } = new List<TakenMessageState>();

        /// <summary>Later fragments held back until the first of their message arrives.</summary>
        public List<AcFragment> Held { get; } = new List<AcFragment>();

        /// <summary>Held fragments let go and numbered, still waiting for room in a packet.</summary>
        public List<AcFragment> LetGo { get; } = new List<AcFragment>();

        public int InjectedMessages { get; set; }

        public int WithheldMessages { get; set; }

        public int WithheldSplitMessages { get; set; }

        public int AbandonedMessages { get; set; }

        public int HeldFragments { get; set; }

        public int SessionResets { get; set; }

        public int UnrewritablePackets { get; set; }

        internal static void WriteOptional(BinaryWriter writer, RewriterState state)
        {
            writer.Write(state != null);
            if (state == null)
                return;

            writer.Write(state.Offset);
            writer.Write(state.Renumbered);
            writer.Write(state.LastEmittedSequence);
            writer.Write(state.LastOriginalSequence);

            writer.Write(state.Shifts.Count);
            foreach ((uint anchor, int delta) in state.Shifts)
            {
                writer.Write(anchor);
                writer.Write(delta);
            }

            writer.Write(state.Withheld.Count);
            foreach (uint sequence in state.Withheld)
                writer.Write(sequence);

            writer.Write(state.Taking.Count);
            foreach (TakenMessageState taking in state.Taking)
            {
                writer.Write(taking.Sequence);
                writer.Write(taking.Count);
                Fragments.WriteList(writer, taking.Parts);
            }

            Fragments.WriteList(writer, state.Held);
            Fragments.WriteList(writer, state.LetGo);

            writer.Write(state.InjectedMessages);
            writer.Write(state.WithheldMessages);
            writer.Write(state.WithheldSplitMessages);
            writer.Write(state.AbandonedMessages);
            writer.Write(state.HeldFragments);
            writer.Write(state.SessionResets);
            writer.Write(state.UnrewritablePackets);
        }

        internal static RewriterState ReadOptional(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
                return null;

            RewriterState state = new RewriterState
            {
                Offset = reader.ReadInt32(),
                Renumbered = reader.ReadBoolean(),
                LastEmittedSequence = reader.ReadUInt32(),
                LastOriginalSequence = reader.ReadUInt32(),
            };

            int shifts = Fragments.ReadCount(reader);
            for (int i = 0; i < shifts; i++)
                state.Shifts.Add((reader.ReadUInt32(), reader.ReadInt32()));

            int withheld = Fragments.ReadCount(reader);
            for (int i = 0; i < withheld; i++)
                state.Withheld.Add(reader.ReadUInt32());

            int taking = Fragments.ReadCount(reader);
            for (int i = 0; i < taking; i++)
            {
                TakenMessageState message = new TakenMessageState(reader.ReadUInt32(), reader.ReadUInt16());
                message.Parts.AddRange(Fragments.ReadList(reader));
                state.Taking.Add(message);
            }

            state.Held.AddRange(Fragments.ReadList(reader));
            state.LetGo.AddRange(Fragments.ReadList(reader));

            state.InjectedMessages = reader.ReadInt32();
            state.WithheldMessages = reader.ReadInt32();
            state.WithheldSplitMessages = reader.ReadInt32();
            state.AbandonedMessages = reader.ReadInt32();
            state.HeldFragments = reader.ReadInt32();
            state.SessionResets = reader.ReadInt32();
            state.UnrewritablePackets = reader.ReadInt32();
            return state;
        }
    }

    /// <summary>A message being taken out of the stream: its number, how many fragments it has, and those in hand.</summary>
    public sealed class TakenMessageState
    {
        public TakenMessageState(uint sequence, ushort count)
        {
            Sequence = sequence;
            Count = count;
        }

        public uint Sequence { get; }

        public ushort Count { get; }

        public List<AcFragment> Parts { get; } = new List<AcFragment>();
    }

    /// <summary>Fragments written as they go on the wire: the header, then the payload it declares.</summary>
    internal static class Fragments
    {
        /// <summary>More of anything than a relay ever holds; a count past it means the bytes are not a relay's state.</summary>
        private const int MostEverHeld = 1 << 20;

        internal static void WriteList(BinaryWriter writer, IReadOnlyCollection<AcFragment> fragments)
        {
            writer.Write(fragments.Count);
            byte[] header = new byte[FragmentHeader.Size];
            foreach (AcFragment fragment in fragments)
            {
                FragmentHeader written = fragment.Header;
                written.TotalSize = (ushort)(FragmentHeader.Size + fragment.Payload.Length);
                written.Write(header);
                writer.Write(header);
                writer.Write(fragment.Payload.Span);
            }
        }

        internal static List<AcFragment> ReadList(BinaryReader reader)
        {
            int count = ReadCount(reader);
            List<AcFragment> fragments = new List<AcFragment>(count);
            for (int i = 0; i < count; i++)
            {
                byte[] header = reader.ReadBytes(FragmentHeader.Size);
                if (header.Length != FragmentHeader.Size || !FragmentHeader.TryParse(header, out FragmentHeader parsed) || !parsed.IsWellFormed)
                    throw new InvalidDataException("A fragment in a relay's state does not parse.");

                byte[] payload = reader.ReadBytes(parsed.PayloadSize);
                if (payload.Length != parsed.PayloadSize)
                    throw new InvalidDataException("A relay's state ends inside a fragment.");

                fragments.Add(new AcFragment(parsed, payload));
            }

            return fragments;
        }

        internal static int ReadCount(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MostEverHeld)
                throw new InvalidDataException($"A relay's state claims {count} of something.");
            return count;
        }
    }
}
