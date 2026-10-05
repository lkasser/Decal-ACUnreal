using System.Collections.Generic;
using AC.Host.World;
using AC.Protocol;

namespace AC.Host.Decoding
{
    /// <summary>
    /// Readers for the composite encodings the game layer uses on top of the plain
    /// integers: packed dwords, length-prefixed strings, hash tables, positions.
    /// </summary>
    /// <remarks>
    /// Each is a <c>Try</c>, for the same reason every <see cref="SpanReader"/> read
    /// is: the input is a message from the network and may end anywhere.
    /// </remarks>
    internal static class WireReaders
    {
        /// <summary>
        /// A dword written as a ushort when it fits in 15 bits, otherwise as two
        /// ushorts with the high bit of the first set. Used for data-file ids.
        /// </summary>
        public static bool TryReadPackedDword(ref SpanReader reader, out uint value)
        {
            value = 0;

            if (!reader.TryReadUInt16(out ushort first))
                return false;

            if ((first & 0x8000) == 0)
            {
                value = first;
                return true;
            }

            if (!reader.TryReadUInt16(out ushort second))
                return false;

            value = ((uint)(first & 0x7FFF) << 16) | second;
            return true;
        }

        /// <summary>
        /// A packed dword whose high byte (the data-file type) was stripped before
        /// packing. Zero stays zero; anything else gets the type back.
        /// </summary>
        public static bool TryReadPackedDwordOfKnownType(ref SpanReader reader, uint type, out uint value)
        {
            if (!TryReadPackedDword(ref reader, out uint packed))
            {
                value = 0;
                return false;
            }

            value = packed == 0 ? 0 : packed | type;
            return true;
        }

        /// <summary>The count/bucket header that opens every packed hash table.</summary>
        public static bool TryReadHashTableHeader(ref SpanReader reader, out int count)
        {
            count = 0;

            if (!reader.TryReadUInt16(out ushort c))
                return false;

            if (!reader.TryReadUInt16(out _))
                return false;

            count = c;
            return true;
        }

        public static bool TryReadIntTable(ref SpanReader reader, Dictionary<uint, int> into)
        {
            if (!TryReadHashTableHeader(ref reader, out int count))
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint key) || !reader.TryReadInt32(out int value))
                    return false;

                into[key] = value;
            }

            return true;
        }

        public static bool TryReadInt64Table(ref SpanReader reader, Dictionary<uint, long> into)
        {
            if (!TryReadHashTableHeader(ref reader, out int count))
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint key) || !reader.TryReadUInt64(out ulong value))
                    return false;

                into[key] = (long)value;
            }

            return true;
        }

        public static bool TryReadBoolTable(ref SpanReader reader, Dictionary<uint, bool> into)
        {
            if (!TryReadHashTableHeader(ref reader, out int count))
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint key) || !reader.TryReadUInt32(out uint value))
                    return false;

                into[key] = value != 0;
            }

            return true;
        }

        public static bool TryReadFloatTable(ref SpanReader reader, Dictionary<uint, double> into)
        {
            if (!TryReadHashTableHeader(ref reader, out int count))
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint key) || !reader.TryReadDouble(out double value))
                    return false;

                into[key] = value;
            }

            return true;
        }

        public static bool TryReadStringTable(ref SpanReader reader, Dictionary<uint, string> into)
        {
            if (!TryReadHashTableHeader(ref reader, out int count))
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint key) || !reader.TryReadString(out string value))
                    return false;

                into[key] = value;
            }

            return true;
        }

        public static bool TryReadUIntTable(ref SpanReader reader, Dictionary<uint, uint> into)
        {
            if (!TryReadHashTableHeader(ref reader, out int count))
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint key) || !reader.TryReadUInt32(out uint value))
                    return false;

                into[key] = value;
            }

            return true;
        }

        /// <summary>A full position: landblock cell, origin, and quaternion. 32 bytes.</summary>
        public static bool TryReadLocation(ref SpanReader reader, out Location location)
        {
            location = default;

            if (!reader.TryReadUInt32(out uint cell)) return false;
            if (!reader.TryReadSingle(out float x)) return false;
            if (!reader.TryReadSingle(out float y)) return false;
            if (!reader.TryReadSingle(out float z)) return false;
            if (!reader.TryReadSingle(out float qw)) return false;
            if (!reader.TryReadSingle(out float qx)) return false;
            if (!reader.TryReadSingle(out float qy)) return false;
            if (!reader.TryReadSingle(out float qz)) return false;

            location = new Location(cell, x, y, z, qw, qx, qy, qz);
            return true;
        }

        /// <summary>
        /// The compact position an UpdatePosition message carries: flags decide which
        /// quaternion components and extras are present. Consumes the four trailing
        /// sequence numbers too.
        /// </summary>
        public static bool TryReadPositionPack(ref SpanReader reader, out Location location)
        {
            location = default;

            if (!reader.TryReadUInt32(out uint flags)) return false;
            if (!reader.TryReadUInt32(out uint cell)) return false;
            if (!reader.TryReadSingle(out float x)) return false;
            if (!reader.TryReadSingle(out float y)) return false;
            if (!reader.TryReadSingle(out float z)) return false;

            float qw = 0, qx = 0, qy = 0, qz = 0;

            if ((flags & 0x08) == 0 && !reader.TryReadSingle(out qw)) return false;
            if ((flags & 0x10) == 0 && !reader.TryReadSingle(out qx)) return false;
            if ((flags & 0x20) == 0 && !reader.TryReadSingle(out qy)) return false;
            if ((flags & 0x40) == 0 && !reader.TryReadSingle(out qz)) return false;

            if ((flags & 0x01) != 0 && !reader.TrySkip(12)) return false;
            if ((flags & 0x02) != 0 && !reader.TrySkip(4)) return false;

            // instance, position, teleport, force-position sequences
            if (!reader.TrySkip(8)) return false;

            location = new Location(cell, x, y, z, qw, qx, qy, qz);
            return true;
        }
    }
}
