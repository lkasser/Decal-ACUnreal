using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Actions;
using AC.Host.Decoding;
using AC.Host.Handover;
using AC.Host.Plugins;
using AC.Host.Runtime;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using AC.Proxy;
using Decal.Adapter.Hosting;
using Decal.Adapter.Wrappers;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// What a relog is made of, as captured - the client logging off and entering the world again -
    /// and the host doing both itself, as AC:Unreal lets it live: logging out by the client's own
    /// message put into its stream, and entering by the character select's own Enter, clicked.
    /// </summary>
    /// <remarks>
    /// The bytes are session-20260929-1934.acap's: the character list and the server's name at the
    /// login (packet 6), the client's CharacterEnterWorld naming Testchar I (10); later, two of its
    /// movement reports (7103, 7104), its CharacterLogOff (7105) and the server's answer,
    /// CharacterLogOff, the list and the name in one packet (7152). The client's login request is
    /// never used here: it carries the account's password.
    /// </remarks>
    public class RelogTests
    {
        internal const uint Testchar = 0x50000006;
        internal const string Account = "testacct";

        /// <summary>Captured: the character list - Testchar I, eleven slots, the account, Turbine chat on.</summary>
        internal const string CapturedCharacterList =
            "00000000" + "01000000" + "06000050" + "0A0054657374636861722049" + "00000000"
            + "00000000" + "0B000000" + "08007465737461636374" + "0000" + "01000000" + "01000000";

        /// <summary>Captured: the server's name after the list.</summary>
        internal const string CapturedServerName = "00000000" + "80000000" + "0E004578616D706C6520536572766572";

        /// <summary>Captured: CharacterEnterWorld's payload, Testchar I (0x50000006) and the account.</summary>
        internal const string CapturedEnterWorld = "06000050" + "0800" + "7465737461636374" + "0000";

        /// <summary>Captured: a MoveToState of the client's, its fragment 15 (packet 7103).</summary>
        private const string ClientMoveToStatePacket =
            "99090000060000009ED4D24800007309540001000F000000000000800100540000000300B1F700000D0000001CF6000003000000010000003D0000802E00112B0050F6422074FA4200004042E586473F0000000000000000E16320BFC70000000000000001000000";

        /// <summary>Captured: a position report of the client's, its fragment 16 (packet 7104).</summary>
        private const string ClientPositionPacket =
            "9A090000060000006DCFD0B8000073094800010010000000000000800100480000000700B1F700000E00000053F700002E00112B0050F6422074FA4200004042E586473F0000000000000000E16320BFC70000000000000001000000";

        /// <summary>Captured: the client's CharacterLogOff, its fragment 17 (packet 7105).</summary>
        private const string ClientLogOffPacket = "9B0900000600000080A6E24900007309140001001100000000000080010014000000090053F60000";

        /// <summary>Captured: the server's answer to a logoff - CharacterLogOff, the list and the name, in one packet (7152).</summary>
        private const string ServerLogOffPacket =
            "DA0F00000600000047CD07880B00FAC58C000100"
            + "3C110000000000800100140000000900" + "53F60000"
            + "3D1100000000008001004C0000000900" + "58F60000" + CapturedCharacterList
            + "3E1100000000008001002C0000000900" + "E1F70000" + CapturedServerName;

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private static IReadOnlyList<AcFragment> Fragments(byte[] datagram)
        {
            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            return packet.Fragments;
        }

        private static uint KeyOf(byte[] datagram)
        {
            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.True(packet.TryRecoverEncryptionKey(out uint key));
            return key;
        }

        /// <summary>
        /// The same number in the client's run, the same size, the same bytes - the fragment the client
        /// itself sent. Only the fragment's id and queue are the relay's own, as for every message it
        /// puts in: ACE hands a message to its handler by the opcode alone.
        /// </summary>
        private static void AssertTheClientsOwn(AcFragment theirs, AcFragment ours)
        {
            Assert.Equal(theirs.Header.Sequence, ours.Header.Sequence);
            Assert.Equal(theirs.Header.Count, ours.Header.Count);
            Assert.Equal(theirs.Header.Index, ours.Header.Index);
            Assert.Equal(theirs.Header.TotalSize, ours.Header.TotalSize);
            Assert.Equal(theirs.Payload.ToArray(), ours.Payload.ToArray());
        }

        // ------------------------------------------------------------------- what the session says

        /// <summary>
        /// The phases a relog goes through, read off the captured messages: the list, entering,
        /// in the world, the client asking to log off - four times, two seconds apart, as captured -
        /// and the server's answer.
        /// </summary>
        [Fact]
        public void ACapturedRelogWalksThePhases()
        {
            RelogRig rig = new RelogRig();
            List<SessionPhase> phases = new List<SessionPhase> { rig.World.Phase };
            void Note() => phases.Add(rig.World.Phase);

            rig.Server(Opcodes.CharacterList, CapturedCharacterList);
            Note();
            Assert.Equal(Account, rig.World.AccountName);
            AccountCharacter only = Assert.Single(rig.World.AccountCharacters);
            Assert.Equal((Testchar, "Testchar I", 0u), (only.Id, only.Name, only.DeleteTimeout));

            rig.Client(Opcodes.CharacterEnterWorldRequest);
            Note();
            rig.Server(Opcodes.CharacterEnterWorldServerReady);
            rig.Client(Opcodes.CharacterEnterWorld, CapturedEnterWorld);
            Note();
            Assert.Equal(Testchar, rig.World.EnteringCharacterId);
            rig.ServerCreatesTheCharacter();
            Note();

            for (int i = 0; i < 4; i++)
                rig.Client(Opcodes.CharacterLogOff);
            Note();
            rig.ServerLogsOff();
            Note();

            Assert.Equal(new[]
            {
                SessionPhase.None,
                SessionPhase.CharacterList,
                SessionPhase.EnteringWorld,
                SessionPhase.EnteringWorld,
                SessionPhase.InWorld,
                SessionPhase.LoggingOff,
                SessionPhase.CharacterList,
            }, phases);

            // The client asks four times; the logoff is asked for once.
            Assert.Equal(new[] { $"player {Testchar:X8}", "logging off", "logged off: the server logged the character off" }, rig.Heard);
        }

        [Fact]
        public void TheServerTurningACharacterDownSendsTheEntryBackToTheList()
        {
            RelogRig rig = new RelogRig();
            rig.AtTheCharacterList();
            rig.Client(Opcodes.CharacterEnterWorldRequest);
            rig.Server(Opcodes.CharacterEnterWorldServerReady);
            rig.Client(Opcodes.CharacterEnterWorld, CapturedEnterWorld);

            rig.Server(new WireWriter(Opcodes.CharacterError).U32(SessionControl.CharacterStillInWorld));

            Assert.Equal(SessionPhase.CharacterList, rig.World.Phase);
            Assert.Equal(13u, rig.World.LastCharacterError);
            Assert.Equal(0u, rig.World.EnteringCharacterId);
        }

        /// <summary>
        /// A host that joined a session under way, nothing handed over, knows nothing of the
        /// character - but what is around one, and what one does, says one is in the world, and so
        /// it can log it out, which is how such a host comes to know the session whole.
        /// </summary>
        [Fact]
        public void AHostThatJoinedMidSessionSeesACharacterInTheWorldByItsTraffic()
        {
            RelogRig rig = new RelogRig();
            Assert.Equal(SessionPhase.None, rig.World.Phase);

            rig.Server(new WireWriter(Opcodes.UpdatePosition).U32(0x80000301).U32(0x08 | 0x10 | 0x20).U32(0xA9B40019).F32(10).F32(20).F32(30).F32(0.7f).U16(1).U16(2).U16(3).U16(4));
            Assert.Equal(SessionPhase.InWorld, rig.World.Phase);
            Assert.Equal(0u, rig.World.Character.Id);

            Assert.NotNull(rig.Session.BeginLogOut(SessionRoute.Messages, out _));
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff }, rig.Relay().Select(m => m.Opcode));

            // Nobody it knew left, so nothing is said of a logoff; but the list is had.
            rig.ServerLogsOff();
            Assert.Equal(SessionPhase.CharacterList, rig.World.Phase);
            Assert.Empty(rig.Heard);
            Assert.Single(rig.World.AccountCharacters);
        }

        /// <summary>A booted account has no character list to go back to.</summary>
        [Fact]
        public void ABootLeavesNoSession()
        {
            RelogRig rig = new RelogRig();
            rig.InTheWorld();

            rig.Server(Opcodes.AccountBoot);

            Assert.Equal(SessionPhase.None, rig.World.Phase);
        }

        // ------------------------------------------------------------------- the client's own messages

        [Fact]
        public async Task TheBareLogoffIsByteForByteTheClients()
        {
            RigTransport transport = new RigTransport();
            ClientActions actions = new ClientActions(transport, new ListLog(), new WorldState());

            Assert.True(await actions.SendLogOffAsync());

            AcMessage sent = Assert.Single(transport.TakeSent());
            Assert.Equal(0xF653u, sent.Opcode);
            Assert.Empty(sent.Payload.ToArray());
        }

        [Fact]
        public async Task NothingGoesWhileActingIsOff()
        {
            RigTransport transport = new RigTransport();
            ClientActions actions = new ClientActions(transport, new ListLog(), new WorldState()) { Allowed = () => false };

            Assert.False(await actions.SendLogOffAsync());
            Assert.Empty(transport.TakeSent());
        }

        /// <summary>
        /// Logging out by message: the CharacterLogOff rides out behind the client's position report,
        /// and is the very fragment the client sent next in the capture.
        /// </summary>
        [Fact]
        public void LoggedOutByMessageTheClientsPacketCarriesTheFragmentTheClientSentNext()
        {
            RelogRig rig = new RelogRig { OverlayAttached = false };
            rig.InTheWorld();
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rig.Transport.Rewriter = rewriter;

            byte[] first = Hex(ClientMoveToStatePacket);
            Assert.Same(first, rewriter.Rewrite(first));

            Assert.NotNull(rig.Session.BeginLogOut(SessionRoute.Auto, out _));
            byte[] position = Hex(ClientPositionPacket);
            byte[] carried = rewriter.Rewrite(position);

            IReadOnlyList<AcFragment> fragments = Fragments(carried);
            Assert.Equal(new uint[] { 16, 17 }, fragments.Select(f => f.Header.Sequence));
            Assert.Equal(Fragments(position)[0].Payload.ToArray(), fragments[0].Payload.ToArray());
            AssertTheClientsOwn(Assert.Single(Fragments(Hex(ClientLogOffPacket))), fragments[1]);
            Assert.Equal(KeyOf(position), KeyOf(carried));
        }

        // ------------------------------------------------------------------- logging out

        [Fact]
        public void LoggingOutByMessageAsksTheServerAndWatchesTheClientSettle()
        {
            RelogRig rig = new RelogRig { OverlayAttached = false };
            rig.InTheWorld();

            SessionRequest request = rig.Session.BeginLogOut(SessionRoute.Auto, out string refusal);
            Assert.Null(refusal);
            Assert.Equal(SessionRoute.Messages, request.Route);
            Assert.Empty(rig.Published);

            // Out in the client's next packet, and seen going: the logoff is asked for.
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff }, rig.Relay().Select(m => m.Opcode));
            Assert.Equal(SessionPhase.LoggingOff, rig.World.Phase);
            Assert.Equal(new[] { $"player {Testchar:X8}", "logging off" }, rig.Heard);

            // About six seconds later, ACE's answer.
            rig.Pass(TimeSpan.FromSeconds(6));
            Assert.Null(request.Succeeded);
            rig.ServerLogsOff();
            Assert.Equal(SessionPhase.CharacterList, rig.World.Phase);
            Assert.Null(request.Succeeded);

            // The client is given its moment to drop the session, as every captured logoff's did.
            rig.Pass(rig.Session.ClientSettles);
            Assert.True(request.Succeeded);
            Assert.StartsWith("Logged Testchar I (0x50000006) out to the character list", request.Outcome);
            Assert.Equal("Logged Testchar I (0x50000006) out to the character list", request.Finished.Result.Split(':')[0]);
            Assert.Equal(new[] { $"player {Testchar:X8}", "logging off", "logged off: the server logged the character off" }, rig.Heard);
        }

        /// <summary>
        /// Logging out by keys, as asked for live, pressed Ctrl+Q - which AC:Unreal took as Q, its
        /// autorun, and the character ran 22 m into a fence. Now no key is pressed at all, whichever
        /// way is asked for and whether or not the game's window is showing: the message goes,
        /// and the log says why the keys cannot be had.
        /// </summary>
        [Theory]
        [InlineData(SessionRoute.Keys)]
        [InlineData(SessionRoute.Auto)]
        [InlineData(SessionRoute.Messages)]
        public void ALogoutPressesNothingAndGoesByTheMessage(SessionRoute asked)
        {
            RelogRig rig = new RelogRig { Window = new GameWindowState(known: true, minimized: false, drawing: true, parked: false) };
            rig.InTheWorld();

            SessionRequest request = rig.Session.BeginLogOut(asked, out string refusal);
            Assert.Null(refusal);
            Assert.Equal(SessionRoute.Messages, request.Route);
            Assert.Equal(asked, request.Asked);
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff }, rig.Relay().Select(m => m.Opcode));

            rig.ServerLogsOff();
            rig.Pass(rig.Session.ClientSettles);
            Assert.True(request.Succeeded);

            Assert.Empty(rig.Published);
            Assert.Empty(rig.Clicked);
            Assert.Equal(asked == SessionRoute.Keys, rig.Log.Lines.Any(l => l.Contains("by keys cannot be done safely in AC:Unreal") && l.Contains("its autorun")));
        }

        /// <summary>
        /// Keys held for plugins - a walk, a turn - are let go before the logoff goes, so the client
        /// is not left moving; nothing is pressed to stop a movement of its own, such as its autorun,
        /// which is a toggle.
        /// </summary>
        [Fact]
        public void TheKeysHeldForPluginsAreLetGoBeforeTheLogoff()
        {
            RelogRig rig = new RelogRig();
            rig.InTheWorld();
            Assert.True(rig.Keys.Hold(GameKey.Forward, true));
            Assert.True(rig.Keys.Hold(GameKey.TurnLeft, true));

            SessionRequest request = rig.Session.BeginLogOut(SessionRoute.Auto, out _);

            Assert.Empty(rig.Keys.Held);
            Assert.Empty(rig.Published.Last());
            Assert.All(rig.Published, keys => Assert.DoesNotContain(0x11, keys));
            Assert.Contains(rig.Log.Lines, l => l.Contains("let go of the keys held for plugins first (") && l.Contains("Forward") && l.Contains("TurnLeft"));
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff }, rig.Relay().Select(m => m.Opcode));
            Assert.Null(request.Succeeded);
        }

        /// <summary>A logout needs no overlay: the message goes whatever was asked for.</summary>
        [Fact]
        public void ALogoutByKeysWithoutTheOverlayStillGoes()
        {
            RelogRig rig = new RelogRig { OverlayAttached = false };
            rig.InTheWorld();

            SessionRequest request = rig.Session.BeginLogOut(SessionRoute.Keys, out string refusal);
            Assert.Null(refusal);
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff }, rig.Relay().Select(m => m.Opcode));
            rig.ServerLogsOff();
            rig.Pass(rig.Session.ClientSettles);
            Assert.True(request.Succeeded);
        }

        [Fact]
        public void NothingIsAskedForWhileActingIsOffOrFromTheWrongPlace()
        {
            RelogRig rig = new RelogRig { ActingAllowed = false };
            rig.InTheWorld();
            Assert.Null(rig.Session.BeginLogOut(SessionRoute.Auto, out string refusal));
            Assert.Contains("may not act", refusal);

            rig.ActingAllowed = true;
            Assert.Null(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out refusal));
            Assert.Contains("log out first", refusal);

            Assert.NotNull(rig.Session.BeginLogOut(SessionRoute.Messages, out _));
            Assert.Null(rig.Session.BeginLogOut(SessionRoute.Messages, out refusal));
            Assert.StartsWith("already busy: log out by messages", refusal);

            Assert.Empty(rig.Published);
            Assert.Single(rig.Relay());
            Assert.Equal(SessionPhase.LoggingOff, rig.World.Phase);
        }

        /// <summary>The player's own logoff, under way, is not asked for a second time.</summary>
        [Fact]
        public void APlayersOwnLogoffIsNotAskedForAgain()
        {
            RelogRig rig = new RelogRig();
            rig.InTheWorld();
            rig.Client(Opcodes.CharacterLogOff);

            Assert.Null(rig.Session.BeginLogOut(SessionRoute.Auto, out string refusal));
            Assert.Equal("the character is already logging off", refusal);
            Assert.Empty(rig.Published);
        }

        /// <summary>
        /// The client is still showing the world when the server has logged the character off - an
        /// answer it did not ask for, not taken up. No key is pressed for it, none logging out safely:
        /// the player is asked to log out in the game, and the request the client then sends is
        /// answered with the server's own words, the very bytes the server sent.
        /// </summary>
        [Fact]
        public void AClientLeftInTheWorldIsAnsweredWhenThePlayerLogsOutInTheGame()
        {
            RelogRig rig = new RelogRig();
            rig.InTheWorld();
            rig.Session.NoteClientLog("[2026.10.05-01.33.49:652][ 40]LogTemp: ACE UIFlow mode -> 6 layout=0x21000005");

            SessionRequest request = rig.Session.BeginLogOut(SessionRoute.Messages, out _);
            rig.Relay();
            rig.ServerLogsOff();
            rig.Pass(rig.Session.ClientSettles);

            // Still the game, by its log: the player is asked, and nothing pressed.
            Assert.Null(request.Succeeded);
            Assert.Empty(rig.Published);
            Assert.Equal("the player to log out in the game", request.Waiting);
            Assert.Contains(rig.Log.Lines, l => l.StartsWith("WARN ", StringComparison.Ordinal) && l.Contains("log out in the game (Esc, Log Out)"));

            // Its own logoff, which the server - at the character list already - drops; the host answers.
            rig.Pass(TimeSpan.FromSeconds(20));
            rig.Client(Opcodes.CharacterLogOff);
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff, Opcodes.CharacterList, Opcodes.ServerName }, rig.Transport.Shown.Select(m => m.Opcode));
            Assert.Equal(CapturedCharacterList, Convert.ToHexString(rig.Transport.Shown[1].Payload.Span));
            Assert.Equal(CapturedServerName, Convert.ToHexString(rig.Transport.Shown[2].Payload.Span));

            rig.Session.NoteClientLog("[2026.09.30-03.56.59:096][455]LogTemp: [ACE] Logged off — returned to character select");
            rig.Pass(rig.Session.ClientSettles);
            Assert.True(request.Succeeded);
            Assert.Contains("the client shows its character select", request.Outcome);
        }

        [Fact]
        public void AClientLeftInTheWorldThatNobodyLogsOutIsReported()
        {
            RelogRig rig = new RelogRig { OverlayAttached = false };
            rig.InTheWorld();
            rig.Session.NoteClientLog("LogTemp: ACE UIFlow mode -> 6 layout=0x21000005");

            SessionRequest request = rig.Session.BeginLogOut(SessionRoute.Messages, out _);
            rig.Relay();
            rig.ServerLogsOff();
            rig.Pass(rig.Session.ClientSettles);
            Assert.Null(request.Succeeded);

            rig.Pass(rig.Session.PlayerLogsOut);
            Assert.False(request.Succeeded);
            Assert.Contains("the client is still showing the world", request.Outcome);
            Assert.Contains("log out in the game", request.Outcome);
            Assert.Empty(rig.Transport.Shown);
        }

        /// <summary>
        /// Without its log, a client's acting in the world after the server took the character out
        /// is the sign it never left: the player is asked to log out, and nothing is pressed.
        /// </summary>
        [Fact]
        public void AClientStillActingInTheWorldIsSaidToBeThere()
        {
            RelogRig rig = new RelogRig();
            rig.InTheWorld();
            SessionRequest request = rig.Session.BeginLogOut(SessionRoute.Messages, out _);
            rig.Relay();
            rig.ServerLogsOff();

            rig.Pass(TimeSpan.FromSeconds(2));
            rig.Client(new WireWriter(Opcodes.GameAction).U32(9).U32(GameActions.LoginComplete));
            rig.Pass(rig.Session.ClientSettles);

            Assert.Null(request.Succeeded);
            Assert.Equal("the player to log out in the game", request.Waiting);
            Assert.Empty(rig.Published);
        }

        [Fact]
        public void AClientThatGoesOnToItsLoginScreenHasEndedItsSession()
        {
            RelogRig rig = new RelogRig();
            rig.InTheWorld();
            SessionRequest request = rig.Session.BeginLogOut(SessionRoute.Messages, out _);
            rig.Relay();
            rig.ServerLogsOff();

            rig.Session.NoteClientLog("[2026.09.30-03.57.00:129][785]LogTemp: ACE UIFlow mode -> 2 layout=0x21000001");
            rig.Pass(rig.Session.ClientSettles);

            Assert.False(request.Succeeded);
            Assert.Contains("login screen", request.Outcome);
            Assert.Contains("password", request.Outcome);
        }

        /// <summary>As every captured logoff was followed by within a second and a half: the client closing the session.</summary>
        [Fact]
        public void TheSessionEndingFailsWhatIsUnderWay()
        {
            RelogRig rig = new RelogRig();
            rig.InTheWorld();
            SessionRequest request = rig.Session.BeginLogOut(SessionRoute.Messages, out _);
            rig.Relay();
            rig.ServerLogsOff();
            rig.Pass(TimeSpan.FromSeconds(1));

            rig.World.EndSession("the client closed the session");
            rig.Session.OnSessionEnded("the client closed the session");

            Assert.False(request.Succeeded);
            Assert.Equal("Could not log out: the session ended - the client closed the session.", request.Outcome);
            Assert.Null(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Messages, out string refusal));
            Assert.Contains("needs a full login, with the password", refusal);
        }

        [Fact]
        public void AServerThatNeverAnswersIsGivenUpOn()
        {
            RelogRig rig = new RelogRig();
            rig.InTheWorld();
            SessionRequest request = rig.Session.BeginLogOut(SessionRoute.Messages, out _);
            rig.Relay();

            rig.Pass(rig.Session.ServerLogsOff);

            Assert.False(request.Succeeded);
            Assert.Contains("the server did not log the character off", request.Outcome);
        }

        // ------------------------------------------------------------------- entering the world

        /// <summary>
        /// Entering is the character select clicked as a player clicks it - the character's row,
        /// then its Enter - and the client's own request, ready and naming going by. Without the
        /// client's log, its LoginComplete after the server created the character is arrival.
        /// </summary>
        [Fact]
        public void EnteringClicksTheCharactersRowAndEnterAndFollowsTheClientsOwnEntry()
        {
            RelogRig rig = new RelogRig();
            rig.AtTheCharacterList();

            SessionRequest request = rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out string refusal);
            Assert.Null(refusal);
            Assert.Equal(SessionRoute.Keys, request.Route);
            GameClick click = Assert.Single(rig.Clicked);
            Assert.Equal((800, 600), (click.LayoutWidth, click.LayoutHeight));
            Assert.Equal(new[] { (122, 220), (344, 394) }, click.Points);
            Assert.Empty(rig.Published);
            Assert.Contains(rig.Log.Lines, l => l.Contains("Entering the world as Testchar I (0x50000006): clicked its row, the first on the character select, then Enter"));

            rig.Client(Opcodes.CharacterEnterWorldRequest);
            rig.Server(Opcodes.CharacterEnterWorldServerReady);
            Assert.Contains(Opcodes.CharacterEnterWorldServerReady, rig.ClientSaw);
            rig.Client(Opcodes.CharacterEnterWorld, CapturedEnterWorld);
            rig.ServerCreatesTheCharacter();
            Assert.Null(request.Succeeded);
            rig.Client(RelogRig.LoginComplete(1));

            Assert.True(request.Succeeded);
            Assert.Equal("Entered the world as Testchar I (0x50000006): the client has finished arriving (its LoginComplete; its own log is not read here).", request.Outcome);
            Assert.Empty(rig.Relay());
        }

        /// <summary>
        /// Since release 94 the client draws its character select at its Desktop UI Scale. The click
        /// keeps the retail layout's own points and carries the scale the player chose, which the
        /// overlay - knowing the window - turns into pixels as the client does; the log says it.
        /// </summary>
        [Fact]
        public void EnteringClicksAtTheClientsDesktopUiScale()
        {
            RelogRig rig = new RelogRig { UiScale = 1.75 };
            rig.AtTheCharacterList();

            Assert.NotNull(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out _));
            GameClick click = Assert.Single(rig.Clicked);
            Assert.Equal((800, 600), (click.LayoutWidth, click.LayoutHeight));
            Assert.Equal(new[] { (122, 220), (344, 394) }, click.Points);
            Assert.Equal(1.75, click.UiScale);
            Assert.Contains(rig.Log.Lines, l => l.Contains("(122,220, then 344,394 of 800x600 at 175% centred in the game window)"));
        }

        /// <summary>A scale between the client's quarter steps, or outside its 100% to 300%, is clicked as the client takes it.</summary>
        [Theory]
        [InlineData(2.1, 2.0)]
        [InlineData(0.5, 1.0)]
        [InlineData(7.0, 3.0)]
        [InlineData(double.NaN, 1.0)]
        public void EnteringClicksAtTheScaleTheClientTakes(double chosen, double clicked)
        {
            RelogRig rig = new RelogRig { UiScale = chosen };
            rig.AtTheCharacterList();

            rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out _);

            Assert.Equal(clicked, Assert.Single(rig.Clicked).UiScale);
        }

        /// <summary>
        /// Entering by messages, as asked for live, put the server's login to a client that had not
        /// asked: AC:Unreal logged it and sent LoginComplete, but stayed at its character select. So
        /// it goes by the character select's Enter instead, and the log says why.
        /// </summary>
        [Fact]
        public void EnteringByMessagesClicksTheCharacterSelectInstead()
        {
            RelogRig rig = new RelogRig();
            rig.AtTheCharacterList();

            SessionRequest request = rig.Session.BeginEnterWorld(Testchar, SessionRoute.Messages, out _);

            Assert.Equal(SessionRoute.Messages, request.Asked);
            Assert.Equal(SessionRoute.Keys, request.Route);
            Assert.Single(rig.Clicked);
            Assert.Empty(rig.Relay());
            Assert.Contains(rig.Log.Lines, l => l.Contains("by messages cannot be done in AC:Unreal") && l.Contains("its character select stays up"));
        }

        /// <summary>AC:Unreal enters only from its character select, which only the overlay can click: without it, no way in.</summary>
        [Fact]
        public void EnteringWithoutTheOverlayOrWithTheGameMinimizedIsRefused()
        {
            RelogRig rig = new RelogRig { OverlayAttached = false };
            rig.AtTheCharacterList();

            foreach (SessionRoute route in new[] { SessionRoute.Auto, SessionRoute.Keys, SessionRoute.Messages })
            {
                Assert.Null(rig.Session.BeginEnterWorld(Testchar, route, out string refusal));
                Assert.Equal("AC:Unreal enters the world only from its own character select, whose Enter the overlay clicks, and the overlay is not attached", refusal);
            }

            rig.OverlayAttached = true;
            rig.Window = new GameWindowState(known: true, minimized: true, drawing: false, parked: false);
            Assert.Null(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out string minimized));
            Assert.Contains("the game window is minimized", minimized);
            Assert.Contains("ctl window keep on", minimized);

            // Parked off-screen in place of minimized, it still draws and takes the click.
            rig.Window = new GameWindowState(known: true, minimized: false, drawing: true, parked: true);
            Assert.NotNull(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out _));

            Assert.Single(rig.Clicked);
            Assert.Empty(rig.Relay());
            Assert.Empty(rig.Published);
        }

        [Fact]
        public void AClientThatDoesNotAskToEnterAfterTheClickIsReported()
        {
            RelogRig rig = new RelogRig();
            rig.AtTheCharacterList();
            rig.Session.NoteClientLog("LogTemp: ACE UIFlow mode -> 3 layout=0x21000004");

            SessionRequest request = rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out _);
            rig.Pass(rig.Session.ClientAnswers);

            Assert.False(request.Succeeded);
            Assert.Equal("Could not enter the world as Testchar I (0x50000006): the client did not ask to enter within 5 s of its character select's Enter being clicked"
                + " - its log says it shows its character select. ACUnrealOverlay.log, beside the overlay, says where it clicked. Enter in the game.", request.Outcome);
            Assert.Contains(rig.Log.Lines, l => l.StartsWith("WARN Could not enter the world", StringComparison.Ordinal));
            Assert.Empty(rig.Relay());
        }

        /// <summary>
        /// Another character is chosen by clicking its own row, in the order the server lists the
        /// account's characters, sixteen pixels apart as the retail layout has them.
        /// </summary>
        [Fact]
        public void AnotherCharactersOwnRowIsClicked()
        {
            const uint Second = 0x50000007;
            RelogRig rig = new RelogRig();
            rig.InTheWorld();
            rig.Session.BeginLogOut(SessionRoute.Messages, out _);
            rig.Relay();
            rig.Server(Opcodes.CharacterLogOff);
            rig.Server(TwoCharacters(Second));
            rig.Pass(rig.Session.ClientSettles);

            SessionRequest request = rig.Session.BeginEnterWorld(Second, SessionRoute.Auto, out _);
            Assert.Equal(new[] { (122, 236), (344, 394) }, Assert.Single(rig.Clicked).Points);

            // The client names whichever its select chose; the host says so if it is not the one asked for.
            rig.Client(Opcodes.CharacterEnterWorldRequest);
            rig.Server(Opcodes.CharacterEnterWorldServerReady);
            rig.Client(Opcodes.CharacterEnterWorld, CapturedEnterWorld);
            Assert.Contains(rig.Log.Lines, l => l.Contains("the client named 0x50000006, the character its character select had chosen, not Testchar II (0x50000007)"));
            Assert.Null(request.Succeeded);
        }

        /// <summary>
        /// Entering asked for while a logout is being finished - the server has the character out
        /// and the client is being watched, as Mag-Filter asks a second after the character list
        /// comes - waits for the logout to be done, then clicks; asked for before the server has
        /// answered, it is refused, as ever. A logout that does not finish takes the entering with it.
        /// </summary>
        [Fact]
        public void EnteringAskedForAsTheLogoutFinishesWaitsForIt()
        {
            const uint Second = 0x50000007;
            RelogRig rig = new RelogRig();
            rig.InTheWorld();
            SessionRequest logout = rig.Session.BeginLogOut(SessionRoute.Auto, out _);
            rig.Relay();

            Assert.Null(rig.Session.BeginEnterWorld(Second, SessionRoute.Auto, out string early));
            Assert.StartsWith("already busy: log out", early);

            rig.Server(Opcodes.CharacterLogOff);
            rig.Server(TwoCharacters(Second));
            SessionRequest entering = rig.Session.BeginEnterWorld(Second, SessionRoute.Auto, out string refusal);
            Assert.Null(refusal);
            Assert.Null(entering.Succeeded);
            Assert.Empty(rig.Clicked);
            Assert.Contains("; then enter the world as Testchar II by the character select's Enter: the logout to be done", rig.Session.Describe());
            Assert.Null(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out string another));
            Assert.EndsWith("; then enter the world as Testchar II", another);

            rig.Pass(rig.Session.ClientSettles);
            Assert.True(logout.Succeeded);
            Assert.Same(entering, rig.Session.Current);
            Assert.Equal(new[] { (122, 236), (344, 394) }, Assert.Single(rig.Clicked).Points);
            Assert.Contains(rig.Log.Lines, l => l.Contains("Entering the world as Testchar II (0x50000007) once logging Testchar I (0x50000006) out is done"));

            // A logout the session's end cuts short: the entering waiting for it ends too.
            RelogRig cut = new RelogRig();
            cut.InTheWorld();
            cut.Session.BeginLogOut(SessionRoute.Auto, out _);
            cut.Relay();
            cut.Server(Opcodes.CharacterLogOff);
            cut.Server(TwoCharacters(Second));
            SessionRequest waiting = cut.Session.BeginEnterWorld(Second, SessionRoute.Auto, out _);
            cut.Session.OnSessionEnded("the client closed it");
            Assert.False(waiting.Succeeded);
            Assert.StartsWith("Could not enter the world as Testchar II (0x50000007): the logout it waited for did not finish", waiting.Outcome);
            Assert.Empty(cut.Clicked);
        }

        private static WireWriter TwoCharacters(uint second)
            => new WireWriter(Opcodes.CharacterList).U32(0).U32(2)
                .U32(Testchar).String16L("Testchar I").U32(0)
                .U32(second).String16L("Testchar II").U32(0)
                .U32(0).U32(11).String16L(Account).U32(1).U32(1);

        /// <summary>
        /// The live failure: the server created the character, and the client sent LoginComplete -
        /// but its log still showed its character select. That is not arrival. The host says so,
        /// waits for the log to show the game, and when it does not, logs the character off again,
        /// so neither end is left with the character standing in the world unplayed.
        /// </summary>
        [Fact]
        public void AClientStillAtItsCharacterSelectHasNotArrivedAndIsLoggedOffAgain()
        {
            RelogRig rig = new RelogRig();
            rig.AtTheCharacterList();
            rig.Session.NoteClientLog("[2026.10.05-20.03.01:946][452]LogTemp: ACE UIFlow mode -> 3 layout=0x21000004");
            SessionRequest request = rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out _);
            rig.Client(Opcodes.CharacterEnterWorldRequest);
            rig.Server(Opcodes.CharacterEnterWorldServerReady);
            rig.Client(Opcodes.CharacterEnterWorld, CapturedEnterWorld);
            rig.ServerCreatesTheCharacter();

            // As live: "[ACE] LoginComplete (exited portal space)", and no UIFlow mode -> 6.
            rig.Client(RelogRig.LoginComplete(1));
            Assert.Null(request.Succeeded);
            Assert.Contains(rig.Log.Lines, l => l.Contains("the client sent LoginComplete, but its log shows its character select; waiting for it to show the game"));

            rig.Pass(rig.Session.ClientFinishes);

            Assert.False(request.Succeeded);
            Assert.Equal("Could not enter the world as Testchar I (0x50000006): the server created the character, but the client still shows its character select,"
                + " by its log, 30 s on. Logged the character off again, so both ends are back at the character list; enter in the game.", request.Outcome);
            Assert.Contains(rig.Log.Lines, l => l.StartsWith("WARN Could not enter the world as Testchar I", StringComparison.Ordinal));
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff }, rig.Relay().Select(m => m.Opcode));
        }

        /// <summary>Where the client's log is read, its showing the game - UIFlow mode 6 - is arrival, with the server's login before it.</summary>
        [Fact]
        public void TheClientsLogShowingTheGameIsArrival()
        {
            RelogRig rig = new RelogRig();
            rig.AtTheCharacterList();
            rig.Session.NoteClientLog("LogTemp: ACE UIFlow mode -> 3 layout=0x21000004");
            SessionRequest request = rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out _);
            rig.Client(Opcodes.CharacterEnterWorldRequest);
            rig.Session.NoteClientLog("[2026.10.05-19.26.54:300][157]LogTemp: ACE UIFlow mode -> 0 layout=0x00000000");
            rig.Server(Opcodes.CharacterEnterWorldServerReady);
            rig.Client(Opcodes.CharacterEnterWorld, CapturedEnterWorld);
            rig.ServerCreatesTheCharacter();
            rig.Client(RelogRig.LoginComplete(1));
            Assert.Null(request.Succeeded);

            rig.Pass(TimeSpan.FromSeconds(1));
            rig.Session.NoteClientLog("[2026.10.05-19.26.55:534][245]LogTemp: ACE UIFlow mode -> 6 layout=0x21000005");
            rig.Pass(TimeSpan.FromMilliseconds(100));

            Assert.True(request.Succeeded);
            Assert.Equal("Entered the world as Testchar I (0x50000006): the client shows the game, by its log.", request.Outcome);
        }

        /// <summary>Just after a logoff ACE can still be finishing with the character: said, with when to try again.</summary>
        [Fact]
        public void ACharacterStillInTheWorldIsSaidToBe()
        {
            RelogRig rig = new RelogRig();
            rig.AtTheCharacterList();
            SessionRequest request = rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out _);
            rig.Client(Opcodes.CharacterEnterWorldRequest);
            rig.Server(new WireWriter(Opcodes.CharacterError).U32(SessionControl.CharacterStillInWorld));

            Assert.False(request.Succeeded);
            Assert.Equal("Could not enter the world as Testchar I (0x50000006): the server refused it - a character on this account is still in the world;"
                + " the server may still be finishing with the last one: try again in a few seconds.", request.Outcome);
            Assert.Empty(rig.Relay());
            Assert.Single(rig.Clicked);
        }

        [Fact]
        public void EnteringIsRefusedForWhatCannotBeEntered()
        {
            RelogRig rig = new RelogRig();
            Assert.Null(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out string refusal));
            Assert.Contains("not at the character list", refusal);

            rig.AtTheCharacterList();
            Assert.Null(rig.Session.BeginEnterWorld(0x50000099, SessionRoute.Auto, out refusal));
            Assert.Equal("the account has no character 0x50000099", refusal);

            rig.Server(new WireWriter(Opcodes.CharacterList).U32(0).U32(1).U32(Testchar).String16L("Testchar I").U32(3600).U32(0).U32(11).String16L(Account).U32(1).U32(1));
            Assert.Null(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out refusal));
            Assert.Equal("Testchar I is being deleted", refusal);

            rig.AtTheCharacterList();
            rig.Session.NoteClientLog("[2026.09.30-03.57.00:129][785]LogTemp: ACE UIFlow mode -> 2 layout=0x21000001");
            Assert.Null(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out refusal));
            Assert.Contains("login screen", refusal);

            // The client still in the game, by its own log, has nowhere to click.
            rig.Session.NoteClientLog("LogTemp: ACE UIFlow mode -> 6 layout=0x21000005");
            Assert.Null(rig.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out refusal));
            Assert.Contains("the client still shows the game", refusal);

            Assert.Empty(rig.Relay());
            Assert.Empty(rig.Published);
            Assert.Empty(rig.Clicked);
        }

        // ------------------------------------------------------------------- the client's log

        /// <summary>The screens AC:Unreal's log names, as it wrote them at a logoff and a login.</summary>
        [Fact]
        public void TheClientsLogSaysWhichScreenItShows()
        {
            RelogRig rig = new RelogRig();
            List<ClientScreen> screens = new List<ClientScreen>();
            void Say(string line)
            {
                rig.Session.NoteClientLog(line);
                screens.Add(rig.Session.Screen);
            }

            Say("[2026.09.30-03.56.53:088][165]LogTemp: [ACE] CharacterLogOff (0xF653) sent");
            Say("[2026.09.30-03.56.59:096][455]LogTemp: [ACE] Logged off — returned to character select");
            Say("[2026.09.30-03.56.59:103][455]LogTemp: ACE UIFlow mode -> 2 layout=0x21000001");
            Say("[2026.09.30-03.56.59:112][455]LogTemp: ACE UIFlow mode -> 3 layout=0x21000004");
            Say("[2026.10.05-01.33.48:911][983]LogTemp: [ACE] EnterWorld request character=1342177286 — awaiting server ready");
            Say("[2026.10.05-01.33.48:912][983]LogTemp: ACE UIFlow mode -> 0 layout=0x00000000");
            Say("[2026.10.05-01.33.49:652][ 40]LogTemp: ACE UIFlow mode -> 6 layout=0x21000005");

            Assert.Equal(new[]
            {
                ClientScreen.Unknown,
                ClientScreen.CharacterSelect,
                ClientScreen.Login,
                ClientScreen.CharacterSelect,
                ClientScreen.CharacterSelect,
                ClientScreen.Changing,
                ClientScreen.InGame,
            }, screens);
        }

        [Fact]
        public void TheClientsLogIsReadAsItIsWrittenAndFromTheStartWhenItIsNew()
        {
            string path = Path.Combine(Path.GetTempPath(), "achost-clientlog-" + Guid.NewGuid().ToString("N") + ".log");
            List<string> handed = new List<string>();
            try
            {
                File.WriteAllText(path,
                    "LogInit: Build: ++UE5+Release-5.8\r\n"
                    + "[1]LogTemp: ACE UIFlow mode -> 2 layout=0x21000001\r\n"
                    + "[2]LogTemp: [ACE] Character list: 1 character(s)\r\n"
                    + "[3]LogTemp: ACE UIFlow mode -> 3 layout=0x21000004\r\n"
                    + "[4]LogTemp: ACE Sound: opcode guid=0x50000006 type=150 vol=1.00\r\n");

                ClientLog log = new ClientLog(() => path, handed.Add);

                // Found while the game runs: only where the client is now.
                log.Poll();
                Assert.Equal(new[] { "[3]LogTemp: ACE UIFlow mode -> 3 layout=0x21000004" }, handed);

                // Then what it writes, the lines worth handing on - a half-written one once whole.
                handed.Clear();
                File.AppendAllText(path, "[5]LogTemp: ACE Sound: x\r\n[6]LogTemp: [ACE] EnterWorld request character=1342177286 — awaiting server ready\r\n[7]LogTemp: ACE UIFlow mo");
                log.Poll();
                Assert.Equal(new[] { "[6]LogTemp: [ACE] EnterWorld request character=1342177286 — awaiting server ready" }, handed);
                File.AppendAllText(path, "de -> 0 layout=0x00000000\r\n");
                log.Poll();
                Assert.Equal("[7]LogTemp: ACE UIFlow mode -> 0 layout=0x00000000", handed.Last());

                // The client started again: a new log, shorter, read from its start.
                handed.Clear();
                File.WriteAllText(path, "[8]LogTemp: ACE UIFlow mode -> 2 layout=0x21000001\r\n");
                log.Poll();
                Assert.Equal(new[] { "[8]LogTemp: ACE UIFlow mode -> 2 layout=0x21000001" }, handed);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ------------------------------------------------------------------- plumbing

        [Fact]
        public void APressIsLetGoOfAndLeavesWhatIsHeldHeld()
        {
            List<int[]> published = new List<int[]>();
            GameInput keys = new GameInput(new ListLog()) { Publish = k => published.Add(k.ToArray()), Attached = () => true };

            Assert.True(keys.Hold(GameKey.Forward, true));
            Assert.True(keys.Press(0x11, 'Q'));
            Assert.Equal(new[] { (int)'W', 0x11, 'Q' }, published.Last());

            keys.Tick(TimeSpan.FromMilliseconds(100));
            Assert.Equal(new[] { 0x11, (int)'Q' }, keys.Pressed);
            keys.Tick(GameInput.PressLength);
            Assert.Equal(new[] { (int)'W' }, published.Last());
            Assert.Empty(keys.Pressed);

            keys.Attached = () => false;
            Assert.False(keys.Press(0x0D));
        }

        [Theory]
        [InlineData("Testchar I", Testchar)]
        [InlineData("testchar i", Testchar)]
        [InlineData("0x50000007", 0x50000007u)]
        [InlineData("1342177287", 0x50000007u)]
        [InlineData("2", 0x50000007u)]
        [InlineData("3", 0u)]
        [InlineData("Testchar", 0u)]
        [InlineData("", 0u)]
        public void ACharacterIsFoundByNameIdOrNumber(string text, uint expected)
        {
            AccountCharacter[] characters = { new AccountCharacter(Testchar, "Testchar I", 0), new AccountCharacter(0x50000007, "Testchar II", 0) };
            Assert.Equal(expected, AccountCharacter.Find(characters, text)?.Id ?? 0);
        }

        [Fact]
        public void TwoCharactersOfOneNameAreNeitherFound()
        {
            AccountCharacter[] characters = { new AccountCharacter(1, "Twin", 0), new AccountCharacter(2, "twin", 0) };
            Assert.Null(AccountCharacter.Find(characters, "Twin"));
        }

        /// <summary>"keys", "messages" or "auto" as the last word names the way; True when it did, or the line is empty.</summary>
        [Theory]
        [InlineData("", SessionRoute.Auto, "", true)]
        [InlineData("keys", SessionRoute.Keys, "", true)]
        [InlineData("messages", SessionRoute.Messages, "", true)]
        [InlineData("Testchar I", SessionRoute.Auto, "Testchar I", false)]
        [InlineData("Testchar I keys", SessionRoute.Keys, "Testchar I", true)]
        [InlineData("2 Messages", SessionRoute.Messages, "2", true)]
        public void TheWayIsTheLastWord(string line, SessionRoute route, string rest, bool named)
        {
            Assert.Equal(named, ControlPipe.TryReadRoute(line, out SessionRoute found, out string remainder));
            Assert.Equal(route, found);
            Assert.Equal(rest, remainder);
        }

        // ------------------------------------------------------------------- whole captures

        private static string FindCapture(string name)
        {
            for (DirectoryInfo folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            {
                string path = Path.Combine(folder.FullName, name);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        /// <summary>Every message either way in a capture, in order, with the datagram it finished in.</summary>
        private static IEnumerable<(int Index, PacketDirection Direction, AcMessage Message)> MessagesOf(string path)
        {
            MessageAssembler inbound = new MessageAssembler();
            MessageAssembler outbound = new MessageAssembler();
            int index = 0;
            foreach (CapturedDatagram datagram in CaptureReader.Read(path))
            {
                if (AcPacket.TryParse(datagram.Bytes, out AcPacket packet))
                {
                    MessageAssembler assembler = datagram.Direction == PacketDirection.Inbound ? inbound : outbound;
                    foreach (AcMessage message in assembler.Accept(packet))
                        yield return (index, datagram.Direction, message);
                }

                index++;
            }
        }

        /// <summary>
        /// In every capture, the character the client entered as is one of the character list's
        /// before it: what the host reads as the character entering, and checks against the one
        /// asked for.
        /// </summary>
        [SkippableTheory]
        [InlineData("session.acap")]
        [InlineData("session-readonly.acap")]
        [InlineData("session-20260929-1118.acap")]
        [InlineData("session-20260929-1208.acap")]
        [InlineData("session-20260929-1256.acap")]
        [InlineData("session-20260929-1558.acap")]
        [InlineData("session-20260929-1708.acap")]
        [InlineData("session-20260929-1934.acap")]
        public void EveryCapturedEntryNamesACharacterOfTheList(string name)
        {
            string path = FindCapture(name);
            Skip.If(path == null, $"{name} is not on this machine; captures are never committed.");

            WorldState world = new WorldState();
            int entries = 0;
            foreach (var (_, direction, message) in MessagesOf(path))
            {
                if (message.Opcode is Opcodes.CharacterList or Opcodes.CharacterEnterWorldRequest or Opcodes.CharacterEnterWorld)
                    MessageDecoder.Apply(message, direction, world);

                if (direction != PacketDirection.Outbound || message.Opcode != Opcodes.CharacterEnterWorld)
                    continue;

                uint id = BitConverter.ToUInt32(message.Payload.Span);
                Assert.Contains(world.AccountCharacters, c => c.Id == id);
                Assert.Equal(id, world.EnteringCharacterId);
                entries++;
            }

            Assert.True(entries > 0);
        }

        /// <summary>
        /// Every logoff the client asked for and the server answered, as the captures have them: the
        /// client's CharacterLogOff, empty, asked again two seconds apart; and the server's
        /// CharacterLogOff, the list and the name, together in one packet.
        /// </summary>
        [SkippableTheory]
        [InlineData("session.acap")]
        [InlineData("session-readonly.acap")]
        [InlineData("session-20260929-1934.acap")]
        public void EveryCapturedLogoffIsAskedAndAnsweredAlike(string name)
        {
            string path = FindCapture(name);
            Skip.If(path == null, $"{name} is not on this machine; captures are never committed.");

            List<(int Index, PacketDirection Direction, AcMessage Message)> messages = MessagesOf(path)
                .Where(m => m.Message.Opcode is Opcodes.CharacterLogOff or Opcodes.CharacterList or Opcodes.ServerName)
                .ToList();

            int answer = messages.FindIndex(m => m.Direction == PacketDirection.Inbound && m.Message.Opcode == Opcodes.CharacterLogOff);
            Assert.True(answer > 0);

            List<AcMessage> asked = messages.Take(answer).Where(m => m.Direction == PacketDirection.Outbound).Select(m => m.Message).ToList();
            Assert.InRange(asked.Count, 1, 5);
            Assert.All(asked, m => Assert.Equal((Opcodes.CharacterLogOff, 0), (m.Opcode, m.Payload.Length)));

            List<(int Index, PacketDirection Direction, AcMessage Message)> answered = messages.Skip(answer).Where(m => m.Direction == PacketDirection.Inbound).Take(3).ToList();
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff, Opcodes.CharacterList, Opcodes.ServerName }, answered.Select(m => m.Message.Opcode));
            Assert.Single(answered.Select(m => m.Index).Distinct());
            Assert.Empty(answered[0].Message.Payload.ToArray());
        }

        /// <summary>
        /// A whole captured session through the host, from its login to its logoff and the client
        /// closing it: what plugins hear, and where the session stood as they heard it.
        /// </summary>
        [SkippableFact]
        public async Task ACapturedSessionIsHeardAsALoginAndALogoff()
        {
            string path = FindCapture("session-20260929-1934.acap");
            Skip.If(path == null, "session-20260929-1934.acap is not on this machine; captures are never committed.");

            await using GameHost host = new GameHost(new CaptureTransport(path), new ListLog(), dataRoot: Path.Combine(Path.GetTempPath(), "achost-relog-tests"));
            PhasePlugin plugin = new PhasePlugin();
            host.AddPlugin(plugin);
            await host.StartAsync();
            await host.Ended.WaitAsync(TimeSpan.FromMinutes(1));

            // The player identified twice, as at every live login: by the character's description, a
            // game event naming it, and again by PlayerCreate.
            Assert.Equal(new[]
            {
                "server Example Server (CharacterList)",
                "player 50000006 (InWorld)",
                "player 50000006 (InWorld)",
                "logging off Testchar I (LoggingOff)",
                "logged off Testchar I (CharacterList)",
                "server Example Server (CharacterList)",
            }, plugin.Heard);
            Assert.Equal(SessionPhase.None, host.World.Phase);
        }

        /// <summary>What a plugin hears of the session, and where it stood each time.</summary>
        private sealed class PhasePlugin : IPlugin
        {
            public List<string> Heard { get; } = new List<string>();

            public string Name => "Phases";

            public void Startup(IHost host)
            {
                host.ServerConnected += (_, name) => Heard.Add($"server {name} ({host.World.Phase})");
                host.PlayerIdentified += (_, id) => Heard.Add($"player {id:X8} ({host.World.Phase})");
                host.LoggingOff += (_, _) => Heard.Add($"logging off {host.Character.Name} ({host.World.Phase})");
                host.LoggedOff += (_, _) => Heard.Add($"logged off {host.Character.Name} ({host.World.Phase})");
            }

            public void Shutdown()
            {
            }
        }

        // ------------------------------------------------------------------- handed over

        /// <summary>
        /// A host started at the character list - the one before it stopped after a logout - knows
        /// the account and its characters from what was handed over, and can enter the world.
        /// </summary>
        [Fact]
        public async Task AHostCarryingOnAtTheCharacterListCanEnterTheWorld()
        {
            StandIn before = new StandIn();
            HandoverSnapshot handover;
            await using (GameHost first = new GameHost(before, new ListLog(), dataRoot: NewRoot()))
            {
                first.ActionsAllowed = true;
                await first.StartAsync();
                before.Starts(SessionStart.Login);
                before.LogsIn();
                Assert.NotNull(await OnGameThreadAsync(first, () => first.Session.BeginLogOut(SessionRoute.Messages, out _)));
                Assert.Equal(SessionPhase.CharacterList, await OnGameThreadAsync(first, () => first.World.Phase));
                handover = ThroughBytes(await OnGameThreadAsync(first, () => first.CreateHandover(DateTimeOffset.UtcNow)));
            }

            StandIn after = new StandIn { Answers = false };
            await using GameHost second = new GameHost(after, new ListLog(), dataRoot: NewRoot());
            second.OfferHandover(handover);
            await second.StartAsync();
            after.Starts(SessionStart.UnderWay);

            Assert.Equal(SessionPhase.CharacterList, await OnGameThreadAsync(second, () => second.World.Phase));
            Assert.Equal(Account, second.World.AccountName);
            Assert.Equal("Testchar I", Assert.Single(second.World.AccountCharacters).Name);
            Assert.Equal("Example Server", second.World.ServerName);
            Assert.True(second.ActionsAllowed);

            // With an overlay to click the character select, in it goes; the client asks itself.
            List<GameClick> clicked = new List<GameClick>();
            second.InputKeys.Publish = _ => { };
            second.InputKeys.PublishClick = clicked.Add;
            second.InputKeys.Attached = () => true;
            Assert.NotNull(await OnGameThreadAsync(second, () => second.Session.BeginEnterWorld(Testchar, SessionRoute.Auto, out _)));
            Assert.Equal(new[] { (122, 220), (344, 394) }, Assert.Single(clicked).Points);
            Assert.Empty(after.Sent);
        }

        /// <summary>
        /// Handed over between the client asking to log off and the server agreeing: the next host
        /// knows the character is leaving, says nothing of it again, and hears it leave.
        /// </summary>
        [Fact]
        public async Task AHostCarryingOnMidLogoffHearsTheCharacterLeave()
        {
            StandIn before = new StandIn();
            HandoverSnapshot handover;
            await using (GameHost first = new GameHost(before, new ListLog(), dataRoot: NewRoot()))
            {
                await first.StartAsync();
                before.Starts(SessionStart.Login);
                before.LogsIn();
                before.FromClient(AcMessage.Create(Opcodes.CharacterLogOff, Array.Empty<byte>()));
                Assert.Equal(SessionPhase.LoggingOff, await OnGameThreadAsync(first, () => first.World.Phase));
                handover = ThroughBytes(await OnGameThreadAsync(first, () => first.CreateHandover(DateTimeOffset.UtcNow)));
            }

            StandIn after = new StandIn();
            await using GameHost second = new GameHost(after, new ListLog(), dataRoot: NewRoot());
            PhasePlugin plugin = new PhasePlugin();
            second.AddPlugin(plugin);
            second.OfferHandover(handover);
            await second.StartAsync();
            after.Starts(SessionStart.UnderWay);

            Assert.Equal(SessionPhase.LoggingOff, await OnGameThreadAsync(second, () => second.World.Phase));
            after.ServerLogsOff();
            Assert.Equal(SessionPhase.CharacterList, await OnGameThreadAsync(second, () => second.World.Phase));

            Assert.Equal(new[]
            {
                "server Example Server (LoggingOff)",
                "player 50000006 (LoggingOff)",
                "logged off Testchar I (CharacterList)",
                "server Example Server (CharacterList)",
            }, plugin.Heard);
        }

        private static string NewRoot() => Path.Combine(Path.GetTempPath(), "achost-relog-" + Guid.NewGuid().ToString("N"));

        private static HandoverSnapshot ThroughBytes(HandoverSnapshot snapshot)
        {
            using MemoryStream stream = new MemoryStream();
            snapshot.Write(stream);
            stream.Position = 0;
            return HandoverSnapshot.Read(stream);
        }

        private static Task<T> OnGameThreadAsync<T>(GameHost host, Func<T> work)
        {
            TaskCompletionSource<T> done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() =>
            {
                try
                {
                    done.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            });

            return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// A relay stand-in: what the host sends rides out and is seen going, and the game answers as
        /// ACE and AC:Unreal did in the captures - the logoff with the list and the name, a request to
        /// enter with the ready, the character named with its login, which the client finishes.
        /// </summary>
        internal sealed class StandIn : IGameTransport, ISessionStarts, IClientboundFilter
        {
            public List<AcMessage> Sent { get; } = new List<AcMessage>();

            /// <summary>The server's messages that reached the client: those not kept from it.</summary>
            public List<uint> ClientSaw { get; } = new List<uint>();

            public string Description => "relog stand-in";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public event EventHandler<SessionStart> SessionStarted;

            public bool CanSend => true;

            public bool CanShowInGame => true;

            public Func<uint, ReadOnlyMemory<byte>, bool> WithholdFromClient { get; set; }

            public bool CanWithholdFromClient => true;

            public int MessagesWithheldFromClient { get; private set; }

            public int SplitMessagesWithheldFromClient => 0;

            /// <summary>Whether the game answers what the host sends, as ACE and the client would.</summary>
            public bool Answers { get; set; } = true;

            public void Starts(SessionStart start) => SessionStarted?.Invoke(this, start);

            public void FromServer(AcMessage message)
            {
                if (WithholdFromClient?.Invoke(message.Opcode, message.Payload) == true)
                    MessagesWithheldFromClient++;
                else
                    ClientSaw.Add(message.Opcode);

                MessageReceived?.Invoke(this, new GameMessageEventArgs(PacketDirection.Inbound, message));
            }

            public void FromClient(AcMessage message)
                => MessageReceived?.Invoke(this, new GameMessageEventArgs(PacketDirection.Outbound, message));

            public void LogsIn()
            {
                FromServer(AcMessage.Create(Opcodes.CharacterList, Hex(CapturedCharacterList)));
                FromServer(AcMessage.Create(Opcodes.ServerName, Hex(CapturedServerName)));
                ClientEnters();
            }

            /// <summary>The client entering from its character select, as on its own Enter: its request, the ready, its naming, the login.</summary>
            public void ClientEnters()
            {
                FromClient(AcMessage.Create(Opcodes.CharacterEnterWorldRequest, Array.Empty<byte>()));
                FromServer(AcMessage.Create(Opcodes.CharacterEnterWorldServerReady, Array.Empty<byte>()));
                FromClient(AcMessage.Create(Opcodes.CharacterEnterWorld, Hex(CapturedEnterWorld)));
                Arrives();
            }

            public void ServerLogsOff()
            {
                FromServer(AcMessage.Create(Opcodes.CharacterLogOff, Array.Empty<byte>()));
                FromServer(AcMessage.Create(Opcodes.CharacterList, Hex(CapturedCharacterList)));
                FromServer(AcMessage.Create(Opcodes.ServerName, Hex(CapturedServerName)));
            }

            private void Arrives()
            {
                FromServer(new WireWriter(Opcodes.PlayerCreate).U32(Testchar).ToMessage());
                FromServer(WireWriter.ObjectCreate(Testchar, "Testchar I", 1, ItemTypes.Creature, 0).ToMessage());
                FromClient(RelogRig.LoginComplete(1).ToMessage());
            }

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default)
            {
                lock (Sent)
                    Sent.Add(message);

                // Out in the client's next packet, and seen going by.
                FromClient(message);

                if (!Answers)
                    return Task.CompletedTask;

                switch (message.Opcode)
                {
                    case Opcodes.CharacterLogOff:
                        ServerLogsOff();
                        break;
                    case Opcodes.CharacterEnterWorldRequest:
                        FromServer(AcMessage.Create(Opcodes.CharacterEnterWorldServerReady, Array.Empty<byte>()));
                        break;
                    case Opcodes.CharacterEnterWorld:
                        Arrives();
                        break;
                }

                return Task.CompletedTask;
            }

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                Ended?.Invoke(this, EventArgs.Empty);
                return ValueTask.CompletedTask;
            }
        }

        // ------------------------------------------------------------------- achost ctl

        /// <summary>
        /// The verbs testers use, through the control pipe of a whole host: the characters, a logout
        /// and a login waited for and answered with how they ended, and what status then says.
        /// </summary>
        [Fact]
        public async Task CtlLogsOutAndInAndSaysHowItWent()
        {
            string root = NewRoot();
            StandIn game = new StandIn();
            HostRuntimeOptions options = new HostRuntimeOptions
            {
                Transport = game,
                PluginDirectory = Path.Combine(root, "plugins"),
                DataDirectory = Path.Combine(root, "data"),
                NoDat = true,
                NoPlugins = true,
                EnableActions = true,
                ControlPipeName = "achost-test-relog-" + Guid.NewGuid().ToString("N"),
                OverlayPipeName = "achost-test-relog-overlay-" + Guid.NewGuid().ToString("N"),
            };

            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            Assert.Null(runtime.ClientLog);
            runtime.Host.Session.ClientSettles = TimeSpan.FromMilliseconds(300);
            await runtime.StartAsync();
            game.LogsIn();

            string pipe = options.ControlPipeName;
            Assert.Matches(@"1  0x50000006  Testchar I\s+in the world", await ControlPipe.SendAsync("characters", pipe));
            Assert.Contains("phase      in the world as Testchar I", await ControlPipe.SendAsync("status", pipe));

            Assert.Equal("logout takes nothing, or keys or messages", await ControlPipe.SendAsync("logout now", pipe));

            // By keys, as asked for live: no overlay is needed, since the message goes all the same.
            string loggedOut = await ControlPipe.SendAsync("logout keys", pipe);
            Assert.StartsWith("Logged Testchar I (0x50000006) out to the character list", loggedOut);
            string status = await ControlPipe.SendAsync("status", pipe);
            Assert.Contains("phase      at the character list (account testacct, 1 character(s))", status);
            Assert.Contains("relog      Logged Testchar I (0x50000006) out to the character list", status);
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff }, game.Sent.Select(m => m.Opcode));

            // Entering needs the overlay, to click the character select.
            Assert.Equal("no one character is \"Nobody\"; `characters` lists them", await ControlPipe.SendAsync("login Nobody", pipe));
            Assert.Equal("cannot enter the world: AC:Unreal enters the world only from its own character select, whose Enter the overlay clicks, and the overlay is not attached",
                await ControlPipe.SendAsync("login Testchar I messages", pipe));

            // An overlay that clicks, and a client that enters on the click as AC:Unreal does.
            runtime.Host.InputKeys.Publish = _ => { };
            runtime.Host.InputKeys.Attached = () => true;
            runtime.Host.InputKeys.PublishClick = _ => game.ClientEnters();
            Assert.Equal("Entered the world as Testchar I (0x50000006): the client has finished arriving (its LoginComplete; its own log is not read here).",
                await ControlPipe.SendAsync("login Testchar I", pipe));
            Assert.Equal(0, game.MessagesWithheldFromClient);
            Assert.Equal(new uint[] { Opcodes.CharacterLogOff }, game.Sent.Select(m => m.Opcode));
            Assert.Contains("phase      in the world as Testchar I", await ControlPipe.SendAsync("status", pipe));

            Assert.Equal("acting off", (await ControlPipe.SendAsync("act off", pipe)).Trim());
            Assert.StartsWith("cannot log out: plugins may not act", await ControlPipe.SendAsync("logout", pipe));
        }
    }

    /// <summary>Decal's own: Hooks.Logout, and the Logoff a logout raises, Requested then Authorized.</summary>
    [Collection(DecalCollection.Name)]
    public class RelogDecalTests
    {
        [Fact]
        public void DecalsLogoutLogsOutAndItsLogoffIsRequestedThenAuthorized()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            runtime.CompleteStartup();
            List<string> heard = new List<string>();
            runtime.Core.CharacterFilter.Logoff += (_, e) => heard.Add($"logoff {e.Type} {runtime.Core.CharacterFilter.Name}");

            runtime.Core.Actions.Logout();
            Assert.Equal(new[] { "log out" }, host.Actions.Calls);

            // What then goes by: the client's request, asked again two seconds on, and the server's answer.
            AcMessage logOff = AcMessage.Create(Opcodes.CharacterLogOff, Array.Empty<byte>());
            MessageDecoder.Apply(logOff, PacketDirection.Outbound, host.WorldState);
            MessageDecoder.Apply(logOff, PacketDirection.Outbound, host.WorldState);
            MessageDecoder.Apply(logOff, PacketDirection.Inbound, host.WorldState);
            MessageDecoder.Apply(AcMessage.Create(Opcodes.CharacterList, Convert.FromHexString(RelogTests.CapturedCharacterList)), PacketDirection.Inbound, host.WorldState);

            Assert.Equal(new[] { "logoff Requested Tester", "logoff Authorized Tester" }, heard);

            // At the character list Decal's filter still answers for the account.
            Assert.Equal(RelogTests.Account, runtime.Core.CharacterFilter.AccountName);
            Assert.Equal(new[] { "Testchar I" }, runtime.Core.CharacterFilter.Characters.Select(c => c.Name));
            Assert.Equal(0, runtime.Core.CharacterFilter.LoginStatus);
        }
    }

    /// <summary>
    /// A session control on its own, over a world the test feeds: the server's messages and the
    /// client's, what the host sends taken as having gone out, keys recorded, and time that moves
    /// only when the test passes it.
    /// </summary>
    internal sealed class RelogRig
    {
        public RelogRig()
        {
            World = new WorldState(() => Now);
            Actions = new ClientActions(Transport, Log, World) { Allowed = () => ActingAllowed };
            Keys = new GameInput(Log)
            {
                Publish = keys => Published.Add(keys.ToArray()),
                PublishClick = Clicked.Add,
                Attached = () => OverlayAttached,
                Allowed = () => ActingAllowed,
            };
            Session = new SessionControl(Actions, Transport, World, Log, () => Keys, () => Window, () => UiScale);
            Actions.Session = Session;
            World.LoggingOff += (_, _) => Heard.Add("logging off");
            World.LoggedOff += (_, why) => Heard.Add("logged off: " + why);
            World.PlayerIdentified += (_, id) => Heard.Add($"player {id:X8}");
        }

        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public WorldState World { get; }

        public RigTransport Transport { get; } = new RigTransport();

        public ClientActions Actions { get; }

        public GameInput Keys { get; }

        /// <summary>Every set of keys the overlay was told to hold, in order.</summary>
        public List<int[]> Published { get; } = new List<int[]>();

        /// <summary>Every click the overlay was asked to make, in order.</summary>
        public List<GameClick> Clicked { get; } = new List<GameClick>();

        public bool OverlayAttached { get; set; } = true;

        public bool ActingAllowed { get; set; } = true;

        /// <summary>What the overlay says of the game's window; not known unless a test says.</summary>
        public GameWindowState Window { get; set; } = GameWindowState.Unknown;

        /// <summary>The client's Desktop UI Scale as its settings would say; 100% unless a test says.</summary>
        public double UiScale { get; set; } = 1.0;

        public SessionControl Session { get; }

        public ListLog Log { get; } = new ListLog();

        /// <summary>What the world told of the session: a character arriving, a logoff asked for, one done.</summary>
        public List<string> Heard { get; } = new List<string>();

        /// <summary>The server's messages that reached the client: every one - nothing of a relog is kept from it.</summary>
        public List<uint> ClientSaw { get; } = new List<uint>();

        public void Server(AcMessage message)
        {
            ClientSaw.Add(message.Opcode);
            See(PacketDirection.Inbound, message);
        }

        public void Server(uint opcode, string hex = "") => Server(AcMessage.Create(opcode, Convert.FromHexString(hex)));

        public void Server(WireWriter message) => Server(message.ToMessage());

        public void Client(uint opcode, string hex = "") => See(PacketDirection.Outbound, AcMessage.Create(opcode, Convert.FromHexString(hex)));

        public void Client(WireWriter message) => See(PacketDirection.Outbound, message.ToMessage());

        /// <summary>What the host sent goes out in the client's next packet, and is seen going by. Returns it.</summary>
        public List<AcMessage> Relay()
        {
            List<AcMessage> sent = Transport.TakeSent();
            foreach (AcMessage message in sent)
                See(PacketDirection.Outbound, message);
            return sent;
        }

        private void See(PacketDirection direction, AcMessage message)
        {
            MessageDecoder.Apply(message, direction, World);
            Session.OnMessage(direction, message);
        }

        /// <summary>Time passing a tick at a time, as the host's tick passes it.</summary>
        public void Pass(TimeSpan time)
        {
            TimeSpan step = TimeSpan.FromMilliseconds(100);
            for (TimeSpan passed = TimeSpan.Zero; passed < time; passed += step)
            {
                Now += step;
                Keys.Tick(step);
                Session.Tick();
            }
        }

        public void AtTheCharacterList()
        {
            Server(Opcodes.CharacterList, RelogTests.CapturedCharacterList);
            Server(Opcodes.ServerName, RelogTests.CapturedServerName);
        }

        /// <summary>A login as captured: the list, the client entering as Testchar I, the server creating it, the client arriving.</summary>
        public void InTheWorld()
        {
            AtTheCharacterList();
            Client(Opcodes.CharacterEnterWorldRequest);
            Server(Opcodes.CharacterEnterWorldServerReady);
            Client(Opcodes.CharacterEnterWorld, RelogTests.CapturedEnterWorld);
            ServerCreatesTheCharacter();
            Client(LoginComplete(1));
            Heard.Clear();
            Heard.Add($"player {RelogTests.Testchar:X8}");
        }

        public void ServerCreatesTheCharacter()
        {
            Server(new WireWriter(Opcodes.PlayerCreate).U32(RelogTests.Testchar));
            Server(WireWriter.ObjectCreate(RelogTests.Testchar, "Testchar I", 1, ItemTypes.Creature, 0));
        }

        /// <summary>ACE's answer to a logoff, as captured: CharacterLogOff, the list, the name.</summary>
        public void ServerLogsOff()
        {
            Server(Opcodes.CharacterLogOff);
            Server(Opcodes.CharacterList, RelogTests.CapturedCharacterList);
            Server(Opcodes.ServerName, RelogTests.CapturedServerName);
        }

        /// <summary>The client's word that it has arrived, as captured: GameAction LoginComplete (0x00A1).</summary>
        public static WireWriter LoginComplete(uint sequence) => new WireWriter(Opcodes.GameAction).U32(sequence).U32(GameActions.LoginComplete);
    }

    /// <summary>
    /// A transport that keeps what is sent - handing it to a real rewriter too, when given one - and
    /// what is shown to the client.
    /// </summary>
    internal sealed class RigTransport : IGameTransport
    {
        private readonly List<AcMessage> _sent = new List<AcMessage>();

        public List<AcMessage> Shown { get; } = new List<AcMessage>();

        /// <summary>When set, what is sent is queued here as the relay queues it.</summary>
        public ClientStreamRewriter Rewriter { get; set; }

        public string Description => "relog rig";

        public event EventHandler<GameMessageEventArgs> MessageReceived { add { } remove { } }

        public event EventHandler Ended { add { } remove { } }

        public bool CanSend => true;

        public bool CanShowInGame => true;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default)
        {
            Rewriter?.Enqueue(message.Opcode, message.Payload);
            _sent.Add(message);
            return Task.CompletedTask;
        }

        public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default)
        {
            Shown.Add(message);
            return Task.CompletedTask;
        }

        public List<AcMessage> TakeSent()
        {
            List<AcMessage> sent = new List<AcMessage>(_sent);
            _sent.Clear();
            return sent;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
