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

import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
FAILURES: list[str] = []
UNMEASURED: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
        return
    FAILURES.append(name)
    print(f"  FAIL {name}" + (f" {detail}" if detail else ""))


def _load(script: str):
    """Import a tools/ script by path — the filenames are hyphenated, so `import` cannot."""
    spec = importlib.util.spec_from_file_location(
        script.replace("-", "_").removesuffix(".py"), os.path.join(HERE, script))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


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
    # Exit 1 means something different in each tool and neither is "nothing was measured":
    # a failing required check in ci-wait.py, a no-match/disagreement/failure in
    # corpus-pass-count.py. Both send a reader somewhere real, which is why neither may be
    # reported for a missing binary.
    check(f"...and {script} does not exit 1, its own measured-negative code", rc != 1, f"exit {rc}")
    check(f"...and {script} does not crash with a traceback",
          "Traceback (most recent call last)" not in out, out[-200:])

print()
print("...and the guard is INERT when gh is present")
# The control in this PR's first revision ran the gh-present path against the UNMUTATED predicate,
# where require_gh() is a no-op by construction — so it restated the code rather than testing it,
# and could not fail. Both of these walk straight through it (#4329, found in review):
#
#   if shutil.which("gh") is None:  ->  if True:          10/10 green, ci-wait returns 3 for
#   ... or os.environ.get("CI") != "\x00never"            EVERY pr, including genuinely red ones
#
# So assert the other direction directly: with gh on PATH the guard must not fire. That is the
# half that keeps a widened predicate from silently converting every verdict into "could not
# measure" — a far worse defect than the one this file exists to fix.
if shutil.which("gh") is None:
    # UNMEASURED, not failed — and routed around check() deliberately, because check() knows only
    # pass and fail, so anything reported through it can only ever exit 0 or 1.
    #
    # This guard exists because ci-wait.py spelled "could not measure" as its measured-negative
    # code. Reporting its own could-not-measure as exit 1 is that defect one level up, and it is
    # the more dangerous copy: a caller sweeping tools/test_*.py reads 1 as "this guard caught
    # something", which is the opposite of what happened (#4346).
    UNMEASURED.append(
        "the inert-direction half needs gh on PATH and there is none here. The absence half above "
        "ran and is reported; this half asserted nothing, which is not the same as passing.")
else:
    # Call require_gh() directly rather than driving the whole tool: the property is "the guard
    # does not fire when gh is present", and reaching it through a real PR or corpus run costs
    # four network calls (~29s) and makes this guard fail on an offline box for a reason that has
    # nothing to do with its subject.
    for script in ("ci-wait.py", "corpus-pass-count.py"):
        mod = _load(script)
        try:
            mod.require_gh()
            fired = False
        except mod.GhUnavailable as exc:
            fired, detail = True, str(exc)
        check(f"{script}'s require_gh() is inert when gh is on PATH", not fired,
              f"it raised with gh present: {detail if fired else ''}")

print()
# Order matters: a real failure outranks an unmeasured half. Both can happen at once — the absence
# direction can catch a regression on a box with no gh — and reporting 3 there would hide a
# measured negative behind "could not measure", which is the same conflation pointed the other way.
if FAILURES:
    print(f"{len(FAILURES)} failed, 0 passed")
    sys.exit(1)
if UNMEASURED:
    for note in UNMEASURED:
        print(f"  UNMEASURED {note}")
    print(f"{len(UNMEASURED)} check group(s) could not be measured; nothing here is a pass")
    sys.exit(3)
print("all checks passed")
