namespace Newfarm.Client;

/// <summary>
/// Where a session currently lives, as published by whichever peer is hosting it.
/// </summary>
/// <remarks>
/// Newfarm never interprets <see cref="Credential"/>. It is whatever the relay in use needs to be joined with: a room
/// code, an allocation id, a host and port. Text rather than bytes so any of those fit without newfarm being taught
/// about them, and capped at <see cref="Wire.NewfarmWireFormat.MaximumTextLength"/> characters.
/// <see cref="AdapterTag"/> is what tells a peer whether it is able to use what it has been handed.
/// </remarks>
public readonly struct NewfarmCredential
{
    /// <summary>
    /// Names the service the credential belongs to.
    /// </summary>
    public readonly string AdapterTag;

    /// <summary>
    /// What a peer needs in order to reach the room.
    /// </summary>
    public readonly string Credential;

    /// <summary>
    /// The epoch the credential was published for, which increases on every handover.
    /// </summary>
    public readonly uint Epoch;

    /// <summary>
    /// Creates a credential.
    /// </summary>
    /// <param name="adapterTag">Names the service the credential belongs to.</param>
    /// <param name="credential">What a peer needs in order to reach the room.</param>
    /// <param name="epoch">The epoch the credential was published for.</param>
    public NewfarmCredential(string adapterTag, string credential, uint epoch)
    {
        AdapterTag = adapterTag;
        Credential = credential;
        Epoch = epoch;
    }
}
