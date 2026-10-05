using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using AC.Protocol;
using Decal.Adapter.Hosting;
using Decal.Adapter.NetParser;

namespace Decal.Adapter
{
    /// <summary>
    /// One struct of a network message - the message itself, a field of a named type, a vector,
    /// or one entry of a vector - read by name against Decal's messages.xml.
    /// </summary>
    /// <remarks>
    /// A port of Decal 2.9.8.3's own reader, rule for rule, because plugins were written against
    /// what it did rather than against the protocol: a WORD comes back as a short and a DWORD as
    /// an int, so a plugin asks for <c>Value&lt;short&gt;</c> and <c>Value&lt;int&gt;</c>; names
    /// match ignoring case; a field that is not there reads as the default, null or nothing; the
    /// fields a switch or maskmap chose sit beside the struct's own, in order, as though always
    /// there; a vector's entries are named "0", "1" and so on; and an empty case that is taken
    /// leaves a nameless, valueless field behind, as Decal's did.
    ///
    /// <para>
    /// Nothing is read until a plugin asks: the first question parses the whole message, once,
    /// and the answers come from that. A message the schema does not fit is read as far as it
    /// fits. Decal threw from that first question and answered every later one from the fields
    /// it had got through; here nobody is thrown at, the fields read so far are the answer from
    /// the start, and the runtime says once in the log which message did not fit.
    /// </para>
    /// </remarks>
    public class MessageStruct : MarshalByRefObject
    {
        /// <summary>One field as read: its name, what it was, its value and where its bytes are.</summary>
        internal struct MessageField
        {
            internal string Name;

            internal MemberParserType Type;

            internal object Value;

            internal int Offset;

            internal int Length;
        }

        private readonly byte[] _data;
        private readonly int _offset;
        private readonly MemberParser _parser;
        private readonly MessageStruct _parent;
        private readonly int _index;
        private int _length;
        private bool _parsed;
        private int _count;
        private MessageField[] _fields;

        /// <summary>For <see cref="Message"/>, which answers through a struct of its own.</summary>
        internal MessageStruct()
        {
        }

        /// <summary>A whole message's fields, starting after its type. The bytes are kept, not copied.</summary>
        internal MessageStruct(byte[] data, int offset, MemberParser parser)
        {
            _data = data;
            _offset = offset;
            _parser = parser;
            _count = -1;
        }

        /// <summary>A struct inside another, read at once by the one it is in.</summary>
        private MessageStruct(byte[] data, int offset, MemberParser parser, MessageStruct parent, int index)
        {
            _data = data;
            _offset = offset;
            _parser = parser;
            _parent = parent;
            _index = index;
            _count = -1;
        }

        /// <summary>A vector of <paramref name="length"/> entries, each read with <paramref name="parser"/>.</summary>
        private MessageStruct(byte[] data, int offset, MemberParser parser, int length, MessageStruct parent, int index)
        {
            _data = data;
            _offset = offset;
            _parser = parser;
            _parent = parent;
            _index = index;
            _count = length;
        }

        /// <summary>The number of fields, or of entries for a vector.</summary>
        public virtual int Count
        {
            get
            {
                EnsureParsed();
                return _count;
            }
        }

        /// <summary>A field's value by position: a number, a string, or a <see cref="MessageStruct"/>; null past the end.</summary>
        public virtual object this[int index]
        {
            get
            {
                EnsureParsed();
                return index >= 0 && index < _count ? _fields[index].Value : null;
            }
        }

        /// <summary>A field's value by name, ignoring case; null when there is no such field.</summary>
        public virtual object this[string name]
        {
            get
            {
                EnsureParsed();
                int index = IndexFromName(name);
                return index >= 0 ? _fields[index].Value : null;
            }
        }

        /// <summary>
        /// This struct's own bytes. For a whole message, every byte of it, its type included, as
        /// Decal gave them - so a plugin that reads the type from the first four still finds it there.
        /// </summary>
        public virtual byte[] RawData
        {
            get
            {
                if (_parent != null)
                    return _parent.RawValue(_index);

                return (byte[])_data.Clone();
            }
        }

        /// <summary>
        /// The field after this one in the struct this one is in - for an entry of a vector, the
        /// next entry. Null for a whole message and for the last.
        /// </summary>
        public virtual object Next => _parent?[_index + 1];

        /// <summary>The struct or vector this one is in; null for a whole message.</summary>
        public virtual MessageStruct Parent => _parent;

        /// <summary>A field's name by position; null past the end.</summary>
        public virtual string Name(int index)
        {
            EnsureParsed();
            return index >= 0 && index < _count ? _fields[index].Name : null;
        }

        /// <summary>A field's name as the schema spells it, found ignoring case; null when there is no such field.</summary>
        public virtual string Name(string memberName)
        {
            EnsureParsed();
            int index = IndexFromName(memberName);
            return index >= 0 ? _fields[index].Name : null;
        }

        /// <summary>A field that is a struct or a vector, by position; null for anything else.</summary>
        public virtual MessageStruct Struct(int index)
        {
            EnsureParsed();
            return index >= 0 && index < _count ? StructAt(index) : null;
        }

        /// <summary>A field that is a struct or a vector, by name; null for anything else, or no such field.</summary>
        public virtual MessageStruct Struct(string name)
        {
            EnsureParsed();
            int index = IndexFromName(name);
            return index >= 0 ? StructAt(index) : null;
        }

        /// <summary>A field's value as <typeparamref name="FieldType"/>, by position; the default past the end.</summary>
        /// <remarks>See <see cref="Value{FieldType}(string)"/> for how a value becomes another type.</remarks>
        public virtual FieldType Value<FieldType>(int index)
        {
            EnsureParsed();
            return index >= 0 && index < _count ? UnboxTo<FieldType>(_fields[index].Value) : default;
        }

        /// <summary>A field's value as <typeparamref name="FieldType"/>, by name; the default when there is no such field.</summary>
        /// <remarks>
        /// As Decal converted: the value itself when it already is one, otherwise whatever its
        /// type converter makes of it. So a DWORD, an int, reads as a uint, a long, a double, a
        /// bool (non-zero is true) or a string; a WORD, a short, reads as an int. A conversion that
        /// cannot be made throws as it did under Decal: a negative int asked for as a uint throws
        /// OverflowException, and a string or a struct asked for as a number InvalidCastException.
        /// </remarks>
        public virtual FieldType Value<FieldType>(string name)
        {
            EnsureParsed();
            int index = IndexFromName(name);
            return index >= 0 ? UnboxTo<FieldType>(_fields[index].Value) : default;
        }

        /// <summary>A field's own bytes, by position; null past the end.</summary>
        public virtual byte[] RawValue(int index)
        {
            EnsureParsed();
            return index >= 0 && index < _count ? BytesOf(index) : null;
        }

        /// <summary>A field's own bytes, by name; null when there is no such field.</summary>
        public virtual byte[] RawValue(string name)
        {
            EnsureParsed();
            int index = IndexFromName(name);
            return index >= 0 ? BytesOf(index) : null;
        }

        /// <summary>
        /// Called once, with what went wrong, when the message's bytes ran out or did not make
        /// sense before the schema did. Only a whole message is ever parsed on its own - every
        /// struct inside it is read as part of it - so only the outermost has this set.
        /// </summary>
        internal Action<MessageStruct, Exception> ParseFailed { get; set; }

        /// <summary>How many bytes the whole message is, its type included, without copying them.</summary>
        internal int MessageLength => _data?.Length ?? 0;

        private MessageStruct StructAt(int index)
        {
            MemberParserType type = _fields[index].Type;
            return type == MemberParserType.Struct || type == MemberParserType.Vector ? _fields[index].Value as MessageStruct : null;
        }

        private byte[] BytesOf(int index)
        {
            byte[] bytes = new byte[_fields[index].Length];
            Buffer.BlockCopy(_data, _fields[index].Offset, bytes, 0, bytes.Length);
            return bytes;
        }

        private int IndexFromName(string name)
        {
            for (int i = 0; i < _count; i++)
            {
                if (string.Compare(name, _fields[i].Name, ignoreCase: true, CultureInfo.InvariantCulture) == 0)
                    return i;
            }

            return -1;
        }

        private void EnsureParsed()
        {
            if (_parsed)
                return;

            try
            {
                if (_count == -1)
                    ParseStruct();
                else
                    ParseVector();
            }
            catch (Exception ex)
            {
                // What was read before the bytes gave out stands, as it did in Decal once its
                // throw had passed; the fields array is simply longer than the count.
                _parsed = true;
                _fields ??= Array.Empty<MessageField>();
                if (_count < 0)
                    _count = 0;

                ParseFailed?.Invoke(this, ex);
            }
        }

        private void ParseStruct()
        {
            _fields = new MessageField[Math.Max(CountMembers(_parser), 1)];
            _parsed = true;
            int byteIndex = _offset;
            _count = 0;
            ParseHelper(_parser, ref _count, ref byteIndex);
            _length = byteIndex - _offset;
            Array.Resize(ref _fields, _count);
        }

        private void ParseVector()
        {
            int count = _count;
            _fields = new MessageField[count];
            _parsed = true;
            int byteIndex = _offset;

            for (int i = 0; i < count; i++)
            {
                MessageStruct entry = new MessageStruct(_data, byteIndex, _parser, this, i);
                _fields[i].Name = i.ToString(CultureInfo.InvariantCulture);
                _fields[i].Offset = byteIndex;
                _fields[i].Type = MemberParserType.Struct;
                entry.ParseStruct();
                _fields[i].Value = entry;
                _fields[i].Length = entry._length;
                byteIndex += entry._length;
            }

            _length = byteIndex - _offset;
        }

        /// <summary>How many fields there could be, counting every case's as though all were chosen.</summary>
        private static int CountMembers(MemberParser parser)
        {
            int count = 0;
            for (; parser != null; parser = parser.Next)
                count += parser.MemberType == MemberParserType.Case ? CountMembers(parser.Child) : 1;

            return count;
        }

        /// <summary>
        /// Reads members from <paramref name="byteIndex"/> into fields from <paramref name="fieldIndex"/>,
        /// leaving both just past what was read. A case that is chosen reads its members into
        /// this struct's own fields, which is why a switch's fields are found beside the struct's.
        /// </summary>
        private void ParseHelper(MemberParser parser, ref int fieldIndex, ref int byteIndex)
        {
            for (; parser != null; parser = parser.Next)
            {
                if (parser.PreAlignment != 0)
                {
                    int over = byteIndex % parser.PreAlignment;
                    if (over != 0)
                        byteIndex += parser.PreAlignment - over;
                }

                bool present = parser.Condition == MemberParserCondition.None;
                if (!present)
                {
                    object tested = FindUpwards(parser.ConditionField);
                    if (tested != null)
                    {
                        long value = (AsLong(tested) ^ parser.ConditionXor) & parser.ConditionAnd;
                        switch (parser.Condition)
                        {
                            case MemberParserCondition.EQ:
                                present = value == parser.ConditionResult;
                                break;
                            case MemberParserCondition.NE:
                                present = value != parser.ConditionResult;
                                break;
                            case MemberParserCondition.GE:
                                present = value >= parser.ConditionResult;
                                break;
                            case MemberParserCondition.GT:
                                present = value > parser.ConditionResult;
                                break;
                            case MemberParserCondition.LE:
                                present = value <= parser.ConditionResult;
                                break;
                            case MemberParserCondition.LT:
                                present = value < parser.ConditionResult;
                                break;
                        }
                    }
                }

                if (present)
                {
                    if (fieldIndex >= _fields.Length)
                        Array.Resize(ref _fields, _fields.Length * 2);

                    _fields[fieldIndex].Name = parser.MemberName;
                    _fields[fieldIndex].Offset = byteIndex;
                    _fields[fieldIndex].Type = parser.MemberType;
                    ReadMember(parser, ref fieldIndex, ref byteIndex);
                    _fields[fieldIndex].Length = byteIndex - _fields[fieldIndex].Offset;
                }

                if (parser.PostAlignment != 0)
                {
                    int over = byteIndex % parser.PostAlignment;
                    if (over != 0)
                    {
                        int padding = parser.PostAlignment - over;
                        byteIndex += padding;
                        if (present)
                            _fields[fieldIndex].Length += padding;
                    }
                }

                if (present)
                    fieldIndex++;
            }
        }

        private void ReadMember(MemberParser parser, ref int fieldIndex, ref int byteIndex)
        {
            switch (parser.MemberType)
            {
                case MemberParserType.BYTE:
                    Need(byteIndex, 1);
                    _fields[fieldIndex].Value = _data[byteIndex++];
                    break;

                case MemberParserType.WORD:
                    Need(byteIndex, 2);
                    _fields[fieldIndex].Value = BitConverter.ToInt16(_data, byteIndex);
                    byteIndex += 2;
                    break;

                case MemberParserType.PackedWORD:
                    _fields[fieldIndex].Value = ReadPackedWord(ref byteIndex);
                    break;

                case MemberParserType.DWORD:
                    Need(byteIndex, 4);
                    _fields[fieldIndex].Value = BitConverter.ToInt32(_data, byteIndex);
                    byteIndex += 4;
                    break;

                case MemberParserType.PackedDWORD:
                {
                    // A WORD, or - top bit set - the low fifteen bits of it above the next WORD.
                    Need(byteIndex, 2);
                    int value = BitConverter.ToInt16(_data, byteIndex);
                    byteIndex += 2;
                    if ((value & 0x8000) != 0)
                    {
                        Need(byteIndex, 2);
                        value = ((value & 0x7FFF) << 16) | (BitConverter.ToInt16(_data, byteIndex) & 0xFFFF);
                        byteIndex += 2;
                    }

                    _fields[fieldIndex].Value = value;
                    break;
                }

                case MemberParserType.QWORD:
                    Need(byteIndex, 8);
                    _fields[fieldIndex].Value = BitConverter.ToInt64(_data, byteIndex);
                    byteIndex += 8;
                    break;

                case MemberParserType.@float:
                    Need(byteIndex, 4);
                    _fields[fieldIndex].Value = BitConverter.ToSingle(_data, byteIndex);
                    byteIndex += 4;
                    break;

                case MemberParserType.@double:
                    Need(byteIndex, 8);
                    _fields[fieldIndex].Value = BitConverter.ToDouble(_data, byteIndex);
                    byteIndex += 8;
                    break;

                case MemberParserType.String:
                {
                    // A WORD length (0xFFFF: a DWORD one follows), the game's single-byte text,
                    // then padding to four bytes - counted from the message's start, its type
                    // included, which is where the client and Decal counted from.
                    Need(byteIndex, 2);
                    int length = BitConverter.ToInt16(_data, byteIndex);
                    byteIndex += 2;
                    if (length == -1)
                    {
                        Need(byteIndex, 4);
                        length = BitConverter.ToInt32(_data, byteIndex);
                        byteIndex += 4;
                    }

                    Need(byteIndex, length);
                    _fields[fieldIndex].Value = AcEncoding.Text.GetString(_data, byteIndex, length);
                    byteIndex = (byteIndex + length + 3) & ~3;
                    break;
                }

                case MemberParserType.WString:
                {
                    // A packed length, then that many UTF-16 characters, unpadded.
                    int length = ReadPackedWord(ref byteIndex);
                    Need(byteIndex, length * 2);
                    char[] text = new char[length];
                    for (int i = 0; i < length; i++)
                    {
                        text[i] = BitConverter.ToChar(_data, byteIndex);
                        byteIndex += 2;
                    }

                    _fields[fieldIndex].Value = new string(text);
                    break;
                }

                case MemberParserType.Struct:
                {
                    MessageStruct child = new MessageStruct(_data, byteIndex, parser.Child, this, fieldIndex);
                    child.ParseStruct();
                    byteIndex += child._length;
                    _fields[fieldIndex].Value = child;
                    break;
                }

                case MemberParserType.Vector:
                {
                    object found = FindUpwards(parser.LengthField);
                    long length = found != null ? AsLong(found) : 0;
                    long mask = parser.LengthMask;
                    length &= mask;
                    if (mask != 0)
                    {
                        while ((mask & 1) == 0)
                        {
                            length >>= 1;
                            mask >>= 1;
                        }
                    }

                    // Decal took any length on trust and asked for that many entries at once, so a
                    // garbled one ran it out of memory; no vector can have more entries than there
                    // are bytes left to read them from.
                    int count = unchecked((int)length + parser.LengthDelta);
                    if (count < 0 || count > _data.Length - byteIndex)
                        throw new InvalidDataException($"The vector '{parser.MemberName}' says it has {count} entries, more than the {_data.Length - byteIndex} bytes left.");

                    MessageStruct vector = new MessageStruct(_data, byteIndex, parser.Child, count, this, fieldIndex);
                    vector.ParseVector();
                    byteIndex += vector._length;
                    _fields[fieldIndex].Value = vector;
                    break;
                }

                case MemberParserType.Case:
                {
                    // The case's members become this struct's. The field opened for the case is
                    // its first member's; with none, it stays, nameless - Decal's quirk, kept.
                    int first = fieldIndex;
                    ParseHelper(parser.Child, ref fieldIndex, ref byteIndex);
                    if (fieldIndex > first)
                        fieldIndex--;

                    break;
                }
            }
        }

        private int ReadPackedWord(ref int byteIndex)
        {
            Need(byteIndex, 1);
            int value = _data[byteIndex++];
            if ((value & 0x80) != 0)
            {
                Need(byteIndex, 1);
                value = ((value & 0x7F) << 8) | _data[byteIndex++];
            }

            return value;
        }

        private void Need(int byteIndex, int count)
        {
            if (count < 0 || byteIndex > _data.Length - count)
                throw new EndOfStreamException($"The message ended at byte {_data.Length}; a field at {byteIndex} needs {count} more.");
        }

        /// <summary>
        /// A field a condition or a vector's length refers to: in this struct as read so far, or
        /// else in the struct it is in, and so on out.
        /// </summary>
        private object FindUpwards(string name)
        {
            for (MessageStruct where = this; where != null; where = where._parent)
            {
                int index = where.IndexFromName(name);
                if (index >= 0 && where._fields[index].Value != null)
                    return where._fields[index].Value;
            }

            return null;
        }

        /// <summary>A tested value as Decal tested it: unsigned at its own width; anything not a whole number, 0.</summary>
        private static long AsLong(object value)
            => value switch
            {
                int i => i & 0xFFFFFFFFL,
                short s => s & 0xFFFF,
                byte b => b,
                long l => l,
                _ => 0,
            };

        /// <summary>
        /// Decal's conversion: a cast, and when that fails, the value's type converter -
        /// whatever <c>TypeDescriptor</c> gives for an int converts to any primitive and to a
        /// string, through <c>Convert.ChangeType</c>, overflow checks and all.
        /// </summary>
        private static T UnboxTo<T>(object value)
        {
            if (value is T same)
                return same;

            // A nameless field left by an empty case. Decal's cast threw NullReferenceException
            // for a value type here; the default is what every other missing value reads as.
            if (value == null)
                return default;

            // Decal's cast unboxed an int as an enum whose underlying type is int, which no
            // converter does; only an enum needs the cast tried, so nothing else pays for a throw.
            if (typeof(T).IsEnum)
            {
                try
                {
                    return (T)value;
                }
                catch (InvalidCastException)
                {
                }
            }

            TypeConverter converter = TypeDescriptor.GetConverter(value.GetType());
            if (converter.CanConvertTo(typeof(T)))
                return (T)converter.ConvertTo(value, typeof(T));

            throw new InvalidCastException($"A {value.GetType().Name} field cannot be read as {typeof(T).Name}.");
        }
    }

    /// <summary>
    /// A whole network message: its <see cref="Type"/>, and its fields as <see cref="MessageStruct"/> reads them.
    /// </summary>
    /// <remarks>
    /// The type is the message's opcode. A game event (0xF7B0) or a game action (0xF7B1) is
    /// one type for all of them, as in Decal: which event or action it is, is the "event" or
    /// "action" field, and messages.xml switches on that to find the rest.
    /// </remarks>
    public class Message : MessageStruct
    {
        private readonly int _type;
        private readonly MessageStruct _struct;

        /// <summary>The schema whose parsers <see cref="GetParser"/> answers from: the one the running Decal reads with.</summary>
        private static MessageSchema _schema;

        /// <summary>
        /// A message from all its bytes, the four of its type first, read with
        /// <paramref name="Parser"/>. Decal's own constructor, kept for plugins that built
        /// messages through it by reflection; the bytes are copied, as Decal copied them.
        /// </summary>
        internal Message(byte[] Data, MemberParser Parser)
            : this(BitConverter.ToInt32(Data, 0), new MessageStruct((byte[])Data.Clone(), 4, Parser))
        {
        }

        internal Message(Message Source)
            : this(Source._type, Source._struct, Source.Direction)
        {
        }

        private Message(int type, MessageStruct fields, MessageDirection direction = MessageDirection.Inbound)
        {
            _type = type;
            _struct = fields;
            Direction = direction;
        }

        /// <summary>A message over bytes it may keep - the type's four first - without copying them.</summary>
        internal static Message Over(byte[] data, MemberParser parser, MessageDirection direction)
            => new Message(BitConverter.ToInt32(data, 0), new MessageStruct(data, 4, parser), direction);

        /// <summary>The message's opcode: 0xF745 for a created object, 0xF7B0 for any game event.</summary>
        public int Type => _type;

        public override int Count => _struct.Count;

        public override object this[int index] => _struct[index];

        public override object this[string name] => _struct[name];

        public override byte[] RawData => _struct.RawData;

        public override object Next => _struct.Next;

        public override MessageStruct Parent => _struct.Parent;

        public override string Name(int index) => _struct.Name(index);

        public override string Name(string memberName) => _struct.Name(memberName);

        public override MessageStruct Struct(int index) => _struct.Struct(index);

        public override MessageStruct Struct(string name) => _struct.Struct(name);

        public override FieldType Value<FieldType>(int index) => _struct.Value<FieldType>(index);

        public override FieldType Value<FieldType>(string name) => _struct.Value<FieldType>(name);

        public override byte[] RawValue(int index) => _struct.RawValue(index);

        public override byte[] RawValue(string name) => _struct.RawValue(name);

        /// <summary>How many bytes it is, its type's four included.</summary>
        internal int RawLength => _struct.MessageLength;

        /// <summary>Which way it went: from the server, or from the client.</summary>
        internal MessageDirection Direction { get; }

        /// <summary>Has <paramref name="report"/> told, once, if the message turns out not to fit its schema.</summary>
        internal void ReportUnfitTo(Action<Message, Exception> report)
            => _struct.ParseFailed = report == null ? null : (_, ex) => report(this, ex);

        /// <summary>The schema <see cref="GetParser"/> uses, set by the runtime when it picks one.</summary>
        internal static MessageSchema Schema
        {
            get => _schema ?? MessageSchema.Shipped;
            set => _schema = value;
        }

        /// <summary>
        /// The parser for a message type going one way, or null when the schema has none.
        /// Decal's own static, kept for plugins that called it by reflection.
        /// </summary>
        internal static MemberParser GetParser(int type, MessageDirection dir) => Schema.GetParser(type, dir);
    }
}
