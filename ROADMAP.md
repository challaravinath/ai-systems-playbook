# Roadmap

A living plan. Problems are built in roughly this order, and the order changes as I learn what's most useful. That's why they go by name, not by number.

**Status** is one of: Done, In progress, Planned.

**Next up:**
1. Learning path from a pile of docs
2. Per-tenant rate limiter
3. Dynamic batcher

**How the two halves connect:** each DSA problem builds a component that a system design uses. By the time a design is written, its hardest parts already exist here as tested code. The **Used in** column shows the link.

> The real-world anchors below are *starting points*. Each one is checked against public sources when its problem is built, and then cited in that problem's README.

---

## Data structures and algorithms

| Problem | Patterns | Real-world anchor | Used in | Status |
|---|---|---|---|---|
| [Agent plan executor](dsa/graphs/agent-plan-executor/) | Topological sort, cycle detection, DP on a DAG, bounded concurrency | PyTorch autograd, LangGraph, Durable Functions, Temporal, Airflow | Agent platform | Done |
| [Prefix-cache radix tree](dsa/trees-and-tries/prefix-cache-radix-tree/) | Radix tree / trie, LRU, reference counting | SGLang RadixAttention, vLLM automatic prefix caching | Inference serving | Done |
| Learning path from a pile of docs | SCC (Tarjan), condensation DAG, topological sort, ancestor/descendant queries | Microsoft GraphRAG, docs prerequisite graphs | Enterprise RAG | Planned |
| Per-tenant token-bucket rate limiter, thread-safe | Hashing, token bucket, lock-free counters | Tokens-per-minute and requests-per-minute limits in LLM APIs | AI gateway | Planned |
| Dynamic batcher: send at max size *or* max wait | Bounded channels, timers, back-pressure | NVIDIA Triton dynamic batching | Inference serving | Planned |
| Top-k and top-p sampling | Heaps, partial sort, numerically stable softmax | Hugging Face `generate` | Inference serving | Planned |
| Merge ranked results from N shards | k-way merge, reciprocal rank fusion | Hybrid search in Elasticsearch / Azure AI Search | Enterprise RAG | Planned |
| Semantic cache | LRU + TTL, similarity threshold, kNN | GPTCache, Azure API Management semantic caching | Semantic cache at scale | Planned |
| Session affinity with consistent hashing and bounded loads | Consistent hashing, load caps | Google's consistent hashing with bounded loads, prefix-aware routing | Inference serving | Planned |
| Pack the context window | 0/1 knapsack DP vs greedy, token budgets | RAG context assembly | Enterprise RAG | Planned |
| Single-flight: stop a cache stampede | Request coalescing, `TaskCompletionSource` | Go `singleflight`, CDN request collapsing | Semantic cache at scale | Planned |
| Near-duplicate detection in training data | MinHash, locality-sensitive hashing | Deduplication in LLM pre-training corpora | Embedding pipeline | Planned |
| Vector search: from brute force to IVF | Heaps, k-means, recall vs latency | FAISS | Embedding pipeline | Planned |
| Fewest GPUs that meet the SLO | Binary search on the answer | Capacity planning for inference | Inference serving | Planned |
| Continuous-batching scheduler with a KV-cache memory budget | Greedy scheduling, pre-emption | Orca, vLLM PagedAttention | Inference serving | Planned |
| Place models on GPUs | Bin packing (first-fit decreasing), approximation bounds | Multi-model serving | Adapter serving | Planned |
| Parse tool-call JSON while it streams | State machines, incremental parsing | Streaming function calling in LLM APIs | Agent platform | Planned |
| Token streaming with back-pressure and cancellation | `IAsyncEnumerable`, channels, SSE | Streaming chat completions | AI gateway | Planned |
| Circuit breaker, retries with jitter, hedged requests | Concurrency, state machines | "The Tail at Scale" (Google) | AI gateway | Planned |
| Resolve duplicate profiles | Union-find, blocking, transitive closure | Entity resolution | Agentic recruiting | Planned |
| Detect an agent stuck in a loop | Cycle detection on a state graph, hashing states | Agent recursion limits | Agent platform | Planned |
| Beam search decoding | Heaps, tree search, length normalisation | Sequence decoding | Inference serving | Planned |
| Sliding-window token usage per tenant | Ring buffer, Fenwick tree | Usage metering and billing | AI gateway | Planned |
| Attention with a KV cache, from scratch | Matrix maths, numerical stability | Transformer inference | Inference serving | Planned |
| Live p50/p99 latency | Two heaps, t-digest, streaming quantiles | SLO monitoring | Evaluation platform | Planned |

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
