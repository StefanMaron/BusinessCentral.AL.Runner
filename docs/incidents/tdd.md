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
per-collision repeat count is not a property of that description at all: it moves with the regex
alone, and independent runs while writing this recorded 2, 7, 9, 11, 13 and 15 — a spread, not a
range to quote back, so any single number for it is an artefact of a pattern nobody wrote down
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


## #4343 — a restored mutation is still in the binary, and the red it produces looks like a finding

Filed from a reviewer's account of nearly filing a false finding on PR #4335. The reviewer
restored its mutation with `tools/apply-mutation.py --restore`, re-ran with `--no-build`, and got
`Failed: 1` on `ACodeunitsSubscriber_DoesNotSilenceThePageWithTheSameId` — across five runs, and
again with the class run alone. That is the exact shape of the cross-collection leak it had been
asked to look for. Rebuilding gave `2/2` and `56/56`.

### Re-derived before building on it

The issue said plainly that the five-run figure was the reviewer's measurement, not its author's.
Reproduced independently on this branch, mutating `RegisterPageSubscriberWitness` in
`AlRunner/Patches/RecordPatches.CodeunitSubscriberWitness.cs` to pass `CodeunitTypePrefix` — the
cross-kind leak the arm exists to catch:

| step | result |
|---|---|
| baseline, built | `Failed: 0, Passed: 2` |
| mutated, built | `Failed: 2, Passed: 0` — correctly caught |
| **restored in source, `--no-build` ×5** | **`Failed: 2` five times**, `mutation-verdict.py` reporting a confident RED each time |
| rebuilt | `Failed: 0, Passed: 2` |

### Why this one is worse than the traps already in the rule

Every other mutation trap in `tdd.md` fails toward a **green** that reads as coverage, and the
reader is told to distrust a surprising green. This fails toward a **red**, which is what a
reviewer is hunting, and it has all three properties that normally *end* an investigation:
deterministic, narrow, and on the right arm for the hypothesis. Running the class alone — the
usual cross-check — reproduces it, because the binary does not change.

And two correct practices point in opposite directions. `--no-build` is recommended elsewhere in
this repository (build → bootstrap → `dotnet test --no-build --settings engine.runsettings`,
because a build restores a pristine `Ncl.dll`). An agent following both lands on a stale binary.
The remedy is therefore not "stop using `--no-build`".

### Why the stamp, and not the two cheaper options

The issue named three. Touching the file already happens — which is *why* a plain `dotnet test`
recovers and only `--no-build` bites — and a printed warning on restore is read or not read. Only
recording the restore and refusing the verdict cannot be skipped by a reader in a hurry, and it
fits `mutation-verdict.py`'s existing `3 unmeasured` rather than inventing a fourth code.

### The false-refusal mode, found by its own control

The first implementation compared the restore stamp against the assembly named on the log's
`Test run for …` line. Its end-to-end GREEN control failed: after a restore at 02:03:20,
`dotnet build` wrote `al-runner.dll` at 02:03:52 while `AlRunner.Tests.dll` stayed at 01:54:28,
because no test source had changed. The mutated code almost always lives in a **dependency**, so
keying on the named assembly refuses a run that was correctly rebuilt — this check's own version
of the defect it exists to catch. Fixed by reading the newest `.dll` in the output directory.

Worth recording that the control is what caught it. The refusal direction passed throughout; only
the "a real verdict must still get through" arm could have found this, which is the argument for
pairing every refusal mutation with a control.

### A leftover the `.gitignore` entry exists for

Mutating `STAMP_NAME` to `.mutation-restore-stamp-RENAMED` left an untracked file behind at the
repository root, because the ignore rule carries the literal. Same cross-file contract as `SUFFIX`,
and the reason `test_mutation_verdict.py` pins that the two tools name the same file: that mutation
is invisible to `test_apply_mutation.py` on its own, which reported GREEN.

### The same defect twice, one population further out each time

The first revision keyed the refusal on the assembly the log names. The second keyed it on the
output directory. **Both were caught by a control, and neither by a refusal arm** — the refusal
direction passed throughout in both rounds.

| round | honest path wrongly refused | found by |
|---|---|---|
| 1 | a rebuilt **dependency** (`al-runner.dll` fresh, `AlRunner.Tests.dll` stale) | the author's own end-to-end GREEN control |
| 2 | a mutation in a **file no build reads** — a `.py` guard, a rule, a manifest | review |

Round 2 is the worse of the two, because the refusal is **unclearable**: `--restore` stamps every
mutation, the demanded rebuild is a legitimate no-op, nothing gains an mtime, and the stamp is
deleted by no code path — so it then refuses *unrelated* later runs. Reproduced on the PR head:
mutate `tools/mutation-verdict.py`, restore, `dotnet build` (exit 0, 2.06s no-op) → still
`UNMEASURED`, `gap=261s`; a second rebuild did not move it. The printed `remedy: rebuild, then
re-run` could not work.

Not a corner case: 41 `tools/test_*.py` guards, 20 `.github/scripts/test_*`, and the PR
introducing the check was itself such a change.

**The fix was already in hand and being discarded.** `--restore` wrote the mutated path on the
stamp's second line from the first revision, and `read_restore_stamp` called `fh.readline()`
once. Reading the second line and skipping the check for non-build-inputs is the whole fix.

The lesson is narrower than "write controls": a guard's refusal arms cannot find a path that
*should not* be refused, because they all pass. Only a control naming the honest path can, and
the honest population has to be enumerated deliberately — a build input is not the same set as
"a file I might mutate".

### A comment claiming a pin that was never written

`apply-mutation.py`'s `STAMP_NAME` carried a comment saying `test_apply_mutation.py` fails if it
and `.gitignore` drift. It did not: deleting the `.gitignore` line while leaving `STAMP_NAME`
intact returned 0 from both guards. Only `SUFFIX` had that pin, and the comment was written by
analogy to it.

Found by a **control that should have redded and did not** — the same instrument as the finding
above, in its other direction. The claim was true of the neighbouring constant and copied across,
which is exactly the shape `guards-need-a-third-state.md` records as "a guard that is safe only by
accident of a neighbour is not safe".

Fixed by adding the pin rather than deleting the claim, since the hazard is real: a committed
stamp refuses every verdict in a fresh clone, naming a restore nobody on that box performed.

**And the pin added to fix it was itself green on the mutation it was written to catch.** It
asked `am.STAMP_NAME in open(".gitignore").read()` — a substring test, and the name is a
substring of every rename of itself. Measured on three shapes (`-RENAMED`, `XYZ`, and a
`#`-comment-out): guard rc=0 in all three while `git check-ignore` reported the path **not
ignored**, which is exactly the hazard the check's own message describes. Only outright deletion
redded.

Four lines above it sat the sentence "a comment asserting coverage that does not exist is worse
than no comment", and this file recorded the weak pin as the fix. **A weak pin is the same defect
wearing the fix's clothes** — the third round of one shape in one PR.

The remedy is to ask the tool that owns the question: `git check-ignore -q`, which discriminates
all three shapes, because a substring test cannot separate "the rule is present" from "the rule's
name appears in a comment". The neighbouring `SUFFIX` pin had the identical weakness, pre-existing
and copied from; both were fixed together, since a known-weak pin beside a fixed one is
`guards-need-a-third-state.md`'s "safe only by accident of a neighbour".

### "Not listed" means "skip the check", so a wrong entry restores the defect

`BUILD_INPUT_SUFFIXES` listed `.sln`, which this repository does not have, and omitted `.slnx`,
which it does and which four workflows build. Measured with a stale binary: `.slnx` → `RED`
(check skipped), `.sln` → `UNMEASURED`. Counted with `git ls-files`: `.cs` 1223, `.csproj` 7,
`.props` 1, `.targets` 1, `.slnx` 1, `.sln` 0.

Low severity — no `.slnx` mutation appears in any recorded incident — but the direction is
asymmetric and that is the general point: **a missing extension silently restores the original
defect for that file type, while a spare one costs only a refusal a real rebuild clears.** So the
list errs toward listing, and is pinned against `git ls-files` rather than against a hand-written
roster, so the same omission cannot recur for a file type added later.

Considered and rejected (agreeing with the reviewer): inverting the predicate to "did any assembly
get newer". That is what `stale_binary` already computes, and it cannot separate *"nothing needed
rebuilding"* from *"the user forgot"* — which is the original defect.

### A defence that cannot fire on its documented input

`classify_exit`'s staleness check needs the `Test run for …dll (` line. Measured over **all 41**
`tools/test_*.py` guards: **zero** emit it — they are processes, not `dotnet test` suites. So the
check is defence in depth for the case where `--exit` is handed a dotnet log, and not protection
for Python guards; what protects those is the build-input gate. Both the code comment and the PR
body now say so, rather than claiming the broader thing.

### The fourth instance, at the outermost layer: the census that passes over nothing

Rounds 1-3 were checks that failed to catch something. This one is the *census that pins the
list* — the outermost guard, added in round 3 precisely so the `.slnx` omission could not
recur — and it could report a pass having asserted nothing.

`git ls-files` **raising** and `git ls-files` **succeeding with empty stdout** are different
events with the same falsy value, and only the first was handled. The second is reachable:
`git init` a directory and `git ls-files` exits 0, zero bytes, no stderr. Then `if _tracked:`
skipped both tree-keyed assertions and the run reported `all passed`.

The same "green because it never looked" shape as the three rounds above, one layer further
out, and in the file that records the through-line — which is what made it worth fixing rather
than noting, since it is not live under CI (a real checkout always has tracked files).

Now three refusals with distinct causes, each verified to fire:

| state | how it was produced | message |
|---|---|---|
| git raises | `git` off `PATH` | `git ls-files could not be run: [Errno 2] …` |
| git succeeds, empty | a stub `git` exiting 0 with no output | `git ls-files succeeded but listed no tracked files` |
| git succeeds, wrong tree | a stub printing `a.txt`, `b.md` | `returned 2 path(s) but no .cs/.csproj among them — the census is reading the wrong tree` |

The third is not the reported defect. It is the fourth-mechanism shape from
`verify-execution-not-the-tick.md` — a correct instrument reading the wrong subject — and a
census that inspected two irrelevant paths would otherwise have passed every extension check
vacuously.

### Two correct numbers for "what does the build-input mutation red", and they differ 6x

Reported as 24; re-derived as **4**. Both are right, and they measure different mutations:

| target | mutation | reds |
|---|---|---|
| the call site (`M4`) | `if mutated and not is_build_input(mutated)` → `if False` | **4** — exactly the four non-build-input controls |
| the predicate | `return path.lower().endswith(...)` → `return False` | **24** |
| the predicate, inverted | → `return True` | 11 |

The call-site mutation disables the gate while leaving `is_build_input` answering truthfully, so
only the four controls that depend on the gate move. Mutating the predicate to a constant also
reds every direct assertion *about the predicate* — the both-directions cases and the tree
census — which is a coarser result: it proves coverage exists rather than that the tests
discriminate (`tdd.md`, "choose the mutation to test a property, not to produce a red").

Worth recording because neither figure is wrong and a reader comparing them would assume one
was. **A mutation count is meaningless without naming the target** — and the two targets are the
predicate and its own call site, in the same file, each a plausible reading of "the build-input
mutation".

(An earlier draft of this paragraph said "three lines apart". They are 47 apart. A wrong number
inside a note about numbers is the shape this repository keeps re-learning, so the distance is
now stated as the relationship rather than as a count that rots on the next edit.)

### The fifth instance, and the sharper rule it gives: check BOTH sides of a boundary

`read_restore_stamp` walks up from a directory looking for the stamp and stops at the repository
root. Deleting that stop — `if os.path.exists(os.path.join(d, ".git")): break` — left **all 89
assertions green**.

Not dead code. Measured on a nested fixture: pristine returns `(None, '')`, the mutant returns a
real timestamp read from a **foreign repository's** stamp.

**Live, not theoretical.** `.claude/worktrees/` is nested *inside* the main repository tree, so a
stamp at the outer root sits above a worktree's `.git` file. Without the stop, one agent's
walk-up reaches another agent's stamp and answers `UNMEASURED` on an honest run — a false refusal
no rebuild clears, which is round 2's defect arriving by a different route. This work created a
dozen such worktrees.

No existing case reached it, because every reader-side test put the stamp directly in
`mkdtemp()`, so the walk exited on iteration one and the boundary never executed.

**The tell was an asymmetry, and it is the generalisable part.** The *writer's* identical boundary
in `apply-mutation.py` was pinned — removing it reds the worktree-`.git`-file test — while the
*reader's* was not. Same boundary, same file pair, one side guarded.

> When one side of a two-sided boundary is pinned, ask immediately whether the other is.

That is narrower and more actionable than "write controls", and it would have found this in one
query rather than five rounds.

The pair added for it discriminates in both directions: deleting the stop reds the two boundary
arms; replacing the walk with an unconditional `break` — the degenerate "fix" that stops crossing
the boundary by never walking at all — reds **only** the control asserting a stamp inside the
repository is still found.

### The through-line, after five rounds

Every one of the five was found by a control, or by a mutation aimed at a check's own blind side.
**None was found by the check's own arms**, and round 4's instance was inside the guard written to
prevent round 3's.

| round | the check | how it failed | found by |
|---|---|---|---|
| 1 | staleness vs the named assembly | refused a correctly rebuilt dependency | the author's GREEN control |
| 2 | staleness vs mtimes | refused forever for a non-build-input | review |
| 3 | the `.gitignore` pin | substring test, green on rename and comment-out | a control that should have redded |
| 4 | the tree census | passed over an empty-but-successful read | review |
| 5 | the reader's repository boundary | unpinned, while the writer's was pinned | the asymmetry |

## Figures moved out of the rule (#4539)

The rule now cites these measurements rather than restating them; the figures are what each
citation measured, frozen to that moment. Population figures (counts of labels, files,
transcripts on one box) were deleted outright rather than moved, because they go stale here too.

- **#3923.** `~ExtensionRuntimeDeltasTests` returned 8 of 9 tests; mutating the correctly-named
  method gave 7 of 9 red.
- **PR #3947.** Returning `null` from the `bool?` reader reddened all three tests.
