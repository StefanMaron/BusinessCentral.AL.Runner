_Written by Claude Code agent `stma-auto-2` for the account holder. This is a feasibility measurement, not a feature PR._

**Question:** if I subscribe to an event in the sales posting routine, can the runner find which Microsoft Base App tests reach that event, and run only those?

**Short answer:** yes, with one correction. Selecting the individual tests that raised the event missed 14 of 36 affected tests. All 14 misses have one cause: shared setup that the first test of a codeunit runs for all the others (filed as #5035). Selecting whole codeunits instead had no misses, and running that selection took 137 s against 356 to 437 s for the full bucket.

## Setup

- Bucket: **Tests-SMB**, 1,028 tests. I chose it over Tests-ERM (9,496 tests) because the measurement needs seven full-bucket runs, and ERM runs take far longer and more memory than this shared box had free. Tests-SMB posts sales documents in 11 codeunits (O365 sales invoice, credit memo, totals and so on).
- BC 28.4.53241.53955 (engine built for that build), `--test-data` from the 28.4.53241.54318 W1 backup, backup reader `bcdb 0.1.2`, company `CRONUS International Ltd_`, private `--cache`, `--test-timeout 300`, `DOTNET_GCHeapCount=2`.
- Baseline CLI run: **733 pass, 295 fail, 0 hangs** (no codeunit had to be parked), 356 s wall. Control run (bucket plus an empty extension, the same app set as the probe runs): 732 pass, 296 fail.
- Probe extension: one app per subscriber, each subscriber calls `Error('PROBE-<n>')`, run one at a time so one probe cannot hide another:
  1. `Sales-Post` (80) `OnBeforePostSalesDoc`
  2. `Sales-Post` (80) `OnAfterPostSalesDoc`
  3. `Sales Line` (37) `OnAfterValidateEvent` on `Quantity`
  4. `Sales-Quote to Order` (86) `OnBeforeOnRun` (expected to be rare)

## Finding 1: Base App events were already recorded; no extra seeding was needed

The premise was that #4999 only seeds event scopes of stamped request modules, so a Base App publisher with no subscriber would never reach the dispatcher. On this configuration that is not the case. The recording run listed 16 stamped modules, including the Microsoft dependency modules (`Dep_Microsoft_...` and content-hash-named modules), and seeded **24,033 event scopes on 8,085 publisher objects in 6.0 s**, once per server process. The prototype that seeds every unstamped AL app assembly found **0** such assemblies. Keys like `ev|Codeunit|80|OnBeforePostSalesDoc` were recorded without the prototype.

I did not establish which load path makes Microsoft's apps stamped here, or whether some other path leaves them unstamped. `docs/server-mode.md` says the seeding covers "not Microsoft's Base or System Application"; on this box that sentence is wrong.

## Finding 2: recording cost

Same server process, warm, same bucket:

| request | wall | pass / fail |
|---|---|---|
| plain `runTests` | 235.3 s | 728 / 300 |
| `perTestCoverage` recording | 252.7 s (+7%) | 728 / 300 |
| first request, recording, cold | 366.5 s | 728 / 300 |
| first request, plain, cold (another process) | 312.0 s | 729 / 299 |

Peak RSS rose from 4.7 GB to 6.5 GB over three requests. The recording holds **6,305 distinct keys and 415,438 test-key pairs** for 1,028 tests (median 189 keys per test, 90th percentile 833, max 2,462). As plain JSON that is 17 MB; 1.6 MB gzipped.

The box was shared with other work, so full-run wall times varied between 284 s and 437 s for the same bucket. Compare times within one row group only.

## Finding 3: selection against the reference result

"Truth" is the full bucket run with the probe installed. Strict truth: tests failing with `PROBE-n` in the message. Broad truth: strict, plus tests whose outcome differs from the control run. The broad set matters because 25 tests failed with `Unhandled UI: Page 700`: the posting collected the probe error into BC's error-message page, so the message no longer says `PROBE-1`.

| probe | selected tests | truth strict / broad | missed (by test) | selected by codeunit | missed (by codeunit) | over-selected (test / codeunit) | selected tests already failing in control |
|---|---|---|---|---|---|---|---|
| 1 OnBeforePostSalesDoc | 47 | 36 / 62 | 14 | 275 (11 codeunits) | 0 | 0 / 214 | 9 |
| 2 OnAfterPostSalesDoc | 47 | 36 / 62 | 14 | 275 | 0 | 0 / 214 | 9 |
| 3 Sales Line Quantity OnAfterValidate | 212 | 224 / 224 | 14 | 410 (18 codeunits) | 0 | 2 / 186 | 110 |
| 4 Quote to Order OnBeforeOnRun | 0 | 0 / 0 | 0 | 0 | 0 | 0 / 0 | 0 |
| union | 212 | 224 / 224 | 14 | 410 | 0 | | |

- **The misses are all in codeunit 139126 "O365 Activites Tests"** (14 tests). Its `Initialize()` posts 12 sales invoices once, guarded by `isInitialized`. The recording attributes that posting to the first test, `CalcOverdueSalesInvoiceAmount`, which is selected. With the subscriber installed, setup never finishes, so every later test runs `Initialize()` again and fails. The tests' recordings are correct for the run they were made in. The problem is that a test's result depends on setup an earlier test performed. This affects `affectedOnly` statement coverage the same way. Filed as #5035.
- The one "broad" miss for probes 1, 2 and 4 is `Codeunit138009.AmountOnUnpostedCrMemos` / `AmountOnQuotes`. These tests fail on random amounts off by 0.01, and they changed result between two identical plain runs too. That is noise, not a miss.
- By test, there was no over-selection for the posting events: every one of the 47 selected tests failed with the probe installed. Probe 3 is recorded per table (`trig|Table|37`), not per field or per trigger event. Here that over-selected only 2 tests, because nearly every test that touches a sales line validates `Quantity`.
- Tests already failing for runner reasons give no signal: 9 of 47 for posting, 110 of 212 for the sales line probe.
- **Recording is order- and state-dependent.** A cold and a warm recording in one process differed for 230 tests. Events behind session caches (for example `GetDatabaseTableTriggerSetup`) are raised only by the first test that fills the cache. The keys for the four probes were identical across all three recordings, but a selection index has to be recorded in a fresh process, in bucket order.

## Finding 4: time to run only the selection

With probe 1 installed, `AL_RUNNER_EXACT_TESTS` (spike-only switch):

| run | tests | wall | result |
|---|---|---|---|
| full bucket | 1,028 | 356 to 437 s | 36 `PROBE-1` failures |
| selected by test | 47 | 49 s | all 47 fail; 22 with `PROBE-1`; same outcome as the full run for 47 of 47 |
| selected by codeunit | 275 | 137 s | 36 of 36 `PROBE-1` failures; same outcome as the full run for 275 of 275 |

In the two selected runs, 13 to 20 s of wall time was spent outside test execution (startup, compile, test data load).

## What was measured and what was assumed

Measured: every count and time above, on one bucket, one BC build, four events. Assumed: that Tests-SMB is representative of the other buckets. The shared-setup pattern is common in Microsoft's test codeunits, so I expect the by-test miss rate to hold elsewhere, but I only measured it here. I did not measure Tests-ERM, and I did not test page trigger events, which #4999 already treats as "run everything".

## What a developer-facing mode needs (for example `--ms-tests-for <extension>`)

1. **A per-build event index for the Microsoft surface.** Recording each bucket in a fresh process gives a test-to-key table. Scaled from this bucket, the full 40,530-test surface is about 16 million test-key pairs, roughly 65 MB with 4-byte ids before compression. It should be built once per BC build in CI (`ms-bucket.yml` / `ms-surface.yml` with recording on) and downloaded, not recorded on every developer machine (a single-process run of the whole surface takes over an hour).
2. **Selection at codeunit level** (#5035), or a reliable way to attribute shared setup to the codeunit.
3. **Key mapping from the extension.** The runner already reads a module's `[EventSubscriber]` bindings (`CurrentModuleSubscriberBindings`) and turns them into keys (`BindingEventKey`); a tableextension maps to `tbl|` keys (#5010). Page trigger events and other object types still need a "cannot select" answer.
4. **A stored per-test result per build**, so the mode can report "newly failing with your extension" separately from "already failing in the runner", and can mark known flaky tests such as the 138009 amounts.
5. **Provisioning for the developer's BC build.** Today `--auto-provision` fetches the demo backup (933 MB for 28.4, #4923) but not the backup reader (#4925), the Microsoft test apps (21 MB for 28.4, currently `tools/DownloadArtifacts test-apps`), or the bucket sources (`tools/DownloadArtifacts test-sources`, 199 KB compressed for Tests-SMB). Platform apps are 118 MB.
6. **The CLI itself:** read the extension, look up keys in the index, pick buckets and codeunits, run them with the extension installed and `--test-data`, and report against the stored results.

## Recommendation

It is feasible, and the numbers support building it. In order:

1. #5035: codeunit-level selection. It fixes the only misses found, and it affects `affectedOnly` today.
2. Correct the `docs/server-mode.md` statement about Microsoft apps not being seeded, after checking which load path stamps them.
3. A recording mode for `ms-bucket.yml` that writes the per-test key table per bucket, and publishes it per BC build.
4. Auto-provisioning of the test apps and bucket sources (with #4925 for the reader).
5. The `--ms-tests-for` CLI on top of 1 to 4.

## How to reproduce

Branch `agent/stma-auto-2/issue-3415-ms-selection-spike` (not for merge). Runner switches: `AL_RUNNER_EVENT_DUMP=<file>` writes the per-test keys after a `perTestCoverage` request in `--server`; `AL_RUNNER_EXACT_TESTS=<file>` runs only the listed `Codeunit.Method` names; `AL_RUNNER_RECORD_ALL_APP_EVENTS=1` is the seeding prototype, which found nothing to add here. Folder `spike-3415/` has the scripts (`srv.py` for server runs, `runcli.sh` for CLI runs, `mkprobes.py`, `analyze.py`, `selcheck.py`), the probe apps, and the results (`results/`, per-test keys gzipped).
