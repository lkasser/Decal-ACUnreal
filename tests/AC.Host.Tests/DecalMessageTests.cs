using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Protocol;
using Decal.Adapter;
using Decal.Adapter.Hosting;
using Decal.Compat;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Decal's raw messages: messages.xml read as Decal read it, real captured messages parsed
    /// against it field by field, and ServerDispatch, ClientDispatch and MessageProcessed raised
    /// from the host's own traffic.
    /// </summary>
    /// <remarks>
    /// The captured messages are from a session on ACE, each the bytes after the opcode exactly
    /// as the server or the client sent them. Every message in two whole captured sessions was
    /// parsed while this was written, and none failed to fit; these are the ones that between
    /// them use every construct the schema has.
    /// </remarks>
    [Collection(DecalCollection.Name)]
    public class DecalMessageTests
    {
        private const uint Player = 0x50000006;

        /// <summary>A CreateObject for a pack: palettes, textures and models in its model data, a maskmap in each part.</summary>
        private const string PackCreated =
            "F23E008011010301EF0BF20B0000008302830200810281020082028202007305811802001404000065000000140000202B000034510100020000E03F"
            + "00000000000000000000000000000000000000003E40210004005061636B00008800B21B00020000130000001800DAAB0200380000000000003F06000050000000004705";

        /// <summary>The character standing still: flags 0x34, grounded with no x or y in its rotation.</summary>
        private const string PlayerMoved = "06000050340000003000112B001FFE42E09A2A431F0540421FD0003F2C3B5DBFBC00010000000000";

        /// <summary>The client casting a spell at a creature: game action 0x004A.</summary>
        private const string CastAtTarget = "160400004A0000002F2B0080F7140000";

        /// <summary>The contents of the pack above, sixteen items: game event 0x0196.</summary>
        private const string PackContents =
            "060000500400000096010000F23E00801000000096A2008000000000969E0080000000004A9200800000000098990080000000006343008000000000"
            + "7E8B0080000000005B86008000000000BE390080000000001078008000000000BE26008000000000B1050080000000008819008000000000CB1F0080"
            + "00000000F61E0080000000000366008000000000D466008000000000";

        /// <summary>An enchantment landing on the character: game event 0x02C2, one struct of fifteen fields.</summary>
        private const string EnchantmentAdded =
            "0600005028020000C2020000561401007502010001000000000000000000000000000000000028400600005000000000008026C40000000000000000"
            + "04900002340100000000A04100000000";

        private static Message Parse(uint opcode, string hex, MessageDirection direction = MessageDirection.Inbound)
            => MessageSchema.Shipped.Parse(opcode, Convert.FromHexString(hex), direction);

        private static Message Parse(WireWriter writer, MessageDirection direction = MessageDirection.Inbound)
        {
            byte[] bytes = writer.ToArray();
            return MessageSchema.Shipped.Parse(BitConverter.ToUInt32(bytes, 0), bytes.AsSpan(4), direction);
        }

        // ------------------------------------------------------------------- the schema

        [Fact]
        public void TheBuiltInSchemaIsDecalsOwnMessagesXml()
        {
            MessageSchema schema = MessageSchema.Shipped;

            Assert.Equal("2013.07.27.0", schema.Revision);
            Assert.Equal("built in", schema.Source);

            // 47 messages: 45 the server sends, two only the client does, and 0xF7DE both ways.
            Assert.Equal(45, schema.InboundCount);
            Assert.Equal(3, schema.OutboundCount);
            Assert.True(schema.Knows(0xF745, MessageDirection.Inbound));
            Assert.False(schema.Knows(0xF745, MessageDirection.Outbound));
            Assert.True(schema.Knows(0xF7B1, MessageDirection.Outbound));
            Assert.False(schema.Knows(0xF7B1, MessageDirection.Inbound));
            Assert.True(schema.Knows(0xF7DE, MessageDirection.Inbound));
            Assert.True(schema.Knows(0xF7DE, MessageDirection.Outbound));
        }

        [Fact]
        public void AnInstalledSchemaIsChosenOnlyWhenItIsALaterRevision()
        {
            string folder = Path.Combine(Path.GetTempPath(), "achost-messages-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string same = Path.Combine(folder, "same.xml");
                string later = Path.Combine(folder, "later.xml");
                string broken = Path.Combine(folder, "broken.xml");
                File.WriteAllText(same, ShippedXml());
                File.WriteAllText(later, ShippedXml().Replace("2013.07.27.0", "2099.01.01.0"));
                File.WriteAllText(broken, "<schema><revision version=\"2099.01.01.0\" /><datatypes>");

                Assert.Same(MessageSchema.Shipped, MessageSchema.Choose(null, out _));
                Assert.Same(MessageSchema.Shipped, MessageSchema.Choose(Path.Combine(folder, "missing.xml"), out string why));
                Assert.Contains("not found", why);
                Assert.Same(MessageSchema.Shipped, MessageSchema.Choose(same, out why));
                Assert.Contains("no later", why);
                Assert.Same(MessageSchema.Shipped, MessageSchema.Choose(broken, out why));
                Assert.Contains("could not be read", why);

                MessageSchema chosen = MessageSchema.Choose(later, out why);
                Assert.Equal("2099.01.01.0", chosen.Revision);
                Assert.Equal(Path.GetFullPath(later), chosen.Source);
                Assert.Equal(45, chosen.InboundCount);
                Assert.Contains("later than the built-in", why);

                // The same file again is the same schema, not read twice.
                Assert.Same(chosen, MessageSchema.Load(later));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        private static string ShippedXml()
        {
            using Stream stream = typeof(MessageSchema).Assembly.GetManifestResourceStream(MessageSchema.ResourceName);
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // ------------------------------------------------------------------- reading messages

        [Fact]
        public void SpeechReadsItsStringsWithTheirPaddingAndNamesIgnoringCase()
        {
            Message message = Parse(new WireWriter(Opcodes.HearSpeech).String16L("Hello").String16L("Bobby").U32(0x50000002).U32(2));

            Assert.Equal(0x02BB, message.Type);
            Assert.Equal(4, message.Count);
            Assert.Equal("Hello", message.Value<string>("text"));
            Assert.Equal("Bobby", message.Value<string>("SENDERNAME"));
            Assert.Equal(0x50000002, message.Value<int>("sender"));
            Assert.Equal(2, message["type"]);

            // A length, five letters and a byte to make eight; "Bobby" the same - so "sender"
            // starts where it should, which it would not if the padding were wrong.
            Assert.Equal(8, message.RawValue("text").Length);
            Assert.Equal(new byte[] { 5, 0, (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0 }, message.RawValue(0));
            Assert.Equal("text", message.Name(0));
            Assert.Equal("senderName", message.Name("sendername"));
        }

        [Fact]
        public void AStringThatFillsItsFourBytesHasNoPadding()
        {
            Message message = Parse(new WireWriter(Opcodes.HearSpeech).String16L("hi").String16L("Bob").U32(7).U32(2));

            Assert.Equal(4, message.RawValue("text").Length);
            Assert.Equal("Bob", message.Value<string>("senderName"));
            Assert.Equal(7, message.Value<int>("sender"));
        }

        [Fact]
        public void AGameEventIsOneTypeWithItsEventAsAField()
        {
            Message message = Parse(WireWriter.GameEvent(Player, 0x02BD).String16L("hi there").String16L("Bob").U32(0x50000002).U32(Player).U32(3));

            Assert.Equal(0xF7B0, message.Type);
            Assert.Equal(0x02BD, message.Value<int>("event"));
            Assert.Equal("hi there", message.Value<string>("text"));
            Assert.Equal("Bob", message.Value<string>("senderName"));
            Assert.Equal(0x50000002, message.Value<int>("sender"));
            Assert.Equal(unchecked((int)Player), message.Value<int>("target"));
            Assert.Equal(8, message.Count);
        }

        [Fact]
        public void AGameActionIsReadGoingOutAndNotComingIn()
        {
            Message sent = Parse(0xF7B1, CastAtTarget, MessageDirection.Outbound);

            Assert.Equal(0xF7B1, sent.Type);
            Assert.Equal(0x004A, sent.Value<int>("action"));
            Assert.Equal(unchecked((int)0x80002B2F), sent.Value<int>("target"));
            Assert.Equal(0x14F7, sent.Value<int>("spell"));
            Assert.Equal(new[] { "sequence", "action", "target", "spell" }, Enumerable.Range(0, sent.Count).Select(sent.Name));

            // The schema has no inbound 0xF7B1, so the same bytes from the server are nothing.
            Message received = Parse(0xF7B1, CastAtTarget, MessageDirection.Inbound);
            Assert.Equal(0xF7B1, received.Type);
            Assert.Equal(0, received.Count);
            Assert.Equal(0, received.Value<int>("action"));
            Assert.Equal(20, received.RawData.Length);
        }

        [Fact]
        public void CreateObjectReadsItsMasksVectorsAndAlignment()
        {
            Message message = Parse(0xF745, PackCreated);

            Assert.Equal(0xF745, message.Type);
            Assert.Equal(new[] { "object", "model", "physics", "game" }, Enumerable.Range(0, message.Count).Select(message.Name));
            Assert.Equal(unchecked((int)0x80003EF2), message.Value<int>("object"));

            // Model data: one palette, so the maskmap on paletteCount reads the base palette,
            // then a vector of each, and the whole padded to four bytes.
            MessageStruct model = message.Struct("model");
            Assert.Equal((byte)1, model.Value<byte>("paletteCount"));
            Assert.Equal(0x0BEF, model.Value<int>("palette"));
            Assert.Equal(0x0BF2, model.Struct("palettes").Struct(0).Value<int>("palette"));
            MessageStruct textures = model.Struct("textures");
            Assert.Equal(3, textures.Count);
            Assert.Equal(0x0281, textures.Struct(1).Value<int>("old"));
            Assert.Equal(0x0573, model.Struct("models").Struct(0).Value<int>("model"));
            Assert.Equal(28, model.RawData.Length);

            // Physics: the fields its flags name and no others, in the schema's order.
            MessageStruct physics = message.Struct("physics");
            Assert.Equal(0x00021881, physics.Value<int>("flags"));
            Assert.Equal(0x02000151, physics.Value<int>("model"));
            Assert.Equal(1.75f, physics.Value<float>("scale"));
            Assert.Null(physics["position"]);
            Assert.Null(physics.Struct("position"));
            Assert.Equal(0, physics.Value<int>("equipper"));
            Assert.Null(physics.RawValue("equipper"));
            Assert.Null(physics.Name("equipper"));

            // The last WORD carries the struct's alignment in its bytes.
            Assert.Equal(4, physics.RawValue("unknown9").Length);

            MessageStruct game = message.Struct("game");
            Assert.Equal("Pack", game.Value<string>("name"));
            Assert.Equal(0x88, game.Value<int>("type"));
            Assert.Equal((byte)24, game.Value<byte>("itemSlots"));
            Assert.Equal(0.5f, game.Value<float>("approachDistance"));
            Assert.Equal(unchecked((int)Player), game.Value<int>("container"));
            Assert.Equal((short)1351, game.Value<short>("burden"));
            Assert.Equal(14, game.Count);

            // Every byte read, and the first four of the whole are its type.
            Assert.Equal(PackCreated.Length / 2, BytesRead(message));
            Assert.Equal(PackCreated.Length / 2 + 4, message.RawData.Length);
            Assert.Equal(0xF745, BitConverter.ToInt32(message.RawData, 0));
        }

        [Fact]
        public void APositionHasTheRotationItsFlagsSayAfterTheirXor()
        {
            Message message = Parse(0xF748, PlayerMoved);
            MessageStruct position = message.Struct("position");

            // 0x34: no x and no y in the rotation (0x10, 0x20), so the xor with 0x78 leaves w and z.
            Assert.Equal(0x34, position.Value<int>("flags"));
            Assert.Equal(0x2B110030, position.Value<int>("landcell"));
            Assert.Equal(new[] { "flags", "landcell", "x", "y", "z", "wQuat", "zQuat" }, Enumerable.Range(0, position.Count).Select(position.Name));
            Assert.Equal(-0.86418414f, position.Value<float>("zQuat"), 6);
            Assert.Null(position["xQuat"]);
            Assert.Null(position["dx"]);
            Assert.Equal((short)188, message.Value<short>("logins"));
            Assert.Equal(6, message.Count);
        }

        [Fact]
        public void AVectorOfStructsIsReadByIndexWithItsNeighboursAndParents()
        {
            Message message = Parse(0xF7B0, PackContents);

            Assert.Equal(0x0196, message.Value<int>("event"));
            Assert.Equal(16, message.Value<int>("itemCount"));
            MessageStruct items = message.Struct("items");
            Assert.Equal(16, items.Count);
            Assert.Equal("0", items.Name(0));
            Assert.Equal(unchecked((int)0x8000A296), items.Struct(0).Value<int>("item"));
            Assert.Equal(unchecked((int)0x800066D4), items.Struct("15").Value<int>("item"));
            Assert.Same(items.Struct(1), items.Struct(0).Next);
            Assert.Null(items.Struct(15).Next);
            Assert.Same(items, items.Struct(3).Parent);
            Assert.Same(message.Struct("items"), items);
            Assert.Equal(8, items.Struct(2).RawData.Length);
            Assert.Equal(128, items.RawData.Length);
        }

        [Fact]
        public void AStructTypeIsReadWhole()
        {
            Message message = Parse(0xF7B0, EnchantmentAdded);
            MessageStruct enchantment = message.Struct("enchantment");

            Assert.Equal(0x02C2, message.Value<int>("event"));
            Assert.Equal(15, enchantment.Count);
            Assert.Equal((short)0x1456, enchantment.Value<short>("spell"));
            Assert.Equal(12.0, enchantment.Value<double>("duration"));
            Assert.Equal(unchecked((int)Player), enchantment.Value<int>("caster"));
            Assert.Equal(20f, enchantment.Value<float>("value"));
        }

        [Fact]
        public void AWholeLoginReadsToItsLastByte()
        {
            Message message = Parse(0xF7B0, LoginHex());

            Assert.Equal(0x0013, message.Value<int>("event"));
            Assert.Equal(new[] { "character", "sequence", "event", "properties", "vectors", "options", "inventoryCount", "inventory", "equippedCount", "equipped" },
                Enumerable.Range(0, message.Count).Select(message.Name));

            // The same counts the host's own decoder finds in it.
            MessageStruct vectors = message.Struct("vectors");
            Assert.Equal(783, vectors.Struct("spellbook").Count);
            Assert.Equal(39, vectors.Struct("skills").Count);
            Assert.Equal(372, vectors.Struct("health").Value<int>("current"));
            Assert.Equal(95, message.Struct("inventory").Count);
            Assert.Equal(21, message.Struct("equipped").Count);
            Assert.Equal(message.Value<int>("equippedCount"), message.Struct("equipped").Count);

            // Every byte read: the fields' own bytes add up to the whole, its type aside.
            Assert.Equal(21 * 12, message.RawValue("equipped").Length);
            Assert.Equal(message.RawData.Length - 4, BytesRead(message));
        }

        /// <summary>How many of a message's bytes its fields account for.</summary>
        private static int BytesRead(MessageStruct message) => Enumerable.Range(0, message.Count).Sum(i => message.RawValue(i).Length);

        private static string LoginHex()
        {
            using Stream stream = typeof(DecalMessageTests).Assembly.GetManifestResourceStream("AC.Host.Tests.Resources.login-full-vitals.hex");
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }

        [Fact]
        public void ValuesConvertAsDecalConvertedThem()
        {
            Message contents = Parse(0xF7B0, PackContents);
            Message moved = Parse(0xF748, PlayerMoved);

            // The same type: no conversion at all.
            Assert.Equal(16, contents.Value<int>("itemCount"));

            // A DWORD is an int, and its converter makes any primitive or a string of it.
            Assert.Equal(16u, contents.Value<uint>("itemCount"));
            Assert.Equal(16L, contents.Value<long>("itemCount"));
            Assert.Equal(16.0, contents.Value<double>("itemCount"));
            Assert.Equal(16f, contents.Value<float>("itemCount"));
            Assert.True(contents.Value<bool>("itemCount"));
            Assert.Equal("16", contents.Value<string>("itemCount"));
            Assert.Equal(-2147467534L, contents.Value<long>("container"));

            // A WORD is a short, read as an int just as well.
            Assert.Equal(188, moved.Value<int>("logins"));

            // What Decal could not convert, it threw for: an id with its top bit set does not
            // fit a uint by Convert's rules, and a string or a struct is not a number.
            Assert.Throws<OverflowException>(() => contents.Value<uint>("container"));
            Assert.Throws<InvalidCastException>(() => Parse(new WireWriter(Opcodes.HearSpeech).String16L("x").String16L("y").U32(1).U32(2)).Value<int>("text"));
            Assert.Throws<InvalidCastException>(() => contents.Value<int>("items"));

            // A field that is not there is the default, and no field by number is nothing.
            Assert.Equal(0, contents.Value<int>("nothing"));
            Assert.Null(contents.Value<string>("nothing"));
            Assert.Null(contents["nothing"]);
            Assert.Null(contents[99]);
            Assert.Null(contents.Name(99));
            Assert.Null(contents.Struct(0));
            Assert.Null(contents.RawValue(-1));
            Assert.Null(contents.Parent);
            Assert.Null(contents.Next);
        }

        [Fact]
        public void AMessageCutShortIsReadAsFarAsItGoes()
        {
            // The pack's CreateObject without its game data's end: object, model and physics
            // are whole, the game data is not, and so is not there at all.
            string cut = PackCreated.Substring(0, PackCreated.Length - 40);
            Message message = Parse(0xF745, cut);

            Assert.Equal(3, message.Count);
            Assert.Equal(1.75f, message.Struct("physics").Value<float>("scale"));
            Assert.Null(message.Struct("game"));
            Assert.Null(message.Value<string>("name"));

            // Nor does asking again throw, or change the answer.
            Assert.Equal(3, message.Count);
        }

        [Fact]
        public void ATakenEmptyCaseLeavesANamelessFieldAsDecalsDid()
        {
            // UpdateMotion with animation type 9 and type_flags 0x02, whose mask in the
            // schema is empty: Decal still opened a field for it, nameless and valueless.
            WireWriter writer = new WireWriter(0xF74C).U32(Player).U16(1).U16(2).U16(3).U16(4).U8(9).U8(0x02).U16(0x3D)
                .U32(0).F32(1.5f).F32(90f);
            Message message = Parse(writer);

            Assert.Equal(12, message.Count);
            Assert.Equal(1.5f, message.Value<float>("animation_speed"));
            Assert.Equal(90f, message.Value<float>("heading"));
            Assert.Null(message.Name(11));
            Assert.Null(message[11]);
            Assert.Null(message["targetid"]);
        }

        // ------------------------------------------------------------------- from the host

        [Fact]
        public async Task TheHostGivesEveryMessageBothWaysInOrderOnTheGameThread()
        {
            PushTransport transport = new PushTransport();
            await using GameHost host = new GameHost(transport, new ListLog(), dataRoot: NewRoot());
            MessageWatcher watcher = new MessageWatcher();
            host.AddPlugin(watcher);
            await host.StartAsync();

            transport.Push(new WireWriter(Opcodes.HearSpeech).String16L("hi").String16L("Bob").U32(2).U32(2));
            transport.Push(PacketDirection.Outbound, 0xF7B1, Convert.FromHexString(CastAtTarget));
            transport.Push(PacketDirection.Inbound, 0xF745, Convert.FromHexString(PackCreated));
            await SettleAsync(host);

            Assert.Equal(new[] { "Inbound 02BB", "Outbound F7B1", "Inbound F745" }, watcher.Seen);
            Assert.True(watcher.AllOnGameThread);

            // After the world: the pack was already in it when its bytes went by.
            Assert.True(watcher.PackKnownWhenSeen);
        }

        [Fact]
        public async Task ServerAndClientMessagesReachFiltersPluginsAndMessageProcessedInDecalsOrder()
        {
            PushTransport transport = new PushTransport();
            await using GameHost host = new GameHost(transport, new ListLog(), dataRoot: NewRoot());
            await host.StartAsync();

            List<string> heard = new List<string>();
            DecalRuntime runtime = await OnGameThreadAsync(host, () =>
            {
                DecalRuntime started = new DecalRuntime(host);
                started.Start(new DispatchFilter(heard), AppContext.BaseDirectory);
                started.Start(new DispatchPlugin(heard), AppContext.BaseDirectory);
                return started;
            });

            transport.Push(new WireWriter(Opcodes.HearSpeech).String16L("Hello").String16L("Bob").U32(0x50000002).U32(2));
            transport.Push(PacketDirection.Outbound, 0xF7B1, Convert.FromHexString(CastAtTarget));
            transport.Push(PacketDirection.Inbound, 0xF7B0, Convert.FromHexString(PackContents));
            await SettleAsync(host);

            Assert.Equal(
                new[]
                {
                    "filter server 02BB Hello",
                    "plugin server 02BB Hello",
                    "echo server 02BB Hello",
                    "processed 02BB Hello size 28",
                    "filter client F7B1 action 004A",
                    "plugin client F7B1 action 004A",
                    "filter server F7B0 event 0196",
                    "plugin server F7B0 event 0196",
                    "echo server F7B0 event 0196",
                    "processed F7B0 event 0196 size 152",
                },
                heard);

            await OnGameThreadAsync(host, () =>
            {
                runtime.Dispose();
                return 0;
            });
        }

        [Fact]
        public async Task BaseEventAttributesWireTheDispatchEventsBothWays()
        {
            PushTransport transport = new PushTransport();
            await using GameHost host = new GameHost(transport, new ListLog(), dataRoot: NewRoot());
            await host.StartAsync();

            List<string> heard = new List<string>();
            DecalRuntime runtime = await OnGameThreadAsync(host, () =>
            {
                DecalRuntime started = new DecalRuntime(host);
                started.Start(new WiredDispatchPlugin(heard), AppContext.BaseDirectory);
                return started;
            });

            transport.Push(new WireWriter(Opcodes.HearSpeech).String16L("wired").String16L("Bob").U32(2).U32(2));
            transport.Push(PacketDirection.Outbound, 0xF7B1, Convert.FromHexString(CastAtTarget));
            await SettleAsync(host);

            // Wired in whatever order reflection lists the methods, so compared as a set.
            Assert.Equal(new[] { "echo server 02BB wired", "own client F7B1 action 004A", "own server 02BB wired" }, heard.OrderBy(h => h, StringComparer.Ordinal));
            await OnGameThreadAsync(host, () =>
            {
                runtime.Dispose();
                return 0;
            });
        }

        [Fact]
        public async Task AHandlerThatThrowsIsSaidOnceAndTheRestStillHear()
        {
            PushTransport transport = new PushTransport();
            ListLog log = new ListLog();
            await using GameHost host = new GameHost(transport, log, dataRoot: NewRoot());
            await host.StartAsync();

            List<string> heard = new List<string>();
            List<DecalFaultEventArgs> faults = new List<DecalFaultEventArgs>();
            DecalRuntime runtime = await OnGameThreadAsync(host, () =>
            {
                DecalRuntime started = new DecalRuntime(host);
                started.Faulted += (_, e) => faults.Add(e);
                started.Start(new DispatchPlugin(heard) { ThrowOnServer = true }, AppContext.BaseDirectory);
                started.Start(new DispatchFilter(heard), AppContext.BaseDirectory);
                return started;
            });

            for (int i = 0; i < 3; i++)
                transport.Push(new WireWriter(Opcodes.HearSpeech).String16L("line " + i).String16L("Bob").U32(2).U32(2));
            await SettleAsync(host);

            // The thrower heard each, threw each time, and cost no one else theirs.
            Assert.Equal(3, heard.Count(h => h.StartsWith("plugin server", StringComparison.Ordinal)));
            Assert.Equal(3, heard.Count(h => h.StartsWith("echo server", StringComparison.Ordinal)));
            Assert.Equal(3, heard.Count(h => h.StartsWith("processed", StringComparison.Ordinal)));

            // Said once, not three times, and reported as that plugin's.
            Assert.Single(log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("ServerDispatch") && l.Contains("0x02BB"));
            DecalFaultEventArgs fault = Assert.Single(faults);
            Assert.Equal(typeof(DispatchPlugin).Assembly, fault.Assembly);
            Assert.Equal(0, host.Statistics.PluginExceptions);

            await OnGameThreadAsync(host, () =>
            {
                runtime.Dispose();
                return 0;
            });
        }

        [Fact]
        public async Task AMessageThatDoesNotFitIsSaidOnceAndOnlyWhenSomeoneReadsIt()
        {
            PushTransport transport = new PushTransport();
            ListLog log = new ListLog();
            await using GameHost host = new GameHost(transport, log, dataRoot: NewRoot());
            await host.StartAsync();

            byte[] cut = Convert.FromHexString(PackCreated.Substring(0, PackCreated.Length - 40));
            DecalRuntime runtime = await OnGameThreadAsync(host, () => new DecalRuntime(host));

            // Nobody listening: nothing is made of it, so nothing is found wrong with it.
            transport.Push(PacketDirection.Inbound, 0xF745, cut);
            await SettleAsync(host);
            Assert.DoesNotContain(log.Lines, l => l.Contains("does not fit"));

            List<int> counts = new List<int>();
            await OnGameThreadAsync(host, () =>
            {
                runtime.Core.EchoFilter.ServerDispatch += (_, e) => counts.Add(e.Message.Count);
                return 0;
            });

            transport.Push(PacketDirection.Inbound, 0xF745, cut);
            transport.Push(PacketDirection.Inbound, 0xF745, cut);
            await SettleAsync(host);

            Assert.Equal(new[] { 3, 3 }, counts);
            string warning = Assert.Single(log.Lines, l => l.Contains("does not fit"));
            Assert.StartsWith("WARN", warning);
            Assert.Contains("0xF745", warning);
            Assert.Contains("2013.07.27.0", warning);

            await OnGameThreadAsync(host, () =>
            {
                runtime.Dispose();
                return 0;
            });
        }

        [Fact]
        public async Task AStoppedPluginHearsNoMoreMessagesThoughItNeverUnsubscribed()
        {
            PushTransport transport = new PushTransport();
            await using GameHost host = new GameHost(transport, new ListLog(), dataRoot: NewRoot());
            await host.StartAsync();

            List<string> heard = new List<string>();
            DispatchPlugin plugin = new DispatchPlugin(heard);
            DecalRuntime runtime = await OnGameThreadAsync(host, () =>
            {
                DecalRuntime started = new DecalRuntime(host);
                started.Start(plugin, AppContext.BaseDirectory);
                return started;
            });

            transport.Push(new WireWriter(Opcodes.HearSpeech).String16L("before").String16L("Bob").U32(2).U32(2));
            await SettleAsync(host);
            int before = heard.Count;
            Assert.True(before > 0);

            await OnGameThreadAsync(host, () =>
            {
                runtime.Stop(plugin);
                return 0;
            });
            transport.Push(new WireWriter(Opcodes.HearSpeech).String16L("after").String16L("Bob").U32(2).U32(2));
            await SettleAsync(host);

            Assert.Equal(before, heard.Count);
            await OnGameThreadAsync(host, () =>
            {
                runtime.Dispose();
                return 0;
            });
        }

        [Fact]
        public async Task DecalCompatReadsMessagesAgainstTheInstallsSchemaWhenItIsLater()
        {
            string agent = NewRoot();
            Directory.CreateDirectory(agent);
            File.WriteAllText(Path.Combine(agent, "messages.xml"), ShippedXml().Replace("2013.07.27.0", "2099.01.01.0"));

            foreach (bool installed in new[] { false, true })
            {
                ListLog log = new ListLog();
                Dictionary<string, string> settings = new Dictionary<string, string>
                {
                    ["DecalCompat:Folder"] = Path.Combine(agent, "no plugins"),
                };

                await using GameHost host = new GameHost(new PushTransport(), log, settings, NewRoot());
                DecalCompatPlugin decal = new DecalCompatPlugin(new FakeDecalRegistry { AgentPath = installed ? agent : null });
                host.AddPlugin(decal);
                await host.StartAsync();

                MessageSchema schema = await OnGameThreadAsync(host, () => decal.Runtime.Messages);
                if (installed)
                {
                    Assert.Equal("2099.01.01.0", schema.Revision);
                    Assert.Contains(log.Lines, l => l.Contains("network messages are read against messages.xml revision 2099.01.01.0 from"));
                }
                else
                {
                    Assert.Same(MessageSchema.Shipped, schema);
                    Assert.Contains(log.Lines, l => l.Contains("network messages are read against messages.xml revision 2013.07.27.0, built in"));
                }

                // No plugin asked for anything this host does not do.
                Assert.DoesNotContain(log.Lines, l => l.Contains("EchoFilter") || l.Contains("ServerDispatch"));
            }

            Directory.Delete(agent, recursive: true);
        }

        // ------------------------------------------------------------------- helpers

        private static string NewRoot() => Path.Combine(Path.GetTempPath(), "achost-messages-" + Guid.NewGuid().ToString("N"));

        private static async Task SettleAsync(GameHost host)
        {
            TaskCompletionSource done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() => done.SetResult());
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task<T> OnGameThreadAsync<T>(GameHost host, Func<T> work)
        {
            TaskCompletionSource<T> done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>How a test's handler says what it heard: type, then what tells it apart.</summary>
        private static string Describe(Message message)
            => message.Type switch
            {
                0x02BB => $"02BB {message.Value<string>("text")}",
                0xF7B0 => $"F7B0 event {message.Value<int>("event"):X4}",
                0xF7B1 => $"F7B1 action {message.Value<int>("action"):X4}",
                _ => $"{message.Type:X4}",
            };

        /// <summary>A transport a test hands messages to, either way; open until disposed.</summary>
        private sealed class PushTransport : IGameTransport
        {
            public string Description => "pushed";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public bool CanSend => false;

            public bool CanShowInGame => false;

            public void Push(PacketDirection direction, uint opcode, byte[] payload)
                => MessageReceived?.Invoke(this, new GameMessageEventArgs(direction, AcMessage.Create(opcode, payload)));

            public void Push(WireWriter writer)
            {
                byte[] bytes = writer.ToArray();
                Push(PacketDirection.Inbound, BitConverter.ToUInt32(bytes, 0), bytes.AsSpan(4).ToArray());
            }

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                Ended?.Invoke(this, EventArgs.Empty);
                MessageReceived = null;
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>One of the host's own plugins, watching the raw messages go by.</summary>
        private sealed class MessageWatcher : IPlugin
        {
            private GameHost _host;

            public string Name => "Watcher";

            public List<string> Seen { get; } = new List<string>();

            public bool AllOnGameThread { get; private set; } = true;

            public bool PackKnownWhenSeen { get; private set; }

            public void Startup(IHost host)
            {
                _host = (GameHost)host;
                host.MessageSeen += (_, e) =>
                {
                    Seen.Add($"{e.Direction} {e.Message.Opcode:X4}");
                    AllOnGameThread &= _host.IsGameThread;
                    if (e.Message.Opcode == 0xF745)
                        PackKnownWhenSeen = _host.World.TryGet(0x80003EF2, out AC.Host.World.WorldObject _);
                };
            }

            public void Shutdown()
            {
            }
        }

        /// <summary>A Decal plugin hearing messages every way Decal let it - and never unsubscribing.</summary>
        private sealed class DispatchPlugin : PluginBase
        {
            private readonly List<string> _heard;

            internal DispatchPlugin(List<string> heard)
            {
                _heard = heard;
            }

            internal bool ThrowOnServer { get; set; }

            protected override void Startup()
            {
                ServerDispatch += (_, e) =>
                {
                    _heard.Add("plugin server " + Describe(e.Message));
                    if (ThrowOnServer)
                        throw new InvalidOperationException("plugin bug");
                };
                ClientDispatch += (_, e) => _heard.Add("plugin client " + Describe(e.Message));
                Core.EchoFilter.ServerDispatch += (_, e) => _heard.Add("echo server " + Describe(e.Message));
                Core.MessageProcessed += (_, e) =>
                {
                    Assert.Equal(0, e.Data);
                    _heard.Add($"processed {Describe(e.Message)} size {e.Size}");
                };
            }

            protected override void Shutdown()
            {
            }
        }

        /// <summary>A Decal plugin hearing messages as most did: through [BaseEvent] methods, its own events and the echo filter's.</summary>
        [WireUpBaseEvents]
        private sealed class WiredDispatchPlugin : PluginBase
        {
            private readonly List<string> _heard;

            internal WiredDispatchPlugin(List<string> heard)
            {
                _heard = heard;
            }

            protected override void Startup()
            {
            }

            protected override void Shutdown()
            {
            }

            [BaseEvent("ServerDispatch")]
            private void OnServer(object sender, NetworkMessageEventArgs e) => _heard.Add("own server " + Describe(e.Message));

            [BaseEvent("ServerDispatch", "EchoFilter")]
            private void OnEchoServer(object sender, NetworkMessageEventArgs e) => _heard.Add("echo server " + Describe(e.Message));

            [BaseEvent("ClientDispatch")]
            private void OnClient(object sender, NetworkMessageEventArgs e) => _heard.Add("own client " + Describe(e.Message));
        }

        /// <summary>A Decal network filter, which hears each message before any plugin.</summary>
        private sealed class DispatchFilter : FilterBase
        {
            private readonly List<string> _heard;

            internal DispatchFilter(List<string> heard)
            {
                _heard = heard;
            }

            protected override void Startup()
            {
                ServerDispatch += (_, e) => _heard.Add("filter server " + Describe(e.Message));
                ClientDispatch += (_, e) => _heard.Add("filter client " + Describe(e.Message));
            }

            protected override void Shutdown()
            {
            }
        }
    }
}
