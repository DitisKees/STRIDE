using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO; // Bevat de herstelde GeoJsonWriter
using STRIDE.Abstractions;

namespace STRIDE.Blocks;

[StrideBlock("GeoJsonSink")]
public sealed class GeoJsonSink : ISinkBlock
{
    private readonly string _outputPath;
    private readonly GeoJsonWriter _geoJsonWriter;

    public GeoJsonSink(Dictionary<string, string> parameters)
    {
        _outputPath = parameters["outputPath"].Trim('"');
        _geoJsonWriter = new GeoJsonWriter();
    }

    public Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas)
    {
        if (inputSchemas.TryGetValue("in", out var inputSchema)) return inputSchema;
        return new Schema(System.Collections.Immutable.ImmutableArray<FieldDef>.Empty);
    }

    public async Task WriteAsync(IAsyncEnumerable<IRecordBatch> inputStream, BlockContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"[GeoJsonSink] Starting export to {_outputPath}");
        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Ensure previous output file is removed to avoid sharing violations
        if (File.Exists(_outputPath))
            File.Delete(_outputPath);
        await using var fileStream = new FileStream(_outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 65536, useAsync: true);
        await using var writer = new StreamWriter(fileStream, Encoding.UTF8, bufferSize: 65536);

        await writer.WriteAsync("{\n\"type\": \"FeatureCollection\",\n\"features\": [\n");

        bool isFirstFeature = true;
        int totalFeaturesWritten = 0;

        await foreach (var batch in inputStream.WithCancellation(ct))
        {
            using (batch)
            {
                int rowCount = batch.RowCount;
                var schema = batch.Schema;

                int geomOrdinal = -1;
                var primitiveOrdinals = new List<(int Ordinal, FieldType Type, string Name)>();
                var stringOrdinals = new List<(int Ordinal, string Name)>();

                for (int i = 0; i < schema.Fields.Length; i++)
                {
                    var field = schema.Fields[i];
                    if (field.Type == FieldType.Geometry) geomOrdinal = i;
                    else if (field.Type == FieldType.Utf8String) stringOrdinals.Add((i, field.Name));
                    else primitiveOrdinals.Add((i, field.Type, field.Name));
                }

                if (geomOrdinal == -1)
                {
                    throw new InvalidOperationException("GeoJsonSink vereist minimaal één Geometry-kolom.");
                }

                // Lus door de rijen. Schrijf direct naar de gebufferde StreamWriter (sync) —
                // writer.Write() is geen await-punt, dus ref structs blijven geldig in scope.
                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    // --- START SYNCHROON BLOK (Geen await binnen deze scope) ---
                    {
                        var geomColumn = batch.GetGeometryColumn(geomOrdinal);
                        var geometry = geomColumn.Geometries[rowIndex];
                        if (geometry == null) continue;

                        if (!isFirstFeature) writer.Write(",\n");
                        isFirstFeature = false;

                        writer.Write("{\n\"type\": \"Feature\",\n\"geometry\": ");
                        writer.Write(_geoJsonWriter.Write(geometry));
                        writer.Write(",\n\"properties\": {");

                        bool isFirstProp = true;

                        // Primitieven parsen (Unmanaged geheugen naar string)
                        foreach (var prop in primitiveOrdinals)
                        {
                            if (!isFirstProp) writer.Write(", ");
                            isFirstProp = false;

                            writer.Write($"\"{prop.Name}\": ");

                            if (batch.IsNull(prop.Ordinal, rowIndex))
                            {
                                writer.Write("null");
                            }
                            else if (prop.Type == FieldType.Int64)
                            {
                                writer.Write(batch.GetColumnMemory<long>(prop.Ordinal).Span[rowIndex]);
                            }
                            else if (prop.Type == FieldType.Int32)
                            {
                                writer.Write(batch.GetColumnMemory<int>(prop.Ordinal).Span[rowIndex]);
                            }
                            else if (prop.Type == FieldType.Float64)
                            {
                                writer.Write(batch.GetColumnMemory<double>(prop.Ordinal).Span[rowIndex]);
                            }
                            else if (prop.Type == FieldType.Boolean)
                            {
                                writer.Write(batch.GetColumnMemory<bool>(prop.Ordinal).Span[rowIndex] ? "true" : "false");
                            }
                        }

                        totalFeaturesWritten++;
                        // Report progress every 1000 features
                        if (totalFeaturesWritten % 1000 == 0)
                        {
                            Console.WriteLine($"[GeoJsonSink] Written {totalFeaturesWritten} features so far.");
                        }
                        foreach (var prop in stringOrdinals)
                        {
                            if (!isFirstProp) writer.Write(", ");
                            isFirstProp = false;

                            writer.Write($"\"{prop.Name}\": ");

                            if (batch.IsNull(prop.Ordinal, rowIndex))
                            {
                                writer.Write("null");
                                continue;
                            }

                            var stringCol = batch.GetStringColumn(prop.Ordinal);
                            ReadOnlySpan<byte> rowBytes = stringCol.GetRowSpan(rowIndex);
                            if (rowBytes.IsEmpty)
                            {
                                writer.Write("null");
                            }
                            else
                            {
                                string val = Encoding.UTF8.GetString(rowBytes);
                                if (string.IsNullOrWhiteSpace(val))
                                    writer.Write("null");
                                else
                                    writer.Write($"\"{EscapeJsonString(val)}\"");
                            }
                        }

                        writer.Write("}\n}");
                    }
                    // --- EINDE SYNCHROON BLOK ---
                }
                // Flush eenmalig per batch voor efficiënte asynchrone I/O
                await writer.FlushAsync();
            }
        }

        if (totalFeaturesWritten % 1000 != 0)
            Console.WriteLine($"[GeoJsonSink] Written {totalFeaturesWritten} features so far.");

        await writer.WriteAsync("\n]\n}");
        await writer.FlushAsync();
    }
    private static string EscapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}