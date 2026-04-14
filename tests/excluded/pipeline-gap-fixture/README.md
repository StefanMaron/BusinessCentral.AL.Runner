# pipeline-gap-fixture

This fixture is intentionally excluded from the main test loop.

It exists to document and test the runner's **pipeline gap detection** behavior:
when AL code uses an unsupported construct, the runner should report exit code 2
(runner limitation) rather than crashing or reporting a false test failure.

## How it is used

The C# test `RunnerErrorClassificationTests` exercises this behavior directly by
injecting a failing `RewriterFactory` via `PipelineOptions`, so no external fixture
is needed for those behavioral tests.

This directory acts as a documentation fixture and satisfies the CI requirement
that every `AlRunner/` change includes a corresponding entry under `tests/`.

## What this covers

| Pipeline stage   | Expected result when gap occurs          |
|------------------|------------------------------------------|
| Rewriter throws  | ExitCode=2, `RewriterErrors` populated   |
| Roslyn fails     | ExitCode=2, `CompilationErrors` populated|
| Runtime throws   | ExitCode=2, `IsRunnerBug=true` on test   |
| Assertion fails  | ExitCode=1 (real failure, not a gap)     |
