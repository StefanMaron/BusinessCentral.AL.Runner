# A green tick does not prove execution — and the check for it false-zeros

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

## Use the tool

```bash
tools/corpus-pass-count.py <run-id> <prefix>     # prefix read from the .al file
```

It does the per-leg distinct count correctly in one call: both log spellings, distinct
names rather than lines, per leg, and it separates "0 because the codeunit did not run
here" from "0 because your pattern matched nothing". Exit 0 = at least one leg ran the
codeunit and nothing failed; exit 1 = nothing matched anywhere, a leg disagrees with the
others on the count, or something failed.

Everything below is why the hand-rolled version keeps going wrong. Read it when the tool
does not apply — a non-corpus log, a different harness — not instead of running it.

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

## The general rule these share

**A zero from a pattern you chose is not evidence.** Confirm it with a second,
differently-shaped query before believing it — and prefer one whose shape does not depend
on the same assumption. Useful second queries on a corpus leg:

- the harness's own summary line, `2915 total, 2915 passed, 0 failed, 0 skipped` — it
  distinguishes a leg that ran a suite from one that never reached the test phase;
- the count of *all* PASS names on the leg, which tells you the log parsed at all;
- for a prefix question, grep for one full test name copied from the `.al` file.

## Which legs were ever going to run it

The corpus runs 16 legs: eight cloud and eight OnPrem, and **only the eight cloud legs are
the required contexts**. The OnPrem legs run a different, much smaller suite — 29 tests
against 2915 on run `34079169063` — and have run none of the recent cloud additions. They
are green for unrelated reasons. So on a cloud-app corpus PR, eight zeros are the correct
answer and eight non-zeros are the finding; reading the OnPrem zeros as "half the matrix
did not run my tests" is a false alarm, and `corpus-pass-count.py` labels them `not-run`
for exactly that reason.

## Sister rules

- `ci-verdicts.md` — the other half of "a green tick is not a verdict": stale runs,
  cancelled leftovers in the rollup, and why a required context can be green on a commit
  that is not yours
- `bc-behavior-tests-go-upstream.md` — why the corpus adjudicates a BC claim at all;
  `docs/upstream-corpus-workflow.md` § "Step 2 in full" is the long form
- `ask-the-corpus-before-claiming-bc-behavior.md` — a corpus test green on a real service
  tier is evidence only if it *ran*
- `no-assumption-fixes.md` — a zero you cannot attribute is not a diagnosis
