using System.Globalization;
using FewestGpus;

// What-if runs on the Azure LLM inference trace (2023):
//   dotnet run --project demo/FewestGpus.Experiments -- --trace <AzureLLMInferenceTrace_code.csv>
// Same model and target as the main demo: p99 of TTFT / TTFT on an idle A100 machine, at most 6.
// Seeds are fixed, so every run prints the same numbers.

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
const double P99 = 99;
const double Target = 6;

string? tracePath = Arg("--trace");
if (tracePath is null)
{
    Console.Error.WriteLine("Usage: --trace <path to an Azure trace csv>");
    return 1;
}

var rows = AzureTrace.Load(tracePath);
double[] baseline = rows.Select(r => PrefillModel.SplitwiseA100.IdleTtftMs(r.ContextTokens)).ToArray();
var gpus = new[] { PrefillModel.SplitwiseA100, PrefillModel.SplitwiseH100 };

async Task<int?> Fewest(Request[] trace, double[] baselineMs, Func<int, CancellationToken, ValueTask<bool>> check) =>
    (await ServingSimulator.FewestReplicasAsync(trace, check)).Answer;

Console.WriteLine($"Trace: {Path.GetFileName(tracePath)}, {rows.Count:N0} requests");

// 1. The whole curve. In a simulator a check costs milliseconds, so look at every n.
Console.WriteLine();
Console.WriteLine("1. p99 slowdown for each machine count (target: at most 6)");
foreach (var gpu in gpus)
{
    var trace = AzureTrace.ToRequests(rows, gpu);
    var cells = Enumerable.Range(1, 16).Select(n => $"{n}:{ServingSimulator.SlowdownPercentile(trace, baseline, n, P99):0.0#}");
    Console.WriteLine($"   {gpu.Name}  " + string.Join("  ", cells));
}

// 2. Bursts against smooth traffic: the same prompts arriving as a Poisson process at the same rate.
Console.WriteLine();
Console.WriteLine("2. Real arrivals against Poisson arrivals at the same average rate (20 seeds)");
foreach (var gpu in gpus)
{
    var trace = AzureTrace.ToRequests(rows, gpu);
    int? real = await Fewest(trace, baseline, ServingSimulator.MeetsSlowdownTarget(trace, baseline, P99, Target));
    var smooth = new List<int>();
    for (int seed = 1; seed <= 20; seed++)
    {
        var poisson = Arrivals.Poisson(trace, seed);
        // The Poisson trace is in arrival order, so line the baselines up with it.
        var order = Enumerable.Range(0, trace.Length).OrderBy(i => trace[i].ArrivalMs).ToArray();
        double[] b = order.Select(i => baseline[i]).ToArray();
        smooth.Add((await Fewest(poisson, b, ServingSimulator.MeetsSlowdownTarget(poisson, b, P99, Target)))!.Value);
    }
    smooth.Sort();
    double avg = trace.Sum(r => r.PrefillMs) / trace.Max(r => r.ArrivalMs);
    Console.WriteLine($"   {gpu.Name}  real {real}  Poisson median {smooth[smooth.Count / 2]} (range {smooth[0]} to {smooth[^1]})" +
        $"  average load {avg:0.00}  peak work in 1 s {Arrivals.PeakMachines(trace, 1000):0.0}, 10 s {Arrivals.PeakMachines(trace, 10_000):0.0}, 60 s {Arrivals.PeakMachines(trace, 60_000):0.0} machines");
}

// 3. A noisy check. Every call multiplies each prefill by its own lognormal factor (mean 1).
Console.WriteLine();
Console.WriteLine("3. Noisy check, A100: answers from 1,000 searches, and pass rates near the answer");
{
    var trace = AzureTrace.ToRequests(rows, PrefillModel.SplitwiseA100);
    foreach (double sigma in new[] { 0.1, 0.3 })
    {
        foreach (int k in new[] { 1, 3 })
        {
            var random = new Random(20231116);
            var noisy = Checks.NoisySlowdownTarget(trace, baseline, P99, Target, sigma, random);
            int calls = 0;
            var counted = Checks.MajorityOf(k, (n, ct) => { calls++; return noisy(n, ct); });
            var answers = new Dictionary<int, int>();
            for (int run = 0; run < 1000; run++)
            {
                int a = (await ServingSimulator.FewestReplicasAsync(trace, counted)).Answer!.Value;
                answers[a] = answers.GetValueOrDefault(a) + 1;
            }
            string dist = string.Join(", ", answers.OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Value / 10.0:0.#}%"));
            Console.WriteLine($"   sigma {sigma}, {(k == 1 ? "one run per check" : $"majority of {k}")}: {dist}; {calls / 1000.0:0.0} load tests per search");
        }
        var single = Checks.NoisySlowdownTarget(trace, baseline, P99, Target, sigma, new Random(7));
        var rates = new List<string>();
        foreach (int n in new[] { 10, 11, 12 })
        {
            int pass = 0;
            for (int i = 0; i < 1000; i++)
            {
                pass += await single(n, CancellationToken.None) ? 1 : 0;
            }
            rates.Add($"{n}: {pass / 10.0:0.#}%");
        }
        Console.WriteLine($"   sigma {sigma}, pass rate of one check at " + string.Join(", ", rates));
    }
}

// 4. How much the answer depends on our choices.
Console.WriteLine();
Console.WriteLine("4. Fewest A100 machines for each target and percentile");
{
    var trace = AzureTrace.ToRequests(rows, PrefillModel.SplitwiseA100);
    double[] targets = [2, 3, 4, 6, 10];
    double[] percentiles = [90, 95, 99, 99.9];
    Console.WriteLine("   target   " + string.Concat(percentiles.Select(p => $"{"p" + p,8}")));
    foreach (double t in targets)
    {
        var line = $"   {t,4}x    ";
        foreach (double p in percentiles)
        {
            line += $"{(await Fewest(trace, baseline, ServingSimulator.MeetsSlowdownTarget(trace, baseline, p, t)))?.ToString() ?? "none",8}";
        }
        Console.WriteLine(line);
    }

    Console.WriteLine("   The idle line 25% off (slope, fixed part), for the machine and the baseline alike:");
    foreach (double slope in new[] { 0.75, 1.0, 1.25 })
    {
        foreach (double fixedPart in new[] { 0.75, 1.0, 1.25 })
        {
            var line = new PrefillModel("A100", PrefillModel.SplitwiseA100.FixedMs * fixedPart, PrefillModel.SplitwiseA100.MsPerToken * slope);
            var t2 = AzureTrace.ToRequests(rows, line);
            double[] b2 = rows.Select(r => line.IdleTtftMs(r.ContextTokens)).ToArray();
            Console.Write($"   slope x{slope}, fixed x{fixedPart}: {await Fewest(t2, b2, ServingSimulator.MeetsSlowdownTarget(t2, b2, P99, Target))}");
        }
        Console.WriteLine();
    }

    // Leave out the busiest 60 seconds of arrivals.
    var sorted = trace.Select((r, i) => (r, i)).OrderBy(x => x.r.ArrivalMs).ToArray();
    int bestStart = 0, bestCount = 0;
    for (int s = 0, e = 0; e < sorted.Length; e++)
    {
        while (sorted[e].r.ArrivalMs - sorted[s].r.ArrivalMs >= 60_000) s++;
        if (e - s + 1 > bestCount) { bestCount = e - s + 1; bestStart = s; }
    }
    var drop = sorted.Skip(bestStart).Take(bestCount).Select(x => x.i).ToHashSet();
    var kept = Enumerable.Range(0, trace.Length).Where(i => !drop.Contains(i)).ToArray();
    var t3 = kept.Select(i => trace[i]).ToArray();
    double[] b3 = kept.Select(i => baseline[i]).ToArray();
    Console.WriteLine($"   Without the busiest 60 seconds ({bestCount} requests): {await Fewest(t3, b3, ServingSimulator.MeetsSlowdownTarget(t3, b3, P99, Target))}");
}

// 5. One p99 over the hour, against every 5-minute and every 1-minute window on its own.
Console.WriteLine();
Console.WriteLine("5. Fewest machines when every window must meet p99 at most 6");
foreach (var gpu in gpus)
{
    var trace = AzureTrace.ToRequests(rows, gpu);
    int? hourly = await Fewest(trace, baseline, ServingSimulator.MeetsSlowdownTarget(trace, baseline, P99, Target));
    int? five = await Fewest(trace, baseline, Checks.SlowdownTargetInEveryWindow(trace, baseline, P99, Target, 300_000));
    int? one = await Fewest(trace, baseline, Checks.SlowdownTargetInEveryWindow(trace, baseline, P99, Target, 60_000));
    double worstAtHourly = Checks.WorstWindowSlowdown(trace, baseline, hourly!.Value, P99, 300_000);
    Console.WriteLine($"   {gpu.Name}  whole trace {hourly}, every 5 minutes {five}, every minute {one}; at {hourly} machines the worst 5-minute p99 is {worstAtHourly:0.0}x");
}
return 0;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
