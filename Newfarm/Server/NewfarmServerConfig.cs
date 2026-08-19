namespace Newfarm.Server;

/// <summary>
/// Configuration for a <see cref="NewfarmServer"/> instance. Every field has a working default; override only what
/// the deployment needs.
/// </summary>
public sealed class NewfarmServerConfig
{
    // ReSharper disable FieldCanBeMadeReadOnly.Global

    /// <summary>
    /// The UDP port to bind.
    /// </summary>
    public int Port = DefaultPort;

    /// <summary>
    /// How long a hosting peer may go without a heartbeat before newfarm treats the host as gone and starts
    /// electing a replacement.
    /// </summary>
    /// <remarks>
    /// This is deliberately far shorter than a relay's own timeout, because it is the whole reason a peer learns
    /// about a dead host quickly rather than waiting for the relay to notice.
    /// </remarks>
    public uint HostTimeoutMilliseconds = 5000;

    /// <summary>
    /// How long a waiting peer may go without a heartbeat before newfarm forgets it, so an election is never handed
    /// to a peer that has already gone.
    /// </summary>
    public uint WaiterTimeoutMilliseconds = 5000;

    /// <summary>
    /// How long an elected peer has to publish a credential before its election is withdrawn and the next waiter is
    /// elected in its place.
    /// </summary>
    public uint ElectionDeadlineMilliseconds = 30000;

    /// <summary>
    /// How long a session with no live host survives after a credential was published, so a peer that was slow to
    /// notice the handover still receives the credential.
    /// </summary>
    public uint CredentialGraceMilliseconds = 60000;

    /// <summary>
    /// How long a session with no live host, no election and nobody waiting survives before newfarm forgets it,
    /// measured from the moment its host went quiet.
    /// </summary>
    /// <remarks>
    /// This has to outlast the slowest client's own discovery of the dead host, because a client arriving after the
    /// session was forgotten cannot be told anything at all. That discovery comes from the relay rather than from
    /// newfarm, so the window is sized against a relay's host timeout, not against
    /// <see cref="HostTimeoutMilliseconds"/>.
    /// </remarks>
    public uint HostlessGraceMilliseconds = 120000;

    /// <summary>
    /// How long a challenged host has to publish a credential before that challenge round is closed against it.
    /// </summary>
    /// <remarks>
    /// A heartbeat says a peer's connection works, which is not the same as it still being able to host: its game may
    /// have wedged, its room may be gone, or its route to the relay may have failed while its route to newfarm did
    /// not. When peers report they cannot reach the host, newfarm asks the host to prove it by publishing a
    /// credential, which tests the capability actually in question rather than a proxy for it.
    /// </remarks>
    public uint HostChallengeIntervalMilliseconds = 3000;

    /// <summary>
    /// The least time newfarm leaves a host alone between challenges, however many peers are reporting it
    /// unreachable and however often each of them reports.
    /// </summary>
    /// <remarks>
    /// The cost of a challenge is borne by the host, and the number of peers able to trigger one is the size of the
    /// session, so without a limit here a room full of peers whose links all dropped at once would each turn into a
    /// demand on the one machine least able to spare the attention. One challenge per session per cooldown is all
    /// newfarm ever needs: the answer is the same whoever asked for it.
    /// Measured from when a challenge was sent, so a value below
    /// <see cref="HostChallengeIntervalMilliseconds"/> leaves the round length as the real spacing.
    /// </remarks>
    public uint HostChallengeCooldownMilliseconds = 10000;

    /// <summary>
    /// How many challenge rounds may close without the host answering before newfarm stands it down and elects a
    /// replacement, however healthily it is heartbeating.
    /// </summary>
    /// <remarks>
    /// More than one, because a single round can close against a host that was merely slow. A host that does answer
    /// keeps the session however many peers are complaining, since newfarm cannot tell a host nobody can reach from a
    /// peer that can reach nobody, and only one of those two is worth taking a session away over.
    /// </remarks>
    public uint MaximumIneffectiveHostChallenges = 2;

    /// <summary>
    /// How often a waiting peer is told it is still queued, so silence from newfarm is distinguishable from newfarm
    /// being unreachable.
    /// </summary>
    public uint WaitingKeepAliveIntervalMilliseconds = 1000;

    /// <summary>
    /// How often the server sweeps sessions for lapsed heartbeats, expired elections and evictions. It also bounds
    /// how long a receive blocks, so it doubles as the loop's tick.
    /// </summary>
    public uint SweepIntervalMilliseconds = 250;

    /// <summary>
    /// The most sessions newfarm will hold at once.
    /// <see cref="UnlimitedConcurrentSessions"/> removes the cap.
    /// </summary>
    public int MaximumConcurrentSessions = UnlimitedConcurrentSessions;

    /// <summary>
    /// The port newfarm binds when <see cref="Port"/> is left alone.
    /// </summary>
    public const int DefaultPort = 47778;

    /// <summary>
    /// Sentinel value: pass as <see cref="MaximumConcurrentSessions"/> to hold as many sessions as memory allows.
    /// </summary>
    public const int UnlimitedConcurrentSessions = 0;
}
