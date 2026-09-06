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


import contextlib  # noqa: E402
import io  # noqa: E402


@contextlib.contextmanager
def env(**kv):
    """Set/unset environment variables for the duration of the block.

    A value of None removes the variable. Restoring by hand around every guard
    invocation is what made the older helpers here diverge -- run() managed
    REQUIRED_CONTEXTS and run_live() managed two variables, and PENDING_CONTEXTS
    (#3165) needs to be managed by BOTH or every pre-existing fixture inherits
    the real pending list and fails for the wrong reason.
    """
    old = {k: os.environ.get(k) for k in kv}
    try:
        for k, v in kv.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
        yield
    finally:
        for k, v in old.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v


def run_dir(wf_dir: str, required: str, pending: str = "") -> tuple[int, str]:
    """Run the guard against an existing workflows dir, offline, return (rc, output)."""
    err, out = io.StringIO(), io.StringIO()
    with env(REQUIRED_CONTEXTS=required, PENDING_CONTEXTS=pending,
             SKIP_RULESET_DRIFT_CHECK=None):
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
            rc = crc.main(["check_required_contexts.py", wf_dir])
    return rc, err.getvalue() + out.getvalue()


def run(files: dict[str, str], required: str, pending: str = "") -> tuple[int, str]:
    """Write fixture workflows to a temp dir, run the guard, return (rc, stderr).

    `pending` defaults to EMPTY, not to the shipped PENDING_REQUIRED_CONTEXTS:
    a fixture directory contains none of the real workflows, so inheriting the
    real pending list would fail every fixture with "produced by NO workflow".
    """
    with tempfile.TemporaryDirectory() as d:
        for fname, body in files.items():
            with open(os.path.join(d, fname), "w", encoding="utf-8") as fh:
                fh.write(body)
        return run_dir(d, required, pending)


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
    name: BC test matrix passed
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

rc, msg = run({"test-matrix.yml": DEFAULT_TYPES_CANCELLING}, "BC test matrix passed")
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
              "Tests updated,BC test matrix passed")
check("a mixed set fails on the bad one only", rc == 1, f"(rc={rc})")
check("...and does not accuse the safe one",
      "BC test matrix passed" not in msg.split("Fix:")[0], msg)

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
    name: BC test matrix passed
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


def run_live(fetch, files=None, pending=""):
    """Run the guard with NO REQUIRED_CONTEXTS override, so the live check applies."""
    files = SAFE_WORKFLOWS if files is None else files
    with tempfile.TemporaryDirectory() as d:
        for fname, body in files.items():
            with open(os.path.join(d, fname), "w", encoding="utf-8") as fh:
                fh.write(body)
        err, out = io.StringIO(), io.StringIO()
        with env(REQUIRED_CONTEXTS=None, SKIP_RULESET_DRIFT_CHECK=None,
                 PENDING_CONTEXTS=pending):
            with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
                rc = crc.main(["check_required_contexts.py", d], fetch=fetch)
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
rc, msg = run_live(lambda: rules_payload(["BC test matrix passed"]))
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
        fh.write('RULESET_CONTEXTS = ("BC test matrix passed",)\n')
    problems = crc.ruleset_drift_problems(
        fetch=lambda: rules_payload(crc.DEFAULT_REQUIRED_CONTEXTS), ci_wait_path=_fake)
check("a drifted tools/ci-wait.py list is reported as a problem",
      any("ci-wait" in p for p in problems), str(problems))


# ===========================================================================
# #3165 -- contexts the ruleset is ABOUT to require: PENDING_REQUIRED_CONTEXTS
# ===========================================================================
# The ruleset edit and the code change cannot land in the same instant. A
# maintainer edits the ruleset by hand; a PR merges through CI. So there is a
# window, and both halves of the guard have to be green on BOTH sides of it.
#
# Putting the new names straight into DEFAULT_REQUIRED_CONTEXTS (and into
# tools/ci-wait.py's RULESET_CONTEXTS) before the ruleset carries them is not
# just untidy, it is actively breaking: ci-wait.py treats RULESET_CONTEXTS as a
# FLOOR and returns exit 3 UNDETERMINED whenever the live ruleset is a subset of
# it (#3002), so every agent's CI wait in the repository would stop returning a
# verdict until the ruleset moved. That is asserted below, against the real
# tools/ci-wait.py.
#
# PENDING_REQUIRED_CONTEXTS is the seam. A name listed there is analysed exactly
# like a required one -- it must be produced by a pull_request workflow and must
# not be cancellable -- but the live-ruleset drift comparison tolerates it in
# EITHER state, present or absent. So the guard proves the new gating workflow is
# safe before the ruleset moves, and stays green the moment it does.

PENDING = getattr(crc, "PENDING_REQUIRED_CONTEXTS", None)

check("check_required_contexts.py declares PENDING_REQUIRED_CONTEXTS",
      isinstance(PENDING, list), repr(PENDING))
check("...as plain strings", isinstance(PENDING, list)
      and all(isinstance(p, str) and p for p in PENDING), repr(PENDING))
check("...disjoint from DEFAULT_REQUIRED_CONTEXTS (a name is one or the other)",
      isinstance(PENDING, list)
      and not (set(PENDING) & set(crc.DEFAULT_REQUIRED_CONTEXTS)),
      repr(PENDING))

# A check-run name may contain a comma -- one of the real ones does. Splitting on
# commas unconditionally turned it into two contexts, and the guard then reported
# both halves as "produced by NO workflow": a failure with a plausible-looking
# message and a cause nowhere near it.
_COMMA_NAME = "PR body closing references must be correct, both directions"
check("a context name containing a comma survives the newline-separated override",
      crc._split(f"BC test matrix passed\n{_COMMA_NAME}")
      == ["BC test matrix passed", _COMMA_NAME],
      str(crc._split(f"BC test matrix passed\n{_COMMA_NAME}")))
check("...while a single-line value still splits on commas",
      crc._split("BC test matrix passed, Tests updated")
      == ["BC test matrix passed", "Tests updated"],
      str(crc._split("BC test matrix passed, Tests updated")))

PENDING_CANCELLABLE = """
name: PR Check
on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, reopened, labeled, unlabeled, edited]
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
jobs:
  guard:
    name: Soon To Gate
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""

PENDING_SAFE = """
name: PR Gate
on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, reopened, labeled, unlabeled, edited]
jobs:
  guard:
    name: Soon To Gate
    runs-on: ubuntu-latest
    steps:
      - run: 'true'
"""

# A pending context is analysed, not merely declared. Without this the seam would
# be a way to add a name that nothing checks -- the opposite of the point.
rc, msg = run({"pr-check.yml": PENDING_CANCELLABLE, "require-tests.yml": SAFE_NO_CONCURRENCY},
              "Tests updated", pending="Soon To Gate")
check("a PENDING context that is cancellable fails the guard", rc == 1, f"(rc={rc}) {msg}")
check("...and names it", "Soon To Gate" in msg, msg)

rc, msg = run({"require-tests.yml": SAFE_NO_CONCURRENCY},
              "Tests updated", pending="Soon To Gate")
check("a PENDING context no workflow produces fails the guard", rc == 1, f"(rc={rc}) {msg}")
check("...and says so distinctly", "produced by NO workflow" in msg, msg)

rc, msg = run({"pr-gate.yml": PENDING_SAFE, "require-tests.yml": SAFE_NO_CONCURRENCY},
              "Tests updated", pending="Soon To Gate")
check("a PENDING context in a workflow that cannot cancel it passes",
      rc == 0, f"(rc={rc}) {msg}")

# The drift comparison, both sides of the window. This is the pair that decides
# whether the PR is mergeable before the ruleset edit AND after it.
_PENDING_WF = dict(SAFE_WORKFLOWS)
_PENDING_WF["pr-gate.yml"] = PENDING_SAFE

rc, msg = run_live(lambda: rules_payload(crc.DEFAULT_REQUIRED_CONTEXTS),
                   files=_PENDING_WF, pending="Soon To Gate")
check("a live ruleset that does NOT yet require a PENDING context passes",
      rc == 0, f"(rc={rc}) {msg}")

rc, msg = run_live(lambda: rules_payload(
                       list(crc.DEFAULT_REQUIRED_CONTEXTS) + ["Soon To Gate"]),
                   files=_PENDING_WF, pending="Soon To Gate")
check("a live ruleset that HAS started requiring a PENDING context also passes",
      rc == 0, f"(rc={rc}) {msg}")
check("...and says the name can now be promoted out of the pending list",
      "Soon To Gate" in msg and "PENDING_REQUIRED_CONTEXTS" in msg, msg)

# The tolerance is scoped to names actually listed as pending. An unrecognised
# context appearing in the ruleset is still drift, or the seam would swallow
# exactly the #2785 case it sits next to.
rc, msg = run_live(lambda: rules_payload(
                       list(crc.DEFAULT_REQUIRED_CONTEXTS) + ["Provenance attested"]),
                   files=_PENDING_WF, pending="Soon To Gate")
check("an added context that is NOT pending still fails the guard",
      rc == 1, f"(rc={rc}) {msg}")

# tools/ci-wait.py must NOT have been "helpfully" updated ahead of the ruleset.
_cw = crc.load_ci_wait()
check("tools/ci-wait.py's RULESET_CONTEXTS carries no PENDING name yet",
      isinstance(PENDING, list) and not (set(_cw.RULESET_CONTEXTS) & set(PENDING)),
      f"{_cw.RULESET_CONTEXTS} vs pending {PENDING}")

# ...and the reason, measured rather than asserted: a floor wider than the live
# ruleset is what makes ci-wait.py stop answering.
if isinstance(PENDING, list) and PENDING:
    _live_now = list(crc.DEFAULT_REQUIRED_CONTEXTS)
    _verdict = _cw.classify(
        [{"name": c, "status": "completed", "conclusion": "success"} for c in _live_now],
        contexts=tuple(_live_now) + (PENDING[0],))
    check("a ci-wait floor NARROWER than the judged set is fine (control)",
          _verdict.code != 3, f"code={_verdict.code} {_verdict.lines}")
    _verdict2 = _cw.classify(
        [{"name": c, "status": "completed", "conclusion": "success"} for c in _live_now],
        contexts=tuple(_live_now[:1]))
    check("...while a judged set narrower than the floor is exit 3 UNDETERMINED "
          "-- which is what adding a pending name to RULESET_CONTEXTS early would do",
          _verdict2.code == 3, f"code={_verdict2.code} {_verdict2.lines}")


# ===========================================================================
# The SHIPPED workflows must satisfy the guard for DEFAULT + PENDING
# ===========================================================================
# Every check above drives synthetic fixtures. This one drives the real
# .github/workflows directory, so "the gating workflow cannot leave a required
# context cancelled" is a claim about what actually ships, not about a fixture
# that resembles it. Offline: the context list is supplied, so no network call.

_REPO = os.path.dirname(os.path.dirname(HERE))
_WF_DIR = os.path.join(_REPO, ".github", "workflows")

_rc, _msg = run_dir(_WF_DIR,
                    "\n".join(crc.DEFAULT_REQUIRED_CONTEXTS),
                    "\n".join(PENDING if isinstance(PENDING, list) else []))
check("the shipped workflows produce every required and pending context, "
      "and none of them can be left cancelled", _rc == 0, f"(rc={_rc}) {_msg}")

# Two workflows producing the SAME check name is not a syntax error and nothing
# else catches it: the ruleset reads the newest check run with that name, so
# which of the two decides the merge depends on which finished last. Moving a job
# between workflow files by copying rather than moving produces exactly this.
_produced: dict[str, list[str]] = {}
for _f in sorted(os.listdir(_WF_DIR)):
    if not _f.endswith((".yml", ".yaml")):
        continue
    _doc = crc.load(os.path.join(_WF_DIR, _f))
    if not crc.pull_request_types(_doc):
        continue
    for _ctx in crc.contexts_of(_doc, _WF_DIR):
        _produced.setdefault(_ctx, []).append(_f)
_dupes = {c: fs for c, fs in _produced.items() if len(fs) > 1}
check("no check-run name is produced by two pull_request workflows at once",
      not _dupes, str(_dupes))


# ===========================================================================
# Neither bypass may be switched on where it would silently disarm the guard
# ===========================================================================
# SKIP_RULESET_DRIFT_CHECK=1 at least prints when it stands the live comparison
# down, and it is now used ON PURPOSE: the gating invocation runs offline so a
# required context can never go red because api.github.com was unreachable, while
# a separate advisory invocation does the live comparison with no bypass at all.
#
# REQUIRED_CONTEXTS (and, since #3165, PENDING_CONTEXTS) are different: main()
# branches on REQUIRED_CONTEXTS, never calls resolve_contexts(), and so skips
# BOTH the live comparison and the tools/ci-wait.py cross-check. Either one set
# in a workflow turns the guard into a green no-op, which is exactly what an
# agent unbreaking CI during a GitHub API outage would reach for.
#
# So, across EVERY shipped workflow rather than one named file -- the earlier
# version of this block read pr-check.yml only, and moving the job to another
# file would have made it assert nothing:
#
#   * no invocation anywhere may set REQUIRED_CONTEXTS or PENDING_CONTEXTS;
#   * at least one invocation must set NO bypass at all, or nothing performs the
#     live drift comparison and #2785 is back.
import re  # noqa: E402

HARD_BYPASSES = ("REQUIRED_CONTEXTS", "PENDING_CONTEXTS")
SOFT_BYPASS = "SKIP_RULESET_DRIFT_CHECK"

# Matches the GUARD invocation only. A bare "check_required_contexts.py" substring
# also matches the step that runs test_check_required_contexts.py, which would
# make the offender list double-count and the vacuity check pass on the test step
# alone.
_GUARD_RUN = re.compile(r"(?<!test_)check_required_contexts\.py")

_offenders: list[str] = []
_invocations = 0
_unbypassed = 0
for _f in sorted(os.listdir(_WF_DIR)):
    if not _f.endswith((".yml", ".yaml")):
        continue
    _wf = crc.load(os.path.join(_WF_DIR, _f))
    _wf_env = _wf.get("env") or {}
    for _job_name, _job in (_wf.get("jobs") or {}).items():
        if not isinstance(_job, dict):
            continue
        _job_env = _job.get("env") or {}
        for _step in _job.get("steps") or []:
            if not _GUARD_RUN.search(_step.get("run") or ""):
                continue
            _invocations += 1
            _seen = dict(_wf_env)
            _seen.update(_job_env)
            _seen.update(_step.get("env") or {})
            for _var in HARD_BYPASSES:
                if _var in _seen:
                    _offenders.append(f"{_f}:{_job_name} sets {_var}")
            if SOFT_BYPASS not in _seen:
                _unbypassed += 1

check("a shipped workflow actually runs check_required_contexts.py "
      "(or the assertions below are vacuous)", _invocations > 0,
      f"{_invocations} invocation(s)")
check("...and none of them sets REQUIRED_CONTEXTS or PENDING_CONTEXTS",
      not _offenders, "; ".join(_offenders))
check("...and at least one runs the LIVE drift comparison with no bypass",
      _unbypassed > 0, f"{_unbypassed} of {_invocations} invocation(s) unbypassed")

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
