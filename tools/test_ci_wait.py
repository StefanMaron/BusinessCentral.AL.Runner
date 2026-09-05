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

# --- #2807: a required context ABSENT from the rollup is not evidence of
# --- anything. This block used to assert `v.code == 0` for exactly this input,
# --- which is the defect #2807 reports written down as the expected answer:
# --- "has not appeared yet" and "will never appear" are indistinguishable from
# --- the rollup alone, and the tie was broken toward GREEN. The rollup alone can
# --- no longer decide it -- the workflow-run list for the commit is what says
# --- whether anything is still coming.
runs = [run(n, "success") for n in LEGS]
runs.append(run("All BC versions passed", "success"))
v = cw.classify(runs)
check("a required context absent from the rollup is pending, not green, "
      "when nothing says whether it is still coming",
      v.code is None, f"(code={v.code}) {v.lines}")

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


# ===========================================================================
# #2807 -- GREEN reported while the required contexts had not registered yet
# ===========================================================================
# Real shape, PR #2793 head 50551f4c, measured seconds after the push: the only
# check run on the commit was "Tests updated" (8s), every bc-tests leg was still
# pending and "All BC versions passed" was not in the rollup at all. classify()
# built a pool of ONE, found it complete and clean, and returned GREEN saying
# "all 1 required checks passed".
JUST_PUSHED = [run("Tests updated", "success")]

QUEUED_RUNS = [{"id": 33964656436, "name": "Test Matrix", "status": "queued",
                "conclusion": None},
               {"id": 33964852712, "name": "PR Check", "status": "completed",
                "conclusion": "success"}]
ALL_DONE_RUNS = [{"id": 33964656436, "name": "Test Matrix", "status": "completed",
                  "conclusion": "success"},
                 {"id": 33964852712, "name": "PR Check", "status": "completed",
                  "conclusion": "success"}]

v = cw.classify(JUST_PUSHED, workflow_runs=QUEUED_RUNS)
check("a rollup missing 'All BC versions passed' while its run is queued is NOT green",
      v.code is None, f"(code={v.code}) {v.lines}")

v = cw.classify(JUST_PUSHED, workflow_runs=[])
check("...nor when no workflow run has even registered for the commit yet",
      v.code is None, f"(code={v.code}) {v.lines}")

v = cw.classify(JUST_PUSHED, workflow_runs=None)
check("...nor when the workflow-run list could not be read at all "
      "(unknown resolves toward 'call again', never toward green)",
      v.code is None, f"(code={v.code}) {v.lines}")

# The escape hatch the old behaviour was bought with -- a context that will never
# report must not hang the tool forever -- is kept, but it resolves to BLOCKED
# rather than GREEN: a required context with no report on the head commit is
# precisely "everything passed and the merge is refused with nothing saying why".
v = cw.classify(JUST_PUSHED, workflow_runs=ALL_DONE_RUNS)
check("a required context still absent once every workflow run completed is BLOCKED",
      v.code == 4, f"(code={v.code}) {v.lines}")
check("...and names the context that never reported",
      any("All BC versions passed" in l for l in v.lines), v.lines)
check("...and does not claim anything failed",
      not any("failed" in l.lower() for l in v.lines), v.lines)

# ...and the ordinary green case is unaffected once both contexts are present.
v = cw.classify(green_set(), workflow_runs=ALL_DONE_RUNS)
check("a complete rollup with both ruleset contexts is still GREEN",
      v.code == 0, f"(code={v.code}) {v.lines}")
check("...and says which ruleset contexts it actually confirmed",
      any("All BC versions passed" in l and "Tests updated" in l for l in v.lines), v.lines)


# ===========================================================================
# #2785 -- a context added to the live ruleset must be waited for
# ===========================================================================
# The verdict has to be driven by the contexts the ruleset requires RIGHT NOW,
# not by a tuple frozen into the module. Passing a third context in must change
# the answer, or the live lookup is decorative.
v = cw.classify(green_set(), contexts=("All BC versions passed", "Tests updated",
                                       "Provenance attested"),
                workflow_runs=QUEUED_RUNS)
check("a context newly added to the ruleset keeps the verdict pending",
      v.code is None, f"(code={v.code}) {v.lines}")

v = cw.classify(green_set(), contexts=("All BC versions passed", "Tests updated"),
                workflow_runs=QUEUED_RUNS)
check("...while the same rollup with the known context set is green",
      v.code == 0, f"(code={v.code}) {v.lines}")

# Real payload from GET /repos/{o}/{r}/rules/branches/main, 2026-09-05. That
# endpoint returns the EFFECTIVE rules, so only ACTIVE rulesets appear -- there
# is no ruleset id to get wrong, which is the point of using it.
BRANCH_RULES = [
    {"type": "deletion", "ruleset_source_type": "Repository", "ruleset_id": 15001420},
    {"type": "non_fast_forward", "ruleset_source_type": "Repository", "ruleset_id": 15001420},
    {"type": "pull_request", "parameters": {}, "ruleset_id": 15001420},
    {"type": "required_status_checks",
     "parameters": {"strict_required_status_checks_policy": False,
                    "do_not_enforce_on_create": False,
                    "required_status_checks": [{"context": "All BC versions passed"},
                                               {"context": "Tests updated"}]},
     "ruleset_source_type": "Repository",
     "ruleset_source": "StefanMaron/BusinessCentral.AL.Runner",
     "ruleset_id": 15001420},
]
check("the required contexts are read out of the live branch-rules payload",
      cw.contexts_from_branch_rules(BRANCH_RULES)
      == ("All BC versions passed", "Tests updated"),
      str(cw.contexts_from_branch_rules(BRANCH_RULES)))

# The trap from #2785: ruleset 15039643 is disabled and carries no
# required_status_checks rule, so querying it answers with an empty list. An
# empty answer must read as UNKNOWN, never as "nothing is required" -- the latter
# would silently disable every gate this module exists to apply.
check("a payload with no required_status_checks rule is UNKNOWN, not 'nothing required'",
      cw.contexts_from_branch_rules(
          [{"type": "pull_request", "parameters": {}, "ruleset_id": 15039643}]) is None)
check("...and so is an empty context list",
      cw.contexts_from_branch_rules(
          [{"type": "required_status_checks",
            "parameters": {"required_status_checks": []}}]) is None)
check("...and so is a payload that is not a list at all",
      cw.contexts_from_branch_rules({"message": "Not Found"}) is None)


# ===========================================================================
# #2748 -- several runs of one workflow on one SHA
# ===========================================================================
# A check run's id is allocated when its JOB STARTS, not when its workflow run is
# created, so ordering by check-run id alone does not order by run recency.
# Measured on PR #2742 head 22e5c13b: Test Matrix run 33964656436 (created
# 11:56:25) owns check run 101303131614, a HIGHER id than every check run of PR
# Check run 33964852712 (created 12:00:55, max 101303055107).
#
# Two runs of ONE workflow overlapping on one SHA is the designed behaviour for
# require-tests.yml, which produces the required "Tests updated" context: it
# carries no `concurrency` block at all (deliberately, #2726) and triggers on
# 'labeled'/'unlabeled'.
OLD_RUN, NEW_RUN = 33964786612, 33964852712


def cr(name, conclusion, wf_run, check_id, status="completed"):
    return {"name": name, "status": status, "conclusion": conclusion, "id": check_id,
            "details_url": "https://github.com/StefanMaron/BusinessCentral.AL.Runner"
                           f"/actions/runs/{wf_run}/job/{check_id}"}


def two_run_set(old_conclusion, new_conclusion):
    runs = [cr(n, "success", NEW_RUN, 101303055000 + i) for i, n in enumerate(LEGS)]
    runs.append(cr("All BC versions passed", "success", NEW_RUN, 101303055107))
    # the OLDER run's job started later, so it carries the HIGHER check-run id
    runs.append(cr("Tests updated", old_conclusion, OLD_RUN, 101303131614))
    runs.append(cr("Tests updated", new_conclusion, NEW_RUN, 101303055037))
    return runs


v = cw.classify(two_run_set("failure", "success"), workflow_runs=ALL_DONE_RUNS)
check("a failure from an OLDER workflow run does not outrank the newer run's "
      "success, even holding the higher check-run id",
      v.code == 0, f"(code={v.code}) {v.lines}")

# ...and the mirror, so this is not just 'prefer success'.
v = cw.classify(two_run_set("success", "failure"), workflow_runs=ALL_DONE_RUNS)
check("...and the NEWER run's failure still fails, though its check-run id is lower",
      v.code == 1, f"(code={v.code}) {v.lines}")
check("...and the failing entry names the workflow run it came from",
      any(str(NEW_RUN) in l for l in v.lines), v.lines)

check("the workflow run id is read out of a details_url",
      cw.run_id_from("https://github.com/o/r/actions/runs/33964852712/job/101303055037")
      == 33964852712)
check("...and a details_url with no run segment reads as unknown, not as 0",
      cw.run_id_from("https://example.invalid/nothing") is None)

# Within ONE workflow run a re-run attempt still wins on check-run id.
runs = [cr(n, "success", NEW_RUN, 101303055000 + i) for i, n in enumerate(LEGS)]
runs.append(cr("All BC versions passed", "success", NEW_RUN, 101303055107))
runs.append(cr("Tests updated", "failure", NEW_RUN, 101303055037))
runs.append(cr("Tests updated", "success", NEW_RUN, 101303099999))
v = cw.classify(runs, workflow_runs=ALL_DONE_RUNS)
check("inside one workflow run the newer check-run id still wins",
      v.code == 0, f"(code={v.code}) {v.lines}")


# ===========================================================================
# The failing list is PARTIAL until every required check has reported
# ===========================================================================
# A coordinator read "1 of 9 required checks failed" as "only BC 27.0 is
# affected" and started a version-specific diagnosis; `gh pr checks` after the
# run finished showed eight legs failing. The tool was not wrong -- it says what
# it knows -- so the wording has to say that it is not the whole list yet.
runs = [cr(LEGS[0], "failure", NEW_RUN, 101303055001),
        cr(LEGS[1], None, NEW_RUN, 101303055002, status="in_progress"),
        cr("All BC versions passed", None, NEW_RUN, 101303055107, status="queued"),
        cr("Tests updated", "success", NEW_RUN, 101303055037)]
v = cw.classify(runs, workflow_runs=QUEUED_RUNS)
check("a failure while others are still running is still a FAILED verdict",
      v.code == 1, f"(code={v.code}) {v.lines}")
check("...and says how many required checks have not reported yet",
      any("not reported" in l.lower() for l in v.lines), v.lines)
check("...and warns the failing list can still grow",
      any("grow" in l.lower() for l in v.lines), v.lines)

# When everything HAS reported the caveat must not be printed, or it is noise.
runs = [cr(LEGS[0], "failure", NEW_RUN, 101303055001),
        cr(LEGS[1], "success", NEW_RUN, 101303055002),
        cr("All BC versions passed", "failure", NEW_RUN, 101303055107),
        cr("Tests updated", "success", NEW_RUN, 101303055037)]
v = cw.classify(runs, workflow_runs=ALL_DONE_RUNS)
check("a complete rollup's failing list carries no 'still to come' caveat",
      v.code == 1 and not any("grow" in l.lower() for l in v.lines),
      f"(code={v.code}) {v.lines}")

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
