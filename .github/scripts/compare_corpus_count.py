#!/usr/bin/env python3
"""How many corpus tests did this leg run, and is that fewer than last time? (#3675)

WHAT REPLACED THE COMMITTED NUMBER
----------------------------------
`tests/expectations/count-baseline/test-count-baseline.json` used to carry an
exact expected test count for the corpus suites, and the runner exited 4 on any
difference in either direction. That worked because the corpus was PINNED: the
count could only move when a pull request moved the gitlink, in the same commit.

With the corpus resolved per run (#3737) the corpus moves without this repository
committing anything, so a checked-in exact count would go stale the moment an
upstream corpus PR merged -- every leg of every pull request red, exit 4, with
nothing here to fix. The corpus suites are therefore no longer declared in that
file at all (`CountBaseline.cs`: a suite the manifest does not mention imposes no
expectation), and the comparison moved here.

`runner-extras` stays in the committed baseline, exactly as before: it lives in
this repository, so its count moves only when a commit here moves it.

WHAT IT READS, AND THE MISTAKE THAT COST A WHOLE CI ROUND
---------------------------------------------------------
It reads the runner's `--count-out` document:

    { "bcVersion": "27.0",
      "suites": { "al-language": { "tests": 3123, "appGroups": 1 } } }

NOT `--out`, which is a FAILURE REPORT (`generated`, `total_failures`,
`classifications`, `all_failures`) and carries no test list at all. The first
version of this pointed at `--out` and every BC leg answered "no 'tests' array",
so the guard never ran once (run 34412771210). `--count-out` writes the same
tally `--count-baseline` judges, so the number compared and the number judged
cannot disagree.

WHAT THIS GUARDS, WHICH IS NOT WHAT --strict GUARDS
---------------------------------------------------
`--strict` fails a run when a test FAILS. It cannot see a suite that silently
stopped being discovered -- a dependency rename, a duplicate app id (#1850), a
dropped app group (#1861). Those tests do not fail, they stop existing, and the
run reports success over a smaller set. That is the drop this catches, PER SUITE:
a run-wide total can stay level while one suite's tests vanish into another's,
which is exactly the case a total-only comparison cannot see.

A GROWTH IS NOT A FAILURE ANY MORE, AND THAT IS THE REAL COST
-------------------------------------------------------------
The committed baseline failed on growth too, deliberately: a stale baseline under
a passing run hides a later real drop above it. That direction cannot survive a
moving corpus -- every upstream corpus PR that adds tests is a growth, and it
arrives here without anyone pushing anything. So growth is reported and allowed,
and this is a one-way ratchet. Said plainly rather than glossed: this guard is
weaker than the one it replaces, in exchange for a corpus that can move.

THE THIRD STATE (`guards-need-a-third-state.md`)
------------------------------------------------
"No previous count" and "a drop to zero" must never look alike. A document that
cannot be read, or carries no `suites` object at all, is exit 3 -- not a count of
zero, which would read as the largest possible drop and fail the leg for a
measurement that never happened. An EMPTY `suites` object is a real measurement
of zero and is compared as one. A previous count that is simply ABSENT (the first
run after this lands, a cache that expired) is a pass with a warning.

Exit codes
  0  measured, and not a drop (equal, growth, or nothing to compare against)
  1  measured, and FEWER tests than the previous run -- names both corpus SHAs
  3  could not measure: a document that is missing, unreadable, or has no `suites`

0 AND 1 BOTH WRITE --out; 3 DOES NOT, AND THE CALLER DEPENDS ON THAT
--------------------------------------------------------------------
A drop is a measurement, so exit 1 records the smaller number. The workflow may
then save it, which is the only thing that makes the recovery promised below
reachable: without it, main drops once and every later run -- and every pull
request reading main's cache -- restores the same larger number and fails against
it forever, with no in-repo remedy (caught in review of #3737).

Exit 3 returns BEFORE writing --out, so whatever the caller restored is still on
disk untouched. That is deliberate and load-bearing: saving it under this run's
corpus SHA would launder an old number onto a new commit.
"""
from __future__ import annotations

import argparse
import json
import os
import sys


def _load(path: str, what: str) -> dict:
    if not os.path.isfile(path):
        raise ValueError(
            f"{path} does not exist. The corpus run writes it with --count-out; if the run "
            f"aborted before that, there is no {what} to compare and this refuses rather "
            f"than reporting zero -- zero would read as the largest possible drop.")
    try:
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
    except Exception as e:  # noqa: BLE001 - any parse failure is the same refusal
        raise ValueError(f"{path} is not readable JSON ({e}). Nothing was measured.")
    if not isinstance(doc, dict):
        raise ValueError(f"{path} is not a JSON object, so it is not a count document.")
    return doc


def read_suites(doc: dict, path: str) -> dict:
    """`{suite: tests}` out of a `--count-out` document.

    A missing `suites` key and an empty one are different facts: the second means
    the run executed nothing, which is a real (and catastrophic) measurement,
    while the first means this is not a count document and nothing was measured.
    """
    suites = doc.get("suites")
    if not isinstance(suites, dict):
        raise ValueError(
            f"{path} has no `suites` object, so it is not a runner --count-out document. "
            f"Refusing to call that a count of zero. (Pointing this at --out is the "
            f"mistake that made the guard silently never run: that file is a failure "
            f"report and carries no counts.)")
    out = {}
    for name, entry in suites.items():
        if not isinstance(entry, dict) or not isinstance(entry.get("tests"), int):
            raise ValueError(
                f"{path}: suite '{name}' has no integer `tests`. A suite whose count "
                f"cannot be read is not a suite that ran zero tests.")
        out[name] = entry["tests"]
    return out


def read_measured(path: str) -> tuple[dict, str]:
    """(suite -> tests, bcVersion) for this run."""
    doc = _load(path, "count")
    return read_suites(doc, path), str(doc.get("bcVersion") or "")


def read_previous(path: str | None) -> dict | None:
    """The previous run's record, or None when there is not one.

    None is a legitimate, ordinary state -- the first run after this lands, or a
    cache entry that expired -- and stays a pass. A file that EXISTS but cannot be
    read is not: that is a broken measurement wearing the same clothes.
    """
    if not path or not os.path.isfile(path):
        return None
    doc = _load(path, "previous count")
    suites = read_suites(doc, path)
    return {"suites": suites, "corpusSha": doc.get("corpusSha", "<unrecorded>")}


def compare(now: dict, previous: dict | None, corpus_sha: str) -> tuple[int, list[str]]:
    """(exit code, lines to print). The whole decision, with no I/O in it."""
    total = sum(now.values())
    lines = [f"corpus tests run: {total} (corpus {corpus_sha})"]
    for name in sorted(now):
        lines.append(f"  {name}: {now[name]}")

    if previous is None:
        # ::warning:: rather than a plain line: a run summary and the annotations are
        # where this is read, and "nothing was compared" is exactly the state that
        # must not look like a comparison that passed.
        lines.append(
            "::warning::no previous count to compare against -- recording this one. "
            "That is the first run after a cache miss or after #3675 landed, not a "
            "finding, but it means this leg's count was NOT checked against anything.")
        return 0, lines

    was, was_sha = previous["suites"], previous.get("corpusSha", "<unrecorded>")
    lines.append(f"previous count:   {sum(was.values())} (corpus {was_sha})")

    # PER SUITE, not just the total: one suite's tests vanishing into another's leaves
    # the run-wide total intact, and a total-only comparison cannot see it.
    drops = [(name, count, now.get(name, 0))
             for name, count in sorted(was.items()) if now.get(name, 0) < count]
    if drops:
        lines.append("")
        for name, before, after in drops:
            lines.append(f"::error::suite '{name}' ran {before - after} FEWER tests "
                         f"({before} -> {after}).")
        lines.append(
            f"::error::that is the failure --strict cannot see: tests that stop being "
            f"DISCOVERED do not fail, they stop existing, and every surviving one still "
            f"passes (#1850, #1861). now at corpus {corpus_sha}; was at corpus {was_sha}. "
            f"Diff those two corpus commits before assuming the corpus legitimately shrank "
            f"-- if it did, this leg goes green again once a main run records the smaller "
            f"number.")
        return 1, lines

    new_suites = sorted(set(now) - set(was))
    if new_suites:
        lines.append(f"new suite(s): {', '.join(new_suites)}")
    grew = sum(now.values()) - sum(was.values())
    if grew > 0:
        lines.append(
            f"+{grew} since the previous run. Growth is allowed and recorded: the corpus "
            f"moves without this repository committing anything, so an upstream corpus PR "
            f"adding tests arrives here on its own.")
    elif grew == 0:
        lines.append("unchanged.")
    return 0, lines


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description=__doc__.split("\n\n")[0],
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="exit codes:\n  0 not a drop\n  1 a drop\n  3 could not measure")
    ap.add_argument("--counts", required=True,
                    help="the corpus run's --count-out document")
    ap.add_argument("--corpus-sha", required=True,
                    help="the corpus SHA this leg resolved")
    ap.add_argument("--bc-version", default="",
                    help="the BC version this leg ran; the document's own wins if it has one")
    ap.add_argument("--previous", default=None,
                    help="the previous run's record, if one was restored")
    ap.add_argument("--out", default=None,
                    help="write this run's record here")
    args = ap.parse_args(argv)

    try:
        now, bc_version = read_measured(args.counts)
        previous = read_previous(args.previous)
    except ValueError as e:
        print(f"::error::compare_corpus_count.py: {e}", file=sys.stderr)
        return 3

    rc, lines = compare(now, previous, args.corpus_sha)
    for line in lines:
        print(line)

    if args.out:
        out_dir = os.path.dirname(os.path.abspath(args.out))
        if out_dir:
            os.makedirs(out_dir, exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as fh:
            json.dump({"corpusSha": args.corpus_sha,
                       "bcVersion": bc_version or args.bc_version,
                       "tests": sum(now.values()),
                       "suites": {k: {"tests": v} for k, v in sorted(now.items())}},
                      fh, indent=2)
            fh.write("\n")
    return rc


if __name__ == "__main__":
    sys.exit(main())
