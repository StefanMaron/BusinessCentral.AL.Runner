#!/usr/bin/env python3
"""Unit tests for tools/armed-prs.py's classification and exit code.

The defect this tool exists for is a PR armed on a green verdict that goes red
afterwards and sits there (#4006): measured windows of 12 and 119 minutes on a
night with an attentive coordinator and nineteen PRs armed at once. So the
property under test is not "does it call gh" but **which of the five per-PR
verdicts each `ci-wait.py` exit code maps to, and what the run-level exit code
is for each mix of them**.

Two of those mappings carry the whole value of the tool and are the ones a
plausible-looking rewrite gets wrong:

  * exit 2 (checks still running) is the ORDINARY state of an armed PR and must
    be QUIET. A tool that reports it reports twenty-one PRs on a busy night and
    is then ignored, which is indistinguishable from not existing.
  * exit 3 (could not read the verdict) is NOT quiet. Folding an unreadable
    verdict into the green set is precisely the defect
    `guards-need-a-third-state.md` exists to prevent -- it asserts a PR is fine
    having failed to measure it.

`ci_wait_rc` is injected rather than mocked at the subprocess layer, because the
thing worth pinning is the mapping, not gh's argv.

Run: python3 tools/test_armed_prs.py
"""
from __future__ import annotations

import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "armed_prs", os.path.join(HERE, "armed-prs.py"))
ap = importlib.util.module_from_spec(_spec)
sys.modules["armed_prs"] = ap
_spec.loader.exec_module(ap)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def pr(number: int, armed: bool = True, enabled_at: str | None = None) -> dict:
    """A `gh pr list --json number,autoMergeRequest,headRefOid` element."""
    return {
        "number": number,
        "headRefOid": f"{number:040x}",
        "autoMergeRequest": (
            {"enabledAt": enabled_at or "2026-09-13T04:00:00Z",
             "mergeMethod": "SQUASH"} if armed else None),
    }


# --------------------------------------------------------------------------
# armed(): which PRs are armed at all
# --------------------------------------------------------------------------
print("armed():")

check("armed selects autoMergeRequest != null",
      [p["number"] for p in ap.armed([pr(1), pr(2, armed=False), pr(3)])]
      == [1, 3])

check("armed on an empty list is empty", ap.armed([]) == [])

check("armed ignores a PR whose autoMergeRequest key is absent entirely",
      ap.armed([{"number": 9, "headRefOid": "a" * 40}]) == [])


# --------------------------------------------------------------------------
# classify(): ci-wait.py's exit code -> this tool's verdict
# --------------------------------------------------------------------------
print("classify():")

# The two quiet ones. Exit 2 being quiet is the whole reason this tool is
# usable on a night with 21 armed PRs.
check("rc 0 (green) is OK", ap.classify(0) == ap.OK)
check("rc 2 (still running) is OK -- the ordinary armed state",
      ap.classify(2) == ap.OK)

# The reported ones.
check("rc 1 (a required check failed) is FAILING", ap.classify(1) == ap.FAILING)
check("rc 4 (blocked, not failing) is FAILING", ap.classify(4) == ap.FAILING)

# The third state.
check("rc 3 (could not determine) is UNREADABLE",
      ap.classify(3) == ap.UNREADABLE)
check("an unmapped rc is UNREADABLE, never OK",
      ap.classify(99) == ap.UNREADABLE)
check("a negative rc (killed by a signal) is UNREADABLE, never OK",
      ap.classify(-9) == ap.UNREADABLE)

check("UNREADABLE is a distinct value from OK and FAILING",
      len({ap.OK, ap.FAILING, ap.UNREADABLE}) == 3)


# --------------------------------------------------------------------------
# sweep(): the run-level verdict over a set of armed PRs
# --------------------------------------------------------------------------
print("sweep():")


def sweep(rcs: dict[int, int], prs: list[dict] | None = None):
    """Run a sweep with ci-wait's exit code injected per PR number."""
    prs = prs if prs is not None else [pr(n) for n in sorted(rcs)]
    return ap.sweep(prs, ci_wait_rc=lambda n, **kw: rcs[n])


rc, rows = sweep({1: 0, 2: 2})
check("all green-or-running exits 0", rc == 0)
check("...and reports no rows", rows == [])

rc, rows = sweep({1: 0, 2: 1, 3: 2})
check("one failing among green exits 1", rc == 1)
check("...and reports exactly the failing PR",
      [(r.number, r.verdict) for r in rows] == [(2, ap.FAILING)])

rc, rows = sweep({1: 0, 2: 3})
check("one unreadable among green exits 3 -- NOT 0", rc == 3)
check("...and reports it as UNREADABLE, not as failing",
      [(r.number, r.verdict) for r in rows] == [(2, ap.UNREADABLE)])

# The precedence question: a run carrying both must not let the unreadable
# verdict hide the failing one, nor the reverse. Exit 1 is the actionable
# verdict, so it wins -- but BOTH rows are still reported, which is the half
# that matters. An exit code carrying only one of them is still a report that
# names both.
rc, rows = sweep({1: 1, 2: 3})
check("failing + unreadable exits 1 (the actionable verdict wins)", rc == 1)
check("...and BOTH are reported, so neither is lost to the exit code",
      sorted((r.number, r.verdict) for r in rows)
      == [(1, ap.FAILING), (2, ap.UNREADABLE)])

rc, rows = sweep({}, prs=[pr(1, armed=False)])
check("no armed PRs at all exits 0 -- a legitimate absence, not a refusal",
      rc == 0 and rows == [])

# ci-wait.py is asked ONLY about armed PRs. Asking about every open PR would
# spend 35 subprocesses to answer a question about 21 (measured on the live
# queue, 2026-09-13).
asked: list[int] = []
ap.sweep([pr(1), pr(2, armed=False), pr(3)],
         ci_wait_rc=lambda n, **kw: asked.append(n) or 0)
check("an unarmed PR is never handed to ci-wait.py", asked == [1, 3])


# --------------------------------------------------------------------------
# A crashing ci_wait_rc is UNREADABLE, not a crashed sweep
# --------------------------------------------------------------------------
print("robustness:")


def boom(n, **kw):
    if n == 2:
        raise OSError("gh not found")
    return 0


rc, rows = ap.sweep([pr(1), pr(2), pr(3)], ci_wait_rc=boom)
check("a raising ci_wait_rc yields UNREADABLE for that PR only",
      [(r.number, r.verdict) for r in rows] == [(2, ap.UNREADABLE)])
check("...and the sweep still exits 3 rather than dying", rc == 3)
check("...and the other PRs were still swept",
      rc == 3 and len(rows) == 1)


# --------------------------------------------------------------------------
# The report itself must name the distinction, not merely carry it
# --------------------------------------------------------------------------
print("report():")

_, rows = sweep({1: 1, 2: 3})
text = ap.report(rows)
check("the report names the failing PR number", "#1" in text)
check("the report names the unreadable PR number", "#2" in text)
check("the report distinguishes the two verdicts in its TEXT",
      "FAILING" in text and "UNREADABLE" in text)
check("the unreadable row says the verdict was not read, not that it is fine",
      "could not" in text.lower() or "unreadable" in text.lower())

check("an empty report is empty rather than a misleading all-clear string",
      ap.report([]) == "")


# --------------------------------------------------------------------------
# armed_for(): the age an armed PR has carried its arming, for the report
# --------------------------------------------------------------------------
print("armed_for():")

check("a 119-minute arming reports in hours-and-minutes",
      ap.armed_for("2026-09-13T02:22:00Z",
                   now="2026-09-13T04:21:00Z") == "1h59m")
check("a 12-minute arming reports in minutes",
      ap.armed_for("2026-09-13T04:14:00Z",
                   now="2026-09-13T04:26:00Z") == "12m")
check("an absent enabledAt is 'unknown', never 0m",
      ap.armed_for(None, now="2026-09-13T04:26:00Z") == "unknown")
check("an unparseable enabledAt is 'unknown', never a crash",
      ap.armed_for("not-a-date", now="2026-09-13T04:26:00Z") == "unknown")


print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)}")
    for f in FAILURES:
        print(f"  - {f}")
    sys.exit(1)
print("all armed-prs tests passed")
