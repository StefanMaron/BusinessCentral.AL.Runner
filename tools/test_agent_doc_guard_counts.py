#!/usr/bin/env python3
"""Pin the guard counts `.claude/agents/impl-agent.md` states against the tree.

WHY THIS EXISTS
  That file's "What to run before you push" block is read by every implementation agent
  before every push, and it states how many guards there are next to the instruction to
  run them. Nothing failed when those numbers drifted: #4235 found "There are 21 of each"
  when the sets were 32 and 19, and a `# 228 tests` comment when the suite was 280 — and
  a third stale `228` eleven lines below the first survived the initial correction, in the
  paragraph arguing against exactly that defect.

  A count that nobody can check is worse beside an instruction to run those guards than no
  count at all: the quiet failure is an agent taking the stated number as the contract and
  never noticing the guards the prose does not account for.

WHY A SIBLING AND NOT AN ENTRY IN test_partial_class_counts.py
  That guard pins `CLAUDE.md`'s partial-class counts: its CLAIMS table, its counting
  function and its target file are all that subject. These are guard-file counts in a
  different document, counted a different way. Folding them in would put two unrelated
  concerns behind one name; the shape it taught — a named claim, a regex, one query that
  settles it — is what is reused here.

  Deliberately NOT a sweep over every number in the file. `verify-execution-not-the-tick.md`
  measured that shape at two false alarms in three, because a checker cannot tell which
  subject a sentence means. Each claim below is named and its query is unambiguous.

THE THIRD STATE
  A regex that matches nothing is NOT a pass. It means the prose was reworded and this
  guard is now measuring nothing — reported as UNMEASURABLE (exit 3), never as success.
"""
from __future__ import annotations

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOC = os.path.join(ROOT, ".claude", "agents", "impl-agent.md")

EXIT_OK, EXIT_DRIFTED, EXIT_CANNOT_MEASURE = 0, 1, 3


def count_files(subdir: str, pattern: str) -> int:
    """Files directly under <subdir> whose basename matches <pattern>."""
    d = os.path.join(ROOT, subdir)
    if not os.path.isdir(d):
        return -1
    rx = re.compile(pattern)
    return sum(
        1 for n in os.listdir(d)
        if rx.match(n) and os.path.isfile(os.path.join(d, n))
    )


# (human name, regex capturing ONE integer from the doc, how to count it for real)
CLAIMS = [
    (
        "tools/test_*.py guards",
        r"The loop above runs \*\*(\d+)\*\* guards",
        lambda: count_files("tools", r"^test_.*\.py$"),
    ),
    (
        ".github/scripts/test_* guards",
        r"`\.github/scripts/test_\*` holds a further\s*\n?\s*\*\*(\d+)\*\*",
        lambda: count_files(os.path.join(".github", "scripts"), r"^test_"),
    ),
]


def main() -> int:
    if not os.path.isfile(DOC):
        print(f"UNMEASURABLE: {DOC} not found", file=sys.stderr)
        return EXIT_CANNOT_MEASURE

    with open(DOC, encoding="utf-8") as fh:
        text = fh.read()

    unmeasurable, drifted, checked = [], [], 0
    for name, rx, counter in CLAIMS:
        m = re.search(rx, text)
        if m is None:
            unmeasurable.append(f"{name}: the doc no longer matches /{rx}/")
            continue
        actual = counter()
        if actual < 0:
            unmeasurable.append(f"{name}: the directory it counts is missing")
            continue
        checked += 1
        stated = int(m.group(1))
        if stated != actual:
            drifted.append(f"{name}: doc says {stated}, tree has {actual}")

    if unmeasurable:
        for line in unmeasurable:
            print(f"UNMEASURABLE: {line}", file=sys.stderr)
        print(
            "A regex matching nothing is not a pass -- it means the prose moved and this "
            "guard is measuring nothing. Re-point the regex or drop the claim.",
            file=sys.stderr,
        )
        return EXIT_CANNOT_MEASURE

    if drifted:
        for line in drifted:
            print(f"FAIL: {line}", file=sys.stderr)
        print(f"Update .claude/agents/impl-agent.md, or the counts mislead every agent that reads it.",
              file=sys.stderr)
        return EXIT_DRIFTED

    print(f"PASS: {checked} guard-count claim(s) in impl-agent.md match the tree")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
