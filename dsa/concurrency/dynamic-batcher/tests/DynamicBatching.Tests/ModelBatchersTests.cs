using Microsoft.Extensions.Time.Testing;

namespace DynamicBatching.Tests;

public class ModelBatchersTests
{
    // Each model scales its input differently, so a mixed-up answer shows at once.
    private static ModelSetup<int, int> Model(string name, int scale, int n, int waitMs,
        List<string>? log = null, Func<Task>? before = null) =>
        new(name, new BatchOptions(n, TimeSpan.FromMilliseconds(waitMs), queueCapacity: 1000), async xs =>
        {
            if (before is not null)
            {
                await before();
            }
            if (log is not null)
            {
                lock (log)
                {
                    log.Add($"{name}:{string.Join(",", xs)}");
                }
            }
            return xs.Select(x => x * scale).ToArray();
        });

    private static void SettleAll(ModelBatchers<int, int> models)
    {
        var spin = new SpinWait();
        long giveUp = Environment.TickCount64 + 5000;
        while (!models.Batchers.All(b => b.IsIdle))
        {
            if (Environment.TickCount64 > giveUp)
            {
                throw new TimeoutException("a model's loop never went idle");
            }
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    [Fact]
    public async Task Requests_reach_their_own_model_and_batches_never_mix()
    {
        var log = new List<string>();
        await using var models = new ModelBatchers<int, int>(
        [
            Model("embed-a", scale: 10, n: 2, waitMs: 60_000, log),
            Model("embed-b", scale: 100, n: 2, waitMs: 60_000, log),
        ]);

        var a1 = models.SubmitAsync("embed-a", 1);
        var b1 = models.SubmitAsync("embed-b", 1);
        var a2 = models.SubmitAsync("embed-a", 2);
        var b2 = models.SubmitAsync("embed-b", 2);

        Assert.Equal(new[] { 10, 20 }, await Task.WhenAll(a1, a2).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 100, 200 }, await Task.WhenAll(b1, b2).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["embed-a:1,2", "embed-b:1,2"], log.Order());
    }

    [Fact]
    public async Task Each_model_closes_batches_on_its_own_N_and_T()
    {
        var clock = new FakeTimeProvider();
        var log = new List<string>();
        await using var models = new ModelBatchers<int, int>(
        [
            Model("big", scale: 1, n: 2, waitMs: 60_000, log),   // closes on size
            Model("fast", scale: 1, n: 5, waitMs: 10, log),      // closes on time
        ], clock);
        SettleAll(models);

        var big = new[] { models.SubmitAsync("big", 1), models.SubmitAsync("big", 2) };
        var fast = models.SubmitAsync("fast", 3);
        SettleAll(models);
        await Task.WhenAll(big).WaitAsync(TimeSpan.FromSeconds(5));   // full at once, no time passed
        Assert.False(fast.IsCompleted);                                // only 1 of 5, waiting for T

        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(3, await fast.Within());
        Assert.Equal(["big:1,2", "fast:3"], log.Order());
    }

    [Fact]
    public async Task A_stuck_model_does_not_hold_up_another()
    {
        var stuck = new TaskCompletionSource();
        await using var models = new ModelBatchers<int, int>(
        [
            Model("slow", scale: 10, n: 1, waitMs: 0, before: () => stuck.Task),
            Model("quick", scale: 100, n: 1, waitMs: 0),
        ]);

        Task<int> slow = models.SubmitAsync("slow", 1);
        Assert.Equal(200, await models.SubmitAsync("quick", 2).Within());   // answered while "slow" is stuck
        Assert.False(slow.IsCompleted);

        stuck.SetResult();
        Assert.Equal(10, await slow.Within());
    }

    [Fact]
    public async Task An_unknown_model_fails_straight_away()
    {
        await using var models = new ModelBatchers<int, int>([Model("embed", scale: 10, n: 1, waitMs: 0)]);

        var ex = await Assert.ThrowsAsync<UnknownModelException>(() => models.SubmitAsync("Embed", 1));

        Assert.Equal("Embed", ex.Model);   // names match exactly
    }

    [Fact]
    public void A_bad_model_list_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ModelBatchers<int, int>([]));
        Assert.Throws<ArgumentException>(() => new ModelBatchers<int, int>(
            [Model("embed", 10, 1, 0), Model("embed", 20, 1, 0)]));
        Assert.Throws<ArgumentException>(() => new ModelBatchers<int, int>([Model(" ", 10, 1, 0)]));
    }

    [Fact]
    public async Task Dispose_drains_every_model()
    {
        var models = new ModelBatchers<int, int>(
        [
            Model("a", scale: 10, n: 10, waitMs: 60_000),
            Model("b", scale: 100, n: 10, waitMs: 60_000),
        ]);
        var tasks = new[] { models.SubmitAsync("a", 1), models.SubmitAsync("b", 2), models.SubmitAsync("a", 3) };

        await models.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 10, 200, 30 }, await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => models.SubmitAsync("a", 4));
    }

    [Fact]
    public async Task Many_threads_across_models_never_get_another_models_answer()
    {
        string[] names = ["m0", "m1", "m2"];
        int crossed = 0;
        await using var models = new ModelBatchers<int, int>(names.Select((name, m) =>
            new ModelSetup<int, int>(name, new BatchOptions(8, TimeSpan.FromMilliseconds(1), 10_000), async xs =>
            {
                // Inputs carry their model number in the thousands digit; any other number here is a mix-up.
                if (xs.Any(x => x / 1000 != m))
                {
                    Interlocked.Increment(ref crossed);
                }
                await Task.Yield();
                return xs.Select(x => x + 1).ToArray();
            })));

        var work = Enumerable.Range(0, 6).Select(w => Task.Run(async () =>
        {
            for (int i = 0; i < 500; i++)
            {
                int m = (w + i) % 3;
                int x = m * 1000 + i;
                Assert.Equal(x + 1, await models.SubmitAsync(names[m], x));
            }
        }));
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, crossed);
    }
}
