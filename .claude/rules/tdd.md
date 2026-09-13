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
2. **Confirm the mutation LANDED** — re-read the mutated region or diff it. A mutation that
   silently no-ops leaves the test **green**, which reads as "my test is broken" when it means
   "I changed nothing". Force a clean rebuild when you mutated a build input (`.csproj`, an
   MSBuild target, a generator), since an incremental build may skip the compile entirely.
3. **Rebuild and re-run. Confirm RED — and that the RED is the assertion, not the build.**
   A mutation that breaks the compile also exits non-zero, and a run with compile errors prints
   no `Total:` line at all. Check the error text says `Assert`, not `error CS`. Restore.
4. **Report both numbers in the PR body** — `Failed: 1, Passed: 7` → `Failed: 0, Passed: 8`.
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

**Trap: one red proves something is covered, not WHICH thing.** A fix that feeds two
observables owes a mutation per observable. #3917 fixed a derivation reaching both an equivalence
projection and the AL-observable virtual table; reverting **only** the AL-observable half left
**252 tests green**, because all three new test files reached the projection alone. Its own body
argued the two-rendering point correctly and it still tested one — so awareness does not close
this, and a single red that says "the fix is covered" is the shape to distrust (#3912).

**Trap: a mutation that breaks the build proves nothing, and it fails LOUDLY.** The landing
check above catches the silent direction; this is the other one. Mutating a call site by text
substitution produced `exit 1` with **10 `error CS`** lines and no `Total:` — a broken build
wearing the shape of a caught regression. Measured twice on one guard in one hour (#3900), by an
agent and its coordinator independently. Prefer mutating a **value** the assertion reads over
editing code structure, and read the error text before believing a red.

**Trap: a failed mutation and a working guard look identical.** Measured twice in one session
(#3895): a backslash edit that a heredoc collapsed, so the file never changed; and a
`-p:` override whose build was incremental and skipped `CoreCompile`, reporting the clean
number. A failed *search* returns nothing and looks like a finding; a failed *mutation* returns
green and looks like the system working.

**Trap: a mutation can LAND, EXECUTE, and still change nothing — because the system absorbs
it.** The traps above are mutations that never reached the code. This one reaches it and runs,
and the green is still not about your test. Measured in review of #4003: duplicating an
`insertRow` call left all 4 tests passing, which reads as "the `Company.Count()` assertion proves
nothing". An AL probe printed `company count = 1` — BC's provider `Insert` **refuses a
duplicate primary key**, returning `false` rather than adding a row, so the second call was a
genuine no-op *for the row count* and no second row ever existed. Note what that leaves: a
rejected operation and an idempotent one are indistinguishable through `Count()` and quite
different through the **return value**, which did move and would have diagnosed this more
cheaply than the probe did. A mutation
seeding a *distinct* company gave `Failed: 1, Passed: 3`, and the test was sound all along.

So step 2's landing check is necessary and not sufficient: confirm the mutation changed the
**observable the assertion reads**, not merely the source. Prefer mutating a value the assertion
consumes over duplicating or removing a call whose effect the system may deduplicate, clamp,
cache or ignore.

**Trap: a filter that matches nothing is a silent pass.** `dotnet test --filter
"FullyQualifiedName~SomeTests"` prints `No test matches the given testcase filter` and **exits
0**. Measured on #3882, where the filename and the four class names inside it differ. So quote
the `Total:` line from every run, and treat a run with no `Total:` line as *unverified* rather
than green — the exit code cannot tell you the difference.

**The harder half: a filter or a mutation target that matches the WRONG thing rather than
nothing.** A zero is at least conspicuous; a plausible number is not. Both measured on #3923 in
one pass: `~ExtensionRuntimeDeltasTests` returned **8** of 9, because the file declares a second
class (`…BcReaderTests`) the filter excluded — and a mutation aimed at
`TryBuildExtensionRuntimeDeltasXml` left every test green, because the tests call
`TryBuildExtensionRuntimeDeltasXmlForApp`, whose name has the first as a **prefix**. Mutating the
right one gave 7 of 9 red.

So: **count the tests you expected**, and after a mutation that leaves things green, check the
symbol you edited is the one the test path calls before concluding the test is weak.

**Trap: CI catches the opposite error, never this one.** A test that fails when it should pass
is red within minutes; one that passes when it should fail is caught only if somebody looks,
and until then it reads as coverage while protecting nothing.

**Choose the mutation to test a property, not to produce a red.** A mutation that reds *everything*
proves coverage exists; one that reds *exactly the right subset* proves the tests discriminate — and
only the second is worth anything on a coverage PR. Measured on PR #3947, where a reviewer replaced
both of the author's mutations and each replacement established something the original could not:
returning `null` from a `bool?` reader reds all three tests, while **inverting** the boolean preserves
`null`, so the absent-case test correctly stays GREEN — which is what proves that test is pinned to
`null` rather than riding along. And `? null : null` on a reader reds its tests whatever the fixture
holds, while making the reader **read the wrong one of two properties** produced
`Expected: [90502] / Actual: [90501]`, validating that the fixture's two ids are actually distinct —
a fixture with one id repeated would have passed the author's mutation and looked covered.

**Corollary: re-running the author's mutation is the weakest check a reviewer can make.** It tests
the same hypothesis by the same route. Pick your own.

A required step, not a tool: no framework, no CI job. One rebuild.

## Sister rules

- `batch-sibling-issues-by-file.md` — one RED→GREEN per closed issue; folding never exempts
- `guards-need-a-third-state.md` — a refusal path with no test is indistinguishable from a
  never-fire path: this rule, applied to the third state
- `loud-failures.md` — what a patch must justify, and in what form

History: docs/incidents/tdd.md
