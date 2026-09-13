# runner-extras-tableext-eviction

Two dep/main suite pairs that pin the purges `RecordPatches.EvictCachedMetaTableForBaseTable`
performs when a tableextension arrives after its base table's NCLMetaTable was built:

| pair | purge it pins | issue |
|---|---|---|
| `tableext-eviction-field-trigger-timing{-dep,}` | `_fieldTriggersWiredTables` | #2463 |
| `tableext-eviction-subscriber-timing{-dep,}` | `EventSubscriberPatches.ForgetInjectedForTable` | #2510 |

## Why they are not in `tests/runner-extras/`

The eviction only happens when the dep app's install trigger builds the table **before** a
later bundle parses the tableextension. `tests/runner-extras` passed as one root is **one
bundle** (`[1/1] tests/runner-extras — 74 suites`): every app's AL is parsed before any install
runs, so there is nothing to evict, and both pairs passed with either purge mutated out (#4079).

So `.github/workflows/bc-tests.yml` runs them in their own step, passing the four directories
**as four ordered arguments**, dep before main. Passing this root directory instead collapses
them back into one bundle and reproduces #4079.

## The execution guard

The step runs with `--verbose` and fails, with its own message, unless the log carries
`[TableExt] evicted stale NCLMetaTable 65270` and `... 65520`. Tests passing without those lines
is the defect this directory exists to prevent, not a pass.

No `--count-baseline`, for the same reason as `tests/runner-extras-isolation-disabled`; `--strict`
plus the marker check is the gate.
