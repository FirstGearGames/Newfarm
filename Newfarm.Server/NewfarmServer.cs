using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newfarm.Wire;

namespace Newfarm.Server;

/// <summary>
/// The newfarm directory. Holds a session per hosting peer, hands the session's current room credential to the peers
/// that ask for it, and elects a replacement host when the current one goes quiet.
/// </summary>
/// <remarks>
/// Single threaded by design: one loop receives datagrams and sweeps sessions, so nothing in the registry needs
/// synchronising. The receive is bounded by <see cref="NewfarmServerConfig.SweepIntervalMilliseconds"/>, which makes
/// the socket timeout double as the loop's tick.
/// </remarks>
public sealed class NewfarmServer : IDisposable
{
    /// <summary>
    /// Raised for anything an operator would want in a log. Left unsubscribed the server is silent.
    /// </summary>
    public event NewfarmLogHandler? Logged;

    /// <summary>
    /// The configuration the server was constructed with.
    /// </summary>
    public NewfarmServerConfig Config { get; }

    /// <summary>
    /// The number of sessions currently held.
    /// </summary>
    public int SessionCount => _registry.Count;

    /// <summary>
    /// The port the socket is bound to, which is resolved after <see cref="Run"/> binds and differs from
    /// <see cref="NewfarmServerConfig.Port"/> only when that was left at zero.
    /// </summary>
    public int BoundPort { get; private set; }

    /// <summary>
    /// Holds every session and owns every decision about them.
    /// </summary>
    private readonly NewfarmSessionRegistry _registry;

    /// <summary>
    /// Decides which datagrams are worth acting on, so no one peer or machine can crowd the directory out.
    /// </summary>
    private readonly NewfarmRequestLimiter _limiter;

    /// <summary>
    /// Reused across sweeps and requests to collect what should be sent, so a busy tick allocates nothing.
    /// </summary>
    private readonly List<NewfarmNotification> _notifications = [];

    /// <summary>
    /// The receive buffer, sized to <see cref="NewfarmWireFormat.MaximumDatagramSize"/>.
    /// </summary>
    private readonly byte[] _receiveBuffer = new byte[NewfarmWireFormat.MaximumDatagramSize];

    /// <summary>
    /// The send buffer. Only ever used between building a message and handing it to the socket.
    /// </summary>
    private readonly byte[] _sendBuffer = new byte[NewfarmWireFormat.MaximumDatagramSize];

    /// <summary>
    /// The bound socket, or <see langword="null"/> before <see cref="Run"/> and after disposal.
    /// </summary>
    private Socket? _socket;

    /// <summary>
    /// When the next sweep is due, in <see cref="NewfarmClock.Milliseconds"/> milliseconds.
    /// </summary>
    private long _nextSweepMilliseconds;

    /// <summary>
    /// True once <see cref="Dispose"/> has run, so the loop stops and a second disposal does nothing.
    /// </summary>
    private bool _isDisposed;

    /// <summary>
    /// Creates a server from the supplied configuration.
    /// </summary>
    /// <param name="config">The configuration to bind and run with.</param>
    public NewfarmServer(NewfarmServerConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));

        _registry = new NewfarmSessionRegistry(Config);
        _limiter = new NewfarmRequestLimiter(Config);
    }

    /// <summary>
    /// Binds the socket and runs the receive and sweep loop until the supplied token is cancelled or the server is
    /// disposed.
    /// </summary>
    /// <param name="cancellationToken">Cancelled to stop the loop.</param>
    public void Run(CancellationToken cancellationToken)
    {
        Bind();

        while (!cancellationToken.IsCancellationRequested && !_isDisposed)
        {
            ReceiveOne();

            SweepIfDue();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try
        {
            _socket?.Close();
        }
        catch (SocketException)
        {
            // Closing a socket that is already unusable is not worth reporting.
        }

        _socket?.Dispose();
        _socket = null;
    }

    /// <summary>
    /// Binds the configured port, accepting datagrams from both address families where the platform allows it.
    /// </summary>
    private void Bind()
    {
        _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        }
        catch (SocketException)
        {
            // A platform that refuses dual-stack still serves IPv6 peers, which is enough to run.
        }

        _socket.ReceiveTimeout = (int)Config.SweepIntervalMilliseconds;
        _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, Config.Port));

        BoundPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;

        _nextSweepMilliseconds = NewfarmClock.Milliseconds + Config.SweepIntervalMilliseconds;

        Log($"Newfarm listening on UDP port [{BoundPort}].");
    }

    /// <summary>
    /// Receives at most one datagram, returning as soon as the socket's timeout elapses so the loop can sweep.
    /// </summary>
    private void ReceiveOne()
    {
        EndPoint senderEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0);

        int receivedLength;

        try
        {
            receivedLength = _socket!.ReceiveFrom(_receiveBuffer, ref senderEndPoint);
        }
        catch (SocketException socketException) when (socketException.SocketErrorCode is SocketError.TimedOut)
        {
            return;
        }
        catch (SocketException)
        {
            // A datagram-specific failure, most often a prior ICMP unreachable surfacing here. Skip it.
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (receivedLength <= 0)
            return;

        try
        {
            HandleDatagram(new ReadOnlySpan<byte>(_receiveBuffer, 0, receivedLength), (IPEndPoint)senderEndPoint);
        }
        catch (Exception exception)
        {
            // One malformed datagram must never stop the directory.
            Log($"Newfarm failed to handle a datagram from [{senderEndPoint}]: [{exception.Message}].");
        }
    }

    /// <summary>
    /// Sweeps every session when the sweep interval has elapsed, and sends whatever the sweep decided.
    /// </summary>
    private void SweepIfDue()
    {
        long nowMilliseconds = NewfarmClock.Milliseconds;

        if (nowMilliseconds < _nextSweepMilliseconds)
            return;

        _nextSweepMilliseconds = nowMilliseconds + Config.SweepIntervalMilliseconds;

        _notifications.Clear();
        _registry.Sweep(nowMilliseconds, _notifications);

        SendNotifications();

        _limiter.Sweep(nowMilliseconds);
    }

    /// <summary>
    /// Dispatches one received datagram to the handler for its type.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    /// <param name="senderEndPoint">The peer that sent them.</param>
    private void HandleDatagram(ReadOnlySpan<byte> datagram, IPEndPoint senderEndPoint)
    {
        // Too short to have been padded, so either it was not sent by a newfarm client or it was sent by something
        // hoping newfarm would answer at length to an address that never asked. Either way there is nothing to say.
        if (datagram.Length < NewfarmWireFormat.MinimumRequestSize)
            return;

        if (!NewfarmWireFormat.TryReadType(datagram, out NewfarmPacketType newfarmPacketType))
            return;

        long nowMilliseconds = NewfarmClock.Milliseconds;

        if (!_limiter.TryAdmit(senderEndPoint, nowMilliseconds))
            return;

        switch (newfarmPacketType)
        {
            case NewfarmPacketType.CreateSession:
                HandleCreateSession(senderEndPoint, nowMilliseconds);
                break;

            case NewfarmPacketType.HostHeartbeat:
                HandleHostHeartbeat(datagram, senderEndPoint, nowMilliseconds);
                break;

            case NewfarmPacketType.PublishCredential:
                HandlePublishCredential(datagram, senderEndPoint, nowMilliseconds);
                break;

            case NewfarmPacketType.DeclineElection:
                HandleDeclineElection(datagram, senderEndPoint, nowMilliseconds);
                break;

            case NewfarmPacketType.CloseSession:
                HandleCloseSession(datagram, senderEndPoint);
                break;

            case NewfarmPacketType.SurrenderHosting:
                HandleSurrenderHosting(datagram, senderEndPoint, nowMilliseconds);
                break;

            case NewfarmPacketType.CredentialUnreachable:
                HandleCredentialUnreachable(datagram, senderEndPoint, nowMilliseconds);
                break;

            case NewfarmPacketType.AwaitSession:
                HandleAwaitSession(datagram, senderEndPoint, nowMilliseconds);
                break;

            case NewfarmPacketType.WaiterHeartbeat:
                HandleWaiterHeartbeat(datagram, senderEndPoint, nowMilliseconds);
                break;

            default:
                // Server-to-peer types, and anything unrecognised, have no meaning arriving here.
                break;
        }
    }

    /// <summary>
    /// Opens a session for the sender and returns its identity.
    /// </summary>
    /// <param name="senderEndPoint">The peer asking for a session.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void HandleCreateSession(IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (!_registry.TryCreateSession(senderEndPoint, nowMilliseconds, out NewfarmSession? session))
        {
            SendRefused(sessionId: 0, NewfarmRefusalReason.ServerAtCapacity, senderEndPoint);

            return;
        }

        Log($"Newfarm opened session [{session!.SessionId:x16}] for host [{senderEndPoint}].");

        int length = NewfarmWireFormat.WriteSessionEpoch(_sendBuffer, NewfarmPacketType.SessionCreated, session.SessionId, session.Epoch);

        Send(length, senderEndPoint);
    }

    /// <summary>
    /// Records that the sender is still hosting.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    /// <param name="senderEndPoint">The peer that sent them.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void HandleHostHeartbeat(ReadOnlySpan<byte> datagram, IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out ulong sessionId, out uint _))
            return;

        if (!TryResolveOrRefuse(sessionId, senderEndPoint, out NewfarmSession? session))
            return;

        session!.TryRecordHostHeartbeat(senderEndPoint, nowMilliseconds);
    }

    /// <summary>
    /// Accepts a credential from the elected peer and hands it to everyone waiting.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    /// <param name="senderEndPoint">The peer that sent them.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void HandlePublishCredential(ReadOnlySpan<byte> datagram, IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out ulong sessionId, out uint epoch))
            return;

        if (!NewfarmWireFormat.TryReadCredential(datagram, NewfarmWireFormat.SessionEpochHeaderSize, out string adapterTag, out string credential))
            return;

        if (!TryResolveOrRefuse(sessionId, senderEndPoint, out NewfarmSession? session))
            return;

        _notifications.Clear();

        NewfarmRequestResult publishResult = _registry.PublishCredential(session!, senderEndPoint, epoch, adapterTag, credential, nowMilliseconds, _notifications);

        if (publishResult is not NewfarmRequestResult.Accepted)
        {
            Log($"Newfarm refused a credential for session [{sessionId:x16}] from [{senderEndPoint}]: [{publishResult}].");

            return;
        }

        Log($"Newfarm accepted a credential for session [{sessionId:x16}] epoch [{epoch}] from [{senderEndPoint}], fanning out to [{_notifications.Count}] waiters.");

        SendSessionEpoch(NewfarmPacketType.CredentialAccepted, session!, senderEndPoint);

        SendNotifications();
    }

    /// <summary>
    /// Stands the sender down from an election and hands it to the next waiter.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    /// <param name="senderEndPoint">The peer that sent them.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void HandleDeclineElection(ReadOnlySpan<byte> datagram, IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out ulong sessionId, out uint epoch))
            return;

        if (!TryResolveOrRefuse(sessionId, senderEndPoint, out NewfarmSession? session))
            return;

        _notifications.Clear();

        NewfarmRequestResult declineResult = _registry.DeclineElection(session!, senderEndPoint, epoch, nowMilliseconds, _notifications);

        if (declineResult is not NewfarmRequestResult.Accepted)
            return;

        Log($"Newfarm stood [{senderEndPoint}] down from hosting session [{sessionId:x16}] at its own request.");

        SendNotifications();
    }

    /// <summary>
    /// Removes a session at its host's request.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    /// <param name="senderEndPoint">The peer that sent them.</param>
    private void HandleCloseSession(ReadOnlySpan<byte> datagram, IPEndPoint senderEndPoint)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out ulong sessionId, out uint epoch))
            return;

        if (!TryResolveOrRefuse(sessionId, senderEndPoint, out NewfarmSession? session))
            return;

        NewfarmRequestResult closeResult = _registry.CloseSession(session!, senderEndPoint, epoch);

        if (closeResult is not NewfarmRequestResult.Accepted)
        {
            Log($"Newfarm refused to close session [{sessionId:x16}] for [{senderEndPoint}]: [{closeResult}].");

            return;
        }

        Log($"Newfarm closed session [{sessionId:x16}] at the request of [{senderEndPoint}].");
    }

    /// <summary>
    /// Hands the session on because its host has said it is no longer hosting.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    /// <param name="senderEndPoint">The peer that sent them.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void HandleSurrenderHosting(ReadOnlySpan<byte> datagram, IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out ulong sessionId, out uint epoch))
            return;

        if (!TryResolveOrRefuse(sessionId, senderEndPoint, out NewfarmSession? session))
            return;

        _notifications.Clear();

        NewfarmRequestResult surrenderResult = _registry.SurrenderHosting(session!, senderEndPoint, epoch, nowMilliseconds, _notifications);

        if (surrenderResult is not NewfarmRequestResult.Accepted)
            return;

        Log($"Newfarm took session [{sessionId:x16}] back from [{senderEndPoint}], which gave up hosting it.");

        SendNotifications();
    }

    /// <summary>
    /// Records that the sender cannot reach the host of a session.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    /// <param name="senderEndPoint">The peer that sent them.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void HandleCredentialUnreachable(ReadOnlySpan<byte> datagram, IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out ulong sessionId, out uint epoch))
            return;

        if (!TryResolveOrRefuse(sessionId, senderEndPoint, out NewfarmSession? session))
            return;

        if (_registry.ReportCredentialUnreachable(session!, senderEndPoint, epoch, nowMilliseconds) is not NewfarmRequestResult.Accepted)
            return;

        Log($"Newfarm heard from [{senderEndPoint}] that session [{sessionId:x16}] cannot be reached at the credential it holds.");

        session!.RecordWaiterKeepAlive(senderEndPoint, nowMilliseconds);

        SendSessionEpoch(NewfarmPacketType.Waiting, session, senderEndPoint);
    }

    /// <summary>
    /// Adds the sender to a session's waiting set and tells it where things stand.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    /// <param name="senderEndPoint">The peer that sent them.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void HandleAwaitSession(ReadOnlySpan<byte> datagram, IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out ulong sessionId, out uint _))
            return;

        if (!TryResolveOrRefuse(sessionId, senderEndPoint, out NewfarmSession? session))
            return;

        NewfarmWaitOutcome waitOutcome = _registry.AddWaiter(session!, senderEndPoint, nowMilliseconds);

        if (waitOutcome is NewfarmWaitOutcome.SessionFull)
        {
            SendRefused(sessionId, NewfarmRefusalReason.SessionFull, senderEndPoint);

            return;
        }

        if (waitOutcome is NewfarmWaitOutcome.CredentialAvailable)
        {
            SendCredential(session!, senderEndPoint);

            return;
        }

        session!.RecordWaiterKeepAlive(senderEndPoint, nowMilliseconds);

        SendSessionEpoch(NewfarmPacketType.Waiting, session, senderEndPoint);
    }

    /// <summary>
    /// Records that a waiting peer is still there.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    /// <param name="senderEndPoint">The peer that sent them.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void HandleWaiterHeartbeat(ReadOnlySpan<byte> datagram, IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out ulong sessionId, out uint _))
            return;

        if (!TryResolveOrRefuse(sessionId, senderEndPoint, out NewfarmSession? session))
            return;

        if (session!.RecordWaiterHeartbeat(senderEndPoint, nowMilliseconds))
            return;

        session.TryRecordElectedHeartbeat(senderEndPoint, nowMilliseconds);
    }

    /// <summary>
    /// Resolves a session and replies with the reason when it cannot be resolved.
    /// </summary>
    /// <param name="sessionId">The session being addressed.</param>
    /// <param name="senderEndPoint">The peer to reply to on refusal.</param>
    /// <param name="session">When this returns <see langword="true"/>, the resolved session.</param>
    /// <returns><see langword="true"/> when the session resolved.</returns>
    private bool TryResolveOrRefuse(ulong sessionId, IPEndPoint senderEndPoint, out NewfarmSession? session)
    {
        if (_registry.TryResolve(sessionId, out session) is NewfarmRequestResult.Accepted)
            return true;

        SendRefused(sessionId, NewfarmRefusalReason.SessionNotFound, senderEndPoint);

        return false;
    }

    /// <summary>
    /// Tells a peer its request was refused, and why.
    /// </summary>
    /// <param name="sessionId">The session the request named.</param>
    /// <param name="newfarmRefusalReason">Why the request was refused.</param>
    /// <param name="endPoint">The peer to reply to.</param>
    private void SendRefused(ulong sessionId, NewfarmRefusalReason newfarmRefusalReason, IPEndPoint endPoint)
    {
        int length = NewfarmWireFormat.WriteRefused(_sendBuffer, sessionId, newfarmRefusalReason);

        Send(length, endPoint);
    }

    /// <summary>
    /// Sends everything the registry decided should be sent, then clears the list.
    /// </summary>
    private void SendNotifications()
    {
        for (int i = 0; i < _notifications.Count; i++)
        {
            NewfarmNotification notification = _notifications[i];

            if (notification.PacketType is NewfarmPacketType.CredentialAvailable)
            {
                SendCredential(notification.Session, notification.EndPoint);

                continue;
            }

            SendSessionEpoch(notification.PacketType, notification.Session, notification.EndPoint);
        }

        _notifications.Clear();
    }

    /// <summary>
    /// Sends a message whose payload is the session id and its epoch.
    /// </summary>
    /// <param name="newfarmPacketType">The message to send.</param>
    /// <param name="session">The session it concerns.</param>
    /// <param name="endPoint">The peer to send to.</param>
    private void SendSessionEpoch(NewfarmPacketType newfarmPacketType, NewfarmSession session, IPEndPoint endPoint)
    {
        int length = NewfarmWireFormat.WriteSessionEpoch(_sendBuffer, newfarmPacketType, session.SessionId, session.Epoch);

        Send(length, endPoint);
    }

    /// <summary>
    /// Sends the credential a session currently lives at.
    /// </summary>
    /// <param name="session">The session whose credential is being sent.</param>
    /// <param name="endPoint">The peer to send to.</param>
    private void SendCredential(NewfarmSession session, IPEndPoint endPoint)
    {
        if (session.Credential is null)
            return;

        int length = NewfarmWireFormat.WriteSessionEpoch(_sendBuffer, NewfarmPacketType.CredentialAvailable, session.SessionId, session.Epoch);

        length += NewfarmWireFormat.WriteCredential(new Span<byte>(_sendBuffer, length, _sendBuffer.Length - length), session.AdapterTag, session.Credential);

        Send(length, endPoint);
    }

    /// <summary>
    /// Puts the first <paramref name="length"/> bytes of the send buffer on the wire.
    /// </summary>
    /// <param name="length">How many bytes of the send buffer to send.</param>
    /// <param name="endPoint">The peer to send to.</param>
    private void Send(int length, IPEndPoint endPoint)
    {
        try
        {
            _socket!.SendTo(_sendBuffer, 0, length, SocketFlags.None, endPoint);
        }
        catch (SocketException socketException)
        {
            Log($"Newfarm failed to send to [{endPoint}]: [{socketException.SocketErrorCode}].");
        }
        catch (ObjectDisposedException)
        {
            // The server is shutting down.
        }
    }

    /// <summary>
    /// Raises <see cref="Logged"/> when anything is listening.
    /// </summary>
    /// <param name="message">The message to report.</param>
    private void Log(string message)
    {
        Logged?.Invoke(message);
    }
}

/// <summary>
/// Receives operator-facing messages from a <see cref="NewfarmServer"/>.
/// </summary>
/// <param name="message">The message to report.</param>
public delegate void NewfarmLogHandler(string message);
