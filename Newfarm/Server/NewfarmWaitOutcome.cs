namespace Newfarm.Server;

/// <summary>
/// What a peer that has just joined a session's waiting set should be told.
/// </summary>
public enum NewfarmWaitOutcome : byte
{
    /// <summary>
    /// The session already has as many peers waiting on it as it is configured to allow.
    /// </summary>
    SessionFull,

    /// <summary>
    /// The session already lives somewhere the peer can reach, so it is handed the credential rather than queued.
    /// </summary>
    CredentialAvailable,

    /// <summary>
    /// The peer is queued. It will be elected if the session needs a host, or handed a credential once one exists.
    /// </summary>
    Waiting,
}
