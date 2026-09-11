# A green tick does not prove execution — and the check for it false-zeros

Before merging a corpus PR, confirm the tests you added actually **ran**: corpus PR #220
carried 32 new tests, all 16 legs went green, and none of the 32 ever executed, because BC's
per-object codegen had failed and the other 2639 tests carried the run.

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

- the harness's own summary line, `2915 total, 2915 passed, 0 failed, 0 skipped` — it
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
| a log pattern for `PASS +<name>` | 6 of 9 tests | the `(known-gap)` column widened the gap the `+` had to span |

The third is the sharpest: **six of nine is exactly the shape of a real finding** — "that codeunit
did not run" — on a leg that was green. A second, differently-shaped query found all nine.

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
pushed to. The second produced an all-zeros table. The third produced **61 duplicates** that do
not exist, because a codeunit and a table may share an id.

**Ask what the query read, not only what it returned.** A checkout resolved elsewhere, a
container that is empty because the data moved, and a scope wider than the thing being validated
all return clean answers to a question nobody asked.

**A fourth instance, and the one least likely to be re-checked: the coordinator's own number,
supplied while correcting an agent.** Told that a permission mask was case-sensitive on one
codeunit, the coordinator "corrected" the scale to 27 lowercase-bearing entries. All 27 were on
**Tables**; the scan walked every `Properties` bag without tracking which object kind owned it,
and the issue's surface was codeunits, where the true count is **one**. The agent re-derived the
figure instead of relaying it and found the split.

Two things make that shape worse than an agent's own miss. A correction arrives with authority,
so it is taken rather than tested — and it had already been relayed onto two sibling issues
before anyone checked it. **Re-derive a number you are about to hand someone as a correction, on
the population THEY are working**, not the one your query happened to cover.


## Which legs were ever going to run it

The corpus runs 16 legs, eight cloud and eight OnPrem, and **only the eight cloud legs are the
required contexts**. The OnPrem legs run a different, much smaller suite and have run none of
the recent cloud additions. So on a cloud-app corpus PR, eight zeros are the correct answer and
eight non-zeros are the finding; `corpus-pass-count.py` labels the OnPrem legs `not-run` for
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
