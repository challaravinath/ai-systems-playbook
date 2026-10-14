using System.Collections.Concurrent;
using AgentDag;

namespace AgentDag.Tests;

public class ExecutorTests
{
    private static readonly ExecutorOptions NoBackoff = new() { Backoff = _ => TimeSpan.Zero };

    private static Dictionary<string, NodeWork> Work(Plan plan, Func<NodeContext, Task<object?>> body)
        => plan.Nodes.Keys.ToDictionary(id => id, _ => (NodeWork)(ctx => body(ctx)));

    [Fact]
    public async Task Each_node_receives_exactly_its_parents_outputs()
    {
        var plan = Plan.Build([new PlanNode("a"), new PlanNode("b"), new PlanNode("sum", "a", "b")]);
        var work = new Dictionary<string, NodeWork>
        {
            ["a"] = _ => Task.FromResult<object?>(2),
            ["b"] = _ => Task.FromResult<object?>(40),
            ["sum"] = ctx => Task.FromResult<object?>(ctx.Inputs.Values.Sum(v => (int)v!)),
        };
        var run = await new DagExecutor().RunAsync(plan, work);
        Assert.True(run.Succeeded);
        Assert.Equal(42, run["sum"].Output);
    }

    [Fact]
    public async Task Never_more_than_K_tools_in_flight_and_K_is_actually_reached()
    {
        // 20 independent web lookups, a quota of 3 concurrent calls.
        var plan = Plan.Build(Enumerable.Range(0, 20).Select(i => new PlanNode($"lookup{i}")));
        int now = 0, peak = 0;
        var run = await new DagExecutor(new ExecutorOptions { MaxConcurrency = 3 }).RunAsync(plan, Work(plan, async ctx =>
        {
            var c = Interlocked.Increment(ref now);
            InterlockedMax(ref peak, c);
            await Task.Delay(15, ctx.Cancellation);
            Interlocked.Decrement(ref now);
            return null;
        }));
        Assert.True(run.Succeeded);
        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task Random_plan_of_300_nodes_runs_each_node_once_and_never_before_its_parents()
    {
        var rng = new Random(7);
        var nodes = Enumerable.Range(0, 300).Select(i => new PlanNode($"n{i:D3}",
            Enumerable.Range(0, i).Where(_ => rng.NextDouble() < 0.02).Select(j => $"n{j:D3}").ToArray(), TimeSpan.FromMilliseconds(rng.Next(1, 50)))).ToList();
        var plan = Plan.Build(nodes);
        var calls = new ConcurrentDictionary<string, int>();
        var finished = new ConcurrentDictionary<string, bool>();
        var violations = 0;
        var run = await new DagExecutor(new ExecutorOptions { MaxConcurrency = 8 }).RunAsync(plan, Work(plan, async ctx =>
        {
            calls.AddOrUpdate(ctx.NodeId, 1, (_, c) => c + 1);
            if (plan.Parents[ctx.NodeId].Any(p => !finished.ContainsKey(p))) Interlocked.Increment(ref violations);
            await Task.Yield();
            finished[ctx.NodeId] = true;
            return null;
        }));
        Assert.True(run.Succeeded);
        Assert.Equal(0, violations);
        Assert.Equal(300, calls.Count);
        Assert.All(calls.Values, c => Assert.Equal(1, c));
    }

    [Fact]
    public async Task One_candidates_failed_lookup_skips_only_its_own_dossier()
    {
        //        ┌ lookup_ana ─ dossier_ana ┐
        // shortlist                         panel_pack
        //        └ lookup_bo  ─ dossier_bo  ┘
        var plan = Plan.Build([
            new PlanNode("shortlist"),
            new PlanNode("lookup_ana", "shortlist"), new PlanNode("dossier_ana", "lookup_ana"),
            new PlanNode("lookup_bo", "shortlist"), new PlanNode("dossier_bo", "lookup_bo"),
            new PlanNode("panel_pack", "dossier_ana", "dossier_bo"),
        ]);
        var run = await new DagExecutor().RunAsync(plan, Work(plan, ctx =>
            ctx.NodeId == "lookup_bo" ? throw new InvalidOperationException("GitHub returned 404") : Task.FromResult<object?>("ok")));

        Assert.Equal(NodeStatus.Succeeded, run["dossier_ana"].Status);
        Assert.Equal(NodeStatus.Failed, run["lookup_bo"].Status);
        Assert.Equal(NodeStatus.Skipped, run["dossier_bo"].Status);
        Assert.Equal(NodeStatus.Skipped, run["panel_pack"].Status);
    }

    [Fact]
    public async Task Fail_fast_cancels_the_slow_sibling_and_starts_nothing_new()
    {
        var plan = Plan.Build([new PlanNode("bad"), new PlanNode("slow"), new PlanNode("after_slow", "slow")]);
        var started = new ConcurrentBag<string>();
        var work = new Dictionary<string, NodeWork>
        {
            ["bad"] = async _ => { await Task.Delay(20); throw new InvalidOperationException("schema invalid"); },
            ["slow"] = async ctx => { started.Add("slow"); await Task.Delay(TimeSpan.FromSeconds(30), ctx.Cancellation); return null; },
            ["after_slow"] = _ => { started.Add("after_slow"); return Task.FromResult<object?>(null); },
        };
        var t0 = DateTime.UtcNow;
        var run = await new DagExecutor(new ExecutorOptions { FailurePolicy = FailurePolicy.FailFast }).RunAsync(plan, work);

        Assert.True(DateTime.UtcNow - t0 < TimeSpan.FromSeconds(5));
        Assert.Equal(NodeStatus.Failed, run["bad"].Status);
        Assert.Equal(NodeStatus.Cancelled, run["slow"].Status);
        Assert.Equal(NodeStatus.Cancelled, run["after_slow"].Status);
        Assert.DoesNotContain("after_slow", started);
    }

    [Fact]
    public async Task Rate_limited_tool_succeeds_on_the_third_attempt()
    {
        var plan = Plan.Build([new PlanNode("github")]);
        var calls = 0;
        var run = await new DagExecutor(NoBackoff with { MaxAttempts = 3 }).RunAsync(plan, Work(plan, ctx =>
            Interlocked.Increment(ref calls) < 3 ? throw new HttpRequestException("429 Too Many Requests") : Task.FromResult<object?>(ctx.Attempt)));
        Assert.Equal(NodeStatus.Succeeded, run["github"].Status);
        Assert.Equal(3, run["github"].Attempts);
        Assert.Equal(3, run["github"].Output);
    }

    [Fact]
    public async Task A_bug_is_not_retried()
    {
        var plan = Plan.Build([new PlanNode("score")]);
        var calls = 0;
        var run = await new DagExecutor(NoBackoff with { MaxAttempts = 5 }).RunAsync(plan, Work(plan, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new ArgumentException("rubric missing");
        }));
        Assert.Equal(NodeStatus.Failed, run["score"].Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Hung_tool_times_out_and_is_retried_then_fails()
    {
        var plan = Plan.Build([new PlanNode("hung")]);
        var run = await new DagExecutor(NoBackoff with { NodeTimeout = TimeSpan.FromMilliseconds(50), MaxAttempts = 2 })
            .RunAsync(plan, Work(plan, async ctx => { await Task.Delay(Timeout.Infinite, ctx.Cancellation); return null; }));
        Assert.Equal(NodeStatus.Failed, run["hung"].Status);
        Assert.IsType<TimeoutException>(run["hung"].Error);
        Assert.Equal(2, run["hung"].Attempts);
    }

    [Fact]
    public async Task Tool_that_ignores_its_token_still_cannot_hang_the_run()
    {
        var plan = Plan.Build([new PlanNode("rude")]);
        var run = await new DagExecutor(new ExecutorOptions { NodeTimeout = TimeSpan.FromMilliseconds(50) })
            .RunAsync(plan, Work(plan, async _ => { await Task.Delay(TimeSpan.FromSeconds(30)); return null; }));
        Assert.Equal(NodeStatus.Failed, run["rude"].Status);
    }

    [Fact]
    public async Task Manager_cancels_mid_run_and_every_node_is_accounted_for()
    {
        var plan = Plan.Build([new PlanNode("a"), new PlanNode("b", "a"), new PlanNode("c", "b")]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var run = await new DagExecutor().RunAsync(plan, Work(plan, async ctx => { await Task.Delay(TimeSpan.FromSeconds(30), ctx.Cancellation); return null; }), cts.Token);
        Assert.Equal(3, run.Nodes.Count);
        Assert.All(run.Nodes.Values, r => Assert.Equal(NodeStatus.Cancelled, r.Status));
    }

    [Fact]
    public async Task With_one_slot_the_node_with_the_longest_tail_starts_first()
    {
        // Alphabetical order would start "a_quick" first. Critical-path-first starts "z_feeds_long_chain".
        var plan = Plan.Build([
            new PlanNode("a_quick", [], TimeSpan.FromMilliseconds(10)),
            new PlanNode("z_feeds_long_chain", [], TimeSpan.FromMilliseconds(10)),
            new PlanNode("long_dossier", ["z_feeds_long_chain"], TimeSpan.FromSeconds(5)),
        ]);
        var order = new ConcurrentQueue<string>();
        await new DagExecutor(new ExecutorOptions { MaxConcurrency = 1 }).RunAsync(plan, Work(plan, ctx => { order.Enqueue(ctx.NodeId); return Task.FromResult<object?>(null); }));
        Assert.Equal("z_feeds_long_chain", order.First());
    }

    [Fact]
    public async Task Parallel_run_takes_about_the_critical_path_not_the_sum()
    {
        // Four 200 ms lookups in parallel, then a 100 ms join: ~300 ms, not ~900 ms.
        var plan = Plan.Build([.. Enumerable.Range(0, 4).Select(i => new PlanNode($"l{i}")), new PlanNode("join", "l0", "l1", "l2", "l3")]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new DagExecutor(new ExecutorOptions { MaxConcurrency = 4 }).RunAsync(plan, Work(plan, async ctx =>
        {
            await Task.Delay(ctx.NodeId == "join" ? 100 : 200, ctx.Cancellation);
            return null;
        }));
        Assert.InRange(sw.ElapsedMilliseconds, 280, 700);
    }

    [Fact]
    public async Task Missing_work_for_a_node_is_caught_before_anything_runs()
    {
        var plan = Plan.Build([new PlanNode("a"), new PlanNode("b", "a")]);
        await Assert.ThrowsAsync<ArgumentException>(() => new DagExecutor().RunAsync(plan, new Dictionary<string, NodeWork> { ["a"] = _ => Task.FromResult<object?>(null) }));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
