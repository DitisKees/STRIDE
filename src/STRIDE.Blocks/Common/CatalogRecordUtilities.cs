using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using System.Collections.Immutable;
using System.Globalization;

namespace STRIDE.Blocks;

internal sealed record CatalogFeatureRecord(
    IReadOnlyDictionary<string, object?> Attributes,
    Geometry? Geometry);

internal static class CatalogRecordUtilities
{
    public static Schema BuildSchema(IReadOnlyList<CatalogFeatureRecord> records, string geometryColumn)
    {
        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            foreach (var key in record.Attributes.Keys)
            {
                fieldNames.Add(key);
            }
        }

        var ordered = fieldNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var fields = new FieldDef[ordered.Length + 1];

        for (var i = 0; i < ordered.Length; i++)
        {
            var fieldName = ordered[i];
            var values = records
                .Select(record => record.Attributes.TryGetValue(fieldName, out var value) ? value : null)
                .ToArray();

            fields[i] = new FieldDef(fieldName, InferType(values), true);
        }

        fields[^1] = new FieldDef(geometryColumn, FieldType.Geometry, true);
        return new Schema(fields.ToImmutableArray());
    }

    public static RecordBatch BuildBatch(Schema schema, IReadOnlyList<CatalogFeatureRecord> records)
    {
        var columns = new object?[schema.Fields.Length];
        var geometryOrdinal = schema.GeometryFieldIndex;

        for (var col = 0; col < schema.Fields.Length; col++)
        {
            if (col == geometryOrdinal)
            {
                var geometries = new Geometry?[records.Count];
                for (var row = 0; row < records.Count; row++)
                {
                    geometries[row] = records[row].Geometry;
                }

                columns[col] = new GeometryColumn(geometries);
                continue;
            }

            var field = schema.Fields[col];
            switch (field.Type)
            {
                case FieldType.Boolean:
                    {
                        var values = new bool[records.Count];
                        for (var row = 0; row < records.Count; row++)
                        {
                            values[row] = TryConvertToBoolean(GetAttribute(records[row], field.Name), out var parsed) && parsed;
                        }

                        columns[col] = values;
                        break;
                    }

                case FieldType.Int32:
                    {
                        var values = new int[records.Count];
                        for (var row = 0; row < records.Count; row++)
                        {
                            values[row] = TryConvertToInt32(GetAttribute(records[row], field.Name), out var parsed) ? parsed : 0;
                        }

                        columns[col] = values;
                        break;
                    }

                case FieldType.Int64:
                    {
                        var values = new long[records.Count];
                        for (var row = 0; row < records.Count; row++)
                        {
                            values[row] = TryConvertToInt64(GetAttribute(records[row], field.Name), out var parsed) ? parsed : 0L;
                        }

                        columns[col] = values;
                        break;
                    }

                case FieldType.Float64:
                    {
                        var values = new double[records.Count];
                        for (var row = 0; row < records.Count; row++)
                        {
                            values[row] = TryConvertToDouble(GetAttribute(records[row], field.Name), out var parsed) ? parsed : 0d;
                        }

                        columns[col] = values;
                        break;
                    }

                default:
                    {
                        var values = new string?[records.Count];
                        for (var row = 0; row < records.Count; row++)
                        {
                            var scalar = GetAttribute(records[row], field.Name);
                            values[row] = scalar is null ? null : ToInvariantString(scalar);
                        }

                        columns[col] = RecordBatch.CreateUtf8Column(values);
                        break;
                    }
            }
        }

        return new RecordBatch(schema, records.Count, columns);
    }

    public static object? ParseScalarString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var booleanValue))
        {
            return booleanValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int32Value))
        {
            return int32Value;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int64Value))
        {
            return int64Value;
        }

        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var floatValue))
        {
            return floatValue;
        }

        return value.Trim();
    }

    public static string ToInvariantString(object value)
        => value switch
        {
            null => string.Empty,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };

    private static object? GetAttribute(CatalogFeatureRecord record, string key)
        => record.Attributes.TryGetValue(key, out var value)
            ? value
            : null;

    private static FieldType InferType(IReadOnlyList<object?> values)
    {
        var hasValue = false;
        var allBoolean = true;
        var allInt32 = true;
        var allInt64 = true;
        var allDouble = true;

        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            if (value is string text && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            hasValue = true;
            allBoolean &= TryConvertToBoolean(value, out _);
            allInt32 &= TryConvertToInt32(value, out _);
            allInt64 &= TryConvertToInt64(value, out _);
            allDouble &= TryConvertToDouble(value, out _);
        }

        if (!hasValue)
        {
            return FieldType.Utf8String;
        }

        if (allBoolean)
        {
            return FieldType.Boolean;
        }

        if (allInt32)
        {
            return FieldType.Int32;
        }

        if (allInt64)
        {
            return FieldType.Int64;
        }

        if (allDouble)
        {
            return FieldType.Float64;
        }

        return FieldType.Utf8String;
    }

    private static bool TryConvertToBoolean(object? value, out bool output)
    {
        switch (value)
        {
            case null:
                output = false;
                return false;
            case bool direct:
                output = direct;
                return true;
            default:
                return bool.TryParse(ToInvariantString(value), out output);
        }
    }

    private static bool TryConvertToInt32(object? value, out int output)
    {
        switch (value)
        {
            case null:
                output = 0;
                return false;
            case int direct:
                output = direct;
                return true;
            case short shortValue:
                output = shortValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                output = (int)longValue;
                return true;
            default:
                return int.TryParse(ToInvariantString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out output);
        }
    }

    private static bool TryConvertToInt64(object? value, out long output)
    {
        switch (value)
        {
            case null:
                output = 0;
                return false;
            case long direct:
                output = direct;
                return true;
            case int intValue:
                output = intValue;
                return true;
            default:
                return long.TryParse(ToInvariantString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out output);
        }
    }

    private static bool TryConvertToDouble(object? value, out double output)
    {
        switch (value)
        {
            case null:
                output = 0;
                return false;
            case double direct:
                output = direct;
                return true;
            case float floatValue:
                output = floatValue;
                return true;
            case decimal decimalValue:
                output = (double)decimalValue;
                return true;
            default:
                return double.TryParse(
                    ToInvariantString(value),
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out output);
        }
    }
}
