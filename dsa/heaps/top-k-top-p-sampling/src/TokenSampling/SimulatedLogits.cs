namespace TokenSampling;

/// <summary>
/// Made-up logits for tests and experiments. SIMULATED, not from a model.
/// Our assumption: a next-token distribution falls off with rank like a power law, p(rank r) proportional to
/// r^-s. A larger s gives a peaked distribution (the model is sure); a smaller s gives a flat one (it isn't).
/// Token ids are shuffled so the best token isn't always id 0.
/// </summary>
public static class SimulatedLogits
{
    /// <summary>Logit for rank r is -s * ln(r), plus Gaussian noise with the given standard deviation.</summary>
    public static float[] PowerLaw(int vocabulary, double s, Random rng, double noise = 0.0)
    {
        if (vocabulary < 1) throw new ArgumentOutOfRangeException(nameof(vocabulary), vocabulary, "Must be at least 1.");
        if (!double.IsFinite(s) || s < 0) throw new ArgumentOutOfRangeException(nameof(s), s, "Must be finite and 0 or more.");
        if (!double.IsFinite(noise) || noise < 0) throw new ArgumentOutOfRangeException(nameof(noise), noise, "Must be finite and 0 or more.");
        ArgumentNullException.ThrowIfNull(rng);

        var ids = new int[vocabulary];
        for (int i = 0; i < vocabulary; i++) ids[i] = i;
        rng.Shuffle(ids);

        var logits = new float[vocabulary];
        for (int rank = 1; rank <= vocabulary; rank++)
        {
            double z = noise == 0 ? 0 : noise * Gaussian(rng);
            logits[ids[rank - 1]] = (float)(-s * Math.Log(rank) + z);
        }
        return logits;
    }

    static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
