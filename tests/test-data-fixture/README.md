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
- `TestDataLobValues.Codeunit.al` (#2270, #2268) — Blob, Media, MediaSet, RecordId, Duration
  and a DB NULL. The load-bearing assertion is the Blob's CONTENT: a BC Blob column stores
  BC's container (four magic bytes + raw Deflate), not the field's bytes, so a codec that
  stored it verbatim would still give a blob with `HasValue` = true and a plausible length.

- `TestDataSameAppExtensionColumns.Codeunit.al` (#2273, #2301) — a column the base table's own
  AL field list does not name, because BC stores a tableextension's fields in the base table
  itself when the extension is declared in the same app. Twelve tables used to be refused whole
  over one such column. The assertions are on VALUES (`No. Series Line`'s CONT range, `Item`'s
  `Routing No.`) because a test asserting only "the table has rows" would pass with every
  extension field left blank — which is the bug's near miss. `ANumberCanBeDrawnFromAHydratedSeries`
  is the failure in the form Microsoft's Tests-SINGLESERVER hit it: ~220 of its tests failed with
  "You cannot assign new numbers from the number series CONT" against a backup where that series
  has 99,977 numbers left.

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
