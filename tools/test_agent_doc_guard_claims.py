#!/usr/bin/env python3
"""impl-agent.md's warning that `.github/scripts/test_*` is MIXED must stay true.

`.claude/agents/impl-agent.md` tells every implementation agent to run the repository's
guards before pushing, and warns that `.github/scripts/test_*` holds both `.py` and `.sh`
files, so one `bash` loop over it reports every `.py` as a failure that is not there (#4509).
That warning is what this checks: the sentence is present, and the directory really holds
both kinds. If either kind disappears the warning sends readers to dispatch on an extension
for no reason; if the sentence is reworded this guard measures nothing and says so.

Until #4248 this guard pinned the per-set COUNTS. Two PRs each adding a guard each bumped
the figure the same way, identical hunks merged cleanly, and `main` went red on the merged
count. The counts are gone on the owner's direction that figures which change without anyone
editing the sentence belong in no durable text (#4539), and
tools/test_agent_doc_claims_survive_a_clean_merge.py replays that merge against this file.
Deliberately no check for numbers in the prose: see verify-execution-not-the-tick.md
§ "Does this want a tool?".

Exit (guards-need-a-third-state.md):
  0  the warning is present and the directory holds both `.py` and `.sh` guards
  1  the warning is present and one kind is gone, so the warning is false
  3  the doc or the directory could not be read, or the warning sentence is not found

Run: python3 tools/test_agent_doc_guard_claims.py
"""
from __future__ import annotations

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOC = os.path.join(ROOT, ".claude", "agents", "impl-agent.md")
SCRIPTS = os.path.join(ROOT, ".github", "scripts")

# The warning, keyed on what it asserts rather than on its wording around it.
WARNING = re.compile(r"`\.github/scripts/test_\*`[^\n]*\n?[^\n]*\bMIXED\b")

EXIT_OK, EXIT_FALSE, EXIT_CANNOT_MEASURE = 0, 1, 3


def main() -> int:
    if not os.path.isfile(DOC):
        print(f"UNMEASURABLE: {DOC} not found", file=sys.stderr)
        return EXIT_CANNOT_MEASURE
    if not os.path.isdir(SCRIPTS):
        print(f"UNMEASURABLE: {SCRIPTS} not found", file=sys.stderr)
        return EXIT_CANNOT_MEASURE

    with open(DOC, encoding="utf-8") as fh:
        text = fh.read()
    if WARNING.search(text) is None:
        print(f"UNMEASURABLE: impl-agent.md no longer matches /{WARNING.pattern}/ -- the "
              "mixed-set warning was reworded or removed, so this guard checks nothing. "
              "Re-point the regex or delete this guard with the warning.", file=sys.stderr)
        return EXIT_CANNOT_MEASURE

    names = [n for n in os.listdir(SCRIPTS)
             if n.startswith("test_") and os.path.isfile(os.path.join(SCRIPTS, n))]
    kinds = {ext for ext in (".py", ".sh") if any(n.endswith(ext) for n in names)}
    missing = sorted({".py", ".sh"} - kinds)
    if missing:
        print(f"FAIL: impl-agent.md warns that .github/scripts/test_* is MIXED, but it holds "
              f"no {' or '.join(missing)} guard any more. Update the warning.", file=sys.stderr)
        return EXIT_FALSE

    print("PASS: impl-agent.md's mixed-set warning holds: .github/scripts/test_* has both "
          ".py and .sh guards")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
