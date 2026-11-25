namespace DynamicBatching.Experiments;

// How long one batch takes on the GPU, by batch size. We have no GPU, so the numbers come from
// a published table, and sizes between two rows are read off the straight line between them.
public sealed class BatchCost
{
    private readonly (int Size, double Ms)[] _rows;

    public BatchCost(string name, IEnumerable<(int Size, double Ms)> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rows);
        _rows = rows.OrderBy(r => r.Size).ToArray();
        if (_rows.Length == 0 || _rows[0].Size != 1)
        {
            throw new ArgumentException("the table must start at batch size 1", nameof(rows));
        }
        for (int i = 1; i < _rows.Length; i++)
        {
            if (_rows[i].Size == _rows[i - 1].Size || _rows[i].Ms <= 0)
            {
                throw new ArgumentException("sizes must be distinct and times positive", nameof(rows));
            }
        }
        Name = name;
    }

    // NVIDIA TensorRT BERT demo README (release/10.0 branch): BERT Base, sequence length 128, FP16,
    // A100 40GB, average latency in ms. Measured by NVIDIA, not by us.
    public static BatchCost BertBaseA100 { get; } = new("BERT Base, A100 (NVIDIA)",
    [
        (1, 0.63), (2, 0.73), (4, 0.94), (8, 1.31), (12, 1.71),
        (16, 2.06), (24, 3.02), (32, 3.89), (64, 7.57), (128, 15.21),
    ]);

    public string Name { get; }
    public int MaxBatchSize => _rows[^1].Size;

    public double Ms(int batchSize)
    {
        if (batchSize < 1 || batchSize > MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), $"the table covers 1 to {MaxBatchSize}");
        }

        int i = 0;
        while (_rows[i].Size < batchSize)
        {
            i++;
        }
        if (_rows[i].Size == batchSize)
        {
            return _rows[i].Ms;
        }

        // Between two rows: the straight line through them.
        var (s0, m0) = _rows[i - 1];
        var (s1, m1) = _rows[i];
        return m0 + (m1 - m0) * (batchSize - s0) / (s1 - s0);
    }
}
