namespace STRIDE.Abstractions;

public sealed class BlockParams
{
    public static BlockParams Empty { get; } = new(new Dictionary<string, object?>(0, StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, object?> _values;

    public BlockParams(IReadOnlyDictionary<string, object?> values)
    {
        _values = values;
    }

    public IReadOnlyDictionary<string, object?> Values => _values;

    public static BlockParams FromStringMap(IReadOnlyDictionary<string, string> values)
    {
        var output = new Dictionary<string, object?>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in values)
        {
            output[entry.Key] = entry.Value;
        }

        return new BlockParams(output);
    }

    public bool TryGetValue(string key, out object? value)
        => _values.TryGetValue(key, out value);

    public string GetString(string key)
        => Convert.ToString(_values[key]) ?? string.Empty;

    public string GetRequiredString(string key)
    {
        if (!_values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(Convert.ToString(value)))
        {
            throw new InvalidOperationException($"Required parameter '{key}' is missing.");
        }

        return Convert.ToString(value)!;
    }

    public double GetDouble(string key)
        => Convert.ToDouble(_values[key], System.Globalization.CultureInfo.InvariantCulture);

    public int GetInt32(string key)
        => Convert.ToInt32(_values[key], System.Globalization.CultureInfo.InvariantCulture);

    public long GetInt64(string key)
        => Convert.ToInt64(_values[key], System.Globalization.CultureInfo.InvariantCulture);

    public bool GetBoolean(string key)
        => Convert.ToBoolean(_values[key], System.Globalization.CultureInfo.InvariantCulture);

    public string? GetOptionalString(string key)
        => _values.TryGetValue(key, out var value) ? Convert.ToString(value) : null;

    public int? GetOptionalInt32(string key)
        => _values.TryGetValue(key, out var value)
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    public long? GetOptionalInt64(string key)
        => _values.TryGetValue(key, out var value)
            ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    public double? GetOptionalDouble(string key)
        => _values.TryGetValue(key, out var value)
            ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    public bool? GetOptionalBoolean(string key)
        => _values.TryGetValue(key, out var value)
            ? Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
}