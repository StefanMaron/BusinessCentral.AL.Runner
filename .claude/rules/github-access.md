# GitHub access: never assume the `gh` CLI exists

Agents in this repo run in more than one environment, and they do **not** all have
the same GitHub access:

| Environment | GitHub access |
|---|---|
| Local CLI (`claude` in a terminal) | `gh` CLI, authenticated |
| Claude Code on the web / remote sessions | **no `gh`, no `hub`, no direct GitHub API** — GitHub MCP tools only (`mcp__github__*`) |

A definition written only in `gh` recipes is dead on arrival in a web session:
every `gh` call fails with "command not found", and the agent either stalls or —
worse — reports success on work it never did.

## The rule

**Detect, then pick.** Once, at the start of a pass:

```bash
command -v gh >/dev/null 2>&1 && echo "gh available" || echo "use mcp__github__* tools"
```

- **`gh` available** → use it. Pass `--repo StefanMaron/BusinessCentral.AL.Runner`
  on every command.
- **`gh` missing** → use the GitHub MCP tools. They are deferred: load schemas with
  `ToolSearch` (`select:mcp__github__issue_read,mcp__github__list_issues,…`) before
  calling them. Pass `owner: StefanMaron`, `repo: BusinessCentral.AL.Runner`.

Never fall back to `curl` against `api.github.com` — the token is not in the
environment, and a 404 from an unauthenticated request is indistinguishable from
"this does not exist."

## Operation → tool map

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
`mcp__github__*` tools it needs in its `tools:` frontmatter — otherwise the
fallback is unavailable precisely where it is needed.

**`ToolSearch` is not optional in that grant.** The `mcp__github__*` tools reach a
subagent *deferred*: the name is visible but the schema is not, and calling one
before loading it fails with `InputValidationError`. An agent granted the GitHub
tools but not `ToolSearch` can see them and cannot call them. Grant `ToolSearch`
alongside them, always.

Verified end-to-end in a web session (no `gh` present): the `orchestrator` agent
detected `gh` missing, loaded `mcp__github__list_pull_requests` via
`ToolSearch("select:mcp__github__list_pull_requests")`, called it, and returned the
open-PR queue — first attempt, no errors. The frontmatter grant does take effect and
the MCP connection is inherited by subagents.

## Things `gh` gives you that the MCP tools do not

- **`mergeable_state` is not a conflict check.** A PR's `base.sha` records the
  base at *creation* time and never moves, so a merged PR can still read as based
  on an old commit. To decide whether a branch conflicts with current `main`, ask
  git, not the API:
  ```bash
  git fetch origin main <branch>
  git merge-tree --write-tree --messages origin/main origin/<branch> >/dev/null \
    && echo CLEAN || echo CONFLICT
  ```
- **Merge state is authoritative from git.** After a merge, confirm with
  `git merge-base --is-ancestor <sha> origin/main`, not by re-reading the PR's
  base.

## Sister rules

- `branch-and-pr.md` — branch naming, `Closes #N`, the assignee ownership boundary
