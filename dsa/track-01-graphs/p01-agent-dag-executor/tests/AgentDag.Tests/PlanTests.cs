using AgentDag;

namespace AgentDag.Tests;

public class PlanTests
{
    private static PlanNode N(string id, int ms, params string[] deps) => new(id, deps, TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void Plan_with_a_loop_is_rejected_and_the_loop_is_named()
    {
        // The planner LLM wrote: verify → write → critic → verify. A loop must never reach the executor.
        var ex = Assert.Throws<PlanValidationException>(() => Plan.Build([
            new PlanNode("retrieve"),
            new PlanNode("verify", "retrieve", "critic"),
            new PlanNode("write", "verify"),
            new PlanNode("critic", "write"),
        ]));
        Assert.NotNull(ex.Cycle);
        Assert.Equal(ex.Cycle![0], ex.Cycle[^1]);
        Assert.Equal(["critic", "verify", "write"], ex.Cycle.Distinct().Order(StringComparer.Ordinal));
        // Every consecutive pair is a real edge, parent → child.
        var deps = new Dictionary<string, string[]> { ["verify"] = ["retrieve", "critic"], ["write"] = ["verify"], ["critic"] = ["write"] };
        for (var i = 0; i + 1 < ex.Cycle.Count; i++) Assert.Contains(ex.Cycle[i], deps[ex.Cycle[i + 1]]);
    }

    [Fact]
    public void Node_that_waits_on_itself_is_a_cycle()
    {
        var ex = Assert.Throws<PlanValidationException>(() => Plan.Build([new PlanNode("a", "a")]));
        Assert.Equal(["a", "a"], ex.Cycle);
    }

    [Fact]
    public void Plan_that_calls_a_tool_nobody_defined_is_rejected()
    {
        var ex = Assert.Throws<PlanValidationException>(() => Plan.Build([new PlanNode("write", "search_linkedin")]));
        Assert.Contains("unknown node 'search_linkedin'", ex.Message);
    }

    [Fact]
    public void Two_nodes_with_the_same_id_are_rejected()
        => Assert.Throws<PlanValidationException>(() => Plan.Build([new PlanNode("a"), new PlanNode("a")]));

    [Fact]
    public void Order_respects_every_edge_and_is_the_same_every_time()
    {
        PlanNode[] nodes = [new("dossier", "verify", "score"), new("verify", "collect"), new("collect", "parse"), new("score", "parse"), new("parse")];
        var a = Plan.Build(nodes).TopologicalOrder;
        var b = Plan.Build(nodes.Reverse()).TopologicalOrder;
        Assert.Equal(a, b);
        foreach (var n in nodes)
            foreach (var d in n.DependsOn) Assert.True(a.ToList().IndexOf(d) < a.ToList().IndexOf(n.Id));
    }

    [Fact]
    public void Duplicate_dependencies_count_once()
    {
        var plan = Plan.Build([new PlanNode("a"), new PlanNode("b", "a", "a")]);
        Assert.Equal(["a"], plan.Parents["b"]);
        Assert.Equal(["b"], plan.Children["a"]);
    }

    [Fact]
    public void Critical_path_is_the_slowest_chain_not_the_longest_one()
    {
        // parse(100) → {web_lookup(2000) | score(300) → rerank(400) → fairness(50)} → dossier(800)
        var plan = Plan.Build([
            N("parse", 100), N("web_lookup", 2000, "parse"), N("score", 300, "parse"),
            N("rerank", 400, "score"), N("fairness", 50, "rerank"), N("dossier", 800, "web_lookup", "fairness"),
        ]);
        var (length, path) = plan.CriticalPath();
        Assert.Equal(TimeSpan.FromMilliseconds(2900), length);
        Assert.Equal(["parse", "web_lookup", "dossier"], path);
    }

    [Fact]
    public void Bottom_level_ranks_the_node_with_the_most_work_behind_it_highest()
    {
        var plan = Plan.Build([N("a", 10), N("b", 10), N("long_tail", 500, "b")]);
        Assert.True(plan.BottomLevel["b"] > plan.BottomLevel["a"]);
        Assert.Equal(TimeSpan.FromMilliseconds(510), plan.BottomLevel["b"]);
    }

    [Fact]
    public void Empty_plan_is_valid_and_takes_no_time()
    {
        var plan = Plan.Build([]);
        Assert.Empty(plan.TopologicalOrder);
        Assert.Equal(TimeSpan.Zero, plan.CriticalPath().Length);
    }

    [Fact]
    public void Hundred_thousand_node_chain_with_a_back_edge_does_not_overflow_the_stack()
    {
        const int n = 100_000;
        var nodes = Enumerable.Range(0, n).Select(i => new PlanNode($"n{i}", i == 0 ? [$"n{n - 1}"] : [$"n{i - 1}"], TimeSpan.Zero));
        var ex = Assert.Throws<PlanValidationException>(() => Plan.Build(nodes));
        Assert.Equal(n + 1, ex.Cycle!.Count);
    }
}
