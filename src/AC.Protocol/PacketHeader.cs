using System;
using System.Buffers.Binary;

namespace AC.Protocol
{
    /// <summary>Which way a packet was travelling. Framing is identical either way.</summary>
    public enum PacketDirection
    {
        /// <summary>Client to server.</summary>
        Outbound,

        /// <summary>Server to client.</summary>
        Inbound,
    }

    [Flags]
    public enum PacketHeaderFlags : uint
    {
        None = 0x00000000,
        Retransmission = 0x00000001,

        /// <summary>
        /// The header checksum is masked with a key from the connection's ISAAC
        /// stream. Cannot be paired with <see cref="Retransmission"/>.
        /// </summary>
        EncryptedChecksum = 0x00000002,

        /// <summary>The body carries message fragments.</summary>
        BlobFragments = 0x00000004,

        ServerSwitch = 0x00000100,
        LogonServerAddr = 0x00000200,
        EmptyHeader1 = 0x00000400,
        Referral = 0x00000800,
        RequestRetransmit = 0x00001000,
        RejectRetransmit = 0x00002000,
        AckSequence = 0x00004000,
        Disconnect = 0x00008000,
        LoginRequest = 0x00010000,
        WorldLoginRequest = 0x00020000,
        ConnectRequest = 0x00040000,
        ConnectResponse = 0x00080000,
        NetError = 0x00100000,
        NetErrorDisconnect = 0x00200000,
        CICMDCommand = 0x00400000,
        TimeSync = 0x01000000,
        EchoRequest = 0x02000000,
        EchoResponse = 0x04000000,
        Flow = 0x08000000,
    }

    /// <summary>
    /// The fixed 20-byte packet header that opens every datagram.
    /// </summary>
    public struct PacketHeader
    {
        public const int Size = 20;

        /// <summary>
        /// The value the checksum field is set to while the header's own checksum is
        /// computed, so that the checksum does not cover itself.
        /// </summary>
        public const uint ChecksumPlaceholder = 0xBADD70DD;

        public uint Sequence;
        public PacketHeaderFlags Flags;
        public uint Checksum;
        public ushort Id;
        public ushort Time;

        /// <summary>Length of the body after this header, in bytes.</summary>
        public ushort BodySize;

        public ushort Iteration;

        public bool HasFlag(PacketHeaderFlags flag) => (Flags & flag) != 0;

        public static bool TryParse(ReadOnlySpan<byte> buffer, out PacketHeader header)
        {
            header = default;

            if (buffer.Length < Size)
                return false;

            header.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            header.Flags = (PacketHeaderFlags)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4));
            header.Checksum = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8));
            header.Id = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(12));
            header.Time = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(14));
            header.BodySize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(16));
            header.Iteration = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(18));

            return true;
        }

        public void Write(Span<byte> buffer)
        {
            if (buffer.Length < Size)
                throw new ArgumentException($"A packet header needs {Size} bytes.", nameof(buffer));

            BinaryPrimitives.WriteUInt32LittleEndian(buffer, Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4), (uint)Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8), Checksum);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(12), Id);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(14), Time);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(16), BodySize);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(18), Iteration);
        }

        /// <summary>
        /// The header's contribution to the packet checksum: the header with its own
        /// checksum field replaced by <see cref="ChecksumPlaceholder"/>.
        /// </summary>
        public uint CalculateHash32()
        {
            Span<byte> buffer = stackalloc byte[Size];

            uint original = Checksum;
            Checksum = ChecksumPlaceholder;
            Write(buffer);
            Checksum = original;

            return Hash32.Calculate(buffer);
        }

        public override string ToString()
            => $"seq={Sequence} id={Id} iter={Iteration} size={BodySize} flags={Flags}";
    }

    /// <summary>
    /// The 16-byte header that opens each message fragment inside a packet body.
    /// </summary>
    public struct FragmentHeader
    {
        public const int Size = 16;

        /// <summary>
        /// Largest legal value of <see cref="TotalSize"/>, header included. The wire
        /// format allows no larger fragment.
        /// </summary>
        public const int MaxTotalSize = 464;

        /// <summary>
        /// Identifies the logical message. All fragments of one message share it, and
        /// it is the key reassembly groups on.
        /// </summary>
        public uint Sequence;

        public uint Id;

        /// <summary>How many fragments the message was split into.</summary>
        public ushort Count;

        /// <summary>Length of this fragment including this header.</summary>
        public ushort TotalSize;

        /// <summary>This fragment's position, from 0 to <see cref="Count"/> - 1.</summary>
        public ushort Index;

        public ushort Queue;

        /// <summary>Length of this fragment's payload, excluding the header.</summary>
        public int PayloadSize => TotalSize - Size;

        public static bool TryParse(ReadOnlySpan<byte> buffer, out FragmentHeader header)
        {
            header = default;

            if (buffer.Length < Size)
                return false;

            header.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            header.Id = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4));
            header.Count = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(8));
            header.TotalSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(10));
            header.Index = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(12));
            header.Queue = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(14));

            return true;
        }

        public void Write(Span<byte> buffer)
        {
            if (buffer.Length < Size)
                throw new ArgumentException($"A fragment header needs {Size} bytes.", nameof(buffer));

            BinaryPrimitives.WriteUInt32LittleEndian(buffer, Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4), Id);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(8), Count);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(10), TotalSize);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(12), Index);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(14), Queue);
        }

        /// <summary>
        /// Whether the header describes a fragment that could exist on the wire.
        /// Checked before trusting <see cref="PayloadSize"/> as a length.
        /// </summary>
        public bool IsWellFormed
            => TotalSize >= Size
               && TotalSize <= MaxTotalSize
               && Count > 0
               && Index < Count;

        public override string ToString()
            => $"seq={Sequence} id={Id} {Index + 1}/{Count} size={TotalSize} queue={Queue}";
    }
}
