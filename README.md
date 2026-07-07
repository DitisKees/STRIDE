# STRIDE

STRIDE is a high-performance streaming ETL engine for spatial and tabular data, built with .NET 10 and designed for Native AOT.

## Current workspace status

This repository currently includes:
- Core solution and project structure
- DAG validation baseline
- Source-generated block factory baseline
- CSV -> Filter -> CSV demo pipeline
- Test workflow and sample data for a quick run

## Prerequisites

- .NET SDK 10.0.301 or newer (as pinned in global.json)

## Quick start

1. Run the demo workflow:

```powershell
dotnet run --project src/STRIDE.Cli/STRIDE.Cli.csproj -- test-workflow.yaml
```

2. View the generated output:

```powershell
Get-Content output/filtered.csv
```

Expected output rows include IDs greater than 10 from sample-data/input.csv.

## Run tests

```powershell
dotnet test STRIDE.slnx -c Release -v minimal
```

## Key files

- test-workflow.yaml: runnable demo pipeline configuration
- sample-data/input.csv: demo source input
- output/filtered.csv: demo sink output (generated at runtime)
- STRIDE_revised.md: technical blueprint
- implementation_plan.md: implementation roadmap

## Notes

- The repository ignores build artifacts, runtime output, and local IDE settings via .gitignore.
- Native AOT and analyzer settings are configured in project files and shared build props.
