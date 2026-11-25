namespace DynamicBatching.Tests;

public class DynamicBatcherTests
{
    [Fact]
    public async Task A_full_batch_runs_at_once_without_waiting()
    {
        await using var h = new Harness(maxBatchSize: 3, maxWaitMs: 60_000);

        var tasks = new[] { h.Submit(1), h.Submit(2), h.Submit(3) };

        Assert.Equal(new[] { 10, 20, 30 }, await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("0:1,2,3", h.Describe());
    }

    [Fact]
    public async Task A_partial_batch_runs_when_the_oldest_has_waited_MaxWait()
    {
        await using var h = new Harness(maxBatchSize: 5, maxWaitMs: 10);

        Task<int> a = h.Submit(1);
        h.AdvanceMs(6);
        Task<int> b = h.Submit(2);
        h.AdvanceMs(3);
        Assert.Empty(h.Batches);   // t = 9: A has waited 9 ms, B 3 ms

        h.AdvanceMs(1);            // t = 10: A has waited 10 ms

        Assert.Equal(10, await a.Within());
        Assert.Equal(20, await b.Within());
        Assert.Equal("10:1,2", h.Describe());   // closed on A's clock, not B's
    }

    [Fact]
    public async Task MaxWait_zero_runs_whatever_is_there_straight_away()
    {
        await using var h = new Harness(maxBatchSize: 5, maxWaitMs: 0);

        Assert.Equal(10, await h.Submit(1).Within());
        Assert.Equal("0:1", h.Describe());
    }

    [Fact]
    public async Task Each_caller_gets_the_output_at_its_own_position()
    {
        await using var h = new Harness(maxBatchSize: 4, maxWaitMs: 60_000);

        var tasks = new[] { h.Submit(7), h.Submit(3), h.Submit(9), h.Submit(1) };

        Assert.Equal(new[] { 70, 30, 90, 10 }, await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_full_queue_turns_the_next_request_away_straight_away()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var h = new Harness(maxBatchSize: 1, maxWaitMs: 0, queueCapacity: 1,
            runBatch: async xs =>
            {
                started.TrySetResult();
                await release.Task;
                return xs.Select(x => x * 10).ToArray();
            });

        Task<int> first = h.Batcher.SubmitAsync(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));   // the loop is busy with 1
        Task<int> second = h.Batcher.SubmitAsync(2);             // waits in the queue (capacity 1)
        Task<int> third = h.Batcher.SubmitAsync(3);              // no room

        var ex = await Assert.ThrowsAsync<QueueFullException>(() => third);
        Assert.Equal(1, ex.Capacity);

        release.SetResult();
        Assert.Equal(10, await first.Within());
        Assert.Equal(20, await second.Within());
    }

    [Fact]
    public async Task If_the_loop_was_busy_an_overdue_request_runs_as_soon_as_it_is_free()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Harness? h = null;
        await using var harness = h = new Harness(maxBatchSize: 2, maxWaitMs: 10,
            runBatch: async xs =>
            {
                if (xs[0] == 1)
                {
                    started.TrySetResult();
                    await release.Task;
                }
                return await h!.Record(xs);
            });

        Task<int> first = h.Submit(1);
        h.Clock.Advance(TimeSpan.FromMilliseconds(10));         // batch [1] starts and blocks
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<int> second = h.Batcher.SubmitAsync(2);            // arrives at t = 10
        h.Clock.Advance(TimeSpan.FromMilliseconds(15));         // t = 25: 2 has waited 15 ms
        release.SetResult();

        Assert.Equal(20, await second.Within());                // no more time needed
        Assert.Equal(10, await first.Within());
        Assert.Equal(25, h.Batches.Single(b => b.Items[0] == 2).AtMs);
    }

    [Fact]
    public async Task A_failed_batch_fails_only_its_own_callers()
    {
        await using var h = new Harness(maxBatchSize: 2, maxWaitMs: 60_000, runBatch: xs =>
            xs.Contains(99)
                ? throw new InvalidOperationException("model crashed")
                : Task.FromResult<IReadOnlyList<int>>(xs.Select(x => x * 10).ToArray()));

        Task<int> bad1 = h.Submit(99);
        Task<int> bad2 = h.Submit(1);
        Assert.Equal("model crashed", (await Assert.ThrowsAsync<InvalidOperationException>(() => bad1.Within())).Message);
        Assert.Equal("model crashed", (await Assert.ThrowsAsync<InvalidOperationException>(() => bad2.Within())).Message);

        Task<int> ok1 = h.Submit(2);
        Task<int> ok2 = h.Submit(3);
        Assert.Equal(20, await ok1.Within());
        Assert.Equal(30, await ok2.Within());
    }

    [Fact]
    public async Task The_wrong_number_of_outputs_fails_every_caller_in_the_batch()
    {
        await using var h = new Harness(maxBatchSize: 2, maxWaitMs: 60_000, runBatch: xs =>
            Task.FromResult<IReadOnlyList<int>>([xs[0] * 10]));   // one output for two inputs

        Task<int> a = h.Submit(1);
        Task<int> b = h.Submit(2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Within());
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Within());
    }

    [Fact]
    public async Task A_caller_who_cancels_before_the_batch_runs_is_dropped_from_it()
    {
        await using var h = new Harness(maxBatchSize: 3, maxWaitMs: 10);
        using var cts = new CancellationTokenSource();

        Task<int> a = h.Submit(1);
        Task<int> b = h.Submit(2, cts.Token);
        cts.Cancel();
        h.AdvanceMs(10);

        Assert.Equal(10, await a.Within());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Within());
        Assert.Equal("10:1", h.Describe());   // 2 never reached the model
    }

    [Fact]
    public async Task A_cancelled_request_still_in_the_queue_does_not_take_a_batch_slot()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Harness? h = null;
        await using var harness = h = new Harness(maxBatchSize: 2, maxWaitMs: 0,
            runBatch: async xs =>
            {
                if (xs[0] == 1)
                {
                    started.TrySetResult();
                    await release.Task;
                }
                return await h!.Record(xs);
            });
        using var cts = new CancellationTokenSource();

        Task<int> first = h.Batcher.SubmitAsync(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));   // the loop is busy with [1]
        Task<int> cancelled = h.Batcher.SubmitAsync(2, cts.Token);
        Task<int> c = h.Batcher.SubmitAsync(3);
        Task<int> d = h.Batcher.SubmitAsync(4);
        cts.Cancel();                                            // 2 is still in the queue
        release.SetResult();

        Assert.Equal(30, await c.Within());
        Assert.Equal(40, await d.Within());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Within());
        Assert.Equal("0:1 | 0:3,4", h.Describe());   // 3 and 4 share a batch; 2 took no slot
    }

    [Fact]
    public async Task An_already_cancelled_request_is_never_queued()
    {
        await using var h = new Harness(maxBatchSize: 1, maxWaitMs: 0);

        Task<int> t = h.Batcher.SubmitAsync(1, new CancellationToken(canceled: true));

        Assert.True(t.IsCanceled);
        h.AdvanceMs(5);
        Assert.Empty(h.Batches);
    }

    [Fact]
    public async Task Dispose_runs_what_is_queued_then_refuses_new_requests()
    {
        var h = new Harness(maxBatchSize: 10, maxWaitMs: 60_000);
        var tasks = new[] { h.Submit(1), h.Submit(2), h.Submit(3) };

        await h.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 10, 20, 30 }, await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Batcher.SubmitAsync(4));
    }

    [Theory]
    [InlineData(0, 10, 10)]    // batch size below 1
    [InlineData(5, -1, 10)]    // negative wait
    [InlineData(5, 10, 4)]     // queue smaller than one batch
    public void Options_reject_settings_that_can_never_work(int size, int waitMs, int capacity)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(
            () => new BatchOptions(size, TimeSpan.FromMilliseconds(waitMs), capacity));
    }
}
