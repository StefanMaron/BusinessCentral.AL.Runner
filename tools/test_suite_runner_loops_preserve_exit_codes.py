#!/usr/bin/env python3
"""A workflow loop running guard suites must not collapse their exit codes (#4359).

`for s in "${suites[@]}"; do python3 "$s" || rc=1; done` maps EVERY non-zero to
1, so a guard that CAUGHT a defect and a guard that MEASURED NOTHING (exit 3,
`.claude/rules/guards-need-a-third-state.md`) produce identical output and an
identical exit code. Three copies of that loop were in pr-gate.yml when #4359
was filed, and the convention is spreading: four guards already have a third
state and each new one inherits the collapse.

This guard fails when a workflow reintroduces the shape inline instead of
calling `.github/scripts/run_guard_suites.sh`, which sorts an exit code into
pass / fail / unmeasured / abnormal and says which in the log.

WHAT IT KEYS ON, AND WHY THAT CHANGED (#4362).
It used to key on an allowlist of variable names plus a literal integer
(`(?:rc|RC|ret|status|fail(?:ed)?|err)\\s*=\\s*[0-9]+`). A verbatim
reconstruction of the defect passed whenever either half was spelled
differently -- `|| bad=1` defeats the name half, and `|| rc=$((rc+1))`,
`|| rc=true` and `|| { rc=1; }` defeat the value half while using the
SANCTIONED name, so a reader checking "does it catch `rc=`?" concluded it was
covered. Measured at 9a7ba65d: 1 of 10 spellings caught.

It now keys on a STRUCTURAL property instead: inside a loop over guard suites,
any `||` fallback that discards the command's status is the defect, and the
variable's name and the assigned value are incidental. The context gate is what
buys that width without cost -- see the false-positive measurement below.

STILL A TEXT SCAN, NOT A BASH PARSER. The region tracking is line-based: a
suite glob opens a region, the matching `done` closes it, and a heredoc body is
skipped. That is enough to place a `||` inside a suite loop and no more; it
does not know shell grammar and does not try to.

THE TRAP IN THAT, WHICH SHIPPED ONCE AND WAS CAUGHT IN REVIEW. A line-based
skip that never finds its closer latches to end of file, and a blinded scan
reports the SAME `0 findings` as a clean one. `echo "messages<<PR_COMMITS_EOF"`
in pr-gate.yml is a string, not a heredoc operator, and reading it as one hid
500 of that file's 592 lines -- the file carrying all three original copies of
the defect. `DQ_STRING` below is the fix; `test_suite_runner_loop_guard.py`'s
real-file assertions are what would now catch a repeat, because no assertion
over a fixture can.

THE FALSE-POSITIVE MEASUREMENT (#4362), which is why the context gate exists.
Run against `.github/workflows/` at ebd78d54:

  * `|| <ident>=` with NO context gate flags 5 lines, ALL of them
    `|| rc=$?` in bc-tests.yml -- the idiom that PRESERVES the exit code and
    then discriminates on it (`exit "$rc"` carrying 1/2/3/5/134/139). Flagging
    those would red the very code #4359 asked for, so `$?` is excluded by
    construction, not by allowlist.
  * `|| true` with no context gate flags 5 lines: two greps over a log, a
    subshell capture in coverage-demo.yml, a sed pipeline in ms-bucket.yml and
    a `git fetch` in publish.yml. All legitimate, and one of them
    (coverage-demo.yml:53) is INSIDE a `for` loop -- so "any loop" is not a
    usable gate either; the gate has to be a loop over guard SUITES.
  * With both -- the suite-loop context, and `$?` excluded -- the whole
    workflow tree flags 0 lines.

Exit codes: 0 clean, 1 the shape is back, 3 could not measure.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORKFLOWS = ROOT / ".github" / "workflows"

# What opens a suite-loop region: a glob naming one of the guard-suite families
# this repository discovers by pattern. These are the three that existed when
# #4359 was filed, plus the shape a fourth would take. A line mentioning one is
# either building the suite list or looping over it; either way the lines under
# it until `done` are the region where a discarded status is the defect.
SUITE_GLOB = re.compile(
    r"(?:tools/test_\*"
    r"|\.github/scripts/test_\*"
    r"|scripts/tests/\*\.test\.py"
    r"|\$\{suites\[@\]\}"
    r"|\bsuites=\()"
)

# `for`/`while`/`until` ... `do`, and its `done`. Used only to find where a
# region that a suite glob opened ends. Both are counted PER LINE rather than
# per statement, because a loop written on one line (`for s in x; do :; done`)
# opens and closes in the same line -- counting only the opener would leave the
# region open for the rest of the file, which is a false positive on everything
# after it.
LOOP_OPEN = re.compile(r"(?:^|;|\bdo\b|&&|\|\|)\s*(?:for|while|until)\s")
LOOP_DONE = re.compile(r"(?:^|;|&)\s*done\b")

# The finding: a `||` fallback that throws the command's status away.
#
#   `|| <ident>=<anything>`   -- an assignment, optionally brace-wrapped, whose
#                               right-hand side is NOT `$?`. `$?` is the idiom
#                               that PRESERVES the code (see the measurement in
#                               the module docstring), so it is excluded here
#                               rather than by naming the variables that may
#                               use it.
#   `|| true` / `|| :`        -- swallowing the status outright.
#
# No name allowlist and no literal-integer requirement: both were the #4362
# defect.
DISCARD_ASSIGN = re.compile(r"\|\|\s*\{?\s*([A-Za-z_][A-Za-z_0-9]*)\s*=(?!\s*\$\?(?![A-Za-z_0-9]))")
DISCARD_SWALLOW = re.compile(r"\|\|\s*(?:true|:)\s*(?:;|&|\||$)")

# The sanctioned caller. A workflow line invoking this is running the loop that
# does discriminate, so it is not a finding however it is spelled.
SANCTIONED = "run_guard_suites.sh"

# A heredoc body is data, not shell: a workflow embedding an EXAMPLE of the bad
# shape (this guard's own fixtures do) must not be a finding.
HEREDOC_OPEN = re.compile(r"<<-?\s*['\"]?([A-Za-z_][A-Za-z_0-9]*)['\"]?")

# A double-quoted STRING is not shell syntax, and `<<` inside one is not a
# heredoc operator. pr-gate.yml:92 writes GitHub's own multiline-output protocol
# as `echo "messages<<PR_COMMITS_EOF"`, whose closer is `echo "PR_COMMITS_EOF"`
# -- a line that never strips to the bare delimiter. So a skip armed on that
# line NEVER closes, and it swallowed 500 of pr-gate.yml's 592 lines (84%): the
# one file that carried all three original copies of #4359's defect, blinded
# (#4362, caught in review of PR #4377).
#
# Blanking quoted interiors before looking for the operator takes the
# heredoc-skipped line count across the tree from 559 to 9, leaves the live tree
# at 0 findings, and restores detection inside pr-gate.yml. It is deliberately
# only applied to the HEREDOC search: the finding patterns must still see the
# real text, since `python3 "$s" || rc=1` carries the command in quotes.
DQ_STRING = re.compile(r'"[^"]*"')


def scan_text(name: str, text: str) -> list[tuple[str, int, str]]:
    """Return [(name, lineno, stripped)] for each discarded status in a suite loop.

    Exposed so `tools/test_suite_runner_loop_guard.py` can drive the detection
    on fixtures rather than on the live tree -- a guard whose detection cannot
    be exercised is one whose widening nobody can prove (`tdd.md`).
    """
    findings: list[tuple[str, int, str]] = []
    region_depth = 0       # loop nesting inside an open suite region; 0 = closed
    armed = False          # a suite glob seen, waiting for the loop it belongs to
    heredoc: str | None = None

    for n, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()

        if heredoc is not None:
            if stripped == heredoc:
                heredoc = None
            continue

        # Quoted interiors blanked so a `<<DELIM` inside a string literal is
        # not read as a heredoc opener -- see DQ_STRING above.
        m = HEREDOC_OPEN.search(DQ_STRING.sub('""', line))
        if m:
            heredoc = m.group(1)
            continue

        if stripped.startswith("#"):
            continue  # a comment naming the shape (the #4360 fix leaves several)

        if SUITE_GLOB.search(line):
            armed = True

        opens = len(LOOP_OPEN.findall(" " + stripped))
        closes = len(LOOP_DONE.findall(" " + stripped))
        if opens and (armed or region_depth > 0):
            region_depth += opens
            armed = False
        if closes and region_depth > 0:
            region_depth = max(0, region_depth - closes)

        if region_depth == 0:
            continue
        if SANCTIONED in line:
            continue
        if DISCARD_ASSIGN.search(line) or DISCARD_SWALLOW.search(line):
            findings.append((name, n, stripped))

    return findings


def main() -> int:
    if not WORKFLOWS.is_dir():
        print(f"UNMEASURED: no workflow directory at {WORKFLOWS}", file=sys.stderr)
        print("1 check group(s) could not be measured; nothing here is a pass")
        return 3

    files = sorted(WORKFLOWS.glob("*.yml")) + sorted(WORKFLOWS.glob("*.yaml"))
    if not files:
        print(f"UNMEASURED: {WORKFLOWS} holds no workflow files to scan", file=sys.stderr)
        print("1 check group(s) could not be measured; nothing here is a pass")
        return 3

    findings = []
    scanned = 0
    for path in files:
        try:
            text = path.read_text(encoding="utf-8")
        except OSError as exc:  # unreadable is unmeasured, not absent
            print(f"UNMEASURED: cannot read {path}: {exc}", file=sys.stderr)
            print("1 check group(s) could not be measured; nothing here is a pass")
            return 3
        scanned += 1
        findings.extend(scan_text(str(path.relative_to(ROOT)), text))

    print(f"scanned {scanned} workflow file(s) under {WORKFLOWS.relative_to(ROOT)}")

    if findings:
        print()
        print("FAIL - a suite-running loop discards its suites' exit codes:")
        for rel, n, text in findings:
            print(f"  {rel}:{n}: {text}")
        print()
        print("  Every non-zero collapses, so a guard that could not measure")
        print("  (exit 3) is indistinguishable from one that caught a defect.")
        print(f"  Call .github/scripts/{SANCTIONED} instead -- it sorts each suite")
        print("  into pass / fail / unmeasured / abnormal and names which in the log.")
        print(f"\n0 passed, {len(findings)} failed")
        return 1

    print("ok   - no suite loop discards a suite's exit code")
    print("\n1 passed, 0 failed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
