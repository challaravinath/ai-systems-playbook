using System.Collections.Frozen;

namespace DynamicBatching;

// One model on the server: its name, its own N / T / queue size, and how to run a batch on it.
public sealed record ModelSetup<TIn, TOut>(
    string Name,
    BatchOptions Options,
    Func<IReadOnlyList<TIn>, Task<IReadOnlyList<TOut>>> RunBatch);

// The request named a model the server doesn't have. A server would answer 404.
public sealed class UnknownModelException(string model)
    : Exception($"no model named '{model}'")
{
    public string Model { get; } = model;
}

// Several models on one server, each with its own batcher. A batch only ever goes to one model,
// and each model has its own loop, so a slow model never holds up the others.
//
// Models are set once, at startup. The map never changes after that, so it needs no locks:
// a FrozenDictionary is built for "read many times, never change".
// All models here share one input and output type (for example, several embedding models:
// text in, vector out). A reranker, with a different shape, would get its own ModelBatchers.
public sealed class ModelBatchers<TIn, TOut> : IAsyncDisposable
{
    private readonly FrozenDictionary<string, DynamicBatcher<TIn, TOut>> _models;

    public ModelBatchers(IEnumerable<ModelSetup<TIn, TOut>> models, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        var setups = models.ToList();
        if (setups.Count == 0)
        {
            throw new ArgumentException("give at least one model", nameof(models));
        }

        // Check every name before starting any batcher, so a bad list never leaves loops running.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setup in setups)
        {
            ArgumentNullException.ThrowIfNull(setup);
            ArgumentException.ThrowIfNullOrWhiteSpace(setup.Name, nameof(models));
            if (!names.Add(setup.Name))
            {
                throw new ArgumentException($"model '{setup.Name}' is listed twice", nameof(models));
            }
        }

        // Names match exactly: "Embed" and "embed" are different models.
        _models = setups.ToFrozenDictionary(
            s => s.Name,
            s => new DynamicBatcher<TIn, TOut>(s.Options, s.RunBatch, clock),
            StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Models => _models.Keys;

    public Task<TOut> SubmitAsync(string model, TIn input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        return _models.TryGetValue(model, out var batcher)
            ? batcher.SubmitAsync(input, cancellationToken)
            : Task.FromException<TOut>(new UnknownModelException(model));
    }

    // Drains every model at the same time, so shutting down takes as long as the slowest model, not the sum.
    public async ValueTask DisposeAsync() =>
        await Task.WhenAll(_models.Values.Select(b => b.DisposeAsync().AsTask())).ConfigureAwait(false);

    // For tests only: lets them wait until every model's loop is idle.
    internal IEnumerable<DynamicBatcher<TIn, TOut>> Batchers => _models.Values;
}
