# A company that did not finish initializing is a run-level result

Base App codeunit 2 `"Company-Initialize"` is what gives the runner's company its setup rows —
`Company Information`, `Source Code Setup`, the setup No. Series, `General Ledger Setup` and the
rest. The runner runs BC's own codeunit rather than fabricating those rows
(`AlRunner/CompanyInitializer.cs`). When it aborts part-way, the runner **keeps** what it wrote:
that partial state is worth +5 tests over not running it at all, measured both ways, and the
tests that never read a missing row are real results.

What #3538 settled is what the run then **records**. Before it: a `[warn]` line on stderr at the
moment of the abort, and nothing else — the summary, `--out`, `--output-json` and the JUnit
document were byte-identical to a clean run's, and the process exited 0. The report behind the
issue had roughly 2800 tests pass that way against a half-initialized database, with the few
that did fail reporting plain "cannot be found" and "must have a value" errors a long way from
the cause.

## What real BC does, and why that makes it a run-level condition

Codeunit 2's `OnRun` commits **once**, as its last statement, after `InitSetupTables()`,
`InitSourceCodeSetup()`, `OnCompanyInitialize()` and the rest have all run. Measured on
Microsoft's shipped `Microsoft_Base Application_28.1.49838.50256.app`,
`src/Foundation/Company/CompanyInitialize.Codeunit.al`: **one `Commit()` in 806 lines**, and it
is the final statement of the trigger.

So on a service tier the outcome is all-or-nothing. An error anywhere inside that trigger rolls
the write transaction back and the company does not acquire a subset of its setup rows; a
company that exists has the whole set. The state the runner is in after an abort is one **no BC
service tier can produce**, which is why it cannot be expressed as a per-test result — no test
is wrong, the database every test read is.

Two limits on that citation, stated rather than glossed: the subscribers on `OnBeforeOnRun` and
`OnCompanyInitialize` (18 files in Base Application alone) were not audited for a `Commit` of
their own, and the platform path that runs codeunit 2 during company creation lives in the
service tier, not in AL, so what a *failed* company creation leaves behind on disk was not
measured. Neither affects the conclusion drawn here, which rests on the single commit in the
trigger the runner itself invokes.

## The decision

The run **records** the condition everywhere it reports, and **does not exit 0**. It does not
refuse to run, and it does not discard results.

| surface | what it carries |
|---|---|
| printed summary | a `Company initialization: INCOMPLETE (N abort(s))` block below the totals, one line per abort naming the codeunit, the exception type and the message |
| `--output-json` | `companyInitFailures`: `codeunitId`, `codeunit`, `exceptionType`, `message`. Additive and null-omitted — a clean run's document is unchanged |
| `--out` | a record with `"kind": "company-init"` and `"classification": "company-init/partial"`, ranked ahead of the test failures it may explain |
| `--output-junit` | an XML comment per affected bucket, the same convention #2919 chose for a lost suite: a synthetic `testsuite` would have to invent counts, and this reports a condition without moving the numbers a dashboard plots |
| exit code | **2**, and only when the run would otherwise have exited 0 |

### Why exit 2, and why it is safe

2 is the code `docs/cli-output-paths.md` already established for "this says nothing about the
AL": a lost `--out` write, and a carried resume attempt that did not arrive. A partial company
is the same kind of statement — the database the AL ran against was not the one asked for — so a
consumer must not read it as "some tests failed", which is what 1 means. It is never *raised*
above what the tests earned: a run with a failing test still reports 1, a compile failure still
reports 3. `--no-strict-exit` still forces 0 for a consumer that wants the old behaviour, and
`--output-json`'s `exitCode` field still reports the real outcome in that mode.

The escalation was measured before it was chosen. On the last green `main` matrix run before
#3538 (workflow run `34216039259`, all eight legs green), the string `CompanyInitializer` appears
**zero times in all eight leg logs** — 1.1 MB per leg, covering the corpus run, `runner-extras`
and the unit-test step. Codeunit 2 completes on every BC version the matrix builds, so nothing
in CI trips this. #3054 measured it aborting on five of eight legs before #3067 fixed the cause,
which is why the condition is worth reporting rather than assuming away.

### What was rejected

**Fail the run before any test executes.** The tests that do not read a missing setup row are
real results — the report behind the issue had ~2800 of them — and `CompanyInitializer`'s own
header records the measured decision to keep the partial state. Refusing to run would trade a
silent wrong answer for no answer.

**Record it and keep exiting 0.** That is the state the issue was filed about. The condition is
machine-readable then, but every existing consumer — CI, a suite comparison, a person reading a
shell's exit status — still reads the run as clean, and none of them opts in to a new field
they do not know exists.

## The warm-cache half

`EnsureCompanyInitialized` runs only on a dependency-company-baseline cache MISS. On a HIT the
snapshot restored **is** the partial company, and before #3538 nothing said so: the abort was
reported once, by whichever app group missed, and every later group — and every later *process*,
through the disk tier — inherited a half-initialized company in silence. That is the emit-exclusion
cache defect exactly (#3476), where a warm run of a module missing objects came back
byte-identical to a clean one.

Two levers, in `AlRunner/TestExecutor.cs`:

- the in-memory cache stores the failure next to the snapshot, and a HIT re-reports it;
- the **disk** tier does not store an entry at all when the codeunit aborted, because that tier
  carries the snapshot and nothing else. A later process pays for the dependency baseline again
  — seconds, and it re-runs codeunit 2 itself — and gets a run that reports the condition, or a
  clean one once a runner fix has made the abort stop happening.

## Where the code is

- `AlRunner/CompanyInitializer.cs` — the catch path, the run-wide accumulator, and the
  `AL_RUNNER_TEST_FAIL_COMPANY_INIT` seam the proving tests drive it with
- `AlRunner/TestExecutor.cs` — the cache carry and the withheld disk write
- `AlRunner/Program.cs` — draining the accumulator into the bucket's `BucketResult`, and the
  exit-code escalation
- `AlRunner/Reporter.cs`, `AlRunner/JUnitReport.cs` — the four reporting surfaces
- `AlRunner.Tests/PartialCompanyInitializationTests.cs` — the proving tests, including the
  negative control and the cold-then-warm pair
