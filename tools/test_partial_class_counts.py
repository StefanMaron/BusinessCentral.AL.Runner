#!/usr/bin/env python3
"""CLAUDE.md's partial-class file counts must match the tree (#4059).

CLAUDE.md section 2d tells an agent that a `private` member in another file is
usually still reachable, and supports it with per-class file counts:

    `RecordPatches` is ONE `partial class` spread over **94 files**; `BcRuntime`
    over 24, `NclCecilRewrite` and `ProgramSupport` over 9 each, ...

#4059 asked whether the numeric claims in `.claude/rules/` and `CLAUDE.md` need
a mechanical check. The answer there is mostly no: a figure about a *moment* --
"seven orphaned registrations", "1,573 codeunit ids" -- has no source of truth
in the tree, and a sweep that guesses at the subject of each sentence gets it
wrong two times in three (docs/incidents/verify-execution-not-the-tick.md).

These six counts are the exception, and the reason is worth stating because it
decides what else may be added here: their subject is the **current tree**, the
query is unambiguous, and drift is silent. `RecordPatches` was written as 94 on
2026-09-12 (#3946) and was 96 by 2026-09-13 -- wrong within a day, with nothing
failing.

So the bar for a new entry is not "it is a number in a document". It is: the
document states a count *of the tree as it is now*, and one grep settles it.

Third state (guards-need-a-third-state.md). A prose pattern that could not be read
never passes vacuously -- a reworded sentence and a correct one are otherwise
indistinguishable, which is the silent-pass shape #3299 and #3681 were. Since
#4241 the verdicts are split by the remedy they send the reader to:

  0  every claim matched once and agrees with the tree
  1  a claim DISAGREES with the tree -- go fix the prose's number
  3  a claim could not be READ: the pattern matches nothing (reworded), or it
     matches several places that disagree with each other, so the guard cannot
     tell which sentence it pins -- go fix the prose's shape

Copies that agree are measurable and are compared like a single match: the
document says one thing, twice. Only disagreeing copies are unmeasurable. And a
measured disagreement outranks an unreadable claim, so a real drift is never
downgraded to "could not tell".

Run: python3 tools/test_partial_class_counts.py
"""
from __future__ import annotations

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(ROOT, "AlRunner")
CLAUDE_MD = os.path.join(ROOT, "CLAUDE.md")

FAILURES: list[str] = []
UNMEASURABLE: list[str] = []

# Each entry: the class name as CLAUDE.md spells it, and a regex with ONE group
# capturing the number that follows it in the prose. Kept explicit rather than
# derived from the sentence, so a rewording fails loudly instead of matching a
# different number in the same paragraph.
CLAIMS: list[tuple[str, str]] = [
    ("RecordPatches", r"`RecordPatches` is ONE `partial class` spread over \*\*(\d+) files\*\*"),
    ("BcRuntime", r"`BcRuntime` over (\d+)"),
    ("NclCecilRewrite", r"`NclCecilRewrite` and `ProgramSupport` over (\d+) each"),
    ("ProgramSupport", r"`NclCecilRewrite` and `ProgramSupport` over (\d+) each"),
    ("LiveNavTestPage", r"`LiveNavTestPage` (\d+)"),
    ("RunnerPageInstance", r"`RunnerPageInstance` (\d+)"),
]


def count_partial_class_files(name: str) -> int:
    """Files under AlRunner/ declaring `partial class <name>`."""
    pattern = re.compile(r"\bpartial\s+class\s+" + re.escape(name) + r"\b")
    hits = 0
    for dirpath, dirnames, filenames in os.walk(SOURCE_DIR):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            path = os.path.join(dirpath, fn)
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as fh:
                    if pattern.search(fh.read()):
                        hits += 1
            except OSError as exc:  # pragma: no cover - unreadable file is a real fault
                FAILURES.append(f"could not read {path}: {exc}")
    return hits


def main() -> int:
    if not os.path.isdir(SOURCE_DIR):
        print(f"FAIL: {SOURCE_DIR} does not exist; cannot measure", file=sys.stderr)
        return 3
    try:
        with open(CLAUDE_MD, "r", encoding="utf-8") as fh:
            prose = fh.read()
    except OSError as exc:
        print(f"FAIL: could not read CLAUDE.md: {exc}", file=sys.stderr)
        return 3

    for name, pattern in CLAIMS:
        found = re.findall(pattern, prose)
        if not found:
            # The third state. A pattern matching nothing means the sentence was
            # reworded; reporting that as a pass would retire the check silently.
            UNMEASURABLE.append(
                f"{name}: the CLAUDE.md sentence this check reads no longer matches "
                f"/{pattern}/. Re-point the pattern at the new wording, or drop the "
                f"entry if the claim is gone -- do not leave it unmatched."
            )
            continue
        if len(found) > 1 and len(set(found)) > 1:
            # findall, not search (#4241). `re.search` returns the FIRST match and
            # stops, so a document stating the claim twice -- one copy correct, one
            # drifted -- reads as correct and exits 0. The drift is present in the
            # file, and the guard whose whole job is to notice it reports success.
            #
            # `len(set(found)) > 1` is load-bearing: it refuses only when the copies
            # DISAGREE, which is the case where the guard genuinely cannot tell what
            # the document claims. Copies that AGREE are measurable -- the document
            # says one thing, twice -- so they fall through and get compared against
            # the tree like any single match. Without that term, a claim duplicated
            # with both copies wrong (888 twice against a tree of 105) reported exit 3
            # "make the claim unique", and deduplicating it leaves the survivor still
            # saying 888. The remedy would have destroyed the evidence of the drift it
            # was hiding -- the same defect one layer in (found in review of this PR).
            #
            # Two matches is UNMEASURABLE rather than a failure: the guard can no
            # longer tell which sentence it pins, so it must not report either
            # verdict. guards-need-a-third-state.md, applied to the guard's own
            # ambiguity rather than to its subject.
            #
            # Not hypothetical for a file this long, edited by many hands, that
            # states counts in more than one register. Nothing makes the copy a
            # writer edits the copy that appears first.
            UNMEASURABLE.append(
                f"{name}: /{pattern}/ matches {len(found)} places in CLAUDE.md, so "
                f"this check cannot tell which sentence it is pinning. Make the claim "
                f"unique, or narrow the pattern."
            )
            continue
        claimed = int(found[0])
        actual = count_partial_class_files(name)
        if actual == 0:
            FAILURES.append(
                f"{name}: no file under AlRunner/ declares `partial class {name}`. "
                f"Either the class was renamed or the count query is wrong; a zero "
                f"here is not a measurement."
            )
        elif claimed != actual:
            FAILURES.append(
                f"{name}: CLAUDE.md says {claimed} files, the tree has {actual}. "
                f"Update the prose (command grep -rl 'partial class {name}' AlRunner --include=*.cs | wc -l)."
            )

    # A measured disagreement outranks a pattern that stopped matching: if any claim
    # is demonstrably wrong, say so, whatever happened to the others. Only when
    # nothing is measurably wrong does an unmeasurable claim decide the verdict --
    # and then it is exit 3, never 1 and never 0. A reworded or duplicated sentence
    # means nothing was measured, which is a different thing from a count being
    # wrong and sends the reader to a different remedy (#4241).
    if FAILURES:
        print("FAIL: CLAUDE.md partial-class counts do not match the tree", file=sys.stderr)
        for f in FAILURES:
            print(f"  - {f}", file=sys.stderr)
        for u in UNMEASURABLE:
            print(f"  - (also unmeasurable) {u}", file=sys.stderr)
        return 1

    if UNMEASURABLE:
        print("UNMEASURABLE: CLAUDE.md's partial-class claims could not be read",
              file=sys.stderr)
        for u in UNMEASURABLE:
            print(f"  - {u}", file=sys.stderr)
        print(f"  {len(CLAIMS) - len(UNMEASURABLE)} of {len(CLAIMS)} claim(s) checked "
              f"and consistent.", file=sys.stderr)
        return 3

    print(f"PASS: {len(CLAIMS)} partial-class counts in CLAUDE.md match the tree")
    return 0


if __name__ == "__main__":
    sys.exit(main())
