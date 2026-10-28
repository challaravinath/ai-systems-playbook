using LearningPath;

namespace LearningPath.Tests;

internal static class Oracle
{
    /// <summary>reach[u][v]: v can be reached from u (every node reaches itself). Plain BFS from every node.</summary>
    public static bool[][] Reachability(int n, IReadOnlyList<(int From, int To)> edges)
    {
        var adj = new List<int>[n];
        for (int v = 0; v < n; v++) adj[v] = [];
        foreach (var (a, b) in edges) adj[a].Add(b);

        var reach = new bool[n][];
        for (int s = 0; s < n; s++)
        {
            var seen = reach[s] = new bool[n];
            var queue = new Queue<int>();
            seen[s] = true;
            queue.Enqueue(s);
            while (queue.TryDequeue(out var v))
                foreach (var w in adj[v])
                    if (!seen[w]) { seen[w] = true; queue.Enqueue(w); }
        }
        return reach;
    }

    /// <summary>Relabels components by first appearance (node 0's component becomes 0, ...), so two partitions can be compared.</summary>
    public static int[] Canonical(Components c)
    {
        var map = new int[c.Count];
        Array.Fill(map, -1);
        var result = new int[c.Of.Length];
        int next = 0;
        for (int v = 0; v < c.Of.Length; v++)
        {
            ref var m = ref map[c.Of[v]];
            if (m == -1) m = next++;
            result[v] = m;
        }
        return result;
    }

    /// <summary>
    /// Random graph with a mix of shapes: mostly forward edges (low id to high id, no cycles) plus a few back
    /// edges that close cycles, sometimes dense, sometimes a planted ring. Self-loops and duplicates happen too.
    /// </summary>
    public static (int N, List<(int, int)> Edges) RandomGraph(Random rng)
    {
        int n = rng.Next(0, 41);
        var edges = new List<(int, int)>();
        if (n == 0) return (n, edges);

        int m = rng.Next(0, 3 * n + 1);
        double backShare = rng.NextDouble() * 0.3;
        for (int i = 0; i < m; i++)
        {
            int a = rng.Next(n), b = rng.Next(n);
            bool forward = rng.NextDouble() >= backShare;
            edges.Add(forward == (a <= b) ? (a, b) : (b, a));
        }
        if (rng.Next(4) == 0 && n >= 3)
        {
            // plant a ring over a random subset
            var ring = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).Take(rng.Next(2, n + 1)).ToArray();
            for (int i = 0; i < ring.Length; i++) edges.Add((ring[i], ring[(i + 1) % ring.Length]));
        }
        return (n, edges);
    }
}

public class AgainstBruteForce
{
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 20).Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Tarjan_matches_reachability_and_Kosaraju(int seed)
    {
        // 20 seeds x 50 graphs = 1000 random graphs
        var rng = new Random(seed);
        for (int round = 0; round < 50; round++)
        {
            var (n, edges) = Oracle.RandomGraph(rng);
            var g = Graph.FromEdges(n, edges);
            var reach = Oracle.Reachability(n, edges);
            var tarjan = Scc.Tarjan(g);
            var kosaraju = Scc.Kosaraju(g);

            // Same component exactly when each reaches the other.
            for (int u = 0; u < n; u++)
                for (int v = 0; v < n; v++)
                    Assert.Equal(reach[u][v] && reach[v][u], tarjan.Of[u] == tarjan.Of[v]);

            // Every id 0..Count-1 is used.
            Assert.Equal(tarjan.Count, tarjan.Of.Distinct().Count());
            Assert.All(tarjan.Of, c => Assert.InRange(c, 0, tarjan.Count - 1));

            // Same partition from both algorithms.
            Assert.Equal(tarjan.Count, kosaraju.Count);
            Assert.Equal(Oracle.Canonical(tarjan), Oracle.Canonical(kosaraju));

            // Numbering: Tarjan closes sinks first, Kosaraju finds sources first.
            foreach (var (a, b) in edges)
            {
                if (tarjan.Of[a] != tarjan.Of[b]) Assert.True(tarjan.Of[a] > tarjan.Of[b]);
                if (kosaraju.Of[a] != kosaraju.Of[b]) Assert.True(kosaraju.Of[a] < kosaraju.Of[b]);
            }

            CheckCondensation(g, edges, tarjan);
        }
    }

    private static void CheckCondensation(Graph g, List<(int From, int To)> edges, Components comps)
    {
        var cond = Condensation.Build(g, comps);
        var order = cond.TopologicalOrder();

        // order is a permutation of the components
        Assert.Equal(Enumerable.Range(0, cond.Count), order.Order());
        var pos = new int[cond.Count];
        for (int i = 0; i < order.Length; i++) pos[order[i]] = i;

        // DAG edges are exactly the distinct cross-component pairs, and no self-loops
        var expected = edges.Select(e => (comps.Of[e.From], comps.Of[e.To])).Where(p => p.Item1 != p.Item2).Distinct().Order().ToList();
        var actual = Enumerable.Range(0, cond.Count).SelectMany(c => cond.Dag.Out(c).ToArray().Select(d => (c, d))).Order().ToList();
        Assert.Equal(expected, actual);

        // Every DAG edge goes forward in the order. With a permutation that also proves the DAG has no cycle.
        foreach (var (a, b) in actual) Assert.True(pos[a] < pos[b]);

        // And every original edge between different components is respected.
        foreach (var (u, v) in edges)
            if (comps.Of[u] != comps.Of[v]) Assert.True(pos[comps.Of[u]] < pos[comps.Of[v]]);

        // Members are grouped right and ascending
        for (int c = 0; c < cond.Count; c++)
        {
            var members = cond.Members(c).ToArray();
            Assert.Equal(members.Order(), members);
            Assert.All(members, v => Assert.Equal(c, comps.Of[v]));
            Assert.Equal(members[0], cond.MinMember(c));
        }
        Assert.Equal(g.NodeCount, Enumerable.Range(0, cond.Count).Sum(c => cond.Members(c).Length));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Plans_from_both_algorithms_are_identical(int seed)
    {
        var rng = new Random(1000 + seed);
        for (int round = 0; round < 50; round++)
        {
            var (n, edges) = Oracle.RandomGraph(rng);
            var docs = Enumerable.Range(0, n)
                .Select(v => new Doc($"doc{v:D2}", [.. edges.Where(e => e.Item2 == v).Select(e => $"doc{e.Item1:D2}")]))
                .ToList();
            Assert.Equal(T.Path(docs, SccAlgorithm.Tarjan), T.Path(docs, SccAlgorithm.Kosaraju));
        }
    }

    [Fact]
    public void Every_prerequisite_comes_earlier_or_in_the_same_group()
    {
        var rng = new Random(99);
        for (int round = 0; round < 300; round++)
        {
            var (n, edges) = Oracle.RandomGraph(rng);
            var docs = Enumerable.Range(0, n)
                .Select(v => new Doc($"doc{v:D2}", [.. edges.Where(e => e.Item2 == v).Select(e => $"doc{e.Item1:D2}")]))
                .ToList();
            var plan = DocSet.Plan(docs);
            var group = new Dictionary<string, int>();
            for (int i = 0; i < plan.Count; i++)
                foreach (var d in plan[i].Docs) group[d] = i;

            Assert.Equal(n, group.Count);
            foreach (var d in docs)
                foreach (var dep in d.DependsOn)
                    Assert.True(group[dep] <= group[d.Id]);
        }
    }
}

public class LargeGraphs
{
    private const int Million = 1_000_000;

    // Runs on a thread with a 256 KB stack. A recursive DFS would need a frame per node on a
    // million-deep path and die here; the iterative versions use none.
    private static void OnSmallStack(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Exception("failed on small stack", error);
    }

    private static Graph Chain(int n, bool closeRing)
    {
        var edges = new List<(int, int)>(n);
        for (int v = 0; v + 1 < n; v++) edges.Add((v, v + 1));
        if (closeRing) edges.Add((n - 1, 0));
        return Graph.FromEdges(n, edges);
    }

    [Fact]
    public void Million_node_chain_does_not_overflow()
    {
        var g = Chain(Million, closeRing: false);
        OnSmallStack(() =>
        {
            var tarjan = Scc.Tarjan(g);
            Assert.Equal(Million, tarjan.Count);
            Assert.Equal(Million - 1, tarjan.Of[0]); // the source is closed last

            var kosaraju = Scc.Kosaraju(g);
            Assert.Equal(Million, kosaraju.Count);
            Assert.Equal(0, kosaraju.Of[0]);

            var order = Condensation.Build(g, tarjan).TopologicalOrder();
            for (int i = 0; i < Million; i++) Assert.Equal(tarjan.Of[i], order[i]);
        });
    }

    [Fact]
    public void Million_node_ring_is_one_component()
    {
        var g = Chain(Million, closeRing: true);
        OnSmallStack(() =>
        {
            Assert.Equal(1, Scc.Tarjan(g).Count);
            Assert.Equal(1, Scc.Kosaraju(g).Count);
        });
    }

    [Fact]
    public void Million_doc_chain_through_the_doc_api()
    {
        var docs = new Doc[Million];
        docs[0] = new Doc("d0000000");
        for (int i = 1; i < Million; i++) docs[i] = new Doc($"d{i:D7}", $"d{i - 1:D7}");
        IReadOnlyList<ReadingGroup>? plan = null;
        OnSmallStack(() => plan = DocSet.Plan(docs));
        Assert.Equal(Million, plan!.Count);
        Assert.Equal("d0000000", plan[0].Docs[0]);
        Assert.Equal("d0999999", plan[^1].Docs[0]);
        Assert.DoesNotContain(plan, g => g.IsCycle);
    }
}
