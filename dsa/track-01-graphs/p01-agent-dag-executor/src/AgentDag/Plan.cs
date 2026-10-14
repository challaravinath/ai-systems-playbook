namespace AgentDag;

/// <summary>One step of an agent's plan: a tool call, a model call, or a human gate.</summary>
/// <param name="Id">Unique within the plan.</param>
/// <param name="DependsOn">Nodes whose outputs this node needs. Order doesn't matter; duplicates are ignored.</param>
/// <param name="EstimatedLatency">A prior from telemetry (e.g. p50 of this tool). Used for the critical path and scheduling priority.</param>
public sealed record PlanNode(string Id, IReadOnlyList<string> DependsOn, TimeSpan EstimatedLatency)
{
    public PlanNode(string id, params string[] dependsOn) : this(id, dependsOn, TimeSpan.Zero) { }
}

public sealed class PlanValidationException(string message, IReadOnlyList<string>? cycle = null) : Exception(message)
{
    /// <summary>When the plan has a cycle: the nodes on it, first node repeated at the end (a → b → a).</summary>
    public IReadOnlyList<string>? Cycle { get; } = cycle;
}

/// <summary>
/// A validated, immutable plan. Building one is the "compiler" step: it rejects duplicate ids,
/// unknown dependencies and cycles before anything runs, and precomputes what the executor needs.
/// </summary>
public sealed class Plan
{
    public IReadOnlyDictionary<string, PlanNode> Nodes { get; }

    /// <summary>Distinct parents per node (the edges, pointing backwards).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Parents { get; }

    /// <summary>Children per node (the edges, pointing forwards). The executor walks these.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children { get; }

    /// <summary>A topological order. Deterministic: ties are broken by ordinal id.</summary>
    public IReadOnlyList<string> TopologicalOrder { get; }

    /// <summary>
    /// Longest estimated latency from the start of a node to the end of the run, through any descendants
    /// (the "bottom level" in list scheduling). Higher means more urgent: start it first.
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan> BottomLevel { get; }

    private Plan(Dictionary<string, PlanNode> nodes, Dictionary<string, IReadOnlyList<string>> parents,
                 Dictionary<string, IReadOnlyList<string>> children, List<string> order)
    {
        Nodes = nodes;
        Parents = parents;
        Children = children;
        TopologicalOrder = order;
        BottomLevel = ComputeBottomLevel(nodes, children, order);
    }

    /// <summary>Validate and build. O(V + E) apart from the O(V log V) deterministic tie-break.</summary>
    public static Plan Build(IEnumerable<PlanNode> nodes)
    {
        var map = new Dictionary<string, PlanNode>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id)) throw new PlanValidationException("A node has an empty id.");
            if (n.EstimatedLatency < TimeSpan.Zero) throw new PlanValidationException($"Node '{n.Id}' has a negative latency estimate.");
            if (!map.TryAdd(n.Id, n)) throw new PlanValidationException($"Duplicate node id '{n.Id}'.");
        }

        var parents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var children = map.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var n in map.Values)
        {
            var distinct = n.DependsOn.Distinct(StringComparer.Ordinal).ToList();
            foreach (var d in distinct)
            {
                if (d == n.Id) throw new PlanValidationException($"Node '{n.Id}' depends on itself.", [n.Id, n.Id]);
                if (!map.ContainsKey(d)) throw new PlanValidationException($"Node '{n.Id}' depends on unknown node '{d}'.");
                children[d].Add(n.Id);
            }
            parents[n.Id] = distinct;
        }

        // Kahn's algorithm. A min-heap on id (instead of a plain queue) makes the order reproducible,
        // which matters for audit replays and for tests.
        var indegree = parents.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
        var ready = new PriorityQueue<string, string>(StringComparer.Ordinal);
        foreach (var (id, deg) in indegree) if (deg == 0) ready.Enqueue(id, id);

        var order = new List<string>(map.Count);
        while (ready.TryDequeue(out var id, out _))
        {
            order.Add(id);
            foreach (var c in children[id])
                if (--indegree[c] == 0) ready.Enqueue(c, c);
        }

        // Nodes never reaching indegree 0 are on, or downstream of, a cycle.
        if (order.Count != map.Count)
        {
            var stuck = indegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            var cycle = FindCycle(stuck, parents);
            throw new PlanValidationException($"Plan has a cycle: {string.Join(" → ", cycle)}.", cycle);
        }

        return new Plan(map, parents,
            children.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal), order);
    }

    /// <summary>
    /// The longest chain of estimated latencies: a lower bound on the run's wall-clock time with unlimited
    /// parallelism. If this alone breaks the SLO, more workers won't help; the plan has to change.
    /// </summary>
    public (TimeSpan Length, IReadOnlyList<string> Path) CriticalPath()
    {
        if (TopologicalOrder.Count == 0) return (TimeSpan.Zero, []);
        var finish = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var via = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var id in TopologicalOrder)
        {
            var start = TimeSpan.Zero;
            string? best = null;
            foreach (var p in Parents[id])
                if (finish[p] > start || best is null) { start = finish[p]; best = p; }
            finish[id] = start + Nodes[id].EstimatedLatency;
            via[id] = best;
        }
        var end = TopologicalOrder.MaxBy(id => finish[id])!;
        var path = new List<string>();
        for (string? at = end; at is not null; at = via[at]) path.Add(at);
        path.Reverse();
        return (finish[end], path);
    }

    /// <summary>Every node reachable from <paramref name="id"/> (not including it). Used to skip work after a failure.</summary>
    public IReadOnlyCollection<string> Descendants(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(Children[id]);
        while (stack.TryPop(out var n))
            if (seen.Add(n)) foreach (var c in Children[n]) stack.Push(c);
        return seen;
    }

    private static Dictionary<string, TimeSpan> ComputeBottomLevel(
        Dictionary<string, PlanNode> nodes, Dictionary<string, IReadOnlyList<string>> children, List<string> order)
    {
        var bl = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var id = order[i];
            var tail = TimeSpan.Zero;
            foreach (var c in children[id]) if (bl[c] > tail) tail = bl[c];
            bl[id] = nodes[id].EstimatedLatency + tail;
        }
        return bl;
    }

    /// <summary>
    /// Walk parent links inside the stuck set. Every stuck node has at least one stuck parent
    /// (otherwise its indegree would have reached 0), so the walk must revisit a node: that's the cycle.
    /// Iterative, so a 100k-node plan can't overflow the stack.
    /// </summary>
    private static List<string> FindCycle(HashSet<string> stuck, Dictionary<string, IReadOnlyList<string>> parents)
    {
        var start = stuck.Min(StringComparer.Ordinal)!;
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        var walk = new List<string>();
        var at = start;
        while (!position.ContainsKey(at))
        {
            position[at] = walk.Count;
            walk.Add(at);
            at = parents[at].Where(stuck.Contains).Min(StringComparer.Ordinal)!;
        }
        // walk[position[at]..] is the cycle, followed backwards along parent links; reverse to read forwards.
        var cycle = walk.Skip(position[at]).Reverse().ToList();
        cycle.Add(cycle[0]);
        return cycle;
    }
}
