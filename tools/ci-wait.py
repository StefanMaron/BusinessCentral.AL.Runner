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
    4  everything green, but a REQUIRED context is `cancelled` on this commit --
       the merge is blocked and nothing else reports why (#2726)

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


# The contexts the `main` branch ruleset requires, verbatim. Re-derive with:
#   gh api repos/StefanMaron/BusinessCentral.AL.Runner/rulesets/15001420 \
#     --jq '[.rules[]|select(.type=="required_status_checks")
#            |.parameters.required_status_checks[].context]'
# 15001420 is the ACTIVE "main" ruleset and the only one carrying a
# required_status_checks rule. Do NOT query 15039643 ("Copilot review for
# default branch"): it is disabled and has no such rule, so it answers with an
# empty list, and emptying this list on the strength of that would restore the
# exact false-green this module exists to prevent.
# (see .github/scripts/check_required_contexts.py, which carries the exact query
# and is the CI guard for the same list).
#
# Names containing "(required)" are the bc-tests matrix legs. They are not
# ruleset contexts themselves, but "All BC versions passed" fails when any of
# them does, so a cancelled leg blocks just as surely.
RULESET_CONTEXTS = ("All BC versions passed", "Tests updated")

NOT_FAILURES = ("success", "neutral", "skipped")


def is_required(name: str | None) -> bool:
    name = name or ""
    return "required" in name or name in RULESET_CONTEXTS


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


def newest_per_name(runs: list[dict]) -> dict[str, dict]:
    """The check run a ruleset would read for each context name.

    Check-run ids on a commit increase with creation, so the highest id for a
    name is the newest attempt at that context. This is what makes a superseded
    cancellation (harmless) distinguishable from a cancellation that is still the
    latest word on its context (merge-blocking).
    """
    newest: dict[str, dict] = {}
    for r in runs:
        name = r.get("name") or ""
        cur = newest.get(name)
        if cur is None or (r.get("id") or 0) > (cur.get("id") or 0):
            newest[name] = r
    return newest


def classify(runs: list[dict]) -> Verdict:
    """Turn a commit's check runs into one verdict. Pure -- see tools/test_ci_wait.py.

    The pool used to be "every check whose name contains 'required'", which is
    the 8 bc-tests legs and nothing else. That silently excluded BOTH ruleset
    contexts: a failing "Tests updated" was reported GREEN. Ruleset contexts are
    now judged too, but only once they have actually appeared in the rollup --
    a docs-only PR skips "Tests updated" at job level, and waiting for a context
    that will never report would turn a green PR into a timeout.
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
    pool = [r for n, r in newest.items() if is_required(n)] or list(newest.values())
    done = [r for r in pool if r.get("status") == "completed"]
    bad = [r for r in done if r.get("conclusion") not in NOT_FAILURES
           and r.get("conclusion") != "cancelled"]
    progress = f"{len(done)}/{len(pool)} complete, {len(bad)} failing"

    if bad:
        lines = [f"{len(bad)} of {len(pool)} required checks failed:"]
        lines += [f"  {r['name']}: {r.get('conclusion')}" for r in bad]
        return Verdict(1, lines, log_target=bad[0], progress=progress)

    if len(done) != len(pool):
        # A cancellation seen mid-run is usually a run being superseded while its
        # replacement is still queued; the replacement outranks it as soon as it
        # reports. Calling that a block here would cry wolf on every push.
        return Verdict(None, progress=progress)

    blocking = sorted(
        (r for r in newest.values()
         if r.get("conclusion") == "cancelled" and is_required(r.get("name"))),
        key=lambda r: r.get("name") or "",
    )
    superseded = sorted(
        {(r.get("name") or "") for r in runs
         if r.get("conclusion") == "cancelled"
         and newest.get(r.get("name") or "", {}).get("id") != r.get("id")}
    )
    cosmetic = sorted(
        {(r.get("name") or "") for r in newest.values()
         if r.get("conclusion") == "cancelled" and not is_required(r.get("name"))}
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

    lines = [f"all {len(pool)} required checks passed."]
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

    deadline = time.time() + args.timeout
    last = ""
    while time.time() < deadline:
        runs = required_checks(sha)
        if runs is None:
            time.sleep(args.interval)
            continue
        v = classify(runs)
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
