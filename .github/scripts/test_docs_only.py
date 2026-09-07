#!/usr/bin/env python3
"""Tests for docs_only.py and for the test-matrix.yml wiring that consumes it (#2890).

Two halves. The classifier is exercised as a subprocess with fixture path lists.
The wiring half reads .github/workflows/test-matrix.yml and asserts the three
things that make a docs-only pull request cheap WITHOUT making the required
context vanish: the diff is classified, `bc-tests` is gated on the answer, and
the aggregate `BC test matrix passed` job still runs on the docs-only path. A
`paths-ignore` on the workflow would satisfy the first two and break the third
(#2726: a required context that never reports blocks the PR forever).

Usage: python3 test_docs_only.py
Exits 0 when every case passes, 1 on the first failure.
"""

import pathlib
import re
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
SCRIPT = HERE / "docs_only.py"
WORKFLOW = HERE.parent / "workflows" / "test-matrix.yml"

failures = 0


def check(name, ok, detail=""):
    global failures
    if ok:
        print(f"PASS  {name}")
    else:
        failures += 1
        print(f"FAIL  {name}\n      {detail}")


def run(paths):
    return subprocess.run(
        [sys.executable, str(SCRIPT)],
        input="\n".join(paths) + ("\n" if paths else ""),
        capture_output=True,
        text=True,
    )


# ---- classifier -----------------------------------------------------------------

cases = [
    ("a single .md file -> docs-only", ["docs/limitations.md"], "docs-only=true"),
    ("several .md files across the tree -> docs-only",
     ["README.md", ".claude/rules/tdd.md", "docs/scope.md", "AlRunner/README.md"], "docs-only=true"),
    (".md plus a C# file -> not docs-only", ["docs/scope.md", "AlRunner/Program.cs"], "docs-only=false"),
    ("the corpus gitlink alone -> not docs-only", ["tests/al-language"], "docs-only=false"),
    ("an expectations manifest -> not docs-only",
     ["tests/expectations/known-gaps-pages.json"], "docs-only=false"),
    ("a non-.md file under docs/ -> not docs-only", ["docs/archive/coverage.yaml"], "docs-only=false"),
    ("a workflow file -> not docs-only", [".github/workflows/test-matrix.yml"], "docs-only=false"),
    ("'.md' in the middle of the name, not the suffix -> not docs-only",
     ["docs/notes.md.bak"], "docs-only=false"),
    ("upper-case .MD is not the documented suffix -> not docs-only", ["README.MD"], "docs-only=false"),
    ("blank lines around the list are ignored", ["", "docs/a.md", "  ", "docs/b.md", ""], "docs-only=true"),
]
for name, paths, expected in cases:
    r = run(paths)
    check(name, r.returncode == 0 and r.stdout.strip() == expected,
          f"exit={r.returncode} stdout={r.stdout!r} stderr={r.stderr!r}")

r = run([])
check("an empty list is refused with exit 2, never classified",
      r.returncode == 2 and "docs-only=" not in r.stdout and "broken measurement" in r.stderr,
      f"exit={r.returncode} stdout={r.stdout!r} stderr={r.stderr!r}")

r = run(["docs/a.md", "AlRunner/X.cs", "AlRunner/Y.cs"])
check("the refusal names a non-documentation path so the author can see why",
      "AlRunner/X.cs" in r.stderr, r.stderr)

# ---- wiring in test-matrix.yml ----------------------------------------------------

text = WORKFLOW.read_text(encoding="utf-8").replace("\r\n", "\n")
code = "\n".join(l for l in text.split("\n") if not l.lstrip().startswith("#"))


def job_block(name):
    """The code-only text of one top-level job, from its `  name:` line to the next."""
    m = re.search(rf"^  {re.escape(name)}:\n(.*?)(?=^  \S|\Z)", code, re.S | re.M)
    return m.group(1) if m else ""


changes = job_block("changes")
bc_tests = job_block("bc-tests")
aggregate = job_block("all-tests")

check("test-matrix.yml has a `changes` job that measures the diff through pr_changed_files.sh",
      "pr_changed_files.sh" in changes, changes or "(no `changes` job)")
check("the `changes` job classifies the diff with docs_only.py",
      "docs_only.py" in changes, changes or "(no `changes` job)")
check("the `changes` job passes both event-payload SHAs, the endpoints pr_changed_files.sh insists on",
      "github.event.pull_request.base.sha" in changes and "github.event.pull_request.head.sha" in changes,
      changes)
check("the `changes` job checks out full history so both endpoints are present",
      re.search(r"fetch-depth:\s*0", changes) is not None, changes)
check("`bc-tests` is gated on the docs-only answer",
      re.search(r"if:.*needs\.changes\.outputs\.docs-only\s*!=\s*'true'", bc_tests) is not None
      and re.search(r"needs:.*\bchanges\b", bc_tests) is not None,
      bc_tests or "(no `bc-tests` job)")
check("`bc-tests` still delegates to the shared matrix",
      "uses: ./.github/workflows/bc-tests.yml" in bc_tests, bc_tests)
check("the aggregate job still exists under its required-context name",
      "name: BC test matrix passed" in aggregate, aggregate or "(no `all-tests` job)")
check("the aggregate job runs on every path (`if: always()`), so the required context always reports",
      re.search(r"if:\s*always\(\)", aggregate) is not None, aggregate)
check("the aggregate job depends on both `changes` and `bc-tests`",
      re.search(r"needs:.*\bchanges\b", aggregate) is not None
      and re.search(r"needs:.*\bbc-tests\b", aggregate) is not None, aggregate)
check("the aggregate job reads the docs-only answer, so it can pass on a skipped matrix",
      "needs.changes.outputs.docs-only" in aggregate, aggregate)
check("the aggregate job refuses when the classification itself failed",
      "needs.changes.result" in aggregate, aggregate)
check("the workflow does not use paths-ignore / paths, which would stop the required context reporting",
      re.search(r"^\s*paths(-ignore)?:", code, re.M) is None, "found a paths filter")

if failures:
    print(f"\n{failures} check(s) failed")
    sys.exit(1)
print("\nall checks passed")
