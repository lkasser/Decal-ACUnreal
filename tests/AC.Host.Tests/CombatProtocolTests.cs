using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Actions;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Fighting on the wire: the attacks and wields the host composes, the answers it reads,
    /// and what a death looks like.
    /// </summary>
    /// <remarks>
    /// Two kinds of evidence, kept apart. What the recorded sessions contain - a death, a
    /// health report, a stance change - is tested against the bytes as captured. What they do
    /// not yet contain, because the character recorded fights with spells, is the character
    /// swinging or shooting; those layouts are ACE's handlers' and writers' read and write
    /// order, transcribed into hex by hand here field by field, so a test still cannot inherit
    /// an assumption from the code it checks. The defender's notification, captured, matches
    /// ACE's writer for it word for word, which is why its mirror is taken from the same place.
    /// </remarks>
    public class CombatProtocolTests
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

        private static ClientActions Actions(RecordingTransport transport) => new ClientActions(transport, new ListLog(), new WorldState());

        /// <summary>Everything after the ordering sequence, which is the host's to choose.</summary>
        private static string AfterSequence(AcMessage message)
        {
            Assert.Equal(Opcodes.GameAction, message.Opcode);
            return Convert.ToHexString(message.Payload.Span.Slice(4));
        }

        private static DecodeOutcome ApplyEvent(string hex, WorldState world)
            => MessageDecoder.Apply(AcMessage.Create(Opcodes.GameEvent, Convert.FromHexString(hex)), PacketDirection.Inbound, world);

        private static DecodeOutcome ApplyInbound(uint opcode, string hex, WorldState world)
            => MessageDecoder.Apply(AcMessage.Create(opcode, Convert.FromHexString(hex)), PacketDirection.Inbound, world);

        // ------------------------------------------------------------------- what the host sends

        /// <summary>
        /// ACE's handler reads a word for the target, a word for the height and a float for the
        /// power. The Auroch Bull is a creature from a recorded session; 0x3F800000 is 1.0.
        /// </summary>
        [Fact]
        public async Task AMeleeAttackIsTheTargetTheHeightAndThePower()
        {
            RecordingTransport transport = new RecordingTransport();

            Assert.True(await Actions(transport).MeleeAttackAsync(0x80000A33, AttackHeight.Medium, 1.0f));

            Assert.Equal("08000000" + "330A0080" + "02000000" + "0000803F", AfterSequence(Assert.Single(transport.Sent)));
        }

        /// <summary>A low-power swing is how a weapon that can both thrust and slash is made to thrust.</summary>
        [Fact]
        public async Task ALowPowerSwingCarriesItsPowerAsAFloat()
        {
            RecordingTransport transport = new RecordingTransport();

            Assert.True(await Actions(transport).MeleeAttackAsync(0x80000A33, AttackHeight.High, 0.2f));

            // 0.2f is 0x3E4CCCCD.
            Assert.Equal("08000000" + "330A0080" + "01000000" + "CDCC4C3E", AfterSequence(transport.Sent[0]));
        }

        [Fact]
        public async Task AMissileAttackIsTheTargetTheHeightAndTheAccuracy()
        {
            RecordingTransport transport = new RecordingTransport();

            Assert.True(await Actions(transport).MissileAttackAsync(0x80000A33, AttackHeight.Low, 0.5f));

            Assert.Equal("0A000000" + "330A0080" + "03000000" + "0000003F", AfterSequence(transport.Sent[0]));
        }

        [Fact]
        public async Task CancellingAnAttackCarriesNothingButItsType()
        {
            RecordingTransport transport = new RecordingTransport();

            Assert.True(await Actions(transport).CancelAttackAsync());

            Assert.Equal("B7010000", AfterSequence(transport.Sent[0]));
        }

        [Fact]
        public async Task AnUntargetedCastIsTheSpellAlone()
        {
            RecordingTransport transport = new RecordingTransport();

            Assert.True(await Actions(transport).CastUntargetedAsync(5367));

            Assert.Equal("48000000" + "F7140000", AfterSequence(transport.Sent[0]));
        }

        /// <summary>
        /// The item, then the slot. The Academy Wand is one the recorded character carried, and
        /// the slot is Held - where that session showed its wand wielded.
        /// </summary>
        [Fact]
        public async Task AWieldIsTheItemAndTheSlot()
        {
            RecordingTransport transport = new RecordingTransport();

            Assert.True(await Actions(transport).WieldAsync(0x80000F73, EquipMasks.Held));

            Assert.Equal("1A000000" + "730F0080" + "00000001", AfterSequence(transport.Sent[0]));
        }

        [Fact]
        public async Task WithoutATransportThatCanSendNothingIsComposed()
        {
            IGameActions actions = new ClientActions(new CaptureTransport(Array.Empty<AC.Proxy.CapturedDatagram>()), new ListLog());

            Assert.False(await actions.MeleeAttackAsync(1, AttackHeight.Medium, 1));
            Assert.False(await actions.WieldAsync(1, EquipMasks.MeleeWeapon));
        }

        // ------------------------------------------------------------------- the answers, per ACE's writers

        /// <summary>
        /// AttackDone is the error and nothing else. ACE ends every attack sequence with
        /// ActionCancelled, and sends zero between the swings of one it is repeating.
        /// </summary>
        [Fact]
        public void AnAttackThatEndsSaysSoWithItsError()
        {
            WorldState world = new WorldState();
            List<uint> finished = new List<uint>();
            world.AttackFinished += (_, e) => finished.Add(e);

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("06000050" + "4C000000" + "A7010000" + "00000000", world));
            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("06000050" + "4D000000" + "A7010000" + "36000000", world));

            Assert.Equal(new[] { 0u, WeenieErrors.ActionCancelled }, finished);
        }

        /// <summary>
        /// The defender's name, padded to a word; the damage type (4, bludgeoning); the share of
        /// its health as a double (0.25); the amount (17); the critical word; then eight bytes of
        /// attack conditions.
        /// </summary>
        private const string BlowLandedHex =
            "06000050" + "4A000000" + "B1010000"
            + "0B00" + "4175726F63682042756C6C" + "000000"
            + "04000000" + "000000000000D03F" + "11000000" + "01000000" + "0000000000000000";

        [Fact]
        public void ABlowTheCharacterLandsNamesWhatItHitAndHowHard()
        {
            WorldState world = new WorldState();
            DamageDealt dealt = null;
            world.DamageDealt += (_, d) => dealt = d;

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(BlowLandedHex, world));

            Assert.Equal("Auroch Bull", dealt.Defender);
            Assert.Equal(DamageTypes.Bludgeon, dealt.DamageType);
            Assert.Equal(0.25, dealt.Percentage, 6);
            Assert.Equal(17u, dealt.Amount);
            Assert.True(dealt.IsCritical);
            Assert.Equal("hit Auroch Bull for 17 bludgeoning (25.0%) - critical", dealt.ToString());
        }

        [Fact]
        public void ABlowCutShortIsMalformedRatherThanHalfRead()
        {
            WorldState world = new WorldState();
            DamageDealt dealt = null;
            world.DamageDealt += (_, d) => dealt = d;

            // Cut inside the attack conditions.
            Assert.Equal(DecodeOutcome.Malformed, ApplyEvent(BlowLandedHex.Substring(0, BlowLandedHex.Length - 8), world));
            Assert.Null(dealt);
        }

        [Fact]
        public void ATargetThatEvadesIsNamed()
        {
            WorldState world = new WorldState();
            string evaded = null;
            world.TargetEvaded += (_, name) => evaded = name;

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("06000050" + "4B000000" + "B3010000" + "0B00" + "4175726F63682042756C6C" + "000000", world));

            Assert.Equal("Auroch Bull", evaded);
        }

        [Fact]
        public void TheNextSwingOfARepeatingAttackCarriesNothing()
        {
            WorldState world = new WorldState();
            int commenced = 0;
            world.AttackCommenced += (_, _) => commenced++;

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent("06000050" + "4E000000" + "B8010000", world));

            Assert.Equal(1, commenced);
        }

        // ------------------------------------------------------------------- a death, as captured

        // An Auroch Yearling the character killed, in the order the server sent it: its health
        // reported as nothing, the death message, its dying motion three messages later, and
        // its removal a hundred and eighteen messages after that, as the corpse appeared.
        private const string YearlingHealthGoneHex = "0600005047000000C00100003A0A008000000000";

        private const string YearlingSlainHex =
            "0600005048000000AD0100004D00596F7520736C6179204175726F636820596561726C696E6720766963696F75736C7920656E6F75676820746F20696D70617274206465617468207365766572616C2074696D6573206F7665722100";

        private const string YearlingDiesHex = "3A0A0080000006000600000000003D00030000003D001100";

        private const string YearlingRemovedHex = "3A0A008000000000";

        /// <summary>The same Yearling earlier in the session, standing about alive: forward command 3.</summary>
        private const string YearlingStandingHex = "3A0A0080000002000200000000003D00030000003D000300";

        [Fact]
        public void ACreatureThatDiesHoldsTheDeadCommand()
        {
            WorldState world = new WorldState();

            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.Motion, YearlingStandingHex, world));
            Assert.False(world.Get(0x80000A3A).Movement.IsDead);

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(YearlingHealthGoneHex, world));
            Assert.Equal(0f, world.Get(0x80000A3A).HealthFraction);

            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.Motion, YearlingDiesHex, world));
            Assert.True(world.Get(0x80000A3A).Movement.IsDead);
            Assert.Equal(MovementState.DeadCommand, world.Get(0x80000A3A).Movement.ForwardCommand);

            Assert.Equal(DecodeOutcome.Applied, ApplyInbound(Opcodes.ObjectDelete, YearlingRemovedHex, world));
            Assert.Null(world.Get(0x80000A3A));
        }

        /// <summary>
        /// The death message comes as the server wrote it and names the creature only in prose,
        /// so it stays chat; the id comes from the motion and the removal.
        /// </summary>
        [Fact]
        public void TheDeathMessageIsChat()
        {
            WorldState world = new WorldState();
            ChatMessage said = null;
            world.ChatReceived += (_, c) => said = c;

            Assert.Equal(DecodeOutcome.Applied, ApplyEvent(YearlingSlainHex, world));

            Assert.Equal(ChatKind.Combat, said.Kind);
            Assert.Equal("You slay Auroch Yearling viciously enough to impart death several times over!", said.Text);
        }
    }
}
