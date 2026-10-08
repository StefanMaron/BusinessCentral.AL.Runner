#!/usr/bin/env python3
"""A package-install step in a workflow must be bounded, and so must the release pack job (#5441).

`bc-tests.yml`'s `pack` job ("Release pack (dry, no publish)") had no `timeout-minutes`, so
GitHub's 360-minute default applied; one `apt-get install` hung for 79 minutes while the job
held the required `BC test matrix passed` context. The bounds, and the job durations they
were sized from, are in the PR that added this file.

The population is DERIVED, not listed: every step whose `run:` calls `apt-get install` is
checked, so a new install step is in scope the day it lands. Two rules:

  1. each such step carries a step-level `timeout-minutes` of at most STEP_MAX;
  2. the one job named in BOUNDED_JOBS carries a job-level `timeout-minutes` of at most
     JOB_MAX.

The ceilings are CEILINGS, not the values the workflows use: an absurd value (360, the
default restated) must fail as surely as an absent key. Parse the YAML, never grep it --
comments in these files mention timeouts.

Third state (guards-need-a-third-state.md): no readable workflow directory, a workflow that
does not parse, zero install steps found, or a named job that no longer exists REFUSES (3)
rather than passing -- each is a moved file or a renamed job, not a clean tree.

Run: python3 tools/test_workflow_install_timeouts.py
Exit: 0 measured and fine, 1 measured and broken, 3 could not measure.
"""
from __future__ import annotations

import os
import re
import sys
import tempfile

import yaml

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Ceilings in minutes. Healthy apt step: seconds, slowest healthy observed 12 min;
# healthy pack job: slowest observed 25 min. Both ceilings are a multiple of that, tight
# enough that GitHub's 360-minute default restated by hand still fails.
STEP_MAX = 30
JOB_MAX = 90

# (workflow file under .github/workflows, job id): jobs that must carry their own bound.
BOUNDED_JOBS = [("bc-tests.yml", "pack")]

APT_INSTALL = re.compile(r"\bapt-get\s+(?:-\S+\s+)*install\b")


def _minutes(value):
    """A timeout-minutes value as an int, or None when it is not a plain positive int."""
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        return None
    return value


def check(root: str):
    """Returns (exit_code, messages)."""
    wf_dir = os.path.join(root, ".github", "workflows")
    try:
        names = sorted(n for n in os.listdir(wf_dir) if n.endswith((".yml", ".yaml")))
    except OSError as exc:
        return 3, [f"UNMEASURABLE: cannot list {wf_dir} ({exc})."]
    docs = {}
    for name in names:
        try:
            with open(os.path.join(wf_dir, name), encoding="utf-8") as fh:
                doc = yaml.safe_load(fh)
        except (OSError, yaml.YAMLError) as exc:
            return 3, [f"UNMEASURABLE: {name} could not be read as YAML ({exc})."]
        docs[name] = doc if isinstance(doc, dict) else {}

    failures, installs = [], 0
    for name, doc in docs.items():
        jobs = doc.get("jobs")
        for job_id, job in (jobs.items() if isinstance(jobs, dict) else []):
            for i, step in enumerate((job or {}).get("steps") or []):
                run = step.get("run") if isinstance(step, dict) else None
                if not isinstance(run, str) or not APT_INSTALL.search(run):
                    continue
                installs += 1
                label = f"{name} job `{job_id}` step {i + 1} ({step.get('name', 'unnamed')})"
                got = step.get("timeout-minutes")
                m = _minutes(got)
                if got is None:
                    failures.append(f"{label}: no step-level timeout-minutes (cap {STEP_MAX}).")
                elif m is None or m > STEP_MAX:
                    failures.append(f"{label}: timeout-minutes {got!r} is not a positive "
                                    f"integer <= {STEP_MAX}.")

    if installs == 0:
        return 3, ["UNMEASURABLE: no `apt-get install` step found in any workflow. The "
                   "population is derived, so zero is a moved directory or a changed "
                   "spelling, not a clean tree."]

    for wf, job_id in BOUNDED_JOBS:
        job = ((docs.get(wf) or {}).get("jobs") or {}).get(job_id)
        if not isinstance(job, dict):
            return 3, [f"UNMEASURABLE: {wf} has no job `{job_id}`. Renamed? Update "
                       "BOUNDED_JOBS in this file."]
        got = job.get("timeout-minutes")
        m = _minutes(got)
        if got is None:
            failures.append(f"{wf} job `{job_id}`: no job-level timeout-minutes, so GitHub's "
                            f"360-minute default applies (cap {JOB_MAX}).")
        elif m is None or m > JOB_MAX:
            failures.append(f"{wf} job `{job_id}`: timeout-minutes {got!r} is not a positive "
                            f"integer <= {JOB_MAX}.")

    if failures:
        return 1, ["FAIL:"] + ["  " + f for f in failures]
    return 0, [f"PASS: {installs} apt-get install step(s) bounded; "
               f"{len(BOUNDED_JOBS)} named job(s) bounded."]


# ---- the refusal and failure paths, driven in throwaway trees -------------------------

def _tree(files):
    tree = tempfile.mkdtemp()
    wf = os.path.join(tree, ".github", "workflows")
    os.makedirs(wf)
    for name, text in files.items():
        with open(os.path.join(wf, name), "w", encoding="utf-8") as fh:
            fh.write(text)
    return tree


def _wf(job_extra="", step_extra="", run="sudo apt-get update && sudo apt-get install -y gcc"):
    return ("jobs:\n  pack:\n    runs-on: ubuntu-latest\n" + job_extra +
            "    steps:\n      - name: install\n" + step_extra +
            "        run: " + run + "\n")


CASES = [
    ("control: both bounded", {"bc-tests.yml": _wf("    timeout-minutes: 60\n", "        timeout-minutes: 20\n")}, 0),
    ("step timeout absent", {"bc-tests.yml": _wf("    timeout-minutes: 60\n")}, 1),
    ("step timeout at the default", {"bc-tests.yml": _wf("    timeout-minutes: 60\n", "        timeout-minutes: 360\n")}, 1),
    ("job timeout absent", {"bc-tests.yml": _wf("", "        timeout-minutes: 20\n")}, 1),
    ("job timeout at the default", {"bc-tests.yml": _wf("    timeout-minutes: 360\n", "        timeout-minutes: 20\n")}, 1),
    ("job timeout is an expression", {"bc-tests.yml": _wf("    timeout-minutes: ${{ inputs.t }}\n", "        timeout-minutes: 20\n")}, 1),
    ("no install step anywhere", {"bc-tests.yml": _wf("    timeout-minutes: 60\n", run="echo hi")}, 3),
    ("named job missing", {"bc-tests.yml": "jobs:\n  other:\n    steps:\n      - run: sudo apt-get install -y x\n        timeout-minutes: 5\n"}, 3),
    ("workflow unparseable", {"bc-tests.yml": "jobs: [unclosed\n"}, 3),
]


def _self_test() -> int:
    import shutil

    failed = 0
    for name, files, want in CASES:
        tree = _tree(files)
        try:
            rc, msgs = check(tree)
        finally:
            shutil.rmtree(tree, ignore_errors=True)
        ok = rc == want
        failed += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {name}: exit {rc} (want {want}) | {msgs[0]}")
    print(f"Total: {len(CASES)}, Failed: {failed}, Passed: {len(CASES) - failed}")
    return 1 if failed else 0


def main() -> int:
    rc, msgs = check(ROOT)
    print("\n".join(msgs))
    if rc == 0:
        print("\n--- refusal and failure paths ---")
        rc = _self_test()
    return rc


if __name__ == "__main__":
    sys.exit(main())
