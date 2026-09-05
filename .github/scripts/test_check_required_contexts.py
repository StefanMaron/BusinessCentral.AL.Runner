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

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
