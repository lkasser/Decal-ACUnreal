using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace AC.Dat.Geometry
{
    /// <summary>
    /// Reads the client's model files - cells, environments, objects' shapes, landblocks - from
    /// their bytes, throwing at the first read past the end.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="DatBinaryReader"/>, which answers false and leaves the caller to carry on,
    /// this throws: a model is read whole or not at all, nested several levels deep, and the
    /// parsers above it catch once and answer null. Alignment is to four bytes from the start of
    /// the file, as the client wrote it.
    /// </remarks>
    internal sealed class ModelReader
    {
        private readonly byte[] _data;

        public ModelReader(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public int Position { get; private set; }

        public int Remaining => _data.Length - Position;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining)
                throw new InvalidDataException($"A model ran past its end at byte {Position} of {_data.Length}.");

            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(_data, Position, count);
            Position += count;
            return span;
        }

        public void Skip(int count) => Take(count);

        public byte ReadByte() => Take(1)[0];

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public Vector3 ReadVector3() => new Vector3(ReadSingle(), ReadSingle(), ReadSingle());

        /// <summary>A placement: where, then which way, the rotation stored w first.</summary>
        public Placement ReadPlacement()
        {
            Vector3 origin = ReadVector3();
            float w = ReadSingle();
            float x = ReadSingle();
            float y = ReadSingle();
            float z = ReadSingle();
            return new Placement(origin, new Quaternion(x, y, z, w));
        }

        /// <summary>A plane: its normal, then the distance term.</summary>
        public Plane ReadPlane() => new Plane(ReadVector3(), ReadSingle());

        /// <summary>To the next four-byte boundary of the file.</summary>
        public void Align()
        {
            int over = Position % 4;
            if (over != 0)
                Skip(4 - over);
        }

        /// <summary>A count packed into one, two or four bytes, by the top bits of the first.</summary>
        public uint ReadCompressedUInt32()
        {
            byte first = ReadByte();
            if ((first & 0x80) == 0)
                return first;

            byte second = ReadByte();
            if ((first & 0x40) == 0)
                return (uint)(((first & 0x7F) << 8) | second);

            ushort rest = ReadUInt16();
            return (uint)(((((first & 0x3F) << 8) | second) << 16) | rest);
        }

        /// <summary>A string with a 16-bit length, skipped: only its length matters here.</summary>
        public void SkipString() => Skip(ReadUInt16());

        /// <summary>A count, as a whole word, with a sanity limit so a misread does not allocate the world.</summary>
        public int ReadCount(int limit = 1 << 20)
        {
            uint count = ReadUInt32();
            if (count > limit)
                throw new InvalidDataException($"A count of {count} at byte {Position - 4} is past belief.");
            return (int)count;
        }
    }
}
