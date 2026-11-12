using System.Globalization;

namespace TenantRateLimiter;

public readonly record struct TraceRow(DateTime Timestamp, int ContextTokens, int GeneratedTokens);

// Reads the Azure LLM inference traces (2023). The files aren't in this repo, get them from
// https://github.com/Azure/AzurePublicDataset/blob/master/AzureLLMInferenceDataset2023.md
public static class AzureTrace
{
    private const string Header = "TIMESTAMP,ContextTokens,GeneratedTokens";
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

    public static IReadOnlyList<TraceRow> Load(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader);
    }

    public static IReadOnlyList<TraceRow> Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? header = reader.ReadLine();
        if (header?.Trim() != Header)
        {
            throw new FormatException($"expected header '{Header}', got '{header}'");
        }

        var rows = new List<TraceRow>();
        int lineNo = 1;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (line.Length == 0)
            {
                continue;
            }

            string[] f = line.Split(',');
            if (f.Length != 3
                || !DateTime.TryParseExact(f[0], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)
                || !int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int context)
                || !int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out int generated))
            {
                throw new FormatException($"Line {lineNo} isn't a trace row: '{line}'");
            }
            rows.Add(new TraceRow(ts, context, generated));
        }

        if (rows.Count == 0)
        {
            throw new FormatException("no rows in the trace");
        }
        return rows;
    }
}
