# AI Systems Playbook

**Data structures, algorithms and system design, worked through the way real AI systems use them.**

Underneath every AI system are old, well-understood ideas: graphs, heaps, hashing, dynamic programming, concurrency. An agent running its plan is a topological sort. A prompt cache can be a radix tree with LRU eviction. Packing retrieved chunks into a context window is a knapsack problem.

This playbook takes one real AI problem at a time and works it through properly: why the problem exists, where the algorithm shows up in real systems (with sources), how to solve it and why it's correct, what breaks at scale, and C# code with tests that run.

This is code for learning. It's tested and it runs, but it isn't a library to drop into production. Each topic says what it leaves out.

[![tests](../../actions/workflows/test.yml/badge.svg)](../../actions/workflows/test.yml)

---

## What every problem contains

1. **The problem**, stated plainly, with hints you can expand, so you can try it first.
2. **Where it runs in the real world:** which systems use this algorithm, for what, with links to their docs or papers.
3. **The questions that shape the design**, and how each answer changes it.
4. **The mental model:** the one idea that carries the solution.
5. **From brute force to optimal**, with complexity, and the trade-offs between designs.
6. **Invariants:** the properties that make the solution correct.
7. **C# code with tests that run**, each test named after the behaviour it protects.
8. **Going further:** what real systems need next (scale, durability, distribution) and how to get there.
9. **From working code to production:** observability, failure, security.
10. **Common mistakes**, and why each one hurts.

Each problem folder also has a PDF of its README for offline reading.

---

## Contents

### Data structures and algorithms

| Problem | Patterns | Real-world anchor | Status |
|---|---|---|---|
| [Agent plan executor](dsa/graphs/agent-plan-executor/) | Topological sort, cycle detection, DP on a DAG, bounded concurrency, cancellation | PyTorch autograd, LangGraph, Durable Functions, Temporal, Airflow, Bazel | Done, 48 tests |
| [Prefix-cache radix tree](dsa/trees-and-tries/prefix-cache-radix-tree/) | Trie, radix tree, LRU eviction over leaves with a min-heap, reference counting | SGLang RadixAttention, vLLM prefix caching, NVIDIA Dynamo, provider prompt caching | Done, 26 tests |
| [Learning path from a pile of docs](dsa/graphs/learning-path-from-docs/) | Strongly connected components (Tarjan, Kosaraju), condensation DAG, topological sort with a stable tie-break | CMake target dependencies, LLVM SCC iterator | Done, 65 tests |
| [Fewest GPUs that meet the SLO](dsa/search/fewest-gpus-for-slo/) | Binary search on the answer, doubling search, monotone checks | Azure LLM inference trace, Splitwise SLO, Triton Perf Analyzer `--binary-search` | Done, 78 tests |
| [Per-tenant rate limiter](dsa/concurrency/per-tenant-rate-limiter/) | Token bucket with lazy refill, a hash map of buckets per customer, a lock per customer | Claude API rate limits, Azure OpenAI quotas, .NET `TokenBucketRateLimiter`, Azure LLM inference trace | Done, 31 tests |
| [Top-k and top-p sampling](dsa/heaps/top-k-top-p-sampling/) | Size-k min-heap, heapify and pop, quickselect, stable softmax | vLLM sampler order, Hugging Face top-k / top-p warpers, nucleus sampling paper | Done, 67 tests |
| [Dynamic batcher](dsa/concurrency/dynamic-batcher/) | Bounded channel, one reader loop, a completion slot per caller, a frozen dictionary of models | Triton dynamic batcher, Hugging Face TEI, Clipper, NVIDIA BERT batch timings, Azure LLM inference trace | Done, 59 tests |

### System design

Full designs of AI systems, from the architecture down to failure modes, evaluation and responsible AI. *Coming soon.*

---

## Book

If you want more depth, From Algorithms to AI Systems (https://leanpub.com/fromalgorithmstoaisystems) takes each problem here further. It shows where the algorithm fits in real systems, traces it by hand, explains why each design choice beat the alternatives, and covers what reviews caught after the code first worked. The code in this repo stays free.

It's an ebook (PDF), published as it grows, and buyers get every new chapter as an update at no extra cost.

---

## Posts

I write one LinkedIn post a week about a problem in this repo. Each post links to its folder here, and each folder is listed below with its post.

| Post | Problem |
|---|---|
| [Agent loops vs agent plans](https://lnkd.in/p/dKuhFZhE) | [Agent plan executor](dsa/graphs/agent-plan-executor/) |
| [Prefix caching in LLM serving](https://lnkd.in/p/dp9yJrcK) | [Prefix-cache radix tree](dsa/trees-and-tries/prefix-cache-radix-tree/) |
| [Ordering docs that depend on each other](https://lnkd.in/p/dmkKHdxM) | [Learning path from a pile of docs](dsa/graphs/learning-path-from-docs/) |
| [Sizing an LLM endpoint with binary search](https://lnkd.in/p/dHHmqYjJ) | [Fewest GPUs that meet the SLO](dsa/search/fewest-gpus-for-slo/) |
| [Per-customer rate limits for an LLM API](https://lnkd.in/p/d75dfh3m) | [Per-tenant rate limiter](dsa/concurrency/per-tenant-rate-limiter/) |
| [How the serving engine picks each token](https://lnkd.in/p/dg4Kt4Ma) | [Top-k and top-p sampling](dsa/heaps/top-k-top-p-sampling/) |

---

## Roadmap

**Up next:** *Pack the context window*; *Near-duplicate detection in training data*.

The full plan, 25 problems and 12 system designs, is in **[ROADMAP.md](ROADMAP.md)**. Each DSA problem builds a component that a later design uses: the rate limiter and the retries become parts of the AI gateway, the radix tree and the batcher become parts of inference serving.

Problems are grouped by pattern, and each group is a folder under `dsa/`:

| Group | Patterns | Example AI problems |
|---|---|---|
| Graphs and DAGs | Topological sort, SCC, union-find, shortest paths | Agent plan execution (done), document prerequisite graphs (done), entity resolution, tool-call loop detection |
| Trees and tries | Trie, radix tree, segment tree, tree search | Prefix-cache radix tree with LRU (done), tokenizer prefix match, beam search |
| Heaps and top-K | Heaps, k-way merge, streaming quantiles | Top-k / top-p sampling (done), merging shard results, p99 latency |
| Hashing and caching | LRU/LFU with expiry, consistent hashing, Bloom, MinHash | Semantic cache, session affinity, training-data dedup |
| Search | kNN, IVF/HNSW, binary search on the answer | Vector search, the fewest GPUs that meet an SLO (done) |
| DP and strings | Knapsack, edit distance, Viterbi | Packing the context window, word error rate, chunking |
| Optimisation | Greedy, bin packing, assignment, scheduling | Placing models on GPUs, continuous batching |
| Concurrency | Token buckets, locks, channels, semaphores, coalescing, circuit breakers | Per-tenant rate limiter (done), dynamic batcher (done), single-flight cache |
| Streaming and parsing | State machines, incremental parsers | Streaming tokens over SSE, parsing tool-call JSON as it arrives |
| ML from scratch | Numerically stable maths | Softmax, attention with a KV cache, BM25, k-means |

---

## Run it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

Each problem folder has its own solution and tests. For example:

```bash
cd dsa/graphs/agent-plan-executor
dotnet test
```

---

## A note on sources

Company examples come from public documentation, papers and engineering posts, linked in each problem. Internal systems change over time; describe them as *"publicly described as..."*. The designs here are original work, not any company's internal system.

## License

Code: [MIT](LICENSE). Written content and diagrams: [CC BY 4.0](LICENSE-content)
