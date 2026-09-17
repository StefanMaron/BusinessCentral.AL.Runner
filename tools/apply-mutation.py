#!/usr/bin/env python3
"""Apply one mutation to one file, and REFUSE unless the anchor matched exactly once.

    tools/apply-mutation.py <file> --anchor-file a.txt --replacement-file b.txt
    tools/apply-mutation.py <file> --restore            # put the original back

`tools/mutation-verdict.py` classifies a mutation's RESULT. Nothing classified its
APPLICATION, and that is a distinct failure with an identical output: a mutation whose
anchor no longer matches changes nothing, the suite passes, and the row enters the table
as GREEN meaning "the guard did not catch this" when it means "this was never applied"
(#4316, measured on PR #4308 revision 3).

The two are opposite conclusions and indistinguishable from the run.

Three states, three exit codes, because a mutator that detects zero matches but not two
has the same hole one step along:

  exit 0  APPLIED      the anchor matched exactly once and the file changed
  exit 1  NOT-APPLIED  zero matches: a repair moved the line, or the anchor was mistyped
  exit 2  AMBIGUOUS    more than one match: which one was mutated is not determined

The original is saved beside the file as `<file>.mutation-backup` and `--restore` puts it
back. Restoring from a copy is deliberate: `git checkout -- <path>` discards an
uncommitted FIX along with the mutation, silently and with no reflog entry
(`no-git-stash-with-worktrees.md`).
"""
from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    # Before any print: the refusal messages carry em dashes, which cp1252 stdout cannot encode.
    _stdio.enable_utf8_stdio()

APPLIED, NOT_APPLIED, AMBIGUOUS = 0, 1, 2
SUFFIX = ".mutation-backup"


def apply(path: str, anchor: str, replacement: str) -> tuple[int, str]:
    with open(path, encoding="utf-8") as fh:
        text = fh.read()

    n = text.count(anchor)
    if n == 0:
        return NOT_APPLIED, (
            "the anchor matched NOTHING, so the file is unchanged and any verdict from a run "
            "after this is about unmutated code. A repair that touches the line a mutation "
            "anchors on invalidates that mutation, and the invalidation is green (#4316).")
    if n > 1:
        return AMBIGUOUS, (
            f"the anchor matched {n} times, so which site would be mutated is not determined. "
            "Narrow the anchor until it matches once — a mutation applied in two places "
            "proves less than one applied where you meant it.")

    with open(path + SUFFIX, "w", encoding="utf-8") as fh:
        fh.write(text)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(text.replace(anchor, replacement, 1))

    # Re-read rather than trusting the write: the landing check tdd.md asks for, done here so
    # it is not per-agent discipline.
    with open(path, encoding="utf-8") as fh:
        after = fh.read()
    if after == text:
        return NOT_APPLIED, "the write left the file byte-identical — anchor and replacement are the same"
    return APPLIED, f"the anchor matched once and the file changed; original saved as {path}{SUFFIX}"


def restore(path: str) -> tuple[int, str]:
    backup = path + SUFFIX
    if not os.path.exists(backup):
        return NOT_APPLIED, f"no {backup} to restore from"
    with open(backup, encoding="utf-8") as fh:
        text = fh.read()
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(text)
    os.remove(backup)
    return APPLIED, "restored from the backup and removed it"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(add_help=False)
    ap.add_argument("file")
    ap.add_argument("--anchor-file")
    ap.add_argument("--replacement-file")
    ap.add_argument("--restore", action="store_true")
    ap.add_argument("-h", "--help", action="store_true")
    try:
        args = ap.parse_args(argv[1:])
    except SystemExit:
        print(__doc__)
        return NOT_APPLIED
    if args.help:
        print(__doc__)
        return APPLIED

    if args.restore:
        code, why = restore(args.file)
    else:
        if not args.anchor_file or not args.replacement_file:
            print("--anchor-file and --replacement-file are both required (or --restore)")
            return NOT_APPLIED
        with open(args.anchor_file, encoding="utf-8") as fh:
            anchor = fh.read()
        with open(args.replacement_file, encoding="utf-8") as fh:
            replacement = fh.read()
        code, why = apply(args.file, anchor, replacement)

    print(f"{['APPLIED', 'NOT-APPLIED', 'AMBIGUOUS'][code]}: {why}")
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv))
