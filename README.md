# STRIDE

STRIDE is a streaming ETL engine for tabular and geospatial data. Workflows are defined in YAML and executed by the CLI as a directed acyclic graph (DAG) of source, transform, and sink blocks.

## Highlights

- YAML workflow definitions with DAG validation before execution
- Source-generated block factory for fast startup and AOT-friendly block resolution
- Streaming pipeline execution with cancellation support
- Secret substitution from environment variables using `${VAR_NAME}` syntax
- Broad block catalog for CSV, JSON, GeoJSON, Excel, Postgres, and geospatial transforms

## Prerequisites

- .NET SDK `10.0.301` or newer (pinned in `global.json`)
- Optional: PostgreSQL/PostGIS for database-based workflows

## Quick Start

Run the bundled demo workflow:

```powershell
dotnet run --project src/STRIDE.Cli/STRIDE.Cli.csproj -- test-workflow.yaml
```

Inspect the generated output:

```powershell
Get-Content output/filtered.csv
```

The demo filters `sample-data/input.csv` with `id > 10` and writes results to `output/filtered.csv`.

## Run the PostgreSQL -> GeoJSON Template

Use the template workflow in `template-workflow-postgres-geojson.yaml`:

```powershell
dotnet run --project src/STRIDE.Cli/STRIDE.Cli.csproj -- template-workflow-postgres-geojson.yaml
```

Expected outputs:

- `output/features.geojson`

## Secure Secrets in Workflow YAML

Do not hardcode credentials in workflow files. Use environment variable placeholders instead:

```yaml
connectionString: "${STRIDE_PG_CONNECTION_STRING}"
```

Then set the variable in your shell before running:

```powershell
$env:STRIDE_PG_CONNECTION_STRING = "Host=...;Port=5432;Database=...;Username=...;Password=..."
```

If a referenced variable is missing, STRIDE fails fast with a clear startup error.

## CLI Usage

```text
dotnet run --project src/STRIDE.Cli/STRIDE.Cli.csproj -- <workflow.yaml>
```

- First `Ctrl+C`: requests graceful cancellation and drains in-flight work
- Second `Ctrl+C`: forces immediate shutdown

## Build and Test

Build all projects:

```powershell
dotnet build STRIDE.slnx
```

Run tests:

```powershell
dotnet test STRIDE.slnx -c Release -v minimal
```

## Repository Layout

- `src/STRIDE.Abstractions`: shared contracts and record/schema model
- `src/STRIDE.Blocks`: source, transform, and sink block implementations
- `src/STRIDE.Core`: runner, DAG validation, secret resolution, observability helpers
- `src/STRIDE.Schema`: workflow definition and YAML loading
- `src/STRIDE.SourceGen`: source generator for block registration
- `src/STRIDE.Cli`: command-line entry point
- `tests/*`: project test suites

## Related Documents

- `STRIDE_revised.md`: architecture and blueprint
