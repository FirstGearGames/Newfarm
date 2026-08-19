using System.Globalization;

namespace Newfarm.Client;

/// <summary>
/// The identity of a session, which a host receives from newfarm and is then responsible for distributing to its
/// clients.
/// </summary>
/// <remarks>
/// One value, drawn from a cryptographic source, and it is a bearer token: whoever holds it can be elected to host the
/// session and can say where the session has moved to. It has to reach every client for the session to survive its
/// host, so it travels as far as the players do and no further. Do not log it, and do not put it anywhere a player
/// would not have been given it anyway.
/// </remarks>
public readonly struct NewfarmSessionIdentity
{
    /// <summary>
    /// Names the session.
    /// </summary>
    public readonly ulong SessionId;

    /// <summary>
    /// The epoch in force when this identity was issued or last refreshed, which increases on every handover.
    /// </summary>
    public readonly uint Epoch;

    /// <summary>
    /// Creates an identity.
    /// </summary>
    /// <param name="sessionId">Names the session.</param>
    /// <param name="epoch">The epoch in force.</param>
    public NewfarmSessionIdentity(ulong sessionId, uint epoch)
    {
        SessionId = sessionId;
        Epoch = epoch;
    }

    /// <summary>
    /// Renders the id as the text a host distributes to its clients.
    /// </summary>
    /// <returns>Sixteen hexadecimal characters.</returns>
    public string ToText() => SessionId.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>
    /// Rebuilds an identity from the text a client was given by its host.
    /// </summary>
    /// <param name="sessionIdText">The session id as hexadecimal characters.</param>
    /// <param name="identity">When this returns <see langword="true"/>, the identity that was parsed.</param>
    /// <returns><see langword="true"/> when the value parsed.</returns>
    public static bool TryParse(string sessionIdText, out NewfarmSessionIdentity identity)
    {
        if (!ulong.TryParse(sessionIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong sessionId))
        {
            identity = default;

            return false;
        }

        identity = new NewfarmSessionIdentity(sessionId, epoch: 0);

        return true;
    }
}
