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
| exit code | **2**, and only when the run would otherwise have exited 0 — unless the manifest accepts the abort (below) |

### The targeted opt-out, and what the blanket one costs (#3561)

`--no-strict-exit` is not an opt-out from *this*: it forces exit **0 for everything** — a failing
test, a compile failure (3), a lost `--out` file, a carried resume attempt that did not arrive.
A suite that accepts one known abort had to give up its exit code entirely to stop tripping over
it, which is a strictly worse trade than the one it wanted.

The targeted opt-out is an expectations-manifest entry naming the initialization codeunit
(`docs/expectations.md` § `accept-partial-company-init`), with a mandatory free-text `Reason`:

```jsonc
{ "codeunitId": 2, "CodeunitName": "Company-Initialize", "Method": "*",
  "Mode": "accept-partial-company-init", "Reason": "<why this project accepts it>" }
```

It suppresses the 0 → 2 escalation and **nothing else**. The abort is still in the summary
(`[accepted: <reason>]`), `--out`, `--output-json` (`accepted`) and the JUnit comment; 1/3/4/5
are untouched; an abort of a codeunit no entry names still exits 2. An entry whose codeunit ran
to completion is drift and fails the run with "remove the entry".

**The residual, deliberately.** Drift fires only when the codeunit actually ran to completion in
this run. A run that never attempted it — no Base App in the bundle, or a clean dependency-company
baseline restored from disk without re-running codeunit 2 — leaves the entry inert, so a stale
entry is caught on the first run that initializes cleanly for real rather than on every run. The
alternative, failing whenever an entry did not match, is exactly what `--expectations-require-match`
is opt-in to avoid: the manifest directory is auto-probed and shared by every invocation.

### One abort, one line

N app groups sharing one cached dependency-company baseline each re-report the abort on their
cache HIT — deliberately: each really did run its tests against the partial company. Identical
records (same codeunit id, codeunit name, exception type and message) are collapsed into one
summary line carrying `×N app group(s)` and one `--output-json` entry carrying `count: N`; the
header keeps counting app groups. `--out` is not collapsed, because it is a per-bucket triage
worklist and each bucket's record belongs to that bucket. The app id is not part of "identical":
the accumulator records none, and the codeunit it records is always Base App's codeunit 2.

### Server mode

`--server` never builds a `BucketResult`, so before #3561 it never drained the accumulator: the
static list grew for the life of the process and no response carried the condition at all. It is
now drained once per request, in both the `runTests` and `execute` handlers, and the responses
carry `companyInitFailures` (same field shape as `--output-json`, null-omitted). The response's
`exitCode` escalates exactly as the CLI's does, because a client reading only `exitCode` is the
consumer this was filed for.

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

Both are measured rather than reasoned, and the measurement needs a fixture whose dependency
closure **writes rows**: with an empty snapshot the codec refuses to persist anything at all
(`not persisting: snapshot has 0 DataAccessSource(s)`), no disk entry exists in either arm, and
a warm run MISSes for a reason that has nothing to do with the withhold. So both tests build a
closure with `AlRunner.Tests/InstallSeedClosure.cs`, and the disk test carries a control arm —
the same fixture shape without an abort must reach `DISK-HIT` — so that the abort arm's `MISS`
is the withhold and not a fixture that never persisted. Remove the two levers and both tests
fail; that is what makes this section a claim about the code rather than about #3476.

## Where the code is

- `AlRunner/CompanyInitializer.cs` — the catch path, the run-wide accumulator, and the
  `AL_RUNNER_TEST_FAIL_COMPANY_INIT` seam the proving tests drive it with
- `AlRunner/TestExecutor.cs` — the cache carry and the withheld disk write
- `AlRunner/Program.cs` — draining the accumulator into the bucket's `BucketResult`, and the
  exit-code escalation
- `AlRunner/Reporter.cs`, `AlRunner/JUnitReport.cs` — the four reporting surfaces
- `AlRunner/Infrastructure/ResumeCarry.cs` — the abort crosses the watchdog-resume process
  boundary with the attempt's results
- `AlRunner.Tests/PartialCompanyInitializationTests.cs` — the proving tests: the negative
  control, the two cache levers, and the run that earns exit 1 on its own
