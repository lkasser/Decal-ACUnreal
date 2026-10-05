using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Setup.Common;
using Xunit;

namespace Setup.Tests
{
    /// <summary>The batch file that finishes an uninstall once the uninstaller has exited, run for real on files of the test's own.</summary>
    public class DeferredDeleteTests
    {
        [Fact]
        public void NamesEachFileAndFolderAndEscapesPercent()
        {
            string script = DeferredDelete.Script(new[] { @"C:\A\100% sure.dll" }, new[] { @"C:\A" });

            Assert.StartsWith("@echo off", script);
            Assert.Contains("chcp 65001", script);
            Assert.Contains("del /f /q \"C:\\A\\100%% sure.dll\"", script);
            Assert.Contains("if exist \"C:\\A\\100%% sure.dll\" goto wait", script);
            Assert.Contains("rd \"C:\\A\"", script);

            // No rd /s anywhere: a folder with anything left in it stays.
            Assert.DoesNotContain("rd /s", script);
        }

        [Fact]
        public void DeletesTheFilesAndEmptyFoldersAndItself()
        {
            using TempFolder temp = new TempFolder();
            string folder = temp["Décal 100% Agent"];
            string kept = temp["Kept"];
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            Directory.CreateDirectory(kept);
            string first = Path.Combine(folder, "Uninstall.exe");
            string second = Path.Combine(folder, "sub", "coreclr.dll");
            File.WriteAllText(first, "1");
            File.WriteAllText(second, "2");
            File.WriteAllText(Path.Combine(kept, "mine.txt"), "mine");

            string script = DeferredDelete.Write(new[] { first, second }, new[] { Path.Combine(folder, "sub"), folder, kept }, temp.Path);
            using (Process run = DeferredDelete.Start(script))
                Assert.True(run.WaitForExit(30000));

            Assert.False(Directory.Exists(folder));
            Assert.True(File.Exists(Path.Combine(kept, "mine.txt")));
            Assert.False(File.Exists(script));
        }

        [Fact]
        public async Task WaitsForAFileToComeFree()
        {
            using TempFolder temp = new TempFolder();
            string file = temp["held.dll"];
            File.WriteAllText(file, "held");

            Process run;
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                run = DeferredDelete.Start(DeferredDelete.Write(new[] { file }, new string[0], temp.Path));
                await Task.Delay(1500);
                Assert.True(File.Exists(file));
                Assert.False(run.HasExited);
            }

            using (run)
                Assert.True(run.WaitForExit(30000));
            Assert.False(File.Exists(file));
        }
    }
}
