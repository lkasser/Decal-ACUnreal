using System;

namespace Setup.Common
{
    /// <summary>Why an install or uninstall could not go on, in words fit to show the player.</summary>
    public sealed class SetupException : Exception
    {
        public SetupException(string message, int exitCode = SetupExitCode.Failed, Exception inner = null)
            : base(message, inner)
        {
            ExitCode = exitCode;
        }

        /// <summary>What a silent run exits with for it.</summary>
        public int ExitCode { get; }
    }

    /// <summary>How far an install has got: files written of all of them, and the one being written.</summary>
    public readonly record struct SetupProgress(int Done, int Total, string Current);

    /// <summary>What the setups and the uninstaller exit with, for a silent run's caller to tell what happened.</summary>
    public static class SetupExitCode
    {
        public const int Success = 0;

        /// <summary>It failed, or the player cancelled.</summary>
        public const int Failed = 1;

        /// <summary>Something on the command line was not understood.</summary>
        public const int BadCommandLine = 2;

        /// <summary>Virindi Tank's setup found no Decal Agent to install into.</summary>
        public const int NoAgent = 3;

        /// <summary>Decal Agent is running from the folder, and must be exited first.</summary>
        public const int AgentRunning = 4;

        /// <summary>The setup was built without its files: build it with tools\build-installers.ps1.</summary>
        public const int NoPayload = 5;
    }
}
