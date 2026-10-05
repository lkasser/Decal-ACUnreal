using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using AC.Proxy;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// The character leaving the world, and the host noticing. Live, AC:Unreal failed to finish
    /// entering the world and closed the session; ACE took the character out six seconds later -
    /// and the host, which had read nothing as an ending, kept the character logged in for a
    /// quarter of an hour, its plugins with it.
    /// </summary>
    /// <remarks>
    /// The bytes are the captures' own: from session-20260929-1934.acap, ACE's answer to a logoff -
    /// CharacterLogOff, the character list and the server's name in one packet - and the client's
    /// Disconnect after it; from session-20260929-1256.acap, the Disconnect the client sent on
    /// exiting from the world with no logoff at all, which is what the failed login sent too. No
    /// capture has an account booted, so that one is ACE's writer's layout.
    /// </remarks>
    public class LogoffTests
    {
        private const uint Player = 0x50000006;
        private const uint Robe = 0x80000011;
        private const uint Stone = 0x80000012;

        /// <summary>Captured: CharacterLogOff, CharacterList and ServerName, three fragments in one packet (packet 4058).</summary>
        private const string CapturedLogOffPacket =
            "DA0F00000600000047CD07880B00FAC58C000100"
            + "3C110000000000800100140000000900" + "53F60000"
            + "3D1100000000008001004C0000000900" + "58F60000" + CapturedCharacterList
            + "3E1100000000008001002C0000000900" + "E1F70000" + CapturedServerName;

        /// <summary>Captured: the client's Disconnect after that logoff - a bare header flagged 0x8002 (packet 2481).</summary>
        private const string CapturedDisconnect = "B109000002800000009C24960000820900000100";

        /// <summary>Captured: the Disconnect the client sent leaving the world with no logoff (session-20260929-1256.acap, packet 10668).</summary>
        private const string CapturedExitDisconnect = "AC29000002800000F536614F0000D82700000100";

        /// <summary>
        /// Captured: the character list, as ACE sends it at login and after a logoff - one character,
        /// Testchar I (0x50000006), no deletion pending; eleven slots; the account; Turbine chat on.
        /// </summary>
        private const string CapturedCharacterList =
            "00000000" + "01000000" + "06000050" + "0A0054657374636861722049" + "00000000"
            + "00000000" + "0B000000" + "08007465737461636374" + "0000" + "01000000" + "01000000";

        /// <summary>Captured: the server's name after the list - nobody online, 128 allowed, "Example Server".</summary>
        private const string CapturedServerName = "00000000" + "80000000" + "0E004578616D706C6520536572766572";

        private static DecodeOutcome Inbound(WorldState world, uint opcode, string hex)
            => MessageDecoder.Apply(AcMessage.Create(opcode, Convert.FromHexString(hex)), PacketDirection.Inbound, world);

        /// <summary>A world with the character in it: named, with a robe in its pack and a stone on the ground.</summary>
        private static WorldState InTheWorld()
        {
            WorldState world = new WorldState(() => DateTimeOffset.UnixEpoch);
            world.SetServerName("Example Server");
            WorldObject me = world.GetOrAdd(Player, out _);
            me.Name = "Testchar I";
            world.SetPlayerId(Player);

            world.GetOrAdd(Robe, out _).ContainerId = Player;
            world.GetOrAdd(Stone, out _);
            return world;
        }

        // ------------------------------------------------------------------- the messages

        [Fact]
        public void TheServersLogOffTakesTheCharacterOutOfTheWorld()
        {
            WorldState world = InTheWorld();
            List<string> left = new List<string>();
            List<uint> removed = new List<uint>();
            world.LoggedOff += (_, why) => left.Add($"{world.Character.Name} {world.Character.Id:X8}: {why}");
            world.ObjectRemoved += (_, id) => removed.Add(id);

            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.CharacterLogOff, string.Empty));

            // Said once, while the character was still described.
            Assert.Equal(new[] { "Testchar I 50000006: the server logged the character off" }, left);

            // Then forgotten, with everything it could see.
            Assert.Equal(0u, world.Character.Id);
            Assert.Null(world.Character.Object);
            Assert.Empty(world.Character.Name);
            Assert.Equal(0, world.ObjectCount);
            removed.Sort();
            Assert.Equal(new[] { Player, Robe, Stone }, removed);

            // The list and the name that follow say nothing more: the character is already out, and
            // the account is still connected to the server.
            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.CharacterList, CapturedCharacterList));
            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.ServerName, CapturedServerName));
            Assert.Single(left);
            Assert.Equal("Example Server", world.ServerName);
        }

        [Fact]
        public void ACharacterListWhileACharacterIsInTheWorldMeansItHasLeft()
        {
            WorldState world = InTheWorld();
            List<string> left = new List<string>();
            world.LoggedOff += (_, why) => left.Add(why);

            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.CharacterList, CapturedCharacterList));

            Assert.Equal(new[] { "the server went back to the character list" }, left);
            Assert.Equal(0u, world.Character.Id);
        }

        /// <summary>Every login starts with the character list, before any character is in the world.</summary>
        [Fact]
        public void TheCharacterListAtLoginSaysNothing()
        {
            WorldState world = new WorldState();
            List<string> left = new List<string>();
            world.LoggedOff += (_, why) => left.Add(why);
            world.CharacterUpdated += (_, _) => left.Add("updated");

            Assert.Equal(DecodeOutcome.Applied, Inbound(world, Opcodes.CharacterList, CapturedCharacterList));

            Assert.Empty(left);
        }

        [Fact]
        public void ABootedAccountLeavesTheWorldSayingWhy()
        {
            WorldState world = InTheWorld();
            List<string> left = new List<string>();
            world.LoggedOff += (_, why) => left.Add(why);

            WireWriter boot = new WireWriter(Opcodes.AccountBoot).String16L(" because the character was forced to log off by an admin");
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(boot.ToMessage(), PacketDirection.Inbound, world));

            // ACE boots with no reason as well, when it has none to give.
            WorldState other = InTheWorld();
            other.LoggedOff += (_, why) => left.Add(why);
            Assert.Equal(DecodeOutcome.Applied, Inbound(other, Opcodes.AccountBoot, string.Empty));

            Assert.Equal(new[] { "the server booted the account because the character was forced to log off by an admin", "the server booted the account" }, left);
            Assert.Equal(0u, world.Character.Id);
        }

        [Fact]
        public void ATruncatedBootReasonIsMalformedNotThrown()
        {
            Assert.Equal(DecodeOutcome.Malformed, Inbound(InTheWorld(), Opcodes.AccountBoot, "2000"));
        }

        /// <summary>
        /// Nothing of the character outlives it: the next one in may be another, and even the same
        /// one is described afresh.
        /// </summary>
        [Fact]
        public void TheCharacterIsForgottenWhole()
        {
            WorldState world = InTheWorld();
            world.Character.GetOrAddSkill(33).Ranks = 200;
            world.Character.ReplaceSpellbook(new uint[] { 2053 });
            world.SetClientPosition(new Location(0xA9B40021, 1, 2, 3, 1, 0, 0, 0), default);
            world.SetClientSelection(Stone);
            world.EnterPortalSpace(3);
            world.OpenVendor(Stone);
            world.SetTurbineChannels(new uint[] { 0, 2 });
            List<uint> vendors = new List<uint>();
            world.VendorChanged += (_, id) => vendors.Add(id);

            Inbound(world, Opcodes.CharacterLogOff, string.Empty);

            Assert.Empty(world.Character.Skills);
            Assert.Empty(world.Character.Spellbook);
            Assert.Equal(0u, world.Character.SelectedId);
            Assert.Null(world.Character.Location);
            Assert.False(world.Character.InPortalSpace);
            Assert.Equal(0u, world.Character.OpenVendorId);
            Assert.Equal(new[] { 0u }, vendors);
            Assert.Empty(world.TurbineChannels);
        }

        /// <summary>
        /// What was out of the character's view goes with the character: nothing is left waiting to
        /// be let go of, and the next login starts seeing from wherever it begins.
        /// </summary>
        [Fact]
        public void NothingIsLeftWaitingToBeLetGoOfOnceTheCharacterHasLeft()
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            WorldState world = new WorldState(() => now);
            world.GetOrAdd(Player, out _).Name = "Testchar I";
            world.SetPlayerId(Player);
            world.SetClientPosition(new Location(0x2B110028, 96, 96, 0, 1, 0, 0, 0), default);
            WorldObject far = world.GetOrAdd(Stone, out _);
            far.Location = new Location(0x2B130019, 50, 50, 0, 1, 0, 0, 0);
            world.NotifyCreated(far);
            Assert.Single(world.OutOfView);

            Inbound(world, Opcodes.CharacterLogOff, string.Empty);

            Assert.Empty(world.OutOfView);
            Assert.Equal(0u, world.ViewerCell);

            // The same id, created again at the next login before the character's own position is known.
            List<uint> removed = new List<uint>();
            world.ObjectRemoved += (_, id) => removed.Add(id);
            WorldObject again = world.GetOrAdd(Stone, out _);
            again.Location = new Location(0x2B130019, 50, 50, 0, 1, 0, 0, 0);
            world.NotifyCreated(again);
            now += TimeSpan.FromMinutes(1);
            world.ForgetOutOfView();
            Assert.Empty(removed);
        }

        [Fact]
        public void TheSameCharacterComingBackIsANewLogin()
        {
            WorldState world = InTheWorld();
            List<uint> identified = new List<uint>();
            world.PlayerIdentified += (_, id) => identified.Add(id);

            Inbound(world, Opcodes.CharacterLogOff, string.Empty);
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(new WireWriter(Opcodes.PlayerCreate).U32(Player).ToMessage(), PacketDirection.Inbound, world));

            Assert.Equal(new[] { Player }, identified);
            Assert.Equal(Player, world.Character.Id);
        }

        // ------------------------------------------------------------------- through the host

        private static CapturedDatagram Outbound(string hex)
            => new CapturedDatagram(DateTimeOffset.UtcNow, PacketDirection.Outbound, 9100, Convert.FromHexString(hex));

        private static CapturedDatagram InboundPacket(string hex)
            => new CapturedDatagram(DateTimeOffset.UtcNow, PacketDirection.Inbound, 9101, Convert.FromHexString(hex));

        /// <summary>The server named, the character created and in the world, as a login leaves them.</summary>
        private static IEnumerable<CapturedDatagram> LoggedIn()
        {
            yield return Capture.Inbound(new WireWriter(Opcodes.ServerName).I32(1).I32(128).String16L("Example Server"));
            yield return Capture.Inbound(new WireWriter(Opcodes.PlayerCreate).U32(Player));
            yield return Capture.Inbound(WireWriter.ObjectCreate(Player, "Testchar I", 1, ItemTypes.Creature, 0));
            yield return Capture.Inbound(WireWriter.ObjectCreate(Robe, "Satin Pants", 2, ItemTypes.Clothing, 0, containerId: Player));
        }

        /// <summary>Hears the host's logoff, with who the character still was when it did.</summary>
        private sealed class LogoffPlugin : IPlugin
        {
            private IHost _host;

            public string Name => "Logoff";

            public List<string> Heard { get; } = new List<string>();

            public void Startup(IHost host)
            {
                _host = host;
                host.PlayerIdentified += (_, id) => Heard.Add($"login {id:X8}");
                host.LoggedOff += (_, why) => Heard.Add($"logoff {_host.Character.Name}: {why}");
            }

            public void Shutdown()
            {
            }
        }

        private static async Task<(GameHost Host, LogoffPlugin Plugin)> RunAsync(IEnumerable<CapturedDatagram> datagrams)
        {
            LogoffPlugin plugin = new LogoffPlugin();
            GameHost host = new GameHost(new CaptureTransport(datagrams), new ListLog(), dataRoot: Path.Combine(Path.GetTempPath(), "achost-tests"));
            host.AddPlugin(plugin);
            await host.StartAsync();
            await host.Ended.WaitAsync(TimeSpan.FromSeconds(10));
            await host.DisposeAsync();
            return (host, plugin);
        }

        /// <summary>
        /// What happened live: the client gave up and closed the session, and ACE said nothing - it
        /// cannot, to a session it has dropped. The packet is the only sign, and it is enough.
        /// </summary>
        [Fact]
        public async Task AClientThatClosesTheSessionTakesTheCharacterOutOfTheWorld()
        {
            List<CapturedDatagram> datagrams = new List<CapturedDatagram>(LoggedIn()) { Outbound(CapturedExitDisconnect) };

            (GameHost host, LogoffPlugin plugin) = await RunAsync(datagrams);

            Assert.Equal(new[] { "login 50000006", "logoff Testchar I: the client closed the session" }, plugin.Heard);
            Assert.Equal(0u, host.Character.Id);
            Assert.Null(host.World.ServerName);
            Assert.Equal(0, host.World.ObjectCount);
        }

        [Fact]
        public async Task ALogoffAndTheDisconnectAfterItAreOneLeaving()
        {
            List<CapturedDatagram> datagrams = new List<CapturedDatagram>(LoggedIn())
            {
                InboundPacket(CapturedLogOffPacket),
                Outbound(CapturedDisconnect),
            };

            (GameHost host, LogoffPlugin plugin) = await RunAsync(datagrams);

            Assert.Equal(new[] { "login 50000006", "logoff Testchar I: the server logged the character off" }, plugin.Heard);
            Assert.Equal(0u, host.Character.Id);
            Assert.Null(host.World.ServerName);
        }

        /// <summary>
        /// A client that stops without a word - killed, crashed - leaves no packet behind. Its next
        /// login is the first sign, and the old character is let go then rather than carried into
        /// the new session.
        /// </summary>
        [Fact]
        public async Task ANewLoginEndsASessionTheClientNeverClosed()
        {
            byte[] login = PacketWriter.Build(new PacketHeader { Sequence = 0, Flags = PacketHeaderFlags.LoginRequest }, new byte[] { 1, 2, 3, 4 });
            List<CapturedDatagram> datagrams = new List<CapturedDatagram>(LoggedIn())
            {
                new CapturedDatagram(DateTimeOffset.UtcNow, PacketDirection.Outbound, 9100, login),
                Capture.Inbound(new WireWriter(Opcodes.ServerName).I32(1).I32(128).String16L("Example Server")),
                Capture.Inbound(new WireWriter(Opcodes.PlayerCreate).U32(Player)),
            };

            (GameHost host, LogoffPlugin plugin) = await RunAsync(datagrams);

            Assert.Equal(new[] { "login 50000006", "logoff Testchar I: the client began a new login", "login 50000006" }, plugin.Heard);
            Assert.Equal(Player, host.Character.Id);
            Assert.Equal("Example Server", host.World.ServerName);
        }

        /// <summary>
        /// The live case behind the ghost aurochs (docs/live-tests/combat.md): at 11:02 a login that
        /// never finished entering the world, the client's Disconnect at 11:03, and a new login at
        /// 11:18, by when the landblock had been loaded afresh and its creatures created again with
        /// new ids where the old ones stood. Nothing of the first session reaches the second.
        /// </summary>
        [Fact]
        public async Task NothingOfAFailedLoginSurvivesIntoTheNextSession()
        {
            const uint OldYearling = 0x80000C92, NewYearling = 0x80000F10, OldLongbow = 0x80000BF6;
            byte[] login = PacketWriter.Build(new PacketHeader { Sequence = 0, Flags = PacketHeaderFlags.LoginRequest }, new byte[] { 1, 2, 3, 4 });
            List<CapturedDatagram> datagrams = new List<CapturedDatagram>(LoggedIn())
            {
                Capture.Inbound(WireWriter.ObjectCreate(OldYearling, "Auroch Yearling", 3, ItemTypes.Creature, DescriptionFlags.Stuck | DescriptionFlags.Attackable)),
                Capture.Inbound(WireWriter.ObjectCreate(OldLongbow, "Longbow", 4, ItemTypes.MissileWeapon, 0)),
                Outbound(CapturedExitDisconnect),
                new CapturedDatagram(DateTimeOffset.UtcNow, PacketDirection.Outbound, 9100, login),
            };
            datagrams.AddRange(LoggedIn());
            datagrams.Add(Capture.Inbound(WireWriter.ObjectCreate(NewYearling, "Auroch Yearling", 3, ItemTypes.Creature, DescriptionFlags.Stuck | DescriptionFlags.Attackable)));

            (GameHost host, LogoffPlugin plugin) = await RunAsync(datagrams);

            Assert.Equal(new[] { "login 50000006", "logoff Testchar I: the client closed the session", "login 50000006" }, plugin.Heard);
            Assert.False(host.World.TryGet(OldYearling, out _));
            Assert.False(host.World.TryGet(OldLongbow, out _));
            Assert.True(host.World.TryGet(NewYearling, out _));
        }

        /// <summary>A session closed at the character list, with nobody in the world, is no logoff.</summary>
        [Fact]
        public async Task ClosingASessionWithNobodyInTheWorldSaysNothing()
        {
            (GameHost host, LogoffPlugin plugin) = await RunAsync(new[]
            {
                Capture.Inbound(new WireWriter(Opcodes.ServerName).I32(1).I32(128).String16L("Example Server")),
                Outbound(CapturedDisconnect),
            });

            Assert.Empty(plugin.Heard);
            Assert.Null(host.World.ServerName);
        }
    }

}
