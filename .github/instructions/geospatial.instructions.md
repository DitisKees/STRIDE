---
description: "Use when editing geospatial sources, geometry transforms, reprojection, CRS handling, grid-shift setup, or spatial sink behavior."
name: "STRIDE Geospatial Rules"
applyTo: "src/STRIDE.Blocks/Transforms/Geometry/**/*.cs,src/STRIDE.Blocks/Sources/**/*.cs,src/STRIDE.Blocks/Sinks/**/*.cs"
---
# STRIDE Geospatial Rules

## Canonical References
- Architecture and geospatial intent: [STRIDE_revised.md](../../STRIDE_revised.md)
- Current reprojection implementation: [src/STRIDE.Blocks/Transforms/Geometry/TransformReprojectBlock.cs](../../src/STRIDE.Blocks/Transforms/Geometry/TransformReprojectBlock.cs)
- Workflow settings for grids: [src/STRIDE.Schema/WorkflowDefinition.cs](../../src/STRIDE.Schema/WorkflowDefinition.cs)
- YAML settings mapping: [src/STRIDE.Schema/WorkflowYamlLoader.cs](../../src/STRIDE.Schema/WorkflowYamlLoader.cs)

## Must Keep True
- Reprojection must stay deterministic and fail fast when required datum grid tables are missing.
- Do not silently fall back from grid-shift transforms to lower-accuracy alternatives.
- Preserve SRID propagation on output geometries.
- Keep per-batch behavior immutable: do not mutate shared geometry instances across parallel workers.

## CRS And Grid-Shift Guardrails
- Accept CRS as EPSG code input and validate at load time.
- If a transform requires datum grids, validate grid presence before processing records.
- Keep strict missing-grid exception behavior enabled in reprojection logic.
- Keep configuration path-based, using workflow setting gridShiftDirectory.

## Spatial Join And Indexing
- Build spatial indexes before parallel querying.
- Query indexes in read-only mode from workers.
- Keep ordering behavior aligned with pipeline preserveOrder semantics.

## Testing Expectations For Geospatial Changes
- Add or update at least one CRS correctness test.
- Add a missing-grid validation test when touching reprojection logic.
- Verify no regression in spatial join and split behavior.
- Run: dotnet test STRIDE.slnx --no-build -v minimal
