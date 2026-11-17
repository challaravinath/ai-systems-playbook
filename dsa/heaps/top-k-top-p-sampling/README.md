# Top-k and Top-p Sampling
### Picking the next token: the k best, or the smallest set that covers p

**Tracks:** Heaps (size-k min-heap, heapify and pop), selection (quickselect), numerically stable softmax
**Code:** `src/TokenSampling/TopK.cs`, `src/TokenSampling/Sampler.cs`. **Tests:** 67, run with `dotnet test`
**Book:** this topic will become a chapter of [From Algorithms to AI Systems](https://leanpub.com/fromalgorithmstoaisystems), with the step-by-step walkthrough and the design decisions.

This one is short on purpose: the problem, what I built, what I measured, and what I assumed.

---

## The problem

Each time a language model writes a token, it gives a score (a logit) to every token in its vocabulary, often 100,000 or more. The serving engine turns the scores into probabilities and picks one. It does this for every token of every request, so the step has to be cheap.

Two common filters decide which tokens are allowed:

- **Top-k:** keep only the k most likely tokens.
- **Top-p (nucleus sampling):** keep the smallest set of most likely tokens whose probabilities add up to p or more. Hugging Face's `TopPLogitsWarper` puts it as "the smallest set of most probable tokens with probabilities that add up to `top_p` or higher" [3].

Holtzman et al. introduced top-p to sample "from the dynamic nucleus of the probability distribution", "truncating the less reliable tail" [1]. A fixed k can't do that: when the model is sure, k is too many; when it isn't, k is too few.

## Where this step runs

In vLLM's sampler the order is documented: "b) Apply temperature. c) Apply logit processors which are argmax-invariant, by default the min_p processor. d) Apply top_k and/or top_p. e) Sample the next tokens with the probability distribution." [2]. vLLM's `top_k` is off at 0 or -1: "Set to 0 (or -1) to consider all tokens" [2].

I follow the same order, and top-p is computed over the top-k survivors, renormalised.

One thing I didn't expect: hosted APIs are taking these settings away. Claude's docs say "The `temperature`, `top_p`, and `top_k` sampling parameters are not supported on Claude 4.7 and later models ... Setting them to a non-default value returns a 400 error." [4]. So this is code you own when you run your own serving engine.

## The example

The hiring assistant from the earlier topics, one level down. It sends two kinds of request to the same model:

1. **Pull a fact from a CV** (skills, years). Greedy: always the best token.
2. **Write the brief for the panel.** Temperature 0.8, top-k 50, top-p 0.9.

Hugging Face's guide gives the reason for the split: "Greedy search works well for tasks with relatively short outputs where creativity is not a priority" and "Sampling strategies reduce repetition and can generate more creative and diverse outputs." [5]. **The exact numbers (0.8, 50, 0.9) are my choice**, not a recommendation from any source.

Both kinds of request share one batch, so every row carries its own settings (`SampleBatch`).

## What I built

| Step | How | Cost for V tokens |
|---|---|---|
| Find the k best | **Full sort** (the test oracle) | O(V log V) |
| | **Min-heap of size k**: the root is the worst token kept, so a new token only has to beat the root | O(V log k) |
| | **Quickselect**: partition until the k-th best lands at position k - 1, then sort those k | O(V + k log k) on average |
| Top-p with no top-k | **Max-heap**: heapify all V in O(V), then pop only until the nucleus reaches p | O(V + m log V) for m tokens kept |
| Probabilities | **Stable softmax**: subtract the largest logit first, so `e^x` can't overflow | O(V) |

A few choices worth knowing:

- **Ties go to the lower token id, and top-k keeps exactly k.** That makes the order total, so all three methods return exactly the same tokens in the same order, and the tests can compare them one to one. **This is my rule, not a standard one.** Hugging Face removes only tokens scoring below the k-th score (`scores < torch.topk(scores, top_k)[0][..., -1, None]` [6]), so it keeps every token tied with the k-th and can keep more than k.
- **Why the heap wins in practice.** After the first k tokens, a new token only costs work if it beats the root. When the tokens arrive in random order, that happens about k ln(V/k) times, not V times (token i makes it into the top k so far with probability k/i). Most tokens are one comparison and done.
- **Temperature doesn't change which tokens top-k keeps.** It divides every logit by the same positive number. So top-k runs on the raw logits, and temperature only changes the probabilities.
- **Quickselect can't do top-p alone.** It needs k up front, and top-p doesn't know its size until it has added up enough probability. That case uses the heap.
- **Tokens that can't be drawn are left out:** masked tokens (logit `-infinity`), and tokens whose probability rounds to exactly 0 in a double (logit 0 next to logit 10 at temperature 0.01 is e^-1000). NaN and `+infinity` are rejected, because NaN breaks the order and the three methods would disagree.

**Checked before publishing:** an independent review found two small bugs (a NaN could reach `TopK.Select`, and a token with probability 0 could stay in the list) and pointed out that "the same order as vLLM" covers the order of the steps, not how ties at the k-th place are cut. Both bugs are fixed, each with a test, and the tie rule is now stated above. Then every guard was broken on purpose, one at a time, to make sure a test fails: 24 of 24 were caught.

## What I measured

> **Simulated.** Every row of scores below is made up, not taken from a model. My assumption: probability falls off with rank like a power law, p(rank r) proportional to r^-s, with token ids shuffled. Larger s means a peaked distribution (the model is sure), smaller s a flat one. The sizes in tables 2 and 3 depend entirely on that shape. Times are one CPU thread in C#; real engines run this step on the GPU, over a whole batch at once. Read the times as a comparison between methods, nothing more.

**1. Finding the top k: time per row** (median of 31 runs)

| Vocabulary | k | Full sort | Heap | Quickselect |
|---|---|---|---|---|
| 32,000 | 50 | 4.2 ms | 0.12 ms | 0.26 ms |
| 32,000 | 1,000 | 4.0 ms | 0.42 ms | 0.33 ms |
| 128,000 | 50 | 11.8 ms | 0.31 ms | 1.08 ms |
| 128,000 | 1,000 | 11.2 ms | 0.61 ms | 0.98 ms |
| 256,000 | 50 | 23.5 ms | 0.51 ms | 2.58 ms |
| 256,000 | 1,000 | 22.8 ms | 0.92 ms | 2.66 ms |

I expected quickselect to win, since it's O(V) on average. The heap won 5 of the 6 rows. Quickselect only won at the smallest vocabulary with the larger k.

**2. Top-p alone (p = 0.9), vocabulary 128,000.** Both columns include two O(V) passes (check the row, add up the total).

| Shape | Tokens kept | Full sort | Heap, popping only the nucleus |
|---|---|---|---|
| Peaked (s = 1.6) | 32 | 12.8 ms | 2.5 ms |
| Medium (s = 1.1) | 15,704 | 13.7 ms | 6.3 ms |
| Flat (s = 0.8) | 75,561 | 19.6 ms | 15.9 ms |

The heap helps most when the model is sure. When the nucleus is most of the vocabulary, it's close to a full sort anyway.

**3. One fixed k against the shape of the distribution,** vocabulary 128,000

| Shape | Probability the top 50 cover | Tokens for p = 0.9 | Tokens for p = 0.95 |
|---|---|---|---|
| Peaked (s = 1.6) | 93.2% | 27 | 85 |
| Medium (s = 1.1) | 50.8% | 14,231 | 39,502 |
| Flat (s = 0.8) | 13.5% | 75,134 | 96,789 |

This is the paper's argument [1], seen on made-up rows: k = 50 is more than enough in one case and covers 13.5% in another.

**4. The order matters.** Top-k 50 then top-p 0.9 (vLLM's order) against top-p then top-k, on 1,000 rows: the kept set was different in all 1,000. On average 24.7 tokens against 43.4, because after top-k the probabilities are renormalised over just 50 tokens, and 0.9 of that is reached sooner.

**5. Temperature moves the nucleus a lot.** Tokens kept at p = 0.9 (s = 1.1, vocabulary 128,000): temperature 0.5 keeps 4, 0.7 keeps 35, 1.0 keeps 14,211, 1.3 keeps 69,666. Top-p and temperature aren't independent knobs.

**6. One step for a batch of 64 requests** with mixed settings (half greedy, half temperature 0.8, top-k 50, top-p 0.9), vocabulary 128,000: full sort 361 ms, heap 22 ms, quickselect 49 ms. Half the rows are greedy, which costs the same for every method.

## What this shows and what it doesn't

- **Shows:** the three ways to pick the top k agree exactly (tested against a plain LINQ oracle on thousands of random rows full of ties), and how their costs compare on one CPU thread.
- **Shows:** a fixed k and a fixed p behave very differently as the distribution changes shape, and temperature moves the nucleus size a lot.
- **Doesn't show:** real model distributions. Real ones may be much more peaked than my "flat" rows. It also doesn't show GPU performance, or anything about output quality.
- **Not covered:** min-p, repetition penalties, beam search, and the GPU kernels engines actually use.

## Run it

```bash
cd dsa/heaps/top-k-top-p-sampling
dotnet test
dotnet run -c Release --project demo/TokenSampling.Experiments
```

Requires .NET 10 SDK.

## Sources

1. Holtzman, Buys, Du, Forbes, Choi. *The Curious Case of Neural Text Degeneration.* ICLR 2020. https://arxiv.org/abs/1904.09751
2. vLLM, `Sampler` (v0.22.1): https://docs.vllm.ai/en/v0.22.1/api/vllm/v1/sample/sampler/ and `SamplingParams`: https://docs.vllm.ai/en/latest/api/vllm/sampling_params.html
3. Hugging Face Transformers, `TopKLogitsWarper` and `TopPLogitsWarper`: https://huggingface.co/docs/transformers/main/en/internal/generation_utils
4. Claude docs, Using the Messages API: https://platform.claude.com/docs/en/build-with-claude/working-with-messages
5. Hugging Face Transformers, Generation strategies: https://huggingface.co/docs/transformers/generation_strategies
6. Hugging Face Transformers, `logits_process.py` (`TopKLogitsWarper.__call__`): https://github.com/huggingface/transformers/blob/main/src/transformers/generation/logits_process.py

*Quotes checked on 17 November 2026.*
