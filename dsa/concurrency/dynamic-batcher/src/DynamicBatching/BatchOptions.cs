namespace DynamicBatching;

// The three knobs every batching server we read exposes, under different names.
// Triton: max_batch_size, max_queue_delay_microseconds, max_queue_size.
public sealed class BatchOptions
{
    public BatchOptions(int maxBatchSize, TimeSpan maxWait, int queueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWait, TimeSpan.Zero);
        // A queue smaller than one batch could never fill a batch.
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, maxBatchSize);

        MaxBatchSize = maxBatchSize;
        MaxWait = maxWait;
        QueueCapacity = queueCapacity;
    }

    // N: a batch closes as soon as it has this many requests.
    public int MaxBatchSize { get; }

    // T: a batch closes once its oldest request has waited this long, full or not.
    public TimeSpan MaxWait { get; }

    // How many requests can wait in the queue. One more is turned away straight away.
    public int QueueCapacity { get; }
}
