using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PrefixCache.Tests")]

namespace PrefixCache;

/// <summary>
/// Remembers which prompt prefixes already have their KV cache computed, so a new request only pays
/// prefill for the tokens after the longest prefix it shares with earlier requests.
///
/// Design: a radix tree over token ids (a trie whose single-child chains are merged into one edge).
/// Capacity is counted in tokens. When it's full, the least recently used <i>leaf</i> that no running
/// request is using is evicted; a parent whose last child goes becomes a leaf and may follow.
/// This is the shape of SGLang's RadixAttention.
///
/// Not thread-safe on purpose: one scheduler loop owns it, the same "single owner" design as the
/// agent plan executor's main loop.
/// </summary>
public sealed class RadixPrefixCache
{
    private readonly Node _root = new([], parent: null);
    private long _clock; // logical time for LRU: deterministic, unlike a wall clock

    public RadixPrefixCache(int capacityTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityTokens);
        Capacity = capacityTokens;
    }

    /// <summary>Most tokens the cache may hold (a stand-in for GPU memory reserved for KV cache).</summary>
    public int Capacity { get; }

    /// <summary>Tokens currently cached. Shared prefixes are stored once.</summary>
    public int CachedTokens { get; private set; }

    /// <summary>Prompt tokens seen by <see cref="Acquire"/>, and how many of them were already cached.</summary>
    public long RequestedTokens { get; private set; }
    public long ReusedTokens { get; private set; }

    /// <summary>Share of prompt tokens whose prefill was skipped.</summary>
    public double ReuseRatio => RequestedTokens == 0 ? 0 : (double)ReusedTokens / RequestedTokens;

    /// <summary>
    /// Step 1 of a request: find the longest cached prefix of <paramref name="tokens"/> and pin it,
    /// so it can't be evicted while this request runs. Dispose the lease when the request finishes.
    /// </summary>
    public PrefixLease Acquire(IReadOnlyList<int> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var copy = tokens.ToArray(); // the caller may reuse its buffer
        var (node, matched) = WalkAndPin(_root, copy, 0);
        RequestedTokens += copy.Length;
        ReusedTokens += matched;
        return new PrefixLease(this, copy, node, matched);
    }

    /// <summary>
    /// Step 2, after computing KV for the uncached tail: cache the rest of the lease's prompt so later
    /// requests can reuse it. Evicts unpinned LRU leaves to make room. If even that isn't enough, caches
    /// as much of the tail as fits (a shorter prefix is still useful). Returns the tokens added.
    /// </summary>
    public int Insert(PrefixLease lease)
    {
        CheckUsable(lease);
        var tokens = lease.Tokens;

        // Another request may have cached more of this prompt since Acquire: walk on from where we stopped.
        var (node, i) = WalkAndPin(lease.Last, tokens, lease.CachedLength);

        var need = tokens.Length - i;
        var room = Capacity - CachedTokens;
        if (need > room) room += Evict(need - room);
        var take = Math.Min(need, room);

        if (take > 0)
        {
            var leaf = new Node(tokens[i..(i + take)], node) { RefCount = 1, LastAccess = ++_clock };
            node.Children[leaf.Key[0]] = leaf;
            CachedTokens += take;
            node = leaf;
            i += take;
        }

        lease.Last = node;
        lease.CachedLength = i;
        return take;
    }

    /// <summary>Step 3: the request finished. Unpin its path so it becomes evictable (but stays cached).</summary>
    internal void Release(PrefixLease lease)
    {
        for (var n = lease.Last; n != _root; n = n.Parent!) n.RefCount--;
    }

    // Follows tokens[i..] down from `node`. Pins and touches every node it enters.
    // A match that ends inside an edge splits that edge, so the matched part is a whole node that can be pinned.
    private (Node Node, int Matched) WalkAndPin(Node node, int[] tokens, int i)
    {
        var now = ++_clock;
        while (i < tokens.Length && node.Children.TryGetValue(tokens[i], out var child))
        {
            var common = CommonLength(child.Key, tokens, i);
            if (common < child.Key.Length) child = Split(child, common);
            node = child;
            i += common;
            node.LastAccess = now;
            node.RefCount++;
        }
        return (node, i);
    }

    // Cuts the edge into `child` after `at` tokens: a new node `top` takes the first `at` tokens,
    // and `child` keeps the rest, now hanging under `top`.
    private static Node Split(Node child, int at)
    {
        var parent = child.Parent!;
        var top = new Node(child.Key[..at], parent) { RefCount = child.RefCount, LastAccess = child.LastAccess };
        parent.Children[top.Key[0]] = top;
        child.Key = child.Key[at..];
        child.Parent = top;
        top.Children[child.Key[0]] = child;
        return top; // no tokens added or removed: CachedTokens is unchanged
    }

    // Frees at least `needed` tokens if it can, oldest unpinned leaves first. Returns tokens freed.
    private int Evict(int needed)
    {
        var heap = new PriorityQueue<Node, long>();
        foreach (var leaf in Leaves())
            if (leaf.RefCount == 0) heap.Enqueue(leaf, leaf.LastAccess);

        var freed = 0;
        while (freed < needed && heap.TryDequeue(out var leaf, out _))
        {
            var parent = leaf.Parent!;
            parent.Children.Remove(leaf.Key[0]);
            freed += leaf.Key.Length;
            CachedTokens -= leaf.Key.Length;
            if (parent != _root && parent.Children.Count == 0 && parent.RefCount == 0)
                heap.Enqueue(parent, parent.LastAccess); // it just became a leaf
        }
        return freed;
    }

    private IEnumerable<Node> Leaves()
    {
        var stack = new Stack<Node>(_root.Children.Values);
        while (stack.TryPop(out var n))
        {
            if (n.Children.Count == 0) yield return n;
            else foreach (var c in n.Children.Values) stack.Push(c);
        }
    }

    private static int CommonLength(int[] key, int[] tokens, int start)
    {
        var n = 0;
        while (n < key.Length && start + n < tokens.Length && key[n] == tokens[start + n]) n++;
        return n;
    }

    private void CheckUsable(PrefixLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Owner != this) throw new ArgumentException("The lease belongs to a different cache.", nameof(lease));
        ObjectDisposedException.ThrowIf(lease.Released, lease);
    }

    /// <summary>For tests: checks the tree's invariants and returns what it found.</summary>
    internal (int Tokens, int Nodes, int PinnedNodes) Audit()
    {
        int tokens = 0, nodes = 0, pinned = 0;
        var stack = new Stack<Node>(_root.Children.Values);
        while (stack.TryPop(out var n))
        {
            if (n.Key.Length == 0) throw new InvalidOperationException("Empty edge.");
            if (n.RefCount < 0) throw new InvalidOperationException("Negative ref count.");
            if (n.Parent!.Children[n.Key[0]] != n) throw new InvalidOperationException("Child indexed under the wrong token.");
            if (n.Parent != _root && n.RefCount > n.Parent.RefCount) throw new InvalidOperationException("Child pinned more than its parent.");
            tokens += n.Key.Length;
            nodes++;
            if (n.RefCount > 0) pinned++;
            foreach (var c in n.Children.Values) stack.Push(c);
        }
        if (tokens != CachedTokens) throw new InvalidOperationException($"CachedTokens is {CachedTokens}, tree holds {tokens}.");
        if (tokens > Capacity) throw new InvalidOperationException("Over capacity.");
        return (tokens, nodes, pinned);
    }
}

/// <summary>
/// A request's hold on its cached prefix. While it's alive, the prefix can't be evicted.
/// Dispose it when the request finishes; disposing twice is harmless.
/// </summary>
public sealed class PrefixLease : IDisposable
{
    internal PrefixLease(RadixPrefixCache owner, int[] tokens, Node last, int reused)
    {
        Owner = owner;
        Tokens = tokens;
        Last = last;
        ReusedLength = reused;
        CachedLength = reused;
    }

    internal RadixPrefixCache Owner { get; }
    internal int[] Tokens { get; }
    internal Node Last { get; set; }
    internal bool Released { get; private set; }

    /// <summary>Prompt length in tokens.</summary>
    public int Length => Tokens.Length;

    /// <summary>Tokens already cached at <see cref="RadixPrefixCache.Acquire"/>: prefill you skip.</summary>
    public int ReusedLength { get; }

    /// <summary>Leading tokens of this prompt that are cached and pinned now (grows after Insert).</summary>
    public int CachedLength { get; internal set; }

    public void Dispose()
    {
        if (Released) return;
        Released = true;
        Owner.Release(this);
    }
}

internal sealed class Node(int[] key, Node? parent)
{
    public int[] Key = key;                 // the edge label: tokens from the parent to here
    public Node? Parent = parent;
    public readonly Dictionary<int, Node> Children = []; // keyed by each child's first token
    public int RefCount;                    // running requests using this node (pinned while > 0)
    public long LastAccess;                 // logical time of the last request that touched it
}
