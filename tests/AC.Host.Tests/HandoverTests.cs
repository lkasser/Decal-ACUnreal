using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Handover;
using AC.Host.Plugins;
using AC.Host.Runtime;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using AC.Proxy;
using Decal.Adapter.Hosting;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// A host stopped and started again while the game stays connected - the Decal Agent updated
    /// with the player in the world. The one stopping hands the session over; the one starting
    /// rebuilds the world from it and tells plugins what a login would have; and one that starts
    /// with nothing handed over knows it, and asks the player to log in again.
    /// </summary>
    /// <remarks>
    /// In Decal's collection, since one of these runs a Decal and Decal is one per process.
    /// </remarks>
    [Collection(DecalCollection.Name)]
    public class HandoverTests
    {
        private const uint Player = 0x50000006;
        private const uint Pack = 0x80000202;
        private const uint Gem = 0x80000203;
        private const uint Sword = 0x80000201;
        private const uint Drudge = 0x80000301;

        /// <summary>A relay stand-in the test drives: how the traffic began, what each end says, and what was shown.</summary>
        private sealed class RelayStandIn : IGameTransport, ISessionStarts, ISessionBoundaries
        {
            public List<AcMessage> Shown { get; } = new List<AcMessage>();

            public string Description => "handover test relay";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public event EventHandler<SessionEnd> SessionEnded;

            public event EventHandler<SessionStart> SessionStarted;

            public bool CanSend => true;

            public bool CanShowInGame => true;

            public void Starts(SessionStart start) => SessionStarted?.Invoke(this, start);

            public void FromServer(WireWriter message)
                => MessageReceived?.Invoke(this, new GameMessageEventArgs(PacketDirection.Inbound, message.ToMessage()));

            public void FromServer(AcMessage message)
                => MessageReceived?.Invoke(this, new GameMessageEventArgs(PacketDirection.Inbound, message));

            public void FromClient(WireWriter message)
                => MessageReceived?.Invoke(this, new GameMessageEventArgs(PacketDirection.Outbound, message.ToMessage()));

            public void Closes() => SessionEnded?.Invoke(this, SessionEnd.ClientClosed);

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default)
            {
                lock (Shown)
                    Shown.Add(message);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                Ended?.Invoke(this, EventArgs.Empty);
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>Writes down every login-shaped thing the host tells it, in order.</summary>
        private sealed class EventRecorder : IPlugin
        {
            private IHost _host;

            public List<string> Heard { get; } = new List<string>();

            public string Name => "EventRecorder";

            public void Startup(IHost host)
            {
                _host = host;
                host.ServerConnected += (_, name) => Heard.Add("server " + name);
                host.PlayerIdentified += (_, id) => Heard.Add($"player {id:X8} {(host.Character.Object == null ? "undescribed" : "described")}");
                host.ObjectCreated += (_, obj) => Heard.Add($"created {obj.Id:X8}");
                host.ObjectUpdated += (_, obj) => Heard.Add($"updated {obj.Id:X8}");
                host.EnchantmentChanged += (_, e) => Heard.Add("enchantment");
                host.CharacterUpdated += (_, _) => Heard.Add("character");
                host.ChatReceived += (_, m) => Heard.Add("chat " + m.Text);
                host.LoggedOff += (_, why) => Heard.Add("logged off");
                host.MessageSeen += (_, e) => Heard.Add($"message {e.Message.Opcode:X4}");
            }

            public void Shutdown()
            {
            }
        }

        private static string NewRoot() => Path.Combine(Path.GetTempPath(), "achost-handover-" + Guid.NewGuid().ToString("N"));

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

        private static Task DrainAsync(GameHost host) => OnGameThreadAsync(host, () => 0);

        /// <summary>A login wearing the player's own buffs, captured whole: its PlayerDescription.</summary>
        private static AcMessage SelfBuffedDescription()
        {
            using Stream stream = typeof(HandoverTests).Assembly.GetManifestResourceStream("AC.Host.Tests.Resources.login-self-buffed.hex");
            using StreamReader reader = new StreamReader(stream);
            return AcMessage.Create(Opcodes.GameEvent, Convert.FromHexString(reader.ReadToEnd().Trim()));
        }

        private static WireWriter Position(uint id, float x)
            => new WireWriter(Opcodes.UpdatePosition).U32(id)
                .U32(0x08 | 0x10 | 0x20)
                .U32(0xA9B40019).F32(x).F32(20).F32(30)
                .F32(0.7f)
                .U16(1).U16(2).U16(3).U16(4);

        /// <summary>What the server says at a login, as ACE says it, with a word of chat and a step or two after.</summary>
        private static IEnumerable<AcMessage> Login()
        {
            yield return new WireWriter(Opcodes.ServerName).I32(1).I32(128).String16L("Example Server").ToMessage();
            yield return new WireWriter(Opcodes.PlayerCreate).U32(Player).ToMessage();
            yield return WireWriter.ObjectCreate(Player, "Testchar I", 1, ItemTypes.Creature, 0).ToMessage();
            yield return SelfBuffedDescription();
            yield return WireWriter.ObjectCreate(Pack, "Pack", 136, ItemTypes.Container, 0, containerId: Player, itemCapacity: 24, withLocation: false).ToMessage();
            yield return WireWriter.ObjectCreate(Gem, "Diamond", 2410, ItemTypes.Gem, 0, containerId: Pack, withLocation: false).ToMessage();
            yield return WireWriter.ObjectCreate(Sword, "Frost Dolabra", 3010, ItemTypes.MeleeWeapon, 0, wielderId: Player, currentlyWielded: 0x100000, withLocation: false).ToMessage();
            yield return WireWriter.ObjectCreate(Drudge, "Drudge Skulker", 940, ItemTypes.Creature, 0).ToMessage();
            yield return new WireWriter(Opcodes.ServerMessage).String16L("Welcome to Asheron's Call").I32(0).ToMessage();
            yield return Position(Drudge, 10).ToMessage();
            yield return Position(Drudge, 11).ToMessage();
        }

        /// <summary>
        /// Runs a host over <paramref name="messages"/> from a login, then the client's word that it
        /// has finished entering the world, as it says once the login's messages are in; and takes
        /// what it would hand over - written out and read back, as the file carries it.
        /// </summary>
        private static async Task<HandoverSnapshot> HandOverAfterAsync(IEnumerable<AcMessage> messages, bool acting = true)
        {
            RelayStandIn transport = new RelayStandIn();
            await using GameHost before = new GameHost(transport, new ListLog(), dataRoot: NewRoot());
            before.ActionsAllowed = acting;
            await before.StartAsync();

            transport.Starts(SessionStart.Login);
            foreach (AcMessage message in messages)
                transport.FromServer(message);
            transport.FromClient(MacroTestHost.ClientEntered());

            HandoverSnapshot snapshot = await OnGameThreadAsync(before, () => before.CreateHandover(DateTimeOffset.UtcNow));
            return ThroughBytes(snapshot);
        }

        private static HandoverSnapshot ThroughBytes(HandoverSnapshot snapshot)
        {
            using MemoryStream stream = new MemoryStream();
            snapshot.Write(stream);
            stream.Position = 0;
            return HandoverSnapshot.Read(stream);
        }

        // ------------------------------------------------------------------- carrying on

        [Fact]
        public async Task CarryingTheSessionOnTellsPluginsWhatALoginTellsThemInALoginsOrder()
        {
            HandoverSnapshot handover = await HandOverAfterAsync(Login());

            RelayStandIn transport = new RelayStandIn();
            ListLog log = new ListLog();
            await using GameHost host = new GameHost(transport, log, dataRoot: NewRoot());
            host.ActionsAllowed = false;
            EventRecorder recorder = new EventRecorder();
            host.AddPlugin(recorder);

            // Decal, as DecalCompat starts it: before the first traffic, with an empty world.
            using DecalRuntime decal = new DecalRuntime(host);
            List<string> decalHeard = new List<string>();
            decal.Core.CharacterFilter.Login += (_, e) => decalHeard.Add($"login {e.Id:X8}");
            decal.Core.CharacterFilter.LoginComplete += (_, _) => decalHeard.Add("complete");
            decal.Core.WorldFilter.CreateObject += (_, e) => decalHeard.Add($"created {e.New.Id:X8}");
            decal.Core.EchoFilter.ServerDispatch += (_, e) => decalHeard.Add($"dispatch {e.Message.Type:X4}");

            // Virindi Reporter's luminance: its own, from the description it hears, counted from
            // what it had at LoginComplete.
            long luminance = 0;
            long luminanceAtStart = -1;
            decal.Core.MessageProcessed += (_, e) =>
            {
                if (e.Message.Type != Opcodes.GameEvent || e.Message.Value<int>("event") != GameEvents.PlayerDescription)
                    return;

                global::Decal.Adapter.MessageStruct qwords = e.Message.Struct("properties").Struct("qwords");
                for (int i = 0; i < qwords.Count; i++)
                {
                    if (qwords.Struct(i).Value<int>("key") == 6)
                        luminance = qwords.Struct(i).Value<long>("value");
                }
            };
            decal.Core.CharacterFilter.LoginComplete += (_, _) => luminanceAtStart = luminance;

            host.OfferHandover(handover);
            await host.StartAsync();

            // The game was connected all along: the first packet is the session going on, and the
            // first message after it a drudge taking another step.
            transport.Starts(SessionStart.UnderWay);
            transport.FromServer(Position(Drudge, 12));
            await DrainAsync(host);

            List<string> heard = recorder.Heard;
            int enchantments = host.Character.Enchantments.Count;
            Assert.True(enchantments > 0);

            // The server, then the character - named before it is described, as at a login - then
            // the character's own object, what it carries and wears, what is around it; its
            // enchantments; the messages the world was built from, as a login brings them, but for
            // the server's word of welcome, and the client's word that it had entered the world;
            // and the character as a whole. Then the session goes on.
            string[] builtFrom =
            {
                $"message {Opcodes.ServerName:X4}",
                $"message {Opcodes.PlayerCreate:X4}",
                $"message {Opcodes.ObjectCreate:X4}",
                $"message {Opcodes.GameEvent:X4}",
                $"message {Opcodes.ObjectCreate:X4}",
                $"message {Opcodes.ObjectCreate:X4}",
                $"message {Opcodes.ObjectCreate:X4}",
                $"message {Opcodes.ObjectCreate:X4}",
                $"message {Opcodes.UpdatePosition:X4}",
            };
            List<string> expected = new List<string>
            {
                "server Example Server",
                $"player {Player:X8} undescribed",
                $"created {Player:X8}",
                $"created {Pack:X8}",
                $"created {Gem:X8}",
                $"created {Sword:X8}",
                $"created {Drudge:X8}",
            };
            expected.AddRange(Enumerable.Repeat("enchantment", enchantments));
            expected.AddRange(builtFrom);
            expected.Add($"message {Opcodes.GameAction:X4}");
            expected.Add("character");
            expected.Add($"updated {Drudge:X8}");
            expected.Add($"message {Opcodes.UpdatePosition:X4}");
            Assert.Equal(expected, heard);

            // Nothing that happened before is told again as happening now: no chat.
            Assert.DoesNotContain(heard, h => h.StartsWith("chat", StringComparison.Ordinal));
            Assert.DoesNotContain($"message {Opcodes.ServerMessage:X4}", heard);

            // Decal's plugins hear a login: Login, every object, the login's messages through their
            // ServerDispatch, then LoginComplete on the client's word, retold after them as it came.
            Assert.Equal($"login {Player:X8}", decalHeard.First());
            Assert.Equal(5, decalHeard.Count(h => h.StartsWith("created", StringComparison.Ordinal)));
            int complete = decalHeard.IndexOf("complete");
            Assert.Equal(
                builtFrom.Select(m => m.Replace("message ", "dispatch ", StringComparison.Ordinal)),
                decalHeard.Take(complete).Where(h => h.StartsWith("dispatch", StringComparison.Ordinal)));
            Assert.Equal(new[] { "complete", $"dispatch {Opcodes.UpdatePosition:X4}" }, decalHeard.Skip(complete));
            Assert.Equal(36_200, luminanceAtStart);

            // The world is the one the host before had, and goes on.
            Assert.Equal("Example Server", host.World.ServerName);
            Assert.Equal("Testchar I", host.Character.Name);
            Assert.Equal(Pack, host.World.Get(Gem).ContainerId);
            Assert.Equal(Player, host.World.Get(Sword).WielderId);
            Assert.Equal(12f, host.World.Get(Drudge).Location.Value.X);
            Assert.NotEmpty(host.Character.Spellbook);
            Assert.Equal(handover.WrittenAt, host.CarriedOnFrom);

            // Decal's character filter answers from that world: Reporter's experience is the character's.
            Assert.Equal(191_226_310_247L, decal.Core.CharacterFilter.TotalXP);
            Assert.Equal("Example Server", decal.Core.CharacterFilter.Server);
            Assert.Equal(1, decal.Core.CharacterFilter.ServerPopulation);
            Assert.False(host.JoinedMidSession);

            // Acting as the player left it, and the log saying what happened.
            Assert.True(host.ActionsAllowed);
            lock (log.Lines)
                Assert.Contains(log.Lines, l => l.StartsWith("INFO Carried on the session", StringComparison.Ordinal) && l.Contains("Testchar I in the world"));
        }

        /// <summary>
        /// Keeps its own track of the world from the messages alone, as Virindi Global Inventory,
        /// Item Tool and Virindi HUDs do: whose character it is, every object created and not
        /// since deleted, what the client sent - its word that it is in, above all - and what was
        /// said to it.
        /// </summary>
        private sealed class MessageTracker
        {
            public MessageTracker(DecalRuntime decal)
            {
                decal.Core.EchoFilter.ServerDispatch += (_, e) =>
                {
                    switch (e.Message.Type)
                    {
                        case 0xF745:
                            Known.Add(e.Message.Value<int>("object"));
                            break;
                        case 0xF747:
                            Known.Remove(e.Message.Value<int>("object"));
                            break;
                        case 0xF7E0:
                            Said.Add(e.Message.Value<string>("text"));
                            break;
                        case 0xF7B0 when e.Message.Value<int>("event") == 0x0013:
                            Character = e.Message.Value<int>("character");
                            break;
                        case 0xF7B0 when e.Message.Value<int>("event") == 0x02BD:
                            Said.Add(e.Message.Value<string>("text"));
                            break;
                    }
                };
                decal.Core.EchoFilter.ClientDispatch += (_, e) =>
                {
                    if (e.Message.Type != 0xF7B1)
                        return;

                    Sent.Add(e.Message.Value<int>("action"));
                };
            }

            public HashSet<int> Known { get; } = new HashSet<int>();

            public int Character { get; private set; }

            /// <summary>The actions the client sent, by number.</summary>
            public List<int> Sent { get; } = new List<int>();

            public List<string> Said { get; } = new List<string>();
        }

        /// <summary>
        /// A Decal plugin that keeps its own track of the world from the messages finds, after a
        /// host restart, what it had found at the login: its character, every object still there
        /// and none since deleted, and the client's word that it was in - but nothing said is said
        /// to it twice, not the server's welcome, a tell, or what the player said.
        /// </summary>
        [Fact]
        public async Task ADecalPluginTrackingTheMessagesFindsWhatItFoundAtTheLoginWhenTheSessionIsCarriedOn()
        {
            const uint Wanderer = 0x80000302;

            RelayStandIn first = new RelayStandIn();
            HandoverSnapshot handover;
            MessageTracker atLogin;
            await using (GameHost before = new GameHost(first, new ListLog(), dataRoot: NewRoot()))
            {
                using DecalRuntime decal = new DecalRuntime(before);
                atLogin = new MessageTracker(decal);
                await before.StartAsync();

                first.Starts(SessionStart.Login);
                foreach (AcMessage message in Login())
                    first.FromServer(message);
                first.FromClient(new WireWriter(Opcodes.GameAction).U32(1).U32(GameActions.LoginComplete));
                first.FromServer(WireWriter.ObjectCreate(Wanderer, "Drudge Wanderer", 941, ItemTypes.Creature, 0));
                first.FromServer(WireWriter.GameEvent(Player, GameEvents.Tell).String16L("psst").String16L("Alice").U32(0x50000003).U32(Player).U32(3).U32(0));
                first.FromClient(new WireWriter(Opcodes.GameAction).U32(2).U32(GameActions.Talk).String16L("hello"));
                first.FromServer(new WireWriter(Opcodes.ObjectDelete).U32(Wanderer).U16(1));

                handover = ThroughBytes(await OnGameThreadAsync(before, () => before.CreateHandover(DateTimeOffset.UtcNow)));
            }

            Assert.Equal(new[] { "Welcome to Asheron's Call", "psst" }, atLogin.Said);
            Assert.Equal(new[] { (int)GameActions.LoginComplete, (int)GameActions.Talk }, atLogin.Sent);

            RelayStandIn next = new RelayStandIn();
            await using GameHost host = new GameHost(next, new ListLog(), dataRoot: NewRoot());
            using DecalRuntime carriedOn = new DecalRuntime(host);
            MessageTracker afterRestart = new MessageTracker(carriedOn);
            host.OfferHandover(handover);
            await host.StartAsync();
            next.Starts(SessionStart.UnderWay);
            await DrainAsync(host);

            Assert.Equal(unchecked((int)Player), afterRestart.Character);
            Assert.Equal(atLogin.Known.OrderBy(id => id), afterRestart.Known.OrderBy(id => id));
            Assert.DoesNotContain(unchecked((int)Wanderer), afterRestart.Known);
            Assert.Equal(new[] { (int)GameActions.LoginComplete }, afterRestart.Sent);
            Assert.Empty(afterRestart.Said);
        }

        /// <summary>
        /// Writes down what a Decal plugin reads of the character at Login and at LoginComplete -
        /// Mag-Tools' HUD, made at LoginComplete, asks every second for the free slots of the pack
        /// that is CharacterFilter.Id - and of the character's own object in the world filter.
        /// </summary>
        private static List<string> ReadAtLogin(DecalRuntime decal)
        {
            List<string> read = new List<string>();
            global::Decal.Adapter.Wrappers.CharacterFilter character = decal.Core.CharacterFilter;
            string Read(string when)
                => $"{when}: {character.Id:X8} {character.Name}, status {character.LoginStatus}, level {character.Level}, xp {character.TotalXP}, "
                    + $"burden {character.BurdenUnits}, {character.Server}; self {decal.Core.WorldFilter[character.Id]?.Name ?? "unknown"}";

            character.Login += (_, e) => read.Add(Read($"login {e.Id:X8}"));
            character.LoginComplete += (_, _) => read.Add(Read("complete"));
            return read;
        }

        /// <summary>
        /// A Decal plugin reading the character at Login - before LoginComplete - finds, after a host
        /// restart, what it found at the login itself: the character's id and name, its level and
        /// experience, and its own object. Carried on, the host names the character before it tells
        /// of the character's object, as at a login; Decal's Login waits for the object, as Decal's
        /// waited for the description that fills it.
        /// </summary>
        [Fact]
        public async Task ADecalPluginReadingTheCharacterAtLoginFindsWhatItFoundAtTheLoginWhenTheSessionIsCarriedOn()
        {
            RelayStandIn first = new RelayStandIn();
            HandoverSnapshot handover;
            List<string> atLogin;
            await using (GameHost before = new GameHost(first, new ListLog(), dataRoot: NewRoot()))
            {
                using DecalRuntime decal = new DecalRuntime(before);
                atLogin = ReadAtLogin(decal);
                await before.StartAsync();

                first.Starts(SessionStart.Login);
                foreach (AcMessage message in Login())
                    first.FromServer(message);
                first.FromClient(MacroTestHost.ClientEntered());

                handover = ThroughBytes(await OnGameThreadAsync(before, () => before.CreateHandover(DateTimeOffset.UtcNow)));
            }

            string described = $"{Player:X8} Testchar I, status 1, level 275, xp 191226310247";
            Assert.Equal(2, atLogin.Count);
            Assert.StartsWith($"login {Player:X8}: {described}", atLogin[0], StringComparison.Ordinal);
            Assert.EndsWith("Example Server; self Testchar I", atLogin[0], StringComparison.Ordinal);

            RelayStandIn next = new RelayStandIn();
            await using GameHost host = new GameHost(next, new ListLog(), dataRoot: NewRoot());
            using DecalRuntime carriedOn = new DecalRuntime(host);
            List<string> afterRestart = ReadAtLogin(carriedOn);
            host.OfferHandover(handover);
            await host.StartAsync();
            next.Starts(SessionStart.UnderWay);
            await DrainAsync(host);

            Assert.Equal(atLogin, afterRestart);
        }

        /// <summary>
        /// The player's own captures, handed over from their middle to a host with Decal running
        /// over it: what a Decal plugin reads of the character at Login is what it reads at
        /// LoginComplete - the character named, described and in the world filter. Captures are
        /// never committed, so this skips without them (see <see cref="CaptureHandoverTests"/>).
        /// </summary>
        [SkippableTheory]
        [InlineData("session.acap")]
        [InlineData("session-readonly.acap")]
        [InlineData("session-20260929-1118.acap")]
        [InlineData("session-20260929-1208.acap")]
        [InlineData("session-20260929-1227.acap")]
        [InlineData("session-20260929-1245.acap")]
        [InlineData("session-20260929-1256.acap")]
        [InlineData("session-20260929-1558.acap")]
        [InlineData("session-20260929-1708.acap")]
        [InlineData("session-20260929-1934.acap")]
        public async Task ACaptureCarriedOnFromItsMiddleHasDecalsLoginFindTheCharacterDescribed(string name)
        {
            string path = CaptureHandoverTests.FindCapture(name);
            Skip.If(path == null, $"{name} is not on this machine; captures are never committed.");

            List<CapturedDatagram> all = CaptureReader.Read(path).ToList();
            int split = CaptureHandoverTests.SplitPoint(all, 0.5);
            Skip.If(split < 0, $"{name} has nothing after its middle to carry on.");

            CaptureTransport firstTransport = new CaptureTransport(all.Take(split).ToList());
            HandoverSnapshot snapshot;
            uint character;
            await using (GameHost first = new GameHost(firstTransport, new ListLog(), dataRoot: NewRoot()))
            {
                await first.StartAsync();
                await first.Ended.WaitAsync(TimeSpan.FromMinutes(2));
                character = first.WorldState.Character.Id;
                snapshot = first.CreateHandover(DateTimeOffset.UtcNow);
            }

            Skip.If(character == 0 || snapshot == null, $"{name} has no character in the world in its middle.");
            snapshot.Relay = firstTransport.SaveState();

            // Only the packet that shows the session going on, so that what Decal hears is the
            // carrying on itself.
            CaptureTransport nextTransport = new CaptureTransport(all.Skip(split).Take(1).ToList());
            nextTransport.Resume(snapshot.Relay);
            await using GameHost carried = new GameHost(nextTransport, new ListLog(), dataRoot: NewRoot());
            using DecalRuntime decal = new DecalRuntime(carried);
            List<string> read = ReadAtLogin(decal);
            carried.OfferHandover(snapshot);
            await carried.StartAsync();
            await carried.Ended.WaitAsync(TimeSpan.FromMinutes(2));

            Assert.NotNull(carried.CarriedOnFrom);
            Skip.If(read.Count < 2, $"{name}'s first half has no login whose word that the client had entered the world is retold.");

            static string Reading(string line) => line.Substring(line.IndexOf(": ", StringComparison.Ordinal) + 2);
            Assert.False(string.IsNullOrEmpty(carried.Character.Name));
            Assert.StartsWith($"login {character:X8}: {character:X8} {carried.Character.Name}, status 1, level ", read[0], StringComparison.Ordinal);
            Assert.DoesNotContain(", level 0,", read[0], StringComparison.Ordinal);
            Assert.EndsWith($"; self {carried.Character.Name}", read[0], StringComparison.Ordinal);
            Assert.StartsWith("complete: ", read[1], StringComparison.Ordinal);
            Assert.Equal(Reading(read[1]), Reading(read[0]));
        }

        /// <summary>
        /// An enchantment says what it had left when the server sent it, and plugins count down from
        /// when they hear of it - which, carried on, is now. So it is aged by the time since.
        /// </summary>
        [Fact]
        public async Task EnchantmentsCarriedOnAreAgedByTheTimeSinceTheServerSentThem()
        {
            DateTimeOffset tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10);
            HandoverSnapshot handover = new HandoverSnapshot { WrittenAt = DateTimeOffset.UtcNow };
            foreach (AcMessage message in Login().Take(4))
                handover.Journal.Add(new JournalEntry(tenMinutesAgo, PacketDirection.Inbound, message.Opcode, message.Payload.ToArray()));

            WorldState sent = new WorldState();
            foreach (AcMessage message in Login().Take(4))
                MessageDecoder.Apply(message, PacketDirection.Inbound, sent);

            RelayStandIn transport = new RelayStandIn();
            await using GameHost host = new GameHost(transport, new ListLog(), dataRoot: NewRoot());
            host.OfferHandover(handover);
            await host.StartAsync();
            transport.Starts(SessionStart.UnderWay);
            await DrainAsync(host);

            List<Enchantment> expiring = sent.Character.Enchantments.Values.Where(e => !e.IsPermanent).ToList();
            Assert.NotEmpty(expiring);
            foreach (Enchantment then in expiring)
            {
                Enchantment now = host.Character.Enchantments[then.PackedId];
                Assert.InRange(then.RemainingWhenSent - now.RemainingWhenSent, 598, 610);
            }

            // One an item gives while worn does not run out, and is left as it was.
            foreach (Enchantment worn in sent.Character.Enchantments.Values.Where(e => e.IsPermanent))
                Assert.Equal(worn.StartTime, host.Character.Enchantments[worn.PackedId].StartTime);
        }

        private static WireWriter PositionAt(uint id, uint cell)
            => new WireWriter(Opcodes.UpdatePosition).U32(id)
                .U32(0x08 | 0x10 | 0x20)
                .U32(cell).F32(10).F32(20).F32(30)
                .F32(0.7f)
                .U16(1).U16(2).U16(3).U16(4);

        /// <summary>
        /// What the client let go of out of view is gone for the host carrying on too: one the host
        /// before let go of on its tick, and one that went out of view too long ago by the time the
        /// session is carried on - neither is told to plugins, and the next host will not have them.
        /// </summary>
        [Fact]
        public async Task WhatWentOutOfViewLongEnoughAgoIsNotCarriedOn()
        {
            const uint Near = 0x80000401;
            const uint Far = 0x80000402;
            const uint Leaving = 0x80000403;
            const uint Elsewhere = 0x2B110019;

            DateTimeOffset clock = DateTimeOffset.UtcNow.AddSeconds(-60);
            RelayStandIn transport = new RelayStandIn();
            HandoverSnapshot handover;
            await using (GameHost before = new GameHost(transport, new ListLog(), dataRoot: NewRoot(), world: new WorldState(() => clock)))
            {
                await before.StartAsync();
                transport.Starts(SessionStart.Login);
                foreach (AcMessage message in Login().Take(3))
                    transport.FromServer(message);
                transport.FromServer(WireWriter.ObjectCreate(Near, "Drudge Prowler", 941, ItemTypes.Creature, 0));
                transport.FromServer(WireWriter.ObjectCreate(Far, "Drudge Slinker", 942, ItemTypes.Creature, 0, withLocation: false));
                transport.FromServer(PositionAt(Far, Elsewhere));
                await DrainAsync(before);

                // Thirty seconds on, the host's tick lets go of the one out of view.
                clock = clock.AddSeconds(30);
                for (int i = 0; i < 50 && await OnGameThreadAsync(before, () => before.World.Get(Far) != null); i++)
                    await Task.Delay(50);
                Assert.Null(before.World.Get(Far));

                // And another goes out of view just before the host stops.
                transport.FromServer(WireWriter.ObjectCreate(Leaving, "Drudge Ravener", 943, ItemTypes.Creature, 0, withLocation: false));
                transport.FromServer(PositionAt(Leaving, Elsewhere));
                handover = ThroughBytes(await OnGameThreadAsync(before, () => before.CreateHandover(clock)));
            }

            RelayStandIn next = new RelayStandIn();
            await using GameHost host = new GameHost(next, new ListLog(), dataRoot: NewRoot());
            EventRecorder recorder = new EventRecorder();
            host.AddPlugin(recorder);
            host.OfferHandover(handover);
            await host.StartAsync();
            next.Starts(SessionStart.UnderWay);
            await DrainAsync(host);

            Assert.NotNull(host.World.Get(Near));
            Assert.Null(host.World.Get(Far));
            Assert.Null(host.World.Get(Leaving));
            Assert.Equal(new[] { $"created {Player:X8}", $"created {Near:X8}" }, recorder.Heard.Where(h => h.StartsWith("created", StringComparison.Ordinal)));

            // Kept as let go of, for the host after this one.
            List<(uint Opcode, uint Id)> kept = await OnGameThreadAsync(host, () => host.SessionJournal.Entries.Select(e => (e.Opcode, BitConverter.ToUInt32(e.Payload, 0))).ToList());
            Assert.Contains((Opcodes.ObjectDelete, Far), kept);
            Assert.Contains((Opcodes.ObjectDelete, Leaving), kept);
        }

        [Fact]
        public async Task ANewLoginLetsWhatWasHandedOverGo()
        {
            HandoverSnapshot handover = await HandOverAfterAsync(Login());

            RelayStandIn transport = new RelayStandIn();
            ListLog log = new ListLog();
            await using GameHost host = new GameHost(transport, log, dataRoot: NewRoot());
            host.OfferHandover(handover);
            await host.StartAsync();

            transport.Starts(SessionStart.Login);
            await DrainAsync(host);

            Assert.Equal(0, host.World.ObjectCount);
            Assert.Null(host.World.ServerName);
            Assert.Null(host.CarriedOnFrom);
            Assert.False(host.JoinedMidSession);
            Assert.Equal("let go: the client began a new login", await OnGameThreadAsync(host, () => host.HandoverFate));
            lock (log.Lines)
                Assert.Contains(log.Lines, l => l.Contains("new login, so the session the host before this one handed over is not carried on"));
        }

        /// <summary>
        /// Taken in time, but the game was not heard from until it was too old to carry on: the host
        /// joined in the middle after all, and says that it was handed something and let it go - not
        /// that nothing was handed over, which would send whoever reads the log looking for a file.
        /// </summary>
        [Fact]
        public async Task AHandoverTheGameIsHeardFromTooLateForIsLetGoAndTheLogSaysSo()
        {
            HandoverSnapshot handover = await HandOverAfterAsync(Login());
            handover.WrittenAt = DateTimeOffset.UtcNow - HandoverSnapshot.MaxAge - TimeSpan.FromSeconds(30);

            RelayStandIn transport = new RelayStandIn();
            ListLog log = new ListLog();
            await using GameHost host = new GameHost(transport, log, dataRoot: NewRoot());
            host.OfferHandover(handover);
            Assert.Equal("to be carried on once the game is heard from", host.HandoverFate);
            await host.StartAsync();

            transport.Starts(SessionStart.UnderWay);
            transport.FromServer(Position(Drudge, 12));
            await DrainAsync(host);

            Assert.True(host.JoinedMidSession);
            Assert.Null(host.CarriedOnFrom);
            Assert.StartsWith("let go: the game was first heard from 15", await OnGameThreadAsync(host, () => host.HandoverFate));
            lock (log.Lines)
            {
                Assert.Contains(log.Lines, l => l.StartsWith("WARN The host joined a session already under way, and the session the host before it handed over at", StringComparison.Ordinal)
                    && l.Contains("s old by the time the game was first heard from"));
                Assert.DoesNotContain(log.Lines, l => l.Contains("nothing was handed over"));
            }
        }

        // ------------------------------------------------------------------- joining with nothing

        [Fact]
        public async Task AHostJoiningASessionWithNothingHandedOverSaysSoOnceAndAsksForALoginAgain()
        {
            RelayStandIn transport = new RelayStandIn();
            ListLog log = new ListLog();
            await using GameHost host = new GameHost(transport, log, dataRoot: NewRoot());
            await host.StartAsync();

            transport.Starts(SessionStart.UnderWay);
            transport.FromServer(Position(Drudge, 10));
            transport.FromServer(Position(Drudge, 11));
            await DrainAsync(host);

            Assert.True(host.JoinedMidSession);
            lock (log.Lines)
                Assert.Contains(log.Lines, l => l.StartsWith("WARN The host joined a session already under way", StringComparison.Ordinal));

            // Told once, in the game, in plain words.
            AcMessage shown;
            lock (transport.Shown)
                shown = Assert.Single(transport.Shown);
            WorldState reading = new WorldState();
            string text = null;
            reading.ChatReceived += (_, m) => text = m.Text;
            MessageDecoder.Apply(shown, PacketDirection.Inbound, reading);
            Assert.Equal("[Decal] Decal Agent started while you were in the world: log out to character select and enter the world again so it can see your character.", text);

            // The player does as asked: back to the character list, and into the world again, which
            // the host sees from the start.
            transport.FromServer(new WireWriter(Opcodes.CharacterList).U32(0));
            transport.FromServer(new WireWriter(Opcodes.PlayerCreate).U32(Player));
            transport.FromServer(WireWriter.ObjectCreate(Player, "Testchar I", 1, ItemTypes.Creature, 0));
            await DrainAsync(host);

            Assert.False(host.JoinedMidSession);
            Assert.Equal("Testchar I", host.Character.Name);
            lock (transport.Shown)
                Assert.Single(transport.Shown);
        }

        /// <summary>At the character list there is no chat window to read a line in, and nothing to be told.</summary>
        [Fact]
        public async Task AHostJoiningAtTheCharacterListShowsNothingAndSeesTheLoginWhole()
        {
            RelayStandIn transport = new RelayStandIn();
            await using GameHost host = new GameHost(transport, new ListLog(), dataRoot: NewRoot());
            await host.StartAsync();

            transport.Starts(SessionStart.UnderWay);
            transport.FromServer(new WireWriter(Opcodes.CharacterList).U32(0));
            await DrainAsync(host);
            Assert.True(host.JoinedMidSession);

            transport.FromServer(new WireWriter(Opcodes.PlayerCreate).U32(Player));
            await DrainAsync(host);

            Assert.False(host.JoinedMidSession);
            lock (transport.Shown)
                Assert.Empty(transport.Shown);
        }

        [Fact]
        public async Task StatusSaysTheHostJoinedWhileThePlayerWasInTheWorld()
        {
            RelayStandIn transport = new RelayStandIn();
            HostRuntimeOptions options = Isolated(NewRoot(), transport);
            await using HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();

            transport.Starts(SessionStart.UnderWay);
            transport.FromServer(Position(Drudge, 10));

            string status = await ControlPipe.SendAsync("status", options.ControlPipeName);
            Assert.Contains("server     (not connected)", status);
            Assert.Contains("session    joined while you were in the world: log out to character select and enter the world again", status);
        }

        // ------------------------------------------------------------------- the whole runtime

        private static HostRuntimeOptions Isolated(string root, IGameTransport transport)
            => new HostRuntimeOptions
            {
                Transport = transport,
                PluginDirectory = Path.Combine(root, "plugins"),
                DataDirectory = Path.Combine(root, "data"),
                NoDat = true,
                ControlPipeName = "achost-test-control-" + Guid.NewGuid().ToString("N"),
                OverlayPipeName = "achost-test-overlay-" + Guid.NewGuid().ToString("N"),
            };

        /// <summary>
        /// The Decal Agent updated with the player in the world, start to finish: the host stopping
        /// leaves the session in its data folder, the host starting takes it up - deleting the file -
        /// and `ctl status` names the server and the character, and says it carried the session on.
        /// </summary>
        [Fact]
        public async Task AHostStoppedInTheWorldHandsTheSessionToTheNextOnTheSamePorts()
        {
            string root = NewRoot();
            RelayStandIn before = new RelayStandIn();
            HostRuntimeOptions first = Isolated(root, before);
            ListLog stoppingLog = new ListLog();
            HostRuntime stopping = new HostRuntime(first, stoppingLog);
            await stopping.StartAsync();

            before.Starts(SessionStart.Login);
            foreach (AcMessage message in Login())
                before.FromServer(message);
            Assert.Equal("acting allowed", (await ControlPipe.SendAsync("act on", first.ControlPipeName)).Trim());

            await stopping.DisposeAsync();
            string path = stopping.HandoverPath;
            Assert.Equal(Path.Combine(root, "data", "handover-9100.bin"), path);
            Assert.True(File.Exists(path));

            // The whole path written, in the log of the host that wrote it.
            lock (stoppingLog.Lines)
                Assert.Contains(stoppingLog.Lines, l => l.StartsWith($"INFO Handed the session over in {path} (", StringComparison.Ordinal));

            RelayStandIn after = new RelayStandIn();
            HostRuntimeOptions second = Isolated(root, after);
            ListLog log = new ListLog();
            await using HostRuntime starting = new HostRuntime(second, log);
            Assert.False(File.Exists(path));
            Assert.False(starting.Host.ActionsAllowed);

            // And the same path, in the log of the host that took it.
            lock (log.Lines)
                Assert.Contains(log.Lines, l => l.StartsWith("INFO The host before this one handed over the session", StringComparison.Ordinal) && l.Contains($", in {path} ("));

            await starting.StartAsync();
            after.Starts(SessionStart.UnderWay);

            string status = await ControlPipe.SendAsync("status", second.ControlPipeName);
            Assert.Contains("server     Example Server", status);
            Assert.Contains("character  Testchar I", status);
            Assert.Contains("session    carried on from the host that stopped at", status);
            Assert.Contains($"handover   taken from {path}, written at ", status);
            Assert.Contains("message(s)): carried on", status);
            Assert.Contains("acting     allowed", status);
            Assert.Contains("Diamond", await ControlPipe.SendAsync("find Diamond", second.ControlPipeName));
        }

        [Fact]
        public async Task AHostThatSawNoSessionLeavesNothingAndSaysSo()
        {
            string root = NewRoot();
            ListLog log = new ListLog();
            HostRuntime runtime = new HostRuntime(Isolated(root, new RelayStandIn()), log);
            await runtime.StartAsync();
            await runtime.DisposeAsync();

            Assert.False(File.Exists(runtime.HandoverPath));
            lock (log.Lines)
                Assert.Contains($"INFO Nothing was handed over in {runtime.HandoverPath}: no session was going on.", log.Lines);
        }

        /// <summary>
        /// A host that finds nothing says exactly where it looked and what the folder holds instead:
        /// set beside the path the host before it wrote to, that tells a file written somewhere else,
        /// or out of this host's sight, from a file never written.
        /// </summary>
        [Fact]
        public async Task AHostThatFindsNothingHandedOverSaysWhereItLookedAndWhatIsThere()
        {
            string root = NewRoot();
            string data = Path.Combine(root, "data");
            Directory.CreateDirectory(Path.Combine(data, "logs"));
            File.WriteAllText(Path.Combine(data, "handover-9200.bin"), "left for another host's ports");

            RelayStandIn transport = new RelayStandIn();
            HostRuntimeOptions options = Isolated(root, transport);
            ListLog log = new ListLog();
            await using HostRuntime runtime = new HostRuntime(options, log);
            string path = Path.Combine(data, "handover-9100.bin");
            Assert.Equal(path, runtime.HandoverPath);

            lock (log.Lines)
                Assert.Contains($"INFO No session was handed over: there is nothing at {path}. {data} holds handover-9200.bin, logs.", log.Lines);

            // Another port's handover is not this host's to take.
            Assert.True(File.Exists(Path.Combine(data, "handover-9200.bin")));

            await runtime.StartAsync();
            transport.Starts(SessionStart.UnderWay);
            transport.FromServer(Position(Drudge, 10));

            string status = await ControlPipe.SendAsync("status", options.ControlPipeName);
            Assert.Contains($"handover   none at {path}{Environment.NewLine}", status);
            Assert.Contains("session    joined while you were in the world", status);
        }

        /// <summary>
        /// A file that does not read as a handover - cut short, or with a length or an address that
        /// makes no sense - is said to be unreadable and deleted, and the host starts as with none:
        /// one bad file must never keep the Agent from starting, at this start or the next.
        /// </summary>
        [Fact]
        public async Task AHandoverThatDoesNotReadAsOneIsDeletedAndTheHostStartsAnyway()
        {
            string root = NewRoot();
            RelayStandIn transport = new RelayStandIn();
            HostRuntimeOptions options = Isolated(root, transport);
            string path = HandoverSnapshot.PathIn(options.DataDirectory, options.Proxy.ListenPort);
            File.WriteAllBytes(EnsureFolder(path), Garbled(options.Proxy));

            ListLog log = new ListLog();
            await using HostRuntime runtime = new HostRuntime(options, log);
            Assert.False(File.Exists(path));
            lock (log.Lines)
                Assert.Contains(log.Lines, l => l.StartsWith($"INFO The session handed over in {path} could not be read (", StringComparison.Ordinal));

            await runtime.StartAsync();
            string status = await ControlPipe.SendAsync("status", options.ControlPipeName);
            Assert.Contains($"handover   not taken: The session handed over in {path} could not be read (", status);
        }

        private static string EnsureFolder(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return path;
        }

        /// <summary>A handover written whole for <paramref name="relay"/>, with the length of its first string made nonsense.</summary>
        private static byte[] Garbled(ProxyOptions relay)
        {
            HandoverSnapshot snapshot = new HandoverSnapshot { WrittenAt = DateTimeOffset.UtcNow };
            snapshot.SetEndpoints(relay);
            using MemoryStream stream = new MemoryStream();
            snapshot.Write(stream);
            byte[] bytes = stream.ToArray();

            // After the magic, the format and the time: the listen address's length, as seven-bit
            // groups that never end.
            for (int i = 20; i < 25; i++)
                bytes[i] = 0xFF;
            return bytes;
        }

        /// <summary>
        /// The Decal Agent updated with the player in the world, as tools\update-agent.ps1 does it:
        /// the session left in the data folder by the Agent that stopped, a few seconds old, and an
        /// Agent's host built exactly as AgentForm builds one - the real relay on its own ports, the
        /// overlay served, acting off - taking it up before it relays anything. The copies the host
        /// before it ran its plugins from, in running, are cleared in the same breath; a host that
        /// does neither is not looking at the folder the one before it wrote to.
        /// </summary>
        [Fact]
        public async Task AnAgentStartedOnTheSamePortsTakesUpWhatTheOneBeforeItLeft()
        {
            string root = NewRoot();
            string data = Path.Combine(root, "data");
            int port = HostRuntimeTests.FreePortPair();
            int serverPort = HostRuntimeTests.FreePortPair();
            while (Math.Abs(serverPort - port) < 2)
                serverPort = HostRuntimeTests.FreePortPair();

            HostRuntimeOptions options = new HostRuntimeOptions
            {
                Proxy = new ProxyOptions { ServerHost = "127.0.0.1", ServerPort = serverPort, ListenPort = port },
                PluginDirectory = Path.Combine(root, "plugins"),
                DataDirectory = data,
                EnableActions = false,
                Overlay = true,
                NoDat = true,
                ControlPipeName = "achost-test-control-" + Guid.NewGuid().ToString("N"),
                OverlayPipeName = "achost-test-overlay-" + Guid.NewGuid().ToString("N"),
            };

            // What the Agent that stopped left: its session, written seconds ago for these ports,
            // and the folder its plugins ran from, under a process id no process has.
            HandoverSnapshot left = await HandOverAfterAsync(Login());
            left.SetEndpoints(options.Proxy);
            left.WrittenAt = DateTimeOffset.UtcNow.AddSeconds(-4);
            string path = HandoverSnapshot.PathIn(data, port);
            left.Save(path);
            string oldCopies = Path.Combine(data, "running", "2147483644", "Counting.Plugin-0123456789ab");
            Directory.CreateDirectory(oldCopies);
            File.WriteAllText(Path.Combine(oldCopies, "Counting.Plugin.pdb"), "a copy");

            ListLog log = new ListLog();
            HostRuntime runtime = new HostRuntime(options, log);
            Assert.IsType<ProxyTransport>(runtime.Transport);
            Assert.True(runtime.HandsOver);
            Assert.Equal(path, runtime.HandoverPath);
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.Combine(data, "running", "2147483644")));
            lock (log.Lines)
                Assert.Contains(log.Lines, l => l.StartsWith("INFO The host before this one handed over the session", StringComparison.Ordinal) && l.Contains($", in {path} ({left.Journal.Count} message(s))"));

            await runtime.StartAsync();
            string status = await ControlPipe.SendAsync("status", options.ControlPipeName);
            Assert.Contains($"handover   taken from {path}, written at {left.WrittenAt.ToLocalTime():HH:mm:ss} ({left.Journal.Count} message(s)): to be carried on once the game is heard from", status);

            // Stopped again before the game was heard from - the update run twice, say - it hands
            // on what it was handed, as it came: the session is not lost between two restarts.
            await runtime.DisposeAsync();
            Assert.True(File.Exists(path));
            lock (log.Lines)
                Assert.Contains(log.Lines, l => l.StartsWith($"INFO Handed on the session the host before this one handed over at {left.WrittenAt.ToLocalTime():HH:mm:ss}, never taken up", StringComparison.Ordinal));

            HandoverSnapshot handedOn = HandoverSnapshot.Take(path, options.Proxy, DateTimeOffset.UtcNow, out string refusal);
            Assert.Null(refusal);
            Assert.Equal(left.WrittenAt, handedOn.WrittenAt);
            Assert.Equal(left.Journal.Count, handedOn.Journal.Count);
        }

        [Fact]
        public async Task AHostWhoseSessionEndedLeavesNothing()
        {
            string root = NewRoot();
            RelayStandIn transport = new RelayStandIn();
            HostRuntimeOptions options = Isolated(root, transport);
            HostRuntime runtime = new HostRuntime(options, new ListLog());
            await runtime.StartAsync();
            foreach (AcMessage message in Login())
                transport.FromServer(message);
            transport.Closes();
            await ControlPipe.SendAsync("status", options.ControlPipeName);
            await runtime.DisposeAsync();

            Assert.False(File.Exists(runtime.HandoverPath));
        }

        // ------------------------------------------------------------------- the file

        [Fact]
        public void ASnapshotRoundTripsThroughItsFileAndIsDeletedOnceTaken()
        {
            ProxyOptions relay = new ProxyOptions { ServerHost = "127.0.0.1", ServerPort = 9000, ListenPort = 9100 };
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HandoverSnapshot snapshot = new HandoverSnapshot
            {
                WrittenAt = now.AddSeconds(-20),
                ActionsAllowed = true,
                JoinedMidSession = true,
                ToldJoinedMidSession = true,
                LastTellFrom = "Bob",
                LastTellTo = "Alice",
                LastActionSequence = 1234,
                Relay = new RelayState { Inbound = new ClientStreamRewriter().SaveState() },
            };
            snapshot.SetEndpoints(relay);
            snapshot.Relay.Clients[9100] = new IPEndPoint(IPAddress.Loopback, 50123);
            snapshot.Journal.Add(new JournalEntry(now.AddMinutes(-1), PacketDirection.Inbound, Opcodes.ServerName, new byte[] { 1, 2, 3, 4 }));
            snapshot.Journal.Add(new JournalEntry(now, PacketDirection.Outbound, Opcodes.GameAction, new byte[] { 5, 6, 7, 8, 9, 10, 11, 12 }, pinned: true));

            string path = HandoverSnapshot.PathIn(NewRoot(), relay.ListenPort);
            snapshot.Save(path);

            HandoverSnapshot taken = HandoverSnapshot.Take(path, relay, now, out string refusal);
            Assert.Null(refusal);
            Assert.False(File.Exists(path));

            Assert.Equal(snapshot.WrittenAt, taken.WrittenAt);
            Assert.Equal("127.0.0.1", taken.ListenAddress);
            Assert.Equal(9100, taken.ListenPort);
            Assert.Equal("127.0.0.1", taken.ServerHost);
            Assert.Equal(9000, taken.ServerPort);
            Assert.Equal(2, taken.PortCount);
            Assert.True(taken.ActionsAllowed);
            Assert.True(taken.JoinedMidSession);
            Assert.True(taken.ToldJoinedMidSession);
            Assert.Equal("Bob", taken.LastTellFrom);
            Assert.Equal("Alice", taken.LastTellTo);
            Assert.Equal(1234u, taken.LastActionSequence);
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, 50123), taken.Relay.Clients[9100]);
            Assert.NotNull(taken.Relay.Inbound);
            Assert.Null(taken.Relay.Outbound);

            Assert.Equal(2, taken.Journal.Count);
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(snapshot.Journal[i].At, taken.Journal[i].At);
                Assert.Equal(snapshot.Journal[i].Direction, taken.Journal[i].Direction);
                Assert.Equal(snapshot.Journal[i].Opcode, taken.Journal[i].Opcode);
                Assert.Equal(snapshot.Journal[i].Payload, taken.Journal[i].Payload);
                Assert.Equal(snapshot.Journal[i].Pinned, taken.Journal[i].Pinned);
            }

            // Once taken, it is gone: nothing there is nothing, said nothing about.
            Assert.Null(HandoverSnapshot.Take(path, relay, now, out refusal));
            Assert.Null(refusal);
        }

        [Fact]
        public void ASnapshotTooOldOrForAnotherRelayIsNotTakenAndIsDeleted()
        {
            ProxyOptions relay = new ProxyOptions { ServerHost = "127.0.0.1", ServerPort = 9000, ListenPort = 9100 };
            string path = HandoverSnapshot.PathIn(NewRoot(), relay.ListenPort);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            HandoverSnapshot stale = new HandoverSnapshot { WrittenAt = now - HandoverSnapshot.MaxAge - TimeSpan.FromSeconds(1) };
            stale.SetEndpoints(relay);
            stale.Save(path);
            Assert.Null(HandoverSnapshot.Take(path, relay, now, out string refusal));
            Assert.Contains("too long ago", refusal);
            Assert.False(File.Exists(path));

            HandoverSnapshot elsewhere = new HandoverSnapshot { WrittenAt = now };
            elsewhere.SetEndpoints(new ProxyOptions { ServerHost = "10.0.0.5", ServerPort = 9000, ListenPort = 9100 });
            elsewhere.Save(path);
            Assert.Null(HandoverSnapshot.Take(path, relay, now, out refusal));
            Assert.Contains("not where this host relays", refusal);
            Assert.False(File.Exists(path));

            File.WriteAllText(path, "not a handover");
            Assert.Null(HandoverSnapshot.Take(path, relay, now, out refusal));
            Assert.Contains("could not be read", refusal);
            Assert.False(File.Exists(path));

            // A length that never ends, which the reader says in a way of its own.
            File.WriteAllBytes(path, Garbled(relay));
            Assert.Null(HandoverSnapshot.Take(path, relay, now, out refusal));
            Assert.Contains("could not be read (A handover that does not read as one", refusal);
            Assert.False(File.Exists(path));

            // Nothing there, and a folder that is not there either: nothing, said nothing about.
            Assert.Null(HandoverSnapshot.Take(Path.Combine(NewRoot(), "handover-9100.bin"), relay, now, out refusal));
            Assert.Null(refusal);
        }

        /// <summary>
        /// A handover another program has open - a scanner looking at a file just written - is read
        /// all the same: only a file not found is "nothing handed over".
        /// </summary>
        [Fact]
        public void AHandoverSomethingElseHasOpenIsTakenAllTheSame()
        {
            ProxyOptions relay = new ProxyOptions { ServerHost = "127.0.0.1", ServerPort = 9000, ListenPort = 9100 };
            string path = HandoverSnapshot.PathIn(NewRoot(), relay.ListenPort);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HandoverSnapshot snapshot = new HandoverSnapshot { WrittenAt = now };
            snapshot.SetEndpoints(relay);
            snapshot.Save(path);

            HandoverSnapshot taken;
            string refusal;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                taken = HandoverSnapshot.Take(path, relay, now, out refusal);

            Assert.Null(refusal);
            Assert.NotNull(taken);
            Assert.False(File.Exists(path));
        }

        // ------------------------------------------------------------------- the journal

        private static JournalEntry Entry(PacketDirection direction, WireWriter message, bool pinned = false)
        {
            AcMessage built = message.ToMessage();
            return new JournalEntry(DateTimeOffset.UtcNow, direction, built.Opcode, built.Payload.ToArray(), pinned);
        }

        private static WireWriter ClientAction(uint sequence, uint action) => new WireWriter(Opcodes.GameAction).U32(sequence).U32(action).U32(0);

        [Fact]
        public void TheJournalKeepsOnlyTheLatestOfWhatEachLaterMessageReplacesWhole()
        {
            SessionJournal journal = new SessionJournal();

            journal.Record(Entry(PacketDirection.Inbound, WireWriter.ObjectCreate(Drudge, "Drudge Skulker", 940, ItemTypes.Creature, 0)));
            journal.Record(Entry(PacketDirection.Inbound, Position(Drudge, 1)));
            journal.Record(Entry(PacketDirection.Outbound, ClientAction(1, GameActions.AutonomousPosition)));
            journal.Record(Entry(PacketDirection.Inbound, Position(Gem, 1)));
            journal.Record(Entry(PacketDirection.Inbound, WireWriter.Motion(Drudge, 0, 0x3D)));
            journal.Record(Entry(PacketDirection.Inbound, Position(Drudge, 2)));
            journal.Record(Entry(PacketDirection.Outbound, ClientAction(2, GameActions.AutonomousPosition)));
            journal.Record(Entry(PacketDirection.Outbound, ClientAction(3, GameActions.Use)));
            journal.Record(Entry(PacketDirection.Inbound, WireWriter.Motion(Drudge, 0, 0x3C)));
            journal.Record(Entry(PacketDirection.Inbound, Position(Drudge, 3)));
            journal.Record(Entry(PacketDirection.Outbound, ClientAction(4, GameActions.AutonomousPosition)));
            journal.Record(Entry(PacketDirection.Outbound, ClientAction(5, GameActions.Use)));

            // The creation, the gem's one position, both uses; and of the drudge's positions and
            // motions and the character's own steps, the last of each - in the order they came.
            Assert.Equal(
                new[] { "F745", "F748 gem", "B1 3", "F74C", "F748 3", "B1 4", "B1 5" },
                journal.Entries.Select(Describe));

            static string Describe(JournalEntry entry)
            {
                if (entry.Opcode == Opcodes.GameAction)
                    return "B1 " + BitConverter.ToUInt32(entry.Payload, 0);
                if (entry.Opcode == Opcodes.UpdatePosition)
                    return BitConverter.ToUInt32(entry.Payload, 0) == Gem ? "F748 gem" : "F748 " + BitConverter.ToSingle(entry.Payload, 12);
                return entry.Opcode.ToString("X4", CultureInfo.InvariantCulture);
            }
        }

        [Fact]
        public void AStepThatClosedAVendorsWindowIsKeptWhenALaterStepComes()
        {
            SessionJournal journal = new SessionJournal();
            journal.Record(Entry(PacketDirection.Outbound, ClientAction(1, GameActions.AutonomousPosition), pinned: true));
            journal.Record(Entry(PacketDirection.Outbound, ClientAction(2, GameActions.AutonomousPosition)));
            journal.Record(Entry(PacketDirection.Outbound, ClientAction(3, GameActions.AutonomousPosition)));

            Assert.Equal(new uint[] { 1, 3 }, journal.Entries.Select(e => BitConverter.ToUInt32(e.Payload, 0)));
        }

        [Fact]
        public void AJournalPastItsLimitGivesUpUntilTheSessionEnds()
        {
            SessionJournal journal = new SessionJournal(maxBytes: 4096);
            for (uint i = 0; i < 40 && !journal.Overflowed; i++)
                journal.Record(new JournalEntry(DateTimeOffset.UtcNow, PacketDirection.Inbound, Opcodes.ServerMessage, new byte[200]));

            Assert.True(journal.Overflowed);
            Assert.Equal(0, journal.Count);
            journal.Record(new JournalEntry(DateTimeOffset.UtcNow, PacketDirection.Inbound, Opcodes.ServerMessage, new byte[4]));
            Assert.Equal(0, journal.Count);

            journal.Clear();
            Assert.False(journal.Overflowed);
            journal.Record(new JournalEntry(DateTimeOffset.UtcNow, PacketDirection.Inbound, Opcodes.ServerMessage, new byte[4]));
            Assert.Equal(1, journal.Count);
        }

        [Fact]
        public async Task LeavingTheWorldForgetsAllButTheServersNameAndTheSessionEndingForgetsEverything()
        {
            RelayStandIn transport = new RelayStandIn();
            await using GameHost host = new GameHost(transport, new ListLog(), dataRoot: NewRoot());
            await host.StartAsync();
            transport.Starts(SessionStart.Login);
            foreach (AcMessage message in Login())
                transport.FromServer(message);

            transport.FromServer(new WireWriter(Opcodes.CharacterList).U32(0));
            List<uint> left = await OnGameThreadAsync(host, () => host.SessionJournal.Entries.Select(e => e.Opcode).ToList());
            Assert.Equal(new[] { Opcodes.ServerName, Opcodes.CharacterList }, left);

            transport.Closes();
            Assert.Equal(0, await OnGameThreadAsync(host, () => host.SessionJournal.Count));
        }
    }

    /// <summary>
    /// The player's own captures, each cut in two: the first part replayed by one host, handed
    /// over, and the rest replayed by another carrying it on. The world at the end must be the one
    /// a host that replayed it all would have.
    /// </summary>
    /// <remarks>
    /// Captures can hold account names and are never committed, so these skip on a machine
    /// without them. They are looked for in the folder of every checkout above the test build,
    /// which finds the main checkout's from a worktree.
    /// </remarks>
    public class CaptureHandoverTests
    {
        internal static string FindCapture(string name)
        {
            for (DirectoryInfo folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            {
                string path = Path.Combine(folder.FullName, name);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        /// <summary>The first datagram at or after <paramref name="fraction"/> of the way through that is not part of a login.</summary>
        internal static int SplitPoint(IReadOnlyList<CapturedDatagram> datagrams, double fraction)
        {
            for (int i = (int)(datagrams.Count * fraction); i < datagrams.Count; i++)
            {
                if (AcPacket.TryParse(datagrams[i].Bytes, out AcPacket packet) && SessionBoundary.ReadStart(packet) == SessionStart.UnderWay)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Where the character in the world at <paramref name="from"/> leaves it - logged off, sent
        /// to the character list, the session closed or begun again - or the end of the capture. The
        /// world is compared just before, while there still is one: leaving empties every host's.
        /// </summary>
        private static int EndOfWorld(IReadOnlyList<CapturedDatagram> datagrams, int from)
        {
            MessageAssembler server = new MessageAssembler();
            for (int i = from; i < datagrams.Count; i++)
            {
                if (!AcPacket.TryParse(datagrams[i].Bytes, out AcPacket packet))
                    continue;

                if (SessionBoundary.TryRead(packet, datagrams[i].Direction, out _))
                    return i;

                if (datagrams[i].Direction == PacketDirection.Inbound
                    && server.Accept(packet).Any(m => m.Opcode == Opcodes.CharacterLogOff || m.Opcode == Opcodes.CharacterList || m.Opcode == Opcodes.AccountBoot))
                    return i;
            }

            return datagrams.Count;
        }

        private static async Task<GameHost> ReplayAsync(IEnumerable<CapturedDatagram> datagrams, HandoverSnapshot handover = null)
        {
            CaptureTransport transport = new CaptureTransport(datagrams.ToList());
            if (handover?.Relay != null)
                transport.Resume(handover.Relay);

            GameHost host = new GameHost(transport, new ListLog(), dataRoot: Path.Combine(Path.GetTempPath(), "achost-handover-captures"));
            if (handover != null)
                host.OfferHandover(handover);

            await host.StartAsync();
            await host.Ended.WaitAsync(TimeSpan.FromMinutes(2));
            return host;
        }

        [SkippableTheory]
        [InlineData("session.acap", 0.5)]
        [InlineData("session-readonly.acap", 0.5)]
        [InlineData("session-20260929-1118.acap", 0.5)]
        [InlineData("session-20260929-1208.acap", 0.3)]
        [InlineData("session-20260929-1208.acap", 0.7)]
        [InlineData("session-20260929-1227.acap", 0.5)]
        [InlineData("session-20260929-1245.acap", 0.5)]
        [InlineData("session-20260929-1256.acap", 0.5)]
        [InlineData("session-20260929-1558.acap", 0.5)]
        [InlineData("session-20260929-1708.acap", 0.5)]
        [InlineData("session-20260929-1934.acap", 0.5)]
        public async Task ACaptureCarriedOnFromItsMiddleEndsWithTheWorldOfOneReplayedWhole(string name, double fraction)
        {
            string path = FindCapture(name);
            Skip.If(path == null, $"{name} is not on this machine; captures are never committed.");

            List<CapturedDatagram> all = CaptureReader.Read(path).ToList();
            int split = SplitPoint(all, fraction);
            Skip.If(split < 0, $"{name} has nothing after {fraction:P0} to carry on.");
            all = all.Take(EndOfWorld(all, split)).ToList();

            GameHost whole = await ReplayAsync(all);
            Skip.If(whole.WorldState.Character.Id == 0, $"{name} has no character in the world {fraction:P0} of the way through.");

            // The first part, and what its host hands over - with the halves of messages its
            // assemblers held - written out and read back as the file carries it.
            CaptureTransport firstTransport = new CaptureTransport(all.Take(split).ToList());
            GameHost first = new GameHost(firstTransport, new ListLog(), dataRoot: Path.Combine(Path.GetTempPath(), "achost-handover-captures"));
            await first.StartAsync();
            await first.Ended.WaitAsync(TimeSpan.FromMinutes(2));
            HandoverSnapshot snapshot = first.CreateHandover(DateTimeOffset.UtcNow);
            Assert.NotNull(snapshot);
            snapshot.Relay = firstTransport.SaveState();
            using (MemoryStream file = new MemoryStream())
            {
                snapshot.Write(file);
                file.Position = 0;
                snapshot = HandoverSnapshot.Read(file);
            }

            GameHost carried = await ReplayAsync(all.Skip(split), snapshot);

            Assert.NotNull(carried.CarriedOnFrom);
            Assert.NotEqual(0, carried.WorldState.ObjectCount);
            Assert.Equal(Describe(whole.WorldState), Describe(carried.WorldState));

            // Carried on, an enchantment is aged by the time since the first host heard of it: the
            // seconds the replays took, here.
            foreach (Enchantment enchantment in whole.WorldState.Character.Enchantments.Values)
            {
                Enchantment other = carried.WorldState.Character.Enchantments[enchantment.PackedId];
                Assert.InRange(enchantment.StartTime - other.StartTime, 0, 120);
            }

            await whole.DisposeAsync();
            await first.DisposeAsync();
            await carried.DisposeAsync();
        }

        // ------------------------------------------------------------------- describing a world

        /// <summary>
        /// Everything the world holds, a line a thing, read by reflection so that nothing a decoder
        /// keeps - now or later - is left out of the comparison. Only times are left out: when the
        /// host heard of something, and the order things arrived in, which a replay takes afresh.
        /// </summary>
        private static List<string> Describe(WorldState world)
        {
            List<string> lines = new List<string>
            {
                "server " + (world.ServerName ?? "(none)"),
                "channels " + string.Join(",", world.TurbineChannels.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")),
                "tells " + world.LastTellFrom + " / " + world.LastTellTo,
                "objects " + world.ObjectCount,
                "viewer " + world.ViewerCell.ToString("X8", CultureInfo.InvariantCulture),
                "out of view " + string.Join(",", world.OutOfView.Keys.OrderBy(id => id).Select(id => id.ToString("X8", CultureInfo.InvariantCulture))),
            };

            foreach (PropertyInfo property in Readable(typeof(CharacterState)))
                lines.Add("character." + property.Name + " " + Dump(property.GetValue(world.Character), 0));

            foreach (WorldObject obj in world.Objects.OrderBy(o => o.Id))
            {
                foreach (PropertyInfo property in Readable(typeof(WorldObject)))
                    lines.Add($"0x{obj.Id:X8}.{property.Name} {Dump(property.GetValue(obj), 0)}");
            }

            return lines;
        }

        private static readonly HashSet<string> Unkept = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(WorldObject.LastSeen),
            nameof(WorldObject.ArrivalOrder),
            nameof(CharacterState.Object),

            // A count whose movement alone means anything - the client answering held keys - and
            // which a host carried on starts afresh, as the keys do.
            nameof(CharacterState.ClientReports),
            nameof(Enchantment.StartTime),
            nameof(Enchantment.RemainingWhenSent),
            "GameData",
        };

        private static IEnumerable<PropertyInfo> Readable(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && !Unkept.Contains(p.Name) && p.PropertyType != typeof(DateTimeOffset) && p.PropertyType != typeof(DateTimeOffset?))
                .OrderBy(p => p.Name, StringComparer.Ordinal);

        private static string Dump(object value, int depth)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string text:
                    return "\"" + text + "\"";
                case float f:
                    return f.ToString("R", CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("R", CultureInfo.InvariantCulture);
                case IFormattable formattable when value.GetType().IsPrimitive || value.GetType().IsEnum:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                case bool b:
                    return b ? "true" : "false";
                case IDictionary dictionary:
                    return "{" + string.Join(", ", dictionary.Keys.Cast<object>()
                        .Select(k => Dump(k, depth + 1) + ": " + Dump(dictionary[k], depth + 1))
                        .OrderBy(s => s, StringComparer.Ordinal)) + "}";
                case IEnumerable items:
                    IEnumerable<string> dumped = items.Cast<object>().Select(i => Dump(i, depth + 1));
                    return "[" + string.Join(", ", value is IList ? dumped : dumped.OrderBy(s => s, StringComparer.Ordinal)) + "]";
            }

            if (depth > 4)
                return value.ToString();

            Type type = value.GetType();
            return type.Name + "{" + string.Join(", ", Readable(type).Select(p => p.Name + "=" + Dump(p.GetValue(value), depth + 1))) + "}";
        }
    }
}
