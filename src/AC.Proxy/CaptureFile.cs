using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using AC.Protocol;

namespace AC.Proxy
{
    /// <summary>One datagram as recorded in a capture.</summary>
    public sealed class CapturedDatagram
    {
        public CapturedDatagram(DateTimeOffset timestamp, PacketDirection direction, int localPort, byte[] bytes)
        {
            Timestamp = timestamp;
            Direction = direction;
            LocalPort = localPort;
            Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        }

        public DateTimeOffset Timestamp { get; }

        public PacketDirection Direction { get; }

        public int LocalPort { get; }

        public byte[] Bytes { get; }
    }

    /// <summary>
    /// Records relayed datagrams to a file so decoders can be tested against real
    /// sessions, replayed offline, without a client or server present.
    /// </summary>
    /// <remarks>
    /// Format, all little-endian:
    /// <code>
    ///   magic   8 bytes  "ACPCAP01"
    ///   record  repeated to end of file:
    ///     ticks      int64   DateTimeOffset.UtcTicks
    ///     direction  byte    0 = client to server, 1 = server to client
    ///     port       uint16  local port the datagram arrived on
    ///     length     int32   datagram length
    ///     bytes      length  the datagram, byte-for-byte as relayed
    /// </code>
    /// A record is written whole and flushed, so a capture cut short by a crash is
    /// readable up to the last complete record.
    /// </remarks>
    public sealed class CaptureWriter : IDisposable
    {
        internal static readonly byte[] Magic = { (byte)'A', (byte)'C', (byte)'P', (byte)'C', (byte)'A', (byte)'P', (byte)'0', (byte)'1' };

        private const int RecordHeaderSize = 8 + 1 + 2 + 4;

        private readonly Stream _stream;
        private readonly byte[] _header = new byte[RecordHeaderSize];
        private readonly object _gate = new object();
        private bool _disposed;

        public CaptureWriter(string path)
            : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
        }

        public CaptureWriter(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _stream.Write(Magic, 0, Magic.Length);
            _stream.Flush();
        }

        public long RecordsWritten { get; private set; }

        public void Write(PacketObservedEventArgs observed)
            => Write(observed.Timestamp, observed.Direction, observed.LocalPort, observed.Datagram.Span);

        public void Write(DateTimeOffset timestamp, PacketDirection direction, int localPort, ReadOnlySpan<byte> datagram)
        {
            if (localPort < 0 || localPort > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(localPort));

            // Two relayed ports report from two receive loops; the file needs whole
            // records, not interleaved halves.
            lock (_gate)
            {
                if (_disposed)
                    return;

                BinaryPrimitives.WriteInt64LittleEndian(_header, timestamp.UtcTicks);
                _header[8] = (byte)(direction == PacketDirection.Inbound ? 1 : 0);
                BinaryPrimitives.WriteUInt16LittleEndian(_header.AsSpan(9), (ushort)localPort);
                BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(11), datagram.Length);

                _stream.Write(_header, 0, _header.Length);
                _stream.Write(datagram);
                _stream.Flush();

                RecordsWritten++;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _stream.Dispose();
            }
        }
    }

    public static class CaptureReader
    {
        /// <summary>
        /// Reads every complete record. A truncated final record ends the sequence
        /// quietly, because that is what a capture from a crashed proxy looks like and
        /// the records before it are still good.
        /// </summary>
        /// <exception cref="InvalidDataException">The file is not a capture.</exception>
        public static IEnumerable<CapturedDatagram> Read(string path)
        {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            foreach (CapturedDatagram datagram in Read(stream))
                yield return datagram;
        }

        public static IEnumerable<CapturedDatagram> Read(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            byte[] magic = new byte[CaptureWriter.Magic.Length];
            if (!ReadExactly(stream, magic) || !magic.AsSpan().SequenceEqual(CaptureWriter.Magic))
                throw new InvalidDataException("Not an AC proxy capture (bad magic).");

            byte[] header = new byte[8 + 1 + 2 + 4];

            while (ReadExactly(stream, header))
            {
                long ticks = BinaryPrimitives.ReadInt64LittleEndian(header);
                PacketDirection direction = header[8] == 1 ? PacketDirection.Inbound : PacketDirection.Outbound;
                int port = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(9));
                int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(11));

                // A length the writer could never have produced means the file is
                // corrupt from here on, not merely truncated.
                if (length < 0 || length > AcPacket.MaxDatagramSize)
                    throw new InvalidDataException($"Capture record declares an impossible length of {length}.");

                byte[] bytes = new byte[length];
                if (!ReadExactly(stream, bytes))
                    yield break;

                yield return new CapturedDatagram(
                    new DateTimeOffset(ticks, TimeSpan.Zero),
                    direction,
                    port,
                    bytes);
            }
        }

        private static bool ReadExactly(Stream stream, byte[] buffer)
        {
            int read = 0;

            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                    return false;
                read += n;
            }

            return true;
        }
    }
}
