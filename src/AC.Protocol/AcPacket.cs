using System;
using System.Collections.Generic;

namespace AC.Protocol
{
    /// <summary>One message fragment carried by a packet.</summary>
    public sealed class AcFragment
    {
        public AcFragment(FragmentHeader header, ReadOnlyMemory<byte> payload)
        {
            Header = header;
            Payload = payload;
        }

        public FragmentHeader Header { get; }

        public ReadOnlyMemory<byte> Payload { get; }

        /// <summary>This fragment's contribution to the packet checksum.</summary>
        public uint CalculateHash32()
        {
            Span<byte> header = stackalloc byte[FragmentHeader.Size];
            FragmentHeader h = Header;
            h.Write(header);

            return Hash32.Calculate(header) + Hash32.Calculate(Payload.Span);
        }

        public override string ToString() => $"fragment {Header} payload={Payload.Length}B";
    }

    /// <summary>
    /// A parsed datagram: fixed header, optional header section, and any fragments.
    /// </summary>
    /// <remarks>
    /// Parsing never throws on bad input. A proxy sees whatever arrives on a UDP
    /// socket - truncated packets, scanner noise, packets from a version it does not
    /// know - and must be able to shrug and forward them.
    /// </remarks>
    public sealed class AcPacket
    {
        /// <summary>
        /// Largest datagram accepted. The protocol's own limit is 1024; the slack
        /// absorbs a server sending more without the parser rejecting outright.
        /// </summary>
        public const int MaxDatagramSize = 2048;

        private static readonly IReadOnlyList<AcFragment> NoFragments = Array.Empty<AcFragment>();

        private AcPacket(PacketHeader header, OptionalHeaders optional, IReadOnlyList<AcFragment> fragments)
        {
            Header = header;
            Optional = optional;
            Fragments = fragments;
        }

        public PacketHeader Header { get; }

        public OptionalHeaders Optional { get; }

        public IReadOnlyList<AcFragment> Fragments { get; }

        /// <summary>
        /// Parses one datagram. Returns false when the bytes are not a well-formed
        /// packet, in which case nothing about them should be believed.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> datagram, out AcPacket packet)
        {
            packet = null;

            if (!PacketHeader.TryParse(datagram, out PacketHeader header))
                return false;

            // The declared body length must actually be present. A body shorter than
            // advertised is the usual shape of a truncated or spoofed packet.
            int available = datagram.Length - PacketHeader.Size;
            if (header.BodySize > available)
                return false;

            ReadOnlySpan<byte> body = datagram.Slice(PacketHeader.Size, header.BodySize);

            if (!OptionalHeaders.TryParse(body, header.Flags, out OptionalHeaders optional))
                return false;

            IReadOnlyList<AcFragment> fragments = NoFragments;

            if (header.HasFlag(PacketHeaderFlags.BlobFragments))
            {
                if (!TryParseFragments(body.Slice(optional.Length), out fragments))
                    return false;
            }

            packet = new AcPacket(header, optional, fragments);
            return true;
        }

        private static bool TryParseFragments(ReadOnlySpan<byte> region, out IReadOnlyList<AcFragment> fragments)
        {
            fragments = NoFragments;
            List<AcFragment> parsed = new List<AcFragment>(2);
            int offset = 0;

            while (offset < region.Length)
            {
                if (!FragmentHeader.TryParse(region.Slice(offset), out FragmentHeader header))
                    return false;

                if (!header.IsWellFormed)
                    return false;

                int payloadStart = offset + FragmentHeader.Size;
                int payloadSize = header.PayloadSize;

                if (payloadStart + payloadSize > region.Length)
                    return false;

                parsed.Add(new AcFragment(header, region.Slice(payloadStart, payloadSize).ToArray()));
                offset = payloadStart + payloadSize;
            }

            fragments = parsed;
            return true;
        }

        /// <summary>
        /// Recomputes the checksum the sender should have written, for a packet whose
        /// <see cref="PacketHeaderFlags.EncryptedChecksum"/> flag is clear.
        /// </summary>
        /// <remarks>
        /// When that flag IS set the written checksum is this value plus a key from
        /// the connection's ISAAC stream, so it cannot be verified without tracking
        /// that stream. A relay does not need to: it forwards bytes untouched.
        /// </remarks>
        public uint CalculatePlainChecksum()
            => unchecked(CalculateHeaderHash() + CalculatePayloadHash());

        /// <summary>The header's own contribution to the checksum.</summary>
        public uint CalculateHeaderHash()
        {
            PacketHeader header = Header;
            return header.CalculateHash32();
        }

        /// <summary>
        /// The body's contribution: the optional header section plus every fragment.
        /// </summary>
        public uint CalculatePayloadHash()
        {
            uint checksum = Optional.CalculateHash32();

            foreach (AcFragment fragment in Fragments)
                checksum = unchecked(checksum + fragment.CalculateHash32());

            return checksum;
        }

        /// <summary>
        /// Recovers the ISAAC key the sender masked this packet's checksum with.
        /// </summary>
        /// <remarks>
        /// An encrypted checksum is <c>headerHash + (key ^ payloadHash)</c>, and every
        /// term but the key is computable from the bytes in hand - so observing a
        /// packet yields the key that produced it. That is what makes it possible to
        /// alter a packet in flight and re-stamp it so the receiver still accepts it,
        /// without knowing the connection's seed or tracking its key stream.
        ///
        /// Taking a key from the stream instead would be far worse than useless: the
        /// receiver's window advances to find whichever key it is shown, so spending a
        /// key the real sender still needs gets that sender's next packet rejected.
        /// </remarks>
        public bool TryRecoverEncryptionKey(out uint key)
        {
            if (!Header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
            {
                key = 0;
                return false;
            }

            key = unchecked((Header.Checksum - CalculateHeaderHash()) ^ CalculatePayloadHash());
            return true;
        }

        /// <summary>
        /// True when the checksum in the header matches the packet's contents. Only
        /// meaningful for packets without <see cref="PacketHeaderFlags.EncryptedChecksum"/>;
        /// returns false for encrypted ones rather than pretending to know.
        /// </summary>
        public bool HasValidPlainChecksum()
            => !Header.HasFlag(PacketHeaderFlags.EncryptedChecksum)
               && CalculatePlainChecksum() == Header.Checksum;

        public override string ToString()
            => Fragments.Count == 0
                ? Header.ToString()
                : $"{Header} fragments={Fragments.Count}";
    }
}
