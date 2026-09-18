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

It is a TEXT scan over the workflow tree, which bounds what it can claim: it
catches the spelling that actually recurred here, not every conceivable way of
discarding an exit code. That is deliberate -- the alternative is a bash parser
whose false positives would cost more than the defect.

Exit codes: 0 clean, 1 the shape is back, 3 could not measure.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORKFLOWS = ROOT / ".github" / "workflows"

# `<something> "$s" || rc=1` and its near spellings: any assignment of a LITERAL
# exit code as the fallback of a command whose status is then thrown away.
COLLAPSE = re.compile(
    r"\|\|\s*(?:rc|RC|ret|status|fail(?:ed)?|err)\s*=\s*[0-9]+",
)

# The sanctioned caller. A workflow line invoking this is running the loop that
# does discriminate, so it is not a finding however it is spelled.
SANCTIONED = "run_guard_suites.sh"


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
            lines = path.read_text(encoding="utf-8").splitlines()
        except OSError as exc:  # unreadable is unmeasured, not absent
            print(f"UNMEASURED: cannot read {path}: {exc}", file=sys.stderr)
            print("1 check group(s) could not be measured; nothing here is a pass")
            return 3
        scanned += 1
        for n, line in enumerate(lines, 1):
            stripped = line.strip()
            if stripped.startswith("#"):
                continue  # a comment naming the shape (this fix leaves several)
            if SANCTIONED in line:
                continue
            if COLLAPSE.search(line):
                findings.append((path.relative_to(ROOT), n, stripped))

    print(f"scanned {scanned} workflow file(s) under {WORKFLOWS.relative_to(ROOT)}")

    if findings:
        print()
        print("FAIL - a suite-running loop discards its suites' exit codes:")
        for rel, n, text in findings:
            print(f"  {rel}:{n}: {text}")
        print()
        print("  Every non-zero collapses to 1, so a guard that could not measure")
        print("  (exit 3) is indistinguishable from one that caught a defect.")
        print(f"  Call .github/scripts/{SANCTIONED} instead -- it sorts each suite")
        print("  into pass / fail / unmeasured / abnormal and names which in the log.")
        print(f"\n0 passed, {len(findings)} failed")
        return 1

    print("ok   - no workflow collapses a suite's exit code into a literal")
    print("\n1 passed, 0 failed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
