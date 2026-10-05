using System;
using System.Collections.Generic;

namespace AC.Protocol
{
    /// <summary>
    /// The variable-length section between the fixed header and any fragments. Which
    /// fields are present, and in what order, is decided entirely by the header
    /// flags; there is no length prefix, so a reader that disagrees with the sender
    /// about one field desynchronizes everything after it.
    /// </summary>
    /// <remarks>
    /// The field order below is the wire order and is not arbitrary - it follows
    /// ascending flag value, which is how both the retail client and ACE emit it.
    /// </remarks>
    public sealed class OptionalHeaders
    {
        private static readonly IReadOnlyList<uint> NoRetransmits = Array.Empty<uint>();

        /// <summary>Bytes this section consumed, where the fragments then begin.</summary>
        public int Length { get; private set; }

        /// <summary>Highest sequence the peer has received, when <see cref="PacketHeaderFlags.AckSequence"/> is set.</summary>
        public uint AckSequence { get; private set; }

        /// <summary>Peer clock, when <see cref="PacketHeaderFlags.TimeSync"/> is set.</summary>
        public double TimeSync { get; private set; }

        /// <summary>Client timestamp to echo, when <see cref="PacketHeaderFlags.EchoRequest"/> is set.</summary>
        public float EchoRequestClientTime { get; private set; }

        /// <summary>Sequences the peer wants resent, when <see cref="PacketHeaderFlags.RequestRetransmit"/> is set.</summary>
        public IReadOnlyList<uint> RetransmitRequests { get; private set; } = NoRetransmits;

        public uint FlowBytes { get; private set; }

        public ushort FlowInterval { get; private set; }

        /// <summary>
        /// The exact bytes the checksum covers. Kept rather than recomputed because
        /// three of the fields contribute to the checksum without being consumed, so
        /// re-serializing what was parsed would not reproduce the original input.
        /// </summary>
        private byte[] _checksumBytes = Array.Empty<byte>();

        public uint CalculateHash32() => Hash32.Calculate(_checksumBytes);

        /// <summary>
        /// Parses the optional section at the start of <paramref name="body"/>.
        /// Returns false on a truncated or self-inconsistent section, leaving the
        /// caller to reject the packet rather than act on half-read fields.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> body, PacketHeaderFlags flags, out OptionalHeaders headers)
        {
            headers = null;

            OptionalHeaders result = new OptionalHeaders();
            SpanReader reader = new SpanReader(body);

            // Three flags below contribute bytes to the checksum without consuming
            // them, so the checksum input is not simply the consumed prefix.
            List<byte> checksummed = new List<byte>(32);

            if ((flags & PacketHeaderFlags.ServerSwitch) != 0)
            {
                if (!reader.TrySkip(8)) return false;
            }

            if ((flags & PacketHeaderFlags.RequestRetransmit) != 0)
            {
                if (!reader.TryReadUInt32(out uint count)) return false;

                // A count is only credible if the sequences it promises are present.
                // Checking first stops a bogus count from allocating a huge array.
                if (count > (uint)(reader.Remaining / 4)) return false;

                uint[] sequences = new uint[count];
                for (uint i = 0; i < count; i++)
                {
                    if (!reader.TryReadUInt32(out sequences[i])) return false;
                }

                result.RetransmitRequests = sequences;
            }

            if ((flags & PacketHeaderFlags.RejectRetransmit) != 0)
            {
                if (!reader.TryReadUInt32(out uint count)) return false;
                if (count > (uint)(reader.Remaining / 4)) return false;
                if (!reader.TrySkip((int)count * 4)) return false;
            }

            if ((flags & PacketHeaderFlags.AckSequence) != 0)
            {
                if (!reader.TryReadUInt32(out uint ack)) return false;
                result.AckSequence = ack;
            }

            // Everything consumed so far is checksum input.
            AppendSpan(checksummed, reader.Consumed);

            // LoginRequest, WorldLoginRequest and ConnectResponse feed the checksum but
            // do NOT advance the position: the client leaves them in place for the
            // handler that consumes them, so anything after this section starts where
            // these began.
            if ((flags & PacketHeaderFlags.LoginRequest) != 0)
            {
                if (reader.Remaining < 1) return false;
                if (!reader.TryPeekBytes(reader.Remaining, out ReadOnlySpan<byte> login)) return false;
                AppendSpan(checksummed, login);
            }

            if ((flags & PacketHeaderFlags.WorldLoginRequest) != 0)
            {
                if (!reader.TryPeekBytes(8, out ReadOnlySpan<byte> worldLogin)) return false;
                AppendSpan(checksummed, worldLogin);
            }

            if ((flags & PacketHeaderFlags.ConnectResponse) != 0)
            {
                if (!reader.TryPeekBytes(8, out ReadOnlySpan<byte> connectResponse)) return false;
                AppendSpan(checksummed, connectResponse);
            }

            // Back to fields that both consume and checksum.
            int checksummedThrough = reader.Offset;

            if ((flags & PacketHeaderFlags.CICMDCommand) != 0)
            {
                if (!reader.TrySkip(8)) return false;
            }

            if ((flags & PacketHeaderFlags.TimeSync) != 0)
            {
                if (!reader.TryReadDouble(out double timeSync)) return false;
                result.TimeSync = timeSync;
            }

            if ((flags & PacketHeaderFlags.EchoRequest) != 0)
            {
                if (!reader.TryReadSingle(out float echo)) return false;
                result.EchoRequestClientTime = echo;
            }

            if ((flags & PacketHeaderFlags.Flow) != 0)
            {
                if (!reader.TryReadUInt32(out uint flowBytes)) return false;
                if (!reader.TryReadUInt16(out ushort flowInterval)) return false;
                result.FlowBytes = flowBytes;
                result.FlowInterval = flowInterval;
            }

            if (reader.Offset > checksummedThrough)
                AppendSpan(checksummed, reader.Consumed.Slice(checksummedThrough));

            result.Length = reader.Offset;
            result._checksumBytes = checksummed.ToArray();

            headers = result;
            return true;
        }

        private static void AppendSpan(List<byte> sink, ReadOnlySpan<byte> bytes)
        {
            foreach (byte b in bytes)
                sink.Add(b);
        }
    }
}
