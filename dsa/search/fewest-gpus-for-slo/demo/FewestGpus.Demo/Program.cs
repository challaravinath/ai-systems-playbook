using System.Globalization;
using FewestGpus;

// Usage:
//   dotnet run --project demo/FewestGpus.Demo -- --trace <path to an Azure trace csv> --gpu A100
// The trace files are not in this repo. Download them from
// https://github.com/Azure/AzurePublicDataset/blob/master/AzureLLMInferenceDataset2023.md

const double Percentile = 99;   // Splitwise, Table VI: P99 TTFT
const double MaxSlowdown = 6;   // at most 6x the TTFT on an idle machine

string? tracePath = Arg("--trace");
string gpu = Arg("--gpu") ?? "A100";
if (tracePath is null)
{
    Console.Error.WriteLine("Usage: --trace <path to an Azure trace csv> [--gpu A100|H100]");
    return 1;
}

PrefillModel prefill = gpu.ToUpperInvariant() switch
{
    "A100" => PrefillModel.SplitwiseA100,
    "H100" => PrefillModel.SplitwiseH100,
    _ => throw new ArgumentException($"Unknown GPU '{gpu}'. Use A100 or H100."),
};

var rows = AzureTrace.Load(tracePath);
var requests = AzureTrace.ToRequests(rows, prefill);
double spanSeconds = requests.Max(r => r.ArrivalMs) / 1000;
var busiest = AzureTrace.BusiestSecond(rows);
double medianTokens = ServingSimulator.Percentile(rows.Select(r => (double)r.ContextTokens).ToArray(), 50);

Console.WriteLine($"Trace:            {Path.GetFileName(tracePath)}");
Console.WriteLine($"Requests:         {rows.Count:N0} over {spanSeconds / 60:F1} minutes");
Console.WriteLine($"Average:          {rows.Count / spanSeconds:F2} requests a second");
Console.WriteLine($"Busiest second:   {busiest.Requests} requests at {busiest.Second:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine($"Median prompt:    {medianTokens:F0} tokens");
// Total idle work divided by the trace's length: how many machines the average traffic keeps busy.
Console.WriteLine($"Average load:     {requests.Sum(r => r.PrefillMs) / 1000 / spanSeconds:F2} machines busy, if the traffic were perfectly even");
Console.WriteLine($"Idle TTFT ({prefill.Name}): {prefill.FixedMs.ToString("F1", CultureInfo.InvariantCulture)} ms + {prefill.MsPerToken.ToString("F4", CultureInfo.InvariantCulture)} ms per prompt token");
Console.WriteLine($"Target:           p{Percentile} of TTFT / TTFT on an idle A100 machine, at most {MaxSlowdown}");
Console.WriteLine();

// Splitwise's baseline is the same request on an idle DGX-A100, whichever GPU is being sized.
double[] baseline = rows.Select(r => PrefillModel.SplitwiseA100.IdleTtftMs(r.ContextTokens)).ToArray();
var check = ServingSimulator.MeetsSlowdownTarget(requests, baseline, Percentile, MaxSlowdown);
var result = await ServingSimulator.FewestReplicasAsync(requests, check);

Console.WriteLine("Checks, in the order the search ran them:");
foreach (var c in result.Log)
{
    double observed = ServingSimulator.SlowdownPercentile(requests, baseline, c.Candidate, Percentile);
    Console.WriteLine($"  {c.Candidate,5} machines: p99 slowdown {observed,8:F2}  {(c.Passed ? "Y" : "N")}");
}

Console.WriteLine();
if (result.Answer is int machines)
{
    Console.WriteLine($"Fewest machines: {machines} ({machines * PrefillModel.GpusPerMachine} GPUs at {PrefillModel.GpusPerMachine} per machine), found in {result.Checks} check(s).");
}
else
{
    Console.WriteLine($"No number of machines meets the target, checked in {result.Checks} check(s).");
}

// The same answer, found the other two ways, to compare how many checks each one costs.
var plainBinary = await AnswerSearch.FewestThatPassAsync(1, requests.Length, check);
int oneByOne = result.Answer ?? requests.Length;
Console.WriteLine();
Console.WriteLine("Same answer, other ways:");
Console.WriteLine($"  Try 1, 2, 3 ... in order:            {oneByOne} checks");
Console.WriteLine($"  Binary search over 1 to {requests.Length:N0} (one per request): {plainBinary.Checks} checks (answer {plainBinary.Answer?.ToString() ?? "none"})");
Console.WriteLine($"  Doubling, then binary (used above):  {result.Checks} checks");
return 0;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
