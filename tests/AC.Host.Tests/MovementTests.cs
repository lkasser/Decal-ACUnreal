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
    /// Composing movement. The test that matters is the round trip against bytes the
    /// client itself sent: decode one of its walk messages, ask for the same walk, and
    /// require the result to be identical. Anything wrong in the layout - a field in the
    /// wrong order, a float where an int belongs, a sequence invented instead of echoed
    /// - shows up as a byte difference rather than as a plausible-looking message the
    /// server will quietly discard.
    /// </summary>
    public class MovementTests
    {
        /// <summary>A walk-forward MoveToState, captured from AC:Unreal.</summary>
        private const string WalkingHex =
            "170000001CF600001F000000020000003D00008005000045010000000000803F3000112B001FFE42E09A2A431D0040421FD0003F00000000000000002C3B5DBFBC0000000000000001000000";

        private sealed class RecordingTransport : IGameTransport
        {
            public List<AcMessage> Sent { get; } = new List<AcMessage>();

            public string Description => "recording";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public bool CanSend => true;

            public bool CanShowInGame => true;

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default)
            {
                Shown.Add(message);
                return Task.CompletedTask;
            }

            public List<AcMessage> Shown { get; } = new List<AcMessage>();

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default)
            {
                Sent.Add(message);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                // Silences the unused-event warnings without pretending to raise them.
                MessageReceived = null;
                Ended = null;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class SilentLog : IPluginLog
        {
            public List<string> Lines { get; } = new List<string>();

            public void Info(string message) => Lines.Add("INFO " + message);

            public void Warn(string message) => Lines.Add("WARN " + message);

            public void Error(string message, Exception exception = null) => Lines.Add("ERROR " + message);
        }

        private static WorldState WorldAfterWalking()
        {
            WorldState world = new WorldState();

            DecodeOutcome outcome = MessageDecoder.Apply(
                AcMessage.Create(Opcodes.GameAction, Convert.FromHexString(WalkingHex)),
                PacketDirection.Outbound,
                world);

            Assert.Equal(DecodeOutcome.Applied, outcome);
            return world;
        }

        [Fact]
        public async Task AComposedWalkIsByteIdenticalToTheClientsOwn()
        {
            WorldState world = WorldAfterWalking();
            RecordingTransport transport = new RecordingTransport();
            ClientActions actions = new ClientActions(transport, new SilentLog(), world);

            Assert.True(await actions.WalkForwardAsync());

            AcMessage sent = Assert.Single(transport.Sent);
            Assert.Equal(Opcodes.GameAction, sent.Opcode);

            byte[] client = Convert.FromHexString(WalkingHex);

            // Everything but the leading ordering sequence, which is ours to choose.
            Assert.Equal(client.Length, sent.Payload.Length);
            Assert.Equal(
                Convert.ToHexString(client.AsSpan(4)),
                Convert.ToHexString(sent.Payload.Span.Slice(4)));
        }

        [Fact]
        public async Task AComposedWalkCarriesASequenceAboveAnythingTheClientHasUsed()
        {
            WorldState world = WorldAfterWalking();
            RecordingTransport transport = new RecordingTransport();
            ClientActions actions = new ClientActions(transport, new SilentLog(), world);

            // The captured message used 0x17.
            Assert.Equal(0x17u, world.Character.LastActionSequence);

            Assert.True(await actions.WalkForwardAsync());

            SpanReader reader = new SpanReader(transport.Sent[0].Payload.Span);
            Assert.True(reader.TryReadUInt32(out uint sequence));
            Assert.True(sequence > 0x17u);
        }

        [Fact]
        public async Task StoppingSendsTheStandingStateAndNoMovementFields()
        {
            WorldState world = WorldAfterWalking();
            RecordingTransport transport = new RecordingTransport();
            ClientActions actions = new ClientActions(transport, new SilentLog(), world);

            Assert.True(await actions.StopAsync());

            // Read it back the way the server would.
            WorldState readBack = new WorldState();
            Assert.Equal(
                DecodeOutcome.Applied,
                MessageDecoder.Apply(transport.Sent[0], PacketDirection.Outbound, readBack));

            ClientMotionState motion = readBack.Character.Motion;
            Assert.False(motion.IsMoving);
            Assert.Equal(MotionCommands.Ready, motion.CurrentStyle);

            // The position must survive a stop: the character has not moved.
            Assert.Equal(
                world.Character.Location.Value.LandblockCell,
                readBack.Character.Location.Value.LandblockCell);
        }

        [Fact]
        public async Task TurningLeftAndRightUseDifferentCommands()
        {
            WorldState world = WorldAfterWalking();
            RecordingTransport transport = new RecordingTransport();
            ClientActions actions = new ClientActions(transport, new SilentLog(), world);

            await actions.TurnAsync(1.0f);
            await actions.TurnAsync(-0.5f);

            WorldState right = new WorldState();
            MessageDecoder.Apply(transport.Sent[0], PacketDirection.Outbound, right);
            Assert.Equal(MotionCommands.TurnRight, right.Character.Motion.TurnCommand);
            Assert.Equal(1.0f, right.Character.Motion.TurnSpeed);

            WorldState left = new WorldState();
            MessageDecoder.Apply(transport.Sent[1], PacketDirection.Outbound, left);
            Assert.Equal(MotionCommands.TurnLeft, left.Character.Motion.TurnCommand);

            // The sign chose the command, so the speed itself is never negative.
            Assert.Equal(0.5f, left.Character.Motion.TurnSpeed);
        }

        /// <summary>
        /// A cast the client sent: Nether Arc VII at 0x80002B2F. The spell id resolves to
        /// a real spell in the client's own data, and the capture contains the Nether Arc
        /// projectiles that followed, so the second word really is a spell.
        /// </summary>
        private const string CastHex = "160400004A0000002F2B0080F7140000";

        [Fact]
        public async Task AComposedCastIsByteIdenticalToTheClientsOwn()
        {
            RecordingTransport transport = new RecordingTransport();
            ClientActions actions = new ClientActions(transport, new SilentLog(), new WorldState());

            Assert.True(await actions.CastAsync(0x80002B2F, 5367));

            byte[] client = Convert.FromHexString(CastHex);
            Assert.Equal(
                Convert.ToHexString(client.AsSpan(4)),
                Convert.ToHexString(transport.Sent[0].Payload.Span.Slice(4)));
        }

        [Fact]
        public async Task ChangingStanceSendsTheModeTheServerNames()
        {
            RecordingTransport transport = new RecordingTransport();
            ClientActions actions = new ClientActions(transport, new SilentLog(), new WorldState());

            Assert.True(await actions.SetCombatModeAsync(CombatMode.Magic));

            // The client's own was action 0x0053 carrying 0x00000008.
            SpanReader reader = new SpanReader(transport.Sent[0].Payload.Span);
            Assert.True(reader.TryReadUInt32(out _));                   // ordering sequence
            Assert.True(reader.TryReadUInt32(out uint actionType));
            Assert.True(reader.TryReadUInt32(out uint mode));

            Assert.Equal(GameActions.ChangeCombatMode, actionType);
            Assert.Equal(0x08u, mode);
        }

        [Fact]
        public async Task ACastNeedsNoPositionAndSoWorksBeforeTheClientHasMoved()
        {
            RecordingTransport transport = new RecordingTransport();
            ClientActions actions = new ClientActions(transport, new SilentLog(), new WorldState());

            // Movement is refused without a position; casting must not be.
            Assert.False(await actions.WalkForwardAsync());
            Assert.True(await actions.CastAsync(0x50000006, 5401));
            Assert.True(await actions.QueryHealthAsync(0x80001471));
        }

        [Fact]
        public async Task MovingBeforeTheClientHasReportedAPositionIsRefusedNotGuessed()
        {
            RecordingTransport transport = new RecordingTransport();
            SilentLog log = new SilentLog();
            ClientActions actions = new ClientActions(transport, log, new WorldState());

            Assert.False(await actions.WalkForwardAsync());

            Assert.Empty(transport.Sent);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN") && l.Contains("has not reported a position"));
        }

        [Fact]
        public async Task WithoutAWorldMovementIsRefusedRatherThanThrowing()
        {
            RecordingTransport transport = new RecordingTransport();
            ClientActions actions = new ClientActions(transport, new SilentLog());

            Assert.False(await actions.StopAsync());
            Assert.Empty(transport.Sent);
        }
    }
}
