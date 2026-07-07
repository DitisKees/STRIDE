using STRIDE.Abstractions;
using System.Runtime.InteropServices;

namespace STRIDE.Blocks;

[StrideBlock("TransformConditionalSplitter")]
public sealed class TransformConditionalSplitterBlock(string branches, char delimiter = ';') : ITransformBlock
{
    private readonly SplitterBranch[] _branches = ParseBranches(branches, delimiter);

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

                var rowIndexesByPort = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var branch in _branches)
                {
                    rowIndexesByPort[branch.Name] = new List<int>();
                }

                var defaultRows = new List<int>();

                for (var row = 0; row < batch.RowCount; row++)
                {
                    var matched = false;
                    foreach (var branch in _branches)
                    {
                        if (branch.Predicate.Evaluate(batch, row))
                        {
                            rowIndexesByPort[branch.Name].Add(row);
                            matched = true;
                            break;
                        }
                    }

                    if (!matched)
                    {
                        defaultRows.Add(row);
                    }
                }

                foreach (var (port, rows) in rowIndexesByPort)
                {
                    if (rows.Count == 0)
                    {
                        continue;
                    }

                    if (!context.Outputs.TryGetValue(port, out var output))
                    {
                        continue;
                    }

                    var slice = SliceRows(batch, CollectionsMarshal.AsSpan(rows));
                    await output.WriteAsync(slice, cancellationToken).ConfigureAwait(false);
                }

                if (defaultRows.Count > 0 && context.Outputs.TryGetValue("out", out var defaultOutput))
                {
                    var slice = SliceRows(batch, CollectionsMarshal.AsSpan(defaultRows));
                    await defaultOutput.WriteAsync(slice, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static IRecordBatch SliceRows(IRecordBatch batch, ReadOnlySpan<int> selectedRows)
    {
        if (batch is RecordBatch recordBatch)
        {
            return recordBatch.SelectRows(selectedRows);
        }

        var rows = new string[selectedRows.Length][];
        for (var i = 0; i < selectedRows.Length; i++)
        {
            rows[i] = new string[batch.Schema.Fields.Length];
            for (var col = 0; col < batch.Schema.Fields.Length; col++)
            {
                rows[i][col] = batch.GetValueAsString(col, selectedRows[i]);
            }
        }

        return RecordBatch.FromRows(batch.Schema, rows);
    }

    private static SplitterBranch[] ParseBranches(string branches, char delimiter)
    {
        if (string.IsNullOrWhiteSpace(branches))
        {
            throw new InvalidOperationException("TransformConditionalSplitter requires a 'branches' definition.");
        }

        var output = new List<SplitterBranch>();
        foreach (var part in branches.Split(delimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (split.Length != 2)
            {
                throw new InvalidOperationException($"Invalid branch definition '{part}'. Expected 'name:expression'.");
            }

            var name = split[0];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"Invalid branch name in '{part}'.");
            }

            output.Add(new SplitterBranch(name, ExpressionEvaluator.Compile(split[1])));
        }

        return output.ToArray();
    }

    private sealed record SplitterBranch(string Name, ExpressionEvaluator Predicate);
}
