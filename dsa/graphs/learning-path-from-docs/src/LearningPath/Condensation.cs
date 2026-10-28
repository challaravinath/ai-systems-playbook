namespace LearningPath;

/// <summary>
/// The condensation of a graph: one node per strongly connected component, and an edge A to B when some
/// edge of the original graph goes from a node in A to a node in B (A != B). Each such pair appears once.
/// It is always a DAG: a cycle of components would make them all mutually reachable, i.e. one component.
/// </summary>
public sealed class Condensation
{
    private readonly int[] _memberStart; // members of component c: _members[_memberStart[c] .. _memberStart[c + 1]]
    private readonly int[] _members;

    private Condensation(Graph dag, int[] memberStart, int[] members)
    {
        Dag = dag;
        _memberStart = memberStart;
        _members = members;
    }

    /// <summary>The component graph. Node c here is component c.</summary>
    public Graph Dag { get; }

    public int Count => Dag.NodeCount;

    /// <summary>Original nodes in component c, ascending.</summary>
    public ReadOnlySpan<int> Members(int c) => _members.AsSpan(_memberStart[c], _memberStart[c + 1] - _memberStart[c]);

    /// <summary>Smallest original node in component c. Used for stable tie-breaking.</summary>
    public int MinMember(int c) => _members[_memberStart[c]];

    public static Condensation Build(Graph g, Components components)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(components);
        int n = g.NodeCount, k = components.Count;
        var of = components.Of;

        // Group nodes by component (counting sort). Walking v upward keeps each group ascending.
        var memberStart = new int[k + 1];
        foreach (var c in of) memberStart[c + 1]++;
        for (int c = 0; c < k; c++) memberStart[c + 1] += memberStart[c];
        var fill = memberStart[..k];
        var members = new int[n];
        for (int v = 0; v < n; v++) members[fill[of[v]]++] = v;

        // One edge per (A, B) pair: while scanning component A, seen[B] == A means A to B is already added.
        // Visiting components in id order and their members in node order makes the edge order deterministic.
        var seen = new int[k];
        Array.Fill(seen, -1);
        var edges = new List<(int, int)>();
        for (int a = 0; a < k; a++)
        {
            for (int i = memberStart[a]; i < memberStart[a + 1]; i++)
            {
                foreach (var w in g.Out(members[i]))
                {
                    int b = of[w];
                    if (b == a || seen[b] == a) continue;
                    seen[b] = a;
                    edges.Add((a, b));
                }
            }
        }
        return new Condensation(Graph.FromEdges(k, edges), memberStart, members);
    }

    /// <summary>
    /// Kahn's algorithm on the component DAG: repeatedly take a component with no unread prerequisites.
    /// When several are ready, the one whose smallest member is smallest goes first, so the order
    /// is fully determined by the input, not by how the SCC algorithm numbered things.
    /// </summary>
    public int[] TopologicalOrder()
    {
        var dag = Dag;
        int k = dag.NodeCount;
        var indegree = new int[k];
        foreach (var t in dag.Targets) indegree[t]++;

        var ready = new PriorityQueue<int, int>();
        for (int c = 0; c < k; c++)
            if (indegree[c] == 0) ready.Enqueue(c, MinMember(c));

        var order = new int[k];
        int placed = 0;
        while (ready.TryDequeue(out var c, out _))
        {
            order[placed++] = c;
            foreach (var d in dag.Out(c))
                if (--indegree[d] == 0) ready.Enqueue(d, MinMember(d));
        }

        // Can't happen for a real condensation; would mean the SCC step merged too little.
        if (placed != k) throw new InvalidOperationException("component graph has a cycle");
        return order;
    }
}
