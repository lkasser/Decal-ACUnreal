namespace AC.Injector
{
    /// <summary>
    /// What happened, in a sentence fit to print.
    /// </summary>
    /// <remarks>
    /// Injection fails for a handful of ordinary reasons - the wrong elevation, the wrong
    /// architecture, the game not running, a path typed wrongly - and each of them comes
    /// back from Win32 as a number. A number is no use to whoever is holding the keyboard,
    /// so nothing in this assembly reports one without saying what operation produced it
    /// and what it usually means.
    /// </remarks>
    public sealed class InjectionResult
    {
        private InjectionResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        /// <summary>True only if the DLL is now loaded in the target.</summary>
        public bool Success { get; }

        /// <summary>What happened, and where it leaves things.</summary>
        public string Message { get; }

        public override string ToString() => Message;

        internal static InjectionResult Loaded(string message) => new InjectionResult(true, message);

        /// <summary>
        /// Nothing was changed in the target. Used both for a refusal before anything was
        /// attempted and for a failure part way through, because from the caller's side
        /// they are the same outcome.
        /// </summary>
        internal static InjectionResult Refused(string message) => new InjectionResult(false, message);
    }

    /// <summary>A running process the DLL could be put into.</summary>
    public sealed class InjectionTarget
    {
        internal InjectionTarget(int processId, string name, bool? is64Bit)
        {
            ProcessId = processId;
            Name = name;
            Is64Bit = is64Bit;
        }

        public int ProcessId { get; }

        /// <summary>The executable's name without the extension, which is what --process takes.</summary>
        public string Name { get; }

        /// <summary>
        /// Null when the process could not be opened to ask, which is itself worth seeing:
        /// a target that cannot be opened cannot be injected into either.
        /// </summary>
        public bool? Is64Bit { get; }

        public override string ToString()
            => string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0,8}  {1,-32} {2}",
                ProcessId,
                Name,
                Is64Bit == null ? "cannot be opened" : Is64Bit.Value ? "x64" : "32-bit");
    }
}
