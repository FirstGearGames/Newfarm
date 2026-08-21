using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using Newfarm.Wire;

namespace Newfarm.Server;

/// <summary>
/// Holds every live session and owns every decision newfarm makes about them: who is elected, when an election is
/// withdrawn, who is told about a credential, and when a session is forgotten.
/// </summary>
/// <remarks>
/// Deliberately free of sockets. It decides and reports what should be sent through
/// <see cref="NewfarmNotification"/> values, and <see cref="NewfarmServer"/> does the sending, which is what makes
/// every rule in here testable without a network.
/// Touched only from the server's single loop thread, so its state is unsynchronised.
/// </remarks>
internal sealed class NewfarmSessionRegistry
{
    /// <summary>
    /// Every live session, keyed by the identity its host distributes.
    /// </summary>
    private readonly Dictionary<ulong, NewfarmSession> _sessionsById = [];

    /// <summary>
    /// The configuration the timings and caps are read from.
    /// </summary>
    private readonly NewfarmServerConfig _config;

    /// <summary>
    /// The number of sessions currently held.
    /// </summary>
    public int Count => _sessionsById.Count;

    /// <summary>
    /// Creates a registry bound to the supplied configuration.
    /// </summary>
    /// <param name="config">The configuration to read timings and caps from.</param>
    public NewfarmSessionRegistry(NewfarmServerConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Opens a session for a peer that is about to host, generating both its identity and its secret.
    /// </summary>
    /// <param name="hostEndPoint">The peer asking for the session.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="session">When this returns <see langword="true"/>, the session that was opened.</param>
    /// <returns><see langword="true"/> unless newfarm is already holding its configured maximum.</returns>
    public bool TryCreateSession(IPEndPoint hostEndPoint, long nowMilliseconds, out NewfarmSession? session)
    {
        if (_config.MaximumConcurrentSessions is not NewfarmServerConfig.UnlimitedConcurrentSessions && _sessionsById.Count >= _config.MaximumConcurrentSessions)
        {
            session = null;

            return false;
        }

        ulong sessionId = CreateSessionId();

        session = new NewfarmSession(sessionId, hostEndPoint, nowMilliseconds);

        _sessionsById.Add(sessionId, session);

        return true;
    }

    /// <summary>
    /// Looks a session up.
    /// </summary>
    /// <param name="sessionId">The session being addressed.</param>
    /// <param name="session">When this returns <see cref="NewfarmRequestResult.Accepted"/>, the session found.</param>
    /// <returns>Why the request was refused, or <see cref="NewfarmRequestResult.Accepted"/>.</returns>
    public NewfarmRequestResult TryResolve(ulong sessionId, out NewfarmSession? session)
    {
        if (!_sessionsById.TryGetValue(sessionId, out session))
            return NewfarmRequestResult.SessionNotFound;

        return NewfarmRequestResult.Accepted;
    }

    /// <summary>
    /// Adds a peer to a session's waiting set and decides what it should be told straight away.
    /// </summary>
    /// <param name="session">The session the peer is waiting on.</param>
    /// <param name="waiterEndPoint">The waiting peer.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns>What the peer should be told.</returns>
    /// <remarks>
    /// A peer arriving while the host is still alive is simply queued: it is not elected, because the host being
    /// alive means this peer lost its own link rather than the session losing its host. That is what stops a single
    /// peer's network blip from splitting the session in two.
    /// </remarks>
    public NewfarmWaitOutcome AddWaiter(NewfarmSession session, IPEndPoint waiterEndPoint, long nowMilliseconds)
    {
        // Peers already in the queue are always let through, so a full session still keeps the ones it has informed
        // and only turns away arrivals it has no room to inform.
        if (session.WaiterOrder.Count >= _config.MaximumWaitersPerSession && !session.WaiterHeartbeats.ContainsKey(waiterEndPoint))
            return NewfarmWaitOutcome.SessionFull;

        session.AddWaiter(waiterEndPoint, nowMilliseconds);

        if (session.HasCredential && !session.IsHostLost(nowMilliseconds, _config.HostTimeoutMilliseconds))
        {
            session.RemoveWaiter(waiterEndPoint);

            return NewfarmWaitOutcome.CredentialAvailable;
        }

        if (session.HasCredential)
            return NewfarmWaitOutcome.CredentialAvailable;

        return NewfarmWaitOutcome.Waiting;
    }

    /// <summary>
    /// Accepts a credential from the peer that was elected, and reports who should be told about it.
    /// </summary>
    /// <param name="session">The session the credential belongs to.</param>
    /// <param name="publisherEndPoint">The peer publishing the credential.</param>
    /// <param name="epoch">The epoch the peer believes it was elected for.</param>
    /// <param name="adapterTag">The service the credential belongs to.</param>
    /// <param name="credential">The opaque credential bytes.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="notifications">Receives one <see cref="NewfarmPacketType.CredentialAvailable"/> per waiting peer.</param>
    /// <returns>Why the publication was refused, or <see cref="NewfarmRequestResult.Accepted"/>.</returns>
    /// <remarks>
    /// The peer that opened the session publishes through here as well, which is how the room the session started in
    /// is registered. Without that, a peer that lost only its own link would be queued against a session newfarm
    /// could not say the whereabouts of, and would sit there while a perfectly healthy host carried on.
    /// </remarks>
    public NewfarmRequestResult PublishCredential(NewfarmSession session, IPEndPoint publisherEndPoint, uint epoch, string adapterTag, string credential, long nowMilliseconds, List<NewfarmNotification> notifications)
    {
        if (epoch != session.Epoch)
            return NewfarmRequestResult.StaleEpoch;

        bool isPublisherElected = session.ElectedEndPoint is not null && session.ElectedEndPoint.Equals(publisherEndPoint);
        bool isPublisherHosting = session.HostEndPoint is not null && session.HostEndPoint.Equals(publisherEndPoint);

        if (!isPublisherElected && !isPublisherHosting)
            return NewfarmRequestResult.NotElected;

        session.AcceptCredential(publisherEndPoint, adapterTag, credential, nowMilliseconds);

        for (int i = 0; i < session.WaiterOrder.Count; i++)
            notifications.Add(new NewfarmNotification(NewfarmPacketType.CredentialAvailable, session.WaiterOrder[i], session));

        return NewfarmRequestResult.Accepted;
    }

    /// <summary>
    /// Withdraws the election of a peer that has said it cannot host, and elects the next waiter in its place.
    /// </summary>
    /// <param name="session">The session being handed on.</param>
    /// <param name="decliningEndPoint">The peer declining the election.</param>
    /// <param name="epoch">The epoch the peer believes it was elected for.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="notifications">Receives the abort for the declining peer and the election for its replacement.</param>
    /// <returns>Why the decline was refused, or <see cref="NewfarmRequestResult.Accepted"/>.</returns>
    public NewfarmRequestResult DeclineElection(NewfarmSession session, IPEndPoint decliningEndPoint, uint epoch, long nowMilliseconds, List<NewfarmNotification> notifications)
    {
        if (epoch != session.Epoch)
            return NewfarmRequestResult.StaleEpoch;

        if (session.ElectedEndPoint is null || !session.ElectedEndPoint.Equals(decliningEndPoint))
            return NewfarmRequestResult.NotElected;

        WithdrawElection(session, nowMilliseconds, notifications);

        session.RecordDeclined(decliningEndPoint);

        return NewfarmRequestResult.Accepted;
    }

    /// <summary>
    /// Gives the session up on behalf of the peer hosting it, and elects a replacement at once.
    /// </summary>
    /// <param name="session">The session being handed on.</param>
    /// <param name="hostEndPoint">The peer giving the session up.</param>
    /// <param name="epoch">The epoch the peer believes it is hosting.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="notifications">Receives the election for the replacement.</param>
    /// <returns>Why the surrender was refused, or <see cref="NewfarmRequestResult.Accepted"/>.</returns>
    /// <remarks>
    /// The peer stays in the session as a waiter, because giving up hosting is not the same as leaving: the usual
    /// reason is a player who dropped out of the match but is still sitting there, and it should be handed the
    /// credential of whoever hosts next like anyone else.
    /// </remarks>
    public NewfarmRequestResult SurrenderHosting(NewfarmSession session, IPEndPoint hostEndPoint, uint epoch, long nowMilliseconds, List<NewfarmNotification> notifications)
    {
        if (epoch != session.Epoch)
            return NewfarmRequestResult.StaleEpoch;

        if (session.HostEndPoint is null || !session.HostEndPoint.Equals(hostEndPoint))
            return NewfarmRequestResult.NotHosting;

        session.SurrenderHosting(nowMilliseconds);
        session.AddWaiter(hostEndPoint, nowMilliseconds);
        session.RecordDeclined(hostEndPoint);

        TryElectNextWaiter(session, nowMilliseconds, notifications);

        return NewfarmRequestResult.Accepted;
    }

    /// <summary>
    /// Records that a peer cannot reach the host with the credential it holds, and queues it so it can be elected if
    /// the host turns out to be gone.
    /// </summary>
    /// <param name="session">The session the peer cannot reach.</param>
    /// <param name="reporterEndPoint">The peer reporting.</param>
    /// <param name="epoch">The epoch the peer believes is current.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns>Why the report was refused, or <see cref="NewfarmRequestResult.Accepted"/>.</returns>
    public NewfarmRequestResult ReportCredentialUnreachable(NewfarmSession session, IPEndPoint reporterEndPoint, uint epoch, long nowMilliseconds)
    {
        if (epoch != session.Epoch)
            return NewfarmRequestResult.StaleEpoch;

        session.AddWaiter(reporterEndPoint, nowMilliseconds);
        session.RecordCredentialUnreachable(reporterEndPoint);

        return NewfarmRequestResult.Accepted;
    }

    /// <summary>
    /// Removes a session outright, which a host does when it shuts a session down deliberately.
    /// </summary>
    /// <param name="session">The session to remove.</param>
    public void RemoveSession(NewfarmSession session)
    {
        _sessionsById.Remove(session.SessionId);
    }

    /// <summary>
    /// Advances every session by one tick: forgets peers that have gone quiet, withdraws elections that ran out of
    /// time, elects a replacement where one is needed, keeps waiting peers informed, and evicts sessions with nothing
    /// left worth keeping.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="notifications">Receives everything that should be sent as a result of this sweep.</param>
    public void Sweep(long nowMilliseconds, List<NewfarmNotification> notifications)
    {
        List<ulong>? expiredSessionIds = null;

        foreach (KeyValuePair<ulong, NewfarmSession> entry in _sessionsById)
        {
            NewfarmSession session = entry.Value;

            RemoveLapsedWaiters(session, nowMilliseconds);

            if (session.HostEndPoint is not null && session.IsHostLost(nowMilliseconds, _config.HostTimeoutMilliseconds))
                session.ClearHost(nowMilliseconds);

            ChallengeHostIfUnreachable(session, nowMilliseconds, notifications);

            if (session.IsElectionExpired(nowMilliseconds))
                WithdrawElection(session, nowMilliseconds, notifications);
            else if (IsElectedPeerLost(session, nowMilliseconds))
                WithdrawElection(session, nowMilliseconds, notifications);

            if (session.HostEndPoint is null && session.ElectedEndPoint is null)
                TryElectNextWaiter(session, nowMilliseconds, notifications);

            AddDueWaiterKeepAlives(session, nowMilliseconds, notifications);

            if (!session.IsExpired(nowMilliseconds, _config.CredentialGraceMilliseconds, _config.HostlessGraceMilliseconds))
                continue;

            expiredSessionIds ??= [];
            expiredSessionIds.Add(entry.Key);
        }

        if (expiredSessionIds is null)
            return;

        for (int i = 0; i < expiredSessionIds.Count; i++)
            _sessionsById.Remove(expiredSessionIds[i]);
    }

    /// <summary>
    /// Asks a host its peers cannot reach to prove it is still hosting, and stands it down once enough rounds have
    /// closed without that proof.
    /// </summary>
    /// <param name="session">The session to check.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="notifications">Receives the challenge for the host.</param>
    /// <remarks>
    /// A heartbeat only says a peer's connection to newfarm works. A peer whose game has wedged, whose room has gone,
    /// or whose route to the relay has failed while its route here has not, keeps heartbeating perfectly while
    /// hosting nothing, and without this the session would wait on it forever. Its peers are the ones who know, so
    /// their reports are what newfarm acts on, and the host is given the chance to answer with the one thing that
    /// settles it.
    /// </remarks>
    private void ChallengeHostIfUnreachable(NewfarmSession session, long nowMilliseconds, List<NewfarmNotification> notifications)
    {
        if (session.HostEndPoint is null)
            return;

        if (session.IsHostChallengeOutstanding)
        {
            if (nowMilliseconds < session.HostChallengeDeadlineMilliseconds)
                return;

            if (!session.CloseHostChallenge())
                return;

            if (session.IneffectiveHostChallengeCount < _config.MaximumIneffectiveHostChallenges)
                return;

            IPEndPoint stoodDownEndPoint = session.HostEndPoint;

            session.ClearHost(nowMilliseconds);

            // It stays in the session as a waiter, so it is told where everyone went, and is passed over by the
            // election, having just spent several rounds failing to show it could host.
            session.AddWaiter(stoodDownEndPoint, nowMilliseconds);
            session.RecordDeclined(stoodDownEndPoint);

            notifications.Add(new NewfarmNotification(NewfarmPacketType.HostingRevoked, stoodDownEndPoint, session));

            return;
        }

        if (!session.HasCurrentUnreachableReports)
            return;

        if (!session.IsHostChallengeDue(nowMilliseconds, _config.HostChallengeCooldownMilliseconds))
            return;

        session.BeginHostChallenge(nowMilliseconds, _config.HostChallengeIntervalMilliseconds);

        notifications.Add(new NewfarmNotification(NewfarmPacketType.ProveHosting, session.HostEndPoint, session));
    }

    /// <summary>
    /// Elects the longest waiting peer, opening a new epoch first so anything published against the previous one is
    /// refused.
    /// </summary>
    /// <param name="session">The hostless session to hand on.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="notifications">Receives the election for the chosen peer.</param>
    private void TryElectNextWaiter(NewfarmSession session, long nowMilliseconds, List<NewfarmNotification> notifications)
    {
        IPEndPoint? electedEndPoint = null;

        for (int i = 0; i < session.WaiterOrder.Count; i++)
        {
            if (session.DeclinedEndPoints.Contains(session.WaiterOrder[i]))
                continue;

            electedEndPoint = session.WaiterOrder[i];

            break;
        }

        if (electedEndPoint is null)
            return;

        session.AdvanceEpoch();
        session.Elect(electedEndPoint, nowMilliseconds, _config.ElectionDeadlineMilliseconds);

        notifications.Add(new NewfarmNotification(NewfarmPacketType.ElectHost, electedEndPoint, session));
    }

    /// <summary>
    /// Withdraws an outstanding election and tells the peer it has been stood down.
    /// </summary>
    /// <param name="session">The session whose election is being withdrawn.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="notifications">Receives the abort for the peer that was elected.</param>
    private static void WithdrawElection(NewfarmSession session, long nowMilliseconds, List<NewfarmNotification> notifications)
    {
        IPEndPoint? abortedEndPoint = session.AbortElection(nowMilliseconds);

        if (abortedEndPoint is null)
            return;

        notifications.Add(new NewfarmNotification(NewfarmPacketType.AbortElection, abortedEndPoint, session));
    }

    /// <summary>
    /// Returns whether the peer currently elected has stopped heartbeating, so its election can be withdrawn without
    /// waiting for the deadline.
    /// </summary>
    /// <param name="session">The session to check.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns><see langword="true"/> when the elected peer looks gone.</returns>
    private bool IsElectedPeerLost(NewfarmSession session, long nowMilliseconds)
    {
        if (session.ElectedEndPoint is null)
            return false;

        return nowMilliseconds - session.LastElectedHeartbeatMilliseconds > _config.WaiterTimeoutMilliseconds;
    }

    /// <summary>
    /// Forgets waiting peers that have stopped heartbeating, so an election is never handed to a peer that has gone.
    /// </summary>
    /// <param name="session">The session to prune.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void RemoveLapsedWaiters(NewfarmSession session, long nowMilliseconds)
    {
        List<IPEndPoint>? lapsedEndPoints = null;

        for (int i = 0; i < session.WaiterOrder.Count; i++)
        {
            IPEndPoint waiterEndPoint = session.WaiterOrder[i];

            if (!session.WaiterHeartbeats.TryGetValue(waiterEndPoint, out long lastHeartbeatMilliseconds))
                continue;

            if (nowMilliseconds - lastHeartbeatMilliseconds <= _config.WaiterTimeoutMilliseconds)
                continue;

            lapsedEndPoints ??= [];
            lapsedEndPoints.Add(waiterEndPoint);
        }

        if (lapsedEndPoints is null)
            return;

        for (int i = 0; i < lapsedEndPoints.Count; i++)
            session.RemoveWaiter(lapsedEndPoints[i]);
    }

    /// <summary>
    /// Queues a keep-alive for every waiting peer that is due one.
    /// </summary>
    /// <param name="session">The session whose waiters are being kept informed.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="notifications">Receives a keep-alive per due peer.</param>
    private void AddDueWaiterKeepAlives(NewfarmSession session, long nowMilliseconds, List<NewfarmNotification> notifications)
    {
        for (int i = 0; i < session.WaiterOrder.Count; i++)
        {
            IPEndPoint waiterEndPoint = session.WaiterOrder[i];

            if (!session.IsWaiterKeepAliveDue(waiterEndPoint, nowMilliseconds, _config.WaitingKeepAliveIntervalMilliseconds))
                continue;

            session.RecordWaiterKeepAlive(waiterEndPoint, nowMilliseconds);

            notifications.Add(new NewfarmNotification(NewfarmPacketType.Waiting, waiterEndPoint, session));
        }
    }

    /// <summary>
    /// Produces a session id from cryptographically strong bytes, never from an ordinary random number generator.
    /// </summary>
    /// <returns>A session id nobody can work out from the ones already handed out.</returns>
    /// <remarks>
    /// The id is the whole of a session's security: whoever holds one can be elected to host that session and can say
    /// where it has moved to. Sixty four unguessable bits is what that rests on, so it comes from a cryptographic
    /// source, and a session id nobody was given is a session id nobody can find.
    /// A collision would hand one session's players to another, so an id already in use is drawn again.
    /// </remarks>
    private ulong CreateSessionId()
    {
        byte[] sessionIdBytes = new byte[NewfarmWireFormat.SessionIdSize];

        while (true)
        {
#if NET8_0_OR_GREATER
            RandomNumberGenerator.Fill(sessionIdBytes);
#else
            using (RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create())
            {
                randomNumberGenerator.GetBytes(sessionIdBytes);
            }
#endif
            ulong sessionId = 0;

            for (int i = 0; i < sessionIdBytes.Length; i++)
                sessionId = (sessionId << 8) | sessionIdBytes[i];

            if (sessionId is not UnusableSessionId && !_sessionsById.ContainsKey(sessionId))
                return sessionId;
        }
    }

    /// <summary>
    /// Never handed out as a session id, so that a default or zeroed value never names a real session.
    /// </summary>
    private const ulong UnusableSessionId = 0;
}
