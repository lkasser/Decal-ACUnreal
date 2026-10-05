using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using AC.Injector;
using Xunit;

namespace AC.Overlay.Tests
{
    /// <summary>
    /// The checks that happen before anything is written into another process.
    /// </summary>
    /// <remarks>
    /// Nothing here injects: a test that started a remote thread in a real process would be
    /// testing Windows, and would leave a DLL loaded in whatever it picked. What is worth
    /// testing is the part that goes wrong in practice - the arguments - and that each
    /// refusal says which one and why, because a Win32 number tells whoever is holding the
    /// keyboard nothing at all.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public class InjectorValidationTests
    {
        /// <summary>A file that certainly exists, so the DLL checks are out of the way.</summary>
        private static string AFileThatExists => typeof(InjectorValidationTests).Assembly.Location;

        [Fact]
        public void ADllPathThatDoesNotExistIsRefusedAndSaysWhereItLooked()
        {
            string missing = Path.Combine(Path.GetTempPath(), "no-such-overlay-" + Guid.NewGuid().ToString("N") + ".dll");

            InjectionResult result = DllInjector.Inject("ACUnreal", missing);

            Assert.False(result.Success);
            Assert.Contains("There is no file at", result.Message);
            Assert.Contains(missing, result.Message);
        }

        [Fact]
        public void ARelativeDllPathIsRefusedBecauseTheTargetWouldResolveItItself()
        {
            InjectionResult result = DllInjector.Inject("ACUnreal", Path.Combine("overlay", "ACUnrealOverlay.dll"));

            Assert.False(result.Success);
            Assert.Contains("must be absolute", result.Message);
            Assert.Contains("working directory", result.Message);
        }

        [Fact]
        public void AnUnknownProcessNameIsRefusedWithSomethingToTryInstead()
        {
            string nobody = "no-such-client-" + Guid.NewGuid().ToString("N");

            InjectionResult result = DllInjector.Inject(nobody, AFileThatExists);

            Assert.False(result.Success);
            Assert.Contains(nobody, result.Message);
            Assert.Contains("is running", result.Message);
            Assert.Contains("--list", result.Message);
        }

        [Fact]
        public void NoProcessNameAtAllIsRefusedBeforeAnythingIsLookedUp()
        {
            InjectionResult result = DllInjector.Inject("   ", AFileThatExists);

            Assert.False(result.Success);
            Assert.Contains("--process", result.Message);
        }

        [Fact]
        public void NoDllAtAllIsRefused()
        {
            InjectionResult result = DllInjector.Inject("ACUnreal", null);

            Assert.False(result.Success);
            Assert.Contains("--dll", result.Message);
        }

        [Fact]
        public void AProcessIdThatIsNotRunningIsRefusedByNumber()
        {
            // Negative ids do not exist, so this exercises the not-running path without the
            // risk of naming a number something has just started using.
            InjectionResult result = DllInjector.InjectInto(-1, AFileThatExists);

            Assert.False(result.Success);
            Assert.Contains("-1", result.Message);
        }

        [Fact]
        public void ListingCandidatesFindsTheProcessRunningTheTests()
        {
            using Process self = Process.GetCurrentProcess();

            IReadOnlyList<InjectionTarget> candidates = DllInjector.Candidates(self.ProcessName);

            Assert.Contains(candidates, c => c.ProcessId == Environment.ProcessId);
            Assert.All(candidates, c => Assert.Equal(self.ProcessName, c.Name, ignoreCase: true));
        }

        [Fact]
        public void ListingCandidatesForANameNothingIsCalledFindsNothing()
        {
            Assert.Empty(DllInjector.Candidates("no-such-client-" + Guid.NewGuid().ToString("N")));
        }

        /// <summary>
        /// The name people type is the one Task Manager shows them, which has the extension
        /// on it; Win32 process names do not.
        /// </summary>
        [Fact]
        public void TheProcessNameMayBeGivenWithOrWithoutTheExeOnIt()
        {
            using Process self = Process.GetCurrentProcess();

            Assert.Equal(
                DllInjector.Candidates(self.ProcessName).Count,
                DllInjector.Candidates(self.ProcessName + ".exe").Count);
        }
    }
}
