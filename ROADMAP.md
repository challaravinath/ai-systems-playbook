# Roadmap

A living plan. Problems are built in roughly this order; the order changes as I learn what's most useful.

**Status:** ✅ done · 🛠️ in progress · 📋 planned

**How the two halves connect:** each DSA problem builds a component that a system design uses. By the time a design is written, its hardest parts already exist here as tested code. The **Feeds** column shows the link.

> The real-world anchors below are *starting points*. Each one is checked against public sources when its problem is built, and then cited in that problem's README.

---

## Data structures and algorithms

| # | Problem | Patterns | Real-world anchor | Feeds | Status |
|---|---|---|---|---|---|
| P01 | [Agent plan executor](dsa/track-01-graphs/p01-agent-dag-executor/) | Topological sort, cycle detection, DP on a DAG, bounded concurrency | PyTorch autograd, LangGraph, Durable Functions, Temporal, Airflow | SD04 | ✅ |
| P02 | Learning path from a pile of docs | SCC (Tarjan), condensation DAG, topological sort, ancestor/descendant queries | Microsoft GraphRAG, docs prerequisite graphs | SD03 | 🛠️ |
| P03 | Prefix-cache radix tree with LRU eviction | Radix tree / trie, LRU, reference counting | SGLang RadixAttention, vLLM automatic prefix caching | SD01 | 📋 |
| P04 | Per-tenant token-bucket rate limiter, thread-safe | Hashing, token bucket, lock-free counters | Tokens-per-minute and requests-per-minute limits in LLM APIs | SD02 | 📋 |
| P05 | Dynamic batcher: send at max size *or* max wait | Bounded channels, timers, back-pressure | NVIDIA Triton dynamic batching | SD01 | 📋 |
| P06 | Top-k and top-p sampling | Heaps, partial sort, numerically stable softmax | Hugging Face `generate` | SD01 | 📋 |
| P07 | Merge ranked results from N shards | k-way merge, reciprocal rank fusion | Hybrid search in Elasticsearch / Azure AI Search | SD03 | 📋 |
| P08 | Semantic cache | LRU + TTL, similarity threshold, kNN | GPTCache, Azure API Management semantic caching | SD07 | 📋 |
| P09 | Session affinity with consistent hashing and bounded loads | Consistent hashing, load caps | Google's consistent hashing with bounded loads, prefix-aware routing | SD01 | 📋 |
| P10 | Pack the context window | 0/1 knapsack DP vs greedy, token budgets | RAG context assembly | SD03 | 📋 |
| P11 | Single-flight: stop a cache stampede | Request coalescing, `TaskCompletionSource` | Go `singleflight`, CDN request collapsing | SD07 | 📋 |
| P12 | Near-duplicate detection in training data | MinHash, locality-sensitive hashing | Deduplication in LLM pre-training corpora | SD12 | 📋 |
| P13 | Vector search: brute force → IVF | Heaps, k-means, recall vs latency | FAISS | SD12 | 📋 |
| P14 | Fewest GPUs that meet the SLO | Binary search on the answer | Capacity planning for inference | SD01 | 📋 |
| P15 | Continuous-batching scheduler with a KV-cache memory budget | Greedy scheduling, pre-emption | Orca, vLLM PagedAttention | SD01 | 📋 |
| P16 | Place models on GPUs | Bin packing (first-fit decreasing), approximation bounds | Multi-model serving | SD10 | 📋 |
| P17 | Parse tool-call JSON while it streams | State machines, incremental parsing | Streaming function calling in LLM APIs | SD04 | 📋 |
| P18 | Token streaming with back-pressure and cancellation | `IAsyncEnumerable`, channels, SSE | Streaming chat completions | SD02 | 📋 |
| P19 | Circuit breaker, retries with jitter, hedged requests | Concurrency, state machines | "The Tail at Scale" (Google) | SD02 | 📋 |
| P20 | Resolve duplicate profiles | Union-find, blocking, transitive closure | Entity resolution | SD05 | 📋 |
| P21 | Detect an agent stuck in a loop | Cycle detection on a state graph, hashing states | Agent recursion limits | SD04 | 📋 |
| P22 | Beam search decoding | Heaps, tree search, length normalisation | Sequence decoding | SD01 | 📋 |
| P23 | Sliding-window token usage per tenant | Ring buffer, Fenwick tree | Usage metering and billing | SD02 | 📋 |
| P24 | Attention with a KV cache, from scratch | Matrix maths, numerical stability | Transformer inference | SD01 | 📋 |
| P25 | Live p50/p99 latency | Two heaps, t-digest, streaming quantiles | SLO monitoring | SD06 | 📋 |

---

## System design

| # | Design | Core questions | Built from | Status |
|---|---|---|---|---|
| SD01 | LLM inference serving with cache-aware routing | Prefix-cache reuse vs load balance, batching, KV-cache memory, autoscaling, cost per token | P03, P05, P06, P09, P14, P15, P22, P24 | 📋 |
| SD02 | AI gateway for LLM APIs | Quotas, routing and fallback across models and regions, streaming, cost metering, abuse | P04, P18, P19, P23 | 📋 |
| SD03 | Enterprise RAG over 100k documents | Ingestion DAG, hybrid retrieval, permissions, freshness, grounding, evaluation | P02, P07, P10 | 📋 |
| SD04 | Agent platform | Plans as DAGs, durable execution, tool sandboxing, human approval steps, prompt injection | P01, P17, P21 | 📋 |
| SD05 | Agentic recruiting system | Matching people to online accounts, untrusted web content, fairness, compliance | P01, P20 | 📋 |
| SD06 | LLM evaluation and regression platform | Golden sets, LLM-as-judge, gating releases, drift | P25 | 📋 |
| SD07 | Semantic cache at scale | Hit rate vs correctness, invalidation, tenant isolation | P08, P11 | 📋 |
| SD08 | Real-time voice agent | Latency budget, barge-in, streaming at every stage | P18 | 📋 |
| SD09 | Content safety pipeline | Layered classifiers, latency, false positives, appeals | — | 📋 |
| SD10 | Multi-tenant fine-tuning and adapter serving | LoRA adapter swapping, isolation, scheduling | P16 | 📋 |
| SD11 | Coding assistant backend | Context retrieval from a codebase, latency, privacy | P07, P10 | 📋 |
| SD12 | Embedding pipeline and vector store | Backfills, re-embedding on model change, index build, dedup | P12, P13 | 📋 |

---

## Later

- **More DSA:** BM25 from scratch; k-means; mixture-of-experts token routing; speculative decoding acceptance; tokenizer (byte-pair encoding) merges; reservoir sampling for eval sets.
- **Practice sets:** a "mixed bag" of problems that don't say which pattern they need, so you learn to spot it.
