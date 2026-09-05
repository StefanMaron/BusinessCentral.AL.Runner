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

# --- neutral/skipped are not failures, and `skipped` SATISFIES a required
# --- context rather than leaving it unreported. Measured, because a proposal to
# --- treat `skipped` as "no verdict yet" would break the documented
# --- 'docs-only' / 'no-tests-needed' bypass: four merged PRs carry
# --- 'Tests updated' = skipped on their head SHA with 'All BC versions passed'
# --- = success -- #2759 (451c757b), #2749 (8717aec3), #2717 (dbd3a1a2) and
# --- #2668 (3d1e9792). GitHub's ruleset accepted every one of them.
runs = [run(n, "success") for n in LEGS]
runs.append(run("All BC versions passed", "success"))
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

# The real thing, reconstructed from PR #2868's head 4d69b0e2 at the moment the
# false green was reported: PR Check (run 33984617959) and Require Tests (run
# 33984617957) had both completed, Test Matrix (run 33984618294) was still
# pending, so not one of its check runs existed. Running origin/main's classify()
# over exactly this list returns code 0 with the line "all 1 required checks
# passed." -- verbatim what was printed on a PR GitHub was reporting as BLOCKED.
#
# Note 'Tests updated' is `skipped` here and that is NOT the defect: a skipped
# required context satisfies the ruleset (see the merged-PR measurement above).
# The single missing context is 'All BC versions passed'.
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
      "All BC versions passed" in v.progress, v.progress)

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
# A single-variable control: same rollup, same workflow-run list, ONLY the
# context set differs, so the verdict flipping can only be the added context.
#
# This pair used to pass QUEUED_RUNS to both halves and assert `v.code == 0` for
# the second -- which quietly encoded the superseding-run false green below as
# the expected answer, because green_set()'s check runs are all backed by
# workflow run id 1 while QUEUED_RUNS has run 33964656436 queued. The control is
# now run against a finished run list, where the only thing left to vary is the
# context set. The "still in flight" question gets its own section further down.
v = cw.classify(green_set(), contexts=("All BC versions passed", "Tests updated",
                                       "Provenance attested"),
                workflow_runs=ALL_DONE_RUNS)
check("a context newly added to the ruleset is NOT reported green",
      v.code != 0, f"(code={v.code}) {v.lines}")
check("...and, with every workflow run finished, reads as BLOCKED rather than pending",
      v.code == 4, f"(code={v.code}) {v.lines}")
check("...and names the context nothing reported",
      any("Provenance attested" in l for l in v.lines), v.lines)

v = cw.classify(green_set(), contexts=("All BC versions passed", "Tests updated"),
                workflow_runs=ALL_DONE_RUNS)
check("...while the same rollup and run list with the known context set is green",
      v.code == 0, f"(code={v.code}) {v.lines}")

# ...and the pending path for a newly-required context is still reachable, when
# the run list says something is genuinely still coming.
v = cw.classify(green_set(), contexts=("All BC versions passed", "Tests updated",
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

# The count is a LOWER BOUND and the wording has to admit it. `pool` is built
# from the ruleset contexts plus the rollup entries already present, so a
# required leg with no check run yet is counted by neither term. The #2837 shape:
# one leg reported `failure`, both ruleset contexts were present, and seven more
# bc-tests legs had not created a check run at all -- so the honest arithmetic
# says 1 and the truth was 8.
runs = [cr(LEGS[0], "failure", NEW_RUN, 101303055001),
        cr("All BC versions passed", None, NEW_RUN, 101303055107, status="queued"),
        cr("Tests updated", "success", NEW_RUN, 101303055037)]
v = cw.classify(runs, workflow_runs=QUEUED_RUNS)
check("the unreported count is stated as a LOWER bound, not an exact number",
      any("at least 1 required check" in l for l in v.lines), v.lines)
check("...and the caveat says a leg with no check run yet is not counted at all",
      any("lower bound" in l.lower() for l in v.lines), v.lines)

# When everything HAS reported the caveat must not be printed, or it is noise.
runs = [cr(LEGS[0], "failure", NEW_RUN, 101303055001),
        cr(LEGS[1], "success", NEW_RUN, 101303055002),
        cr("All BC versions passed", "failure", NEW_RUN, 101303055107),
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
    runs.append(cr("All BC versions passed", "success", TM_RUN, 101303055107))
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
# says the list can grow.
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


print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
