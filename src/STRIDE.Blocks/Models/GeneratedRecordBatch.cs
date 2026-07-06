using System;
using System.Buffers;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using STRIDE.Abstractions;

namespace STRIDE.Blocks.Models;

public sealed class GeneratedRecordBatch : IRecordBatch
{
    private readonly Dictionary<int, object> _columns = new();
    private readonly Dictionary<int, bool[]> _nullBitmaps = new();
    private readonly List<IDisposable?> _disposables = new();
    private bool _isDisposed;

    public Schema Schema { get; }
    public int RowCount { get; }

    public GeneratedRecordBatch(Schema schema, int rowCount)
    {
        Schema = schema;
        RowCount = rowCount;
    }

    public void AddPrimitiveColumn<T>(int ordinal, T[] rentedArray, int length, bool[]? nullBitmap = null) where T : unmanaged
    {
        _columns[ordinal] = new Memory<T>(rentedArray, 0, length);
        _disposables.Add(new ArrayPoolOwner<T>(rentedArray));
        if (nullBitmap != null)
        {
            _nullBitmaps[ordinal] = nullBitmap;
            _disposables.Add(new ArrayPoolOwner<bool>(nullBitmap));
        }
    }

    public void AddGeometryColumn(int ordinal, Geometry[] geometries, int length)
    {
        // Sla de array op (of pak een Memory/Span variant indien gewenst)
        _columns[ordinal] = geometries;

        // Zorg dat ook de Geometry[] array netjes wordt teruggegeven aan de ArrayPool
        _disposables.Add(new ReferenceArrayPoolOwner<Geometry>(geometries));
    }

    public void AddStringColumn(int ordinal, int[] rentedOffsets, byte[] rentedData, int length, bool[]? nullBitmap = null)
    {
        _columns[ordinal] = (rentedOffsets, rentedData, length);

        _disposables.Add(new ArrayPoolOwner<int>(rentedOffsets));
        _disposables.Add(new ArrayPoolOwner<byte>(rentedData));
        if (nullBitmap != null)
        {
            _nullBitmaps[ordinal] = nullBitmap;
            _disposables.Add(new ArrayPoolOwner<bool>(nullBitmap));
        }
    }

    public bool IsNull(int ordinal, int rowIndex)
    {
        return _nullBitmaps.TryGetValue(ordinal, out var bitmap) && bitmap[rowIndex];
    }

    public ReadOnlyMemory<T> GetColumnMemory<T>(int ordinal) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_columns.TryGetValue(ordinal, out var col) && col is Memory<T> mem)
        {
            return mem;
        }
        throw new InvalidOperationException($"Kolom op index {ordinal} is niet van het type {typeof(T).Name} of bestaat niet.");
    }

    public Utf8StringColumn GetStringColumn(int ordinal)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_columns.TryGetValue(ordinal, out var col) && col is ValueTuple<int[], byte[], int> tuple)
        {
            var offsetsSpan = new ReadOnlySpan<int>(tuple.Item1, 0, tuple.Item3 + 1);
            int totalByteLength = tuple.Item1[tuple.Item3];
            var dataSpan = new ReadOnlySpan<byte>(tuple.Item2, 0, totalByteLength);

            return new Utf8StringColumn(offsetsSpan, dataSpan);
        }
        throw new InvalidOperationException($"Kolom op index {ordinal} is geen string-kolom.");
    }

    public GeometryColumn GetGeometryColumn(int ordinal)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_columns.TryGetValue(ordinal, out var col) && col is Geometry[] geos)
        {
            return new GeometryColumn(geos.AsSpan(0, RowCount));
        }
        throw new InvalidOperationException($"Kolom op index {ordinal} is geen geometrie-kolom.");
    }

    public void ShareColumnsWith(GeneratedRecordBatch target)
    {
        foreach (var kvp in _columns)
        {
            target._columns[kvp.Key] = kvp.Value;
        }
        foreach (var kvp in _nullBitmaps)
        {
            target._nullBitmaps[kvp.Key] = kvp.Value;
        }
        foreach (var disposable in _disposables)
        {
            target._disposables.Add(disposable);
        }
        _disposables.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        foreach (var disposable in _disposables)
        {
            disposable?.Dispose();
        }

        _isDisposed = true;
    }

    // Kleine helper om ArrayPool primitieve buffers veilig te disposen
    private sealed class ArrayPoolOwner<T>(T[] array) : IDisposable where T : unmanaged
    {
        public void Dispose() => ArrayPool<T>.Shared.Return(array);
    }

    // Extra helper voor reference types (zoals String en Geometry) omdat T : unmanaged de andere helper uitsluit
    private sealed class ReferenceArrayPoolOwner<T>(T[] array) : IDisposable where T : class
    {
        public void Dispose() => ArrayPool<T>.Shared.Return(array, clearArray: true); // clearArray is belangrijk voor reference types i.v.m. hergebruik/GC leaks
    }
}