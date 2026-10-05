using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AC.Dat;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// The client letting go of what it can no longer see. ACE sends nothing when an object goes
    /// out of view: it stops counting it as seen, forgets it 25 seconds later, and creates it afresh
    /// if it comes back - the client's own rule, which its physics was taken from. The host lets go
    /// the same way and says so as a deletion would, so plugins do not chase what the client no
    /// longer has.
    /// </summary>
    /// <remarks>
    /// The captured bytes are session-20260929-1118.acap's: Tunlok Weapons Master and the Lugian
    /// Mace in his hand, created at 41.3 s on the landblock 0x2B12, and the client's MoveToState
    /// reports at 1095.5 s and 1095.6 s, which carry the character from 0x2C11 into 0x2C10 - two
    /// landblocks south of him. Replayed whole on the capture's clock, the host lets go of him 25
    /// seconds later, at 1120.6 s, with 46 others - everything of 0x2B12, and what its creatures held
    /// - and when the character came back, at 1240.2 s, ACE created him again with these same bytes,
    /// the mace with him. In that capture and session2.acap the host let go of 164 objects, ACE
    /// created 76 of them again as the character came back, and never once spoke of one between the
    /// host letting it go and creating it again; nor did it create again anything the host still held.
    /// </remarks>
    public class VisibilityTests
    {
        private const uint Player = 0x50000006;
        private const uint Tunlok = 0x72B12012, Mace = 0x80000923;

        /// <summary>Captured: ObjectCreate for Tunlok Weapons Master, standing in 0x2B120019.</summary>
        private const string CapturedTunlok =
            "1220B17211010A09C610C810000000E81BE71B00EF1BEE1B02E21BE11B05E21BE11B07EB1BDD1B07DE1BDD1B09E21BE11B0CE21BE11B13F81BF71B14F81BF71B"
            + "00232102FE2005FE2007FB200925210C25210E3E2113282114292100C3880100180420000C00000000003D00030000003D000300000000001900122BF087B942"
            + "023C8B41DEE291429FAFF9BE0000000000000000D47E5FBF060000090A0000200B0A00020100000023090080010000009A99993F00000000000000000000000000"
            + "0000000000000036008000150054756E6C6F6B20576561706F6E73204D617374657200146037101000000004020000FFFF20000000000040400400";

        /// <summary>Captured: ObjectCreate for the Lugian Mace in his hand, which names him as its parent.</summary>
        private const string CapturedMace =
            "2309008011000000A11802001404000001000000140000202B0000343B0100021220B17201000000000000400000000000000000000000000000000000000000"
            + "188223100B004C756769616E204D616365000000CD5CC4100100000012000000F401000001000000011220B172000010000000100050140200000000";

        /// <summary>Captured: the client's MoveToState from 0x2C110009, a landblock from Tunlok's either way.</summary>
        private const string CapturedMoveIn2C11 =
            "BE0700001CF600001F070000020000003D00008005000045010000000000803F0D000065010000000000803F0900112C00A9244200405D3F060160425C9586BE"
            + "000000000000000064FF76BFC00000000000000001000000";

        /// <summary>Captured: the next, from 0x2C100010 - two landblocks south of him.</summary>
        private const string CapturedMoveInto2C10 =
            "BF0700001CF600001F000000020000003D00008005000045010000000000803F1000102C0088204280C03F4306016042FDB8E8BE0000000000000000C30664BF"
            + "C00000000000000001000000";

        private sealed class Clock
        {
            public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        }

        /// <summary>A world with the character in it, on <paramref name="clock"/>'s time.</summary>
        private static WorldState InTheWorld(Clock clock, ICellData cells = null)
        {
            WorldState world = new WorldState(() => clock.Now) { Cells = cells };
            WorldObject me = world.GetOrAdd(Player, out _);
            me.Name = "Testchar I";
            world.SetPlayerId(Player);
            return world;
        }

        private static DecodeOutcome Inbound(WorldState world, uint opcode, string hex)
            => MessageDecoder.Apply(AcMessage.Create(opcode, Convert.FromHexString(hex)), PacketDirection.Inbound, world);

        private static DecodeOutcome Outbound(WorldState world, string hex)
            => MessageDecoder.Apply(AcMessage.Create(Opcodes.GameAction, Convert.FromHexString(hex)), PacketDirection.Outbound, world);

        /// <summary>Puts the character in a cell, as its client's report of where it is would.</summary>
        private static void StandIn(WorldState world, uint cell)
            => world.SetClientPosition(new Location(cell, 96, 96, 0, 1, 0, 0, 0), default);

        /// <summary>An object standing in a cell, announced as a decoder would.</summary>
        private static WorldObject Place(WorldState world, uint id, string name, uint cell, Action<WorldObject> fill = null)
        {
            WorldObject obj = world.GetOrAdd(id, out _);
            obj.Name = name;
            obj.Location = new Location(cell, 50, 50, 0, 1, 0, 0, 0);
            fill?.Invoke(obj);
            world.NotifyCreated(obj);
            return obj;
        }

        /// <summary>Lets time pass and the host's tick come round.</summary>
        private static void Wait(WorldState world, Clock clock, double seconds)
        {
            clock.Now += TimeSpan.FromSeconds(seconds);
            world.ForgetOutOfView();
        }

        /// <summary>Indoor cells as a test describes them.</summary>
        private sealed class Cells : ICellData
        {
            private readonly Dictionary<uint, EnvCellInfo> _cells = new Dictionary<uint, EnvCellInfo>();

            public Cells Room(uint id, bool seenOutside, params ushort[] visible)
            {
                _cells[id] = new EnvCellInfo(id, seenOutside, visible);
                return this;
            }

            public EnvCellInfo GetEnvCell(uint cellId) => _cells.TryGetValue(cellId, out EnvCellInfo cell) ? cell : null;
        }

        // ------------------------------------------------------------------- walking away

        [Fact]
        public void WalkingTwoLandblocksAwayLetsGoOfWhatCanNoLongerBeSeenTwentyFiveSecondsLater()
        {
            Clock clock = new Clock();
            WorldState world = InTheWorld(clock);
            List<uint> removed = new List<uint>();
            world.ObjectRemoved += (_, id) => removed.Add(id);

            Assert.Equal(DecodeOutcome.Applied, Outbound(world, CapturedMoveIn2C11));
            Assert.Equal(0x2C110009u, world.ViewerCell);
            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.ObjectCreate, CapturedMace));
            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.ObjectCreate, CapturedTunlok));
            Assert.Equal(0x2B120019u, world.Get(Tunlok).Location.Value.LandblockCell);
            Assert.Equal(Tunlok, world.Get(Mace).ParentId);
            Assert.Empty(world.OutOfView);

            // Into 0x2C10: two landblocks south of him. Only he has a place of his own; the mace
            // goes with him.
            Assert.Equal(DecodeOutcome.Applied, Outbound(world, CapturedMoveInto2C10));
            Assert.Equal(0x2C100010u, world.ViewerCell);
            Assert.Equal(new[] { Tunlok }, world.OutOfView.Keys);

            Wait(world, clock, 24.9);
            Assert.Empty(removed);
            Assert.NotNull(world.Get(Tunlok));

            Wait(world, clock, 0.1);
            Assert.Equal(new[] { Tunlok, Mace }, removed);
            Assert.Null(world.Get(Tunlok));
            Assert.Null(world.Get(Mace));
            Assert.Empty(world.OutOfView);

            // Back, and ACE creates him afresh, with the same bytes.
            Outbound(world, CapturedMoveIn2C11);
            Inbound(world, Opcodes.ObjectCreate, CapturedTunlok);
            Inbound(world, Opcodes.ObjectCreate, CapturedMace);
            Assert.Equal("Tunlok Weapons Master", world.Get(Tunlok).Name);
            Assert.Empty(world.OutOfView);
        }

        /// <summary>
        /// The 25 seconds are the client's grace: something that steps out of view and back within
        /// them is still there, and ACE, which kept it as long, does not create it again.
        /// </summary>
        [Fact]
        public void WhatComesBackIntoViewInTimeIsKeptAndItsTimeStartsAgainWhenItNextGoes()
        {
            Clock clock = new Clock();
            WorldState world = InTheWorld(clock);
            StandIn(world, 0x2B110028);
            Place(world, 0x80000100, "Drudge Skulker", 0x2B120019);

            StandIn(world, 0x2B100030);
            Wait(world, clock, 20);
            StandIn(world, 0x2B110028);
            Wait(world, clock, 10);
            Assert.NotNull(world.Get(0x80000100));

            StandIn(world, 0x2B100030);
            Wait(world, clock, 24);
            Assert.NotNull(world.Get(0x80000100));
            Wait(world, clock, 1);
            Assert.Null(world.Get(0x80000100));
        }

        [Fact]
        public void OutdoorsTheCharactersLandblockAndTheEightAroundItAreInView()
        {
            WorldState world = new WorldState();

            Assert.True(world.CanSee(0x2B110028, 0x2B110001));
            Assert.True(world.CanSee(0x2B110028, 0x2A100040));
            Assert.True(world.CanSee(0x2B110028, 0x2C120001));
            Assert.False(world.CanSee(0x2B110028, 0x2D110001));
            Assert.False(world.CanSee(0x2B110028, 0x2B130001));
            Assert.False(world.CanSee(0x2B110028, 0x29130001));
        }

        /// <summary>An object walking away out of view goes as well; one that walks back before its time is up stays.</summary>
        [Fact]
        public void ACreatureThatWalksOutOfViewGoesAndOneThatWalksBackStays()
        {
            Clock clock = new Clock();
            WorldState world = InTheWorld(clock);
            StandIn(world, 0x2B110028);
            WorldObject leaving = Place(world, 0x80000100, "Drudge Skulker", 0x2B120019);
            WorldObject returning = Place(world, 0x80000101, "Mosswart", 0x2B120019);

            foreach (WorldObject walker in new[] { leaving, returning })
            {
                walker.Location = new Location(0x2B130019, 50, 50, 0, 1, 0, 0, 0);
                world.NotifyUpdated(walker);
            }

            Wait(world, clock, 20);
            returning.Location = new Location(0x2B120019, 50, 50, 0, 1, 0, 0, 0);
            world.NotifyUpdated(returning);
            Wait(world, clock, 10);

            Assert.Null(world.Get(0x80000100));
            Assert.NotNull(world.Get(0x80000101));
        }

        // ------------------------------------------------------------------- what is never let go of

        /// <summary>
        /// The character, its packs and what is in them, what it wears, and what lies in a container
        /// have no place of their own on the landscape, and stay wherever the character goes; a chest
        /// left behind goes.
        /// </summary>
        [Fact]
        public void TheCharacterAndEverythingItCarriesOrWearsStayWhereverItGoes()
        {
            Clock clock = new Clock();
            WorldState world = InTheWorld(clock);
            StandIn(world, 0x2B110028);
            Place(world, 0x80000200, "Pack", 0x2B110028, o => { o.Location = null; o.ContainerId = Player; });
            Place(world, 0x80000201, "Salvage", 0x2B110028, o => { o.Location = null; o.ContainerId = 0x80000200; });
            Place(world, 0x80000202, "Wand", 0x2B110028, o => { o.Location = null; o.WielderId = Player; o.CurrentlyWieldedLocation = 0x01000000; });
            Place(world, 0x80000300, "Chest", 0x2B110028);
            Place(world, 0x80000301, "Pyreal", 0x2B110028, o => { o.Location = null; o.ContainerId = 0x80000300; });

            world.Get(Player).Location = new Location(0x01D90100, 0, -50, 0, 1, 0, 0, 0);
            world.NotifyUpdated(world.Get(Player));
            Wait(world, clock, 60);

            Assert.NotNull(world.Get(Player));
            Assert.Equal(Player, world.Character.Object.Id);
            Assert.NotNull(world.Get(0x80000200));
            Assert.NotNull(world.Get(0x80000201));
            Assert.NotNull(world.Get(0x80000202));
            Assert.NotNull(world.Get(0x80000301));
            Assert.Null(world.Get(0x80000300));
        }

        // ------------------------------------------------------------------- portals

        /// <summary>
        /// A teleport: the server says where the character is going right after PlayerTeleport, before
        /// the client has arrived, and what cannot be seen from there goes 25 seconds later - what is
        /// at the destination stays.
        /// </summary>
        [Fact]
        public void ATeleportLetsGoOfEverythingNotAtTheDestination()
        {
            Clock clock = new Clock();
            WorldState world = InTheWorld(clock);
            List<uint> removed = new List<uint>();
            world.ObjectRemoved += (_, id) => removed.Add(id);
            StandIn(world, 0x2B110028);
            Place(world, 0x80000100, "Drudge Skulker", 0x2B110030);
            Place(world, 0x72B11000, "Life Stone", 0x2B11012E);

            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.PlayerTeleport, "01000000"));
            WireWriter there = new WireWriter(Opcodes.PrivateUpdatePosition).U8(1).U32(1).Location(0xA9B40019, 84f, 7.4f, 94f);
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(there.ToMessage(), PacketDirection.Inbound, world));
            Assert.Equal(0xA9B40019u, world.ViewerCell);
            Place(world, 0x80000400, "Banderling", 0xA9B40021);

            Wait(world, clock, 25);
            removed.Sort();
            Assert.Equal(new uint[] { 0x72B11000, 0x80000100 }, removed);
            Assert.NotNull(world.Get(0x80000400));
        }

        // ------------------------------------------------------------------- indoors

        /// <summary>
        /// In a dungeon, only the cells the character's own cell lists as visible are in view - none
        /// of them outside, and nothing in another landblock.
        /// </summary>
        [Fact]
        public void InADungeonOnlyTheCellsTheCharactersCellSeesAreInView()
        {
            Clock clock = new Clock();
            Cells cells = new Cells().Room(0x01D90100, seenOutside: false, 0x0101, 0x0115);
            WorldState world = InTheWorld(clock, cells);
            StandIn(world, 0x01D90100);
            Place(world, 0x80000100, "Here", 0x01D90100);
            Place(world, 0x80000101, "Through the door", 0x01D90101);
            Place(world, 0x80000102, "Round the corner", 0x01D90130);
            Place(world, 0x80000103, "Next landblock", 0x01DA0100);

            Wait(world, clock, 25);

            Assert.NotNull(world.Get(0x80000100));
            Assert.NotNull(world.Get(0x80000101));
            Assert.Null(world.Get(0x80000102));
            Assert.Null(world.Get(0x80000103));
        }

        /// <summary>From a building's room, which sees outside, the landblocks around are in view as from outdoors.</summary>
        [Fact]
        public void FromARoomThatSeesOutsideTheLandblocksAroundAreInView()
        {
            Cells cells = new Cells().Room(0x2B12012E, seenOutside: true, 0x012F, 0x0131);
            WorldState world = new WorldState { Cells = cells };

            Assert.True(world.CanSee(0x2B12012E, 0x2B120131));
            Assert.True(world.CanSee(0x2B12012E, 0x2A110010));
            Assert.True(world.CanSee(0x2B12012E, 0x2B130150));
            Assert.False(world.CanSee(0x2B12012E, 0x2D120010));
        }

        /// <summary>
        /// From outdoors, a room that sees outside is in view like the landscape around it; a cellar
        /// or a dungeon that does not, is not.
        /// </summary>
        [Fact]
        public void FromOutdoorsARoomIsInViewOnlyIfItSeesOutside()
        {
            Cells cells = new Cells().Room(0x2B12012E, seenOutside: true).Room(0x2B110150, seenOutside: false);
            WorldState world = new WorldState { Cells = cells };

            Assert.True(world.CanSee(0x2B110028, 0x2B12012E));
            Assert.False(world.CanSee(0x2B110028, 0x2B110150));
        }

        /// <summary>
        /// Without the client's cell archive no cell is known to see less than a building's room, so
        /// indoors everything within a landblock is kept: nothing the client still has is let go of.
        /// </summary>
        [Fact]
        public void WithoutTheCellArchiveIndoorsKeepsWhatIsWithinALandblock()
        {
            WorldState world = new WorldState();

            Assert.True(world.CanSee(0x01D90100, 0x01D90130));
            Assert.True(world.CanSee(0x01D90100, 0x01DA0100));
            Assert.False(world.CanSee(0x01D90100, 0x01DB0100));
            Assert.True(world.CanSee(0x2B110028, 0x2B110150));
        }

        // ------------------------------------------------------------------- what plugins see

        /// <summary>A transport that never ends by itself, so the host keeps ticking.</summary>
        private sealed class OpenTransport : IGameTransport
        {
            public string Description => "visibility test";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public bool CanSend => false;

            public bool CanShowInGame => false;

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                MessageReceived = null;
                Ended?.Invoke(this, EventArgs.Empty);
                Ended = null;
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// Plugins hear of it as of a deletion - IHost.ObjectRemoved, which Decal's ReleaseObject is
        /// made from - on the host's own tick, with no message from the server at all.
        /// </summary>
        [Fact]
        public async Task PluginsHearOfItAsTheyHearOfADeletionOnTheHostsTick()
        {
            Clock clock = new Clock();
            WorldState world = InTheWorld(clock);
            await using GameHost host = new GameHost(new OpenTransport(), new ListLog(),
                dataRoot: Path.Combine(Path.GetTempPath(), "achost-visibility-" + Guid.NewGuid().ToString("N")), world: world);
            TaskCompletionSource<uint> removed = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.ObjectRemoved += (_, id) => removed.TrySetResult(id);
            await host.StartAsync();

            TaskCompletionSource placed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() =>
            {
                StandIn(world, 0x2B110028);
                Place(world, 0x80000100, "Drudge Skulker", 0x2B130019);
                clock.Now += WorldState.ForgetAfter;
                placed.SetResult();
            });
            await placed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0x80000100u, await removed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }
}
