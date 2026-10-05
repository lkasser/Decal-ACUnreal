using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Setup.Common
{
    /// <summary>
    /// What a setup did, line by line, in the file /Log names; nowhere when it names none. A
    /// silent run has no window, and its exit code alone says only that something went wrong.
    /// </summary>
    public sealed class SetupLog
    {
        private readonly object _gate = new object();
        private readonly string _path;

        public SetupLog(string path)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }

        /// <summary>A log that keeps nothing.</summary>
        public static SetupLog None { get; } = new SetupLog(null);

        public void Info(string message) => Write("INFO ", message);

        public void Warn(string message) => Write("WARN ", message);

        public void Error(string message) => Write("ERROR", message);

        private void Write(string level, string message)
        {
            if (_path == null)
                return;

            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + level + " " + message + Environment.NewLine;
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.AppendAllText(_path, line, new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // A log that cannot be written is no reason to stop an install.
                }
            }
        }
    }
}
