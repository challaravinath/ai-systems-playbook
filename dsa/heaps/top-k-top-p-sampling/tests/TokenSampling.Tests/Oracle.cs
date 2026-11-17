namespace TokenSampling.Tests;

/// <summary>
/// The slowest, plainest version of every rule, written straight from the definitions with LINQ.
/// The library is checked against this, never against itself.
/// </summary>
static class Oracle
{
    public static int[] RankAll(float[] logits) =>
        Enumerable.Range(0, logits.Length).OrderByDescending(i => logits[i]).ThenBy(i => i).ToArray();

    public static int[] TopK(float[] logits, int k) => RankAll(logits).Take(k).ToArray();

    /// <summary>Probabilities of the given tokens at temperature t, renormalised over just those tokens.</summary>
    public static double[] Probabilities(float[] logits, int[] ids, double t)
    {
        var live = ids.Where(i => !float.IsNegativeInfinity(logits[i])).ToArray();
        double max = live.Max(i => (double)logits[i]);
        var w = ids.Select(i => float.IsNegativeInfinity(logits[i]) ? 0 : Math.Exp((logits[i] - max) / t)).ToArray();
        double sum = w.Sum();
        return w.Select(x => x / sum).ToArray();
    }

    /// <summary>
    /// The full rule: temperature, top-k, top-p over the survivors, drop masked tokens.
    /// Also returns how close the top-p cut was to p, so a test can tell a real bug from a rounding tie.
    /// </summary>
    public static (int[] Ids, double BoundaryGap) Filter(float[] logits, SamplingParams s)
    {
        if (s.Temperature == 0) return ([RankAll(logits)[0]], double.PositiveInfinity);
        int[] ids = s.TopK > 0 ? TopK(logits, s.TopK) : RankAll(logits);
        double gap = double.PositiveInfinity;
        if (s.TopP < 1)
        {
            var p = Probabilities(logits, ids, s.Temperature);
            double cumulative = 0;
            int keep = ids.Length;
            for (int i = 0; i < ids.Length; i++)
            {
                cumulative += p[i];
                if (cumulative >= s.TopP) { keep = i + 1; gap = Math.Abs(cumulative - s.TopP); break; }
            }
            if (keep > 1) gap = Math.Min(gap, Math.Abs(cumulative - p[keep - 1] - s.TopP));
            ids = ids.Take(keep).ToArray();
        }
        // A token that can't be drawn isn't a candidate: masked (-inf), or a weight that rounds to exactly 0.
        double best = logits[ids[0]];
        return (ids.Where(i => !float.IsNegativeInfinity(logits[i]) && Math.Exp((logits[i] - best) / s.Temperature) > 0).ToArray(), gap);
    }

    /// <summary>A row with many ties (small whole numbers) and some masked tokens.</summary>
    public static float[] RowWithTies(Random rng, int vocabulary, double maskedShare = 0.1)
    {
        var row = new float[vocabulary];
        for (int i = 0; i < vocabulary; i++)
            row[i] = rng.NextDouble() < maskedShare ? float.NegativeInfinity : rng.Next(-6, 7);
        if (row.All(float.IsNegativeInfinity)) row[rng.Next(vocabulary)] = 0;
        return row;
    }
}
