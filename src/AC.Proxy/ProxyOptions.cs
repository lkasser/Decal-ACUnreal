using System;
using System.Net;

namespace AC.Proxy
{
    /// <summary>
    /// Where the proxy listens and where it forwards to.
    /// </summary>
    public sealed class ProxyOptions
    {
        /// <summary>
        /// How many consecutive ports to relay, starting at <see cref="ListenPort"/>
        /// and <see cref="ServerPort"/>.
        /// </summary>
        /// <remarks>
        /// Two by default, because ACE binds its configured port and that port plus
        /// one: the first carries authentication, the second the world session. A proxy
        /// on one port only would lose the game the moment the client logged in.
        /// </remarks>
        public const int DefaultPortCount = 2;

        /// <summary>Address to listen on. Loopback keeps the relay off the network.</summary>
        public IPAddress ListenAddress { get; set; } = IPAddress.Loopback;

        /// <summary>
        /// First local port. This is the port to configure as the server port in the
        /// client, with <see cref="ListenAddress"/> as the host. Defaults to 9100 rather
        /// than ACE's 9000 so that a server on this same machine does not collide.
        /// </summary>
        public int ListenPort { get; set; } = 9100;

        /// <summary>Host name or address of the real server.</summary>
        public string ServerHost { get; set; }

        /// <summary>First port on the real server. ACE's default is 9000.</summary>
        public int ServerPort { get; set; } = 9000;

        public int PortCount { get; set; } = DefaultPortCount;

        /// <summary>
        /// True when the local and upstream port ranges overlap. Combined with the
        /// two sides resolving to the same address, that is a self-loop: the relay
        /// forwards to a port it is itself listening on.
        /// </summary>
        public bool PortRangesOverlap
            => Math.Abs(ListenPort - ServerPort) < PortCount;

        /// <summary>
        /// Path to write a capture to, or null for none. A capture is what makes the
        /// decoders testable against real traffic instead of only synthetic packets.
        /// </summary>
        public string CapturePath { get; set; }

        /// <summary>
        /// Throws if these options could not work, naming the specific problem.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ServerHost))
                throw new InvalidOperationException("ServerHost is required.");

            if (PortCount < 1)
                throw new InvalidOperationException("PortCount must be at least 1.");

            ValidatePortRange(ListenPort, nameof(ListenPort));
            ValidatePortRange(ServerPort, nameof(ServerPort));
        }

        private void ValidatePortRange(int first, string name)
        {
            if (first < 1 || first > ushort.MaxValue)
                throw new InvalidOperationException($"{name} {first} is not a valid port.");

            int last = first + PortCount - 1;

            if (last > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"{name} {first} with PortCount {PortCount} runs past port {ushort.MaxValue}.");
            }
        }
    }
}
