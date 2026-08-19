using System;

namespace Newfarm.Client;

/// <summary>
/// The identity of a session, which a host receives from newfarm and is then responsible for distributing to its
/// clients.
/// </summary>
/// <remarks>
/// Both values are needed. The id alone names the session; the secret is what proves a peer was told about it by the
/// host rather than having guessed or overheard the id, and newfarm refuses to elect a peer or hand out a credential
/// without it.
/// </remarks>
public readonly struct NewfarmSessionIdentity
{
    /// <summary>
    /// Names the session. Safe to log.
    /// </summary>
    public readonly Guid SessionId;

    /// <summary>
    /// Proves the holder was told about the session by its host. Never log this.
    /// </summary>
    public readonly Guid SessionSecret;

    /// <summary>
    /// The epoch in force when this identity was issued or last refreshed.
    /// </summary>
    public readonly uint Epoch;

    /// <summary>
    /// Creates an identity.
    /// </summary>
    /// <param name="sessionId">Names the session.</param>
    /// <param name="sessionSecret">Proves the holder was told about the session.</param>
    /// <param name="epoch">The epoch in force.</param>
    public NewfarmSessionIdentity(Guid sessionId, Guid sessionSecret, uint epoch)
    {
        SessionId = sessionId;
        SessionSecret = sessionSecret;
        Epoch = epoch;
    }

    /// <summary>
    /// Renders the id and secret as the two text values a host distributes to its clients.
    /// </summary>
    /// <param name="sessionIdText">Receives the session id as thirty two hexadecimal characters.</param>
    /// <param name="sessionSecretText">Receives the session secret as thirty two hexadecimal characters.</param>
    public void ToText(out string sessionIdText, out string sessionSecretText)
    {
        sessionIdText = SessionId.ToString("N");
        sessionSecretText = SessionSecret.ToString("N");
    }

    /// <summary>
    /// Rebuilds an identity from the two text values a client was given by its host.
    /// </summary>
    /// <param name="sessionIdText">The session id as thirty two hexadecimal characters.</param>
    /// <param name="sessionSecretText">The session secret as thirty two hexadecimal characters.</param>
    /// <param name="identity">When this returns <see langword="true"/>, the identity that was parsed.</param>
    /// <returns><see langword="true"/> when both values parsed.</returns>
    public static bool TryParse(string sessionIdText, string sessionSecretText, out NewfarmSessionIdentity identity)
    {
        if (!Guid.TryParse(sessionIdText, out Guid sessionId) || !Guid.TryParse(sessionSecretText, out Guid sessionSecret))
        {
            identity = default;

            return false;
        }

        identity = new NewfarmSessionIdentity(sessionId, sessionSecret, epoch: 0);

        return true;
    }
}
