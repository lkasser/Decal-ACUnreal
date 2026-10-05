using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AC.Protocol
{
    /// <summary>
    /// A complete game message: a 4-byte opcode followed by its payload.
    /// </summary>
    public sealed class AcMessage
    {
        /// <summary>Smallest legal message: the opcode and nothing else.</summary>
        public const int MinimumSize = 4;

        internal AcMessage(uint opcode, ReadOnlyMemory<byte> payload, uint fragmentSequence)
        {
            Opcode = opcode;
            Payload = payload;
            FragmentSequence = fragmentSequence;
        }

        /// <summary>
        /// A message to send. <see cref="FragmentSequence"/> is zero because the
        /// number is assigned when it is put on the wire, not when it is composed.
        /// </summary>
        public static AcMessage Create(uint opcode, ReadOnlyMemory<byte> payload)
            => new AcMessage(opcode, payload, 0);

        public uint Opcode { get; }

        /// <summary>The message body, with the opcode already stripped.</summary>
        public ReadOnlyMemory<byte> Payload { get; }

        /// <summary>
        /// The fragment sequence this message was assembled from, for correlating
        /// against packet logs.
        /// </summary>
        public uint FragmentSequence { get; }

        public override string ToString()
            => $"opcode=0x{Opcode:X8} payload={Payload.Length}B";
    }

    /// <summary>
    /// Reassembles fragmented game messages for one direction of one connection.
    /// </summary>
    /// <remarks>
    /// Fragments of a message share a sequence number and carry their index and
    /// total count, and they need not arrive in order or exactly once - UDP allows
    /// reordering, duplication and loss, and the protocol's own retransmission adds
    /// more of the first two. So this keys partial messages by sequence, ignores a
    /// repeat of an index it already holds, and emits a message only when every
    /// index is present.
    ///
    /// Not thread-safe: give each direction its own instance and drive it from one
    /// thread. It is per-direction because the two directions number their fragment
    /// sequences independently.
    ///
    /// A message whose fragments never all arrive would otherwise sit in the buffer
    /// forever, so <see cref="MaxPartialMessages"/> bounds it: the oldest incomplete
    /// message is dropped when the limit is hit. That bound is what keeps a lossy or
    /// hostile peer from growing this without limit.
    /// </remarks>
    public sealed class MessageAssembler
    {
        /// <summary>
        /// How many incomplete messages to hold before dropping the oldest. Well over
        /// what a healthy connection needs; low enough to bound memory.
        /// </summary>
        public const int MaxPartialMessages = 64;

        private readonly Dictionary<uint, PartialMessage> _partials = new Dictionary<uint, PartialMessage>();

        /// <summary>Messages dropped because their fragments never all arrived.</summary>
        public int AbandonedMessages { get; private set; }

        /// <summary>Fragments ignored as duplicates of an index already held.</summary>
        public int DuplicateFragments { get; private set; }

        /// <summary>Fragments rejected as malformed or self-inconsistent.</summary>
        public int RejectedFragments { get; private set; }

        /// <summary>Incomplete messages currently buffered.</summary>
        public int PendingMessages => _partials.Count;

        /// <summary>
        /// Feeds every fragment of a packet in and yields whatever messages that
        /// completed. A packet with no fragments yields nothing.
        /// </summary>
        public IEnumerable<AcMessage> Accept(AcPacket packet)
        {
            if (packet == null)
                throw new ArgumentNullException(nameof(packet));

            List<AcMessage> completed = null;

            foreach (AcFragment fragment in packet.Fragments)
            {
                if (TryAccept(fragment, out AcMessage message))
                {
                    completed ??= new List<AcMessage>(1);
                    completed.Add(message);
                }
            }

            return completed ?? (IEnumerable<AcMessage>)Array.Empty<AcMessage>();
        }

        /// <summary>
        /// Feeds one fragment in. Returns true when it completed a message.
        /// </summary>
        public bool TryAccept(AcFragment fragment, out AcMessage message)
        {
            message = null;

            if (fragment == null)
                throw new ArgumentNullException(nameof(fragment));

            FragmentHeader header = fragment.Header;

            if (!header.IsWellFormed || fragment.Payload.Length != header.PayloadSize)
            {
                RejectedFragments++;
                return false;
            }

            // The common case: a whole message in one fragment, no buffering.
            if (header.Count == 1)
                return TryBuildMessage(header.Sequence, fragment.Payload, out message);

            if (!_partials.TryGetValue(header.Sequence, out PartialMessage partial))
            {
                EvictIfFull();
                partial = new PartialMessage(header.Count);
                _partials[header.Sequence] = partial;
            }
            else if (partial.TotalFragments != header.Count)
            {
                // Two messages claiming the same sequence with different lengths: the
                // buffered one cannot be trusted, so restart from this fragment.
                AbandonedMessages++;
                partial = new PartialMessage(header.Count);
                _partials[header.Sequence] = partial;
            }

            if (!partial.Add(header.Index, fragment.Payload))
            {
                DuplicateFragments++;
                return false;
            }

            if (!partial.IsComplete)
                return false;

            _partials.Remove(header.Sequence);

            return TryBuildMessage(header.Sequence, partial.Concatenate(), out message);
        }

        /// <summary>
        /// The fragments of every message not yet complete, as fragments again, so that another
        /// assembler fed them by <see cref="TryAccept"/> holds exactly what this one does. For a
        /// relay handing its session on to the next; the fragments' ids and queues are not kept,
        /// since nothing here reads them.
        /// </summary>
        public IReadOnlyList<AcFragment> SavePartials()
        {
            List<AcFragment> fragments = new List<AcFragment>();
            foreach (KeyValuePair<uint, PartialMessage> partial in _partials)
                fragments.AddRange(partial.Value.Fragments(partial.Key));

            return fragments;
        }

        /// <summary>
        /// Discards every buffered partial message. Call on reconnect: fragment
        /// sequences restart, so anything held over would be misattributed.
        /// </summary>
        public void Reset()
        {
            AbandonedMessages += _partials.Count;
            _partials.Clear();
        }

        private static bool TryBuildMessage(uint sequence, ReadOnlyMemory<byte> body, out AcMessage message)
        {
            message = null;

            if (body.Length < AcMessage.MinimumSize)
                return false;

            uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body.Span);
            message = new AcMessage(opcode, body.Slice(AcMessage.MinimumSize), sequence);

            return true;
        }

        private void EvictIfFull()
        {
            if (_partials.Count < MaxPartialMessages)
                return;

            // Lowest sequence is the oldest, since sequences ascend within a direction.
            uint oldest = uint.MaxValue;
            bool found = false;

            foreach (uint sequence in _partials.Keys)
            {
                if (!found || sequence < oldest)
                {
                    oldest = sequence;
                    found = true;
                }
            }

            if (found)
            {
                _partials.Remove(oldest);
                AbandonedMessages++;
            }
        }

        private sealed class PartialMessage
        {
            private readonly ReadOnlyMemory<byte>[] _fragments;

            // Presence is tracked separately from the payloads: the wire format permits
            // a zero-length fragment payload, which is indistinguishable from an absent
            // one if emptiness is used as the test.
            private readonly bool[] _present;

            private int _held;

            internal PartialMessage(int totalFragments)
            {
                _fragments = new ReadOnlyMemory<byte>[totalFragments];
                _present = new bool[totalFragments];
            }

            internal int TotalFragments => _fragments.Length;

            internal bool IsComplete => _held == _fragments.Length;

            /// <summary>The fragments held, as fragments of the message numbered <paramref name="sequence"/>.</summary>
            internal IEnumerable<AcFragment> Fragments(uint sequence)
            {
                for (int i = 0; i < _fragments.Length; i++)
                {
                    if (!_present[i])
                        continue;

                    FragmentHeader header = new FragmentHeader
                    {
                        Sequence = sequence,
                        Count = (ushort)_fragments.Length,
                        Index = (ushort)i,
                        TotalSize = (ushort)(FragmentHeader.Size + _fragments[i].Length),
                    };

                    yield return new AcFragment(header, _fragments[i]);
                }
            }

            /// <summary>Returns false when this index is already held.</summary>
            internal bool Add(int index, ReadOnlyMemory<byte> payload)
            {
                if (index < 0 || index >= _fragments.Length)
                    return false;

                if (_present[index])
                    return false;

                _fragments[index] = payload;
                _present[index] = true;
                _held++;

                return true;
            }

            internal ReadOnlyMemory<byte> Concatenate()
            {
                int total = 0;
                foreach (ReadOnlyMemory<byte> fragment in _fragments)
                    total += fragment.Length;

                byte[] buffer = new byte[total];
                int offset = 0;

                foreach (ReadOnlyMemory<byte> fragment in _fragments)
                {
                    fragment.Span.CopyTo(buffer.AsSpan(offset));
                    offset += fragment.Length;
                }

                return buffer;
            }
        }
    }
}
