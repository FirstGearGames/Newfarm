using System;
using System.Net;

namespace Newfarm.Client
{
    /// <summary>
    /// Configuration for a <see cref="NewfarmClient"/> instance.
    /// </summary>
    public sealed class NewfarmClientConfig
    {
        // ReSharper disable FieldCanBeMadeReadOnly.Global

        /// <summary>
        /// The newfarm directory's IP address or host name.
        /// </summary>
        /// <remarks>A host name is looked up through DNS each time a session is opened or joined. An IP address is never looked up.</remarks>
        public string ServerAddress { get; }

        /// <summary>
        /// The port the newfarm directory listens on.
        /// </summary>
        public ushort ServerPort { get; }

        /// <summary>
        /// How often the client tells newfarm it is still hosting.
        /// </summary>
        /// <remarks>
        /// Has to stay comfortably under the directory's own host timeout, <c>HostTimeoutMilliseconds</c> in its
        /// server configuration, because a missed heartbeat is what makes newfarm start electing a replacement.
        /// </remarks>
        public uint HostHeartbeatIntervalMilliseconds = 1000;

        /// <summary>
        /// How often the client tells newfarm it is still waiting.
        /// </summary>
        public uint WaiterHeartbeatIntervalMilliseconds = 1000;

        /// <summary>
        /// How often an unanswered request is repeated, since newfarm speaks over plain UDP and a request can be lost.
        /// </summary>
        public uint RequestRetryIntervalMilliseconds = 500;

        /// <summary>
        /// How often this peer will tell newfarm it cannot reach the host. Calls made sooner are dropped.
        /// </summary>
        /// <remarks>
        /// Reporting is meant to be safe to call straight from a failed join, and a failed join can repeat as fast as the
        /// game retries it. This limit is per peer, and newfarm separately leaves the host alone between challenges
        /// through <c>HostChallengeCooldownMilliseconds</c> in its server configuration, so a room full of peers cannot
        /// add up to a flood either.
        /// </remarks>
        public uint UnreachableReportIntervalMilliseconds = 5000;

        /// <summary>
        /// Creates a configuration pointing at the directory at an IP address or host name.
        /// </summary>
        /// <param name="serverAddress">The newfarm directory's IP address or host name.</param>
        /// <param name="serverPort">The port the newfarm directory listens on.</param>
        public NewfarmClientConfig(string serverAddress, ushort serverPort)
        {
            ServerAddress = serverAddress ?? throw new ArgumentNullException(nameof(serverAddress));
            ServerPort = serverPort;
        }

        /// <summary>
        /// Creates a configuration pointing at the directory at an IP address and port.
        /// </summary>
        /// <param name="serverEndPoint">The newfarm directory to talk to.</param>
        public NewfarmClientConfig(IPEndPoint serverEndPoint) : this(serverEndPoint.Address.ToString(), (ushort)serverEndPoint.Port) { }
    }
}
