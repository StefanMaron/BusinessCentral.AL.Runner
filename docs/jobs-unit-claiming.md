# `--jobs`: sharing one bundle between workers (#5130)

`--jobs N` used to hand whole bundles to workers, so the largest bundle set the wall-clock
floor however many workers ran. A bundle that is heavy enough is now **shared**: several workers
load it and each runs only the test codeunits it claims.

## What decides it

- **Weight** is the bundle's AL file count (`ParallelFanOut.WeighBundle`), known before anything
  compiles. A bundle gets `round(weight / (total / jobs))` workers, at most `N`, and never so many
  that a piece is lighter than `AL_RUNNER_JOBS_SPLIT_MIN_FILES` files (default 20, measured:
  [§ Floor](#floor)). Every extra worker pays startup, bundle load and test-data company load
  again.
- **Free memory** limits how many workers share one bundle ([§ Memory](#memory)).
- **Isolation** is what makes it possible. Under `Codeunit` (the default) or `Test` isolation the
  database is rolled back between codeunits, and a test codeunit is the smallest piece that can
  run anywhere (its tests share state through the one instance). SingleInstance state is NOT
  reset at a codeunit boundary (#4781), so sharing a bundle changes which codeunits a codeunit
  follows, and with it what it inherits: a serial/split difference can be real order dependence
  as well as a runner defect. With `--isolation disabled` state carries across every test, so
  the bundle stays in order on one worker.
- **Not shared** with `--count-baseline` (each worker would compare its fraction of the bundle
  against the whole bundle's count) or `--expectations-require-match` (each worker discovers only
  the codeunits it claimed, so every other entry would read as unmatched). The plan line says so.
- **One bundle** is fanned out only when it is shared, and not when the run also passes `--out`,
  `--output-json`, `--count-out` or `--coverage`: a fan-out writes each worker's own `--out` /
  `--output-json` and refuses the other two, and a single bundle never used to be affected.

## How the claim works

The parent makes one directory under its scratch dir and gives every worker
`AL_RUNNER_UNIT_CLAIM_DIR` plus `AL_RUNNER_UNIT_CLAIM_BUNDLES` (the shared bundles). A worker runs
a shared bundle's test codeunits in order of test-method count, largest first, and before each one
creates `<hash>.claim` with `FileMode.CreateNew`; the create that succeeds owns the codeunit.
The hash covers bundle path, app group and codeunit, so two bundles that both declare
`Codeunit50000` do not shadow each other. No round trip to the parent and no worker protocol.

A claim file that cannot be created for any reason other than "it exists" throws rather than
reading as claimed (that would drop the codeunit) or as free (that would run it twice).
The claim comes after `--test`/`--filter` selection, so a deselected codeunit is never claimed and
the workers' selected counts sum to the run's.

A watchdog abort in a shared bundle ends that worker's run of it. The abort line counts the later
codeunits nobody has claimed yet, and only those: one another worker already claimed is not lost.
Whether the worker resumes depends on that count (`AbortResumePlan.AbandonedLaterCodeunits`), so a
hang that finds every later codeunit claimed does not spend a BC boot on a resume. A resumed
attempt inherits the claim directory, so it keeps claiming and never reruns what another worker
took. `--per-suite` claims through the same queue. Tests: `JobsSharedBundleAbortTests` and
`JobsSharedBundleEndToEndTests`.

## Memory

A worker costs a **base** before it runs anything, and running tests then grows it. Sharing a
bundle between N workers does not multiply the bundle's tests, it shares them out, so what the
extra workers cost is mostly the base again.

Measured with `tools/process-tree-peak.py` (peak proportional set size of the whole process tree,
the backup-reader sidecar included), `--test-data`, BC 28.1.49838.53910, the worker environment a
`--jobs` worker gets (one GC heap, conserve memory 9), on a 12th Gen i5-1235U (2 performance and
8 efficiency cores, 12 threads) with 15 GB, shared with other jobs:

| run | tests per worker | peak PSS | peak RSS |
|---|---|---|---|
| Tests-ERM, one codeunit of 11 tests, warm | 11 | 1,453 MB | 1,524 MB |
| Tests-SMB, one codeunit of 4 tests, warm | 4 | 1,322 MB | 1,417 MB |
| Tests-VAT, one codeunit of 4 tests, warm | 4 | 1,385 MB | 1,481 MB |
| Tests-SCM, one codeunit of 6 tests, warm | 6 | 1,549 MB | 1,645 MB |
| Tests-SMB serial | 1,027 | 2,295 MB | 2,407 MB |
| Tests-SMB `--jobs 2` | 436, 591 | 3,441 MB | 3,868 MB |
| Tests-SMB `--jobs 3` | 334, 241, 452 | 4,387 MB | 5,104 MB |
| Tests-ERM serial (random seed; seed 7: 4,975 MB PSS) | 9,497 | 4,875 MB | 4,935 MB |
| Tests-ERM `--jobs 2` | 4,288, 5,209 | 6,784 MB | 7,173 MB |
| Tests-ERM `--jobs 3` | 2,762, 3,759, 2,976 | 8,188 MB | 8,943 MB |
| Tests-VAT serial, held out of the fit | 1,200 | 2,230 MB | 2,290 MB |

- **The base does not depend on the bundle's size.** 45 AL files (SMB) to 293 (ERM) all start at
  1.3 to 1.55 GB.
- **Tests do grow a worker, less than in proportion.** ERM went from 1.45 GB at 11 tests to 4.9 GB
  at 9,497; SMB from 1.3 GB to 2.3 GB at 1,027. (The figures in `ShardPlanner.cs` and `--help`
  that say peak memory tracks bundles loaded, not tests run, are from one configuration; with
  `--test-data` tests run do cost memory.)
- **The model** (`JobsMemory.Model`) is `1,390 MB + 3.31 MB x tests^0.76` per worker, a
  least-squares fit on the relative error of every row above but the held-out one. It lands within
  -11% to +14% of each of them, and 5% under the held-out Tests-VAT run (the prediction, 2,115
  MB, was computed before the run). `JobsMemoryModelTests` holds the rows and the 15% band.
  A plan may claim 80% of the free memory, which covers the under-estimates.
- **The reading** of free memory is `MemAvailable` (and the tightest cgroup v2 limit on the way
  up). Where it cannot be read the plan is not sized at all; `AL_RUNNER_JOBS_FREE_MEMORY_MB`
  overrides it. Linux only; elsewhere it is unknown, never zero.
- **Only sharing is limited.** How many workers an unshared run uses is still the caller's
  `--jobs`. When memory cuts the sharing of a bundle the plan prints why:
  `jobs: free memory holds fewer workers per bundle than --jobs asked for: ...`.

Not in the model, and each one measured to be real:

- **A cold cache.** The first compile of Tests-ERM peaked at 3.4 GB in one process, against 1.5 GB
  warm (SMB 2.2, VAT 1.8, SCM 3.6). Workers started against an empty cache are not sized for it.
- **Abort-resume stacks attempts.** The aborted attempt stays alive while the retry runs, so a run
  that resumed twice held three attempts at once: Tests-SCM-Service (two watchdog aborts) peaked at
  10.8 GB (PSS), and one process of it reached 6.5 GB on its own. That run is why it is not in the
  fit (#5236).
- **More than one bundle in a worker.** The base was measured on single-bundle workers.

To repeat a row:

```console
tools/process-tree-peak.py --label erm-jobs3 --out results.jsonl -- \
  al-runner <Tests-ERM dir> --jobs 3 --test-data <bak> --test-data-company "CRONUS International Ltd_" \
  --package-cache <platform-apps> --package-cache <test-apps> --cache <dir> --seed 7 --quiet
```

## Floor

The 100-file default was a judgement. Measured on Tests-SMB (45 AL files, 1,027 tests), the same
box, a warm cache, `AL_RUNNER_JOBS_SPLIT_MIN_FILES=1`:

| workers | files per piece | wall | load1 mean | CPU |
|---|---|---|---|---|
| 1 (serial, two runs) | 45 | 605 s, 488 s | 7.7, 4.2 | 629 s, 537 s |
| 2 | 22 | 305 s | 5.6 | 671 s |
| 3 | 15 | 278 s | 7.6 | 930 s |

Two workers ran it 1.6x to 2.0x faster than one (the two serial runs span the box's load); a third
bought about 10% more for another 1 GB. Each extra worker's own fixed cost is its warm start:
a warm run of one small codeunit took 17 to 30 s wall on the four buckets above. So the floor is **20 files**: a 45-file bundle is shared
by two and not by three, and one under 40 files is not shared.

Not measured: pieces under 15 files, and the file count as a proxy for time. Seconds per test
differ by two to three times between the two buckets here (0.22 s in ERM, 0.46 to 0.57 s in SMB)
and tests per file from under 1 to 41 across the BaseApp buckets, so a floor in tests would be a better one. `CountTests`
exists now (the memory model needs it); weighing by it is the next step.

## Speed and correctness on a large bucket

Tests-ERM (293 AL files, 9,497 tests), same box and configuration, `--test-data`. The box was
shared with other jobs, which is what the load column says, and it is why the speed-up is lower than
the worker count; CPU seconds are the figure that does not move with other jobs' load much.

| workers | wall | speed-up | CPU | load1 mean |
|---|---|---|---|---|
| 1 | 2,288 s | 1.00 | 2,338 s | 6.3 |
| 2 (`--jobs 2`, default floor) | 1,294 s | 1.77 | 2,613 s | 4.7 |
| 3 (`--jobs 3`, floor 90) | 1,089 s | 2.10 | 3,136 s | 6.4 |

The workers finish within a second of each other (`--jobs 3`: 2,762, 3,759 and 2,976 tests, each
1,075 s), which is first come, first served doing its job.

**Every test ran exactly once** (9,497 distinct cases, none missing, none twice) in both split
runs.

**Verdicts.** With the run seed fixed (`--seed 7`) the split run and the serial run agree on all
9,497 ERM tests and on all 1,027 SMB tests (serial twice, `--jobs 2` and `--jobs 3`): 0 differences.
With a random seed they differ, and that is the seed, not the split: serial against serial (random
seed against seed 7) differs on the same 101 tests that serial against `--jobs 3` does. The seed
reaches every test through its own identity (`docs/run-seed.md`), so it does not depend on which
worker ran it. So no runner isolation defect showed on either bucket, and none of the order
dependence #4781 warns about: the claim order differs from serial order on every split run.

## What the unit is

A test codeunit. Per-method units under `Test` isolation are not implemented, on two measurements:

- **Granularity is not what limits the BaseApp surface.** Per-codeunit duration in the serial ERM
  run: the largest codeunit is 8.4% of the bucket's test time (Codeunit134265, 54 tests, 173 s) and
  the largest single test 12 s. Dealing the codeunits out first come, first served in the order of
  the run's own durations finishes within 1% of an ideal split up to 12 workers; a 16th worker
  has nothing it can take (173 s against an ideal 129 s). That is a model over the serial run's
  per-codeunit times, not a parallel run.
- **The unit would not be a method.** Every isolation mode runs all of a codeunit's tests on one
  instance (`TestExecutor`, corpus 60898 and #4826), so AL globals persist from one test to the
  next and only the database resets. A method that moved to another worker would start on an
  instance its predecessors never touched, which is the order dependence above at a finer grain.
  That is a decision for the owner, not a default: #5234.

## Claim order

Largest first by test count. The same model shows what that costs against dealing by duration:

| workers | ideal | by test count (shipped) | by recorded duration |
|---|---|---|---|
| 2 | 1,033 s | 1,034 s | 1,033 s |
| 4 | 517 s | 518 s | 517 s |
| 8 | 258 s | 268 s | 258 s |
| 12 | 172 s | 233 s | 173 s |
| 16 | 129 s | 213 s | 173 s |

Up to 6 workers the order is within 0.3% of the best; at 12 it leaves 35% on the table, because the
slowest codeunit (54 tests) is claimed late. A recorded per-codeunit duration is not kept anywhere
(#5235).

## What it does not do yet

- The weight is a file count, so the number of workers per bundle is only as good as that proxy.
- Memory is not modelled for a cold cache, for abort-resume chains or for workers holding several
  bundles (§ Memory), and the reading exists on Linux only.
- The caller's `--output-junit` is merged from the shard files; `--out` is still written by every
  worker to the one path (#5129).
