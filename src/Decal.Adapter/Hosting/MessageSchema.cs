using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using Decal.Adapter.NetParser;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// Decal's messages.xml, compiled: what turns a message's bytes into the named fields a
    /// plugin reads from <see cref="Message"/>.
    /// </summary>
    /// <remarks>
    /// Read the way Decal 2.9.8.3 read it, element for element: the datatypes, ten of them
    /// primitive and the rest structs of fields; messages by hex type and direction - inbound
    /// unless it says "outbound" or "both", the first of a type winning; fields, vectors with a
    /// length field and an optional mask and skip, switches with their cases, maskmaps with their
    /// masks and xor, and alignment after whatever precedes it. Every number is hex, with or
    /// without its 0x. Anything else is skipped, as Decal skipped it, so a newer file with more
    /// in it still loads.
    ///
    /// <para>
    /// A copy of Decal's own file is built into this assembly - Decal's files are free to use -
    /// so messages are read whether or not Decal is installed; <see cref="Choose"/> prefers the
    /// install's when it is a later revision, which is what Decal's Update button fetched.
    /// </para>
    /// </remarks>
    public sealed class MessageSchema
    {
        /// <summary>Where the built-in copy is, in this assembly's resources.</summary>
        public const string ResourceName = "Decal.Adapter.messages.xml";

        private static readonly Lazy<MessageSchema> _shipped = new Lazy<MessageSchema>(LoadShipped);
        private static readonly Dictionary<string, (DateTime Written, MessageSchema Schema)> _loaded = new Dictionary<string, (DateTime, MessageSchema)>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<int, MemberParser> _recv = new Dictionary<int, MemberParser>();
        private readonly Dictionary<int, MemberParser> _send = new Dictionary<int, MemberParser>();
        private readonly Dictionary<string, MemberParser> _types = new Dictionary<string, MemberParser>();
        /// <summary>The file being compiled, for a type used before it is defined; let go once compiled.</summary>
        private XmlDocument _document;

        private MessageSchema(XmlDocument document, string source)
        {
            _document = document;
            Source = source;
            Revision = ReadRevision(document);

            foreach (XmlNode type in document.SelectNodes("/schema/datatypes/type"))
                ParseType(type);

            foreach (XmlNode message in document.SelectNodes("/schema/messages/message"))
                ParseMessage(message);

            _document = null;
        }

        /// <summary>The copy built into this assembly.</summary>
        public static MessageSchema Shipped => _shipped.Value;

        /// <summary>Where it was read from: a path, or "built in" for <see cref="Shipped"/>.</summary>
        public string Source { get; }

        /// <summary>The file's own revision, from its &lt;revision version="..."&gt;; null when it has none.</summary>
        public string Revision { get; }

        /// <summary>How many message types it reads arriving from the server.</summary>
        public int InboundCount => _recv.Count;

        /// <summary>How many it reads going to the server.</summary>
        public int OutboundCount => _send.Count;

        /// <summary>
        /// A messages.xml read from disk, or the same one again if it has not changed since -
        /// every Decal started in a process asks, and the file is the same each time.
        /// </summary>
        public static MessageSchema Load(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            string full = Path.GetFullPath(path);
            DateTime written = File.GetLastWriteTimeUtc(full);
            lock (_loaded)
            {
                if (_loaded.TryGetValue(full, out (DateTime Written, MessageSchema Schema) known) && known.Written == written)
                    return known.Schema;
            }

            XmlDocument document = new XmlDocument();
            document.Load(full);
            MessageSchema schema = new MessageSchema(document, full);

            lock (_loaded)
                _loaded[full] = (written, schema);

            return schema;
        }

        /// <summary>A messages.xml read from a stream; <paramref name="source"/> says where from.</summary>
        public static MessageSchema Read(Stream stream, string source)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            XmlDocument document = new XmlDocument();
            document.Load(stream);
            return new MessageSchema(document, source ?? "a stream");
        }

        /// <summary>
        /// The schema to read messages with: the Decal install's messages.xml at
        /// <paramref name="installed"/> when it is a later revision than the built-in copy and
        /// reads, otherwise the built-in copy.
        /// </summary>
        /// <param name="installed">The install's messages.xml, or null when there is no install.</param>
        /// <param name="why">Why the one chosen was, in a sentence, for the log.</param>
        public static MessageSchema Choose(string installed, out string why)
        {
            MessageSchema shipped = Shipped;

            if (string.IsNullOrEmpty(installed) || !File.Exists(installed))
            {
                why = $"messages.xml revision {shipped.Revision}, built in; Decal's own was not found";
                return shipped;
            }

            string revision;
            try
            {
                revision = ReadRevision(installed);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException)
            {
                why = $"messages.xml revision {shipped.Revision}, built in; {installed} could not be read ({ex.Message})";
                return shipped;
            }

            if (CompareRevisions(revision, shipped.Revision) <= 0)
            {
                why = $"messages.xml revision {shipped.Revision}, built in; {installed} is revision {revision ?? "unknown"}, no later";
                return shipped;
            }

            try
            {
                MessageSchema schema = Load(installed);
                why = $"messages.xml revision {schema.Revision} from {installed}, later than the built-in {shipped.Revision}";
                return schema;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException)
            {
                why = $"messages.xml revision {shipped.Revision}, built in; {installed} is later but could not be read ({ex.Message})";
                return shipped;
            }
        }

        /// <summary>
        /// A message to read against this schema, from its opcode and the bytes after it. Nothing
        /// is read until something is asked of it; the bytes are copied, so the caller's may change.
        /// </summary>
        /// <param name="direction">
        /// Which way it went: the schema reads a type differently each way, or only one way - a
        /// game action, 0xF7B1, only going out.
        /// </param>
        public Message Parse(uint opcode, ReadOnlySpan<byte> payload, MessageDirection direction)
        {
            byte[] data = new byte[4 + payload.Length];
            BitConverter.TryWriteBytes(data.AsSpan(0, 4), opcode);
            payload.CopyTo(data.AsSpan(4));
            return Message.Over(data, GetParser(unchecked((int)opcode), direction), direction);
        }

        /// <summary>Whether this schema reads a message of this type going this way.</summary>
        public bool Knows(uint opcode, MessageDirection direction) => GetParser(unchecked((int)opcode), direction) != null;

        internal MemberParser GetParser(int type, MessageDirection direction)
        {
            Dictionary<int, MemberParser> parsers = direction == MessageDirection.Outbound ? _send : _recv;
            return parsers.TryGetValue(type, out MemberParser parser) ? parser : null;
        }

        // ------------------------------------------------------------------- reading the file

        private static MessageSchema LoadShipped()
        {
            using Stream stream = typeof(MessageSchema).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"{ResourceName} is missing from {typeof(MessageSchema).Assembly.GetName().Name}.");
            return Read(stream, "built in");
        }

        private static string ReadRevision(XmlDocument document)
            => (document.SelectSingleNode("/schema/revision/@version") as XmlAttribute)?.Value;

        /// <summary>Only as far as the revision: the file is otherwise read only if it is chosen.</summary>
        private static string ReadRevision(string path)
        {
            using XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreComments = true, DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (reader.LocalName == "revision")
                    return reader.GetAttribute("version");

                // The revision comes before any of the schema proper; past that, there is none.
                if (reader.LocalName == "datatypes" || reader.LocalName == "messages")
                    return null;
            }

            return null;
        }

        /// <summary>Revisions are dotted numbers, "2013.07.27.0"; one that is not counts as earlier than any that is.</summary>
        private static int CompareRevisions(string a, string b)
        {
            bool haveA = Version.TryParse(a, out Version va);
            bool haveB = Version.TryParse(b, out Version vb);
            if (haveA && haveB)
                return va.CompareTo(vb);

            return haveA.CompareTo(haveB);
        }

        private void ParseType(XmlNode node)
        {
            string name = Attribute(node, "name");
            if (name == null || _types.ContainsKey(name))
                return;

            MemberParser parser = new MemberParser();
            _types.Add(name, parser);

            if (Is(Attribute(node, "primitive"), "true"))
            {
                // A primitive the reader has no rule for reads as the first, a BYTE - Decal's default too.
                foreach (MemberParserType primitive in Primitives)
                {
                    if (Is(name, primitive.ToString()))
                    {
                        parser.MemberType = primitive;
                        break;
                    }
                }
            }
            else
            {
                parser.MemberType = MemberParserType.Struct;
                parser.Child = ParseStruct(node.ChildNodes);
            }
        }

        private static readonly MemberParserType[] Primitives =
        {
            MemberParserType.BYTE, MemberParserType.WORD, MemberParserType.PackedWORD, MemberParserType.DWORD,
            MemberParserType.PackedDWORD, MemberParserType.QWORD, MemberParserType.@float, MemberParserType.@double,
            MemberParserType.String, MemberParserType.WString,
        };

        private void ParseMessage(XmlNode node)
        {
            string typeText = Attribute(node, "type");
            if (typeText == null || !TryHex(typeText, out long type))
                return;

            int key = unchecked((int)type);
            bool inbound = true;
            bool outbound = false;

            string direction = Attribute(node, "direction");
            if (Is(direction, "outbound"))
            {
                inbound = false;
                outbound = true;
            }
            else if (Is(direction, "both"))
            {
                outbound = true;
            }

            if ((inbound && !_recv.ContainsKey(key)) || (outbound && !_send.ContainsKey(key)))
            {
                MemberParser parser = ParseStruct(node.ChildNodes);
                if (inbound)
                    _recv.TryAdd(key, parser);
                if (outbound)
                    _send.TryAdd(key, parser);
            }
        }

        private MemberParser ParseStruct(XmlNodeList nodes)
        {
            MemberParser head = new MemberParser();
            MemberParser last = head;

            foreach (XmlNode node in nodes)
            {
                if (node.NodeType != XmlNodeType.Element)
                    continue;

                string element = node.Name;
                if (Is(element, "field"))
                {
                    string type = Attribute(node, "type");
                    if (type == null)
                        continue;

                    if (!_types.ContainsKey(type))
                    {
                        // A type used before it is defined, or never defined: the latter is not
                        // a field anyone can read, so it is left out rather than failing the file.
                        XmlNode definition = _document.SelectSingleNode("/schema/datatypes/type[@name=" + XPathLiteral(type) + "]");
                        if (definition == null)
                            continue;

                        ParseType(definition);
                    }

                    last.Next = new MemberParser(_types[type]);
                    last = last.Next;
                    last.MemberName = Attribute(node, "name");
                }
                else if (Is(element, "maskmap"))
                {
                    TryHex(Attribute(node, "xor"), out long xor);
                    string field = Attribute(node, "name");

                    last.Next = ParseCases(node.ChildNodes, "mask");
                    while (last.Next != null)
                    {
                        // Each mask is there when its bits, after the xor, are not all clear.
                        last = last.Next;
                        last.Condition = MemberParserCondition.NE;
                        last.ConditionResult = 0;
                        last.ConditionField = field;
                        last.ConditionXor = xor;
                    }
                }
                else if (Is(element, "switch"))
                {
                    string field = Attribute(node, "name");
                    long mask = -1;

                    // Decal took a switch's mask as the field to test as well as the mask, so a
                    // masked switch never matched anything. No messages.xml has one; it is kept so
                    // one that does reads as it did.
                    string maskText = Attribute(node, "mask");
                    if (maskText != null && TryHex(maskText, out mask))
                        field = StripHex(maskText);

                    last.Next = ParseCases(node.ChildNodes, "case");
                    while (last.Next != null)
                    {
                        // Each case is there when the field, masked, is its value.
                        last = last.Next;
                        last.Condition = MemberParserCondition.EQ;
                        last.ConditionField = field;
                        last.ConditionAnd = mask;
                    }
                }
                else if (Is(element, "vector"))
                {
                    last.Next = new MemberParser();
                    last = last.Next;
                    last.MemberType = MemberParserType.Vector;
                    last.MemberName = Attribute(node, "name");
                    last.LengthField = Attribute(node, "length");

                    long mask = -1;
                    string maskText = Attribute(node, "mask");
                    if (maskText != null)
                        TryHex(maskText, out mask);

                    last.LengthMask = mask;

                    TryHex(Attribute(node, "skip"), out long skip);
                    last.LengthDelta = -(int)skip;
                    last.Child = ParseStruct(node.ChildNodes);
                }
                else if (Is(element, "align"))
                {
                    // Alignment belongs to whatever came before it; at the start of a struct, to
                    // its first member, before it.
                    string type = Attribute(node, "type");
                    if (Is(type, "WORD"))
                        last.PostAlignment = 2;
                    else if (Is(type, "DWORD"))
                        last.PostAlignment = 4;
                    else if (Is(type, "QWORD"))
                        last.PostAlignment = 8;
                }
            }

            if (head.PostAlignment != 0 && head.Next != null)
                head.Next.PreAlignment = head.PostAlignment;

            return head.Next;
        }

        /// <summary>A maskmap's masks or a switch's cases, each a case holding its members, with its value as the mask or the result.</summary>
        private MemberParser ParseCases(XmlNodeList nodes, string element)
        {
            bool masks = element == "mask";
            MemberParser head = new MemberParser();
            MemberParser last = head;

            foreach (XmlNode node in nodes)
            {
                if (node.NodeType != XmlNodeType.Element || !Is(node.Name, element))
                    continue;

                TryHex(Attribute(node, "value"), out long value);

                last.Next = new MemberParser { MemberType = MemberParserType.Case };
                last = last.Next;
                if (masks)
                    last.ConditionAnd = value;
                else
                    last.ConditionResult = value;

                last.Child = ParseStruct(node.ChildNodes);
            }

            return head.Next;
        }

        private static string Attribute(XmlNode node, string name) => node?.Attributes?[name]?.Value;

        private static bool Is(string a, string b) => a != null && string.Compare(a, b, ignoreCase: true, CultureInfo.InvariantCulture) == 0;

        private static string StripHex(string text)
            => text.Length > 1 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X') ? text.Substring(2) : text;

        /// <summary>A number as Decal read every number in the file: hex, 0x or not. False, and 0, for anything else.</summary>
        private static bool TryHex(string text, out long value)
        {
            value = 0;
            return !string.IsNullOrEmpty(text) && long.TryParse(StripHex(text.Trim()), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static string XPathLiteral(string text)
            => text.Contains('\'') ? "\"" + text + "\"" : "'" + text + "'";
    }
}
