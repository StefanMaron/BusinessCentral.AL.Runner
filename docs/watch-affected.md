# `--watch --affected`: rerun only the tests an edit can affect

```
al-runner MyApp MyApp.Test --watch --affected [--include-failing] [--cache DIR]
```

Without `--affected`, every `--watch` cycle reruns every test in the bundles. With it, each
cycle runs the tests the edit can affect and skips the rest (#5027).

## Where the selection comes from

A `--watch --affected` cycle is the same run a `--server` `runTests` request with
`affectedOnly: true` makes: it goes through `RunTestsWithSelection` in `AlRunner/Program.cs`,
which compiles the bundles, decides the selection, runs the selected tests and records the
per-test data the next cycle selects from. Everything
[docs/server-mode.md](server-mode.md#affectedonly-and-previously-failing-tests) says about
`affectedOnly` applies unchanged: per-test coverage, entered scopes and built objects, raised
events and subscriber bindings, table keys, the packaged-dependency rule, the AL-output cache and
generation rules, the widening to the tests that share state under the `--isolation` in effect
(a selected test selects its whole codeunit under the default Codeunit isolation), the widening by
[session state](server-mode.md#affectedonly-and-session-state) no isolation resets, and every reason
a cycle is forced to run everything.

`--include-failing` is the request's `includeFailing: true`: every test whose last result was
not a pass runs again, whatever changed.

The watch process keeps one selection state for its lifetime, as one server process does, and
reads and writes the same persisted baseline (`<cache root>/affected-baseline/`, keyed on the
bundle paths). So the first cycle narrows when an earlier `--watch --affected` or `--server`
run on the same cache root recorded a baseline for these bundles that still describes the
source; otherwise it runs everything and says why.

## What a cycle prints

The per-test lines and the `Tests:` summary line cover only the tests that ran. A skipped test
is never printed and never counted as passed. When skipped tests are still failing from an earlier
cycle, the summary's failed figure says so: `failed 0 (+1 still failing, not re-run)`. Under the
summary:

```
[watch] affected: ran 2 of 5   skipped-unaffected 2   skipped-failing 1
[watch] affected: changed: Codeunit 60401 Helper
[watch] affected: not re-run, still failing from an earlier cycle: Tests.Broken
```

- The first line is always printed, with all three counts. `skipped-failing` counts skipped
  tests whose last recorded result was not a pass; the third line names them.
- A cycle that ran everything prints `[watch] affected: full run — <reason>` in place of the
  `changed:` line, with the same reason text a server response carries in `selection.reason`.

The interactive dashboard shows the same lines above the test tree.

## What it does not combine with

`--affected` is rejected with exit 2 without `--watch` (a `--server` client sets `affectedOnly`
per request), and together with `--tdd`, `--per-suite` or `--test`/`--filter`: the selecting
run applies none of them, so accepting them would drop them silently.

Under `--affected` an EMIT-EXCLUDED object fails the whole bundle's compile, as it does for a
`--server` request: the cycle reports `COMPILE FAIL` and runs no test of that bundle. Plain `--watch`
is more lenient: it reports the bundle as partial and still runs its healthy tests.

Under `--affected` a cycle runs through the server's run path, which resets the bundle caches
and re-runs the dependency pre-passes itself, so the watch loop's own reset and pre-pass are
skipped for that cycle. The run reports no per-phase times, so the whole cycle's time is shown as
run time.
