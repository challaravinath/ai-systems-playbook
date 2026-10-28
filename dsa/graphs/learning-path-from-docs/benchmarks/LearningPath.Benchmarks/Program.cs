using BenchmarkDotNet.Running;
using LearningPath;
using LearningPath.Benchmarks;

// dotnet run -c Release -- --stats     prints what the benchmark graphs look like
// dotnet run -c Release -- --filter *  runs the benchmarks
if (args.Contains("--stats"))
{
    foreach (var m in new[] { 10_000, 100_000, 1_000_000 })
    {
        var g = Workload.Build(m, seed: 42);
        var c = Scc.Tarjan(g);
        var sizes = new int[c.Count];
        foreach (var x in c.Of) sizes[x]++;
        Console.WriteLine($"edges={m,9} nodes={g.NodeCount,8} components={c.Count,8} largest={sizes.Max(),7} in-cycles={sizes.Where(s => s > 1).Sum(),7}");
    }
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(SccBenchmarks).Assembly).Run(args);
