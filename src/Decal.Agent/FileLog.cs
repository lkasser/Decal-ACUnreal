using System;
using System.Globalization;
using System.IO;
using System.Text;
using AC.Host.Plugins;

namespace Decal.Agent
{
    /// <summary>
    /// The host's log, written to a file a day under the data folder's logs. A window program has
    /// no console, and "what did the host say?" still needs an answer.
    /// </summary>
    internal sealed class FileLog : IPluginLog, IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _folder;
        private StreamWriter _writer;
        private DateTime _day;

        public FileLog(string folder)
        {
            _folder = folder;
        }

        /// <summary>Today's file, whether or not anything has been written to it yet.</summary>
        public string CurrentPath => Path.Combine(_folder, "DecalAgent-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

        public string Folder => _folder;

        public void Info(string message) => Write("INFO ", message, null);

        public void Warn(string message) => Write("WARN ", message, null);

        public void Error(string message, Exception exception = null) => Write("ERROR", message, exception);

        private void Write(string level, string message, Exception exception)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + level + " " + message;
            if (exception != null)
                line += Environment.NewLine + "    " + exception.GetType().Name + ": " + exception.Message;

            lock (_gate)
            {
                try
                {
                    Writer()?.WriteLine(line);
                }
                catch (IOException)
                {
                    // A log that cannot be written is not worth taking the host down for.
                }
            }
        }

        private StreamWriter Writer()
        {
            if (_writer != null && _day == DateTime.Today)
                return _writer;

            _writer?.Dispose();
            _writer = null;

            try
            {
                Directory.CreateDirectory(_folder);
                FileStream stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _day = DateTime.Today;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _writer = null;
            }

            return _writer;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
