using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
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
    /// A host obtains a session, and the identity it is given survives the round trip through the text it hands to
    /// its clients.
    /// </summary>
    [Fact]
    public void HostObtainsASessionItCanDistribute()
    {
        using NewfarmTestHarness harness = new();

        NewfarmTestPeer host = harness.CreatePeer();

        host.Client.CreateSession();

        harness.PumpUntil(() => host.CreatedIdentity is not null, NewfarmTestHarness.WaitTimeout, "the session to be created");

        NewfarmSessionIdentity identity = host.CreatedIdentity!.Value;

        Assert.NotEqual(0ul, identity.SessionId);
        Assert.Equal(1u, identity.Epoch);
        Assert.Equal(1, harness.Server.SessionCount);

        string sessionIdText = identity.ToText();

        Assert.True(NewfarmSessionIdentity.TryParse(sessionIdText, out NewfarmSessionIdentity parsed));
        Assert.Equal(identity.SessionId, parsed.SessionId);
    }

    /// <summary>
    /// Session ids are drawn from a cryptographic source, so two sessions never share one and a run of them shows no
    /// pattern a third party could follow.
    /// </summary>
    [Fact]
    public void SessionIdsAreUnpredictableAndDistinct()
    {
        using NewfarmTestHarness harness = new();

        HashSet<ulong> sessionIds = [];

        for (int i = 0; i < 16; i++)
        {
            NewfarmTestPeer host = harness.CreatePeer();

            host.Client.CreateSession();

            harness.PumpUntil(() => host.CreatedIdentity is not null, NewfarmTestHarness.WaitTimeout, "a session to be created");

            ulong sessionId = host.CreatedIdentity!.Value.SessionId;

            Assert.True(sessionIds.Add(sessionId), "The same session id was handed out twice.");

            // A counter, a timestamp, or anything else ordered would sit inside a narrow band. These do not.
            Assert.NotEqual(0ul, sessionId >> 32);
        }
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

        string credential = "ROOM-0002";

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

        string credential = "ROOM-LATE";

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

        string credential = "ROOM-SECOND";

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
    /// An elected peer that dies without declining must not hold the session open. Its election is withdrawn for
    /// silence, it lapses from the waiting set like any other silent peer, and the session is evicted once the
    /// hostless grace passes. Without that, the withdrawal would re-queue the dead peer as freshly heard, the same
    /// sweep would elect it again, and the session would cycle between electing and withdrawing the same dead peer
    /// forever, never emptying and never being evicted.
    /// </summary>
    [Fact]
    public void ADeadElectedPeerDoesNotKeepTheSessionAliveForever()
    {
        using NewfarmTestHarness harness = new(config => config.HostlessGraceMilliseconds = 2000);

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer doomedClient = harness.CreatePeer();

        AbandonHost(harness, host);

        doomedClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => doomedClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the doomed client to be elected");

        // The peer dies the moment it is elected, without publishing, declining, or ever heartbeating again.
        doomedClient.IsAbandoned = true;

        harness.PumpUntil(() => harness.Server.SessionCount == 0, NewfarmTestHarness.WaitTimeout, "the session held only by the dead elected peer to be evicted");
    }

    /// <summary>
    /// A live peer that arrives while a dead peer holds the election is elected once that election is withdrawn,
    /// rather than the session being handed straight back to the dead peer it was just taken from, so one dead
    /// peer that got there first cannot starve everyone who arrives after it.
    /// </summary>
    [Fact]
    public void ALiveLateArriverIsElectedOverADeadPeerAheadOfIt()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer deadClient = harness.CreatePeer();
        NewfarmTestPeer lateClient = harness.CreatePeer();

        AbandonHost(harness, host);

        deadClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => deadClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the first client to be elected");

        // The elected peer dies, and only then does the live peer arrive, so the session already belonged to a
        // dead peer before the live one ever asked for it.
        deadClient.IsAbandoned = true;

        lateClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => lateClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the live late arriver to be elected");

        string credential = "ROOM-RESCUED";

        lateClient.Client.PublishCredential(AdapterTag, credential);

        NewfarmTestPeer joiningClient = harness.CreatePeer();

        joiningClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => joiningClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "a joining client to be handed the live peer's credential");

        Assert.Equal(credential, joiningClient.ReceivedCredential!.Value.Credential);
        Assert.Equal(1, lateClient.ElectionCount);
    }

    /// <summary>
    /// A peer guessing at a session id gets nothing: not a queue place, not an election, not a credential. The id is
    /// the whole of a session's security, so the answer to one nobody was given has to be the same answer an expired
    /// session gives.
    /// </summary>
    [Fact]
    public void AGuessedSessionIdIsRefused()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer impostor = harness.CreatePeer();

        AbandonHost(harness, host);

        // One bit away from a real session, which is as close as guessing gets anyone.
        impostor.Client.AwaitSession(new NewfarmSessionIdentity(identity.SessionId ^ 1ul, epoch: 0));

        harness.PumpUntil(() => impostor.Refusals.Count > 0, NewfarmTestHarness.WaitTimeout, "the impostor to be refused");

        Assert.Contains(NewfarmRefusalReason.SessionNotFound, impostor.Refusals);
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

        peer.Client.AwaitSession(new NewfarmSessionIdentity(0xDEADBEEFCAFEF00D, epoch: 0));

        harness.PumpUntil(() => peer.Refusals.Count > 0, NewfarmTestHarness.WaitTimeout, "the peer to be refused");

        Assert.Contains(NewfarmRefusalReason.SessionNotFound, peer.Refusals);
    }

    /// <summary>
    /// A refusal is answered once, not at the heartbeat interval forever: it ends the pursuit it refuses, so the client goes idle
    /// and stops addressing the session newfarm said it does not hold.
    /// </summary>
    /// <remarks>Without the idle transition the waiter's heartbeats each earn another refusal, and a peer that asked one bad question is told the same answer every second until something else stops it.</remarks>
    [Fact]
    public void ARefusedPeerStopsAskingAndGoesIdle()
    {
        using NewfarmTestHarness harness = new();

        NewfarmTestPeer peer = harness.CreatePeer();

        peer.Client.AwaitSession(new NewfarmSessionIdentity(0xDEADBEEFCAFEF00D, epoch: 0));

        harness.PumpUntil(() => peer.Refusals.Count > 0, NewfarmTestHarness.WaitTimeout, "the peer to be refused");

        Assert.Equal(NewfarmClientMode.Idle, peer.Client.Mode);

        /* Long enough for several waiter heartbeats to have come due; an idle client sends none, so the one refusal it already
         * holds stays the only one. */
        int refusalCountAfterFirst = peer.Refusals.Count;
        harness.PumpFor(TimeSpan.FromMilliseconds(3500));

        Assert.Equal(refusalCountAfterFirst, peer.Refusals.Count);
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

        string liveCredential = "ROOM-LIVE";

        electedPeer.Client.PublishCredential(AdapterTag, liveCredential);

        harness.PumpUntil(() => otherPeer.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the other peer to learn the live credential");

        // The old host comes back believing it still holds epoch 1 and tries to point the session at its own room.
        host.IsAbandoned = false;
        host.Client.StartHosting(new NewfarmSessionIdentity(identity.SessionId, epoch: 1));
        host.Client.PublishCredential(AdapterTag, "ROOM-STALE");

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

        // Nothing that looks like a room code: newfarm does not read this, so a value with punctuation, spaces and
        // characters outside ASCII has to survive it exactly.
        string credential = "10.0.0.1:7777/ab cd-é";

        successor.Client.PublishCredential("allocation+jwt", credential);

        harness.PumpUntil(() => bystander.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the bystander to learn the credential");

        Assert.Equal(credential, bystander.ReceivedCredential!.Value.Credential);
        Assert.Equal("allocation+jwt", bystander.ReceivedCredential!.Value.AdapterTag);
    }

    /// <summary>
    /// A credential runs to the limit and no further, which is checked where the caller can still do something about
    /// it rather than by a directory quietly shortening it into a room nobody is in.
    /// </summary>
    [Fact]
    public void ACredentialPastTheLimitIsRefusedRatherThanShortened()
    {
        using NewfarmTestHarness harness = new();

        CreateSession(harness, out NewfarmTestPeer host);

        string longestAllowed = new('r', NewfarmWireFormat.MaximumTextLength);

        host.Client.PublishCredential(AdapterTag, longestAllowed);

        Assert.Throws<ArgumentException>(() => host.Client.PublishCredential(AdapterTag, longestAllowed + "r"));
        Assert.Throws<ArgumentException>(() => host.Client.PublishCredential(new string('t', NewfarmWireFormat.MaximumTextLength + 1), "ROOM-0001"));

        NewfarmTestPeer client = harness.CreatePeer();

        client.Client.AwaitSession(host.CreatedIdentity!.Value);

        harness.PumpUntil(() => client.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the client to be given the credential");

        Assert.Equal(longestAllowed, client.ReceivedCredential!.Value.Credential);
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

        string credential = "ROOM-0001";

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

        string credential = "ROOM-ELECTED";

        waiter.Client.PublishCredential(AdapterTag, credential);

        harness.PumpFor(TimeSpan.FromMilliseconds(300));

        NewfarmTestPeer lateClient = harness.CreatePeer();

        lateClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => lateClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "a late client to be given the elected peer's credential");

        Assert.Equal(credential, lateClient.ReceivedCredential!.Value.Credential);
    }

    /// <summary>
    /// The case a heartbeat cannot answer: the host is online and heartbeating, but its game is no longer hosting
    /// anything. The peers are the only ones who know, so their reports have to be able to override a live heartbeat.
    /// </summary>
    [Fact]
    public void AHostThatHeartbeatsButCannotHostIsStoodDown()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        string deadCredential = "ROOM-DEAD";

        host.Client.PublishCredential(AdapterTag, deadCredential);

        NewfarmTestPeer strandedClient = harness.CreatePeer();

        strandedClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => strandedClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the client to be given the credential");

        // The host keeps heartbeating throughout, and never answers a challenge, which is what a wedged game or a
        // host that lost its route to the relay looks like from here.
        for (int i = 0; i < 12; i++)
        {
            strandedClient.Client.ReportCredentialUnreachable();

            harness.PumpFor(TimeSpan.FromMilliseconds(500));

            if (strandedClient.ElectionCount > 0)
                break;
        }

        Assert.True(host.ChallengeCount > 0, "The host was never asked to prove it was hosting.");
        Assert.True(strandedClient.ElectionCount > 0, "The stranded client was never elected, so a heartbeating host stalled the session.");

        // The peer that lost the session is told so, rather than being left heartbeating at a directory that has
        // stopped listening to it and never hearing where everyone went.
        Assert.Equal(1, host.RevocationCount);
        Assert.Equal(NewfarmClientMode.Waiting, host.Client.Mode);
    }

    /// <summary>
    /// The other side of that: a host that answers the challenge keeps the session, so one peer with a broken link
    /// cannot take a working session away from everybody else.
    /// </summary>
    [Fact]
    public void AHostThatAnswersTheChallengeKeepsTheSession()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        string liveCredential = "ROOM-LIVE";

        host.ChallengeAnswerAdapterTag = AdapterTag;
        host.ChallengeAnswer = liveCredential;

        host.Client.PublishCredential(AdapterTag, liveCredential);

        NewfarmTestPeer complainingClient = harness.CreatePeer();

        complainingClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => complainingClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the client to be given the credential");

        for (int i = 0; i < 8; i++)
        {
            complainingClient.Client.ReportCredentialUnreachable();

            harness.PumpFor(TimeSpan.FromMilliseconds(500));
        }

        Assert.True(host.ChallengeCount > 0, "The host was never asked to prove it was hosting.");
        Assert.Equal(0, complainingClient.ElectionCount);
        Assert.Equal(1u, identity.Epoch);
        Assert.Equal(NewfarmClientMode.Hosting, host.Client.Mode);
    }

    /// <summary>
    /// The host is challenged on a schedule of newfarm's choosing, not on one set by however many peers happen to be
    /// reporting. A whole room's links dropping at once must not turn into a whole room's worth of demands on the one
    /// machine least able to spare the attention.
    /// </summary>
    [Fact]
    public void ManyPeersReportingDoNotAddUpToManyChallenges()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        string credential = "ROOM-BUSY";

        // The host answers every challenge, so it keeps the session and stays challengeable for the whole test.
        host.ChallengeAnswerAdapterTag = AdapterTag;
        host.ChallengeAnswer = credential;

        host.Client.PublishCredential(AdapterTag, credential);

        NewfarmTestPeer[] complainingClients = new NewfarmTestPeer[6];

        for (int i = 0; i < complainingClients.Length; i++)
        {
            complainingClients[i] = harness.CreatePeer();
            complainingClients[i].Client.AwaitSession(identity);
        }

        harness.PumpUntil(() => AllHaveCredential(complainingClients), NewfarmTestHarness.WaitTimeout, "every client to be given the credential");

        int reportCount = 0;

        long startedTimestamp = Stopwatch.GetTimestamp();

        for (int round = 0; round < 16; round++)
        {
            for (int i = 0; i < complainingClients.Length; i++)
            {
                complainingClients[i].Client.ReportCredentialUnreachable();

                reportCount++;
            }

            harness.PumpFor(TimeSpan.FromMilliseconds(150));
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedTimestamp);

        // One challenge per cooldown is the ceiling, whatever the peers do. The spare two cover the round in flight
        // when the window opened and the one that may start as it closes.
        int permittedChallenges = (int)(elapsed.TotalMilliseconds / harness.Server.Config.HostChallengeCooldownMilliseconds) + 2;

        // Without this the test could pass for the wrong reason, by standing the host down early and so leaving
        // nobody to challenge.
        Assert.Equal(0, TotalElections(complainingClients));
        Assert.True(host.ChallengeCount > 0, "The host was never challenged, so the limit was not what held the count down.");
        Assert.True(host.ChallengeCount <= permittedChallenges, $"[{reportCount}] reports over [{elapsed.TotalMilliseconds:0}]ms produced [{host.ChallengeCount}] challenges, more than the [{permittedChallenges}] the cooldown allows.");
        Assert.Equal(NewfarmClientMode.Hosting, host.Client.Mode);
    }

    /// <summary>
    /// A host that knows it has stopped hosting says so, and the session moves on at once rather than waiting out a
    /// heartbeat that is never going to lapse.
    /// </summary>
    [Fact]
    public void AHostThatGivesUpHostingHandsOverImmediately()
    {
        // A host timeout far longer than the test runs for, so an election here can only have come from the
        // surrender and never from the heartbeat lapsing.
        using NewfarmTestHarness harness = new(config => config.HostTimeoutMilliseconds = 30000);

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        host.Client.PublishCredential(AdapterTag, "ROOM-FIRST");

        NewfarmTestPeer successor = harness.CreatePeer();

        successor.Client.AwaitSession(identity);

        harness.PumpUntil(() => successor.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the successor to join the first room");

        host.Client.SurrenderHosting();

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        // The room died with the host, which is what sends a peer back to the directory.
        successor.Client.AwaitSession(identity);

        harness.PumpUntil(() => successor.ElectionCount > 0, TimeSpan.FromSeconds(2), "the successor to be elected on the surrender");

        string secondCredential = "ROOM-SECOND";

        successor.Client.PublishCredential(AdapterTag, secondCredential);

        harness.PumpUntil(() => host.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the peer that gave up hosting to be told where the session went");

        Assert.Equal(secondCredential, host.ReceivedCredential!.Value.Credential);
    }

    /// <summary>
    /// A peer that has said it cannot host is passed over when the next election runs, rather than being handed the
    /// same job again and again.
    /// </summary>
    [Fact]
    public void APeerThatDeclinedIsPassedOverByTheNextElection()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer decliningClient = harness.CreatePeer();

        AbandonHost(harness, host);

        decliningClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => decliningClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the only client to be elected");

        decliningClient.Client.DeclineElection();

        harness.PumpFor(TimeSpan.FromSeconds(1));

        Assert.Equal(1, decliningClient.ElectionCount);

        // A peer that can host arrives, and gets the job the declining one turned down.
        NewfarmTestPeer capableClient = harness.CreatePeer();

        capableClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => capableClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the capable client to be elected instead");

        string credential = "ROOM-CAPABLE";

        capableClient.Client.PublishCredential(AdapterTag, credential);

        harness.PumpUntil(() => decliningClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the declining peer to still be told where the session went");

        Assert.Equal(credential, decliningClient.ReceivedCredential!.Value.Credential);
        Assert.Equal(1, decliningClient.ElectionCount);
    }

    /// <summary>
    /// Newfarm says nothing at all to a datagram too short to have been padded. Answering one would make it a weapon
    /// against whoever's address was on the request, since the reply is several times the size of what provokes it.
    /// </summary>
    [Fact]
    public void AnUnpaddedRequestIsIgnoredSoNewfarmCannotAmplify()
    {
        using NewfarmTestHarness harness = new();

        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        IPEndPoint serverEndPoint = new(IPAddress.Loopback, harness.Port);

        // The whole of a CreateSession, which padded would be answered with a session id and an epoch.
        byte[] unpadded = [(byte)NewfarmPacketType.CreateSession];

        probe.SendTo(unpadded, serverEndPoint);

        Thread.Sleep(TimeSpan.FromMilliseconds(500));

        Assert.Equal(0, probe.Available);
        Assert.Equal(0, harness.Server.SessionCount);

        // Padded, the very same request is answered, so it is the size and nothing else that was refused.
        byte[] padded = new byte[NewfarmWireFormat.MinimumRequestSize];

        padded[0] = (byte)NewfarmPacketType.CreateSession;

        probe.SendTo(padded, serverEndPoint);

        long startedTimestamp = Stopwatch.GetTimestamp();

        while (probe.Available == 0 && Stopwatch.GetElapsedTime(startedTimestamp) < NewfarmTestHarness.WaitTimeout)
            Thread.Sleep(5);

        Assert.True(probe.Available > 0, "A padded request went unanswered, so the size rule is refusing legitimate traffic.");
        Assert.True(probe.Available <= NewfarmWireFormat.MinimumRequestSize, $"Newfarm answered [{probe.Available}] bytes to a [{padded.Length}] byte request, so it can still be made to amplify.");
    }

    /// <summary>
    /// The same, against the largest reply newfarm can be made to produce: a credential and a tag at their longest,
    /// in characters that take four bytes each. This is the case the padding size is derived from, so if the two ever
    /// drift apart it is this that catches it.
    /// </summary>
    [Fact]
    public void TheLargestPossibleReplyStillFitsInsideTheRequestItAnswers()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        // Four bytes each once encoded, which is the worst a value of this length can weigh.
        string longestCredential = string.Concat(Enumerable.Repeat("𝄞", NewfarmWireFormat.MaximumTextLength / 2));
        string longestAdapterTag = string.Concat(Enumerable.Repeat("𝄢", NewfarmWireFormat.MaximumTextLength / 2));

        host.Client.PublishCredential(longestAdapterTag, longestCredential);

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        byte[] request = new byte[NewfarmWireFormat.MinimumRequestSize];

        NewfarmWireFormat.WriteSessionEpoch(request, NewfarmPacketType.AwaitSession, identity.SessionId, identity.Epoch);

        probe.SendTo(request, new IPEndPoint(IPAddress.Loopback, harness.Port));

        long startedTimestamp = Stopwatch.GetTimestamp();

        while (probe.Available == 0 && Stopwatch.GetElapsedTime(startedTimestamp) < NewfarmTestHarness.WaitTimeout)
        {
            harness.PumpPeers();

            Thread.Sleep(5);
        }

        Assert.True(probe.Available > 0, "The request went unanswered, so this test is not measuring a reply at all.");
        Assert.True(probe.Available <= request.Length, $"The largest reply is [{probe.Available}] bytes against a [{request.Length}] byte request, so newfarm amplifies by [{(double)probe.Available / request.Length:0.00}] times.");
    }

    /// <summary>
    /// The limits are there for abuse and abuse alone. A great many peers sharing one address is ordinary: a school,
    /// an office, a carrier-grade NAT. Every one of them has to be served.
    /// </summary>
    [Fact]
    public void ManyPeersBehindOneAddressAreAllServed()
    {
        using NewfarmTestHarness harness = new();

        NewfarmTestPeer[] hosts = new NewfarmTestPeer[48];

        for (int i = 0; i < hosts.Length; i++)
        {
            hosts[i] = harness.CreatePeer();

            hosts[i].Client.CreateSession();
        }

        harness.PumpUntil(() => AllHaveIdentity(hosts), NewfarmTestHarness.WaitTimeout, "every peer sharing the address to be given a session");

        Assert.Equal(hosts.Length, harness.Server.SessionCount);

        for (int i = 0; i < hosts.Length; i++)
            Assert.Empty(hosts[i].Refusals);
    }

    /// <summary>
    /// One peer going as hard as a real one ever legitimately goes, for long enough to spend a burst allowance
    /// several times over, is never throttled.
    /// </summary>
    [Fact]
    public void APeerAtItsBusiestIsNeverThrottled()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer busyClient = harness.CreatePeer();

        busyClient.Client.AwaitSession(identity);

        // Heartbeats, request retries and reports all at once for two seconds. A real peer is quieter than this.
        for (int i = 0; i < 20; i++)
        {
            busyClient.Client.ReportCredentialUnreachable();

            harness.PumpFor(TimeSpan.FromMilliseconds(100));
        }

        Assert.Empty(busyClient.Refusals);

        // Still being served after all of that, rather than having been quietly cut off. Whether it ended up waiting
        // or hosting is beside the point here: what matters is that it was still being listened to.
        Assert.Equal(1, harness.Server.SessionCount);
        Assert.NotEqual(NewfarmClientMode.Idle, busyClient.Client.Mode);
    }

    /// <summary>
    /// A session already holding as many waiters as it is allowed turns the next arrival away, rather than growing
    /// without end for whoever keeps coming back.
    /// </summary>
    [Fact]
    public void ASessionFullOfWaitersRefusesTheNextArrival()
    {
        using NewfarmTestHarness harness = new(config => config.MaximumWaitersPerSession = 2);

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer firstClient = harness.CreatePeer();
        NewfarmTestPeer secondClient = harness.CreatePeer();
        NewfarmTestPeer thirdClient = harness.CreatePeer();

        AbandonHost(harness, host);

        firstClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => firstClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the first client to be elected");

        secondClient.Client.AwaitSession(identity);
        thirdClient.Client.AwaitSession(identity);

        harness.PumpFor(TimeSpan.FromMilliseconds(400));

        NewfarmTestPeer fourthClient = harness.CreatePeer();

        fourthClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => fourthClient.Refusals.Count > 0, NewfarmTestHarness.WaitTimeout, "the peer past the limit to be turned away");

        Assert.Contains(NewfarmRefusalReason.SessionFull, fourthClient.Refusals);

        // The peers already queued are untouched, which makes this a limit on arrivals rather than a cull.
        Assert.Empty(secondClient.Refusals);
        Assert.Empty(thirdClient.Refusals);
    }

    /// <summary>
    /// Returns whether every supplied peer has been given a session.
    /// </summary>
    /// <param name="peers">The peers to check.</param>
    /// <returns><see langword="true"/> when none of them are still waiting on one.</returns>
    private static bool AllHaveIdentity(NewfarmTestPeer[] peers)
    {
        for (int i = 0; i < peers.Length; i++)
        {
            if (peers[i].CreatedIdentity is null)
                return false;
        }

        return true;
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
    /// Returns whether every supplied peer has been handed a credential.
    /// </summary>
    /// <param name="peers">The peers to check.</param>
    /// <returns><see langword="true"/> when none of them are still waiting.</returns>
    private static bool AllHaveCredential(NewfarmTestPeer[] peers)
    {
        for (int i = 0; i < peers.Length; i++)
        {
            if (peers[i].ReceivedCredential is null)
                return false;
        }

        return true;
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
    private static void AssertCredential(NewfarmTestPeer peer, NewfarmTestPeer electedPeer, string credential)
    {
        if (peer == electedPeer)
            return;

        Assert.NotNull(peer.ReceivedCredential);
        Assert.Equal(credential, peer.ReceivedCredential!.Value.Credential);
        Assert.Equal(AdapterTag, peer.ReceivedCredential!.Value.AdapterTag);
    }
}
