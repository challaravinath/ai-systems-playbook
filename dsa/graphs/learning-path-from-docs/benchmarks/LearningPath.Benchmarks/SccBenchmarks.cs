using BenchmarkDotNet.Attributes;
using LearningPath;

namespace LearningPath.Benchmarks;

/// <summary>
/// Tarjan (one DFS) vs Kosaraju (two DFS passes plus building the reversed graph) on the same graphs.
/// Graph shape: Edges / 4 nodes, mostly forward edges (low id to high id) with 2% back edges, so there
/// are real cycles but also many single-node components, like a doc pile with a few tangles.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class SccBenchmarks
{
    [Params(10_000, 100_000, 1_000_000)]
    public int Edges { get; set; }

    private Graph _graph = null!;
    private Components _components = null!;

    [GlobalSetup]
    public void Setup()
    {
        _graph = Workload.Build(Edges, seed: 42);
        _components = Scc.Tarjan(_graph);
    }

    [Benchmark(Baseline = true)]
    public int Tarjan() => Scc.Tarjan(_graph).Count;

    [Benchmark]
    public int Kosaraju() => Scc.Kosaraju(_graph).Count;

    /// <summary>The step after SCC: build the component DAG and order it with Kahn's. For scale only.</summary>
    [Benchmark]
    public int CondenseAndOrder() => Condensation.Build(_graph, _components).TopologicalOrder().Length;
}

public static class Workload
{
    public static Graph Build(int edgeCount, int seed)
    {
        var rng = new Random(seed);
        int n = Math.Max(2, edgeCount / 4);
        var edges = new (int, int)[edgeCount];
        for (int i = 0; i < edgeCount; i++)
        {
            int a = rng.Next(n), b = rng.Next(n);
            if (a > b) (a, b) = (b, a);
            edges[i] = rng.NextDouble() < 0.02 ? (b, a) : (a, b); // 2% point backwards and can close a cycle
        }
        return Graph.FromEdges(n, edges);
    }
}
