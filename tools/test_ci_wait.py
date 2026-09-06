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
    runs.append(run("BC test matrix passed", "success"))
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
runs.append(run("BC test matrix passed", "success"))
runs.append(run("Tests updated", "failure"))
v = cw.classify(runs)
check("a failing 'Tests updated' is not reported green", v.code == 1, f"(code={v.code})")

runs = [run(n, "success") for n in LEGS]
runs.append(run("BC test matrix passed", None, status="queued"))
v = cw.classify(runs)
check("a queued 'BC test matrix passed' keeps the verdict pending",
      v.code is None, f"(code={v.code})")

# --- #2807: a required context ABSENT from the rollup is not evidence of
# --- anything. This block used to assert `v.code == 0` for exactly this input,
# --- which is the defect #2807 reports written down as the expected answer:
# --- "has not appeared yet" and "will never appear" are indistinguishable from
# --- the rollup alone, and the tie was broken toward GREEN. The rollup alone can
# --- no longer decide it -- the workflow-run list for the commit is what says
# --- whether anything is still coming.
runs = [run(n, "success") for n in LEGS]
runs.append(run("BC test matrix passed", "success"))
v = cw.classify(runs)
check("a required context absent from the rollup is pending, not green, "
      "when nothing says whether it is still coming",
      v.code is None, f"(code={v.code}) {v.lines}")

# --- neutral/skipped are not failures, and `skipped` SATISFIES a required
# --- context rather than leaving it unreported. Measured, because a proposal to
# --- treat `skipped` as "no verdict yet" would break the documented
# --- 'docs-only' / 'no-tests-needed' bypass: four merged PRs carry
# --- 'Tests updated' = skipped on their head SHA with 'BC test matrix passed'
# --- = success -- #2759 (451c757b), #2749 (8717aec3), #2717 (dbd3a1a2) and
# --- #2668 (3d1e9792). GitHub's ruleset accepted every one of them.
runs = [run(n, "success") for n in LEGS]
runs.append(run("BC test matrix passed", "success"))
runs.append(run("Tests updated", "skipped"))
v = cw.classify(runs)
check("a skipped required context is not a failure", v.code == 0, f"(code={v.code}) {v.lines}")
# A DISTINCT claim, not `v.code == 0` a second time: `skipped` must count as a
# check that HAS reported. If it were treated as unreported, the verdict would
# still be 0 here (nothing failed) while the progress line under-counted the
# completed pool -- green for the wrong reason, and invisible to a code check.
check("...and a skipped required context counts as reported, not as still-pending",
      v.progress.startswith("4/4 complete") and "not in the rollup" not in v.progress,
      f"progress={v.progress!r}")

# --- real data, PR #2740 head 6b95477f: 'Tests updated' failed, then a
# --- no-tests-needed label produced a newer 'skipped'. GitHub read the newer
# --- one and the PR merged. Scanning every entry instead of the newest per
# --- name reported that merged PR as FAILED.
runs = [run(n, "success") for n in LEGS]
runs.append(run("BC test matrix passed", "success"))
runs.append(run("Tests updated", "failure", cid=101297899468))
runs.append(run("Tests updated", "skipped", cid=101297995090))
v = cw.classify(runs)
check("a failure superseded by a newer skipped run is GREEN, not FAILED",
      v.code == 0, f"(code={v.code}) {v.lines}")

# ...and the reverse ordering is still a failure, so this is not just ignoring
# every failure that shares a name with something.
runs = [run(n, "success") for n in LEGS]
runs.append(run("BC test matrix passed", "success"))
runs.append(run("Tests updated", "skipped", cid=101297899468))
runs.append(run("Tests updated", "failure", cid=101297995090))
v = cw.classify(runs)
check("...but a failure that IS the newest entry still fails", v.code == 1, f"(code={v.code})")

# --- an older cancelled entry must not hold the pool 'incomplete' forever
runs = [run(n, "success") for n in LEGS]
runs.append(run("BC test matrix passed", "success"))
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
# pending and "BC test matrix passed" was not in the rollup at all. classify()
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
check("a rollup missing 'BC test matrix passed' while its run is queued is NOT green",
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
      any("BC test matrix passed" in l for l in v.lines), v.lines)
check("...and does not claim anything failed",
      not any("failed" in l.lower() for l in v.lines), v.lines)

# The real thing, reconstructed from PR #2868's head 4d69b0e2 at the moment the
# false green was reported: PR Check (run 33984617959) and Require Tests (run
# 33984617957) had both completed, Test Matrix (run 33984618294) was still
# pending, so not one of its check runs existed. Running origin/main's classify()
# over exactly this list returns code 0 with the line "all 1 required checks
# passed." -- verbatim what was printed on a PR GitHub was reporting as BLOCKED.
#
# Note 'Tests updated' is `skipped` here and that is NOT the defect: a skipped
# required context satisfies the ruleset (see the merged-PR measurement above).
# The single missing context is 'BC test matrix passed'.
PR2868 = [run(n, "success") for n in [
    "Agent definitions must allowlist the MCP tools they document",
    "CHANGELOG generator tests",
    "PE Authenticode detection tests",
    "PR body closing references must be correct, both directions",
    "PR title/body must not contain a CI-skip directive",
    "Release workflow script tests",
    "Required contexts must not be cancellable on the same commit",
    "ci-wait.py unit tests",
    "pull_request trigger lists must keep their load-bearing event types",
    "scripts/ unit tests",
]]
PR2868.append(run("Tests updated", "skipped"))
PR2868_RUNS = [
    {"id": 33984617959, "name": "PR Check", "status": "completed", "conclusion": "success"},
    {"id": 33984617957, "name": "Require Tests", "status": "completed", "conclusion": "skipped"},
    {"id": 33984618294, "name": "Test Matrix", "status": "queued", "conclusion": None},
]
v = cw.classify(PR2868, workflow_runs=PR2868_RUNS)
check("PR #2868's real rollup is NOT green while Test Matrix is still queued",
      v.code is None, f"(code={v.code}) {v.lines}")
check("...and the progress line names the context that has not reported",
      "BC test matrix passed" in v.progress, v.progress)

# ...and the ordinary green case is unaffected once both contexts are present.
v = cw.classify(green_set(), workflow_runs=ALL_DONE_RUNS)
check("a complete rollup with both ruleset contexts is still GREEN",
      v.code == 0, f"(code={v.code}) {v.lines}")
check("...and says which ruleset contexts it actually confirmed",
      any("BC test matrix passed" in l and "Tests updated" in l for l in v.lines), v.lines)


# ===========================================================================
# #2785 -- a context added to the live ruleset must be waited for
# ===========================================================================
# The verdict has to be driven by the contexts the ruleset requires RIGHT NOW,
# not by a tuple frozen into the module. Passing a third context in must change
# the answer, or the live lookup is decorative.
# A single-variable control: same rollup, same workflow-run list, ONLY the
# context set differs, so the verdict flipping can only be the added context.
#
# This pair used to pass QUEUED_RUNS to both halves and assert `v.code == 0` for
# the second -- which quietly encoded the superseding-run false green below as
# the expected answer, because green_set()'s check runs are all backed by
# workflow run id 1 while QUEUED_RUNS has run 33964656436 queued. The control is
# now run against a finished run list, where the only thing left to vary is the
# context set. The "still in flight" question gets its own section further down.
v = cw.classify(green_set(), contexts=("BC test matrix passed", "Tests updated",
                                       "Provenance attested"),
                workflow_runs=ALL_DONE_RUNS)
check("a context newly added to the ruleset is NOT reported green",
      v.code != 0, f"(code={v.code}) {v.lines}")
check("...and, with every workflow run finished, reads as BLOCKED rather than pending",
      v.code == 4, f"(code={v.code}) {v.lines}")
check("...and names the context nothing reported",
      any("Provenance attested" in l for l in v.lines), v.lines)

v = cw.classify(green_set(), contexts=("BC test matrix passed", "Tests updated"),
                workflow_runs=ALL_DONE_RUNS)
check("...while the same rollup and run list with the known context set is green",
      v.code == 0, f"(code={v.code}) {v.lines}")

# ...and the pending path for a newly-required context is still reachable, when
# the run list says something is genuinely still coming.
v = cw.classify(green_set(), contexts=("BC test matrix passed", "Tests updated",
                                       "Provenance attested"),
                workflow_runs=QUEUED_RUNS)
check("a context newly added to the ruleset keeps the verdict pending "
      "while a workflow run is still queued",
      v.code is None, f"(code={v.code}) {v.lines}")

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
                    "required_status_checks": [{"context": "BC test matrix passed"},
                                               {"context": "Tests updated"}]},
     "ruleset_source_type": "Repository",
     "ruleset_source": "StefanMaron/BusinessCentral.AL.Runner",
     "ruleset_id": 15001420},
]
check("the required contexts are read out of the live branch-rules payload",
      cw.contexts_from_branch_rules(BRANCH_RULES)
      == ("BC test matrix passed", "Tests updated"),
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
    runs.append(cr("BC test matrix passed", "success", NEW_RUN, 101303055107))
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

# Not hypothetical: measured live on PR #2863's head, on the REQUIRED context.
# Three concurrent "Require Tests" runs on one SHA, and the newest run owns a
# LOWER check-run id than the run before it, because their jobs interleaved.
#   run 33983248257  check 101352123189  success
#   run 33983255476  check 101352142567  success   <- older run, HIGHER check id
#   run 33983255561  check 101352142543  success   <- newest run, LOWER check id
# All three passed, so nothing broke that time; that is what a latent wrong
# verdict looks like the day before it matters.
# The payload AS MEASURED -- all three `success`, which is why nothing broke.
PR2863 = [
    cr("Tests updated", "success", 33983248257, 101352123189),
    cr("Tests updated", "success", 33983255476, 101352142567),
    cr("Tests updated", "success", 33983255561, 101352142543),
]
check("the newest of three real concurrent 'Tests updated' runs wins, "
      "though its check-run id is the lowest of the two newest",
      cw.newest_per_name(PR2863)["Tests updated"]["id"] == 101352142543,
      str(cw.newest_per_name(PR2863)["Tests updated"]))

# The same payload with ONE field mutated, so the selection is proved rather than
# merely exercised: on the real all-success payload any selection rule at all
# returns a `success`, so it cannot distinguish a correct rule from a broken one.
# Flipping the middle entry -- the OLDER run holding the HIGHER check-run id --
# makes recency the only thing that can produce the right answer. Kept separate
# from PR2863 above so the measured payload stays exactly as measured.
PR2863_MUTATED = [
    cr("Tests updated", "success", 33983248257, 101352123189),
    cr("Tests updated", "failure", 33983255476, 101352142567),  # older run, higher check id
    cr("Tests updated", "success", 33983255561, 101352142543),  # newest run, lower check id
]
check("...and with the older run's entry flipped to `failure`, the newest run's "
      "`success` is still what gets read",
      cw.newest_per_name(PR2863_MUTATED)["Tests updated"]["conclusion"] == "success",
      str(cw.newest_per_name(PR2863_MUTATED)["Tests updated"]))

check("the workflow run id is read out of a details_url",
      cw.run_id_from("https://github.com/o/r/actions/runs/33964852712/job/101303055037")
      == 33964852712)
check("...and a details_url with no run segment reads as unknown, not as 0",
      cw.run_id_from("https://example.invalid/nothing") is None)

# Within ONE workflow run a re-run attempt still wins on check-run id.
runs = [cr(n, "success", NEW_RUN, 101303055000 + i) for i, n in enumerate(LEGS)]
runs.append(cr("BC test matrix passed", "success", NEW_RUN, 101303055107))
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
        cr("BC test matrix passed", None, NEW_RUN, 101303055107, status="queued"),
        cr("Tests updated", "success", NEW_RUN, 101303055037)]
v = cw.classify(runs, workflow_runs=QUEUED_RUNS)
check("a failure while others are still running is still a FAILED verdict",
      v.code == 1, f"(code={v.code}) {v.lines}")
check("...and says how many required checks have not reported yet",
      any("not reported" in l.lower() for l in v.lines), v.lines)
check("...and warns the failing list can still grow",
      any("grow" in l.lower() for l in v.lines), v.lines)

# The count is a LOWER BOUND and the wording has to admit it. `pool` is built
# from the ruleset contexts plus the rollup entries already present, so a
# required leg with no check run yet is counted by neither term. The #2837 shape:
# one leg reported `failure`, both ruleset contexts were present, and seven more
# bc-tests legs had not created a check run at all -- so the honest arithmetic
# says 1 and the truth was 8.
runs = [cr(LEGS[0], "failure", NEW_RUN, 101303055001),
        cr("BC test matrix passed", None, NEW_RUN, 101303055107, status="queued"),
        cr("Tests updated", "success", NEW_RUN, 101303055037)]
v = cw.classify(runs, workflow_runs=QUEUED_RUNS)
check("the unreported count is stated as a LOWER bound, not an exact number",
      any("at least 1 required check" in l for l in v.lines), v.lines)
check("...and the caveat says a leg with no check run yet is not counted at all",
      any("lower bound" in l.lower() for l in v.lines), v.lines)

# When everything HAS reported the caveat must not be printed, or it is noise.
runs = [cr(LEGS[0], "failure", NEW_RUN, 101303055001),
        cr(LEGS[1], "success", NEW_RUN, 101303055002),
        cr("BC test matrix passed", "failure", NEW_RUN, 101303055107),
        cr("Tests updated", "success", NEW_RUN, 101303055037)]
v = cw.classify(runs, workflow_runs=ALL_DONE_RUNS)
check("a complete rollup's failing list carries no 'still to come' caveat",
      v.code == 1 and not any("grow" in l.lower() for l in v.lines),
      f"(code={v.code}) {v.lines}")


# ===========================================================================
# The fourth false green: a context PRESENT in the rollup, backed by a run
# that a newer run of the same workflow has already superseded
# ===========================================================================
# rollup_is_final() was only ever consulted while a required context was ABSENT
# ("missing and final is not True"), and the run list was only fetched in that
# same case. So this shape returned GREEN: "Tests updated" sitting in the rollup
# with `success` from Require Tests run N, while run N+1 of Require Tests is
# queued on the same SHA and has not created its check run yet. `missing` empty
# => finality never consulted => stale conclusion returned as the verdict.
#
# Designed in, not hypothetical. require-tests.yml:22 states there is
# deliberately NO `concurrency` block, and :57 triggers on 'labeled'/'unlabeled'
# -- so a label applied mid-wait starts a second run on the same commit, and
# until its job starts the rollup still shows the first run's conclusion.
TM_RUN, RT_RUN_OLD, RT_RUN_NEW = 33964656436, 33983248257, 33983299999


def present_but_stale_rollup():
    runs = [cr(n, "success", TM_RUN, 101303055000 + i) for i, n in enumerate(LEGS)]
    runs.append(cr("BC test matrix passed", "success", TM_RUN, 101303055107))
    runs.append(cr("Tests updated", "success", RT_RUN_OLD, 101352123189))
    return runs


SUPERSEDING = [
    {"id": TM_RUN, "name": "Test Matrix", "status": "completed", "conclusion": "success"},
    {"id": RT_RUN_OLD, "name": "Require Tests", "status": "completed", "conclusion": "success"},
    # the label-triggered second run, queued, no check run of its own yet
    {"id": RT_RUN_NEW, "name": "Require Tests", "status": "queued", "conclusion": None},
]

v = cw.classify(present_but_stale_rollup(), workflow_runs=SUPERSEDING)
check("a required context whose workflow has a NEWER run still queued is not green",
      v.code is None, f"(code={v.code}) {v.lines}")
check("...and the progress line names the context and the run superseding it",
      "Tests updated" in v.progress and str(RT_RUN_NEW) in v.progress,
      f"progress={v.progress!r}")

# The control: the very same rollup, once that second run has finished. Only the
# run list's `status` differs, so green here can only be the finality change.
SETTLED = [dict(w, status="completed",
                conclusion=w["conclusion"] or "success") for w in SUPERSEDING]
v = cw.classify(present_but_stale_rollup(), workflow_runs=SETTLED)
check("...and the identical rollup IS green once that run has completed",
      v.code == 0, f"(code={v.code}) {v.lines}")

# The narrowing that keeps this from swallowing every green: an in-flight run of
# a workflow that produces NO required context must not hold the verdict.
# .claude/rules/ci-verdicts.md tells agents to dispatch bc-leg-rerun.yml against
# the branch for a second opinion on a leg, and the two ms-bucket workflows are
# 9,500-test runs; blocking on any of them would trade one false green for a
# class of false "still pending" on the documented diagnostic path.
UNRELATED_DISPATCH = [
    {"id": TM_RUN, "name": "Test Matrix", "status": "completed", "conclusion": "success"},
    {"id": RT_RUN_OLD, "name": "Require Tests", "status": "completed", "conclusion": "success"},
    {"id": RT_RUN_NEW, "name": "BC single-leg re-run (diagnostic)",
     "status": "in_progress", "conclusion": None},
]
v = cw.classify(present_but_stale_rollup(), workflow_runs=UNRELATED_DISPATCH)
check("a dispatched bc-leg-rerun in flight does NOT hold up a green verdict",
      v.code == 0, f"(code={v.code}) {v.lines}")

MS_BUCKET = [dict(w) for w in UNRELATED_DISPATCH]
MS_BUCKET[2] = {"id": RT_RUN_NEW, "name": "MS bucket (manual)",
                "status": "queued", "conclusion": None}
v = cw.classify(present_but_stale_rollup(), workflow_runs=MS_BUCKET)
check("...nor does a queued ms-bucket run", v.code == 0, f"(code={v.code}) {v.lines}")

# An OLDER run of the same workflow still in flight is not a supersession -- it
# is the run our evidence already replaced. Blocking on it would hang the tool.
OLDER_SAME_WORKFLOW = [
    {"id": TM_RUN, "name": "Test Matrix", "status": "completed", "conclusion": "success"},
    {"id": RT_RUN_OLD - 10, "name": "Require Tests", "status": "in_progress",
     "conclusion": None},
    {"id": RT_RUN_OLD, "name": "Require Tests", "status": "completed",
     "conclusion": "success"},
]
v = cw.classify(present_but_stale_rollup(), workflow_runs=OLDER_SAME_WORKFLOW)
check("an OLDER run of the same workflow still in flight does not hold the verdict",
      v.code == 0, f"(code={v.code}) {v.lines}")

# A real failure is still a verdict: it is reported even while a newer run of the
# same workflow is queued. Delaying it costs real time, and the caveat already
# says the list can grow. This assertion pins a deliberate tradeoff, not an
# ideal: the stale-FAILED residual it allows is tracked in #2922, and flipping
# this expectation is the RED baseline for whoever takes that on.
runs = present_but_stale_rollup()
runs[0] = cr(LEGS[0], "failure", TM_RUN, 101303055000)
v = cw.classify(runs, workflow_runs=SUPERSEDING)
check("a failing required leg still reports FAILED with a superseding run queued",
      v.code == 1, f"(code={v.code}) {v.lines}")

# The sibling path with the same blind spot: a `cancelled` required context whose
# replacement run is already queued used to read as exit 4, telling the caller to
# "re-run the cancelled run" when the re-run was on its way.
runs = present_but_stale_rollup()
runs.append(cr("Tests updated", "cancelled", RT_RUN_OLD + 1, 101352123999))
v = cw.classify(runs, workflow_runs=[
    {"id": TM_RUN, "name": "Test Matrix", "status": "completed", "conclusion": "success"},
    {"id": RT_RUN_OLD + 1, "name": "Require Tests", "status": "completed",
     "conclusion": "cancelled"},
    {"id": RT_RUN_NEW, "name": "Require Tests", "status": "queued", "conclusion": None},
])
check("a cancelled required context is not called BLOCKED while its replacement "
      "run is still queued",
      v.code is None, f"(code={v.code}) {v.lines}")

# ...and once nothing is in flight, that same cancellation IS the blocking answer.
v = cw.classify(runs, workflow_runs=[
    {"id": TM_RUN, "name": "Test Matrix", "status": "completed", "conclusion": "success"},
    {"id": RT_RUN_OLD + 1, "name": "Require Tests", "status": "completed",
     "conclusion": "cancelled"},
])
check("...and is BLOCKED once no newer run of that workflow is coming",
      v.code == 4, f"(code={v.code}) {v.lines}")



# ===========================================================================
# #3002 -- three false verdicts in one night, one shape: a verdict resolved
#          from evidence that is not the current head's LIVE run
# ===========================================================================
# Every payload below is RECORDED, not hand-built: fetched back out of the
# GitHub API for the exact head SHAs that produced the wrong answers, so the
# cases cannot drift into a reconstruction that agrees with the code.
#
#   PR #2842  head e0841eed  GREEN, "all 1 required checks passed"  (matrix job
#                            had not been created)
#   PR #2971  head 47f30db4  GREEN, "all 1 required checks passed"  (8 legs
#                            still pending)
#   PR #3010  head 95c16b20  FAILURE, read off a superseded CANCELLED run while
#                            the live run was green

# ---------------------------------------------------------------------------
# #3010, the RED direction. Recorded from
#   GET /commits/95c16b20a500f638fbbe7eeb14f78545c22f92ee/check-runs
#   GET /actions/runs?head_sha=95c16b20a500f638fbbe7eeb14f78545c22f92ee
#
# Test Matrix run 34002828792 has RUN-LEVEL conclusion `cancelled`, and its
# aggregate job "BC test matrix passed" concluded `failure` -- the aggregate
# runs `if: always()` over `needs` that were killed, so a cancelled run reports
# a FAILING required context. Seven of its eight legs are `cancelled`; leg 27.3
# had already finished `success`.
#
# Test Matrix run 34004261321 replaced it and was green throughout, but its
# aggregate job starts LAST, so for several minutes the newest check run named
# "BC test matrix passed" on this commit was the failure from the run that had
# been abandoned. classify() put it in `bad` and returned exit 1 -- and `bad`
# short-circuits BEFORE the in-flight guard, which is why the guard that exists
# for exactly this shape never got consulted.
PR3010_TM_CANCELLED = 34002828792   # run-level conclusion: cancelled
PR3010_TM_LIVE = 34004261321        # the superseding run, green
PR3010_RT = 34002828699             # Require Tests -> "Tests updated"

PR3010_LEGS = ["bc-tests / BC 27.0.38460.53934 (required)",
               "bc-tests / BC 27.5.46862.53931 (required)",
               "bc-tests / BC 28.4.53241.54318 (required)"]


def pr3010_rollup(with_live_legs: bool = False):
    """The rollup while the live Test Matrix run's aggregate job had not started."""
    runs = [cr("BC test matrix passed", "failure", PR3010_TM_CANCELLED, 101405801410)]
    for i, n in enumerate(PR3010_LEGS):
        runs.append(cr(n, "cancelled", PR3010_TM_CANCELLED, 101404706127 + i))
    runs.append(cr("bc-tests / BC 27.3.44313.53909 (required)", "success",
                   PR3010_TM_CANCELLED, 101404706089))
    runs.append(cr("Tests updated", "success", PR3010_RT, 101404642460))
    if with_live_legs:
        for i, n in enumerate(PR3010_LEGS):
            runs.append(cr(n, "success", PR3010_TM_LIVE, 101408579871 + i))
    return runs


PR3010_WF_LIVE_IN_FLIGHT = [
    {"id": PR3010_RT, "name": "Require Tests", "status": "completed",
     "conclusion": "success"},
    {"id": PR3010_TM_CANCELLED, "name": "Test Matrix", "status": "completed",
     "conclusion": "cancelled"},
    {"id": PR3010_TM_LIVE, "name": "Test Matrix", "status": "in_progress",
     "conclusion": None},
]

v = cw.classify(pr3010_rollup(), workflow_runs=PR3010_WF_LIVE_IN_FLIGHT)
check("#3010: a `failure` inherited from a CANCELLED workflow run is not a FAILED "
      "verdict while a newer run of the same workflow is in flight",
      v.code is None, f"(code={v.code}) {v.lines}")
check("...and no killed job is offered up for a log fetch",
      v.log_target is None, str(v.log_target))

v = cw.classify(pr3010_rollup(with_live_legs=True),
                workflow_runs=PR3010_WF_LIVE_IN_FLIGHT)
check("...still not FAILED once the live run's LEGS have reported green but its "
      "aggregate job has not",
      v.code is None, f"(code={v.code}) {v.lines}")

# Same commit, one instant earlier: the replacement run has not registered yet.
# The answer is still not "FAILED" -- the job did not fail on its merits, the run
# was killed -- but it is not "wait" either once nothing further is coming. That
# is exactly the #2726 shape, and exit 4 already says it.
PR3010_WF_NO_REPLACEMENT = [
    {"id": PR3010_RT, "name": "Require Tests", "status": "completed",
     "conclusion": "success"},
    {"id": PR3010_TM_CANCELLED, "name": "Test Matrix", "status": "completed",
     "conclusion": "cancelled"},
]
v = cw.classify(pr3010_rollup(), workflow_runs=PR3010_WF_NO_REPLACEMENT)
check("#3010: a cancelled Test Matrix run with no replacement is BLOCKED, not FAILED",
      v.code == 4, f"(code={v.code}) {v.lines}")
check("...and names the required context it is blocked on",
      any("BC test matrix passed" in l for l in v.lines), v.lines)
check("...and never tells the caller something failed",
      not any("failed" in l.lower() for l in v.lines), v.lines)

# The guard must not swallow a REAL failure. Same rollup, but the Test Matrix run
# that produced the failing aggregate ran to completion on its own merits.
PR3010_WF_GENUINE = [
    {"id": PR3010_RT, "name": "Require Tests", "status": "completed",
     "conclusion": "success"},
    {"id": PR3010_TM_CANCELLED, "name": "Test Matrix", "status": "completed",
     "conclusion": "failure"},
]
v = cw.classify(pr3010_rollup(), workflow_runs=PR3010_WF_GENUINE)
check("a failing required context from a run that was NOT cancelled is still FAILED",
      v.code == 1, f"(code={v.code}) {v.lines}")
check("...and still offers the failing job for the log fetch",
      (v.log_target or {}).get("name") == "BC test matrix passed", str(v.log_target))

# ---------------------------------------------------------------------------
# #2971 / #2842, the GREEN direction. Recorded from
#   GET /commits/47f30db4d37415378f227b62e9f6d38433166f17/check-runs
#   GET /actions/runs?head_sha=47f30db4d37415378f227b62e9f6d38433166f17
#
# All three workflow runs were created within one second of the push
# (00:09:25-26Z), so "BC test matrix passed" was always COMING. Exhaustively:
# with the real two-context ruleset this rollup cannot produce a 1-count green
# under post-#2882 code -- either the context is in the rollup (pool >= 2) or it
# is `missing` and the Test Matrix run is in flight, so `final is not True` and
# the verdict is withheld. The only reachable route to the printed line is a
# `contexts` of length ONE: a required-context set that got NARROWED.
PR2971_RT, PR2971_PRC, PR2971_TM = 34000544365, 34000544377, 34000544502

PR2971_JUST_PUSHED = [
    cr("Tests updated", "success", PR2971_RT, 101398501503),
    cr("ci-wait.py unit tests", "success", PR2971_PRC, 101398501751),
    cr("scripts/ unit tests", "success", PR2971_PRC, 101398501706),
]
PR2971_WF = [
    {"id": PR2971_RT, "name": "Require Tests", "status": "completed",
     "conclusion": "success"},
    {"id": PR2971_PRC, "name": "PR Check", "status": "completed",
     "conclusion": "success"},
    {"id": PR2971_TM, "name": "Test Matrix", "status": "in_progress",
     "conclusion": None},
]

# The control: with the real ruleset this is already withheld (#2882 did that).
v = cw.classify(PR2971_JUST_PUSHED, workflow_runs=PR2971_WF)
check("#2971 control: the recorded rollup is withheld under the real two-context "
      "ruleset", v.code is None, f"(code={v.code}) {v.lines}")

# The defect: hand a NARROWED context set to the same rollup. Nothing else about
# the commit changed, and the verdict flips to a green naming ONE check.
v = cw.classify(PR2971_JUST_PUSHED, contexts=("Tests updated",),
                workflow_runs=PR2971_WF)
check("#3002: a NARROWED required-context set is refused, not answered",
      v.code != 0, f"(code={v.code}) {v.lines}")
check("...and it is undetermined (3), not a failure and not a block",
      v.code == 3, f"(code={v.code}) {v.lines}")
check("...and says which required context went missing from the set",
      any("BC test matrix passed" in l for l in v.lines), v.lines)
check("...and never prints the false-green line",
      not any("all 1 required checks passed" in l for l in v.lines), v.lines)

v = cw.classify(PR2971_JUST_PUSHED, contexts=(), workflow_runs=PR2971_WF)
check("an EMPTY required-context set is refused too", v.code == 3,
      f"(code={v.code}) {v.lines}")

# A green must be able to account for every ruleset context BY NAME, and say so.
# Every real green that night named 9 or 10 checks; both false greens named 1.
v = cw.classify(green_set())
check("a green names how many ruleset contexts it accounted for",
      any("2/2 ruleset context(s)" in l for l in v.lines), v.lines)
check("...and names them",
      all(any(c in l for l in v.lines) for c in cw.RULESET_CONTEXTS), v.lines)

# ---------------------------------------------------------------------------
# Where a narrowed set comes from: the ruleset read itself. The test INJECTS the
# recorded API response rather than hand-building a tuple, because a hand-built
# tuple cannot reproduce a degraded read -- the defect is in what the fetch
# returns, not in what the caller passes.
#
# Recorded verbatim from GET /repos/.../rules/branches/main on 2026-09-06.
RECORDED_BRANCH_RULES = [
    {"type": "deletion", "ruleset_id": 15001420},
    {"type": "non_fast_forward", "ruleset_id": 15001420},
    {"type": "pull_request", "ruleset_id": 15001420},
    {"type": "required_status_checks", "ruleset_id": 15001420, "parameters": {
        "strict_required_status_checks_policy": False,
        "do_not_enforce_on_create": False,
        "required_status_checks": [{"context": "BC test matrix passed"},
                                   {"context": "Tests updated"}]}},
]
DEGRADED_BRANCH_RULES = [
    r for r in RECORDED_BRANCH_RULES if r["type"] != "required_status_checks"
] + [{"type": "required_status_checks", "ruleset_id": 15001420, "parameters": {
        "strict_required_status_checks_policy": False,
        "do_not_enforce_on_create": False,
        "required_status_checks": [{"context": "Tests updated"}]}}]

ctx, status, notes = cw.resolve_required_contexts(fetch=lambda b: RECORDED_BRANCH_RULES)
check("the recorded live ruleset resolves to both required contexts",
      sorted(ctx) == sorted(cw.RULESET_CONTEXTS) and status == "live",
      f"{ctx} {status}")

ctx, status, notes = cw.resolve_required_contexts(fetch=lambda b: DEGRADED_BRANCH_RULES)
check("a ruleset read that drops a known required context is DEGRADED",
      status == "degraded", f"{ctx} {status}")
check("...and the degraded set is never handed on as the required set",
      sorted(ctx) == sorted(cw.RULESET_CONTEXTS), f"{ctx}")
check("...and the note names the context that went missing",
      any("BC test matrix passed" in n for n in notes), notes)

ctx, status, notes = cw.resolve_required_contexts(fetch=lambda b: None)
check("an UNREADABLE ruleset falls back to the full built-in set, never a subset",
      sorted(ctx) == sorted(cw.RULESET_CONTEXTS) and status == "fallback",
      f"{ctx} {status}")

EXTRA = RECORDED_BRANCH_RULES[:3] + [
    {"type": "required_status_checks", "ruleset_id": 15001420, "parameters": {
        "required_status_checks": [{"context": "BC test matrix passed"},
                                   {"context": "Tests updated"},
                                   {"context": "Some new gate"}]}}]
ctx, status, notes = cw.resolve_required_contexts(fetch=lambda b: EXTRA)
check("a context ADDED in the UI is picked up and waited for",
      sorted(ctx) == ["BC test matrix passed", "Some new gate", "Tests updated"]
      and status == "live", f"{ctx} {status}")


# ===========================================================================
# #3142: "Superseded ... Harmless" must never absorb a genuine failure
# ===========================================================================
# Observed on PR #3112's head c6377b30: a GREEN verdict listed
# `preflight.py unit tests` under "Superseded ... Harmless" while that context's
# NEWEST check run on the commit concluded `failure`, from a workflow run that
# was itself `failure` -- neither cancelled nor superseded. Nothing else in the
# output said anything had failed.
#
# The payload is BUILT here rather than fetched, so the property is pinned
# independently of whatever the API happens to return today. It is the shape of
# the real one: three check runs for one context name, the middle one cancelled
# by a superseding run, the newest a real failure.
#
# The invariant: a `failure` may be discounted ONLY when the run that PRODUCED
# it is itself cancelled or superseded -- never on the strength of some OTHER
# run carrying the same context name.
MATRIX_RUN, KILLED_PR_CHECK, LIVE_PR_CHECK, REQ_RUN = 34036994584, 34037049850, 34037050361, 34036994488

C6377B30_RUNS = [
    {"id": MATRIX_RUN, "name": "Test Matrix", "status": "completed", "conclusion": "success"},
    {"id": KILLED_PR_CHECK, "name": "PR Check", "status": "completed", "conclusion": "cancelled"},
    {"id": LIVE_PR_CHECK, "name": "PR Check", "status": "completed", "conclusion": "failure"},
    {"id": REQ_RUN, "name": "Require Tests", "status": "completed", "conclusion": "success"},
]


def absorbed_failure_set(failing_name):
    """Required contexts all green; `failing_name` red on the live run, with a
    cancelled sibling entry left behind by a superseded run of that workflow."""
    runs = [cr(n, "success", MATRIX_RUN, 101496852900 + i) for i, n in enumerate(LEGS)]
    runs.append(cr("All BC versions passed", "success", MATRIX_RUN, 101499731123))
    runs.append(cr("Tests updated", "success", REQ_RUN, 101496768931))
    # the cancelled leftover, and a genuinely failing NEWEST entry for one name
    runs.append(cr(failing_name, "cancelled", KILLED_PR_CHECK, 101496919780))
    runs.append(cr(failing_name, "failure", LIVE_PR_CHECK, 101496925176))
    return runs


runs = absorbed_failure_set("preflight.py unit tests")
v = cw.classify(runs, workflow_runs=C6377B30_RUNS)
# The verdict itself is correct and must STAY correct: `preflight.py unit tests`
# is not a ruleset context, so it does not gate the merge.
check("a failing NON-required context still does not block the merge",
      v.code == 0, f"(code={v.code}) {v.lines}")
harmless = [l for l in v.lines if "harmless" in l.lower()]
idx = v.lines.index(harmless[0]) if harmless else len(v.lines)
check("...but a context whose NEWEST run FAILED is never listed as harmlessly "
      "superseded (#3142)",
      not any("preflight.py unit tests" in l for l in v.lines[idx:]),
      "\n".join(v.lines))
check("...and the green output NAMES it as failing, so a real red is not silent",
      any("preflight.py unit tests" in l and "failure" in l for l in v.lines),
      "\n".join(v.lines))
check("...and points at the workflow run that produced the failure",
      any(str(LIVE_PR_CHECK) in l for l in v.lines), "\n".join(v.lines))

# The mirror, so this is not just "never say harmless": a cancelled entry that a
# newer run really did re-report as SUCCESS is still explained as harmless.
runs = [cr(n, "success", MATRIX_RUN, 101496852900 + i) for i, n in enumerate(LEGS)]
runs.append(cr("All BC versions passed", "success", MATRIX_RUN, 101499731123))
runs.append(cr("Tests updated", "success", REQ_RUN, 101496768931))
runs.append(cr("scripts/ unit tests", "cancelled", KILLED_PR_CHECK, 101496919785))
runs.append(cr("scripts/ unit tests", "success", LIVE_PR_CHECK, 101496925213))
v = cw.classify(runs, workflow_runs=C6377B30_RUNS)
check("a cancelled entry a newer run re-reported as SUCCESS is still GREEN",
      v.code == 0, f"(code={v.code}) {v.lines}")
check("...and is still explained as harmlessly superseded",
      any("scripts/ unit tests" in l for l in v.lines)
      and any("harmless" in l.lower() for l in v.lines), "\n".join(v.lines))
check("...and is NOT reported as a failing check",
      not any("scripts/ unit tests" in l and "failure" in l for l in v.lines),
      "\n".join(v.lines))

# The same shape on a REQUIRED context has to fail outright, not merely be
# named. One ruleset edit is all that separates the two payloads.
runs = absorbed_failure_set("Tests updated")
v = cw.classify(runs, workflow_runs=C6377B30_RUNS)
check("the same shape on a REQUIRED context is a FAILED verdict, not a green "
      "with a footnote",
      v.code == 1, f"(code={v.code}) {v.lines}")
check("...and never describes that failure as harmless",
      not any("harmless" in l.lower() for l in v.lines), "\n".join(v.lines))

# A failure inside a run that IS cancelled at the run level stays discountable --
# that is the deliberate #3002 trade-off, and narrowing it is not this fix.
runs = [cr(n, "success", MATRIX_RUN, 101496852900 + i) for i, n in enumerate(LEGS)]
runs.append(cr("All BC versions passed", "success", MATRIX_RUN, 101499731123))
runs.append(cr("Tests updated", "success", REQ_RUN, 101496768931))
runs.append(cr("scripts/ unit tests", "failure", KILLED_PR_CHECK, 101496919785))
v = cw.classify(runs, workflow_runs=C6377B30_RUNS)
check("a failure from a run that is itself CANCELLED is still discounted",
      v.code == 0, f"(code={v.code}) {v.lines}")
check("...and is not announced as a real failing check",
      not any("scripts/ unit tests" in l and "failure" in l for l in v.lines),
      "\n".join(v.lines))

# A REQUIRED context whose check run concluded `failure` on its merits inside a
# run that was cancelled afterwards is still reported as BLOCKED, not FAILED --
# that trade-off errs away from green and is not being narrowed here. But the
# exit-4 advice ("a cancelled run has no failure log to overwrite") is wrong for
# exactly this entry: it has one, and `gh run rerun` destroys it permanently.
runs = [cr(n, "success", MATRIX_RUN, 101496852900 + i) for i, n in enumerate(LEGS)]
runs.append(cr("All BC versions passed", "success", MATRIX_RUN, 101499731123))
runs.append(cr("Tests updated", "failure", KILLED_PR_CHECK, 101496919511))
v = cw.classify(runs, workflow_runs=C6377B30_RUNS)
check("a required failure inside a CANCELLED run is blocked, not failed",
      v.code == 4, f"(code={v.code}) {v.lines}")
check("...and the output warns that this one has a real log to lose",
      any("WOULD overwrite" in l for l in v.lines), "\n".join(v.lines))
check("...and says what it actually concluded, not just 'cancelled'",
      any("Tests updated" in l and "failure" in l for l in v.lines),
      "\n".join(v.lines))

# ...and the plain case keeps the unqualified advice, or the warning above would
# just be noise on every cancellation.
runs = [cr(n, "success", MATRIX_RUN, 101496852900 + i) for i, n in enumerate(LEGS)]
runs.append(cr("All BC versions passed", "success", MATRIX_RUN, 101499731123))
runs.append(cr("Tests updated", "cancelled", KILLED_PR_CHECK, 101496919511))
v = cw.classify(runs, workflow_runs=C6377B30_RUNS)
check("a genuinely cancelled required context is blocked with no such warning",
      v.code == 4 and not any("WOULD overwrite" in l for l in v.lines),
      f"(code={v.code}) " + "\n".join(v.lines))
# ...but the advice it DOES print may never restate the falsified claim. The
# exemption is a narrowing, not a reversal: re-running is still right in the
# #2726 case, conditional on nothing having failed before the cancellation.
advice = "\n".join(v.lines)
check("the exit-4 advice no longer claims a cancelled run has no log to lose",
      "no failure log" not in advice, advice)
check("...states the condition instead of exempting cancellations outright",
      "only while nothing on this commit concluded" in advice, advice)
check("...and points at the rule file rather than restating the reasoning",
      "ci-verdicts.md" in advice, advice)
check("...while still calling the re-run correct where the condition holds",
      "gh run rerun" in advice and "--admin" in advice, advice)

# The same claim lived twice; the docstring copy is pinned too, so a reader who
# opens the file instead of running it gets the narrowed version.
check("the module docstring does not carry the falsified claim either",
      "no failure log" not in (cw.__doc__ or ""), (cw.__doc__ or "")[:0])
check("...and defers to the rule file as the normative statement",
      "ci-verdicts.md" in (cw.__doc__ or ""), "")

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
