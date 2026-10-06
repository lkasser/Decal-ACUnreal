using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Actions;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using Decal.Adapter;
using Decal.Adapter.Hosting;
using Decal.Adapter.Wrappers;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Decal's hooks that Mag-Tools' commands go through, over the host: the fellowship's
    /// buttons, a use on what a plugin selected, a spell with no target, the container last
    /// opened, a turn to a heading by the game's keys, keys posted to Decal's window, a line run
    /// as the chat box would, and a plugin switched on after the others started.
    /// </summary>
    [Collection(DecalCollection.Name)]
    public sealed class DecalHooksTests
    {
        private const uint Chest = 0x80000301;
        private const uint Key = 0x80000302;
        private const uint CharacterY = 0x50000777;

        // ------------------------------------------------------------------- the fellowship

        [Fact]
        public void TheFellowshipHooksAreTheFellowshipPanelsButtons()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            HooksWrapper hooks = runtime.Core.Actions;

            hooks.FellowshipRecruit(unchecked((int)CharacterY));
            hooks.FellowshipQuit();
            hooks.FellowshipDisband();
            hooks.FellowshipDismiss(unchecked((int)CharacterY));
            hooks.FellowshipGrantLeader(unchecked((int)CharacterY));
            hooks.FellowshipSetOpen(true);

            Assert.Equal(new[]
            {
                $"fellow recruit 0x{CharacterY:X8}", "fellow quit", "fellow disband", $"fellow dismiss 0x{CharacterY:X8}", $"fellow leader 0x{CharacterY:X8}", "fellow open",
            }, host.Actions.Calls);
        }

        /// <summary>Laid out as ACE's handlers read them: the action, then a player's id or a word.</summary>
        [Fact]
        public async Task TheFellowshipActionsAreLaidOutAsAcesHandlersReadThem()
        {
            SendingTransport transport = new SendingTransport();
            ClientActions actions = new ClientActions(transport, new ListLog(), new WorldState());

            Assert.True(await actions.FellowshipRecruitAsync(0x50000777));
            Assert.True(await actions.FellowshipQuitAsync(disband: true));
            Assert.True(await actions.FellowshipQuitAsync(disband: false));
            Assert.True(await actions.FellowshipDismissAsync(0x50000777));
            Assert.True(await actions.FellowshipAssignLeaderAsync(0x50000777));
            Assert.True(await actions.FellowshipSetOpenAsync(open: false));

            Assert.Equal(new[]
            {
                "A5000000" + "77070050",
                "A3000000" + "01000000",
                "A3000000" + "00000000",
                "A4000000" + "77070050",
                "90020000" + "77070050",
                "91020000" + "00000000",
            }, transport.Sent.Select(AfterSequence));
        }

        // ------------------------------------------------------------------- selecting, using, casting

        /// <summary>
        /// Mag-Tools' "/mt use key on chest" selects the chest and uses the key on the selection:
        /// the client's selection cannot be set, but the plugin's choice stands for the use, and for
        /// CurrentSelection, until the player selects something in the client.
        /// </summary>
        [Fact]
        public void AUseOnTheSelectionGoesToWhatThePluginSelected()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            host.AddObject(Chest, "Prison Warden's Chest");
            host.AddObject(Key, "Prison Warden's Key", o => o.ContainerId = MacroTestHost.PlayerId);
            HooksWrapper hooks = runtime.Core.Actions;

            hooks.SelectItem(unchecked((int)Chest));
            hooks.UseItem(unchecked((int)Key), 1, unchecked((int)Chest));
            Assert.Equal(unchecked((int)Chest), hooks.CurrentSelection);

            // The player selects something else: the client's own selection is the selection again.
            host.WorldState.SetClientSelection(CharacterY);
            hooks.UseItem(unchecked((int)Key), 1);
            hooks.UseItem(unchecked((int)Key), 0);

            Assert.Equal(new[] { $"useon 0x{Key:X8} 0x{Chest:X8}", $"useon 0x{Key:X8} 0x{CharacterY:X8}", $"use 0x{Key:X8}" }, host.Actions.Calls);
            Assert.Equal(unchecked((int)CharacterY), hooks.CurrentSelection);
        }

        /// <summary>A spell with nothing named, as Mag-Tools' "/mt castp" casts: at the character itself, unless it takes no target.</summary>
        [Fact]
        public void ASpellWithNoTargetIsCastAtTheCharacter()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            runtime.Core.Actions.CastSpell(2, 0);
            runtime.Core.Actions.CastSpell(2, unchecked((int)CharacterY));

            Assert.Equal(new[] { $"cast 2 0x{MacroTestHost.PlayerId:X8}", $"cast 2 0x{CharacterY:X8}" }, host.Actions.Calls);
        }

        /// <summary>The chest or corpse last opened is OpenedContainer, where "/mt loot" looks, until it closes; a pack is not.</summary>
        [Fact]
        public void TheOpenedContainerIsTheChestLastOpenedUntilItCloses()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            host.AddObject(Chest, "Prison Warden's Chest");
            host.AddObject(Key, "Pack", o => o.ContainerId = MacroTestHost.PlayerId);
            HooksWrapper hooks = runtime.Core.Actions;
            Assert.Equal(0, hooks.OpenedContainer);

            host.WorldState.NotifyContainerViewed(new ContainerContents(Chest, Array.Empty<ContainedItem>()));
            Assert.Equal(unchecked((int)Chest), hooks.OpenedContainer);

            host.WorldState.NotifyContainerViewed(new ContainerContents(Key, Array.Empty<ContainedItem>()));
            Assert.Equal(unchecked((int)Chest), hooks.OpenedContainer);

            host.WorldState.NotifyContainerClosed(Chest);
            Assert.Equal(0, hooks.OpenedContainer);
        }

        // ------------------------------------------------------------------- turning

        /// <summary>
        /// "/mt face 90": the turn key toward the heading, held as long as the angle needs at the
        /// rate the character turns, then let go; the client's report as the key comes up says it
        /// faces the heading, and the turn is done.
        /// </summary>
        [Fact]
        public void FaceHeadingTurnsByTheGamesKeysUntilTheClientSaysItFacesTheHeading()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            host.PlaceCharacter(0x7D640013, 50, 50, heading: 0);

            Assert.True(runtime.Core.Actions.FaceHeading(90, true));
            Assert.Equal(new[] { GameKey.TurnRight }, host.Input.Held);

            // Half a second at 180 degrees a second: let go on the tick it ends in.
            Tick(host, 4);
            Assert.Equal(new[] { GameKey.TurnRight }, host.Input.Held);
            Tick(host, 1);
            Assert.Empty(host.Input.Held);

            // The client says where the key came up: 88 degrees, near enough.
            host.PlaceCharacter(0x7D640013, 50, 50, heading: 88);
            Tick(host, 1);
            Assert.Empty(host.Input.Held);
            Assert.Equal(88, runtime.Core.Actions.Heading, 3);

            // And back to the left, past north.
            Assert.True(runtime.Core.Actions.FaceHeading(315, true));
            Assert.Equal(new[] { GameKey.TurnLeft }, host.Input.Held);
        }

        /// <summary>A pulse that fell short is followed by another for what is left; a turn the client never answers is given up.</summary>
        [Fact]
        public void ATurnThatFallsShortTurnsAgainAndOneNeverAnsweredIsGivenUp()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            host.PlaceCharacter(0x7D640013, 50, 50, heading: 0);

            runtime.Core.Actions.FaceHeading(180, true);
            Tick(host, 10);
            Assert.Empty(host.Input.Held);

            // It turned only 120 of the 180 degrees: the rest, by another pulse.
            host.PlaceCharacter(0x7D640013, 50, 50, heading: 120);
            Tick(host, 1);
            Assert.Equal(new[] { GameKey.TurnRight }, host.Input.Held);

            // The client says nothing more: ten seconds on, the turn is given up and the key let go.
            Tick(host, 110);
            Assert.Empty(host.Input.Held);
            Assert.Contains(host.Log.Lines, l => l.Contains("gave up"));
        }

        [Fact]
        public void FaceHeadingWithoutTheGamesKeysSaysSoAndDoesNothing()
        {
            MacroTestHost host = new MacroTestHost();
            host.Input.Attached = () => false;
            using DecalRuntime runtime = new DecalRuntime(host);
            host.PlaceCharacter(0x7D640013, 50, 50, heading: 0);

            Assert.False(runtime.Core.Actions.FaceHeading(90, true));
            Assert.Empty(host.Input.Held);
            Assert.Contains(host.Log.Lines, l => l.Contains("asked to face a heading while the game's keys cannot be pressed"));
        }

        // ------------------------------------------------------------------- Decal's window

        /// <summary>
        /// Keys a plugin posts to Decal.Hwnd - Mag-Tools' "/mt send enter", its jumps - are pressed
        /// in the game by their codes, in the order posted, one change a tick; a click is said once
        /// and dropped, and so is a request to close the window.
        /// </summary>
        [Fact]
        public void KeysPostedToDecalsWindowArePressedInTheGameOneChangeATick()
        {
            if (!OperatingSystem.IsWindows())
                return;

            MacroTestHost host = new MacroTestHost();
            List<int[]> published = new List<int[]>();
            host.Input.Publish = keys => published.Add(keys.ToArray());
            using DecalRuntime runtime = new DecalRuntime(host);
            IntPtr window = runtime.Core.Decal.Hwnd;
            Assert.NotEqual(IntPtr.Zero, window);

            // Space held, then W down, space up and W up at once: a jump forward.
            PostMessageW(window, WmKeyDown, (IntPtr)0x20, IntPtr.Zero);
            PostMessageW(window, WmKeyDown, (IntPtr)'W', IntPtr.Zero);
            PostMessageW(window, WmKeyUp, (IntPtr)0x20, IntPtr.Zero);
            PostMessageW(window, WmKeyUp, (IntPtr)'W', IntPtr.Zero);
            PostMessageW(window, WmLButtonDown, IntPtr.Zero, IntPtr.Zero);
            PostMessageW(window, WmDestroy, IntPtr.Zero, IntPtr.Zero);
            Tick(host, 5);

            Assert.Equal(new[] { new[] { 0x20 }, new[] { 0x20, 'W' }, new[] { (int)'W' }, Array.Empty<int>() }, published);
            Assert.Contains(host.Log.Lines, l => l.Contains("posted mouse clicks to the game's window"));
            Assert.Contains(host.Log.Lines, l => l.Contains("asked the game's window to close"));
            Assert.Equal(0, runtime.ClientWindow.Waiting);
        }

        [Fact]
        public void KeysPostedWhileTheGamesKeysCannotBePressedAreDropped()
        {
            if (!OperatingSystem.IsWindows())
                return;

            MacroTestHost host = new MacroTestHost();
            host.Input.Allowed = () => false;
            using DecalRuntime runtime = new DecalRuntime(host);

            PostMessageW(runtime.Core.Decal.Hwnd, WmKeyDown, (IntPtr)13, IntPtr.Zero);
            PostMessageW(runtime.Core.Decal.Hwnd, WmKeyUp, (IntPtr)13, IntPtr.Zero);
            Tick(host, 2);

            Assert.Empty(host.Input.HeldKeys);
            Assert.Equal(0, runtime.ClientWindow.Waiting);
            Assert.Contains(host.Log.Lines, l => l.Contains("while the game's keys cannot be pressed"));
        }

        /// <summary>
        /// A line a plugin types into the chat - Enter, the line's keys, and a tick later the Enter
        /// that sends it, as Mag-Filter types the commands it queues for a login - is run as the
        /// chat box runs a line, keys and all never pressed; Enter alone, and keys with no Enter
        /// after them in time, are pressed as keys.
        /// </summary>
        [Fact]
        public void ALineTypedIntoTheChatIsRunAsTheChatBoxRunsOne()
        {
            if (!OperatingSystem.IsWindows())
                return;

            MacroTestHost host = new MacroTestHost();
            List<int[]> published = new List<int[]>();
            host.Input.Publish = keys => published.Add(keys.ToArray());
            using DecalRuntime runtime = new DecalRuntime(host);
            runtime.Owner = new NamedPlugin();
            List<string> offered = new List<string>();
            runtime.Core.CommandLineText += (_, e) => offered.Add(e.Text);
            IntPtr window = runtime.Core.Decal.Hwnd;

            // As Mag-Filter's PostMessageTools types: the keys' codes, a key-down and a key-up each.
            PostKeys(window, 13);
            PostKeys(window, "/VT META LOAD STIPENDSIB".Select(c => c == '/' ? 0xBF : (int)c).ToArray());
            Tick(host, 3);
            Assert.Empty(published);
            Assert.Empty(host.ChatCommands);

            PostKeys(window, 13);
            Tick(host, 2);
            Assert.Empty(published);
            Assert.Equal(new[] { "/vt meta load stipendsib" }, offered);
            Assert.Equal(new[] { "/vt meta load stipendsib" }, host.ChatCommands.Select(c => c.Text));
            Assert.Equal(0, runtime.ClientWindow.Waiting);
            Assert.Contains(host.Log.Lines, l => l.Contains("typed \"/vt meta load stipendsib\" into the chat"));

            // Shift makes capitals and the shifted keys' signs.
            PostKeys(window, 13);
            PostMessageW(window, WmKeyDown, (IntPtr)0x10, IntPtr.Zero);
            PostKeys(window, 'H', 'I', '1');
            PostMessageW(window, WmKeyUp, (IntPtr)0x10, IntPtr.Zero);
            PostKeys(window, 13);
            Tick(host, 2);
            Assert.Equal("HI!", host.ChatCommands[^1].Text);

            // Enter alone is a key.
            PostKeys(window, 13);
            Tick(host, 3);
            Assert.Equal(new[] { new[] { 13 }, Array.Empty<int>() }, published);

            // Enter and keys with no Enter to send them, in time, are keys after all.
            published.Clear();
            PostKeys(window, 13, 'U');
            Tick(host, ClientWindow.LineWaitTicks + 5);
            Assert.Equal(new[] { new[] { 13 }, Array.Empty<int>(), new[] { (int)'U' }, Array.Empty<int>() }, published);
            Assert.Equal(2, host.ChatCommands.Count);
        }

        /// <summary>
        /// At the character list, a plugin's clicks on the old client's character select - a row,
        /// then Enter, where the retail client had them - enter the world as the character on that
        /// row through the host, rows counted by name and sized by the account's slots. Enter clicked
        /// again while that is under way asks nothing more; with no row clicked it is the character
        /// the client last named; and in the world, a click is still only said.
        /// </summary>
        [Fact]
        public void ClicksOnTheOldCharacterSelectEnterTheWorldThroughTheHost()
        {
            if (!OperatingSystem.IsWindows())
                return;

            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            IntPtr window = runtime.Core.Decal.Hwnd;

            // In the world: nothing is entered, and the click is said once.
            Click(window, 121, 223);
            Tick(host, 1);
            Assert.Empty(host.Actions.Calls);
            Assert.Contains(host.Log.Lines, l => l.Contains("posted mouse clicks to the game's window"));

            // At the character list, the server listing them in an order of its own; eleven slots.
            host.Receive(AcMessage.Create(Opcodes.CharacterLogOff, Array.Empty<byte>()));
            host.Receive(Characters(11, ("Zed", 0x50000002), ("Abe", 0x50000003), ("Moe", 0x50000004)).ToMessage());
            Assert.Equal(11, runtime.Core.CharacterFilter.CharacterSlots);

            // Mag-Filter's third row, by its own reckoning: 209 down, then two and a half rows of a
            // 323-pixel list shared among the eleven slots.
            Click(window, 121, 282);
            Click(window, 0x15C, 0x185);
            Tick(host, 1);
            Assert.Equal(new[] { "enter 0x50000002" }, host.Actions.Calls);
            Assert.Contains(host.Log.Lines, l => l.Contains("chose Zed (0x50000002) on the old client's character select (row 3 of 3, by name)"));

            // Its retries, five a second, while the host's entering goes on.
            Click(window, 0x15C, 0x185);
            Tick(host, 1);
            Assert.Single(host.Actions.Calls);

            // Later, with no row clicked: the one the client last asked to enter as.
            Tick(host, ClientWindow.EnterAgainTicks);
            host.Receive(new WireWriter(Opcodes.CharacterEnterWorld).U32(0x50000003).String16L("account").ToMessage(), PacketDirection.Outbound);
            host.Receive(new WireWriter(Opcodes.CharacterError).U32(13).ToMessage());
            Click(window, 0x15C, 0x185);
            Tick(host, 1);
            Assert.Equal(new[] { "enter 0x50000002", "enter 0x50000003" }, host.Actions.Calls);
        }

        private static WireWriter Characters(uint slots, params (string Name, uint Id)[] characters)
        {
            WireWriter list = new WireWriter(Opcodes.CharacterList).U32(0).U32((uint)characters.Length);
            foreach ((string name, uint id) in characters)
                list.U32(id).String16L(name).U32(0);
            return list.U32(0).U32(slots).String16L("account").U32(1).U32(1);
        }

        private static void PostKeys(IntPtr window, params int[] keys)
        {
            foreach (int key in keys)
            {
                PostMessageW(window, WmKeyDown, (IntPtr)key, IntPtr.Zero);
                PostMessageW(window, WmKeyUp, (IntPtr)key, IntPtr.Zero);
            }
        }

        /// <summary>A click as Mag-Filter's PostMessageTools posts one: the pointer moved there, the button pressed and let go.</summary>
        private static void Click(IntPtr window, int x, int y)
        {
            IntPtr at = (IntPtr)((y << 16) | x);
            PostMessageW(window, 0x0200, IntPtr.Zero, at);
            PostMessageW(window, WmLButtonDown, (IntPtr)1, at);
            PostMessageW(window, 0x0202, IntPtr.Zero, at);
        }

        // ------------------------------------------------------------------- the chat box

        /// <summary>
        /// A line no Decal plugin takes - Mag-Tools' periodic "/fixcombat" - goes on as the chat box
        /// would send it, through the host, and is not offered back to Decal; one offered first
        /// through Decal.dll's DispatchOnChatCommand is not offered to the plugins twice.
        /// </summary>
        [Fact]
        public void ALineNoPluginTakesGoesOnThroughTheHostsChatBox()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);
            NamedPlugin owner = new NamedPlugin();
            runtime.Owner = owner;
            List<string> offered = new List<string>();
            runtime.Core.CommandLineText += (_, e) =>
            {
                offered.Add(e.Text);
                e.Eat = e.Text.StartsWith("/mine", StringComparison.Ordinal);
            };

            runtime.Core.Actions.InvokeChatParser("/fixcombat");
            Assert.Equal(1, DispatchOnChatCommand("/mine too"));
            Assert.Equal(0, DispatchOnChatCommand("/f hello"));
            runtime.Core.Actions.InvokeChatParser("/f hello");

            Assert.Equal(new[] { "/fixcombat", "/mine too", "/f hello" }, offered);
            Assert.Equal(new[] { ("/fixcombat", (IPlugin)owner), ("/f hello", owner) }, host.ChatCommands);
        }

        // ------------------------------------------------------------------- a plugin started late

        /// <summary>
        /// A plugin started while the host runs, with the character in the world, hears what the
        /// others heard when they started - PluginInitComplete, then Login, then LoginComplete. One
        /// started before the others were told hears nothing early.
        /// </summary>
        [Fact]
        public void APluginStartedLateIsToldEverythingIsUpAndTheCharacterHasLoggedIn()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            LatePlugin early = new LatePlugin();
            runtime.Start(early, AppContext.BaseDirectory);
            runtime.CatchUp(new Extension[] { early });
            Assert.Empty(early.Heard);

            runtime.CompleteStartup();
            Assert.Equal(new[] { "init", $"login 0x{MacroTestHost.PlayerId:X8}", "login complete" }, early.Heard);

            LatePlugin late = new LatePlugin();
            runtime.Start(late, AppContext.BaseDirectory);
            runtime.CatchUp(new Extension[] { late });
            Assert.Equal(new[] { "init", $"login 0x{MacroTestHost.PlayerId:X8}", "login complete" }, late.Heard);
        }

        /// <summary>
        /// The player ticks a registered plugin on in the middle of a session: it hears the login
        /// it missed - the troubled plugin's Login handler throws, and says so - and the plugins
        /// already running do not hear it again.
        /// </summary>
        [Fact]
        public void APluginTickedOnInTheWorldHearsTheLoginAndTheOthersDoNotAgain()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "achost-late-" + Guid.NewGuid().ToString("N"));
            MacroTestHost host = new MacroTestHost(dataRoot: root);
            host.Settings["DecalCompat:Folder"] = System.IO.Path.Combine(root, "folder");
            host.Settings["DecalCompat:UserFolders"] = System.IO.Path.Combine(root, "user");
            FakeDecalRegistry registry = new FakeDecalRegistry();
            registry.Plugins.Add(new AC.Dat.DecalRegistryEntry("{0B5C2E1D-7A4F-4C3B-9E21-5D6F7A8B9C0D}", "Troubled Plugin",
                System.IO.Path.Combine(AppContext.BaseDirectory, "decal-registered", "TroubledPlugin"), "TroubledPlugin.dll", enabled: false));

            global::Decal.Compat.DecalCompatPlugin decal = new global::Decal.Compat.DecalCompatPlugin(registry);
            decal.Startup(host);
            try
            {
                List<string> others = new List<string>();
                decal.Runtime.Core.PluginInitComplete += (_, _) => others.Add("init");
                decal.Runtime.Core.CharacterFilter.Login += (_, _) => others.Add("login");

                global::Decal.Compat.DecalPluginEntry entry = decal.Find("Troubled Plugin");
                decal.SetEnabled(entry, true);

                Assert.Equal("running, with errors", entry.Status);
                Assert.Contains("its Login handler failed: InvalidOperationException: Troubled at login.", entry.Detail);
                Assert.Empty(others);
            }
            finally
            {
                decal.Shutdown();
                try
                {
                    System.IO.Directory.Delete(root, recursive: true);
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
                {
                }
            }
        }

        // ------------------------------------------------------------------- helpers

        private const uint WmDestroy = 0x0002;
        private const uint WmKeyDown = 0x0100;
        private const uint WmKeyUp = 0x0101;
        private const uint WmLButtonDown = 0x0201;

        private static void Tick(MacroTestHost host, int ticks)
        {
            for (int i = 0; i < ticks; i++)
                host.RaiseTick(TimeSpan.FromMilliseconds(100));
        }

        private static int DispatchOnChatCommand(string line)
        {
            IntPtr text = Marshal.StringToBSTR(line);
            try
            {
                return DecalNative.DispatchOnChatCommand(ref text, 1);
            }
            finally
            {
                Marshal.FreeBSTR(text);
            }
        }

        private static string AfterSequence(AcMessage message)
        {
            Assert.Equal(Opcodes.GameAction, message.Opcode);
            return Convert.ToHexString(message.Payload.Span.Slice(4));
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        private sealed class NamedPlugin : IPlugin
        {
            public string Name => "DecalCompat";

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }
        }

        private sealed class LatePlugin : PluginBase
        {
            internal List<string> Heard { get; } = new List<string>();

            protected override void Startup()
            {
                Core.PluginInitComplete += (_, _) => Heard.Add("init");
                Core.CharacterFilter.Login += (_, e) => Heard.Add($"login 0x{e.Id:X8}");
                Core.CharacterFilter.LoginComplete += (_, _) => Heard.Add("login complete");
            }

            protected override void Shutdown()
            {
            }
        }

        private sealed class SendingTransport : IGameTransport
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
    }
}
