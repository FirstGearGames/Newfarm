namespace Newfarm.Client
{
    /// <summary>
    /// What a <see cref="NewfarmClient"/> is currently doing, which decides which heartbeat it sends.
    /// </summary>
    public enum NewfarmClientMode : byte
    {
        /// <summary>
        /// The client holds no session and sends nothing.
        /// </summary>
        Idle,

        /// <summary>
        /// The client has asked for a session and is waiting to be told its identity.
        /// </summary>
        CreatingSession,

        /// <summary>
        /// The client is hosting, and heartbeats newfarm so no replacement is elected.
        /// </summary>
        Hosting,

        /// <summary>
        /// The client has lost its host and is queued, heartbeating so it stays eligible to be elected.
        /// </summary>
        Waiting,

        /// <summary>
        /// The client has been told to host and has not yet published a credential.
        /// </summary>
        Elected,
    }
}
