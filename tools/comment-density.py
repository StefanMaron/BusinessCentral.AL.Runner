#!/usr/bin/env python3
"""Measure the comment/code split of a C# tree.

Exists so successive passes at issue #3260 measure the same way instead of
re-deriving a classifier each time and comparing incomparable numbers.

Classification, per physical line:

  blank    nothing but whitespace
  comment  the line's non-whitespace content is entirely comment: a `//` or
           `///` line, or a line wholly inside a `/* ... */` block
  code     anything else, including a line with a trailing `// ...` comment

A trailing comment counts as CODE, not comment. That is deliberate and
conservative: it under-counts comment mass rather than over-counting it, so a
reduction measured with this script is never flattered by it.

`//` and `/*` inside a string or char literal do not open a comment; the
scanner tracks string, verbatim-string, interpolated-string and char state so
that `var s = "// not a comment";` classifies as code.

Usage:
    tools/comment-density.py [ROOT ...] [--top N] [--json] [--min-lines N]
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import dataclass, asdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    # Before any print: stdout is built from the console codec, and cp1252
    # cannot encode the robot emoji in an agent-authored PR body (#3589).
    _stdio.enable_utf8_stdio()

SKIP_DIRS = {"obj", "bin", "graphify-out", ".git", "node_modules"}


@dataclass
class Counts:
    total: int = 0
    comment: int = 0
    code: int = 0
    blank: int = 0

    def add(self, other: "Counts") -> None:
        self.total += other.total
        self.comment += other.comment
        self.code += other.code
        self.blank += other.blank

    @property
    def share(self) -> float:
        """Comment share of NON-BLANK lines, which is the figure #3260 quotes."""
        nonblank = self.comment + self.code
        return (self.comment / nonblank * 100.0) if nonblank else 0.0


def classify(text: str) -> Counts:
    """Classify every physical line of a C# source file."""
    counts = Counts()
    in_block = False          # inside /* ... */
    for raw in text.splitlines():
        counts.total += 1
        stripped = raw.strip()
        if not stripped and not in_block:
            counts.blank += 1
            continue

        saw_code = False
        saw_comment = in_block
        i = 0
        n = len(raw)
        while i < n:
            ch = raw[i]
            if in_block:
                if ch == "*" and i + 1 < n and raw[i + 1] == "/":
                    in_block = False
                    i += 2
                    continue
                i += 1
                continue
            if ch == "/" and i + 1 < n and raw[i + 1] == "/":
                saw_comment = True
                break  # rest of the line is a line comment
            if ch == "/" and i + 1 < n and raw[i + 1] == "*":
                in_block = True
                saw_comment = True
                i += 2
                continue
            if ch == "@" and i + 1 < n and raw[i + 1] == '"':
                saw_code = True
                i = _skip_verbatim(raw, i + 2)
                continue
            if ch == '"':
                saw_code = True
                i = _skip_quoted(raw, i + 1, '"')
                continue
            if ch == "'":
                saw_code = True
                i = _skip_quoted(raw, i + 1, "'")
                continue
            if not ch.isspace():
                saw_code = True
            i += 1

        if saw_code:
            counts.code += 1
        elif saw_comment:
            counts.comment += 1
        elif stripped:
            counts.code += 1
        else:
            counts.blank += 1
    return counts


def _skip_quoted(line: str, i: int, quote: str) -> int:
    """Advance past a regular string/char literal opened just before `i`."""
    n = len(line)
    while i < n:
        if line[i] == "\\":
            i += 2
            continue
        if line[i] == quote:
            return i + 1
        i += 1
    return i


def _skip_verbatim(line: str, i: int) -> int:
    """Advance past a @"..." literal, where "" is an escaped quote."""
    n = len(line)
    while i < n:
        if line[i] == '"':
            if i + 1 < n and line[i + 1] == '"':
                i += 2
                continue
            return i + 1
        i += 1
    return i


def walk(roots: list[str]) -> list[str]:
    found: list[str] = []
    for root in roots:
        if os.path.isfile(root):
            found.append(root)
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for name in filenames:
                if name.endswith(".cs"):
                    found.append(os.path.join(dirpath, name))
    return sorted(found)


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("roots", nargs="*", default=["AlRunner"],
                    help="directories or files to scan (default: AlRunner)")
    ap.add_argument("--top", type=int, default=15,
                    help="how many worst-offender files to list (default 15)")
    ap.add_argument("--min-lines", type=int, default=200,
                    help="only rank files with at least this many lines (default 200)")
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    args = ap.parse_args(argv)

    roots = args.roots or ["AlRunner"]
    files = walk(roots)
    if not files:
        print(f"no .cs files under {roots}", file=sys.stderr)
        return 1

    per_file: dict[str, Counts] = {}
    total = Counts()
    for path in files:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            c = classify(fh.read())
        per_file[path] = c
        total.add(c)

    if args.json:
        print(json.dumps({
            "roots": roots,
            "files": len(files),
            "total": asdict(total) | {"share": round(total.share, 2)},
            "per_file": {p: asdict(c) | {"share": round(c.share, 2)}
                         for p, c in per_file.items()},
        }, indent=2))
        return 0

    print(f"{'/'.join(roots)}  {len(files)} files")
    print(f"  total   {total.total:>7,}")
    print(f"  comment {total.comment:>7,}")
    print(f"  code    {total.code:>7,}")
    print(f"  blank   {total.blank:>7,}")
    print()
    print(f"comment share of non-blank lines : {total.share:.1f}%")

    ranked = sorted(
        ((c, p) for p, c in per_file.items() if c.total >= args.min_lines),
        key=lambda t: t[0].share, reverse=True)[:args.top]
    if ranked:
        print()
        print(f"worst offenders (files of >= {args.min_lines} lines):")
        print(f"{'share':>7}  {'cmt/code':>10}  file")
        for c, p in ranked:
            print(f"{c.share:>6.1f}%  {c.comment:>4}/{c.code:<5}  {p}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
