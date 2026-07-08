using STRIDE.Abstractions;
using System.Collections.Concurrent;

namespace STRIDE.Core;

public sealed class SpillManager : ISpillManager, IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _runDirectory;
    private readonly string _runLockFilePath;
    private readonly FileStream _runLockFileStream;
    private readonly ConcurrentDictionary<string, SpillScope> _scopes = new(StringComparer.Ordinal);
    private int _isDisposed;

    public SpillManager(string? rootDirectory = null)
    {
        _rootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory) ? "./.stride-spill" : rootDirectory);
        Directory.CreateDirectory(_rootDirectory);

        _runDirectory = Path.Combine(_rootDirectory, $"run-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        _runLockFilePath = Path.Combine(_runDirectory, ".active");

        CleanupOrphanedRunDirectories();
        Directory.CreateDirectory(_runDirectory);
        _runLockFileStream = new FileStream(_runLockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public ValueTask<ISpillScope> BeginScopeAsync(string blockId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        var safeBlockId = string.Concat(blockId.Select(static ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var scopeDirectory = Path.Combine(_runDirectory, $"{safeBlockId}-{Guid.NewGuid():N}");
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

        try
        {
            _runLockFileStream.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }

        TryDeleteDirectory(_runDirectory);
    }

    private void CleanupOrphanedRunDirectories()
    {
        foreach (var runDirectory in Directory.EnumerateDirectories(_rootDirectory, "run-*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(runDirectory, _runDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var activeMarkerPath = Path.Combine(runDirectory, ".active");

            try
            {
                if (File.Exists(activeMarkerPath))
                {
                    using var _ = new FileStream(activeMarkerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }

                TryDeleteDirectory(runDirectory);
            }
            catch
            {
                // Another process likely owns this run directory.
            }
        }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
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

        if (payload.Length == 0)
        {
            await File.WriteAllBytesAsync(filePath, [], cancellationToken).ConfigureAwait(false);
            return filePath;
        }

        await using (var stream = new FileStream(
            filePath,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous,
            }))
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

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

            var fileLength = new FileInfo(file).Length;
            if (fileLength == 0)
            {
                yield return ReadOnlyMemory<byte>.Empty;
                continue;
            }

            if (fileLength > int.MaxValue)
            {
                throw new InvalidOperationException($"Spill payload '{file}' is too large to read into memory ({fileLength} bytes).");
            }

            var bytes = GC.AllocateUninitializedArray<byte>((int)fileLength);
            await using (var stream = new FileStream(
                file,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
            {
                var bytesRead = 0;
                while (bytesRead < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(bytesRead), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    bytesRead += read;
                }

                if (bytesRead != bytes.Length)
                {
                    Array.Resize(ref bytes, bytesRead);
                }
            }

            yield return bytes;

            await Task.Yield();
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