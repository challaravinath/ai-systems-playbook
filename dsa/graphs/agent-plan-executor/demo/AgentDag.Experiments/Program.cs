// What-if experiments for the agent plan executor.
// Every plan and latency here is SIMULATED: the shapes and times are assumptions, written down below.
// Sections 1, 2, 6, 7 and 8 run the real DagExecutor with Task.Delay. Sections 3 to 5 use Sim, a small
// event simulator of the same scheduling rule, after section 2 checks it against the real executor.
// Run: dotnet run -c Release --project demo/AgentDag.Experiments
using System.Diagnostics;
using AgentDag;

var sections = args.Length == 0 ? null : args.Select(int.Parse).ToHashSet();
bool Run(int n) => sections is null || sections.Contains(n);

if (Run(1)) await Experiments.WorkedExample();
if (Run(2)) await Experiments.SimulatorAgreesWithTheExecutor();
if (Run(3)) Experiments.PolicySweep("Agent plans (20 candidates, 30% need a long extra step)", seed => Plans.AgentPlan(seed, 20));
if (Run(4)) Experiments.PolicySweep("Random 300-node plans (each earlier node is a parent with probability 0.02)", seed => Plans.RandomPlan(seed, 300));
if (Run(9)) Experiments.PolicySweep("The same agent plans, every estimate exact (no noise)", seed => Plans.AgentPlan(seed, 20, noise: 0));
if (Run(5)) Experiments.GrahamBound();
if (Run(6)) await Experiments.SchedulerOverhead();
if (Run(7)) await Experiments.SkipCost();
if (Run(8)) await Experiments.StrayTool();
if (Run(10)) await Experiments.StraysAreOutsideK();
if (Run(11)) Experiments.OldSkipWalk();
if (Run(12)) Experiments.Anomalies();
if (Run(13)) await Experiments.TimerRoundingAtFullScale();

/// <summary>A plan plus the time each step actually takes in one run. The plan's EstimatedLatency is only the prior.</summary>
public sealed record Scenario(Plan Plan, IReadOnlyDictionary<string, double> ActualMs);

public enum Policy { CriticalPathFirst, Fifo, LevelByLevel }

public static class Plans
{
    /// <summary>The six-step dossier plan from the README and the tests. Estimates are illustrative.</summary>
    public static Scenario Dossier()
    {
        var ms = new Dictionary<string, double> { ["parse"] = 100, ["web_lookup"] = 2000, ["score"] = 300, ["rerank"] = 400, ["fairness"] = 50, ["dossier"] = 800 };
        var plan = Plan.Build([
            N("parse", 100), N("web_lookup", 2000, "parse"), N("score", 300, "parse"),
            N("rerank", 400, "score"), N("fairness", 50, "rerank"), N("dossier", 800, "web_lookup", "fairness"),
        ]);
        return new(plan, ms);
    }

    /// <summary>
    /// An assumed agent plan. plan -> shortlist -> per candidate: lookup -> score -> dossier, and 30% of candidates
    /// also need portfolio_review (between lookup and dossier). All dossiers feed panel_pack.
    /// Estimates are each tool's median; actual times are lognormal around it (sigma in brackets):
    /// plan 1500 (0.3), shortlist 400 (0.3), lookup 800 (0.8), score 600 (0.4), portfolio_review 4000 (0.5),
    /// dossier 2000 (0.4), panel_pack 3000 (0.3) ms.
    /// </summary>
    public static Scenario AgentPlan(int seed, int candidates, double noise = 1)
    {
        var rng = new Random(seed);
        var nodes = new List<PlanNode>();
        var ms = new Dictionary<string, double>();
        void Add(string id, double median, double sigma, params string[] deps)
        {
            nodes.Add(new PlanNode(id, deps, TimeSpan.FromMilliseconds(median)));
            ms[id] = median * Math.Exp(noise * sigma * Normal(rng));
        }
        Add("plan", 1500, 0.3);
        Add("shortlist", 400, 0.3, "plan");
        var dossiers = new List<string>();
        for (var c = 0; c < candidates; c++)
        {
            Add($"lookup_{c:D2}", 800, 0.8, "shortlist");
            Add($"score_{c:D2}", 600, 0.4, $"lookup_{c:D2}");
            if (rng.NextDouble() < 0.3)
            {
                Add($"portfolio_review_{c:D2}", 4000, 0.5, $"lookup_{c:D2}");
                Add($"dossier_{c:D2}", 2000, 0.4, $"score_{c:D2}", $"portfolio_review_{c:D2}");
            }
            else Add($"dossier_{c:D2}", 2000, 0.4, $"score_{c:D2}");
            dossiers.Add($"dossier_{c:D2}");
        }
        Add("panel_pack", 3000, 0.3, [.. dossiers]);
        return new(Plan.Build(nodes), ms);
    }

    /// <summary>The random DAG from the tests: each earlier node is a parent with probability 0.02.
    /// Estimates are 50 to 2,000 ms; actual times are lognormal around them (sigma 0.5).</summary>
    public static Scenario RandomPlan(int seed, int n)
    {
        var rng = new Random(seed);
        var nodes = new List<PlanNode>();
        var ms = new Dictionary<string, double>();
        for (var i = 0; i < n; i++)
        {
            var est = 50 + rng.NextDouble() * 1950;
            var deps = Enumerable.Range(0, i).Where(_ => rng.NextDouble() < 0.02).Select(j => $"n{j:D3}").ToArray();
            nodes.Add(new PlanNode($"n{i:D3}", deps, TimeSpan.FromMilliseconds(est)));
            ms[$"n{i:D3}"] = est * Math.Exp(0.5 * Normal(rng));
        }
        return new(Plan.Build(nodes), ms);
    }

    public static Scenario Scaled(Scenario s, double factor)
        => new(s.Plan, s.ActualMs.ToDictionary(kv => kv.Key, kv => kv.Value * factor));

    private static PlanNode N(string id, int ms, params string[] deps) => new(id, deps, TimeSpan.FromMilliseconds(ms));
    private static double Normal(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}

/// <summary>
/// Event simulation of a run on K slots, with actual times known. CriticalPathFirst is the executor's rule
/// (highest bottom level from the estimates, then id). Fifo starts ready steps in the order they became ready,
/// then id. LevelByLevel waits for a whole level (steps at the same depth) before starting the next.
/// </summary>
public static class Sim
{
    public static double Makespan(Scenario s, int k, Policy policy)
        => policy == Policy.LevelByLevel ? LevelByLevel(s, k) : Live(s, k, policy);

    private static double Live(Scenario s, int k, Policy policy)
    {
        var plan = s.Plan;
        var waiting = plan.Parents.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var ready = new PriorityQueue<string, (double, double, string)>();
        double seq = 0;
        void MakeReady(string id, double at) => ready.Enqueue(id, policy == Policy.CriticalPathFirst
            ? (-plan.BottomLevel[id].TotalMilliseconds, 0, id) : (at, seq++, id));
        foreach (var (id, n) in waiting) if (n == 0) MakeReady(id, 0);

        var running = new PriorityQueue<string, (double, string)>();
        double now = 0;
        while (true)
        {
            while (running.Count < k && ready.TryDequeue(out var id, out _)) running.Enqueue(id, (now + s.ActualMs[id], id));
            if (!running.TryDequeue(out var done, out var at)) break;
            now = at.Item1;
            foreach (var c in plan.Children[done]) if (--waiting[c] == 0) MakeReady(c, now);
        }
        return now;
    }

    private static double LevelByLevel(Scenario s, int k)
    {
        double total = 0;
        foreach (var level in Levels(s.Plan))
        {
            var slots = new PriorityQueue<int, double>();
            for (var i = 0; i < k; i++) slots.Enqueue(i, 0);
            double end = 0;
            foreach (var id in level)
            {
                slots.TryDequeue(out var slot, out var free);
                var finish = free + s.ActualMs[id];
                end = Math.Max(end, finish);
                slots.Enqueue(slot, finish);
            }
            total += end;
        }
        return total;
    }

    /// <summary>Depth = the most edges on any path from a root. Each level is sorted by id.</summary>
    public static List<List<string>> Levels(Plan plan)
    {
        var depth = new Dictionary<string, int>();
        foreach (var id in plan.TopologicalOrder) depth[id] = plan.Parents[id].Select(p => depth[p] + 1).DefaultIfEmpty(0).Max();
        return depth.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
            .Select(g => g.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList()).ToList();
    }

    public static (double Work, double Chain) Bounds(Scenario s)
    {
        var work = s.ActualMs.Values.Sum();
        var finish = new Dictionary<string, double>();
        foreach (var id in s.Plan.TopologicalOrder)
            finish[id] = s.ActualMs[id] + s.Plan.Parents[id].Select(p => finish[p]).DefaultIfEmpty(0).Max();
        return (work, finish.Values.DefaultIfEmpty(0).Max());
    }
}

public static class Real
{
    /// <summary>The real executor, each step a Task.Delay of its actual time. Returns wall-clock ms.</summary>
    public static async Task<double> Executor(Scenario s, int k)
    {
        var work = s.Plan.Nodes.Keys.ToDictionary(id => id, id => (NodeWork)(async ctx =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(s.ActualMs[id]), ctx.Cancellation);
            return null;
        }));
        var sw = Stopwatch.StartNew();
        var run = await new DagExecutor(new ExecutorOptions { MaxConcurrency = k }).RunAsync(s.Plan, work);
        if (!run.Succeeded) throw new InvalidOperationException("run failed");
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>The common first attempt: await Task.WhenAll for each level, at most K at a time.</summary>
    public static async Task<double> LevelByLevel(Scenario s, int k)
    {
        using var gate = new SemaphoreSlim(k);
        var sw = Stopwatch.StartNew();
        foreach (var level in Sim.Levels(s.Plan))
            await Task.WhenAll(level.Select(async id =>
            {
                await gate.WaitAsync();
                try { await Task.Delay(TimeSpan.FromMilliseconds(s.ActualMs[id])); }
                finally { gate.Release(); }
            }));
        return sw.Elapsed.TotalMilliseconds;
    }
}

public static class Experiments
{
    static double Median(IEnumerable<double> xs) { var a = xs.Order().ToArray(); return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }

    public static async Task WorkedExample()
    {
        Console.WriteLine("== 1. The six-step dossier plan, K = 4 (illustrative times), median of 5 real runs");
        var s = Plans.Dossier();
        var (work, chain) = Sim.Bounds(s);
        Console.WriteLine($"   critical path {chain} ms, total work {work} ms");
        Console.WriteLine($"   levels: {string.Join(" | ", Sim.Levels(s.Plan).Select(l => string.Join(", ", l)))}");
        var live = new List<double>(); var level = new List<double>();
        await Real.Executor(s, 4); // warm-up
        for (var i = 0; i < 5; i++) { live.Add(await Real.Executor(s, 4)); level.Add(await Real.LevelByLevel(s, 4)); }
        Console.WriteLine($"   executor:       real {Median(live):F0} ms, simulated {Sim.Makespan(s, 4, Policy.CriticalPathFirst):F0} ms");
        Console.WriteLine($"   level by level: real {Median(level):F0} ms, simulated {Sim.Makespan(s, 4, Policy.LevelByLevel):F0} ms");
        Console.WriteLine();
    }

    public static async Task SimulatorAgreesWithTheExecutor()
    {
        Console.WriteLine("== 2. Simulator vs real executor: 10 agent plans, times scaled to 1/10, K = 4");
        var worst = 0.0;
        for (var seed = 1; seed <= 10; seed++)
        {
            var s = Plans.Scaled(Plans.AgentPlan(seed, 20), 0.1);
            var sim = Sim.Makespan(s, 4, Policy.CriticalPathFirst);
            var real = await Real.Executor(s, 4);
            var diff = (real - sim) / sim;
            worst = Math.Max(worst, Math.Abs(diff));
            Console.WriteLine($"   seed {seed,2}: simulated {sim,6:F0} ms, real {real,6:F0} ms, {diff:+0.0%;-0.0%}");
        }
        Console.WriteLine($"   worst difference {worst:P1}");
        Console.WriteLine();
    }

    public static void PolicySweep(string name, Func<int, Scenario> make)
    {
        const int seeds = 200;
        Console.WriteLine($"== {name}, {seeds} seeds, simulated");
        Console.WriteLine("   run time divided by the lower bound max(critical path, work / K), mean over seeds");
        Console.WriteLine("    K   critical-path-first   FIFO    level-by-level   CP-first beats FIFO   CP-first worse than FIFO");
        var scenarios = Enumerable.Range(1, seeds).Select(make).ToList();
        foreach (var k in new[] { 1, 2, 4, 8, 16, 64 })
        {
            double cp = 0, fifo = 0, lvl = 0, worstLoss = 0, bestWin = 0; int better = 0, worse = 0;
            var diffs = new List<double>();
            foreach (var s in scenarios)
            {
                var (work, chain) = Sim.Bounds(s);
                var lb = Math.Max(chain, work / k);
                var a = Sim.Makespan(s, k, Policy.CriticalPathFirst);
                var b = Sim.Makespan(s, k, Policy.Fifo);
                cp += a / lb; fifo += b / lb; lvl += Sim.Makespan(s, k, Policy.LevelByLevel) / lb;
                if (a < b - 1e-6) better++; else if (a > b + 1e-6) worse++;
                worstLoss = Math.Max(worstLoss, a / b - 1); bestWin = Math.Max(bestWin, 1 - a / b);
                diffs.Add(a / b - 1);
            }
            Console.WriteLine($"   {k,2}   {cp / seeds,10:F3}          {fifo / seeds,7:F3}   {lvl / seeds,9:F3}          {better / (double)seeds,8:P0}            {worse / (double)seeds,8:P0}      best win {bestWin:P1}, worst loss {worstLoss:P1}, median {Median(diffs):+0.00%;-0.00%}");
        }
        Console.WriteLine();
    }

    public static void GrahamBound()
    {
        Console.WriteLine("== 5. Graham's bound T <= W/K + L, checked on every simulated live run (both families, K 1 to 64)");
        var worst = 0.0; var runs = 0;
        foreach (var make in new Func<int, Scenario>[] { seed => Plans.AgentPlan(seed, 20), seed => Plans.RandomPlan(seed, 300) })
            for (var seed = 1; seed <= 200; seed++)
            {
                var s = make(seed);
                var (work, chain) = Sim.Bounds(s);
                foreach (var k in new[] { 1, 2, 4, 8, 16, 64 })
                    foreach (var p in new[] { Policy.CriticalPathFirst, Policy.Fifo })
                    {
                        worst = Math.Max(worst, Sim.Makespan(s, k, p) / (work / k + chain));
                        runs++;
                    }
            }
        Console.WriteLine($"   {runs} runs, largest T / (W/K + L) = {worst:F3}");
        Console.WriteLine();
    }

    public static async Task SchedulerOverhead()
    {
        Console.WriteLine("== 6. Scheduler overhead: steps that do no work, real executor, median of 5");
        foreach (var (name, nodes) in new (string, List<PlanNode>)[]
        {
            ("100,000 independent steps, K = 16", [.. Enumerable.Range(0, 100_000).Select(i => new PlanNode($"n{i}"))]),
            ("a 100,000-step chain, K = 16", [new PlanNode("n0"), .. Enumerable.Range(1, 99_999).Select(i => new PlanNode($"n{i}", $"n{i - 1}"))]),
        })
        {
            var plan = Plan.Build(nodes);
            var work = plan.Nodes.Keys.ToDictionary(id => id, _ => (NodeWork)(_ => Task.FromResult<object?>(null)));
            var times = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var sw = Stopwatch.StartNew();
                await new DagExecutor(new ExecutorOptions { MaxConcurrency = 16 }).RunAsync(plan, work);
                if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"   {name}: {Median(times):F0} ms, {Median(times) * 1000 / nodes.Count:F1} microseconds per step");
        }
        Console.WriteLine();
    }

    public static async Task SkipCost()
    {
        Console.WriteLine("== 7. Skipping: N failing lookups that all feed one N-step chain, real executor");
        foreach (var n in new[] { 1_000, 2_000, 4_000, 20_000 })
        {
            var nodes = new List<PlanNode>();
            nodes.AddRange(Enumerable.Range(0, n).Select(i => new PlanNode($"lookup{i}")));
            nodes.Add(new PlanNode("chain0", [.. Enumerable.Range(0, n).Select(i => $"lookup{i}")], TimeSpan.Zero));
            nodes.AddRange(Enumerable.Range(1, n - 1).Select(i => new PlanNode($"chain{i}", $"chain{i - 1}")));
            var plan = Plan.Build(nodes);
            var work = plan.Nodes.Keys.ToDictionary(id => id, _ => (NodeWork)(_ => throw new InvalidOperationException("404")));
            var sw = Stopwatch.StartNew();
            var run = await new DagExecutor(new ExecutorOptions { MaxConcurrency = 16 }).RunAsync(plan, work);
            Console.WriteLine($"   N = {n,6:N0}: {sw.Elapsed.TotalMilliseconds,7:F0} ms, {run.Nodes.Values.Count(r => r.Status == NodeStatus.Skipped):N0} skipped");
        }
        Console.WriteLine();
    }

    public static async Task StraysAreOutsideK()
    {
        Console.WriteLine("== 10. K counts steps, not tool calls: K = 1, 300 ms timeout. a_rude ignores its token and runs 1,000 ms; b1 to b3 take 100 ms each");
        var plan = Plan.Build([new PlanNode("a_rude"), new PlanNode("b1"), new PlanNode("b2"), new PlanNode("b3")]);
        int now = 0, peak = 0;
        NodeWork Tracked(int ms, bool ignoresToken) => async ctx =>
        {
            var c = Interlocked.Increment(ref now);
            lock (plan) peak = Math.Max(peak, c);
            try { await Task.Delay(ms, ignoresToken ? CancellationToken.None : ctx.Cancellation); }
            finally { Interlocked.Decrement(ref now); }
            return null;
        };
        var work = new Dictionary<string, NodeWork> { ["a_rude"] = Tracked(1000, true), ["b1"] = Tracked(100, false), ["b2"] = Tracked(100, false), ["b3"] = Tracked(100, false) };
        var opts = new ExecutorOptions { MaxConcurrency = 1, NodeTimeout = TimeSpan.FromMilliseconds(300), CancellationGrace = TimeSpan.FromMilliseconds(100) };
        var run = await new DagExecutor(opts).RunAsync(plan, work);
        await run.StrayWork;
        Console.WriteLine($"   most tool calls running at once: {peak} (K = 1). a_rude {run["a_rude"].Status}, b1 to b3 {string.Join(", ", new[] { "b1", "b2", "b3" }.Select(id => run[id].Status))}");
        Console.WriteLine();
    }

    public static void OldSkipWalk()
    {
        Console.WriteLine("== 11. The skip walk alone, old against new: N failed lookups that all feed one N-step chain");
        foreach (var n in new[] { 1_000, 2_000, 4_000, 20_000 })
        {
            var nodes = new List<PlanNode>();
            nodes.AddRange(Enumerable.Range(0, n).Select(i => new PlanNode($"lookup{i}")));
            nodes.Add(new PlanNode("chain0", [.. Enumerable.Range(0, n).Select(i => $"lookup{i}")], TimeSpan.Zero));
            nodes.AddRange(Enumerable.Range(1, n - 1).Select(i => new PlanNode($"chain{i}", $"chain{i - 1}")));
            var plan = Plan.Build(nodes);

            // Old: every failure walks all of its descendants, even ones already skipped.
            var sw = Stopwatch.StartNew();
            var marked = new Dictionary<string, bool>();
            for (var i = 0; i < n; i++) foreach (var d in plan.Descendants($"lookup{i}")) marked.TryAdd(d, true);
            var oldMs = sw.Elapsed.TotalMilliseconds;

            // New: the walk stops at steps already marked.
            sw.Restart();
            marked.Clear();
            for (var i = 0; i < n; i++)
            {
                var stack = new Stack<string>(plan.Children[$"lookup{i}"]);
                while (stack.TryPop(out var d))
                    if (marked.TryAdd(d, true)) foreach (var c in plan.Children[d]) stack.Push(c);
            }
            Console.WriteLine($"   N = {n,6:N0}: old {oldMs,9:F0} ms, new {sw.Elapsed.TotalMilliseconds,5:F1} ms");
        }
        Console.WriteLine();
    }

    public static void Anomalies()
    {
        Console.WriteLine("== 12. Graham's anomalies: seeds where one more slot made the simulated run slower, K from 1 to 32");
        foreach (var (name, make) in new (string, Func<int, Scenario>)[] { ("agent plans", seed => Plans.AgentPlan(seed, 20)), ("random plans", seed => Plans.RandomPlan(seed, 300)) })
            foreach (var p in new[] { Policy.CriticalPathFirst, Policy.Fifo })
            {
                int seedsWith = 0; double worst = 0;
                for (var seed = 1; seed <= 200; seed++)
                {
                    var s = make(seed);
                    var prev = Sim.Makespan(s, 1, p); var any = false;
                    for (var k = 2; k <= 32; k++)
                    {
                        var t = Sim.Makespan(s, k, p);
                        if (t > prev + 1e-6) { any = true; worst = Math.Max(worst, t / prev - 1); }
                        prev = t;
                    }
                    if (any) seedsWith++;
                }
                Console.WriteLine($"   {name}, {p}: {seedsWith} of 200 seeds, worst slowdown {worst:P1}");
            }
        Console.WriteLine();
    }

    public static async Task TimerRoundingAtFullScale()
    {
        Console.WriteLine("== 13. Is timer rounding the gap? The same check at full scale (times x 10 against section 2), 3 seeds, K = 4");
        for (var seed = 1; seed <= 3; seed++)
        {
            var s = Plans.AgentPlan(seed, 20);
            var sim = Sim.Makespan(s, 4, Policy.CriticalPathFirst);
            var real = await Real.Executor(s, 4);
            Console.WriteLine($"   seed {seed}: simulated {sim,6:F0} ms, real {real,6:F0} ms, {(real - sim) / sim:+0.0%;-0.0%}, {real - sim:F0} ms");
        }
        Console.WriteLine();
    }

    public static async Task StrayTool()
    {
        Console.WriteLine("== 8. A tool that ignores its token: 50 ms timeout, 3 attempts, K = 1, the tool runs 500 ms");
        var plan = Plan.Build([new PlanNode("send_email")]);
        int calls = 0, now = 0, peak = 0;
        var work = new Dictionary<string, NodeWork>
        {
            ["send_email"] = async _ =>
            {
                Interlocked.Increment(ref calls);
                var c = Interlocked.Increment(ref now);
                if (c > Volatile.Read(ref peak)) Volatile.Write(ref peak, c);
                await Task.Delay(500);
                Interlocked.Decrement(ref now);
                return null;
            },
        };
        var opts = new ExecutorOptions { NodeTimeout = TimeSpan.FromMilliseconds(50), MaxAttempts = 3, MaxConcurrency = 1, Backoff = _ => TimeSpan.Zero, CancellationGrace = TimeSpan.FromMilliseconds(100) };
        var sw = Stopwatch.StartNew();
        var run = await new DagExecutor(opts).RunAsync(plan, work);
        Console.WriteLine($"   status {run["send_email"].Status}, calls {calls}, most copies at once {peak}, still running at return: [{string.Join(", ", run.StillRunning)}], returned after {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"   error: {run["send_email"].Error?.Message}");
        Console.WriteLine();
    }
}
