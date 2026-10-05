using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AC.Host.Transport;
using AC.Protocol;
using AC.Proxy;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// The answer to an appraisal only the host asked for is kept from the client, whose examine
    /// panel would open for it; one the player asked for goes through.
    /// </summary>
    public class AppraisalRequestsTests
    {
        private DateTime _now = new DateTime(2026, 10, 4, 8, 0, 0, DateTimeKind.Utc);

        private AppraisalRequests New() => new AppraisalRequests(() => _now);

        /// <summary>The server's IdentifyObjectResponse game event: recipient, sequence, event, object.</summary>
        private static ReadOnlyMemory<byte> Answer(uint objectId)
        {
            byte[] payload = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), 0x50000001);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 7);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 0x00C9);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), objectId);
            return payload;
        }

        /// <summary>The client's IdentifyObject game action: sequence, action, object.</summary>
        private static byte[] Request(uint objectId)
        {
            byte[] payload = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x00C8);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), objectId);
            return payload;
        }

        [Fact]
        public void TheHostsOwnAppraisalIsAnsweredToTheHostAlone()
        {
            AppraisalRequests requests = New();
            requests.HostAsked(0x80001234);

            Assert.True(requests.Withhold(0xF7B0, Answer(0x80001234)));
            Assert.Equal(1, requests.Withheld);

            // Its one answer used up, a further one is the client's.
            Assert.False(requests.Withhold(0xF7B0, Answer(0x80001234)));
        }

        [Fact]
        public void ThePlayersAppraisalGoesThroughEvenWhenTheHostAskedToo()
        {
            AppraisalRequests requests = New();
            requests.HostAsked(0x80001234);
            requests.Sent(0xF7B1, Request(0x80001234));

            Assert.False(requests.Withhold(0xF7B0, Answer(0x80001234)));
            Assert.True(requests.Withhold(0xF7B0, Answer(0x80001234)));

            AppraisalRequests player = New();
            player.Sent(0xF7B1, Request(0x80005678));
            Assert.False(player.Withhold(0xF7B0, Answer(0x80005678)));
        }

        [Fact]
        public void EachOfTheHostsRequestsKeepsOneAnswer()
        {
            AppraisalRequests requests = New();
            requests.HostAsked(0x80001234);
            requests.HostAsked(0x80001234);

            Assert.True(requests.Withhold(0xF7B0, Answer(0x80001234)));
            Assert.True(requests.Withhold(0xF7B0, Answer(0x80001234)));
            Assert.False(requests.Withhold(0xF7B0, Answer(0x80001234)));
        }

        [Fact]
        public void AnAnswerTooLateOrOfAnotherKindGoesThrough()
        {
            AppraisalRequests requests = New();
            requests.HostAsked(0x80001234);
            _now += TimeSpan.FromSeconds(31);
            Assert.False(requests.Withhold(0xF7B0, Answer(0x80001234)));

            requests.HostAsked(0x80001234);
            Assert.False(requests.Withhold(0xF7B0, new byte[8]));
            Assert.False(requests.Withhold(0xF745, Answer(0x80001234)));

            byte[] otherEvent = Answer(0x80001234).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(otherEvent.AsSpan(8), 0x02C2);
            Assert.False(requests.Withhold(0xF7B0, otherEvent));
        }

        /// <summary>
        /// Worn armour with many spells is answered in more than one fragment - "Satin Pants" kept
        /// opening the examine panel while Virindi Tank's mana upkeep looked at it. The relay asks
        /// about the answer at its first fragment, which names the object, even when ACE sent the
        /// last one first; then the whole answer reaches the host.
        /// </summary>
        [Fact]
        public void AnAnswerInSeveralFragmentsIsJudgedByItsFirstAndHandedOnWhole()
        {
            AppraisalRequests requests = New();
            requests.HostAsked(0x80001234);
            ClientStreamRewriter relay = new ClientStreamRewriter { ArrivesInOrder = false, Withhold = requests.Withhold };
            byte[] handedOn = null;
            relay.Withheld += (opcode, payload) => handedOn = payload.ToArray();

            byte[] answer = new byte[600];
            Answer(0x80001234).Span.CopyTo(answer);
            for (int i = 24; i < answer.Length; i++)
                answer[i] = (byte)i;

            IReadOnlyList<AcFragment> parts = PacketWriter.Fragment(0xF7B0, answer, 40);
            byte[] Packet(uint sequence, AcFragment fragment) => PacketWriter.BuildEncrypted(
                new PacketHeader { Sequence = sequence, Flags = PacketHeaderFlags.EncryptedChecksum | PacketHeaderFlags.BlobFragments },
                ReadOnlySpan<byte>.Empty, new[] { fragment }, 0x5000 + sequence);

            Assert.True(AcPacket.TryParse(relay.Rewrite(Packet(1, parts[1])), out AcPacket first));
            Assert.True(AcPacket.TryParse(relay.Rewrite(Packet(2, parts[0])), out AcPacket second));

            Assert.Empty(first.Fragments);
            Assert.Empty(second.Fragments);
            Assert.Equal(answer, handedOn);
            Assert.Equal(1, requests.Withheld);
        }
    }
}
