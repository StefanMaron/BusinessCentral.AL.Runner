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

**A fifth arrived anyway, because this list had no enforcement.** Not as a class -- as a
checked-in fixture, which the paragraph above never mentioned:
`AlRunner.Tests/Fixtures/RecordTriggerXRec/app.json`, a bundle with `"dependencies": []`
and its own private Assert codeunit that needs nothing from Microsoft. It was also the
most-spawned fixture in the suite (28 spawns per BC leg, 313-472 s of subprocess wall),
so the property nobody noticed was costing about a quarter of all subprocess time in the
unit-test step. `AlRunner.Tests/BaseAppFloorFixtureGuardTests.cs` now enforces both halves
-- generated manifests AND checked-in fixture manifests -- against a named allowlist, and
fails when an allowlist entry goes stale. Add to the allowlist only with the reason the
floor is genuinely the subject.

**A sixth turned out to be legitimate, and stays on the fixture allowlist: `Fixtures/
SubscriberScanAudit`.** Dropping the floor from `RecordTriggerXRec` broke
`EventSubscriberScanEquivalenceTests`, which drives the runner with
`AL_RUNNER_SUBSCRIBER_SCAN_AUDIT=1` and asserts over 3,000 real `[NavEventSubscriber]`
methods across Base Application + System Application -- a count with nothing to count
without the platform closure loaded. That test never declared its own need for the floor;
it rode along on a fixture another 13 test classes shared. Its whole claim is a count of
subscribers in real BC assemblies, so without the floor it asserts against nothing --
the same shape as `PlaceholderFloorProvisioningTests` above, not a #2364-style violation.
The fix is not to restore the floor to `RecordTriggerXRec` (that un-does the entire point
of this PR for its other 13 users) but to give the one test that needs it a fixture of its
own, so the floor is paid once per CI leg instead of 28 times.

**How these six were identified, and how they were nearly missed twice.** The property was
removed from all 49 classes and the full suite run. A *partial* local run reported three
classes; reading it before it finished produced a wrong list, and CI — running to completion
on all eight legs — found five failures in two further classes. Later, removing the floor
from the checked-in `RecordTriggerXRec` fixture (rather than a class-generated manifest)
found a sixth failure the same way: only a completed eight-leg CI run surfaced
`EventSubscriberScanEquivalenceTests` failing with `found 0` subscribers, on multiple BC
legs at once. **Do not conclude a set of failures from a run that has not finished.** The
bar for adding to either allowlist is a completed run showing the class or fixture fails
without the floor, not a reading of what the test looks like it needs.

## Sister rules

- `.claude/rules/tdd.md` — a test that passes without proving anything is the failure
  mode this rule must not create while chasing speed
- `.claude/rules/local-test-scope.md` — run targeted tests locally; CI runs the sweep
