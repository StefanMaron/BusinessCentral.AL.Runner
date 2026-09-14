# GitHub access: never assume the `gh` CLI exists

Agents in this repo run in more than one environment, and they do **not** all have
the same GitHub access:

| Environment | GitHub access |
|---|---|
| Local CLI (`claude` in a terminal) | `gh` CLI, authenticated |
| Claude Code on the web / remote sessions | **no `gh`, no `hub`, no direct GitHub API** — GitHub MCP tools only (`mcp__github__*`) |

A definition written only in `gh` recipes is dead on arrival in a web session: every
`gh` call fails with "command not found", and the agent either stalls or reports
success on work it never did.

## The rule

**Detect, then pick.** Once, at the start of a pass:

```bash
command -v gh >/dev/null 2>&1 && echo "gh available" || echo "use mcp__github__* tools"
```
<!-- Recipe-unpinned: its answer IS the environment under test; a box with gh cannot measure the gh-less branch -->

- **`gh` available** → use it. Pass `--repo StefanMaron/BusinessCentral.AL.Runner`
  on every command.
- **`gh` missing** → use the GitHub MCP tools. They are deferred: load schemas with
  `ToolSearch` (`select:mcp__github__issue_read,mcp__github__list_issues,…`) before
  calling them. Pass `owner: StefanMaron`, `repo: BusinessCentral.AL.Runner`.

Never fall back to `curl` against `api.github.com` — the token is not in the
environment, and a 404 from an unauthenticated request is indistinguishable from
"this does not exist."

The operation → tool map (which `gh` subcommand pairs with which `mcp__github__*`
call) lives in the `al-runner-workflow` skill — reference material, not a rule.

## Things `gh` gives you that the MCP tools do not

- **`mergeable_state` is not a conflict check.** A PR's `base.sha` records the base
  at *creation* time and never moves, so a merged PR can still read as based on an
  old commit. To decide whether a branch conflicts with current `main`, ask git, not
  the API:
  ```bash
  git fetch origin main <branch>
  git merge-tree --write-tree --messages origin/main origin/<branch> >/dev/null \
    && echo CLEAN || echo CONFLICT
  ```
- **Merge state comes from the PR's own merge record, not from branch ancestry.** This
  repository squash-merges (`branch-and-pr.md`, "This repo squash-merges"), so a merged
  branch's head commit is **never** an ancestor of `main` — the squash creates a new commit
  with the same tree and a different history. `git merge-base --is-ancestor <branch-head>
  origin/main` therefore exits non-zero for a PR that merged perfectly, and it reads as "not
  in main yet" (#3383). Ask the PR instead:
  ```bash
  gh pr view <N> --repo StefanMaron/BusinessCentral.AL.Runner \
    --json state,mergedAt,mergeCommit \
    --jq '"state=\(.state) mergedAt=\(.mergedAt) mergeCommit=\(.mergeCommit.oid // "-")"'
  ```
  `state=MERGED` with a non-null `mergedAt` is the answer. If you want git to confirm it,
  test the **merge commit**, never the branch head:
  `git merge-base --is-ancestor <mergeCommit.oid> origin/main`. (Without `gh`, the
  REST pull-request object the MCP tools return carries the same answer as `merged`,
  `merged_at` and `merge_commit_sha`.)

## Sister rules

- `branch-and-pr.md` — branch naming, `Closes #N`, the assignee ownership boundary
