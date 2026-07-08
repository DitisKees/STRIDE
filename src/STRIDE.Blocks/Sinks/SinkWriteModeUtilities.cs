using STRIDE.Abstractions;

namespace STRIDE.Blocks;

internal enum SinkWriteModeKind
{
    Transactional,
    BatchCommit,
}

internal readonly record struct SinkWriteMode(SinkWriteModeKind Kind, int BatchCommitInterval)
{
    public static SinkWriteMode Transactional => new(SinkWriteModeKind.Transactional, 0);

    public bool IsTransactional => Kind == SinkWriteModeKind.Transactional;
}

internal static class SinkWriteModeUtilities
{
    private const int DefaultBatchCommitInterval = 1;

    public static SinkWriteMode Parse(BlockParams parameters)
    {
        var raw = parameters.GetOptionalString("writeMode");
        return Parse(raw);
    }

    public static SinkWriteMode Parse(string? writeMode)
    {
        if (string.IsNullOrWhiteSpace(writeMode)
            || writeMode.Equals("Transactional", StringComparison.OrdinalIgnoreCase))
        {
            return SinkWriteMode.Transactional;
        }

        if (writeMode.StartsWith("BatchCommit", StringComparison.OrdinalIgnoreCase))
        {
            var interval = ExtractBatchCommitInterval(writeMode);
            return new SinkWriteMode(SinkWriteModeKind.BatchCommit, interval);
        }

        throw new InvalidOperationException($"Unsupported writeMode '{writeMode}'. Use 'Transactional' or 'BatchCommit(n)'.");
    }

    public static string CreateTransactionalStagingPath(string outputPath)
        => $"{outputPath}.stride-txn-{Guid.NewGuid():N}.tmp";

    private static int ExtractBatchCommitInterval(string value)
    {
        var openParen = value.IndexOf('(');
        var closeParen = value.IndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
        {
            var numeric = value.Substring(openParen + 1, closeParen - openParen - 1);
            if (int.TryParse(numeric, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            throw new InvalidOperationException($"Invalid BatchCommit interval in writeMode '{value}'.");
        }

        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            var numeric = value[(colon + 1)..];
            if (int.TryParse(numeric, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            throw new InvalidOperationException($"Invalid BatchCommit interval in writeMode '{value}'.");
        }

        return DefaultBatchCommitInterval;
    }
}
