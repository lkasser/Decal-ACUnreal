using AC.Host.Overlay;
using Xunit;

namespace AC.Overlay.Tests
{
    /// <summary>
    /// The JSON is the contract with native/ACUnrealOverlay/overlay_state.h, and nothing in
    /// this solution compiles the C++ side. These tests are the only thing standing between
    /// a renamed property and an overlay that draws nothing.
    /// </summary>
    public class OverlaySnapshotTests
    {
        private static OverlayState Full()
        {
            return new OverlayState
            {
                Status = new OverlayStatus
                {
                    ServerConnected = true,
                    Acting = true,
                    Looting = false,
                    Server = "127.0.0.1:9000",
                    Character = "Frostfell",
                    Position = "0xAB94001C 12.3 45.6 0.0",
                    MessagesIn = 12345,
                    MessagesOut = 678,
                    Malformed = 9,
                    Objects = 312,
                },
                Panels =
                {
                    new OverlayPanel
                    {
                        Title = "Loot",
                        Columns = { "Item", "Decision", "Rule" },
                        Rows =
                        {
                            new OverlayRow { Tone = RowTones.Good, Cells = { "Leather Cap", "Keep", "Keep weapons and caps" } },

                            // An empty cell is ordinary: a decision with no rule behind it.
                            new OverlayRow { Tone = RowTones.Muted, Cells = { "Rock", string.Empty, string.Empty } },
                        },
                    },
                    new OverlayPanel
                    {
                        Title = "Combat",
                        Columns = { "Attacker", "Damage" },
                    },
                },
                Revision = 7,
            };
        }

        [Fact]
        public void ASnapshotSurvivesTheRoundTripThroughJson()
        {
            OverlayState state = Full();

            OverlayState back = OverlayJson.ReadState(OverlayJson.ToJson(state));

            Assert.True(back.Status.ServerConnected);
            Assert.True(back.Status.Acting);
            Assert.False(back.Status.Looting);
            Assert.Equal("127.0.0.1:9000", back.Status.Server);
            Assert.Equal("Frostfell", back.Status.Character);
            Assert.Equal("0xAB94001C 12.3 45.6 0.0", back.Status.Position);
            Assert.Equal(12345L, back.Status.MessagesIn);
            Assert.Equal(678L, back.Status.MessagesOut);
            Assert.Equal(9L, back.Status.Malformed);
            Assert.Equal(312L, back.Status.Objects);
            Assert.Equal(7L, back.Revision);

            Assert.Equal(2, back.Panels.Count);
            Assert.Equal("Loot", back.Panels[0].Title);
            Assert.Equal(new[] { "Item", "Decision", "Rule" }, back.Panels[0].Columns);
            Assert.Equal(2, back.Panels[0].Rows.Count);
            Assert.Equal(RowTones.Good, back.Panels[0].Rows[0].Tone);
            Assert.Equal(new[] { "Leather Cap", "Keep", "Keep weapons and caps" }, back.Panels[0].Rows[0].Cells);
            Assert.Equal(RowTones.Muted, back.Panels[0].Rows[1].Tone);
            Assert.Equal(string.Empty, back.Panels[0].Rows[1].Cells[1]);
            Assert.Equal(string.Empty, back.Panels[0].Rows[1].Cells[2]);

            Assert.Equal("Combat", back.Panels[1].Title);
            Assert.Empty(back.Panels[1].Rows);
        }

        /// <summary>
        /// A host with nothing to show yet publishes this, and it must arrive as an empty
        /// array rather than as null: the overlay reads it into a std::vector.
        /// </summary>
        [Fact]
        public void ASnapshotWithNoPanelsSurvivesAsAnEmptyList()
        {
            OverlayState state = OverlayStateBuilder.Build(false, false, false, null, null, null, 0, 0, 0, 0);

            string json = OverlayJson.ToJson(state);
            OverlayState back = OverlayJson.ReadState(json);

            Assert.Contains("\"panels\":[]", json);
            Assert.NotNull(back.Panels);
            Assert.Empty(back.Panels);
            Assert.Equal(string.Empty, back.Status.Server);
        }

        [Fact]
        public void APanelsOwnerSurvivesTheRoundTrip()
        {
            OverlayState state = Full();
            state.Panels[0].Owner = "VirindiTank";

            OverlayState back = OverlayJson.ReadState(OverlayJson.ToJson(state));

            Assert.Equal("VirindiTank", back.Panels[0].Owner);
        }

        [Fact]
        public void TheJsonNamesAreTheOnesTheOverlayParses()
        {
            string json = OverlayJson.ToJson(Full());

            // Every name the C++ parser in native/ACUnrealOverlay/overlay_ipc.cpp looks
            // for. The two sides agree only by these strings, and nothing else in either
            // build would notice a rename until the overlay quietly drew defaults.
            foreach (string key in new[]
            {
                "\"status\"", "\"panels\"", "\"revision\"", "\"published_ms\"",
                "\"server_connected\"", "\"acting\"", "\"looting\"", "\"server\"", "\"character\"", "\"position\"",
                "\"notice\"", "\"messages_in\"", "\"messages_out\"", "\"malformed\"", "\"objects\"",
                "\"title\"", "\"owner\"", "\"key\"", "\"columns\"", "\"rows\"", "\"cells\"", "\"tone\"", "\"id\"",
            })
            {
                Assert.Contains(key, json);
            }

            Assert.DoesNotContain("MessagesIn", json);

            // The old name must not survive anywhere: the parser no longer reads it, so a
            // leftover would serialise a field nobody receives.
            Assert.DoesNotContain("\"connected\":", json);

            string command = OverlayJson.ToJson(new OverlayCommand { Name = "toggle-looting", Value = "on", RowId = "0x80001234", Owner = "VirindiTank" });
            Assert.Contains("\"name\"", command);
            Assert.Contains("\"value\"", command);
            Assert.Contains("\"row_id\"", command);
            Assert.Contains("\"owner\"", command);
        }

        /// <summary>
        /// The owner is what the host routes on. A command that arrives without one goes
        /// to the host's own window, so "absent" must read as empty rather than null -
        /// null would throw inside the dispatcher on the first click.
        /// </summary>
        [Fact]
        public void ACommandsOwnerSurvivesTheRoundTripAndAnAbsentOneIsEmpty()
        {
            OverlayCommand back = OverlayJson.ReadCommand(
                OverlayJson.ToJson(new OverlayCommand { Name = "toggle-looting", Owner = "VirindiTank" }));
            Assert.Equal("VirindiTank", back.Owner);

            OverlayCommand bare = OverlayJson.ReadCommand("{\"name\":\"reload-profile\"}");
            Assert.Equal(string.Empty, bare.Owner);
            Assert.Equal(string.Empty, bare.RowId);
        }

        [Fact]
        public void ACommandSurvivesTheRoundTripThroughJson()
        {
            OverlayCommand back = OverlayJson.ReadCommand(
                OverlayJson.ToJson(new OverlayCommand { Name = "reload-profile", Value = @"C:\profiles\take.utl" }));

            Assert.Equal("reload-profile", back.Name);
            Assert.Equal(@"C:\profiles\take.utl", back.Value);
        }

        private static OverlayState WithWindows()
        {
            OverlayState state = Full();
            state.Windows.Add(new OverlayWindow
            {
                Owner = "VirindiTank",
                Title = "uTank2",
                Controls =
                {
                    new OverlayControl { Id = "loot", Label = "Pick loot up", Kind = ControlKinds.Toggle, Value = "false", Tooltip = "Needs --enable-actions." },
                    new OverlayControl { Id = "range", Label = "Range", Kind = ControlKinds.Slider, Value = "12.5", Min = 1, Max = 40, Step = 0.5 },
                    new OverlayControl { Id = "mode", Label = "Mode", Kind = ControlKinds.Choice, Value = "Melee", Options = { "Melee", "Missile", "Magic" } },
                },
            });
            state.Windows.Add(new OverlayWindow { Owner = "Broken", Enabled = false });
            return state;
        }

        [Fact]
        public void WindowsAndTheirControlsSurviveTheRoundTrip()
        {
            OverlayState back = OverlayJson.ReadState(OverlayJson.ToJson(WithWindows()));

            Assert.Equal(2, back.Windows.Count);

            OverlayWindow tank = back.Windows[0];
            Assert.Equal("VirindiTank", tank.Owner);
            Assert.Equal("uTank2", tank.Title);
            Assert.True(tank.Enabled);
            Assert.Equal(3, tank.Controls.Count);

            Assert.Equal("loot", tank.Controls[0].Id);
            Assert.Equal("Needs --enable-actions.", tank.Controls[0].Tooltip);

            OverlayControl range = tank.Controls[1];
            Assert.Equal(ControlKinds.Slider, range.Kind);
            Assert.Equal("12.5", range.Value);
            Assert.Equal(1.0, range.Min);
            Assert.Equal(40.0, range.Max);
            Assert.Equal(0.5, range.Step);

            Assert.Equal(new[] { "Melee", "Missile", "Magic" }, tank.Controls[2].Options);

            // Disabled must survive as false. The native side defaults a missing "enabled"
            // to true, so a serialiser that dropped false values would heal a broken plugin.
            Assert.False(back.Windows[1].Enabled);
        }

        [Fact]
        public void TheWindowAndControlNamesAreTheOnesTheOverlayParses()
        {
            string json = OverlayJson.ToJson(WithWindows());

            // Read by ReadWindow and ReadControl in native/ACUnrealOverlay/overlay_ipc.cpp.
            foreach (string key in new[]
            {
                "\"windows\"", "\"owner\"", "\"title\"", "\"enabled\"", "\"controls\"",
                "\"id\"", "\"label\"", "\"kind\"", "\"value\"", "\"options\"", "\"min\"", "\"max\"", "\"step\"", "\"tooltip\"",
            })
            {
                Assert.Contains(key, json);
            }

            Assert.Contains("\"enabled\":false", json.Replace(" ", string.Empty));

            string command = OverlayJson.ToJson(new OverlayCommand { Name = "set", Value = "true", Owner = "VirindiTank", ControlId = "loot" });
            Assert.Contains("\"control_id\"", command);
            Assert.DoesNotContain("ControlId", command);
        }

        /// <summary>
        /// A command from a control carries its id; one from anything older carries none, and
        /// must read as empty rather than null for the same reason an absent owner does.
        /// </summary>
        [Fact]
        public void ACommandsControlIdSurvivesTheRoundTripAndAnAbsentOneIsEmpty()
        {
            OverlayCommand back = OverlayJson.ReadCommand(
                OverlayJson.ToJson(new OverlayCommand { Name = "set", Value = "false", Owner = "VirindiTank", ControlId = "echo" }));
            Assert.Equal("echo", back.ControlId);
            Assert.Equal("false", back.Value);

            OverlayCommand old = OverlayJson.ReadCommand("{\"name\":\"toggle-looting\",\"owner\":\"VirindiTank\"}");
            Assert.Equal(string.Empty, old.ControlId);
        }

        [Fact]
        public void TheBuilderPutsAnEmptyStringWhereACallerLeftANull()
        {
            OverlayState state = OverlayStateBuilder.Build(
                true, false, false, null, null, null, 0, 0, 0, 0,
                (null, new[] { "Item", null }, new[] { OverlayStateBuilder.Row(RowTones.Normal, null, "Keep") }));

            Assert.Equal(string.Empty, state.Panels[0].Title);
            Assert.Equal(string.Empty, state.Panels[0].Columns[1]);
            Assert.Equal(string.Empty, state.Panels[0].Rows[0].Cells[0]);
            Assert.DoesNotContain("null", OverlayJson.ToJson(state));
        }
    }
}
