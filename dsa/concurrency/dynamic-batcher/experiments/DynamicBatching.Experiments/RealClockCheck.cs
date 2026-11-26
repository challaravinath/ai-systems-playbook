using System.Diagnostics;

namespace DynamicBatching.Experiments;

public sealed record ClockRow(
    double AskedMs,
    int Samples,
    double MinMs,
    double P50Ms,
    double P99Ms,
    double MaxMs,
    int ClosedEarly,
    double TimersPerRequest,
    int MostTimersForOneRequest);

// Runs the batcher on the machine's real clock and measures how long a lone request really waits
// before its batch runs. N is large, so only the wait T can close the batch.
//
// Each request is sent after a random pause (a busy wait, 0 to 20 ms), so requests land anywhere
// between two ticks of the system clock instead of lining up with them.
// The wait is measured with Stopwatch from just before SubmitAsync until the batch function starts.
public static class RealClockCheck
{
    public static async Task<ClockRow> MeasureAsync(TimeSpan maxWait, int samples, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);
        var random = new Random(seed);
        var clock = new CountingClock();
        var waits = new double[samples];
        var timers = new int[samples];
        long sent = 0;
        int current = 0;
        var ran = new SemaphoreSlim(0);

        await using (var batcher = new DynamicBatcher<int, int>(new BatchOptions(1000, maxWait, 1000), ids =>
        {
            waits[current] = Stopwatch.GetElapsedTime(Volatile.Read(ref sent)).TotalMilliseconds;
            timers[current] = clock.TakeCount();
            ran.Release();
            return Task.FromResult<IReadOnlyList<int>>(ids.ToArray());
        }, clock))
        {
            for (current = 0; current < samples; current++)
            {
                SpinFor(TimeSpan.FromMilliseconds(random.NextDouble() * 20));
                clock.TakeCount();
                Volatile.Write(ref sent, Stopwatch.GetTimestamp());
                _ = batcher.SubmitAsync(current);
                if (!await ran.WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException($"request {current} never ran");
                }
            }
        }

        double asked = maxWait.TotalMilliseconds;
        int early = waits.Count(w => w < asked);
        var sorted = waits.Order().ToArray();
        return new ClockRow(
            AskedMs: asked,
            Samples: samples,
            MinMs: sorted[0],
            P50Ms: Experiment.Percentile(sorted, 50),
            P99Ms: Experiment.Percentile(sorted, 99),
            MaxMs: sorted[^1],
            ClosedEarly: early,
            TimersPerRequest: timers.Average(),
            MostTimersForOneRequest: timers.Max());
    }

    private static void SpinFor(TimeSpan time)
    {
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < time)
        {
            Thread.SpinWait(20);
        }
    }

    // The system clock, unchanged, except that it counts the timers created through it.
    private sealed class CountingClock : TimeProvider
    {
        private int _timers;

        public int TakeCount() => Interlocked.Exchange(ref _timers, 0);

        public override long GetTimestamp() => System.GetTimestamp();
        public override long TimestampFrequency => System.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow();
        public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timers);
            return System.CreateTimer(callback, state, dueTime, period);
        }
    }
}
