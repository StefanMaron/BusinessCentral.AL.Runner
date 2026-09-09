# `--count-baseline`: the expected test count per suite

`test-count-baseline.json` says how many tests and how many app groups each suite must run.
CI passes it on both corpus legs (`.github/workflows/bc-tests.yml`), and the runner exits **4**
when a count does not match — in **either** direction.

Both directions are the point. `--strict` fails a run when a test *fails*, but a suite that
silently stops being discovered (a dependency rename, a duplicate app id — #1850, a dropped
app group — #1861) still exits 0 with every surviving test green. A drop is that bug. A growth
has to be just as hard, or a stale baseline sits under a passing run nobody reads the stderr
of, and a later real drop lands above the stale number and passes unnoticed (#1880, and PR
#1882's review).

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

### Flat (`tests` / `appGroups`) — `al-language`, and any external caller

```json
"al-language": {
  "tests": { "default": 2523 },
  "appGroups": { "default": 1 }
}
```

`byBcVersion` may override `default` per BC version key (`"27.0"`, `"28.4"`, …).

`al-language` deliberately stays flat. Its count only ever moves when the `tests/al-language`
submodule pin moves, and a pin is a single gitlink entry: two PRs that both bump the corpus
conflict on the pin whatever this file looks like (measured: of the last 25 commits touching
this file, 11 also moved the gitlink). Splitting the corpus per codeunit would buy no merge
that is not already blocked, and would make every pin bump regenerate hundreds of lines that
must match CI on all eight legs.

`--count-baseline` is a public CLI flag, so the flat form is supported forever, not a
migration step.

## How to bump it

**Added or removed a runner-extras app group** — add or delete its one line under `groups`,
keyed by directory name, sorted. Nothing else moves: the suite total and the app-group count
are derived. A group that only compiles from BC 28.0 on gets `absentOn`.

**Added or removed tests in an existing runner-extras group** — edit that group's `tests`.

**Bumped the corpus pin** — edit `al-language`'s `tests.default` to the number the run
reported, and add an entry to `history.md` under `## al-language` saying which upstream PRs
came in and that you measured it rather than computed it.

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

`.github/scripts/check_count_baseline_history.sh`, run by `pr-gate.yml`, closes that. It fires
when a PR moves the `tests/al-language` gitlink and does not touch `history.md`. The trigger is
the **pin**, not this file: adding or removing a `runner-extras` group line needs no entry —
the group name and its count say what changed — and of the last 12 commits touching
`test-count-baseline.json` when the guard was written, the 2 that carried no `history.md`
change were both exactly that, and neither was a violation. Of the 31 commits that moved the
pin since `history.md` existed, all 31 carried an entry.

If a bump genuinely has nothing to record, say so on its own line in the PR body — the reason
is mandatory, and a placeholder is refused:

```
Count-Baseline-History-NA: <why this bump has nothing to record>
```

It checks only that an entry *exists*. Whether it is a good one is a reviewer's call.
