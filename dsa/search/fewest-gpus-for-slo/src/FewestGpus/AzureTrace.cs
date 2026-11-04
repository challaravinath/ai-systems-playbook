using System.Globalization;

namespace FewestGpus;

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

    // Arrival = ms after the earliest row. Prefill comes from the given line.
    public static Request[] ToRequests(IReadOnlyList<TraceRow> rows, PrefillModel prefill)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(prefill);
        if (rows.Count == 0)
        {
            throw new ArgumentException("no rows", nameof(rows));
        }

        DateTime start = rows.Min(r => r.Timestamp);
        return rows
            .Select(r => new Request((r.Timestamp - start).TotalMilliseconds, prefill.IdleTtftMs(r.ContextTokens)))
            .ToArray();
    }

    public static (DateTime Second, int Requests) BusiestSecond(IReadOnlyList<TraceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows
            .GroupBy(r => new DateTime(r.Timestamp.Ticks - (r.Timestamp.Ticks % TimeSpan.TicksPerSecond)))
            .Select(g => (Second: g.Key, Requests: g.Count()))
            .OrderByDescending(x => x.Requests)
            .ThenBy(x => x.Second)
            .First();
    }
}
