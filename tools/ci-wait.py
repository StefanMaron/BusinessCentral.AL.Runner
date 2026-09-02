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
        req = [r for r in runs if "required" in (r.get("name") or "")]
        pool = req or runs
        if not pool:
            time.sleep(args.interval)
            continue
        done = [r for r in pool if r.get("status") == "completed"]
        bad = [r for r in done if r.get("conclusion") not in ("success", "neutral", "skipped")]
        state = f"{len(done)}/{len(pool)} complete, {len(bad)} failing"
        if state != last:
            last = state  # only reported at the end; keeps the transcript to one block
        if bad:
            print(f"\nFAILED on {sha[:8]} -- {len(bad)} of {len(pool)} required checks:")
            for r in bad:
                print(f"  {r['name']}: {r.get('conclusion')}")
            if not args.no_log:
                # The check-run `id` is NOT the Actions job id that `gh run view --job`
                # wants; passing it fails with "could not find job". The job id is the
                # trailing path segment of details_url (.../actions/runs/<run>/job/<job>).
                job = job_id_from(bad[0].get("details_url"))
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
                    if bad[0].get("details_url"):
                        print(f" or open {bad[0]['details_url']}")
            print("\nNEVER `gh run rerun` -- it overwrites this log permanently. "
                  "Push a new commit for a fresh run.")
            return 1
        if len(done) == len(pool):
            print(f"\nGREEN on {sha[:8]} -- all {len(pool)} required checks passed.")
            print("Confirm this SHA is still the PR head before reporting it.")
            return 0
        time.sleep(args.interval)

    print(f"\nSTILL RUNNING after {args.timeout}s ({last}). "
          "This is NOT a verdict -- call again; do not report a result.")
    return 2


if __name__ == "__main__":
    sys.exit(main())
