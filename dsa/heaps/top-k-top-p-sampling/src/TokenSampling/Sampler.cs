namespace TokenSampling;

/// <summary>
/// What one request asks of the sampler. The defaults change nothing: temperature 1, no top-k, no top-p.
/// Each request carries its own settings, so one batch can mix them.
/// </summary>
/// <param name="Temperature">0 means greedy (always the best token). Below 1 sharpens the distribution, above 1 flattens it.</param>
/// <param name="TopK">Keep only the k best tokens. 0 turns it off, as in vLLM, where 0 or -1 means "consider all tokens".</param>
/// <param name="TopP">Keep the smallest set of best tokens whose probabilities add up to TopP or more. 1 turns it off.</param>
public sealed record SamplingParams(double Temperature = 1.0, int TopK = 0, double TopP = 1.0)
{
    /// <summary>Always pick the best token.</summary>
    public static SamplingParams Greedy { get; } = new(Temperature: 0);

    internal void Validate()
    {
        if (double.IsNaN(Temperature) || double.IsInfinity(Temperature) || Temperature < 0)
            throw new ArgumentOutOfRangeException(nameof(Temperature), Temperature, "Must be 0 or a finite positive number.");
        if (TopK < 0)
            throw new ArgumentOutOfRangeException(nameof(TopK), TopK, "Must be 0 (off) or positive.");
        if (double.IsNaN(TopP) || TopP <= 0 || TopP > 1)
            throw new ArgumentOutOfRangeException(nameof(TopP), TopP, "Must be in (0, 1]. 1 turns it off.");
    }
}

/// <summary>A token that survived filtering, with its probability after renormalising over the survivors.</summary>
public readonly record struct Candidate(int TokenId, double Probability);

/// <summary>
/// The sampling step of a serving engine, for one row of logits (one request, one next token).
/// Order, as in vLLM's sampler: temperature, then top-k, then top-p, then draw.
/// Top-p is computed over the top-k survivors, renormalised.
/// </summary>
public static class Sampler
{
    /// <summary>
    /// The tokens the draw can pick from, best first, with probabilities that add up to 1.
    /// Every candidate has a probability above 0; tokens that can't be drawn are left out.
    /// </summary>
    public static Candidate[] Filter(float[] logits, SamplingParams settings, TopKMethod method = TopKMethod.Heap)
    {
        ValidateLogits(logits);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (settings.Temperature == 0) return [new Candidate(Argmax(logits), 1.0)];

        double t = settings.Temperature;
        bool useTopK = settings.TopK > 0 && settings.TopK < logits.Length;
        bool useTopP = settings.TopP < 1.0;

        // Temperature divides every logit by the same positive number, so it never changes which tokens
        // rank highest. Top-k can run on the raw logits; temperature only matters for the probabilities.
        int[] kept;
        if (useTopK)
        {
            kept = TopK.Select(logits, settings.TopK, method);
            if (useTopP) kept = CutNucleus(logits, kept, t, settings.TopP, Total(logits, kept, t));
        }
        else if (useTopP)
        {
            // Top-p alone doesn't know how many tokens it needs, so quickselect can't help: it needs k up front.
            // Sort does everything; the heap path only pops as many tokens as the nucleus holds.
            kept = method == TopKMethod.Sort
                ? CutNucleus(logits, TopK.SortAll(logits), t, settings.TopP, TotalOfAll(logits, t))
                : NucleusByHeap(logits, t, settings.TopP);
        }
        else
        {
            kept = TopK.SortAll(logits);
        }
        return ToCandidates(logits, kept, t);
    }

    /// <summary>Draws one token id. Same logits, settings and generator state give the same token.</summary>
    public static int Sample(float[] logits, SamplingParams settings, Random rng, TopKMethod method = TopKMethod.Heap)
    {
        ArgumentNullException.ThrowIfNull(rng);
        var candidates = Filter(logits, settings, method);
        double u = rng.NextDouble(), cumulative = 0;
        foreach (var c in candidates)
        {
            cumulative += c.Probability;
            if (u < cumulative) return c.TokenId;
        }
        return candidates[^1].TokenId; // rounding left the sum a hair under 1
    }

    /// <summary>One token per row. Every row has its own settings, as requests in one batch do.</summary>
    public static int[] SampleBatch(IReadOnlyList<float[]> rows, IReadOnlyList<SamplingParams> settings, Random rng,
        TopKMethod method = TopKMethod.Heap)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(settings);
        if (rows.Count != settings.Count)
            throw new ArgumentException($"{rows.Count} rows but {settings.Count} settings; every row needs its own.", nameof(settings));
        var tokens = new int[rows.Count];
        for (int i = 0; i < rows.Count; i++) tokens[i] = Sample(rows[i], settings[i], rng, method);
        return tokens;
    }

    /// <summary>Softmax that can't overflow: subtract the largest logit first, so the biggest exponent is e^0 = 1.</summary>
    public static double[] Softmax(float[] logits, double temperature = 1.0)
    {
        ValidateLogits(logits);
        if (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature <= 0)
            throw new ArgumentOutOfRangeException(nameof(temperature), temperature, "Must be a finite positive number.");
        float max = MaxLogit(logits);
        var p = new double[logits.Length];
        double sum = 0;
        for (int i = 0; i < p.Length; i++) sum += p[i] = Weight(logits[i], max, temperature);
        for (int i = 0; i < p.Length; i++) p[i] /= sum;
        return p;
    }

    // exp((logit - max) / t). A masked token (-inf) weighs 0.
    static double Weight(float logit, float max, double t) =>
        float.IsNegativeInfinity(logit) ? 0.0 : Math.Exp((logit - (double)max) / t);

    static double Total(float[] logits, int[] ids, double t)
    {
        float max = logits[ids[0]];
        double total = 0;
        foreach (int id in ids) total += Weight(logits[id], max, t);
        return total;
    }

    static double TotalOfAll(float[] logits, double t)
    {
        float max = MaxLogit(logits);
        double total = 0;
        for (int i = 0; i < logits.Length; i++) total += Weight(logits[i], max, t);
        return total;
    }

    // Keep the shortest best-first prefix whose weight reaches p of the total. Always keeps at least one token.
    static int[] CutNucleus(float[] logits, int[] bestFirst, double t, double p, double total)
    {
        float max = logits[bestFirst[0]];
        double target = p * total, cumulative = 0;
        for (int i = 0; i < bestFirst.Length; i++)
        {
            cumulative += Weight(logits[bestFirst[i]], max, t);
            if (cumulative >= target) return bestFirst[..(i + 1)];
        }
        return bestFirst; // rounding: p just under 1 can need every token
    }

    // Build a max-heap over all tokens in O(V), then pop best first until the nucleus is full: O(V + m log V)
    // for a nucleus of m tokens. Pops come out in the same order as a full sort, so the sums match it exactly.
    static int[] NucleusByHeap(float[] logits, double t, double p)
    {
        int n = logits.Length;
        var heap = new int[n];
        for (int i = 0; i < n; i++) heap[i] = i;
        for (int i = n / 2 - 1; i >= 0; i--) SiftDownBest(heap, i, n, logits);

        float max = logits[heap[0]];
        double target = p * TotalOfAll(logits, t), cumulative = 0;
        var kept = new List<int>();
        int count = n;
        while (count > 0)
        {
            int best = heap[0];
            kept.Add(best);
            cumulative += Weight(logits[best], max, t);
            if (cumulative >= target) break;
            heap[0] = heap[--count];
            SiftDownBest(heap, 0, count, logits);
        }
        return [.. kept];
    }

    // In this heap a parent is never worse than its children.
    static void SiftDownBest(int[] heap, int i, int count, float[] logits)
    {
        while (true)
        {
            int left = 2 * i + 1, right = left + 1, best = i;
            if (left < count && TopK.Beats(logits, heap[left], heap[best])) best = left;
            if (right < count && TopK.Beats(logits, heap[right], heap[best])) best = right;
            if (best == i) return;
            (heap[best], heap[i]) = (heap[i], heap[best]);
            i = best;
        }
    }

    // Drops tokens that can never be drawn: masked ones (-inf) and ones whose weight underflows to exactly 0,
    // such as logit 0 next to logit 10 at temperature 0.01. The best token always weighs e^0 = 1, so one survives.
    static Candidate[] ToCandidates(float[] logits, int[] bestFirst, double t)
    {
        float max = logits[bestFirst[0]];
        var live = bestFirst.Where(id => Weight(logits[id], max, t) > 0).ToArray();
        var w = live.Select(id => Weight(logits[id], max, t)).ToArray();
        double sum = w.Sum();
        var result = new Candidate[live.Length];
        for (int i = 0; i < live.Length; i++) result[i] = new Candidate(live[i], w[i] / sum);
        return result;
    }

    static int Argmax(float[] logits)
    {
        int best = 0;
        for (int i = 1; i < logits.Length; i++) if (TopK.Beats(logits, i, best)) best = i;
        return best;
    }

    static float MaxLogit(float[] logits) => logits[Argmax(logits)];

    internal static void ValidateLogits(float[] logits)
    {
        ArgumentNullException.ThrowIfNull(logits);
        if (logits.Length == 0) throw new ArgumentException("No logits.", nameof(logits));
        bool anyFinite = false;
        for (int i = 0; i < logits.Length; i++)
        {
            float x = logits[i];
            if (float.IsNaN(x) || float.IsPositiveInfinity(x))
                throw new ArgumentException($"Logit {i} is {x}. Only finite numbers and -infinity (a masked token) are allowed.", nameof(logits));
            if (!float.IsNegativeInfinity(x)) anyFinite = true;
        }
        if (!anyFinite) throw new ArgumentException("Every token is masked (-infinity); there is nothing to sample.", nameof(logits));
    }
}
