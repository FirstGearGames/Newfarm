using System;
using System.Net;
using System.Net.Sockets;
using Newfarm.Wire;

namespace Newfarm.Client;

/// <summary>
/// Talks to a newfarm directory on behalf of one peer: obtains a session while hosting, reports that it is still
/// hosting, and after a host is lost either takes the session over or waits to be told where it moved to.
/// </summary>
/// <remarks>
/// Poll driven, like the transports it sits beside. <see cref="Poll"/> drains the socket, raises events on the
/// calling thread, and sends whichever heartbeat the current <see cref="Mode"/> calls for, so the client owns no
/// threads and needs no synchronisation.
/// </remarks>
public sealed class NewfarmClient : IDisposable
{
    /// <summary>
    /// Raised when newfarm has opened a session, carrying the identity the host must distribute to its clients.
    /// </summary>
    public event NewfarmSessionCreatedHandler? SessionCreated;

    /// <summary>
    /// Receives the identity of a newly opened session.
    /// </summary>
    /// <param name="identity">The identity to distribute to clients.</param>
    public delegate void NewfarmSessionCreatedHandler(NewfarmSessionIdentity identity);

    /// <summary>
    /// Raised when newfarm has told this peer to host the session. The peer should create a room and publish its
    /// credential through <see cref="PublishCredential"/> before the deadline, or decline through
    /// <see cref="DeclineElection"/> if it cannot.
    /// </summary>
    public event NewfarmElectedToHostHandler? ElectedToHost;

    /// <summary>
    /// Receives an instruction to host the session.
    /// </summary>
    /// <param name="epoch">The epoch the peer is elected for, which its credential must be published against.</param>
    public delegate void NewfarmElectedToHostHandler(uint epoch);

    /// <summary>
    /// Raised when newfarm has withdrawn this peer's election, because it ran out of time, declined, or went quiet.
    /// The peer is queued again and will be told where the session moved to.
    /// </summary>
    public event NewfarmElectionAbortedHandler? ElectionAborted;

    /// <summary>
    /// Receives notice that an election has been withdrawn.
    /// </summary>
    /// <param name="epoch">The epoch in force when the election was withdrawn.</param>
    public delegate void NewfarmElectionAbortedHandler(uint epoch);

    /// <summary>
    /// Raised when peers have reported they cannot reach this host, and newfarm wants it to prove otherwise. The
    /// peer should publish a credential through <see cref="PublishCredential"/>, creating a fresh room if the one it
    /// had is gone, or give the session up through <see cref="SurrenderHosting"/> if it can no longer host at all.
    /// </summary>
    /// <remarks>
    /// Saying nothing is an answer too, and the wrong one: after enough unanswered rounds newfarm stands the host
    /// down and elects a replacement, however healthily this peer is still heartbeating.
    /// </remarks>
    public event NewfarmHostingChallengedHandler? HostingChallenged;

    /// <summary>
    /// Receives a demand that this peer prove it is still hosting.
    /// </summary>
    /// <param name="epoch">The epoch the session is being hosted for.</param>
    public delegate void NewfarmHostingChallengedHandler(uint epoch);

    /// <summary>
    /// Raised when newfarm has handed over the credential the session now lives at.
    /// </summary>
    public event NewfarmCredentialAvailableHandler? CredentialAvailable;

    /// <summary>
    /// Receives the credential a session now lives at.
    /// </summary>
    /// <param name="credential">Where the session lives, and which service it lives on.</param>
    public delegate void NewfarmCredentialAvailableHandler(NewfarmCredential credential);

    /// <summary>
    /// Raised when newfarm has refused a request, whether because it holds no such session, because the secret did
    /// not match, or because it is full.
    /// </summary>
    public event NewfarmRefusedHandler? Refused;

    /// <summary>
    /// Receives the reason newfarm refused a request.
    /// </summary>
    /// <param name="newfarmPacketType">The refusal newfarm sent.</param>
    public delegate void NewfarmRefusedHandler(NewfarmPacketType newfarmPacketType);

    /// <summary>
    /// The configuration the client was constructed with.
    /// </summary>
    public NewfarmClientConfig Config { get; }

    /// <summary>
    /// What the client is currently doing.
    /// </summary>
    public NewfarmClientMode Mode { get; private set; } = NewfarmClientMode.Idle;

    /// <summary>
    /// The session the client holds, valid once <see cref="Mode"/> has left
    /// <see cref="NewfarmClientMode.Idle"/> and <see cref="NewfarmClientMode.CreatingSession"/>.
    /// </summary>
    public NewfarmSessionIdentity Identity { get; private set; }

    /// <summary>
    /// The socket used to reach newfarm, bound to an ephemeral port.
    /// </summary>
    private readonly Socket _socket;

    /// <summary>
    /// The receive buffer, sized to <see cref="NewfarmWireFormat.MaximumDatagramSize"/>.
    /// </summary>
    private readonly byte[] _receiveBuffer = new byte[NewfarmWireFormat.MaximumDatagramSize];

    /// <summary>
    /// The send buffer. Only ever used between building a message and handing it to the socket.
    /// </summary>
    private readonly byte[] _sendBuffer = new byte[NewfarmWireFormat.MaximumDatagramSize];

    /// <summary>
    /// When the next heartbeat is due, in <see cref="NewfarmClock.Milliseconds"/> milliseconds.
    /// </summary>
    private long _nextHeartbeatMilliseconds;

    /// <summary>
    /// When an unanswered request should be repeated, in <see cref="NewfarmClock.Milliseconds"/> milliseconds, or zero
    /// when nothing is outstanding.
    /// </summary>
    private long _nextRequestRetryMilliseconds;

    /// <summary>
    /// The request to repeat while it goes unanswered, so a lost datagram does not strand the peer.
    /// </summary>
    private NewfarmPacketType _outstandingRequest;

    /// <summary>
    /// The service tag of a credential published but not yet confirmed, or <see langword="null"/> when nothing is
    /// awaiting confirmation.
    /// </summary>
    private string? _unconfirmedAdapterTag;

    /// <summary>
    /// The bytes of a credential published but not yet confirmed.
    /// </summary>
    private byte[]? _unconfirmedCredential;

    /// <summary>
    /// The epoch a credential awaiting confirmation was published against, so a confirmation for an earlier
    /// publication does not clear a later one.
    /// </summary>
    private uint _unconfirmedEpoch;

    /// <summary>
    /// True once <see cref="Dispose"/> has run.
    /// </summary>
    private bool _isDisposed;

    /// <summary>
    /// Creates a client bound to an ephemeral local port.
    /// </summary>
    /// <param name="config">The configuration naming the directory to talk to.</param>
    public NewfarmClient(NewfarmClientConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));

        _socket = new Socket(Config.ServerEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Blocking = false;
    }

    /// <summary>
    /// Asks newfarm for a session. The identity arrives through <see cref="SessionCreated"/>, after which the peer
    /// is treated as hosting and is heartbeated for.
    /// </summary>
    public void CreateSession()
    {
        Mode = NewfarmClientMode.CreatingSession;

        BeginRequest(NewfarmPacketType.CreateSession);
    }

    /// <summary>
    /// Joins the waiting set for a session whose host has been lost. The peer is either elected, through
    /// <see cref="ElectedToHost"/>, or told where the session moved to, through <see cref="CredentialAvailable"/>.
    /// </summary>
    /// <param name="identity">The identity the peer was given by the host.</param>
    public void AwaitSession(NewfarmSessionIdentity identity)
    {
        Identity = identity;
        Mode = NewfarmClientMode.Waiting;

        ClearUnconfirmedCredential();

        BeginRequest(NewfarmPacketType.AwaitSession);
    }

    /// <summary>
    /// Declares that this peer is hosting an already known session, which is what a peer does after publishing a
    /// credential or when resuming a session it created earlier.
    /// </summary>
    /// <param name="identity">The identity of the session being hosted.</param>
    public void StartHosting(NewfarmSessionIdentity identity)
    {
        Identity = identity;
        Mode = NewfarmClientMode.Hosting;

        ClearUnconfirmedCredential();

        _outstandingRequest = default;
        _nextRequestRetryMilliseconds = 0;
        _nextHeartbeatMilliseconds = 0;
    }

    /// <summary>
    /// Reports the credential the peer's newly created room can be joined with, which newfarm hands to every waiting
    /// peer and which promotes this peer to host.
    /// </summary>
    /// <param name="adapterTag">Names the service the credential belongs to.</param>
    /// <param name="credential">The opaque bytes a peer needs to reach the room.</param>
    /// <remarks>
    /// The publication is repeated from <see cref="Poll"/> until newfarm confirms it, because a publication lost on
    /// the way would leave this peer hosting a room newfarm cannot direct anyone to.
    /// </remarks>
    public void PublishCredential(string adapterTag, byte[] credential)
    {
        _unconfirmedAdapterTag = adapterTag ?? throw new ArgumentNullException(nameof(adapterTag));
        _unconfirmedCredential = credential ?? throw new ArgumentNullException(nameof(credential));
        _unconfirmedEpoch = Identity.Epoch;

        _outstandingRequest = NewfarmPacketType.PublishCredential;
        _nextRequestRetryMilliseconds = NewfarmClock.Milliseconds + Config.RequestRetryIntervalMilliseconds;

        SendCredential();

        Mode = NewfarmClientMode.Hosting;

        _nextHeartbeatMilliseconds = 0;
    }

    /// <summary>
    /// Tells newfarm this peer cannot host after all, so the next waiter is elected without waiting for the
    /// election deadline to pass.
    /// </summary>
    public void DeclineElection()
    {
        SendAuthenticated(NewfarmPacketType.DeclineElection);

        Mode = NewfarmClientMode.Waiting;
    }

    /// <summary>
    /// Gives up hosting while staying in the session, so newfarm elects a replacement at once instead of waiting for
    /// this peer's heartbeat to lapse.
    /// </summary>
    /// <remarks>
    /// This is what a peer calls when it stops being able to host but is still perfectly online: the player left the
    /// match, or the game server it was running shut down. Its heartbeats would otherwise keep the session waiting on
    /// a host that is not hosting anything.
    /// </remarks>
    public void SurrenderHosting()
    {
        ClearUnconfirmedCredential();

        Mode = NewfarmClientMode.Waiting;

        BeginRequest(NewfarmPacketType.SurrenderHosting);
    }

    /// <summary>
    /// Reports that the credential this peer holds does not get it to the host, which asks newfarm to make the host
    /// prove it is still hosting.
    /// </summary>
    /// <remarks>
    /// Call this when joining the credential has actually been tried and failed. It is the only way newfarm can find
    /// out that a host is unreachable while its heartbeats keep arriving, since only the peers can tell.
    /// </remarks>
    public void ReportCredentialUnreachable()
    {
        Mode = NewfarmClientMode.Waiting;

        BeginRequest(NewfarmPacketType.CredentialUnreachable);
    }

    /// <summary>
    /// Ends the session deliberately, so newfarm forgets it rather than electing a replacement host.
    /// </summary>
    public void CloseSession()
    {
        SendAuthenticated(NewfarmPacketType.CloseSession);

        Mode = NewfarmClientMode.Idle;
    }

    /// <summary>
    /// Drains everything newfarm has sent, raises the events for it, and sends any heartbeat or retry now due. Call
    /// this regularly, for instance once per frame or per network tick.
    /// </summary>
    public void Poll()
    {
        if (_isDisposed)
            return;

        ReceiveAll();

        long nowMilliseconds = NewfarmClock.Milliseconds;

        SendHeartbeatIfDue(nowMilliseconds);
        RepeatRequestIfDue(nowMilliseconds);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _socket.Dispose();
    }

    /// <summary>
    /// Reads every datagram the socket has buffered.
    /// </summary>
    private void ReceiveAll()
    {
        while (true)
        {
            int receivedLength;

            try
            {
                if (_socket.Available == 0)
                    return;

                receivedLength = _socket.Receive(_receiveBuffer, SocketFlags.None);
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (receivedLength <= 0)
                return;

            HandleDatagram(new ReadOnlySpan<byte>(_receiveBuffer, 0, receivedLength));
        }
    }

    /// <summary>
    /// Dispatches one datagram from newfarm to the event it represents.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    private void HandleDatagram(ReadOnlySpan<byte> datagram)
    {
        if (!NewfarmWireFormat.TryReadType(datagram, out NewfarmPacketType newfarmPacketType))
            return;

        switch (newfarmPacketType)
        {
            case NewfarmPacketType.SessionCreated:
                HandleSessionCreated(datagram);
                break;

            case NewfarmPacketType.ElectHost:
                HandleElectHost(datagram);
                break;

            case NewfarmPacketType.AbortElection:
                HandleAbortElection(datagram);
                break;

            case NewfarmPacketType.CredentialAvailable:
                HandleCredentialAvailable(datagram);
                break;

            case NewfarmPacketType.CredentialAccepted:
                HandleCredentialAccepted(datagram);
                break;

            case NewfarmPacketType.ProveHosting:
                HandleProveHosting(datagram);
                break;

            case NewfarmPacketType.Waiting:
                HandleWaiting(datagram);
                break;

            case NewfarmPacketType.SessionNotFound:
            case NewfarmPacketType.SecretRejected:
            case NewfarmPacketType.ServerAtCapacity:
                _outstandingRequest = default;
                _nextRequestRetryMilliseconds = 0;

                ClearUnconfirmedCredential();

                Refused?.Invoke(newfarmPacketType);
                break;

            default:
                // Peer-to-server types, and anything unrecognised, have no meaning arriving here.
                break;
        }
    }

    /// <summary>
    /// Adopts a newly opened session and reports its identity.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    private void HandleSessionCreated(ReadOnlySpan<byte> datagram)
    {
        if (!NewfarmWireFormat.TryReadAuthenticated(datagram, out Guid sessionId, out Guid sessionSecret, out uint epoch))
            return;

        Identity = new NewfarmSessionIdentity(sessionId, sessionSecret, epoch);
        Mode = NewfarmClientMode.Hosting;

        _outstandingRequest = default;
        _nextRequestRetryMilliseconds = 0;
        _nextHeartbeatMilliseconds = 0;

        SessionCreated?.Invoke(Identity);
    }

    /// <summary>
    /// Adopts the epoch being elected for and reports the instruction to host.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    private void HandleElectHost(ReadOnlySpan<byte> datagram)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out Guid sessionId, out uint epoch))
            return;

        if (sessionId != Identity.SessionId)
            return;

        Identity = new NewfarmSessionIdentity(Identity.SessionId, Identity.SessionSecret, epoch);
        Mode = NewfarmClientMode.Elected;

        _outstandingRequest = default;
        _nextRequestRetryMilliseconds = 0;

        ClearUnconfirmedCredential();

        ElectedToHost?.Invoke(epoch);
    }

    /// <summary>
    /// Returns to waiting and reports that the election was withdrawn.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    private void HandleAbortElection(ReadOnlySpan<byte> datagram)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out Guid sessionId, out uint epoch))
            return;

        if (sessionId != Identity.SessionId)
            return;

        Mode = NewfarmClientMode.Waiting;

        ClearUnconfirmedCredential();

        ElectionAborted?.Invoke(epoch);
    }

    /// <summary>
    /// Reports where the session now lives.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    private void HandleCredentialAvailable(ReadOnlySpan<byte> datagram)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out Guid sessionId, out uint epoch))
            return;

        if (sessionId != Identity.SessionId)
            return;

        if (!NewfarmWireFormat.TryReadCredential(datagram, NewfarmWireFormat.SessionEpochHeaderSize, out string adapterTag, out byte[] credential))
            return;

        Identity = new NewfarmSessionIdentity(Identity.SessionId, Identity.SessionSecret, epoch);

        _outstandingRequest = default;
        _nextRequestRetryMilliseconds = 0;

        CredentialAvailable?.Invoke(new NewfarmCredential(adapterTag, credential, epoch));
    }

    /// <summary>
    /// Passes on a demand that this peer prove it is still hosting.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    private void HandleProveHosting(ReadOnlySpan<byte> datagram)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out Guid sessionId, out uint epoch))
            return;

        if (sessionId != Identity.SessionId || Mode is not NewfarmClientMode.Hosting)
            return;

        HostingChallenged?.Invoke(epoch);
    }

    /// <summary>
    /// Accepts confirmation that a published credential was taken, which stops it being repeated.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    private void HandleCredentialAccepted(ReadOnlySpan<byte> datagram)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out Guid sessionId, out uint epoch))
            return;

        if (sessionId != Identity.SessionId || epoch != _unconfirmedEpoch)
            return;

        ClearUnconfirmedCredential();
    }

    /// <summary>
    /// Accepts confirmation that the peer is queued, which also confirms the request arrived.
    /// </summary>
    /// <param name="datagram">The bytes received.</param>
    private void HandleWaiting(ReadOnlySpan<byte> datagram)
    {
        if (!NewfarmWireFormat.TryReadSessionEpoch(datagram, out Guid sessionId, out uint epoch))
            return;

        if (sessionId != Identity.SessionId)
            return;

        Identity = new NewfarmSessionIdentity(Identity.SessionId, Identity.SessionSecret, epoch);

        if (_outstandingRequest is NewfarmPacketType.AwaitSession)
        {
            _outstandingRequest = default;
            _nextRequestRetryMilliseconds = 0;
        }
    }

    /// <summary>
    /// Sends the heartbeat the current mode calls for, when one is due.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void SendHeartbeatIfDue(long nowMilliseconds)
    {
        if (Mode is NewfarmClientMode.Idle or NewfarmClientMode.CreatingSession)
            return;

        if (nowMilliseconds < _nextHeartbeatMilliseconds)
            return;

        if (Mode is NewfarmClientMode.Hosting)
        {
            _nextHeartbeatMilliseconds = nowMilliseconds + Config.HostHeartbeatIntervalMilliseconds;

            SendAuthenticated(NewfarmPacketType.HostHeartbeat);

            return;
        }

        _nextHeartbeatMilliseconds = nowMilliseconds + Config.WaiterHeartbeatIntervalMilliseconds;

        SendAuthenticated(NewfarmPacketType.WaiterHeartbeat);
    }

    /// <summary>
    /// Repeats an unanswered request, since a request lost on the way to newfarm would otherwise strand the peer.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    private void RepeatRequestIfDue(long nowMilliseconds)
    {
        if (_nextRequestRetryMilliseconds == 0 || nowMilliseconds < _nextRequestRetryMilliseconds)
            return;

        _nextRequestRetryMilliseconds = nowMilliseconds + Config.RequestRetryIntervalMilliseconds;

        if (_outstandingRequest is NewfarmPacketType.CreateSession)
        {
            SendType(NewfarmPacketType.CreateSession);

            return;
        }

        if (_outstandingRequest is NewfarmPacketType.PublishCredential)
        {
            SendCredential();

            return;
        }

        SendAuthenticated(_outstandingRequest);
    }

    /// <summary>
    /// Sends a request and arms its repeat.
    /// </summary>
    /// <param name="newfarmPacketType">The request to send.</param>
    private void BeginRequest(NewfarmPacketType newfarmPacketType)
    {
        _outstandingRequest = newfarmPacketType;
        _nextRequestRetryMilliseconds = NewfarmClock.Milliseconds + Config.RequestRetryIntervalMilliseconds;

        if (newfarmPacketType is NewfarmPacketType.CreateSession)
        {
            SendType(newfarmPacketType);

            return;
        }

        SendAuthenticated(newfarmPacketType);
    }

    /// <summary>
    /// Sends the credential awaiting confirmation, against the epoch it was published for rather than the current
    /// one, so a repeat is refused for the same reason the original would have been if the epoch has since moved on.
    /// </summary>
    private void SendCredential()
    {
        if (_unconfirmedAdapterTag is null || _unconfirmedCredential is null)
            return;

        int length = NewfarmWireFormat.WriteAuthenticated(_sendBuffer, NewfarmPacketType.PublishCredential, Identity.SessionId, Identity.SessionSecret, _unconfirmedEpoch);

        length += NewfarmWireFormat.WriteCredential(new Span<byte>(_sendBuffer, length, _sendBuffer.Length - length), _unconfirmedAdapterTag, _unconfirmedCredential);

        Send(length);
    }

    /// <summary>
    /// Forgets a credential awaiting confirmation, and stops repeating it.
    /// </summary>
    private void ClearUnconfirmedCredential()
    {
        _unconfirmedAdapterTag = null;
        _unconfirmedCredential = null;
        _unconfirmedEpoch = 0;

        if (_outstandingRequest is not NewfarmPacketType.PublishCredential)
            return;

        _outstandingRequest = default;
        _nextRequestRetryMilliseconds = 0;
    }

    /// <summary>
    /// Sends a message carrying the session id, its secret and the current epoch.
    /// </summary>
    /// <param name="newfarmPacketType">The message to send.</param>
    private void SendAuthenticated(NewfarmPacketType newfarmPacketType)
    {
        int length = NewfarmWireFormat.WriteAuthenticated(_sendBuffer, newfarmPacketType, Identity.SessionId, Identity.SessionSecret, Identity.Epoch);

        Send(length);
    }

    /// <summary>
    /// Sends a message that carries nothing but its type.
    /// </summary>
    /// <param name="newfarmPacketType">The message to send.</param>
    private void SendType(NewfarmPacketType newfarmPacketType)
    {
        int length = NewfarmWireFormat.WriteType(_sendBuffer, newfarmPacketType);

        Send(length);
    }

    /// <summary>
    /// Puts the first <paramref name="length"/> bytes of the send buffer on the wire.
    /// </summary>
    /// <param name="length">How many bytes of the send buffer to send.</param>
    private void Send(int length)
    {
        try
        {
            _socket.SendTo(_sendBuffer, 0, length, SocketFlags.None, Config.ServerEndPoint);
        }
        catch (SocketException)
        {
            // Newfarm being briefly unreachable is expected; the retry and heartbeat timers cover it.
        }
        catch (ObjectDisposedException)
        {
            // The client is shutting down.
        }
    }
}
