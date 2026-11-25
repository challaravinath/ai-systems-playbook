# Dynamic batcher experiments (simulated)

Batch cost: BERT Base, A100 (NVIDIA). Latency = time from arrival until the batch holding the request finishes on the GPU.
Poisson runs: 20,000 requests, seed 1. One GPU, one batch at a time.

## 1. Throughput vs batch size, under overload (Poisson 20,000/s)

| N | Throughput (req/s) | From the table: N / cost(N) | Mean batch |
|---|---|---|---|
| 1 | 1,587 | 1,587 | 1.0 |
| 2 | 2,740 | 2,740 | 2.0 |
| 4 | 4,255 | 4,255 | 4.0 |
| 8 | 6,106 | 6,107 | 8.0 |
| 16 | 7,765 | 7,767 | 16.0 |
| 32 | 8,221 | 8,226 | 32.0 |
| 64 | 8,442 | 8,454 | 63.9 |
| 128 | 8,398 | 8,416 | 127.4 |

## 2. Latency vs wait T, under light load (Poisson 200/s, N = 32)

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 32 | 0 | 1.0 | 0.63 | 1.23 | 203 | 13% |
| 32 | 1 | 1.2 | 1.63 | 1.84 | 203 | 11% |
| 32 | 2 | 1.4 | 2.63 | 2.84 | 203 | 10% |
| 32 | 5 | 2.0 | 5.63 | 6.03 | 203 | 7% |
| 32 | 10 | 3.0 | 8.39 | 11.13 | 203 | 6% |

## 3. Load sweep (Poisson): no batching vs batching with and without a wait

**200 requests/s**

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 | 0 | 1.0 | 0.63 | 1.26 | 203 | 13% |
| 32 | 0 | 1.0 | 0.63 | 1.23 | 203 | 13% |
| 32 | 2 | 1.4 | 2.63 | 2.84 | 203 | 10% |
| 32 | 5 | 2.0 | 5.63 | 6.03 | 203 | 7% |

**1,000 requests/s**

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 | 0 | 1.0 | 0.97 | 3.95 | 1,016 | 64% |
| 32 | 0 | 1.2 | 0.75 | 1.46 | 1,016 | 56% |
| 32 | 2 | 3.0 | 2.43 | 3.13 | 1,016 | 28% |
| 32 | 5 | 6.1 | 4.22 | 6.51 | 1,016 | 19% |

**1,500 requests/s**

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 | 0 | 1.0 | 6.03 | 24.98 | 1,523 | 96% |
| 32 | 0 | 1.4 | 0.95 | 1.59 | 1,524 | 74% |
| 32 | 2 | 4.1 | 2.36 | 3.31 | 1,524 | 35% |
| 32 | 5 | 8.6 | 4.31 | 6.80 | 1,524 | 24% |

**2,000 requests/s**

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 (can't keep up) | 0 | 1.0 | 1341.08 | 2721.57 | 1,587 | 100% |
| 32 | 0 | 1.7 | 1.06 | 1.75 | 2,032 | 85% |
| 32 | 2 | 5.1 | 2.37 | 3.41 | 2,032 | 41% |
| 32 | 5 | 11.2 | 4.50 | 7.06 | 2,032 | 29% |

**4,000 requests/s**

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 (can't keep up) | 0 | 1.0 | 3820.54 | 7597.87 | 1,587 | 100% |
| 32 | 0 | 3.7 | 1.45 | 2.52 | 4,063 | 99% |
| 32 | 2 | 9.1 | 2.65 | 3.89 | 4,063 | 63% |
| 32 | 5 | 21.3 | 5.43 | 8.35 | 4,060 | 51% |

**6,000 requests/s**

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 (can't keep up) | 0 | 1.0 | 4647.03 | 9223.25 | 1,587 | 100% |
| 32 | 0 | 8.1 | 2.11 | 4.05 | 6,093 | 100% |
| 32 | 2 | 13.6 | 3.05 | 4.91 | 6,092 | 83% |
| 32 | 5 | 29.5 | 6.05 | 8.67 | 6,086 | 75% |

**7,500 requests/s**

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 (can't keep up) | 0 | 1.0 | 4977.62 | 9873.40 | 1,587 | 100% |
| 32 | 0 | 17.2 | 3.63 | 7.51 | 7,610 | 100% |
| 32 | 2 | 20.6 | 4.11 | 7.77 | 7,610 | 97% |
| 32 | 5 | 31.6 | 6.28 | 9.05 | 7,606 | 93% |

## 4. Azure LLM inference trace 2024, busiest hour (arrival times only)

Trace: AzureLLMInferenceTrace_code_1week.csv, 16,803,695 requests in the week. Busiest hour: 2024-05-15 19:00 UTC, 301,105 requests (83.6/s on average, busiest second 142).

**As recorded**

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 | 0 | 1.0 | 0.63 | 2.20 | 84 | 5% |
| 32 | 0 | 1.1 | 0.63 | 1.33 | 84 | 5% |
| 32 | 2 | 2.1 | 2.63 | 3.03 | 84 | 3% |
| 32 | 5 | 3.0 | 5.21 | 6.22 | 84 | 2% |

**Sped up 10x** (simulated load: busiest second 1,074)

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 | 0 | 1.0 | 1.86 | 7.23 | 836 | 53% |
| 32 | 0 | 2.0 | 1.20 | 2.05 | 836 | 30% |
| 32 | 2 | 4.6 | 2.79 | 3.71 | 836 | 18% |
| 32 | 5 | 7.1 | 5.67 | 7.06 | 836 | 14% |

**Sped up 50x** (simulated load: busiest second 4,867)

| N | T (ms) | Mean batch | p50 (ms) | p99 (ms) | Throughput (req/s) | GPU busy |
|---|---|---|---|---|---|---|
| 1 (can't keep up) | 0 | 1.0 | 59068.03 | 116532.24 | 1,587 | 100% |
| 32 | 0 | 4.6 | 1.70 | 3.00 | 4,182 | 90% |
| 32 | 2 | 10.5 | 3.26 | 4.83 | 4,182 | 62% |
| 32 | 5 | 22.3 | 5.63 | 8.71 | 4,182 | 53% |
