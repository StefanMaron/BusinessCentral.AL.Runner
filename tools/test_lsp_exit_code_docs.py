#!/usr/bin/env python3
"""lsp-query.py's exit-1 caveat must be stated wherever exit 1 is documented (#4465).

`csharp-ls` builds its workspace-symbol index from types and their members, so a
LOCAL FUNCTION -- one declared inside another method -- is absent from it.
`tools/lsp-query.py symbol|callers` then exits 1, which three documents describe as
a negative you may rely on. Measured: RunAllBundlesForServer (AlRunner/Program.cs),
RunDependencyPrePasses and PaintWatchRunning all report it, against a control
(ResetForNewBundleReload, an ordinary method) that resolves.

WHY THIS GUARD EXISTS RATHER THAN A NOTE: the licence lived in THREE places and the
fix for #4465 initially corrected two. CLAUDE.md -- the one file auto-loaded into
every session, and the one impl-agent.md and triager.md both route agents to with
"do not rediscover any of it" -- kept saying "1 = a genuine not-found you may rely
on". Caught in review; nothing else would have.

WHAT IT PINS, and deliberately not more: each document that documents exit 1 must
also state the local-function caveat. It does NOT pin wording -- a rephrase is fine
and this guard must not make one expensive -- only that the caveat is present where
the licence is. A document that stops mentioning exit 1 at all is not a violation;
it can no longer mislead.

TRAP for a later editor: do not "fix" a red here by deleting the exit-1 line from a
document. That removes the guidance and satisfies the guard, which is the failure
this shape is meant to prevent.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Each file that documents exit 1 for lsp-query.py. A file with no exit-1 mention is
# skipped: it cannot grant a licence it never states.
DOCS = [
    Path("CLAUDE.md"),
    Path(".claude/skills/find-code/SKILL.md"),
    Path("tools/lsp-query.py"),
]

# "this document talks about exit 1" -- kept broad on purpose, so a reworded table
# or prose sentence still counts as documenting it.
# Deliberately broad. A document that documents exit 1 in a shape this misses is
# reported as `skip` -- visible, but no longer guarded -- so each alternative below
# was added against a spelling that occurs or could occur after a routine reformat.
# Reviewer of #4466 found the indented-block form resting on ONE alternative: a
# `1  x` -> `1: x` edit silently dropped lsp-query.py out of the checked set.
MENTIONS_EXIT_1 = re.compile(
    r"(?:exit(?:\s+code)?\s*1\b"       # "exit 1" / "exit code 1" in prose
    r"|\bexits\s+1\b"                  # "the tool exits 1"
    r"|^\s*\|\s*\*?\*?1\*?\*?\s*\|"   # a markdown table row whose first cell is 1
    r"|\b1\s*=\s"                      # "1 = ..." in a prose list
    r"|^\s{2,}1\s*[:.)]?\s{1,}\S)",    # an indented "1  x" / "1: x" / "1) x" block
    re.I | re.M,
)
# the caveat itself, by CONCEPT rather than phrasing
CAVEAT = re.compile(r"local function", re.I)


def main() -> int:
    failures, checked, skipped = [], [], []
    for rel in DOCS:
        p = ROOT / rel
        if not p.exists():
            failures.append(f"{rel}: listed here but missing from the tree")
            continue
        text = p.read_text(encoding="utf-8")
        if not MENTIONS_EXIT_1.search(text):
            skipped.append(f"{rel}: documents no exit 1, nothing to qualify")
            continue
        checked.append(str(rel))
        if not CAVEAT.search(text):
            checked.pop()   # a file that fails is not also reported as ok
            failures.append(
                f"{rel}: documents lsp-query.py exit 1 but never says a LOCAL FUNCTION "
                f"is absent from csharp-ls's index, so a reader is licensed to treat a "
                f"local function's exit 1 as a settled absence (#4465). Add the caveat; "
                f"do NOT delete the exit-1 guidance to silence this."
            )

    for s in skipped:
        print(f"  skip {s}")
    for c in checked:
        print(f"  ok   {c}: documents exit 1 AND states the local-function caveat")

    if failures:
        print("\nFAIL: lsp-query.py exit-1 documentation drift", file=sys.stderr)
        for f in failures:
            print(f"  {f}", file=sys.stderr)
        return 1
    if not checked:
        print("\nFAIL: no document mentions exit 1 -- this guard is measuring nothing, "
              "which is not the same as passing", file=sys.stderr)
        return 1
    print(f"\nall {len(checked)} document(s) documenting exit 1 carry the caveat")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
