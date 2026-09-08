#!/usr/bin/env python3
"""Line-ending guard: no tracked text file carries CR in the index (#3455).

The repository is LF throughout, and a file that reaches the index with CRLF
turns its next edit into a whole-file rewrite: #2426 made a 56-line change land
as 2,372 lines, and on 2026-09-07 the same shape recurred on docs/limitations.md
(25 added lines, 1560 insertions / 1531 deletions) because a Windows write
produced CRLF. The cost is not cosmetic -- git merge-tree reported CONFLICT on
that file against two unrelated open PRs and was CLEAN one commit earlier.

.gitattributes now pins `* text=auto eol=lf`, which stops a NEW file arriving
that way. It cannot repair one already committed with CRLF: text=auto explicitly
declines to convert a file already stored with CRLF, and eol=lf only governs
checkout. So this suite is what notices, and it fails on any i/crlf or i/mixed
row of `git ls-files --eol`.

Run: python3 tools/test_line_endings.py
"""
from __future__ import annotations

import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# A gitlink (submodule) row has an empty eolinfo in every column; it is not a
# file and has no line endings to judge.
GITLINK_EOL = ""

FAILURES: list[str] = []


def parse_ls_files_eol() -> list[tuple[str, str]]:
    """(index-eol, path) per tracked file.

    -z, and split the path off at the first tab rather than with .split():
    since this change the attr/ column itself contains a space
    ("attr/text=auto eol=lf"), so a whitespace split mis-indexes every row --
    the exact way this guard would go quietly vacuous.
    """
    out = subprocess.run(
        ["git", "ls-files", "--eol", "-z"],
        cwd=ROOT, capture_output=True, text=True, check=True,
    ).stdout
    rows = []
    for record in out.split("\0"):
        if not record:
            continue
        info, _, path = record.partition("\t")
        if not path:
            FAILURES.append(f"unparseable `git ls-files --eol` record: {record!r}")
            continue
        index_eol = info.split()[0]  # "i/lf", "i/crlf", "i/-text", "i/none", "i/"
        rows.append((index_eol[2:], path))
    return rows


def main() -> int:
    rows = parse_ls_files_eol()

    # Non-vacuous: a parser that drifted to match nothing must fail here rather
    # than report a clean zero, which is the answer that ends an investigation.
    if len(rows) < 1000:
        FAILURES.append(
            f"only {len(rows)} tracked files parsed -- expected >1000; the parser is broken"
        )
    kinds = {eol for eol, _ in rows}
    for expected in ("lf", "-text"):
        if expected not in kinds:
            FAILURES.append(
                f"no tracked file reported i/{expected} -- the parser is not reading "
                f"the index column (saw: {sorted(kinds)})"
            )

    bad = [(eol, p) for eol, p in rows if eol in ("crlf", "mixed")]
    for eol, path in bad:
        FAILURES.append(
            f"{path}: index line endings are {eol.upper()}, expected LF. "
            f"Fix with: git add --renormalize -- {path}"
        )

    attrs_path = os.path.join(ROOT, ".gitattributes")
    try:
        with open(attrs_path, encoding="utf-8") as fh:
            attrs = fh.read()
    except OSError as exc:
        FAILURES.append(f".gitattributes unreadable: {exc}")
    else:
        if "* text=auto eol=lf" not in attrs:
            FAILURES.append(
                ".gitattributes no longer pins `* text=auto eol=lf` -- narrowing the "
                "pin is how #2426 recurred on a file type it did not cover (#3455)"
            )

    if FAILURES:
        print(f"FAIL: {len(FAILURES)} problem(s)")
        for f in FAILURES:
            print(f"  - {f}")
        return 1

    counts: dict[str, int] = {}
    for eol, _ in rows:
        counts[eol] = counts.get(eol, 0) + 1
    print(
        "PASS: no tracked file has CR in the index "
        f"({len(rows)} tracked; " + ", ".join(f"{k or 'gitlink'}={v}" for k, v in sorted(counts.items())) + ")"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
