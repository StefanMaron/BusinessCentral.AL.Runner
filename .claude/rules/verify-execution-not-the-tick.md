# A green tick does not prove execution — and the check for it false-zeros

Before merging a corpus PR, confirm the tests you added actually **ran**: corpus PR #220's
new tests never executed while every leg went green, because BC's per-object codegen had failed
and the rest of the corpus carried the run.

The check itself has produced a wrong answer — **always zero, always in the shape of a result**
— by five mechanisms (#3311), and zero is the answer that ends an investigation.

## Use the tool

```bash
tools/corpus-pass-count.py <run-id> <prefix>     # prefix read from the .al file
```

It does the per-leg distinct count in one call: both log spellings, distinct names rather than
lines, per leg, and it separates "0 because the codeunit did not run here" from "0 because your
pattern matched nothing". Exit 0 = at least one leg ran the codeunit and nothing failed; exit 1
= nothing matched anywhere, a leg disagrees with the others on the count, or something failed.

Read the traps below when the tool does not apply — a non-corpus log, a different harness — not
instead of running it.

## The five ways a hand-rolled check false-zeros

1. **The per-leg log format differs across the matrix**: 27.x prints `PASS` with two spaces and
   no timing, 28.x with one space, a duration and a deeper indent, and `FAIL` differs the same
   way. Match `PASS +<prefix>` with a `+` quantifier, never a literal run of spaces, and count
   **distinct names** so a duplicated line cannot inflate the figure.
2. **Read the test-name prefix out of the diff or the `.al` file**, never inferred from the
   feature's surface name — a guessed prefix returns zero and reads as a finding (corpus #225).
3. **Check that the compile *ran***, not merely that it printed nothing: a compiler that failed
   to start emits no diagnostics at all, which greps as "no errors", and a real `AL0166` reached
   CI that way (corpus #227).
4. **A correctly-named `tools/test_*.py` or `.github/scripts/test_*` gates the day it lands**,
   discovered by glob in `pr-gate.yml`, so no filename appears in any workflow — searching the
   workflows for one and finding nothing does not mean it is un-gated.
5. **Treat an empty log body as *unavailable*, never as zero passes** — every corpus job log is
   coloured, and `gh api .../logs` refuses one without `--allow-escape-sequences`:
   `ci-verdicts.md` § "An empty log fetch is a refusal, not an empty log" owns both shapes of
   that refusal.

## The general rule these share

**A zero from a pattern you chose is not evidence.** Confirm it with a second,
differently-shaped query whose shape does not depend on the same assumption:

- the harness's own summary line, `<N> total, <N> passed, 0 failed, 0 skipped` — it
  distinguishes a leg that ran a suite from one that never reached the test phase;
- the count of *all* PASS names on the leg, which tells you the log parsed at all;
- for a prefix question, grep for one full test name copied from the `.al` file.

**Where this discipline actually fails: on the instrument, not the subject.** The confirmations
above get applied to the thing under investigation and skipped on the tool doing the
investigating — and the tool's answer is the one nothing else cross-checks. Three instances on
2026-09-11, all by one agent in one task, all caught only because something else ran:

| the instrument | what it returned | why it looked like an answer |
|---|---|---|
| a `\| tail` pipeline's `$?` | 0 | it is 0 whatever the tool returned (#3864) |
| an exit-code claim about `ci-wait.py` | "exits 0 on a non-verdict" | never re-run directly; it exits 2 |
| a log pattern for `PASS +<name>` | some of the codeunit's tests, not all | the `(known-gap)` column widened the gap the `+` had to span |

The third is the sharpest: **a partial count is exactly the shape of a real finding** — "that
codeunit did not run" — on a leg that was green. A second, differently-shaped query found them all.

So when a query about a run returns something surprising, re-derive it a second way **before**
reporting it, and treat the instrument with the suspicion you would give the subject: a tool that
cannot be wrong in the direction you are reading is not evidence.

### The fourth mechanism: a correct instrument reading the WRONG SUBJECT

The three above are instruments that malfunction. This one works perfectly and answers a
different question than the one asked — so its output is well-formed, plausible, and wrong.
Three instances in one issue's work (#3805 / corpus #325), each caught by someone doubting a
surprising result rather than by the instrument:

| the measurement | what it read | what it should have read |
|---|---|---|
| "which object ids are free?" | the corpus checkout **inside the runner worktree**, resolved per run and older than `master` | the branch being pushed to |
| "how many enum values omit `Ordinal`?" | the top-level `EnumTypes` array, which is **empty** | the `Namespaces` tree, where the enums live |
| "are any ids duplicated?" | the id alone, repo-wide | `(object kind, id)`, scoped to one `app.json`'s `idRanges` |

The first produced an id that was genuinely free in the tree measured and taken in the tree
pushed to. The second produced an all-zeros table. The third produced **dozens of duplicates** that do
not exist, because a codeunit and a table may share an id.

**Ask what the query read, not only what it returned.** A checkout resolved elsewhere, a
container that is empty because the data moved, and a scope wider than the thing being validated
all return clean answers to a question nobody asked.

**A fourth instance, and the one least likely to be re-checked: the coordinator's own number,
supplied while correcting an agent.** Told that a permission mask was case-sensitive on one
codeunit, the coordinator "corrected" the scale to a much larger count of lowercase-bearing
entries. Every one of them was on **Tables**; the scan walked every `Properties` bag without tracking which object kind owned it,
and the issue's surface was codeunits, where there was **a single** such entry. The agent re-derived the
figure instead of relaying it and found the split.

Two things make that shape worse than an agent's own miss. A correction arrives with authority,
so it is taken rather than tested — and it had already been relayed onto two sibling issues
before anyone checked it. **Re-derive a number you are about to hand someone as a correction, on
the population THEY are working**, not the one your query happened to cover.


### The fifth: a number that travels

The four above are measurements you take. This one is a measurement **someone else took** that you
repeat — and repeating is where the checking stops, because re-deriving a figure that arrived from
someone who did the work feels redundant.

Measured six times, each caught only downstream — and the last two are the author of the
re-derivation sentence failing to apply it:

| the figure | what was wrong with it | how far it travelled |
|---|---|---|
| a count of orphaned registrations | an undercount, with one member name invented by expanding a brace shorthand | a PR body and several dispatch briefs (#3940) |
| a count of rules | off by one | an issue body, a PR body, a commit message, then merged into `CLAUDE.md` as measured fact (#3972) |
| an account of how that count arose | the method it named does not produce it; the real cause is unrecoverable | the correction's own issue and PR body |
| a count of distinct binaries | an undercount — every hash differed | a PR body and a coordinator comment praising it for binary-identity discipline |
| a count of codeunit ids | every per-chunk figure off | issue comments, a PR comment, a test-file header (#4090) |
| the scratch-repository orderings behind a dot-count claim | the claim they supported does not reproduce at all | **the shipped rule text on `main`** (#4059) |

Every one reads correctly, arrives with provenance, and is cheap to check — a `sha256sum`, a
`git diff --name-status | wc -l`, a `grep -c`. The cost of re-deriving is seconds; the cost of
not is that the figure reaches a rule file, where the next reader inherits it. The figures
themselves are in `docs/incidents/verify-execution-not-the-tick.md`.

**Re-derive a number before you repeat it in anything durable** — an issue, a PR body, a commit
message, a rule. Passing one along unchecked makes you its second source, and a reader cannot tell
a number you verified from one you forwarded.

Note the binaries row errs *toward* caution, which is the safe direction for binary identity — but
it is still wrong, and `CLAUDE.md` asks you to cite the binaries you measured, not a count of them.

**Checking one component of a figure is not checking the figure.** The codeunit-id row's author had
verified one component — how many chunks the population came in, against the package — and
repeated the rest as though the whole number had been measured. A partly-checked figure carries
the full authority of a checked one, to its author most of all.

**And a conclusion that survives the error is what removes the last chance of noticing.** Those
wrong per-chunk figures gave a headline percentage within a rounding step of the true one, so
nothing downstream looked wrong, because nothing downstream *was* wrong — and the number was
published in several places (#4090). The corollary is uncomfortable and worth stating plainly:
**a figure whose precision does not change any decision is the one least likely to be checked,
and it is not therefore harmless** — it is what a later reader cites for a decision that *is*
sensitive to it.

**Re-derive, do not relay, a correction you are handed.** A correction arrives with the authority
of someone who found an error, which is the last thing that gets re-tested. #4059's own brief
carried one, about how many orderings a claim depended on, and re-deriving it in scratch
repositories produced a *different and larger* correction: the dot-count remedy the figures
decorated cannot work at all, because `git reset --soft origin/main` makes `origin/main` HEAD's
parent, so the merge base **is** `origin/main` and three-dot equals two-dot by construction
(measured, both `b6ce42df`). Relaying the brief would have published a second wrong number in the
same sentence.

### Does this want a tool? No — do not write the count

#4059 asked whether the figures in `.claude/rules/` and `CLAUDE.md` want a mechanical check. A
sweep re-deriving them found one real drift — `RecordPatches`' partial-class file count, wrong
within a day of being written — and false alarms of the sweep's own making, where its grep read a
different subject than the sentence meant. That is the fourth mechanism above, fired by the
checking tool itself. **A guard that cannot tell which subject a sentence measures inherits that
error**, and a check that is often wrong trains its readers to dismiss it — so do not add one.

The answer is upstream of any guard: **a figure that changes without anyone editing the sentence
is not written at all** (owner's direction, #4539). #4059 answered it the other way for
tree-state counts, pinning them with a test; that kept the numbers in the prose with a maintenance
cost attached. #4539 removed the counts and reshaped the pin into
`tools/test_partial_class_claims.py`, which checks the claim — each named class really spans
several files — without a number. So the split is by what the number is *about*:

| the figure is about | what to write |
|---|---|
| **the tree, the queue or the box as it is now** — a file count, a call-site count, a label count, a backlog share | **nothing numeric**: state the claim and the trap, and name the query that answers it, so the reader measures it themselves |
| **the configuration** — how many legs a PR runs, which versions | **the source file** (`.github/pr-bc-versions.txt`, `.github/bc-versions.txt`), never a copy of what it says today |
| **a moment** — a run's output, a diff that no longer exists, an assembly hash | **the citation**: the issue, PR or run id, so the next reader can re-run it; the numbers go in `docs/incidents/` if anywhere |
| **a value the reader acts on** — an exit code, a flag, a timeout | **the value** — it is the instruction |

## Which legs were ever going to run it

The corpus runs a cloud and an OnPrem leg for every BC version in its `.github/workflows/ci.yml`
matrix, and **only the cloud legs are the required contexts**. The OnPrem legs run a different,
much smaller suite and have run none of the recent cloud additions. So on a cloud-app corpus PR,
zeros on the OnPrem legs are the correct answer and non-zeros on the cloud legs are the finding; `corpus-pass-count.py` labels the OnPrem legs `not-run` for
exactly that reason.

## Sister rules

- `ci-verdicts.md` — the other half of "a green tick is not a verdict": stale runs,
  cancelled leftovers in the rollup, and why a required context can be green on a commit
  that is not yours
- `bc-behavior-tests-go-upstream.md` — why the corpus adjudicates a BC claim at all;
  `docs/upstream-corpus-workflow.md` § "Step 2 in full" is the long form
- `ask-the-corpus-before-claiming-bc-behavior.md` — a corpus test green on a real service
  tier is evidence only if it *ran*
- `no-assumption-fixes.md` — a zero you cannot attribute is not a diagnosis
- `guards-need-a-third-state.md` — the same class in the guards this repository writes
  itself: a check that cannot measure must not report its success state

History: docs/incidents/verify-execution-not-the-tick.md
