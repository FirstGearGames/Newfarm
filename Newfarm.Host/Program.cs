using System;
using System.Threading;
using Newfarm.Server;

namespace Newfarm.Host;

/// <summary>
/// Runs a newfarm directory as a standalone service.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Parses the command line, runs the directory, and stops on Ctrl+C.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>Zero on a clean shutdown, one when the arguments could not be parsed.</returns>
    private static int Main(string[] args)
    {
        if (!TryParseConfig(args, out NewfarmServerConfig? config))
        {
            WriteUsage();

            return 1;
        }

        using CancellationTokenSource cancellationTokenSource = new();

        Console.CancelKeyPress += (_, consoleCancelEventArgs) =>
        {
            consoleCancelEventArgs.Cancel = true;

            cancellationTokenSource.Cancel();
        };

        using NewfarmServer server = new(config!);

        server.Logged += Console.WriteLine;

        server.Run(cancellationTokenSource.Token);

        Console.WriteLine("Newfarm stopped.");

        return 0;
    }

    /// <summary>
    /// Builds a configuration from the command line.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="config">When this returns <see langword="true"/>, the configuration that was parsed.</param>
    /// <returns><see langword="true"/> when every argument was understood.</returns>
    private static bool TryParseConfig(string[] args, out NewfarmServerConfig? config)
    {
        config = new NewfarmServerConfig();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p":
                case "--port":
                    if (!TryReadUInt32(args, ref i, out uint port))
                        return false;

                    config.Port = (int)port;
                    break;

                case "--host-timeout-ms":
                    if (!TryReadUInt32(args, ref i, out uint hostTimeoutMilliseconds))
                        return false;

                    config.HostTimeoutMilliseconds = hostTimeoutMilliseconds;
                    break;

                case "--election-deadline-ms":
                    if (!TryReadUInt32(args, ref i, out uint electionDeadlineMilliseconds))
                        return false;

                    config.ElectionDeadlineMilliseconds = electionDeadlineMilliseconds;
                    break;

                case "--credential-grace-ms":
                    if (!TryReadUInt32(args, ref i, out uint credentialGraceMilliseconds))
                        return false;

                    config.CredentialGraceMilliseconds = credentialGraceMilliseconds;
                    break;

                case "--waiter-timeout-ms":
                    if (!TryReadUInt32(args, ref i, out uint waiterTimeoutMilliseconds))
                        return false;

                    config.WaiterTimeoutMilliseconds = waiterTimeoutMilliseconds;
                    break;

                case "--hostless-grace-ms":
                    if (!TryReadUInt32(args, ref i, out uint hostlessGraceMilliseconds))
                        return false;

                    config.HostlessGraceMilliseconds = hostlessGraceMilliseconds;
                    break;

                case "--challenge-interval-ms":
                    if (!TryReadUInt32(args, ref i, out uint hostChallengeIntervalMilliseconds))
                        return false;

                    config.HostChallengeIntervalMilliseconds = hostChallengeIntervalMilliseconds;
                    break;

                case "--challenge-cooldown-ms":
                    if (!TryReadUInt32(args, ref i, out uint hostChallengeCooldownMilliseconds))
                        return false;

                    config.HostChallengeCooldownMilliseconds = hostChallengeCooldownMilliseconds;
                    break;

                case "--max-ineffective-challenges":
                    if (!TryReadUInt32(args, ref i, out uint maximumIneffectiveHostChallenges))
                        return false;

                    config.MaximumIneffectiveHostChallenges = maximumIneffectiveHostChallenges;
                    break;

                case "--keep-alive-ms":
                    if (!TryReadUInt32(args, ref i, out uint waitingKeepAliveIntervalMilliseconds))
                        return false;

                    config.WaitingKeepAliveIntervalMilliseconds = waitingKeepAliveIntervalMilliseconds;
                    break;

                case "--sweep-interval-ms":
                    if (!TryReadUInt32(args, ref i, out uint sweepIntervalMilliseconds))
                        return false;

                    config.SweepIntervalMilliseconds = sweepIntervalMilliseconds;
                    break;

                case "--max-sessions":
                    if (!TryReadUInt32(args, ref i, out uint maximumConcurrentSessions))
                        return false;

                    config.MaximumConcurrentSessions = (int)maximumConcurrentSessions;
                    break;

                case "--max-waiters":
                    if (!TryReadUInt32(args, ref i, out uint maximumWaitersPerSession))
                        return false;

                    config.MaximumWaitersPerSession = (int)maximumWaitersPerSession;
                    break;

                case "--peer-rate":
                    if (!TryReadUInt32(args, ref i, out uint maximumRequestsPerSecondPerPeer))
                        return false;

                    config.MaximumRequestsPerSecondPerPeer = maximumRequestsPerSecondPerPeer;
                    break;

                case "--peer-burst":
                    if (!TryReadUInt32(args, ref i, out uint requestBurstPerPeer))
                        return false;

                    config.RequestBurstPerPeer = requestBurstPerPeer;
                    break;

                case "--address-rate":
                    if (!TryReadUInt32(args, ref i, out uint maximumRequestsPerSecondPerAddress))
                        return false;

                    config.MaximumRequestsPerSecondPerAddress = maximumRequestsPerSecondPerAddress;
                    break;

                case "--address-burst":
                    if (!TryReadUInt32(args, ref i, out uint requestBurstPerAddress))
                        return false;

                    config.RequestBurstPerAddress = requestBurstPerAddress;
                    break;

                case "--max-tracked-peers":
                    if (!TryReadUInt32(args, ref i, out uint maximumTrackedPeers))
                        return false;

                    config.MaximumTrackedPeers = (int)maximumTrackedPeers;
                    break;

                case "--max-tracked-addresses":
                    if (!TryReadUInt32(args, ref i, out uint maximumTrackedAddresses))
                        return false;

                    config.MaximumTrackedAddresses = (int)maximumTrackedAddresses;
                    break;

                case "-h":
                case "--help":
                    config = null;
                    return false;

                default:
                    config = null;
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the unsigned value following an option.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="index">The index of the option, advanced past its value.</param>
    /// <param name="value">When this returns <see langword="true"/>, the value that was read.</param>
    /// <returns><see langword="true"/> when a value was present and parsed.</returns>
    private static bool TryReadUInt32(string[] args, ref int index, out uint value)
    {
        value = 0;

        if (index + 1 >= args.Length)
            return false;

        index++;

        return uint.TryParse(args[index], out value);
    }

    /// <summary>
    /// Writes the supported options to the console.
    /// </summary>
    private static void WriteUsage()
    {
        NewfarmServerConfig defaults = new();

        Console.WriteLine("Usage: Newfarm.Host [options]");
        Console.WriteLine();
        Console.WriteLine($"  -p, --port <int>                    UDP port to bind (default: {NewfarmServerConfig.DefaultPort})");
        Console.WriteLine();
        Console.WriteLine("Timings");
        Console.WriteLine($"  --host-timeout-ms <uint>            Host silence before a replacement is elected (default: {defaults.HostTimeoutMilliseconds})");
        Console.WriteLine($"  --waiter-timeout-ms <uint>          Waiter silence before it is forgotten (default: {defaults.WaiterTimeoutMilliseconds})");
        Console.WriteLine($"  --election-deadline-ms <uint>       How long an elected peer has to publish (default: {defaults.ElectionDeadlineMilliseconds})");
        Console.WriteLine($"  --credential-grace-ms <uint>        How long a credential outlives its host (default: {defaults.CredentialGraceMilliseconds})");
        Console.WriteLine($"  --hostless-grace-ms <uint>          How long a hostless session survives; must outlast the slowest");
        Console.WriteLine($"                                      client's own discovery of the dead host (default: {defaults.HostlessGraceMilliseconds})");
        Console.WriteLine($"  --keep-alive-ms <uint>              How often a waiting peer is told it is still queued (default: {defaults.WaitingKeepAliveIntervalMilliseconds})");
        Console.WriteLine($"  --sweep-interval-ms <uint>          Sweep period, which is also the loop's tick (default: {defaults.SweepIntervalMilliseconds})");
        Console.WriteLine();
        Console.WriteLine("Proving a host is still hosting");
        Console.WriteLine($"  --challenge-interval-ms <uint>      How long a challenged host has to answer (default: {defaults.HostChallengeIntervalMilliseconds})");
        Console.WriteLine($"  --challenge-cooldown-ms <uint>      Least time between challenges to one host, whatever its peers");
        Console.WriteLine($"                                      report (default: {defaults.HostChallengeCooldownMilliseconds})");
        Console.WriteLine($"  --max-ineffective-challenges <uint> Unanswered rounds before a host is stood down (default: {defaults.MaximumIneffectiveHostChallenges})");
        Console.WriteLine();
        Console.WriteLine("Limits. Raise these for a deployment serving many players behind one address,");
        Console.WriteLine("such as a school, an office, or a carrier-grade NAT.");
        Console.WriteLine($"  --max-sessions <uint>               Concurrent session cap; 0 = unlimited (default: {defaults.MaximumConcurrentSessions})");
        Console.WriteLine($"  --max-waiters <uint>                Peers that may wait on one session (default: {defaults.MaximumWaitersPerSession})");
        Console.WriteLine($"  --peer-rate <uint>                  Requests a second from one peer (default: {defaults.MaximumRequestsPerSecondPerPeer})");
        Console.WriteLine($"  --peer-burst <uint>                 Requests at once from one peer (default: {defaults.RequestBurstPerPeer})");
        Console.WriteLine($"  --address-rate <uint>               Requests a second from one address (default: {defaults.MaximumRequestsPerSecondPerAddress})");
        Console.WriteLine($"  --address-burst <uint>              Requests at once from one address (default: {defaults.RequestBurstPerAddress})");
        Console.WriteLine($"  --max-tracked-peers <uint>          Peers tracked separately for rate limiting (default: {defaults.MaximumTrackedPeers})");
        Console.WriteLine($"  --max-tracked-addresses <uint>      Addresses tracked separately (default: {defaults.MaximumTrackedAddresses})");
        Console.WriteLine();
        Console.WriteLine("  -h, --help");
    }
}
