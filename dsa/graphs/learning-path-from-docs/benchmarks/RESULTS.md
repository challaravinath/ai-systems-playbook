# Tarjan vs Kosaraju: benchmark results

What runs: `SccBenchmarks` in `LearningPath.Benchmarks`. Each graph has Edges / 4 nodes. Edges are random,
mostly pointing from a lower node id to a higher one, with 2% pointing back, so some of them close cycles.
`CondenseAndOrder` is the step after SCC (build the component DAG, then Kahn's order) and is there for scale.

Shape of the graphs (`dotnet run -c Release -- --stats`):

```
edges=    10000 nodes=    2500 components=    1524 largest=    977 in-cycles=    977
edges=   100000 nodes=   25000 components=   14172 largest=  10829 in-cycles=  10829
edges=  1000000 nodes=  250000 components=  140932 largest= 109069 in-cycles= 109069
```

So about 43% of nodes sit in one big tangle and the rest are single-node components.

Command (from `code/benchmarks/LearningPath.Benchmarks`):

```
dotnet run -c Release -- --filter *
```

Full run (BenchmarkDotNet's default job, about 10 minutes), on my laptop:

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core Ultra 5 225U 1.50GHz, 1 CPU, 14 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
```

| Method           | Edges   | Mean         | Error        | StdDev        | Median       | Ratio | Allocated   | Alloc Ratio |
|----------------- |-------- |-------------:|-------------:|--------------:|-------------:|------:|------------:|------------:|
| Tarjan           | 10000   |     73.33 us |     1.464 us |      2.786 us |     73.49 us |  1.00 |    61.23 KB |        1.00 |
| Kosaraju         | 10000   |    146.85 us |     0.568 us |      0.503 us |    146.91 us |  2.01 |   100.36 KB |        1.64 |
| CondenseAndOrder | 10000   |    252.29 us |     4.981 us |      8.458 us |    248.12 us |  3.45 |   207.89 KB |        3.39 |
| Tarjan           | 100000  |  1,703.31 us |    33.774 us |     61.758 us |  1,690.05 us |  1.00 |   610.59 KB |        1.00 |
| Kosaraju         | 100000  |  2,313.04 us |    45.167 us |     61.825 us |  2,286.07 us |  1.36 |  1001.31 KB |        1.64 |
| CondenseAndOrder | 100000  |  3,407.00 us |   313.591 us |    919.707 us |  3,362.15 us |  2.00 |  1752.56 KB |        2.87 |
| Tarjan           | 1000000 | 23,337.01 us |   459.108 us |    958.330 us | 23,111.08 us |  1.00 |  6103.83 KB |        1.00 |
| Kosaraju         | 1000000 | 27,958.75 us |   554.283 us |    700.990 us | 28,067.84 us |  1.20 | 10010.18 KB |        1.64 |
| CondenseAndOrder | 1000000 | 43,273.29 us | 4,732.766 us | 13,655.112 us | 36,373.43 us |  1.86 | 15282.66 KB |        2.50 |

CondenseAndOrder at 1M edges is noisy (the StdDev is large), probably garbage collection on the big arrays, so use its median.
The Tarjan vs Kosaraju numbers are steady.

These are numbers from one laptop on random graphs. They show the ratio between the two, not what you'd get on your own docs.
