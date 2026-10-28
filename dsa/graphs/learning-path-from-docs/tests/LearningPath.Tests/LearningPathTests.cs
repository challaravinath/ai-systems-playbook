using LearningPath;

namespace LearningPath.Tests;

internal static class T
{
    public static Doc D(string id, params string[] dependsOn) => new(id, dependsOn);

    /// <summary>The path as strings, a cycle shown as {a, b}, so a whole plan fits in one Assert.Equal.</summary>
    public static string[] Path(params Doc[] docs) => [.. DocSet.Plan(docs).Select(g => g.ToString())];

    public static string[] Path(IEnumerable<Doc> docs, SccAlgorithm algorithm) => [.. DocSet.Plan(docs, algorithm).Select(g => g.ToString())];
}

public class HandCases
{
    [Fact]
    public void Empty_pile_gives_empty_path()
    {
        Assert.Empty(T.Path());
    }

    [Fact]
    public void Single_doc()
    {
        Assert.Equal(["a"], T.Path(T.D("a")));
    }

    [Fact]
    public void Chain_is_read_prerequisite_first()
    {
        // c needs b, b needs a. Listed backwards on purpose.
        Assert.Equal(["a", "b", "c"], T.Path(T.D("c", "b"), T.D("b", "a"), T.D("a")));
    }

    [Fact]
    public void Diamond_breaks_ties_by_id()
    {
        // top needs left and right, both need base. left and right are both ready after base: "left" < "right".
        Assert.Equal(["base", "left", "right", "top"],
            T.Path(T.D("top", "right", "left"), T.D("right", "base"), T.D("left", "base"), T.D("base")));
    }

    [Fact]
    public void One_big_cycle_is_one_group()
    {
        Assert.Equal(["{a, b, c, d}"], T.Path(T.D("a", "d"), T.D("b", "a"), T.D("c", "b"), T.D("d", "c")));
    }

    [Fact]
    public void Two_cycles_linked()
    {
        // {a, b} and {x, y} are cycles; x needs b, so the whole first cycle comes before the second.
        Assert.Equal(["{a, b}", "{x, y}"],
            T.Path(T.D("y", "x"), T.D("x", "y", "b"), T.D("b", "a"), T.D("a", "b")));
    }

    [Fact]
    public void Two_cycles_linked_the_other_way()
    {
        // Same shape, but now a needs y: the {x, y} cycle goes first even though "a" sorts first.
        Assert.Equal(["{x, y}", "{a, b}"],
            T.Path(T.D("y", "x"), T.D("x", "y"), T.D("b", "a"), T.D("a", "b", "y")));
    }

    [Fact]
    public void Self_loop_is_a_cycle_of_one()
    {
        var plan = DocSet.Plan([T.D("a"), T.D("b", "b", "a")]);
        Assert.Equal(2, plan.Count);
        Assert.Equal(new ReadingGroup(["a"], false).ToString(), plan[0].ToString());
        Assert.Equal(["b"], plan[1].Docs);
        Assert.True(plan[1].IsCycle);
        Assert.False(plan[0].IsCycle);
    }

    [Fact]
    public void Disconnected_parts_interleave_by_id()
    {
        // Two separate chains: a1 to a2, b1 to b2. Ready set starts {a1, b1}; a1 first, then a2 (< b1).
        Assert.Equal(["a1", "a2", "b1", "b2"], T.Path(T.D("b2", "b1"), T.D("a2", "a1"), T.D("b1"), T.D("a1")));
    }

    [Fact]
    public void Doc_after_a_cycle_waits_for_the_whole_cycle()
    {
        Assert.Equal(["{p, q}", "z"], T.Path(T.D("z", "p"), T.D("p", "q"), T.D("q", "p")));
    }

    [Fact]
    public void Input_order_does_not_change_the_path()
    {
        Doc[] docs = [T.D("e", "d"), T.D("d", "c", "b"), T.D("c", "a"), T.D("b", "a", "c"), T.D("a"), T.D("f")];
        var expected = T.Path(docs);
        var rng = new Random(3);
        for (int r = 0; r < 20; r++)
            Assert.Equal(expected, T.Path([.. docs.OrderBy(_ => rng.Next())]));
    }
}

public class HiringAssistantDocs
{
    // The pile behind a hiring assistant. The rubric is defined against the dossier's fields, and the dossier
    // says which rubric scores it carries, so those two refer to each other and have to be read together.
    private static readonly Doc[] Pile =
    [
        T.D("interview loop", "scoring rubric", "candidate dossier", "job spec format"),
        T.D("scoring rubric", "job spec format", "candidate dossier"),
        T.D("candidate dossier", "scoring rubric", "job spec format"),
        T.D("job spec format"),
        T.D("offer decision", "interview loop", "scoring rubric"),
        T.D("tool permissions"),
        T.D("agent prompt", "tool permissions", "job spec format"),
    ];

    [Fact]
    public void Rubric_and_dossier_are_read_together()
    {
        var cycles = DocSet.Plan(Pile).Where(g => g.IsCycle).ToList();
        var only = Assert.Single(cycles);
        Assert.Equal(["candidate dossier", "scoring rubric"], only.Docs);
    }

    [Fact]
    public void Full_reading_order()
    {
        // Ready at start: "job spec format" and "tool permissions". Smallest id first each time:
        // job spec format, then {candidate dossier, scoring rubric} (its smallest member "candidate dossier" < "tool permissions"),
        // then interview loop, offer decision, tool permissions, and agent prompt last (it needs tool permissions).
        Assert.Equal(
            ["job spec format", "{candidate dossier, scoring rubric}", "interview loop", "offer decision", "tool permissions", "agent prompt"],
            T.Path(Pile));
    }

    [Fact]
    public void Kosaraju_gives_the_same_path()
    {
        Assert.Equal(T.Path(Pile, SccAlgorithm.Tarjan), T.Path(Pile, SccAlgorithm.Kosaraju));
    }
}

public class Validation
{
    [Fact]
    public void Unknown_dependency_is_reported_with_its_doc()
    {
        var ex = Assert.Throws<UnknownDependencyException>(() => new DocSet([T.D("a", "ghost"), T.D("b", "a", "phantom")]));
        Assert.Equal([("a", "ghost"), ("b", "phantom")], ex.Missing);
        Assert.Contains("'a' needs 'ghost'", ex.Message);
    }

    [Fact]
    public void Ids_are_case_sensitive()
    {
        Assert.Throws<UnknownDependencyException>(() => new DocSet([T.D("Rubric"), T.D("loop", "rubric")]));
    }

    [Fact]
    public void Duplicate_doc_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DocSet([T.D("a"), T.D("a")]));
    }

    [Fact]
    public void Empty_doc_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DocSet([T.D("")]));
    }

    [Fact]
    public void Duplicate_edges_count_once()
    {
        var set = new DocSet([T.D("a"), T.D("b", "a", "a", "a")]);
        Assert.Equal(1, set.Graph.EdgeCount);
        Assert.Equal(2, set.DuplicateEdges);
        Assert.Equal(["a", "b"], set.Plan().Select(g => g.ToString()));
    }

    [Fact]
    public void Self_loop_is_kept_as_an_edge()
    {
        var set = new DocSet([T.D("a", "a")]);
        Assert.Equal(1, set.Graph.EdgeCount);
        var only = Assert.Single(set.Plan());
        Assert.True(only.IsCycle);
    }

    [Fact]
    public void Graph_rejects_edges_outside_the_node_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Graph.FromEdges(2, [(0, 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Graph.FromEdges(2, [(-1, 0)]));
    }
}
