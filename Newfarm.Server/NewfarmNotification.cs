using System.Net;
using Newfarm.Wire;

namespace Newfarm.Server;

/// <summary>
/// One message the registry has decided should be sent, which <see cref="NewfarmServer"/> then puts on the wire.
/// </summary>
/// <remarks>
/// The registry reports what to send rather than sending it, so every decision it makes is observable in a test
/// without a socket.
/// </remarks>
internal readonly struct NewfarmNotification
{
    /// <summary>
    /// The message to send.
    /// </summary>
    public readonly NewfarmPacketType PacketType;

    /// <summary>
    /// The peer to send it to.
    /// </summary>
    public readonly IPEndPoint EndPoint;

    /// <summary>
    /// The session the message concerns, which carries the epoch and any credential the message needs.
    /// </summary>
    public readonly NewfarmSession Session;

    /// <summary>
    /// Creates a notification.
    /// </summary>
    /// <param name="packetType">The message to send.</param>
    /// <param name="endPoint">The peer to send it to.</param>
    /// <param name="session">The session the message concerns.</param>
    public NewfarmNotification(NewfarmPacketType packetType, IPEndPoint endPoint, NewfarmSession session)
    {
        PacketType = packetType;
        EndPoint = endPoint;
        Session = session;
    }
}
