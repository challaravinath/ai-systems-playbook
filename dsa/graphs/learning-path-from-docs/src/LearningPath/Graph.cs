using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LearningPath.Tests")]

namespace LearningPath;

/// <summary>
/// A directed graph over nodes 0..NodeCount-1, stored as compressed sparse rows (CSR):
/// the out-edges of v are Targets[Start[v] .. Start[v + 1]]. Two flat arrays instead of a list per node,
/// so a million-node graph is two allocations and the scans stay cache friendly.
///
/// Edge direction in this topic: "from" is the prerequisite, "to" is the doc that needs it,
/// so every edge reads "read from before to".
///
/// Duplicate edges and self-loops are kept as given. The SCC algorithms don't mind either;
/// <see cref="DocSet"/> is where input gets cleaned up.
/// </summary>
public sealed class Graph
{
    internal readonly int[] Start;   // length NodeCount + 1
    internal readonly int[] Targets; // length EdgeCount

    private Graph(int[] start, int[] targets)
    {
        Start = start;
        Targets = targets;
    }

    public int NodeCount => Start.Length - 1;
    public int EdgeCount => Targets.Length;

    /// <summary>Out-neighbours of <paramref name="v"/>, in the order the edges were given.</summary>
    public ReadOnlySpan<int> Out(int v) => Targets.AsSpan(Start[v], Start[v + 1] - Start[v]);

    public static Graph FromEdges(int nodeCount, IReadOnlyList<(int From, int To)> edges)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeCount);
        ArgumentNullException.ThrowIfNull(edges);

        // Counting sort by source: count, prefix-sum, then place. Keeps each node's edges in input order.
        var start = new int[nodeCount + 1];
        foreach (var (from, to) in edges)
        {
            if ((uint)from >= (uint)nodeCount || (uint)to >= (uint)nodeCount)
                throw new ArgumentOutOfRangeException(nameof(edges), $"edge ({from}, {to}) is outside 0..{nodeCount - 1}");
            start[from + 1]++;
        }
        for (int v = 0; v < nodeCount; v++) start[v + 1] += start[v];

        var fill = start[..nodeCount];
        var targets = new int[edges.Count];
        foreach (var (from, to) in edges) targets[fill[from]++] = to;
        return new Graph(start, targets);
    }

    /// <summary>Same nodes, every edge flipped. Kosaraju's second pass walks this.</summary>
    public Graph Reverse()
    {
        int n = NodeCount;
        var start = new int[n + 1];
        foreach (var t in Targets) start[t + 1]++;
        for (int v = 0; v < n; v++) start[v + 1] += start[v];

        var fill = start[..n];
        var targets = new int[Targets.Length];
        for (int v = 0; v < n; v++)
            for (int e = Start[v]; e < Start[v + 1]; e++)
                targets[fill[Targets[e]]++] = v;
        return new Graph(start, targets);
    }
}
