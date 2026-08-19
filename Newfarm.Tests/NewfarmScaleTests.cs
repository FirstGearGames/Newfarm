using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newfarm.Server;
using Newfarm.Wire;
using Xunit.Abstractions;

namespace Newfarm.Tests;

/// <summary>
/// Measures a directory carrying far more than one game's worth of sessions. Skipped unless <c>NEWFARM_SCALE=1</c> is set,
/// because these run for minutes and hold thousands of sessions, which is not what a normal suite run is for.
/// </summary>
/// <remarks>
/// The property under test is not a number, it is that the loop keeps up. Newfarm is single threaded on purpose: one loop
/// receives datagrams and sweeps every session, so any cost that grows with the number of sessions is paid out of the time
/// it has to answer peers. What these measure is what a peer would actually notice, the time to get an answer, while the
/// sweep has thousands of sessions to walk.
/// </remarks>
public sealed class NewfarmScaleTests
{
    /// <summary>
    /// Set to 1 to run these.
    /// </summary>
    private const string ScaleEnvironmentVariable = "NEWFARM_SCALE";

    /// <summary>
    /// How many sessions to hold, unless <c>NEWFARM_SCALE_SESSIONS</c> says otherwise.
    /// </summary>
    private const int DefaultSessionCount = 10000;

    /// <summary>
    /// Where the measurements are written.
    /// </summary>
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Creates the suite.
    /// </summary>
    /// <param name="output">Where the measurements are written.</param>
    public NewfarmScaleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ADirectoryHoldingThousandsOfSessionsStillAnswersPromptly()
    {
        if (!IsScaleRunRequested())
            return;

        int sessionCount = ReadSessionCount();

        using NewfarmTestHarness harness = new(ConfigureForScale);

        using NewfarmRawPeer creator = new(harness.Port);

        long memoryBeforeBytes = GC.GetTotalMemory(forceFullCollection: true);

        long startedTimestamp = Stopwatch.GetTimestamp();

        List<ulong> sessionIds = creator.CreateSessions(sessionCount);

        TimeSpan creationElapsed = Stopwatch.GetElapsedTime(startedTimestamp);

        Assert.Equal(sessionCount, sessionIds.Count);
        Assert.Equal(sessionCount, harness.Server.SessionCount);

        long memoryAfterBytes = GC.GetTotalMemory(forceFullCollection: true);

        _output.WriteLine($"Created [{sessionCount}] sessions in [{creationElapsed.TotalMilliseconds:0}]ms, [{sessionCount / creationElapsed.TotalSeconds:0}] a second.");
        _output.WriteLine($"Held in [{(memoryAfterBytes - memoryBeforeBytes) / 1024.0 / 1024.0:0.0}]MB, [{(memoryAfterBytes - memoryBeforeBytes) / (double)sessionCount:0}] bytes a session.");

        /* Every one of those sessions is now swept on a fixed period, on the same thread that answers peers. A probe
           measures what a peer would actually feel while that goes on. */

        using NewfarmRawPeer probe = new(harness.Port);

        List<double> roundTripMilliseconds = probe.MeasureRoundTrips(sessionIds[sessionIds.Count / 2], sampleCount: 200);

        roundTripMilliseconds.Sort();

        double medianMilliseconds = roundTripMilliseconds[roundTripMilliseconds.Count / 2];
        double worstMilliseconds = roundTripMilliseconds[roundTripMilliseconds.Count - 1];
        double ninetyNinthMilliseconds = roundTripMilliseconds[(int)(roundTripMilliseconds.Count * 0.99)];

        _output.WriteLine($"Round trip with [{sessionCount}] sessions held: median [{medianMilliseconds:0.00}]ms, 99th [{ninetyNinthMilliseconds:0.00}]ms, worst [{worstMilliseconds:0.00}]ms.");

        Assert.Equal(200, roundTripMilliseconds.Count);

        // A sweep the loop cannot afford shows up here and nowhere else: the answer waits behind it. The worst case is
        // allowed a sweep interval's worth of waiting, since a request can arrive the moment one begins.
        Assert.True(medianMilliseconds < 5.0, $"The median answer took [{medianMilliseconds:0.00}]ms with [{sessionCount}] sessions held, so the sweep is eating the loop.");
        Assert.True(worstMilliseconds < harness.Server.Config.SweepIntervalMilliseconds * 2.0, $"The worst answer took [{worstMilliseconds:0.00}]ms, which is more than two sweeps.");
    }

    [Fact]
    public void ADirectoryUnderSustainedHeartbeatsLosesNoSessions()
    {
        if (!IsScaleRunRequested())
            return;

        int sessionCount = ReadSessionCount();

        using NewfarmTestHarness harness = new(ConfigureForScale);

        using NewfarmRawPeer host = new(harness.Port);

        List<ulong> sessionIds = host.CreateSessions(sessionCount);

        Assert.Equal(sessionCount, harness.Server.SessionCount);

        /* Every session heartbeated on the cadence a real host uses, for long enough that any session whose heartbeat
           was being dropped would lapse and be swept away. */

        TimeSpan runFor = TimeSpan.FromSeconds(30);

        long startedTimestamp = Stopwatch.GetTimestamp();
        long heartbeatCount = 0;

        while (Stopwatch.GetElapsedTime(startedTimestamp) < runFor)
        {
            for (int i = 0; i < sessionIds.Count; i++)
            {
                host.SendSessionEpoch(NewfarmPacketType.HostHeartbeat, sessionIds[i], epoch: 1);

                heartbeatCount++;
            }

            Thread.Sleep(1000);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedTimestamp);

        _output.WriteLine($"Sent [{heartbeatCount}] heartbeats over [{elapsed.TotalSeconds:0.0}]s, [{heartbeatCount / elapsed.TotalSeconds:0}] a second.");
        _output.WriteLine($"Sessions held after the run: [{harness.Server.SessionCount}] of [{sessionCount}].");

        // A dropped heartbeat is a lost session: the host looks gone, and with nobody waiting the session is forgotten.
        Assert.Equal(sessionCount, harness.Server.SessionCount);
    }

    /// <summary>
    /// Widens the limits for a run where one machine stands in for thousands of players, and leaves the timings alone so
    /// what is measured is the directory a deployment would actually run.
    /// </summary>
    /// <param name="config">The configuration to widen.</param>
    private static void ConfigureForScale(NewfarmServerConfig config)
    {
        config.HostTimeoutMilliseconds = 5000;
        config.WaiterTimeoutMilliseconds = 5000;
        config.SweepIntervalMilliseconds = 250;

        // The point of a scale run is to find where the loop gives out, which cannot be measured through a cap that
        // stops it first.
        config.MaximumConcurrentSessions = NewfarmServerConfig.UnlimitedConcurrentSessions;

        // One socket here is standing in for one player each, so the per-peer and per-address limits are the wrong shape
        // for it. They are measured for what they are in the ordinary suite.
        config.MaximumRequestsPerSecondPerPeer = uint.MaxValue / 2;
        config.RequestBurstPerPeer = uint.MaxValue / 2;
        config.MaximumRequestsPerSecondPerAddress = uint.MaxValue / 2;
        config.RequestBurstPerAddress = uint.MaxValue / 2;
    }

    /// <summary>
    /// Returns whether the caller asked for a scale run.
    /// </summary>
    /// <returns><see langword="true"/> when the environment variable is set to 1.</returns>
    private bool IsScaleRunRequested()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(ScaleEnvironmentVariable), "1", StringComparison.Ordinal))
            return true;

        _output.WriteLine($"Skipped: set {ScaleEnvironmentVariable}=1 to run the scale measurements.");

        return false;
    }

    /// <summary>
    /// Reads how many sessions to hold.
    /// </summary>
    /// <returns>The configured count, or <see cref="DefaultSessionCount"/>.</returns>
    private static int ReadSessionCount()
    {
        string? configured = Environment.GetEnvironmentVariable("NEWFARM_SCALE_SESSIONS");

        if (configured is not null && int.TryParse(configured, out int sessionCount) && sessionCount > 0)
            return sessionCount;

        return DefaultSessionCount;
    }
}

/// <summary>
/// Speaks the wire format over one socket, without the bookkeeping a <see cref="Newfarm.Client.NewfarmClient"/> keeps per
/// session, so a single peer can stand in for thousands of them.
/// </summary>
internal sealed class NewfarmRawPeer : IDisposable
{
    /// <summary>
    /// The directory being driven.
    /// </summary>
    private readonly IPEndPoint _serverEndPoint;

    /// <summary>
    /// The socket every request goes out of.
    /// </summary>
    private readonly Socket _socket;

    /// <summary>
    /// The send buffer, reused for every request.
    /// </summary>
    private readonly byte[] _sendBuffer = new byte[NewfarmWireFormat.MaximumDatagramSize];

    /// <summary>
    /// The receive buffer, reused for every reply.
    /// </summary>
    private readonly byte[] _receiveBuffer = new byte[NewfarmWireFormat.MaximumDatagramSize];

    /// <summary>
    /// Creates a peer pointed at a directory.
    /// </summary>
    /// <param name="serverPort">The port the directory is bound to.</param>
    public NewfarmRawPeer(int serverPort)
    {
        _serverEndPoint = new IPEndPoint(IPAddress.Loopback, serverPort);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _socket.ReceiveTimeout = 5000;

        // Deep enough that a burst of replies is not dropped by the socket while this peer is still sending.
        _socket.ReceiveBufferSize = 8 * 1024 * 1024;
        _socket.SendBufferSize = 8 * 1024 * 1024;
    }

    /// <summary>
    /// Opens sessions one after another, waiting for each identity before asking for the next.
    /// </summary>
    /// <param name="sessionCount">How many to open.</param>
    /// <returns>The identities that were issued.</returns>
    public List<ulong> CreateSessions(int sessionCount)
    {
        List<ulong> sessionIds = new(sessionCount);

        for (int i = 0; i < sessionCount; i++)
        {
            int length = NewfarmWireFormat.WriteType(_sendBuffer, NewfarmPacketType.CreateSession);

            Send(length);

            if (TryReceive(out NewfarmPacketType newfarmPacketType, out ulong sessionId, out uint _) && newfarmPacketType is NewfarmPacketType.SessionCreated)
                sessionIds.Add(sessionId);
        }

        return sessionIds;
    }

    /// <summary>
    /// Times how long the directory takes to answer, over and over.
    /// </summary>
    /// <param name="sessionId">The session to ask about.</param>
    /// <param name="sampleCount">How many round trips to time.</param>
    /// <returns>Every round trip, in milliseconds.</returns>
    public List<double> MeasureRoundTrips(ulong sessionId, int sampleCount)
    {
        List<double> roundTripMilliseconds = new(sampleCount);

        for (int i = 0; i < sampleCount; i++)
        {
            long startedTimestamp = Stopwatch.GetTimestamp();

            SendSessionEpoch(NewfarmPacketType.AwaitSession, sessionId, epoch: 1);

            if (TryReceive(out NewfarmPacketType _, out ulong _, out uint _))
                roundTripMilliseconds.Add(Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);

            Thread.Sleep(5);
        }

        return roundTripMilliseconds;
    }

    /// <summary>
    /// Sends a message carrying a session id and an epoch.
    /// </summary>
    /// <param name="newfarmPacketType">The message to send.</param>
    /// <param name="sessionId">The session it concerns.</param>
    /// <param name="epoch">The epoch it concerns.</param>
    public void SendSessionEpoch(NewfarmPacketType newfarmPacketType, ulong sessionId, uint epoch)
    {
        int length = NewfarmWireFormat.WriteSessionEpoch(_sendBuffer, newfarmPacketType, sessionId, epoch);

        Send(length);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _socket.Dispose();
    }

    /// <summary>
    /// Pads and sends whatever was written.
    /// </summary>
    /// <param name="length">How many bytes the message came to.</param>
    private void Send(int length)
    {
        _socket.SendTo(_sendBuffer, 0, NewfarmWireFormat.PadRequest(_sendBuffer, length), SocketFlags.None, _serverEndPoint);
    }

    /// <summary>
    /// Reads one reply.
    /// </summary>
    /// <param name="newfarmPacketType">When this returns <see langword="true"/>, the type that was read.</param>
    /// <param name="sessionId">When this returns <see langword="true"/>, the session the reply concerns.</param>
    /// <param name="epoch">When this returns <see langword="true"/>, the epoch the reply carried.</param>
    /// <returns><see langword="true"/> when a reply arrived before the socket timed out.</returns>
    private bool TryReceive(out NewfarmPacketType newfarmPacketType, out ulong sessionId, out uint epoch)
    {
        newfarmPacketType = default;
        sessionId = 0;
        epoch = 0;

        try
        {
            int receivedLength = _socket.Receive(_receiveBuffer);

            if (receivedLength <= 0)
                return false;

            ReadOnlySpan<byte> datagram = new(_receiveBuffer, 0, receivedLength);

            NewfarmWireFormat.TryReadType(datagram, out newfarmPacketType);

            return NewfarmWireFormat.TryReadSessionEpoch(datagram, out sessionId, out epoch);
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
