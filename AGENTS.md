# AGENTS

Purpose: give coding agents the minimum high-signal context to work safely and quickly in this repository.

## Start Here
- Architecture and design intent: [STRIDE_revised.md](STRIDE_revised.md)
- Developer quickstart and workflow examples: [README.md](README.md)
- Build policy and warning gates: [Directory.Build.props](Directory.Build.props)

## Required Commands
- Build everything: dotnet build STRIDE.slnx
- Run tests: dotnet test STRIDE.slnx -c Release -v minimal
- Run sample workflow: dotnet run --project src/STRIDE.Cli/STRIDE.Cli.csproj -- test-workflow.yaml

## Project Boundaries
- Contracts only: [src/STRIDE.Abstractions](src/STRIDE.Abstractions)
- Runtime orchestration and validation: [src/STRIDE.Core](src/STRIDE.Core)
- Workflow schema and YAML loading: [src/STRIDE.Schema](src/STRIDE.Schema)
- Concrete blocks: [src/STRIDE.Blocks](src/STRIDE.Blocks)
- Compile-time block registry generator: [src/STRIDE.SourceGen](src/STRIDE.SourceGen)
- CLI entrypoint: [src/STRIDE.Cli](src/STRIDE.Cli)

Keep dependency direction aligned with the blueprint in [STRIDE_revised.md](STRIDE_revised.md). Do not introduce Core dependencies into Abstractions or Blocks.

## Block Catalog Layout
- Sources: [src/STRIDE.Blocks/Sources](src/STRIDE.Blocks/Sources)
- Sinks: [src/STRIDE.Blocks/Sinks](src/STRIDE.Blocks/Sinks)
- Transforms/Attributes: [src/STRIDE.Blocks/Transforms/Attributes](src/STRIDE.Blocks/Transforms/Attributes)
- Transforms/Geometry: [src/STRIDE.Blocks/Transforms/Geometry](src/STRIDE.Blocks/Transforms/Geometry)
- Transforms/Quality: [src/STRIDE.Blocks/Transforms/Quality](src/STRIDE.Blocks/Transforms/Quality)
- Shared helpers: [src/STRIDE.Blocks/Common](src/STRIDE.Blocks/Common)

## Runtime Behavior To Preserve
- Cancellation model in CLI:
  - First Ctrl+C requests graceful drain.
  - Second Ctrl+C requests abort/rollback path.
  - Third Ctrl+C forces process exit.
  - See [src/STRIDE.Cli/Program.cs](src/STRIDE.Cli/Program.cs) and [src/STRIDE.Core/PipelineRunner.cs](src/STRIDE.Core/PipelineRunner.cs).
- Sink write modes are parsed centrally in [src/STRIDE.Blocks/Sinks/SinkWriteModeUtilities.cs](src/STRIDE.Blocks/Sinks/SinkWriteModeUtilities.cs). Preserve Transactional and BatchCommit semantics.
- Source-generated block registration is required for AOT safety. Keep [StrideBlock] usage and constructor signatures compatible with [src/STRIDE.SourceGen/BlockRegistryGenerator.cs](src/STRIDE.SourceGen/BlockRegistryGenerator.cs).

## Reprojection And Grids
- Reprojection implementation: [src/STRIDE.Blocks/Transforms/Geometry/TransformReprojectBlock.cs](src/STRIDE.Blocks/Transforms/Geometry/TransformReprojectBlock.cs)
- Workflow settings model: [src/STRIDE.Schema/WorkflowDefinition.cs](src/STRIDE.Schema/WorkflowDefinition.cs)
- YAML mapping: [src/STRIDE.Schema/WorkflowYamlLoader.cs](src/STRIDE.Schema/WorkflowYamlLoader.cs)

When workflows use TransformReproject, ensure grid configuration is valid:
- gridShiftDirectory should point to a real directory.
- Missing required datum grid tables must fail fast.

## Validation And Quality Gates
- DAG and schema propagation checks: [src/STRIDE.Core/DagValidator.cs](src/STRIDE.Core/DagValidator.cs)
- TreatWarningsAsErrors is enabled, including IL2xxx and IL3xxx warnings in [Directory.Build.props](Directory.Build.props).
- Do not bypass warning fixes with broad suppression unless explicitly requested.

## Testing Conventions
- Test projects are under [tests](tests).
- Keep tests split by concern and avoid returning to generic UnitTest1 naming.
- Prefer focused tests near changed behavior and run full test suite before finalizing larger refactors.
