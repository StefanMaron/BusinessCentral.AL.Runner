---
name: al-runner-workflow
description: Multi-agent workflow contract for this repo — orchestrator vs implementation agents, GitHub label state machine, PR lifecycle, the al-language submodule contract, and the expectations-manifest path for OOS-by-design tests. Use when acting as orchestrator/impl-agent without the dedicated sub-agent, when triaging the issue/PR queue manually, or when deciding whether an issue is a runner gap, an OOS-by-design declaration, or a corpus bug to upstream.
---

# Agent workflow

This repository uses a multi-agent workflow. Agents are identified by GitHub issue / PR labels.

## Identity

Your agent identity (`impl-1`, `impl-2`, `orchestrator`) is given in the task prompt. It maps to a GitHub label (`agent: impl-1`, etc.).

## Implementation agent loop

If you are `impl-1` or `impl-2`:

1. Check for issues labeled `agent: <your-id>` AND `status: in-progress` — that is your active issue if one exists.
2. If no active issue, find the next unclaimed issue: `status: ready` with no `agent:` label and no human assignee. Claim it: add `agent: <your-id>`, `status: in-progress`, assignee `@me`. Remove `status: ready`.
3. **Verify you understand the AL pattern.** If the issue body lacks a runnable AL reproducer or a specific failing assertion, do not guess. Add `status: needs-input`, ask the reporter, stop (`.claude/rules/no-assumption-fixes.md`).
4. Branch: `agent/<your-id>/issue-<N>`.
5. Implement red → green (`.claude/rules/tdd.md`). The right test depends on what kind of issue this is — see "Issue kinds" below.
6. Open PR with `Closes #N` in the body. Label PR `agent: <your-id>` + `status: review-ready`. Assign to `@me`.
7. Fix CI failures or review comments.
8. Auto-merge fires when approved + green (`allow_auto_merge=true` is a repo setting, not visible in the checkout). Return to step 1.

**One issue at a time per impl agent.** No second claim while a PR is open.

## Issue kinds — where does the test go?

| Kind | Test lives in | Notes |
|---|---|---|
| Runner gap on an in-scope AL pattern | An al-language test that fails before, passes after. If the corpus does not cover the pattern, write the upstream test first in `StefanMaron/BusinessCentral.AL.Language.Tests`, get it merged, then bump the submodule pin in your runner PR. | Most common. The corpus must validate against real BC before the runner can claim parity. |
| Test is OOS-by-design (SMTP, real HTTP, …) | New entry in `tests/expectations/oos-<area>.json` per `docs/expectations.md`. | The runner must throw `RunnerOutOfScopeException` with the reason from `docs/scope.md`. `.claude/rules/loud-failures.md`. |
| In-scope but not yet implemented | `tests/expectations/known-gaps-<area>.json` entry linking the GH issue. | Transient — entry is removed when the gap is closed. |
| Runner-specific positive assertion | New suite under `tests/runner-extras/`. | E.g. "calling X throws OOS with reason Y". |
| Corpus bug (test mis-asserts something real BC also fails) | Upstream PR against the corpus. | Do not edit the submodule from this repo. |

`tests/al-language/` is read-only. Never edit. See `.claude/rules/al-language-submodule.md`.

## Orchestrator loop (priority order)

If you are `orchestrator`:

1. **PRs first.** Find PRs labeled `status: review-ready`. CI green + no unresolved threads + no `CHANGELOG.md` in diff + no edits under `tests/al-language/` + relevant expectation entries / runner-extras tests cited in the body → approve and squash-merge (`gh pr merge --auto --squash`, or `mcp__github__merge_pull_request` with `merge_method: "squash"` — `gh` is absent in web/remote sessions, see `.claude/rules/github-access.md`). Otherwise leave actionable review comments.
2. **Unblock.** Review `status: blocked` issues; resolve if possible.
3. Triage of new untriaged issues is owned by the `triager` sub-agent (Opus), which runs at the start of a cycle and sets `status: ready` vs. `status: needs-input`. The orchestrator does not triage.

Workers self-select from the `status: ready` queue. The orchestrator does not assign issues to specific workers.

## GitHub access: operation → tool map

`.claude/rules/github-access.md` covers detecting whether `gh` is available.
Once detected, here is which tool covers which operation:

| Operation | `gh` | MCP tool |
|---|---|---|
| Who am I | `gh api user --jq .login` | `mcp__github__get_me` |
| List issues | `gh issue list` | `mcp__github__list_issues` |
| Read issue / its comments | `gh issue view` | `mcp__github__issue_read` (`get`, `get_comments`) |
| Label / assign / close issue | `gh issue edit`, `gh issue close` | `mcp__github__issue_write` (`method: update`) |
| Comment on issue or PR | `gh issue comment`, `gh pr comment` | `mcp__github__add_issue_comment` (PRs too — pass the PR number) |
| List PRs | `gh pr list` | `mcp__github__list_pull_requests` |
| PR detail / diff / files / CI | `gh pr view`, `gh pr diff`, `gh pr checks` | `mcp__github__pull_request_read` (`get`, `get_diff`, `get_files`, `get_check_runs`) |
| Merge a PR | `gh pr merge --squash` | `mcp__github__merge_pull_request` (`merge_method: "squash"`) |
| Open a PR | `gh pr create` | `mcp__github__create_pull_request` |
| Label a PR | `gh pr edit --add-label` | `mcp__github__update_pull_request` |
| Read failing CI logs | `gh run view --log-failed` | `mcp__github__get_job_logs` (`failed_only: true`, `return_content: true`) |
| Search for duplicates | `gh issue list --search` | `mcp__github__search_issues` |

Any agent definition granting `Bash` for GitHub work must also grant the
`mcp__github__*` tools it needs (plus `ToolSearch`, since those tools are
deferred) in its `tools:` frontmatter — otherwise the fallback is unavailable
precisely where it is needed.

## Concurrency with human maintainers

The **GitHub assignee field** is the boundary between agent-owned and human-owned work:

- When an impl agent claims an issue, it assigns `@me` alongside the labels. PRs the bot opens are also assigned to `@me`.
- Every agent (triager, orchestrator, impl) skips any issue or PR whose assignee is a user other than `@me` — a human maintainer is on it.
- A human can take over an in-flight agent task by re-assigning the issue / PR; agents back off on their next pass.

## Hard rules (all agents)

- Never push directly to `main` — always via PR.
- Never touch an issue or PR assigned to a non-`@me` user.
- Impl agents never self-assign work outside the orchestrator queue.
- Branch name: `agent/<agent-id>/issue-<N>` — no exceptions.
- PR body must contain `Closes #N`.
- Set `status: review-ready` on the PR once CI is green.
- One PR at a time per impl agent.
- Never edit `CHANGELOG.md`.
- Never edit a file inside `tests/al-language/`. A pin bump is folded into the fix PR it enables, never its own PR (`al-language-submodule.md`).
- Honour the precompiled-DLL contract (`.claude/rules/precompiled-dll-respect.md`) and loud-failures rule (`.claude/rules/loud-failures.md`).
- `--repo StefanMaron/BusinessCentral.AL.Runner` on every `gh` command when running outside the repo's default.

## Label state machine

| Label | Meaning |
|---|---|
| `status: ready` | Unclaimed, ready for an impl agent to pick up |
| `status: in-progress` | Currently being worked on by the labeled `agent: *` |
| `status: review-ready` | PR is open, CI green, ready for orchestrator review/merge |
| `status: blocked` | Needs human or cross-issue input |
| `status: needs-input` | Issue body too thin to identify root cause; reporter must elaborate (set by triager — see `no-assumption-fixes`) |
| `agent: impl-1` / `agent: impl-2` | Identity claim on an issue or PR |

## Sister docs

- `docs/expectations.md` — schema and modes for `tests/expectations/`
- `docs/scope.md` — per-API in/out-of-scope reasons
- `.claude/rules/branch-and-pr.md`
- `.claude/rules/al-language-submodule.md`
- `.claude/rules/precompiled-dll-respect.md`
- `.claude/rules/loud-failures.md`
- `.claude/rules/tdd.md`
- `.claude/rules/no-assumption-fixes.md`
- `.claude/rules/no-changelog-edits.md`
- `.claude/rules/file-issues-for-gaps.md`
