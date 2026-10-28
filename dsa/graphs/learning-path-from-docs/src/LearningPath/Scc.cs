namespace LearningPath;

/// <summary>
/// The strongly connected components of a graph: Of[v] is the component of node v, ids 0..Count-1.
/// u and v share a component exactly when u reaches v and v reaches u.
/// </summary>
public sealed class Components
{
    internal Components(int count, int[] of)
    {
        Count = count;
        Of = of;
    }

    public int Count { get; }
    public int[] Of { get; }
}

/// <summary>
/// Two ways to find strongly connected components, both O(V + E) and both iterative:
/// recursion would put one stack frame per node on the thread stack, and a 1,000,000-doc chain
/// would overflow it (the default is about 1 MB). Here the "call stack" is an int array on the heap.
/// </summary>
public static class Scc
{
    /// <summary>
    /// Tarjan's algorithm: one DFS. Components come out in reverse topological order
    /// (a component is finished only after every component it points to), so id 0 is a sink.
    /// </summary>
    public static Components Tarjan(Graph g)
    {
        ArgumentNullException.ThrowIfNull(g);
        int n = g.NodeCount;
        var start = g.Start;
        var targets = g.Targets;

        // index[v]: when DFS first reached v (0, 1, 2, ...), -1 = not yet visited.
        // low[v]: smallest index v can reach using its DFS subtree plus one edge to a node still on the
        //         component stack. low[v] == index[v] means nothing below v climbs above it: v is the
        //         root of a component.
        // onStack[v]: v is on the component stack, i.e. visited but its component isn't closed yet.
        var index = new int[n];
        var low = new int[n];
        var onStack = new bool[n];
        var comp = new int[n];
        Array.Fill(index, -1);

        var cursor = new int[n];      // next edge of v to look at; this is what a recursive version keeps in its frame
        var dfs = new int[n];         // explicit call stack: dfs[top - 1] is the node being "executed"
        var sccStack = new int[n];    // component stack: visited nodes whose component isn't known yet
        int dfsTop = 0, sccTop = 0, nextIndex = 0, count = 0;

        for (int root = 0; root < n; root++)
        {
            if (index[root] != -1) continue;

            // "call" root
            index[root] = low[root] = nextIndex++;
            cursor[root] = start[root];
            dfs[dfsTop++] = root;
            sccStack[sccTop++] = root;
            onStack[root] = true;

            while (dfsTop > 0)
            {
                int v = dfs[dfsTop - 1];
                if (cursor[v] < start[v + 1])
                {
                    int w = targets[cursor[v]++];
                    if (index[w] == -1)
                    {
                        // tree edge: "call" w. v resumes at its next edge when w returns.
                        index[w] = low[w] = nextIndex++;
                        cursor[w] = start[w];
                        dfs[dfsTop++] = w;
                        sccStack[sccTop++] = w;
                        onStack[w] = true;
                    }
                    else if (onStack[w])
                    {
                        // w is visited and its component is still open, so w is an ancestor of v or in the
                        // same open component: v can climb to w.
                        low[v] = Math.Min(low[v], index[w]);
                    }
                    // else: w's component is already closed. It can't be part of v's, so the edge is ignored.
                }
                else
                {
                    // all edges of v done: "return" from v
                    dfsTop--;
                    if (low[v] == index[v])
                    {
                        // v is a component root. Everything above v on the component stack was reached from v
                        // and couldn't climb above v, so v and those nodes are exactly one component.
                        int w;
                        do
                        {
                            w = sccStack[--sccTop];
                            onStack[w] = false;
                            comp[w] = count;
                        } while (w != v);
                        count++;
                    }
                    if (dfsTop > 0)
                    {
                        // what the recursive version does after the call returns: the parent inherits v's low
                        int parent = dfs[dfsTop - 1];
                        low[parent] = Math.Min(low[parent], low[v]);
                    }
                }
            }
        }
        return new Components(count, comp);
    }

    /// <summary>
    /// Kosaraju's algorithm: two DFS passes. Pass 1 on the graph records finish order. Pass 2 walks the
    /// reversed graph, starting from the node that finished last; each walk collects one component.
    /// Components come out in topological order, so id 0 is a source. Same partition as Tarjan,
    /// different numbering.
    /// </summary>
    public static Components Kosaraju(Graph g)
    {
        ArgumentNullException.ThrowIfNull(g);
        int n = g.NodeCount;
        var start = g.Start;
        var targets = g.Targets;

        // Pass 1: iterative DFS, push a node to `finished` when all its edges are done (post-order).
        var visited = new bool[n];
        var cursor = new int[n];
        var dfs = new int[n];
        var finished = new int[n];
        int done = 0;
        for (int root = 0; root < n; root++)
        {
            if (visited[root]) continue;
            visited[root] = true;
            cursor[root] = start[root];
            int top = 0;
            dfs[top++] = root;
            while (top > 0)
            {
                int v = dfs[top - 1];
                if (cursor[v] < start[v + 1])
                {
                    int w = targets[cursor[v]++];
                    if (!visited[w])
                    {
                        visited[w] = true;
                        cursor[w] = start[w];
                        dfs[top++] = w;
                    }
                }
                else
                {
                    top--;
                    finished[done++] = v;
                }
            }
        }

        // Pass 2: the last node to finish is in a source component. On the reversed graph it can only reach
        // its own component, so a plain flood fill (order doesn't matter here) collects exactly that component.
        // Then take the next unassigned node by decreasing finish time, and repeat.
        var rev = g.Reverse();
        var rstart = rev.Start;
        var rtargets = rev.Targets;
        var comp = new int[n];
        Array.Fill(comp, -1);
        var stack = dfs; // reuse: pass 1 is finished with it
        int count = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            int s = finished[i];
            if (comp[s] != -1) continue;
            comp[s] = count;
            int top = 0;
            stack[top++] = s;
            while (top > 0)
            {
                int v = stack[--top];
                for (int e = rstart[v]; e < rstart[v + 1]; e++)
                {
                    int w = rtargets[e];
                    if (comp[w] == -1)
                    {
                        comp[w] = count;
                        stack[top++] = w;
                    }
                }
            }
            count++;
        }
        return new Components(count, comp);
    }
}
