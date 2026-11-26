# Dynamic Batcher
### Send a batch at N requests or when the oldest has waited T, whichever comes first

**Tracks:** Concurrency (bounded queue, one reader loop, timers, a completion slot per caller), hashing (a frozen dictionary of model -> batcher)
**Code:** `src/DynamicBatching/`. **Tests:** 59, run with `dotnet test`. **Experiments:** `experiments/`
**Book:** this topic will become a chapter of [From Algorithms to AI Systems](https://leanpub.com/fromalgorithmstoaisystems), with the step-by-step walkthrough and the design decisions.

This is learning code, not production code. It's short on purpose: the problem, what I built, what I measured, and what I assumed.

---

## The problem

A model server gets requests one at a time. A GPU does far more work per pass on a batch. NVIDIA's GPU guide puts it plainly: "a fully-connected layer applied to a single vector (a tensor for a mini-batch of size 1)) is memory limited." [1]

So the server holds requests for a moment and runs them together. It sends a batch when it has **N** requests, or when the oldest one has waited **T**. Each caller still gets back its own answer.

## Where this runs

- **Triton** has the same rule. On the delay: "If the delay expires the dynamic batcher sends the batch as is, even though it is not a maximum or preferred size." Its advice is to "Try increasing delay values until the latency budget is exceeded to see the impact on throughput." [2]
- **Embedding and reranker servers.** Hugging Face TEI: "TEI incorporates token-based dynamic batching thus optimizing resource utilization during inference." [3]
- **Not LLM text generation.** A generated answer takes many passes, one token each. The Orca paper: "We argue that the current request-level scheduling mechanism cannot efficiently handle workloads with multi-iteration characteristic." [4] LLMs use continuous batching instead, a later topic.

## What I built

| Piece | Data structure | Job |
|---|---|---|
| The queue | Bounded `Channel<T>`, many writers, one reader | Requests wait in arrival order. When it's full, the request is turned away at once (`QueueFullException`) |
| The loop | One task, a `List<T>` for the batch | Takes the oldest request, then keeps taking until N, or until that request has waited T |
| The answer slot | One `TaskCompletionSource` per request | Hands result i back to caller i |
| Many models | `FrozenDictionary` of model name -> batcher | A batch only ever holds one model's requests. An unknown model fails at once |
| The clock | `TimeProvider` | Tests use a fake clock, so no test sleeps |

A few choices worth knowing:

- **T is measured from the oldest request in the batch**, not from when the batch started.
- **A caller that cancels before its batch runs is dropped** from the batch. The others aren't affected.
- **If `runBatch` throws, every caller in that batch gets the exception.** The loop keeps going.
- **Shutting down sends what's already queued** before it stops.

LeetCode with the same structures: 1188 Design Bounded Blocking Queue, 622 Design Circular Queue, 933 Number of Recent Calls.

## What I measured

### 1. Is batching worth it? (simulated)

> **Simulated.** No GPU was used. The cost of a batch comes from NVIDIA's published table for BERT Base on an A100 (FP16, sequence length 128) [5], with a straight line between its rows. NVIDIA notes those times "do not include the time required to copy inputs to the device and copy outputs to the host." The batcher is my real code, run on a fake clock against a simulated GPU that runs one batch at a time. Arrivals are random (Poisson), or the busiest hour of the Azure LLM inference trace 2024 [6], arrival times only. That trace is LLM traffic, not embeddings. I couldn't find a public trace of an embedding server.

**Throughput against N, under overload**

| N | 1 | 8 | 32 | 64 | 128 |
|---|---|---|---|---|---|
| Requests per second | 1,587 | 6,106 | 8,221 | 8,442 | 8,398 |

Batching buys about 5x. Past 64 it stops helping.

**Latency at 2,000 requests per second**

| N | T | Mean batch | p50 | p99 | GPU busy |
|---|---|---|---|---|---|
| 1 | 0 | 1.0 | 1,341 ms and growing | 2,722 ms | 100% |
| 32 | 0 | 1.7 | 1.06 ms | 1.75 ms | 85% |
| 32 | 2 ms | 5.1 | 2.37 ms | 3.41 ms | 41% |
| 32 | 5 ms | 11.2 | 4.50 ms | 7.06 ms | 29% |

**The surprise.** In every run, N = 32 with **T = 0** had the lowest latency. While the GPU is busy, requests pile up anyway, so batches form with no wait at all. A T above 0 never lowered latency here. What it did was free GPU time: at 1,000 requests per second, T = 2 ms cut GPU busy from 56% to 28%.

That isn't true for every model. Clipper found that the gain depends on "the ratio of the fixed cost for sending a batch to the variable cost of increasing the size of a batch." For one model, "a 2ms batch delay provides a 3.3x improvement in throughput", and "Delayed batching provides no increase in throughput for the Spark SVM" [7]. My runs are one model, one GPU, one cost curve.

On the busiest hour of the Azure trace sped up 10x, N = 32 with T = 0 had a p99 of 2.05 ms against 7.23 ms with no batching. All four experiments are in `experiments/results/results.md`.

### 2. Does the real clock honour T? (measured)

I ran the batcher on the real clock of one Windows machine, one request at a time, so only T could close a batch. Batches **closed before T**: 61 of 200 at T = 10 ms, the shortest after 0.81 ms.

The cause is in .NET's Windows timer code. It reads `QueryUnbiasedInterruptTime` [8]. Microsoft says that count "is incremented at each clock interrupt by the length of a clock tick" [9], and the tick is "typically in the range of 0.5 milliseconds to 15.625 milliseconds, depending on the hardware platform" [10]. So a timer can fire early. My loop trusted the timer and never checked its own clock.

**The fix.** When the timer fires, the loop checks the batcher's own clock again, and only closes the batch once T has really passed.

| T asked | Closed before T, before the fix | After the fix | p50 waited, after the fix |
|---|---|---|---|
| 1 ms | 198 of 200 | 0 of 200 | 1.02 ms |
| 2 ms | 12 of 200 | 0 of 200 | 11.32 ms |
| 10 ms | 61 of 200 | 0 of 200 | 15.13 ms |
| 20 ms | 22 of 200 | 0 of 200 | 28.78 ms |

It's never early now, but it's still late, by up to about a tick. On this machine, a T of 2 ms is really about 11 ms. The fix has a cost: at T = 1 ms the loop set about 35 timers per request before the millisecond passed. Full tables, and the machine, are in `experiments/results/real-clock-windows.md`.

## What this shows and what it doesn't

- **Shows:** the batcher keeps its rule (at most N, never sent before T unless full or shutting down, every caller gets its own answer), checked against a simple model of the rule on 1,000 random runs.
- **Shows:** how much batching buys on NVIDIA's BERT curve, and what T costs.
- **Doesn't show:** a real GPU, real embedding traffic, or a GPU shared between models.
- **Doesn't show:** any OS other than Windows. Other machines have other tick lengths.
- **Not covered:** batching by tokens (like TEI), priorities, or deadlines per request.

## Run it

```bash
cd dsa/concurrency/dynamic-batcher
dotnet test
dotnet run -c Release --project experiments/DynamicBatching.Experiments -- --out experiments/results
dotnet run -c Release --project experiments/DynamicBatching.Experiments -- --real-clock --out experiments/results
```

The Azure experiment needs the trace, which isn't in this repo. Download `AzureLLMInferenceTrace_code_1week.csv` from the [dataset page](https://github.com/Azure/AzurePublicDataset/blob/master/AzureLLMInferenceDataset2024.md) and add `--trace <path to the file>`.

Requires .NET 10 SDK.

## Sources

1. NVIDIA, GPU Performance Background User's Guide: https://docs.nvidia.com/deeplearning/performance/dl-performance-gpu-background/index.html
2. NVIDIA Triton Inference Server, Dynamic Batcher: https://docs.nvidia.com/deeplearning/triton-inference-server/user-guide/docs/user_guide/batcher.html
3. Hugging Face, Text Embeddings Inference: https://huggingface.co/docs/text-embeddings-inference/index
4. Yu, Jeong, Kim, Kim, Chun. *Orca: A Distributed Serving System for Transformer-Based Generative Models.* OSDI 2022. https://www.usenix.org/system/files/osdi22-yu.pdf
5. NVIDIA TensorRT, BERT demo README (release/10.0, A100 40GB, BERT Base): https://github.com/NVIDIA/TensorRT/blob/release/10.0/demo/BERT/README.md
6. Microsoft Azure, [Azure LLM Inference Dataset 2024](https://github.com/Azure/AzurePublicDataset/blob/master/AzureLLMInferenceDataset2024.md), CC-BY. Its paper is DynamoLLM (Stojkovic et al., HPCA 2025): https://arxiv.org/abs/2408.00741. Data not included here.
7. Crankshaw, Wang, Zhou, Franklin, Gonzalez, Stoica. *Clipper: A Low-Latency Online Prediction Serving System.* NSDI 2017. https://www.usenix.org/system/files/conference/nsdi17/nsdi17-crankshaw.pdf
8. .NET runtime, `TimerQueue.Windows.cs` (release/10.0): https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.Private.CoreLib/src/System/Threading/TimerQueue.Windows.cs
9. Microsoft, QueryUnbiasedInterruptTime: https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttime
10. Microsoft, Interrupt Time: https://learn.microsoft.com/en-us/windows/win32/sysinfo/interrupt-time

*Quotes checked on 26 November 2026.*
