namespace Newfarm.Server;

/// <summary>
/// Why newfarm accepted or refused a request from a peer.
/// </summary>
public enum NewfarmRequestResult : byte
{
    /// <summary>
    /// The request was carried out.
    /// </summary>
    Accepted,

    /// <summary>
    /// No session is held under the requested identity, either because it never existed or because it has expired.
    /// </summary>
    SessionNotFound,

    /// <summary>
    /// The session exists, but the secret presented with the request did not match the one it was opened with.
    /// </summary>
    SecretRejected,

    /// <summary>
    /// The request named an epoch that is no longer current, which is what a returning old host looks like.
    /// </summary>
    StaleEpoch,

    /// <summary>
    /// The peer is not the one newfarm elected, so it has no standing to publish a credential or decline.
    /// </summary>
    NotElected,
}
