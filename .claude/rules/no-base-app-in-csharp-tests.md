# C# test fixtures may declare `platform`, never `application`

A fixture `app.json` written by a test in `AlRunner.Tests` must **not** carry an
`"application"` property. `"platform"` is fine and stays.

There are no exceptions. If a test appears to need Base Application objects —
`Customer`, `Item`, `Company Information`, `No. Series` and the like — the answer is to
find another way to assert what it is asserting, not to add the floor back.

In Business Central, `"application"` is the Base Application dependency. It is not
declared through the `dependencies` array, which is why this is easy to add without
noticing what it pulls in: the whole Base Application closure, loaded on every runner
invocation.

## What it costs

Measured on two bundles identical except for that one line, same runner build, same
machine, both discovering and passing one test:

| | cold wall | warm wall | test-execution phase (warm) |
|---|---|---|---|
| with `"application"` | 94.9s | 9.6-13.4s | 2.7-2.9s |
| without | 25.2s | 4.3s | 0.1s |

About 70 seconds cold and 6 seconds warm, per runner invocation. 71 of the 246 files in
`AlRunner.Tests` spawn the runner as a subprocess, and the suite spawns it roughly 130
times, so this is the single largest cost in the C# suite.

## Four classes still carry the floor. Three are debt; one is legitimate.

**Legitimate, and it stays:** `PlaceholderFloorProvisioningTests`. The placeholder
`1.0.0.0` application floor IS its subject — remove it and nothing is being tested.

**Outstanding violations, tracked in #2364, not permission:**

- `InstallBaselineDiskCacheTests` and `InstallSeedDepCompanyCacheTests` — they test #1867
  install-baseline caching, and need a closure whose install triggers WRITE ROWS. Without
  one the runner logs `not persisting: snapshot has 0 DataAccessSource(s)` and the
  assertions have nothing to observe, so they would pass vacuously.
- `MissingTestDataDiagnosisTests` — resolves "Source Code Setup" (table 242) against real
  metadata, asserting BC's own table id so the diagnosis cannot pass by echoing a name back.

None is a precedent to cite. Do not add a fifth. When #2364 lands, this section shrinks to
the one legitimate case.

**How these four were identified, and how they were nearly missed.** The property was
removed from all 49 classes and the full suite run. A *partial* local run reported three
classes; reading it before it finished produced a wrong list, and CI — running to completion
on all eight legs — found five failures in two further classes. **Do not conclude a set of
failures from a run that has not finished.** The bar for adding to this list is a completed
run showing the class fails without the floor, not a reading of what the test looks like it
needs.

## Sister rules

- `.claude/rules/tdd.md` — a test that passes without proving anything is the failure
  mode this rule must not create while chasing speed
- `.claude/rules/local-test-scope.md` — run targeted tests locally; CI runs the sweep
