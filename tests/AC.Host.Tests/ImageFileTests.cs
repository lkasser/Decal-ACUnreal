using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AC.Dat;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// The theme artwork's formats, where it is installed, and the catalogue that ties the two
    /// together. Every image here is built by the test itself, byte by byte, so the suite
    /// needs nothing from a game install - and so each test says exactly which part of a
    /// format it is exercising, which a real file would not.
    /// </summary>
    public sealed class ImageFileTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "achost-images-" + Guid.NewGuid().ToString("N"));

        public ImageFileTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory left behind is not worth failing a test over.
            }
        }

        // ---------------------------------------------------------------- the host's own

        /// <summary>
        /// Decal's icon lives in its agent's resources, not in a file any install has, so the host
        /// carries it: the round C and gold cross, 16 pixels, for Decal's square on the bar.
        /// </summary>
        [Fact]
        public void DecalsIconIsOneTheHostCarries()
        {
            ImageCatalog catalog = new ImageCatalog(null, (string)null, null);

            Assert.True(catalog.TryGet("host:decal", out RgbaImage icon, out string error), error);
            Assert.Equal(16, icon.Width);
            Assert.Equal(16, icon.Height);

            Assert.False(catalog.TryGet("host:nothing", out _, out _));
            Assert.False(catalog.TryGet("host:../decal", out _, out _));
        }

        /// <summary>
        /// Virindi HUDs' own icons - the MiniRemote's lock and tank, the Comps HUD's plus and
        /// minus - are carried the same way, since they were only ever inside its DLL.
        /// </summary>
        [Theory]
        [InlineData("host:vhuds-lock_on")]
        [InlineData("host:vhuds-lock_off")]
        [InlineData("host:vhuds-tanklogo")]
        [InlineData("host:vhuds-plusicon")]
        [InlineData("host:vhuds-minusicon")]
        public void VirindiHudsIconsAreOnesTheHostCarries(string key)
        {
            ImageCatalog catalog = new ImageCatalog(null, (string)null, null);

            Assert.True(catalog.TryGet(key, out RgbaImage icon, out string error), error);
            Assert.Equal((16, 16), (icon.Width, icon.Height));
        }

        // ---------------------------------------------------------------- PNG

        [Fact]
        public void TheHostsOwnPngsDecodeToTheSamePixels()
        {
            byte[] pixels =
            {
                255, 0, 0, 255,     0, 255, 0, 128,     0, 0, 255, 0,
                10, 20, 30, 40,     0, 255, 255, 255,   250, 251, 252, 253,
            };
            RgbaImage original = new RgbaImage(3, 2, pixels);

            Assert.True(ImageFiles.TryDecodePng(original.ToPng(), out RgbaImage decoded, out string error), error);

            Assert.Equal(3, decoded.Width);
            Assert.Equal(2, decoded.Height);
            Assert.Equal(pixels, decoded.Rgba);
        }

        [Fact]
        public void TruecolourWithoutAlphaIsOpaque()
        {
            byte[] png = Png(2, 2, 8, 2, Unfiltered(
                new byte[] { 255, 0, 0, 0, 255, 0 },
                new byte[] { 0, 0, 255, 10, 20, 30 }));

            Assert.True(ImageFiles.TryDecodePng(png, out RgbaImage image, out string error), error);

            Assert.Equal(
                new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 10, 20, 30, 255 },
                image.Rgba);
        }

        [Fact]
        public void PaletteAlphaComesFromTrnsAndStopsWhereItDoes()
        {
            // Three colours but only two alphas: the third colour is past the end of tRNS
            // and so is opaque, as the format says.
            byte[] palette = { 0, 255, 255, 200, 100, 50, 1, 2, 3 };
            byte[] alpha = { 0, 128 };

            byte[] png = Png(3, 1, 8, 3, Unfiltered(new byte[] { 0, 1, 2 }), new[] { ("PLTE", palette), ("tRNS", alpha) });

            Assert.True(ImageFiles.TryDecodePng(png, out RgbaImage image, out string error), error);

            Assert.Equal(
                new byte[] { 0, 255, 255, 0, 200, 100, 50, 128, 1, 2, 3, 255 },
                image.Rgba);
        }

        [Fact]
        public void PackedPaletteSamplesAreReadLeftmostInTheHighBits()
        {
            // Two bits a pixel, five pixels a row - so each row ends part-way through a byte.
            byte[] palette = { 0, 0, 0, 85, 85, 85, 170, 170, 170, 255, 255, 255 };
            byte[] rows = Unfiltered(
                new byte[] { 0b00_01_10_11, 0b01_000000 },   // 0 1 2 3 1
                new byte[] { 0b11_11_00_00, 0b10_000000 });  // 3 3 0 0 2

            byte[] png = Png(5, 2, 2, 3, rows, new[] { ("PLTE", palette) });

            Assert.True(ImageFiles.TryDecodePng(png, out RgbaImage image, out string error), error);

            int[] expected = { 0, 1, 2, 3, 1, 3, 3, 0, 0, 2 };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i] * 85, image.Rgba[i * 4]);
                Assert.Equal(255, image.Rgba[i * 4 + 3]);
            }
        }

        [Fact]
        public void OneBitGreyScalesToFullRangeAndHonoursItsTransparentValue()
        {
            // Nine pixels: 1 0 1 1 0 0 1 0 | 1. tRNS makes the value 0 see-through.
            byte[] png = Png(9, 1, 1, 0, Unfiltered(new byte[] { 0b10110010, 0b1_0000000 }), new[] { ("tRNS", new byte[] { 0, 0 }) });

            Assert.True(ImageFiles.TryDecodePng(png, out RgbaImage image, out string error), error);

            int[] bits = { 1, 0, 1, 1, 0, 0, 1, 0, 1 };
            for (int i = 0; i < bits.Length; i++)
            {
                Assert.Equal(bits[i] * 255, image.Rgba[i * 4]);
                Assert.Equal(bits[i] * 255, image.Rgba[i * 4 + 2]);
                Assert.Equal(bits[i] == 1 ? 255 : 0, image.Rgba[i * 4 + 3]);
            }
        }

        [Fact]
        public void GreyWithAlphaSpreadsTheGreyAcrossAllThreeColours()
        {
            byte[] png = Png(2, 1, 8, 4, Unfiltered(new byte[] { 50, 255, 200, 0 }));

            Assert.True(ImageFiles.TryDecodePng(png, out RgbaImage image, out string error), error);

            Assert.Equal(new byte[] { 50, 50, 50, 255, 200, 200, 200, 0 }, image.Rgba);
        }

        [Theory]
        [InlineData(6, 4)]
        [InlineData(2, 3)]
        public void EveryFilterIsUndone(int colourType, int bytesPerPixel)
        {
            // Row 0 uses Paeth, whose "above" neighbours are then all zero; the rest use every
            // filter at least once, with values that wrap past 255 so modular arithmetic counts.
            int[] filters = { 4, 1, 2, 3, 4, 3, 0 };
            const int Width = 5;
            int height = filters.Length;

            byte[][] rows = new byte[height][];
            for (int y = 0; y < height; y++)
            {
                rows[y] = new byte[Width * bytesPerPixel];
                for (int i = 0; i < rows[y].Length; i++)
                    rows[y][i] = (byte)((i * 37) + (y * 91) + (i * y * 13) + 200);
            }

            byte[] png = Png(Width, height, 8, colourType, Filtered(rows, bytesPerPixel, filters));

            Assert.True(ImageFiles.TryDecodePng(png, out RgbaImage image, out string error), error);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    for (int c = 0; c < bytesPerPixel; c++)
                        Assert.Equal(rows[y][x * bytesPerPixel + c], image.Rgba[(y * Width + x) * 4 + c]);

                    if (bytesPerPixel == 3)
                        Assert.Equal(255, image.Rgba[(y * Width + x) * 4 + 3]);
                }
            }
        }

        [Fact]
        public void ATruncatedPngFailsWithAReasonAtEveryLength()
        {
            byte[] png = new RgbaImage(4, 3, Enumerable.Range(0, 48).Select(i => (byte)(i * 5)).ToArray()).ToPng();

            for (int length = 0; length < png.Length; length++)
            {
                Assert.False(ImageFiles.TryDecodePng(png.AsSpan(0, length), out RgbaImage image, out string error), $"{length} bytes decoded");
                Assert.Null(image);
                Assert.False(string.IsNullOrEmpty(error), $"no reason given at {length} bytes");
            }
        }

        [Fact]
        public void UnsupportedAndBrokenPngsSayWhy()
        {
            byte[] row = Unfiltered(new byte[] { 1, 2, 3, 4 });

            Assert.False(ImageFiles.TryDecodePng(Png(1, 1, 8, 6, row, interlace: 1), out _, out string error));
            Assert.Contains("interlaced", error);

            Assert.False(ImageFiles.TryDecodePng(Png(1, 1, 16, 6, row), out _, out error));
            Assert.Contains("16-bit", error);

            Assert.False(ImageFiles.TryDecodePng(Png(1, 1, 8, 3, Unfiltered(new byte[] { 0 })), out _, out error));
            Assert.Contains("PLTE", error);

            // Not zlib at all where the pixels should be.
            byte[] garbage = WithIdat(1, 1, 8, 6, new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A });
            Assert.False(ImageFiles.TryDecodePng(garbage, out _, out error));
            Assert.False(string.IsNullOrEmpty(error));

            byte[] notPng = Encoding.ASCII.GetBytes("GIF89a, as it happens");
            Assert.False(ImageFiles.TryDecodePng(notPng, out _, out error));
            Assert.Contains("signature", error);
        }

        // ---------------------------------------------------------------- BMP

        [Theory]
        [InlineData(40)]    // BITMAPINFOHEADER
        [InlineData(108)]   // BITMAPV4HEADER
        [InlineData(124)]   // BITMAPV5HEADER
        public void TwentyFourBitRowsAreBottomUpBgrAndPadded(int infoSize)
        {
            // Width 3 at three bytes a pixel is nine bytes a row, padded to twelve. The padding
            // is filled with junk to show it is skipped, not read as the next row's start.
            byte[] stored =
            {
                0xFF, 0xFF, 0xFF,   0x00, 0x00, 0x00,   0x1E, 0x14, 0x0A,   0xEE, 0xEE, 0xEE,   // bottom: white, black, (10,20,30)
                0x00, 0x00, 0xFF,   0x00, 0xFF, 0x00,   0xFF, 0x00, 0x00,   0xEE, 0xEE, 0xEE,   // top: red, green, blue
            };

            byte[] bmp = Bmp(3, 2, 24, null, stored, infoSize);

            Assert.True(ImageFiles.TryDecodeBmp(bmp, out RgbaImage image, out string error), error);

            Assert.Equal(3, image.Width);
            Assert.Equal(2, image.Height);
            Assert.Equal(
                new byte[]
                {
                    255, 0, 0, 255,     0, 255, 0, 255,     0, 0, 255, 255,
                    255, 255, 255, 255, 0, 0, 0, 255,       10, 20, 30, 255,
                },
                image.Rgba);
        }

        [Fact]
        public void ThirtyTwoBitTopDownIgnoresTheFourthByte()
        {
            // A negative height: the first stored row is the top one.
            byte[] stored =
            {
                0x00, 0x00, 0xFF, 0x00,   0x00, 0xFF, 0x00, 0x7F,   // top: red, green
                0xFF, 0x00, 0x00, 0x13,   0xFF, 0xFF, 0x00, 0xFF,   // bottom: blue, cyan
            };

            byte[] bmp = Bmp(2, -2, 32, null, stored);

            Assert.True(ImageFiles.TryDecodeBmp(bmp, out RgbaImage image, out string error), error);

            Assert.Equal(
                new byte[]
                {
                    255, 0, 0, 255,   0, 255, 0, 255,
                    0, 0, 255, 255,   0, 255, 255, 255,
                },
                image.Rgba);
        }

        [Fact]
        public void EightBitIndicesGoThroughThePalette()
        {
            uint[] palette = { 0x00FFFF, 0xC86432, 0x010203 };
            byte[] stored =
            {
                2, 1, 0, 0xEE,   // bottom row, padded from three bytes to four
                0, 1, 2, 0xEE,   // top row
            };

            byte[] bmp = Bmp(3, 2, 8, palette, stored);

            Assert.True(ImageFiles.TryDecodeBmp(bmp, out RgbaImage image, out string error), error);

            Assert.Equal(
                new byte[]
                {
                    0, 255, 255, 255,   200, 100, 50, 255,   1, 2, 3, 255,
                    1, 2, 3, 255,       200, 100, 50, 255,   0, 255, 255, 255,
                },
                image.Rgba);
        }

        [Fact]
        public void FourBitIndicesAreReadHighNibbleFirst()
        {
            uint[] palette = { 0x000000, 0xFF0000, 0x0000FF };
            byte[] stored = { 0x10, 0x20, 0x00, 0x00 };   // 1 0 2, padded to four bytes

            byte[] bmp = Bmp(3, 1, 4, palette, stored);

            Assert.True(ImageFiles.TryDecodeBmp(bmp, out RgbaImage image, out string error), error);

            Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 0, 255, 0, 0, 255, 255 }, image.Rgba);
        }

        [Fact]
        public void ATruncatedBmpFailsWithAReason()
        {
            byte[] stored = new byte[12 * 2];
            byte[] bmp = Bmp(3, 2, 24, null, stored);

            // The last row's three bytes of padding are not needed to draw it, so the file is
            // only truly short once it loses more than those.
            for (int length = 0; length < bmp.Length - 3; length++)
            {
                Assert.False(ImageFiles.TryDecodeBmp(bmp.AsSpan(0, length), out RgbaImage image, out string error), $"{length} bytes decoded");
                Assert.Null(image);
                Assert.False(string.IsNullOrEmpty(error), $"no reason given at {length} bytes");
            }

            Assert.True(ImageFiles.TryDecodeBmp(bmp.AsSpan(0, bmp.Length - 3), out _, out string last), last);
        }

        [Fact]
        public void TryDecodeGoesByTheBytesNotTheName()
        {
            byte[] bmp = Bmp(1, 1, 24, null, new byte[] { 1, 2, 3, 0 });
            byte[] png = new RgbaImage(1, 1, new byte[] { 3, 2, 1, 255 }).ToPng();

            Assert.True(ImageFiles.TryDecode(bmp, out RgbaImage fromBmp, out _));
            Assert.True(ImageFiles.TryDecode(png, out RgbaImage fromPng, out _));
            Assert.Equal(fromPng.Rgba, fromBmp.Rgba);

            Assert.False(ImageFiles.TryDecode(new byte[] { 1, 2, 3 }, out _, out string error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        // ---------------------------------------------------------------- colour key

        [Fact]
        public void OnlyExactCyanBecomesTransparent()
        {
            byte[] pixels =
            {
                0, 255, 255, 255,     // cyan
                0, 255, 254, 255,     // nearly
                1, 255, 255, 255,     // nearly
                0, 254, 255, 255,     // nearly
                255, 255, 255, 255,   // white
            };
            byte[] expected = (byte[])pixels.Clone();

            // The cyan pixel goes transparent and takes its one opaque neighbour's colour;
            // every other pixel is untouched.
            expected[0] = 0;
            expected[1] = 255;
            expected[2] = 254;
            expected[3] = 0;
            RgbaImage original = new RgbaImage(5, 1, pixels);

            RgbaImage keyed = ImageFiles.WithColourKey(original, 0, 255, 255);

            Assert.Equal(expected, keyed.Rgba);

            // A copy: the image it was made from still has its cyan pixel opaque.
            Assert.Equal(255, original.Rgba[3]);
        }

        /// <summary>
        /// The overlay samples with linear filtering, so a transparent pixel's colour bleeds
        /// into the opaque edge beside it. Left cyan, every tab and switch had a cyan fringe;
        /// the keyed pixel instead takes the average of its opaque neighbours.
        /// </summary>
        [Fact]
        public void AKeyedPixelTakesItsOpaqueNeighboursColourSoEdgesDoNotFringe()
        {
            byte[] pixels =
            {
                100, 0, 0, 255,   0, 255, 255, 255,   200, 0, 0, 255,
                0, 255, 255, 255, 0, 255, 255, 255,   0, 255, 255, 255,
                0, 255, 255, 255, 0, 255, 255, 255,   0, 255, 255, 255,
            };

            RgbaImage keyed = ImageFiles.WithColourKey(new RgbaImage(3, 3, pixels), 0, 255, 255);

            // Top middle: neighbours are the two reds, so it averages them.
            Assert.Equal(new byte[] { 150, 0, 0, 0 }, keyed.Rgba.AsSpan(4, 4).ToArray());

            // Bottom middle: every neighbour is keyed too, so it has nothing to take and is black.
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, keyed.Rgba.AsSpan(7 * 4, 4).ToArray());
        }

        // ---------------------------------------------------------------- manifest resources

        private const string ProbeResource = "AC.Host.Tests.Resources.resource-probe.txt";

        private static string TestAssemblyPath => typeof(ImageFileTests).Assembly.Location;

        [Fact]
        public void EmbeddedResourcesAreReadFromTheFileAsTheRuntimeReadsThem()
        {
            Assert.Contains(ProbeResource, ManifestResources.List(TestAssemblyPath));

            Assert.True(ManifestResources.TryRead(TestAssemblyPath, ProbeResource, out byte[] data, out string error), error);

            // The runtime's own reader is the reference: same bytes, not merely similar text.
            using Stream stream = typeof(ImageFileTests).Assembly.GetManifestResourceStream(ProbeResource);
            using MemoryStream expected = new MemoryStream();
            stream.CopyTo(expected);

            Assert.Equal(expected.ToArray(), data);
            Assert.Contains("ManifestResources is tested", Encoding.UTF8.GetString(data));
        }

        [Fact]
        public void ResourcesThatAreNotThereAreReportedNotThrown()
        {
            Assert.False(ManifestResources.TryRead(TestAssemblyPath, "AC.Host.Tests.Resources.nothing.txt", out byte[] data, out string error));
            Assert.Null(data);
            Assert.Contains("no resource", error);

            // The exact name is required; the catalogue does its own suffix matching.
            Assert.False(ManifestResources.TryRead(TestAssemblyPath, "resource-probe.txt", out _, out _));

            string text = Path.Combine(_root, "not-an-assembly.dll");
            File.WriteAllText(text, "MZ, but not really");
            Assert.False(ManifestResources.TryRead(text, ProbeResource, out _, out error));
            Assert.False(string.IsNullOrEmpty(error));
            Assert.Empty(ManifestResources.List(text));

            Assert.False(ManifestResources.TryList(Path.Combine(_root, "missing.dll"), out IReadOnlyList<string> names, out error));
            Assert.Empty(names);
            Assert.Contains("does not exist", error);
        }

        // ---------------------------------------------------------------- Decal install

        private sealed class FakeRegistry : IDecalRegistry
        {
            public bool HasAgent { get; set; } = true;

            public string AgentPath { get; set; }

            public string PortalPath { get; set; }

            public List<DecalRegistryEntry> Plugins { get; } = new List<DecalRegistryEntry>();

            public List<DecalRegistryEntry> Services { get; } = new List<DecalRegistryEntry>();

            public bool TryReadAgent(out string agentPath, out string portalPath)
            {
                agentPath = HasAgent ? AgentPath : null;
                portalPath = HasAgent ? PortalPath : null;
                return HasAgent;
            }

            public IReadOnlyList<DecalRegistryEntry> ReadPlugins() => Plugins;

            public IReadOnlyList<DecalRegistryEntry> ReadServices() => Services;
        }

        private string MakeDirectory(params string[] parts)
        {
            string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        [Fact]
        public void VirindiViewServiceIsFoundBesideAPlugin()
        {
            string decal = MakeDirectory("Decal 3.0");
            string client = MakeDirectory("Turbine", "Asheron's Call");
            MakeDirectory("Elsewhere", "ChaosHelper");
            string tank = MakeDirectory("Virindi Plugins", "VirindiTank");
            string vvs = Path.Combine(MakeDirectory("Virindi Plugins", "VirindiViewService"), "VirindiViewService.dll");
            File.WriteAllBytes(vvs, new byte[] { 0x4D, 0x5A });

            FakeRegistry registry = new FakeRegistry { AgentPath = decal + Path.DirectorySeparatorChar, PortalPath = client };
            registry.Plugins.Add(new DecalRegistryEntry("{A}", "Decal Hotkey System"));
            registry.Plugins.Add(new DecalRegistryEntry("{B}", "ChaosHelper", Path.Combine(_root, "Elsewhere", "ChaosHelper"), "ChaosHelper.dll"));
            registry.Plugins.Add(new DecalRegistryEntry("{C}", "Virindi Tank", tank + Path.DirectorySeparatorChar, "utank2-i.dll"));
            registry.Services.Add(new DecalRegistryEntry("{D}", "Virindi View Service Bootstrapper"));

            DecalInstall install = DecalInstall.Detect(registry);

            Assert.Equal(vvs, install.VirindiViewServicePath);
            Assert.Equal(decal + Path.DirectorySeparatorChar, install.DecalDirectory);
            Assert.Equal(client, install.PortalDirectory);

            // The native plugin has no directory and is not listed.
            Assert.Equal(new[] { "ChaosHelper", "Virindi Tank" }, install.Plugins.Select(p => p.Name));
            Assert.Equal(Path.Combine(tank + Path.DirectorySeparatorChar, "utank2-i.dll"), install.FindPlugin("Virindi Tank").FullPath);

            Assert.Contains(install.Describe(), line => line.Contains(vvs) && line.Contains("Virindi Tank"));
        }

        [Fact]
        public void TheBootstrappersThemeDirectoryIsItsOwnFolder()
        {
            string vvsDirectory = MakeDirectory("VVS");
            string vvs = Path.Combine(vvsDirectory, "VirindiViewService.dll");
            File.WriteAllBytes(vvs, new byte[] { 0x4D, 0x5A });

            FakeRegistry registry = new FakeRegistry { HasAgent = false };
            registry.Services.Add(new DecalRegistryEntry("{D}", "Virindi View Service Bootstrapper", themeDirectory: vvsDirectory + Path.DirectorySeparatorChar));

            Assert.Equal(vvs, DecalInstall.Detect(registry).VirindiViewServicePath);
        }

        [Fact]
        public void WithoutAnAgentKeyNothingIsFoundAndTheDescriptionSaysSo()
        {
            FakeRegistry registry = new FakeRegistry { HasAgent = false };

            DecalInstall install = DecalInstall.Detect(registry);

            Assert.Null(install.DecalDirectory);
            Assert.Null(install.PortalDirectory);
            Assert.Null(install.VirindiViewServicePath);
            Assert.Empty(install.Plugins);
            Assert.Contains(install.Describe(), line => line.StartsWith("Decal:") && line.Contains("no Decal Agent key"));
            Assert.Contains(install.Describe(), line => line.StartsWith("Virindi View Service:") && line.Contains("not found"));
        }

        [Fact]
        public void DirectoriesThatAreGoneAreNotReported()
        {
            FakeRegistry registry = new FakeRegistry
            {
                AgentPath = Path.Combine(_root, "uninstalled"),
                PortalPath = null,
            };

            DecalInstall install = DecalInstall.Detect(registry);

            Assert.Null(install.DecalDirectory);
            Assert.Null(install.PortalDirectory);
            Assert.Contains(install.Describe(), line => line.Contains("does not exist"));
            Assert.Contains(install.Describe(), line => line.Contains("no PortalPath"));
        }

        [Fact]
        public void GivenLocationsWinOverTheRegistry()
        {
            string registered = MakeDirectory("Registered");
            string given = MakeDirectory("Given");
            string vvs = Path.Combine(given, "MyVirindiViewService.dll");
            File.WriteAllBytes(vvs, new byte[] { 0x4D, 0x5A });

            FakeRegistry registry = new FakeRegistry { AgentPath = registered };

            DecalInstall install = DecalInstall.Detect(registry, decalDirectory: given, virindiViewServicePath: vvs);

            Assert.Equal(given, install.DecalDirectory);
            Assert.Equal(vvs, install.VirindiViewServicePath);

            // One that does not exist is reported, and the registry used after all.
            install = DecalInstall.Detect(registry, decalDirectory: Path.Combine(_root, "Nope"));
            Assert.Equal(registered, install.DecalDirectory);
            Assert.Contains(install.Describe(), line => line.Contains("Nope") && line.Contains("does not exist"));
        }

        [Fact]
        public void PluginsAreFoundByNameWhateverTheCase()
        {
            FakeRegistry registry = new FakeRegistry { HasAgent = false };
            registry.Plugins.Add(new DecalRegistryEntry("{C}", "Virindi Tank", @"C:\Plugins\Virindi Plugins\VirindiTank\", "utank2-i.dll"));
            registry.Plugins.Add(new DecalRegistryEntry("{A}", "Decal Hotkey System"));

            DecalInstall install = DecalInstall.Detect(registry);

            Assert.Equal("{C}", install.FindPlugin("virindi tank")?.Clsid);
            Assert.Equal("{C}", install.FindPlugin("VIRINDI TANK")?.Clsid);
            Assert.Null(install.FindPlugin("Virindi"));
            Assert.Null(install.FindPlugin("Decal Hotkey System"));
            Assert.Null(install.FindPlugin(null));
        }

        // ---------------------------------------------------------------- catalogue

        [Fact]
        public void DecalKeysReadTheBitmapAndKeyOutCyan()
        {
            string decal = MakeDirectory("Decal 3.0");
            byte[] stored = { 0xFF, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00 };   // cyan, red
            File.WriteAllBytes(Path.Combine(decal, "Switch-Active.bmp"), Bmp(2, 1, 24, null, stored));

            ImageCatalog catalog = new ImageCatalog(null, decal, null);

            Assert.True(catalog.TryGet("decal:Switch-Active.bmp", out RgbaImage image, out string error), error);
            // Keyed out, and coloured like its opaque neighbour so it cannot fringe.
            Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 255 }, image.Rgba);

            // Case does not matter to the key, as it does not to the file system underneath.
            Assert.True(catalog.TryGet("DECAL:switch-active.BMP", out _, out error), error);
        }

        /// <summary>
        /// Decal's switch is its texture in the template's shape, shaded by the template: the
        /// template's middle brightness takes the texture as it is, its darker rim darker, and
        /// its cyan stays outside.
        /// </summary>
        [Fact]
        public void ABarSwitchIsTheTextureInTheTemplatesShapeAndShading()
        {
            string decal = MakeDirectory("Decal 3.0");

            // Template, stored blue-green-red: cyan outside, a middle at brightness 190, a rim
            // at 95 (half). Grey so brightness is exact.
            byte[] template = { 0xFF, 0xFF, 0x00, 190, 190, 190, 95, 95, 95, 0, 0, 0 };
            File.WriteAllBytes(Path.Combine(decal, "Switchbar Template.bmp"), Bmp(3, 1, 24, null, template));

            // The gold texture, one colour: red 200, green 160, blue 40.
            byte[] gold = { 40, 160, 200, 40, 160, 200, 40, 160, 200, 0, 0, 0 };
            File.WriteAllBytes(Path.Combine(decal, "Switch-Active.bmp"), Bmp(3, 1, 24, null, gold));

            ImageCatalog catalog = new ImageCatalog(null, decal, null);

            Assert.True(catalog.TryGet("bar:open", out RgbaImage open, out string error), error);
            Assert.Equal(3, open.Width);

            Assert.Equal(0, open.Rgba[3]);                                                  // outside: transparent
            Assert.Equal(new byte[] { 200, 160, 40, 255 }, open.Rgba.AsSpan(4, 4).ToArray());   // middle: the texture
            Assert.Equal(new byte[] { 100, 80, 20, 255 }, open.Rgba.AsSpan(8, 4).ToArray());    // rim: half as bright

            // A switch nobody drew, and one whose bitmap is missing, both say why.
            Assert.False(catalog.TryGet("bar:sideways", out _, out error));
            Assert.Contains("open, closed and faulted", error);
            Assert.False(catalog.TryGet("bar:closed", out _, out error));
            Assert.Contains("Switch-Inactive.bmp", error);
        }

        [Theory]
        [InlineData("decal:../secret.bmp")]
        [InlineData(@"decal:..\secret.bmp")]
        [InlineData("decal:..")]
        [InlineData("decal:sub/secret.bmp")]
        [InlineData(@"decal:sub\secret.bmp")]
        [InlineData("decal:C:secret.bmp")]
        [InlineData("decal:secret.bmp:stream")]
        [InlineData("decal:")]
        public void DecalKeysCannotLeaveDecalsDirectory(string key)
        {
            string decal = MakeDirectory("Decal 3.0");
            MakeDirectory("Decal 3.0", "sub");
            byte[] secret = Bmp(1, 1, 24, null, new byte[] { 1, 2, 3, 0 });
            File.WriteAllBytes(Path.Combine(_root, "secret.bmp"), secret);
            File.WriteAllBytes(Path.Combine(decal, "sub", "secret.bmp"), secret);

            ImageCatalog catalog = new ImageCatalog(null, decal, null);

            Assert.False(catalog.TryGet(key, out RgbaImage image, out string error));
            Assert.Null(image);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Theory]
        [InlineData("switch-active.bmp")]
        [InlineData("file:Switch-Active.bmp")]
        [InlineData("portal")]
        [InlineData("")]
        [InlineData(null)]
        public void KeysWithoutAKnownPrefixAreRefused(string key)
        {
            ImageCatalog catalog = new ImageCatalog(null, MakeDirectory("Decal 3.0"), null);

            Assert.False(catalog.TryGet(key, out RgbaImage image, out string error));
            Assert.Null(image);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void SourcesThatWereNotFoundFailEveryKeyWithAReason()
        {
            ImageCatalog catalog = new ImageCatalog(null, (string)null, null);

            Assert.False(catalog.TryGet("portal:0600126F", out _, out string error));
            Assert.Contains("portal", error);

            Assert.False(catalog.TryGet("vvs:Decal_Theme_Images.TabActiveLeft.png", out _, out error));
            Assert.Contains("Virindi View Service", error);

            Assert.False(catalog.TryGet("decal:Switch-Active.bmp", out _, out error));
            Assert.Contains("Decal", error);

            Assert.Equal(3, catalog.Failures.Count);
        }

        [Fact]
        public void AnswersAreRememberedWhateverHappensOnDiskAfterwards()
        {
            string decal = MakeDirectory("Decal 3.0");
            string present = Path.Combine(decal, "Tab-Active.bmp");
            string absent = Path.Combine(decal, "Tab-Inactive.bmp");
            File.WriteAllBytes(present, Bmp(1, 1, 24, null, new byte[] { 1, 2, 3, 0 }));

            ImageCatalog catalog = new ImageCatalog(null, decal, null);

            Assert.True(catalog.TryGet("decal:Tab-Active.bmp", out RgbaImage first, out _));
            Assert.False(catalog.TryGet("decal:Tab-Inactive.bmp", out _, out string firstError));

            // Swap the two over. If either key went back to the disk, its answer would change.
            File.Delete(present);
            File.WriteAllBytes(absent, Bmp(1, 1, 24, null, new byte[] { 1, 2, 3, 0 }));

            Assert.True(catalog.TryGet("decal:Tab-Active.bmp", out RgbaImage second, out _));
            Assert.Same(first, second);

            Assert.False(catalog.TryGet("decal:Tab-Inactive.bmp", out _, out string secondError));
            Assert.Equal(firstError, secondError);

            Assert.Equal(new[] { "decal:Tab-Inactive.bmp" }, catalog.Failures.Keys);
            Assert.Equal(2, catalog.Count);
        }

        [Fact]
        public void ThreadsAskingAtOnceAllGetTheOneAnswer()
        {
            string decal = MakeDirectory("Decal 3.0");
            File.WriteAllBytes(Path.Combine(decal, "MapObject.bmp"), Bmp(1, 1, 24, null, new byte[] { 1, 2, 3, 0 }));

            ImageCatalog catalog = new ImageCatalog(null, decal, null);
            RgbaImage[] results = new RgbaImage[64];

            Parallel.For(0, results.Length, i => catalog.TryGet("decal:MapObject.bmp", out results[i], out _));

            Assert.All(results, image => Assert.Same(results[0], image));
            Assert.NotNull(results[0]);
        }

        [Fact]
        public void VvsKeysMatchTheEndOfAResourceNameAtADot()
        {
            // The test assembly stands in for VirindiViewService.dll: its one resource is not a
            // PNG, so a match shows up as a decode failure naming it, and no match as not found.
            ImageCatalog catalog = new ImageCatalog(null, null, TestAssemblyPath);

            Assert.False(catalog.TryGet("vvs:Resources.resource-probe.txt", out _, out string error));
            Assert.Contains(ProbeResource, error);
            Assert.Contains("PNG", error);

            Assert.False(catalog.TryGet("vvs:probe.txt", out _, out error));
            Assert.Contains("no resource ending in probe.txt", error);
        }

        [Fact]
        public void TheRealVirindiThemeDecodesWhereItIsInstalled()
        {
            DecalInstall install = DecalInstall.Detect();

            // Most machines running this suite have no game install; there is nothing to check
            // against, and that is not a failure.
            if (install.VirindiViewServicePath == null)
                return;

            ImageCatalog catalog = new ImageCatalog(null, install);

            Assert.True(catalog.TryGet("vvs:Decal_Theme_Images.TabActiveLeft.png", out RgbaImage image, out string error), error);
            Assert.Equal(6, image.Width);
            Assert.Equal(16, image.Height);
        }

        // ---------------------------------------------------------------- builders

        /// <summary>Scanlines with the None filter: each row prefixed with a zero.</summary>
        private static byte[] Unfiltered(params byte[][] rows)
            => rows.SelectMany(row => new byte[] { 0 }.Concat(row)).ToArray();

        /// <summary>
        /// Scanlines encoded with the given filter per row - the encoder's side of what the
        /// decoder undoes, written out longhand from the specification.
        /// </summary>
        private static byte[] Filtered(byte[][] rows, int bpp, int[] filters)
        {
            List<byte> output = new List<byte>();

            for (int y = 0; y < rows.Length; y++)
            {
                byte[] row = rows[y];
                byte[] prior = y > 0 ? rows[y - 1] : new byte[row.Length];
                output.Add((byte)filters[y]);

                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0;
                    int b = prior[i];
                    int c = i >= bpp ? prior[i - bpp] : 0;

                    int predicted = filters[y] switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        _ => PaethPredictor(a, b, c),
                    };

                    output.Add((byte)(row[i] - predicted));
                }
            }

            return output.ToArray();
        }

        private static int PaethPredictor(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static byte[] Compress(byte[] data)
        {
            using MemoryStream output = new MemoryStream();
            using (ZLibStream z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(data);
            return output.ToArray();
        }

        /// <summary>
        /// A PNG from scanlines already carrying their filter bytes. The compressed data is
        /// split across two IDAT chunks, because real encoders split it and a decoder that
        /// only read the first would otherwise pass.
        /// </summary>
        private static byte[] Png(int width, int height, int bitDepth, int colourType, byte[] scanlines, (string Type, byte[] Data)[] before = null, int interlace = 0)
            => WithIdat(width, height, bitDepth, colourType, Compress(scanlines), before, interlace);

        private static byte[] WithIdat(int width, int height, int bitDepth, int colourType, byte[] idat, (string Type, byte[] Data)[] before = null, int interlace = 0)
        {
            using MemoryStream png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            byte[] header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = (byte)bitDepth;
            header[9] = (byte)colourType;
            header[12] = (byte)interlace;
            Chunk(png, "IHDR", header);

            foreach ((string type, byte[] data) in before ?? Array.Empty<(string, byte[])>())
                Chunk(png, type, data);

            int half = idat.Length / 2;
            Chunk(png, "IDAT", idat.AsSpan(0, half).ToArray());
            Chunk(png, "IDAT", idat.AsSpan(half).ToArray());
            Chunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void Chunk(Stream stream, string type, byte[] data)
        {
            byte[] word = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
            stream.Write(word);

            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);

            // Correct CRCs, though the decoder does not check them, so a file dumped from a
            // failing test opens in an image viewer.
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in typeBytes.Concat(data))
            {
                crc ^= value;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFFu);
            stream.Write(word);
        }

        /// <summary>
        /// A bitmap from rows exactly as stored - padding included, in whichever order the sign
        /// of <paramref name="height"/> says. Palette entries are 0xRRGGBB.
        /// </summary>
        private static byte[] Bmp(int width, int height, int bitCount, uint[] palette, byte[] storedRows, int infoSize = 40)
        {
            int paletteBytes = (palette?.Length ?? 0) * 4;
            int pixelOffset = 14 + infoSize + paletteBytes;
            byte[] file = new byte[pixelOffset + storedRows.Length];

            file[0] = (byte)'B';
            file[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), pixelOffset);

            Span<byte> info = file.AsSpan(14, infoSize);
            BinaryPrimitives.WriteInt32LittleEndian(info, infoSize);
            BinaryPrimitives.WriteInt32LittleEndian(info.Slice(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(info.Slice(8), height);
            BinaryPrimitives.WriteUInt16LittleEndian(info.Slice(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(info.Slice(14), (ushort)bitCount);
            BinaryPrimitives.WriteInt32LittleEndian(info.Slice(20), storedRows.Length);
            BinaryPrimitives.WriteInt32LittleEndian(info.Slice(32), palette?.Length ?? 0);

            // The larger headers' extra fields (masks, colour space, gamma) are left zero; a
            // decoder that tried to use them rather than skip them would get nonsense.
            for (int i = 0; i < (palette?.Length ?? 0); i++)
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14 + infoSize + i * 4), palette[i]);

            storedRows.CopyTo(file, pixelOffset);
            return file;
        }
    }
}
