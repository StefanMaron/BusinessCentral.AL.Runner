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
    3  could not determine state (auth, network, no checks reported)
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

This is the one case in this repository where `gh run rerun` is the right call:
a cancelled run has no failure log to overwrite, so re-running it destroys no
evidence, and it re-reports the context as success within a minute. The workflow
side of #2726 stops required contexts being cancellable in the first place; this
verdict exists for anything that still gets there.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time

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


def live_ruleset_contexts(branch: str = "main") -> tuple[str, ...] | None:
    """What the branch ruleset requires RIGHT NOW, or None if it cannot be read.

    Uses GET /repos/{owner}/{repo}/rules/branches/{branch}, which returns the
    EFFECTIVE rules for that branch -- only rulesets whose enforcement is
    active. That is why it is the right endpoint and /rulesets/<id> is not:
    there is no ruleset id to get wrong. Measured 2026-09-05, it answers 200
    even unauthenticated on this public repo, and reports exactly
    ["All BC versions passed", "Tests updated"] carrying ruleset_id 15001420.
    """
    rc, out = gh(["api", f"repos/{REPO}/rules/branches/{branch}"])
    if rc != 0:
        return None
    try:
        return contexts_from_branch_rules(json.loads(out))
    except Exception:
        return None


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
# ruleset contexts themselves, but "All BC versions passed" fails when any of
# them does, so a cancelled leg blocks just as surely.
RULESET_CONTEXTS = ("All BC versions passed", "Tests updated")

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
    if not runs:
        return Verdict(None)

    # Judge the NEWEST run per context name and nothing else -- that is what a
    # ruleset reads, and a commit routinely carries several attempts at the same
    # context. Measured against PR #2740's head 6b95477f: "Tests updated" is
    # present twice, `failure` and then `skipped` after a no-tests-needed label
    # was applied. GitHub took the newer `skipped` and the PR merged; scanning
    # every entry instead would report that merged PR as FAILED.
    newest = newest_per_name(runs)
    pool = [r for n, r in newest.items() if is_required(n, contexts)] or list(newest.values())
    done = [r for r in pool if r.get("status") == "completed"]
    bad = [r for r in done if r.get("conclusion") not in NOT_FAILURES
           and r.get("conclusion") != "cancelled"]
    missing = [c for c in contexts if c not in newest]
    final = rollup_is_final(workflow_runs)

    progress = f"{len(done)}/{len(pool)} complete, {len(bad)} failing"
    if missing:
        progress += (f", {len(missing)} ruleset context(s) not in the rollup yet: "
                     + ", ".join(missing))

    if bad:
        # This list is what is known SO FAR. Returning it while other required
        # checks are still running is correct -- a failure is a verdict -- but
        # reading it as the complete failing set is not, and someone did exactly
        # that with a single-leg failure that turned out to be eight.
        unreported = (len(pool) - len(done)) + len(missing)
        head = f"{len(bad)} of {len(pool)} required checks failed"
        if unreported:
            head += f" SO FAR ({unreported} required check(s) have not reported yet)"
        lines = [head + ":"]
        for r in bad:
            rid = run_id_from(r.get("details_url"))
            lines.append(f"  {r['name']}: {r.get('conclusion')}"
                         + (f"   (workflow run {rid})" if rid else ""))
        if unreported:
            lines += [
                "",
                "This failing list can still GROW -- it names only the required checks",
                "that have already reported. Do not scope a diagnosis to these names",
                "until every check has reported; re-run this tool, or read",
                f"`gh pr checks` once the run finishes.",
            ]
        return Verdict(1, lines, log_target=bad[0], progress=progress)

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

    blocking = sorted(
        (r for r in newest.values()
         if r.get("conclusion") == "cancelled" and is_required(r.get("name"), contexts)),
        key=lambda r: r.get("name") or "",
    )
    superseded = sorted(
        {(r.get("name") or "") for r in runs
         if r.get("conclusion") == "cancelled"
         and newest.get(r.get("name") or "", {}).get("id") != r.get("id")}
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
            "Re-run the cancelled run -- this is the ONE case where `gh run rerun` is",
            "correct: a cancelled run has no failure log to overwrite. Do NOT reach",
            "for --admin; branch protection is working, the context genuinely is not",
            "satisfied.",
        ]
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

    lines = [f"all {len(pool)} required checks passed."]
    lines.append("Ruleset contexts confirmed present and passing on this commit: "
                 + ", ".join(contexts) + ".")
    if cosmetic:
        lines.append("")
        lines.append("Cosmetic: these NON-required contexts are 'cancelled' on this "
                     "commit and do not block a merge:")
        lines += [f"  {n}" for n in cosmetic]
    if superseded:
        lines.append("")
        lines.append("Superseded: these contexts have a 'cancelled' entry on this "
                     "commit that a newer run already re-reported. Harmless.")
        lines += [f"  {n}" for n in superseded]
    return Verdict(0, lines, progress=progress)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pr")
    ap.add_argument("--timeout", type=int, default=1800)
    ap.add_argument("--interval", type=int, default=25)
    ap.add_argument("--no-log", action="store_true")
    args = ap.parse_args()

    sha = head_sha(args.pr)
    if not sha:
        print(f"could not read PR #{args.pr} head SHA", file=sys.stderr)
        return 3
    print(f"PR #{args.pr} head {sha[:8]} -- waiting for required checks "
          f"(up to {args.timeout}s, polling internally)")

    # Ask the ruleset what it requires RIGHT NOW rather than trusting a tuple
    # frozen into this file (#2785). A context added to the ruleset since this
    # was written must be waited for, not silently ignored.
    live = live_ruleset_contexts()
    if live is None:
        contexts = RULESET_CONTEXTS
        print("note: could not read the live branch ruleset for 'main'; falling back "
              f"to the built-in list {list(RULESET_CONTEXTS)}. If a required context "
              "has been added since, it is NOT being waited for.")
    else:
        contexts = live
        if set(live) != set(RULESET_CONTEXTS):
            print(f"note: the live ruleset requires {sorted(live)}, which differs from "
                  f"this script's built-in list {sorted(RULESET_CONTEXTS)}. Using the "
                  "LIVE set. Update RULESET_CONTEXTS in tools/ci-wait.py and "
                  "DEFAULT_REQUIRED_CONTEXTS in "
                  ".github/scripts/check_required_contexts.py (#2785).")

    deadline = time.time() + args.timeout
    last = ""
    while time.time() < deadline:
        runs = required_checks(sha)
        if runs is None:
            time.sleep(args.interval)
            continue
        # Only worth an extra round trip while a ruleset context is missing from
        # the rollup -- that is the only question the workflow-run list answers.
        present = {r.get("name") for r in runs}
        wf_runs = (workflow_runs_for(sha)
                   if any(c not in present for c in contexts) else None)
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
