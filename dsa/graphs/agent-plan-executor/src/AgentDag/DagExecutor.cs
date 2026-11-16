using System.Collections.Concurrent;
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
/// <param name="Inputs">Each parent's id mapped to its output. Only parents, never the whole run state (least privilege).
/// The dictionary is the node's own, but the outputs in it are shared with sibling nodes: treat them as read-only.</param>
/// <param name="Attempt">1-based. Tells a tool it is being retried. An idempotency key must NOT include it: build the key
/// from your own run id and <paramref name="NodeId"/>, so every attempt of a step sends the same key.</param>
/// <param name="Cancellation">Fires on run cancellation, fail-fast, or this attempt's timeout. Work must observe it.</param>
public sealed record NodeContext(string NodeId, IReadOnlyDictionary<string, object?> Inputs, int Attempt, CancellationToken Cancellation);

public delegate Task<object?> NodeWork(NodeContext context);

public sealed record NodeResult(string Id, NodeStatus Status, object? Output = null, Exception? Error = null, int Attempts = 0);

/// <param name="Nodes">Exactly one result per node.</param>
/// <param name="StillRunning">Nodes whose work ignored cancellation and had not finished when the run returned.
/// The executor stopped waiting for them, but it can't stop them. Empty when every tool observes its token.
/// They don't count against K any more, so while they run, more than K tool calls can be in flight.</param>
/// <param name="StrayWork">Completes when every piece of work in <see cref="StillRunning"/> has finished. Await it before
/// re-running the plan or shutting down, so a stray "send email" can't overlap the next attempt. Never faults.</param>
public sealed record RunResult(IReadOnlyDictionary<string, NodeResult> Nodes, IReadOnlyCollection<string> StillRunning, Task StrayWork)
{
    public bool Succeeded => Nodes.Values.All(n => n.Status == NodeStatus.Succeeded);
    public NodeResult this[string id] => Nodes[id];
}

public sealed record ExecutorOptions
{
    /// <summary>At most this many nodes run at once (tool quotas, model rate limits, memory).</summary>
    public int MaxConcurrency { get; init; } = 4;
    public FailurePolicy FailurePolicy { get; init; } = FailurePolicy.SkipDescendants;
    /// <summary>Per attempt. Null means no timeout, so a tool that never returns and ignores its token hangs the run.
    /// Set one for every tool you don't control.</summary>
    public TimeSpan? NodeTimeout { get; init; }
    /// <summary>How long a cancelled attempt gets to stop before it counts as ignoring cancellation.
    /// A tool that observes its token stops in well under this, so the wait is usually a few milliseconds.</summary>
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Total attempts per node, including the first.</summary>
    public int MaxAttempts { get; init; } = 1;
    /// <summary>Which failures are worth retrying. Default: <see cref="IsTransient"/>.</summary>
    public Func<Exception, bool> IsRetryable { get; init; } = IsTransient;
    /// <summary>Delay before attempt n+1 given n. Default: exponential with full jitter, capped at 10 s. A negative delay counts as zero.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = DefaultBackoff;

    /// <summary>
    /// Timeouts, I/O errors, and HTTP failures that may go away: no response at all, 408, 429 and 5xx.
    /// Other HTTP statuses (400, 401, 404 ...) fail the same way every time, so they aren't retried.
    /// </summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException or IOException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } code } => (int)code is 408 or 429 or >= 500,
        _ => false,
    };

    public static TimeSpan DefaultBackoff(int failedAttempt)
    {
        var capMs = Math.Min(10_000, 100 * (1 << Math.Clamp(failedAttempt - 1, 0, 16)));
        return TimeSpan.FromMilliseconds(Random.Shared.Next(0, capMs + 1));
    }
}

/// <summary>
/// Runs a <see cref="Plan"/> with bounded parallelism.
///
/// Design: one scheduler loop owns all mutable state (indegrees, ready queue, results), so it needs no locks.
/// Workers never touch that state; each reports exactly one completion through a channel.
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
        var strays = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);

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
                _ = RunNodeAsync(id, work[id], inputs, runCts.Token, completions.Writer, strays);
            }

            if (running == 0) break; // Nothing in flight and nothing startable: done (or stopped).

            // Wait for any node to finish. No token here on purpose: every started node reports exactly once,
            // so the loop never stops waiting for a node it started.
            var done = (await completions.Reader.ReadAsync().ConfigureAwait(false)).Result;
            running--;
            results[done.Id] = done;

            switch (done.Status)
            {
                case NodeStatus.Succeeded:
                    outputs[done.Id] = done.Output;
                    foreach (var c in plan.Children[done.Id])
                        if (--waitingOn[c] == 0) MakeReady(c);
                    break;

                case NodeStatus.Failed when _o.FailurePolicy == FailurePolicy.FailFast:
                    stopping = true;
                    // A tool's own cancellation callback may throw. The token is cancelled either way,
                    // and the run must still wait for every node in flight, so the error is not the run's.
                    try { await runCts.CancelAsync().ConfigureAwait(false); } catch (AggregateException) { }
                    break;

                case NodeStatus.Failed:
                    // Mark every descendant Skipped, so none is ever enqueued. The walk stops at nodes already
                    // marked: their descendants were marked with them. Each node is marked once, so all the
                    // skipping in a run costs O(V + E) in total, however many nodes fail.
                    var upstream = new InvalidOperationException($"Upstream node '{done.Id}' failed.");
                    var stack = new Stack<string>(plan.Children[done.Id]);
                    while (stack.TryPop(out var d))
                        if (results.TryAdd(d, new NodeResult(d, NodeStatus.Skipped, Error: upstream)))
                            foreach (var c in plan.Children[d]) stack.Push(c);
                    break;
            }
        }

        // Anything never started was stopped by cancellation or fail-fast.
        foreach (var id in plan.Nodes.Keys)
            results.TryAdd(id, new NodeResult(id, NodeStatus.Cancelled));

        var stillRunning = strays.Where(kv => !kv.Value.IsCompleted).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        var strayWork = Task.WhenAll(strays.Values).ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new RunResult(results, stillRunning, strayWork);
    }

    private async Task RunNodeAsync(string id, NodeWork work, IReadOnlyDictionary<string, object?> inputs,
                                    CancellationToken runToken, ChannelWriter<Completion> report,
                                    ConcurrentDictionary<string, Task> strays)
    {
        // Leave the caller's thread and synchronization context at once. Task.Yield alone would post back to a
        // UI-style context, and a work function that blocks before its first await would then stall the loop.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        NodeResult result;
        var attempt = 0;
        try
        {
            while (true)
            {
                attempt++;
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                if (_o.NodeTimeout is { } t) attemptCts.CancelAfter(t);
                Task<object?>? call = null;
                try
                {
                    call = work(new NodeContext(id, inputs, attempt, attemptCts.Token));
                    // WaitAsync: stop waiting on cancel even if the work ignores its token.
                    var output = await call.WaitAsync(attemptCts.Token).ConfigureAwait(false);
                    result = new NodeResult(id, NodeStatus.Succeeded, output, Attempts: attempt);
                    break;
                }
                catch (OperationCanceledException ex) when (attemptCts.IsCancellationRequested)
                {
                    // We cancelled this attempt: the run was cancelled, or the attempt timed out.
                    var stopped = await StoppedWithinGraceAsync(id, call, strays).ConfigureAwait(false);
                    if (runToken.IsCancellationRequested)
                    {
                        result = new NodeResult(id, NodeStatus.Cancelled, Attempts: attempt);
                        break;
                    }
                    if (!stopped)
                    {
                        // Retrying now would run the same tool twice at once. Fail instead.
                        var ignored = new TimeoutException($"Node '{id}' timed out after {_o.NodeTimeout} " +
                                                           "and its work ignored cancellation, so it was not retried.", ex);
                        result = new NodeResult(id, NodeStatus.Failed, Error: ignored, Attempts: attempt);
                        break;
                    }
                    var timeout = new TimeoutException($"Node '{id}' timed out after {_o.NodeTimeout}.", ex);
                    if (!await RetryAsync(timeout).ConfigureAwait(false))
                    {
                        result = new NodeResult(id, NodeStatus.Failed, Error: timeout, Attempts: attempt);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // The work failed on its own, including a tool that threw OperationCanceledException without
                    // being asked to: that's the tool's error, not a timeout.
                    if (!await RetryAsync(ex).ConfigureAwait(false))
                    {
                        result = new NodeResult(id, NodeStatus.Failed, Error: ex, Attempts: attempt);
                        break;
                    }
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

        async Task<bool> RetryAsync(Exception error)
        {
            if (attempt >= _o.MaxAttempts || !_o.IsRetryable(error)) return false;
            var delay = _o.Backoff(attempt);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, runToken).ConfigureAwait(false);
            runToken.ThrowIfCancellationRequested();
            return true;
        }
    }

    /// <summary>
    /// After a cancel, give the work a short grace to stop. If it doesn't, remember it as stray work and observe
    /// its eventual fault, so a late exception never goes unobserved.
    /// </summary>
    private async Task<bool> StoppedWithinGraceAsync(string id, Task<object?>? call, ConcurrentDictionary<string, Task> strays)
    {
        if (call is null || call.IsCompleted) return true;
        await Task.WhenAny(call, Task.Delay(_o.CancellationGrace)).ConfigureAwait(false);
        if (call.IsCompleted) return true;
        strays[id] = call;
        _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return false;
    }

    private static ExecutorOptions Validate(ExecutorOptions o)
    {
        if (o.MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(o.MaxConcurrency), "Must be at least 1.");
        if (o.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(o.MaxAttempts), "Must be at least 1.");
        if (o.NodeTimeout is { } t && t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(o.NodeTimeout), "Must be positive.");
        if (o.CancellationGrace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(o.CancellationGrace), "Must not be negative.");
        return o;
    }
}
