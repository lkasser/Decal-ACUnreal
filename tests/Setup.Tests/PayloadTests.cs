using System.IO;
using Setup.Common;
using Xunit;

namespace Setup.Tests
{
    /// <summary>The zip a setup carries: its names made Windows paths, and refused when they would leave the folder.</summary>
    public class PayloadTests
    {
        [Fact]
        public void NamesAreRelativeWindowsPathsWhicheverSlashTheZipUsed()
        {
            using Payload payload = Payloads.Of(("a/b/c.dll", "one"), ("d\\e.dll", "two"), ("f.txt", "three"));

            Assert.Equal(new[] { @"a\b\c.dll", @"d\e.dll", "f.txt" }, payload.Files);
            Assert.Equal(11, payload.TotalLength);
            Assert.Equal(5, payload.LengthOf("f.txt"));
        }

        [Fact]
        public void FoldersInTheZipAreNotFiles()
        {
            using Payload payload = Payloads.Of(("plugins/", string.Empty), ("plugins/x.dll", "x"));

            Assert.Equal(new[] { @"plugins\x.dll" }, payload.Files);
        }

        [Theory]
        [InlineData("../outside.dll")]
        [InlineData("a/../../outside.dll")]
        [InlineData("C:/Windows/evil.dll")]
        [InlineData("/rooted.dll")]
        [InlineData("file.dll:stream")]
        [InlineData("a//b.dll")]
        public void ANameThatWouldLeaveTheFolderRefusesTheWholeZip(string name)
        {
            Assert.Throws<InvalidDataException>(() => Payloads.Of(("fine.dll", "fine"), (name, "bad")));
        }

        [Fact]
        public void TheSameFileTwiceIsRefused()
        {
            Assert.Throws<InvalidDataException>(() => Payloads.Of(("a/x.dll", "1"), ("A\\X.dll", "2")));
        }

        [Fact]
        public void ExtractsOverWhatIsThere()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Of(("sub/x.dll", "new"));
            string target = temp[@"out\sub\x.dll"];
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, "old and longer");

            payload.Extract(@"sub\x.dll", target);

            Assert.Equal("new", File.ReadAllText(target));
        }

        [Fact]
        public void AFileInUseThatIsAlreadyTheSameIsLeftAlone()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Of(("x.dll", "same"));
            string target = temp["x.dll"];
            File.WriteAllText(target, "same");

            // Held as the game holds the overlay it loaded: it may be read, never written.
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                Assert.Null(payload.Extract("x.dll", target));

            Assert.Equal("same", File.ReadAllText(target));
            Assert.False(File.Exists(Payload.SetAsideName(target, 0)));
        }

        [Fact]
        public void AFileInUseThatChangesIsMovedAsideAndTheNewOneWritten()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Of(("x.dll", "new"));
            string target = temp["x.dll"];
            File.WriteAllText(target, "old");

            string aside;
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                aside = payload.Extract("x.dll", target);

            Assert.Equal(Payload.SetAsideName(target, 0), aside);
            Assert.Equal("new", File.ReadAllText(target));
            Assert.Equal("old", File.ReadAllText(aside));
        }

        [Fact]
        public void AFileThatMayNotEvenBeMovedStillRefuses()
        {
            using TempFolder temp = new TempFolder();
            using Payload payload = Payloads.Of(("x.dll", "new"));
            string target = temp["x.dll"];
            File.WriteAllText(target, "old");

            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => payload.Extract("x.dll", target));

            Assert.Equal("old", File.ReadAllText(target));
            Assert.False(File.Exists(Payload.SetAsideName(target, 0)));
        }

        [Fact]
        public void ASetupBuiltWithoutOneHasNone()
        {
            Assert.Null(Payload.FromAssembly(typeof(PayloadTests).Assembly));
        }
    }
}
