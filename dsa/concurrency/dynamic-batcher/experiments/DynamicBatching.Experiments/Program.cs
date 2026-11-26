using System.Diagnostics;
using System.Globalization;
using System.Text;
using DynamicBatching.Experiments;

// Is batching worth it? Runs the real DynamicBatcher on a fake clock with a simulated GPU.
//   dotnet run -c Release --project experiments/DynamicBatching.Experiments -- [--trace <AzureLLMInferenceTrace_code_1week.csv>] [--out <dir>]
// Batch cost: NVIDIA's published BERT Base table (A100). Arrivals: Poisson (simulated) and,
// if --trace is given, the busiest hour of the Azure LLM inference trace 2024.
// Seeds are fixed, so every run prints the same numbers.

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var cost = BatchCost.BertBaseA100;
const int Requests = 20_000;
const int Seed = 1;
string outDir = Arg("--out") ?? "results";
string? tracePath = Arg("--trace");
Directory.CreateDirectory(outDir);

// The real clock (PR 4): how long does a lone request really wait for a given T on this machine?
//   dotnet run -c Release --project experiments/DynamicBatching.Experiments -- --real-clock
if (args.Contains("--real-clock"))
{
    var clockReport = new StringBuilder();
    void ClockLine(string text = "")
    {
        Console.WriteLine(text);
        clockReport.AppendLine(text);
    }

    ClockLine("# The real clock: how long a lone request waits for T");
    ClockLine();
    ClockLine($"Measured on: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, " +
              $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
              $"{Environment.ProcessorCount} logical processors, {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "processor not reported"}.");
    ClockLine($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC. Other machines, and the same machine under other load, give other numbers.");
    ClockLine();
    ClockLine("Each request is sent alone, after a random 0-20 ms pause, with N = 1000 so only T can close the batch.");
    ClockLine("Waited = from just before SubmitAsync until the batch function starts (Stopwatch). 200 requests per T, seed 1.");
    ClockLine();
    ClockLine("| T asked (ms) | Min | p50 | p99 | Max | Closed before T | Timers per request (mean / most) |");
    ClockLine("|---|---|---|---|---|---|---|");
    foreach (double t in new[] { 1.0, 2, 5, 10, 20 })
    {
        var row = await RealClockCheck.MeasureAsync(TimeSpan.FromMilliseconds(t), samples: 200, seed: 1);
        ClockLine($"| {t} | {row.MinMs:0.00} | {row.P50Ms:0.00} | {row.P99Ms:0.00} | {row.MaxMs:0.00} | " +
                  $"{row.ClosedEarly} of {row.Samples} | {row.TimersPerRequest:0.00} / {row.MostTimersForOneRequest} |");
    }

    string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";
    string clockFile = Path.Combine(outDir, $"real-clock-{os}.md");
    File.WriteAllText(clockFile, clockReport.ToString());
    Console.Error.WriteLine($"wrote {clockFile}");
    return 0;
}

var report = new StringBuilder();
var csv = new StringBuilder("experiment,arrivals,rate_per_s,N,T_ms,requests,batches,mean_batch,p50_ms,p99_ms,max_ms,throughput_per_s,gpu_busy_pct\n");
var watch = Stopwatch.StartNew();

void Line(string text = "")
{
    Console.WriteLine(text);
    report.AppendLine(text);
}

async Task<RunResult> Run(string experiment, string arrivalsName, double rate, int n, double tMs, long[] arrivals)
{
    var r = await Experiment.RunAsync(cost, n, TimeSpan.FromMilliseconds(tMs), arrivals);
    csv.AppendLine($"{experiment},{arrivalsName},{rate:0.#},{n},{tMs},{r.Requests},{r.Batches},{r.MeanBatch:0.00},{r.P50Ms:0.000},{r.P99Ms:0.000},{r.MaxMs:0.000},{r.ThroughputPerSecond:0},{r.GpuBusyPercent:0.0}");
    return r;
}

const string Header = "| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |";
const string Rule = "|---|---|---|---|---|---|---|";
// Marked when requests arrive faster, on average, than full batches of N can be served:
// the queue then grows for the whole run, so the latency depends on how long the run is.
string Row(int n, double t, RunResult r, double rate = 0) =>
    $"| {n}{(rate > n / cost.Ms(n) * 1000 ? " (can't keep up)" : "")} | {t} | {r.MeanBatch:0.0} | {r.P50Ms:0.00} | {r.P99Ms:0.00} | {r.ThroughputPerSecond:N0} | {r.GpuBusyPercent:0}% |";

Line("# Dynamic batcher experiments (simulated)");
Line();
Line($"Batch cost: {cost.Name}. Latency = time from arrival until the batch holding the request finishes on the GPU.");
Line($"Poisson runs: {Requests:N0} requests, seed {Seed}. One GPU, one batch at a time.");

// 1. Throughput vs N. Far more requests than the GPU can take, so it never waits for work.
Line();
Line("## 1. Throughput vs batch size, under overload (Poisson 20,000/s)");
Line();
Line("| N | Throughput (req/s) | From the table: N / cost(N) | Mean batch |");
Line("|---|---|---|---|");
var flood = Arrivals.Poisson(20_000, Requests, Seed);
foreach (int n in new[] { 1, 2, 4, 8, 16, 32, 64, 128 })
{
    var r = await Run("1-throughput", "poisson", 20_000, n, 5, flood);
    Line($"| {n} | {r.ThroughputPerSecond:N0} | {n / cost.Ms(n) * 1000:N0} | {r.MeanBatch:0.0} |");
}

// 2. Latency vs T when traffic is light: waiting T is all cost.
Line();
Line("## 2. Latency vs wait T, under light load (Poisson 200/s, N = 32)");
Line();
Line(Header);
Line(Rule);
var light = Arrivals.Poisson(200, Requests, Seed);
foreach (double t in new[] { 0, 1, 2, 5, 10 })
{
    Line(Row(32, t, await Run("2-light", "poisson", 200, 32, t, light)));
}

// 3. From light to heavy: when does T start to pay?
Line();
Line("## 3. Load sweep (Poisson): no batching vs batching with and without a wait");
foreach (double rate in new[] { 200, 1_000, 1_500, 2_000, 4_000, 6_000, 7_500 })
{
    Line();
    Line($"**{rate:N0} requests/s**");
    Line();
    Line(Header);
    Line(Rule);
    var arrivals = Arrivals.Poisson(rate, Requests, Seed);
    foreach (var (n, t) in new[] { (1, 0.0), (32, 0.0), (32, 2.0), (32, 5.0) })
    {
        Line(Row(n, t, await Run("3-sweep", "poisson", rate, n, t, arrivals), rate));
    }
}

// 4. Real arrival times: the busiest hour of the Azure trace, as recorded and sped up.
if (tracePath is not null)
{
    var hour = Arrivals.BusiestHour(tracePath);
    Line();
    Line("## 4. Azure LLM inference trace 2024, busiest hour (arrival times only)");
    Line();
    Line($"Trace: {Path.GetFileName(tracePath)}, {hour.TraceRequests:N0} requests in the week. " +
         $"Busiest hour: {hour.HourUtc:yyyy-MM-dd HH}:00 UTC, {hour.Times.Length:N0} requests " +
         $"({hour.Times.Length / 3600.0:0.0}/s on average, busiest second {Arrivals.Busiest(hour.Times, TimeSpan.FromSeconds(1))}).");
    foreach (int speed in new[] { 1, 10, 50 })
    {
        var times = Arrivals.SpeedUp(hour.Times, speed);
        Line();
        Line(speed == 1
            ? "**As recorded**"
            : $"**Sped up {speed}x** (simulated load: busiest second {Arrivals.Busiest(times, TimeSpan.FromSeconds(1)):N0})");
        Line();
        Line(Header);
        Line(Rule);
        foreach (var (n, t) in new[] { (1, 0.0), (32, 0.0), (32, 2.0), (32, 5.0) })
        {
            double rate = hour.Times.Length / 3600.0 * speed;
            Line(Row(n, t, await Run("4-azure", $"azure-x{speed}", rate, n, t, times), rate));
        }
    }
}

File.WriteAllText(Path.Combine(outDir, "results.md"), report.ToString());
File.WriteAllText(Path.Combine(outDir, "results.csv"), csv.ToString());
Console.Error.WriteLine($"done in {watch.Elapsed.TotalSeconds:0}s, wrote {outDir}/results.md and results.csv");
return 0;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
