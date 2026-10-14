# AI Systems Playbook

**Data structures, algorithms and system design, worked through the way real AI systems use them.**

Underneath every AI system are old, well-understood ideas: graphs, heaps, hashing, dynamic programming, concurrency. An agent running its plan is a topological sort. A prompt cache is a radix tree with LRU eviction. Packing retrieved chunks into a context window is a knapsack problem.

This playbook takes one real AI problem at a time and works it through properly: why the problem exists, where companies run the algorithm in production (with sources), how to solve it and why it's correct, what breaks at scale, and C# code with tests that run.

[![tests](../../actions/workflows/test.yml/badge.svg)](../../actions/workflows/test.yml)

---

## What every problem contains

1. **The problem**, stated plainly, with hints you can expand, so you can try it first.
2. **Where it runs in the real world:** which companies use this algorithm, for what, with links to their docs, papers or engineering posts.
3. **The questions that shape the design**, and how each answer changes it.
4. **The mental model:** the one idea that carries the solution.
5. **Brute force → optimal**, with complexity, and the trade-offs between designs.
6. **Invariants:** the properties that make the solution correct.
7. **C# code with tests that run**, each test named after the behaviour it protects.
8. **Going further:** what real systems need next (scale, durability, distribution) and how to get there.
9. **From working code to production:** observability, failure, security.
10. **Common mistakes**, and why each one hurts.

Each problem folder also has a PDF of its README for offline reading.

---

## Contents

### Data structures and algorithms

| # | Problem | Patterns | Real-world anchor | Status |
|---|---|---|---|---|
| P01 | [Agent plan executor](dsa/track-01-graphs/p01-agent-dag-executor/) | Topological sort, cycle detection, DP on a DAG, bounded concurrency, cancellation | PyTorch autograd, LangGraph, Durable Functions, Temporal, Airflow, Bazel | ✅ 23 tests |

### System design

Full designs of AI systems, from the architecture down to failure modes, evaluation and responsible AI. *Coming soon.*

---

## Roadmap

**Up next:** P02 *Learning path from a pile of docs* (SCCs, condensation DAG) · P03 *Prefix-cache radix tree with LRU* · P04 *Per-tenant rate limiter*.

The full plan, 25 problems and 12 system designs, is in **[ROADMAP.md](ROADMAP.md)**. Each DSA problem builds a component that a later design uses: the rate limiter and the retries become parts of the AI gateway, the radix tree and the batcher become parts of inference serving.

Problems are grouped into tracks by pattern:

| Track | Patterns | Example AI problems |
|---|---|---|
| 1 · Graphs and DAGs | Topological sort, SCC, union-find, shortest paths | Agent plan execution ✅, document prerequisite graphs, entity resolution, tool-call loop detection |
| 2 · Trees and tries | Trie, radix tree, segment tree, tree search | Tokenizer prefix match, prefix-cache radix tree with LRU, beam search |
| 3 · Heaps and top-K | Heaps, k-way merge, streaming quantiles | Top-k / top-p sampling, merging shard results, p99 latency |
| 4 · Hashing and caching | LRU/LFU with expiry, consistent hashing, Bloom, MinHash | Semantic cache, session affinity, training-data dedup |
| 5 · Search | kNN → IVF/HNSW, binary search on the answer | Vector search, the fewest GPUs that meet an SLO |
| 6 · DP and strings | Knapsack, edit distance, Viterbi | Packing the context window, word error rate, chunking |
| 7 · Optimisation | Greedy, bin packing, assignment, scheduling | Placing models on GPUs, continuous batching |
| 8 · Concurrency | Channels, semaphores, coalescing, circuit breakers | Dynamic batcher, concurrent rate limiter, single-flight cache |
| 9 · Streaming and parsing | State machines, incremental parsers | Streaming tokens over SSE, parsing tool-call JSON as it arrives |
| 10 · ML from scratch | Numerically stable maths | Softmax, attention with a KV cache, BM25, k-means |

---

## Run it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd dsa/track-01-graphs/p01-agent-dag-executor
dotnet test
```

To rebuild a PDF after editing a README (needs Node 18+ and Chrome):

```bash
node tools/build-pdf.mjs dsa/track-01-graphs/p01-agent-dag-executor/README.md
```

---

## A note on sources

Company examples come from public documentation, papers and engineering posts, linked in each problem. Internal systems change over time; describe them as *"publicly described as…"*. The designs here are original work, not any company's internal system.

## License

Code: [MIT](LICENSE) · Written content and diagrams: [CC BY 4.0](LICENSE-content)
