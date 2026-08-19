namespace Newfarm.Server;

/// <summary>
/// What a peer that has just joined a session's waiting set should be told.
/// </summary>
public enum NewfarmWaitOutcome : byte
{
    /// <summary>
    /// No session is held under the requested identity.
    /// </summary>
    SessionNotFound,

    /// <summary>
    /// The secret presented with the request did not match.
    /// </summary>
    SecretRejected,

    /// <summary>
    /// The session already lives somewhere the peer can reach, so it is handed the credential rather than queued.
    /// </summary>
    CredentialAvailable,

    /// <summary>
    /// The peer is queued. It will be elected if the session needs a host, or handed a credential once one exists.
    /// </summary>
    Waiting,
}
