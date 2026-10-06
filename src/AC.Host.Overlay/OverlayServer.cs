using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AC.Host.Overlay
{
    /// <summary>
    /// Serves snapshots to an injected overlay over a named pipe, and raises the commands
    /// the overlay sends back.
    /// </summary>
    /// <remarks>
    /// The overlay is optional and lives in another process that the host does not
    /// control: it may never appear, it may appear an hour late, and it may vanish
    /// mid-write when the game closes or its renderer trips over a client update. None of
    /// that is allowed to reach the host, so every failure in here is reported through the
    /// error callback and the pipe is simply opened again. <see cref="Publish"/> never
    /// blocks and never throws for anything the overlay does or does not do.
    ///
    /// Frames are length-prefixed: four bytes of little-endian byte count, then that many
    /// UTF-8 bytes. A newline delimiter would have been shorter to write on the C++ side,
    /// but it would rest on the serialiser never emitting a raw newline inside a string -
    /// true only as long as nobody turns on indented output or swaps the encoder, and a
    /// change like that would show up as an overlay that silently draws half a frame. A
    /// byte count rests on nothing, and lets the reader wait for exactly the right number
    /// of bytes instead of scanning for a delimiter.
    ///
    /// Snapshots are coalesced rather than queued. If the overlay is slow, an older frame
    /// is worthless - the next one describes the same world more accurately - so a pending
    /// frame is dropped in favour of the newest.
    ///
    /// <para>
    /// Images are the opposite: each is needed, none supersedes another, and each is sent
    /// once per connection rather than with every snapshot. They are kept in a list of
    /// their own, and every connection sends the ones it has not yet sent - all of them,
    /// for a new one - before the snapshot that may name them.
    /// </para>
    /// </remarks>
    public sealed class OverlayServer : IAsyncDisposable
    {
        /// <summary>The base name a host uses unless it is told otherwise.</summary>
        public const string DefaultPipeName = "achost-overlay";

        /// <summary>
        /// The most a single command may be. A length prefix is four bytes of whatever
        /// happens to be at the other end, so it is not trusted: a wrong one would
        /// otherwise ask for a multi-gigabyte buffer.
        /// </summary>
        private const int MaxCommandBytes = 1 << 20;

        private const int ReopenDelayMilliseconds = 500;
        private const int ShutdownWaitSeconds = 2;

        private readonly Action<string, Exception> _onError;
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly object _gate = new object();

        private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        /// <summary>The last frame published, kept so a late overlay is not sent a blank one.</summary>
        private byte[] _latest;

        /// <summary>
        /// Written to the outgoing channel when an image is published, to wake the send loop.
        /// Never sent; a reference compared by identity.
        /// </summary>
        private static readonly byte[] ImagesChanged = new byte[1];

        /// <summary>The same, for the keys to hold.</summary>
        private static readonly byte[] InputChanged = new byte[1];

        /// <summary>The latest keys-to-hold frame, or null before any.</summary>
        private byte[] _input;
        private long _inputSequence;

        /// <summary>
        /// The overlay's own limit on one message. An image frame past it would not merely be
        /// dropped: the overlay treats an oversized message as a lost stream and hangs up.
        /// </summary>
        private const int MaxFrameBytes = 1 << 20;

        private sealed class ImageEntry
        {
            public string Key;
            public long Sequence;
            public byte[] Frame;
        }

        /// <summary>Every image published, the latest for each key, in the order published.</summary>
        private readonly System.Collections.Generic.List<ImageEntry> _images = new System.Collections.Generic.List<ImageEntry>();
        private long _imageSequence;

        /// <summary>
        /// The last snapshot as JSON with the revision left at zero, which is what one
        /// snapshot is compared against the next.
        /// </summary>
        private string _lastShape;

        private long _revision;

        /// <summary>When the last frame went out, changed or not.</summary>
        private long _lastSentMs;

        /// <summary>
        /// How often a quiet host repeats itself. A second is well inside the overlay's
        /// staleness threshold and far slower than the tick, so an idle session costs
        /// one small message a second rather than four.
        /// </summary>
        private const long HeartbeatMs = 1000;
        private long _published;
        private long _skipped;
        private Task _loop;
        private bool _disposed;
        private volatile bool _connected;

        /// <summary>
        /// Creates a server on a pipe named after <paramref name="pipeName"/> and this
        /// process.
        /// </summary>
        /// <param name="pipeName">
        /// The base name. The pipe actually served is <see cref="PipeName"/>, which
        /// includes the host's process id: two hosts run side by side often enough - one
        /// per character - and a shared pipe name would mean the second one silently
        /// never getting a client.
        /// </param>
        /// <param name="onError">
        /// Called with a sentence and the exception whenever the pipe misbehaves. The host
        /// passes its logger here; the library takes a delegate rather than an interface so
        /// that it needs no reference to the host.
        /// </param>
        public OverlayServer(string pipeName = DefaultPipeName, Action<string, Exception> onError = null)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("A pipe name is needed.", nameof(pipeName));

            // Used as given. Suffixing the process id by default made the common case
            // fail silently - the injected overlay connects to the base name and has no
            // way to learn a pid - to solve a collision that only happens when someone
            // deliberately runs two hosts. NameFor is still here for that case, and then
            // both sides are told the same name explicitly.
            PipeName = pipeName;
            _onError = onError;
        }

        /// <summary>
        /// The pipe name for a given base name and host process. The injected overlay is
        /// told which process to talk to, and works the same name out from here.
        /// </summary>
        public static string NameFor(string pipeName, int processId) => pipeName + "-" + processId.ToString();

        /// <summary>The pipe this server listens on, base name and process id included.</summary>
        public string PipeName { get; }

        /// <summary>Whether an overlay is attached right now.</summary>
        public bool IsConnected => _connected;

        /// <summary>The revision of the last snapshot published.</summary>
        public long Revision
        {
            get { lock (_gate) return _revision; }
        }

        /// <summary>Snapshots that differed from the one before and so were published.</summary>
        public long Published
        {
            get { lock (_gate) return _published; }
        }

        /// <summary>Snapshots identical to the one before, which were not published.</summary>
        public long Skipped
        {
            get { lock (_gate) return _skipped; }
        }

        /// <summary>
        /// A command from the overlay. Raised on the pipe's own thread, so a host with a
        /// game thread hands the work to it rather than doing it here. A handler that
        /// throws is reported and the others still run.
        /// </summary>
        public event EventHandler<OverlayCommand> CommandReceived;

        /// <summary>
        /// Starts listening. Harmless to call more than once, and not required before
        /// <see cref="Publish"/>: a host that never starts one still keeps its snapshots
        /// up to date.
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_loop != null || _disposed)
                    return;

                _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
            }
        }

        /// <summary>
        /// Offers a snapshot. Returns true if it differed from the last one and so became
        /// a new revision, false if it was identical and was dropped.
        /// </summary>
        /// <remarks>
        /// The comparison is on the serialised form. Hand-written equality over the whole
        /// tree would be another thing to keep in step with overlay_state.h, and getting it
        /// subtly wrong would show up as an overlay that stops redrawing. The revision is
        /// excluded from the comparison - it changes on every publish by definition, so
        /// including it would make every snapshot look new.
        ///
        /// <paramref name="state"/> has its revision filled in, so the caller can read back
        /// what was published.
        /// </remarks>
        public bool Publish(OverlayState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            byte[] frame;
            lock (_gate)
            {
                // The publish time is excluded along with the revision, for the same
                // reason: both describe the act of publishing rather than what is being
                // published, and a clock in the comparison makes every snapshot look new.
                // It did - the revision climbed on every tick and the overlay was told to
                // redraw four times a second while nothing had changed.
                state.Revision = 0;
                long publishedAt = state.PublishedMs;
                state.PublishedMs = 0;

                string shape = OverlayJson.ToJson(state);

                if (string.Equals(shape, _lastShape, StringComparison.Ordinal))
                {
                    state.PublishedMs = publishedAt;
                    // Left holding the revision it matches rather than zero: a caller that
                    // shows the revision should not see it fall back on a quiet tick.
                    state.Revision = _revision;
                    _skipped++;

                    // Unchanged is not the same as absent. Taking the clock out of the
                    // comparison was right, but it meant an idle host never sent anything
                    // at all, and the overlay - which judges liveness by the timestamp it
                    // last received - declared a healthy host stale after a quiet minute.
                    // So a quiet host still says so, at most once a second: the same
                    // revision, a fresh time.
                    if (_latest != null && publishedAt - _lastSentMs >= HeartbeatMs)
                    {
                        _lastSentMs = publishedAt;
                        _latest = Frame(OverlayJson.ToJson(state));
                        _outgoing.Writer.TryWrite(_latest);
                    }

                    return false;
                }

                _lastShape = shape;
                state.PublishedMs = publishedAt;
                state.Revision = ++_revision;
                _lastSentMs = publishedAt;
                _latest = Frame(OverlayJson.ToJson(state));
                _published++;
                frame = _latest;
            }

            // Never blocks, whether or not anything is reading: the channel holds one
            // frame and drops the older of two.
            _outgoing.Writer.TryWrite(frame);
            return true;
        }

        /// <summary>
        /// Adds an image the overlay may draw, or replaces the one with the same key. Sent to
        /// the overlay now if one is attached, and to every overlay that attaches later.
        /// </summary>
        /// <returns>False, with the reason reported, if the image is too large to send.</returns>
        public bool PublishImage(OverlayImage image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (string.IsNullOrEmpty(image.Key)) throw new ArgumentException("An image needs a key.", nameof(image));

            byte[] frame = Frame(OverlayJson.ToJson(image));
            if (frame.Length - sizeof(int) > MaxFrameBytes)
            {
                Report($"The image {image.Key} ({image.Width}x{image.Height}) is too large to send to the overlay.", null);
                return false;
            }

            lock (_gate)
            {
                _images.RemoveAll(entry => string.Equals(entry.Key, image.Key, StringComparison.Ordinal));
                _images.Add(new ImageEntry { Key = image.Key, Sequence = ++_imageSequence, Frame = frame });
            }

            _outgoing.Writer.TryWrite(ImagesChanged);
            return true;
        }

        /// <summary>
        /// Tells the overlay which keys to hold down in the game - the whole set, by virtual-key
        /// code; an empty set releases everything. The overlay lets go of every key if it hears
        /// nothing for a second, so while keys are held this must be repeated more often than
        /// that.
        /// </summary>
        public void PublishInput(System.Collections.Generic.IEnumerable<int> held)
        {
            lock (_gate)
            {
                _held = held == null ? new System.Collections.Generic.List<int>() : new System.Collections.Generic.List<int>(held);
                PublishInputLocked();
            }

            _outgoing.Writer.TryWrite(InputChanged);
        }

        /// <summary>
        /// How long a click rides in the input frames after it is asked for: long enough that a
        /// frame lost or dropped for a newer one cannot lose it, short enough that an overlay
        /// attaching afterwards is not sent a click meant for the game as it was.
        /// </summary>
        public static readonly TimeSpan ClickLasts = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Asks the overlay to click in the game's window, once (<see cref="OverlayClick"/>). Its
        /// id is set here, different from every click any host has asked for before, and goes
        /// out with the keys held now - and with every change of them for <see cref="ClickLasts"/>.
        /// </summary>
        /// <returns>The click's id.</returns>
        public long PublishClick(OverlayClick click)
        {
            if (click == null) throw new ArgumentNullException(nameof(click));

            lock (_gate)
            {
                // Milliseconds since 1970, or one more than the last: a host started afresh never
                // repeats an id an overlay has already clicked for.
                _clickId = Math.Max(_clickId + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                click.Id = _clickId;
                _click = click;
                _clickAskedMs = Environment.TickCount64;
                PublishInputLocked();
            }

            _outgoing.Writer.TryWrite(InputChanged);
            return click.Id;
        }

        private void PublishInputLocked()
        {
            OverlayInput input = new OverlayInput();
            input.Held.AddRange(_held);
            if (_click != null && Environment.TickCount64 - _clickAskedMs < (long)ClickLasts.TotalMilliseconds)
                input.Click = _click;

            input.Sequence = ++_inputSequence;
            _input = Frame(OverlayJson.ToJson(input));
        }

        /// <summary>The keys last asked to be held, and the last click and when it was asked for; under the gate.</summary>
        private System.Collections.Generic.List<int> _held = new System.Collections.Generic.List<int>();
        private OverlayClick _click;
        private long _clickAskedMs;
        private long _clickId;

        /// <summary>How many distinct images have been published.</summary>
        public int ImageCount
        {
            get
            {
                lock (_gate)
                    return _images.Count;
            }
        }

        /// <summary>The images published after <paramref name="after"/>, oldest first, and the newest sequence.</summary>
        private System.Collections.Generic.List<byte[]> ImagesSince(long after, out long newest)
        {
            System.Collections.Generic.List<byte[]> frames = new System.Collections.Generic.List<byte[]>();
            lock (_gate)
            {
                newest = after;
                foreach (ImageEntry entry in _images)
                {
                    if (entry.Sequence <= after)
                        continue;

                    frames.Add(entry.Frame);
                    newest = Math.Max(newest, entry.Sequence);
                }
            }

            return frames;
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    pipe?.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    // Most often another host already holds this name, which is worth
                    // saying once a second rather than in a tight loop.
                    pipe?.Dispose();
                    Report($"The overlay pipe '{PipeName}' could not be opened.", ex);

                    try
                    {
                        await Task.Delay(ReopenDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                _connected = true;
                try
                {
                    await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Either the host is stopping or the overlay went; both are ordinary.
                }
                catch (Exception ex)
                {
                    Report("The overlay connection ended unexpectedly.", ex);
                }
                finally
                {
                    _connected = false;

                    try
                    {
                        if (pipe.IsConnected)
                            pipe.Disconnect();
                    }
                    catch (Exception)
                    {
                        // The client has already gone; there is nothing to disconnect.
                    }

                    pipe.Dispose();
                }
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            // Cancelled as soon as either direction notices the overlay has gone, so the
            // other one stops waiting instead of holding the pipe open for nothing.
            using CancellationTokenSource gone = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task reading = ReadCommandsAsync(pipe, gone);

            try
            {
                // Keys held for an overlay that has gone mean nothing to this one; it starts
                // with none, and the host's next repeat says what it wants now. Noted before
                // anything else, so a change published while this connection is being set up
                // is newer than what was noted, and is sent.
                byte[] lastInput;
                lock (_gate) lastInput = _input;

                // Anything the channel still holds was published before this overlay
                // existed and is superseded by the frames below.
                while (_outgoing.Reader.TryRead(out _))
                {
                }

                // Every image first, so the first snapshot's images are already there.
                long sentImages = 0;
                foreach (byte[] image in ImagesSince(sentImages, out sentImages))
                    await WriteFrameAsync(pipe, image, gone.Token).ConfigureAwait(false);

                byte[] first;
                lock (_gate) first = _latest;

                byte[] lastState = null;
                if (first != null)
                {
                    await WriteFrameAsync(pipe, first, gone.Token).ConfigureAwait(false);
                    lastState = first;
                }

                // A change of keys that arrived while the first frames went out, and whose
                // wake-up the channel no longer holds, goes now rather than on the next wake.
                byte[] pending;
                lock (_gate) pending = _input;
                if (pending != null && !ReferenceEquals(pending, lastInput))
                {
                    await WriteFrameAsync(pipe, pending, gone.Token).ConfigureAwait(false);
                    lastInput = pending;
                }

                while (!gone.IsCancellationRequested)
                {
                    // The channel holds one frame, so any wake-up may have displaced another:
                    // after each, whatever this connection has not had yet - images, keys,
                    // the latest snapshot - is sent, whichever woke it.
                    await _outgoing.Reader.ReadAsync(gone.Token).ConfigureAwait(false);

                    foreach (byte[] image in ImagesSince(sentImages, out sentImages))
                        await WriteFrameAsync(pipe, image, gone.Token).ConfigureAwait(false);

                    byte[] input;
                    lock (_gate) input = _input;
                    if (input != null && !ReferenceEquals(input, lastInput))
                    {
                        await WriteFrameAsync(pipe, input, gone.Token).ConfigureAwait(false);
                        lastInput = input;
                    }

                    byte[] state;
                    lock (_gate) state = _latest;
                    if (state != null && !ReferenceEquals(state, lastState))
                    {
                        await WriteFrameAsync(pipe, state, gone.Token).ConfigureAwait(false);
                        lastState = state;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The overlay went, or the host is stopping.
            }
            catch (ChannelClosedException)
            {
                // Disposal completes the channel a moment before it cancels the token, so
                // this is the same thing as cancellation arriving.
            }
            catch (IOException ex)
            {
                // A broken pipe: the overlay was unloaded or the game closed mid-write.
                Report("The overlay stopped reading.", ex);
            }
            finally
            {
                gone.Cancel();

                try
                {
                    await reading.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Already reported where it happened.
                }
            }
        }

        private async Task ReadCommandsAsync(NamedPipeServerStream pipe, CancellationTokenSource gone)
        {
            byte[] prefix = new byte[sizeof(int)];

            try
            {
                while (!gone.IsCancellationRequested)
                {
                    if (!await ReadExactlyAsync(pipe, prefix, prefix.Length, gone.Token).ConfigureAwait(false))
                        return;

                    int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                    if (length <= 0 || length > MaxCommandBytes)
                    {
                        Report($"The overlay sent a frame of {length} bytes, which cannot be right; dropping the connection.", null);
                        return;
                    }

                    byte[] body = new byte[length];
                    if (!await ReadExactlyAsync(pipe, body, length, gone.Token).ConfigureAwait(false))
                        return;

                    OverlayCommand command;
                    try
                    {
                        command = OverlayJson.ReadCommand(Encoding.UTF8.GetString(body));
                    }
                    catch (JsonException ex)
                    {
                        // One bad frame is not a reason to drop the overlay: the next one
                        // may well be fine, and the alternative is a UI that stops
                        // responding after a single mangled click.
                        Report("The overlay sent something that is not a command.", ex);
                        continue;
                    }

                    if (command != null)
                        RaiseCommand(command);
                }
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }
            catch (IOException ex)
            {
                Report("The overlay closed the pipe.", ex);
            }
            catch (Exception ex)
            {
                Report("Reading from the overlay failed.", ex);
            }
            finally
            {
                // Tells the writing side there is no longer anyone there.
                try
                {
                    gone.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The connection is already being torn down.
                }
            }
        }

        private static async Task<bool> ReadExactlyAsync(Stream pipe, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            int read = 0;
            while (read < count)
            {
                int got = await pipe.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);

                // Zero bytes on a pipe means the other end has gone, not that it is quiet.
                if (got == 0)
                    return false;

                read += got;
            }

            return true;
        }

        private static async Task WriteFrameAsync(Stream pipe, byte[] frame, CancellationToken cancellationToken)
        {
            // The prefix and the payload go in one write, so a reader can never be handed
            // a length without the bytes it promises.
            await pipe.WriteAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static byte[] Frame(string json)
        {
            int count = Encoding.UTF8.GetByteCount(json);
            byte[] frame = new byte[sizeof(int) + count];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, sizeof(int)), count);
            Encoding.UTF8.GetBytes(json, frame.AsSpan(sizeof(int)));
            return frame;
        }

        private void RaiseCommand(OverlayCommand command)
        {
            EventHandler<OverlayCommand> handler = CommandReceived;
            if (handler == null)
                return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<OverlayCommand>)subscriber)(this, command);
                }
                catch (Exception ex)
                {
                    // This runs on the pipe's thread. An exception let out here would end
                    // the connection and lose every later command.
                    Report($"A handler threw on overlay command '{command.Name}'.", ex);
                }
            }
        }

        private void Report(string message, Exception exception)
        {
            try
            {
                _onError?.Invoke(message, exception);
            }
            catch (Exception)
            {
                // A logger that throws is not going to be told about it.
            }
        }

        /// <summary>
        /// Stops listening and drops any attached overlay. Safe to call twice, and never
        /// throws.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            Task loop;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                loop = _loop;
            }

            _outgoing.Writer.TryComplete();

            try
            {
                _stopping.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by a racing caller.
            }

            if (loop != null)
            {
                try
                {
                    // Bounded: a pipe wait that ignores cancellation must not keep the host
                    // from shutting down.
                    await loop.WaitAsync(TimeSpan.FromSeconds(ShutdownWaitSeconds)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Nothing useful left to do with it at this point.
                }
            }

            _stopping.Dispose();
        }
    }
}
