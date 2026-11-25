namespace DynamicBatching;

// The queue already holds QueueCapacity requests. Like Triton's max_queue_size, we say no
// straight away instead of making the caller wait. A server would turn this into HTTP 503 or 429.
public sealed class QueueFullException(int capacity)
    : Exception($"the batch queue is full ({capacity} waiting)")
{
    public int Capacity { get; } = capacity;
}
