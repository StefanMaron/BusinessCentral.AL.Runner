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

Third state (guards-need-a-third-state.md): a prose pattern that matches nothing
FAILS rather than passing vacuously, because a reworded sentence and a correct
one are otherwise indistinguishable -- that silent-pass shape is exactly what
#3299 and #3681 were.

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
        match = re.search(pattern, prose)
        if match is None:
            # The third state. A pattern matching nothing means the sentence was
            # reworded; reporting that as a pass would retire the check silently.
            FAILURES.append(
                f"{name}: the CLAUDE.md sentence this check reads no longer matches "
                f"/{pattern}/. Re-point the pattern at the new wording, or drop the "
                f"entry if the claim is gone -- do not leave it unmatched."
            )
            continue
        claimed = int(match.group(1))
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

    if FAILURES:
        print("FAIL: CLAUDE.md partial-class counts do not match the tree", file=sys.stderr)
        for f in FAILURES:
            print(f"  - {f}", file=sys.stderr)
        return 1
    print(f"PASS: {len(CLAIMS)} partial-class counts in CLAUDE.md match the tree")
    return 0


if __name__ == "__main__":
    sys.exit(main())
