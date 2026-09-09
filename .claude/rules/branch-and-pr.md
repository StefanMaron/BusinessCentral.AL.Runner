# Branch and PR rules

- **Never push directly to `main`.** Always via PR; branch protection enforces it even when a task says otherwise.
- **Branch name:** `agent/<agent-id>/issue-<N>`. The identity comes from a fixed, reusable pool sized to the concurrency limit, so the issue number is what makes a branch unique and reusing an identity never collides. `.claude/agents/impl-agent.md` has the reset recipe for a reused worktree.
- **The PR body must contain `Closes #N`**, and must not put a closing keyword (`Closes`/`Fixes`/`Resolves`, any tense, any case) next to any other issue number unless you mean to close that one too. `pr-gate.yml`'s `reject-bad-closing-references` job blocks the merge in both directions (#3165).
- **A branch named `agent/<id>/issue-<N>` must say what the PR does about N**, and `pr-gate.yml` enforces it (#3678): either the body's `Closes #N`, or a standalone `Part of #N` line when the PR lands only part of the issue and N stays open. `Part of` carries no closing keyword, so it closes nothing; it is what `.github/workflows/issue-label-hygiene.yml` reads on merge to put N back on the ready queue. A `Part of #N` naming the branch's own issue also satisfies the "declare something" direction by itself, so a partial landing never needs the `No linked issue:` escape hatch. Thirty-one merged PRs sat on an `issue-N` branch declaring neither, leaving 12 issues open and invisible to the ready queue.
- **One open PR per impl agent** — do not claim a second issue while a PR is open. That bounds *concurrency*, not content: one PR may close several issues when each gets its own proving test (`batch-sibling-issues-by-file.md`).
- **Set `status: review-ready`** on the PR when you mark it ready — that is how the orchestrator finds your work; the coordinator reads CI, not you (`.claude/agents/impl-agent.md`, Step 5).
- **Assign the issue to `@me` when you claim it** (`gh issue edit <N> --add-assignee @me`, or `mcp__github__issue_write` with `method: update` and your login in `assignees`), and skip any issue or PR assigned to a user other than `@me`: the assignee is the boundary between agent-owned and human-owned work on a public repo, and only the repo owner waives it. What each claim signal is worth between agents, and how to release a claim, are in `check-open-prs-before-claiming.md`.
- **GitHub access:** `gh` is absent in web and remote sessions (`github-access.md`).
- **Edit a PR body with `tools/pr-body.py`**, never by hand fetch-modify-upload: it refuses an empty or short fetch, holds every anchor to its expected count, refuses to drop a declared closing reference or add a foreign one, refuses a large shrink, and verifies by **re-reading**, because a write's exit code is not evidence that the write landed (#2790). Two traps it exists for: `gh pr view --json body --jq .body` returns an **empty string** on a network failure, so a hand-rolled edit appends to `""` and uploads it over a real body; and a guard of the shape `changed if b != orig` **cannot fail** after an append, because appending always changes the string. `--check` re-asserts a body against its diff after a rebase; `--dry-run` prints the diff and every assertion. Before any of that: a note belongs in a comment, not in the body.

## This repo squash-merges: your COMMIT MESSAGES become the merge commit, the PR body links the issues

`squash_merge_commit_message` is `COMMIT_MESSAGES` and `squash_merge_commit_title` is
`COMMIT_OR_PR_TITLE` on this repository, so text reaches the merged result by two
independent routes and both fire (#2491). Re-read the setting rather than trusting this line:

```bash
gh api repos/StefanMaron/BusinessCentral.AL.Runner --jq '{squash_merge_commit_title, squash_merge_commit_message}'
```

| route | source text | what acts on it |
|---|---|---|
| the merge commit | your branch's **commit messages**, concatenated (subject: a commit subject, or the PR title) | GitHub's parse of the commit landing on `main` — closing references, CI-skip directives |
| the pull request | the PR **title and body** | `closingIssuesReferences`, which closes those issues when the PR merges |

Declare the target in the PR **body**, where a reviewer sees it, and refer to every other
issue in a commit message without a trigger keyword or directive form: GitHub's parser
ignores negation and qualifying prose, and undoing it means rewording the commit and
force-pushing, not editing the body (#2127, #2486).

Three traps that parser sets:

- **A trailing `(#N)` already in the PR title** survives into the merge commit and the squash appends a second one; `generate_changelog.py` strips both (#2109). Nothing guards this — watch for it when GitHub is about to append its own.
- **A CI-skip spelling anywhere in a commit message or PR body** (`[skip ci]`, `[ci skip]`, `[no ci]`, `[skip actions]`, `[actions skip]`, `***NO_CI***`) skips every workflow on the merge commit, including the one required check on `main`; `reject-ci-skip-directives` blocks it before merge (#2116).
- **Both guards read the commit messages too**, not only the title and body, so a body that is clean does not clear you (#2491).

History: docs/incidents/branch-and-pr.md
