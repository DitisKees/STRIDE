---
description: "Add or update tests for a STRIDE block implementation with schema, runtime, and failure-path coverage."
name: "Add STRIDE Block Tests"
argument-hint: "Block type and behavior to test, for example: TransformReproject missing-grid validation"
agent: "agent"
---
Add or update tests for the requested STRIDE block behavior.

## Inputs
- Block type and expected behavior: ${input:BlockTypeAndBehavior}

## Required Workflow
1. Locate the target block implementation and existing related tests.
2. Add focused tests in the correct test project under tests.
3. Cover these dimensions where applicable:
   - Schema derivation behavior
   - Happy path record and batch processing behavior
   - Error policy or failure-path behavior
   - Ordering and parallel behavior when relevant
4. Keep tests deterministic and small.
5. Prefer extending existing test files by concern over creating generic files.

## Project Conventions
- Follow [AGENTS.md](../../AGENTS.md)
- Architecture reference: [STRIDE_revised.md](../../STRIDE_revised.md)
- Existing block tests: [tests/STRIDE.Blocks.Tests](../../tests/STRIDE.Blocks.Tests)
- Runtime tests: [tests/STRIDE.Core.Tests](../../tests/STRIDE.Core.Tests)

## Verification
- Run: dotnet build STRIDE.slnx
- Run: dotnet test STRIDE.slnx --no-build -v minimal

## Output Format
Return a concise summary with:
- Files changed
- New test cases added
- Any uncovered edge case that still needs follow-up
