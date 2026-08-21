namespace Newfarm.Wire
{
    /// <summary>
    /// The leading byte of every newfarm datagram, identifying what the rest of the payload contains.
    /// </summary>
    /// <remarks>
    /// Values start at <c>0xA0</c> so a newfarm datagram can never be mistaken for a SynapseSocket packet type or a
    /// SynapseBeacon one, which leaves room for newfarm to ride an engine socket later without a wire change.
    /// </remarks>
    public enum NewfarmPacketType : byte
    {
        /// <summary>
        /// A peer asks newfarm to open a session for it. Carries no payload, because newfarm has no interest in who is
        /// asking.
        /// </summary>
        CreateSession = 0xA0,

        /// <summary>
        /// The hosting peer reports that it is still hosting, which is what stops newfarm electing a replacement.
        /// </summary>
        HostHeartbeat = 0xA1,

        /// <summary>
        /// The hosting peer reports the credential its room can be joined with, for newfarm to hand to every waiter.
        /// </summary>
        PublishCredential = 0xA2,

        /// <summary>
        /// An elected peer reports that it cannot host after all, so newfarm elects the next waiter without waiting for
        /// the election deadline to pass.
        /// </summary>
        DeclineElection = 0xA3,

        /// <summary>
        /// The hosting peer ends the session deliberately.
        /// </summary>
        CloseSession = 0xA4,

        /// <summary>
        /// The hosting peer gives the session up while staying in it, which hands the session on at once instead of
        /// waiting for its heartbeat to lapse.
        /// </summary>
        /// <remarks>
        /// This is the graceful half of the answer to a host that is online but no longer hosting: the peer that left
        /// the match, or shut its server down, says so rather than leaving everyone to infer it.
        /// </remarks>
        SurrenderHosting = 0xA7,

        /// <summary>
        /// A peer reports that the credential it holds does not get it to the host, which is what makes newfarm ask the
        /// host to prove it is still hosting.
        /// </summary>
        /// <remarks>
        /// The ungraceful half: peers are the only ones who know whether the host is actually serving them, so this is
        /// how a host that heartbeats but cannot host is found out.
        /// </remarks>
        CredentialUnreachable = 0xA8,

        /// <summary>
        /// A peer that has lost its host joins the waiting set for a session, to be elected or to be handed the
        /// credential of whoever is.
        /// </summary>
        AwaitSession = 0xA5,

        /// <summary>
        /// A waiting peer reports that it is still there, which is what stops newfarm electing a peer that has gone.
        /// </summary>
        WaiterHeartbeat = 0xA6,

        /// <summary>
        /// Newfarm returns the identity of a newly opened session, which the host is then responsible for distributing.
        /// </summary>
        SessionCreated = 0xB0,

        /// <summary>
        /// Newfarm tells a waiting peer to host the session, and by when it has to report a credential.
        /// </summary>
        ElectHost = 0xB1,

        /// <summary>
        /// Newfarm withdraws an election, because the elected peer ran out of time, declined, or stopped answering.
        /// </summary>
        AbortElection = 0xB2,

        /// <summary>
        /// Newfarm hands a waiting peer the credential the session now lives at.
        /// </summary>
        CredentialAvailable = 0xB3,

        /// <summary>
        /// Newfarm tells a waiting peer that it is still queued, so silence from the directory is distinguishable from
        /// the directory being gone.
        /// </summary>
        Waiting = 0xB4,

        /// <summary>
        /// Newfarm refused a request, carrying a <see cref="NewfarmRefusalReason"/> saying why.
        /// </summary>
        Refused = 0xB5,

        /// <summary>
        /// Newfarm asks a host that peers cannot reach to publish a credential, which is the only proof of hosting it
        /// can ask for, and stands the host down if it cannot give one.
        /// </summary>
        ProveHosting = 0xB9,

        /// <summary>
        /// Newfarm tells a peer it is no longer the host, having stood it down for not answering its challenges.
        /// </summary>
        /// <remarks>
        /// Without being told, a stood-down host would go on believing it holds a session that has moved out from under
        /// it, heartbeating at a directory that stopped listening and never hearing where everyone went.
        /// </remarks>
        HostingRevoked = 0xBA,

        /// <summary>
        /// Newfarm confirms it has taken a published credential, which is what stops the publisher repeating it.
        /// </summary>
        /// <remarks>
        /// The publication is the one message in the exchange with no natural answer, and it is also the one that must
        /// not be lost: without a confirmation, a publisher whose datagram went missing would believe it is hosting while
        /// newfarm went on to elect somebody else.
        /// </remarks>
        CredentialAccepted = 0xB8,
    }
}
