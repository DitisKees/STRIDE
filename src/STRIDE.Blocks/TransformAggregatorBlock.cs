using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace STRIDE.Blocks;

[StrideBlock("TransformAggregator")]
public sealed class TransformAggregatorBlock(string groupBy, string aggregates, char delimiter = ',') : ITransformBlock
{
    private readonly string[] _groupFields = SplitList(groupBy, delimiter);
    private readonly AggregateSpec[] _aggregateSpecs = ParseAggregateSpecs(aggregates);

    public bool IsBlocking => true;

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        if (!inputSchemas.TryGetValue("in", out var inputSchema))
        {
            throw new InvalidOperationException("TransformAggregator requires an 'in' input schema.");
        }

        var outputFields = new List<FieldDef>(_groupFields.Length + _aggregateSpecs.Length);

        foreach (var groupField in _groupFields)
        {
            if (!inputSchema.TryGetOrdinal(groupField, out var ordinal))
            {
                throw new InvalidOperationException($"TransformAggregator group field '{groupField}' was not found in the input schema.");
            }

            outputFields.Add(inputSchema.Fields[ordinal]);
        }

        var outputFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in outputFields)
        {
            outputFieldNames.Add(field.Name);
        }

        foreach (var aggregate in _aggregateSpecs)
        {
            var outputType = aggregate.Function switch
            {
                AggregateFunction.Count => FieldType.Int64,
                AggregateFunction.Sum => FieldType.Float64,
                AggregateFunction.Avg => FieldType.Float64,
                AggregateFunction.Min => FieldType.Float64,
                AggregateFunction.Max => FieldType.Float64,
                _ => throw new InvalidOperationException($"Unsupported aggregate function '{aggregate.Function}'."),
            };

            if (aggregate.Function is not AggregateFunction.Count)
            {
                if (!inputSchema.TryGetOrdinal(aggregate.InputField, out var inputOrdinal))
                {
                    throw new InvalidOperationException($"TransformAggregator aggregate field '{aggregate.InputField}' was not found in the input schema.");
                }

                var inputType = inputSchema.Fields[inputOrdinal].Type;
                if (!IsNumericType(inputType))
                {
                    throw new InvalidOperationException($"Aggregate '{aggregate.Function}' requires a numeric input field, but '{aggregate.InputField}' has type '{inputType}'.");
                }
            }

            if (!outputFieldNames.Add(aggregate.OutputField))
            {
                throw new InvalidOperationException($"TransformAggregator output field '{aggregate.OutputField}' is defined more than once.");
            }

            outputFields.Add(new FieldDef(aggregate.OutputField, outputType, true));
        }

        return new Schema(outputFields.ToImmutableArray());
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            yield break;
        }

        var hasData = false;
        AggregationPlan? plan = null;

        var groupComparer = new GroupKeyComparer();
        var groups = new Dictionary<GroupKey, AggregateAccumulator[]>(groupComparer);
        var estimatedBytes = 0L;

        var spillThresholdBytes = context.Parameters.GetOptionalInt64("spillThresholdBytes") ?? 8L * 1024L * 1024L;

        await using var spillScope = await context.SpillManager.BeginScopeAsync(context.NodeId, cancellationToken).ConfigureAwait(false);

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var batch))
            {
                if (batch.RowCount == 0)
                {
                    continue;
                }

                hasData = true;
                plan ??= BuildPlan(batch.Schema);

                for (var row = 0; row < batch.RowCount; row++)
                {
                    var keyValues = new string[plan.GroupOrdinals.Length];
                    for (var i = 0; i < plan.GroupOrdinals.Length; i++)
                    {
                        keyValues[i] = batch.GetValueAsString(plan.GroupOrdinals[i], row);
                    }

                    var key = new GroupKey(keyValues);
                    if (!groups.TryGetValue(key, out var accumulators))
                    {
                        accumulators = CreateAccumulators(plan.Aggregates.Length);
                        groups[key] = accumulators;
                        estimatedBytes += EstimateGroupBytes(keyValues, accumulators.Length);
                    }

                    for (var i = 0; i < plan.Aggregates.Length; i++)
                    {
                        var aggregate = plan.Aggregates[i];
                        var accumulator = accumulators[i];

                        switch (aggregate.Function)
                        {
                            case AggregateFunction.Count:
                                if (aggregate.InputOrdinal < 0 || ShouldCountValue(batch, aggregate.FieldType, aggregate.InputOrdinal, row))
                                {
                                    accumulator.Count++;
                                }

                                break;

                            case AggregateFunction.Sum:
                            case AggregateFunction.Avg:
                            case AggregateFunction.Min:
                            case AggregateFunction.Max:
                                if (!TryReadNumericValue(batch, aggregate.FieldType, aggregate.InputOrdinal, row, out var numericValue))
                                {
                                    break;
                                }

                                if (aggregate.Function is AggregateFunction.Sum or AggregateFunction.Avg)
                                {
                                    accumulator.Sum += numericValue;
                                    accumulator.Count++;
                                }

                                if (aggregate.Function == AggregateFunction.Min)
                                {
                                    if (!accumulator.HasValue || numericValue < accumulator.Min)
                                    {
                                        accumulator.Min = numericValue;
                                    }

                                    accumulator.HasValue = true;
                                }

                                if (aggregate.Function == AggregateFunction.Max)
                                {
                                    if (!accumulator.HasValue || numericValue > accumulator.Max)
                                    {
                                        accumulator.Max = numericValue;
                                    }

                                    accumulator.HasValue = true;
                                }

                                break;
                        }
                    }
                }

                if (estimatedBytes >= spillThresholdBytes && groups.Count > 0 && plan is not null)
                {
                    await SpillGroupsAsync(spillScope, groups, cancellationToken).ConfigureAwait(false);
                    groups.Clear();
                    estimatedBytes = 0;
                }
            }
        }

        if (!hasData || plan is null)
        {
            yield break;
        }

        var finalGroups = new Dictionary<GroupKey, AggregateAccumulator[]>(groupComparer);

        foreach (var group in groups)
        {
            finalGroups[group.Key] = CloneAccumulators(group.Value);
        }

        await foreach (var payload in spillScope.ReadPayloadsAsync(cancellationToken).ConfigureAwait(false))
        {
            MergePayloadInto(finalGroups, payload);
        }

        if (finalGroups.Count == 0)
        {
            yield break;
        }

        var outputRows = new List<string[]>(finalGroups.Count);
        foreach (var group in finalGroups)
        {
            outputRows.Add(BuildOutputRow(group, plan));
        }

        var batchSize = context.Parameters.GetOptionalInt32("batchSize") ?? 1000;
        for (var i = 0; i < outputRows.Count; i += batchSize)
        {
            var count = Math.Min(batchSize, outputRows.Count - i);
            var chunk = new string[count][];
            for (var row = 0; row < count; row++)
            {
                chunk[row] = outputRows[i + row];
            }

            yield return RecordBatch.FromRows(plan.OutputSchema, chunk);
        }
    }

    private AggregationPlan BuildPlan(Schema inputSchema)
    {
        var schema = DeriveOutputSchema(new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase)
        {
            ["in"] = inputSchema,
        });

        var groupOrdinals = new int[_groupFields.Length];
        for (var i = 0; i < _groupFields.Length; i++)
        {
            if (!inputSchema.TryGetOrdinal(_groupFields[i], out var ordinal))
            {
                throw new InvalidOperationException($"TransformAggregator group field '{_groupFields[i]}' was not found in the input schema.");
            }

            groupOrdinals[i] = ordinal;
        }

        var aggregates = new ResolvedAggregateSpec[_aggregateSpecs.Length];
        for (var i = 0; i < _aggregateSpecs.Length; i++)
        {
            var aggregate = _aggregateSpecs[i];
            if (aggregate.Function == AggregateFunction.Count && aggregate.InputField == "*")
            {
                aggregates[i] = new ResolvedAggregateSpec(aggregate.Function, -1, FieldType.Null);
                continue;
            }

            if (!inputSchema.TryGetOrdinal(aggregate.InputField, out var ordinal))
            {
                throw new InvalidOperationException($"TransformAggregator aggregate field '{aggregate.InputField}' was not found in the input schema.");
            }

            var fieldType = inputSchema.Fields[ordinal].Type;
            aggregates[i] = new ResolvedAggregateSpec(aggregate.Function, ordinal, fieldType);
        }

        return new AggregationPlan(schema, groupOrdinals, aggregates);
    }

    private static async ValueTask SpillGroupsAsync(
        ISpillScope spillScope,
        IReadOnlyDictionary<GroupKey, AggregateAccumulator[]> groups,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(groups.Count);
            foreach (var group in groups)
            {
                writer.Write(group.Key.Values.Length);
                foreach (var value in group.Key.Values)
                {
                    writer.Write(value ?? string.Empty);
                }

                writer.Write(group.Value.Length);
                foreach (var accumulator in group.Value)
                {
                    writer.Write(accumulator.Count);
                    writer.Write(accumulator.Sum);
                    writer.Write(accumulator.Min);
                    writer.Write(accumulator.Max);
                    writer.Write(accumulator.HasValue);
                }
            }
        }

        await spillScope.WritePayloadAsync(stream.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static void MergePayloadInto(Dictionary<GroupKey, AggregateAccumulator[]> groups, ReadOnlyMemory<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        var groupCount = reader.ReadInt32();
        for (var g = 0; g < groupCount; g++)
        {
            var keyLength = reader.ReadInt32();
            var keyValues = new string[keyLength];
            for (var i = 0; i < keyLength; i++)
            {
                keyValues[i] = reader.ReadString();
            }

            var stateLength = reader.ReadInt32();
            var spilledState = new AggregateAccumulator[stateLength];
            for (var i = 0; i < stateLength; i++)
            {
                spilledState[i] = new AggregateAccumulator
                {
                    Count = reader.ReadInt64(),
                    Sum = reader.ReadDouble(),
                    Min = reader.ReadDouble(),
                    Max = reader.ReadDouble(),
                    HasValue = reader.ReadBoolean(),
                };
            }

            var key = new GroupKey(keyValues);
            if (!groups.TryGetValue(key, out var existingState))
            {
                groups[key] = spilledState;
                continue;
            }

            for (var i = 0; i < existingState.Length; i++)
            {
                existingState[i].MergeFrom(spilledState[i]);
            }
        }
    }

    private static string[] SplitList(string value, char delimiter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(delimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AggregateSpec[] ParseAggregateSpecs(string aggregates)
    {
        if (string.IsNullOrWhiteSpace(aggregates))
        {
            throw new InvalidOperationException("TransformAggregator requires an 'aggregates' definition.");
        }

        var specs = new List<AggregateSpec>();
        foreach (var aggregateDefinition in aggregates.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = aggregateDefinition.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts.Length > 3)
            {
                throw new InvalidOperationException($"Invalid aggregate definition '{aggregateDefinition}'. Expected 'func:field[:alias]'.");
            }

            var function = parts[0].ToLowerInvariant() switch
            {
                "count" => AggregateFunction.Count,
                "sum" => AggregateFunction.Sum,
                "avg" => AggregateFunction.Avg,
                "min" => AggregateFunction.Min,
                "max" => AggregateFunction.Max,
                _ => throw new InvalidOperationException($"Unsupported aggregate function '{parts[0]}'."),
            };

            var inputField = parts[1];
            if (function != AggregateFunction.Count && inputField == "*")
            {
                throw new InvalidOperationException($"Aggregate function '{parts[0]}' cannot use '*' as input field.");
            }

            var outputField = parts.Length == 3
                ? parts[2]
                : $"{function.ToString().ToLowerInvariant()}_{inputField.Replace('*', 'a')}";

            specs.Add(new AggregateSpec(function, inputField, outputField));
        }

        return specs.ToArray();
    }

    private static bool IsNumericType(FieldType fieldType)
        => fieldType is FieldType.Int32 or FieldType.Int64 or FieldType.Float64;

    private static bool ShouldCountValue(IRecordBatch batch, FieldType fieldType, int ordinal, int rowIndex)
        => fieldType switch
        {
            FieldType.Boolean => true,
            FieldType.Int32 => true,
            FieldType.Int64 => true,
            FieldType.Float64 => true,
            FieldType.Geometry => batch.GeometryColumn(ordinal).Values[rowIndex] is not null,
            _ => !string.IsNullOrWhiteSpace(batch.GetValueAsString(ordinal, rowIndex)),
        };

    private static bool TryReadNumericValue(IRecordBatch batch, FieldType fieldType, int ordinal, int rowIndex, out double value)
    {
        switch (fieldType)
        {
            case FieldType.Int32:
                value = batch.Column<int>(ordinal)[rowIndex];
                return true;
            case FieldType.Int64:
                value = batch.Column<long>(ordinal)[rowIndex];
                return true;
            case FieldType.Float64:
                value = batch.Column<double>(ordinal)[rowIndex];
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static AggregateAccumulator[] CreateAccumulators(int length)
    {
        var accumulators = new AggregateAccumulator[length];
        for (var i = 0; i < length; i++)
        {
            accumulators[i] = new AggregateAccumulator();
        }

        return accumulators;
    }

    private static AggregateAccumulator[] CloneAccumulators(AggregateAccumulator[] source)
    {
        var clone = new AggregateAccumulator[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            clone[i] = source[i].Clone();
        }

        return clone;
    }

    private static long EstimateGroupBytes(IReadOnlyList<string> keys, int aggregateCount)
    {
        var keyBytes = keys.Sum(static k => k.Length * sizeof(char));
        return keyBytes + (aggregateCount * 48L) + 64L;
    }

    private static string[] BuildOutputRow(KeyValuePair<GroupKey, AggregateAccumulator[]> group, AggregationPlan plan)
    {
        var row = new string[plan.OutputSchema.Fields.Length];
        for (var i = 0; i < group.Key.Values.Length; i++)
        {
            row[i] = group.Key.Values[i];
        }

        for (var i = 0; i < plan.Aggregates.Length; i++)
        {
            var aggregate = plan.Aggregates[i];
            var accumulator = group.Value[i];
            var targetColumn = plan.GroupOrdinals.Length + i;

            row[targetColumn] = aggregate.Function switch
            {
                AggregateFunction.Count => accumulator.Count.ToString(CultureInfo.InvariantCulture),
                AggregateFunction.Sum => accumulator.Sum.ToString(CultureInfo.InvariantCulture),
                AggregateFunction.Avg => accumulator.Count == 0
                    ? "0"
                    : (accumulator.Sum / accumulator.Count).ToString(CultureInfo.InvariantCulture),
                AggregateFunction.Min => accumulator.HasValue
                    ? accumulator.Min.ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                AggregateFunction.Max => accumulator.HasValue
                    ? accumulator.Max.ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                _ => string.Empty,
            };
        }

        return row;
    }

    private enum AggregateFunction
    {
        Count,
        Sum,
        Avg,
        Min,
        Max,
    }

    private sealed record AggregateSpec(AggregateFunction Function, string InputField, string OutputField);

    private sealed record ResolvedAggregateSpec(AggregateFunction Function, int InputOrdinal, FieldType FieldType);

    private sealed record AggregationPlan(Schema OutputSchema, int[] GroupOrdinals, ResolvedAggregateSpec[] Aggregates);

    private sealed class GroupKey
    {
        public GroupKey(string[] values)
        {
            Values = values;
        }

        public string[] Values { get; }
    }

    private sealed class GroupKeyComparer : IEqualityComparer<GroupKey>
    {
        public bool Equals(GroupKey? x, GroupKey? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Values.Length != y.Values.Length)
            {
                return false;
            }

            for (var i = 0; i < x.Values.Length; i++)
            {
                if (!string.Equals(x.Values[i], y.Values[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(GroupKey obj)
        {
            var hash = new HashCode();
            foreach (var value in obj.Values)
            {
                hash.Add(value, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }

    private sealed class AggregateAccumulator
    {
        public long Count { get; set; }

        public double Sum { get; set; }

        public double Min { get; set; }

        public double Max { get; set; }

        public bool HasValue { get; set; }

        public AggregateAccumulator Clone()
            => new()
            {
                Count = Count,
                Sum = Sum,
                Min = Min,
                Max = Max,
                HasValue = HasValue,
            };

        public void MergeFrom(AggregateAccumulator other)
        {
            Count += other.Count;
            Sum += other.Sum;

            if (other.HasValue)
            {
                if (!HasValue || other.Min < Min)
                {
                    Min = other.Min;
                }

                if (!HasValue || other.Max > Max)
                {
                    Max = other.Max;
                }

                HasValue = true;
            }
        }
    }
}
