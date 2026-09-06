#!/usr/bin/env python3
"""Unit tests for check_required_contexts.py, against synthetic workflow fixtures.

Same pattern as test_check_pr_check_triggers.sh: the guard is provable here
rather than only by opening a real PR, editing its body at the wrong moment and
watching a merge get refused.

Run: python3 .github/scripts/test_check_required_contexts.py
"""
from __future__ import annotations

import importlib.util
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "check_required_contexts", os.path.join(HERE, "check_required_contexts.py")
)
crc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(crc)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def run(files: dict[str, str], required: str) -> tuple[int, str]:
    """Write fixture workflows to a temp dir, run the guard, return (rc, stderr)."""
    import contextlib
    import io

    with tempfile.TemporaryDirectory() as d:
        for fname, body in files.items():
            with open(os.path.join(d, fname), "w", encoding="utf-8") as fh:
                fh.write(body)
        old = os.environ.get("REQUIRED_CONTEXTS")
        os.environ["REQUIRED_CONTEXTS"] = required
        err = io.StringIO()
        out = io.StringIO()
        try:
            with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
                rc = crc.main(["check_required_contexts.py", d])
        finally:
            if old is None:
                os.environ.pop("REQUIRED_CONTEXTS", None)
            else:
                os.environ["REQUIRED_CONTEXTS"] = old
        return rc, err.getvalue() + out.getvalue()


CANCELLABLE_EDITED = """
name: PR Check
on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, reopened, labeled, unlabeled, edited]
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
jobs:
  require-tests:
    name: Tests updated
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""

SAFE_NO_SAME_SHA_TYPES = """
name: PR Check
on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, reopened]
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
jobs:
  require-tests:
    name: Tests updated
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""

SAFE_NO_CONCURRENCY = """
name: Require Tests
on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, reopened, labeled, unlabeled]
jobs:
  require-tests:
    name: Tests updated
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""

DEFAULT_TYPES_CANCELLING = """
name: Test Matrix
on:
  push:
    branches: [main]
  pull_request:
    branches: [main]
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
jobs:
  all-tests:
    name: All BC versions passed
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""

JOB_LEVEL_CANCEL = """
name: Require Tests
on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, edited]
jobs:
  require-tests:
    name: Tests updated
    runs-on: ubuntu-latest
    concurrency:
      group: rt-${{ github.ref }}
      cancel-in-progress: true
    steps:
      - run: 'true'
"""

EXPLICIT_FALSE = """
name: Require Tests
on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, edited]
concurrency:
  group: rt-${{ github.ref }}
  cancel-in-progress: false
jobs:
  require-tests:
    name: Tests updated
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""

REUSABLE_CALLER = """
name: Test Matrix
on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, edited]
concurrency:
  group: tm-${{ github.ref }}
  cancel-in-progress: true
jobs:
  bc-tests:
    uses: ./.github/workflows/called.yml
"""

REUSABLE_CALLED = """
name: BC Tests
on:
  workflow_call:
jobs:
  matrix-leg:
    name: BC 28.3 (required)
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""

PUSH_ONLY = """
name: Publish
on:
  push:
    tags: ['v*']
jobs:
  ship:
    name: Tests updated
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""


print("check_required_contexts.py")

# --- the bug: cancel-in-progress + a same-SHA trigger type on a required context
rc, msg = run({"pr-check.yml": CANCELLABLE_EDITED}, "Tests updated")
check("edited + cancel-in-progress on a required context fails", rc == 1, f"(rc={rc})")
check("...and names the offending trigger type", "edited" in msg, msg)
check("...and names the context", "Tests updated" in msg, msg)
check("...and names the workflow file", "pr-check.yml" in msg, msg)

# --- the other direction: safe shapes must stay green, or the guard is noise
rc, msg = run({"pr-check.yml": SAFE_NO_SAME_SHA_TYPES}, "Tests updated")
check("cancel-in-progress with no same-SHA type is fine", rc == 0, f"(rc={rc}) {msg}")

rc, msg = run({"require-tests.yml": SAFE_NO_CONCURRENCY}, "Tests updated")
check("same-SHA types with no concurrency block is fine", rc == 0, f"(rc={rc}) {msg}")

rc, msg = run({"test-matrix.yml": DEFAULT_TYPES_CANCELLING}, "All BC versions passed")
check("default pull_request types + cancel-in-progress is fine",
      rc == 0, f"(rc={rc}) {msg}")

rc, msg = run({"require-tests.yml": EXPLICIT_FALSE}, "Tests updated")
check("cancel-in-progress: false with edited is fine", rc == 0, f"(rc={rc}) {msg}")

# --- job-level concurrency is the same defect one level down
rc, msg = run({"require-tests.yml": JOB_LEVEL_CANCEL}, "Tests updated")
check("job-level cancel-in-progress is caught too", rc == 1, f"(rc={rc}) {msg}")

# --- a required context nothing produces is the mirror-image outage
rc, msg = run({"pr-check.yml": SAFE_NO_CONCURRENCY}, "No Such Context")
check("a required context no workflow produces fails", rc == 1, f"(rc={rc})")
check("...and says so distinctly, not as a cancellation",
      "produced by NO workflow" in msg, msg)

rc, msg = run({"publish.yml": PUSH_ONLY}, "Tests updated")
check("a context produced only on push does not count as produced",
      rc == 1, f"(rc={rc}) {msg}")

# --- reusable-workflow contexts are qualified by the calling job id
rc, msg = run({"test-matrix.yml": REUSABLE_CALLER, "called.yml": REUSABLE_CALLED},
              "bc-tests / BC 28.3 (required)")
check("a reusable workflow's job resolves to '<caller> / <name>'",
      rc == 1 and "bc-tests / BC 28.3 (required)" in msg, f"(rc={rc}) {msg}")

# --- multiple required contexts: one bad is enough, and both get reported
rc, msg = run({"pr-check.yml": CANCELLABLE_EDITED, "test-matrix.yml": DEFAULT_TYPES_CANCELLING},
              "Tests updated,All BC versions passed")
check("a mixed set fails on the bad one only", rc == 1, f"(rc={rc})")
check("...and does not accuse the safe one",
      "All BC versions passed" not in msg.split("Fix:")[0], msg)

# --- unreadable input is exit 2, distinct from 'check failed'
rc, msg = run({}, "Tests updated")
check("an empty workflows directory is exit 2, not a failure verdict",
      rc == 2, f"(rc={rc}) {msg}")


# ===========================================================================
# #2785 -- the hardcoded list must be checked against the LIVE ruleset
# ===========================================================================
# Nothing compared DEFAULT_REQUIRED_CONTEXTS against what ruleset 15001420
# actually requires, so a required context added in the GitHub UI would be
# ignored by this guard AND by tools/ci-wait.py, both of which would keep
# reporting green having not checked it.

SAFE_WORKFLOWS = {
    "require-tests.yml": """
name: Tests
on:
  pull_request:
    types: [opened, synchronize, reopened, labeled, unlabeled]
jobs:
  require-tests:
    name: Tests updated
    runs-on: ubuntu-latest
""",
    "test-matrix.yml": """
name: Test Matrix
on:
  pull_request:
concurrency:
  group: x
  cancel-in-progress: true
jobs:
  all-tests:
    name: All BC versions passed
    runs-on: ubuntu-latest
""",
}


def rules_payload(contexts):
    """The shape GET /repos/{o}/{r}/rules/branches/main returns (measured 2026-09-05)."""
    return [
        {"type": "deletion", "ruleset_source_type": "Repository", "ruleset_id": 15001420},
        {"type": "non_fast_forward", "ruleset_source_type": "Repository",
         "ruleset_id": 15001420},
        {"type": "pull_request", "parameters": {}, "ruleset_id": 15001420},
        {"type": "required_status_checks",
         "parameters": {"strict_required_status_checks_policy": False,
                        "do_not_enforce_on_create": False,
                        "required_status_checks": [{"context": c} for c in contexts]},
         "ruleset_source_type": "Repository",
         "ruleset_source": "StefanMaron/BusinessCentral.AL.Runner",
         "ruleset_id": 15001420},
    ]


def run_live(fetch, files=None):
    """Run the guard with NO REQUIRED_CONTEXTS override, so the live check applies."""
    import contextlib
    import io

    files = SAFE_WORKFLOWS if files is None else files
    with tempfile.TemporaryDirectory() as d:
        for fname, body in files.items():
            with open(os.path.join(d, fname), "w", encoding="utf-8") as fh:
                fh.write(body)
        old = os.environ.pop("REQUIRED_CONTEXTS", None)
        old_skip = os.environ.pop("SKIP_RULESET_DRIFT_CHECK", None)
        err, out = io.StringIO(), io.StringIO()
        try:
            with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
                rc = crc.main(["check_required_contexts.py", d], fetch=fetch)
        finally:
            if old is not None:
                os.environ["REQUIRED_CONTEXTS"] = old
            if old_skip is not None:
                os.environ["SKIP_RULESET_DRIFT_CHECK"] = old_skip
        return rc, err.getvalue() + out.getvalue()


# --- the live set matching the hardcoded one is the only green case
rc, msg = run_live(lambda: rules_payload(crc.DEFAULT_REQUIRED_CONTEXTS))
check("a live ruleset matching the hardcoded list passes", rc == 0, f"(rc={rc}) {msg}")

# --- a context ADDED to the ruleset that the list does not know about
rc, msg = run_live(lambda: rules_payload(
    list(crc.DEFAULT_REQUIRED_CONTEXTS) + ["Provenance attested"]))
check("a required context added to the ruleset fails the guard", rc == 1, f"(rc={rc}) {msg}")
check("...and names it", "Provenance attested" in msg, msg)

# --- and the other direction: a name still listed here but no longer required
rc, msg = run_live(lambda: rules_payload(["All BC versions passed"]))
check("a context no longer required by the ruleset also fails the guard",
      rc == 1, f"(rc={rc}) {msg}")
check("...and names it", "Tests updated" in msg, msg)

# --- a failed lookup must be LOUD. Reading an error as 'no differences' is the
# --- exact failure mode #2785 is about: an unauthenticated 404 and an empty
# --- result are indistinguishable.
def boom():
    raise OSError("dial tcp 140.82.121.5:443: i/o timeout")


rc, msg = run_live(boom)
check("a failed ruleset lookup fails the guard rather than passing quietly",
      rc == 1, f"(rc={rc}) {msg}")
check("...and says the lookup itself failed",
      "i/o timeout" in msg or "could not read" in msg.lower(), msg)

# --- the disabled-ruleset trap: 15039643 has no required_status_checks rule, so
# --- querying it answers with an empty list. That must read as UNKNOWN.
rc, msg = run_live(lambda: [{"type": "pull_request", "parameters": {},
                            "ruleset_id": 15039643}])
check("a payload carrying no required_status_checks rule fails, not passes",
      rc == 1, f"(rc={rc}) {msg}")
rc, msg = run_live(lambda: rules_payload([]))
check("...and so does an empty required-context list", rc == 1, f"(rc={rc}) {msg}")

# --- the REQUIRED_CONTEXTS override must still win, per #2785: it exists so the
# --- unit tests above can drive synthetic fixtures, and the live check must not
# --- fight it. If it did, every test in this file would need a network call.
rc, msg = run({"a.yml": SAFE_WORKFLOWS["require-tests.yml"]}, "Tests updated")
check("the REQUIRED_CONTEXTS override skips the live check entirely",
      rc == 0, f"(rc={rc}) {msg}")

# --- tools/ci-wait.py carries the SAME list; the two must not drift apart
# --- either, or fixing one leaves the merge gate reading the other.
check("the guard's list and tools/ci-wait.py's RULESET_CONTEXTS agree",
      sorted(crc.load_ci_wait().RULESET_CONTEXTS) == sorted(crc.DEFAULT_REQUIRED_CONTEXTS),
      f"{crc.load_ci_wait().RULESET_CONTEXTS} vs {crc.DEFAULT_REQUIRED_CONTEXTS}")

rc, msg = run_live(lambda: rules_payload(crc.DEFAULT_REQUIRED_CONTEXTS),
                   files=SAFE_WORKFLOWS)
check("...and the guard passes with both in agreement", rc == 0, f"(rc={rc}) {msg}")

# A ci-wait.py whose list has drifted is caught, without touching the real file.
with tempfile.TemporaryDirectory() as _d:
    _fake = os.path.join(_d, "ci-wait.py")
    with open(_fake, "w", encoding="utf-8") as fh:
        fh.write('RULESET_CONTEXTS = ("All BC versions passed",)\n')
    problems = crc.ruleset_drift_problems(
        fetch=lambda: rules_payload(crc.DEFAULT_REQUIRED_CONTEXTS), ci_wait_path=_fake)
check("a drifted tools/ci-wait.py list is reported as a problem",
      any("ci-wait" in p for p in problems), str(problems))


# ===========================================================================
# Neither bypass may be switched on in pr-check.yml itself
# ===========================================================================
# SKIP_RULESET_DRIFT_CHECK=1 at least prints when it stands the live comparison
# down. REQUIRED_CONTEXTS is worse: main() branches on it, never calls
# resolve_contexts() at all, and so skips BOTH the live comparison and the
# ci-wait.py cross-check -- silently, until the ::warning:: added alongside this
# test. Either one set in the workflow turns the guard into a green no-op, which
# is exactly what an agent unbreaking CI during a GitHub API outage would reach
# for. The `check_required_contexts.py` job would still report success and
# nothing would say the guard was off.
#
# So: assert the real workflow does not set them, at the workflow, job or step
# level. This reads pr-check.yml on disk -- it is a claim about the shipped
# workflow, not about a fixture.
import re  # noqa: E402
import yaml as _yaml  # noqa: E402  (same dependency the guard itself uses)

_REPO = os.path.dirname(os.path.dirname(HERE))
_PR_CHECK = os.path.join(_REPO, ".github", "workflows", "pr-check.yml")
BYPASSES = ("REQUIRED_CONTEXTS", "SKIP_RULESET_DRIFT_CHECK")

with open(_PR_CHECK, encoding="utf-8") as fh:
    _wf = _yaml.safe_load(fh)

# Matches the GUARD invocation only. A bare "check_required_contexts.py" substring
# also matches the step that runs test_check_required_contexts.py, which would
# make the offender list double-count and the vacuity check pass on the test step
# alone.
_GUARD_RUN = re.compile(r"(?<!test_)check_required_contexts\.py")

_offenders: list[str] = []
_seen_step = False
for _var in BYPASSES:
    if _var in (_wf.get("env") or {}):
        _offenders.append(f"workflow env sets {_var}")
for _job_name, _job in (_wf.get("jobs") or {}).items():
    _job_env = _job.get("env") or {}
    _runs_guard = False
    for _step in _job.get("steps") or []:
        if not _GUARD_RUN.search(_step.get("run") or ""):
            continue
        _seen_step = _runs_guard = True
        for _var in BYPASSES:
            if _var in (_step.get("env") or {}):
                _offenders.append(f"{_job_name} step env sets {_var}")
    if _runs_guard:
        for _var in BYPASSES:
            if _var in _job_env:
                _offenders.append(f"{_job_name} job env sets {_var}")

check("pr-check.yml actually runs check_required_contexts.py "
      "(or the assertion below is vacuous)", _seen_step)
check("...and neither REQUIRED_CONTEXTS nor SKIP_RULESET_DRIFT_CHECK is set for it",
      not _offenders, "; ".join(_offenders))


print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
