using STRIDE.Abstractions;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace STRIDE.Blocks;

[StrideBlock("SourceWfs")]
public sealed class SourceWfsBlock(
    string url,
    string geometryColumn = "geom",
    int batchSize = 1000,
    string? accept = null) : ISourceBlock
{
    private readonly int _batchSize = Math.Max(1, batchSize);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Schema? _cachedSchema;
    private IReadOnlyList<CatalogFeatureRecord>? _cachedRecords;

    public Schema DeriveOutputSchema()
    {
        EnsureLoadedAsync(CancellationToken.None).GetAwaiter().GetResult();
        return _cachedSchema!;
    }

    public async IAsyncEnumerable<IRecordBatch> ExecuteAsync(
        BlockContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var records = _cachedRecords!;
        var schema = _cachedSchema!;

        for (var i = 0; i < records.Count; i += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(_batchSize, records.Count - i);
            var slice = new CatalogFeatureRecord[count];
            for (var row = 0; row < count; row++)
            {
                slice[row] = records[i + row];
            }

            yield return CatalogRecordUtilities.BuildBatch(schema, slice);
            await Task.Yield();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cachedSchema is not null && _cachedRecords is not null)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedSchema is not null && _cachedRecords is not null)
            {
                return;
            }

            using var client = new HttpClient();
            if (!string.IsNullOrWhiteSpace(accept))
            {
                client.DefaultRequestHeaders.Accept.ParseAdd(accept);
            }

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            IReadOnlyList<CatalogFeatureRecord> records = contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                ? await LoadGeoJsonRecordsAsync(stream, cancellationToken).ConfigureAwait(false)
                : await LoadGmlRecordsAsync(stream, cancellationToken).ConfigureAwait(false);

            _cachedRecords = records;
            _cachedSchema = CatalogRecordUtilities.BuildSchema(records, geometryColumn);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<IReadOnlyList<CatalogFeatureRecord>> LoadGeoJsonRecordsAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return GeoJsonFeatureUtilities.ParseFeatureCollection(document.RootElement, geometryColumn);
    }

    private async Task<IReadOnlyList<CatalogFeatureRecord>> LoadGmlRecordsAsync(Stream stream, CancellationToken cancellationToken)
    {
        var records = new List<CatalogFeatureRecord>();

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

            if (reader.NodeType != XmlNodeType.Element
                || reader.LocalName is not ("featureMember" or "member" or "featureMembers"))
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
                records.Add(GmlGeometryUtilities.ParseFeatureElement(featureElement, geometryColumn));
            }
        }

        return records;
    }
}
