# The real clock: how long a lone request waits for T

Measured on: Microsoft Windows 10.0.26200, .NET 10.0.12, 14 logical processors, Intel64 Family 6 Model 181 Stepping 0, GenuineIntel.
Date: 2026-11-26 03:27 UTC. Other machines, and the same machine under other load, give other numbers.

Each request is sent alone, after a random 0-20 ms pause, with N = 1000 so only T can close the batch.
Waited = from just before SubmitAsync until the batch function starts (Stopwatch). 200 requests per T, seed 1.

| T asked (ms) | Min | p50 | p99 | Max | Closed before T | Timers per request (mean / most) |
|---|---|---|---|---|---|---|
| 1 | 1.00 | 1.02 | 2.84 | 8.91 | 0 of 200 | 34.78 / 64 |
| 2 | 2.00 | 11.32 | 16.62 | 16.68 | 0 of 200 | 1.41 / 15 |
| 5 | 5.00 | 13.11 | 19.75 | 21.50 | 0 of 200 | 1.52 / 21 |
| 10 | 10.02 | 15.13 | 24.85 | 25.31 | 0 of 200 | 1.70 / 19 |
| 20 | 20.02 | 28.78 | 34.94 | 35.90 | 0 of 200 | 1.22 / 15 |

## Before the fix

The same program, same machine, same day, run against the batcher at commit 6937c49. That loop closed the batch as soon as its timer fired, without checking its own clock.

| T asked (ms) | Min | p50 | p99 | Max | Closed before T | Timers per request (mean / most) |
|---|---|---|---|---|---|---|
| 1 | 0.02 | 0.08 | 0.92 | 9.41 | 198 of 200 | 1.00 / 1 |
| 2 | 0.35 | 10.78 | 16.21 | 16.67 | 12 of 200 | 1.00 / 1 |
| 5 | 0.26 | 11.65 | 18.72 | 19.08 | 26 of 200 | 1.00 / 1 |
| 10 | 0.81 | 12.68 | 22.84 | 23.63 | 61 of 200 | 1.00 / 1 |
| 20 | 16.89 | 27.75 | 34.38 | 35.09 | 22 of 200 | 1.00 / 1 |
