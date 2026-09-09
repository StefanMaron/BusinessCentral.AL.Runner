# C# test fixtures may declare `platform`, never `application`

A fixture `app.json` written by a test in `AlRunner.Tests` must **not** carry an `"application"`
property. `"platform"` is fine and stays. If a test appears to need Base Application objects —
`Customer`, `Item`, `Company Information`, `No. Series` and the like — find another way to
assert what it is asserting.

`"application"` is the Base Application dependency, and it is not declared through the
`dependencies` array, which is why it gets added without anyone noticing what it pulls in: the
whole Base Application closure, loaded on every runner invocation. That costs about 70 seconds
cold and 6 seconds warm per invocation, and the suite spawns the runner roughly 130 times —
the single largest cost in the C# suite (#2364).

## Enforcement, and the allowlist

`AlRunner.Tests/BaseAppFloorFixtureGuardTests.cs` enforces both halves — generated manifests
AND checked-in fixture manifests — against a named allowlist, and fails when an allowlist entry
goes stale. **Add to the allowlist only with the reason the floor is genuinely the subject.**

Legitimate, and they stay:

- `PlaceholderFloorProvisioningTests` — the placeholder `1.0.0.0` application floor IS its
  subject; remove it and nothing is being tested.
- `Fixtures/SubscriberScanAudit` — `EventSubscriberScanEquivalenceTests` asserts over 3,000 real
  `[NavEventSubscriber]` methods across Base Application + System Application, a count with
  nothing to count without the platform closure loaded. It has its own fixture so the floor is
  paid once per CI leg rather than 28 times.

**There are no outstanding violations**, and #2364's three discharges each needed one specific
property the floor happened to supply, not the floor:

- an install closure whose triggers WRITE ROWS, now `AlRunner.Tests/InstallSeedClosure.cs` —
  without one the runner logs `not persisting: snapshot has 0 DataAccessSource(s)` and the
  assertions pass vacuously;
- real metadata for one table id, replaced by three tables the test declares itself, which let
  it assert two different empty tables are each explained with their own id.

**So the pattern for the next class that looks like it needs the floor:** work out which single
property of Base Application it is leaning on and supply that. In three of three cases it was
cheaper to supply than to load. A checked-in fixture counts too — the violation the class list
missed was `AlRunner.Tests/Fixtures/RecordTriggerXRec/app.json`, the most-spawned fixture in
the suite.

## Do not conclude a failure set from a run that has not finished

**The bar for adding to either allowlist is a completed run showing the class or fixture fails
without the floor**, never a reading of what the test looks like it needs: a partial local run
named three classes and CI, running to completion on eight legs, found five failures in two
further classes (#2364).

## Sister rules

- `.claude/rules/tdd.md` — a test that passes without proving anything is the failure
  mode this rule must not create while chasing speed
- `.claude/rules/local-test-scope.md` — run targeted tests locally; CI runs the sweep

History: docs/incidents/no-base-app-in-csharp-tests.md
