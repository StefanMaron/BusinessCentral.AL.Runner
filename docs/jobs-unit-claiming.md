# `--jobs`: sharing one bundle between workers (#5130)

`--jobs N` used to hand whole bundles to workers, so the largest bundle set the wall-clock
floor however many workers ran. A bundle that is heavy enough is now **shared**: several workers
load it and each runs only the test codeunits it claims.

## What decides it

- **Weight** is the bundle's AL file count (`ParallelFanOut.WeighBundle`), known before anything
  compiles. A bundle gets `round(weight / (total / jobs))` workers, at most `N`, and never so many
  that a piece is lighter than `AL_RUNNER_JOBS_SPLIT_MIN_FILES` files (default 100; 20 for slow
  tests, measured: [§ Floor](#floor)). Every extra worker pays startup, bundle load and test-data company load
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

A test codeunit the compile drops (EMIT-EXCLUDED, #3476) is never run, so it is never offered as a
unit, yet every worker compiles the bundle and finds the same drop. Each dropped object is claimed
through the same directory (`UnitClaimQueue.ClaimDropped`) and only the worker that wins reports its
`[Test]` procedures as SKIPPED, so the aggregate and the merged JUnit count them once, as a single
process does (#5256). Every worker still prints the EMIT-EXCLUDED line, and a worker left with no
test of its own and nothing but that drop to report is a partial bundle, not a COMPILE FAIL one
(`BundleFailureStage.OnlyDropsAPeerReports`). The `--tdd` branch reports an excluded object's
synthetic FAILED tests the same way from every worker and is not covered (#5262).

A worker that resumes after a watchdog abort (#2280) compiles the bundle again and finds the same drop. It
does not report SKIPPED rows its carried attempts already hold (`ResumeCarry.NotYetReported`, #5268), so
the summary, the JUnit and `--output-json` count each once, and the aggregate counts a bundle once however
many attempts its worker made, because each attempt prints its own `SUITE ERRORS` header
(`ParallelFanOut.CountBundleHeaders`, #5269). Its `--tdd` FAILED rows and its structured outputs
are held to the same rule (#5272, #5273): `docs/watchdog-resume-reporting.md`.

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
| Tests-VAT serial | 1,200 | 2,230 MB | 2,290 MB |
| Tests-Job serial | 1,290 | 2,608 MB | 2,669 MB |
| Tests-Workflow serial | 1,058 | 2,579 MB | 2,666 MB |

- **The base does not depend on the bundle's size.** 45 AL files (SMB) to 293 (ERM) all start at
  1.3 to 1.55 GB.
- **Tests do grow a worker, less than in proportion.** ERM went from 1.45 GB at 11 tests to 4.9 GB
  at 9,497; SMB from 1.3 GB to 2.3 GB at 1,027. (The figures in `ShardPlanner.cs` and `--help`
  that say peak memory tracks bundles loaded, not tests run, are from one configuration; with
  `--test-data` tests run do cost memory.)
- **The model** (`JobsMemory.Model`) is `1,370 MB + 6.9 MB x tests^0.68` per worker, a
  least-squares fit on the relative error of every row above. It lands between -16% (Tests-Workflow)
  and +18% (Tests-SMB `--jobs 3`) of them, with a root-mean-square error of 9%. The spread is
  bucket to bucket, not noise in the fit: buckets of 1,000 to 1,300 tests grew a worker by 0.85 to
  1.25 GB. A first fit on the six ERM and SMB runs predicted Tests-VAT 5% under, Tests-Job 17% under
  and Tests-Workflow 21% under before those three were run (the predictions were computed first), so
  the fit was redone on all of them and no run is held out any more. `JobsMemoryModelTests` holds the
  rows, a 20% ceiling on over-estimates, and that a plan sized exactly to its budget would use at
  most 97% of the free memory on every one of them. A plan may claim 80% of the free memory: that
  covers the fitted runs (the worst, Tests-Workflow at -16%, would use 96%), and it is an
  extrapolation beyond them, not a held-out margin. The one run the first fit had not seen and
  missed by 21% would have used about 101%.
- **The reading** of free memory is `MemAvailable` (and the tightest cgroup v2 limit on the way
  up). Where it cannot be read the plan is not sized at all; `AL_RUNNER_JOBS_FREE_MEMORY_MB`
  overrides it, and a value that is set but is not a positive whole number of MB (`8GB`, `0`,
  `-1`) is named in a `jobs:` line and ignored. Linux only; elsewhere it is unknown, never zero.
- **Only sharing is limited.** How many workers an unshared run uses is still the caller's
  `--jobs`. When memory cuts the sharing of a bundle the plan prints why:
  `jobs: free memory holds fewer workers per bundle than --jobs asked for: ...`.

Not in the model, and each one measured to be real:

- **A cold cache.** The first compile of Tests-ERM peaked at 3.4 GB in one process, against 1.5 GB
  warm (SMB 2.2, VAT 1.8, SCM 3.6). That peak is now paid by ONE worker of a shared bundle, not by
  each ([§ Cold cache](#cold-cache)), but it is still not in the model: a plan sized to the warm
  numbers does not fit the worker that compiles.
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

The default stays **100 files** a piece, and 20 is the setting for slow-per-test bundles
(`AL_RUNNER_JOBS_SPLIT_MIN_FILES=20`, e.g. `--test-data` BaseApp buckets). Both ends were measured,
on the same box.

**Slow tests: a lower floor pays.** Tests-SMB (45 AL files, 1,027 tests, about 0.5 s a test),
`--test-data`, warm cache, `AL_RUNNER_JOBS_SPLIT_MIN_FILES=1`:

| workers | files per piece | wall | load1 mean | CPU |
|---|---|---|---|---|
| 1 (serial, two runs) | 45 | 605 s, 488 s | 7.7, 4.2 | 629 s, 537 s |
| 2 | 22 | 305 s | 5.6 | 671 s |
| 3 | 15 | 278 s | 7.6 | 930 s |

Two workers ran it 1.6x to 2.0x faster than one (the two serial runs span the box's load); a third
bought about 10% more for another 1 GB. A worker's own fixed cost is its warm start: a warm run of
one small codeunit took 17 to 30 s wall on the four buckets above.

**Fast tests: a lower floor loses.** A synthetic bundle of 45 files and 90 trivial tests (two empty
`[Test]`s a file), no `--test-data`, `--jobs 2`, `tools/process-tree-peak.py --interval 0.1`, load1
about 2.5, floor 20 against floor 100 (the second does not share the bundle):

| | floor 20 | floor 100 |
|---|---|---|
| warm, 3 runs each | 2.9 s wall, 5.1 to 5.2 s CPU, 461 to 468 MB | 2.3 s wall, 2.5 s CPU, 238 to 242 MB |
| cold, 1 run each | 15.8 s wall, 45.8 s CPU, 1,048 MB | 13.2 s wall, 27.4 s CPU, 559 MB |

So sharing a bundle of fast tests costs about a fifth more wall time warm, twice the CPU and twice
the memory. (The cold column was taken before the workers stopped compiling a bundle each, #5238:
[§ Cold cache](#cold-cache).) Lowering the default would have charged that to every `--jobs` user
for the benefit of the slow-test ones.

No files-per-piece number is right for both. File count is a poor proxy for time: seconds per test
differ two to three times between the two buckets here (0.22 s in ERM, 0.46 to 0.57 s in SMB) and
tests per file run from under 1 to 41 across the BaseApp buckets. Weighing by test count or by a
recorded duration, and measuring the weight itself, is #5239 (`CountTests` exists now).

## Cold cache

Against an empty `--cache` the workers of a shared bundle used to compile everything it needs, each
of them: the bundle's own AL-output entry, and every dependency compiled from source (the
`compiled-deps` entries, which on Tests-SMB cost far more than the bundle: `Any`, `System
Application Test Library` and `Tests-TestLibraries` each took between 13 s and 66 s in the runs here, the
bundle's own emit and C# compile about 6 s). CPU and memory were paid
once per worker while wall time stayed put, so a run that only watched the clock did not see it
(#5238). Nothing locked the "entry missing, compile, publish" step: `AlCacheWriter` makes concurrent
writers correct (identical bytes, atomic rename), not cheap.

Two locks, in this order (`CompilePhase`, then `CacheCompileLock`):

- **The compile phase**, per shared bundle, in the run's claim directory. The first worker that
  misses takes it and keeps it until its own compile phase for the bundle is over (every app loaded,
  before any test runs). A worker that misses meanwhile waits for it and then finds everything
  cached. This is what puts all the compiling in ONE process: per-key locks alone only stop two
  workers compiling the same key, and the workers then split the keys between them, so both carry
  the compiler's footprint (measured on Tests-SMB `--jobs 2`, one unpaired run on a busier box:
  CPU 390 s to 295 s, peak 4.3 to 4.1 GB; with the phase, below). A worker that waited and then
  hits gives the phase back at once, so a third worker waits for a compile, never for a load, and a
  holder that has compiled keeps it through any later hit.
- **A lock per cache key**, beside the entry (`<key>.compile.lock`), for every process that
  shares a cache directory, workers or not. It is what makes one of two unrelated runs compile a
  shared dependency while the other waits, without holding up a run that needs a different key.

Both are OS file locks: a killed holder frees them with its process, so there is no stale lock,
only a live holder that is slow. The wait is bounded (`AL_RUNNER_CACHE_LOCK_WAIT_SEC`, default
1800, longer than the 301 s of wall #5238 recorded for the largest cold compile, Tests-ERM); on expiry the waiter says so
and compiles in its own process, which is the behaviour before the locks: slower, never wrong. A
lock file that cannot be taken for any other reason is named and compiled around, not read as "free",
with one case that is not: on Unix .NET reports a conflict only for `EWOULDBLOCK`, so a filesystem that
refuses `flock`, or `DOTNET_SYSTEM_IO_DISABLEFILELOCKING`, would give every process the lock without a
message, which is the behaviour before the locks (correct, not cheaper). That is a reading of .NET's
`SafeFileHandle.Unix.cs` by the reviewer of #5248, not something measured here.
A wait of more than 5 s prints one line saying what it is waiting for. The lock files are never
deleted (unlinking one another process already opened lets a third lock a different file).

Measured on `Tests-SMB`, BC 28.1.49838.53910, `--test-data`, one small codeunit selected
(`--test Codeunit138041`, 4 tests), `AL_RUNNER_JOBS_SPLIT_MIN_FILES=1`, a fresh `--cache` per run, one
box at a load of about 3, `tools/process-tree-peak.py`. Base is `origin/main` at 3993c32b.

| run | wall | CPU | peak process-tree PSS |
|---|---|---|---|
| one process, base | 67 s | 177 s | 3,769 MB |
| one process, with the locks | 68 s | 180 s | 3,695 MB |
| `--jobs 2`, base (2 runs) | 114 s, 119 s | 341 s, 356 s | 4,335 MB, 4,484 MB |
| `--jobs 2`, with the locks (2 runs) | 92 s, 92 s | 162 s, 166 s | 3,385 MB, 3,188 MB |

CPU of the two-worker run is now that of one process's cold compile, where it was twice that. The
one-process rows run without the worker GC settings a `--jobs` worker gets (one heap, conserve
memory 9), so their peak is not comparable with the `--jobs` rows. A synthetic bundle (45 files, 90
trivial tests, no `--test-data`), three runs each, alternating base and new: `--jobs 2` cold took
17.4 to 18.3 s, 52 to 58 s of CPU and 1,044 to 1,064 MB before, and 15.0 to 15.5 s, 31 to 33 s and
776 to 786 MB after. The warm run of that bundle against the cache the cold one wrote: 2.8 s, no
compile, both workers read the entry, 90 of 90 passed in both.

**A key that publishes nothing is not waited for.** An EMIT-EXCLUDED bundle (a dropped object keeps
the module out of the cache), a failed compile and a dependency whose source compile throws leave no
entry, so waiting for the holder would only queue the workers to compile the same thing one after
another, on every run. Such a compile (`UncacheableCompile`) gives both locks up when it ends and
leaves `<key>.uncacheable` beside where the entry would be; a process that sees the marker compiles
without waiting, and a later compile that does publish the key removes it. It is a hint about cost
only: it skips the locks and nothing else, and a stale one costs the locks and nothing else. The
FIRST run is still serial for the compile that finds out, because the workers are already waiting
when it does; the ones waiting on a lock (the key lock, the phase, or the key lock while sharing a phase) see
the marker the moment they get it, hand on whatever they held and compile side by side from there,
rather than one after another. A
45-file synthetic bundle with one uncompilable test codeunit, `--jobs 2`, three runs each at a load
of 4 to 5 (base / the first push of #5248 / with the marker): cold 18.1 to 21.0 s / 22.8 to 24.2 s /
23.9 to 25.2 s; warm 11.2 to 12.0 s / 16.2 to 17.7 s / 10.0 to 11.4 s. Cold, CPU and peak memory stay
below base; warm they match it.

Not covered, and found still duplicated: the install baseline (`[InstallBaselineDisk] wrote`) is
written by each worker on a cold cache (#5252, cost not measured). A holder that drops
`NoteCompiling` in `LoadOne` is not caught by any test either: it would hand the phase on after a
hit on a key an unrelated process compiled, costing time and never correctness.

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

- The wall time of the whole surface (all 33 buckets) under `--jobs` was not measured: #5240.
- The weight is a file count, so the number of workers per bundle is only as good as that proxy
  (#5239).
- Memory is not modelled for a cold cache, for abort-resume chains or for workers holding several
  bundles (§ Memory), and the reading exists on Linux only.
- The caller's `--output-junit` is merged from the shard files; `--out` is still written by every
  worker to the one path (#5129).
