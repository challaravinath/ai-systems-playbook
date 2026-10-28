namespace LearningPath;

/// <summary>One doc in the pile, and the docs it says to read first.</summary>
public sealed record Doc(string Id, IReadOnlyList<string> DependsOn)
{
    public Doc(string id, params string[] dependsOn) : this(id, (IReadOnlyList<string>)dependsOn) { }
}

/// <summary>
/// One step of the learning path. Usually a single doc. When docs refer to each other (a cycle),
/// there is no "first" among them, so they form one group to be read together.
/// </summary>
public sealed record ReadingGroup(IReadOnlyList<string> Docs, bool IsCycle)
{
    public override string ToString() => IsCycle ? "{" + string.Join(", ", Docs) + "}" : string.Join(", ", Docs);
}

/// <summary>Which SCC algorithm <see cref="DocSet.Plan"/> uses. The path is the same either way.</summary>
public enum SccAlgorithm { Tarjan, Kosaraju }

/// <summary>A doc that names a prerequisite which isn't in the pile.</summary>
public sealed class UnknownDependencyException(IReadOnlyList<(string Doc, string Missing)> missing)
    : ArgumentException("unknown dependencies: " + string.Join("; ", missing.Select(m => $"'{m.Doc}' needs '{m.Missing}'")))
{
    public IReadOnlyList<(string Doc, string Missing)> Missing { get; } = missing;
}

/// <summary>
/// The pile of docs, checked and turned into a <see cref="Graph"/>.
///
/// Ids are compared ordinally (case-sensitive) and sorted that way, and node v is the v-th id in that
/// order. So "smallest node" and "smallest id" are the same thing, which is what makes tie-breaking stable.
///
/// Validation:
/// - empty or duplicate doc ids are rejected;
/// - a prerequisite that isn't a doc in the pile is rejected (all of them reported at once);
/// - a doc listing itself is a self-loop: kept, and its group is marked as a cycle of one;
/// - the same prerequisite listed twice counts once.
/// </summary>
public sealed class DocSet
{
    private readonly string[] _ids;
    private readonly bool[] _selfLoop;

    public DocSet(IEnumerable<Doc> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        var list = docs.ToList();

        var byId = new Dictionary<string, Doc>(StringComparer.Ordinal);
        foreach (var d in list)
        {
            ArgumentNullException.ThrowIfNull(d);
            if (string.IsNullOrEmpty(d.Id)) throw new ArgumentException("doc id must not be empty", nameof(docs));
            if (!byId.TryAdd(d.Id, d)) throw new ArgumentException($"duplicate doc id '{d.Id}'", nameof(docs));
        }

        _ids = [.. byId.Keys];
        Array.Sort(_ids, StringComparer.Ordinal);
        var node = new Dictionary<string, int>(_ids.Length, StringComparer.Ordinal);
        for (int i = 0; i < _ids.Length; i++) node[_ids[i]] = i;

        var missing = new List<(string, string)>();
        var edges = new List<(int From, int To)>();
        var seen = new HashSet<(int, int)>();
        _selfLoop = new bool[_ids.Length];
        for (int v = 0; v < _ids.Length; v++)
        {
            foreach (var dep in byId[_ids[v]].DependsOn ?? [])
            {
                if (dep is null || !node.TryGetValue(dep, out var u)) { missing.Add((_ids[v], dep ?? "<null>")); continue; }
                if (!seen.Add((u, v))) { DuplicateEdges++; continue; }
                if (u == v) _selfLoop[v] = true;
                edges.Add((u, v)); // prerequisite first: "read u before v"
            }
        }
        if (missing.Count > 0) throw new UnknownDependencyException(missing);

        Graph = Graph.FromEdges(_ids.Length, edges);
    }

    public Graph Graph { get; }

    /// <summary>Doc ids by node number (ordinal sort).</summary>
    public IReadOnlyList<string> Ids => _ids;

    /// <summary>Prerequisites listed more than once by the same doc, ignored after the first.</summary>
    public int DuplicateEdges { get; }

    /// <summary>
    /// The learning path: groups in reading order. Every prerequisite is in an earlier group or, if it is
    /// on a cycle with the doc, in the same group. Docs within a group are in ordinal order.
    /// </summary>
    public IReadOnlyList<ReadingGroup> Plan(SccAlgorithm algorithm = SccAlgorithm.Tarjan)
    {
        var components = algorithm == SccAlgorithm.Tarjan ? Scc.Tarjan(Graph) : Scc.Kosaraju(Graph);
        var cond = Condensation.Build(Graph, components);
        var path = new List<ReadingGroup>(cond.Count);
        foreach (var c in cond.TopologicalOrder())
        {
            var members = cond.Members(c);
            var docs = new string[members.Length];
            for (int i = 0; i < members.Length; i++) docs[i] = _ids[members[i]];
            path.Add(new ReadingGroup(docs, IsCycle: members.Length > 1 || _selfLoop[members[0]]));
        }
        return path;
    }

    public static IReadOnlyList<ReadingGroup> Plan(IEnumerable<Doc> docs, SccAlgorithm algorithm = SccAlgorithm.Tarjan) =>
        new DocSet(docs).Plan(algorithm);
}
