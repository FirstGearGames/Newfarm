namespace Newfarm.Wire;

/// <summary>
/// Why newfarm refused a request, carried by <see cref="NewfarmPacketType.Refused"/>.
/// </summary>
/// <remarks>
/// A reason of its own rather than a message type per refusal, so a peer can tell what to do next without matching on
/// wire types, and so a new reason costs a value here rather than a value in the protocol.
/// </remarks>
public enum NewfarmRefusalReason : byte
{
    /// <summary>
    /// No session is held under the requested id, either because it never existed or because it has expired.
    /// </summary>
    SessionNotFound = 0,

    /// <summary>
    /// Newfarm is already holding as many sessions as it is configured to allow.
    /// </summary>
    ServerAtCapacity = 1,

    /// <summary>
    /// The session already has as many peers waiting on it as it is configured to allow.
    /// </summary>
    SessionFull = 2,
}
