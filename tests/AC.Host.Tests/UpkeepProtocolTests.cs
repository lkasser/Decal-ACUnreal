using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Actions;
using AC.Host.Decoding;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// The upkeep on the wire: merging stacks, and what the server says about a fellowship.
    /// </summary>
    /// <remarks>
    /// No recorded session has the player merging stacks or in a fellowship, so every layout here
    /// is ACE's - its handler's read order for the merge and the panel request, its writers' write
    /// order for the fellowship events - transcribed into hex by hand, field by field, so a test
    /// cannot inherit an assumption from the code it checks. The ids and stack sizes are from a
    /// recorded session: two stacks of Encapsulated Spirit the character carried, 50 and 6 of 50,
    /// and the character itself, 0x50000006.
    /// </remarks>
    public class UpkeepProtocolTests
    {
        private sealed class RecordingTransport : IGameTransport
        {
            public List<AcMessage> Sent { get; } = new List<AcMessage>();

            public string Description => "recording";

#pragma warning disable CS0067 // never raised: nothing arrives on a transport that only records
            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;
#pragma warning restore CS0067

            public bool CanSend => true;

            public bool CanShowInGame => false;

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default)
            {
                Sent.Add(message);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private static string AfterSequence(AcMessage message)
        {
            Assert.Equal(Opcodes.GameAction, message.Opcode);
            return Convert.ToHexString(message.Payload.Span.Slice(4));
        }

        private static DecodeOutcome ApplyEvent(string hex, WorldState world)
            => MessageDecoder.Apply(AcMessage.Create(Opcodes.GameEvent, Convert.FromHexString(hex)), PacketDirection.Inbound, world);

        private static WorldState World()
        {
            WorldState world = new WorldState();
            MessageDecoder.Apply(AcMessage.Create(Opcodes.PlayerCreate, Convert.FromHexString("06000050")), PacketDirection.Inbound, world);
            return world;
        }

        // ------------------------------------------------------------------- merging stacks

        /// <summary>ACE's GameActionStackableMerge reads the source, the target, then the amount.</summary>
        [Fact]
        public async Task AMergeIsTheSourceTheTargetAndTheAmount()
        {
            RecordingTransport transport = new RecordingTransport();

            Assert.True(await new ClientActions(transport, new ListLog(), new WorldState()).StackableMergeAsync(0x800111CE, 0x8001F7BC, 44));

            Assert.Equal("54000000" + "CE110180" + "BCF70180" + "2C000000", AfterSequence(Assert.Single(transport.Sent)));
        }

        /// <summary>
        /// ACE's GameActionCreateTinkeringTool reads the tool, a count, then that many items. The
        /// Ust is the one the recorded character carried.
        /// </summary>
        [Fact]
        public async Task ASalvageIsTheToolACountAndEachItem()
        {
            RecordingTransport transport = new RecordingTransport();

            Assert.True(await new ClientActions(transport, new ListLog(), new WorldState()).SalvageAsync(0x80000D80, new uint[] { 0x80007100, 0x80007101 }));

            Assert.Equal("7D020000" + "800D0080" + "02000000" + "00710080" + "01710080", AfterSequence(Assert.Single(transport.Sent)));
        }

        /// <summary>ACE's GameActionGiveObjectRequest reads the target, the item, then the amount.</summary>
        [Fact]
        public async Task AGiftIsTheTargetTheItemAndTheAmount()
        {
            RecordingTransport transport = new RecordingTransport();

            Assert.True(await new ClientActions(transport, new ListLog(), new WorldState()).GiveAsync(0x800111CE, 0x7A1C3B00, 3));

            Assert.Equal("CD000000" + "003B1C7A" + "CE110180" + "03000000", AfterSequence(Assert.Single(transport.Sent)));
        }

        [Fact]
        public async Task WithoutATransportThatCanSendNoMergeIsComposed()
        {
            ClientActions actions = new ClientActions(new CaptureTransport(Array.Empty<AC.Proxy.CapturedDatagram>()), new ListLog());

            Assert.False(await actions.StackableMergeAsync(1, 2, 3));
        }

        // ------------------------------------------------------------------- the fellowship

        /// <summary>
        /// Testchar I (level 275; 400/400 health, 500/500 stamina, 300/300 mana) and Helper (level
        /// 100; 90 of 300 health, 200/200 stamina, 10 of 150 mana), then the name "Fellows", the
        /// leader, share experience, even share, open, locked, and two empty tables.
        /// </summary>
        private const string FullUpdateHex =
            "06000050" + "50000000" + "BE020000"
            + "0200" + "1000"
            + "06000050" + "00000000" + "00000000" + "13010000"
            + "90010000" + "F4010000" + "2C010000" + "90010000" + "F4010000" + "2C010000"
            + "10000000" + "0A00" + "54657374636861722049"
            + "07000050" + "00000000" + "00000000" + "64000000"
            + "2C010000" + "C8000000" + "96000000" + "5A000000" + "C8000000" + "0A000000"
            + "10000000" + "0600" + "48656C706572"
            + "0700" + "46656C6C6F7773" + "000000"
            + "06000050" + "01000000" + "01000000" + "00000000" + "00000000"
            + "00001000" + "00001000";

        [Fact]
        public void AFullUpdateDescribesEveryMember()
        {
            WorldState world = World();

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(FullUpdateHex, world));

            IFellowshipView fellowship = world.Character.Fellowship;
            Assert.True(fellowship.IsMember);
            Assert.Equal("Fellows", fellowship.Name);
            Assert.Equal(0x50000006u, fellowship.LeaderId);
            Assert.Equal(2, fellowship.Members.Count);

            Fellow helper = fellowship.Members[0x50000007];
            Assert.Equal("Helper", helper.Name);
            Assert.Equal(100u, helper.Level);
            Assert.Equal((300u, 200u, 150u), (helper.MaxHealth, helper.MaxStamina, helper.MaxMana));
            Assert.Equal((90u, 200u, 10u), (helper.CurrentHealth, helper.CurrentStamina, helper.CurrentMana));
            Assert.Equal("Testchar I", fellowship.Members[0x50000006].Name);

            // ACE's share-loot word, 0x10: anything but zero shares, as Virindi Tank read it.
            Assert.True(helper.ShareLoot);
        }

        /// <summary>
        /// A fellow's vitals as they change: the same fields as one member of the full update, then
        /// the kind of update (3, vitals) - here Helper healed to 250 of 300.
        /// </summary>
        [Fact]
        public void AVitalsUpdateReplacesThatFellowsFigures()
        {
            WorldState world = World();
            ApplyEvent(FullUpdateHex, world);

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(
                "06000050" + "51000000" + "C0020000"
                + "07000050" + "00000000" + "00000000" + "64000000"
                + "2C010000" + "C8000000" + "96000000" + "FA000000" + "C8000000" + "0A000000"
                + "00000000" + "0600" + "48656C706572" + "03000000", world));

            Assert.Equal(250u, world.Character.Fellowship.Members[0x50000007].CurrentHealth);
            Assert.Equal(2, world.Character.Fellowship.Members.Count);
        }

        [Fact]
        public void AFellowWhoQuitsIsGoneAndTheCharacterQuittingEndsIt()
        {
            WorldState world = World();
            ApplyEvent(FullUpdateHex, world);

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("06000050" + "52000000" + "A3000000" + "07000050", world));
            Assert.False(world.Character.Fellowship.Members.ContainsKey(0x50000007));
            Assert.True(world.Character.Fellowship.IsMember);

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("06000050" + "53000000" + "A3000000" + "06000050", world));
            Assert.False(world.Character.Fellowship.IsMember);
            Assert.Empty(world.Character.Fellowship.Members);
        }

        [Fact]
        public void ADismissalAndADisbandingAreHeard()
        {
            WorldState world = World();
            ApplyEvent(FullUpdateHex, world);

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("06000050" + "54000000" + "A4000000" + "07000050", world));
            Assert.False(world.Character.Fellowship.Members.ContainsKey(0x50000007));

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("06000050" + "55000000" + "BF020000", world));
            Assert.False(world.Character.Fellowship.IsMember);
        }

        [Fact]
        public void AFullUpdateCutShortIsMalformed()
        {
            WorldState world = World();

            Assert.Equal(DecodeOutcome.Malformed, ApplyEvent(FullUpdateHex.Substring(0, 120), world));
            Assert.False(world.Character.Fellowship.IsMember);
        }

        /// <summary>
        /// The client's word that its fellowship panel opened or closed: one word, as ACE's
        /// GameActionFellowshipUpdateRequest reads it.
        /// </summary>
        [Fact]
        public void TheClientOpeningItsFellowshipPanelIsNoted()
        {
            WorldState world = World();

            MessageDecoder.Apply(AcMessage.Create(Opcodes.GameAction, Convert.FromHexString("10000000" + "A6000000" + "01000000")), PacketDirection.Outbound, world);
            Assert.True(world.Character.Fellowship.PanelOpen);

            MessageDecoder.Apply(AcMessage.Create(Opcodes.GameAction, Convert.FromHexString("11000000" + "A6000000" + "00000000")), PacketDirection.Outbound, world);
            Assert.False(world.Character.Fellowship.PanelOpen);
        }
    }
}
