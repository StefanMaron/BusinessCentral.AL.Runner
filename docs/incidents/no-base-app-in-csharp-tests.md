# Incidents behind .claude/rules/no-base-app-in-csharp-tests.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the measurements that produced them.

## What it costs

Two bundles identical except for that one line, same runner build, same machine, both
discovering and passing one test:

| | cold wall | warm wall | test-execution phase (warm) |
|---|---|---|---|
| with `"application"` | 94.9s | 9.6-13.4s | 2.7-2.9s |
| without | 25.2s | 4.3s | 0.1s |

About 70 seconds cold and 6 seconds warm, per runner invocation. 71 of the 246 files in `AlRunner.Tests`
spawn the runner as a subprocess and the suite spawns it roughly 130 times — the single
largest cost in the C# suite.

## Enforcement, and the allowlist

**There are no outstanding violations.** #2364 discharged the last three, and none of them
turned out to need the floor — each needed one specific thing the floor happened to supply:

- `InstallBaselineDiskCacheTests`, `InstallSeedDepCompanyCacheTests` test #1867
  install-baseline caching, and needed a dependency closure whose install triggers WRITE ROWS
  (without one the runner logs `not persisting: snapshot has 0 DataAccessSource(s)`, nothing is
  persisted, and the assertions pass vacuously). They now use `AlRunner.Tests/InstallSeedClosure.cs`
  — a dependency app with one table and one `OnInstallAppPerCompany` trigger that inserts two
  rows, read back by value in AL. Measured on that fixture with two bundles in both arms so only
  the floor differs (BC 28.1, `perf stat` instructions-retired): **249.6e9 → 63.7e9 cold
  (-74.5%)** and **47.9e9 → 14.9e9 warm (-68.9%)**; wall 33.9 s → 10.9 s and 6.9 s → 2.8 s.
- `MissingTestDataDiagnosisTests` resolved "Source Code Setup" (table 242) against real
  metadata, asserting a table id so the diagnosis could not pass by echoing a name back. It now
  declares three tables of its own and asserts **two different** empty tables are each explained
  with their own id — a stronger claim than one hardcoded id could make.

End to end, the three suites together (11 tests, same 11 before and after), three reps each,
`dotnet test --no-build` on one loaded box: **191.6 s → 55.3 s, -71%**. The control in the same
sweep — `PlaceholderFloorProvisioningTests`, untouched and still declaring the floor because the
floor is its subject — was 10.0 s → 10.2 s, flat. That pairing is what makes the delta a
measurement rather than a claim about a busy machine.

So the pattern for the next class that looks like it needs the floor: work out which single
property of Base Application it is actually leaning on, and supply that. In three out of three
cases it was cheaper to supply than to load.

The violation the class list missed was a checked-in fixture rather than a class-generated
manifest: `AlRunner.Tests/Fixtures/RecordTriggerXRec/app.json`, a bundle with
`"dependencies": []` and its own private Assert codeunit needing nothing from Microsoft — and
the most-spawned fixture in the suite at 28 spawns per BC leg (313-472 s of subprocess wall),
about a quarter of all subprocess time in the unit-test step.

## Do not conclude a failure set from a run that has not finished

The property was removed from all 49 classes and the full suite run. A *partial* local run
reported three classes; read before it finished, it produced a wrong list, and CI — running to
completion on all eight legs — found five failures in two further classes. Later, dropping the
floor from the `RecordTriggerXRec` fixture surfaced `EventSubscriberScanEquivalenceTests`
failing with `found 0` subscribers on multiple BC legs at once, again only on a completed
eight-leg run. The bar for adding to either allowlist is a completed run showing the class or
fixture fails without the floor, not a reading of what the test looks like it needs.
