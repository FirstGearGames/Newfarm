using System;
using System.Collections.Generic;
using System.Net;

namespace Newfarm.Server;

/// <summary>
/// One session's state: who is hosting it, where it currently lives, and which peers are waiting to be told.
/// </summary>
/// <remarks>
/// Touched only from the server's single loop thread, so its collections are plain and unsynchronised.
/// </remarks>
internal sealed class NewfarmSession
{
    /// <summary>
    /// The identity the host hands to its clients, and the key this session is stored under.
    /// </summary>
    public Guid SessionId { get; }

    /// <summary>
    /// The secret a peer must present to be elected, to publish a credential, or to be told one. Without it a
    /// leaked session id would be enough to take the session over.
    /// </summary>
    public Guid SessionSecret { get; }

    /// <summary>
    /// Increments on every handover, so a peer can tell a newer credential from the one it holds and a returning old
    /// host cannot publish over the live one.
    /// </summary>
    public uint Epoch { get; private set; }

    /// <summary>
    /// The peer newfarm currently believes is hosting, or <see langword="null"/> while the session is hostless.
    /// </summary>
    public IPEndPoint? HostEndPoint { get; private set; }

    /// <summary>
    /// Timestamp of the last heartbeat from <see cref="HostEndPoint"/>, in <see cref="NewfarmClock.Milliseconds"/>
    /// milliseconds.
    /// </summary>
    public long LastHostHeartbeatMilliseconds { get; private set; }

    /// <summary>
    /// The peer currently told to host, or <see langword="null"/> when no election is outstanding.
    /// </summary>
    public IPEndPoint? ElectedEndPoint { get; private set; }

    /// <summary>
    /// When the outstanding election lapses, in <see cref="NewfarmClock.Milliseconds"/> milliseconds.
    /// </summary>
    public long ElectionDeadlineMilliseconds { get; private set; }

    /// <summary>
    /// Timestamp of the last heartbeat from <see cref="ElectedEndPoint"/>, in
    /// <see cref="NewfarmClock.Milliseconds"/> milliseconds.
    /// </summary>
    /// <remarks>
    /// Held apart from <see cref="WaiterHeartbeats"/> rather than folded into it, because an elected peer is out of
    /// the waiting set for the duration of its election and that dictionary is the record of who is in it.
    /// </remarks>
    public long LastElectedHeartbeatMilliseconds { get; private set; }

    /// <summary>
    /// When the session last became hostless, in <see cref="NewfarmClock.Milliseconds"/> milliseconds, which starts
    /// the window a session survives for with nobody hosting it.
    /// </summary>
    public long HostlessSinceMilliseconds { get; private set; }

    /// <summary>
    /// Increments every time a credential is accepted, naming the current one so a report of it being unreachable
    /// can be told apart from a report of the one before it.
    /// </summary>
    public uint AttestationId { get; private set; }

    /// <summary>
    /// The peers that have reported being unable to reach the host, each against the
    /// <see cref="AttestationId"/> that was current when it reported.
    /// </summary>
    public Dictionary<IPEndPoint, uint> UnreachableReports { get; } = [];

    /// <summary>
    /// True while the host has been asked to prove it is still hosting and the round has not yet closed.
    /// </summary>
    public bool IsHostChallengeOutstanding { get; private set; }

    /// <summary>
    /// When the outstanding challenge round closes, in <see cref="NewfarmClock.Milliseconds"/> milliseconds.
    /// </summary>
    public long HostChallengeDeadlineMilliseconds { get; private set; }

    /// <summary>
    /// When the host was last asked to prove it is hosting, in <see cref="NewfarmClock.Milliseconds"/> milliseconds,
    /// or zero when it has not been asked since it took the session on.
    /// </summary>
    public long LastHostChallengeMilliseconds { get; private set; }

    /// <summary>
    /// The <see cref="AttestationId"/> that stood when the outstanding challenge was sent, which is what answering
    /// the challenge moves on.
    /// </summary>
    public uint ChallengeAttestationId { get; private set; }

    /// <summary>
    /// How many challenge rounds have closed in a row with peers still unable to reach the host.
    /// </summary>
    public uint IneffectiveHostChallengeCount { get; private set; }

    /// <summary>
    /// The peers that have said they cannot host this session, which are passed over when a replacement is elected
    /// but still receive the credential of whoever does host it.
    /// </summary>
    public HashSet<IPEndPoint> DeclinedEndPoints { get; } = [];

    /// <summary>
    /// Returns whether any peer says it cannot reach the host as things currently stand, rather than as they stood
    /// before the newest credential was published.
    /// </summary>
    public bool HasCurrentUnreachableReports
    {
        get
        {
            foreach (KeyValuePair<IPEndPoint, uint> report in UnreachableReports)
            {
                if (report.Value == AttestationId)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Names the service <see cref="Credential"/> belongs to, so a peer can tell whether it is able to use it.
    /// </summary>
    public string AdapterTag { get; private set; } = string.Empty;

    /// <summary>
    /// The opaque bytes a peer needs to reach the room the session lives at, or <see langword="null"/> before any
    /// host has published one. Newfarm never interprets these.
    /// </summary>
    public byte[]? Credential { get; private set; }

    /// <summary>
    /// When <see cref="Credential"/> was published, in <see cref="NewfarmClock.Milliseconds"/> milliseconds, which
    /// starts the grace period a hostless session survives for.
    /// </summary>
    public long CredentialPublishedMilliseconds { get; private set; }

    /// <summary>
    /// True once a credential has been published for the current epoch.
    /// </summary>
    public bool HasCredential => Credential is not null;

    /// <summary>
    /// Peers waiting to be elected or to be handed the credential, each with the timestamp of its last heartbeat in
    /// <see cref="NewfarmClock.Milliseconds"/> milliseconds.
    /// </summary>
    /// <remarks>
    /// Insertion order decides election order, which is what makes the first peer to arrive the one that hosts, so
    /// this is a list of endpoints alongside a lookup rather than a dictionary alone.
    /// </remarks>
    public List<IPEndPoint> WaiterOrder { get; } = [];

    /// <summary>
    /// The last heartbeat seen from each waiting peer, keyed by endpoint.
    /// </summary>
    public Dictionary<IPEndPoint, long> WaiterHeartbeats { get; } = [];

    /// <summary>
    /// When each waiting peer was last told it is still queued, keyed by endpoint.
    /// </summary>
    public Dictionary<IPEndPoint, long> WaiterKeepAlives { get; } = [];

    /// <summary>
    /// Creates a session with a fresh identity.
    /// </summary>
    /// <param name="sessionId">The identity the host will distribute.</param>
    /// <param name="sessionSecret">The secret guarding the session.</param>
    /// <param name="hostEndPoint">The peer that asked for the session, which is assumed to be hosting it.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    public NewfarmSession(Guid sessionId, Guid sessionSecret, IPEndPoint hostEndPoint, long nowMilliseconds)
    {
        SessionId = sessionId;
        SessionSecret = sessionSecret;
        Epoch = 1;
        HostEndPoint = hostEndPoint;
        LastHostHeartbeatMilliseconds = nowMilliseconds;
    }

    /// <summary>
    /// Records a heartbeat from the peer newfarm believes is hosting.
    /// </summary>
    /// <param name="hostEndPoint">The peer that sent the heartbeat.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns><see langword="true"/> when the sender is the host and the heartbeat was recorded.</returns>
    /// <remarks>
    /// A heartbeat from any other peer is ignored rather than adopted. Every peer in a session holds the secret, so
    /// adopting the sender would let any of them install itself as host, which both suppresses the election that
    /// should have happened and puts two peers in charge of one session. Hosting is reached by opening the session or
    /// by publishing a credential after being elected, and by nothing else.
    /// </remarks>
    public bool TryRecordHostHeartbeat(IPEndPoint hostEndPoint, long nowMilliseconds)
    {
        if (HostEndPoint is null || !HostEndPoint.Equals(hostEndPoint))
            return false;

        LastHostHeartbeatMilliseconds = nowMilliseconds;

        return true;
    }

    /// <summary>
    /// Returns whether the hosting peer has gone quiet for longer than the configured timeout.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="hostTimeoutMilliseconds">How long a host may go unheard before it is treated as gone.</param>
    /// <returns><see langword="true"/> when the host is considered lost.</returns>
    public bool IsHostLost(long nowMilliseconds, uint hostTimeoutMilliseconds) => HostEndPoint is null || nowMilliseconds - LastHostHeartbeatMilliseconds > hostTimeoutMilliseconds;

    /// <summary>
    /// Drops the current host, which makes the session eligible for an election and starts the window the session
    /// survives for while hostless.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    public void ClearHost(long nowMilliseconds)
    {
        HostEndPoint = null;
        HostlessSinceMilliseconds = nowMilliseconds;

        UnreachableReports.Clear();
        DeclinedEndPoints.Clear();

        IsHostChallengeOutstanding = false;
        HostChallengeDeadlineMilliseconds = 0;
        IneffectiveHostChallengeCount = 0;

        // Whoever hosts next starts with a clean slate rather than serving out the last host's cooldown.
        LastHostChallengeMilliseconds = UnchallengedHost;
    }

    /// <summary>
    /// Records that a peer cannot reach the host with the credential it holds.
    /// </summary>
    /// <param name="reporterEndPoint">The peer that cannot reach the host.</param>
    public void RecordCredentialUnreachable(IPEndPoint reporterEndPoint)
    {
        UnreachableReports[reporterEndPoint] = AttestationId;
    }

    /// <summary>
    /// Returns whether the host may be challenged again yet.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="challengeCooldownMilliseconds">The least time to leave the host alone between challenges.</param>
    /// <returns><see langword="true"/> when a challenge may be sent.</returns>
    /// <remarks>
    /// The cooldown is a property of the session rather than of the peer asking, so however many peers report and
    /// however often, the host hears about it at most this often.
    /// </remarks>
    public bool IsHostChallengeDue(long nowMilliseconds, uint challengeCooldownMilliseconds)
    {
        if (LastHostChallengeMilliseconds == UnchallengedHost)
            return true;

        return nowMilliseconds - LastHostChallengeMilliseconds >= challengeCooldownMilliseconds;
    }

    /// <summary>
    /// Asks the host to prove it is still hosting, and starts the round it has to answer in.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="challengeIntervalMilliseconds">How long the host has to answer.</param>
    public void BeginHostChallenge(long nowMilliseconds, uint challengeIntervalMilliseconds)
    {
        IsHostChallengeOutstanding = true;
        HostChallengeDeadlineMilliseconds = nowMilliseconds + challengeIntervalMilliseconds;
        LastHostChallengeMilliseconds = nowMilliseconds;
        ChallengeAttestationId = AttestationId;
    }

    /// <summary>
    /// Closes an outstanding challenge round and reports whether the host answered it.
    /// </summary>
    /// <returns><see langword="true"/> when the host said nothing, so the round counts against it.</returns>
    /// <remarks>
    /// Answering means publishing, which moves <see cref="AttestationId"/> on, so a round that closes on the
    /// attestation it opened with is one the host had nothing to say to.
    /// Silence is the only thing counted here. A host that answers and whose peers still cannot reach it is not, from
    /// newfarm's side of the wire, distinguishable from peers that cannot reach a room which is perfectly fine, and
    /// standing a host down on that would hand a working session's fate to whichever peer was worst connected. Newfarm
    /// has no census of the peers who are playing happily, since they stop talking to it once they have joined, so
    /// there is nothing to weigh a complaint against.
    /// </remarks>
    public bool CloseHostChallenge()
    {
        IsHostChallengeOutstanding = false;
        HostChallengeDeadlineMilliseconds = 0;

        if (AttestationId != ChallengeAttestationId)
        {
            IneffectiveHostChallengeCount = 0;

            return false;
        }

        IneffectiveHostChallengeCount++;

        return true;
    }

    /// <summary>
    /// Records that a peer has said it cannot host, so it is passed over when a replacement is elected.
    /// </summary>
    /// <param name="decliningEndPoint">The peer that cannot host.</param>
    public void RecordDeclined(IPEndPoint decliningEndPoint)
    {
        DeclinedEndPoints.Add(decliningEndPoint);
    }

    /// <summary>
    /// Adds a peer to the waiting set, or refreshes it when already present.
    /// </summary>
    /// <param name="waiterEndPoint">The waiting peer.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    public void AddWaiter(IPEndPoint waiterEndPoint, long nowMilliseconds)
    {
        if (!WaiterHeartbeats.ContainsKey(waiterEndPoint))
            WaiterOrder.Add(waiterEndPoint);

        WaiterHeartbeats[waiterEndPoint] = nowMilliseconds;
    }

    /// <summary>
    /// Records a heartbeat from a waiting peer.
    /// </summary>
    /// <param name="waiterEndPoint">The waiting peer.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns><see langword="true"/> when the peer was in the waiting set.</returns>
    public bool RecordWaiterHeartbeat(IPEndPoint waiterEndPoint, long nowMilliseconds)
    {
        if (!WaiterHeartbeats.ContainsKey(waiterEndPoint))
            return false;

        WaiterHeartbeats[waiterEndPoint] = nowMilliseconds;

        return true;
    }

    /// <summary>
    /// Removes a peer from the waiting set.
    /// </summary>
    /// <param name="waiterEndPoint">The peer to remove.</param>
    public void RemoveWaiter(IPEndPoint waiterEndPoint)
    {
        WaiterOrder.Remove(waiterEndPoint);
        WaiterHeartbeats.Remove(waiterEndPoint);
        WaiterKeepAlives.Remove(waiterEndPoint);
        DeclinedEndPoints.Remove(waiterEndPoint);
    }

    /// <summary>
    /// Records that a waiting peer has been told it is still queued.
    /// </summary>
    /// <param name="waiterEndPoint">The peer that was told.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    public void RecordWaiterKeepAlive(IPEndPoint waiterEndPoint, long nowMilliseconds)
    {
        WaiterKeepAlives[waiterEndPoint] = nowMilliseconds;
    }

    /// <summary>
    /// Returns whether a waiting peer is due to be told it is still queued.
    /// </summary>
    /// <param name="waiterEndPoint">The waiting peer.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="keepAliveIntervalMilliseconds">How often a waiting peer should hear from newfarm.</param>
    /// <returns><see langword="true"/> when a keep-alive is due.</returns>
    public bool IsWaiterKeepAliveDue(IPEndPoint waiterEndPoint, long nowMilliseconds, uint keepAliveIntervalMilliseconds)
    {
        if (!WaiterKeepAlives.TryGetValue(waiterEndPoint, out long lastKeepAliveMilliseconds))
            return true;

        return nowMilliseconds - lastKeepAliveMilliseconds >= keepAliveIntervalMilliseconds;
    }

    /// <summary>
    /// Elects a waiting peer to host, removing it from the waiting set for the duration of its election.
    /// </summary>
    /// <param name="electedEndPoint">The peer being told to host.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="electionDeadlineMilliseconds">How long the peer has to publish a credential.</param>
    public void Elect(IPEndPoint electedEndPoint, long nowMilliseconds, uint electionDeadlineMilliseconds)
    {
        RemoveWaiter(electedEndPoint);

        ElectedEndPoint = electedEndPoint;
        ElectionDeadlineMilliseconds = nowMilliseconds + electionDeadlineMilliseconds;
        LastElectedHeartbeatMilliseconds = nowMilliseconds;
    }

    /// <summary>
    /// Records a heartbeat from the peer that is currently elected, so an election is withdrawn as soon as that peer
    /// looks gone rather than only once its deadline passes.
    /// </summary>
    /// <param name="electedEndPoint">The peer that sent the heartbeat.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns><see langword="true"/> when the sender is the elected peer and the heartbeat was recorded.</returns>
    public bool TryRecordElectedHeartbeat(IPEndPoint electedEndPoint, long nowMilliseconds)
    {
        if (ElectedEndPoint is null || !ElectedEndPoint.Equals(electedEndPoint))
            return false;

        LastElectedHeartbeatMilliseconds = nowMilliseconds;

        return true;
    }

    /// <summary>
    /// Withdraws the outstanding election and returns the peer it was withdrawn from.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns>The peer that had been elected, or <see langword="null"/> when there was no election.</returns>
    /// <remarks>
    /// The peer rejoins the back of the waiting set, so it still receives the credential the peer elected in its
    /// place goes on to publish.
    /// </remarks>
    public IPEndPoint? AbortElection(long nowMilliseconds)
    {
        IPEndPoint? abortedEndPoint = ElectedEndPoint;

        ElectedEndPoint = null;
        ElectionDeadlineMilliseconds = 0;

        if (abortedEndPoint is not null)
            AddWaiter(abortedEndPoint, nowMilliseconds);

        return abortedEndPoint;
    }

    /// <summary>
    /// Returns whether an outstanding election has run out of time.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns><see langword="true"/> when an election is outstanding and its deadline has passed.</returns>
    public bool IsElectionExpired(long nowMilliseconds) => ElectedEndPoint is not null && nowMilliseconds >= ElectionDeadlineMilliseconds;

    /// <summary>
    /// Accepts a credential from the peer that published it, which promotes that peer to host and ends any
    /// outstanding election.
    /// </summary>
    /// <param name="publisherEndPoint">The peer that published the credential.</param>
    /// <param name="adapterTag">The service the credential belongs to.</param>
    /// <param name="credential">The opaque credential bytes.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    public void AcceptCredential(IPEndPoint publisherEndPoint, string adapterTag, byte[] credential, long nowMilliseconds)
    {
        AdapterTag = adapterTag;
        Credential = credential;
        CredentialPublishedMilliseconds = nowMilliseconds;

        HostEndPoint = publisherEndPoint;
        LastHostHeartbeatMilliseconds = nowMilliseconds;

        ElectedEndPoint = null;
        ElectionDeadlineMilliseconds = 0;

        AttestationId++;

        UnreachableReports.Clear();
        DeclinedEndPoints.Clear();
    }

    /// <summary>
    /// Gives the session up on behalf of the peer hosting it, leaving the session and its waiters intact so a
    /// replacement can be elected at once.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    public void SurrenderHosting(long nowMilliseconds)
    {
        ClearHost(nowMilliseconds);
    }

    /// <summary>
    /// Opens a new epoch, which is done when a hostless session is about to be handed to a new peer so that anything
    /// published against the previous epoch is refused.
    /// </summary>
    public void AdvanceEpoch()
    {
        Epoch++;

        AdapterTag = string.Empty;
        Credential = null;
        CredentialPublishedMilliseconds = 0;
        AttestationId = 0;

        UnreachableReports.Clear();

        IsHostChallengeOutstanding = false;
        HostChallengeDeadlineMilliseconds = 0;
        IneffectiveHostChallengeCount = 0;
        LastHostChallengeMilliseconds = UnchallengedHost;
    }

    /// <summary>
    /// Returns whether the session has nothing left worth keeping: nobody hosting it, nobody elected, nobody
    /// waiting, and both of its grace windows elapsed.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="credentialGraceMilliseconds">How long a published credential outlives its host.</param>
    /// <param name="hostlessGraceMilliseconds">How long a session survives with nobody hosting it.</param>
    /// <returns><see langword="true"/> when the session can be evicted.</returns>
    /// <remarks>
    /// The two windows start at different moments and both have to have passed, because they cover different
    /// arrivals: a peer that was slow to notice the handover comes for the credential, while a peer that was slow to
    /// notice the host had gone has nothing to be given yet and only needs the session to still be here.
    /// </remarks>
    public bool IsExpired(long nowMilliseconds, uint credentialGraceMilliseconds, uint hostlessGraceMilliseconds)
    {
        if (HostEndPoint is not null || ElectedEndPoint is not null || WaiterOrder.Count > 0)
            return false;

        if (HasCredential && nowMilliseconds - CredentialPublishedMilliseconds <= credentialGraceMilliseconds)
            return false;

        return nowMilliseconds - HostlessSinceMilliseconds > hostlessGraceMilliseconds;
    }

    /// <summary>
    /// Sentinel <see cref="LastHostChallengeMilliseconds"/> value: the peer hosting the session has not been
    /// challenged, so the next challenge is due at once.
    /// </summary>
    private const long UnchallengedHost = 0;
}
