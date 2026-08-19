using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Newfarm.Client;
using Newfarm.Server;
using Newfarm.Wire;

namespace Newfarm.Tests;

/// <summary>
/// Runs a real <see cref="NewfarmServer"/> on a loopback port and drives real <see cref="NewfarmClient"/> peers
/// against it, so every test exercises the wire format and the socket rather than a stand-in.
/// </summary>
internal sealed class NewfarmTestHarness : IDisposable
{
    /// <summary>
    /// How long a wait for an expected outcome runs before it is treated as a failure.
    /// </summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The server under test.
    /// </summary>
    public NewfarmServer Server { get; }

    /// <summary>
    /// The port the server bound, which the peers send to.
    /// </summary>
    public int Port => Server.BoundPort;

    /// <summary>
    /// Stops the server's loop on disposal.
    /// </summary>
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>
    /// The thread the server's loop runs on, so the test thread stays free to drive peers.
    /// </summary>
    private readonly Thread _serverThread;

    /// <summary>
    /// Every peer created through <see cref="CreatePeer"/>, disposed with the harness.
    /// </summary>
    private readonly List<NewfarmTestPeer> _peers = [];

    /// <summary>
    /// Starts a server with timings tightened for tests, and waits for it to bind.
    /// </summary>
    /// <param name="configure">Applied to the configuration before the server is constructed.</param>
    public NewfarmTestHarness(Action<NewfarmServerConfig>? configure = null)
    {
        NewfarmServerConfig config = new()
        {
            Port = 0,
            HostTimeoutMilliseconds = 600,
            WaiterTimeoutMilliseconds = 600,
            ElectionDeadlineMilliseconds = 1500,
            CredentialGraceMilliseconds = 3000,
            HostlessGraceMilliseconds = 30000,
            HostChallengeIntervalMilliseconds = 300,
            WaitingKeepAliveIntervalMilliseconds = 150,
            SweepIntervalMilliseconds = 50,
        };

        configure?.Invoke(config);

        Server = new NewfarmServer(config);

        _serverThread = new Thread(() => Server.Run(_cancellation.Token))
        {
            IsBackground = true,
            Name = "Newfarm-Test-Server",
        };

        _serverThread.Start();

        WaitUntil(() => Server.BoundPort != 0, WaitTimeout, "the server to bind");
    }

    /// <summary>
    /// Creates a peer pointed at the server.
    /// </summary>
    /// <returns>The peer, which the harness disposes.</returns>
    public NewfarmTestPeer CreatePeer()
    {
        NewfarmClientConfig config = new(new IPEndPoint(IPAddress.Loopback, Port))
        {
            HostHeartbeatIntervalMilliseconds = 100,
            WaiterHeartbeatIntervalMilliseconds = 100,
            RequestRetryIntervalMilliseconds = 200,
        };

        NewfarmTestPeer peer = new(config);

        _peers.Add(peer);

        return peer;
    }

    /// <summary>
    /// Polls every peer the harness owns until the condition holds, so peers keep heartbeating while a test waits.
    /// </summary>
    /// <param name="condition">The condition being waited for.</param>
    /// <param name="timeout">How long to wait before failing.</param>
    /// <param name="description">Names what was being waited for, used in the failure message.</param>
    public void PumpUntil(Func<bool> condition, TimeSpan timeout, string description)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(startedTimestamp) < timeout)
        {
            PumpPeers();

            if (condition())
                return;

            Thread.Sleep(5);
        }

        PumpPeers();

        if (!condition())
            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for {description}.");
    }

    /// <summary>
    /// Polls every peer for the supplied duration without asserting anything, used when a test needs time to pass
    /// while peers stay alive.
    /// </summary>
    /// <param name="duration">How long to keep polling.</param>
    public void PumpFor(TimeSpan duration)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(startedTimestamp) < duration)
        {
            PumpPeers();

            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// Polls every peer once.
    /// </summary>
    public void PumpPeers()
    {
        for (int i = 0; i < _peers.Count; i++)
        {
            if (_peers[i].IsAbandoned)
                continue;

            _peers[i].Client.Poll();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cancellation.Cancel();

        _serverThread.Join(TimeSpan.FromSeconds(5));

        Server.Dispose();

        for (int i = 0; i < _peers.Count; i++)
            _peers[i].Dispose();

        _cancellation.Dispose();
    }

    /// <summary>
    /// Spins until a condition holds, without polling peers, used before any peer exists.
    /// </summary>
    /// <param name="condition">The condition being waited for.</param>
    /// <param name="timeout">How long to wait before failing.</param>
    /// <param name="description">Names what was being waited for, used in the failure message.</param>
    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string description)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(startedTimestamp) < timeout)
        {
            if (condition())
                return;

            Thread.Sleep(5);
        }

        if (!condition())
            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for {description}.");
    }
}

/// <summary>
/// One peer in a test, recording everything newfarm told it so a test can assert on the sequence.
/// </summary>
internal sealed class NewfarmTestPeer : IDisposable
{
    /// <summary>
    /// The client under test.
    /// </summary>
    public NewfarmClient Client { get; }

    /// <summary>
    /// Set by a test to stop the harness polling this peer, which is how a peer is made to look dead without
    /// disposing the socket it would need if it came back.
    /// </summary>
    public bool IsAbandoned { get; set; }

    /// <summary>
    /// The identity newfarm issued, once it has.
    /// </summary>
    public NewfarmSessionIdentity? CreatedIdentity { get; private set; }

    /// <summary>
    /// How many times this peer has been told to host.
    /// </summary>
    public int ElectionCount { get; private set; }

    /// <summary>
    /// How many times an election of this peer has been withdrawn.
    /// </summary>
    public int AbortCount { get; private set; }

    /// <summary>
    /// The credential this peer was last handed, if any.
    /// </summary>
    public NewfarmCredential? ReceivedCredential { get; private set; }

    /// <summary>
    /// Every refusal newfarm sent this peer.
    /// </summary>
    public List<NewfarmPacketType> Refusals { get; } = [];

    /// <summary>
    /// How many times this peer has been asked to prove it is still hosting.
    /// </summary>
    public int ChallengeCount { get; private set; }

    /// <summary>
    /// Set by a test to have the peer answer a challenge by republishing, standing in for a host whose room is
    /// genuinely still there.
    /// </summary>
    public byte[]? ChallengeAnswer { get; set; }

    /// <summary>
    /// The service tag the peer answers a challenge under.
    /// </summary>
    public string ChallengeAnswerAdapterTag { get; set; } = string.Empty;

    /// <summary>
    /// Creates a peer and subscribes to everything its client reports.
    /// </summary>
    /// <param name="config">The configuration naming the directory to talk to.</param>
    public NewfarmTestPeer(NewfarmClientConfig config)
    {
        Client = new NewfarmClient(config);

        Client.SessionCreated += OnSessionCreated;
        Client.ElectedToHost += OnElectedToHost;
        Client.ElectionAborted += OnElectionAborted;
        Client.CredentialAvailable += OnCredentialAvailable;
        Client.HostingChallenged += OnHostingChallenged;
        Client.Refused += OnRefused;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Client.Dispose();
    }

    /// <summary>
    /// Records the identity newfarm issued.
    /// </summary>
    /// <param name="identity">The identity issued.</param>
    private void OnSessionCreated(NewfarmSessionIdentity identity)
    {
        CreatedIdentity = identity;
    }

    /// <summary>
    /// Records an instruction to host.
    /// </summary>
    /// <param name="epoch">The epoch elected for.</param>
    private void OnElectedToHost(uint epoch)
    {
        ElectionCount++;
    }

    /// <summary>
    /// Records a withdrawn election.
    /// </summary>
    /// <param name="epoch">The epoch in force when it was withdrawn.</param>
    private void OnElectionAborted(uint epoch)
    {
        AbortCount++;
    }

    /// <summary>
    /// Records the credential the session moved to.
    /// </summary>
    /// <param name="credential">Where the session now lives.</param>
    private void OnCredentialAvailable(NewfarmCredential credential)
    {
        ReceivedCredential = credential;
    }

    /// <summary>
    /// Records a demand to prove hosting, and answers it when the test has given it something to answer with.
    /// </summary>
    /// <param name="epoch">The epoch the session is being hosted for.</param>
    private void OnHostingChallenged(uint epoch)
    {
        ChallengeCount++;

        if (ChallengeAnswer is null)
            return;

        Client.PublishCredential(ChallengeAnswerAdapterTag, ChallengeAnswer);
    }

    /// <summary>
    /// Records a refusal.
    /// </summary>
    /// <param name="newfarmPacketType">The refusal newfarm sent.</param>
    private void OnRefused(NewfarmPacketType newfarmPacketType)
    {
        Refusals.Add(newfarmPacketType);
    }
}
