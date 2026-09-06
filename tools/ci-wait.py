#!/usr/bin/env python3
"""Wait for a PR's required checks and return ONE verdict, in ONE tool call.

Why this exists
---------------
Measured across one session's 17 subagents: 328 of 3,282 Bash calls (10%) were
CI waiting, and the shape was wrong -- 107 `gh run view` polls and 37 `sleep`
loops against only 29 blocking `gh run watch` calls. Roughly four polls for every
proper wait.

That is not carelessness. `.claude/rules/no-backgrounding-long-commands.md`
correctly warns that the harness can background a `gh run watch` and promise a
notification that never arrives, and tells agents to re-check with
`gh run view` -- which is a poll loop. Every poll is a round trip that re-sends
the whole conversation.

This script keeps the polling but moves it INSIDE a single call: it loops
internally, prints nothing until it has an answer, and returns one verdict. Ten
to forty round trips become one.

It also encodes the two rules from `.claude/rules/ci-verdicts.md` that agents
keep getting wrong:

  * A verdict belongs to ONE COMMIT. Checks are matched against the PR's current
    head SHA, so a newer completed run for an older push is never reported as
    this push's result.
  * NEVER re-run a failed job -- it destroys the log. On failure this fetches
    `--log-failed` for you, so there is no reason to reach for a re-run and no
    second round trip to read it.

Usage
-----
    tools/ci-wait.py 2379                 # wait for PR 2379's required checks
    tools/ci-wait.py 2379 --timeout 2400  # bound the wait (default 1800s)
    tools/ci-wait.py 2379 --no-log        # skip the failure log fetch

Exit codes
----------
    0  every required check passed ON THE CURRENT HEAD -- safe to report green
    1  at least one required check failed; the failing log is printed
    2  timed out while still running -- NOT a verdict, call again
    3  could not determine state (auth, network, no checks reported, the
       required-context set could not be established without narrowing it, or
       THIS FILE is behind origin/main -- see "Which copy is running" below)
    4  everything green, but the merge is still blocked and nothing else reports
       why: a REQUIRED context is `cancelled` on this commit (#2726), or a
       REQUIRED context produced no check run at all once every workflow run for
       the commit had finished (#2807)

Exit 1 can be a PARTIAL list
----------------------------
A verdict is returned as soon as one exists, so a FAILED verdict names the
required checks that have failed SO FAR, not necessarily all of them. A
coordinator read "1 of 9 required checks failed" as "only BC 27.0 is affected"
and began a version-specific diagnosis; `gh pr checks` after the run finished
showed eight legs failing. The failure block therefore says how many required
checks have not reported yet and that the list can still grow. Nothing about the
verdict changed -- only what it admits it does not know.

One shape behind three wrong verdicts (#3002)
---------------------------------------------
In one night this tool answered wrongly three times, in both directions, and
every one of them resolved a verdict from a run that was NOT the current head's
LIVE run:

  PR #2842  GREEN, "all 1 required checks passed"  -- the matrix job had not been
            created yet, so "every required check that exists has passed" was
            vacuously true over a set of one.
  PR #2971  the same line, with eight legs still pending.
  PR #3010  FAILURE -- read off Test Matrix run 34002828792, whose RUN-LEVEL
            conclusion is `cancelled` and whose aggregate job "All BC versions
            passed" therefore concluded `failure` (it runs `if: always()` over
            cancelled `needs`). The live run 34004261321 was green throughout.

So there are three guards, not three patches:

  * The required-context set may never be narrower than the built-in floor.
    A partial ruleset read returns exit 3, never a reduced set
    (resolve_required_contexts).
  * A check run whose owning workflow run has been superseded by a newer run of
    the same workflow is not a verdict in EITHER direction -- and that is
    checked ahead of the failure short-circuit, which is what #3010 slipped
    past (supersession).
  * A conclusion produced by a run that was CANCELLED is reclassified as a
    cancellation, so it can block (exit 4) but can never be reported as a
    failure or counted toward a green.

And a green must be able to account for every ruleset context BY NAME and say
so: the line now reads "N/N ruleset context(s) accounted for". Every real green
that night named nine or ten checks and both false greens named one; that
asymmetry is the only reason a human caught them.

Which copy is running (#3020)
-----------------------------
Everything above is about this FILE being right. It says nothing about the copy
that actually ran: an agent invokes this by relative path from its own worktree,
and a worktree is created once and never fast-forwarded. Measured 2026-09-06 over
this repository's 109 worktrees, 71 of the 99 copies of this file were NOT
origin/main's, in four distinct versions -- and replaying the recorded PR #2971
rollup through them, 59 printed the exact false GREEN #3018 had already fixed.

So before any verdict, this asks `tools/agent_self_freshness.py` whether
origin/main has moved this file since the checkout branched. If it has, that is
exit 3 -- NOT a verdict -- and no GitHub call is made at all.

Exit 3 rather than a new code, deliberately. Agents branch on these numbers, and
3 already means "no verdict, do not act", which is exactly the right handling; a
code nobody recognises would be handled by whatever each caller's else-branch
happens to do. The several causes of 3 are distinguished in the printed message,
which is already how the rest of them are told apart.

A branch that legitimately edits this tool is not stale and is not refused: what
makes a copy stale is origin/main having moved the file since the branch point,
not the working file differing from origin/main's.

Exit 4, and why it is not exit 0
--------------------------------
A branch ruleset satisfies a required status check from the newest check run
carrying that context name on the head commit. `cancelled` does not satisfy it,
and unlike a failure nothing surfaces it -- `gh pr checks` prints the rollup's
summarised state and reads SUCCESS. Observed on PR #2722 at head d5c334c1: every
context SUCCESS, all 8 BC legs passed, this script reported GREEN, and the merge
was refused, because a superseded PR Check run had left the required
"Tests updated" context cancelled on that same SHA. The natural next move from a
green tool and a refused merge is to assume branch protection is broken and
reach for --admin, which bypasses the ruleset to work around a phantom.

This is the one case in this repository where `gh run rerun` can be the right
call, and it is CONDITIONAL: only while nothing on the commit concluded `failure`
before the cancellation. A check run can fail on its merits and have its parent
workflow run cancelled only afterwards, and a re-run destroys that log exactly as
it destroys any other. `.claude/rules/ci-verdicts.md` section 3 is the normative
statement and carries the check to run first -- correct it there, not here.

Where the condition holds, re-running re-reports the context as success within a
minute. The workflow side of #2726 stops required contexts being cancellable in
the first place; this verdict exists for anything that still gets there, and the
exit-4 output names any entry that fails the condition.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_self_freshness as _freshness
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _freshness = None

REPO = "StefanMaron/BusinessCentral.AL.Runner"
TRANSIENT = ("i/o timeout", "connection reset", "502 Bad", "dial tcp",
             "could not connect", "TLS handshake")


def gh(args: list[str], attempts: int = 4) -> tuple[int, str]:
    """Run gh, retrying transient network failures. Returns (rc, stdout)."""
    last = ""
    for i in range(attempts):
        p = subprocess.run(["gh", *args], capture_output=True, text=True)
        out = (p.stdout or "") + (p.stderr or "")
        # mise prints a banner on stdout; drop it so JSON parses.
        out = "\n".join(l for l in out.split("\n") if not l.startswith("mise "))
        if not any(t.lower() in out.lower() for t in TRANSIENT):
            return p.returncode, out.strip()
        last = out
        time.sleep(3 * (i + 1))
    return 1, last.strip()


def job_id_from(details_url: str | None) -> str | None:
    """Actions job id out of a check-run details_url.

    A check-run's own `id` is a DIFFERENT identifier from the Actions job id that
    `gh run view --job` expects -- passing the former fails with "could not find
    job", which is how this went wrong the first time. The job id is the trailing
    segment of .../actions/runs/<run-id>/job/<job-id>.
    """
    if not details_url:
        return None
    m = re.search(r"/job/(\d+)", details_url)
    return m.group(1) if m else None


def head_sha(pr: str) -> str | None:
    rc, out = gh(["pr", "view", pr, "--repo", REPO, "--json", "headRefOid",
                  "--jq", ".headRefOid"])
    return out.strip() if rc == 0 and out.strip() else None


def required_checks(sha: str) -> list[dict] | None:
    rc, out = gh(["api", f"repos/{REPO}/commits/{sha}/check-runs?per_page=100",
                  "--jq", "[.check_runs[] | {name, status, conclusion, id, details_url}]"])
    if rc != 0:
        return None
    try:
        return json.loads(out)
    except Exception:
        return None


def workflow_runs_for(sha: str) -> list[dict] | None:
    """Every workflow run GitHub has registered for this commit, or None.

    This is what distinguishes "a required context has not appeared YET" from
    "it will never appear" -- a distinction the check-run rollup cannot make on
    its own, and getting it wrong toward GREEN is #2807.
    """
    rc, out = gh(["api",
                  f"repos/{REPO}/actions/runs?head_sha={sha}&per_page=100",
                  "--jq", "[.workflow_runs[] | {id, name, status, conclusion}]"])
    if rc != 0:
        return None
    try:
        got = json.loads(out)
    except Exception:
        return None
    return got if isinstance(got, list) else None


def contexts_from_branch_rules(payload) -> tuple[str, ...] | None:
    """Required status-check contexts out of a `rules/branches/<b>` payload.

    Returns None -- meaning UNKNOWN -- for anything that does not carry a
    non-empty required_status_checks rule. Never an empty tuple: an empty
    required set would silently disable every gate this module applies, and
    #2785 is exactly the story of an empty answer being read as fact. Ruleset
    15039643 ("Copilot review for default branch") is disabled and has no such
    rule, so querying it directly answers with an empty list.
    """
    if not isinstance(payload, list):
        return None
    out: list[str] = []
    for rule in payload:
        if not isinstance(rule, dict) or rule.get("type") != "required_status_checks":
            continue
        params = rule.get("parameters") or {}
        for entry in params.get("required_status_checks") or []:
            ctx = (entry or {}).get("context") if isinstance(entry, dict) else None
            if ctx:
                out.append(str(ctx))
    return tuple(out) or None


def live_ruleset_payload(branch: str = "main"):
    """The raw `rules/branches/<branch>` payload, or None if it cannot be read.

    Split out from the parse so a test can inject a RECORDED response. That
    matters: the #3002 green-direction defect is in what the fetch returns, and
    a test that hand-builds a context tuple cannot reproduce it.
    """
    rc, out = gh(["api", f"repos/{REPO}/rules/branches/{branch}"])
    if rc != 0:
        return None
    try:
        return json.loads(out)
    except Exception:
        return None


def live_ruleset_contexts(branch: str = "main") -> tuple[str, ...] | None:
    """What the branch ruleset requires RIGHT NOW, or None if it cannot be read.

    Uses GET /repos/{owner}/{repo}/rules/branches/{branch}, which returns the
    EFFECTIVE rules for that branch -- only rulesets whose enforcement is
    active. That is why it is the right endpoint and /rulesets/<id> is not:
    there is no ruleset id to get wrong. Measured 2026-09-05, it answers 200
    even unauthenticated on this public repo, and reports exactly
    ["BC test matrix passed", "Tests updated"] carrying ruleset_id 15001420.
    """
    payload = live_ruleset_payload(branch)
    if payload is None:
        return None
    return contexts_from_branch_rules(payload)


def resolve_required_contexts(branch: str = "main", fetch=None):
    """(contexts, status, notes) -- the required set, and how much to trust it.

    status is one of:

      "live"      the ruleset answered and covers every context in the built-in
                  floor. Use it: a context ADDED in the GitHub UI is waited for
                  on the next invocation rather than ignored (#2785).
      "fallback"  the ruleset could not be read at all. Use the built-in list,
                  loudly. Safe in the one direction that matters -- the built-in
                  list is the FULL set, never a subset of it, and
                  .github/scripts/check_required_contexts.py fails CI if the two
                  ever drift, so it cannot quietly go stale.
      "degraded"  the ruleset answered but came back MISSING a context the
                  built-in floor names. That is the #3002 shape and it is not
                  answerable: a partial read and a context genuinely removed by
                  a person look identical here, and resolving the ambiguity
                  toward the SMALLER set is what turns "every required check
                  that exists has passed" into a vacuous green. The caller must
                  return exit 3 rather than judge on a narrowed set.

    Never returns a set narrower than RULESET_CONTEXTS. Silently narrowing what
    counts as required is the defect, whichever route produced it.
    """
    fetch = fetch or live_ruleset_payload
    notes: list[str] = []
    floor = set(RULESET_CONTEXTS)

    try:
        payload = fetch(branch)
    except Exception:
        payload = None
    live = contexts_from_branch_rules(payload) if payload is not None else None

    if live is None:
        notes.append(
            "note: could not read the live branch ruleset for "
            f"'{branch}'; falling back to the built-in list "
            f"{list(RULESET_CONTEXTS)}. If a required context has been added "
            "since, it is NOT being waited for.")
        return RULESET_CONTEXTS, "fallback", notes

    lost = sorted(floor - set(live))
    if lost:
        notes.append(
            "note: the live branch ruleset answered but did NOT name "
            f"{lost}, which this tool's built-in list does. A required-context "
            "set that is a SUBSET of the known one is not usable: a partial "
            "read and a deliberate removal look the same from here, and "
            "judging on the smaller set is how a vacuous GREEN gets printed "
            "(#3002). If the context really was removed, update "
            "RULESET_CONTEXTS in tools/ci-wait.py and "
            "DEFAULT_REQUIRED_CONTEXTS in "
            ".github/scripts/check_required_contexts.py.")
        return RULESET_CONTEXTS, "degraded", notes

    extra = sorted(set(live) - floor)
    if extra:
        notes.append(
            f"note: the live ruleset requires {sorted(live)}, which adds "
            f"{extra} to this script's built-in list "
            f"{sorted(RULESET_CONTEXTS)}. Using the LIVE set. Update "
            "RULESET_CONTEXTS in tools/ci-wait.py and "
            "DEFAULT_REQUIRED_CONTEXTS in "
            ".github/scripts/check_required_contexts.py (#2785).")
    return tuple(live), "live", notes


# FALLBACK only. `main()` asks the live ruleset first, via
# live_ruleset_contexts() above, so a context added to the ruleset by a person in
# the GitHub UI is waited for on the next invocation rather than ignored until
# somebody notices this tuple (#2785). This list is what gets used when that call
# cannot be made -- and the tool says so out loud when it falls back, because a
# stale list here means a required context is not being judged at all.
#
# .github/scripts/check_required_contexts.py fails CI when this tuple and the
# live ruleset disagree in EITHER direction, so it cannot rot silently.
#
# Re-derive by hand with:
#   gh api repos/StefanMaron/BusinessCentral.AL.Runner/rules/branches/main \
#     --jq '[.[]|select(.type=="required_status_checks")
#            |.parameters.required_status_checks[].context]'
# That endpoint reports only ACTIVE rulesets, which is why it is preferred over
# /rulesets/<id>: querying 15039643 ("Copilot review for default branch", which
# is disabled and has no required_status_checks rule) answers with an empty list,
# and emptying this list on the strength of that would restore the exact
# false-green this module exists to prevent. 15001420 is the active "main"
# ruleset if you do want to name an id.
#
# Names containing "(required)" are the bc-tests matrix legs. They are not
# ruleset contexts themselves, but "BC test matrix passed" fails when any of
# them does, so a cancelled leg blocks just as surely.
#
# Renamed from "All BC versions passed" by #3141: a pull request now runs three of the
# eight BC versions (.github/pr-bc-versions.txt), so the old name claimed more than the
# run had measured. This tuple and DEFAULT_REQUIRED_CONTEXTS in
# .github/scripts/check_required_contexts.py must stay identical to each other AND to the
# live ruleset — that guard fails on drift in either direction (#2785).
RULESET_CONTEXTS = (
    "BC test matrix passed",
    "Tests updated",
    "PR title/body must not contain a CI-skip directive",
    "PR body closing references must be correct, both directions",
    "pull_request trigger lists must keep their load-bearing event types",
    "Required contexts must not be cancellable on the same commit",
    "Agent definitions must allowlist the MCP tools they document",
    "tools/ unit tests",
    ".github/scripts/ unit tests",
    "scripts/ unit tests",
)

NOT_FAILURES = ("success", "neutral", "skipped")


def is_required(name: str | None, contexts: tuple[str, ...] = RULESET_CONTEXTS) -> bool:
    name = name or ""
    return "required" in name or name in contexts


class Verdict:
    """One decision about one commit.

    `code` is None while no verdict is available yet -- deliberately distinct
    from 2 (timed out), because "still running" only becomes a reportable answer
    once the caller's deadline passes.
    """

    def __init__(self, code, lines=None, log_target=None, progress=""):
        self.code = code
        self.lines = lines or []
        self.log_target = log_target
        self.progress = progress


def run_id_from(details_url: str | None) -> int | None:
    """Actions WORKFLOW RUN id out of a check-run details_url, or None.

    .../actions/runs/<run-id>/job/<job-id> -- the first number, not the second.
    """
    if not details_url:
        return None
    m = re.search(r"/actions/runs/(\d+)", details_url)
    return int(m.group(1)) if m else None


def recency_key(r: dict) -> tuple[int, int]:
    """How recent a check run is: (workflow run id, check-run id).

    Workflow run ids are monotonic with run CREATION. Check-run ids are not --
    a check run is created when its JOB STARTS, so id order follows job start
    order, which is a different order as soon as two runs overlap. The check-run
    id is therefore only a tie-break within one workflow run, where it correctly
    ranks a re-run attempt above the attempt it replaced.
    """
    return (run_id_from(r.get("details_url")) or 0, r.get("id") or 0)


def newest_per_name(runs: list[dict]) -> dict[str, dict]:
    """The check run a ruleset would read for each context name.

    Keyed on recency_key, NOT on check-run id alone. Ordering by check-run id
    inverts whenever two runs of one workflow overlap on a commit, because the
    id tracks job start rather than run creation. Measured on PR #2742's head
    22e5c13b: Test Matrix run 33964656436 (created 11:56:25) owns check run
    101303131614 -- a higher id than every check run of PR Check run 33964852712
    (created 12:00:55, highest 101303055107).

    Overlapping runs of one workflow on one SHA are not exotic here, they are
    designed in: require-tests.yml produces the required "Tests updated" context
    and deliberately carries NO `concurrency` block (#2726), while triggering on
    'labeled'/'unlabeled'. Reading the older run's conclusion there is #2748, and
    it is wrong in both directions -- a stale failure reported as the verdict, or
    worse, a stale success shadowing the newer run's failure.

    A live inversion on that exact required context, PR #2863's head, three
    concurrent Require Tests runs whose jobs interleaved:

        run 33983248257  check 101352123189
        run 33983255476  check 101352142567   <- older run, HIGHER check id
        run 33983255561  check 101352142543   <- newest run, LOWER check id

    All three concluded `success`, so no verdict was wrong that time. That is
    what this looks like on the day before it matters.

    This is also what makes a superseded cancellation (harmless) distinguishable
    from a cancellation that is still the latest word on its context
    (merge-blocking).
    """
    newest: dict[str, dict] = {}
    for r in runs:
        name = r.get("name") or ""
        cur = newest.get(name)
        if cur is None or recency_key(r) > recency_key(cur):
            newest[name] = r
    return newest


def rollup_is_final(workflow_runs: list[dict] | None) -> bool | None:
    """Has every workflow run for this commit finished? True / False / None.

    None means "could not tell", and it is deliberately distinct from False:
    an unknown must never be resolved toward GREEN (#2807).
    """
    if workflow_runs is None:
        return None
    if not workflow_runs:
        # Nothing registered for the commit yet. The push is seconds old and the
        # rollup is at its emptiest, which is precisely when it lies best.
        return False
    return all(w.get("status") == "completed" for w in workflow_runs)


def superseding_runs(newest: dict[str, dict],
                     contexts: tuple[str, ...],
                     workflow_runs: list[dict] | None) -> list[tuple[str, int]]:
    """Ruleset contexts whose newest check run may already be out of date.

    Returns (context name, in-flight workflow run id) pairs.

    `rollup_is_final` answers "has EVERYTHING for this commit finished", which is
    the right question only while a context is ABSENT from the rollup. It is the
    wrong question once the context is present, and that gap was a fourth way to
    report a false GREEN (#2807 follow-up): a required context sitting in the
    rollup with a conclusion from workflow run N, while run N+1 of the SAME
    workflow is queued on the same SHA and has not created its check run yet.
    `missing` is empty, so `final` was never consulted, and the stale conclusion
    was returned as the verdict.

    Designed-in, not hypothetical: require-tests.yml produces the required
    "Tests updated" context, carries NO `concurrency` block (deliberately, #2726)
    and triggers on 'labeled'/'unlabeled'. Applying a label mid-wait starts a
    second run on the same commit, and until its job starts the rollup still
    shows the first run's conclusion.

    Deliberately NOT "any workflow run for this commit is unfinished". Five
    workflows here can attach to a branch head without producing any required
    context -- bc-leg-rerun.yml, ms-bucket.yml, ms-bucket-nightly.yml,
    coverage-demo.yml and publish.yml, all reachable by `workflow_dispatch`.
    .claude/rules/ci-verdicts.md actively tells agents to dispatch
    bc-leg-rerun.yml against the branch to get a second opinion on a leg, and the
    ms-bucket runs are 9,500 tests apiece. Blocking green on those would trade one
    false GREEN for a class of false "still pending" on the exact diagnostic path
    the rules recommend. So the in-flight run must be a newer run of the SAME
    workflow that produced the evidence being read.
    """
    if not workflow_runs:
        return []
    pending = [w for w in workflow_runs if w.get("status") != "completed"]
    if not pending:
        return []
    by_id = {w.get("id"): w for w in workflow_runs}
    out: list[tuple[str, int]] = []
    for c in contexts:
        r = newest.get(c)
        if r is None:
            continue  # absent entirely -- that is `missing`, judged by finality
        backing = run_id_from(r.get("details_url"))
        if backing is None:
            # No run id to compare against, so we cannot rule out that one of the
            # in-flight runs will re-report this context. Unknown never resolves
            # toward green (#2807).
            wid = pending[0].get("id")
            out.append((c, wid if isinstance(wid, int) else 0))
            continue
        owner = by_id.get(backing)
        wf_name = owner.get("name") if owner else None
        for w in pending:
            wid = w.get("id")
            if not isinstance(wid, int) or wid <= backing:
                continue
            # Same workflow => this run re-reports `c` and outranks what we read.
            # Owner unknown (run list truncated or the id absent) => cannot rule
            # it out, so assume it does.
            if wf_name is None or w.get("name") == wf_name:
                out.append((c, wid))
                break
    return out


def supersession(newest: dict[str, dict],
                 workflow_runs: list[dict] | None) -> tuple[set[str], set[str]]:
    """(superseded, killed) -- names whose newest check run is not a live verdict.

    Both sets answer the ONE question behind all three #3002 misreports: is the
    check run we are about to judge the CURRENT HEAD'S LIVE RUN's word on this
    name, or something else that happens to be sitting in the same rollup?

    superseded
        A newer run of the SAME workflow is still IN FLIGHT on this commit, so
        the conclusion we are reading belongs to a run that is being replaced.
        The window between the newer run starting and its check run appearing is
        exactly when the rollup lies. No verdict for these names, in EITHER
        direction. (A newer run that has already completed without producing a
        check run for the name supersedes nothing -- the older entry is then the
        latest word and a ruleset reads it, so it must stay judgeable.)

    killed
        The workflow run that produced the check run has run-level conclusion
        `cancelled`. The job was killed, not judged. This matters far more than
        it sounds: a cancelled Test Matrix run's aggregate job "All BC versions
        passed" concludes **failure**, not `cancelled`, because it runs
        `if: always()` over `needs` that were cancelled. Recorded on PR #3010's
        head 95c16b20 -- run 34002828792 is `cancelled` at the run level and its
        aggregate job is `failure`, while the live run 34004261321 was green
        throughout. Read literally, that failure is a FAILED verdict complete
        with an offer to fetch the log of a job nobody ever ran to completion.

        A killed run's conclusions are therefore reclassified as `cancelled`,
        which routes them to the exit-4 "blocked, not failing" path when they
        are still the latest word, and to "no verdict yet" when a replacement is
        on its way. Neither is ever a green.

        Deliberate trade-off: a job that genuinely failed on its merits inside a
        run somebody then cancelled is reported as blocked rather than failed.
        That errs toward "not a verdict", never toward green, and the required
        aggregate will not report for a cancelled run anyway -- a new run is the
        only way forward from there regardless.

    #3003 measured 35 of the last 40 `Test Matrix` runs on `main` cancelled as
    superseded, so neither of these is an edge case here.
    """
    if not workflow_runs:
        return set(), set()
    by_id = {w.get("id"): w for w in workflow_runs if isinstance(w.get("id"), int)}
    # Only a run that has NOT finished can still overturn what we are reading.
    # A newer run of the same workflow that has already COMPLETED without
    # producing a check run for this name is not superseding anything -- the
    # older entry is then genuinely the latest word, and a ruleset reads it.
    # Withholding there would stall every verdict behind a job that was skipped.
    latest_pending_of_workflow: dict[object, int] = {}
    for w in workflow_runs:
        wid = w.get("id")
        if not isinstance(wid, int) or w.get("status") == "completed":
            continue
        name = w.get("name")
        if wid > latest_pending_of_workflow.get(name, -1):
            latest_pending_of_workflow[name] = wid

    superseded: set[str] = set()
    killed: set[str] = set()
    for name, r in newest.items():
        rid = run_id_from(r.get("details_url"))
        if rid is None:
            continue
        owner = by_id.get(rid)
        if owner is None:
            # The run list is truncated or the run is not in it. Unknown never
            # resolves toward a verdict on its own; superseding_runs() below
            # still applies its own conservative rule for ruleset contexts.
            continue
        if latest_pending_of_workflow.get(owner.get("name"), rid) > rid:
            superseded.add(name)
        elif owner.get("conclusion") == "cancelled":
            killed.add(name)
    return superseded, killed


def discount_reason(name: str, superseded: set[str], killed: set[str]) -> str | None:
    """Why a non-passing conclusion is NOT this commit's word on `name`, or None.

    THE INVARIANT, and the only place it is decided:

        A `failure` may be discounted ONLY when the workflow run that PRODUCED
        it is itself cancelled, or is being replaced by a newer run of the same
        workflow that is still in flight. NEVER on the strength of some OTHER
        check run carrying the same context name.

    Both admissible reasons are properties of the *producing run*, which is why
    both sets arrive from supersession() keyed on the run behind the check run.
    "Some other entry for this name was cancelled" is not on the list, and #3142
    is what happens when it is used as though it were: on PR #3112's head
    c6377b30 a GREEN verdict listed `preflight.py unit tests` under
    "Superseded ... Harmless" because a *sibling* entry for that name was
    cancelled, while its newest check run had concluded `failure` inside a run
    that was itself `failure` -- neither cancelled nor superseded. Nothing in
    the output said anything had failed.

    That misreport could not change the merge decision on #3112 only because the
    absorbed context was not a ruleset context. One ruleset edit -- exactly what
    resolve_required_contexts() reads live, with nobody editing this file -- is
    all that separates the two payloads, and #3002 exists because three wrong
    verdicts in one night were caught by a human rather than by this tool.

    Returning None means "this is a real verdict on the merits". Callers must
    treat that as undiscountable in EVERY direction: it cannot be dropped from
    the failing list, and it cannot be printed as harmless noise.
    """
    if name in superseded:
        return ("the run that produced it is being replaced by a newer run of "
                "the same workflow, still in flight")
    if name in killed:
        return "the workflow run that produced it was cancelled"
    return None


def classify(runs: list[dict],
             contexts: tuple[str, ...] = RULESET_CONTEXTS,
             workflow_runs: list[dict] | None = None) -> Verdict:
    """Turn a commit's check runs into one verdict. Pure -- see tools/test_ci_wait.py.

    `contexts` is what the branch ruleset requires; `main()` passes the LIVE set
    so a context added to the ruleset is judged without anyone editing this file
    (#2785). `workflow_runs` is every workflow run registered for the commit, and
    it exists to answer one question the check-run rollup cannot: is a required
    context that is not in the rollup still coming, or will it never come?

    The pool used to be "every check whose name contains 'required'", which is
    the 8 bc-tests legs and nothing else. That silently excluded BOTH ruleset
    contexts: a failing "Tests updated" was reported GREEN. Ruleset contexts are
    judged too -- and, since #2807, waited FOR. Judging only the contexts already
    in the rollup meant that seconds after a push, when the only completed check
    was an 8-second "Tests updated", the pool was one item deep, complete and
    clean, and this returned GREEN saying "all 1 required checks passed" while
    every BC leg was still queued.
    """
    floor = set(RULESET_CONTEXTS)
    if not floor <= set(contexts):
        # #3002. "Every required check that EXISTS has passed" is vacuously true
        # when the set of required checks has been narrowed, and that is how a
        # green naming ONE check got printed twice in one night while a whole
        # matrix was still queued. A required-context set that does not cover
        # the built-in floor is not a set this tool can judge on, so it does not
        # judge -- it says it cannot tell.
        lost = sorted(floor - set(contexts))
        return Verdict(3, [
            "UNDETERMINED -- the required-context set is narrower than the one "
            "this tool knows about, so no verdict can be trusted:",
            f"  missing from the set being judged: {', '.join(lost)}",
            "",
            "Judging on a narrowed set makes 'every required check passed' "
            "vacuously true, which is how a GREEN naming a single check gets "
            "printed while eight legs are still queued (#3002). Re-run this "
            "tool; if it repeats, the branch ruleset read is degrading.",
        ], progress="required-context set narrower than the built-in floor")

    if not runs:
        return Verdict(None)

    # Judge the NEWEST run per context name and nothing else -- that is what a
    # ruleset reads, and a commit routinely carries several attempts at the same
    # context. Measured against PR #2740's head 6b95477f: "Tests updated" is
    # present twice, `failure` and then `skipped` after a no-tests-needed label
    # was applied. GitHub took the newer `skipped` and the PR merged; scanning
    # every entry instead would report that merged PR as FAILED.
    raw_newest = newest_per_name(runs)
    superseded, killed = supersession(raw_newest, workflow_runs)

    # A killed run's conclusions are reclassified as `cancelled` BEFORE anything
    # is judged, so every downstream branch -- failure, block, green -- sees the
    # same truth: that run was stopped, its word is not a verdict on its merits.
    newest: dict[str, dict] = {}
    # What a reclassified check run actually said, kept so the exit-4 advice can
    # tell "nobody ever ran this to completion" (re-running destroys no log) from
    # "this job failed on its merits inside a run somebody then cancelled" (it
    # has a real failing log, and `gh run rerun` would overwrite it permanently).
    killed_originals: dict[str, str] = {}
    for n, r in raw_newest.items():
        if n in killed and r.get("conclusion") != "cancelled":
            killed_originals[n] = r.get("conclusion") or "?"
            r = dict(r)
            r["conclusion"] = "cancelled"
        newest[n] = r

    pool = [r for n, r in newest.items() if is_required(n, contexts)] or list(newest.values())
    done = [r for r in pool if r.get("status") == "completed"]
    # A superseded name is excluded from `bad` outright. `bad` short-circuits
    # ahead of every in-flight guard below -- deliberately, since a failure is a
    # verdict -- so a guard placed after it never gets consulted, which is
    # precisely how #3010's stale failure was reported (the residual noted as
    # #2922, now closed in the one direction that was actually misreporting).
    bad = [r for r in done if r.get("conclusion") not in NOT_FAILURES
           and r.get("conclusion") != "cancelled"
           and discount_reason(r.get("name") or "", superseded, killed) is None]
    missing = [c for c in contexts if c not in newest]
    final = rollup_is_final(workflow_runs)
    inflight = superseding_runs(newest, contexts, workflow_runs)

    progress = f"{len(done)}/{len(pool)} complete, {len(bad)} failing"
    if missing:
        progress += (f", {len(missing)} ruleset context(s) not in the rollup yet: "
                     + ", ".join(missing))
    if inflight:
        progress += (", superseding run in flight for: "
                     + ", ".join(f"{c} (run {w})" for c, w in inflight))
    if superseded:
        progress += (", newest check run already superseded for: "
                     + ", ".join(sorted(superseded)))
    if killed:
        progress += (", conclusion came from a CANCELLED workflow run for: "
                     + ", ".join(sorted(killed)))

    if bad:
        # This list is what is known SO FAR. Returning it while other required
        # checks are still running is correct -- a failure is a verdict -- but
        # reading it as the complete failing set is not, and someone did exactly
        # that with a single-leg failure that turned out to be eight.
        # A LOWER BOUND, and it has to say so. `pool` is built from ruleset
        # contexts plus the rollup entries already present, so a required leg
        # that has not created its check run yet is counted by neither term. The
        # #2837 shape printed "1 required check(s) have not reported yet" while
        # seven bc-tests legs were missing from the rollup entirely.
        unreported = (len(pool) - len(done)) + len(missing)
        head = f"{len(bad)} of {len(pool)} required checks failed"
        if unreported:
            head += (f" SO FAR (at least {unreported} required check(s) have not "
                     f"reported yet)")
        lines = [head + ":"]
        for r in bad:
            rid = run_id_from(r.get("details_url"))
            lines.append(f"  {r['name']}: {r.get('conclusion')}"
                         + (f"   (workflow run {rid})" if rid else ""))
        if unreported:
            lines += [
                "",
                "This failing list can still GROW -- it names only the required checks",
                "that have already reported. The count above is a LOWER BOUND: a leg",
                "whose check run does not exist yet is not in it at all. Do not scope",
                "a diagnosis to these names until every check has reported; re-run",
                "this tool, or read `gh pr checks` once the run finishes.",
            ]
        if inflight:
            # #2922. The verdict stays exit 1 -- a failure is a verdict, and
            # holding every genuinely-red PR back for a queued run costs real
            # time on the common case. But the reader is now TOLD that a newer
            # run of the same workflow is in flight on this commit, because the
            # conclusion above is not necessarily the one a ruleset will end up
            # reading.
            #
            # The concrete sequence, from #2922: require-tests.yml produces the
            # required `Tests updated` context, carries no `concurrency` block
            # (#2726) and triggers on 'labeled'. A `no-tests-needed` label
            # applied mid-wait starts a second run on the same SHA; the first
            # run's `failure` is what the rollup still shows, and the second
            # reports `skipped`, satisfying the ruleset. Reporting the failure
            # was right; reporting it without this caveat sent the reader to a
            # log that had already been overtaken.
            #
            # Only the annotation is new. superseding_runs() already computed
            # these pairs for the guards below -- the failure branch
            # short-circuits ahead of them, so it was discarding information it
            # already held.
            lines += [
                "",
                "CAVEAT: a newer run of the same workflow is still in flight on this",
                "commit, so the conclusion above may be overturned before a ruleset",
                "reads it:",
            ]
            lines += [f"  {c}: superseded by workflow run {w}" for c, w in inflight]
            lines += [
                "Re-run this tool once that run finishes before treating the failure",
                "above as final. Do NOT `gh run rerun` the failing run -- that destroys",
                "its log permanently (.claude/rules/ci-verdicts.md).",
            ]
        return Verdict(1, lines, log_target=bad[0], progress=progress)

    if superseded:
        # Some name's newest check run belongs to a run that a newer run of the
        # same workflow has already replaced. Whatever it says -- pass, fail or
        # cancel -- it is not the live run's word, and the live run's word is
        # still coming.
        return Verdict(None, progress=progress)

    if len(done) != len(pool):
        # A cancellation seen mid-run is usually a run being superseded while its
        # replacement is still queued; the replacement outranks it as soon as it
        # reports. Calling that a block here would cry wolf on every push.
        return Verdict(None, progress=progress)

    if missing and final is not True:
        # #2807: "has not appeared yet" and "will never appear" look identical in
        # the rollup, and the tie used to be broken toward GREEN -- the one
        # direction .claude/rules/ci-verdicts.md says a verdict may never go.
        # Unknown (final is None, the API could not be read) lands here too.
        return Verdict(None, progress=progress)

    if inflight:
        # A required context IS in the rollup, but a newer run of the workflow
        # that produced it is still in flight on this commit, so what we just
        # read is not necessarily the conclusion a ruleset will read. Applies to
        # every verdict below: GREEN, and both BLOCKED paths -- a `cancelled`
        # required context whose replacement run is queued reads as exit 4 here
        # ("re-run the cancelled run") when the re-run is already on its way.
        # A FAILURE is deliberately still reported above: a failure is a verdict,
        # and delaying it costs real time, while the risk of the newer run
        # overturning it is bounded and visible in the caveat. That residual --
        # a stale FAILED where a newer run of the same workflow is queued, the
        # mirror of the false green this guard fixes -- is tracked in #2922
        # rather than widened into this change.
        return Verdict(None, progress=progress)

    blocking = sorted(
        (r for r in newest.values()
         if r.get("conclusion") == "cancelled" and is_required(r.get("name"), contexts)),
        key=lambda r: r.get("name") or "",
    )
    # DELIBERATELY not the verdict-path `superseded` -- this used to shadow that
    # name with a far weaker rule and is #3142. A cancelled entry is "harmlessly
    # superseded" only when the newer run that replaced it actually re-reported
    # the context as PASSING. If the newer run said `failure`, the failure is the
    # commit's word on that name (discount_reason() returns None for it) and it
    # belongs in `undiscounted` below, not under the heading "Harmless".
    resolved_cancellations = sorted(
        {(r.get("name") or "") for r in runs
         if r.get("conclusion") == "cancelled"
         and newest.get(r.get("name") or "", {}).get("id") != r.get("id")
         and newest.get(r.get("name") or "", {}).get("conclusion") in NOT_FAILURES}
    )
    # Real failures on this commit that nothing legitimately discounts. Every
    # REQUIRED one is already gone -- it would have returned exit 1 above -- so
    # what is left is non-required, gates no merge, and is exactly the red X a
    # reader has to be told about rather than left to find. #3137 has had one of
    # these red on every PR for days, which is how a tool got a real failure to
    # absorb in the first place.
    undiscounted = sorted(
        (r for r in newest.values()
         if r.get("status") == "completed"
         and r.get("conclusion") not in NOT_FAILURES
         and r.get("conclusion") != "cancelled"
         and discount_reason(r.get("name") or "", superseded, killed) is None),
        key=lambda r: r.get("name") or "",
    )
    cosmetic = sorted(
        {(r.get("name") or "") for r in newest.values()
         if r.get("conclusion") == "cancelled" and not is_required(r.get("name"), contexts)}
    )

    if blocking:
        lines = [
            f"BLOCKED, not failing -- {len(blocking)} REQUIRED context(s) are "
            f"'cancelled' on this commit and nothing else reports it:",
        ]
        lines += [f"  {r['name']}: cancelled" for r in blocking]
        lines += [
            "",
            "Every other check passed. A ruleset reads the NEWEST check run per",
            "context name, and 'cancelled' does not satisfy a required check, so the",
            "merge is refused while `gh pr checks` still reads SUCCESS (#2726).",
            "",
            "Re-run the cancelled run -- this is the ONE case where `gh run rerun`",
            "can be correct, and only while nothing on this commit concluded",
            "`failure` before the cancellation: a check run that concluded",
            "`failure` on its merits has a real log, and a re-run destroys it",
            "like any other. The check to run first is in",
            ".claude/rules/ci-verdicts.md section 3.",
            "Do NOT reach for --admin; branch protection is working, the context",
            "genuinely is not satisfied.",
        ]
        # ...with one exception, and it is the expensive one to get wrong. A
        # check run that concluded `failure` on its merits inside a run somebody
        # then cancelled is reported as blocked rather than failed (the
        # deliberate trade-off in supersession(), which errs away from green).
        # It DOES have a failing log, so the blanket advice above would send a
        # reader to overwrite the one piece of evidence permanently.
        real = [r for r in blocking if (r.get("name") or "") in killed_originals]
        if real:
            lines += [
                "",
                "CAREFUL -- these were NOT merely stopped. Their check run concluded",
                "on its merits inside a workflow run that was cancelled afterwards,",
                "so a real log exists and `gh run rerun` WOULD overwrite it:",
            ]
            lines += [f"  {r['name']}: {killed_originals[r['name']]} "
                      f"(inside a cancelled run)" for r in real]
            lines.append("Read those logs BEFORE re-running anything.")
        for r in blocking:
            if r.get("details_url"):
                lines.append(f"  {r['details_url']}")
        return Verdict(4, lines, progress=progress)

    if missing:
        # final is True here: every workflow run for the commit has finished and
        # these contexts still produced no check run at all. The ruleset has
        # nothing to read for them, so it refuses the merge and, exactly as with
        # a cancellation, nothing else says why.
        lines = [
            f"BLOCKED, not failing -- {len(missing)} REQUIRED context(s) produced no "
            f"check run on this commit:",
        ]
        lines += [f"  {c}: never reported" for c in missing]
        lines += [
            "",
            "Every check that did report passed, and every workflow run for this",
            "commit has finished, so nothing further is coming. A ruleset has no",
            "check run to read for these contexts, so the merge is refused with",
            "nothing stating the reason (#2807).",
            "",
            "Find the workflow that is supposed to produce each name and why it did",
            "not run for this commit -- a `paths:` filter, a `branches:` filter, or a",
            "trigger type that did not fire. Do NOT reach for --admin.",
        ]
        return Verdict(4, lines, progress=progress)

    uniq = list(dict.fromkeys(contexts))
    accounted = [c for c in uniq if c in newest]
    if len(accounted) != len(uniq):
        # Unreachable: `missing` is empty by here. Kept as a hard invariant --
        # a green that cannot account for every ruleset context BY NAME is not a
        # green. Both #3002 false greens named ONE check where every real green
        # that night named nine or ten, and that asymmetry is the only reason a
        # human caught them.
        return Verdict(None, progress=progress)
    if any(is_required(r.get("name"), contexts)
           and r.get("conclusion") not in NOT_FAILURES
           for r in newest.values()):
        # Unreachable: a required failure returned exit 1 and a required
        # cancellation returned exit 4 above. Kept as a hard invariant, because
        # what it forbids is the exact thing #3142 did in the printed output --
        # a green standing over a required context that is not passing. If a
        # future discount rule ever lets one through, this says "cannot tell"
        # rather than green.
        return Verdict(None, progress=progress)

    lines = [f"all {len(pool)} required checks passed "
             f"({len(accounted)}/{len(uniq)} ruleset context(s) accounted for)."]
    lines.append("Ruleset contexts confirmed present and passing on this commit: "
                 + ", ".join(uniq) + ".")
    if undiscounted:
        # Printed BEFORE the two "this is noise" sections, and never merged into
        # them: these are genuine failures that happen not to gate the merge, and
        # a reader who learns to skim past red is how the next real one is missed.
        lines.append("")
        lines.append(f"NOT required, but genuinely FAILING on this commit -- "
                     f"{len(undiscounted)} check(s). The merge is not gated on "
                     f"them, and they are NOT superseded noise:")
        for r in undiscounted:
            rid = run_id_from(r.get("details_url"))
            lines.append(f"  {r['name']}: {r.get('conclusion')}"
                         + (f"   (workflow run {rid})" if rid else ""))
    if cosmetic:
        lines.append("")
        lines.append("Cosmetic: these NON-required contexts are 'cancelled' on this "
                     "commit and do not block a merge:")
        lines += [f"  {n}" for n in cosmetic]
    if resolved_cancellations:
        lines.append("")
        lines.append("Superseded: these contexts have a 'cancelled' entry on this "
                     "commit that a newer run already re-reported as passing. "
                     "Harmless.")
        lines += [f"  {n}" for n in resolved_cancellations]
    return Verdict(0, lines, progress=progress)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pr")
    ap.add_argument("--timeout", type=int, default=1800)
    ap.add_argument("--interval", type=int, default=25)
    ap.add_argument("--no-log", action="store_true")
    # Skips only the `git ls-remote` that confirms the local origin/main ref
    # against the remote. It does NOT disable the staleness check itself: there
    # is no flag for that, because the only legitimate reason to want one -- a
    # branch that edits this tool -- is already not refused.
    ap.add_argument("--no-freshness-fetch", action="store_true")
    args = ap.parse_args()

    # BEFORE anything is asked of GitHub: is the copy of this file that is
    # running actually current? A stale copy answers confidently and wrongly,
    # and #3002's false GREEN is still on disk in most of this box's worktrees
    # (#3020). A refusal here is exit 3 -- undetermined, never a verdict.
    if _freshness is None:
        print("note: could not establish whether this copy of ci-wait.py is current "
              "-- tools/agent_self_freshness.py could not be imported. Answering "
              "anyway; nothing has checked that this copy carries the latest fixes.")
    else:
        # Both halves, not just this file. The verdict logic lives here, but the
        # freshness rule itself lives in the sibling module, and a stale copy of
        # THAT is a stale guard -- the same blind spot one level down. The remote
        # confirmation is asked for once and reused, so this costs one ls-remote.
        refused = False
        confirm = not args.no_freshness_fetch
        for target in (os.path.abspath(__file__),
                       os.path.abspath(_freshness.__file__)):
            fresh = _freshness.assess(target, remote_check=confirm)
            confirm = False  # one ls-remote, not one per file
            for note in fresh.notes:
                print(note)
            refused = refused or fresh.refuse
        if refused:
            print("\nREFUSING to judge PR #%s from a STALE ci-wait.py. This is NOT a "
                  "verdict -- nothing was asked of GitHub." % args.pr)
            return 3

    sha = head_sha(args.pr)
    if not sha:
        print(f"could not read PR #{args.pr} head SHA", file=sys.stderr)
        return 3
    print(f"PR #{args.pr} head {sha[:8]} -- waiting for required checks "
          f"(up to {args.timeout}s, polling internally)")

    # Ask the ruleset what it requires RIGHT NOW rather than trusting a tuple
    # frozen into this file (#2785) -- but never accept an answer NARROWER than
    # the built-in floor, because judging on a reduced required set is what
    # makes "every required check passed" vacuously true (#3002).
    contexts, ctx_status, ctx_notes = resolve_required_contexts()
    for note in ctx_notes:
        print(note)
    if ctx_status == "degraded":
        print("\ncould not establish the required-context set for 'main' -- refusing "
              "to judge this PR on a narrower one. This is NOT a verdict.")
        return 3

    deadline = time.time() + args.timeout
    last = ""
    while time.time() < deadline:
        # ORDER MATTERS: the workflow-run list is read FIRST, the check-run
        # rollup second. Read the other way round, a run completing between the
        # two calls gives a STALE rollup (context still missing) next to a FRESH
        # run list (final=True), which classify() reads as "every workflow
        # finished and this context never reported" -- a false exit 4 sending an
        # agent after a trigger filter that is fine. Run-list-first makes the
        # skew harmless in both directions: the rollup is then the newer of the
        # two, so a context it shows as reported really is reported, and a run
        # the list still calls in-flight has at worst already finished, which
        # only costs one more poll.
        #
        # Fetched unconditionally. It used to be fetched only while a ruleset
        # context was missing from the rollup, on the reasoning that this was the
        # only question the run list answered. It was not: a context PRESENT in
        # the rollup can be carrying a conclusion from a superseded run (see
        # superseding_runs), and that question has to be asked on every poll.
        wf_runs = workflow_runs_for(sha)
        if wf_runs is None:
            # The run list is load-bearing for the verdict now, so failing to read
            # it is "no verdict yet", never "green" (#2807).
            time.sleep(args.interval)
            continue
        runs = required_checks(sha)
        if runs is None:
            time.sleep(args.interval)
            continue
        v = classify(runs, contexts, wf_runs)
        if v.progress:
            last = v.progress  # only reported at the end; keeps the transcript to one block

        if v.code == 1:
            print(f"\nFAILED on {sha[:8]} -- " + v.lines[0])
            for line in v.lines[1:]:
                print(line)
            if not args.no_log:
                # The check-run `id` is NOT the Actions job id that `gh run view --job`
                # wants; passing it fails with "could not find job". The job id is the
                # trailing path segment of details_url (.../actions/runs/<run>/job/<job>).
                job = job_id_from((v.log_target or {}).get("details_url"))
                rc, out = (gh(["run", "view", "--repo", REPO, "--log-failed", "--job", job])
                           if job else (1, ""))
                if rc == 0 and out:
                    tail = out.split("\n")[-120:]
                    print("\n--- failing log (tail) ---")
                    print("\n".join(tail))
                else:
                    where = job or "<job id>"
                    print("\n(could not fetch the failing log; read it with "
                          f"`gh run view --repo {REPO} --log-failed --job {where}`)")
                    if (v.log_target or {}).get("details_url"):
                        print(f" or open {v.log_target['details_url']}")
            print("\nNEVER `gh run rerun` -- it overwrites this log permanently. "
                  "Push a new commit for a fresh run.")
            return 1

        if v.code == 3:
            print(f"\nUNDETERMINED on {sha[:8]} -- " + v.lines[0])
            for line in v.lines[1:]:
                print(line)
            return 3

        if v.code == 4:
            print(f"\nBLOCKED on {sha[:8]} -- " + v.lines[0].split(" -- ", 1)[1])
            for line in v.lines[1:]:
                print(line)
            return 4

        if v.code == 0:
            print(f"\nGREEN on {sha[:8]} -- " + v.lines[0])
            for line in v.lines[1:]:
                print(line)
            print("Confirm this SHA is still the PR head before reporting it.")
            return 0

        time.sleep(args.interval)

    print(f"\nSTILL RUNNING after {args.timeout}s ({last}). "
          "This is NOT a verdict -- call again; do not report a result.")
    return 2


if __name__ == "__main__":
    sys.exit(main())
