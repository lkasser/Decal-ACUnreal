using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Threading;
using AC.Host.Decoding;
using AC.Protocol;
using Decal.Compat;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Mag-Filter, built from Mag-nus's own sources against the stand-ins (third_party\Mag-Filter),
    /// run as a player installs it: dropped in Decal Compat's own folder, which it is loaded from
    /// with no registry entry, its Documents a folder of the test's. Its "/mf" commands are the
    /// ones the player's metas send - read from the metas where they are on this machine - and
    /// its logins go through the host: the next character chosen on the old client's character
    /// select is entered through IGameActions.EnterWorldAsync, and the lines it queues for after a
    /// login are run as the chat box runs them. Skipped where its sources were not fetched.
    /// </summary>
    [Collection(DecalCollection.Name)]
    public sealed class MagFilterTests : IDisposable
    {
        private const uint Zed = 0x50000002;
        private const uint Abe = 0x50000003;
        private const uint Moe = 0x50000004;

        /// <summary>The fourteen lines StipendsIB.met sends Mag-Filter, as it sends them.</summary>
        internal static readonly string[] MetaLines =
            Enumerable.Range(1, 10).Select(n => "/mf lncbi set " + n)
                .Concat(new[] { "/mf lmq clear", "/mf lmq add /vt start", "/mf lmq add /vt meta load StipendsIB", "/mf lmq add /vt opt set enablemeta true" })
                .ToArray();

        private readonly string _root = Path.Combine(Path.GetTempPath(), "achost-magfilter-" + Guid.NewGuid().ToString("N"));
        private MacroTestHost _host;
        private DecalCompatPlugin _decal;

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

        /// <summary>Where the test build puts Mag-Filter, when its sources were there to build.</summary>
        private static string Built => Path.Combine(AppContext.BaseDirectory, "mag-filter", "MagFilter.dll");

        private string Documents => Path.Combine(_root, "user", "Documents", "Decal Plugins", "Mag-Filter");

        /// <summary>
        /// Found in Decal Compat's folder with no registry entry, it starts as the network filter it
        /// is, named as Decal named it; "/mf" is a known command word; and its settings and error log
        /// go in the Documents it was given - its calls of the player's folders pointed at the
        /// host's, as a registered plugin's working copy has them.
        /// </summary>
        [SkippableFact]
        public void ItLoadsFromDecalCompatsFolderAndKeepsItsFilesInTheDocumentsItWasGiven()
        {
            DecalPluginEntry entry = Start();

            Assert.Equal("running", entry.Status);
            Assert.Equal("folder", entry.Source);
            Assert.Equal("Mag-Filter", entry.Name);
            Assert.Equal("1.0.0.0", entry.Version);
            Assert.Contains("mf", _decal.CommandWords);
            Assert.Contains(_host.Log.Lines, l => l.Contains("runs from a copy with its calls of the player's folders"));
            Assert.Contains(_host.Log.Lines, l => l.Contains("Decal plugin Mag-Filter 1.0.0.0 started"));

            Assert.True(_decal.TryCommand("/mf cssmfps 20"));
            Assert.True(_decal.TryCommand("/mf cssmfps 0"));
            Assert.True(File.Exists(Path.Combine(Documents, "Mag-Filter.xml")));
            Assert.Contains("<MaxFPS>0</MaxFPS>", File.ReadAllText(Path.Combine(Documents, "Mag-Filter.xml")));
            AssertNoExceptions();
        }

        /// <summary>
        /// Each line a meta sends it - the fourteen StipendsIB.met sends - is taken, and answered in
        /// the chat as Mag-Filter answers it. (The Virindi Tank repository reads a player's own metas
        /// for the lines they send other plugins.)
        /// </summary>
        [SkippableFact]
        public void TheMetasLinesAreTaken()
        {
            Start();

            foreach (string line in MetaLines)
                Assert.True(_decal.TryCommand(line), line + " was not taken");

            Assert.Contains(_host.ShownInGame, l => l == "<{Mag-Filter}>: Login Next Character set to index: 10");
            Assert.Contains(_host.ShownInGame, l => l == "<{Mag-Filter}>: Login Complete Message Queue cleared");
            Assert.Contains(_host.ShownInGame, l => l == "<{Mag-Filter}>: Login Complete Message Queue added: /vt meta load StipendsIB");

            // What it does not know, it leaves for whoever does.
            Assert.False(_decal.TryCommand("/mf nosuchthing"));
            AssertNoExceptions();
        }

        /// <summary>
        /// StipendsIB.met end to end: the next character chosen by its place, the login's commands
        /// queued, the character logged out. When the server lists the account's characters,
        /// Mag-Filter clicks that character's row and Enter on the old client's character select a
        /// second later - the host enters the world as it, counted by name as the old client listed
        /// them - and once the client has entered, the queued lines are run as the chat box runs
        /// them, so they reach Virindi Tank's commands, in order, as Mag-Filter typed them.
        /// </summary>
        [SkippableFact]
        public void TheNextCharacterIsEnteredThroughTheHostAndTheQueuedLinesRunAfterTheLogin()
        {
            Start();
            foreach (string line in new[] { "/mf lncbi set 2", "/mf lmq clear", "/mf lmq add /vt start", "/mf lmq add /vt meta load StipendsIB", "/mf lmq add /vt opt set enablemeta true" })
                Assert.True(_decal.TryCommand(line));

            // The server's answer to the meta's "/mt logout": the logoff, the list in its own order, the name.
            ServerLogsOff();
            Assert.True(WaitFor(() => _host.Actions.Calls.Count > 0, TimeSpan.FromSeconds(5)), "Mag-Filter chose no character.");
            Assert.Equal(new[] { $"enter 0x{Zed:X8}" }, _host.Actions.Calls);
            Assert.Empty(_host.ChatCommands);

            // The client enters, as the host's click on its own character select has it do.
            _host.Receive(AcMessage.Create(Opcodes.CharacterEnterWorldRequest, Array.Empty<byte>()), PacketDirection.Outbound);
            _host.Receive(new WireWriter(Opcodes.CharacterEnterWorld).U32(Zed).String16L("account").ToMessage(), PacketDirection.Outbound);
            _host.EnterWorld(Zed);
            Assert.True(WaitFor(() => _host.ChatCommands.Count >= 3, TimeSpan.FromSeconds(5)), "The queued lines were not run: " + string.Join(", ", _host.ChatCommands.Select(c => c.Text)));

            Assert.Equal(new[] { "/vt start", "/vt meta load stipendsib", "/vt opt set enablemeta true" }, _host.ChatCommands.Select(c => c.Text));
            Assert.All(_host.ChatCommands, c => Assert.Same(_decal, c.From));
            Assert.Empty(_host.Input.Held);
            Assert.Single(_host.Actions.Calls);
            AssertNoExceptions();
        }

        /// <summary>
        /// The default login character - by its place, for this server and account - is kept in its
        /// settings, and entered by the host when the client first connects, after the two clicks
        /// that skipped the old client's opening movies, which cannot be made and are said.
        /// </summary>
        [SkippableFact]
        public void TheDefaultCharacterIsEnteredWhenTheClientFirstConnects()
        {
            Start();
            ServerLogsOff();
            Assert.True(_decal.TryCommand("/mf dlcbi set 0"));
            Assert.Contains("CharacterIndex=\"0\"", File.ReadAllText(Path.Combine(Documents, "Mag-Filter.xml")));

            // The server's word, only at a first connection, that it has finished with the client's data.
            _host.Receive(AcMessage.Create(0xF7EA, Array.Empty<byte>()));
            Assert.True(WaitFor(() => _host.Actions.Calls.Count > 0, TimeSpan.FromSeconds(8)), "Mag-Filter entered no default character.");
            Assert.Equal(new[] { $"enter 0x{Abe:X8}" }, _host.Actions.Calls);
            Assert.Contains(_host.Log.Lines, l => l.Contains("posted mouse clicks to the game's window"));
            AssertNoExceptions();
        }

        /// <summary>
        /// "A character is still in the world": Mag-Filter clicks OK and Enter five times a second
        /// until the client asks to enter. The host enters as the character the client had named,
        /// once, and not again while that is under way.
        /// </summary>
        [SkippableFact]
        public void ACharacterStillInTheWorldIsTriedAgainOnce()
        {
            Start();
            ServerLogsOff();
            _host.Receive(new WireWriter(Opcodes.CharacterEnterWorld).U32(Moe).String16L("account").ToMessage(), PacketDirection.Outbound);
            _host.Receive(new WireWriter(Opcodes.CharacterError).U32(13).ToMessage());

            Assert.True(WaitFor(() => _host.Actions.Calls.Count > 0, TimeSpan.FromSeconds(5)), "Mag-Filter did not try again.");
            WaitFor(() => false, TimeSpan.FromSeconds(1));
            Assert.Equal(new[] { $"enter 0x{Moe:X8}" }, _host.Actions.Calls);

            // The client asking to enter is what stops it.
            _host.Receive(AcMessage.Create(Opcodes.CharacterEnterWorldRequest, Array.Empty<byte>()), PacketDirection.Outbound);
            AssertNoExceptions();
        }

        /// <summary>
        /// Nothing in the build handles an account's password: not a string, a member or a type
        /// of it names one. Its logins only choose a character, at a character list the account is
        /// already at.
        /// </summary>
        [SkippableFact]
        public void NothingInItHandlesAPassword()
        {
            Skip.IfNot(File.Exists(Built), "Mag-Filter was not built: run third_party\\Mag-Filter\\fetch.ps1, then build the tests again.");

            using FileStream stream = File.OpenRead(Built);
            using PEReader pe = new PEReader(stream);
            MetadataReader reader = pe.GetMetadataReader();
            List<string> names = new List<string>();
            names.AddRange(reader.TypeDefinitions.Select(h => reader.GetString(reader.GetTypeDefinition(h).Name)));
            names.AddRange(reader.MethodDefinitions.Select(h => reader.GetString(reader.GetMethodDefinition(h).Name)));
            names.AddRange(reader.FieldDefinitions.Select(h => reader.GetString(reader.GetFieldDefinition(h).Name)));
            names.AddRange(reader.MemberReferences.Select(h => reader.GetString(reader.GetMemberReference(h).Name)));
            for (UserStringHandle h = MetadataTokens.UserStringHandle(1); !h.IsNil && MetadataTokens.GetHeapOffset(h) < reader.GetHeapSize(HeapIndex.UserString); h = reader.GetNextHandle(h))
                names.Add(reader.GetUserString(h));

            Assert.True(names.Count > 100);
            Assert.DoesNotContain(names, n => n.Contains("password", StringComparison.OrdinalIgnoreCase) || n.Contains("credential", StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------- the rig

        /// <summary>
        /// Decal Compat over a host with a character in the world, reading no registry, with Mag-Filter
        /// put in its folder as tools\install-plugin.ps1 puts it: MagFilter\MagFilter.dll.
        /// </summary>
        private DecalPluginEntry Start()
        {
            Skip.IfNot(File.Exists(Built), "Mag-Filter was not built: run third_party\\Mag-Filter\\fetch.ps1, then build the tests again.");

            string folder = Path.Combine(_root, "folder");
            Directory.CreateDirectory(Path.Combine(folder, "MagFilter"));
            File.Copy(Built, Path.Combine(folder, "MagFilter", "MagFilter.dll"));

            _host = new MacroTestHost(dataRoot: Path.Combine(_root, "data"));
            _host.Settings["DecalCompat:Folder"] = folder;
            _host.Settings["DecalCompat:UserFolders"] = Path.Combine(_root, "user");
            _host.Settings["DecalCompat:Registry"] = "false";

            _decal = new DecalCompatPlugin(new FakeDecalRegistry());
            _decal.Startup(_host);
            _decal.Runtime.Later = null;

            return Assert.Single(_decal.Entries);
        }

        /// <summary>ACE logging the character off: CharacterLogOff, the account's characters in its own order and eleven slots, the server's name.</summary>
        private void ServerLogsOff()
        {
            _host.Receive(AcMessage.Create(Opcodes.CharacterLogOff, Array.Empty<byte>()));
            WireWriter list = new WireWriter(Opcodes.CharacterList).U32(0).U32(3);
            foreach ((string name, uint id) in new[] { ("Zed", Zed), ("Abe", Abe), ("Moe", Moe) })
                list.U32(id).String16L(name).U32(0);
            _host.Receive(list.U32(0).U32(11).String16L("account").U32(1).U32(1).ToMessage());
            _host.Receive(new WireWriter(Opcodes.ServerName).U32(12).U32(0).String16L("Test Server").ToMessage());
        }

        /// <summary>
        /// The host's ticks, about ten a second by the clock as the host's own come, until
        /// <paramref name="done"/>: Mag-Filter's timers are Windows Forms timers, which tick by the
        /// clock as the game thread's messages are pumped.
        /// </summary>
        private bool WaitFor(Func<bool> done, TimeSpan longest)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < longest)
            {
                _host.RaiseTick(TimeSpan.FromMilliseconds(100));
                if (done())
                    return true;
                Thread.Sleep(100);
            }

            return done();
        }

        private void AssertNoExceptions()
        {
            string path = Path.Combine(Documents, "Exceptions.txt");
            Assert.False(File.Exists(path), "Mag-Filter logged exceptions:\n" + (File.Exists(path) ? File.ReadAllText(path) : string.Empty));
        }
    }
}
