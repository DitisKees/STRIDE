# Technical Blueprint: STRIDE Engine (.NET 10 / C# 14)

STRIDE is a high-performance, streaming ETL engine focused on spatial data (but equally capable with non-spatial tabular data). The design is built around three load-bearing decisions: an **AOT-first architecture with zero runtime code generation**, a **config model that expresses the pipeline as an explicit processing graph (DAG)**, and a **schema-first, columnar in-memory record model**. Everything else follows from these.

This document is intended as a standalone reference for design discussion and implementation.

---

## 1. High-Level Architecture & Solution Structure

STRIDE is a headless streaming processor built as a modular .NET solution. The pipeline is a **directed acyclic graph (DAG)** of blocks connected by bounded channels. The engine runs identically whether invoked from the CLI or embedded in another host.

### Solution Structure

| Project | Type | Responsibility |
| --- | --- | --- |
| **`STRIDE.Abstractions`** | Class Library | Stable public contracts only: `ITransformBlock`, `ISourceBlock`, `ISinkBlock`, the record model (`IRecordBatch`, `Schema`, `FieldType`), `ErrorPolicy`, `BlockContext`, and the `[StrideBlock]` attribute. Has **no** dependency on Core. This is the package third-party/first-party block authors reference. |
| **`STRIDE.Core`** | Class Library | Pipeline orchestrator (`PipelineRunner`), DAG builder + validator, `System.Threading.Channels` wiring, `SpillManager`, `SecretResolver`, metrics, cancellation, checkpointing. References `Abstractions`. |
| **`STRIDE.Schema`** | Class Library | Strongly typed `workflow.yaml` models and the AOT-safe YAML loader. References `Abstractions`. |
| **`STRIDE.Blocks`** | Class Library | Concrete block implementations (sources, sinks, geometry, quality, attributes). References `Abstractions` only (not Core). |
| **`STRIDE.SourceGen`** | Roslyn Source Generator (analyzer) | Scans for `[StrideBlock("Type")]` at **compile time** and emits a static `BlockRegistry` (type string → factory delegate). Eliminates runtime reflection/assembly scanning. Also emits parameter binders. |
| **`STRIDE.Cli`** | Console App (Native AOT) | Thin runner: loads config, builds the pipeline, executes, reports. References Core + Blocks + Schema. |
| **`STRIDE.*.Tests`** | xUnit projects | One test project per production project. |

**Dependency direction (no cycles):**

```
Abstractions  ←  Schema
     ↑              ↑
     └── Blocks     │
     ↑              │
Core ──────────────►│
     ↑
Cli ─► Core, Blocks, Schema
SourceGen ─► (referenced as analyzer by Blocks & Cli)
```

### Design principle: AOT-first, zero runtime code generation

Every mechanism that classically relies on runtime IL emission or reflection is replaced by a **compile-time** equivalent. See [Section 8](#8-native-aot-strategy) for the complete matrix. This is a load-bearing decision of the design: it is applied consistently, not per-block.

---

## 2. The Record Model

What flows through the channels is defined explicitly, because it dictates whether the zero-allocation goal is achievable — a `Dictionary<string, object>` would box every value and defeat the entire memory strategy. The engine therefore uses a **schema-first, columnar batch** model.

### 2.1 Schema

A `Schema` is an immutable, ordered list of typed fields, resolved and validated **before** execution. It flows with the data so every block knows its input/output shape statically.

```csharp
public enum FieldType : byte
{
    Boolean, Int32, Int64, Float64, Utf8String, DateTimeUtc, Geometry, Null
}

public sealed record FieldDef(string Name, FieldType Type, bool Nullable);

public sealed class Schema
{
    public ImmutableArray<FieldDef> Fields { get; }
    public int GeometryFieldIndex { get; } // -1 when non-spatial
    // O(1) name → ordinal lookup built once.
    public bool TryGetOrdinal(string name, out int ordinal);
}
```

### 2.2 Columnar batches (Apache Arrow-aligned)

Records travel in **batches**, not one-by-one, to amortize channel overhead and enable SIMD. Each batch is a set of typed columns; the in-memory layout matches the Arrow format used by the `SpillManager`, so spilling is a memcpy rather than a re-encode.

```csharp
public interface IRecordBatch : IDisposable
{
    Schema Schema { get; }
    int RowCount { get; }
    ReadOnlySpan<T> Column<T>(int ordinal) where T : unmanaged; // primitives, zero-copy
    Utf8StringColumn StringColumn(int ordinal);                  // offset+blob, no string alloc
    GeometryColumn GeometryColumn(int ordinal);                  // NTS Geometry[] slice
}
```

- Primitive columns are `unmanaged` spans backed by pooled `ArrayPool<byte>` / `MemoryPool<T>` buffers — **no per-value boxing**.
- Strings use an Arrow-style `(offsets[], utf8 blob)` representation; parsers write UTF-8 bytes directly, avoiding intermediate `string` allocation until a value is actually materialized.
- Geometry columns hold an `NTS Geometry[]` slice. Geometry is inherently reference-typed; batching keeps GC pressure bounded and predictable.
- Batches are **immutable by contract**. Blocks that "modify" data (reproject, calculator) produce a **new output batch**, reusing unchanged columns by reference (structural sharing) and pooling the columns they replace. Immutability is what makes safe parallelism possible — there is no in-place mutation shared across workers (see [Section 4.3](#43-parallelism-and-thread-safety)).

**Batch size** is configurable (default 1,000 rows) and participates in backpressure.

---

## 3. Configuration (`workflow.yaml`) — Graph Model

The pipeline must be able to express branches, joins (e.g., `SpatialJoin`'s second input), conditional routing, and multiple sinks. The configuration is therefore an **explicit DAG**: every node has an `id`, and edges are declared by referencing upstream node ids through **named input ports**. A flat ordered list with a single sink cannot represent these topologies and is deliberately avoided.

### 3.1 Example

```yaml
version: "1.0"
name: "Large-Scale Geometry Processing"

# Optional engine-level settings
settings:
  batchSize: 1000
  maxDegreeOfParallelism: 8
  spillDirectory: "./.stride-spill"
  spillThresholdBytes: 1073741824   # 1 GiB per stateful block before spilling
  errorLog: "./output/error_log.ndjson"

nodes:
  - id: source_features
    type: SourcePostGis
    params:
      connectionString: ${DB_CONNECTION_STRING}
      query: "SELECT id, geom, attributes FROM basis_registratie"
    errorPolicy: StopPipeline           # default

  - id: lookup_municipalities
    type: SourceGeoPackage
    params:
      path: "./ref/gemeenten.gpkg"
      table: "gemeente"

  - id: reproject_rd
    type: TransformReproject
    inputs: { in: source_features }     # named-port edge
    params:
      sourceCrs: "EPSG:4326"
      targetCrs: "EPSG:28992"
    errorPolicy: StopBranch

  - id: enrich
    type: TransformSpatialJoin
    inputs:
      in: reproject_rd                  # streaming side
      lookup: lookup_municipalities     # static, indexed side (2nd input!)
    params:
      predicate: Intersects

  - id: buffer
    type: TransformBuffer
    inputs: { in: enrich }
    params: { distance: 25.0 }
    errorPolicy: Ignore

  - id: route
    type: TransformConditionalSplitter
    inputs: { in: buffer }
    params:
      branches:                          # each yields a named output port
        - name: large
          when: "area > 10000"
        - name: small
          when: "area <= 10000"

sinks:
  - id: sink_large
    type: SinkGeoJson
    inputs: { in: "route:large" }        # reference a specific output port
    params: { path: "./output/large_rd.geojson" }

  - id: sink_small
    type: SinkPostGis
    inputs: { in: "route:small" }
    params:
      connectionString: ${DB_CONNECTION_STRING}
      table: "processed_small"
      writeMode: Transactional           # see §5.3
```

### 3.2 Rules

- **Naming is consistent** everywhere: `Source*`, `Transform*`, `Sink*`. The catalog in [Section 6](#6-transformation-block-catalog) uses these exact type strings.
- Edges are `inputs: { <portName>: <upstreamNodeId[:outputPort]> }`. Most blocks have a single input port `in` and a single implicit output; multi-input blocks (`SpatialJoin`, `Difference`, `SnapGeometries`) and multi-output blocks (`TransformConditionalSplitter`) declare additional ports.
- `sinks:` is a first-class list; **multiple sinks** are supported. Internally sinks are just nodes with no outgoing edges.
- The graph is validated at load time (see [Section 7](#7-workflow-validation)).

---

## 4. Streaming, Memory & Concurrency

### 4.1 Backpressure

Blocks are connected by **bounded** `Channel<IRecordBatch>`. A slow block causes its input channel to fill; the upstream producer's `WriteAsync` awaits capacity, propagating backpressure to the source. This caps memory regardless of dataset size.

### 4.2 Streaming vs. blocking (materializing) operators — explicit classification

Pure-streaming blocks and operators that must see the whole stream have fundamentally different memory behavior, so the engine classifies every block explicitly:

| Class | Behavior | Examples |
| --- | --- | --- |
| **Streaming** | Emits output batches as input batches arrive; O(1) state. | `Reproject`, `Buffer`, `Filter`, `Calculator`, `Centroid`, `Envelope`, `SchemaMapper`, `Reproject`, all sources/sinks. |
| **Materializing** | Must consume the full input before emitting; bounded by `spillThresholdBytes`, then **spills to disk** via `SpillManager`. | `Aggregator`, `Dissolve`, `DeduplicateRecords`, `LineMerger`, `SpatialJoin` (builds the lookup index side only). |

Materializing blocks declare `IsBlocking => true` and receive a `SpillManager` handle from `BlockContext`. Their accumulator (e.g., the `Aggregator` hash table) is **not** an unbounded in-memory `ConcurrentDictionary`; when it exceeds the threshold it is partitioned and spilled, then merged in a streaming pass. This makes the memory strategy internally consistent.

### 4.3 Parallelism and thread-safety

- **Data-parallelism** is applied **within** a streaming block across independent batches using a bounded `Parallel.ForEachAsync` (capped by `maxDegreeOfParallelism`). Because batches are **immutable** and each worker produces a **new** output batch, there is no shared-mutable-state hazard. In-place geometry mutation across workers is prohibited precisely because it is unsafe under parallelism.
- **Ordering:** blocks are order-preserving by default (workers emit into an ordered reassembly buffer keyed by batch sequence number). Order preservation can be disabled per block (`preserveOrder: false`) when the downstream doesn't care, to reduce latency.
- **NTS thread-safety rules** are codified:
  - `STRtree` and any other spatial index are **built once, single-threaded, then frozen** before any concurrent query. `SpatialJoin`/`SnapGeometries` build their index in the block's materializing phase, then query it read-only from parallel workers (STRtree queries are safe once built).
  - Each parallel worker uses its **own** `GeometryFactory` / operation instance (they are not thread-safe to share). Factories are pooled per-worker, not per-record.

### 4.4 Spill-to-disk (`SpillManager`)

- Uses `System.IO.MemoryMappedFiles` over the Arrow-format columnar batches, so spilling is a buffer copy with no re-encoding.
- Spill files live under `settings.spillDirectory`, are created with `FileOptions.DeleteOnClose` where possible, and are **registered for cleanup** on both normal completion and crash (a process-exit + `IHostApplicationLifetime` hook removes orphaned spill files).
- Each materializing block gets an isolated spill scope; parallel branches never share spill files.

---

## 5. Blocks: Contracts, Sources & Sinks

### 5.1 Block contract

```csharp
public interface ITransformBlock
{
    Schema DeriveOutputSchema(IReadOnlyDictionary<string, Schema> inputSchemas);
    bool IsBlocking { get; }
    IAsyncEnumerable<IRecordBatch> ExecuteAsync(BlockContext ctx, CancellationToken ct);
}
```

- `DeriveOutputSchema` runs during validation so the **schema is propagated and checked across the whole graph before any I/O** (fixes the "schema propagation undefined" gap). `SchemaMapper`, `Calculator`, etc. compute their output schema here.
- `BlockContext` exposes named input readers (`ChannelReader<IRecordBatch>` per port), the `SpillManager`, the metrics sink, the error handler, and the `CancellationToken`.

### 5.2 Blocks are discovered at compile time

```csharp
[StrideBlock("TransformBuffer")]
public sealed class BufferBlock : ITransformBlock { /* ... */ }
```

`STRIDE.SourceGen` emits:

```csharp
// <auto-generated>
internal static partial class BlockRegistry
{
    public static ITransformBlock Create(string type, BlockParams p) => type switch
    {
        "TransformBuffer"    => new BufferBlock(p.GetDouble("distance")),
        "TransformReproject" => new ReprojectBlock(p.GetString("sourceCrs"), p.GetString("targetCrs")),
        // ...one arm per [StrideBlock]
        _ => throw new UnknownBlockTypeException(type)
    };
}
```

No `Assembly.GetTypes()`, no `Activator.CreateInstance` — fully trim/AOT-safe and statically verifiable.

### 5.3 Source & sink implementations (unchanged intent, AOT-corrected)

| Direction | Type string | Implementation notes |
| --- | --- | --- |
| Source | `SourcePostGis` | `Npgsql` + `Npgsql.NetTopologySuite` plugin, `IAsyncEnumerable<T>`, geometries deserialized directly to NTS. Npgsql is configured with the AOT-compatible connection setup (no reflection-based type mapping; explicit type mappers registered at startup). |
| Source | `SourceGml` | Async forward-only `XmlReader` (`XmlReaderSettings.Async = true`) + schema-aware state machine → NTS. |
| Source | `SourceGeoJson` | `Utf8JsonReader` loop over `FileStream`. Geometry parsed with **`NetTopologySuite.IO.GeoJSON4STJ`** (System.Text.Json), **not** the Newtonsoft-based `GeoJSON` package. |
| Source | `SourceJson` | Streaming `Utf8JsonReader`, attributes only. |
| Source | `SourceShapefile` | `MemoryMappedFiles` random-access over `.shp`/`.shx`, `ReadOnlySpan<byte>` slices → NTS. |
| Source | `SourceGeoPackage` | `Microsoft.Data.Sqlite` streaming; strip GPKG binary header, feed remainder to NTS `WKBReader`. |
| Source | `SourceCsv` | `ReadOnlySpan<char>` line/field slicing, no intermediate strings. |
| Source | `SourceExcel` | **Streaming SAX** reader (`OpenXmlReader`), row-by-row — not the DOM `WorkbookPart` API. |
| Source | `SourceWfs` | `HttpClient` streaming WFS 2.0.0/OGC API Features, `IAsyncEnumerable<T>`. |
| Sink | `SinkPostGis` | `NpgsqlBinaryImporter` (COPY). **Transactionality is explicit** — see below. |
| Sink | `SinkGeoJson` | `Utf8JsonWriter` on `FileStream` + GeoJSON4STJ writer. |
| Sink | `SinkJson` | `Utf8JsonWriter` streaming. Writing null/empty values is optional |
| Sink | `SinkCsv` | `StreamWriter` + `string.Create` allocation-free formatting. |
| Sink | `SinkExcel` | Streaming zip/OpenXML writer, no full-workbook staging. |
| Sink | `SinkWfsT` | Batched WFS-T POST inserts/updates over pooled `HttpClient`. |

**Sink transactionality (`writeMode`)** — resolves the "COPY vs. error policy" gap:

- `Transactional` (default for `SinkPostGis`): the entire COPY runs inside one transaction. On `StopPipeline`/`StopBranch` the transaction is **rolled back** (all-or-nothing). Best correctness, higher memory/lock cost.
- `BatchCommit(n)`: commit every *n* batches; on failure, everything up to the last committed batch persists, and the failure point is recorded to the error log for resume.
- File sinks flush per batch and, on `StopBranch`, close cleanly leaving a valid partial file with the row count logged.

---

## 6. Transformation Block Catalog

Powered by **NetTopologySuite (NTS)**. Streaming blocks are parallelized across batches with bounded `Parallel.ForEachAsync`; materializing blocks follow [Section 4.2](#42-streaming-vs-blocking-materializing-operators--explicit-classification).

### Geometry (all streaming unless noted)

| Type | Notes |
| --- | --- |
| `TransformReproject` | Reprojects via **`ProjNet`** (managed, no native deps → preserves single-file AOT). Emits a new geometry column; per-worker `CoordinateSequence` transform. *(See CRS note below.)* |
| `TransformBuffer` | `BufferOp`. |
| `TransformSpatialJoin` | **Multi-input** (`in`, `lookup`). Materializes/index-builds the `lookup` side into a frozen `STRtree`, then streams `in`, querying read-only across workers. |
| `TransformCreateGeometry` | Builds `Point` from X/Y or Lat/Lon columns, or re-hydrates via `WKTReader`/`WKBReader`. |
| `TransformRemoveGeometry` | Drops the geometry column → pure tabular schema. |
| `TransformCentroid` | `Geometry.Centroid`. |
| `TransformEnvelope` | `EnvelopeInternal` → bbox polygon. |
| `TransformConvexHull` | `ConvexHull`. |
| `TransformDissolve` | **Materializing.** Groups by attribute key (spill-aware), then `CascadedPolygonUnion` / `LineMerger`. |
| `TransformDifference` | Multi-input; `STRtree` pre-filter to skip non-overlapping features. |
| `TransformAffine` | `AffineTransformation`; emits new geometry column (no shared in-place mutation). |
| `TransformLineMerger` | **Materializing** (needs all touching segments). |
| `TransformSplitGeometry` | Splits by cutting line; parallel across batches. |
| `TransformDensify` | `Densifier`; pre-computes target `CoordinateSequence` size. |

> **CRS accuracy note:** `ProjNet` is chosen to keep the AOT single-binary promise. For datums requiring grid-shift precision (e.g., NL RD/EPSG:28992 with the `rdtrans`/NTv2 correction), `ProjNet` alone may not meet survey-grade accuracy. If that precision is required, ship the NTv2 grid as an embedded resource and load it through `ProjNet`'s grid support; a native-PROJ backend remains an opt-in, non-AOT build variant. This tradeoff is called out explicitly so it is a conscious choice.

### Quality

| Type | Notes |
| --- | --- |
| `TransformValidateGeometry` | `IsValidOp`; invalid records routed per `ErrorPolicy`. |
| `TransformSimplify` | Douglas-Peucker or topology-preserving. |
| `TransformSnapGeometries` | Multi-input; frozen `STRtree` on reference set. |
| `TransformRemoveDuplicateVertices` | Coordinate filtering; produces new coordinate sequences (immutability preserved). |
| `TransformDeduplicateRecords` | **Materializing.** See collision-safe design below. |

**`TransformDeduplicateRecords` — no silent data loss:** a bare 64-bit hash in a `HashSet<long>` would drop legitimate rows on hash collision. This block instead uses the hash only as a **first-level bucket key**, then confirms equality on the actual key bytes (`ReadOnlySpan<byte>` comparison) before treating a row as a duplicate. Structure: `Dictionary<long, SmallList<KeyRef>>` where `KeyRef` points into a pooled key blob; spills when over threshold. Collisions cost an extra comparison, never a dropped record.

### Attributes

| Type | Notes |
| --- | --- |
| `TransformFilter` | Predicate over attributes (AOT-safe expressions — see §8.2). |
| `TransformCalculator` | Field math/value transforms. |
| `TransformSchemaMapper` | Rename/restructure/select; defines output schema in `DeriveOutputSchema`. |
| `TransformRegexExtractor` | **`[GeneratedRegex]`** source-generated patterns (not `RegexOptions.Compiled`, which is a no-op under AOT). |
| `TransformDateTime` | `DateTime.TryParse(ReadOnlySpan<char>, …)` → ISO-8601 UTC. |
| `TransformStringManipulator` | `string.Create`-based casing/trim/pad. |
| `TransformNullHandler` | Type-safe default substitution. |
| `TransformAggregator` | **Materializing**, spill-aware group-by (Sum/Avg/Min/Max/Count). Uses a partitioned hash-aggregate, not an unbounded `ConcurrentDictionary`. |
| `TransformConditionalSplitter` | **Multi-output.** Routes to named ports by expression rules (AOT-safe evaluator). |
| `TransformValueLookup` | O(1) enrichment from a dictionary loaded at startup. |

---

## 7. Workflow Validation

Before any I/O, `STRIDE.Core` runs a **fail-fast validation pass**:

1. **Structural:** every `inputs` reference resolves to an existing node/port; no dangling edges; **DAG acyclicity** (topological sort; reject cycles with the offending path); Transfrom blocks and sinks are force to have at least 1 input. Transform block requiring multiple inputs are forced to have the number of required inputs.
2. **Type registry:** every `type` exists in the source-generated `BlockRegistry`; otherwise `UnknownBlockTypeException` listing valid types.
3. **Parameters:** required params present and coercible to the target types (validated by generated binders).
4. **Schema propagation:** `DeriveOutputSchema` is invoked in topological order; type mismatches (e.g., `Calculator` referencing a non-numeric field, sink expecting a geometry column that was removed upstream) fail here with the node id and field name.
5. **CRS codes:** `Reproject` source/target EPSG codes are resolved against the CRS provider at validation time, not first-record time.

Validation errors are aggregated and reported together (not one-at-a-time).

---

## 8. Native AOT Strategy

AOT is a **first-class constraint**, applied uniformly. `STRIDE.Cli` sets `<PublishAot>true</PublishAot>`, `<InvariantGlobalization>` as appropriate, and builds with `<TrimmerRootAssembly>` only where unavoidable.

### 8.1 Reflection/codegen replacements

| Avoided mechanism | AOT problem | Approach |
| --- | --- | --- |
| `Expression.Compile()` for Filter/Calculator/Splitter/Lookup | Runtime IL emit — unsupported/interpreted under AOT | **Precompiled expression evaluator**: expressions are parsed at load time into an immutable evaluator tree over the columnar schema (visitor over `ReadOnlySpan` columns). No IL emit. Deterministic, trim-safe, fast. |
| `RegexOptions.Compiled` | No-op under AOT | **`[GeneratedRegex]`** source generator (compile-time DFA). |
| `YamlDotNet` reflection deserialize | Trimmer can't see the model | AOT-safe loader using YamlDotNet's **static/typed context** (explicit `ITypeConverter`s for the known model) — or a source-generated binder over the parsed node graph. Models are trimmer-rooted. |
| `System.Text.Json` reflection | Trim/AOT warnings | **`JsonSerializerContext`** source generators for all config/GeoJSON DTOs. |
| Assembly scanning for block types | Reflection + trimming | **`STRIDE.SourceGen`** emits the static `BlockRegistry` and param binders. |
| Newtonsoft-based NTS GeoJSON | Reflection + not AOT-friendly | **`NetTopologySuite.IO.GeoJSON4STJ`**. |
| `Npgsql` reflection type mapping | AOT | Explicit type mapper registration at startup; NetTopologySuite plugin configured statically. |
| `Microsoft.Data.Sqlite` native | Native asset | `SQLitePCLRaw` bundled provider; verified under AOT publish. |

### 8.2 The expression evaluator (Filter/Calculator/TransformConditionalSplitter)

A small, sandboxed grammar (comparisons, arithmetic, boolean logic, string ops, null checks, field references). At load time an expression string like `area > 10000 && status == "active"` is parsed into an `IExprNode` tree bound to column ordinals. Evaluation walks the tree against a batch's `ReadOnlySpan` columns — allocation-free on the hot path and fully AOT-compatible. This provides dynamic expression capability without runtime codegen (no Expression Trees / IL emit).

### 8.3 CI verification

The CI pipeline runs `dotnet publish -c Release -r <RID> /p:PublishAot=true` and **fails on any trim/AOT analyzer warning** (`<TreatWarningsAsErrors>` for `IL2xxx`/`IL3xxx`). This prevents AOT regressions from creeping in.

---

## 9. Secret Management (`SecretResolver`)

Resolving `${VAR}` by regex-replacing the **raw YAML string before parsing** is a YAML-injection/corruption risk (a secret containing `:`, quotes, or newlines could break the document or inject structure). The resolver therefore operates on the **parsed YAML node graph**:

1. Load `.env` (via a configuration provider) and process environment variables.
2. Parse the YAML into a node graph with YamlDotNet.
3. Walk **scalar nodes only**, replacing `${VAR}` occurrences with the variable's value **as a string scalar** — so the value can never alter document structure.
4. Deserialize the resolved node graph into the typed model.

Policy for a missing variable is explicit and configurable: **fail-fast by default** (unresolved `${VAR}` → validation error naming the variable), with an opt-in `allowEmpty` mode. Resolved secrets are never written to logs or metrics.

---

## 10. Error Handling Matrix (`ErrorPolicy`)

Each block wraps its per-record/per-batch processing and applies its configured policy:

1. **`StopPipeline` (default):** propagate to `PipelineRunner`; cancel the shared `CancellationTokenSource`; all channels complete; sinks in `Transactional` mode roll back; process exits with code `1`.
2. **`StopBranch`:** complete this block's output channel(s). Downstream blocks observe completion and finish gracefully; **independent branches of the DAG keep running**. Any `Transactional` sink fed *only* by the stopped branch rolls back; sinks fed by surviving branches commit.
3. **`Ignore`:** write the offending record + error context to `error_log.ndjson` (structured, one JSON object per line) and continue with the next record.

Granularity is explicit: `Ignore` is per-record; `StopBranch`/`StopPipeline` are per-branch/global. Because the topology is a real DAG, "parallel branches continue" has a well-defined meaning — sibling branches that do not depend on the stopped branch keep running.

---

## 11. Observability, Cancellation & Resume

Beyond the `error_log.ndjson` failure record, the engine provides full runtime observability:

- **Structured logging** via `Microsoft.Extensions.Logging` with a console/JSON provider; per-node scopes.
- **Metrics:** per-node rows in/out, batches, bytes, wall time, current channel occupancy (backpressure visibility), spill bytes. Exposed as counters and optionally an OpenTelemetry exporter.
- **Progress reporting:** periodic throughput (records/s) per node to stderr; sources report percent-complete when the total is knowable.
- **Cancellation:** a single linked `CancellationToken` threads through every `ExecuteAsync`; Ctrl-C triggers graceful drain (finish in-flight batches, flush/rollback sinks per policy) with a hard-stop fallback.
- **Checkpoint/resume (optional):** for long jobs, sources that expose a stable ordering key (e.g., PostGIS primary key, file offset) can emit periodic checkpoints; on restart the job resumes after the last committed checkpoint. Enabled per source via `checkpoint: { every: 100000 }`.

---

## 12. Extensibility

- Third-party blocks reference **`STRIDE.Abstractions`** only, annotate a class with `[StrideBlock("MyType")]`, and reference `STRIDE.SourceGen` — the registry picks them up at compile time.
- **Explicit scope decision:** because Native AOT precludes runtime plugin loading, extensibility is **compile-time** (rebuild with the extra block assembly), not drop-in DLL discovery. If dynamic runtime plugins are ever required, a **separate non-AOT (JIT) build variant** of `STRIDE.Cli` can be produced that loads plugins via `AssemblyLoadContext`. The two models are kept from conflicting by isolating the choice in the Cli project, not the block contracts.

---

## 13. GitHub Actions CI/CD (`/.github/workflows/pipeline.yml`)

### On pull request
- `dotnet format --verify-no-changes`
- `dotnet build -c Release` with `TreatWarningsAsErrors` including trim/AOT analyzers (`IL2xxx`/`IL3xxx`).
- `dotnet test -c Release` with coverage (`--collect:"XPlat Code Coverage"`), gate at **≥ 80%** line coverage.
- A **matrix AOT smoke publish** for `linux-x64` to catch AOT breakage early on PRs (fast fail).

### On merge to main
- Full test suite.
- **Native AOT** single-file publish per RID: `linux-x64`, `win-x64`, `osx-arm64`, `osx-x64`.
  ```bash
  dotnet publish STRIDE.Cli -c Release -r <RID> /p:PublishAot=true
  ```
- A **runtime self-test** stage runs each produced binary against a small sample workflow (verifies the AOT binary actually executes reflection-free paths — e.g., YAML load, expression eval, GeoJSON round-trip).
- Attach binaries as assets to a new GitHub Release.

---

## 14. Testing Strategy

- One xUnit project per production project; **≥ 80%** line coverage gate.
- **Golden-file tests** for each source/sink (byte-for-byte round-trips on small fixtures).
- **Property/fuzz tests** for the expression evaluator and `SecretResolver` (including secrets with `:`/newlines/quotes to prove no YAML injection).
- **Concurrency tests** asserting order preservation and STRtree freeze-before-query invariants under `maxDegreeOfParallelism > 1`.
- **Spill tests** forcing `spillThresholdBytes` low to exercise `Aggregator`/`Dissolve`/`Dedup` disk paths and cleanup.
- **AOT integration test** in CI (the runtime self-test above) — the ultimate guard against reflection creeping back in.

---

## Appendix A — Key Design Decisions & Rationale

| # | Concern | Decision |
| --- | --- | --- |
| 1 | **AOT vs. dynamic code** (Expression.Compile, `RegexOptions.Compiled`, reflection YAML/JSON, assembly scanning are all AOT-hostile) | §8: precompiled expression evaluator, `[GeneratedRegex]`, source-generated block registry + binders, `JsonSerializerContext`, AOT-safe YAML loader; CI fails on trim/AOT warnings + runtime self-test. |
| 2 | **Expressing a real pipeline topology** (branches, joins, multiple sinks) | §3: explicit node graph with named input/output ports, multi-input & multi-output blocks, multiple first-class sinks. |
| 3 | **Zero-allocation record flow** (avoid boxing) | §2: schema-first, Arrow-aligned columnar immutable batches; primitives as `unmanaged` spans, UTF-8 string columns, pooled buffers. |
| 4 | **Streaming vs. blocking operators** | §4.2: explicit Streaming/Materializing classification; materializing blocks are spill-aware, no unbounded `ConcurrentDictionary`. |
| 5 | **Safe secret resolution** | §9: resolve on parsed node graph, scalar-only, fail-fast on missing vars. |
| 6 | **Clean contracts & extensibility** | §1 & §12: `STRIDE.Abstractions` contract package; compile-time extensibility; documented AOT-vs-plugins tradeoff. |
| 7 | **NTS thread-safety under parallelism** | §4.3: immutable batches, per-worker factories, STRtree freeze-before-query, ordered reassembly. |
| 8 | **Deduplication without data loss** | §6 Quality: hash-as-bucket + exact key comparison; spill-aware. |
| 9 | **AOT-friendly GeoJSON** | §5.3/§8: `NetTopologySuite.IO.GeoJSON4STJ` (System.Text.Json), not the Newtonsoft-based reader. |
| 10 | **Reprojection vs. single-binary AOT** | §6 CRS note: managed `ProjNet` default; NTv2 grid embedded; native-PROJ as opt-in non-AOT variant. |
| 11 | **Consistent block naming** | §3/§6: `Source*`/`Transform*`/`Sink*` naming throughout. |
| 12 | **Fail-fast configuration validation** | §7: structural + type + param + schema + CRS validation before any I/O. |
| 13 | **Operability** | §11: logging, metrics, progress, linked cancellation, optional checkpoint/resume. |
| 14 | **Sink transactionality vs. error policy** | §5.3 & §10: `writeMode` (Transactional / BatchCommit / file-flush) with defined rollback semantics. |
| 15 | **True streaming Excel** | §5.3: SAX `OpenXmlReader` source, streaming zip writer sink (no DOM staging). |
| 16 | **Schema propagation** | §5.1 & §7: `DeriveOutputSchema` propagated and validated in topological order. |
