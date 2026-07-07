using STRIDE.Abstractions;
using System.Collections.Concurrent;

namespace STRIDE.Core;

public sealed class SpillManager : ISpillManager, IDisposable
{
    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, SpillScope> _scopes = new(StringComparer.Ordinal);
    private int _isDisposed;

    public SpillManager(string? rootDirectory = null)
    {
        _rootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory) ? "./.stride-spill" : rootDirectory);
        Directory.CreateDirectory(_rootDirectory);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public ValueTask<ISpillScope> BeginScopeAsync(string blockId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var safeBlockId = string.Concat(blockId.Select(static ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var scopeDirectory = Path.Combine(_rootDirectory, $"{safeBlockId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scopeDirectory);

        var scope = new SpillScope(scopeDirectory, RemoveScope);
        _scopes.TryAdd(scopeDirectory, scope);
        return ValueTask.FromResult<ISpillScope>(scope);
    }

    private void RemoveScope(string scopeDirectory)
    {
        _scopes.TryRemove(scopeDirectory, out _);
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        foreach (var scope in _scopes.Values)
        {
            scope.TryDeleteDirectory();
        }

        _scopes.Clear();
    }
}

internal sealed class SpillScope : ISpillScope
{
    private readonly string _scopeDirectory;
    private readonly Action<string> _onDispose;
    private int _nextPayloadIndex;
    private int _isDisposed;

    public SpillScope(string scopeDirectory, Action<string> onDispose)
    {
        _scopeDirectory = scopeDirectory;
        _onDispose = onDispose;
    }

    public async ValueTask<string> WritePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var index = Interlocked.Increment(ref _nextPayloadIndex);
        var filePath = Path.Combine(_scopeDirectory, $"spill-{index:D8}.bin");
        await File.WriteAllBytesAsync(filePath, payload.ToArray(), cancellationToken).ConfigureAwait(false);
        return filePath;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadPayloadsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        if (!Directory.Exists(_scopeDirectory))
        {
            yield break;
        }

        var files = Directory
            .EnumerateFiles(_scopeDirectory, "spill-*.bin", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            yield return bytes;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _onDispose(_scopeDirectory);
        TryDeleteDirectory();
        return ValueTask.CompletedTask;
    }

    public void TryDeleteDirectory()
    {
        try
        {
            if (Directory.Exists(_scopeDirectory))
            {
                Directory.Delete(_scopeDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}