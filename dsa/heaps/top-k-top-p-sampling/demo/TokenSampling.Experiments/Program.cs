// What-if experiments for the sampler. Every row of logits here is SIMULATED (SimulatedLogits.PowerLaw):
// p(rank r) proportional to r^-s, token ids shuffled, plus noise. The shape is our assumption, not measured from a model.
// Times are one CPU thread in C#. Real serving engines run this step on the GPU, over a whole batch at once,
// so read the times as a comparison between methods, not as what a GPU engine spends.
//
//   dotnet run -c Release --project demo/TokenSampling.Experiments
using System.Diagnostics;
using System.Globalization;
using TokenSampling;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var methods = new[] { TopKMethod.Sort, TopKMethod.Heap, TopKMethod.Quickselect };

Section("1. Top-k: time per row, median of 31 runs, s = 1.1, noise 0.5");
Console.WriteLine("   Each method's own work only: quickselect includes building its V-long id array, the heap needs just k slots.");
Console.WriteLine("   vocabulary      k        sort        heap   quickselect");
foreach (int v in new[] { 32_000, 128_000, 256_000 })
{
    var row = SimulatedLogits.PowerLaw(v, 1.1, new Random(v), noise: 0.5);
    foreach (int k in new[] { 50, 1_000 })
    {
        var t = methods.Select(m => Median(() => TopK.Select(row, k, m))).ToArray();
        Console.WriteLine($"   {v,10:N0} {k,6:N0} {Us(t[0])} {Us(t[1])} {Us(t[2])}");
    }
}

Section("2. Top-p alone (p = 0.9), no top-k: full sort vs heap that pops only the nucleus, vocabulary 128,000");
Console.WriteLine("   Both columns time the whole Filter call, including two O(V) passes (check the row, add up the total).");
Console.WriteLine("   shape            s   nucleus        sort        heap");
foreach (var (name, s) in Shapes())
{
    var row = SimulatedLogits.PowerLaw(128_000, s, new Random(21), noise: 0.3);
    var settings = new SamplingParams(TopP: 0.9);
    int size = Sampler.Filter(row, settings).Length;
    double sort = Median(() => Sampler.Filter(row, settings, TopKMethod.Sort));
    double heap = Median(() => Sampler.Filter(row, settings, TopKMethod.Heap));
    Console.WriteLine($"   {name,-12} {s,5:0.0} {size,9:N0} {Us(sort)} {Us(heap)}");
}

Section("3. One fixed k against the shape of the distribution, vocabulary 128,000 (Holtzman et al.'s argument, on simulated rows)");
Console.WriteLine("   shape            s   top-50 covers   tokens for p = 0.9   tokens for p = 0.95");
foreach (var (name, s) in Shapes())
{
    var row = SimulatedLogits.PowerLaw(128_000, s, new Random(31), noise: 0.3);
    var all = Sampler.Filter(row, new SamplingParams());
    double top50 = all.Take(50).Sum(c => c.Probability);
    int p90 = Sampler.Filter(row, new SamplingParams(TopP: 0.9)).Length;
    int p95 = Sampler.Filter(row, new SamplingParams(TopP: 0.95)).Length;
    Console.WriteLine($"   {name,-12} {s,5:0.0} {top50,14:P1} {p90,20:N0} {p95,21:N0}");
}

Section("4. Does the order matter? top-k 50 then top-p 0.9 (vLLM's order) vs top-p then top-k, 1,000 rows, vocabulary 32,000");
{
    var rng = new Random(41);
    int differ = 0; long sizeKThenP = 0, sizePThenK = 0;
    for (int i = 0; i < 1_000; i++)
    {
        double s = 0.6 + rng.NextDouble() * 1.2;
        var row = SimulatedLogits.PowerLaw(32_000, s, rng, noise: 0.5);
        int a = Sampler.Filter(row, new SamplingParams(TopK: 50, TopP: 0.9)).Length;
        int nucleus = Sampler.Filter(row, new SamplingParams(TopP: 0.9)).Length;
        int b = Math.Min(50, nucleus); // top-p on the full distribution, then keep at most 50
        if (a != b) differ++;
        sizeKThenP += a; sizePThenK += b;
    }
    Console.WriteLine($"   rows where the kept set differs: {differ} of 1,000");
    Console.WriteLine($"   mean tokens kept: top-k then top-p {sizeKThenP / 1000.0:0.0}, top-p then top-k {sizePThenK / 1000.0:0.0}");
}

Section("5. Temperature changes the nucleus: tokens kept at p = 0.9, s = 1.1, vocabulary 128,000");
{
    var row = SimulatedLogits.PowerLaw(128_000, 1.1, new Random(51), noise: 0.3);
    foreach (double t in new[] { 0.5, 0.7, 1.0, 1.3 })
        Console.WriteLine($"   temperature {t,3:0.0}: {Sampler.Filter(row, new SamplingParams(Temperature: t, TopP: 0.9)).Length,8:N0} tokens");
}

Section("6. One step for a batch of 64 requests with mixed settings, vocabulary 128,000, median of 11");
{
    var rng = new Random(61);
    var rows = Enumerable.Range(0, 64).Select(_ => SimulatedLogits.PowerLaw(128_000, 0.8 + rng.NextDouble(), rng, noise: 0.3)).ToArray();
    // Half "extract a fact" (greedy), half "write the brief" (top-k 50, top-p 0.9). Our split, for illustration.
    Console.WriteLine("   Half the rows are greedy, which is the same O(V) scan for every method, so the gap is smaller than in section 1.");
    var settings = Enumerable.Range(0, 64).Select(i => i % 2 == 0 ? SamplingParams.Greedy : new SamplingParams(Temperature: 0.8, TopK: 50, TopP: 0.9)).ToArray();
    foreach (var m in methods)
    {
        var draw = new Random(1);
        Console.WriteLine($"   {m,-12} {Median(() => Sampler.SampleBatch(rows, settings, draw, m), runs: 11) / 1000.0,8:0.00} ms per batch step");
    }
}

static IEnumerable<(string Name, double S)> Shapes() =>
    [("peaked", 1.6), ("medium", 1.1), ("flat", 0.8)];

static void Section(string title) => Console.WriteLine($"\n== {title}");

static string Us(double microseconds) => $"{microseconds,8:N0} us";

static double Median(Action work, int runs = 31)
{
    for (int i = 0; i < 3; i++) work(); // warm up the JIT
    var times = new double[runs];
    for (int i = 0; i < runs; i++)
    {
        long start = Stopwatch.GetTimestamp();
        work();
        times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
    }
    Array.Sort(times);
    return times[runs / 2];
}
