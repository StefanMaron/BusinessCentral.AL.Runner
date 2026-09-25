# Incidents behind .claude/rules/verify-execution-not-the-tick.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## Why the step exists

Corpus PR #220 carried 32 new tests. All 16 legs went green. **None of the 32 ever
ran** — BC's per-object codegen had failed, and the other 2639 tests carried the run.
So "did the tests I added actually execute" is a real step before merging a corpus PR,
not ceremony.

The problem is that the step itself has produced a **wrong answer — always zero, always
in the shape of a result** — by five separate mechanisms, four of them on one day
(#3311). Zero is exactly the answer that ends an investigation, so a false zero here
silently removes the only guard against a green run that measured nothing. Twice an
agent came within one step of reporting that passing tests had never executed.

`CLAUDE.md` documents two members of this family already — `grep -E` exiting 0 after
rejecting a flag, and `rg` skipping dot-directories. These are the same shape.

## The five mechanisms

**1. The per-leg log format is not the same on every leg.** Measured on run
`34079169063`, all legs green, all 16 running:

```
BC 27.x:      PASS  TestPart_Visible_AnswersTrueForAReachablePart
BC 28.x:        PASS TestPart_Visible_AnswersTrueForAReachablePart (240ms)
```

Two spaces and no timing on 27.x; one space, a duration, and a deeper indent on 28.x.
`FAIL` differs the same way. A pattern written against either fixed spelling reports
**0 on the other half of the matrix while those legs are green and executing** — on that
run the fixed two-space pattern returned 0 on every 28.x cloud leg, each of which had run
all 28 tests. Use `PASS +<prefix>` with a `+` quantifier, never a literal run of spaces,
and count **distinct names** so a duplicated line cannot inflate the figure either.

**2. A guessed test-name prefix returns zero and looks like a finding.** Verifying corpus
#225, an agent used `FPB_` by analogy; the real prefix was `FilterPageBuilder_`. Zero
matches, and it was one step from reporting that 32 passing tests had never executed.
**Read the prefix out of the diff or the `.al` file.** Never infer it from the feature's
surface name.

**3. A compiler that could not run reports no errors.** On corpus #227 the package cache
had been cleared between turns, so `alc` failed with `AL1018` and emitted **no
diagnostics at all** — which reads as "no errors" through a grep for `: error`. A real
`AL0166` reached CI that way. Check that the compile *ran*, not merely that it printed
nothing.

**4. `rg` across `.github/workflows/` says a test file is un-gated when it is gated.**
`pr-gate.yml`'s `tools-tests` job discovers suites by glob — `suites=(tools/test_*.py)` —
so no filename appears anywhere in the workflow and searching for one finds nothing. An
agent nearly concluded its own new test suite did not gate. A correctly-named
`tools/test_*.py` gates the day it lands, with no workflow edit; `.github/scripts/test_*`
works the same way.

**5. `gh api .../logs` writes nothing, and exits 0, on a log carrying ANSI colour.**
Measured while building `corpus-pass-count.py`: every corpus job log is coloured, so

```bash
gh api "repos/$CORPUS/actions/jobs/$id/logs" > leg.log     # 0 bytes, exit 0
```

produces an empty file and the message `the response contains terminal escape sequences;
pass --allow-escape-sequences to output it anyway` — on **stdout**, so a redirect swallows
it. An empty log then greps as "no matches". Pass `--allow-escape-sequences`, and treat an
empty body as *unavailable*, never as zero passes.

## Which legs were ever going to run it

The corpus runs 16 legs: eight cloud and eight OnPrem, and **only the eight cloud legs are
the required contexts**. The OnPrem legs run a different, much smaller suite — 29 tests
against 2915 on run `34079169063` — and have run none of the recent cloud additions. They
are green for unrelated reasons. So on a cloud-app corpus PR, eight zeros are the correct
answer and eight non-zeros are the finding; reading the OnPrem zeros as "half the matrix
did not run my tests" is a false alarm, and `corpus-pass-count.py` labels them `not-run`
for exactly that reason.

## The fifth mechanism, round two: six more instances, and why it stays prose (#4059)

The "a number that travels" section landed with four instances. Six more followed, two of them
by the author of the sentence telling people to re-derive. #4059 asked whether prose is enough.

### The six

| the number | what it was | how far it travelled |
|---|---|---|
| "1,573 codeunit ids" | **1,690** — per-chunk 226/353/348/383/380, every chunk off by 17-35 | two issue comments, one PR comment, a 255-line test-file header (#4090, #4078, #4134) |
| "four scratch repositories, both stale-ref orderings x `--soft`/`--hard`" | the orderings are not a two-way variable, and `--hard` is not an axis | **the shipped rule text on `main`** (`no-git-stash-with-worktrees.md`) |

plus the four already in the rule's table.

### Why the 1,573 row is the instructive one

The wrong figures gave "77.4% lost". The true ones give **77.3%**. The conclusion was insensitive
to the error by a tenth of a percentage point, so nothing downstream looked wrong, and the number
was published in four places.

One component *had* been checked -- the "five chunks" claim, against the package -- and the rest
was repeated as though the whole figure had been. **Checking one component of a figure is not
checking the figure**, and a conclusion that survives the error removes the last chance of
noticing: a number that changed the answer would have been caught by whoever disbelieved the
answer.

### The sweep #4059 asked for, and what it found

The issue's own "Not measured" section named the deciding experiment: how many figures currently
in `.claude/rules/` and `CLAUDE.md` are second-hand. Measured on `775e3d02`:

* 295 raw numeric tokens across 23 files;
* 26 of those are *countable claims* (a number adjacent to a counting noun), the rest exit codes,
  version numbers and list ordinals.

Re-deriving the cheaply checkable ones produced **three different outcomes from three checks**,
and that spread is the finding:

| claim | re-derived | what the gap was |
|---|---|---|
| `RecordPatches` "**94 files**" | **96**, confirmed by `command grep -rl` and `rg --hidden` | a true measurement that **drifted** -- written 2026-09-12 (#3946), wrong within a day |
| `graphify update .` "~2 seconds, **200 files**" | 355 `.cs` files under `AlRunner/` | **my instrument read the wrong subject** -- graphify's index is not every `.cs` file |
| "**20 comments** use brace shorthand" | 37 | **my instrument read the wrong subject** -- my grep matched generics and `{x,` anywhere, not comment member-shorthand |

Five of the six sibling `partial class` counts on the same line were exactly right.

**Two of three "failures" were mine, not the document's.** That ratio is the argument against a
general guard: an automated sweep over prose numbers would have reported all three, and two of
the three reports would have been wrong in precisely the way the fourth mechanism describes -- a
correct instrument reading a different subject. A guard whose false-positive rate is 2/3 trains
its readers to dismiss it, which is worse than no guard.

### The sixth row, re-derived rather than relayed

The dispatch brief for #4059 asserted the shipped claim's variable is "three-way (eight
orderings)" and that `--hard` is not an axis. Re-deriving rather than relaying it -- which is the
rule under discussion -- gave a **different and larger** correction.

Three scratch repositories, covering branch-based-on-new-main/ref-stale,
branch-based-on-old-main/ref-fresh, and the recomputed-merge-base case:

```
### branch based on NEW main, origin/main ref STALE (behind):
  commit content: 2 files changed, 51 insertions(+)
  three-dot:      2 files changed, 51 insertions(+)
  two-dot:        2 files changed, 51 insertions(+)
### branch based on OLD main, origin/main ref FRESH (ahead):
  commit content: 2 files changed, 1 insertion(+), 50 deletions(-)
  three-dot:      2 files changed, 1 insertion(+), 50 deletions(-)
  two-dot:        2 files changed, 1 insertion(+), 50 deletions(-)
```

The second ordering reproduces the documented damage. But **three-dot and two-dot agree in every
ordering**, and the reason is structural rather than incidental: after `git reset --soft
origin/main`, HEAD's parent *is* `origin/main`, so `git merge-base origin/main HEAD` returns
`origin/main` itself (measured: both `b6ce42df`) and the two diff forms are identical **by
construction**.

So the rule's operative instruction -- "`git diff --stat origin/main...HEAD` does NOT catch it --
use two dots", with figures `1 file changed, 1 insertion(+)` against `2 files changed, 1
insertion(+), 50 deletions(-)` -- does not reproduce, and the "use two dots" remedy cannot help.
The surviving true half is the one the figures were decoration on: **`git fetch origin main`
before any command naming `origin/main` as a base**, which is what makes the stale ref fresh and
is the only step that changes the outcome.

The brief's correction and this one disagree; this one is written from three runs whose transcript
is above, which is the standard the rule asks for. The dot-count claim is removed from the rule
rather than re-stated with better numbers, because a remedy that cannot work should not be carried
at any precision.

### Why no general guard

A number in prose has no schema. `test_matrix_docs_drift.py` is the precedent for pinning a prose
claim, and it works because every claim it pins has a **machine-readable source of truth** --
`.github/bc-versions.txt`, a workflow YAML, a rendered path template. It also enforces
`guards-need-a-third-state.md` on itself: *"Every check must see a non-empty match set, so a regex
that drifts to match nothing fails instead of passing vacuously."*

None of the ten instances has such a source:

* "seven orphaned registrations", "1,573 codeunit ids", "two distinct binaries" are measurements
  of a moment, not of a file in the tree;
* "2 of the 14 rules" counts a diff that no longer exists;
* the scratch-repository claim measures behaviour reproduced outside the repository entirely.

A guard able to check any of them would have to know which subject each sentence measures -- the
exact thing the fourth mechanism says goes wrong -- and the sweep above measured the cost of
guessing at 2/3.

**The one exception, which is why a guard is added and not zero.** `RecordPatches` "94 files" is
different in kind: its source of truth is the tree itself, the query is unambiguous
(`partial class <Name>` over `AlRunner/**/*.cs`), and it drifted within a day of being written. It
is checkable exactly because it is *not* second-hand -- it is a first-hand measurement of a
present-tense fact. That is the population a guard can serve: prose numbers whose subject is the
current tree. `tools/test_partial_class_counts.py` pins those six counts and nothing else.

The distinction worth carrying: **a figure about the tree can be pinned; a figure about a moment
can only be cited.** The second is the one that travels, and citation -- not automation -- is its
remedy.

## Figures moved out of the rule (#4539)

The rule now cites these measurements rather than restating them; the figures are what each
citation measured, frozen to that moment. Population figures (counts of labels, files,
transcripts on one box) were deleted outright rather than moved, because they go stale here too.

- **2026-09-11, the `PASS +<name>` pattern.** It matched 6 of 9 tests on a green leg; a
  differently-shaped query found all nine.
- **#3805 / corpus #325.** The id-alone duplicate scan produced 61 duplicates that do not exist.
- **The permission-mask correction.** The coordinator's scan counted 27 lowercase-bearing entries,
  all on Tables; on codeunits the true count was one.

### The partial-class pin no longer pins counts (#4539)

The section above recorded `tools/test_partial_class_counts.py` as the one exception: tree-state
counts, pinned because their subject is the current tree. #4539 reversed that on the owner's
direction (2026-09-25) that figures which change without anyone editing the sentence belong in no
durable text. `CLAUDE.md` § 2d now names the partial classes without counting their files, and
the pin was reshaped with it into `tools/test_partial_class_claims.py`: it checks that each class
the sentence names is a `partial class` declared in more than one file, which is the part of the
claim that does not go stale. Deleting it outright was the first cut, and it reddened
`tools/test_agent_doc_guard_counts.py`, whose pinned guard count lived in
`.claude/agents/impl-agent.md`. #4248 removed that count too, after two PRs each adding a guard
bumped it identically and merged cleanly into a red `main`; the pin became
`tools/test_agent_doc_guard_claims.py`.

## Moved out of the rule to fit the always-loaded budget (#4542)

The rule keeps each mechanism in a sentence or two with its citation. The tables it used to
carry:

### The instrument, not the subject (2026-09-11, one agent, one task)

| the instrument | what it returned | why it looked like an answer |
|---|---|---|
| a `\| tail` pipeline's `$?` | 0 | it is 0 whatever the tool returned (#3864) |
| an exit-code claim about `ci-wait.py` | "exits 0 on a non-verdict" | never re-run directly; it exits 2 |
| a log pattern for `PASS +<name>` | some of the codeunit's tests, not all | the `(known-gap)` column widened the gap the `+` had to span |

All three were caught only because something else ran. The third is the sharpest: a partial
count is exactly "that codeunit did not run", on a leg that was green; a second,
differently-shaped query found them all.

### The wrong subject (#3805 / corpus #325)

| the measurement | what it read | what it should have read |
|---|---|---|
| "which object ids are free?" | the corpus checkout **inside the runner worktree**, resolved per run and older than `master` | the branch being pushed to |
| "how many enum values omit `Ordinal`?" | the top-level `EnumTypes` array, which is **empty** | the `Namespaces` tree, where the enums live |
| "are any ids duplicated?" | the id alone, repo-wide | `(object kind, id)`, scoped to one `app.json`'s `idRanges` |

The first produced an id free in the tree measured and taken in the tree pushed to; the second an
all-zeros table; the third dozens of duplicates that do not exist, because a codeunit and a
table may share an id.

The coordinator instance: told that a permission mask was case-sensitive on one codeunit, the
coordinator "corrected" the scale to a much larger count of lowercase-bearing entries. Every one
was on **Tables** — the scan walked every `Properties` bag without tracking which object kind
owned it — and on codeunits, the issue's surface, there was a single such entry. The correction
had already been relayed onto two sibling issues before the agent re-derived it.

### The six travelling figures

| the figure | what was wrong with it | how far it travelled |
|---|---|---|
| a count of orphaned registrations | an undercount, with one member name invented by expanding a brace shorthand | a PR body and several dispatch briefs (#3940) |
| a count of rules | off by one | an issue body, a PR body, a commit message, then merged into `CLAUDE.md` as measured fact (#3972) |
| an account of how that count arose | the method it named does not produce it; the real cause is unrecoverable | the correction's own issue and PR body |
| a count of distinct binaries | an undercount — every hash differed | a PR body and a coordinator comment praising it for binary-identity discipline |
| a count of codeunit ids | every per-chunk figure off | issue comments, a PR comment, a test-file header (#4090) |
| the scratch-repository orderings behind a dot-count claim | the claim they supported does not reproduce at all | the shipped rule text on `main` (#4059) |

The last two were the author of the re-derivation sentence failing to apply it. The binaries row
errs toward caution, the safe direction for binary identity, but is still wrong: `CLAUDE.md`
asks for the binaries measured, not a count of them. The codeunit-id row's wrong per-chunk
figures gave a headline percentage within a rounding step of the true one, so nothing
downstream looked wrong. The #4059 re-derivation was measured with both commits at `b6ce42df`.

### The ruleset leg the branch never ran (#4593)

`BC 28.5 / test` was a required context in the corpus ruleset before the corpus's `ci.yml`
dispatched it, so a branch on the older `ci.yml` could not satisfy it.
