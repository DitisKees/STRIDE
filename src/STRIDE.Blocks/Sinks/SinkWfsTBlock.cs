using NetTopologySuite.Geometries;
using STRIDE.Abstractions;
using System.Text;
using System.Xml;

namespace STRIDE.Blocks;

[StrideBlock("SinkWfsT")]
public sealed class SinkWfsTBlock(
    string url,
    string featureType = "feature",
    string featureNamespace = "urn:stride:wfs",
    string geometryField = "geom") : ISinkBlock
{
    public async ValueTask ExecuteAsync(BlockContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("in", out var reader))
        {
            return;
        }

        var writeMode = SinkWriteModeUtilities.Parse(context.Parameters);
        var bufferedRows = new List<(IRecordBatch Batch, int RowIndex)>();
        var bufferedBatches = 0;
        var rowsWritten = 0L;

        using var client = new HttpClient();

        async Task FlushAsync(CancellationToken token)
        {
            if (bufferedRows.Count == 0)
            {
                return;
            }

            var payload = BuildTransactionXml(bufferedRows);
            using var content = new StringContent(payload, Encoding.UTF8, "text/xml");
            using var response = await client.PostAsync(url, content, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            rowsWritten += bufferedRows.Count;
            bufferedRows.Clear();
            bufferedBatches = 0;
        }

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var batch))
                {
                    for (var row = 0; row < batch.RowCount; row++)
                    {
                        bufferedRows.Add((batch, row));
                    }

                    bufferedBatches++;
                    if (!writeMode.IsTransactional
                        && writeMode.Kind == SinkWriteModeKind.BatchCommit
                        && bufferedBatches >= writeMode.BatchCommitInterval)
                    {
                        await FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!writeMode.IsTransactional)
            {
                await context.Errors.WriteRecordErrorAsync(
                    context.NodeId,
                    $"SinkWfsT committed transactions up to failure point. rowsWritten={rowsWritten}, batchesWritten={bufferedBatches}.",
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private string BuildTransactionXml(IReadOnlyList<(IRecordBatch Batch, int RowIndex)> rows)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false,
            Indent = false,
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "Transaction", "http://www.opengis.net/wfs");
            writer.WriteAttributeString("service", "WFS");
            writer.WriteAttributeString("version", "1.1.0");
            writer.WriteAttributeString("xmlns", "wfs", null, "http://www.opengis.net/wfs");
            writer.WriteAttributeString("xmlns", "gml", null, "http://www.opengis.net/gml");
            writer.WriteAttributeString("xmlns", "feature", null, featureNamespace);

            foreach (var (batch, row) in rows)
            {
                writer.WriteStartElement("wfs", "Insert", "http://www.opengis.net/wfs");
                writer.WriteStartElement("feature", featureType, featureNamespace);

                for (var col = 0; col < batch.Schema.Fields.Length; col++)
                {
                    var field = batch.Schema.Fields[col];
                    if (field.Type == FieldType.Geometry || string.Equals(field.Name, geometryField, StringComparison.OrdinalIgnoreCase))
                    {
                        WriteGeometryProperty(writer, field.Name, batch, col, row);
                        continue;
                    }

                    var value = batch.GetValueAsString(col, row);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    writer.WriteElementString("feature", field.Name, featureNamespace, value);
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteGeometryProperty(XmlWriter writer, string propertyName, IRecordBatch batch, int ordinal, int row)
    {
        Geometry? geometry = null;

        if (batch.Schema.Fields[ordinal].Type == FieldType.Geometry)
        {
            geometry = batch.GeometryColumn(ordinal).Values[row];
        }

        if (geometry is null)
        {
            return;
        }

        writer.WriteStartElement("feature", propertyName, "urn:stride:wfs");
        WriteGmlGeometry(writer, geometry);
        writer.WriteEndElement();
    }

    private static void WriteGmlGeometry(XmlWriter writer, Geometry geometry)
    {
        switch (geometry)
        {
            case Point point:
                writer.WriteStartElement("gml", "Point", "http://www.opengis.net/gml");
                writer.WriteElementString("gml", "pos", "http://www.opengis.net/gml", $"{point.X} {point.Y}");
                writer.WriteEndElement();
                break;

            case LineString line:
                writer.WriteStartElement("gml", "LineString", "http://www.opengis.net/gml");
                writer.WriteElementString("gml", "posList", "http://www.opengis.net/gml", BuildPosList(line.Coordinates));
                writer.WriteEndElement();
                break;

            case Polygon polygon:
                writer.WriteStartElement("gml", "Polygon", "http://www.opengis.net/gml");

                writer.WriteStartElement("gml", "exterior", "http://www.opengis.net/gml");
                writer.WriteStartElement("gml", "LinearRing", "http://www.opengis.net/gml");
                writer.WriteElementString("gml", "posList", "http://www.opengis.net/gml", BuildPosList(polygon.ExteriorRing.Coordinates));
                writer.WriteEndElement();
                writer.WriteEndElement();

                for (var i = 0; i < polygon.NumInteriorRings; i++)
                {
                    writer.WriteStartElement("gml", "interior", "http://www.opengis.net/gml");
                    writer.WriteStartElement("gml", "LinearRing", "http://www.opengis.net/gml");
                    writer.WriteElementString("gml", "posList", "http://www.opengis.net/gml", BuildPosList(polygon.GetInteriorRingN(i).Coordinates));
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                break;

            default:
                writer.WriteStartElement("gml", "Point", "http://www.opengis.net/gml");
                writer.WriteElementString("gml", "pos", "http://www.opengis.net/gml", $"{geometry.Centroid.X} {geometry.Centroid.Y}");
                writer.WriteEndElement();
                break;
        }
    }

    private static string BuildPosList(IReadOnlyList<Coordinate> coordinates)
        => string.Join(' ', coordinates.Select(static coordinate => $"{coordinate.X} {coordinate.Y}"));
}
