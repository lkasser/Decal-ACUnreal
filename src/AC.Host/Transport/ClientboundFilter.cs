using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AC.Host.Transport
{
    /// <summary>
    /// A transport that can keep a message from the server away from the client while the host
    /// still reads it: the answer to an appraisal the host asked for, which the client never did.
    /// </summary>
    /// <remarks>
    /// Optional, as <see cref="ITypedCommandSource"/> is: the relay takes the message out of the
    /// server's packet on its own thread (<see cref="AC.Proxy.ClientStreamRewriter.Withhold"/>)
    /// and hands it to the host as though it had arrived.
    /// </remarks>
    public interface IClientboundFilter
    {
        /// <summary>
        /// Whether a message from the server, by opcode and payload, is to be kept from the client.
        /// Set by the host; called on the relay's thread, so it must be quick and must not block.
        /// For a message in several fragments it is asked at the first, and given that fragment's
        /// share of the payload - which starts with everything that names the message.
        /// </summary>
        Func<uint, ReadOnlyMemory<byte>, bool> WithholdFromClient { get; set; }

        /// <summary>Whether anything can be kept from the client at all: not when the relay only watches.</summary>
        bool CanWithholdFromClient { get; }

        /// <summary>Messages kept from the client so far.</summary>
        int MessagesWithheldFromClient { get; }

        /// <summary>Of <see cref="MessagesWithheldFromClient"/>, those that came in more than one fragment.</summary>
        int SplitMessagesWithheldFromClient { get; }
    }

    /// <summary>
    /// The appraisals in flight, by who asked: the host's own are answered to the host alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AC:Unreal opens its examine panel for every appraisal answer it is sent, asked for or
    /// not, so the appraisals plugins make - Virindi Tank's of the character's gear, of every
    /// corpse that falls - would keep throwing it up over the game. The answer to an appraisal
    /// only the host asked for is kept from the client; one the player asked for too - by
    /// selecting or examining the object - goes through as always.
    /// </para>
    /// <para>
    /// Thread-safe: the host records its requests on the game thread, the player's are seen on
    /// the relay's, and answers are judged there.
    /// </para>
    /// </remarks>
    public sealed class AppraisalRequests
    {
        /// <summary>The server's answer: game event 0x00C9, IdentifyObjectResponse.</summary>
        private const uint GameEvent = 0xF7B0, IdentifyObjectResponse = 0x00C9;

        /// <summary>The client's request: game action 0x00C8, IdentifyObject.</summary>
        private const uint GameAction = 0xF7B1, IdentifyObject = 0x00C8;

        /// <summary>How long a request is waited on: an answer later than this is the client's to see.</summary>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        // Requests not yet answered, by object: how many, and when the last was made. The host
        // asks again when no answer comes, so one object can have several in flight.
        private readonly Dictionary<uint, (int Count, DateTime At)> _host = new Dictionary<uint, (int, DateTime)>();
        private readonly Dictionary<uint, (int Count, DateTime At)> _client = new Dictionary<uint, (int, DateTime)>();
        private readonly object _gate = new object();
        private readonly Func<DateTime> _clock;

        public AppraisalRequests(Func<DateTime> clock = null)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>How many answers have been kept from the client.</summary>
        public long Withheld { get; private set; }

        /// <summary>The host asked the server to appraise an object.</summary>
        public void HostAsked(uint objectId)
        {
            lock (_gate)
                Add(_host, objectId);
        }

        /// <summary>
        /// A message the client sent, as the relay saw it: an appraisal request is noted, so its
        /// answer goes through.
        /// </summary>
        public void Sent(uint opcode, ReadOnlySpan<byte> payload)
        {
            // The sequence, the action, then the object.
            if (opcode != GameAction || payload.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)) != IdentifyObject)
                return;

            lock (_gate)
                Add(_client, BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8)));
        }

        /// <summary>
        /// Whether a message from the server is the answer to an appraisal the host alone asked
        /// for, and so to be kept from the client. The request is used up either way.
        /// </summary>
        public bool Withhold(uint opcode, ReadOnlyMemory<byte> payload)
        {
            // The recipient, the sequence, the event, then the object.
            ReadOnlySpan<byte> span = payload.Span;
            if (opcode != GameEvent || span.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8)) != IdentifyObjectResponse)
                return false;

            uint objectId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
            lock (_gate)
            {
                // The player's request is answered first: theirs is the one they are waiting on.
                if (Take(_client, objectId))
                    return false;
                if (!Take(_host, objectId))
                    return false;

                Withheld++;
                return true;
            }
        }

        private void Add(Dictionary<uint, (int Count, DateTime At)> requests, uint objectId)
        {
            DateTime now = _clock();
            int count = requests.TryGetValue(objectId, out (int Count, DateTime At) had) && now - had.At <= Patience ? had.Count : 0;
            requests[objectId] = (count + 1, now);
        }

        /// <summary>Uses up one request for the object, if one was made recently enough to be waiting.</summary>
        private bool Take(Dictionary<uint, (int Count, DateTime At)> requests, uint objectId)
        {
            if (!requests.TryGetValue(objectId, out (int Count, DateTime At) had))
                return false;

            if (_clock() - had.At > Patience || had.Count <= 1)
                requests.Remove(objectId);
            else
                requests[objectId] = (had.Count - 1, had.At);
            return _clock() - had.At <= Patience;
        }
    }
}
