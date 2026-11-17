namespace TokenSampling.Tests;

public class TopKTests
{
    public static TheoryData<TopKMethod> Methods => new() { TopKMethod.Sort, TopKMethod.Heap, TopKMethod.Quickselect };

    [Theory, MemberData(nameof(Methods))]
    public void Every_method_matches_a_plain_sort_on_rows_full_of_ties(TopKMethod method)
    {
        var rng = new Random(1);
        for (int trial = 0; trial < 2000; trial++)
        {
            int v = rng.Next(1, 300);
            var row = Oracle.RowWithTies(rng, v);
            int k = rng.Next(1, v + 4); // sometimes larger than the vocabulary
            Assert.Equal(Oracle.TopK(row, k), TopK.Select(row, k, method));
        }
    }

    [Theory, MemberData(nameof(Methods))]
    public void A_tie_goes_to_the_lower_token_id(TopKMethod method)
    {
        float[] row = [2, 5, 5, 1, 5];
        Assert.Equal([1, 2], TopK.Select(row, 2, method));
        Assert.Equal([1, 2, 4, 0], TopK.Select(row, 4, method));
    }

    [Theory, MemberData(nameof(Methods))]
    public void K_larger_than_the_vocabulary_returns_every_token_best_first(TopKMethod method)
    {
        float[] row = [0.5f, 3, -1];
        Assert.Equal([1, 0, 2], TopK.Select(row, 10, method));
    }

    [Theory, MemberData(nameof(Methods))]
    public void Sorted_reversed_and_all_equal_rows_still_match(TopKMethod method)
    {
        // Rows that would hurt a quickselect with a fixed first or last pivot.
        int v = 20_000;
        var ascending = Enumerable.Range(0, v).Select(i => (float)i).ToArray();
        var descending = ascending.Reverse().ToArray();
        var equal = new float[v];
        foreach (var row in new[] { ascending, descending, equal })
            foreach (int k in new[] { 1, 50, v / 2, v })
                Assert.Equal(Oracle.TopK(row, k), TopK.Select(row, k, method));
    }

    [Theory, MemberData(nameof(Methods))]
    public void A_full_size_vocabulary_matches(TopKMethod method)
    {
        var row = SimulatedLogits.PowerLaw(128_000, 1.1, new Random(7), noise: 0.5);
        Assert.Equal(Oracle.TopK(row, 50), TopK.Select(row, 50, method));
    }

    [Theory, MemberData(nameof(Methods))]
    public void A_tie_at_the_k_th_place_keeps_exactly_k(TopKMethod method)
    {
        // Our rule. Hugging Face keeps every token tied with the k-th, so it would keep all three here.
        Assert.Equal([0, 1], TopK.Select([5f, 5f, 5f], 2, method));
    }

    [Theory, MemberData(nameof(Methods))]
    public void A_row_that_would_break_the_order_is_rejected(TopKMethod method)
    {
        // With NaN the three methods used to return three different answers.
        Assert.Throws<ArgumentException>(() => TopK.Select([1f, float.NaN, 2f], 2, method));
        Assert.Throws<ArgumentException>(() => TopK.Select([1f, float.PositiveInfinity], 1, method));
        Assert.Throws<ArgumentException>(() => TopK.Select([], 1, method));
    }

    [Fact]
    public void K_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TopK.Select([1f, 2f], 0, TopKMethod.Heap));
    }
}
