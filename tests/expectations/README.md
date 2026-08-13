# tests/expectations/

Runner-owned manifest declaring expected outcomes for tests in
`tests/al-language/` (the BusinessCentral.AL.Language.Tests submodule).

See [`docs/expectations.md`](../../docs/expectations.md) for the schema, mode
semantics, and result-classification table.

Each JSON file is an array of expectation objects following the schema. File
naming convention:

- `oos-<area>.json` — out-of-scope-by-design (most common). `Mode: expect-oos`,
  matched on the reason anchor of either a typed `RunnerOutOfScopeException` or
  the `out-of-scope: <api> — <reason>` message convention Cecil-injected throw
  sites carry.
- `known-gaps-<area>.json` — in-scope but not yet implemented (transient, links
  to an **open** GitHub issue). `Mode: expect-fail-known-gap`.
- `divergence-<area>.json` — the runner intentionally and permanently answers
  differently from real BC. `Mode: expect-divergence`; carries `Reason` + `Doc`
  and no `Issue`, because there is no open work to link.
- `disabled-<area>.json` — won't compile or won't run; pure skip.

Sharding by area keeps PR diffs small. A single PR adding or removing one
expectation should touch one file with one entry.

The file prefix and the entry's `Mode` must agree — the prefix is what a human
scanning the directory reads. Moving an entry between modes means moving it
between files.

## `count-baseline/` is a different concern, deliberately not a top-level `.json`

`--expectations` (this directory, auto-probed by default) loads every
`*.json` file directly under `tests/expectations/` as an array of
per-test classification entries. `--count-baseline` (see
`AlRunner/Infrastructure/CountBaseline.cs`, #1880) is a *different* schema
entirely — an expected EXACT aggregate test/app-group COUNT per suite, not
a classification of one named test. It lives in the
`tests/expectations/count-baseline/` **subdirectory** specifically so the
`--expectations` directory scan (non-recursive) never tries to parse it as
a classification array. Do not add `*.json` files directly under
`tests/expectations/` unless they follow the classification-entry schema
above.
