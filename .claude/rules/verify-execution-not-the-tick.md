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
get applied to the thing under investigation and skipped on the tool doing the investigating —
a `| tail` pipeline's `$?` (#3864), an exit-code claim never re-run directly, a `PASS +<name>`
pattern that a `(known-gap)` column defeated and that returned a **partial** count, exactly the
shape of a real finding. When a query about a run returns something surprising, re-derive it a
second way **before** reporting it: a tool that cannot be wrong in the direction you are reading
is not evidence.

### The fourth mechanism: a correct instrument reading the WRONG SUBJECT

A working instrument answering a different question than the one asked — well-formed,
plausible, wrong (#3805 / corpus #325): free ids read from the corpus checkout inside the runner
worktree rather than the branch being pushed to; enum values counted in an empty top-level array
while the enums live in the `Namespaces` tree; duplicate ids counted repo-wide by id alone rather
than by `(object kind, id)` within one `app.json`'s `idRanges`. **Ask what the query read, not
only what it returned.**

**The one least likely to be re-checked is a coordinator's own number, supplied while
correcting an agent** — it arrives with authority, so it is taken rather than tested, and gets
relayed. **Re-derive a number you are about to hand someone as a correction, on the population
THEY are working**, not the one your query happened to cover.

### The fifth: a number that travels

A measurement **someone else took** that you repeat — and repeating is where the checking
stops. Six instances, each caught only downstream, some reaching `CLAUDE.md` and shipped rule
text on `main` (#3940, #3972, #4090, #4059; the figures are in
`docs/incidents/verify-execution-not-the-tick.md`). Every one was cheap to check — a
`sha256sum`, a `git diff --name-status | wc -l`, a `grep -c`.

**Re-derive a number before you repeat it in anything durable** — an issue, a PR body, a commit
message, a rule. Passing one along unchecked makes you its second source, and a reader cannot
tell a number you verified from one you forwarded. Three sharpenings:

- **Checking one component of a figure is not checking the figure** — a partly-checked figure
  carries the full authority of a checked one, to its author most of all (#4090).
- **A figure whose precision does not change any decision is the one least likely to be
  checked, and it is not therefore harmless** — it is what a later reader cites for a decision
  that *is* sensitive to it.
- **Re-derive, do not relay, a correction you are handed.** #4059's brief carried one; re-deriving
  it found that the dot-count remedy it decorated cannot work at all — after
  `git reset --soft origin/main` the merge base **is** `origin/main`, so three-dot equals two-dot
  by construction.

### Does this want a tool? No — do not write the count

A guard sweeping the figures in `.claude/rules/` and `CLAUDE.md` cannot tell which subject a
sentence measures, so it inherits the fourth mechanism and trains readers to dismiss it (#4059).
The answer is upstream of any guard: **a figure that changes without anyone editing the sentence
is not written at all** (owner's direction, #4539). A claim that needs pinning is pinned without
a number — `tools/test_partial_class_claims.py` checks that each named class spans several files.
So split by what the number is *about*:

| the figure is about | what to write |
|---|---|
| **the tree, the queue or the box as it is now** — a file count, a call-site count, a label count, a backlog share | **nothing numeric**: state the claim and the trap, and name the query that answers it, so the reader measures it themselves |
| **the configuration** — how many legs a PR runs, which versions | **the source file** (`.github/pr-bc-versions.txt`, `.github/bc-versions.txt`), never a copy of what it says today |
| **a moment** — a run's output, a diff that no longer exists, an assembly hash | **the citation**: the issue, PR or run id, so the next reader can re-run it; the numbers go in `docs/incidents/` if anywhere |
| **a value the reader acts on** — an exit code, a flag, a timeout | **the value** — it is the instruction |

## Which legs were ever going to run it

The corpus runs a cloud and an OnPrem leg for every BC version in its `.github/workflows/ci.yml`
matrix, and **only the cloud legs are the required contexts**. The OnPrem legs run a different,
much smaller suite, so on a cloud-app corpus PR zeros on the OnPrem legs are correct and
`corpus-pass-count.py` labels them `not-run`. **Which cloud legs are required is the corpus
ruleset's answer**, which the tool reads on every call: a required leg that did not run your
codeunit, or is absent from the run, is exit 1, and an unreadable ruleset is exit 3. Trap: the
ruleset can require a leg the branch's `ci.yml` never dispatches (#4593), and such a branch stays
blocked until it runs on a `ci.yml` that does.

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
