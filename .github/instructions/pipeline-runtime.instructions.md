---
description: "Use when editing PipelineRunner, DagValidator, sinks, cancellation behavior, error policy handling, or spill and backpressure behavior."
name: "STRIDE Pipeline Runtime Rules"
applyTo: "src/STRIDE.Core/**/*.cs,src/STRIDE.Blocks/Sinks/**/*.cs,src/STRIDE.Blocks/Common/**/*.cs"
---
# STRIDE Pipeline Runtime Rules

## Canonical References
- Runtime architecture: [STRIDE_revised.md](../../STRIDE_revised.md)
- Pipeline orchestration: [src/STRIDE.Core/PipelineRunner.cs](../../src/STRIDE.Core/PipelineRunner.cs)
- DAG validation: [src/STRIDE.Core/DagValidator.cs](../../src/STRIDE.Core/DagValidator.cs)
- Spill implementation: [src/STRIDE.Core/SpillManager.cs](../../src/STRIDE.Core/SpillManager.cs)
- Sink write mode parser: [src/STRIDE.Blocks/Sinks/SinkWriteModeUtilities.cs](../../src/STRIDE.Blocks/Sinks/SinkWriteModeUtilities.cs)

## Must Keep True
- First cancellation path is graceful drain.
- Abort path must allow transactional rollback behavior in sinks.
- StopBranch must isolate failure to dependent branch and allow independent branches to complete.
- ErrorPolicy.Ignore must log structured error context and continue.

## Pipeline And Sink Semantics
- Preserve bounded-channel backpressure behavior.
- Do not bypass sink writeMode handling.
- Transactional file sinks must promote staged output only on successful completion.
- BatchCommit behavior must keep explicit partial-commit semantics on failure.

## Validation Semantics
- DAG validation remains fail-fast and aggregated.
- Keep type, parameter, input, and schema-propagation checks intact.
- New runtime settings must be wired through WorkflowDefinition, WorkflowYamlLoader, and PipelineRunner parameter merge.

## Testing Expectations For Runtime Changes
- Add or update tests for cancellation, branch stopping, and sink rollback behavior.
- Add or update tests for validator changes.
- Run: dotnet build STRIDE.slnx
- Run: dotnet test STRIDE.slnx --no-build -v minimal
