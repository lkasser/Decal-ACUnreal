using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AC.Dat
{
    /// <summary>
    /// Reads the rows of a table out of an SQLite database file, without SQLite.
    /// </summary>
    /// <remarks>
    /// Virindi View Service kept where each window was left, and how it was drawn, in an SQLite
    /// file of its own. Reading that is all the host needs SQLite for, and the file format is
    /// documented and small enough to read directly - which spares the host a native library
    /// and the player a download. Only reading: nothing here writes, locks or journals, and a
    /// database in WAL mode is read as its main file alone, without what is still in the log.
    ///
    /// <para>
    /// The whole file is read into memory once. The files in question are tens of kilobytes;
    /// anything larger than <see cref="MaxBytes"/> is refused rather than read.
    /// </para>
    /// </remarks>
    public sealed class SqliteFile
    {
        /// <summary>The largest file this will read.</summary>
        public const int MaxBytes = 64 * 1024 * 1024;

        private readonly byte[] _data;
        private readonly int _pageSize;
        private readonly int _usable;
        private readonly Encoding _text;

        private SqliteFile(byte[] data, int pageSize, int usable, Encoding text)
        {
            _data = data;
            _pageSize = pageSize;
            _usable = usable;
            _text = text;
        }

        /// <summary>
        /// Opens a database file for reading, sharing it with whoever else has it open. Throws
        /// <see cref="IOException"/> for a file that cannot be read and
        /// <see cref="InvalidDataException"/> for one that is not an SQLite database.
        /// </summary>
        public static SqliteFile Open(string path)
        {
            byte[] data;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxBytes)
                    throw new InvalidDataException($"{path} is {stream.Length} bytes, more than the {MaxBytes} this reads");
                data = new byte[stream.Length];
                int read = 0;
                while (read < data.Length)
                {
                    int got = stream.Read(data, read, data.Length - read);
                    if (got == 0)
                        break;
                    read += got;
                }

                if (read != data.Length)
                    throw new InvalidDataException($"{path} ended early");
            }

            return FromBytes(data);
        }

        /// <summary>A database already in memory. The array is kept, not copied.</summary>
        public static SqliteFile FromBytes(byte[] data)
        {
            if (data == null || data.Length < 100 || Encoding.ASCII.GetString(data, 0, 16) != "SQLite format 3\0")
                throw new InvalidDataException("not an SQLite 3 database");

            int pageSize = (data[16] << 8) | data[17];
            if (pageSize == 1)
                pageSize = 65536;
            if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0)
                throw new InvalidDataException($"a page size of {pageSize} is not one SQLite writes");

            int usable = pageSize - data[20];
            if (usable < 480)
                throw new InvalidDataException("too much of each page is reserved");

            Encoding text = ReadBigEndian(data, 56, 4) switch
            {
                2 => Encoding.Unicode,
                3 => Encoding.BigEndianUnicode,
                _ => Encoding.UTF8,
            };

            return new SqliteFile(data, pageSize, usable, text);
        }

        /// <summary>The names of the tables in the database.</summary>
        public IReadOnlyList<string> TableNames()
        {
            List<string> names = new List<string>();
            foreach (SchemaEntry entry in Schema())
            {
                if (entry.Type == "table")
                    names.Add(entry.Name);
            }

            return names;
        }

        /// <summary>
        /// A table's rows, each as its column names to values: <see cref="long"/>,
        /// <see cref="double"/>, <see cref="string"/>, a byte array, or null. A column added
        /// after a row was written is missing from that row, as SQLite leaves it until the
        /// row is next written; the caller supplies the column's default.
        /// </summary>
        /// <exception cref="KeyNotFoundException">The database has no such table.</exception>
        public IReadOnlyList<IReadOnlyDictionary<string, object>> ReadTable(string table)
        {
            SchemaEntry entry = null;
            foreach (SchemaEntry each in Schema())
            {
                if (each.Type == "table" && string.Equals(each.Name, table, StringComparison.OrdinalIgnoreCase))
                {
                    entry = each;
                    break;
                }
            }

            if (entry == null)
                throw new KeyNotFoundException($"the database has no table named {table}");

            IReadOnlyList<Column> columns = ParseColumns(entry.Sql);
            List<IReadOnlyDictionary<string, object>> rows = new List<IReadOnlyDictionary<string, object>>();
            foreach ((long rowid, object[] values) in ReadTree(entry.RootPage))
            {
                Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < columns.Count; i++)
                {
                    if (columns[i].IsRowId)
                        row[columns[i].Name] = rowid;
                    else if (i < values.Length)
                        row[columns[i].Name] = values[i];
                }

                rows.Add(row);
            }

            return rows;
        }

        // --- The schema ------------------------------------------------------------------

        private sealed class SchemaEntry
        {
            public string Type;
            public string Name;
            public int RootPage;
            public string Sql;
        }

        private sealed class Column
        {
            public string Name;
            public bool IsRowId;
        }

        private IEnumerable<SchemaEntry> Schema()
        {
            // sqlite_master: type, name, tbl_name, rootpage, sql - rooted on page 1.
            foreach ((_, object[] values) in ReadTree(1))
            {
                if (values.Length < 5)
                    continue;
                yield return new SchemaEntry
                {
                    Type = values[0] as string,
                    Name = values[1] as string,
                    RootPage = values[3] is long page ? (int)page : 0,
                    Sql = values[4] as string,
                };
            }
        }

        /// <summary>
        /// The columns a CREATE TABLE statement declares, in order: the first word of each
        /// part between the outer brackets that is not a table constraint.
        /// </summary>
        private static IReadOnlyList<Column> ParseColumns(string sql)
        {
            List<Column> columns = new List<Column>();
            if (string.IsNullOrEmpty(sql))
                return columns;

            int open = sql.IndexOf('(');
            int close = sql.LastIndexOf(')');
            if (open < 0 || close <= open)
                return columns;

            foreach (string part in SplitTopLevel(sql.Substring(open + 1, close - open - 1)))
            {
                string definition = part.Trim();
                if (definition.Length == 0)
                    continue;

                string name = FirstWord(definition, out string rest);
                string upper = name.ToUpperInvariant();
                bool quoted = definition[0] == '[' || definition[0] == '"' || definition[0] == '`';
                if (!quoted && (upper == "PRIMARY" || upper == "UNIQUE" || upper == "CHECK" || upper == "FOREIGN" || upper == "CONSTRAINT"))
                    continue;

                // INTEGER PRIMARY KEY is the rowid itself, and stored as null in the record.
                string type = rest.Trim().ToUpperInvariant();
                bool isRowId = type.StartsWith("INTEGER", StringComparison.Ordinal) && type.Contains("PRIMARY KEY") && !type.Contains("DESC");
                columns.Add(new Column { Name = name, IsRowId = isRowId });
            }

            return columns;
        }

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            int depth = 0;
            char quote = '\0';
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                    continue;
                }

                if (c == '\'' || c == '"' || c == '`')
                    quote = c;
                else if (c == '[')
                    quote = ']';
                else if (c == '(')
                    depth++;
                else if (c == ')')
                    depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                }
            }

            yield return text.Substring(start);
        }

        private static string FirstWord(string definition, out string rest)
        {
            char first = definition[0];
            char end = first == '[' ? ']' : first == '"' ? '"' : first == '`' ? '`' : '\0';
            if (end != '\0')
            {
                int stop = definition.IndexOf(end, 1);
                if (stop < 0)
                    stop = definition.Length;
                rest = stop + 1 < definition.Length ? definition.Substring(stop + 1) : string.Empty;
                return definition.Substring(1, stop - 1);
            }

            int space = 0;
            while (space < definition.Length && !char.IsWhiteSpace(definition[space]))
                space++;
            rest = definition.Substring(space);
            return definition.Substring(0, space);
        }

        // --- B-trees ---------------------------------------------------------------------

        /// <summary>Every row of the table b-tree rooted at a page, in rowid order.</summary>
        private IEnumerable<(long RowId, object[] Values)> ReadTree(int root)
        {
            Stack<int> pages = new Stack<int>();
            HashSet<int> seen = new HashSet<int>();
            pages.Push(root);
            List<(long, object[])> rows = new List<(long, object[])>();

            // Depth first, right child last, so rows come out in order. A page seen twice is a
            // damaged file; it is read once, and the loop cannot go round for ever.
            while (pages.Count > 0)
            {
                int page = pages.Pop();
                if (!seen.Add(page))
                    continue;

                int start = PageStart(page);
                int header = page == 1 ? 100 : 0;
                byte kind = _data[start + header];
                int cells = (int)ReadBigEndian(_data, start + header + 3, 2);

                if (kind == 0x05)
                {
                    // Interior: each cell points left of its key; the right-most pointer last.
                    List<int> children = new List<int>(cells + 1);
                    for (int i = 0; i < cells; i++)
                    {
                        int cell = start + (int)ReadBigEndian(_data, start + header + 12 + i * 2, 2);
                        children.Add((int)ReadBigEndian(_data, cell, 4));
                    }

                    children.Add((int)ReadBigEndian(_data, start + header + 8, 4));
                    for (int i = children.Count - 1; i >= 0; i--)
                        pages.Push(children[i]);
                }
                else if (kind == 0x0D)
                {
                    for (int i = 0; i < cells; i++)
                    {
                        int cell = start + (int)ReadBigEndian(_data, start + header + 8 + i * 2, 2);
                        long size = ReadVarint(_data, ref cell);
                        long rowid = ReadVarint(_data, ref cell);
                        byte[] payload = ReadPayload(cell, size);
                        rows.Add((rowid, ReadRecord(payload)));
                    }
                }
                else
                {
                    throw new InvalidDataException($"page {page} is not part of a table (type 0x{kind:X2})");
                }
            }

            return rows;
        }

        private int PageStart(int page)
        {
            long start = (long)(page - 1) * _pageSize;
            if (page < 1 || start + _pageSize > _data.Length)
                throw new InvalidDataException($"page {page} is past the end of the file");
            return (int)start;
        }

        /// <summary>A cell's payload: what is on the page, then its overflow pages in turn.</summary>
        private byte[] ReadPayload(int offset, long size)
        {
            if (size < 0 || size > MaxBytes)
                throw new InvalidDataException($"a row claims {size} bytes");

            int total = (int)size;
            int maxLocal = _usable - 35;
            int local = total;
            if (total > maxLocal)
            {
                int minLocal = (_usable - 12) * 32 / 255 - 23;
                int k = minLocal + (total - minLocal) % (_usable - 4);
                local = k <= maxLocal ? k : minLocal;
            }

            byte[] payload = new byte[total];
            Array.Copy(_data, offset, payload, 0, local);
            int filled = local;
            if (filled < total)
            {
                int next = (int)ReadBigEndian(_data, offset + local, 4);
                HashSet<int> seen = new HashSet<int>();
                while (filled < total)
                {
                    if (next == 0 || !seen.Add(next))
                        throw new InvalidDataException("a row's overflow pages end early");
                    int start = PageStart(next);
                    next = (int)ReadBigEndian(_data, start, 4);
                    int take = Math.Min(_usable - 4, total - filled);
                    Array.Copy(_data, start + 4, payload, filled, take);
                    filled += take;
                }
            }

            return payload;
        }

        /// <summary>A record: a header of serial types, then the values they describe.</summary>
        private object[] ReadRecord(byte[] payload)
        {
            int at = 0;
            long headerSize = ReadVarint(payload, ref at);
            List<long> types = new List<long>();
            while (at < headerSize && at < payload.Length)
                types.Add(ReadVarint(payload, ref at));

            int body = (int)headerSize;
            object[] values = new object[types.Count];
            for (int i = 0; i < types.Count; i++)
            {
                long type = types[i];
                switch (type)
                {
                    case 0: values[i] = null; break;
                    case 1: values[i] = (long)(sbyte)payload[body]; body += 1; break;
                    case 2: values[i] = SignExtend(ReadBigEndian(payload, body, 2), 16); body += 2; break;
                    case 3: values[i] = SignExtend(ReadBigEndian(payload, body, 3), 24); body += 3; break;
                    case 4: values[i] = SignExtend(ReadBigEndian(payload, body, 4), 32); body += 4; break;
                    case 5: values[i] = SignExtend(ReadBigEndian(payload, body, 6), 48); body += 6; break;
                    case 6: values[i] = (long)ReadBigEndian(payload, body, 8); body += 8; break;
                    case 7: values[i] = BitConverter.Int64BitsToDouble((long)ReadBigEndian(payload, body, 8)); body += 8; break;
                    case 8: values[i] = 0L; break;
                    case 9: values[i] = 1L; break;
                    default:
                        if (type >= 12)
                        {
                            int length = (int)((type - (type % 2 == 0 ? 12 : 13)) / 2);
                            if (body + length > payload.Length)
                                throw new InvalidDataException("a value runs past the end of its row");
                            if (type % 2 == 0)
                            {
                                byte[] blob = new byte[length];
                                Array.Copy(payload, body, blob, 0, length);
                                values[i] = blob;
                            }
                            else
                            {
                                values[i] = _text.GetString(payload, body, length);
                            }

                            body += length;
                        }
                        break;
                }
            }

            return values;
        }

        private static long SignExtend(ulong value, int bits)
        {
            int shift = 64 - bits;
            return (long)(value << shift) >> shift;
        }

        private static ulong ReadBigEndian(byte[] data, int offset, int count)
        {
            if (offset < 0 || offset + count > data.Length)
                throw new InvalidDataException("a read ran past the end of the file");
            ulong value = 0;
            for (int i = 0; i < count; i++)
                value = (value << 8) | data[offset + i];
            return value;
        }

        /// <summary>SQLite's variable-length integer: seven bits a byte, big end first, the ninth byte whole.</summary>
        private static long ReadVarint(byte[] data, ref int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 9; i++)
            {
                if (offset >= data.Length)
                    throw new InvalidDataException("a number ran past the end of the file");
                byte b = data[offset++];
                if (i == 8)
                    return (long)((value << 8) | b);
                value = (value << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0)
                    break;
            }

            return (long)value;
        }
    }
}
