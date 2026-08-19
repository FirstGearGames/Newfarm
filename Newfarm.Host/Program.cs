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

                case "--max-sessions":
                    if (!TryReadUInt32(args, ref i, out uint maximumConcurrentSessions))
                        return false;

                    config.MaximumConcurrentSessions = (int)maximumConcurrentSessions;
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
        Console.WriteLine("Usage: Newfarm.Host [options]");
        Console.WriteLine($"  -p, --port <int>              UDP port to bind (default: {NewfarmServerConfig.DefaultPort})");
        Console.WriteLine("  --host-timeout-ms <uint>      Host heartbeat timeout before a replacement is elected (default: 5000)");
        Console.WriteLine("  --election-deadline-ms <uint> How long an elected peer has to publish a credential (default: 30000)");
        Console.WriteLine("  --credential-grace-ms <uint>  How long a credential outlives its host (default: 60000)");
        Console.WriteLine("  --max-sessions <uint>         Concurrent session cap; 0 = unlimited (default: 0)");
        Console.WriteLine("  -h, --help");
    }
}
