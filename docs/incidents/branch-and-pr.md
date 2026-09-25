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


## `Closes` is binary, scope is not: three re-homings in two days (#4293)

The parser behaved correctly every time. The mismatch was between a binary keyword and a
non-binary scope, which is why none of the earlier closing-reference work (#2121, #2128, #2646,
#3678 — all about whether a reference *fires*) covered it.

| PR | closed | deferred to | where the work ended up |
|---|---|---|---|
| #4253 | #4249 | *"#4249's own follow-up"* — did not exist, and once #4249 closed, could not | re-homed as #4255 |
| #4256 | #4255 | *"#4255's part 2"*, while its own landed doc comment said it *"does NOT close reachability in general"* | re-homed as #4292 |
| #4291 | #4255 | the same two items | **filed #4292 42 minutes before merging** — the correct shape |

Neither orphaning was caught by CI; #4253's was caught in review, and #4256's only because an
implementation agent dispatched at #4255 arrived to find it closed and reopened it.

### Why the gate keys on the deferral's destination

The obvious key — hedging language — is unusable here, because this repository's own rules
*require* authors to say what they did not fold (`batch-sibling-issues-by-file.md` point 5,
"even when nothing folds"). Measured over the 200 most recently merged PRs (#4017–#4389, 164 of
which declare a closing reference):

| key | flagged | true positives |
|---|---|---|
| a cue phrase (`not folded`, `deferred to`, `belongs in … follow-up`) near a declared target | **12** | 2 |
| the deferral **routed at** a declared target (`#N's own follow-up`, `stays on #N`) | **2** | 2 |

All 10 false positives of the first key were the ordinary queue-scan paragraph, deferring a
*different, separately-numbered* issue — which is a home. The distinguishing property is where
the work is sent, not that some work was left.

### Two false positives found while building it, each a different over-reach

- **Body-wide exemption.** A first Pass 3 exempted any body naming a non-closing issue number.
  Bodies here cite dozens of issues as background, so it exempted **all three** real bodies,
  including both true positives. The exemption now needs an explicit filing *destination*
  (`filed as #4292`, `tracked by #N`) — #4291's body contains both `filed about**, one cycle
  later: #4253` (a citation) and `filed as **#4292**` (the home), and only the second is an
  assertion that the remainder will outlive the merge.
- **The copula.** PR #4176 says *"This is #3482's second half"* — the PR **is** the remainder,
  completing the issue, and #3482 closed once and was never reopened. A bare possessive arm
  flagged it; the arm now requires a routing verb, because a possessive noun phrase is only a
  destination when something is being sent to it.

### What the mutations found

Two gaps, both invisible to reasoning and both found by executing the mutation:

- **First-vs-last number extraction.** Every arm's capture group is the last `#N` in the match,
  and both extractions took the first. On the exemption side that **refused a correctly-homed
  body** — the false-positive direction.
- **Arm overlap masking an arm.** Deleting `stay|stays` from the possessive arm left all 36
  tests green, because `They stay on #4255` — #4256's own words — is caught by the *destination*
  arm instead. Each arm now has a case reachable by no other, and reaching one alone is fiddly:
  `stay in #N's own follow-up` is matched by both, so the pinning cases use a separated verb
  (`stay, unmeasured, in`) and a preposition the destination arm lacks (`inside`).

One mutation measured nothing and looked like a finding: an edit referencing an undefined
`STRAY_ANY_RE` under `set -u` failed on the unbound variable, so the suite stayed green and the
row read as a gap in the tests. Re-run with a real regex, it was caught (`Failed: 1`).
## The discriminator is a preposition, not the tense (#4294)

Filed as a tense problem: `reject-bad-closing-references` fired on two loops' PRs within four
hours, both writing a true past-tense statement that one issue closed another, in the queue-scan
paragraph two rules require. The remedy proposed was vocabulary — prefer *settled*, *subsumed*,
*superseded*.

**The tense framing does not reproduce.** The issue's own first row, `#3153 closed via #4153`,
exits **0** through both the shell gate and `tools/pr-body.py`'s port. Re-measuring the class
showed what actually decides it: `SEP` matches at most one punctuation mark and cannot span a
word, so the keyword reaches the number only when nothing but punctuation is in between.

| clause | verdict | why |
|---|---|---|
| `closed #4249` | **fires** | keyword adjacent to the number |
| `closed: #4249` | **fires** | colon is in `SEP` (#3094, which closed #2942 for real) |
| `#111 - closed, #222 - open` | **fires on #222** | comma is in `SEP`, and it reaches *forward* |
| `closed via #4153` | clean | `via` is a word; `SEP` cannot span it |
| `closed by #4153` | clean | same |
| `fixed in #4153` | clean | same |
| `#456 (closed) and #789 (open)` | clean | `) and (` separates keyword from number |

So the author does not have to give up the verb — `closed via #N` says exactly what they meant
and is safe. That is a cheaper instruction than a vocabulary list, and it is the one the gate's
message now prints.

### The sub-shape nobody had named: the comma reaches forward

`#111 - closed, #222 - open` closes **#222**. The author is tabulating states and the word
`closed` belongs to #111; the separator hands it to the next number on the line. Every
instrument reports this correctly and none of them says the number is not the one you meant —
the old message named `issue number 4255` with no hint that the sentence was about #4249.

Note which separator this is. The colon and the semicolon were both pinned in
`test_check_closing_reference.sh` after #3094; the **comma**, in the same `[,;:]` class, had no
case at all. A table row is the shape that produces it.

### A second hole, in the other direction, found by the same sweep

A line whose **entire** content is `<keyword> #N` matches `CANONICAL_LINE_RE` wherever it sits —
including inside a fenced code block — so it is read as a **declaration**:

```
Closes #123

(a fence containing a bare clause on its own line)
```

prints `Closing reference OK: declared target(s): 123 789` and exits **0**. The issue closes on
merge and no error is produced for anyone to read. `tools/pr-body.py`'s port behaves identically,
so this is not a parity break.

It is narrow: a bullet, a blockquote marker, a table pipe or any other text on the line breaks
the canonical match and the stray check sees it again. Both edges are now pinned, and the hole
itself is pinned at what the script does **today** rather than fixed here — a fence-aware parser
has its own false-positive surface and is a different question from #4294's. Tracked by #4393.

### Documenting it reproduces it

The agent on #4293 recorded hitting the gate three times while writing about this shape, once
inside a code span. Markdown is not protection: GitHub's parser does not see it. Anything
quoting the defect has to write `#<N>` or put a preposition in, which is why the test arms above
use `#789` inside deliberately safe framings and the rule text writes `#111`/`#222` rather than
real numbers.

## Figures moved out of the rule (#4539)

The rule now cites these measurements rather than restating them; the figures are what each
citation measured, frozen to that moment. Population figures (counts of labels, files,
transcripts on one box) were deleted outright rather than moved, because they go stale here too.

- **#1883 / #3960.** The claim's timeline records two `labeled` events at `20:05:42Z` and zero
  `unlabeled`.
- **#4294.** The two loops tripped the past-tense closing-keyword trap within four hours.

## Moved out of the rule to fit the always-loaded budget (#4542)

- **The two scope re-homings.** #4253 closed #4249 deferring to *"#4249's own follow-up"*
  (re-homed as #4255), and #4256 closed #4255 deferring to *"#4255's part 2"* while its own
  landed doc comment said it did not close the question (re-homed as #4292) — twice in two days,
  the second time to the issue filed about the first.
- **Cue phrase against destination (#4293).** Over a range of merged PRs a cue-phrase check
  flagged mostly the ordinary "what I did not fold" paragraph, where the destination key flagged
  only real deferrals.
- **A tracker (#4489)** is a set of related items rather than one unit of work, which is why
  `Closes` on one item shuts the whole record.
- **The past-tense mention (#4294).** Two loops tripped it, in exactly the paragraph
  `batch-sibling-issues-by-file.md` point 5 and `search-for-the-same-defect-first.md` require;
  the agent documenting it hit it twice through markdown. The gate's message now names the
  rewrite. The separator matches at most one punctuation mark, which is why a word defeats it.
- **`PR_HEAD_REF`.** `Closes #123` on `agent/x/issue-4294` exits 0 with the variable unset and 1
  with it set.
- **`Part of` mid-sentence (#3934).** `Part of #1883 - the NavDataTransfer cluster` is accepted;
  `This is part of #1883` is reported as malformed, quoting the line. The gate and
  `part_of_references.sh` accept the same shape.
