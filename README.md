# AI Systems Playbook

**Data structures, algorithms and system design, worked through the way real AI systems use them.**

Coding rounds for senior and staff AI roles still test graphs, heaps, hashing, dynamic programming and concurrency. What has changed is the setting: *"run this agent's plan in parallel"*, *"evict from the prompt cache"*, *"pack retrieved chunks into the context window"*. This guide pairs each classic pattern with a real AI problem, shows where companies run it in production (with sources), and solves it in C# with tests that run.

[![tests](../../actions/workflows/test.yml/badge.svg)](../../actions/workflows/test.yml)

---

## What every problem contains

1. **The prompt**, as an interviewer would say it, with hints you can expand, so you can try it first.
2. **Where it runs in the real world:** which companies use this algorithm, for what, with links to their docs, papers or engineering posts.
3. **Clarifying questions**, and how each answer changes the design.
4. **The mental model:** the one idea that carries the solution.
5. **Brute force → optimal**, with complexity, and the trade-offs between designs.
6. **Invariants** to say out loud, which are what the person interviewing you is grading.
7. **C# code with tests that run**, each test named after the behaviour it protects.
8. **A ladder of follow-ups**, from scale to durability to distribution, with strong answers.
9. **From interview answer to production:** observability, failure, security.
10. **What each level shows** (shallow / senior / staff) and a 45-minute pacing plan.

Each problem folder also has a PDF of its README for offline reading.

---

## Contents

### Data structures and algorithms

| # | Problem | Patterns | Real-world anchor | Status |
|---|---|---|---|---|
| P01 | [Agent plan executor](dsa/track-01-graphs/p01-agent-dag-executor/) | Topological sort, cycle detection, DP on a DAG, bounded concurrency, cancellation | PyTorch autograd, LangGraph, Durable Functions, Temporal, Airflow, Bazel | ✅ 23 tests |

### System design

| Design | Focus | Status |
|---|---|---|
| [Talent Scout: agentic recruiting system](system-design/agentic-talent-dossier/) | LLM planner + checked DAG, durable orchestration, matching people to online accounts, prompt-injection quarantine, grounded generation, fairness and compliance | ✅ |

---

## Roadmap

Tracks planned, each with problems in the format above:

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

Company examples come from public documentation, papers and engineering posts, linked in each problem. Internal systems change over time; describe them as *"publicly described as…"*. The designs here are original study material, not any company's internal system or interview questions.

## License

Code: [MIT](LICENSE) · Written content and diagrams: [CC BY 4.0](LICENSE-content)
