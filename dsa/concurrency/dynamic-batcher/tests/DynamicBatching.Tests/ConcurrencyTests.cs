namespace DynamicBatching.Tests;

// Real clock, real threads. Checks what must hold no matter how the threads interleave.
public class ConcurrencyTests
{
    [Fact]
    public async Task Many_threads_each_get_their_own_answer_and_no_batch_is_too_big()
    {
        const int threads = 8, perThread = 2000, maxBatch = 16;
        int biggest = 0, served = 0;

        await using var batcher = new DynamicBatcher<int, int>(
            new BatchOptions(maxBatch, TimeSpan.FromMilliseconds(1), queueCapacity: threads * perThread),
            async xs =>
            {
                InterlockedMax(ref biggest, xs.Count);
                Interlocked.Add(ref served, xs.Count);
                await Task.Yield();
                return xs.Select(x => x * 10).ToArray();
            });

        var workers = Enumerable.Range(0, threads).Select(w => Task.Run(async () =>
        {
            var mine = new List<(int In, Task<int> Out)>();
            for (int i = 0; i < perThread; i++)
            {
                int x = w * perThread + i;
                mine.Add((x, batcher.SubmitAsync(x)));
            }
            foreach (var (x, task) in mine)
            {
                Assert.Equal(x * 10, await task);
            }
        }));
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(threads * perThread, served);
        Assert.InRange(biggest, 1, maxBatch);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
