# dep-tableext-platform-base — runs in BOTH bundle shapes

This pair (`-dep` + `-main`) pins #1686: a dependency app declaring a `tableextension` on a
**platform-app** table (`Item`, 27), whose own base metadata comes from the precompiled Base
Application `.app` and from no AL source in either bundle.

It stays in `tests/runner-extras/` **and** is run a second time by its own step in
`.github/workflows/bc-tests.yml`, as two ordered bundles. That is deliberate, and the two runs
are not redundant: #1686 has two halves, and each bundle shape is blind to one of them.

## Measured (#4085, BC 28.1, one mutation per half, fresh cache root per run)

| mutation | two ordered bundles | one combined bundle |
|---|---|---|
| none | exit 0, 4/4 PASS | exit 0, 474/474 PASS |
| **cross-bundle half** — `RecordPatches._sourceDirs` de-dup off, plus the field-id de-dup in `MergeExtensionFields` off for table `item` | exit 2, **0 tests**, `table 27 metadata could not be built: NullReferenceException` | exit 0, **474/474, all four PASS** |
| **in-bundle half** — `EmitSiblingSymbols` dropping `bundleResolvedDeps` from the sibling's deps sidecar | exit 0, **4/4 PASS** | exit 3, **`AL0132: 'Record Item' does not contain a definition for 'DTB Repro Flag'`**, the suite's 4 tests dropped |

Neither shape subsumes the other, so removing either run drops real coverage.

**Why the shapes differ.** Passed as two ordered arguments, the dep is its own earlier bundle and
loads as a dependency *package* (`[dep] AL Runner/DTB Platform Base Dep`), reaching
`BuildSiblingSourceDeps` and the `_sourceDirs` de-dup. Passed inside the combined root, the dep is
a sibling *app group* in one bundle — there is no `[dep]` line at all — and the path that matters
is `EmitSiblingSymbols` writing the bundle-wide Microsoft-platform closure into the sibling's
`.symbols.deps.json`, without which a tableextension on a platform table records an empty
dependency closure and never attaches downstream.

## Do not "finish the job" by moving this pair out

#4079/#4080 moved the two `tableext-eviction` pairs to their own root, and doing the same here
reads as tidying up. It would silently remove the in-bundle half above — nothing else covers it,
and no test would fail. `AlRunner.Tests/DepTableExtPlatformBaseBothShapesTests.cs` holds the
both-places property so that edit fails locally in about a second.

## The ordered-bundle step's own guards

Tests passing is not enough for either half, so the step also requires:

- the `[dep] AL Runner/DTB Platform Base Dep` line, proving the dep really loaded cross-bundle
  rather than collapsing back into one bundle (the #4079 defect);
- an anchored `^PASS +<name>( |$)` line per test name. The `[dep]` marker prints at **parse**
  time, so it appears even when zero tests run — #4080 measured a copy with every `[Test]`
  stripped printing its markers, running `0P/0F/0E` and exiting 0. The step passes no
  `--count-baseline`, so these PASS lines are the only thing asserting the tests ran.

The combined run's own count is covered the ordinary way, by
`tests/expectations/count-baseline/test-count-baseline.json` (`-dep`: 0 tests, `-main`: 4). Those
entries are unchanged by #4085, because nothing moved.
