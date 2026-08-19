using System;
using System.Text;
using Newfarm.Client;
using Newfarm.Wire;

namespace Newfarm.Tests;

/// <summary>
/// End-to-end tests for the directory: a real server on a loopback port, real clients, real datagrams. Nothing is
/// stubbed, so a break anywhere between the wire format and the election rules fails these.
/// </summary>
public sealed class NewfarmDirectoryTests
{
    /// <summary>
    /// The tag a test publishes its credential under, standing in for whichever relay a game would be using.
    /// </summary>
    private const string AdapterTag = "blitzrelay";

    /// <summary>
    /// A host obtains a session, and the identity it is given is usable as the two text values it hands to clients.
    /// </summary>
    [Fact]
    public void HostObtainsASessionItCanDistribute()
    {
        using NewfarmTestHarness harness = new();

        NewfarmTestPeer host = harness.CreatePeer();

        host.Client.CreateSession();

        harness.PumpUntil(() => host.CreatedIdentity is not null, NewfarmTestHarness.WaitTimeout, "the session to be created");

        NewfarmSessionIdentity identity = host.CreatedIdentity!.Value;

        Assert.NotEqual(Guid.Empty, identity.SessionId);
        Assert.NotEqual(Guid.Empty, identity.SessionSecret);
        Assert.NotEqual(identity.SessionId, identity.SessionSecret);
        Assert.Equal(1u, identity.Epoch);
        Assert.Equal(1, harness.Server.SessionCount);

        identity.ToText(out string sessionIdText, out string sessionSecretText);

        Assert.True(NewfarmSessionIdentity.TryParse(sessionIdText, sessionSecretText, out NewfarmSessionIdentity parsed));
        Assert.Equal(identity.SessionId, parsed.SessionId);
        Assert.Equal(identity.SessionSecret, parsed.SessionSecret);
    }

    /// <summary>
    /// The whole point of the directory: the host goes away, exactly one of the waiting peers is told to host, and the
    /// credential it publishes reaches every other peer.
    /// </summary>
    [Fact]
    public void LosingTheHostElectsOnePeerAndFansItsCredentialOutToTheRest()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer firstClient = harness.CreatePeer();
        NewfarmTestPeer secondClient = harness.CreatePeer();
        NewfarmTestPeer thirdClient = harness.CreatePeer();

        AbandonHost(harness, host);

        firstClient.Client.AwaitSession(identity);
        secondClient.Client.AwaitSession(identity);
        thirdClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => TotalElections(firstClient, secondClient, thirdClient) > 0, NewfarmTestHarness.WaitTimeout, "a peer to be elected");

        Assert.Equal(1, TotalElections(firstClient, secondClient, thirdClient));

        NewfarmTestPeer electedPeer = ElectedPeer(firstClient, secondClient, thirdClient);

        byte[] credential = Encoding.UTF8.GetBytes("ROOM-0002");

        electedPeer.Client.PublishCredential(AdapterTag, credential);

        harness.PumpUntil(() => firstClient.ReceivedCredential is not null || electedPeer == firstClient, NewfarmTestHarness.WaitTimeout, "the first client to learn the credential");
        harness.PumpUntil(() => secondClient.ReceivedCredential is not null || electedPeer == secondClient, NewfarmTestHarness.WaitTimeout, "the second client to learn the credential");
        harness.PumpUntil(() => thirdClient.ReceivedCredential is not null || electedPeer == thirdClient, NewfarmTestHarness.WaitTimeout, "the third client to learn the credential");

        AssertCredential(firstClient, electedPeer, credential);
        AssertCredential(secondClient, electedPeer, credential);
        AssertCredential(thirdClient, electedPeer, credential);
    }

    /// <summary>
    /// A peer that arrives after the handover is served the credential rather than queued, which is what the grace
    /// period after a publication exists for.
    /// </summary>
    [Fact]
    public void APeerArrivingAfterTheHandoverIsStillGivenTheCredential()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer firstClient = harness.CreatePeer();

        AbandonHost(harness, host);

        firstClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => firstClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the only client to be elected");

        byte[] credential = Encoding.UTF8.GetBytes("ROOM-LATE");

        firstClient.Client.PublishCredential(AdapterTag, credential);

        harness.PumpFor(TimeSpan.FromMilliseconds(300));

        NewfarmTestPeer lateClient = harness.CreatePeer();

        lateClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => lateClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the late client to be given the credential");

        Assert.Equal(credential, lateClient.ReceivedCredential!.Value.Credential);
        Assert.Equal(0, lateClient.ElectionCount);
    }

    /// <summary>
    /// An elected peer that publishes nothing is stood down when its deadline passes, the next waiter is elected in
    /// its place, and the peer that was stood down is then given the credential like any other waiter.
    /// </summary>
    [Fact]
    public void AnElectedPeerThatNeverPublishesIsStoodDownAndTheNextIsElected()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer firstClient = harness.CreatePeer();
        NewfarmTestPeer secondClient = harness.CreatePeer();

        AbandonHost(harness, host);

        firstClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => firstClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the first client to be elected");

        secondClient.Client.AwaitSession(identity);

        // The first client stays silent, so its election has to lapse rather than being declined.
        harness.PumpUntil(() => firstClient.AbortCount > 0, NewfarmTestHarness.WaitTimeout, "the first client's election to be withdrawn");
        harness.PumpUntil(() => secondClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the second client to be elected in its place");

        byte[] credential = Encoding.UTF8.GetBytes("ROOM-SECOND");

        secondClient.Client.PublishCredential(AdapterTag, credential);

        harness.PumpUntil(() => firstClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the stood-down peer to be given the credential");

        Assert.Equal(credential, firstClient.ReceivedCredential!.Value.Credential);
    }

    /// <summary>
    /// An elected peer that knows it cannot host says so, which hands the session on immediately instead of after the
    /// election deadline.
    /// </summary>
    [Fact]
    public void AnElectedPeerThatDeclinesHandsOverWithoutWaitingForItsDeadline()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer firstClient = harness.CreatePeer();
        NewfarmTestPeer secondClient = harness.CreatePeer();

        AbandonHost(harness, host);

        firstClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => firstClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the first client to be elected");

        secondClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => harness.Server.SessionCount == 1, TimeSpan.FromMilliseconds(200), "the second client to be queued");

        firstClient.Client.DeclineElection();

        harness.PumpUntil(() => secondClient.ElectionCount > 0, TimeSpan.FromSeconds(1), "the second client to be elected after the decline");

        Assert.True(secondClient.ElectionCount > 0);
    }

    /// <summary>
    /// The secret is what stands between a leaked session id and a stolen session, so a peer presenting the wrong one
    /// is refused rather than queued or elected.
    /// </summary>
    [Fact]
    public void AWrongSecretIsRefused()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer impostor = harness.CreatePeer();

        AbandonHost(harness, host);

        impostor.Client.AwaitSession(new NewfarmSessionIdentity(identity.SessionId, Guid.NewGuid(), epoch: 0));

        harness.PumpUntil(() => impostor.Refusals.Count > 0, NewfarmTestHarness.WaitTimeout, "the impostor to be refused");

        Assert.Contains(NewfarmPacketType.SecretRejected, impostor.Refusals);
        Assert.Equal(0, impostor.ElectionCount);
        Assert.Null(impostor.ReceivedCredential);
    }

    /// <summary>
    /// An unknown session id is refused, which is what a peer holding a session that has since expired sees.
    /// </summary>
    [Fact]
    public void AnUnknownSessionIsRefused()
    {
        using NewfarmTestHarness harness = new();

        NewfarmTestPeer peer = harness.CreatePeer();

        peer.Client.AwaitSession(new NewfarmSessionIdentity(Guid.NewGuid(), Guid.NewGuid(), epoch: 0));

        harness.PumpUntil(() => peer.Refusals.Count > 0, NewfarmTestHarness.WaitTimeout, "the peer to be refused");

        Assert.Contains(NewfarmPacketType.SessionNotFound, peer.Refusals);
    }

    /// <summary>
    /// A peer that lost only its own link, while the host is still heartbeating, must not be elected. Without this a
    /// single client's network blip would split the session in two.
    /// </summary>
    [Fact]
    public void APeerIsNotElectedWhileTheHostIsStillHeartbeating()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer blippedClient = harness.CreatePeer();

        blippedClient.Client.AwaitSession(identity);

        // Well past the host timeout, but the host is still being polled and so still heartbeating.
        harness.PumpFor(TimeSpan.FromMilliseconds(1500));

        Assert.Equal(0, blippedClient.ElectionCount);
        Assert.Equal(NewfarmClientMode.Hosting, host.Client.Mode);
    }

    /// <summary>
    /// A returning old host publishes against the epoch it remembers, which is no longer current, so the credential
    /// the live host published stands.
    /// </summary>
    [Fact]
    public void AReturningOldHostCannotPublishAgainstAStaleEpoch()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer successor = harness.CreatePeer();
        NewfarmTestPeer bystander = harness.CreatePeer();

        AbandonHost(harness, host);

        successor.Client.AwaitSession(identity);
        bystander.Client.AwaitSession(identity);

        harness.PumpUntil(() => successor.ElectionCount > 0 || bystander.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "a peer to be elected");

        NewfarmTestPeer electedPeer = successor.ElectionCount > 0 ? successor : bystander;
        NewfarmTestPeer otherPeer = electedPeer == successor ? bystander : successor;

        byte[] liveCredential = Encoding.UTF8.GetBytes("ROOM-LIVE");

        electedPeer.Client.PublishCredential(AdapterTag, liveCredential);

        harness.PumpUntil(() => otherPeer.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the other peer to learn the live credential");

        // The old host comes back believing it still holds epoch 1 and tries to point the session at its own room.
        host.IsAbandoned = false;
        host.Client.StartHosting(new NewfarmSessionIdentity(identity.SessionId, identity.SessionSecret, epoch: 1));
        host.Client.PublishCredential(AdapterTag, Encoding.UTF8.GetBytes("ROOM-STALE"));

        harness.PumpFor(TimeSpan.FromMilliseconds(400));

        NewfarmTestPeer lateClient = harness.CreatePeer();

        lateClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => lateClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "a late client to be given the credential that stands");

        Assert.Equal(liveCredential, lateClient.ReceivedCredential!.Value.Credential);
    }

    /// <summary>
    /// Newfarm hands out an opaque credential, so a value that is not text and not a room code survives the round
    /// trip byte for byte.
    /// </summary>
    [Fact]
    public void ACredentialIsCarriedThroughUntouched()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer successor = harness.CreatePeer();
        NewfarmTestPeer bystander = harness.CreatePeer();

        AbandonHost(harness, host);

        successor.Client.AwaitSession(identity);

        harness.PumpUntil(() => successor.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the successor to be elected");

        bystander.Client.AwaitSession(identity);

        harness.PumpFor(TimeSpan.FromMilliseconds(150));

        byte[] credential = new byte[256];

        for (int i = 0; i < credential.Length; i++)
            credential[i] = (byte)(i % 251);

        successor.Client.PublishCredential("allocation+jwt", credential);

        harness.PumpUntil(() => bystander.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the bystander to learn the credential");

        Assert.Equal(credential, bystander.ReceivedCredential!.Value.Credential);
        Assert.Equal("allocation+jwt", bystander.ReceivedCredential!.Value.AdapterTag);
    }

    /// <summary>
    /// A waiting peer is told it is still queued, so it can tell the directory being quiet from the directory being
    /// gone.
    /// </summary>
    [Fact]
    public void AWaitingPeerIsKeptInformedWhileItWaits()
    {
        using NewfarmTestHarness harness = new(config => config.ElectionDeadlineMilliseconds = 30000);

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer electedPeer = harness.CreatePeer();
        NewfarmTestPeer waitingPeer = harness.CreatePeer();

        AbandonHost(harness, host);

        electedPeer.Client.AwaitSession(identity);

        harness.PumpUntil(() => electedPeer.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the first peer to be elected");

        waitingPeer.Client.AwaitSession(identity);

        // The elected peer sits on its election, so the waiting peer stays queued with nothing to be given.
        harness.PumpFor(TimeSpan.FromMilliseconds(600));

        Assert.Equal(NewfarmClientMode.Waiting, waitingPeer.Client.Mode);
        Assert.Equal(0, waitingPeer.ElectionCount);
        Assert.Null(waitingPeer.ReceivedCredential);
    }

    /// <summary>
    /// A session survives its host long enough for a client that was slow to notice to still find it, which is what
    /// makes migration possible at all: a client cannot be told anything about a session newfarm has forgotten.
    /// </summary>
    [Fact]
    public void ASessionOutlivesItsHostForTheHostlessGrace()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        AbandonHost(harness, host);

        // Comfortably past the host timeout, which is what a client slow to notice its host had gone looks like.
        harness.PumpFor(TimeSpan.FromSeconds(2));

        Assert.Equal(1, harness.Server.SessionCount);

        NewfarmTestPeer slowClient = harness.CreatePeer();

        slowClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => slowClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the slow client to be elected");

        Assert.Empty(slowClient.Refusals);
    }

    /// <summary>
    /// A session with nothing left worth keeping is forgotten, so the directory does not accumulate dead sessions.
    /// </summary>
    [Fact]
    public void ASessionWithNoHostAndNoWaitersIsEventuallyForgotten()
    {
        using NewfarmTestHarness harness = new(config =>
        {
            config.CredentialGraceMilliseconds = 200;
            config.HostlessGraceMilliseconds = 200;
        });

        CreateSession(harness, out NewfarmTestPeer host);

        Assert.Equal(1, harness.Server.SessionCount);

        host.IsAbandoned = true;

        harness.PumpUntil(() => harness.Server.SessionCount == 0, NewfarmTestHarness.WaitTimeout, "the abandoned session to be forgotten");
    }

    /// <summary>
    /// The peer that opened a session can publish the room it started in, so a peer that lost only its own link is
    /// sent back to the live room instead of being queued behind a host that never went anywhere.
    /// </summary>
    [Fact]
    public void ABlippedClientIsSentBackToTheRoomTheLiveHostPublished()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        byte[] credential = Encoding.UTF8.GetBytes("ROOM-0001");

        host.Client.PublishCredential(AdapterTag, credential);

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        NewfarmTestPeer blippedClient = harness.CreatePeer();

        blippedClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => blippedClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the blipped client to be sent back to the live room");

        Assert.Equal(credential, blippedClient.ReceivedCredential!.Value.Credential);
        Assert.Equal(0, blippedClient.ElectionCount);
        Assert.Equal(NewfarmClientMode.Hosting, host.Client.Mode);
    }

    /// <summary>
    /// A peer holding the secret cannot install itself as host by heartbeating, which would otherwise suppress the
    /// election that a lost host is supposed to trigger.
    /// </summary>
    [Fact]
    public void APeerCannotMakeItselfHostByHeartbeating()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer usurper = harness.CreatePeer();
        NewfarmTestPeer waiter = harness.CreatePeer();

        AbandonHost(harness, host);

        // Hosting mode is what makes the client send host heartbeats, and it is claiming a session it was never
        // elected for.
        usurper.Client.StartHosting(identity);

        waiter.Client.AwaitSession(identity);

        harness.PumpUntil(() => waiter.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the waiting peer to be elected despite the usurper's heartbeats");

        byte[] credential = Encoding.UTF8.GetBytes("ROOM-ELECTED");

        waiter.Client.PublishCredential(AdapterTag, credential);

        harness.PumpFor(TimeSpan.FromMilliseconds(300));

        NewfarmTestPeer lateClient = harness.CreatePeer();

        lateClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => lateClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "a late client to be given the elected peer's credential");

        Assert.Equal(credential, lateClient.ReceivedCredential!.Value.Credential);
    }

    /// <summary>
    /// Opens a session and returns its identity.
    /// </summary>
    /// <param name="harness">The harness to create the host on.</param>
    /// <param name="host">Receives the peer holding the session.</param>
    /// <returns>The identity the host would distribute.</returns>
    private static NewfarmSessionIdentity CreateSession(NewfarmTestHarness harness, out NewfarmTestPeer host)
    {
        host = harness.CreatePeer();

        host.Client.CreateSession();

        NewfarmTestPeer createdHost = host;

        harness.PumpUntil(() => createdHost.CreatedIdentity is not null, NewfarmTestHarness.WaitTimeout, "the session to be created");

        return host.CreatedIdentity!.Value;
    }

    /// <summary>
    /// Stops polling the host and waits for newfarm to notice, which is how a test kills a host without closing the
    /// socket it may need again.
    /// </summary>
    /// <param name="harness">The harness driving the peers.</param>
    /// <param name="host">The host to abandon.</param>
    private static void AbandonHost(NewfarmTestHarness harness, NewfarmTestPeer host)
    {
        host.IsAbandoned = true;

        harness.PumpFor(TimeSpan.FromMilliseconds(harness.Server.Config.HostTimeoutMilliseconds + 200));
    }

    /// <summary>
    /// Counts how many of the supplied peers have been told to host.
    /// </summary>
    /// <param name="peers">The peers to count across.</param>
    /// <returns>The total number of elections observed.</returns>
    private static int TotalElections(params NewfarmTestPeer[] peers)
    {
        int electionCount = 0;

        for (int i = 0; i < peers.Length; i++)
            electionCount += peers[i].ElectionCount;

        return electionCount;
    }

    /// <summary>
    /// Returns the single peer that was elected.
    /// </summary>
    /// <param name="peers">The peers to search.</param>
    /// <returns>The elected peer.</returns>
    private static NewfarmTestPeer ElectedPeer(params NewfarmTestPeer[] peers)
    {
        for (int i = 0; i < peers.Length; i++)
        {
            if (peers[i].ElectionCount > 0)
                return peers[i];
        }

        throw new InvalidOperationException("No peer was elected.");
    }

    /// <summary>
    /// Asserts that a peer either published the credential itself or was handed exactly it.
    /// </summary>
    /// <param name="peer">The peer to check.</param>
    /// <param name="electedPeer">The peer that published, which is not handed its own credential back.</param>
    /// <param name="credential">The credential that was published.</param>
    private static void AssertCredential(NewfarmTestPeer peer, NewfarmTestPeer electedPeer, byte[] credential)
    {
        if (peer == electedPeer)
            return;

        Assert.NotNull(peer.ReceivedCredential);
        Assert.Equal(credential, peer.ReceivedCredential!.Value.Credential);
        Assert.Equal(AdapterTag, peer.ReceivedCredential!.Value.AdapterTag);
    }
}
