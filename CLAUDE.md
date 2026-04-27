# CLAUDE.md

Run Business Central AL unit tests in milliseconds — no service tier, no Docker, no SQL, no license. The goal is broad AL compatibility: any AL codeunit that can run without the BC service tier should compile and execute here. See `README.md` for architecture and `docs/limitations.md` for the hard architectural limits.

Operating rules live in `.claude/rules/` and are auto-loaded. Task-specific reference is on-demand:

- Pipeline / architecture / key files → skill `al-runner-architecture`
- Writing AL tests, bucket layout, running the matrix → skill `al-runner-tests`
- `--guide` flag, full agent workflow contract → skill `al-runner-workflow`
- Triage new untriaged issues → sub-agent `triager` (Opus, runs once at the start of a cycle)
- Act as orchestrator or implementation agent → sub-agents `orchestrator` / `impl-agent` in `.claude/agents/`
- Drive a full work cycle (triage → parallel impls in worktrees → orchestrator merge pass, until the queue is empty) → slash command `/work-cycle`
