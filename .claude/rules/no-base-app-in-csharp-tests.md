# C# test fixtures may declare `platform`, never `application`

A fixture `app.json` written by a test in `AlRunner.Tests` must **not** carry an `"application"`
property. `"platform"` is fine and stays. If a test appears to need Base Application objects —
`Customer`, `Item`, `Company Information`, `No. Series` and the like — find another way to
assert what it is asserting.

`"application"` is the Base Application dependency, and it is not declared through the
`dependencies` array, which is why it gets added without anyone noticing what it pulls in: the
whole Base Application closure, loaded on every runner invocation. That cost is paid per
invocation, cold or warm, and the suite spawns the runner many times — the single largest cost
in the C# suite (#2364).

## Enforcement, and the allowlist

`AlRunner.Tests/BaseAppFloorFixtureGuardTests.cs` enforces both halves — generated manifests
AND checked-in fixture manifests — against a named allowlist, and fails when an allowlist entry
goes stale. **Add to the allowlist only with the reason the floor is genuinely the subject.**

Legitimate, and they stay:

- `PlaceholderFloorProvisioningTests` — the placeholder `1.0.0.0` application floor IS its
  subject; remove it and nothing is being tested.
- `Fixtures/SubscriberScanAudit` — `EventSubscriberScanEquivalenceTests` asserts over the real
  Base + System Application event subscribers, which do not exist without the platform closure;
  its own fixture pays the floor once per CI leg.

**A class that looks like it needs the floor needs one property of it**, not the floor: work
out which property and supply it (#2364). Two that have come up, with what replaced the floor —

- an install closure whose triggers WRITE ROWS, now `AlRunner.Tests/InstallSeedClosure.cs`;
  without one the runner logs `not persisting: snapshot has 0 DataAccessSource(s)` and the
  assertions pass vacuously;
- real metadata for one table id, replaced by tables the test declares itself.

**A checked-in fixture manifest counts too** — `AlRunner.Tests/Fixtures/RecordTriggerXRec/app.json`
is the violation a class-only list missed (#2364).

## Do not conclude a failure set from a run that has not finished

**The bar for adding to either allowlist is a completed run showing the class or fixture fails
without the floor**, never a reading of what the test looks like it needs — a partial local run
missed classes CI found (#2364).

## Sister rules

- `.claude/rules/tdd.md` — a test that passes without proving anything is the failure
  mode this rule must not create while chasing speed
- `.claude/rules/local-test-scope.md` — run targeted tests locally; CI runs the sweep

History: docs/incidents/no-base-app-in-csharp-tests.md
