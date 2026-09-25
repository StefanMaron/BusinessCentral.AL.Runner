#!/usr/bin/env python3
"""CLAUDE.md § 2d's partial classes must really be partial classes spanning several files.

§ 2d tells an agent that a `private` member in another file is usually still reachable,
because the file is not the class, and names the classes where that bites. Until #4539
it backed the claim with per-class file counts, and this guard pinned the counts; they
drifted within a day of being written (#4059). #4539 removed the counts on the owner's
direction that figures which change without anyone editing the sentence belong in no
durable text. What the paragraph still claims, and what this guard still checks, is the
part that does not go stale: each class it names is a `partial class` declared in MORE
THAN ONE file under AlRunner/. A class renamed, merged into one file, or never partial
makes the sentence send the reader to the wrong conclusion, and nothing else notices.

Deliberately no count of files, and no check for numbers in the prose: see
.claude/rules/verify-execution-not-the-tick.md § "Does this want a tool?".

Third state (guards-need-a-third-state.md):

  0  the sentence was found and every class it names spans several files
  1  a named class is declared in no file, or in only one
  3  the sentence could not be read -- reworded, or stated in several places -- so
     nothing was measured

Run: python3 tools/test_partial_class_claims.py
"""
from __future__ import annotations

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_DIR = os.path.join(ROOT, "AlRunner")
CLAUDE_MD = os.path.join(ROOT, "CLAUDE.md")

# The § 2d sentence, from the first class name up to the "So a `private`" that ends
# the list. Every backticked identifier inside it is a class the paragraph names.
SENTENCE = re.compile(
    r"`RecordPatches` is ONE `partial class` spread over \*\*many files\*\*(?P<rest>.*?)So a `private`",
    re.S,
)
IDENT = re.compile(r"`([A-Z]\w+)`")


def files_declaring(name: str) -> int:
    pattern = re.compile(r"\bpartial\s+class\s+" + re.escape(name) + r"\b")
    hits = 0
    for dirpath, dirnames, filenames in os.walk(SOURCE_DIR):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for fn in filenames:
            if fn.endswith(".cs"):
                with open(os.path.join(dirpath, fn), encoding="utf-8", errors="replace") as fh:
                    if pattern.search(fh.read()):
                        hits += 1
    return hits


def main() -> int:
    if not os.path.isdir(SOURCE_DIR):
        print(f"UNMEASURABLE: {SOURCE_DIR} does not exist", file=sys.stderr)
        return 3
    with open(CLAUDE_MD, encoding="utf-8") as fh:
        prose = fh.read()

    found = SENTENCE.findall(prose)
    if len(found) != 1:
        print(f"UNMEASURABLE: CLAUDE.md § 2d's partial-class sentence matched {len(found)} "
              f"place(s), expected exactly one. Re-point {SENTENCE.pattern!r} at the new "
              f"wording, or drop this guard if the claim is gone.", file=sys.stderr)
        return 3

    names = ["RecordPatches"] + [n for n in IDENT.findall(found[0]) if n != "RecordPatches"]
    if len(names) < 2:
        print("UNMEASURABLE: the sentence names no class besides RecordPatches; the list "
              "this guard reads has moved.", file=sys.stderr)
        return 3

    failures = []
    for name in names:
        n = files_declaring(name)
        if n == 0:
            failures.append(f"{name}: no file under AlRunner/ declares `partial class {name}` "
                            f"(renamed, or not partial) -- fix the sentence")
        elif n == 1:
            failures.append(f"{name}: declared in ONE file only, so 'the file is not the "
                            f"class' is false for it -- drop it from the sentence")

    if failures:
        print("FAIL: CLAUDE.md § 2d names a class the tree does not support", file=sys.stderr)
        for f in failures:
            print(f"  - {f}", file=sys.stderr)
        return 1

    print(f"PASS: all {len(names)} classes CLAUDE.md § 2d names are partial classes "
          f"spanning several files ({', '.join(names)})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
