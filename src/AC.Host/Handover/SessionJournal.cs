using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AC.Host.Decoding;
using AC.Protocol;

namespace AC.Host.Handover
{
    /// <summary>One message the world was built from, as the journal keeps it.</summary>
    public sealed class JournalEntry
    {
        public JournalEntry(DateTimeOffset at, PacketDirection direction, uint opcode, byte[] payload, bool pinned = false)
        {
            At = at;
            Direction = direction;
            Opcode = opcode;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            Pinned = pinned;
        }

        /// <summary>When the host applied it.</summary>
        public DateTimeOffset At { get; }

        public PacketDirection Direction { get; }

        public uint Opcode { get; }

        /// <summary>The bytes after the opcode, a copy of the host's own.</summary>
        public byte[] Payload { get; }

        /// <summary>
        /// Kept even when a later message of its kind about the same object arrives, because it
        /// did something that one does not redo: a step that took the character out of a vendor's
        /// reach, which closed the vendor's window.
        /// </summary>
        public bool Pinned { get; }

        public AcMessage ToMessage() => AcMessage.Create(Opcode, Payload);
    }

    /// <summary>
    /// The messages of the session so far that the world was built from, in order: what a host
    /// started in the middle of the session replays to know everything this one knows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Messages rather than the world they made, because the next host is as often as not a newer
    /// one - the Agent is restarted to update it - and a newer host may read more out of the same
    /// messages: a decoder added, a field kept that was skipped. Replayed through its own decoders
    /// they give it the world it would have had from the start, with nothing to keep in step
    /// between two versions of the world's shape. And the decoders are the only code that knows
    /// what a message does, so replaying is the one way to be sure nothing is left out.
    /// </para>
    /// <para>
    /// The one thing the world does that no message says - letting go of an object out of view
    /// for 25 seconds, as the client does, on the host's tick - is kept as the ObjectDelete it
    /// amounts to. And each message keeps the time it was heard: replayed on the world's clock as
    /// it was then, what was out of view goes when it would have.
    /// </para>
    /// <para>
    /// Kept small by forgetting what can no longer matter. Most of a session is objects moving:
    /// a position, a motion or a velocity for the same object again, each of which the decoder
    /// replaces whole, and the client's own position reports, which replace each other likewise.
    /// Only the latest of each is kept, so the journal grows with the objects seen rather than with
    /// the time spent. The character leaving the world forgets all but the server's name, as the
    /// world does; the session ending forgets everything. Past <see cref="MaxBytes"/> - many hours
    /// of play - the journal gives up, and the session can no longer be handed over.
    /// </para>
    /// <para>Game thread only, like the world.</para>
    /// </remarks>
    public sealed class SessionJournal
    {
        /// <summary>The most the journal holds before giving up on the session.</summary>
        public const long MaxBytes = 64L * 1024 * 1024;

        /// <summary>What an entry costs beyond its payload, near enough.</summary>
        private const int EntryOverhead = 48;

        // Superseded entries are left as holes and swept out once they are half of the list, so
        // that replacing the latest position of an object costs nothing at all.
        private readonly List<JournalEntry> _entries = new List<JournalEntry>();
        private readonly Dictionary<ulong, int> _latest = new Dictionary<ulong, int>();
        private readonly long _maxBytes;
        private int _holes;

        public SessionJournal()
            : this(MaxBytes)
        {
        }

        /// <summary>A journal that gives up past <paramref name="maxBytes"/> rather than <see cref="MaxBytes"/>.</summary>
        public SessionJournal(long maxBytes)
        {
            _maxBytes = maxBytes;
        }

        /// <summary>Messages kept.</summary>
        public int Count => _entries.Count - _holes;

        /// <summary>What the kept messages take, near enough.</summary>
        public long Bytes { get; private set; }

        /// <summary>
        /// True once the session has outgrown the journal: nothing is kept until the session ends,
        /// and there is nothing to hand over.
        /// </summary>
        public bool Overflowed { get; private set; }

        /// <summary>The kept messages, oldest first.</summary>
        public IReadOnlyList<JournalEntry> Entries
        {
            get
            {
                List<JournalEntry> entries = new List<JournalEntry>(Count);
                foreach (JournalEntry entry in _entries)
                {
                    if (entry != null)
                        entries.Add(entry);
                }

                return entries;
            }
        }

        /// <summary>Keeps a message the host has applied, forgetting any it replaces.</summary>
        public void Record(JournalEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (Overflowed)
                return;

            if (TryReadReplacedKey(entry.Direction, entry.Opcode, entry.Payload, out ulong key))
            {
                if (_latest.TryGetValue(key, out int earlier) && _entries[earlier] is JournalEntry replaced && !replaced.Pinned)
                {
                    Forget(earlier);
                    if (_holes > 1024 && _holes * 2 > _entries.Count)
                        Sweep();
                }

                _latest[key] = _entries.Count;
            }

            _entries.Add(entry);
            Bytes += EntryOverhead + entry.Payload.Length;

            if (Bytes > _maxBytes)
            {
                Clear();
                Overflowed = true;
            }
        }

        /// <summary>Forgets everything: the session has ended, and the next starts afresh.</summary>
        public void Clear()
        {
            _entries.Clear();
            _latest.Clear();
            _holes = 0;
            Bytes = 0;
            Overflowed = false;
        }

        /// <summary>Forgets every message but those <paramref name="keep"/> says still matter.</summary>
        public void KeepOnly(Func<JournalEntry, bool> keep)
        {
            if (keep == null) throw new ArgumentNullException(nameof(keep));

            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i] != null && !keep(_entries[i]))
                    Forget(i);
            }

            Sweep();
        }

        private void Forget(int index)
        {
            Bytes -= EntryOverhead + _entries[index].Payload.Length;
            _entries[index] = null;
            _holes++;
        }

        /// <summary>Closes up the holes, and renumbers what the latest of each kind is.</summary>
        private void Sweep()
        {
            if (_holes == 0)
                return;

            int kept = 0;
            Dictionary<int, int> moved = new Dictionary<int, int>(_latest.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i] == null)
                    continue;

                moved[i] = kept;
                _entries[kept++] = _entries[i];
            }

            _entries.RemoveRange(kept, _entries.Count - kept);
            _holes = 0;

            List<KeyValuePair<ulong, int>> latest = new List<KeyValuePair<ulong, int>>(_latest);
            _latest.Clear();
            foreach (KeyValuePair<ulong, int> pair in latest)
            {
                if (moved.TryGetValue(pair.Value, out int now))
                    _latest[pair.Key] = now;
            }
        }

        // ------------------------------------------------------------------- what replaces what

        private const uint KindServerPosition = 1;
        private const uint KindServerMotion = 2;
        private const uint KindServerVelocity = 3;
        private const uint KindClientPosition = 4;
        private const uint KindClientMotion = 5;
        private const uint KindClientSelection = 6;

        /// <summary>
        /// Whether a later message with the same key does everything this one did, so that only
        /// the latest need be kept - and the key if so.
        /// </summary>
        /// <remarks>
        /// Only messages whose decoder replaces the same things whole every time, whatever they
        /// carry:
        /// <list type="bullet">
        /// <item>UpdatePosition, Motion and VectorUpdate, per object: the object's location, its
        /// movement and its velocity, each set outright.</item>
        /// <item>The client's AutonomousPosition and MoveToState: where the character is, its
        /// movement sequences and - for MoveToState - what its body is doing.</item>
        /// <item>The client's IdentifyObject and QueryHealth: what the player has selected.</item>
        /// </list>
        /// The client's ordering sequence, which every action notes, only ever goes up, so the
        /// latest action keeps the highest. Anything else is kept as it came.
        /// </remarks>
        internal static bool TryReadReplacedKey(PacketDirection direction, uint opcode, ReadOnlySpan<byte> payload, out ulong key)
        {
            key = 0;
            uint kind;
            uint subject = 0;

            if (direction == PacketDirection.Inbound)
            {
                switch (opcode)
                {
                    case Opcodes.UpdatePosition:
                        kind = KindServerPosition;
                        break;
                    case Opcodes.Motion:
                        kind = KindServerMotion;
                        break;
                    case Opcodes.VectorUpdate:
                        kind = KindServerVelocity;
                        break;
                    default:
                        return false;
                }

                if (payload.Length < 4)
                    return false;

                subject = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            }
            else
            {
                // The ordering sequence, then the action.
                if (opcode != Opcodes.GameAction || payload.Length < 8)
                    return false;

                switch (BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)))
                {
                    case GameActions.AutonomousPosition:
                        kind = KindClientPosition;
                        break;
                    case GameActions.MoveToState:
                        kind = KindClientMotion;
                        break;
                    case GameActions.IdentifyObject:
                    case GameActions.QueryHealth:
                        kind = KindClientSelection;
                        break;
                    default:
                        return false;
                }
            }

            key = ((ulong)kind << 32) | subject;
            return true;
        }
    }
}
