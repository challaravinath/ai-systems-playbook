# Roadmap

A living plan. Problems are built in roughly this order, and the order changes as I learn what's most useful. That's why they go by name, not by number.

**Status** is one of: Done, In progress, Planned.

**Next up:**
1. Fewest GPUs that meet the SLO
2. Per-tenant rate limiter
3. Top-k / top-p sampling

**How the two halves connect:** each DSA problem builds a component that a system design uses. By the time a design is written, its hardest parts already exist here as tested code. The **Used in** column shows the link.

> Real-world uses are only named in a problem's own README, after the problem is built and every claim is checked against a public source.

---

## Data structures and algorithms

| Problem | Patterns | Used in | Status |
|---|---|---|---|
| [Agent plan executor](dsa/graphs/agent-plan-executor/) | Topological sort, cycle detection, DP on a DAG, bounded concurrency | Agent platform | Done |
| [Prefix-cache radix tree](dsa/trees-and-tries/prefix-cache-radix-tree/) | Radix tree / trie, LRU, reference counting | Inference serving | Done |
| [Learning path from a pile of docs](dsa/graphs/learning-path-from-docs/) | SCC (Tarjan, Kosaraju), condensation DAG, topological sort | Enterprise RAG | Done |
| Per-tenant token-bucket rate limiter, thread-safe | Hashing, token bucket, lock-free counters | AI gateway | Planned |
| Dynamic batcher: send at max size *or* max wait | Bounded channels, timers, back-pressure | Inference serving | Planned |
| Top-k and top-p sampling | Heaps, partial sort, numerically stable softmax | Inference serving | Planned |
| Merge ranked results from N shards | k-way merge, reciprocal rank fusion | Enterprise RAG | Planned |
| Semantic cache | LRU + TTL, similarity threshold, kNN | Semantic cache at scale | Planned |
| Session affinity with consistent hashing and bounded loads | Consistent hashing, load caps | Inference serving | Planned |
| Pack the context window | 0/1 knapsack DP vs greedy, token budgets | Enterprise RAG | Planned |
| Single-flight: stop a cache stampede | Request coalescing, `TaskCompletionSource` | Semantic cache at scale | Planned |
| Near-duplicate detection in training data | MinHash, locality-sensitive hashing | Embedding pipeline | Planned |
| Vector search: from brute force to IVF | Heaps, k-means, recall vs latency | Embedding pipeline | Planned |
| Fewest GPUs that meet the SLO | Binary search on the answer | Inference serving | Planned |
| Continuous-batching scheduler with a KV-cache memory budget | Greedy scheduling, pre-emption | Inference serving | Planned |
| Place models on GPUs | Bin packing (first-fit decreasing), approximation bounds | Adapter serving | Planned |
| Parse tool-call JSON while it streams | State machines, incremental parsing | Agent platform | Planned |
| Token streaming with back-pressure and cancellation | `IAsyncEnumerable`, channels, SSE | AI gateway | Planned |
| Circuit breaker, retries with jitter, hedged requests | Concurrency, state machines | AI gateway | Planned |
| Resolve duplicate profiles | Union-find, blocking, transitive closure | Agentic recruiting | Planned |
| Detect an agent stuck in a loop | Cycle detection on a state graph, hashing states | Agent platform | Planned |
| Beam search decoding | Heaps, tree search, length normalisation | Inference serving | Planned |
| Sliding-window token usage per tenant | Ring buffer, Fenwick tree | AI gateway | Planned |
| Attention with a KV cache, from scratch | Matrix maths, numerical stability | Inference serving | Planned |
| Live p50/p99 latency | Two heaps, t-digest, streaming quantiles | Evaluation platform | Planned |

---

## System design

| Design | Core questions | Built from | Status |
|---|---|---|---|
| **Inference serving:** LLM inference with cache-aware routing | Prefix-cache reuse vs load balance, batching, KV-cache memory, autoscaling, cost per token | Prefix-cache radix tree, dynamic batcher, sampling, consistent hashing, fewest GPUs, continuous batching, beam search, attention with a KV cache | Planned |
| **AI gateway** for LLM APIs | Quotas, routing and fallback across models and regions, streaming, cost metering, abuse | Rate limiter, token streaming, circuit breaker, sliding-window usage | Planned |
| **Enterprise RAG** over 100k documents | Ingestion DAG, hybrid retrieval, permissions, freshness, grounding, evaluation | Learning path from docs, merging shard results, packing the context window | Planned |
| **Agent platform** | Plans as DAGs, durable execution, tool sandboxing, human approval steps, prompt injection | Agent plan executor, streaming tool-call parser, stuck-agent detection | Planned |
| **Agentic recruiting** | Matching people to online accounts, untrusted web content, fairness, compliance | Agent plan executor, duplicate profiles | Planned |
| **Evaluation platform** for LLMs | Golden sets, LLM-as-judge, gating releases, drift | Live p50/p99 latency | Planned |
| **Semantic cache at scale** | Hit rate vs correctness, invalidation, tenant isolation | Semantic cache, single-flight | Planned |
| **Real-time voice agent** | Latency budget, barge-in, streaming at every stage | Token streaming | Planned |
| **Content safety pipeline** | Layered classifiers, latency, false positives, appeals | nothing yet | Planned |
| **Adapter serving:** multi-tenant fine-tuning | LoRA adapter swapping, isolation, scheduling | Placing models on GPUs | Planned |
| **Coding assistant** backend | Context retrieval from a codebase, latency, privacy | Merging shard results, packing the context window | Planned |
| **Embedding pipeline** and vector store | Backfills, re-embedding on model change, index build, dedup | Near-duplicate detection, vector search | Planned |

---

## Later

- **More DSA:** BM25 from scratch; k-means; mixture-of-experts token routing; speculative decoding acceptance; tokenizer (byte-pair encoding) merges; reservoir sampling for eval sets.
- **Practice sets:** a "mixed bag" of problems that don't say which pattern they need, so you learn to spot it.
