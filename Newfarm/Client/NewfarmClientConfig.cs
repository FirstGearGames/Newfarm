using System.Net;

namespace Newfarm.Client;

/// <summary>
/// Configuration for a <see cref="NewfarmClient"/> instance.
/// </summary>
public sealed class NewfarmClientConfig
{
    // ReSharper disable FieldCanBeMadeReadOnly.Global

    /// <summary>
    /// The newfarm directory to talk to.
    /// </summary>
    public IPEndPoint ServerEndPoint { get; }

    /// <summary>
    /// How often the client tells newfarm it is still hosting.
    /// </summary>
    /// <remarks>
    /// Has to stay comfortably under the server's
    /// <see cref="Newfarm.Server.NewfarmServerConfig.HostTimeoutMilliseconds"/>, because a missed heartbeat is what
    /// makes newfarm start electing a replacement.
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
    /// Creates a configuration pointing at the supplied directory.
    /// </summary>
    /// <param name="serverEndPoint">The newfarm directory to talk to.</param>
    public NewfarmClientConfig(IPEndPoint serverEndPoint)
    {
        ServerEndPoint = serverEndPoint;
    }
}
