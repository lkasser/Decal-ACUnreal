using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AC.Protocol;

namespace AC.Proxy
{
    /// <summary>
    /// Rewrites the client-to-server stream so that messages of our own can ride
    /// along inside the client's packets.
    /// </summary>
    /// <remarks>
    /// Two properties of the protocol rule out simply sending our own packets, and
    /// together they dictate this design.
    ///
    /// The first is the checksum. A live packet's checksum is masked with a key from
    /// an ISAAC stream shared by both ends. The receiver tolerates keys arriving out
    /// of order by walking its stream forward to find whichever key it is shown - so
    /// spending a key the real client still needs does not merely waste it, it makes
    /// the client's next packet unverifiable and poisons the window. We therefore
    /// never take a key. We recover the key from the packet in front of us
    /// (<see cref="AcPacket.TryRecoverEncryptionKey"/>), alter that packet, and
    /// re-stamp it with the same key. The server sees exactly one packet using that
    /// key, which is what it expected.
    ///
    /// The second is fragment sequencing. The server requires each fragment to be
    /// exactly one past the last it accepted, buffering anything early. Inserting a
    /// fragment therefore shifts every later one, so this renumbers the client's
    /// fragments by a running offset and assigns injected fragments from what the
    /// server has actually been shown.
    ///
    /// Later means later in the numbering, not later to arrive. Each change is anchored
    /// at the furthest number gone on when it was made and moves only the numbers past
    /// it, so a fragment takes the number its place gives it whenever it comes. That
    /// matters in the other direction, where this carries lines of ours to the client:
    /// ACE numbers a whole bundle and then sends its small messages wherever they fit,
    /// up to a couple of hundred numbers ahead of the large ones before them.
    ///
    /// All of that numbering is per session. The counters are reset when a new
    /// session begins, because a fragment number is meaningless across the boundary
    /// and carrying one over places an injected fragment thousands of slots into the
    /// future, where the server buffers it forever and every later fragment stalls
    /// behind the hole it left. A session can outlive the relay, though: a host stopped
    /// and started again while the game stays connected takes the numbering up where the
    /// last one left it (<see cref="SaveState"/>, <see cref="RestoreState"/>), since the
    /// far ends never learned anything had changed.
    ///
    /// The same renumbering lets a message of the client's be taken out
    /// (<see cref="Withhold"/>): a line the player typed for a plugin, which the server
    /// must never see. Taking one message out is inserting one run backwards - every
    /// later message moves down by one - so the offset simply goes the other way. The
    /// packet itself is still forwarded, with the same key and packet number, because
    /// the server acknowledges packets by that number and would ask for a missing one
    /// again for ever.
    ///
    /// A message in several fragments is taken out whole. Its fragments all carry the
    /// message's one number and only the first says what the message is, so the
    /// decision is made there, and then every fragment with that number is taken out,
    /// in whichever packet it comes and however often it is resent; the offset moves
    /// once, for the message. ACE often sends a message's last fragment first, in the
    /// room a smaller message left, so a later fragment of a message not yet seen is
    /// held back until the first arrives: taken out with it, or put back in beside it.
    /// Holding back is only safe while nothing later has gone past. Once the stream
    /// moves on, numbers have been given that taking the message out behind them would
    /// make wrong, so its held fragments go at once and it is never taken out - as
    /// with any message some part of which has already gone through.
    ///
    /// Not thread-safe by itself; <see cref="Rewrite"/> runs on the relay's receive
    /// path. Queueing from another thread is safe.
    /// </remarks>
    public sealed class ClientStreamRewriter
    {
        /// <summary>
        /// Largest datagram to hand the server. The protocol's own limit; a rewritten
        /// packet that would exceed it carries fewer of our fragments instead.
        /// </summary>
        public const int MaxPacketSize = 1024;

        /// <summary>
        /// How many messages taken out to remember for retransmissions. Comfortably more
        /// than a stalled window, and bounded so a long session cannot grow it.
        /// </summary>
        private const int RememberedWithheld = 512;

        /// <summary>
        /// How far behind the furthest number gone on a change to the numbering is still kept
        /// apart. Fragments come late by up to a couple of hundred numbers, and resent ones by
        /// a handful, so anything further back is long settled.
        /// </summary>
        private const uint ShiftsKeptFor = 4096;

        /// <summary>
        /// How many messages may wait at once, held back for their first fragment or being
        /// taken out until their last. Each waits a packet or two, so this is many times what
        /// a healthy stream needs, and it bounds what a lossy one can make this keep.
        /// </summary>
        private const int MessagesInHand = 16;

        /// <summary>
        /// A drop of at least this many in the client's fragment numbering is read as
        /// a new session. Retransmissions step back by a handful; only a reconnect,
        /// which restarts numbering from the beginning, steps back by more.
        /// </summary>
        private const uint RestartBackwardsJump = 64;

        /// <summary>
        /// Header flags that only appear while a session is being established. Any of
        /// them means the numbering is about to start over.
        /// </summary>
        private const PacketHeaderFlags HandshakeFlags =
            PacketHeaderFlags.LoginRequest
            | PacketHeaderFlags.WorldLoginRequest
            | PacketHeaderFlags.ConnectRequest
            | PacketHeaderFlags.ConnectResponse
            | PacketHeaderFlags.Referral;

        private readonly object _queueGate = new object();
        private readonly Queue<PendingMessage> _pending = new Queue<PendingMessage>();

        /// <summary>
        /// Where the numbering has been changed, oldest first: past each anchor, every client
        /// number moves by the delta - up one past where a message of ours went in, down one past
        /// a message taken out. Anchors never go down, since each is at or past every number that
        /// had gone on when it was made; so a fragment seen before a change is not moved by it.
        /// </summary>
        private readonly List<(uint Anchor, int Delta)> _shifts = new List<(uint, int)>();

        /// <summary>
        /// The numbers of messages taken out, so a later or resent fragment of one is taken
        /// out too rather than reaching the server late.
        /// </summary>
        private readonly HashSet<uint> _withheld = new HashSet<uint>();
        private readonly Queue<uint> _withheldOrder = new Queue<uint>();

        /// <summary>
        /// Messages being taken out whose fragments have not all arrived, by number: put
        /// together to be handed on through <see cref="Withheld"/> once they have.
        /// </summary>
        private readonly Dictionary<uint, TakenMessage> _taking = new Dictionary<uint, TakenMessage>();

        /// <summary>
        /// Later fragments of new messages whose first has not arrived, by number, held back
        /// until it says what the message is.
        /// </summary>
        private readonly SortedDictionary<uint, List<AcFragment>> _held = new SortedDictionary<uint, List<AcFragment>>();

        /// <summary>
        /// Held fragments let go, numbered already, that did not fit the packet they were let
        /// go in. They ride in the next.
        /// </summary>
        private readonly Queue<AcFragment> _letGo = new Queue<AcFragment>();

        /// <summary>
        /// What to add to the number of a client fragment past every anchor: up by one for each
        /// message of ours sent, down by one for each of the client's taken out.
        /// </summary>
        private int _offset;

        /// <summary>
        /// Whether this session's numbering has been changed at all. Once it has, every packet is
        /// looked at, even with the offset back at zero - one message put in and one taken out -
        /// since a retransmission of a fragment renumbered earlier must keep its new number.
        /// </summary>
        private bool _renumbered;

        /// <summary>The highest number the server has been shown, this session.</summary>
        private uint _lastEmittedSequence;

        /// <summary>
        /// The highest number the client has used that has gone on - or been taken out - this
        /// session. A fragment held back has done neither.
        /// </summary>
        private uint _lastOriginalSequence;

        /// <summary>Messages of ours that have been woven into the stream.</summary>
        public int InjectedMessages { get; private set; }

        /// <summary>Messages of the client's that have been taken out of the stream.</summary>
        public int WithheldMessages { get; private set; }

        /// <summary>Of <see cref="WithheldMessages"/>, those that came in more than one fragment.</summary>
        public int WithheldSplitMessages { get; private set; }

        /// <summary>
        /// Messages taken out whose fragments never all arrived, and so were never handed on
        /// through <see cref="Withheld"/>. What did arrive of each was taken out all the same.
        /// </summary>
        public int AbandonedMessages { get; private set; }

        /// <summary>Fragments held back until the first fragment of their message arrived.</summary>
        public int HeldFragments { get; private set; }

        /// <summary>
        /// Decides whether one of the client's messages is kept from the server: given its
        /// opcode and the bytes after it, true to take it out. Null, the default, keeps
        /// everything. Called on the relay's receive path, so it must be quick and must not
        /// block. Asked once a message, at its first fragment - so for a message in several
        /// fragments the bytes are that fragment's share only, which always holds what names
        /// the message - and only while taking it out is still possible: never for a message
        /// resent, nor for one some part of which has already gone through.
        /// </summary>
        public Func<uint, ReadOnlyMemory<byte>, bool> Withhold { get; set; }

        /// <summary>
        /// Raised, on the relay's receive path, for each message taken out once all of it has
        /// arrived: its opcode and the bytes after it, its fragments put back together. Once per
        /// message - not again when the client retransmits it.
        /// </summary>
        public event Action<uint, ReadOnlyMemory<byte>> Withheld;

        /// <summary>
        /// Whether the sending end numbers its fragments in the order it sends them, so that a
        /// number far behind the furthest seen can only mean a new session. True of the game
        /// client. Not of ACE, which sends the small messages and last fragments of a big bundle
        /// as they fit - a couple of hundred numbers early, at login - so for the stream from the
        /// server this is false, and only the handshake starts the numbering over.
        /// </summary>
        public bool ArrivesInOrder { get; set; } = true;

        /// <summary>Times the numbering has been restarted for a new session.</summary>
        public int SessionResets { get; private set; }

        /// <summary>Messages still waiting for a packet to ride out on.</summary>
        public int PendingMessages
        {
            get
            {
                lock (_queueGate)
                    return _pending.Count;
            }
        }

        /// <summary>
        /// Packets that could not be rewritten because their key was unrecoverable.
        /// Only handshake packets lack an encrypted checksum, so this staying at its
        /// starting value after login is the healthy case.
        /// </summary>
        public int UnrewritablePackets { get; private set; }

        /// <summary>Queues a message to be sent as the client. Returns its queue depth.</summary>
        public int Enqueue(uint opcode, ReadOnlyMemory<byte> payload)
        {
            lock (_queueGate)
            {
                _pending.Enqueue(new PendingMessage(opcode, payload));
                return _pending.Count;
            }
        }

        /// <summary>
        /// Starts the numbering over, and abandons anything queued.
        /// </summary>
        /// <remarks>
        /// Queued messages are dropped rather than carried across: they name objects
        /// from a world that no longer exists, and the ids would be wrong even if the
        /// numbering were right. So are fragments held back or half a message taken out:
        /// their numbers belong to the session that has gone.
        /// </remarks>
        public void ResetSession()
        {
            _offset = 0;
            _renumbered = false;
            _lastEmittedSequence = 0;
            _lastOriginalSequence = 0;
            _shifts.Clear();
            _withheld.Clear();
            _withheldOrder.Clear();
            _taking.Clear();
            _held.Clear();
            _letGo.Clear();
            SessionResets++;

            lock (_queueGate)
                _pending.Clear();
        }

        /// <summary>
        /// The numbering as it stands, and every fragment held, for a rewriter that is to carry the
        /// same session on - another relay's, once this one has stopped.
        /// </summary>
        /// <remarks>
        /// Taken while nothing is being rewritten: once the relay has stopped, or before it starts.
        /// Messages queued to go out are not part of it; see <see cref="RelayState"/>.
        /// </remarks>
        public RewriterState SaveState()
        {
            RewriterState state = new RewriterState
            {
                Offset = _offset,
                Renumbered = _renumbered,
                LastEmittedSequence = _lastEmittedSequence,
                LastOriginalSequence = _lastOriginalSequence,
                InjectedMessages = InjectedMessages,
                WithheldMessages = WithheldMessages,
                WithheldSplitMessages = WithheldSplitMessages,
                AbandonedMessages = AbandonedMessages,
                HeldFragments = HeldFragments,
                SessionResets = SessionResets,
                UnrewritablePackets = UnrewritablePackets,
            };

            state.Shifts.AddRange(_shifts);
            state.Withheld.AddRange(_withheldOrder);

            foreach (KeyValuePair<uint, TakenMessage> taking in _taking)
            {
                TakenMessageState message = new TakenMessageState(taking.Key, taking.Value.Count);
                message.Parts.AddRange(taking.Value.Parts(taking.Key));
                state.Taking.Add(message);
            }

            foreach (List<AcFragment> parts in _held.Values)
                state.Held.AddRange(parts);

            state.LetGo.AddRange(_letGo);
            return state;
        }

        /// <summary>
        /// Takes up the numbering another rewriter left, so that a session it was rewriting goes on
        /// through this one with every number where the far end expects it. Before the relay
        /// starts; whatever this rewriter had is replaced, and nothing queued is touched.
        /// </summary>
        /// <remarks>
        /// A session that turns out not to be the same one - the client logging in afresh - starts
        /// the numbering over at its handshake, exactly as it would have here.
        /// </remarks>
        public void RestoreState(RewriterState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            _offset = state.Offset;
            _renumbered = state.Renumbered;
            _lastEmittedSequence = state.LastEmittedSequence;
            _lastOriginalSequence = state.LastOriginalSequence;

            _shifts.Clear();
            _shifts.AddRange(state.Shifts);

            _withheld.Clear();
            _withheldOrder.Clear();
            foreach (uint sequence in state.Withheld)
            {
                if (_withheld.Add(sequence))
                    _withheldOrder.Enqueue(sequence);
            }

            _taking.Clear();
            foreach (TakenMessageState taking in state.Taking)
            {
                TakenMessage message = new TakenMessage(taking.Count);
                foreach (AcFragment part in taking.Parts)
                    message.Add(part);
                _taking[taking.Sequence] = message;
            }

            _held.Clear();
            foreach (AcFragment fragment in state.Held)
            {
                if (!_held.TryGetValue(fragment.Header.Sequence, out List<AcFragment> parts))
                    _held[fragment.Header.Sequence] = parts = new List<AcFragment>(1);
                parts.Add(fragment);
            }

            _letGo.Clear();
            foreach (AcFragment fragment in state.LetGo)
                _letGo.Enqueue(fragment);

            InjectedMessages = state.InjectedMessages;
            WithheldMessages = state.WithheldMessages;
            WithheldSplitMessages = state.WithheldSplitMessages;
            AbandonedMessages = state.AbandonedMessages;
            HeldFragments = state.HeldFragments;
            SessionResets = state.SessionResets;
            UnrewritablePackets = state.UnrewritablePackets;
        }

        /// <summary>
        /// Returns the datagram to forward in place of <paramref name="datagram"/>:
        /// the original when nothing needs changing, otherwise a rewritten copy.
        /// </summary>
        /// <remarks>
        /// Anything that cannot be parsed, or whose key cannot be recovered, is passed
        /// through untouched. Relaying the session correctly always outranks getting a
        /// message of ours out.
        /// </remarks>
        public byte[] Rewrite(byte[] datagram)
        {
            if (datagram == null)
                throw new ArgumentNullException(nameof(datagram));

            if (!AcPacket.TryParse(datagram, out AcPacket packet))
                return datagram;

            NoteSessionBoundary(packet);

            bool wantsToInject = PendingMessages > 0;

            // What becomes of each of the client's fragments: worked out before anything is
            // changed, because a packet that cannot be re-stamped goes through as it is, and then
            // nothing in it was taken out or held back after all.
            FragmentPlan plan = Plan(packet);

            // Nothing queued, nothing to take out, hold back or let go, and nothing renumbered
            // yet: the common case before any action has ever been sent, and it must cost nothing
            // beyond tracking.
            if (!wantsToInject && plan == null && _letGo.Count == 0 && !_renumbered)
            {
                foreach (AcFragment fragment in packet.Fragments)
                    NoteEmitted(fragment.Header.Sequence, fragment.Header.Sequence);

                return datagram;
            }

            if (!packet.TryRecoverEncryptionKey(out uint key))
            {
                UnrewritablePackets++;
                return datagram;
            }

            List<AcFragment> fragments = new List<AcFragment>(packet.Fragments.Count + 1);
            bool moved = false;
            bool tookOut = false;

            // Room for held fragments let go to ride where they stood in the stream, counted as
            // though every fragment here went on.
            int spare = MaxPacketSize - PacketHeader.Size - packet.Optional.Length;
            foreach (AcFragment fragment in packet.Fragments)
                spare -= FragmentHeader.Size + fragment.Payload.Length;

            for (int i = 0; i < packet.Fragments.Count; i++)
            {
                AcFragment fragment = packet.Fragments[i];

                if (plan?.LetGoBefore[i] != null)
                {
                    foreach (uint sequence in plan.LetGoBefore[i])
                        LetGo(sequence, fragments, ref spare);
                }

                switch (plan?.Dispositions[i] ?? Disposition.Forward)
                {
                    case Disposition.TakeOut:
                        uint taken = fragment.Header.Sequence;
                        if (plan.Decided.TryGetValue(taken, out ushort count) && !_withheld.Contains(taken))
                            BeginTakingOut(taken, count);

                        TakeOut(fragment);
                        tookOut = true;
                        continue;

                    case Disposition.Hold:
                        Hold(fragment);
                        continue;
                }

                FragmentHeader header = fragment.Header;
                uint assigned = AssignSequence(fragment.Header.Sequence);
                moved |= assigned != fragment.Header.Sequence;
                header.Sequence = assigned;
                NoteEmitted(fragment.Header.Sequence, assigned);
                fragments.Add(new AcFragment(header, fragment.Payload));

                // The first fragment of a message held back for it, which is staying: what was
                // held follows it.
                if (plan != null && plan.LetGoAfter[i])
                    LetGo(fragment.Header.Sequence, fragments, ref spare);
            }

            int used = PacketHeader.Size + packet.Optional.Length;
            foreach (AcFragment fragment in fragments)
                used += FragmentHeader.Size + fragment.Payload.Length;

            bool gaveBack = TakeLetGo(fragments, ref used);
            bool injected = TakePending(fragments, ref used);
            _renumbered |= injected || tookOut;

            if (!injected && !moved && !gaveBack && plan == null)
                return datagram;

            PacketHeader rewritten = packet.Header;

            // A packet that carried no fragments does not advertise any; it does once
            // one of ours is aboard - and stops, if every one it had was taken out.
            if (fragments.Count > 0)
                rewritten.Flags |= PacketHeaderFlags.BlobFragments;
            else
                rewritten.Flags &= ~PacketHeaderFlags.BlobFragments;

            ReadOnlySpan<byte> optional = datagram.AsSpan(PacketHeader.Size, packet.Optional.Length);

            return PacketWriter.BuildEncrypted(rewritten, optional, fragments, key);
        }

        /// <summary>
        /// Restarts the numbering when this packet shows a new session beginning.
        /// </summary>
        private void NoteSessionBoundary(AcPacket packet)
        {
            if ((packet.Header.Flags & HandshakeFlags) != 0)
            {
                if (_lastOriginalSequence != 0 || _shifts.Count != 0 || _held.Count != 0)
                    ResetSession();

                return;
            }

            // A reconnect restarts the client's numbering from the beginning. Without
            // this, an injected fragment inherits the dead session's high-water mark
            // and lands thousands of slots ahead of where the server is waiting.
            if (!ArrivesInOrder)
                return;

            foreach (AcFragment fragment in packet.Fragments)
            {
                uint sequence = fragment.Header.Sequence;

                if (_lastOriginalSequence > sequence && _lastOriginalSequence - sequence >= RestartBackwardsJump)
                {
                    ResetSession();
                    return;
                }
            }
        }

        /// <summary>
        /// Moves queued messages onto this packet while they fit. Returns whether any
        /// were taken.
        /// </summary>
        /// <remarks>
        /// Ours go last so that every client fragment in the packet has already been
        /// assigned its number, which is what lets the injected one take the next.
        /// </remarks>
        private bool TakePending(List<AcFragment> fragments, ref int used)
        {
            bool injected = false;

            while (true)
            {
                PendingMessage message;

                lock (_queueGate)
                {
                    if (_pending.Count == 0)
                        return injected;

                    message = _pending.Peek();
                }

                uint sequence = _lastEmittedSequence + 1;

                IReadOnlyList<AcFragment> built = PacketWriter.Fragment(
                    message.Opcode,
                    message.Payload.Span,
                    sequence);

                // Only single-fragment messages ride along. A split message would need
                // consecutive numbers across packets that the client might interleave
                // with its own, and every action worth sending fits in one fragment.
                if (built.Count != 1)
                {
                    lock (_queueGate)
                        _pending.Dequeue();

                    continue;
                }

                int cost = FragmentHeader.Size + built[0].Payload.Length;

                if (used + cost > MaxPacketSize)
                    return injected;

                lock (_queueGate)
                {
                    if (_pending.Count == 0 || !ReferenceEquals(_pending.Peek(), message))
                        continue;

                    _pending.Dequeue();
                }

                fragments.Add(built[0]);
                used += cost;
                Shift(_lastOriginalSequence, +1);
                _lastEmittedSequence = sequence;
                InjectedMessages++;
                injected = true;
            }
        }

        /// <summary>
        /// Moves held fragments that have been let go onto this packet while they fit, ahead of
        /// anything of ours. Returns whether any were taken.
        /// </summary>
        private bool TakeLetGo(List<AcFragment> fragments, ref int used)
        {
            bool took = false;

            while (_letGo.Count > 0)
            {
                int cost = FragmentHeader.Size + _letGo.Peek().Payload.Length;
                if (used + cost > MaxPacketSize)
                    break;

                fragments.Add(_letGo.Dequeue());
                used += cost;
                took = true;
            }

            return took;
        }

        /// <summary>
        /// What is to become of each of a packet's fragments, or null when every one simply goes
        /// on. Changes nothing - <see cref="Rewrite"/> carries it out once the packet can be
        /// re-stamped - so it follows the stream through the packet on copies of its own: how far
        /// the client's numbering has gone, which messages are held back, which are being taken out.
        /// </summary>
        private FragmentPlan Plan(AcPacket packet)
        {
            Func<uint, ReadOnlyMemory<byte>, bool> withhold = Withhold;
            int count = packet.Fragments.Count;
            if (count == 0 || (withhold == null && _withheld.Count == 0 && _held.Count == 0))
                return null;

            FragmentPlan plan = null;
            uint last = _lastOriginalSequence;
            SortedSet<uint> held = _held.Count > 0 ? new SortedSet<uint>(_held.Keys) : null;

            for (int i = 0; i < count; i++)
            {
                AcFragment fragment = packet.Fragments[i];
                uint sequence = fragment.Header.Sequence;

                // The stream moving past a held message ends its wait. A later message is about to
                // be numbered, and taking the held one out afterwards would make that number wrong,
                // so it goes now, ahead of what passed it, and is never taken out.
                if (held != null && held.Count > 0 && held.Min < sequence)
                {
                    List<uint> letGo = new List<uint>();
                    while (held.Count > 0 && held.Min < sequence)
                    {
                        uint going = held.Min;
                        held.Remove(going);
                        letGo.Add(going);
                        last = Math.Max(last, going);
                    }

                    (plan ??= new FragmentPlan(count)).LetGoBefore[i] = letGo;
                }

                if (_withheld.Contains(sequence) || (plan != null && plan.Decided.ContainsKey(sequence)))
                {
                    (plan ??= new FragmentPlan(count)).Dispositions[i] = Disposition.TakeOut;
                    last = Math.Max(last, sequence);
                    continue;
                }

                // Only a message never seen can be taken out. One any part of which went through
                // before - the test said no then, or was not set yet, or the stream moved past it -
                // is on its way already, and taking the rest out would move every later number onto
                // one the far end has.
                bool isHeld = held != null && held.Contains(sequence);
                if (isHeld || (withhold != null && sequence > last))
                {
                    if (fragment.Header.Index == 0)
                    {
                        if (Offer(withhold, fragment))
                        {
                            plan ??= new FragmentPlan(count);
                            plan.Decided[sequence] = fragment.Header.Count;
                            plan.Dispositions[i] = Disposition.TakeOut;
                        }
                        else if (isHeld)
                        {
                            (plan ??= new FragmentPlan(count)).LetGoAfter[i] = true;
                        }

                        held?.Remove(sequence);
                        last = Math.Max(last, sequence);
                        continue;
                    }

                    // A later fragment come first: held back for the first, unless too many are
                    // waiting already, when this message simply goes on.
                    if (isHeld || held == null || held.Count < MessagesInHand)
                    {
                        (plan ??= new FragmentPlan(count)).Dispositions[i] = Disposition.Hold;
                        (held ??= new SortedSet<uint>()).Add(sequence);
                        continue;
                    }
                }

                last = Math.Max(last, sequence);
            }

            return plan;
        }

        /// <summary>
        /// Asks <see cref="Withhold"/> about a message by its first fragment.
        /// </summary>
        private static bool Offer(Func<uint, ReadOnlyMemory<byte>, bool> withhold, AcFragment first)
        {
            if (withhold == null || first.Payload.Length < AcMessage.MinimumSize)
                return false;

            uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(first.Payload.Span);
            try
            {
                return withhold(opcode, first.Payload.Slice(AcMessage.MinimumSize));
            }
            catch (Exception)
            {
                // A predicate that fails keeps the message: relaying the stream as it was
                // outranks keeping anything from either end.
                return false;
            }
        }

        /// <summary>
        /// Starts taking a message out: every later client message moves down one, and its
        /// fragments - any held back for it among them - are gathered to be handed on once all
        /// have arrived.
        /// </summary>
        private void BeginTakingOut(uint sequence, ushort count)
        {
            _withheld.Add(sequence);
            _withheldOrder.Enqueue(sequence);
            while (_withheldOrder.Count > RememberedWithheld)
                _withheld.Remove(_withheldOrder.Dequeue());

            Shift(sequence, -1);
            WithheldMessages++;
            if (count > 1)
                WithheldSplitMessages++;

            // One that never completes - a fragment lost for good - must not be kept for ever.
            // The oldest goes unannounced; the rest of it is still taken out as it comes.
            if (_taking.Count >= MessagesInHand)
            {
                uint oldest = uint.MaxValue;
                foreach (uint waiting in _taking.Keys)
                    oldest = Math.Min(oldest, waiting);

                _taking.Remove(oldest);
                AbandonedMessages++;
            }

            TakenMessage message = new TakenMessage(count);
            _taking[sequence] = message;

            if (_held.TryGetValue(sequence, out List<AcFragment> parts))
            {
                _held.Remove(sequence);
                foreach (AcFragment part in parts)
                    message.Add(part);
            }
        }

        /// <summary>
        /// Takes one fragment of a message being taken out of the stream, and hands the message
        /// on once its last fragment is in - the first time only, since a retransmission is the
        /// same message again.
        /// </summary>
        private void TakeOut(AcFragment fragment)
        {
            uint sequence = fragment.Header.Sequence;
            if (sequence > _lastOriginalSequence)
                _lastOriginalSequence = sequence;

            // Nothing gathering it: a resent fragment of a message already handed on, or of one
            // abandoned. Out it goes all the same.
            if (!_taking.TryGetValue(sequence, out TakenMessage message) || !message.Add(fragment) || !message.IsComplete)
                return;

            _taking.Remove(sequence);
            byte[] body = message.Assemble();
            if (body.Length < AcMessage.MinimumSize)
                return;

            uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
            try
            {
                Withheld?.Invoke(opcode, body.AsMemory(AcMessage.MinimumSize));
            }
            catch (Exception)
            {
                // Whoever wanted it can fail; the stream has been rewritten either way.
            }
        }

        /// <summary>Keeps a later fragment of a message back until its first arrives.</summary>
        private void Hold(AcFragment fragment)
        {
            uint sequence = fragment.Header.Sequence;
            if (!_held.TryGetValue(sequence, out List<AcFragment> parts))
            {
                parts = new List<AcFragment>(1);
                _held[sequence] = parts;
            }

            // A resent fragment already held is the same bytes again.
            foreach (AcFragment part in parts)
            {
                if (part.Header.Index == fragment.Header.Index)
                    return;
            }

            parts.Add(fragment);
            HeldFragments++;
        }

        /// <summary>
        /// Puts a message's held fragments back into the stream, numbered as the message is: it
        /// is not being taken out. They ride in this packet, where they stand, if there is room
        /// and nothing let go earlier is still waiting; otherwise they wait their turn for the next.
        /// </summary>
        private void LetGo(uint sequence, List<AcFragment> fragments, ref int spare)
        {
            if (!_held.TryGetValue(sequence, out List<AcFragment> parts))
                return;

            _held.Remove(sequence);
            uint assigned = AssignSequence(sequence);
            NoteEmitted(sequence, assigned);

            foreach (AcFragment part in parts)
            {
                FragmentHeader header = part.Header;
                header.Sequence = assigned;
                AcFragment numbered = new AcFragment(header, part.Payload);

                int cost = FragmentHeader.Size + part.Payload.Length;
                if (_letGo.Count == 0 && cost <= spare)
                {
                    fragments.Add(numbered);
                    spare -= cost;
                }
                else
                {
                    _letGo.Enqueue(numbered);
                }
            }
        }

        /// <summary>
        /// The number a client fragment goes on with: its own, moved by every change made to the
        /// numbering below it.
        /// </summary>
        /// <remarks>
        /// Every change made since a fragment was first seen is anchored at or past it, so a
        /// retransmitted fragment gets the number it was first given - as it must, or the server
        /// sees a gap it will wait forever to fill. And a fragment arriving after others numbered
        /// past it gets the number its place in the run gives it: not the latest offset, which
        /// would land it on a number already used. That is common from ACE, which sends a big
        /// bundle's small messages as they fit, ahead of the large ones numbered before them.
        /// </remarks>
        private uint AssignSequence(uint originalSequence)
        {
            int offset = _offset;
            for (int i = _shifts.Count - 1; i >= 0 && _shifts[i].Anchor >= originalSequence; i--)
                offset -= _shifts[i].Delta;

            return unchecked((uint)(originalSequence + offset));
        }

        /// <summary>
        /// Changes the numbering of every client fragment past <paramref name="anchor"/>: +1 past
        /// where a message of ours went in, -1 past a message taken out.
        /// </summary>
        private void Shift(uint anchor, int delta)
        {
            _shifts.Add((anchor, delta));
            _offset += delta;

            // Changes far behind everything that has gone on can no longer tell any fragment still
            // to come from another; what they add up to stays in the offset.
            int settled = 0;
            while (settled < _shifts.Count
                   && _shifts[settled].Anchor < _lastOriginalSequence
                   && _lastOriginalSequence - _shifts[settled].Anchor > ShiftsKeptFor)
            {
                settled++;
            }

            if (settled > 0)
                _shifts.RemoveRange(0, settled);
        }

        private void NoteEmitted(uint originalSequence, uint emittedSequence)
        {
            if (emittedSequence > _lastEmittedSequence)
                _lastEmittedSequence = emittedSequence;

            if (originalSequence > _lastOriginalSequence)
                _lastOriginalSequence = originalSequence;
        }

        /// <summary>What becomes of one of the client's fragments.</summary>
        private enum Disposition : byte
        {
            Forward,
            TakeOut,
            Hold,
        }

        /// <summary>What a packet's fragments are to have done with them, when not all simply go on.</summary>
        private sealed class FragmentPlan
        {
            internal FragmentPlan(int count)
            {
                Dispositions = new Disposition[count];
                LetGoBefore = new List<uint>[count];
                LetGoAfter = new bool[count];
            }

            internal Disposition[] Dispositions { get; }

            /// <summary>Held messages to put back into the stream ahead of each fragment.</summary>
            internal List<uint>[] LetGoBefore { get; }

            /// <summary>Whether a fragment is the first of a held message that is staying.</summary>
            internal bool[] LetGoAfter { get; }

            /// <summary>Messages this packet begins taking out, with how many fragments each has.</summary>
            internal Dictionary<uint, ushort> Decided { get; } = new Dictionary<uint, ushort>();
        }

        /// <summary>The fragments of a message being taken out, gathered until all are in.</summary>
        private sealed class TakenMessage
        {
            private readonly ReadOnlyMemory<byte>[] _parts;

            // Presence is kept apart from the parts: a fragment may carry nothing, and an empty
            // part cannot be told from a missing one.
            private readonly bool[] _present;

            private int _held;

            internal TakenMessage(ushort count)
            {
                _parts = new ReadOnlyMemory<byte>[count];
                _present = new bool[count];
            }

            internal bool IsComplete => _held == _parts.Length;

            internal ushort Count => (ushort)_parts.Length;

            /// <summary>The fragments in hand, as fragments of the message numbered <paramref name="sequence"/>.</summary>
            internal IEnumerable<AcFragment> Parts(uint sequence)
            {
                for (int i = 0; i < _parts.Length; i++)
                {
                    if (!_present[i])
                        continue;

                    FragmentHeader header = new FragmentHeader
                    {
                        Sequence = sequence,
                        Count = (ushort)_parts.Length,
                        Index = (ushort)i,
                        TotalSize = (ushort)(FragmentHeader.Size + _parts[i].Length),
                    };

                    yield return new AcFragment(header, _parts[i]);
                }
            }

            /// <summary>Returns false for a fragment already in hand, or one that does not fit.</summary>
            internal bool Add(AcFragment fragment)
            {
                int index = fragment.Header.Index;
                if (fragment.Header.Count != _parts.Length || index >= _parts.Length || _present[index])
                    return false;

                _parts[index] = fragment.Payload;
                _present[index] = true;
                _held++;
                return true;
            }

            internal byte[] Assemble()
            {
                int total = 0;
                foreach (ReadOnlyMemory<byte> part in _parts)
                    total += part.Length;

                byte[] body = new byte[total];
                int offset = 0;
                foreach (ReadOnlyMemory<byte> part in _parts)
                {
                    part.Span.CopyTo(body.AsSpan(offset));
                    offset += part.Length;
                }

                return body;
            }
        }

        private sealed class PendingMessage
        {
            internal PendingMessage(uint opcode, ReadOnlyMemory<byte> payload)
            {
                Opcode = opcode;
                Payload = payload;
            }

            internal uint Opcode { get; }

            internal ReadOnlyMemory<byte> Payload { get; }
        }
    }
}
