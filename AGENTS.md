# AGENTS.md

This file is read by OpenAI Codex and any other agent working in this repository.
For Claude Code, see `CLAUDE.md`. For GitHub Copilot, see `.github/copilot-instructions.md`.
The agent workflow contract below applies to all agent types equally.

---

## Repository purpose

Run Business Central AL unit tests in milliseconds — no BC service tier, no Docker, no SQL Server, no license. See `README.md` for architecture and `docs/limitations.md` for known gaps.

---

## TDD is non-negotiable

Every feature, fix, or mock addition requires a test. No exceptions.

**Strict red/green workflow:**
1. **RED** — write the failing AL test first, run it, confirm it fails
2. **GREEN** — implement the fix, run again, confirm it passes
3. Never write implementation code without a prior failing test

**Every test must cover both directions:**
- **Positive**: correct input → expected result (`Assert.AreEqual`)
- **Negative**: invalid input → right error (`asserterror` + `Assert.ExpectedError`)

**Tests must prove, not just pass.** A green test that would also pass with a no-op mock proves nothing. Ask: "would this catch a broken implementation?" If not, strengthen it. See issue #203 for the full standard.

---

## Documentation must stay current

Any change affecting behavior, CLI flags, or mock capabilities must update:
- `README.md` — supported/unsupported feature list, CLI flags
- `PrintGuide()` in `AlRunner/Program.cs` — `--guide` output
- `docs/limitations.md` — if the change affects known gaps

**Do NOT touch `CHANGELOG.md`.** It is generated from commit messages on `main` after merge. Editing it in PRs causes conflicts that block the queue.

---

## Agent workflow

This repository uses a multi-agent workflow. Agents are identified by GitHub issue/PR labels.

### Your identity

Your agent identity (`impl-1`, `impl-2`, or `orchestrator`) is given to you in the task prompt when you are started. It maps to a GitHub label (`agent: impl-1`, etc.).

### Implementation agent loop

If you are `impl-1` or `impl-2`:

1. Check for issues labeled `agent: <your-id>` AND `status: in-progress` — that is your active issue if one exists.
2. If no active issue, find the next unclaimed issue: `status: ready` with no `agent:` label. Claim it by adding `agent: <your-id>` + `status: in-progress` and removing `status: ready`.
3. Implement following TDD rules. Branch: `agent/<your-id>/issue-<N>`.
4. Open PR with `Closes #N`. Add `agent: <your-id>` + `status: review-ready` to the PR.
5. Monitor: fix CI failures, address review comments, rebase on conflict. If blocked, add `status: blocked` with a comment and stop.
6. Once merged: return to step 1.

**One issue at a time.** Do not pick up a second issue while a PR is open.

### Orchestrator loop (priority order)

If you are `orchestrator`:

1. **PRs first**: find PRs labeled `status: review-ready`. If CI is green and no unresolved threads: approve + `gh pr merge --auto --squash`. If CI is failing or threads are open: leave actionable review comments.
2. **Unblock**: review `status: blocked` issues and resolve if possible.
3. **Replenish**: if fewer than 20 issues have `status: ready`, create new ones from coverage gaps (`docs/coverage.yaml`, `coverage-gap` label, `docs/limitations.md` non-architectural items). Do not create issues when PRs are waiting — PRs always come first.

Workers self-select from the `status: ready` queue. The orchestrator does not assign issues to specific workers.

### Rules all agents must follow

- **Never push directly to `main`** — always via PR
- **Never self-assign** (impl agents) — only work on issues the orchestrator has labeled for you
- **Branch naming**: `agent/<agent-id>/issue-<N>` — no exceptions
- **Always link the issue** in the PR body: `Closes #N`
- **Always set `status: review-ready`** on the PR once CI is green — this is how the orchestrator finds your work
- **One PR at a time** per impl agent

---

## Key files

| File | Role |
|------|------|
| `AlRunner/Program.cs` | CLI entry point, AlTranspiler, RoslynCompiler, Executor, PrintGuide |
| `AlRunner/RoslynRewriter.cs` | BC→mock type transformations |
| `AlRunner/Pipeline.cs` | Pipeline orchestration, exit codes |
| `AlRunner/Runtime/` | All mock implementations (MockRecordHandle, AlScope, etc.) |
| `tests/bucket-1/`, `tests/bucket-2/`, `tests/bucket-feature-niw/` | AL test suites. `bucket-1` and `bucket-2` group suites under thematic category subfolders (`record-table`, `codeunit-runtime`, `page-report`, `data-formats`); `bucket-feature-niw` is flat (single theme, no category) |
| `docs/limitations.md` | Known gaps and architectural limits |
| `CHANGELOG.md` | **Do not edit** — generated from commits post-merge |

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | All tests pass |
| 1 | Test failures or fatal error |
| 2 | Runner limitation (blocked tests) — use `--strict` in CI to treat as failure |
| 3 | AL transpilation failure |

CI always passes `--strict`. Any non-zero exit fails the pipeline.
