using STRIDE.Abstractions;
using System.Xml;
using System.Xml.Linq;

namespace STRIDE.Blocks;

[StrideBlock("SourceGml")]
public sealed class SourceGmlBlock(
    string path,
    string geometryColumn = "geom",
    int batchSize = 1000,
    int schemaSampleRows = 256) : ISourceBlock
{
    private readonly int _batchSize = Math.Max(1, batchSize);
    private readonly int _schemaSampleRows = Math.Max(1, schemaSampleRows);
    private Schema? _cachedSchema;

    public Schema DeriveOutputSchema()
    {
        if (_cachedSchema is not null)
        {
            return _cachedSchema;
        }

        var sample = ReadSampleRecords();
        _cachedSchema = CatalogRecordUtilities.BuildSchema(sample, geometryColumn);
        return _cachedSchema;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(
        BlockContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var schema = DeriveOutputSchema();
        var buffer = new List<CatalogFeatureRecord>(_batchSize);

        await foreach (var record in ReadRecordsAsync(cancellationToken).ConfigureAwait(false))
        {
            buffer.Add(record);
            if (buffer.Count < _batchSize)
            {
                continue;
            }

            yield return CatalogRecordUtilities.BuildBatch(schema, buffer);
            buffer.Clear();
        }

        if (buffer.Count > 0)
        {
            yield return CatalogRecordUtilities.BuildBatch(schema, buffer);
        }
    }

    private List<CatalogFeatureRecord> ReadSampleRecords()
    {
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = false,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });

        var rows = new List<CatalogFeatureRecord>(_schemaSampleRows);
        while (reader.Read() && rows.Count < _schemaSampleRows)
        {
            if (reader.NodeType != XmlNodeType.Element || !IsFeatureBoundary(reader.LocalName))
            {
                continue;
            }

            var memberName = reader.LocalName;
            using var subtree = reader.ReadSubtree();
            var member = XElement.Load(subtree, LoadOptions.None);

            IEnumerable<XElement> featureElements = memberName == "featureMembers"
                ? member.Elements()
                : member.Elements().Take(1);

            foreach (var featureElement in featureElements)
            {
                rows.Add(GmlGeometryUtilities.ParseFeatureElement(featureElement, geometryColumn));
                if (rows.Count >= _schemaSampleRows)
                {
                    break;
                }
            }
        }

        return rows;
    }

    private async IAsyncEnumerable<CatalogFeatureRecord> ReadRecordsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element || !IsFeatureBoundary(reader.LocalName))
            {
                continue;
            }

            var memberName = reader.LocalName;
            using var subtree = reader.ReadSubtree();
            var member = await XElement.LoadAsync(subtree, LoadOptions.None, cancellationToken).ConfigureAwait(false);

            IEnumerable<XElement> featureElements = memberName == "featureMembers"
                ? member.Elements()
                : member.Elements().Take(1);

            foreach (var featureElement in featureElements)
            {
                yield return GmlGeometryUtilities.ParseFeatureElement(featureElement, geometryColumn);
            }
        }
    }

    private static bool IsFeatureBoundary(string localName)
        => localName is "featureMember" or "featureMembers";
}
