namespace DynamicBatching.Experiments;

// The batch function we hand to the real DynamicBatcher. It runs one batch at a time, like one GPU.
// It doesn't finish on its own: the driver ends each batch when the fake clock reaches
// start + cost, so the time a batch takes comes only from the cost table.
internal sealed class SimulatedGpu(BatchCost cost, Func<long> now)
{
    private TaskCompletionSource<IReadOnlyList<int>>? _running;
    private int[] _ids = [];

    public long[] Started { get; private set; } = [];
    public long[] Finished { get; private set; } = [];
    public int Batches { get; private set; }
    public long BusyTicks { get; private set; }

    // True from the moment a batch is handed over until the driver finishes it.
    public volatile bool Running;
    public long FinishAt { get; private set; }

    // The ids of the batch now running, so the driver can check they're the oldest waiting ones.
    public IReadOnlyList<int> Current => _ids;

    public void Size(int requests)
    {
        Started = new long[requests];
        Finished = new long[requests];
    }

    public Task<IReadOnlyList<int>> RunAsync(IReadOnlyList<int> ids)
    {
        if (Running)
        {
            throw new InvalidOperationException("two batches at once on one GPU");
        }
        long start = now();
        long ticks = (long)Math.Round(cost.Ms(ids.Count) * TimeSpan.TicksPerMillisecond);
        _ids = [.. ids];
        foreach (int id in _ids)
        {
            Started[id] = start;
        }
        FinishAt = start + ticks;
        BusyTicks += ticks;
        Batches++;

        // RunContinuationsAsynchronously: when the driver finishes the batch, the batcher's loop
        // carries on on its own thread, not inside the driver.
        var batch = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = batch;

        // Last: once Running is true the driver may finish this batch at any moment, even before
        // this method returns, so nothing after this line may touch the fields.
        Running = true;
        return batch.Task;
    }

    public void Finish()
    {
        var running = _running ?? throw new InvalidOperationException("no batch is running");
        foreach (int id in _ids)
        {
            Finished[id] = FinishAt;
        }
        _running = null;
        Running = false;
        running.SetResult(_ids);
    }
}
