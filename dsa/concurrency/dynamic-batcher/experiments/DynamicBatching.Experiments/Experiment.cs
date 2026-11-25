using Microsoft.Extensions.Time.Testing;

namespace DynamicBatching.Experiments;

public sealed record RunResult(
    int Requests,
    int Batches,
    double MeanBatch,
    double P50Ms,
    double P99Ms,
    double MaxMs,
    double ThroughputPerSecond,
    double GpuBusyPercent);

// Runs the real DynamicBatcher on a fake clock, with a simulated GPU, over a list of arrival times.
//
// Time jumps straight to the next thing that can happen. There are only three:
//   - the next request arrives
//   - the GPU finishes its batch
//   - the oldest waiting request has waited T (only matters while the GPU is free)
// Before each jump the driver waits (in real time) until the batcher has nothing left to do at
// the current fake time, so the run gives the same numbers every time.
// At equal times: the GPU finishes first, then the timer fires, then new requests arrive.
public static class Experiment
{
    public static async Task<RunResult> RunAsync(BatchCost cost, int maxBatchSize, TimeSpan maxWait, long[] arrivals)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentNullException.ThrowIfNull(arrivals);
        if (arrivals.Length == 0)
        {
            throw new ArgumentException("no arrivals", nameof(arrivals));
        }
        for (int i = 1; i < arrivals.Length; i++)
        {
            if (arrivals[i] < arrivals[i - 1])
            {
                throw new ArgumentException("arrivals must be sorted", nameof(arrivals));
            }
        }

        var clock = new FakeTimeProvider();
        long now = 0;
        var gpu = new SimulatedGpu(cost, () => Volatile.Read(ref now));
        gpu.Size(arrivals.Length);

        // The queue holds every request, so nothing is turned away: we're measuring waiting, not rejections.
        var options = new BatchOptions(maxBatchSize, maxWait, queueCapacity: Math.Max(arrivals.Length, maxBatchSize));
        var batcher = new DynamicBatcher<int, int>(options, gpu.RunAsync, clock);
        var answers = new Task<int>[arrivals.Length];
        var waiting = new Queue<int>();   // sent to the batcher, not yet on the GPU
        int next = 0;
        int seenBatches = 0;

        void Settle()
        {
            var spin = new SpinWait();
            long giveUp = Environment.TickCount64 + 10_000;
            while (!batcher.IsIdle && !gpu.Running)
            {
                if (Environment.TickCount64 > giveUp)
                {
                    throw new TimeoutException("the batcher never settled");
                }
                spin.SpinOnce(sleep1Threshold: -1);
            }

            // A batch that just started must be the oldest waiting requests, in order.
            if (gpu.Batches != seenBatches)
            {
                seenBatches = gpu.Batches;
                foreach (int id in gpu.Current)
                {
                    if (waiting.Dequeue() != id)
                    {
                        throw new InvalidOperationException("a batch skipped the queue order");
                    }
                }
            }
        }

        Settle();
        while (next < arrivals.Length || waiting.Count > 0 || gpu.Running)
        {
            long arrive = next < arrivals.Length ? arrivals[next] : long.MaxValue;
            long finish = gpu.Running ? gpu.FinishAt : long.MaxValue;
            long deadline = !gpu.Running && waiting.Count > 0 ? arrivals[waiting.Peek()] + maxWait.Ticks : long.MaxValue;
            long at = Math.Min(arrive, Math.Min(finish, deadline));
            if (at == long.MaxValue || at < now || (at == now && arrive != now))
            {
                // Something is due now or earlier and the batcher didn't act on it: a bug, not a result.
                throw new InvalidOperationException($"the batcher is stuck at {now} ticks");
            }

            if (at > now)
            {
                // Advance fires the batcher's timer if it's due.
                long step = at - now;
                Volatile.Write(ref now, at);
                clock.Advance(TimeSpan.FromTicks(step));
            }
            if (gpu.Running && gpu.FinishAt == now)
            {
                gpu.Finish();
            }
            Settle();

            while (next < arrivals.Length && arrivals[next] == now)
            {
                answers[next] = batcher.SubmitAsync(next);
                waiting.Enqueue(next);
                next++;
            }
            Settle();
        }

        await batcher.DisposeAsync();
        await Task.WhenAll(answers);   // every request got its own id back, or this throws

        return Summarise(arrivals, gpu);
    }

    private static RunResult Summarise(long[] arrivals, SimulatedGpu gpu)
    {
        int n = arrivals.Length;
        var latency = new double[n];
        for (int i = 0; i < n; i++)
        {
            latency[i] = (gpu.Finished[i] - arrivals[i]) / (double)TimeSpan.TicksPerMillisecond;
        }
        Array.Sort(latency);

        long span = gpu.Finished.Max() - arrivals[0];
        return new RunResult(
            Requests: n,
            Batches: gpu.Batches,
            MeanBatch: n / (double)gpu.Batches,
            P50Ms: Percentile(latency, 50),
            P99Ms: Percentile(latency, 99),
            MaxMs: latency[^1],
            ThroughputPerSecond: n / (span / (double)TimeSpan.TicksPerSecond),
            GpuBusyPercent: 100.0 * gpu.BusyTicks / span);
    }

    // Nearest rank: the smallest value with at least p% of the values at or below it.
    public static double Percentile(double[] sorted, double p)
    {
        int rank = (int)Math.Ceiling(p / 100 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
