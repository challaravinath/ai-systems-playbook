using AgentDag;

namespace AgentDag.Tests;

/// <summary>
/// The edges a code review found: timeouts that don't stop the work, a tool cancelling itself,
/// a throwing cancel callback, the caller's synchronization context, many failures, bad plan JSON.
/// </summary>
public class HardeningTests
{
    private static readonly ExecutorOptions NoBackoff = new() { Backoff = _ => TimeSpan.Zero };

    private static Dictionary<string, NodeWork> Work(Plan plan, Func<NodeContext, Task<object?>> body)
        => plan.Nodes.Keys.ToDictionary(id => id, _ => (NodeWork)(ctx => body(ctx)));

    [Fact]
    public async Task Tool_that_ignores_its_timeout_is_not_retried_on_top_of_itself()
    {
        // A tool that ignores its token keeps running after the timeout. Retrying it would run it twice at once,
        // and for a "send email" step that is two emails.
        var plan = Plan.Build([new PlanNode("send_email")]);
        int calls = 0, now = 0, peak = 0;
        var opts = NoBackoff with { NodeTimeout = TimeSpan.FromMilliseconds(50), CancellationGrace = TimeSpan.FromMilliseconds(50), MaxAttempts = 3, MaxConcurrency = 1 };
        var run = await new DagExecutor(opts).RunAsync(plan, Work(plan, async _ =>
        {
            Interlocked.Increment(ref calls);
            var c = Interlocked.Increment(ref now);
            if (c > Volatile.Read(ref peak)) Volatile.Write(ref peak, c);
            await Task.Delay(600); // ignores its token
            Interlocked.Decrement(ref now);
            return null;
        }));

        Assert.Equal(NodeStatus.Failed, run["send_email"].Status);
        Assert.IsType<TimeoutException>(run["send_email"].Error);
        Assert.Contains("ignored cancellation", run["send_email"].Error!.Message);
        Assert.Equal(1, calls);
        Assert.Equal(1, peak);
        Assert.Equal(["send_email"], run.StillRunning);
    }

    [Fact]
    public async Task Each_attempt_gets_its_own_timeout_so_a_fast_retry_succeeds()
    {
        // Attempt 1 hangs (politely) and times out. Attempt 2 answers at once and must not inherit attempt 1's timeout.
        var plan = Plan.Build([new PlanNode("search")]);
        var run = await new DagExecutor(NoBackoff with { NodeTimeout = TimeSpan.FromMilliseconds(100), MaxAttempts = 2 })
            .RunAsync(plan, Work(plan, async ctx =>
            {
                if (ctx.Attempt == 1) await Task.Delay(Timeout.Infinite, ctx.Cancellation);
                await Task.Delay(30, ctx.Cancellation);
                return "found";
            }));
        Assert.Equal(NodeStatus.Succeeded, run["search"].Status);
        Assert.Equal(2, run["search"].Attempts);
        Assert.Equal("found", run["search"].Output);
        Assert.Empty(run.StillRunning);
    }

    [Fact]
    public async Task Tool_that_cancels_itself_is_its_own_error_not_a_timeout()
    {
        // No timeout is set, and nobody cancelled the run. The tool threw OperationCanceledException on its own.
        var plan = Plan.Build([new PlanNode("client")]);
        var calls = 0;
        var run = await new DagExecutor(NoBackoff with { MaxAttempts = 3 }).RunAsync(plan, Work(plan, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new OperationCanceledException("the client gave up");
        }));
        Assert.Equal(NodeStatus.Failed, run["client"].Status);
        Assert.IsType<OperationCanceledException>(run["client"].Error);
        Assert.Equal(1, calls); // not retried as if it were a timeout
    }

    [Fact]
    public async Task Fail_fast_still_returns_results_when_a_cancel_callback_throws()
    {
        var plan = Plan.Build([new PlanNode("bad"), new PlanNode("cleanup")]);
        var work = new Dictionary<string, NodeWork>
        {
            ["bad"] = async _ => { await Task.Delay(50); throw new InvalidOperationException("schema invalid"); },
            ["cleanup"] = async ctx =>
            {
                ctx.Cancellation.Register(() => throw new InvalidOperationException("cleanup hook threw"));
                await Task.Delay(TimeSpan.FromSeconds(30), ctx.Cancellation);
                return null;
            },
        };
        var run = await new DagExecutor(new ExecutorOptions { FailurePolicy = FailurePolicy.FailFast }).RunAsync(plan, work);
        Assert.Equal(NodeStatus.Failed, run["bad"].Status);
        Assert.Equal(NodeStatus.Cancelled, run["cleanup"].Status);
    }

    [Fact]
    public async Task Fail_fast_reports_a_sibling_that_reacts_to_the_cancel_with_its_own_error_as_cancelled()
    {
        // Many clients wrap a cancel in their own exception type. The run was stopping, so that sibling was
        // cancelled; it did not fail on its own.
        var plan = Plan.Build([new PlanNode("bad"), new PlanNode("wrapper")]);
        var work = new Dictionary<string, NodeWork>
        {
            ["bad"] = async _ => { await Task.Delay(50); throw new InvalidOperationException("schema invalid"); },
            ["wrapper"] = async ctx =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ctx.Cancellation); }
                catch (OperationCanceledException e) { throw new InvalidOperationException("request aborted", e); }
                return null;
            },
        };
        var run = await new DagExecutor(new ExecutorOptions { FailurePolicy = FailurePolicy.FailFast }).RunAsync(plan, work);
        Assert.Equal(NodeStatus.Failed, run["bad"].Status);
        Assert.Equal(NodeStatus.Cancelled, run["wrapper"].Status);
    }

    [Fact]
    public async Task Work_never_runs_on_the_callers_synchronization_context()
    {
        // A UI or classic ASP.NET caller has a context. Task.Yield posts back to it, so blocking work would
        // queue up on that one thread. Every node must run off it.
        var plan = Plan.Build(Enumerable.Range(0, 4).Select(i => new PlanNode($"n{i}")));
        var marker = new MarkerContext();
        var onCallerContext = 0;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(marker);
        try
        {
            var running = new DagExecutor().RunAsync(plan, Work(plan, ctx =>
            {
                if (SynchronizationContext.Current == marker) Interlocked.Increment(ref onCallerContext);
                return Task.FromResult<object?>(null);
            }));
            SynchronizationContext.SetSynchronizationContext(previous);
            Assert.True((await running).Succeeded);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.Equal(0, onCallerContext);
    }

    [Fact]
    public async Task Thousands_of_failures_skip_their_shared_descendants_in_linear_time()
    {
        // 20,000 independent lookups all fail, and all feed one chain of 20,000 steps. Walking the whole chain
        // once per failure is 400 million steps. Stopping at nodes already skipped is about 40,000.
        const int n = 20_000;
        var nodes = new List<PlanNode>();
        nodes.AddRange(Enumerable.Range(0, n).Select(i => new PlanNode($"lookup{i}")));
        nodes.Add(new PlanNode("chain0", [.. Enumerable.Range(0, n).Select(i => $"lookup{i}")], TimeSpan.Zero));
        nodes.AddRange(Enumerable.Range(1, n - 1).Select(i => new PlanNode($"chain{i}", $"chain{i - 1}")));
        var plan = Plan.Build(nodes);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = await new DagExecutor(new ExecutorOptions { MaxConcurrency = 16 })
            .RunAsync(plan, Work(plan, _ => throw new InvalidOperationException("404")));
        Assert.Equal(n, run.Nodes.Values.Count(r => r.Status == NodeStatus.Failed));
        Assert.Equal(n, run.Nodes.Values.Count(r => r.Status == NodeStatus.Skipped));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Cancelling_during_the_wait_between_retries_ends_the_node_as_cancelled()
    {
        var plan = Plan.Build([new PlanNode("github")]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = await new DagExecutor(new ExecutorOptions { MaxAttempts = 5, Backoff = _ => TimeSpan.FromSeconds(30) })
            .RunAsync(plan, Work(plan, _ => throw new HttpRequestException("429")), cts.Token);
        Assert.Equal(NodeStatus.Cancelled, run["github"].Status);
        Assert.Equal(1, run["github"].Attempts);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_negative_backoff_counts_as_no_wait()
    {
        var plan = Plan.Build([new PlanNode("flaky")]);
        var calls = 0;
        var run = await new DagExecutor(new ExecutorOptions { MaxAttempts = 3, Backoff = _ => TimeSpan.FromSeconds(-5) })
            .RunAsync(plan, Work(plan, _ => Interlocked.Increment(ref calls) < 3 ? throw new IOException("reset") : Task.FromResult<object?>("ok")));
        Assert.Equal(NodeStatus.Succeeded, run["flaky"].Status);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(1, 0, 100)]
    [InlineData(3, 0, 400)]
    [InlineData(20, 0, 10_000)]
    public void Default_backoff_is_full_jitter_under_a_doubling_cap(int failedAttempt, int minMs, int maxMs)
    {
        for (var i = 0; i < 200; i++)
            Assert.InRange(ExecutorOptions.DefaultBackoff(failedAttempt).TotalMilliseconds, minMs, maxMs);
    }

    [Fact]
    public void Plan_json_with_holes_is_a_validation_error()
    {
        // System.Text.Json leaves a missing array as null, whatever the record's nullability says.
        Assert.Throws<PlanValidationException>(() => Plan.Build([new PlanNode("a", null!, TimeSpan.Zero)]));
        Assert.Throws<PlanValidationException>(() => Plan.Build([new PlanNode("a"), new PlanNode("b", ["a", null!], TimeSpan.Zero)]));
        Assert.Throws<PlanValidationException>(() => Plan.Build([new PlanNode("a"), null!]));
    }

    [Fact]
    public void A_built_plan_does_not_change_when_the_callers_list_does()
    {
        var deps = new List<string> { "a" };
        var plan = Plan.Build([new PlanNode("a"), new PlanNode("b", deps, TimeSpan.Zero)]);
        deps.Add("ghost");
        Assert.Equal(["a"], plan.Nodes["b"].DependsOn);
        Assert.False(plan.Parents["b"] is List<string>);
    }

    [Fact]
    public async Task Stray_work_can_be_awaited_before_running_the_plan_again()
    {
        var plan = Plan.Build([new PlanNode("send_email")]);
        var finished = false;
        var run = await new DagExecutor(new ExecutorOptions { NodeTimeout = TimeSpan.FromMilliseconds(50), CancellationGrace = TimeSpan.FromMilliseconds(50) })
            .RunAsync(plan, Work(plan, async _ => { await Task.Delay(400); Volatile.Write(ref finished, true); return null; }));
        Assert.Equal(["send_email"], run.StillRunning);
        Assert.False(Volatile.Read(ref finished));
        await run.StrayWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Volatile.Read(ref finished));
    }

    [Theory]
    [InlineData(null, true)]   // no response at all: connection refused, DNS, reset
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void Only_http_failures_that_may_go_away_are_retried(int? status, bool retried)
        => Assert.Equal(retried, ExecutorOptions.IsTransient(new HttpRequestException("x", null, (System.Net.HttpStatusCode?)status)));

    [Fact]
    public void Bugs_are_not_transient_but_timeouts_and_io_are()
    {
        Assert.False(ExecutorOptions.IsTransient(new ArgumentException("rubric missing")));
        Assert.True(ExecutorOptions.IsTransient(new TimeoutException()));
        Assert.True(ExecutorOptions.IsTransient(new IOException()));
    }

    [Fact]
    public async Task Paper_step_5_with_two_slots_the_step_with_work_behind_it_starts_first()
    {
        // A, B and C take 100 ms and have no parents. D needs C and takes 1,000 ms. K = 2.
        // First come, first served would start A and B. Ranking by bottom level starts C (1,100) and A (100, then id).
        var plan = Plan.Build([
            new PlanNode("A", [], TimeSpan.FromMilliseconds(100)), new PlanNode("B", [], TimeSpan.FromMilliseconds(100)),
            new PlanNode("C", [], TimeSpan.FromMilliseconds(100)), new PlanNode("D", ["C"], TimeSpan.FromMilliseconds(1000)),
        ]);
        var started = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await new DagExecutor(new ExecutorOptions { MaxConcurrency = 2 }).RunAsync(plan, Work(plan, async ctx =>
        {
            started.Enqueue(ctx.NodeId);
            if (started.Count >= 2) bothStarted.TrySetResult();
            await bothStarted.Task; // nothing finishes until the first two have started
            return null;
        }));
        Assert.Equal(["A", "C"], started.Take(2).Order(StringComparer.Ordinal));
    }

    /// <summary>Runs posted work on the pool, but marks it as running on this context, like a UI context would.</summary>
    private sealed class MarkerContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ThreadPool.QueueUserWorkItem(_ =>
        {
            SetSynchronizationContext(this);
            try { d(state); } finally { SetSynchronizationContext(null); }
        });
    }
}
