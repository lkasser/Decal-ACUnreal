using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AC.Host.Runtime
{
    /// <summary>
    /// Reads the game client's own log as it is written - AC:Unreal's <c>Saved\Logs\ACUnreal.log</c> -
    /// for what it says of itself: which screen it shows, and what it sends and hears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The relay sees what the client and the server say to each other, never what the client
    /// shows; and a client the server has taken to the character list may still be drawing the
    /// world. AC:Unreal says which in its log: <c>ACE UIFlow mode -> 3 layout=0x21000004</c> as the
    /// character select comes up, <c>-> 6</c> for the game, <c>-> 2</c> for the login screen,
    /// <c>-> 0</c> between; and its <c>[ACE]</c> lines - "CharacterLogOff (0xF653) sent", "Logged
    /// off - returned to character select", "EnterWorld request character=..." - what it did.
    /// Those lines are handed on as they appear (<see cref="Poll"/>), the last screen first.
    /// </para>
    /// <para>
    /// Only read, and never held open: the file is opened, read from where the last read stopped
    /// and closed again each time, sharing everything, so the client can always write it, and
    /// rename it to a backup when it starts again - when it is found shorter than before, it is a
    /// new log, read from its beginning.
    /// </para>
    /// </remarks>
    public sealed class ClientLog
    {
        /// <summary>Where AC:Unreal's installer puts it, and so where its log is when no client is running to ask.</summary>
        public static string DefaultPath
            => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ACUnreal", "ACUnreal", "Saved", "Logs", "ACUnreal.log");

        /// <summary>How much of a log already written is looked through for the screen the client shows now.</summary>
        private const int TailRead = 256 * 1024;

        private readonly Func<string> _findPath;
        private readonly Action<string> _line;
        private readonly StringBuilder _partial = new StringBuilder();
        private Decoder _decoder = Encoding.UTF8.GetDecoder();
        private long _position = -1;
        private long _nextLook;

        /// <summary>How often the log is looked for while there is none.</summary>
        public static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(3);

        /// <param name="findPath">Where the log is now, or null when there is none to read - asked again while there is none.</param>
        /// <param name="line">Given each line worth handing on, on the thread that polls.</param>
        public ClientLog(Func<string> findPath, Action<string> line)
        {
            _findPath = findPath ?? throw new ArgumentNullException(nameof(findPath));
            _line = line ?? throw new ArgumentNullException(nameof(line));
        }

        /// <summary>The log being read, or null before one has been found.</summary>
        public string Path { get; private set; }

        /// <summary>Whether a line says something about the client's screens or its session - the only lines handed on.</summary>
        public static bool IsWorthHanding(string line)
            => line != null && (line.Contains("[ACE]", StringComparison.Ordinal) || line.Contains("UIFlow mode", StringComparison.Ordinal));

        /// <summary>
        /// Reads what has been written since the last call and hands on the lines worth it. The first
        /// time a log is found, only the last screen it names is handed on, so a host started while
        /// the game runs knows where the client is without hearing its whole history again.
        /// </summary>
        public void Poll()
        {
            if (Path == null)
            {
                // Looking means asking every process its program, so not at every poll.
                long now = Environment.TickCount64;
                if (now < _nextLook)
                    return;

                _nextLook = now + (long)LookEvery.TotalMilliseconds;
                Path = _findPath();
                if (Path == null)
                    return;
            }

            try
            {
                using FileStream file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = file.Length;

                if (_position < 0)
                {
                    // Found now: only where the client is, from the end of what it has written.
                    long start = Math.Max(0, length - TailRead);
                    file.Seek(start, SeekOrigin.Begin);
                    StartOver();
                    string lastScreen = null;
                    foreach (string line in ReadLines(file, length - start, keepPartial: false))
                    {
                        if (line.Contains("UIFlow mode", StringComparison.Ordinal))
                            lastScreen = line;
                    }

                    _position = length;
                    StartOver();
                    if (lastScreen != null)
                        _line(lastScreen);
                    return;
                }

                // Shorter than before: the client started again and this is a new log.
                if (length < _position)
                {
                    _position = 0;
                    StartOver();
                }

                if (length == _position)
                    return;

                file.Seek(_position, SeekOrigin.Begin);
                List<string> lines = ReadLines(file, length - _position, keepPartial: true);
                _position = length;

                foreach (string line in lines)
                {
                    if (IsWorthHanding(line))
                        _line(line);
                }
            }
            catch (FileNotFoundException)
            {
                Forget();
            }
            catch (DirectoryNotFoundException)
            {
                Forget();
            }
            catch (IOException)
            {
                // Being renamed or written just now; the next poll reads it.
            }
            catch (UnauthorizedAccessException)
            {
                Forget();
            }
        }

        /// <summary>The log has gone: looked for again at the next poll, and read from its start when found.</summary>
        private void Forget()
        {
            Path = null;
            _position = 0;
            StartOver();
        }

        /// <summary>Nothing half read is carried into what is read next.</summary>
        private void StartOver()
        {
            _partial.Clear();
            _decoder = Encoding.UTF8.GetDecoder();
        }

        /// <summary>
        /// The whole lines in the next <paramref name="count"/> bytes. A line not yet ended is kept for
        /// the next read when <paramref name="keepPartial"/>, else let go.
        /// </summary>
        private List<string> ReadLines(Stream file, long count, bool keepPartial)
        {
            byte[] bytes = new byte[count];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = file.Read(bytes, read, bytes.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }

            // One decoder across reads, so a character the client had half written - its em dashes are
            // three bytes - is read whole next time rather than garbled now.
            char[] chars = new char[_decoder.GetCharCount(bytes, 0, read)];
            int decoded = _decoder.GetChars(bytes, 0, read, chars, 0);
            _partial.Append(chars, 0, decoded);
            string text = _partial.ToString();
            _partial.Clear();

            List<string> lines = new List<string>();
            int begin = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                    continue;

                lines.Add(text.Substring(begin, i - begin).TrimEnd('\r'));
                begin = i + 1;
            }

            if (keepPartial && begin < text.Length)
                _partial.Append(text, begin, text.Length - begin);

            return lines;
        }

        /// <summary>
        /// Where the running client writes its log: beside its install, found from its program's
        /// path - the game's own under <c>Binaries\Win64</c>, or the launcher beside the install. The
        /// installer's place when no client says otherwise and a log is there; null when there is none.
        /// </summary>
        public static string FindPath()
        {
            foreach (string candidate in RunningClientLogs())
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static IEnumerable<string> RunningClientLogs()
        {
            List<string> found = new List<string>();
            foreach (Process process in Process.GetProcessesByName(ClientWatcher.ProcessName))
            {
                try
                {
                    string program = process.MainModule?.FileName;
                    string folder = program != null ? System.IO.Path.GetDirectoryName(program) : null;
                    if (folder == null)
                        continue;

                    // ACUnreal\Binaries\Win64\ACUnreal.exe writes ACUnreal\Saved\Logs; the launcher
                    // beside the install folder finds it under ACUnreal.
                    found.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, "..", "..", "Saved", "Logs", "ACUnreal.log")));
                    found.Add(System.IO.Path.Combine(folder, "ACUnreal", "Saved", "Logs", "ACUnreal.log"));
                }
                catch (Exception)
                {
                    // Gone, or not ours to look at.
                }
                finally
                {
                    process.Dispose();
                }
            }

            // Only while a client runs: a log left by one that has gone says nothing of now.
            if (found.Count > 0)
                found.Add(DefaultPath);

            return found;
        }
    }
}
