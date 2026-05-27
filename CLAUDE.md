# CLAUDE.md

Run Business Central AL unit tests in milliseconds — no service tier, no Docker, no SQL, no license. The goal is broad AL compatibility: any AL codeunit that can run without the BC service tier should compile and execute here. See `README.md` for architecture and `docs/limitations.md` for the hard architectural limits.

## Test corpus

The canonical test corpus is the **`tests/al-language/` git submodule** pointing at
[`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests).
That repo is the AL-language spec, validated against a real BC service tier. The
runner consumes it read-only — **never modify files under `tests/al-language/`**.

Tests that exercise surfaces the runner cannot support in-process (report
rendering, SMTP, HTTP egress, etc.) are declared in
[`tests/expectations/`](tests/expectations/README.md). See
[`docs/expectations.md`](docs/expectations.md) for the schema and result-classification
table. Runner-specific positive tests (e.g. proving `RunnerOutOfScopeException`
is thrown with the right reason on the right surface) live in `tests/runner-extras/`.

`tests/archive/` holds the legacy `bucket-1` / `bucket-2` / `excluded` etc.
test trees; they are no longer wired into CI and will be deleted once the
al-language corpus + expectations cover their cases.

## Operating rules and skills

Operating rules live in `.claude/rules/` and are auto-loaded. Task-specific reference is on-demand:

- Pipeline / architecture / key files → skill `al-runner-architecture`
- Fixing gaps by reusing BC's service tier / patching the runtime engine (proven: BC's compiler runs headless on Linux) → [`docs/service-tier-reuse.md`](docs/service-tier-reuse.md)
- Writing AL tests, bucket layout, running the matrix → skill `al-runner-tests`
- `--guide` flag, full agent workflow contract → skill `al-runner-workflow`
- Triage new untriaged issues → sub-agent `triager` (Opus, runs once at the start of a cycle)
- Act as orchestrator or implementation agent → sub-agents `orchestrator` / `impl-agent` in `.claude/agents/`
- Drive a full work cycle (triage → parallel impls in worktrees → orchestrator merge pass, until the queue is empty) → slash command `/work-cycle`
