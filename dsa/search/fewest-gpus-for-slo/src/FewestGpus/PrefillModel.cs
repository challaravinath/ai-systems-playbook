namespace FewestGpus;

// Idle TTFT as a straight line in prompt size: FixedMs + MsPerToken * tokens.
public sealed record PrefillModel(string Name, double FixedMs, double MsPerToken)
{
    public static PrefillModel FromTwoPoints(string name, int tokens1, double ms1, int tokens2, double ms2)
    {
        if (tokens1 == tokens2)
        {
            throw new ArgumentException("the two points need different prompt sizes");
        }

        double perToken = (ms2 - ms1) / (tokens2 - tokens1);
        double fixedMs = ms1 - (perToken * tokens1);
        if (perToken < 0 || fixedMs < 0)
        {
            throw new ArgumentException("that line goes negative, so it can't be a prefill time");
        }
        return new PrefillModel(name, fixedMs, perToken);
    }

    public double IdleTtftMs(int promptTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(promptTokens);
        return FixedMs + (MsPerToken * promptTokens);
    }

    // Splitwise (ISCA 2024), Table IV: median TTFT, Llama-70B, one DGX machine, no batching.
    //   coding:       A100 185 ms, H100 95 ms
    //   conversation: A100 155 ms, H100 84 ms
    // Median prompt size of each Azure 2023 trace (the demo prints these): 1469 and 1020 tokens.
    // Splitwise says TTFT "grows almost linearly" with prompt size, so we put a line through
    // the two medians. That's our fit, not their number, and two points always fit a line.
    public const int CodingMedianTokens = 1469;
    public const int ConversationMedianTokens = 1020;

    public static PrefillModel SplitwiseA100 { get; } =
        FromTwoPoints("A100", CodingMedianTokens, 185, ConversationMedianTokens, 155);

    public static PrefillModel SplitwiseH100 { get; } =
        FromTwoPoints("H100", CodingMedianTokens, 95, ConversationMedianTokens, 84);

    // Splitwise runs Llama-70B with tensor parallelism across 8 GPUs.
    public const int GpusPerMachine = 8;
}
