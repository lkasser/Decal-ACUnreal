using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AC.Dat;
using AC.Host.Decoding;
using AC.Host.Plugins;
using Decal.Compat;
using Xunit;
using HostWorldObject = AC.Host.World.WorldObject;

namespace AC.Host.Tests
{
    /// <summary>
    /// The player's own Mag-Tools, the DLL Decal's registry names, run as the player would run it:
    /// off in Decal's list, ticked on in this host's with the character in the world - in the test's
    /// own settings, never in Decal's registry - and its "/mt" commands run as Virindi Tank's metas
    /// run them. Its Documents are a folder of the test's (DecalCompat:UserFolders), so the player's
    /// own Mag-Tools files are never read or written. Skipped on a machine without Mag-Tools.
    /// </summary>
    [Collection(DecalCollection.Name)]
    public sealed class MagToolsTests : IDisposable
    {
        private const uint Cell = 0x7D640013;
        private const uint Mote = 0x80000201;
        private const uint Chips = 0x80000202;
        private const uint Key = 0x80000203;
        private const uint Contract = 0x80000204;
        private const uint Arrow = 0x80000205;
        private const uint Thrungus = 0x80000301;
        private const uint Chest = 0x80000302;
        private const uint Gateway = 0x80000303;
        private const uint CharacterY = 0x50000777;
        private const uint Corpse = 0x80000304;
        private const uint Journal = 0x80000305;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "achost-magtools-" + Guid.NewGuid().ToString("N"));
        private MacroTestHost _host;
        private DecalCompatPlugin _decal;
        private readonly List<int[]> _published = new List<int[]>();

        public void Dispose()
        {
            _decal?.Shutdown();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A copy still held by a context that has not finished unloading; temp is temp.
            }
        }

        private string Documents => Path.Combine(_root, "user", "Documents", "Decal Plugins", "Mag-Tools");

        /// <summary>
        /// Ticked on in the world, it starts clean - its window made, its four hotkeys the host's, its
        /// Status HUD rows on the host's Status HUD, its welcome in the chat - and keeps its files in
        /// the Documents it was given.
        /// </summary>
        [SkippableFact]
        public void TickedOnInTheWorldItStartsShowsItsWindowAndBindsItsHotkeys()
        {
            DecalPluginEntry entry = Start();

            Assert.Equal("running", entry.Status);
            Assert.Empty(entry.Faults);

            // Its window, from its own XML, through VVS.
            OverlayViewWindow window = Assert.Single(_decal.Views, w => w.Key == "Mag-Tools");
            Assert.Equal("Mag-Tools", window.View.Title);
            Assert.True(window.View.Controls.Count > 50);
            Assert.Empty(window.View.Warnings);

            // Its hotkeys, through Virindi Hotkey System, in the host's hotkey windows.
            string[] hotkeys = _decal.Hotkeys.Where(h => h.Source == "Mag-Tools").Select(h => h.Name).ToArray();
            Assert.Contains("One Touch Heal", hotkeys);
            Assert.Contains("Maximize Chat", hotkeys);
            Assert.Contains("Minimize Chat", hotkeys);

            // Its Status HUD rows, through Virindi HUDs.
            Assert.Contains("Mag-Tools - Mana", _host.StatusRows.Keys);
            Assert.Contains("Mag-Tools - DPS Out 1m", _host.StatusRows.Keys);

            // "/mt" typed by the player is kept from the server and given to it.
            Assert.Contains("mt", _decal.CommandWords);

            Assert.Contains(_host.ShownInGame, l => l.StartsWith("<{Mag-Tools}>: Plugin now online.", StringComparison.Ordinal));
            // VTClassic aside: this test's process has the host's own loaded, which a Decal plugin is
            // given; the host keeps it in Virindi Tank's plugin, and there Mag-Tools says at login
            // that its inventory packer could not load.
            Assert.DoesNotContain(_host.ShownInGame, l => l.Contains("Startup Error", StringComparison.Ordinal) && !l.Contains("VTClassic", StringComparison.Ordinal));
            Assert.True(Directory.Exists(Documents));
            Assert.False(File.Exists(Path.Combine(Documents, "Exceptions.txt")), "Mag-Tools logged exceptions:\n" + ReadExceptions());
        }

        /// <summary>
        /// The commands the player's metas send Mag-Tools most, each to the action it asks for -
        /// uses by name, exact or partial, in the packs or nearby; a use of one thing on another;
        /// gifts; the stance; the fellowship; a drop, a loot, a logout.
        /// </summary>
        [SkippableFact]
        public void TheMetasCommandsReachTheHost()
        {
            Start();
            Populate();
            _host.WorldState.NotifyContainerViewed(new AC.Host.World.ContainerContents(Corpse, new[] { new AC.Host.World.ContainedItem(Journal, 0) }));

            (string Command, string[] Calls)[] cases =
            {
                ("/mt use Pyreal Mote", new[] { Use(Mote) }),
                ("/mt usep contract", new[] { Use(Contract) }),
                ("/mt usel Gateway", new[] { Use(Gateway) }),
                ("/mt useip pyreal", new[] { Use(Mote) }),
                ("/mt use closestnpc", new[] { Use(Thrungus) }),
                ("/mt use closestportal", new[] { Use(Gateway) }),
                ("/mt use Prison Warden's Key on Prison Warden's Chest", new[] { $"useon {Id(Key)} {Id(Chest)}" }),
                ("/mt give Bag of Life Stone Chips to Baby Thrungus", new[] { $"give {Id(Chips)} {Id(Thrungus)} 6" }),
                ("/mt givep life stone to baby", new[] { $"give {Id(Chips)} {Id(Thrungus)} 6" }),
                ("/mt combatstate magic", new[] { "stance Magic" }),
                ("/mt combatstate peace", new[] { "stance NonCombat" }),
                ("/mt fellow recruit Character Y", new[] { $"fellow recruit {Id(CharacterY)}" }),
                ("/mt fellow disband", new[] { "fellow disband" }),
                ("/mt fellow quit", new[] { "fellow quit" }),
                ("/mt drop Deadly Fire Arrow", new[] { $"drop {Id(Arrow)}" }),
                ("/mt loot Oswald's Prison Journal", new[] { Use(Journal) }),
                ("/mt selectp prison warden's ch", Array.Empty<string>()),
                ("/mt logout", new[] { "log out" }),
            };

            foreach ((string command, string[] calls) in cases)
            {
                _host.Actions.Calls.Clear();
                Assert.True(_decal.TryCommand(command), command + " was not taken");
                Assert.True(calls.SequenceEqual(_host.Actions.Calls), $"{command}: {string.Join(", ", _host.Actions.Calls)}");
            }

            // A spell by part of its name, from Decal's FileService, cast with no target - at the
            // character, here, where the host has no spell table of its own to say it takes none.
            global::Decal.Filters.SpellTable spells = _decal.Runtime.Core.Filter<global::Decal.Filters.FileService>().SpellTable;
            global::Decal.Filters.Spell summon = Enumerable.Range(0, spells.Length).Select(i => spells[i]).FirstOrDefault(s => s.Name.Contains("Summon Primary", StringComparison.OrdinalIgnoreCase));
            if (summon != null)
            {
                _host.Actions.Calls.Clear();
                Assert.True(_decal.TryCommand("/mt castp Summon Primary"));
                Assert.Equal(new[] { $"cast {summon.Id} {Id(MacroTestHost.PlayerId)}" }, _host.Actions.Calls);
            }

            // What it does not know, it leaves for whoever does - the game, here.
            Assert.False(_decal.TryCommand("/mt use Nothing Of The Sort"));
            Assert.False(File.Exists(Path.Combine(Documents, "Exceptions.txt")), "Mag-Tools logged exceptions:\n" + ReadExceptions());
        }

        /// <summary>
        /// "/mt face 135" turns the character by the game's turn keys until the client says it faces
        /// that way; "/mt send enter" and "/mt jumpw 400" press the keys Mag-Tools posts to the client's
        /// window, in order - the jump's space held, then let go with W down; "/mt click yes" clicks
        /// at the old client's dialog, which cannot be done, and says so.
        /// </summary>
        [SkippableFact]
        public void ItsTurnsAndKeysAreTheGamesOwnKeys()
        {
            Start();

            Assert.True(_decal.TryCommand("/mt face 135"));
            Assert.Equal(new[] { GameKey.TurnRight }, _host.Input.Held);
            Tick(8);
            Assert.Empty(_host.Input.Held);
            _host.PlaceCharacter(Cell, 50, 50, heading: 133);
            Tick(1);
            Assert.Empty(_host.Input.Held);

            _published.Clear();
            Assert.True(_decal.TryCommand("/mt send enter"));
            Tick(3);
            Assert.Equal(new[] { new[] { 13 }, Array.Empty<int>() }, _published);

            // The jump's release comes from a Windows Forms timer of Mag-Tools' own, on the clock.
            _published.Clear();
            Assert.True(_decal.TryCommand("/mt jumpw 400"));
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5) && (_published.Count == 0 || _published[^1].Length != 0 || !_published.Any(k => k.Contains('W'))))
            {
                Tick(1);
                Thread.Sleep(20);
            }

            Assert.Equal(new[] { new[] { 0x20 }, new[] { 0x20, 'W' }, new[] { (int)'W' }, Array.Empty<int>() }, _published);
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(400));

            Assert.True(_decal.TryCommand("/mt click yes"));
            Tick(1);
            Assert.Contains(_host.Log.Lines, l => l.Contains("posted mouse clicks to the game's window"));
        }

        /// <summary>
        /// "/mt jumpw 100" with the host's ticks a tenth of a second apart and its timer between
        /// them, as live: space is held about the tenth of a second Mag-Tools asked for, not three
        /// ticks - one for each of the changes it posted together, W down, space up and W up - and
        /// those three still go far enough apart for the game to see W down before space comes up.
        /// </summary>
        [SkippableFact]
        public void ItsTimedJumpHoldsSpaceForTheTimeItAsked()
        {
            Start();
            ConcurrentQueue<Action> due = new ConcurrentQueue<Action>();
            _decal.Runtime.Later = (delay, action) => _ = Task.Delay(delay).ContinueWith(_ => due.Enqueue(action), TaskScheduler.Default);
            List<(int[] Keys, TimeSpan At)> pressed = new List<(int[], TimeSpan)>();
            Stopwatch clock = Stopwatch.StartNew();
            _host.Input.Publish = keys => pressed.Add((keys.ToArray(), clock.Elapsed));

            Assert.True(_decal.TryCommand("/mt jumpw 100"));
            TimeSpan ticked = TimeSpan.Zero;
            while (clock.Elapsed < TimeSpan.FromSeconds(5) && (pressed.Count == 0 || pressed[^1].Keys.Length != 0 || !pressed.Any(p => p.Keys.Contains('W'))))
            {
                // The game thread: the host's timer's work as it comes due, and a tick every tenth of a second.
                while (due.TryDequeue(out Action action))
                    action();
                if (clock.Elapsed - ticked >= TimeSpan.FromMilliseconds(100))
                {
                    ticked = clock.Elapsed;
                    Tick(1);
                }

                Thread.Sleep(5);
            }

            Assert.Equal(new[] { new[] { 0x20 }, new[] { 0x20, 'W' }, new[] { (int)'W' }, Array.Empty<int>() }, pressed.Select(p => p.Keys));
            TimeSpan held = pressed[2].At - pressed[0].At;
            Assert.InRange(held, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));
            Assert.True(pressed[2].At - pressed[1].At >= ClientWindowApart - TimeSpan.FromMilliseconds(5), $"W went down {(pressed[2].At - pressed[1].At).TotalMilliseconds} ms before space came up");
            Assert.True(pressed[3].At - pressed[2].At >= ClientWindowApart - TimeSpan.FromMilliseconds(5));
        }

        private static TimeSpan ClientWindowApart => global::Decal.Adapter.Hosting.ClientWindow.Apart;

        /// <summary>
        /// With the player's looting settings - chests, and their own corpse - its looter asks
        /// Virindi Tank's loot rules about what is in a chest, is told nothing is kept, says so and
        /// stops; and empties the character's own corpse, which asks no rule. Before the uTank2
        /// stand-in, every look it took failed to compile, ten times a second, into its error log and
        /// the chat.
        /// </summary>
        [SkippableFact]
        public void ItsLooterEmptiesTheOwnCorpseAndFindsNothingTheRulesKeepInAChest()
        {
            Start("<Looting><AutoLootMyCorpse>True</AutoLootMyCorpse><AutoLootChests>True</AutoLootChests></Looting>");
            Populate();

            _host.AddObject(Journal, "Oswald's Prison Journal", o => o.ContainerId = Chest);
            _host.WorldState.NotifyContainerViewed(new AC.Host.World.ContainerContents(Chest, new[] { new AC.Host.World.ContainedItem(Journal, 0) }));
            Tick(1);
            Assert.Contains(_host.ShownInGame, l => l.Contains("No more lootable items found.", StringComparison.Ordinal));
            Assert.DoesNotContain(_host.Actions.Calls, c => c.StartsWith("move", StringComparison.Ordinal));
            _host.WorldState.NotifyContainerClosed(Chest);

            const uint MyCorpse = 0x80000306;
            Near(MyCorpse, "Corpse of Tester", ItemTypes.Container, DescriptionFlags.Corpse, 50);
            _host.AddObject(Journal, "Oswald's Prison Journal", o => o.ContainerId = MyCorpse);
            _host.Actions.Calls.Clear();
            _host.WorldState.NotifyContainerViewed(new AC.Host.World.ContainerContents(MyCorpse, new[] { new AC.Host.World.ContainedItem(Journal, 0) }));

            // It looks once a tenth of a second by the clock.
            Thread.Sleep(150);
            Tick(1);
            Assert.Equal(new[] { $"move {Id(Journal)} {Id(MacroTestHost.PlayerId)} 0" }, _host.Actions.Calls);

            Assert.False(File.Exists(Path.Combine(Documents, "Exceptions.txt")), "Mag-Tools logged exceptions:\n" + ReadExceptions());
        }

        /// <summary>
        /// A command Mag-Tools runs at login - as the player's "/fixcombat" every five minutes - goes
        /// first to the plugins through Decal.dll's DispatchOnChatCommand, and, none taking it, on
        /// to the game as the chat box would send it, through the host; its own "/mt" ones it runs
        /// itself.
        /// </summary>
        [SkippableFact]
        public void ItsLoginCommandsGoOnToTheGameThroughTheHost()
        {
            Start("<_Test_x0020_Server><OnLoginCommands><Command>/fixcombat</Command><Command>/mt combatstate magic</Command></OnLoginCommands></_Test_x0020_Server>");
            Tick(5);

            Assert.Equal(new[] { "/fixcombat" }, _host.ChatCommands.Select(c => c.Text));
            Assert.Same(_decal, _host.ChatCommands[0].From);
            Assert.Contains("stance Magic", _host.Actions.Calls);
            Assert.False(File.Exists(Path.Combine(Documents, "Exceptions.txt")), "Mag-Tools logged exceptions:\n" + ReadExceptions());
        }

        /// <summary>
        /// Its item info - the line it prints when the player appraises something - for a worn
        /// piece whose appraisal lists, besides its own spells, one on it now: the spell book marks
        /// that one with the top bit, which Decal kept apart as an active spell. Given the marked id
        /// as one of its spells, Mag-Tools found no such spell and threw, for every piece of armour.
        /// </summary>
        [SkippableFact]
        public void ItsItemInfoReadsAWornPiecesSpellsAndNotThoseOnIt()
        {
            Start();
            global::Decal.Filters.SpellTable spells = _decal.Runtime.Core.Filter<global::Decal.Filters.FileService>().SpellTable;
            Skip.If(spells.Length < 2, "No portal file on this machine to name spells from.");
            global::Decal.Filters.Spell own = spells[0];
            global::Decal.Filters.Spell onIt = spells[1];

            const uint Coat = 0x80000401;
            _host.AddObject(Coat, "Gromnie Hide Coat", o =>
            {
                o.ItemType = ItemTypes.Armor;
                o.WielderId = MacroTestHost.PlayerId;
                o.Ints[28] = 210;
                o.SpellIds.Add((uint)own.Id);
                o.SpellIds.Add((uint)onIt.Id | 0x80000000);
                o.HasAppraisalData = true;
                o.AppraisalSucceeded = true;
            });

            global::Decal.Adapter.Wrappers.WorldObject coat = _decal.Runtime.Core.WorldFilter[unchecked((int)Coat)];
            Assert.Equal(1, coat.SpellCount);
            Assert.Equal(own.Id, coat.Spell(0));
            Assert.Equal(1, coat.ActiveSpellCount);
            Assert.Equal(onIt.Id, coat.ActiveSpell(0));
            Assert.Equal(1, coat.Values(global::Decal.Adapter.Wrappers.LongValueKey.SpellCount));
            Assert.Equal(1, coat.Values(global::Decal.Adapter.Wrappers.LongValueKey.ActiveSpellCount));

            Type itemInfo = _decal.Find("Mag-Tools").Context.MainAssembly.GetType("MagTools.ItemInfo.ItemInfo", throwOnError: true);
            string line = Activator.CreateInstance(itemInfo, coat).ToString();
            Assert.StartsWith("Gromnie Hide Coat, AL 210", line, StringComparison.Ordinal);
            Assert.Contains(", " + own.Name, line, StringComparison.Ordinal);
        }

        /// <summary>
        /// Its "Show Item Info On Ident" prints what the player selected and had appraised - as
        /// Decal told it, ItemSelected for the client's selection, then the appraisal - and never
        /// the appraisals Virindi Tank makes of the character's gear for its mana: Decal raised no
        /// ItemSelected for a plugin's RequestId, and the host's are not the player's selection.
        /// </summary>
        [SkippableFact]
        public void ItPrintsItemInfoForWhatThePlayerAppraisesAndNotForTheHosts()
        {
            Start();
            const uint Ring = 0x80000402;
            _host.AddObject(Ring, "Ivory Ring", o =>
            {
                o.ItemType = ItemTypes.Jewelry;
                o.WielderId = MacroTestHost.PlayerId;
            });

            // Virindi Tank's mana upkeep: the host asks, the server answers.
            Apply(Question(Ring, 41), fromHost: true);
            Apply(Answer(Ring));
            Tick(2);
            Assert.Equal(0u, _host.WorldState.Character.SelectedId);
            Assert.DoesNotContain(_host.ShownInGame, l => l.Contains("Ivory Ring", StringComparison.Ordinal));

            // The player selects it, and the client asks.
            Apply(Question(Ring, 42), fromHost: false);
            Apply(Answer(Ring));
            Tick(2);
            Assert.Equal(Ring, _host.WorldState.Character.SelectedId);
            Assert.Contains(_host.ShownInGame, l => l.Contains("Ivory Ring", StringComparison.Ordinal));

            Assert.False(File.Exists(Path.Combine(Documents, "Exceptions.txt")), "Mag-Tools logged exceptions:\n" + ReadExceptions());
        }

        /// <summary>An IdentifyObject going to the server: the ordering sequence, the action, the object.</summary>
        private static AC.Protocol.AcMessage Question(uint id, uint sequence)
            => new WireWriter(Opcodes.GameAction).U32(sequence).U32(GameActions.IdentifyObject).U32(id).ToMessage();

        /// <summary>The server's appraisal of an object: an armour level, and that it succeeded.</summary>
        private static AC.Protocol.AcMessage Answer(uint id)
            => WireWriter.GameEvent(MacroTestHost.PlayerId, GameEvents.IdentifyObjectResponse)
                .U32(id).U32(0x0001).U32(1)
                .IntTable(new Dictionary<uint, int> { [28] = 30 })
                .ToMessage();

        private void Apply(AC.Protocol.AcMessage message, bool fromHost = false)
            => MessageDecoder.Apply(message, message.Opcode == Opcodes.GameAction ? AC.Protocol.PacketDirection.Outbound : AC.Protocol.PacketDirection.Inbound, _host.WorldState, fromHost);

        // ------------------------------------------------------------------- the rig

        /// <summary>
        /// Decal Compat over a host with the character in the world, Mag-Tools registered as Decal's
        /// registry has it - off - and then ticked on in this host, as the player would; with the
        /// settings given, as Mag-Tools.xml has them, in its Documents first.
        /// </summary>
        private DecalPluginEntry Start(string settings = null)
        {
            DecalPlugin installed = DecalInstall.Detect().FindPlugin("Mag-Tools");
            Skip.If(installed?.FullPath == null || !File.Exists(installed.FullPath), "Mag-Tools is not installed on this machine.");

            _host = new MacroTestHost(dataRoot: Path.Combine(_root, "data"));
            _host.Settings["DecalCompat:Folder"] = Path.Combine(_root, "folder");
            _host.Settings["DecalCompat:UserFolders"] = Path.Combine(_root, "user");
            _host.Input.Publish = keys => _published.Add(keys.ToArray());
            _host.PlaceCharacter(Cell, 50, 50, heading: 0);

            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new DecalRegistryEntry(installed.Clsid, "Mag-Tools", installed.Directory, installed.AssemblyFileName, enabled: false, objectType: "MagTools.PluginCore"));

            if (settings != null)
            {
                Directory.CreateDirectory(Documents);
                File.WriteAllText(Path.Combine(Documents, "Mag-Tools.xml"), "<Mag-Tools>" + settings + "</Mag-Tools>");
            }

            _decal = new DecalCompatPlugin(registry);
            _decal.Startup(_host);

            // The test's ticks are the clock: turn keys let go on the tick, not by a timer of their own.
            _decal.Runtime.Later = null;

            DecalPluginEntry entry = _decal.Find("Mag-Tools");
            Assert.Equal("off", entry.Status);
            _decal.SetEnabled(entry, true);
            return entry;
        }

        /// <summary>The packs and the place around the character, as the metas expect them.</summary>
        private void Populate()
        {
            Carried(Mote, "Pyreal Mote", ItemTypes.Misc);
            Carried(Chips, "Bag of Life Stone Chips", ItemTypes.Misc).StackSize = 6;
            Carried(Key, "Prison Warden's Key", ItemTypes.Key);
            Carried(Contract, "Contract for Kill: Rynthid Minions", ItemTypes.Writable);
            Carried(Arrow, "Deadly Fire Arrow", ItemTypes.MissileWeapon);

            Near(Thrungus, "Baby Thrungus", ItemTypes.Creature, 0, 52);
            Near(Chest, "Prison Warden's Chest", ItemTypes.Container, 0, 51);
            Near(Gateway, "Gateway", ItemTypes.Portal, DescriptionFlags.Portal, 55);
            Near(CharacterY, "Character Y", ItemTypes.Creature, DescriptionFlags.Player, 53);
            Near(Corpse, "Corpse of Drudge Slinker", ItemTypes.Container, DescriptionFlags.Corpse, 54);
            _host.AddObject(Journal, "Oswald's Prison Journal", o =>
            {
                o.ItemType = ItemTypes.Writable;
                o.ContainerId = Corpse;
            });
        }

        private HostWorldObject Carried(uint id, string name, uint type)
            => _host.AddObject(id, name, o =>
            {
                o.ItemType = type;
                o.ContainerId = MacroTestHost.PlayerId;
            });

        private void Near(uint id, string name, uint type, uint flags, float x)
            => _host.AddObject(id, name, o =>
            {
                o.ItemType = type;
                o.DescriptionFlags = flags;
                o.Location = MacroTestHost.At(Cell, x, 50);
            });

        private void Tick(int ticks)
        {
            for (int i = 0; i < ticks; i++)
                _host.RaiseTick(TimeSpan.FromMilliseconds(100));
        }

        private string ReadExceptions()
        {
            string path = Path.Combine(Documents, "Exceptions.txt");
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }

        private static string Id(uint id) => $"0x{id:X8}";

        private static string Use(uint id) => "use " + Id(id);
    }
}
