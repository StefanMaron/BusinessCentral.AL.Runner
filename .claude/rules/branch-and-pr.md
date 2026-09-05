# Branch and PR rules

- **Never push directly to `main`.** Always via PR. Branch protection enforces this; agents must respect it even if a task says "push to main".
- **Branch name:** `agent/<agent-id>/issue-<N>` — no exceptions. `<agent-id>` comes from a fixed, reusable pool (`impl-1`, `impl-2`) sized to the concurrency limit — it is not a task counter and does not increase. The issue number is what makes the branch unique, so reusing an identity never collides. See `.claude/agents/impl-agent.md` for how to reset a reused worktree safely.
- **PR body must contain `Closes #N`** so the linked issue auto-closes on merge, and it must not contain a closing keyword (`Closes`/`Fixes`/`Resolves`, any tense, case-insensitive) next to any OTHER issue number unless you actually mean to close that issue too — `pr-check.yml`'s `reject-bad-closing-references` job enforces both directions.
- **One open PR per impl agent.** Do not claim a second issue while a PR is open.
- **Set `status: review-ready`** on the PR once CI is green — that is how the orchestrator finds your work.
- **Concurrency with human maintainers.** This is a public repo. When claiming an issue, also assign it to `@me` (`gh issue edit <N> --add-assignee @me`, or `mcp__github__issue_write` with `method: update` and your login in `assignees`). Skip any issue or PR whose assignee is a user other than `@me` — a human maintainer is already on it. The assignee field is the boundary between "agent-owned" and "human-owned" work. A repo owner can waive this for a specific PR, but an agent never waives it on its own.
- **GitHub access:** never assume the `gh` CLI exists — it is absent in web/remote sessions. See `.claude/rules/github-access.md`.
- **Editing a PR body from a script: use `tools/pr-body.py`.** Never fetch-modify-upload by hand. A scripted edit did exactly that to PR #2790: `gh pr view --json body --jq .body` returned an empty string during a network failure, the replacements matched nothing, the append ran against `""`, and 711 bytes went up over a ~4 KB body — removing the standalone closing-reference line, so the linked issue stayed open after merge. The guard in place, `print('changed' if b != orig else 'NO ANCHOR MATCHED')`, **could not fail**: appending always changes the string. `tools/pr-body.py` refuses an empty or short fetch, requires every anchor to be found the expected number of times, refuses to drop a declared closing reference or introduce a foreign one, refuses a large shrink, and verifies the result by **re-reading** — a write's exit code is not evidence here (a `gh` call reported `dial tcp … i/o timeout` on a write that had already landed). `--check` re-asserts a body against its own diff after a rebase; `--dry-run` prints the diff and every assertion. And before any of that: **a note belongs in a comment, not in the body** — #2790's body was being edited only to add one.

## This repo squash-merges: your COMMIT MESSAGES become the merge commit, and the PR body links issues separately

This section used to say "the PR title + body become the commit message". That is not
what this repository is configured to do, and the wrong version is why the guards below
were built to scan only the title and body (#2491). Measured, not assumed:

```bash
gh api repos/StefanMaron/BusinessCentral.AL.Runner \
  --jq '{squash_merge_commit_title, squash_merge_commit_message}'
# {"squash_merge_commit_title":"COMMIT_OR_PR_TITLE","squash_merge_commit_message":"COMMIT_MESSAGES"}
```

So text reaches the merged result by **two independent routes**, and both fire:

| route | source text | what acts on it |
|---|---|---|
| the merge commit | your branch's **commit messages**, concatenated (subject: a commit subject, or the PR title) | GitHub parses the commit landing on `main` — closing references, CI-skip directives |
| the pull request | the PR **title and body** | GitHub's `closingIssuesReferences`, which closes those issues when the PR merges |

Practical consequence: a closing keyword or a skip directive written in a **commit
message** fires even though it never appears in the PR body, and editing the body will
not remove it — the commit has to be reworded and force-pushed. `Closes #N` belongs in
the PR **body**, where the declaration is visible to a reviewer; `pr-check.yml` reads
the title, the body and every commit message and holds the body to being the place a
target is declared.

Anything GitHub parses out of a commit message fires regardless of the author's intent or
the surrounding prose. Refer to issues and directives without their trigger keywords/forms
unless the effect is intended. Four real bugs share this one root cause:

- A trailing `(#N)` already in the title survives into the merge commit and gets a second one appended by the squash itself (`generate_changelog.py` strips both, see #2109). **No automated guard for this one** — watch for it when a squash-merge default message already carries a PR-title `(#N)` and GitHub is about to append its own.
- GitHub matches several CI-skip spellings (`[skip ci]`, `[ci skip]`, `[no ci]`, `[skip actions]`, `[actions skip]`, `***NO_CI***`) ANYWHERE in a commit message, so writing one in a PR body — even just to document it — silently skips every workflow on the resulting merge commit, including the one required check on `main` (this happened for real on #2115's merge, see #2116). `pr-check.yml`'s `reject-ci-skip-directives` job catches it before merge.
- The same parser fires on a **commit message**, which the PR-body guard could not see: PR #2486 declared exactly two closing references (`closingIssuesReferences` confirmed #2478 and #2480), a commit message said "It does not close #2479", and merge commit `28cdcf65` closed #2479 anyway. The issue had to be reopened by hand. `reject-bad-closing-references` and `reject-ci-skip-directives` now scan the commit messages too (#2491).
- GitHub's closing-reference parser (`Closes`/`Fixes`/`Resolves` + `#N`) fires on that pattern anywhere in the message and does not understand negation or qualifying prose: PR #2127's body said "This does not close #2125" and merge commit `fe789a13` closed #2125 regardless. The mirror bug is the parser missing entirely — a PR with no closing reference merges fine but leaves its linked issue open and labeled in-progress forever (real instances: #2046, #1642, #1640). `pr-check.yml`'s `reject-bad-closing-references` job catches both directions.
