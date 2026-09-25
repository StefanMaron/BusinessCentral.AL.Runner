# Branch and PR rules

- **Never push directly to `main`.** Always via PR; branch protection enforces it even when a task says otherwise.
- **Branch name:** `agent/<agent-id>/issue-<N>`. The identity comes from a fixed, reusable pool sized to the concurrency limit, so the issue number is what makes a branch unique and reusing an identity never collides. `.claude/agents/impl-agent.md` has the reset recipe for a reused worktree.
- **The PR body must contain `Closes #N`**, and must not put a closing keyword (`Closes`/`Fixes`/`Resolves`, any tense, any case) next to any other issue number unless you mean to close that one too. `pr-gate.yml`'s `reject-bad-closing-references` job blocks the merge in both directions (#3165).
- **A branch named `agent/<id>/issue-<N>` must say what the PR does about N** — the body's `Closes #N`, or a standalone `Part of #N` line when the PR lands part of the issue and N stays open. `pr-gate.yml` enforces it, and `.github/workflows/issue-label-hygiene.yml` reads the `Part of` line on merge to put N back on the ready queue (#3678). `Part of #N` for the branch's own issue is itself a declaration, so a partial landing never needs the `No linked issue:` escape hatch. **Trap: "standalone" means the marker STARTS the line** — prose after the number is fine, but `This is part of #N` mid-sentence is reported as **malformed** (#3934; pinned by `test_part_of_references.sh`). A suffixed branch (`agent/<id>/issue-N-<step>`) names issue N (#3792).
- **Choosing `Closes` over `Part of` is a scope judgement.** A PR that lands the tractable half and still writes `Closes #N` leaves the deferred half with no home, and nobody notices because a closed issue looks finished (#4249, #4255, #4292). `pr-gate.yml`'s `reject-deferred-scope` job (`check_deferred_scope.sh`) fails a body that routes remaining work **at** a declared closing target; file the remainder as its own issue and name it, or write `Part of #N`. **Trap: it keys on the deferral's DESTINATION, not on hedging language** — saying what you did not fold is required (`batch-sibling-issues-by-file.md` point 5), so it does **not** catch a partial landing described in any other words, and a green tick is not a second opinion on your scope (#4293). **A `type: tracker` issue is the shape it is most blind to**: a PR addressing one item and writing `Closes` shuts the whole record with nothing deferred for the gate to key on. Declare `Part of #N` on a tracker, always; the queue recipes exclude the label so one is never handed to you as a unit of work (#4489).
- **One open PR per impl agent** — do not claim a second issue while a PR is open. That bounds *concurrency*, not content: one PR may close several issues when each gets its own proving test (`batch-sibling-issues-by-file.md`).
- **Set `status: review-ready`** on the PR when you mark it ready — that is how the orchestrator finds your work; the coordinator reads CI, not you (`.claude/agents/impl-agent.md`, Step 5).
- **Assign the issue to `@me` when you claim it**, and skip any issue or PR assigned to a user other than `@me`: the assignee is the boundary between agent-owned and human-owned work on a public repo, and only the repo owner waives it. What each claim signal is worth between agents, and how to release a claim, are in `check-open-prs-before-claiming.md`.
- **GitHub access:** `gh` is absent in web and remote sessions (`github-access.md`).
- **Never pass `--add-label` and `--remove-label` to one `gh issue edit`.** They dispatch as two concurrent goroutines with no ordering (cli/cli v2.98.0, `pkg/cmd/pr/shared/editable_http.go`), and `gh` exits 0 whichever wins (#1883, #3960, #3930). Split into two invocations, **additions first**: if the second is lost, both labels present is visible and self-correcting, while no label at all drops the issue out of every queue. Assignees are not affected — one computed `AssigneeIDs` list, one mutation. **Trap: a single call whose two lists merely happen not to overlap today is one literal away from the race** — `issue-label-hygiene.yml` is safe only because it explicitly filters `status: ready` out of its removals.
- **Edit a PR body with `tools/pr-body.py`**, never by hand fetch-modify-upload: it refuses an empty or short fetch, holds every anchor to its expected count, refuses to drop a declared closing reference or add a foreign one, refuses a large shrink, and verifies by **re-reading** (#2790). The traps it exists for: `gh pr view --json body --jq .body` returns an **empty string** on a network failure, so a hand-rolled edit uploads an append to `""` over a real body; and a guard of the shape `changed if b != orig` **cannot fail** after an append. `--check` re-asserts a body against its diff after a rebase; `--dry-run` prints the diff and every assertion. A note belongs in a comment, not in the body.

## This repo squash-merges: your COMMIT MESSAGES become the merge commit, the PR body links the issues

`squash_merge_commit_message` is `COMMIT_MESSAGES` and `squash_merge_commit_title` is
`COMMIT_OR_PR_TITLE` here, so text reaches the merged result by two independent routes and both
fire (#2491). Re-read the setting rather than trusting this line:

```bash
gh api repos/StefanMaron/BusinessCentral.AL.Runner --jq '{squash_merge_commit_title, squash_merge_commit_message}'
```

| route | source text | what acts on it |
|---|---|---|
| the merge commit | your branch's **commit messages**, concatenated (subject: a commit subject, or the PR title) | GitHub's parse of the commit landing on `main` — closing references, CI-skip directives |
| the pull request | the PR **title and body** | `closingIssuesReferences`, which closes those issues when the PR merges |

Declare the target in the PR **body**, and refer to every other issue in a commit message
without a trigger keyword or directive form: GitHub's parser ignores negation and qualifying
prose, and undoing it means rewording the commit and force-pushing (#2127, #2486).

The traps that parser sets:

- **A trailing `(#N)` already in the PR title** survives into the merge commit and the squash appends a second one; `generate_changelog.py` strips both (#2109). Nothing guards this.
- **A CI-skip spelling anywhere in a commit message or PR body** (`[skip ci]`, `[ci skip]`, `[no ci]`, `[skip actions]`, `[actions skip]`, `***NO_CI***`) skips every workflow on the merge commit, including the required check on `main`; `reject-ci-skip-directives` blocks it (#2116).
- **Both guards read the commit messages too**, so a clean body does not clear you (#2491).
- **A true PAST-TENSE mention fires the same way**, and it is the shape agents actually write — a queue-scan sentence about an issue already shut (#4294). **What discriminates is not the tense but whether a WORD separates the keyword from the number**: `closed #N`, `closed: #N` and `closed, #N` fire; `closed via #N`, `closed by #N` and `fixed in #N` are clean. Keep your verb and add a preposition. **Trap: in a table row or a list the comma hands the keyword to the NEXT number**, so `#111 - closed, #222 - open` closes **#222**. **Markdown is not protection** — a code span or a fence reproduces it. Pinned in `test_check_closing_reference.sh`.
- **Run the gate over your body before you publish it** — `tools/pr-body.py` runs a port of the same check as part of an edit, and the gate reads three environment variables and nothing else:

<!-- Recipe-pinned-by: .github/scripts/test_check_closing_reference.sh -->
```bash
PR_TITLE="$(git log -1 --format=%s)" PR_BODY="$(cat body.md)" \
  PR_HEAD_REF="$(git rev-parse --abbrev-ref HEAD)" \
  bash .github/scripts/check_closing_reference.sh
```

`PR_HEAD_REF` is not optional on an `agent/<id>/issue-<N>` branch: without it the branch check stands down, so a body missing its `Closes`/`Part of` declaration passes locally and fails on CI.

History: docs/incidents/branch-and-pr.md
