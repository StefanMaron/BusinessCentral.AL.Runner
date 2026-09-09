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

WHAT THIS GUARDS, WHICH IS NOT WHAT --strict GUARDS
---------------------------------------------------
`--strict` fails a run when a test FAILS. It cannot see a suite that silently
stopped being discovered -- a dependency rename, a duplicate app id (#1850), a
dropped app group (#1861). Those tests do not fail, they stop existing, and the
run reports success over a smaller set. That is the drop this catches.

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
"No previous count" and "a drop to zero" must never look alike. A results file
that cannot be read, or carries no test array at all, is exit 3 -- not a count of
zero, which would read as the largest possible drop and fail the leg for a
measurement that never happened. A previous count that is simply ABSENT (the
first run after this lands, a cache that expired) is a pass with a warning: the
comparison could not be made, and there is nothing wrong with the run.

Exit codes
  0  measured, and not a drop (equal, growth, or nothing to compare against)
  1  measured, and FEWER tests than the previous run -- names both corpus SHAs
  3  could not measure: a results file that is missing, unreadable, or has no tests
"""
from __future__ import annotations

import argparse
import json
import os
import sys


def read_results(path: str) -> list:
    """The `tests` array out of a runner `--out` document.

    Anything else is a refusal. A missing key and an empty array are different
    facts: the second means the run executed nothing, which is a real (and
    catastrophic) measurement, while the first means this is not a results
    document and nothing was measured at all.
    """
    if not os.path.isfile(path):
        raise ValueError(
            f"{path} does not exist. The corpus run writes it with --out; if the run "
            f"aborted before that, there is no count to compare and this refuses rather "
            f"than reporting zero -- zero would read as the largest possible drop.")
    try:
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
    except Exception as e:  # noqa: BLE001 - any parse failure is the same refusal
        raise ValueError(f"{path} is not readable JSON ({e}). Nothing was measured.")
    if not isinstance(doc, dict) or not isinstance(doc.get("tests"), list):
        raise ValueError(
            f"{path} has no `tests` array, so it is not a runner results document. "
            f"Refusing to call that a count of zero.")
    return doc["tests"]


def read_previous(path: str | None) -> dict | None:
    """The previous run's count document, or None when there is not one.

    None is a legitimate, ordinary state -- the first run after this lands, or a
    cache entry that expired -- and stays a pass. A file that EXISTS but cannot be
    read is not: that is a broken measurement wearing the same clothes.
    """
    if not path or not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as fh:
        doc = json.load(fh)
    if not isinstance(doc, dict) or not isinstance(doc.get("tests"), int):
        raise ValueError(
            f"{path} exists but carries no integer `tests`. That is a corrupt previous "
            f"count, not an absent one, and the difference decides whether this leg is "
            f"comparing against anything at all.")
    return doc


def compare(now: int, previous: dict | None, corpus_sha: str) -> tuple[int, list[str]]:
    """(exit code, lines to print). The whole decision, with no I/O in it."""
    lines = [f"corpus tests run: {now} (corpus {corpus_sha})"]
    if previous is None:
        lines.append(
            "no previous count to compare against -- recording this one. That is the "
            "first run after a cache miss or after #3675 landed, not a finding.")
        return 0, lines

    was, was_sha = previous["tests"], previous.get("corpusSha", "<unrecorded>")
    lines.append(f"previous count:   {was} (corpus {was_sha})")

    if now < was:
        lines.append("")
        lines.append(
            f"::error::the corpus ran {was - now} FEWER tests than the previous run on "
            f"main. That is the failure --strict cannot see: tests that stop being "
            f"DISCOVERED do not fail, they stop existing, and every surviving one still "
            f"passes (#1850, #1861). now={now} at corpus {corpus_sha}; was={was} at "
            f"corpus {was_sha}. Diff those two corpus commits before assuming the corpus "
            f"legitimately shrank -- if it did, this leg goes green again once a main run "
            f"records the smaller number.")
        return 1, lines

    if now > was:
        lines.append(
            f"+{now - was} since the previous run. Growth is allowed and recorded: the "
            f"corpus moves without this repository committing anything, so an upstream "
            f"corpus PR adding tests arrives here on its own.")
    else:
        lines.append("unchanged.")
    return 0, lines


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description=__doc__.split("\n\n")[0],
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="exit codes:\n  0 not a drop\n  1 a drop\n  3 could not measure")
    ap.add_argument("--results", required=True,
                    help="the corpus run's --out document")
    ap.add_argument("--corpus-sha", required=True,
                    help="the corpus SHA this leg resolved")
    ap.add_argument("--bc-version", default="",
                    help="the BC version this leg ran, recorded in the output")
    ap.add_argument("--previous", default=None,
                    help="the previous run's count document, if one was restored")
    ap.add_argument("--out", default=None,
                    help="write this run's count document here")
    args = ap.parse_args(argv)

    try:
        tests = read_results(args.results)
        previous = read_previous(args.previous)
    except ValueError as e:
        print(f"::error::compare_corpus_count.py: {e}", file=sys.stderr)
        return 3

    rc, lines = compare(len(tests), previous, args.corpus_sha)
    for line in lines:
        print(line)

    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with open(args.out, "w", encoding="utf-8") as fh:
            json.dump({"corpusSha": args.corpus_sha, "bcVersion": args.bc_version,
                       "tests": len(tests)}, fh, indent=2)
            fh.write("\n")
    return rc


if __name__ == "__main__":
    sys.exit(main())
