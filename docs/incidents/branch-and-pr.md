# Incidents behind .claude/rules/branch-and-pr.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## Branch and PR rules

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

Anything GitHub parses out of a commit message fires regardless of the author's intent or
the surrounding prose. Refer to issues and directives without their trigger keywords/forms
unless the effect is intended. Four real bugs share this one root cause:

- A trailing `(#N)` already in the title survives into the merge commit and gets a second one appended by the squash itself (`generate_changelog.py` strips both, see #2109). **No automated guard for this one** — watch for it when a squash-merge default message already carries a PR-title `(#N)` and GitHub is about to append its own.
- GitHub matches several CI-skip spellings (`[skip ci]`, `[ci skip]`, `[no ci]`, `[skip actions]`, `[actions skip]`, `***NO_CI***`) ANYWHERE in a commit message, so writing one in a PR body — even just to document it — silently skips every workflow on the resulting merge commit, including the one required check on `main` (this happened for real on #2115's merge, see #2116). `pr-gate.yml`'s `reject-ci-skip-directives` job catches it before merge, and blocks it.
- The same parser fires on a **commit message**, which the PR-body guard could not see: PR #2486 declared exactly two closing references (`closingIssuesReferences` confirmed #2478 and #2480), a commit message said "It does not close #2479", and merge commit `28cdcf65` closed #2479 anyway. The issue had to be reopened by hand. `reject-bad-closing-references` and `reject-ci-skip-directives` now scan the commit messages too (#2491).
- GitHub's closing-reference parser (`Closes`/`Fixes`/`Resolves` + `#N`) fires on that pattern anywhere in the message and does not understand negation or qualifying prose: PR #2127's body said "This does not close #2125" and merge commit `fe789a13` closed #2125 regardless. The mirror bug is the parser missing entirely — a PR with no closing reference merges fine and leaves its linked issue open and labeled in-progress. `pr-gate.yml`'s `reject-bad-closing-references` job catches both directions. (This line used to cite #2046, #1642 and #1640 as instances. None of them was: PR #2050 opened with "Addresses #2046 (does not close it)" and PR #2048 with "Part of #1642 — not closing it", both deliberate partial landings of a tracking issue, which is the correct way to land part of a tracked effort; and #1640 was closed on merge by PR #2040's `Closes #1640`, only its `status: in-progress` / `agent:` labels went stale — a label-hygiene defect, not a parser miss. #2186 has the record.)

## The branch is the third place a PR names an issue, and nothing read it (#3678)

Measured over the 30-day agent-workflow retrospective window
(https://fbakkensen.github.io/al-runner-retro/, finding b-20): **31 merged PRs sat on a branch
named `agent/<id>/issue-N` while declaring no closing reference for N**, and **12 of those
issues were still open afterwards** — labelled in progress, invisible to the ready queue, and
worked on by nobody. The companion finding (b-8): 5 of the 8 open in-progress issues sat behind
a merged "Part of #N" PR that nobody relabelled.

The gate had covered two of the three places a PR names an issue — the title/body and the
commit messages — because both are text GitHub's own parser reads. The branch name is the third,
and it is the one an implementation agent cannot get wrong, since the workflow contract derives
it from the issue number. PR #3744 added `PR_HEAD_REF` to `check_closing_reference.sh` and the
`Part of #N` shape alongside `Closes #N`, plus the two label workflows that act on each.

Blast radius when it landed: of the 5 open PRs at that moment, **none** would have been failed
by the new direction.

