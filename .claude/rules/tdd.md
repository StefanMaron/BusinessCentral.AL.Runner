# TDD is non-negotiable

Every feature, fix, or mock change requires a test. No exceptions.

**Strict red → green:**
1. RED — write the failing AL test first, run it, confirm it fails.
2. GREEN — implement the fix, run again, confirm it passes.

**Cover both directions in every test:**
- Positive: correct input → expected value (`Assert.AreEqual`).
- Negative: invalid input → specific error (`asserterror` + `Assert.ExpectedError('...')`).

**Tests must prove, not just pass.** Assert concrete values, never `Assert.IsTrue(true, ...)`
or bare `asserterror` without an expected message. A test that passes identically with and
without the fix is noise.

The only valid exception is a "no-op stub" test where the *entire* claim is "this does not
crash" — name it `*_NoThrow` / `*_IsNoOp` so the limited claim is explicit.

## Run the mutation; do not just ask the question

"Would this test still pass if the implementation did nothing?" — **execute it rather than
reason about it**, once per guard or behaviour you claim to prove, and once **per closed
issue** (`batch-sibling-issues-by-file.md`):

1. **Mutate the implementation, not the test** — delete the guard, invert the condition, or
   return the default.
2. **Confirm the mutation LANDED** — re-read or diff the mutated region. A mutation that
   silently no-ops leaves the test green, which reads as "my test is broken" when it means "I
   changed nothing". `tools/apply-mutation.py` does this: **0 applied, 1 not applied,
   2 ambiguous**, and **3 refused** — nothing measured at all; never read a 3 as a caught
   mutation or a stale anchor. Prefer it to `sed`:

   ```bash
   tools/apply-mutation.py <file> --anchor-file a.txt --replacement-file b.txt   # exit 0 = applied
   tools/apply-mutation.py <file> --restore
   ```

   **After a review-driven repair, re-confirm every mutation still anchors** — the lines a
   reviewer sends you back to change are the lines mutations target, and a mutation that stopped
   applying is green (#4316).

   Force a clean rebuild when you mutated a build input (`.csproj`, an MSBuild target, a
   generator); an incremental build may skip the compile entirely.

   **Rebuild after `--restore` too** — otherwise `--no-build` re-measures the mutant, and that
   red is deterministic, narrow and on the right arm, so it ends investigations wrongly (#4343).
   `tools/mutation-verdict.py` refuses (exit 3) a run whose output directory predates the last
   `--restore`. Judge the output **directory**, not one assembly (the mutated code usually lives
   in a dependency), and a mutation in a file no build reads has no rebuild that clears it.
3. **Rebuild and re-run. Confirm RED — and that the RED is the assertion, not the build**
   (`Assert` in the error text, not `error CS`; a compile failure prints no `Total:`). Restore.
4. **Report both numbers in the PR body** — `Failed: 1, Passed: 7` → `Failed: 0, Passed: 8`.
   A mutation whose result nobody can see is the same as one nobody ran.

**Citation:** #3819 and #3882 — both unproven guards found only by an executed mutation
(`docs/incidents/tdd.md`).

## The traps

**A test that names the thing is not a test that drives it.** A grep for the symbol finds
assertions about a naming convention and reads as coverage; the mutation is the check (#3882).

**One red proves something is covered, not WHICH thing.** A fix feeding two observables owes a
mutation per observable (#3917: reverting only the AL-observable half left 252 tests green).

**A mutation that breaks the build proves nothing, and fails LOUDLY** — `exit 1`, `error CS`
lines, no `Total:`. Prefer mutating a **value** the assertion reads over editing code
structure (#3900).

**A red from the engine-bootstrap guard prints `Total:` and reads as a caught regression**
(#3948). Pipe the run through `tools/mutation-verdict.py` before believing a red: exit 1 real,
4 a build break, 5 the engine guard, 3 unmeasured (#3957).

**A `tools/test_*.py` guard is a process, not a `dotnet test` suite — pass its exit code:**

```bash
python3 tools/test_no_racing_label_edit.py > g.txt 2>&1; rc=$?
tools/mutation-verdict.py --exit $rc g.txt
```

Exit 1 is ambiguous there — a Python traceback exits 1 too; the tool downgrades a red whose log
carries one, so do not hand-read the number (#4314).

**A failed mutation and a working guard look identical** — a heredoc that collapsed the edit,
a `-p:` override whose incremental build skipped `CoreCompile` (#3895).

**A mutation can land, execute, and still change nothing, because the system absorbs it** —
deduplicates, clamps, caches or refuses it (#4003: a duplicate-key `Insert` returned `false`).
Confirm the mutation changed the **observable the assertion reads**, not merely the source.

**A filter that matches nothing is a silent pass** — `No test matches the given testcase
filter` exits 0. Quote the `Total:` line from every run; no `Total:` means unverified. **The
harder half: one that matches the WRONG thing** — a filter missing a second class in the file,
a mutation aimed at a method whose name is a prefix of the one the tests call (#3923). Count the
tests you expected, and after a green mutation check the edited symbol is the one the test path
calls.

**CI catches the opposite error, never this one.** A test that passes when it should fail reads
as coverage until somebody looks.

**Choose the mutation to test a property, not to produce a red.** One that reds everything
proves coverage exists; one that reds exactly the right subset proves the tests discriminate
(PR #3947). **Re-running the author's mutation is the weakest check a reviewer can make** — pick
your own.

**When one side of a two-sided boundary is pinned, ask whether the other is** — a reader and a
writer sharing a rule usually get one test. Pin both directions: the walk stops at the boundary
*and* still finds what is inside it (#4343).

A required step, not a tool: no framework, no CI job. One rebuild. Measurements behind every
trap: `docs/incidents/tdd.md`.

## Sister rules

- `batch-sibling-issues-by-file.md` — one RED→GREEN per closed issue; folding never exempts
- `guards-need-a-third-state.md` — a refusal path with no test is indistinguishable from a
  never-fire path: this rule, applied to the third state
- `loud-failures.md` — what a patch must justify, and in what form

History: docs/incidents/tdd.md
