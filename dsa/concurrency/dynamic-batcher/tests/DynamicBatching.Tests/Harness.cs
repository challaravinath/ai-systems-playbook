using Microsoft.Extensions.Time.Testing;

namespace DynamicBatching.Tests;

// A batcher on a fake clock. Time only moves when a test moves it, so every timing is exact.
// The default batch function records each batch (when it ran, what was in it) and returns input * 10.
internal sealed class Harness : IAsyncDisposable
{
    private readonly DateTimeOffset _start;
    private readonly Lock _gate = new();
    private readonly List<(long AtMs, int[] Items)> _batches = [];

    public Harness(int maxBatchSize, int maxWaitMs, int queueCapacity = 1000,
        Func<IReadOnlyList<int>, Task<IReadOnlyList<int>>>? runBatch = null, FakeTimeProvider? clock = null)
    {
        Clock = clock ?? new FakeTimeProvider();
        _start = Clock.GetUtcNow();
        Batcher = new DynamicBatcher<int, int>(
            new BatchOptions(maxBatchSize, TimeSpan.FromMilliseconds(maxWaitMs), queueCapacity),
            runBatch ?? Record,
            Clock);

        // Let the loop start and park before the test does anything. Without this, a request sent
        // while the loop is still starting could make Settle read a stale "idle" and move time too early.
        Settle();
    }

    public FakeTimeProvider Clock { get; }
    public DynamicBatcher<int, int> Batcher { get; }

    public long NowMs => (long)(Clock.GetUtcNow() - _start).TotalMilliseconds;

    public IReadOnlyList<(long AtMs, int[] Items)> Batches
    {
        get { lock (_gate) { return [.. _batches]; } }
    }

    // "10:1,2 | 25:3" = a batch of 1 and 2 at 10 ms, then a batch of 3 at 25 ms.
    public string Describe() =>
        string.Join(" | ", Batches.Select(b => $"{b.AtMs}:{string.Join(",", b.Items)}"));

    public Task<IReadOnlyList<int>> Record(IReadOnlyList<int> inputs)
    {
        lock (_gate)
        {
            _batches.Add((NowMs, [.. inputs]));
        }
        return Task.FromResult<IReadOnlyList<int>>(inputs.Select(x => x * 10).ToArray());
    }

    // Wait (in real time) until the loop has nothing left to do at the current fake time.
    public void Settle()
    {
        // SpinOnce(-1) never sleeps. A 1 ms sleep is about 15 ms on Windows, which made the random tests crawl.
        var spin = new SpinWait();
        long giveUp = Environment.TickCount64 + 5000;
        while (!Batcher.IsIdle)
        {
            if (Environment.TickCount64 > giveUp)
            {
                throw new TimeoutException("the batch loop never went idle");
            }
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    // Move the clock one millisecond at a time, so a deadline fires at its own time, not later.
    public void AdvanceMs(int ms)
    {
        for (int i = 0; i < ms; i++)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(1));
            Settle();
        }
    }

    public Task<int> Submit(int x, CancellationToken token = default)
    {
        Task<int> t = Batcher.SubmitAsync(x, token);
        Settle();
        return t;
    }

    public ValueTask DisposeAsync() => Batcher.DisposeAsync();
}

internal static class TaskExtensions
{
    public static Task<T> Within<T>(this Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(5));
}
