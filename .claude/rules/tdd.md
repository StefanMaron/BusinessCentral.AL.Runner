# TDD is non-negotiable

Every feature, fix, or mock change requires a test. No exceptions.

**Strict red → green:**
1. RED — write the failing AL test first, run it, confirm it fails.
2. GREEN — implement the fix, run again, confirm it passes.

**Cover both directions in every test:**
- Positive: correct input → expected value (`Assert.AreEqual`).
- Negative: invalid input → specific error (`asserterror` + `Assert.ExpectedError('...')`).

**Tests must prove, not just pass.** Assert concrete values, never `Assert.IsTrue(true, ...)`
or bare `asserterror` without an expected message.

The only valid exception is a "no-op stub" test where the *entire* claim is "this does not
crash" — name it `*_NoThrow` / `*_IsNoOp` so the limited claim is explicit.

## Run the mutation; do not just ask the question

"Would this test still pass if the implementation did nothing?" is a question this rule used
to leave you to answer by reasoning. **Execute it instead**, once per guard or behaviour you
are claiming to prove:

1. **Mutate the implementation, not the test** — delete the guard, invert the condition, or
   return the default.
2. **Rebuild and re-run. Confirm RED.** Restore.
3. **Report both numbers in the PR body** — `Failed: 1, Passed: 7` → `Failed: 0, Passed: 8`.
   A mutation whose result nobody can see is the same as one nobody ran.

Once **per closed issue**, matching the per-issue RED→GREEN that
`batch-sibling-issues-by-file.md` already requires.

**Citation.** Two instances in one session, both caught only by an executed mutation
(`docs/incidents/tdd.md`). #3819's fixture *constructed* the relationship it was meant to prove
— `FakeExpression(id, name)` asserting `Name` holds the raw AL identifier, which it does not —
so every test passed against a join that resolved nothing for 7,173 declarations. #3882 carried
two guards at review time: mutating `DependencyLoader.LoadOne` gave `Failed: 1, Passed: 7`;
mutating `DependencyMetadataProducer.Ensure` left **all 8 green** — a gap found in review and
fixed before merge, which is the outcome this step exists to produce.

**Trap: a test that names the thing is not a test that drives it.** #3882's second guard had
two references to `METADATA-EMIT-EXCLUDED` — one asserting the naming convention, one passing it
as routing data. Both mention the stage; neither reaches the `throw`. A grep for the symbol
finds them and reads as coverage, which is why the mutation is the check and the grep is not.

**Trap: CI catches the opposite error, never this one.** A test that fails when it should pass
is red within minutes; one that passes when it should fail is caught only if somebody looks,
and until then it reads as coverage while protecting nothing.

A required step, not a tool: no framework, no CI job. One rebuild.

## Sister rules

- `batch-sibling-issues-by-file.md` — one RED→GREEN per closed issue; folding never exempts
- `guards-need-a-third-state.md` — a refusal path with no test is indistinguishable from a
  never-fire path: this rule, applied to the third state
- `loud-failures.md` — what a patch must justify, and in what form

History: docs/incidents/tdd.md
