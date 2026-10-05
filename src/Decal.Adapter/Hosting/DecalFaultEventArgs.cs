using System;
using System.Reflection;

namespace Decal.Adapter.Hosting
{
    /// <summary>A plugin's handler that threw, and was skipped: whose it was, handling what, and why.</summary>
    public sealed class DecalFaultEventArgs : EventArgs
    {
        public DecalFaultEventArgs(Assembly assembly, string what, Exception exception)
        {
            Assembly = assembly;
            What = what ?? string.Empty;
            Exception = exception;
        }

        /// <summary>The assembly the handler belongs to; null when that could not be told.</summary>
        public Assembly Assembly { get; }

        /// <summary>What it was handling - an event's name, "the tick".</summary>
        public string What { get; }

        public Exception Exception { get; }
    }
}
