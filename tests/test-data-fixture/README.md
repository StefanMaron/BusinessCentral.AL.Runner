# `--test-data` hydration fixture

End-to-end proof that rows decoded out of a BC `.bak` land in the in-memory store and read
back through ordinary AL `Record` calls with the right values.

- `TestDataHydration.Codeunit.al` (#2258) — a table with no extension data, "No. Series".
- `TestDataExtensionFields.Codeunit.al` (#2261) — a table whose fields come from a
  **tableextension**, "Source Code Setup". Every assertion there is on an extension field's
  VALUE, because a test asserting "the record was found" would pass with the merge not
  happening at all. On reader builds up to 9701b04 that was reachable: the CLI accepted
  `--mergeExtensions` (camelCase), ignored it, and exited 0. Reader a431ee4 refuses an
  option the command does not accept (BakReader#18), so that spelling now fails — but the
  runner pins no reader version, so the value assertions and `AssertMergeIsHonoured()` both
  stay.
- `TestDataDateValues.Codeunit.al` (#2259) — Date, DateTime, Time and DateFormula. The
  load-bearing assertion is that a BLANK date reads back as AL's `0D` and not as the SQL
  sentinel 1753-01-01 BC stores it under; a test asserting only "the table hydrated" would
  pass with that bug present.
- `TestDataLazyLoad.Codeunit.al` + `TestDataLazyLoadBoundary.Codeunit.al` (#2262) — a table
  read on FIRST TOUCH, mid-test, and still whole after a codeunit-boundary restore. The two
  codeunits share one body (`TdfLazyLoadSteps.Codeunit.al`) and are deliberately symmetric:
  each asserts the pristine state and then dirties it, so whichever the runner happens to run
  second is asserting after a restore of the other's real damage. That matters because the
  runner does **not** run codeunits in object-id order — measured, this bundle runs 64400,
  64402, 64405, 64403, 64404 — so the obvious dirty-then-check shape would have proven
  nothing.

  It also covers the case a load-on-read design gets silently wrong: an `Insert` of a primary
  key the backup already holds must raise a duplicate-key error. Hooking
  `GetDataAccessForTableCore` rather than the read path is what makes reads and writes equally
  covered.

## Checking the lazy policy itself, not just the values

`AL_RUNNER_PERF=1` prints one `TestData.LazyLoad <id> '<name>' <n> row(s)` line per load and
one `InstallBaseline.Restore <n> row(s)` per boundary. Two things are worth reading off it,
and neither is expressible as an AL assertion:

```bash
grep 'TestData.LazyLoad' run.log | sort | uniq -c   # every count must be 1
grep 'InstallBaseline.Restore' run.log              # baseline size, per boundary
```

**Each table must appear exactly once.** A table loaded twice means it was dropped from the
install baseline at a boundary and re-read — correct, but it pays a reader invocation per
table per boundary. Measured with the baseline write removed: `Country/Region`,
`Shipping Agent` and `No. Series` each loaded twice and the run took 10.9 s instead of 7.3 s.

**"A table nothing touches is never loaded" cannot be asserted in AL at all**, by
construction: the whole correctness property of on-demand loading is that AL cannot tell a
table materialised on first touch from one present from the start. It is asserted in
`AlRunner.Tests/TestDataLazyHydrationTests.cs` instead, against a fake `bcbak` that records
every command it is given — which is the level the saving actually happens at.

**CI does not run this bundle, and that is deliberate.** It only passes with `--test-data`
and a BC sandbox backup on the machine (~1 GB, shipped inside the sandbox artifact). CI runs
`tests/runner-extras/` wholesale, without the flag — a bundle asserting hydrated rows would
fail there by construction rather than prove anything. Hence its own directory outside that
tree.

Run it locally:

```bash
export AL_RUNNER_BCBAK=~/.cache/al-runner/bcbak/bcbak       # or put `bcbak` on PATH
dotnet run --project AlRunner -c Release -- \
    tests/test-data-fixture \
    --package-cache "$HOME/.al-runner/platform-apps" \
    --test-data="$HOME/.bcartifacts.cache/sandbox/<version>/w1/BusinessCentral-W1.bak" \
    --test-data-company "CRONUS International Ltd_"
```

Without `--test-data` the tests fail, loudly and on purpose: the whole claim is that the
flag is what puts the rows there.

The runner-side mechanism (flag parsing, backup resolution, the install-baseline cache key,
the exclusion rules, value conversion) is pinned by `AlRunner.Tests/TestDataProvisioningTests.cs`
and `AlRunner.Tests/TestDataLazyHydrationTests.cs`, which run on every CI leg and need neither
the backup nor the reader.
