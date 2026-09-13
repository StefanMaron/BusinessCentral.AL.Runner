#!/usr/bin/env python3
"""The rule-recipe census guard's refusal paths fire (#3955).

`guards-need-a-third-state.md`: a refusal path with no test is indistinguishable
from a never-fire path, which is the defect itself. `tools/test_rule_recipes_executed.py`
passes on the real tree, and that says nothing about whether it *can* fail. This
suite builds synthetic `.claude/rules/` trees that each trip exactly one of its
checks, runs it against them, and asserts it refuses.

It drives the guard through its module API with RULES_DIR and ROOT repointed at a
scratch directory, so no copy of any rule text is kept here -- a second copy is the
drift source the issue is about one level up.

The classifier's two shapes each get a case, because they are separate code paths
and the fenced-block one was written first: an unmarked fenced detection recipe,
and an unmarked INLINE one. The inline case is the regression test for the miss
that mattered -- the first cut of the guard read fences only and did not see the
`git diff --stat origin/main...HEAD` line that produced #3955.

Run: python3 tools/test_rule_recipe_census_refuses.py
"""
from __future__ import annotations

import importlib
import io
import os
import shutil
import sys
import tempfile
from contextlib import redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def run_against(files: dict[str, str], extra: dict[str, str] | None = None,
                pad: int = 24) -> tuple[int, str]:
    """Run the guard over a scratch tree. Returns (exit code, captured stdout)."""
    tmp = tempfile.mkdtemp(prefix="recipe-census-")
    try:
        rules = os.path.join(tmp, ".claude", "rules")
        os.makedirs(rules)
        # The guard requires >10 rule files and >20 recipe blocks before it will
        # judge anything, so pad with inert files carrying a plain recipe block.
        # `pad=0` is how the vacuous-pass floor itself gets tested.
        for i in range(pad):
            with open(os.path.join(rules, f"pad-{i}.md"), "w", encoding="utf-8") as fh:
                fh.write(f"# pad {i}\n\n```bash\nls -l\necho {i}\n```\n")
        for name, body in files.items():
            with open(os.path.join(rules, name), "w", encoding="utf-8") as fh:
                fh.write(body)
        for name, body in (extra or {}).items():
            p = os.path.join(tmp, name)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "w", encoding="utf-8") as fh:
                fh.write(body)

        mod = importlib.import_module("test_rule_recipes_executed")
        importlib.reload(mod)
        mod.ROOT = tmp
        mod.RULES_DIR = rules
        mod.FAILURES = []
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = mod.main()
        return rc, buf.getvalue()
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ---------------------------------------------------------------- the cases

# Baseline: a correctly marked tree passes, so every refusal below is attributable
# to the thing that case changed and not to the harness.
GOOD_FENCED = """# good

Ask the ruleset -- this is the check that tells you whether it is blocked:

<!-- Recipe-unpinned: reads live GitHub state, cannot run offline here -->
```bash
gh api repos/o/r/rules/branches/main
```
"""

BAD_FENCED = """# bad

Ask the ruleset -- this is the check that tells you whether it is blocked:

```bash
gh api repos/o/r/rules/branches/main
```
"""

BAD_INLINE = """# bad inline

**`git diff --stat origin/main...HEAD` does not catch it -- use two dots.** More prose
explaining why the three-dot form reads clean.
"""

GOOD_INLINE = """# good inline

<!-- Recipe-unpinned: needs a scratch repository, pinned separately -->
**`git diff --stat origin/main...HEAD` does not catch it -- use two dots.** More prose
explaining why the three-dot form reads clean.
"""

PINNED_MISSING = """# pinned at nothing

<!-- Recipe-pinned-by: tools/test_does_not_exist.py -->
```bash
git rev-parse HEAD
```
"""

PINNED_ORPHAN = """# pinned at an unrelated suite

<!-- Recipe-pinned-by: tools/test_unrelated.py -->
```bash
git rev-parse HEAD
```
"""

PLACEHOLDER_REASON = """# placeholder

<!-- Recipe-unpinned: n/a -->
```bash
gh api repos/o/r
```
"""


def main() -> int:
    print("refusal paths of the rule-recipe census guard")

    rc, out = run_against({"good.md": GOOD_FENCED})
    check("a correctly marked tree passes (baseline)", rc == 0, out)

    rc, out = run_against({"bad.md": BAD_FENCED})
    check("an unmarked FENCED detection recipe is refused", rc == 1)
    check("...and the message names the offending file",
          "bad.md" in out and "unclassified" in out, out)

    rc, out = run_against({"bad.md": BAD_INLINE})
    check("an unmarked INLINE detection recipe is refused", rc == 1)
    check("...and the message says it was an inline command",
          "inline command" in out, out)

    rc, out = run_against({"good.md": GOOD_INLINE})
    check("a marked inline recipe passes", rc == 0, out)

    rc, out = run_against({"p.md": PINNED_MISSING})
    check("a marker naming a test file that does not exist is refused", rc == 1)
    check("...and the message names the missing test",
          "test_does_not_exist.py" in out, out)

    rc, out = run_against({"p.md": PINNED_ORPHAN},
                          extra={"tools/test_unrelated.py": "# mentions no rule\n"})
    check("a marker pointing at a suite that never mentions the rule is refused", rc == 1)
    check("...and the message says the suite never mentions it",
          "never mentions" in out, out)

    rc, out = run_against(
        {"p.md": PINNED_ORPHAN},
        extra={"tools/test_unrelated.py": "# pins p.md\n"})
    check("...and the same marker passes once that suite names the rule", rc == 0, out)

    rc, out = run_against({"ph.md": PLACEHOLDER_REASON})
    check("a placeholder unpinnable reason is refused", rc == 1)
    check("...and the message quotes the placeholder", "n/a" in out, out)

    # The guard must refuse a tree it cannot measure rather than passing vacuously:
    # an empty rules directory matches nothing, and matching nothing is the shape
    # that reads as success (guards-need-a-third-state.md).
    rc, out = run_against({}, pad=0)
    check("an empty rules tree is refused, not reported as a clean census",
          rc == 1, out)
    check("...and the refusal names both vacuity floors",
          "matched files at all" in out and "found at all" in out, out)

    if FAILURES:
        print(f"\nFAILED: {len(FAILURES)} check(s): {FAILURES}")
        return 1
    print("\nall refusal-path checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
