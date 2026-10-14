using System.Threading.Channels;

namespace AgentDag;

public enum NodeStatus { Succeeded, Failed, Skipped, Cancelled }

public enum FailurePolicy
{
    /// <summary>A failed node's descendants are skipped; independent branches keep running. (Partial results: one candidate's lookup failing shouldn't sink the run.)</summary>
    SkipDescendants,
    /// <summary>First failure cancels everything in flight and starts nothing new. (All-or-nothing plans.)</summary>
    FailFast,
}

/// <summary>What a node's work function receives.</summary>
/// <param name="Inputs">Parent id → parent output. Only parents, never the whole run state (least privilege).</param>
/// <param name="Attempt">1-based. Lets idempotent tools reuse the same idempotency key across retries.</param>
/// <param name="Cancellation">Fires on run cancellation, fail-fast, or this attempt's timeout. Work must observe it.</param>
public sealed record NodeContext(string NodeId, IReadOnlyDictionary<string, object?> Inputs, int Attempt, CancellationToken Cancellation);

public delegate Task<object?> NodeWork(NodeContext context);

public sealed record NodeResult(string Id, NodeStatus Status, object? Output = null, Exception? Error = null, int Attempts = 0);

public sealed record RunResult(IReadOnlyDictionary<string, NodeResult> Nodes)
{
    public bool Succeeded => Nodes.Values.All(n => n.Status == NodeStatus.Succeeded);
    public NodeResult this[string id] => Nodes[id];
}

public sealed record ExecutorOptions
{
    /// <summary>At most this many nodes run at once (tool quotas, model rate limits, memory).</summary>
    public int MaxConcurrency { get; init; } = 4;
    public FailurePolicy FailurePolicy { get; init; } = FailurePolicy.SkipDescendants;
    /// <summary>Per attempt. Null means no timeout.</summary>
    public TimeSpan? NodeTimeout { get; init; }
    /// <summary>Total attempts per node, including the first.</summary>
    public int MaxAttempts { get; init; } = 1;
    /// <summary>Which failures are worth retrying. Default: timeouts and transient I/O, not bugs or bad input.</summary>
    public Func<Exception, bool> IsRetryable { get; init; } = ex => ex is TimeoutException or HttpRequestException or IOException;
    /// <summary>Delay before attempt n+1 given n. Default: exponential with full jitter, capped at 10 s.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = DefaultBackoff;

    public static TimeSpan DefaultBackoff(int failedAttempt)
    {
        var capMs = Math.Min(10_000, 100 * (1 << Math.Min(failedAttempt - 1, 16)));
        return TimeSpan.FromMilliseconds(Random.Shared.Next(0, capMs + 1));
    }
}

/// <summary>
/// Runs a <see cref="Plan"/> with bounded parallelism.
///
/// Design: one scheduler loop owns all mutable state (indegrees, ready queue, results), so it needs no locks.
/// Workers never touch that state; each reports exactly one completion through a channel. This is
/// "share memory by communicating": the same shape as PyTorch's autograd ready queue or a Temporal/Cadence decider.
/// </summary>
public sealed class DagExecutor(ExecutorOptions? options = null)
{
    private readonly ExecutorOptions _o = Validate(options ?? new ExecutorOptions());

    private sealed record Completion(NodeResult Result);

    public async Task<RunResult> RunAsync(Plan plan, IReadOnlyDictionary<string, NodeWork> work, CancellationToken cancellationToken = default)
    {
        foreach (var id in plan.Nodes.Keys)
            if (!work.ContainsKey(id)) throw new ArgumentException($"No work registered for node '{id}'.", nameof(work));

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completions = Channel.CreateUnbounded<Completion>(new UnboundedChannelOptions { SingleReader = true });

        var results = new Dictionary<string, NodeResult>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        var waitingOn = plan.Parents.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);

        // Ready nodes, most urgent first: longest remaining path (bottom level), then id for determinism.
        var ready = new PriorityQueue<string, (long negBottomLevel, string id)>(
            Comparer<(long, string)>.Create((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : string.CompareOrdinal(a.Item2, b.Item2)));
        void MakeReady(string id) => ready.Enqueue(id, (-plan.BottomLevel[id].Ticks, id));
        foreach (var (id, n) in waitingOn) if (n == 0) MakeReady(id);

        var running = 0;
        var stopping = false;

        while (true)
        {
            stopping |= runCts.IsCancellationRequested;

            // Fill free slots.
            while (!stopping && running < _o.MaxConcurrency && ready.TryDequeue(out var id, out _))
            {
                var inputs = plan.Parents[id].ToDictionary(p => p, p => outputs[p], StringComparer.Ordinal);
                running++;
                _ = RunNodeAsync(id, work[id], inputs, runCts.Token, completions.Writer);
            }

            if (running == 0) break; // Nothing in flight and nothing startable: done (or stopped).

            // Wait for any node to finish. No token here on purpose: every started node reports exactly once,
            // so the loop never abandons in-flight work (no orphaned tasks, no lost results).
            var done = (await completions.Reader.ReadAsync().ConfigureAwait(false)).Result;
            running--;
            results[done.Id] = done;

            switch (done.Status)
            {
                case NodeStatus.Succeeded:
                    outputs[done.Id] = done.Output;
                    foreach (var c in plan.Children[done.Id])
                        if (--waitingOn[c] == 0 && !results.ContainsKey(c)) MakeReady(c);
                    break;

                case NodeStatus.Failed when _o.FailurePolicy == FailurePolicy.FailFast:
                    stopping = true;
                    await runCts.CancelAsync().ConfigureAwait(false);
                    break;

                case NodeStatus.Failed:
                    // Pre-mark descendants so they're never enqueued, even if other parents succeed later.
                    foreach (var d in plan.Descendants(done.Id))
                        results.TryAdd(d, new NodeResult(d, NodeStatus.Skipped, Error: new InvalidOperationException($"Upstream node '{done.Id}' failed.")));
                    break;
            }
        }

        // Anything never started was stopped by cancellation or fail-fast.
        foreach (var id in plan.Nodes.Keys)
            results.TryAdd(id, new NodeResult(id, NodeStatus.Cancelled));

        return new RunResult(results);
    }

    private async Task RunNodeAsync(string id, NodeWork work, IReadOnlyDictionary<string, object?> inputs,
                                    CancellationToken runToken, ChannelWriter<Completion> report)
    {
        // Leave the scheduler's context at once, so a work function that blocks synchronously
        // before its first await can't stall the loop.
        await Task.Yield();
        NodeResult result;
        var attempt = 0;
        try
        {
            while (true)
            {
                attempt++;
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                if (_o.NodeTimeout is { } t) attemptCts.CancelAfter(t);
                try
                {
                    var ctx = new NodeContext(id, inputs, attempt, attemptCts.Token);
                    // WaitAsync: stop waiting on cancel even if the work ignores its token.
                    var output = await work(ctx).WaitAsync(attemptCts.Token).ConfigureAwait(false);
                    result = new NodeResult(id, NodeStatus.Succeeded, output, Attempts: attempt);
                    break;
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    result = new NodeResult(id, NodeStatus.Cancelled, Attempts: attempt);
                    break;
                }
                catch (Exception ex)
                {
                    // A cancel we didn't ask for at run level is this attempt's timeout.
                    var error = ex is OperationCanceledException ? new TimeoutException($"Node '{id}' timed out after {_o.NodeTimeout}.", ex) : ex;
                    if (attempt >= _o.MaxAttempts || !_o.IsRetryable(error))
                    {
                        result = new NodeResult(id, NodeStatus.Failed, Error: error, Attempts: attempt);
                        break;
                    }
                    await Task.Delay(_o.Backoff(attempt), runToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = new NodeResult(id, NodeStatus.Cancelled, Attempts: attempt); // cancelled during backoff
        }
        catch (Exception ex)
        {
            result = new NodeResult(id, NodeStatus.Failed, Error: ex, Attempts: attempt); // e.g. a throwing Backoff/IsRetryable
        }
        report.TryWrite(new Completion(result)); // unbounded channel: always succeeds
    }

    private static ExecutorOptions Validate(ExecutorOptions o)
    {
        if (o.MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(o.MaxConcurrency), "Must be at least 1.");
        if (o.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(o.MaxAttempts), "Must be at least 1.");
        if (o.NodeTimeout is { } t && t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(o.NodeTimeout), "Must be positive.");
        return o;
    }
}
