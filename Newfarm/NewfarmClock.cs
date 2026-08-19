using System.Diagnostics;

namespace Newfarm;

/// <summary>
/// A monotonic millisecond clock, used for every heartbeat, deadline and eviction newfarm measures.
/// </summary>
/// <remarks>
/// Built on <see cref="Stopwatch"/> rather than <c>NewfarmClock.Milliseconds</c> so the client compiles for
/// netstandard2.1, which is what a game engine consumes it as. Monotonic matters here: a wall-clock reading could
/// move backwards and make a live host look lost, or a lost host look live.
/// </remarks>
internal static class NewfarmClock
{
    /// <summary>
    /// Milliseconds elapsed since an arbitrary fixed point. Only differences between readings are meaningful.
    /// </summary>
    public static long Milliseconds => (long)(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * 1000.0);
}
