using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
    /// A request to create a session that is repeated before its answer arrives, as a retry is over a slow link, opens a
    /// second session on the directory; the host keeps the one it was first given rather than swapping to the later one.
    /// </summary>
    /// <remarks>
    /// Swapping left the host heartbeating a session it had published nothing for, while its clients had been handed the
    /// one it published to, so the session they held went quiet and was taken off it.
    /// </remarks>
    [Fact]
    public void ARepeatedCreateRequestDoesNotSwapTheSessionTheHostWasGiven()
    {
        using NewfarmTestHarness harness = new();

        NewfarmTestPeer host = harness.CreatePeer();
        List<NewfarmSessionIdentity> createdIdentities = [];

        host.Client.SessionCreated += createdIdentities.Add;

        // The second call sends the request again before the first answer is read, which is what a retry does.
        host.Client.CreateSession();
        host.Client.CreateSession();

        harness.PumpUntil(() => harness.Server.SessionCount == 2, NewfarmTestHarness.WaitTimeout, "the directory to answer both requests");
        harness.PumpFor(TimeSpan.FromMilliseconds(300));

        Assert.Single(createdIdentities);
        Assert.Equal(createdIdentities[0].SessionId, host.Client.Identity.SessionId);
        Assert.Equal(NewfarmClientMode.Hosting, host.Client.Mode);
    }

    /// <summary>
    /// A peer that names the directory by host name finds it through DNS and obtains a session.
    /// </summary>
    [Fact]
    public void APeerNamingTheDirectoryByHostNameObtainsASession()
    {
        using NewfarmTestHarness harness = new();

        NewfarmTestPeer host = harness.CreatePeer("localhost");
        string? lookupFailure = null;
        host.Client.ServerLookupFailed += message => lookupFailure = message;

        host.Client.CreateSession();

        harness.PumpUntil(() => host.CreatedIdentity is not null, NewfarmTestHarness.WaitTimeout, "the session to be created at the looked up address");

        Assert.Null(lookupFailure);
        Assert.Equal(1, harness.Server.SessionCount);
    }

    /// <summary>
    /// A host name that does not resolve is reported, and nothing reaches the directory.
    /// </summary>
    [Fact]
    public void AHostNameThatDoesNotResolveIsReported()
    {
        using NewfarmTestHarness harness = new();

        // The .invalid top-level domain is reserved never to resolve.
        NewfarmTestPeer host = harness.CreatePeer("newfarm.invalid");
        string? lookupFailure = null;
        host.Client.ServerLookupFailed += message => lookupFailure = message;

        host.Client.CreateSession();

        harness.PumpUntil(() => lookupFailure is not null, NewfarmTestHarness.WaitTimeout, "the lookup to fail");

        Assert.Contains("newfarm.invalid", lookupFailure);
        Assert.Null(host.CreatedIdentity);
        Assert.Equal(0, harness.Server.SessionCount);
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
    /// A host that goes quiet past the timeout, for a long frame, a breakpoint or a suspended app, and heartbeats again
    /// before anybody took the session on, is the host again rather than a stranger heartbeating at a session it lost.
    /// </summary>
    [Fact]
    public void AHostThatWentQuietAndCameBackBeforeAnybodyTookOverKeepsTheSession()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        string credential = "ROOM-ORIGINAL";

        host.Client.PublishCredential(AdapterTag, credential);

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        AbandonHost(harness, host);

        host.IsAbandoned = false;

        harness.PumpFor(TimeSpan.FromMilliseconds(400));

        // A client that only lost its own link is sent straight back to the room, which only happens while a host is alive.
        NewfarmTestPeer blippedClient = harness.CreatePeer();

        blippedClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => blippedClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the blipped client to be sent back to the host's room");

        // Long enough for several sweeps, any of which would elect the client if the session were still hostless.
        harness.PumpFor(TimeSpan.FromMilliseconds(500));

        Assert.Equal(credential, blippedClient.ReceivedCredential!.Value.Credential);
        Assert.Equal(0, blippedClient.ElectionCount);
        Assert.Equal(0, host.RevocationCount);
        Assert.Equal(NewfarmClientMode.Hosting, host.Client.Mode);
    }

    /// <summary>
    /// A host that went quiet past the timeout and comes back after the session moved on is told it no longer hosts,
    /// and is given the room the session moved to, rather than left serving an empty room it believes is the session.
    /// </summary>
    [Fact]
    public void AHostThatWentQuietAndCameBackAfterTheSessionMovedOnIsStoodDownAndToldWhere()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        host.Client.PublishCredential(AdapterTag, "ROOM-ORIGINAL");

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        NewfarmTestPeer successor = harness.CreatePeer();

        AbandonHost(harness, host);

        successor.Client.AwaitSession(identity);

        harness.PumpUntil(() => successor.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the successor to be elected");

        string liveCredential = "ROOM-LIVE";

        successor.Client.PublishCredential(AdapterTag, liveCredential);

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        // The old host's frames resume, still believing it hosts epoch 1.
        host.IsAbandoned = false;

        harness.PumpUntil(() => host.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the old host to be told where the session went");

        Assert.Equal(1, host.RevocationCount);
        Assert.Equal(NewfarmClientMode.Waiting, host.Client.Mode);
        Assert.Equal(liveCredential, host.ReceivedCredential!.Value.Credential);
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
    /// A peer acts only on what the directory itself sends. A datagram from anywhere else naming the session, whose id
    /// every peer in it holds, neither stands the host down nor moves a client to another room.
    /// </summary>
    [Fact(Skip = "MIGR-3, held for a decision: filtering by sender costs either about 150 bytes of garbage per received datagram (ReceiveFrom) or a socket pinned to its local address across a network change (Connect). Remove the skip to see it fail.")]
    public void APeerActsOnlyOnWhatTheDirectoryItselfSends()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        string credential = "ROOM-REAL";

        host.Client.PublishCredential(AdapterTag, credential);

        NewfarmTestPeer client = harness.CreatePeer();

        client.Client.AwaitSession(identity);

        harness.PumpUntil(() => client.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the client to be handed the host's room");

        // Pointed at each peer rather than at the directory, from a port of its own on the directory's own address. The
        // epoch is no secret either: it starts at one and only ever counts up.
        using NewfarmRawPeer forgerAtHost = new(LocalPortOf(host.Client));
        using NewfarmRawPeer forgerAtClient = new(LocalPortOf(client.Client));

        forgerAtHost.SendSessionEpoch(NewfarmPacketType.HostingRevoked, identity.SessionId, identity.Epoch);
        forgerAtClient.SendCredential(NewfarmPacketType.CredentialAvailable, identity.SessionId, identity.Epoch + 1, AdapterTag, "ROOM-FORGED");

        harness.PumpFor(TimeSpan.FromMilliseconds(300));

        // One comparison, so a failure shows what both forgeries did.
        Assert.Equal((0, NewfarmClientMode.Hosting, credential), (host.RevocationCount, host.Client.Mode, client.ReceivedCredential!.Value.Credential));
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
    /// Saying the host cannot be reached while it is still alive buys no place in the election that follows its loss. A
    /// peer that arrives once the host is gone is elected ahead of one that only reported it early and kept heartbeating.
    /// </summary>
    /// <remarks>
    /// While the host lives, a peer that merely asks is deliberately not queued, but a report queued its sender anyway.
    /// A cheater could report once, heartbeat to keep its place, and be elected the moment the host timed out, ahead of
    /// every peer that waited for the host to actually be gone, and then carry the session on from a world of its own.
    /// </remarks>
    [Fact(Skip = "MIGR-4, held for a decision: not queueing a report while the host lives passes this, but strands the reporter whenever a stalled host's heartbeat wins it back, and a peer heartbeating at the rate limit still wins the election. Remove the skip to see it fail.")]
    public void AReportMadeWhileTheHostLivedBuysNoPlaceInTheElection()
    {
        using NewfarmTestHarness harness = new();

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        host.Client.PublishCredential(AdapterTag, "ROOM-LIVE");

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        NewfarmTestPeer honestClient = harness.CreatePeer();

        using NewfarmRawPeer cheater = new(harness.Port, IPAddress.Parse("127.0.0.2"));

        // The host dies, and in the same moment the cheater claims it cannot be reached, while the directory still has it alive.
        host.IsAbandoned = true;

        cheater.SendSessionEpoch(NewfarmPacketType.CredentialUnreachable, identity.SessionId, identity.Epoch);

        // One heartbeat keeps the cheater's place past the host timeout, and it says nothing more.
        harness.PumpFor(TimeSpan.FromMilliseconds(300));

        cheater.SendSessionEpoch(NewfarmPacketType.WaiterHeartbeat, identity.SessionId, identity.Epoch);

        // Past the host timeout, which is when an honest peer finds out its host is gone.
        harness.PumpFor(TimeSpan.FromMilliseconds(500));

        honestClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => honestClient.ElectionCount > 0, NewfarmTestHarness.WaitTimeout, "the honest client to be elected");

        Assert.False(cheater.TryReceiveType(NewfarmPacketType.ElectHost), "The peer that reported a live host was elected the moment it timed out, ahead of the peer that came once it was gone.");
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

        /* The successor never has to ask: the surrender leaves the session hostless, its own waiter heartbeats are
         * what make it known there, and the next sweep hands it the session. Needing to notice the dead room first and
         * come back with an await would be strictly slower, and a peer whose await had been lost would never have been
         * able to come back at all. */
        harness.PumpUntil(() => successor.ElectionCount > 0, TimeSpan.FromSeconds(2), "the successor to be elected on the surrender");

        string secondCredential = "ROOM-SECOND";

        successor.Client.PublishCredential(AdapterTag, secondCredential);

        harness.PumpUntil(() => host.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the peer that gave up hosting to be told where the session went");

        Assert.Equal(secondCredential, host.ReceivedCredential!.Value.Credential);
    }

    /// <summary>
    /// A peer heartbeating as a waiter at a session with no living host is adopted as one on the strength of the
    /// heartbeat, and can then be elected to carry the session on, rather than being left to heartbeat at a directory
    /// that no longer knows it exists.
    /// </summary>
    /// <remarks>
    /// The state under test is what a lost surrender or await leaves behind: the peer believes it is queued and
    /// heartbeats accordingly, the directory never heard the request, and without the adoption the two never converge.
    /// Every later election and credential fan-out passes the peer by, and a session whose only survivor is such a
    /// peer deadlocks with nobody to elect. A living host's session deliberately adopts nobody, since its unknown
    /// heartbeats are the ordinary chatter of playing peers.
    /// </remarks>
    [Fact]
    public void AStrandedSurvivorOfADeadHost_IsAdoptedFromItsHeartbeatAndElected()
    {
        using NewfarmTestHarness harness = new(config => config.HostTimeoutMilliseconds = 400);

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        host.Client.PublishCredential(AdapterTag, "ROOM-FIRST");

        NewfarmTestPeer survivor = harness.CreatePeer();

        survivor.Client.AwaitSession(identity);

        harness.PumpUntil(() => survivor.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the survivor to be handed the room");

        /* Handed the credential, the survivor is deliberately not queued, and its client keeps heartbeating as a
         * waiter, which is the standing of every playing peer. The host then dies without a word, so nothing but that
         * heartbeat can ever tell the directory the survivor exists. */
        host.Dispose();

        harness.PumpUntil(() => survivor.ElectionCount > 0, TimeSpan.FromSeconds(5), "the stranded survivor to be adopted from its heartbeat and elected to carry the session on");
    }

    /// <summary>
    /// Only the peer hosting a session can close it. The session id reaches every peer in the session, so a close
    /// newfarm took the sender's word for would let any one of them delete the session for everybody, and a client
    /// that merely quit would take the whole session down with it instead of leaving.
    /// </summary>
    [Fact]
    public void OnlyTheHostCanCloseASession()
    {
        // A host timeout far longer than the test runs for, so the host holds the session throughout and the only
        // thing that can account for the session going is a close newfarm honoured.
        using NewfarmTestHarness harness = new(config => config.HostTimeoutMilliseconds = 30000);

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        NewfarmTestPeer waiter = harness.CreatePeer();

        waiter.Client.AwaitSession(identity);

        harness.PumpFor(TimeSpan.FromMilliseconds(300));

        // The waiter quits, naming the epoch that is genuinely current, so its lack of standing is the only thing
        // left to refuse it on.
        waiter.Client.CloseSession();

        harness.PumpFor(TimeSpan.FromMilliseconds(400));

        Assert.Equal(1, harness.Server.SessionCount);

        // Not merely still counted but still whole: its host can publish into it and its peers are still told where
        // it lives.
        string credential = "ROOM-STILL-THERE";

        host.Client.PublishCredential(AdapterTag, credential);
        waiter.Client.AwaitSession(identity);

        harness.PumpUntil(() => waiter.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "the waiter to be served by the session it failed to close");

        Assert.Equal(credential, waiter.ReceivedCredential!.Value.Credential);

        // The peer that is actually hosting is obeyed.
        host.Client.CloseSession();

        harness.PumpUntil(() => harness.Server.SessionCount == 0, NewfarmTestHarness.WaitTimeout, "the host's own close to be honoured");
    }

    /// <summary>
    /// Sending from the host's address is not enough to speak for the host. A datagram that only carries the host's
    /// address and port, with the session id and the epoch every peer holds, cannot move the session to another room.
    /// </summary>
    /// <remarks>
    /// What the host alone may do, publish, surrender, close and heartbeat, is granted on the source endpoint and an epoch
    /// that starts at one, so a session member on a network that lets it forge source addresses could point everyone
    /// arriving at a room of its own choosing.
    /// </remarks>
    [Fact(Skip = "MIGR-6, held for a decision: closing it needs a host secret issued with the session and carried on every host request, which changes the protocol. Remove the skip to see it fail.")]
    public void ADatagramFromTheHostsAddressCannotSpeakForTheHost()
    {
        // A host timeout far longer than the test, so the directory holds the host throughout.
        using NewfarmTestHarness harness = new(config => config.HostTimeoutMilliseconds = 30000);

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        string credential = "ROOM-REAL";

        host.Client.PublishCredential(AdapterTag, credential);

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        // Standing in for a forged source address: the host's socket gives its port up so the forger's can send from it.
        int hostPort = LocalPortOf(host.Client);

        host.IsAbandoned = true;
        host.Dispose();

        using NewfarmRawPeer forger = new(harness.Port, IPAddress.Loopback, hostPort);

        forger.SendCredential(NewfarmPacketType.PublishCredential, identity.SessionId, identity.Epoch, AdapterTag, "ROOM-FORGED");

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        NewfarmTestPeer lateClient = harness.CreatePeer();

        lateClient.Client.AwaitSession(identity);

        harness.PumpUntil(() => lateClient.ReceivedCredential is not null, NewfarmTestHarness.WaitTimeout, "a late client to be told where the session lives");

        Assert.Equal(credential, lateClient.ReceivedCredential!.Value.Credential);
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
    /// One machine asking for session after session cannot take every place the directory has, so a host anywhere else is
    /// still given one.
    /// </summary>
    /// <remarks>
    /// Opening a session costs a single datagram, and a session nobody heartbeats is held for the hostless grace, so one
    /// address asking over and over filled the directory, held it full, and every other host was told it was at capacity.
    /// </remarks>
    [Fact]
    public void OneAddressCannotTakeEveryPlaceTheDirectoryHas()
    {
        // Room for more sessions than one address may hold, so the places left over are the ones it could not take.
        const int MaximumConcurrentSessions = 1100;

        using NewfarmTestHarness harness = new(config => config.MaximumConcurrentSessions = MaximumConcurrentSessions);

        IPAddress attackerAddress = IPAddress.Parse("127.0.0.2");
        int attackerSessionCount = 0;

        // A fresh port every few requests, as an attacker would, so no one peer's allowance is what stops it.
        for (int i = 0; i < 20; i++)
        {
            using NewfarmRawPeer attacker = new(harness.Port, attackerAddress);

            attackerSessionCount += attacker.CreateSessions(sessionCount: 60).Count;
        }

        NewfarmTestPeer host = harness.CreatePeer();

        host.Client.CreateSession();

        harness.PumpUntil(() => host.CreatedIdentity is not null || host.Refusals.Count > 0, NewfarmTestHarness.WaitTimeout, "the host at another address to be answered");

        Assert.Empty(host.Refusals);
        Assert.NotNull(host.CreatedIdentity);
        Assert.True(attackerSessionCount < MaximumConcurrentSessions, $"One address was given [{attackerSessionCount}] sessions, every place the directory had.");
    }

    /// <summary>
    /// A directory whose table of sending addresses is full still answers an address it has never heard from.
    /// </summary>
    /// <remarks>
    /// Every address that sends anything holds a place in the limiter's table until it has been quiet for a while, and a
    /// full table turned every new address away in silence. Source addresses cost a spoofing attacker nothing, so a trickle
    /// of datagrams from invented ones kept the table full and locked every new host and waiter out of the directory.
    /// </remarks>
    [Fact]
    public void AFullTableOfSendersDoesNotLockANewAddressOut()
    {
        const int MaximumTrackedAddresses = 8;

        using NewfarmTestHarness harness = new(config => config.MaximumTrackedAddresses = MaximumTrackedAddresses);

        // Each loopback address stands in for a source address an attacker invented. Any request at all takes a place, and
        // one that is answered shows the directory took it.
        for (int i = 0; i < MaximumTrackedAddresses; i++)
        {
            using NewfarmRawPeer spoofedSender = new(harness.Port, IPAddress.Parse($"127.0.0.{i + 2}"));

            Assert.Single(spoofedSender.CreateSessions(sessionCount: 1));
        }

        NewfarmTestPeer host = harness.CreatePeer();

        host.Client.CreateSession();

        // Several retries' worth, any of which would be answered by a directory still listening to new addresses.
        harness.PumpFor(TimeSpan.FromMilliseconds(1500));

        Assert.NotNull(host.CreatedIdentity);
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
    /// A report that the host cannot be reached is held to the same limit on waiters as an ask, so a sender with ports to
    /// spare cannot queue itself past it.
    /// </summary>
    /// <remarks>
    /// A report queues its sender, and it skipped the limit an ask is held to. Every queued peer is sent a keep-alive a
    /// second and a copy of every credential published, to wherever the report claimed to come from, so the queue had no
    /// bound and neither did what the directory could be made to send.
    /// </remarks>
    [Fact]
    public void AnUnreachableReportCannotQueueAPeerPastTheWaiterLimit()
    {
        const int MaximumWaitersPerSession = 2;

        using NewfarmTestHarness harness = new(config => config.MaximumWaitersPerSession = MaximumWaitersPerSession);

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        host.Client.PublishCredential(AdapterTag, "ROOM-LIVE");

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        // A port of its own for every report, which costs the sender nothing.
        NewfarmRawPeer[] reporters = new NewfarmRawPeer[8];

        for (int i = 0; i < reporters.Length; i++)
        {
            reporters[i] = new NewfarmRawPeer(harness.Port, IPAddress.Parse("127.0.0.2"));
            reporters[i].SendSessionEpoch(NewfarmPacketType.CredentialUnreachable, identity.SessionId, identity.Epoch);
        }

        harness.PumpFor(TimeSpan.FromMilliseconds(300));

        int queuedReporterCount = 0;

        for (int i = 0; i < reporters.Length; i++)
        {
            if (reporters[i].TryReceiveType(NewfarmPacketType.Waiting))
                queuedReporterCount++;

            reporters[i].Dispose();
        }

        Assert.True(queuedReporterCount <= MaximumWaitersPerSession, $"[{queuedReporterCount}] reporters were queued against a limit of [{MaximumWaitersPerSession}].");
    }

    /// <summary>
    /// A report is held only while the peer that made it is waiting, so the reports a session holds are bounded by its
    /// waiters, and a peer that has stopped waiting cannot keep the host under challenge.
    /// </summary>
    /// <remarks>
    /// Reports were kept per sender until the next credential was published, whatever became of the sender, so a peer
    /// that reported and then asked again, which takes it back out of the queue, left a report behind from every port it
    /// used, and the session's reports grew without bound. The challenge they set off is what shows one was left.
    /// </remarks>
    [Fact]
    public void AnUnreachableReportIsForgottenWithItsReporter()
    {
        // A slow sweep, so nothing is decided between a report and the ask that follows it.
        using NewfarmTestHarness harness = new(config => config.SweepIntervalMilliseconds = 2000);

        NewfarmSessionIdentity identity = CreateSession(harness, out NewfarmTestPeer host);

        host.Client.PublishCredential(AdapterTag, "ROOM-LIVE");

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        // Each port reports and then asks, which hands it the live room and takes it out of the queue again.
        for (int i = 0; i < 8; i++)
        {
            using NewfarmRawPeer reporter = new(harness.Port, IPAddress.Parse("127.0.0.2"));

            reporter.SendSessionEpoch(NewfarmPacketType.CredentialUnreachable, identity.SessionId, identity.Epoch);
            reporter.SendSessionEpoch(NewfarmPacketType.AwaitSession, identity.SessionId, identity.Epoch);
        }

        // Past the next sweep, which challenges the host if any report is still held.
        harness.PumpFor(TimeSpan.FromMilliseconds(2500));

        Assert.Equal(0, host.ChallengeCount);
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
    /// Returns the port a client sends from, which an attacker reads off the wire and a test has to ask the socket for.
    /// </summary>
    /// <param name="newfarmClient">The client whose port is wanted, which has sent at least once.</param>
    /// <returns>The port its socket is bound to.</returns>
    private static int LocalPortOf(NewfarmClient newfarmClient)
    {
        Socket socket = (Socket)typeof(NewfarmClient).GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(newfarmClient)!;

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
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
