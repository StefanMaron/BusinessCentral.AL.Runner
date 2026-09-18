#!/usr/bin/env python3
"""Do ci-wait.py and corpus-pass-count.py report "could not measure" when `gh` is absent?

#4329: both raised an uncaught FileNotFoundError and exited 1. For ci-wait.py exit 1 means "a
required check FAILED" — a verdict agents act on by reading the failing log and pushing a fix
(ci-verdicts.md) — so a missing binary reported a red PR that was not red. That is the shape
guards-need-a-third-state.md forbids: the could-not-measure answer wearing the measured-negative
code, where the measured negative is itself actionable.

github-access.md says web and remote sessions have no `gh` at all, so this is a supported
environment rather than a broken box.

Run: python3 tools/test_gh_absence_is_undetermined.py
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
        return
    FAILURES.append(name)
    print(f"  FAIL {name}" + (f" {detail}" if detail else ""))


def path_without_gh() -> str:
    """A PATH carrying the usual tools but no `gh`.

    Symlinks rather than an empty PATH: the tools shell out to git and need a real interpreter,
    so emptying PATH would make them fail for a second reason and the test would pass for the
    wrong one.
    """
    d = tempfile.mkdtemp(prefix="no-gh-")
    for exe in ("python3", "git", "bash", "sh", "env", "sed", "grep", "uname"):
        src = shutil.which(exe)
        if src:
            os.symlink(src, os.path.join(d, exe))
    return d


def run_without_gh(script: str, *args: str) -> tuple[int, str]:
    d = path_without_gh()
    env = dict(os.environ, PATH=d)
    p = subprocess.run([sys.executable, os.path.join(HERE, script), *args],
                       capture_output=True, text=True, env=env,
                       encoding="utf-8", errors="replace")
    return p.returncode, (p.stdout or "") + (p.stderr or "")


print("gh absent is UNDETERMINED (3), never a verdict")
for script, args in (
    ("ci-wait.py", ("4318", "--timeout", "0")),
    ("corpus-pass-count.py", ("35275673444", "Codeunit60708")),
):
    rc, out = run_without_gh(script, *args)
    check(f"{script} exits 3 when gh is absent", rc == 3, f"exit {rc}: {out[-200:]}")
    # The exit code is what a caller branches on, but the REASON is what an agent acts on, and
    # "a required check failed" and "there is no gh here" have opposite remedies.
    check(f"...and {script} says gh is missing rather than reporting a check result",
          "gh is not on PATH" in out, out[-200:])
    check(f"...and {script} points at the mcp__github__* route (github-access.md)",
          "mcp__github__" in out, out[-200:])
    # The specific wrong answer this replaced.
    check(f"...and {script} does not exit 1, which for ci-wait.py means a check FAILED",
          rc != 1, f"exit {rc}")
    check(f"...and {script} does not crash with a traceback",
          "Traceback (most recent call last)" not in out, out[-200:])

print()
if FAILURES:
    print(f"{len(FAILURES)} failed, 0 passed")
    sys.exit(1)
print("all checks passed")
