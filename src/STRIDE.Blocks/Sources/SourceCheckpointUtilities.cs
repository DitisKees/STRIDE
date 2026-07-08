using System.Globalization;
using System.Text.Json;

namespace STRIDE.Blocks;

internal static class SourceCheckpointUtilities
{
    private const string NullType = "null";
    private const string StringType = "string";
    private const string Int32Type = "int32";
    private const string Int64Type = "int64";
    private const string DoubleType = "double";
    private const string DecimalType = "decimal";
    private const string BooleanType = "bool";
    private const string DateTimeType = "datetime";
    private const string DateTimeOffsetType = "datetimeoffset";
    private const string GuidType = "guid";

    public static async Task<object?> ReadTokenAsync(string? checkpointPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checkpointPath) || !File.Exists(checkpointPath))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('{'))
        {
            var payload = JsonSerializer.Deserialize<CheckpointPayload>(trimmed);
            if (payload is null)
            {
                return null;
            }

            return ParseToken(payload.Type, payload.Value);
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int64Value))
        {
            return int64Value;
        }

        return trimmed;
    }

    public static async Task WriteTokenAsync(string? checkpointPath, object? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checkpointPath))
        {
            return;
        }

        var payload = SerializeToken(token);
        var directory = Path.GetDirectoryName(checkpointPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(payload);
        await File.WriteAllTextAsync(checkpointPath, json, cancellationToken).ConfigureAwait(false);
    }

    public static object? NormalizeDbValue(object? token)
        => token switch
        {
            null => null,
            DBNull => null,
            decimal decimalValue => decimalValue,
            DateTime dateTimeValue => dateTimeValue,
            DateTimeOffset dateTimeOffsetValue => dateTimeOffsetValue,
            Guid guidValue => guidValue,
            string textValue => textValue,
            bool boolValue => boolValue,
            int intValue => intValue,
            long longValue => longValue,
            short shortValue => (int)shortValue,
            byte byteValue => (int)byteValue,
            sbyte sbyteValue => (int)sbyteValue,
            uint uintValue => (long)uintValue,
            ulong ulongValue => unchecked((long)ulongValue),
            float floatValue => (double)floatValue,
            double doubleValue => doubleValue,
            _ => token.ToString(),
        };

    private static CheckpointPayload SerializeToken(object? token)
    {
        if (token is null)
        {
            return new CheckpointPayload(NullType, null);
        }

        return token switch
        {
            string value => new CheckpointPayload(StringType, value),
            int value => new CheckpointPayload(Int32Type, value.ToString(CultureInfo.InvariantCulture)),
            long value => new CheckpointPayload(Int64Type, value.ToString(CultureInfo.InvariantCulture)),
            double value => new CheckpointPayload(DoubleType, value.ToString("R", CultureInfo.InvariantCulture)),
            decimal value => new CheckpointPayload(DecimalType, value.ToString(CultureInfo.InvariantCulture)),
            bool value => new CheckpointPayload(BooleanType, value ? "true" : "false"),
            DateTime value => new CheckpointPayload(DateTimeType, value.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset value => new CheckpointPayload(DateTimeOffsetType, value.ToString("O", CultureInfo.InvariantCulture)),
            Guid value => new CheckpointPayload(GuidType, value.ToString("D", CultureInfo.InvariantCulture)),
            _ => new CheckpointPayload(StringType, token.ToString()),
        };
    }

    private static object? ParseToken(string? type, string? value)
    {
        switch (type)
        {
            case null:
            case NullType:
                return null;

            case StringType:
                return value;

            case Int32Type:
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)
                    ? intValue
                    : 0;

            case Int64Type:
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)
                    ? longValue
                    : 0L;

            case DoubleType:
                return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue)
                    ? doubleValue
                    : 0d;

            case DecimalType:
                return decimal.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var decimalValue)
                    ? decimalValue
                    : 0m;

            case BooleanType:
                return bool.TryParse(value, out var boolValue) && boolValue;

            case DateTimeType:
                return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTimeValue)
                    ? dateTimeValue
                    : default(DateTime);

            case DateTimeOffsetType:
                return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTimeOffsetValue)
                    ? dateTimeOffsetValue
                    : default(DateTimeOffset);

            case GuidType:
                return Guid.TryParse(value, out var guidValue)
                    ? guidValue
                    : Guid.Empty;

            default:
                return value;
        }
    }

    private sealed record CheckpointPayload(string Type, string? Value);
}
