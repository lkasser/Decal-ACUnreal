using System;
using System.Collections.Generic;

namespace AC.Protocol
{
    /// <summary>
    /// Composes a datagram from a header, an optional-header section and fragments,
    /// and stamps the checksum.
    /// </summary>
    /// <remarks>
    /// Only the plain checksum is computed here. Packets a live connection sends after
    /// the handshake set <see cref="PacketHeaderFlags.EncryptedChecksum"/>, whose value
    /// is the plain checksum offset by a key from that connection's ISAAC stream - so
    /// producing one requires tracking the stream, and taking a key from it without
    /// the peer's knowledge desynchronizes the connection. That belongs with the
    /// injection work, not here.
    ///
    /// The optional-header section is supplied as bytes rather than built from fields
    /// because its layout is dictated by the header flags, and only the caller setting
    /// those flags knows what belongs in it.
    /// </remarks>
    public static class PacketWriter
    {
        /// <summary>
        /// Builds a datagram. <paramref name="header"/>'s body size and checksum are
        /// computed and overwritten; every other header field is used as given.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The packet would exceed <see cref="AcPacket.MaxDatagramSize"/>, or a
        /// fragment header does not describe its payload.
        /// </exception>
        /// <summary>
        /// Builds a datagram whose checksum is masked with <paramref name="encryptionKey"/>,
        /// as a live connection's packets are. The caller is responsible for setting
        /// <see cref="PacketHeaderFlags.EncryptedChecksum"/> on the header and for the
        /// key being one the receiver will accept - in practice, the key recovered from
        /// the very packet being rewritten.
        /// </summary>
        public static byte[] BuildEncrypted(
            PacketHeader header,
            ReadOnlySpan<byte> optionalSection,
            IReadOnlyList<AcFragment> fragments,
            uint encryptionKey)
            => Build(header, optionalSection, fragments, encryptionKey);

        public static byte[] Build(
            PacketHeader header,
            ReadOnlySpan<byte> optionalSection,
            IReadOnlyList<AcFragment> fragments = null)
            => Build(header, optionalSection, fragments, null);

        private static byte[] Build(
            PacketHeader header,
            ReadOnlySpan<byte> optionalSection,
            IReadOnlyList<AcFragment> fragments,
            uint? encryptionKey)
        {
            fragments ??= Array.Empty<AcFragment>();

            int bodySize = optionalSection.Length;

            foreach (AcFragment fragment in fragments)
            {
                if (fragment.Header.PayloadSize != fragment.Payload.Length)
                {
                    throw new ArgumentException(
                        $"Fragment header declares {fragment.Header.PayloadSize} payload bytes but carries {fragment.Payload.Length}.",
                        nameof(fragments));
                }

                bodySize += FragmentHeader.Size + fragment.Payload.Length;
            }

            int total = PacketHeader.Size + bodySize;

            if (total > AcPacket.MaxDatagramSize)
            {
                throw new ArgumentException(
                    $"Packet would be {total} bytes, over the {AcPacket.MaxDatagramSize}-byte limit.",
                    nameof(fragments));
            }

            if (bodySize > ushort.MaxValue)
                throw new ArgumentException("Body does not fit the 16-bit size field.", nameof(fragments));

            byte[] datagram = new byte[total];

            header.BodySize = (ushort)bodySize;
            header.Checksum = 0;

            // Body first: the header's checksum covers the body's contribution, so the
            // body must exist before the header can be finalized.
            int offset = PacketHeader.Size;
            optionalSection.CopyTo(datagram.AsSpan(offset));
            offset += optionalSection.Length;

            foreach (AcFragment fragment in fragments)
            {
                FragmentHeader fragmentHeader = fragment.Header;
                fragmentHeader.Write(datagram.AsSpan(offset));
                offset += FragmentHeader.Size;

                fragment.Payload.Span.CopyTo(datagram.AsSpan(offset));
                offset += fragment.Payload.Length;
            }

            header.Write(datagram);

            // Recompute over what was actually written, so the stamped checksum agrees
            // with the bytes rather than with the caller's intent.
            if (!AcPacket.TryParse(datagram, out AcPacket parsed))
                throw new ArgumentException("The composed packet does not parse; check the header flags against the optional section.", nameof(optionalSection));

            header.Checksum = encryptionKey.HasValue
                ? unchecked(parsed.CalculateHeaderHash() + (encryptionKey.Value ^ parsed.CalculatePayloadHash()))
                : parsed.CalculatePlainChecksum();

            header.Write(datagram);

            return datagram;
        }

        /// <summary>
        /// Splits a message into fragments of at most
        /// <see cref="FragmentHeader.MaxTotalSize"/> bytes each.
        /// </summary>
        /// <param name="opcode">Message opcode, prepended to the payload.</param>
        /// <param name="payload">Message body, without the opcode.</param>
        /// <param name="fragmentSequence">Sequence shared by all fragments of this message.</param>
        /// <param name="id">Fragment id field.</param>
        /// <param name="queue">Fragment queue field.</param>
        public static IReadOnlyList<AcFragment> Fragment(
            uint opcode,
            ReadOnlySpan<byte> payload,
            uint fragmentSequence,
            uint id = 0,
            ushort queue = 0)
        {
            byte[] body = new byte[AcMessage.MinimumSize + payload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, opcode);
            payload.CopyTo(body.AsSpan(AcMessage.MinimumSize));

            const int maxPayload = FragmentHeader.MaxTotalSize - FragmentHeader.Size;

            // A zero-length message still needs one fragment, or the receiver would
            // never see it at all.
            int count = Math.Max(1, (body.Length + maxPayload - 1) / maxPayload);

            if (count > ushort.MaxValue)
                throw new ArgumentException("Message needs more fragments than the 16-bit count field allows.", nameof(payload));

            List<AcFragment> fragments = new List<AcFragment>(count);

            for (int index = 0; index < count; index++)
            {
                int start = index * maxPayload;
                int length = Math.Min(maxPayload, body.Length - start);

                byte[] slice = new byte[length];
                body.AsSpan(start, length).CopyTo(slice);

                FragmentHeader fragmentHeader = new FragmentHeader
                {
                    Sequence = fragmentSequence,
                    Id = id,
                    Count = (ushort)count,
                    TotalSize = (ushort)(FragmentHeader.Size + length),
                    Index = (ushort)index,
                    Queue = queue,
                };

                fragments.Add(new AcFragment(fragmentHeader, slice));
            }

            return fragments;
        }
    }
}
