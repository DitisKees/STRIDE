using STRIDE.Abstractions;
using System.Runtime.InteropServices;

namespace STRIDE.Blocks;

[StrideBlock("TransformFilter")]
public sealed partial class TransformFilterBlock(string when) : ITransformBlock
{
    private readonly ExpressionEvaluator _evaluator = ExpressionEvaluator.Compile(when);

    public bool IsBlocking => false;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
        => inputSchemas["in"];

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            yield break;
        }

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                if (batch.RowCount == 0)
                {
                    continue;
                }

                var selected = new List<int>(batch.RowCount);
                for (var row = 0; row < batch.RowCount; row++)
                {
                    if (_evaluator.Evaluate(batch, row))
                    {
                        selected.Add(row);
                    }
                }

                if (selected.Count == 0)
                {
                    continue;
                }

                if (batch is RecordBatch recordBatch)
                {
                    yield return recordBatch.SelectRows(CollectionsMarshal.AsSpan(selected));
                }
                else
                {
                    var rows = new string[selected.Count][];
                    for (var i = 0; i < selected.Count; i++)
                    {
                        rows[i] = new string[batch.Schema.Fields.Length];
                        var sourceRow = selected[i];
                        for (var c = 0; c < batch.Schema.Fields.Length; c++)
                        {
                            rows[i][c] = batch.GetValueAsString(c, sourceRow);
                        }
                    }

                    yield return RecordBatch.FromRows(batch.Schema, rows);
                }
            }
        }
    }
}
