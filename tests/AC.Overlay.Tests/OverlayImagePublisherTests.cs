using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AC.Dat;
using AC.Host.Overlay;
using Xunit;

namespace AC.Overlay.Tests
{
    public class OverlayImagePublisherTests
    {
        /// <summary>
        /// The DLL names the theme's images in decal_view.cpp and the host sends the ones on
        /// its own list. Nothing but this notices when the two disagree - the overlay just
        /// draws a stand-in, which looks like a bug in the drawing.
        /// </summary>
        [Fact]
        public void TheHostSendsExactlyTheThemeImagesTheOverlayDrawsWith()
        {
            // The theme's controls in decal_view.cpp, and the two bars in overlay_ui.cpp.
            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "native", "ACUnrealOverlay", "decal_view.cpp"))
                + File.ReadAllText(Path.Combine(RepositoryRoot(), "native", "ACUnrealOverlay", "overlay_ui.cpp"));

            HashSet<string> drawn = Regex.Matches(source, "\"((?:portal|vvs|bar|decal):[^\"]+)\"")
                .Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            Assert.NotEmpty(drawn);
            Assert.Equal(drawn.OrderBy(k => k, StringComparer.Ordinal), DecalThemeImages.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void AnImageAViewNamesIsSentOnceAndAMissingOneIsSaidOnce()
        {
            string decal = Path.Combine(Path.GetTempPath(), "achost-publisher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(decal);
            File.WriteAllBytes(Path.Combine(decal, "icon.bmp"), TinyBmp());

            try
            {
                OverlayServer server = new OverlayServer("achost-overlay-test-" + Guid.NewGuid().ToString("N"));
                List<string> said = new List<string>();
                OverlayImagePublisher publisher = new OverlayImagePublisher(server, new ImageCatalog(null, decal, null), said.Add);

                OverlayState state = new OverlayState();
                state.Windows.Add(new OverlayWindow
                {
                    Owner = "VirindiTank",
                    View = new OverlayView
                    {
                        Icon = "decal:icon.bmp",
                        Root = new OverlayViewControl
                        {
                            Type = ViewControlTypes.Fixed,
                            Children =
                            {
                                new OverlayViewControl { Type = ViewControlTypes.Button, Image = "decal:missing.bmp" },
                                new OverlayViewControl
                                {
                                    Type = ViewControlTypes.List,
                                    Rows = { new OverlayViewRow { Cells = { new OverlayViewCell { Image = "decal:icon.bmp" } } } },
                                },
                            },
                        },
                    },
                });

                publisher.PublishImagesNamedIn(state);
                publisher.PublishImagesNamedIn(state);

                Assert.Equal(1, publisher.Published);
                Assert.Equal(1, publisher.Missing);
                Assert.Equal(1, server.ImageCount);
                Assert.Contains(said, line => line.Contains("decal:missing.bmp"));
                Assert.Single(said);
            }
            finally
            {
                Directory.Delete(decal, recursive: true);
            }
        }

        /// <summary>
        /// The overlay asks for art it wants and does not have. Asked from the pipe thread,
        /// sent on the game thread, once however many times it is asked.
        /// </summary>
        [Fact]
        public void AnImageTheOverlayAsksForIsSentOnceAndAnUnknownOneSaidOnce()
        {
            string decal = Path.Combine(Path.GetTempPath(), "achost-publisher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(decal);
            File.WriteAllBytes(Path.Combine(decal, "arrow.bmp"), TinyBmp());

            try
            {
                OverlayServer server = new OverlayServer("achost-overlay-test-" + Guid.NewGuid().ToString("N"));
                List<string> said = new List<string>();
                OverlayImagePublisher publisher = new OverlayImagePublisher(server, new ImageCatalog(null, decal, null), said.Add);

                publisher.Want("decal:arrow.bmp");
                publisher.Want("decal:arrow.bmp");
                publisher.Want("nonsense:thing");
                publisher.Want("   ");
                Assert.Equal(0, server.ImageCount);  // nothing until the game thread sends

                publisher.PublishWanted();
                publisher.Want("decal:arrow.bmp");
                publisher.PublishWanted();

                Assert.Equal(1, server.ImageCount);
                Assert.Equal(1, publisher.Published);
                Assert.Single(said, line => line.Contains("nonsense:thing"));
            }
            finally
            {
                Directory.Delete(decal, recursive: true);
            }
        }

        /// <summary>A 1x1 24-bit bitmap, red.</summary>
        private static byte[] TinyBmp()
        {
            byte[] bmp = new byte[14 + 40 + 4];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(54).CopyTo(bmp, 10);
            BitConverter.GetBytes(40).CopyTo(bmp, 14);
            BitConverter.GetBytes(1).CopyTo(bmp, 18);
            BitConverter.GetBytes(1).CopyTo(bmp, 22);
            BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
            BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
            bmp[54] = 0;    // blue
            bmp[55] = 0;    // green
            bmp[56] = 255;  // red
            return bmp;
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Decal-ACUnreal.slnx")))
                directory = directory.Parent;

            Assert.NotNull(directory);
            return directory.FullName;
        }
    }
}
