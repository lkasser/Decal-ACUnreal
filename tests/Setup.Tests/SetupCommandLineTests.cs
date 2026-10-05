using System;
using Setup.Common;
using Xunit;

namespace Setup.Tests
{
    /// <summary>/S, /D= and the rest, as installers have long taken them.</summary>
    public class SetupCommandLineTests
    {
        [Fact]
        public void NothingMeansTheWizard()
        {
            SetupCommandLine line = SetupCommandLine.Parse(Array.Empty<string>());

            Assert.False(line.Silent);
            Assert.Null(line.Folder);
            Assert.False(line.NoRegistry);
            Assert.False(line.NoShortcuts);
        }

        [Fact]
        public void TheTrialRunsSwitches()
        {
            SetupCommandLine line = SetupCommandLine.Parse(new[] { "/S", @"/D=C:\Temp\Trial", "/NoRegistry", "/NoShortcuts" });

            // /D= ends at the next switch when it is not last.
            Assert.True(line.Silent);
            Assert.Equal(@"C:\Temp\Trial", line.Folder);
            Assert.True(line.NoRegistry);
            Assert.True(line.NoShortcuts);
        }

        [Fact]
        public void AnUnquotedFolderWithSpacesIsTheRestOfTheLine()
        {
            // How Windows hands over /S /D=C:\Games\Decal Agent: in pieces.
            SetupCommandLine line = SetupCommandLine.Parse(new[] { "/S", @"/D=C:\Games\Decal", "Agent" });

            Assert.Equal(@"C:\Games\Decal Agent", line.Folder);
        }

        [Fact]
        public void AQuotedFolderLosesItsQuotesAndTrailingSlash()
        {
            SetupCommandLine line = SetupCommandLine.Parse(new[] { "/D=\"C:\\Games\\Decal Agent\\\"" });

            Assert.Equal(@"C:\Games\Decal Agent", line.Folder);
        }

        [Fact]
        public void SwitchesAreReadWhicheverCaseOrDash()
        {
            SetupCommandLine line = SetupCommandLine.Parse(new[] { "-s", "/noregistry", "/DESKTOP", "/start", "/Log=C:\\Temp\\setup.log" });

            Assert.True(line.Silent);
            Assert.True(line.NoRegistry);
            Assert.True(line.Desktop);
            Assert.True(line.Start);
            Assert.Equal(@"C:\Temp\setup.log", line.LogPath);
        }

        [Fact]
        public void TheUninstallersSwitches()
        {
            SetupCommandLine line = SetupCommandLine.Parse(new[] { "/Product=VirindiTank", "/DeleteSettings", @"/Data=C:\Temp\ACHost", "/S" });

            Assert.Equal("VirindiTank", line.Product);
            Assert.Same(SetupProduct.VirindiTank, SetupProduct.Find(line.Product));
            Assert.True(line.DeleteSettings);
            Assert.Equal(@"C:\Temp\ACHost", line.DataFolder);
        }

        [Theory]
        [InlineData("/Bogus")]
        [InlineData("install")]
        [InlineData("/D=")]
        [InlineData("/Log")]
        public void WhatIsNotUnderstoodIsRefusedInWords(string arg)
        {
            ArgumentException refused = Assert.Throws<ArgumentException>(() => SetupCommandLine.Parse(new[] { arg }));
            Assert.False(string.IsNullOrWhiteSpace(refused.Message));
        }
    }
}
