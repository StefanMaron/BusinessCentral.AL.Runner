#!/usr/bin/env python3
"""Unit tests for tools/ci-wait.py's verdict logic.

ci-wait.py is the tool every agent uses to decide a PR is ready, so its verdict
is worth proving against synthetic check-run payloads rather than only against a
live PR. The payloads below are the shape
`GET /repos/{o}/{r}/commits/{sha}/check-runs` returns.

Run: python3 tools/test_ci_wait.py
"""
from __future__ import annotations

import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("ci_wait", os.path.join(HERE, "ci-wait.py"))
cw = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cw)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


_next_id = [1000]


def run(name: str, conclusion: str | None, status: str = "completed", cid: int | None = None):
    if cid is None:
        _next_id[0] += 1
        cid = _next_id[0]
    return {"name": name, "status": status, "conclusion": conclusion, "id": cid,
            "details_url": f"https://x/actions/runs/1/job/{cid}"}


LEGS = ["bc-tests / BC 27.0 (required)", "bc-tests / BC 28.3 (required)"]


def green_set(**kw):
    runs = [run(n, "success") for n in LEGS]
    runs.append(run("All BC versions passed", "success"))
    runs.append(run("Tests updated", "success"))
    runs.append(run("scripts/ unit tests", "success"))
    return runs


print("ci-wait.py verdict logic")

# --- the plain green case must stay green, or a fix that flags everything wins
v = cw.classify(green_set())
check("an all-success rollup is GREEN", v.code == 0, f"(code={v.code}) {v.lines}")
check("...and says nothing about cancellations",
      not any("cancel" in l.lower() for l in v.lines), v.lines)

# --- a real failure still outranks everything, and still gets its log fetched
runs = green_set()
runs[0] = run(LEGS[0], "failure")
v = cw.classify(runs)
check("a failing required leg is FAILED", v.code == 1, f"(code={v.code})")
check("...and the failing run is offered for the log fetch",
      v.log_target is not None and v.log_target["name"] == LEGS[0], str(v.log_target))

# --- #2726: a cancelled REQUIRED context, newest for its name, blocks the merge
runs = green_set()
runs.append(run("Tests updated", "cancelled", cid=9999))
v = cw.classify(runs)
check("a cancelled required context is NOT reported green", v.code != 0, f"(code={v.code})")
check("...and gets its own verdict code, distinct from failure and timeout",
      v.code == 4, f"(code={v.code})")
check("...and names the context", any("Tests updated" in l for l in v.lines), v.lines)
check("...and does not claim anything failed",
      not any("FAILED on" in l for l in v.lines), v.lines)

# --- the other direction: a cancelled entry SUPERSEDED by a later success is
# --- cosmetic, and must not be reported as a block
runs = green_set()
runs.append(run("Tests updated", "cancelled", cid=500))  # older than the success above
v = cw.classify(runs)
check("a cancelled entry superseded by a newer success stays GREEN",
      v.code == 0, f"(code={v.code}) {v.lines}")
check("...but is still mentioned, so the grey entries are explained",
      any("cancelled" in l.lower() for l in v.lines), v.lines)

# --- a cancelled NON-required context does not block a merge, so it must not
# --- produce a blocking verdict either
runs = green_set()
runs.append(run("scripts/ unit tests", "cancelled", cid=9999))
v = cw.classify(runs)
check("a cancelled non-required context stays GREEN", v.code == 0, f"(code={v.code}) {v.lines}")
check("...and is named as cosmetic",
      any("scripts/ unit tests" in l for l in v.lines), v.lines)

# --- a cancelled required LEG is a required context too
runs = green_set()
runs.append(run(LEGS[1], "cancelled", cid=9999))
v = cw.classify(runs)
check("a cancelled '(required)' matrix leg blocks", v.code == 4, f"(code={v.code})")

# --- nothing is decided while checks are still running
runs = green_set()
runs[0] = run(LEGS[0], None, status="in_progress")
v = cw.classify(runs)
check("an in-flight leg is still pending, not a verdict", v.code is None, f"(code={v.code})")

runs = green_set()
runs[0] = run(LEGS[0], None, status="in_progress")
runs.append(run("Tests updated", "cancelled", cid=9999))
v = cw.classify(runs)
check("a cancellation mid-run is not called a block yet", v.code is None, f"(code={v.code})")

# --- the required contexts that are NOT named '(required)' must be waited on
# --- and judged: reporting GREEN while 'Tests updated' is red was possible
runs = [run(n, "success") for n in LEGS]
runs.append(run("All BC versions passed", "success"))
runs.append(run("Tests updated", "failure"))
v = cw.classify(runs)
check("a failing 'Tests updated' is not reported green", v.code == 1, f"(code={v.code})")

runs = [run(n, "success") for n in LEGS]
runs.append(run("All BC versions passed", None, status="queued"))
v = cw.classify(runs)
check("a queued 'All BC versions passed' keeps the verdict pending",
      v.code is None, f"(code={v.code})")

# --- a required context that never reports at all must not hang the tool: a
# --- docs-only PR skips 'Tests updated' entirely
runs = [run(n, "success") for n in LEGS]
runs.append(run("All BC versions passed", "success"))
v = cw.classify(runs)
check("a required context absent from the rollup does not block the verdict",
      v.code == 0, f"(code={v.code}) {v.lines}")

# --- neutral/skipped are not failures
runs = [run(n, "success") for n in LEGS]
runs.append(run("All BC versions passed", "success"))
runs.append(run("Tests updated", "skipped"))
v = cw.classify(runs)
check("a skipped required context is not a failure", v.code == 0, f"(code={v.code}) {v.lines}")

# --- real data, PR #2740 head 6b95477f: 'Tests updated' failed, then a
# --- no-tests-needed label produced a newer 'skipped'. GitHub read the newer
# --- one and the PR merged. Scanning every entry instead of the newest per
# --- name reported that merged PR as FAILED.
runs = [run(n, "success") for n in LEGS]
runs.append(run("All BC versions passed", "success"))
runs.append(run("Tests updated", "failure", cid=101297899468))
runs.append(run("Tests updated", "skipped", cid=101297995090))
v = cw.classify(runs)
check("a failure superseded by a newer skipped run is GREEN, not FAILED",
      v.code == 0, f"(code={v.code}) {v.lines}")

# ...and the reverse ordering is still a failure, so this is not just ignoring
# every failure that shares a name with something.
runs = [run(n, "success") for n in LEGS]
runs.append(run("All BC versions passed", "success"))
runs.append(run("Tests updated", "skipped", cid=101297899468))
runs.append(run("Tests updated", "failure", cid=101297995090))
v = cw.classify(runs)
check("...but a failure that IS the newest entry still fails", v.code == 1, f"(code={v.code})")

# --- an older cancelled entry must not hold the pool 'incomplete' forever
runs = [run(n, "success") for n in LEGS]
runs.append(run("All BC versions passed", "success"))
runs.append(run("Tests updated", "cancelled", cid=10))
runs.append(run("Tests updated", "success", cid=20))
v = cw.classify(runs)
check("an older cancelled entry does not stall the verdict", v.code == 0, f"(code={v.code})")

# --- an empty rollup is not a verdict
v = cw.classify([])
check("an empty rollup is pending, not green", v.code is None, f"(code={v.code})")

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
