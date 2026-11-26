using System.Threading.Channels;

namespace DynamicBatching;

// Callers send one input at a time. One loop groups them and runs a batch when it has
// MaxBatchSize inputs, or when the oldest one has waited MaxWait, whichever comes first.
// Each caller gets back its own result.
//
// Fits models that answer in one pass (embeddings, rerankers, classifiers).
// LLM text generation needs continuous batching instead, because requests finish at different steps.
public sealed class DynamicBatcher<TIn, TOut> : IAsyncDisposable
{
    private readonly BatchOptions _options;
    private readonly Func<IReadOnlyList<TIn>, Task<IReadOnlyList<TOut>>> _runBatch;
    private readonly TimeProvider _clock;
    private readonly Channel<Pending> _queue;
    private readonly Task _loop;
    private volatile bool _closed;
    private volatile bool _idle;

    // runBatch gets the inputs in arrival order and must return one output per input, in the same order.
    public DynamicBatcher(
        BatchOptions options,
        Func<IReadOnlyList<TIn>, Task<IReadOnlyList<TOut>>> runBatch,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runBatch);
        _options = options;
        _runBatch = runBatch;
        _clock = clock ?? TimeProvider.System;

        // One reader (the loop), many writers (the callers).
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _loop = Task.Run(RunAsync);
    }

    // For tests only: true while the loop is parked with nothing it can do yet.
    internal bool IsIdle => _idle;

    public Task<TOut> SubmitAsync(TIn input, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TOut>(cancellationToken);
        }

        var item = new Pending(input, _clock.GetTimestamp());
        if (cancellationToken.CanBeCanceled)
        {
            item.WatchCancel(cancellationToken);
        }

        // TryWrite never waits: on a full queue it returns false and we turn the request away.
        _idle = false;
        if (!_queue.Writer.TryWrite(item))
        {
            item.ForgetCancel();
            // _closed is set before the channel is completed, so a failed write after
            // DisposeAsync always finds it set.
            return _closed
                ? Task.FromException<TOut>(new ObjectDisposedException(nameof(DynamicBatcher<TIn, TOut>)))
                : Task.FromException<TOut>(new QueueFullException(_options.QueueCapacity));
        }
        return item.Result.Task;
    }

    // Stops taking new requests, runs everything already queued, then ends the loop.
    public async ValueTask DisposeAsync()
    {
        _closed = true;
        _queue.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        ChannelReader<Pending> reader = _queue.Reader;
        var batch = new List<Pending>(_options.MaxBatchSize);

        // Wait for the first request. False means the queue is closed and empty: we're done.
        while (await WaitForMoreAsync(reader, CancellationToken.None).ConfigureAwait(false))
        {
            if (!TakeAvailable(reader, batch))
            {
                continue;
            }

            // The clock started when the oldest request arrived, not when the loop picked it up.
            // If the loop was busy with the last batch, the deadline may already be gone.
            long oldest = batch[0].Arrived;
            while (batch.Count < _options.MaxBatchSize)
            {
                TimeSpan left = _options.MaxWait - _clock.GetElapsedTime(oldest);
                if (left <= TimeSpan.Zero)
                {
                    break;
                }
                Wake wake = await WaitUntilAsync(reader, left).ConfigureAwait(false);
                if (wake == Wake.Closed)
                {
                    break;
                }
                if (wake == Wake.More)
                {
                    TakeAvailable(reader, batch);
                }
                // Wake.TimeUp: go round again. `left` is measured again on our own clock, and the
                // batch closes only when that says T has passed. A timer can fire early: on Windows,
                // .NET's timers read a clock that moves once per system clock tick (Microsoft: "typically
                // in the range of 0.5 milliseconds to 15.625 milliseconds"), so trusting the timer alone
                // closed batches before T.
            }

            await RunBatchAsync(batch).ConfigureAwait(false);
            batch.Clear();
        }
    }

    // Moves queued requests into the batch, up to MaxBatchSize. Requests whose caller has
    // already cancelled are dropped here, so they never use model time.
    private bool TakeAvailable(ChannelReader<Pending> reader, List<Pending> batch)
    {
        int before = batch.Count;
        while (batch.Count < _options.MaxBatchSize && reader.TryRead(out Pending? item))
        {
            if (item.Result.Task.IsCompleted)
            {
                item.ForgetCancel();
                continue;
            }
            batch.Add(item);
        }
        return batch.Count > before;
    }

    private enum Wake { More, TimeUp, Closed }

    // Waits until more requests can be read, the timer fires, or the queue is closed.
    private async Task<Wake> WaitUntilAsync(ChannelReader<Pending> reader, TimeSpan left)
    {
        using var timer = new CancellationTokenSource(left, _clock);
        // The timer callback runs on the thread that fires it, so tests see the loop wake up at once.
        using var wake = timer.Token.Register(() => _idle = false);
        try
        {
            return await WaitForMoreAsync(reader, timer.Token).ConfigureAwait(false) ? Wake.More : Wake.Closed;
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested)
        {
            return Wake.TimeUp;
        }
    }

    private async Task<bool> WaitForMoreAsync(ChannelReader<Pending> reader, CancellationToken token)
    {
        ValueTask<bool> wait = reader.WaitToReadAsync(token);
        if (!wait.IsCompleted)
        {
            _idle = true;
        }
        try
        {
            return await wait.ConfigureAwait(false);
        }
        finally
        {
            _idle = false;
        }
    }

    private async Task RunBatchAsync(List<Pending> batch)
    {
        // A caller may have cancelled while the batch was filling. Drop them before the model runs.
        batch.RemoveAll(p =>
        {
            if (!p.Result.Task.IsCompleted)
            {
                return false;
            }
            p.ForgetCancel();
            return true;
        });
        if (batch.Count == 0)
        {
            return;
        }

        var inputs = new TIn[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            inputs[i] = batch[i].Input;
        }

        IReadOnlyList<TOut> outputs;
        try
        {
            outputs = await _runBatch(inputs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // One failed batch fails only the callers in it. The loop carries on with the next batch.
            foreach (Pending p in batch)
            {
                p.Fail(ex);
            }
            return;
        }

        if (outputs is null || outputs.Count != batch.Count)
        {
            // Never guess which output belongs to whom.
            var ex = new InvalidOperationException(
                $"the batch function returned {outputs?.Count ?? 0} results for {batch.Count} inputs");
            foreach (Pending p in batch)
            {
                p.Fail(ex);
            }
            return;
        }

        for (int i = 0; i < batch.Count; i++)
        {
            batch[i].Complete(outputs[i]);
        }
    }

    // One waiting request: its input, when it arrived, and where its answer goes.
    private sealed class Pending(TIn input, long arrived)
    {
        private CancellationTokenRegistration _cancel;

        public TIn Input { get; } = input;
        public long Arrived { get; } = arrived;

        // RunContinuationsAsynchronously: when the loop sets the result, the caller's code
        // runs on its own, not inside the loop. Otherwise one slow caller would stall every batch.
        public TaskCompletionSource<TOut> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void WatchCancel(CancellationToken token) =>
            _cancel = token.Register(static s =>
            {
                var (result, ct) = ((TaskCompletionSource<TOut>, CancellationToken))s!;
                result.TrySetCanceled(ct);
            }, (Result, token));

        public void ForgetCancel() => _cancel.Dispose();

        // TrySet: if the caller cancelled at the last moment, the batch already ran and the answer is dropped.
        public void Complete(TOut output)
        {
            ForgetCancel();
            Result.TrySetResult(output);
        }

        public void Fail(Exception ex)
        {
            ForgetCancel();
            Result.TrySetException(ex);
        }
    }
}
