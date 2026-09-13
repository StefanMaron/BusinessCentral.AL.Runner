# `--count-baseline`: the expected test count per suite

`test-count-baseline.json` says how many tests and how many app groups each suite must run.
CI passes it on the `runner-extras` leg (`.github/workflows/bc-tests.yml`), and the runner
exits **4** when a count does not match — in **either** direction.

**The al-language corpus suites are deliberately not in this file** (#3675). The corpus is
resolved per run rather than pinned (#3737), so a committed exact count for it would go stale
the moment an upstream corpus PR merged — every BC leg red, exit 4, with nothing in this
repository to fix. `CountBaselineCheck` imposes no expectation on a suite this file does not
name, so removing them is a complete removal, not a silent zero. What guards the corpus count
instead is `.github/scripts/compare_corpus_count.py`: each leg counts what it ran and compares
against the last count a `main` run recorded, naming both corpus SHAs on a drop. That guard is
a one-way ratchet — growth is allowed and recorded, because an upstream PR adding tests arrives
here without anyone pushing anything.

Both directions are the point. `--strict` fails a run when a test *fails*, but a suite that
silently stops being discovered (a dependency rename, a duplicate app id — #1850, a dropped
app group — #1861) still exits 0 with every surviving test green. A drop is that bug. A growth
has to be just as hard, or a stale baseline sits under a passing run nobody reads the stderr
of, and a later real drop lands above the stale number and passes unnoticed (#1880, and PR
#1882's review).

**A suite this file declares but the run never produced** is compared against nothing. With
`--count-baseline-require-all` — which the `runner-extras` step passes, because it is the one
invocation covering every suite declared here — that fails with exit 4 and a `MISSING` line
naming the key and this file, so a vanished suite or a misspelled key cannot stand down
silently (#3130). Without the flag the runner prints `not checked` for it and passes, because
`--count-baseline` is a public flag and a caller's baseline may name suites another invocation
covers.

Nothing about this file is a floor, a tolerance, or auto-updated. If your PR changes a count,
you edit it, and CI prints the exact numbers to use.

## The schema

Two forms. A suite uses one or the other; declaring both is refused, because two sources of
truth for one number is how a baseline goes quietly stale.

### Per-app-group (`groups`) — preferred for `runner-extras`

```json
"runner-extras": {
  "groups": {
    "date-virtual-table-window": { "tests": 3 },
    "microsoft-test-library": { "tests": 3, "absentOn": ["27.0", "27.3", "27.5"] }
  }
}
```

One line per app group — one directory under `tests/runner-extras/` with an `app.json`.

- expected **tests** on a BC version = the sum of `tests` over the groups present on it
- expected **app groups** = how many groups those are
- `absentOn` lists the BC version keys where the group does not run at all, which is how a
  suite that needs BC 28.0 (`"platform": "28.0.0.0"` / `"application": "28.0.0.0"` in its
  `app.json`) states that on its own line instead of through a `byBcVersion` override table
  no reader can tie back to a cause
- a dependency-only group with no tests of its own is `{ "tests": 0 }` and still counts as an
  app group

Both derived numbers are compared exactly, both directions, exactly as before. The derivation
cannot agree with a regression: every number it adds up is checked in and reviewed, and none
of it is read back from the run. A test that stops being discovered makes its group's
contribution smaller than the sum says, and that is the DROP the runner exits 4 on.

### Flat (`tests` / `appGroups`) — for any external caller

```json
"some-suite": {
  "tests": { "default": 2523 },
  "appGroups": { "default": 1 }
}
```

`byBcVersion` may override `default` per BC version key (`"27.0"`, `"28.4"`, …).

`--count-baseline` is a public CLI flag, so the flat form is supported forever, not a
migration step. No suite in this repository uses it today: `al-language` did, until the corpus
stopped being pinned (#3675, above).

## How to bump it

**Added or removed a runner-extras app group** — add or delete its one line under `groups`,
keyed by directory name, sorted. Nothing else moves: the suite total and the app-group count
are derived. A group that only compiles from BC 28.0 on gets `absentOn`.

**Added or removed tests in an existing runner-extras group** — edit that group's `tests`.

**Moved the corpus** — nothing to do here. The corpus is resolved per run and its count is
compared in CI (above).

**Never** record the reason for a bump inside `test-count-baseline.json`. It used to live in
one 40,178-character `_comment` line, and because every count-changing PR had to append to it,
every count-changing PR conflicted with every other one — in one session `al-language` moved
2464 → 2496 → 2500 → 2523 and `runner-extras` 234 → 237 → 243 → 250 → 256 → 260, and PRs that
did not disagree about a single number still collided (#2485). CI does not run at all on a
conflicted PR, so that did not merely cost a rebase; it hid whether the PR had ever been
green. Rationale goes in `history.md`, one section per suite.

## What holds the shape in place

`AlRunner.Tests/CountBaselineMergeShapeTests.cs` merges two branches that each carry out a
whole bump, with `git merge-file`, and fails if they conflict. It also fails on any line long
enough to be a conflict magnet, and checks that the `groups` keys are exactly the app-group
directories on disk — so a new suite whose baseline entry was forgotten fails in seconds
locally instead of on eight CI legs.

That holds the *shape* of a `history.md` section if one is written. It cannot see an omission,
and for a long time nothing could: PR #3588 bumped the pin, passed all 13 required checks
green, and wrote no entry at all — caught by a reviewer, not by CI (#3591).

`.github/scripts/check_count_baseline_history.sh` used to close that: it fired when a PR moved
the `tests/al-language` gitlink without touching `history.md`. Both the gitlink and the guard
went at #3737 — a `runner-extras` group line names its own app group and its count, which is
why that guard never keyed on this file in the first place. `history.md` is frozen as the
record of the pin era; per-run corpus SHAs are printed by each leg and carried in the run
summary.
