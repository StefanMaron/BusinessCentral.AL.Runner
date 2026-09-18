# Incidents behind `tdd.md`

## Two tests that passed against implementations doing nothing (2026-09-11)

`tdd.md` has always carried the right question — *would this test still pass if the
implementation always returned a default value?* — as something to **ask**. In one session
the honest answer was **yes** twice, and in neither case did asking surface it. Both were
found by executing the mutation: remove the implementation, rebuild, observe that the tests
stay green.

### Instance 1 — PR #3819 (#3504): the fixture constructed the relationship under test

The change translated a declaration read from `SymbolReference.json` into the key BC's live
binding table uses, by matching the raw AL identifier against each registered expression's
`Name`.

The unit fixtures were built as `FakeExpression("p790p790PageEditable", "PageEditable")` —
that is, they *asserted* that `Name` holds the raw AL identifier and the key holds the mangled
`Id`. Instrumenting page 790's live binding table on 28.1 showed otherwise:

```
[rv] key='p790p790PageEditable'  name='p790p790PageEditable'
[rv] key='Control159866013'      name='GLAccTotaling'
```

`Name` holds the mangled `Id` for exactly the population the fix targeted. The join therefore
fell through to `return declared;` for **all 7,173 expression-bound declarations** — a no-op.
The four accompanying AL tests all declared *literals*, which short-circuit before the lookup
runs, so they could not have exercised it either.

Three rounds of discussion treated the join's **correctness** without anyone noticing it never
executed. What settled it was dumping the live table, reproduced 3/3 and twice more on a
pristine uninstrumented rebuild.

The commit that removed the join (`eef47c8a`) states it directly: *"every test passed anyway
because the unit fake CONSTRUCTED the relationship it was meant to prove … tdd.md's question
answered the wrong way: the tests did pass against an implementation that did nothing."*

A second measurement from the same commit is why no repair was attempted: across the 92
`<Expression>` entries in 2,272 captured page-metadata documents, 59 needed a join, and page
60265 registers **two** keys for one identifier (`Control144826568` and `p60265p60265HideIt`,
both `SourceExpression='HideIt'`). The relation is one-to-many, so no mangling rule
reconstructs it.

### Instance 2 — PR #3882 (#2247): one of two guards was unproven at review

The PR adds a loud failure on a partial dependency emit at two call sites.

| guard | mutation result |
|---|---|
| `DependencyLoader.LoadOne` (`EMIT-EXCLUDED`) | `Failed: 1, Passed: 7` — sound |
| `DependencyMetadataProducer.Ensure` (`METADATA-EMIT-EXCLUDED`) | **all 8 green**, across two runs |

Confirmed a second, differently-shaped way: the only test references to the second stage name
assert the **naming convention** (`Assert.True(DependencyLoader.IsMetadataStage("METADATA-EMIT-EXCLUDED"))`)
or pass it as routing data. Nothing drives the throw.

The contrast is what makes the case: the same PR, the same author, the same session, one guard
proven and one not — and the difference was invisible until the mutation ran.

This one was caught in review and fixed before merge, so it never reached `main`. That is the
outcome the step exists to produce, and it is why the instance is worth recording: nothing
other than the mutation distinguished the two guards.

### Why asking is weaker than doing

The question is answered by reasoning about code you have just written while believing it
works, which is when introspection is least reliable. Executing the mutation is one rebuild and
is not a judgement call: the test goes red or it does not.

Note the asymmetry in cost. A test that fails when it should pass is found immediately, by CI.
A test that passes when it should fail is found only if someone looks — and it then protects
nothing for as long as it exists, while *reading* like coverage. #3819's fixtures would have
gone on asserting a false relationship indefinitely.

### Scope deliberately not taken

No mutation-testing framework, no tooling, no new CI job. The change is to which check is
**required** and that its result is **reported**, so a reviewer can see a number instead of
re-deriving it. Both instances were caught by a human-directed mutation taking one rebuild.


## The mutation that does not land (2026-09-11, #3895)

Requiring an executed mutation (above) created a second-order failure the first rule did not
anticipate: **a mutation that silently fails to apply leaves the test green**, and green after a
mutation is indistinguishable from "my test does not catch this" without looking at the file.

Two instances in the session that introduced the requirement, by different mechanisms:

**The edit never reached the file.** A reviewer mutating a backslash in `AlRunner.csproj` got a
passing test twice and nearly reported a working parity test as broken. In Python source `'\\'`
*is* the single-backslash string, and a shell heredoc collapsed it again, so the "mutated" file
was byte-identical. The third attempt landed and gave the real answer:

```
MUTATED_RC=1
Assert.Equal() Failure: Strings differ
Expected: ...Replace('\\','/')...
Actual:   ...Replace('\','/')...
```

**The build never reran.** Disabling the analyzer target with
`-p:EnableAspNetCoreAnalyzers=false` reported `AD0001=0`, reading as "the target is a no-op".
The build was incremental and had skipped `CoreCompile`; a forced clean rebuild showed the
analyzers present. The same session also produced two *invalid* mutations of a third kind:
renaming a target carrying `BeforeTargets="CoreCompile"` does not disable it, and wiping `obj/`
wipes the restore.

So three distinct ways to not-mutate, all presenting as one green test.

**Why this is not the existing false-zero class.** `CLAUDE.md` and
`verify-execution-not-the-tick.md` cover a zero from a **query**. Here the instrument is an
**edit**, and the direction is worse: a failed search returns nothing and looks like a finding,
while a failed mutation returns green and looks like the system working correctly.

Both were caught by the person running them noticing the result was too clean, and both were
reported rather than quietly fixed — which is the only reason there are two instances to write
down instead of one.


## A filter that matches nothing is a silent pass (2026-09-11)

Found while verifying #3882's fix. Running

```
dotnet test AlRunner.Tests/AlRunner.Tests.csproj \
  --filter "FullyQualifiedName~DependencyEmitExclusionLoudnessTests"
```

printed `No test matches the given testcase filter` and **exited 0**. The filter was the
*filename*; the file declares four classes under different names
(`DependencyEmitExclusionMessageTests`, `...StageRoutingTests`, `...EndToEndTests`,
`DependencyMetadataPartialEmitTests`).

The coordinator had already written "EXIT=0" into a verification note before noticing the run
had executed nothing. Same family as the mutation that does not land: **an action that silently
did nothing reports success**, and the exit code cannot distinguish it from the real thing. The
discriminator is the `Total:` line, which a no-match run does not print at all.


## A mutation that breaks the build fails loudly and proves nothing (2026-09-11, #3900)

The landing check added above catches the **silent** direction — a mutation that does not apply
leaves the test green. This is the other direction, and it is easier to accept because it *looks*
like the check working.

Verifying #3900's null-forgiving ratchet (`Assert.Equal(92, converted)`), the coordinator removed
one converted call site by text substitution. The run returned **exit 1** — the shape of a caught
regression. It was 10 `error CS` lines:

```
RunnerPageInstance.cs(1336,37): error CS1002: ; expected
RunnerPageInstance.cs(1336,37): error CS1513: } expected
```

A build that does not compile cannot exercise an assertion. The tell is the missing `Total:`
line, the same discriminator the `--filter` trap uses: a run that never executed a test prints no
summary.

Re-done by mutating the **expected value** (`92 -> 91`) instead, which compiles and isolates the
assertion: `0 compile errors`, `Expected: 91  Actual: 92`, `Failed: 1, Passed: 4, Total: 5`. That
is what proves 92 is measured rather than asserted.

**The implementing agent hit the same class independently, in the other direction**, and reported
it rather than accepting the result: its first mutation *added* an unconverted line without
removing a converted one, and the ratchet stayed green — correctly, since the converted count was
still 92. It re-did the mutation as a real removal.

Two people, one guard, one hour, two different ways for a mutation to prove nothing. The general
form: **a mutation must change what the assertion reads, and nothing else.** Structural edits to
code are the risky kind; a value the assertion consumes is the safe kind.


## One red proves something is covered, not which thing (2026-09-11, #3917 / #3912)

`tdd.md` already carries *a test that names the thing is not a test that drives it* — a test that
mentions a symbol without reaching it. This is the adjacent failure: tests that **do** drive real
code and **do** go red under mutation, while covering only one of two observables.

**#3912** recorded the first instance: mutating `ReadEnumExtensible` to `return null` left six
tests green, because they exercise the render rather than the compiler-side reader.

**#3917** is the second and the sharper one. A codeunit derivation feeds two independent
renderings — an equivalence **projection** (numeric mask) and the AL-observable **virtual table**
(letter string). A reviewer reverted only the AL-observable half:

```
~CodeunitMetadata|~CodeunitSymbol|~MetadataEquivalence   Failed: 0, Passed: 56
~VirtualTable|~AllObj|~Inherent|~Namespace              Failed: 0, Passed: 196
```

252 tests green with the rendering AL actually reads reverted. All three new test files reference
the virtual-table rendering zero times.

**What makes it sharp: the PR's own body argued the point correctly.** It said fixing only the
projection *"would have closed the issue while leaving `CodeUnit Metadata` still answering BC's
default to AL"*. The author identified the trap, wrote it down, fixed both renderings in the
code — and tested one.

So awareness does not close this, and neither does prose. What closes it is running the mutation
**per observable**. A single red tells you the fix is covered somewhere; it does not tell you
where, and "somewhere" is exactly what a reader infers as "everywhere".

Generalises past enums and codeunits: wherever a derivation feeds both an equivalence projection
and an AL-observable surface — pages, queries, reports, permission sets — those are two
observables and each owes its own red.


## A red from the engine-bootstrap guard reads as a caught regression (2026-09-12, #3948 / #3957)

`BcEngineUnbootstrappedGuard` (`AlRunner.Tests/BcEngineCollection.cs`) fails a `bc-engine-serial`
test on a box that has BC artifacts but never ran `tools/engine-test-bootstrap.sh`. That is
deliberate (#3078, #3835): the skip it replaced printed `Failed: 0, Passed: 0` and exit 0, a
**false green**. The same design produces a **false red** during a mutation check.

An agent running #3948's premise mutation — destroy a sidecar write, see whether the suite notices
— got `Failed: 18, Passed: 163`, which reads as "already covered, close the issue". All 18 failed in
under 1 ms with `REFUSING TO SKIP`. After a Release build, the bootstrap and
`--settings engine.runsettings`, the same mutation gave `Failed: 0, Passed: 181, Skipped: 0` —
nothing noticed, the opposite conclusion. Four more agents hit the guard locally the same day on
`BcEngineReadinessGuardTests.Ready_IsTrue_WhenArtifactsAreProvisioned`, reproduced here at `bb66be98`
as `Failed: 1, Passed: 3, Total: 4` with `[1 ms]`.

Unlike the build break above, this shape **prints a `Total:` line**, so the missing-summary tell
does not catch it.

**Neither tell the issue proposed is enough on its own.** Inverting the guard's own
`IsRecoverableLocally` check and running `BcEngineUnbootstrappedGuardTests` gave
`Failed: 9, Passed: 20` — a genuine mutation RED — with five failures at `[< 1 ms]` and two
carrying `REFUSING TO SKIP` under `Actual:`. What separates the two is where the text sits: the
guard's failure puts it on the **first line** of `Error Message:`; a test asserting over the guard
puts it inside an assertion's `Actual:`. `tools/mutation-verdict.py` keys on that, and
`tools/test_mutation_verdict.py` holds both recordings; keying on the whole failure block instead
reds exactly those two checks.

## The mutation that landed, executed, and changed nothing (2026-09-13, PR #4003)

A reviewer's first mutation on #4003 duplicated an `insertRow` call, expecting the
`Company.Count()` assertion to go red. All 4 tests stayed green — the shape that reads as
"this assertion proves nothing".

It was not. An AL probe printed `company count = 1`: BC's provider `Insert` **refuses** a
duplicate primary key — it returns `false` and adds no row, and the first row's payload
survives while the second call's is discarded. `Insert(true)` behaves the same. So inserting
the same company twice yields one row.

The precision matters, and a reviewer supplied it against the first wording of this entry
("primary-key idempotent"). Rejection and idempotence are indistinguishable through `Count()`
and quite different through the return value: a reader taking "idempotent" literally would
conclude that *no* observable moved and stop looking, when in fact the cheapest available
diagnostic had moved all along. The mutation reached the code,
compiled, and executed; the *system* absorbed it. A second mutation seeding a **distinct**
company produced `Failed: 1, Passed: 3` with an `Assert` failure and the other three green, so
the test discriminates exactly as intended.

This is distinct from the two mechanisms already recorded (#3895): there the mutation never
reached the code at all — a heredoc collapsed a backslash, and an incremental build skipped
`CoreCompile`. Here every step of the landing check passes. What fails is the assumption that a
changed *source* implies a changed *observable*.

Caught only because the reviewer applied step 2 to the property rather than to the file. Had it
been reported as found, a sound test would have been recorded as weak, and the natural follow-up
— strengthening a test that needed nothing — would have been wasted work resting on a wrong
belief about the provider.

## Seven review rounds on one mutation tool, every gap the same shape (#4316, PR #4321)

`tools/apply-mutation.py` exists because a mutation that silently fails to apply leaves the suite
GREEN, and GREEN reads as "the guard did not catch this" when it means "this was never applied".
The tool refuses unless its anchor matched exactly once.

It took seven review rounds. Every round found a real gap, and **every gap was the same defect
one level up**: a property asserted on the member the reviewer had shown, and merely *produced*
on the rest of its population.

| round | population | what was unpinned |
|---|---|---|
| 1 | `--restore` | reported `APPLIED` while leaving mutated code, and deleted the only backup |
| 2 | the arms of `apply()` | the byte-identical arm — stayed GREEN under `if False:`, returned a *measured* code after stranding a backup |
| 3 | the two AMBIGUOUS fixtures | the 2-match fixture asserted `code` and `body`, never `msg`, so a hardcoded `3` published a wrong count |
| 4 | the ten `return REFUSED` arms | four asserted, six merely produced; flipping one made a failed `--restore` report success |
| 5 | the enumeration's cases | a case satisfied the count without reaching the arm it named; a constant `4` stood in for platform-skipped cases |
| 6 | the instrument | the tracer was correct but unasserted — `return rc, set(ALL_LINES)` printed full coverage and passed |
| 7 | the instrument's **scope** | a line number is not a line: the stdlib executes those integers in its own files |

**The escape is always one move: a constant standing in for a measurement.** `2 times`, then
`+ 4` for skipped cases, then `set(_REFUSAL_LINES)` for the trace. Each looks exactly like the
value it replaces, and each passes.

**Round 7 is the one worth reading.** `_REFUSAL_LINES` holds bare integers, and the standard
library executes those same numbers in its own files. The collision count depends entirely on how
much foreign code runs, so what is worth recording is the **floor**, not a headline figure. Even
the most trivial probe — one `re.compile("x")` plus a `tokenize` pass and a `mkdtemp` — collides
**6** times across **5** of the ten arms; a realistic pattern reaches 9-10 across 6. The
per-collision repeat count is not a property of that description at all: it swings from 2 to 11
with the regex alone, so any single number for it is an artefact of a pattern nobody wrote down
(measured three ways while correcting an earlier figure here that was exactly that). What matters
is that the floor is well above zero for any workload.

The consequence is what the count is for: deleting the tracer's file filter left all 51 checks
green at `10/10`, and deleting the filter *and* removing a case from the roster still read
`10/10`, with that arm "reached" only by `tempfile.py`.

Two lessons, and the second is the one that actually ended the regress:

1. **Make identity part of the key, not a filter in front of the data.** A filter can be deleted
   and the data still look right; a key cannot.
2. **A structural fix that nothing can falsify is not finished.** Keying on `(file, line)` was
   correct and both escape mutations stayed GREEN against it, because the existing empty-set probe
   was a single attribute lookup — it ran almost no Python, so no foreign frame existed to be
   misattributed. It could falsify a constant and nothing else. A probe doing real foreign work
   (`re.compile`, `tokenize`) that must still trace to empty is the assertion that discriminates.

**Choose the assertion a constant cannot satisfy.** "A known case traces to one arm" can be faked
by returning everything; "a case reaching NO arm traces to the EMPTY set, while doing real work in
other files" cannot.

Two incidental traps, both of which cost real time:

- **A stale `__pycache__` outlives `--restore`.** A probe read `(0, <a REFUSED arm's message>)` —
  which reads exactly like a latent success-on-failure defect — from bytecode written during an
  earlier mutation. The source was correct throughout; `dis.dis` showed the loaded function
  executing `LOAD_GLOBAL APPLIED`. `python3 -B` does not help: it suppresses *writing* a `.pyc`,
  not reading one.
- **Mutating `SUFFIX` breaks the tool's own `--restore`**, because restore resolves the backup
  name *through* the mutated constant. The backup is never destroyed, only unfindable. Recovering
  it by hand with `mv` then dropped the file's exec bit, which was committed as `100644` and made
  the documented `tools/apply-mutation.py <file>` invocation fail with `Permission denied` for
  everyone — while every test kept passing, because they import the module.
