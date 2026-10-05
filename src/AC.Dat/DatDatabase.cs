using System;
using System.Collections.Generic;
using System.IO;

namespace AC.Dat
{
    /// <summary>Where a file lives inside a DAT.</summary>
    public readonly struct DatFileEntry
    {
        public DatFileEntry(uint id, uint offset, uint size)
        {
            Id = id;
            Offset = offset;
            Size = size;
        }

        public uint Id { get; }

        public uint Offset { get; }

        public uint Size { get; }

        public override string ToString() => $"0x{Id:X8} @0x{Offset:X8} {Size}B";
    }

    /// <summary>
    /// One of the client's .dat archives: a block-allocated file store indexed by a
    /// B-tree, keyed by a 32-bit file id.
    /// </summary>
    /// <remarks>
    /// A file is not contiguous. It is a chain of fixed-size blocks whose first four
    /// bytes point at the next block, so reading one means following that chain and
    /// stitching the remainders together. The directory is stored the same way, which
    /// is why the index has to be read through the same path as the data.
    ///
    /// Opening indexes the whole archive up front: the B-tree walk touches every node
    /// once, and the result is a few hundred thousand entries in a dictionary. That
    /// costs a second or so on the 900 MB portal file and makes every later lookup
    /// free. An archive a session reads little of can be opened without the index
    /// instead, and each read then searches the tree for its file.
    ///
    /// Read-only, and the file is opened for shared reading, so having the game open
    /// at the same time is fine.
    /// </remarks>
    public sealed class DatDatabase : IDisposable
    {
        /// <summary>The header sits here, not at the start of the file.</summary>
        private const long HeaderOffset = 0x140;

        /// <summary>Marks a file as a DAT: 'B', 'T', 0, 0.</summary>
        private const uint ExpectedMagic = 0x00005442;

        /// <summary>
        /// Directory nodes are a fixed shape: 62 branch pointers, a count, then up to
        /// 61 entries of six words. Read whole, because the count is inside it.
        /// </summary>
        private const int BranchCount = 0x3E;
        private const int MaxEntriesPerNode = 0x3D;
        private const int EntrySize = sizeof(uint) * 6;
        private const int DirectoryNodeSize = (sizeof(uint) * BranchCount) + sizeof(uint) + (EntrySize * MaxEntriesPerNode);

        /// <summary>
        /// Depth at which a malformed or circular B-tree is abandoned. A healthy
        /// archive nests only a handful deep.
        /// </summary>
        private const int MaxDirectoryDepth = 32;

        private readonly FileStream _stream;
        private readonly object _gate = new object();
        private readonly Dictionary<uint, DatFileEntry> _files = new Dictionary<uint, DatFileEntry>();
        private readonly uint _rootDirectory;

        private DatDatabase(FileStream stream, uint blockSize, uint rootDirectory, string path, bool indexed)
        {
            _stream = stream;
            BlockSize = blockSize;
            Path = path;
            _rootDirectory = rootDirectory;
            IsIndexed = indexed;

            if (indexed)
                ReadDirectory(rootDirectory, 0);
        }

        public string Path { get; }

        public uint BlockSize { get; }

        /// <summary>
        /// Whether the archive was indexed when it was opened. Without the index,
        /// <see cref="FileCount"/> and <see cref="Files"/> are empty, and every read
        /// searches the directory for its file.
        /// </summary>
        public bool IsIndexed { get; }

        public int FileCount => _files.Count;

        public IReadOnlyDictionary<uint, DatFileEntry> Files => _files;

        /// <summary>
        /// Opens a DAT and indexes it.
        /// </summary>
        /// <exception cref="FileNotFoundException">No such file.</exception>
        /// <exception cref="InvalidDataException">Not a DAT, or its header is unusable.</exception>
        public static DatDatabase Open(string path) => Open(path, indexed: true);

        /// <summary>
        /// Opens a DAT, indexing it only if <paramref name="indexed"/>.
        /// </summary>
        /// <remarks>
        /// Without the index nothing is read at opening, and a read finds its file by
        /// going down the B-tree from the root - a few nodes. That suits an archive of
        /// which a session reads little: the cell archive holds some 800,000 files, which
        /// would take 37 MB to index, and the host reads a few hundred of them.
        /// </remarks>
        /// <exception cref="FileNotFoundException">No such file.</exception>
        /// <exception cref="InvalidDataException">Not a DAT, or its header is unusable.</exception>
        public static DatDatabase Open(string path, bool indexed)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("DAT file not found.", path);

            FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            try
            {
                stream.Seek(HeaderOffset, SeekOrigin.Begin);

                byte[] header = new byte[36];
                ReadExactly(stream, header, header.Length);

                uint magic = BitConverter.ToUInt32(header, 0);
                if (magic != ExpectedMagic)
                    throw new InvalidDataException($"{path} is not a DAT file (magic 0x{magic:X8}).");

                uint blockSize = BitConverter.ToUInt32(header, 4);
                uint rootDirectory = BitConverter.ToUInt32(header, 32);

                // A block must hold its own next-pointer and some payload, and the
                // chain walk assumes as much.
                if (blockSize <= sizeof(uint) || blockSize > 1 << 20)
                    throw new InvalidDataException($"{path} declares an implausible block size of {blockSize}.");

                if (rootDirectory == 0)
                    throw new InvalidDataException($"{path} has no root directory.");

                return new DatDatabase(stream, blockSize, rootDirectory, path, indexed);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public bool Contains(uint fileId)
        {
            lock (_gate)
                return TryFind(fileId, out _);
        }

        /// <summary>
        /// Reads one file whole, following its block chain.
        /// </summary>
        /// <returns>Its bytes, or null when the archive has no such file.</returns>
        public byte[] Read(uint fileId)
        {
            lock (_gate)
                return TryFind(fileId, out DatFileEntry entry) ? ReadChain(entry.Offset, entry.Size) : null;
        }

        /// <summary>Where a file is: from the index, or else by searching the directory. Called under the lock.</summary>
        private bool TryFind(uint fileId, out DatFileEntry entry)
        {
            if (IsIndexed)
                return _files.TryGetValue(fileId, out entry);

            return TrySearch(fileId, out entry);
        }

        /// <summary>
        /// Goes down the B-tree for one file. A node's entries are in order of id, and the
        /// files whose ids fall before an entry - or after the last - are down the branch
        /// on that side of it, so one node is read at each level.
        /// </summary>
        private bool TrySearch(uint fileId, out DatFileEntry entry)
        {
            entry = default;
            uint offset = _rootDirectory;

            for (int depth = 0; offset != 0 && depth <= MaxDirectoryDepth; depth++)
            {
                byte[] node = ReadChain(offset, DirectoryNodeSize);

                int cursor = BranchCount * sizeof(uint);
                uint entryCount = BitConverter.ToUInt32(node, cursor);
                cursor += sizeof(uint);

                if (entryCount > MaxEntriesPerNode)
                    return false;

                uint branch = entryCount;
                for (uint i = 0; i < entryCount; i++, cursor += EntrySize)
                {
                    uint id = BitConverter.ToUInt32(node, cursor + 4);
                    if (id == fileId)
                    {
                        uint fileOffset = BitConverter.ToUInt32(node, cursor + 8);
                        uint fileSize = BitConverter.ToUInt32(node, cursor + 12);
                        if (fileOffset == 0 || fileSize == 0)
                            return false;

                        entry = new DatFileEntry(id, fileOffset, fileSize);
                        return true;
                    }

                    if (fileId < id)
                    {
                        branch = i;
                        break;
                    }
                }

                // A leaf has no first branch, and nowhere further to look.
                if (BitConverter.ToUInt32(node, 0) == 0)
                    return false;

                offset = BitConverter.ToUInt32(node, (int)branch * sizeof(uint));
            }

            return false;
        }

        /// <summary>
        /// Follows a block chain from <paramref name="offset"/>, gathering
        /// <paramref name="size"/> bytes of payload.
        /// </summary>
        private byte[] ReadChain(uint offset, uint size)
        {
            byte[] buffer = new byte[size];
            int written = 0;
            int payloadPerBlock = (int)BlockSize - sizeof(uint);
            uint next = offset;
            int blocks = 0;

            // The chain cannot be longer than the file divided by the block size; a
            // bound stops a corrupt next-pointer looping forever.
            int maxBlocks = (int)(size / (uint)payloadPerBlock) + 2;

            while (written < buffer.Length)
            {
                if (next == 0 || ++blocks > maxBlocks)
                    break;

                _stream.Seek(next, SeekOrigin.Begin);

                byte[] pointer = new byte[sizeof(uint)];
                if (!TryReadExactly(_stream, pointer, pointer.Length))
                    break;

                next = BitConverter.ToUInt32(pointer, 0);

                int wanted = Math.Min(payloadPerBlock, buffer.Length - written);
                if (!TryReadExactly(_stream, buffer, wanted, written))
                    break;

                written += wanted;
            }

            return buffer;
        }

        /// <summary>
        /// Walks one B-tree node, then its branches. Entries are gathered from every
        /// node, internal ones included - a node holds both keys and children.
        /// </summary>
        private void ReadDirectory(uint offset, int depth)
        {
            if (depth > MaxDirectoryDepth)
                return;

            byte[] node = ReadChain(offset, DirectoryNodeSize);

            uint[] branches = new uint[BranchCount];
            for (int i = 0; i < BranchCount; i++)
                branches[i] = BitConverter.ToUInt32(node, i * sizeof(uint));

            int cursor = BranchCount * sizeof(uint);
            uint entryCount = BitConverter.ToUInt32(node, cursor);
            cursor += sizeof(uint);

            // A count past the node's capacity means this is not a directory node.
            if (entryCount > MaxEntriesPerNode)
                return;

            for (uint i = 0; i < entryCount; i++)
            {
                uint flags = BitConverter.ToUInt32(node, cursor);
                uint id = BitConverter.ToUInt32(node, cursor + 4);
                uint fileOffset = BitConverter.ToUInt32(node, cursor + 8);
                uint fileSize = BitConverter.ToUInt32(node, cursor + 12);
                cursor += EntrySize;

                if (id != 0 && fileOffset != 0 && fileSize > 0)
                    _files[id] = new DatFileEntry(id, fileOffset, fileSize);
            }

            // A leaf has no first branch; an internal node has entryCount + 1 of them.
            if (branches[0] == 0)
                return;

            for (uint i = 0; i <= entryCount && i < BranchCount; i++)
            {
                if (branches[i] != 0)
                    ReadDirectory(branches[i], depth + 1);
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            if (!TryReadExactly(stream, buffer, count))
                throw new InvalidDataException("Unexpected end of DAT file.");
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer, int count, int offset = 0)
        {
            int read = 0;

            while (read < count)
            {
                int n = stream.Read(buffer, offset + read, count - read);
                if (n <= 0)
                    return false;
                read += n;
            }

            return true;
        }

        public void Dispose()
        {
            lock (_gate)
                _stream?.Dispose();
        }
    }
}
