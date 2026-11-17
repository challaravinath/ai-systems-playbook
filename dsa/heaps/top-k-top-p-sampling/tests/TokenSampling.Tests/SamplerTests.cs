namespace TokenSampling.Tests;

public class SamplerTests
{
    public static TheoryData<TopKMethod> Methods => new() { TopKMethod.Sort, TopKMethod.Heap, TopKMethod.Quickselect };

    static float[] FromProbabilities(params double[] p) => p.Select(x => (float)Math.Log(x)).ToArray();

    static int[] Ids(Candidate[] c) => c.Select(x => x.TokenId).ToArray();

    [Fact]
    public void Greedy_picks_the_best_token_and_the_lower_id_on_a_tie()
    {
        Assert.Equal(2, Sampler.Filter([1, 3, 4, 4], SamplingParams.Greedy).Single().TokenId);
        Assert.Equal(1.0, Sampler.Filter([1, 3, 4, 4], SamplingParams.Greedy).Single().Probability);
    }

    [Theory, MemberData(nameof(Methods))]
    public void Top_k_of_one_is_the_same_as_greedy(TopKMethod method)
    {
        var rng = new Random(2);
        for (int trial = 0; trial < 300; trial++)
        {
            var row = Oracle.RowWithTies(rng, rng.Next(1, 200));
            Assert.Equal(Ids(Sampler.Filter(row, SamplingParams.Greedy)),
                Ids(Sampler.Filter(row, new SamplingParams(TopK: 1), method)));
        }
    }

    [Fact]
    public void Default_settings_keep_every_live_token_with_softmax_probabilities()
    {
        float[] row = [0.1f, 2.5f, float.NegativeInfinity, -1f];
        var c = Sampler.Filter(row, new SamplingParams());
        var softmax = Sampler.Softmax(row);
        Assert.Equal([1, 0, 3], Ids(c));
        foreach (var x in c) Assert.Equal(softmax[x.TokenId], x.Probability, 12);
        Assert.Equal(0.0, softmax[2]);
    }

    [Fact]
    public void Softmax_does_not_overflow_on_large_logits()
    {
        // e^1000 is infinity in a double. Subtracting the max first makes it e^0 and e^-1.
        var big = Sampler.Softmax([1000f, 1001f]);
        var small = Sampler.Softmax([0f, 1f]);
        Assert.All(big, x => Assert.True(double.IsFinite(x)));
        Assert.Equal(small[0], big[0], 12);
        Assert.Equal(small[1], big[1], 12);
    }

    [Fact]
    public void Lower_temperature_sharpens_and_higher_flattens()
    {
        float[] row = [2f, 1f, 0f];
        double Top(double t) => Sampler.Filter(row, new SamplingParams(Temperature: t))[0].Probability;
        Assert.True(Top(0.5) > Top(1.0));
        Assert.True(Top(1.0) > Top(2.0));
    }

    [Theory, MemberData(nameof(Methods))]
    public void Temperature_never_changes_which_tokens_top_k_keeps(TopKMethod method)
    {
        var row = SimulatedLogits.PowerLaw(5_000, 1.0, new Random(3), noise: 1.0);
        var atOne = Ids(Sampler.Filter(row, new SamplingParams(TopK: 40), method));
        foreach (double t in new[] { 0.2, 0.7, 1.5, 4.0 })
            Assert.Equal(atOne, Ids(Sampler.Filter(row, new SamplingParams(Temperature: t, TopK: 40), method)));
    }

    [Theory]
    [InlineData(0.45, 1)]
    [InlineData(0.75, 2)]
    [InlineData(0.90, 3)]
    [InlineData(0.96, 4)]
    public void Top_p_keeps_the_smallest_set_that_reaches_p(double p, int expected)
    {
        // Probabilities 0.5, 0.3, 0.15, 0.05.
        var row = FromProbabilities(0.5, 0.3, 0.15, 0.05);
        foreach (var method in new[] { TopKMethod.Sort, TopKMethod.Heap, TopKMethod.Quickselect })
            Assert.Equal(expected, Sampler.Filter(row, new SamplingParams(TopP: p), method).Length);
    }

    [Theory, MemberData(nameof(Methods))]
    public void Reaching_p_exactly_is_enough(TopKMethod method)
    {
        // Two equal tokens: 0.5 each, exactly, in binary. "Add up to top_p or higher": 0.5 reaches 0.5, so one token.
        Assert.Single(Sampler.Filter([0f, 0f], new SamplingParams(TopP: 0.5), method));
        Assert.Single(Sampler.Filter([0f, 0f, -50f], new SamplingParams(TopK: 2, TopP: 0.5), method));
    }

    [Fact]
    public void Top_p_runs_on_the_top_k_survivors_renormalised()
    {
        // Probabilities 0.4, 0.3, 0.2, 0.1. Top-k 2 leaves 0.4 and 0.3, renormalised to 0.571 and 0.429.
        // With p = 0.55 the first token alone is enough (0.571 >= 0.55).
        // Top-p on the full distribution would need two (0.4 < 0.55, 0.7 >= 0.55): the order matters.
        var row = FromProbabilities(0.4, 0.3, 0.2, 0.1);
        var c = Sampler.Filter(row, new SamplingParams(TopK: 2, TopP: 0.55));
        Assert.Equal([0], Ids(c));
        Assert.Equal(2, Sampler.Filter(row, new SamplingParams(TopP: 0.55)).Length);
    }

    [Theory, MemberData(nameof(Methods))]
    public void Every_combination_matches_the_oracle_on_random_rows(TopKMethod method)
    {
        var rng = new Random(4);
        int checkedRows = 0;
        for (int trial = 0; trial < 3000; trial++)
        {
            var row = rng.Next(2) == 0
                ? Oracle.RowWithTies(rng, rng.Next(1, 400))
                : SimulatedLogits.PowerLaw(rng.Next(1, 400), rng.NextDouble() * 2, rng, noise: rng.NextDouble());
            var s = new SamplingParams(
                Temperature: rng.Next(10) == 0 ? 0 : 0.1 + rng.NextDouble() * 2,
                TopK: rng.Next(3) == 0 ? 0 : rng.Next(1, row.Length + 3),
                TopP: rng.Next(3) == 0 ? 1.0 : 0.05 + rng.NextDouble() * 0.94);
            var (expected, gap) = Oracle.Filter(row, s);
            if (gap < 1e-9) continue; // p sits on a cumulative sum; the last token is a rounding coin toss
            Assert.Equal(expected, Ids(Sampler.Filter(row, s, method)));
            checkedRows++;
        }
        Assert.True(checkedRows > 2900, $"only {checkedRows} rows checked");
    }

    [Fact]
    public void Probabilities_add_up_to_one_and_come_best_first()
    {
        var rng = new Random(5);
        for (int trial = 0; trial < 500; trial++)
        {
            var row = Oracle.RowWithTies(rng, rng.Next(1, 300));
            var c = Sampler.Filter(row, new SamplingParams(Temperature: 0.3 + rng.NextDouble(), TopK: rng.Next(0, 50), TopP: 0.5 + rng.NextDouble() / 2));
            Assert.Equal(1.0, c.Sum(x => x.Probability), 9);
            for (int i = 1; i < c.Length; i++) Assert.True(c[i - 1].Probability >= c[i].Probability);
        }
    }

    [Theory, MemberData(nameof(Methods))]
    public void Masked_tokens_are_never_candidates(TopKMethod method)
    {
        float[] row = [float.NegativeInfinity, 1f, float.NegativeInfinity, 0f];
        foreach (var s in new[] { new SamplingParams(), new SamplingParams(TopK: 3), new SamplingParams(TopK: 10, TopP: 0.999) })
            Assert.Equal([1, 3], Ids(Sampler.Filter(row, s, method)));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_bad_logit_is_rejected(float bad)
    {
        Assert.Throws<ArgumentException>(() => Sampler.Filter([1f, bad, 0f], new SamplingParams()));
    }

    [Fact]
    public void Empty_null_or_fully_masked_rows_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Sampler.Filter([], new SamplingParams()));
        Assert.Throws<ArgumentNullException>(() => Sampler.Filter(null!, new SamplingParams()));
        Assert.Throws<ArgumentException>(() => Sampler.Filter([float.NegativeInfinity, float.NegativeInfinity], new SamplingParams()));
    }

    public static TheoryData<SamplingParams> BadSettings => new()
    {
        new SamplingParams(Temperature: -1), new SamplingParams(Temperature: double.NaN), new SamplingParams(Temperature: double.PositiveInfinity),
        new SamplingParams(TopK: -1),
        new SamplingParams(TopP: 0), new SamplingParams(TopP: -0.1), new SamplingParams(TopP: 1.1), new SamplingParams(TopP: double.NaN),
    };

    [Theory, MemberData(nameof(BadSettings))]
    public void Bad_settings_are_rejected(SamplingParams s)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sampler.Filter([1f, 2f], s));
    }

    [Fact]
    public void Top_k_zero_and_top_p_one_change_nothing()
    {
        var row = SimulatedLogits.PowerLaw(1_000, 1.0, new Random(6));
        Assert.Equal(1_000, Sampler.Filter(row, new SamplingParams(TopK: 0, TopP: 1.0)).Length);
        Assert.Equal(1_000, Sampler.Filter(row, new SamplingParams(TopK: 1_000)).Length);
    }

    [Fact]
    public void The_same_seed_draws_the_same_tokens()
    {
        var row = SimulatedLogits.PowerLaw(1_000, 0.9, new Random(8));
        var s = new SamplingParams(Temperature: 0.8, TopK: 50, TopP: 0.9);
        var a = new Random(42); var b = new Random(42);
        for (int i = 0; i < 200; i++) Assert.Equal(Sampler.Sample(row, s, a), Sampler.Sample(row, s, b));
    }

    [Fact]
    public void Draws_follow_the_probabilities_and_never_pick_a_filtered_token()
    {
        // Probabilities 0.4, 0.3, 0.2, 0.1; top-k 3 leaves 0.444, 0.333, 0.222.
        var row = FromProbabilities(0.4, 0.3, 0.2, 0.1);
        var s = new SamplingParams(TopK: 3);
        var rng = new Random(9);
        var counts = new int[4];
        const int draws = 1_000_000;
        for (int i = 0; i < draws; i++) counts[Sampler.Sample(row, s, rng)]++;
        Assert.Equal(0, counts[3]);
        // One standard error is about 0.0005 here, so 0.002 is four of them.
        Assert.InRange(counts[0] / (double)draws, 4.0 / 9 - 0.002, 4.0 / 9 + 0.002);
        Assert.InRange(counts[1] / (double)draws, 3.0 / 9 - 0.002, 3.0 / 9 + 0.002);
        Assert.InRange(counts[2] / (double)draws, 2.0 / 9 - 0.002, 2.0 / 9 + 0.002);
    }

    [Theory]
    [InlineData(new float[] { 10f, 0f }, 0.01)]   // e^(-10 / 0.01) = e^-1000 is 0 in a double
    [InlineData(new float[] { 0f, -800f }, 1.0)]  // e^-800 is 0 too
    public void A_token_whose_probability_rounds_to_zero_is_never_a_candidate(float[] row, double t)
    {
        var c = Sampler.Filter(row, new SamplingParams(Temperature: t));
        Assert.Equal([0], Ids(c));
        var rng = new Random(12);
        for (int i = 0; i < 1_000; i++) Assert.Equal(0, Sampler.Sample(row, new SamplingParams(Temperature: t), rng));
    }

    [Fact]
    public void Extreme_temperatures_still_give_valid_probabilities()
    {
        var row = SimulatedLogits.PowerLaw(1_000, 1.0, new Random(13), noise: 1.0);
        foreach (double t in new[] { 1e-6, 1e6 })
        {
            var c = Sampler.Filter(row, new SamplingParams(Temperature: t));
            Assert.Equal(1.0, c.Sum(x => x.Probability), 9);
            Assert.All(c, x => Assert.True(x.Probability > 0));
        }
        Assert.Single(Sampler.Filter(row, new SamplingParams(Temperature: 1e-6))); // as good as greedy
        Assert.Equal(1_000, Sampler.Filter(row, new SamplingParams(Temperature: 1e6)).Length); // nearly uniform
    }

    [Fact]
    public void Simulated_rows_reject_settings_that_would_make_bad_logits()
    {
        var rng = new Random(14);
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatedLogits.PowerLaw(10, double.PositiveInfinity, rng));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatedLogits.PowerLaw(10, 1.0, rng, noise: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatedLogits.PowerLaw(10, 1.0, rng, noise: -1));
    }

    [Fact]
    public void Each_row_in_a_batch_uses_its_own_settings()
    {
        // The hiring assistant: row 0 pulls a fact from a CV (greedy), row 1 writes the panel brief (top-p 0.9).
        var extract = FromProbabilities(0.6, 0.25, 0.1, 0.05);
        var brief = FromProbabilities(0.3, 0.3, 0.2, 0.2);
        var settings = new[] { SamplingParams.Greedy, new SamplingParams(TopP: 0.9) };
        var rng = new Random(10);
        var seen = new HashSet<int>();
        for (int i = 0; i < 2_000; i++)
        {
            var t = Sampler.SampleBatch([extract, brief], settings, rng);
            Assert.Equal(0, t[0]);
            seen.Add(t[1]);
        }
        Assert.Equal(4, seen.Count); // 0.3 + 0.3 + 0.2 = 0.8 < 0.9, so all four are in the nucleus
        Assert.Throws<ArgumentException>(() => Sampler.SampleBatch([extract], settings, rng));
    }

    [Fact]
    public void A_peaked_row_needs_far_fewer_tokens_than_a_flat_one_for_the_same_p()
    {
        var peaked = SimulatedLogits.PowerLaw(50_000, 1.6, new Random(11));
        var flat = SimulatedLogits.PowerLaw(50_000, 0.8, new Random(11));
        int a = Sampler.Filter(peaked, new SamplingParams(TopP: 0.9)).Length;
        int b = Sampler.Filter(flat, new SamplingParams(TopP: 0.9)).Length;
        Assert.True(a * 10 < b, $"peaked {a}, flat {b}");
    }
}
