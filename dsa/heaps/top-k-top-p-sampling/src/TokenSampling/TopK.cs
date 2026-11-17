namespace TokenSampling;

/// <summary>How <see cref="TopK.Select"/> finds the k best tokens.</summary>
public enum TopKMethod
{
    /// <summary>Sort every token, keep the first k. O(V log V). The test oracle.</summary>
    Sort,
    /// <summary>A min-heap of size k whose root is the worst token kept so far. O(V log k).</summary>
    Heap,
    /// <summary>Partition around the k-th best until it lands at position k - 1, then sort those k. O(V + k log k) on average.</summary>
    Quickselect,
}

/// <summary>
/// Picks the k best tokens from one row of logits.
/// "Better" means a higher logit; on a tie, the lower token id. That is a total order, so every
/// method returns exactly the same tokens in the same order, and a test can compare them one to one.
/// This tie rule is ours. Hugging Face's TopKLogitsWarper removes only tokens scoring below the k-th score,
/// so it keeps every token tied with the k-th and can keep more than k. Here it is always exactly k.
/// </summary>
public static class TopK
{
    /// <summary>The k best token ids, best first. k larger than the vocabulary returns every token.</summary>
    public static int[] Select(float[] logits, int k, TopKMethod method)
    {
        Sampler.ValidateLogits(logits); // NaN would break the total order, and the three methods would disagree
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), k, "Must be at least 1.");
        k = Math.Min(k, logits.Length);
        return method switch
        {
            TopKMethod.Sort => BySort(logits, k),
            TopKMethod.Heap => ByHeap(logits, k),
            TopKMethod.Quickselect => ByQuickselect(logits, k),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
        };
    }

    /// <summary>True when token a ranks above token b.</summary>
    internal static bool Beats(float[] logits, int a, int b) =>
        logits[a] > logits[b] || (logits[a] == logits[b] && a < b);

    /// <summary>Every token id, best first.</summary>
    internal static int[] SortAll(float[] logits)
    {
        var ids = new int[logits.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = i;
        Array.Sort(ids, new BestFirst(logits));
        return ids;
    }

    static int[] BySort(float[] logits, int k) => SortAll(logits)[..k];

    static int[] ByHeap(float[] logits, int k)
    {
        // Min-heap by rank: the root is the worst of the k kept so far, so a new token only has to beat the root.
        var heap = new int[k];
        int count = 0;
        for (int id = 0; id < logits.Length; id++)
        {
            if (count < k)
            {
                heap[count] = id;
                SiftUpWorst(heap, count++, logits);
            }
            else if (Beats(logits, id, heap[0]))
            {
                heap[0] = id;
                SiftDownWorst(heap, 0, k, logits);
            }
        }
        Array.Sort(heap, new BestFirst(logits));
        return heap;
    }

    // In this heap a parent is never better than its children.
    static void SiftUpWorst(int[] heap, int i, float[] logits)
    {
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (!Beats(logits, heap[parent], heap[i])) break;
            (heap[parent], heap[i]) = (heap[i], heap[parent]);
            i = parent;
        }
    }

    static void SiftDownWorst(int[] heap, int i, int count, float[] logits)
    {
        while (true)
        {
            int left = 2 * i + 1, right = left + 1, worst = i;
            if (left < count && Beats(logits, heap[worst], heap[left])) worst = left;
            if (right < count && Beats(logits, heap[worst], heap[right])) worst = right;
            if (worst == i) return;
            (heap[worst], heap[i]) = (heap[i], heap[worst]);
            i = worst;
        }
    }

    static int[] ByQuickselect(float[] logits, int k)
    {
        var ids = new int[logits.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = i;

        // A fixed-seed generator for pivots: the result never depends on the pivots (the order is total),
        // only the running time does, and a fixed seed keeps that repeatable.
        ulong state = 0x9E3779B97F4A7C15UL;
        int lo = 0, hi = ids.Length - 1, target = k - 1;
        while (lo < hi)
        {
            int pivot = lo + (int)(NextRandom(ref state) % (ulong)(hi - lo + 1));
            int p = Partition(ids, lo, hi, pivot, logits);
            if (p == target) break;
            if (p < target) lo = p + 1; else hi = p - 1;
        }
        // Now positions 0..k-1 hold the k best, in no particular order.
        var top = ids[..k];
        Array.Sort(top, new BestFirst(logits));
        return top;
    }

    // Lomuto partition: tokens that beat the pivot go left, the pivot lands at the returned position.
    static int Partition(int[] ids, int lo, int hi, int pivotIndex, float[] logits)
    {
        int pivot = ids[pivotIndex];
        (ids[pivotIndex], ids[hi]) = (ids[hi], ids[pivotIndex]);
        int store = lo;
        for (int i = lo; i < hi; i++)
        {
            if (Beats(logits, ids[i], pivot))
            {
                (ids[i], ids[store]) = (ids[store], ids[i]);
                store++;
            }
        }
        (ids[store], ids[hi]) = (ids[hi], ids[store]);
        return store;
    }

    static ulong NextRandom(ref ulong s)
    {
        s ^= s << 13; s ^= s >> 7; s ^= s << 17;
        return s;
    }

    internal sealed class BestFirst(float[] logits) : IComparer<int>
    {
        public int Compare(int a, int b) => a == b ? 0 : Beats(logits, a, b) ? -1 : 1;
    }
}
