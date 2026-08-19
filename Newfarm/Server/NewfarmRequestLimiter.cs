using System.Collections.Generic;
using System.Net;

namespace Newfarm.Server;

/// <summary>
/// Decides whether newfarm should act on a datagram, so no one peer and no one machine can crowd the directory out.
/// </summary>
/// <remarks>
/// Two buckets, because the two things being defended against are different sizes. The per-peer bucket is tight: one
/// peer's traffic is entirely predictable, a heartbeat a second and the odd request, so a limit many times that is
/// nowhere near anything a real peer does. The per-address bucket is deliberately loose, because an address is not a
/// peer: a school, an office, or a carrier-grade NAT puts hundreds of unrelated players behind one of them, and a
/// limit sized for one player would lock out every player in the building. It is there to stop a single machine
/// taking the directory for itself, not to ration ordinary players.
/// Refusal here is silent. Answering a flood doubles it, and a legitimate peer never sees this at all.
/// </remarks>
internal sealed class NewfarmRequestLimiter
{
    /// <summary>
    /// The configuration the rates and caps are read from.
    /// </summary>
    private readonly NewfarmServerConfig _config;

    /// <summary>
    /// A bucket per peer, keyed by the full endpoint.
    /// </summary>
    private readonly Dictionary<IPEndPoint, NewfarmTokenBucket> _bucketsByEndPoint = [];

    /// <summary>
    /// A bucket per machine, keyed by address alone, so changing source port does not buy an attacker a fresh
    /// allowance.
    /// </summary>
    private readonly Dictionary<IPAddress, NewfarmTokenBucket> _bucketsByAddress = [];

    /// <summary>
    /// Reused by <see cref="Sweep"/> to collect what to forget, so a sweep allocates nothing.
    /// </summary>
    private readonly List<IPEndPoint> _lapsedEndPoints = [];

    /// <summary>
    /// Reused by <see cref="Sweep"/> to collect which addresses to forget.
    /// </summary>
    private readonly List<IPAddress> _lapsedAddresses = [];

    /// <summary>
    /// Creates a limiter bound to the supplied configuration.
    /// </summary>
    /// <param name="config">The configuration to read rates and caps from.</param>
    public NewfarmRequestLimiter(NewfarmServerConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Takes one request's worth of allowance for a sender.
    /// </summary>
    /// <param name="senderEndPoint">The peer the datagram came from.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns><see langword="true"/> when the request should be acted on.</returns>
    public bool TryAdmit(IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (!TryAdmitAddress(senderEndPoint.Address, nowMilliseconds))
            return false;

        return TryAdmitEndPoint(senderEndPoint, nowMilliseconds);
    }

    /// <summary>
    /// Forgets senders that have been quiet, which is what keeps the two tables bounded over a long run.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    public void Sweep(long nowMilliseconds)
    {
        _lapsedEndPoints.Clear();

        foreach (KeyValuePair<IPEndPoint, NewfarmTokenBucket> entry in _bucketsByEndPoint)
        {
            if (nowMilliseconds - entry.Value.LastRequestMilliseconds < _config.LimiterIdleForgetMilliseconds)
                continue;

            _lapsedEndPoints.Add(entry.Key);
        }

        for (int i = 0; i < _lapsedEndPoints.Count; i++)
            _bucketsByEndPoint.Remove(_lapsedEndPoints[i]);

        _lapsedAddresses.Clear();

        foreach (KeyValuePair<IPAddress, NewfarmTokenBucket> entry in _bucketsByAddress)
        {
            if (nowMilliseconds - entry.Value.LastRequestMilliseconds < _config.LimiterIdleForgetMilliseconds)
                continue;

            _lapsedAddresses.Add(entry.Key);
        }

        for (int i = 0; i < _lapsedAddresses.Count; i++)
            _bucketsByAddress.Remove(_lapsedAddresses[i]);
    }

    /// <summary>
    /// Takes one request's worth of a machine's allowance.
    /// </summary>
    /// <param name="senderAddress">The address the datagram came from.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns><see langword="true"/> when the request is within the address's allowance.</returns>
    private bool TryAdmitAddress(IPAddress senderAddress, long nowMilliseconds)
    {
        if (_bucketsByAddress.TryGetValue(senderAddress, out NewfarmTokenBucket bucket))
        {
            bool isAdmitted = bucket.TryTake(nowMilliseconds, _config.MaximumRequestsPerSecondPerAddress, _config.RequestBurstPerAddress);

            _bucketsByAddress[senderAddress] = bucket;

            return isAdmitted;
        }

        // A table of addresses is bounded by how many machines can reach the port, and an attacker cannot invent new
        // ones the way it can invent ports, so this one is allowed to grow to its cap and then hold the line.
        if (_bucketsByAddress.Count >= _config.MaximumTrackedAddresses)
            return false;

        bucket = NewfarmTokenBucket.Create(nowMilliseconds, _config.RequestBurstPerAddress);

        bool isFirstAdmitted = bucket.TryTake(nowMilliseconds, _config.MaximumRequestsPerSecondPerAddress, _config.RequestBurstPerAddress);

        _bucketsByAddress.Add(senderAddress, bucket);

        return isFirstAdmitted;
    }

    /// <summary>
    /// Takes one request's worth of a peer's allowance.
    /// </summary>
    /// <param name="senderEndPoint">The peer the datagram came from.</param>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <returns><see langword="true"/> when the request is within the peer's allowance.</returns>
    private bool TryAdmitEndPoint(IPEndPoint senderEndPoint, long nowMilliseconds)
    {
        if (_bucketsByEndPoint.TryGetValue(senderEndPoint, out NewfarmTokenBucket bucket))
        {
            bool isAdmitted = bucket.TryTake(nowMilliseconds, _config.MaximumRequestsPerSecondPerPeer, _config.RequestBurstPerPeer);

            _bucketsByEndPoint[senderEndPoint] = bucket;

            return isAdmitted;
        }

        // Ports are free to invent, so this table is the one an attacker can push at. Once it is full, new peers are
        // admitted on the strength of their address alone rather than being turned away: the address bucket has
        // already had its say, and refusing here would lock out the genuine peer that happened to arrive late.
        if (_bucketsByEndPoint.Count >= _config.MaximumTrackedPeers)
            return true;

        bucket = NewfarmTokenBucket.Create(nowMilliseconds, _config.RequestBurstPerPeer);

        bool isFirstAdmitted = bucket.TryTake(nowMilliseconds, _config.MaximumRequestsPerSecondPerPeer, _config.RequestBurstPerPeer);

        _bucketsByEndPoint.Add(senderEndPoint, bucket);

        return isFirstAdmitted;
    }
}

/// <summary>
/// An allowance that refills at a steady rate up to a ceiling, which is what lets a peer be quiet for a while and then
/// send a short burst without being punished for either.
/// </summary>
internal struct NewfarmTokenBucket
{
    /// <summary>
    /// How much allowance is left, in requests.
    /// </summary>
    public double Tokens;

    /// <summary>
    /// When the allowance was last refilled, in <see cref="NewfarmClock.Milliseconds"/> milliseconds.
    /// </summary>
    public long LastRequestMilliseconds;

    /// <summary>
    /// Creates a full bucket.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="burst">How much allowance the bucket holds when full.</param>
    /// <returns>The new bucket.</returns>
    public static NewfarmTokenBucket Create(long nowMilliseconds, uint burst)
    {
        return new NewfarmTokenBucket
        {
            Tokens = burst,
            LastRequestMilliseconds = nowMilliseconds,
        };
    }

    /// <summary>
    /// Refills for the time that has passed and takes one request's worth.
    /// </summary>
    /// <param name="nowMilliseconds">The current <see cref="NewfarmClock.Milliseconds"/> reading.</param>
    /// <param name="requestsPerSecond">How fast the allowance refills.</param>
    /// <param name="burst">The ceiling the allowance refills to.</param>
    /// <returns><see langword="true"/> when there was allowance to take.</returns>
    public bool TryTake(long nowMilliseconds, uint requestsPerSecond, uint burst)
    {
        long elapsedMilliseconds = nowMilliseconds - LastRequestMilliseconds;

        if (elapsedMilliseconds > 0)
        {
            Tokens += elapsedMilliseconds * requestsPerSecond / 1000.0;

            if (Tokens > burst)
                Tokens = burst;
        }

        LastRequestMilliseconds = nowMilliseconds;

        if (Tokens < 1.0)
            return false;

        Tokens -= 1.0;

        return true;
    }
}
