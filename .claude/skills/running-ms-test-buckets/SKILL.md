---
name: running-ms-test-buckets
description: Run Microsoft's BaseApp test buckets through AL Runner to find real gaps — where the sources come from, the configuration that must be exact, how to size a run, and how to turn failures into issues worth filing. --test-data is mandatory; without it roughly 40% of failures are missing setup data rather than defects. And even with it, --test-data presents a RESTORED CRONUS, not one prepared the way Microsoft's pipelines prepare it, so some failures are an incomplete data recipe on our side rather than runner defects — triage for that before filing. Use when generating work from the Microsoft surface, when triaging a bucket failure, or when measuring where the runner stands against it.
---

# Running Microsoft's BaseApp test buckets

Microsoft ships 33 test buckets inside the BC artifact — about **40,550 tests** — and they run
through AL Runner as ordinary bundles, with no container. That makes them the largest supply of
real, un-guessed work available: every failure is a concrete difference between the runner and
what Microsoft's own tests expect.

The danger is the opposite of scarcity. A badly configured run produces thousands of plausible
failures that are not defects at all, and an agent that clusters those files a stream of
confident, wrong issues.

## `--test-data` is mandatory

Not a refinement — a correctness precondition for the *conclusions*, not just the pass rate.

Measured on Tests-SMB (1,027 tests): **259 passing without test data, 595 with it.** More
importantly, in a full no-test-data run of 29,514 classified failures, the largest clusters were

```
2690  Order Nos. must have a value in Purchases & Payables Setup
2214  The General Posting Setup does not exist
2020  Order Nos. must have a value in Sales & Receivables Setup
1507  Invoice Nos. must have a value in Sales & Receivables Setup
1001  There is no Unit of Measure within the filter
```

Roughly **40% of all failures were missing setup data**, not runner defects. Clustering that run
and filing the top items would have produced a stream of issues describing nothing real.

With test data the same bucket's top clusters are genuine runner gaps — missing trigger
dispatch, unsupported filter kinds, silently skipped handlers. Those are worth filing.

**So: never file an issue from a run without `--test-data`.** A no-test-data run is legitimate
for measuring speed or for bisecting a regression, never for deciding what is broken.

### …and `--test-data` still gives a restored CRONUS, not a *prepared* one

The 40% above is the coarse form of a sharper fact. **Microsoft's pipelines run independent
data-preparation steps on top of CRONUS before their tests execute.** The blueprint for those
steps lives in those pipelines, and we have not replicated it. `--test-data` hydrates from the
demo backup, so it presents the company **as restored** — not as Microsoft's tests were written
against.

A whole class of Microsoft-bucket failures is therefore **neither a runner defect nor a BC
divergence**. It is an incomplete data recipe on our side, and those tests are expected to fail
until someone replicates the preparation blueprint.

**This is deliberately not the priority** (Stefan, resolving #2730): fix the clear runner
failures first. Chasing the recipe to reach 100% green is the *very end* of the work, once
nothing else is left.

#### The triage rule

When a Microsoft bucket test fails, ask **"is this a data-recipe failure?" before treating it
as a runner defect.** The tell is that the runner did the right thing for the company it was
handed — the posting, the validation, the count are all correct *given the data*, and the
expectation encodes a differently-prepared company.

Recipe failures:

- are **expected**, and stay failing until the recipe is replicated;
- must **not** be fixed by bending the runner to match the expectation;
- must **not** be filed as runner gaps;
- must **not** be classified `expect-divergence` — that mode means the runner intentionally
  answers differently from BC *permanently* (`docs/expectations.md`), and this is neither
  permanent nor a disagreement with BC. Calling it divergence records a fixable data gap as a
  settled decision.

#### The worked example, measured

`Codeunit134157`, three tests asserting a G/L Entry count, each off by exactly +1:

| `General Ledger Setup."Additional Reporting Currency"` | result |
|---|---|
| `EUR` — what `--test-data` presents | **3 failed / 3 passed** |
| blank — what Microsoft's test database has | **6 passed / 0 failed** |

Nothing else changed. `HandleAddCurrResidualGLEntry` opens with

```al
if AddCurrencyCode = '' then exit;
```

so given an ACY, **BC's own residual rule correctly adds a sixth G/L Entry**, and the tests
correctly report six where they expect five. The runner was posting correctly for the company
it was handed. There is no runner defect anywhere in that chain.

#### The scale — this is a class, not a cluster

#2730 already records two more from the same single setting: codeunit 134880's four `Reverse…`
tests, and a 16-test exchange-rate cluster (`There is no Detailed Cust. Ledg. Entry within the
filter` after report 596) that cannot be diagnosed cleanly while ACY is set. #2833 is a fourth.
One field of one setup table, four independent clusters — which is what makes this a recipe
problem rather than a handful of odd tests. Expect other prepared state to behave the same way.

#### One thing this does NOT explain, and must not bury

Under ACY the runner's Additional-Currency amounts **miss balance by 0.01** — debits
`54,426.58` against payables `-54,426.57`. If the recipe blanks ACY, that divergence becomes
**unreachable in these tests rather than fixed.** It may still be a real runner defect. Do not
let "explained as a recipe gap" be read as "the arithmetic was fine".

## Getting the sources

The buckets live in the platform artifact under `Applications/BaseApp/Test/` as
`Tests-*.Source.zip`, beside the `.app` files. `AlRunner.Provisioning/ArtifactDownloader`
fetches them with an HTTP ranged read of the ZIP central directory rather than downloading the
whole artifact — `tools/DownloadArtifacts test-sources` and `test-data` are the entry points.
Each zip carries its own `app.json` and needs no edits; the `$(app_*)` version placeholders are
fine.

`--test-data` additionally needs the demo backup (`BusinessCentral-W1.bak`, ~900 MB, from the
sandbox artifact) at the selected build's artifact path, and the backup reader binary the runner
looks for at `~/.cache/al-runner/bcbak/bcbak`.

## The configuration that must be exact

Get these wrong and the numbers mean nothing:

- **Company is `CRONUS International Ltd_`** — trailing underscore, the SQL form, not a period.
  The run fails loudly listing both companies otherwise.
- **Both package caches**, `--package-cache` is repeatable: the platform apps and the test apps.
- **Raise `AL_RUNNER_EMIT_TIMEOUT_SEC`** well above its default for a large bucket. It is
  wall-clock, and a big bundle's emit takes minutes; under `--jobs` it is scaled per worker, but
  a single large bucket still needs headroom.
- **Pass a private `--cache <dir>`.** The shared cache is not keyed on the runner binary, so
  another process's build can silently change your results.

## Sizing a run

Do not run all 33 at once to answer a question. Pick by what you are asking:

- **A quick signal** — Tests-SMB (1,027 tests, ~2 minutes warm with test data). Also the natural
  known-good baseline: 259 without test data, 595 with.
- **A representative sample** — Tests-ERM alone is 9,496 tests, about a quarter of the surface,
  and its cluster ranking has matched the full run's. Big enough to rank work, small enough to
  finish.
- **The complete picture** — all buckets, but expect hours and size the worker count from
  measured headroom.

Memory, measured after the per-worker GC tuning: roughly **1.1 GB per worker** without test
data, **~2.3 GB with it** (including its backup-reader sidecar). Derive the job count from free
RAM, never hardcode it, and set `MemoryHigh` below `MemoryMax` so a cgroup throttles before the
kernel's global OOM killer starts choosing victims elsewhere on the machine.

A single bundle cannot be split across workers, so the largest bucket sets the wall-clock floor
however many workers you add.

## Turning failures into issues

1. **Cluster by normalized message** — strip ids, numbers, quoted names, GUIDs and dates so one
   defect lands in one bucket. `scripts/` carries the tooling; when attributing a failure to a
   bucket, key on the result header, not on the run's planning lines, or every failure is
   credited to whichever bundle was announced last.
2. **Read the top cluster's stack, not just its message.** Three shapes look identical and need
   different responses:
   - a **real gap** — the runner refuses or mishandles something BC supports;
   - a **cascade** — one early failure leaves state broken for the rest of the codeunit. In one
     measured case **46 of 47 failures were a cascade** from a single test that renamed a row and
     died before restoring it. Fixing the first test fixes all 47, and filing 47 issues would
     have been noise;
   - a **symptom** — the failure is downstream of something else entirely. "Declared UI handler
     was not executed" turned out to have at least two unrelated causes.
3. **Confirm against a clean cache before filing.** A cache left inconsistent by a killed run
   silently cost 76% of passing tests once, and three commits were bisected before anyone tried a
   fresh cache. One re-run is cheaper than one wrong issue.
4. **File with the measured count.** "955 failures, 77% of this cluster, here is the sub-shape
   breakdown" is actionable. "Some tests fail" is not.

## Known walls — do not refile these

Large clusters that are already understood, so a fresh run does not generate duplicates:

- Failures that vanish with `--test-data` (see above) are configuration, not defects.
- **Failures that vanish when the company is *prepared* the way Microsoft's pipelines prepare
  it** — the data-recipe class above — are not defects either. The ACY clusters (#2730, #2833,
  codeunit 134157, codeunit 134880's `Reverse…` four, the 16-test exchange-rate cluster) are the
  known instances. Do not refile them, do not bend the runner to them, and do not mark them
  `expect-divergence`.
- `RunObject`-only page actions are refused deliberately and loudly; supporting them is a
  feature, not a bug fix.
- The task scheduler, live external connections, report rendering, SMTP and HTTP egress are
  permanently out of scope — `docs/scope.md` is authoritative. A typed out-of-scope refusal with
  a named reason is the runner working correctly.

Before filing, search the issue queue for the area. A measured cluster is often already filed,
sometimes several times over, and the whole set usually shares one root cause worth fixing
together.

## In CI

`.github/workflows/ms-bucket.yml` runs one bucket on a hosted runner with the full
configuration, `workflow_dispatch` only. It is a measurement job, not a gate: green means it
produced a number, not that the suite passed. Prefer it over a developer machine when the
machine is also doing something else.

## Sister material

`autonomous-cycle` — the unattended loop this feeds, and the gate that must pass before a
cluster becomes an issue.
`project_ms_test_collections_run_recipe` in session memory — the original recipe and its history.
