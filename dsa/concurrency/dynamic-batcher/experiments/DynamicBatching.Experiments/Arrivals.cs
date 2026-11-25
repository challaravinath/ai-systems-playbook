using System.Globalization;

namespace DynamicBatching.Experiments;

// Arrival times in ticks (1 tick = 100 ns) after the start, sorted.
public static class Arrivals
{
    // Simulated: a Poisson process, so the gaps between requests are exponential.
    public static long[] Poisson(double perSecond, int count, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var random = new Random(seed);
        var times = new long[count];
        double t = 0;
        for (int i = 0; i < count; i++)
        {
            t += -Math.Log(1 - random.NextDouble()) / perSecond;
            times[i] = (long)(t * TimeSpan.TicksPerSecond);
        }
        return times;
    }

    // Real: one clock hour of the Azure LLM inference trace 2024, the hour with the most requests.
    // Only the timestamps are used; the token counts are for LLMs and don't apply here.
    public static TraceHour BusiestHour(string path)
    {
        using var reader = new StreamReader(path);
        return BusiestHour(reader);
    }

    public static TraceHour BusiestHour(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        const string header = "TIMESTAMP,ContextTokens,GeneratedTokens";
        const string format = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz";
        if (reader.ReadLine()?.Trim() != header)
        {
            throw new FormatException($"expected header '{header}'");
        }

        var all = new List<long>();
        int lineNo = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (line.Length == 0)
            {
                continue;
            }
            int comma = line.IndexOf(',');
            if (comma < 0 || !DateTimeOffset.TryParseExact(line.AsSpan(0, comma), format,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
            {
                throw new FormatException($"line {lineNo} isn't a trace row: '{line}'");
            }
            all.Add(ts.UtcTicks);
        }
        if (all.Count == 0)
        {
            throw new FormatException("no rows in the trace");
        }
        all.Sort();

        // Count per clock hour, pick the largest (earliest on a tie).
        var perHour = new Dictionary<long, int>();
        foreach (long t in all)
        {
            long hour = t - t % TimeSpan.TicksPerHour;
            perHour[hour] = perHour.GetValueOrDefault(hour) + 1;
        }
        long busiest = perHour.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;

        long[] times = all.Where(t => t >= busiest && t < busiest + TimeSpan.TicksPerHour)
            .Select(t => t - busiest)
            .ToArray();
        return new TraceHour(new DateTime(busiest, DateTimeKind.Utc), all.Count, times);
    }

    // The same arrivals, played `factor` times faster. The bursts keep their shape.
    public static long[] SpeedUp(long[] times, int factor)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        return times.Select(t => t / factor).ToArray();
    }

    // Most arrivals inside any window of the given length: sort, then a sliding window.
    public static int Busiest(long[] sortedTimes, TimeSpan window)
    {
        int best = 0;
        for (int start = 0, end = 0; end < sortedTimes.Length; end++)
        {
            while (sortedTimes[end] - sortedTimes[start] >= window.Ticks)
            {
                start++;
            }
            best = Math.Max(best, end - start + 1);
        }
        return best;
    }
}

public sealed record TraceHour(DateTime HourUtc, int TraceRequests, long[] Times);
