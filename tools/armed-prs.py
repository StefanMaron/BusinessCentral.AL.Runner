#!/usr/bin/env python3
"""Report the armed pull requests that are NOT green. One question, one answer.

Why this exists
---------------
A PR armed with `--auto` lands the moment its required checks pass, with nobody
present. That is the point of arming, and it is safe: `--auto` holds at BLOCKED
and merges nothing red. What it does not do is **tell anyone** when an armed PR
goes red after the fact -- the reviewer has returned, the coordinator has moved
on, and the PR sits armed and failing until somebody happens to sweep.

Measured, rather than suspected (#4006): two instances in one hour sat armed and
red for **12 and 119 minutes** before a manual `ci-wait.py` pass found them --
on a night with an attentive coordinator sweeping every few ticks and **nineteen
PRs armed at once**. The consequence is not a bad merge but the harder thing to
notice: a silently stalled PR. Nothing is red on `main`, nothing is broken, and
the work simply does not land.

This is the two calls such a sweep already makes, with nobody having to remember
to make them.

A second silent stall, same shape (#4206)
----------------------------------------
An armed PR can also sit still while **green on every required context**. The
advisory gate `A cited corpus PR must be able to merge` evaluates on
push/edited/labeled and never when the corpus PR it names moves -- and
corpus-first merging guarantees the corpus PR merges AFTER the runner PR's last
push. The stored `failure` then outlives its cause, and because a failing
NON-required check makes `mergeStateStatus` UNSTABLE,
`enablePullRequestAutoMerge` refuses. `ci-wait.py` says GREEN, prints `corpus PR
#N: MERGED` beside it, and the merge is refused: three instruments, none wrong,
and nothing pointing at the tick.

Measured three times in one session -- #4135/#348, #4202/#368, and #4203/#371,
where the gate concluded `failure` at 20:04:28Z, the corpus PR merged at
20:21:08Z, and a body edit at 20:50:05Z produced a fresh `success`. So this tool
reports a STALE gate even on an exit-0 verdict, and `--refire-stale-corpus-gate`
clears it by toggling a label rather than pushing a commit.

Usage
-----
    tools/armed-prs.py              # the whole answer, no arguments
    tools/armed-prs.py --all        # also list the quiet ones, to see the set
    tools/armed-prs.py --json       # machine-readable rows
    tools/armed-prs.py --refire-stale-corpus-gate   # clear the stale ticks

What it does NOT do
-------------------
It reports. It does not disarm, merge, re-run, schedule itself, or act on
anything it finds -- deliberately, because the repair sequence is **disarm ->
repair -> re-arm on a fresh verdict** (#4006), and the ordering matters: pushing
a fix to a still-armed red PR makes the fix commit itself the green thing that
triggers the merge, at a head nobody reviewed. Choosing to disarm is a merge
decision, and a merge decision stays with the coordinator.

`--refire-stale-corpus-gate` is the one action, and it is deliberately not an
exception to that. It changes no code, moves no head, and merges nothing: it
re-asks a question whose answer is already known to have changed, on a check
that is advisory. The merge decision it unblocks still belongs to whoever armed
the PR, and `--auto` still holds at a red required context.

Exit codes
----------
    0  every armed PR is green or still running -- nothing to do
    1  at least one armed PR is FAILING (`ci-wait.py` exit 1 or 4)
    3  at least one armed PR's verdict COULD NOT BE READ, and none is failing

A STALE corpus gate is reported but does NOT move the exit code off 0: every
verdict was established and every one is green, so claiming 3 would assert
nobody measured it, which is false and sends the reader to the wrong remedy.

Exit 2 from `ci-wait.py` -- required checks still running -- is the **ordinary**
state of an armed PR and is deliberately quiet. Reporting it would have named
twenty-one PRs on the night that motivated this tool, and a report that names
everything is ignored, which is the same as not existing.

Exit 3 is deliberately not folded into 0. An armed PR whose verdict could not be
read is not "fine" -- nobody measured it, and calling an unmeasured thing green
is the defect `guards-need-a-third-state.md` exists to prevent. It gets its own
exit code and its own visibly distinct line, because the remedy differs: a
FAILING PR needs disarming, an UNREADABLE one needs asking again.

Where the verdict comes from
----------------------------
`tools/ci-wait.py <N> --timeout 0` per armed PR, whose exit code carries the
whole answer -- read directly via `subprocess.run(...).returncode`, never
through a pipe. `tools/ci-wait.py <N> --timeout 0 | tail` leaves `$?` as
**tail's** status, which is 0 whatever the verdict was; `ci-verdicts.md` §0
records that biting twice in one night, once being the coordinator.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import subprocess
import sys
from dataclasses import dataclass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    _stdio.enable_utf8_stdio()

REPO = "StefanMaron/BusinessCentral.AL.Runner"
HERE = os.path.dirname(os.path.abspath(__file__))

# The three verdicts. Distinct values, because collapsing UNREADABLE into either
# neighbour is the whole defect: into OK it asserts a PR is fine having failed to
# measure it, into FAILING it sends a coordinator to disarm a PR that may be green.
OK = "ok"
FAILING = "failing"
UNREADABLE = "unreadable"

# ci-wait.py's exit codes, per its own docstring and `ci-verdicts.md` §0. Only
# these five are mapped; anything else falls through to UNREADABLE below, which
# is what makes a future exit code safe to add there before it is handled here.
_RC = {
    0: OK,          # every required check passed on the current head
    2: OK,          # no verdict yet -- the ordinary state of an armed PR
    1: FAILING,     # a required check failed
    4: FAILING,     # green but blocked, and nothing else says why
    3: UNREADABLE,  # could not determine (auth, network, narrowed ruleset, stale copy)
}


# The label toggled to re-fire pr-gate.yml. `labeled` and `unlabeled` are both
# in that workflow's trigger list and are verified there by its own
# verify-trigger-list job, so adding and removing this label fires the gate
# twice without moving the head.
REFIRE_LABEL = "status: review-ready"


@dataclass
class Row:
    number: int
    verdict: str
    rc: int
    head: str = ""
    armed_for: str = "unknown"
    # The stale-corpus-gate verdict for this PR (#4206), one of
    # "STALE" / "CURRENT" / "UNKNOWN", or None when it was never asked.
    corpus_gate: str | None = None


def classify(rc: int) -> str:
    """Map a `ci-wait.py` exit code to one of the three verdicts.

    An UNMAPPED code is UNREADABLE, never OK. That direction is the point: an
    exit code this tool does not recognise means the verdict was not
    established, and the failure mode worth avoiding is a new or anomalous code
    -- a signal death reported as a negative returncode, a future exit 5 --
    being silently absorbed into the green set.
    """
    return _RC.get(rc, UNREADABLE)


def armed(prs: list[dict]) -> list[dict]:
    """The PRs with auto-merge enabled: `autoMergeRequest` present and non-null."""
    return [p for p in prs if p.get("autoMergeRequest")]


def armed_for(enabled_at: str | None, now: str | None = None) -> str:
    """How long an arming has stood, as `1h59m` / `12m`, or `unknown`.

    `unknown` rather than `0m` for an absent or unparseable timestamp: a report
    saying a PR was armed seconds ago when nobody knows is worse than one saying
    nobody knows, because it is the reading that makes a stale arming look fresh.
    """
    if not enabled_at:
        return "unknown"
    try:
        then = _dt.datetime.fromisoformat(enabled_at.replace("Z", "+00:00"))
        ref = (_dt.datetime.fromisoformat(now.replace("Z", "+00:00")) if now
               else _dt.datetime.now(_dt.timezone.utc))
    except (ValueError, AttributeError):
        return "unknown"
    mins = int((ref - then).total_seconds() // 60)
    if mins < 0:
        return "unknown"
    return f"{mins // 60}h{mins % 60:02d}m" if mins >= 60 else f"{mins}m"


def refire_one(number: int, run=None) -> tuple[bool, str]:
    """Re-fire pr-gate.yml on PR `number` by toggling a label. (#4206)

    A LABEL toggle, deliberately, and never an empty commit. A push moves the
    head: it restarts the whole BC matrix (~15 minutes), and it re-arms
    auto-merge against a head nobody has reviewed -- `orchestrating-a-session`
    records both costs. `labeled` and `unlabeled` are in pr-gate.yml's trigger
    list, so removing and re-adding a label re-runs every job in that file on
    the SAME commit, which is what makes the stored conclusion fresh again.

    Add-then-remove is not used: the label is the coordinator's own
    `status: review-ready`, and leaving it ON is its normal state for a PR that
    is armed. So remove, then add -- the PR ends carrying it either way, and the
    intermediate state is the one that lasts milliseconds.

    Returns (did it fire, reason). A failure is returned, never raised: this
    runs inside a sweep over every armed PR and one broken call must not hide
    the rest.
    """
    run = run or _gh
    rc, out = run(["pr", "edit", str(number), "--repo", REPO,
                   "--remove-label", REFIRE_LABEL])
    if rc != 0:
        return False, f"could not remove {REFIRE_LABEL!r}: {out[:200]}"
    rc, out = run(["pr", "edit", str(number), "--repo", REPO,
                   "--add-label", REFIRE_LABEL])
    if rc != 0:
        # The label is OFF and the re-add failed. Say so loudly and name the
        # repair: a PR silently missing `status: review-ready` drops out of the
        # queries the coordinator finds review-ready work with.
        return False, (f"removed {REFIRE_LABEL!r} but could NOT re-add it -- "
                       f"add it back by hand on #{number}: {out[:200]}")
    return True, ""


def refire_stale(rows, refire=None) -> list[tuple[int, bool, str]]:
    """Re-fire every row whose corpus gate is STALE. Returns what it did.

    ONLY "STALE". "UNKNOWN" is deliberately not re-fired: it means the
    comparison never happened -- an unreadable corpus PR, or no stored
    conclusion at all -- and re-firing on it would act on a state nobody
    measured, which is the failure `guards-need-a-third-state.md` names. The
    cost of not acting is one manual re-fire; the cost of acting on an unmeasured
    state is a gate cleared for a reason nobody can reconstruct.
    """
    refire = refire or refire_one
    done: list[tuple[int, bool, str]] = []
    for r in rows:
        if getattr(r, "corpus_gate", None) != "STALE":
            continue
        try:
            ok, why = refire(r.number)
        except Exception as exc:  # one broken call must not abort the sweep
            ok, why = False, f"{type(exc).__name__}: {exc}"
        done.append((r.number, ok, why))
    return done


def _gh(args: list[str]) -> tuple[int, str]:
    """`gh` with the mise banner stripped, like _gh_pr_list below (CLAUDE.md)."""
    p = subprocess.run(["gh", *args], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    out = (p.stdout or "") + (p.stderr or "")
    out = "\n".join(l for l in out.split("\n") if not l.startswith("mise "))
    return p.returncode, out.strip()


def _gh_pr_list() -> list[dict]:
    """Open PRs with the three fields a sweep needs. Exits 3 on a failed read."""
    p = subprocess.run(
        ["gh", "pr", "list", "--repo", REPO, "--state", "open", "--limit", "100",
         "--json", "number,autoMergeRequest,headRefOid"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0:
        print(f"could not list pull requests: {(p.stderr or '').strip()}",
              file=sys.stderr)
        sys.exit(3)
    # mise prints a banner on stdout, which is not JSON (CLAUDE.md).
    body = "\n".join(l for l in (p.stdout or "").split("\n")
                     if not l.startswith("mise "))
    try:
        return json.loads(body)
    except json.JSONDecodeError as exc:
        print(f"could not parse the pull-request list: {exc}", file=sys.stderr)
        sys.exit(3)


def _ci_wait_rc(number: int, timeout_s: int = 240) -> int:
    """`ci-wait.py <N> --timeout 0`'s exit code, read directly.

    `.returncode`, never a pipeline's `$?`, and never `check=True`: a non-zero
    exit IS the answer here, so raising on it would discard every verdict this
    tool exists to collect.
    """
    p = subprocess.run(
        [sys.executable, os.path.join(HERE, "ci-wait.py"), str(number),
         "--timeout", "0", "--no-log"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=timeout_s)
    # (rc, stdout) since #4206: the exit code is still the whole verdict, and the
    # text carries the `corpus gate:` line, which no exit code can. Reading it
    # here costs nothing -- the subprocess already ran and the output was already
    # captured.
    return p.returncode, (p.stdout or "")


GATE_MARKER = "corpus gate:"


def gate_from_output(text: str) -> str | None:
    """The stale-gate verdict in ci-wait.py's output, or None. (#4206)

    None means "no gate line", which covers both the CURRENT case (that verdict
    prints nothing, deliberately) and output from a ci-wait.py predating the
    line. Either way the answer refire_stale needs is the same: not stale.

    The marker must START the line. A PR body quoting the phrase reaches this
    text through a failing log, and matching mid-line would read a PR's own
    prose about staleness as a measurement of it -- the shape
    check-open-prs-before-claiming.md records for a generic body pattern.
    """
    for line in (text or "").splitlines():
        if not line.startswith(GATE_MARKER):
            continue
        rest = line[len(GATE_MARKER):].strip()
        verdict = rest.split(" ", 1)[0].strip()
        if verdict in ("STALE", "UNKNOWN"):
            return verdict
    return None


def sweep(prs: list[dict], ci_wait_rc=_ci_wait_rc) -> tuple[int, list[Row]]:
    """Read every armed PR's verdict. Returns (exit code, rows worth reporting).

    Only armed PRs are handed to `ci-wait.py`: each one is a subprocess making
    live API calls, and asking about the unarmed ones would have spent 35 of
    them to answer a question about 21 (measured on the live queue, 2026-09-13).
    """
    rows: list[Row] = []
    for p in armed(prs):
        number = p["number"]
        try:
            answer = ci_wait_rc(number)
            # (rc, stdout) since #4206; a bare int is the older shape and still
            # works, with the gate verdict simply unknown.
            if isinstance(answer, tuple):
                rc, text = answer
            else:
                rc, text = answer, ""
        except Exception as exc:
            # A ci-wait.py that could not run at all is exactly the third state:
            # this PR's verdict was not established. One PR's broken read must
            # not abort the sweep, or a single unreachable PR hides every other
            # armed-and-red one behind it.
            rows.append(Row(number, UNREADABLE, 3, p.get("headRefOid", "")[:8],
                            f"could not run ci-wait.py: {exc}"))
            continue
        verdict = classify(rc)
        gate = gate_from_output(text)
        # A STALE gate is reported even on an OK verdict, and that is the whole
        # point: the PR is green on every REQUIRED context -- so ci-wait.py says
        # GREEN -- while the failing advisory gate makes mergeStateStatus
        # UNSTABLE and auto-merge refuses. Quiet here is exactly the 46-minute
        # silence measured on #4203 (#4206).
        if verdict == OK and gate != "STALE":
            continue
        rows.append(Row(number, verdict, rc, p.get("headRefOid", "")[:8],
                        armed_for((p.get("autoMergeRequest") or {}).get("enabledAt")),
                        corpus_gate=gate))
    if any(r.verdict == FAILING for r in rows):
        return 1, rows
    if any(r.verdict == UNREADABLE for r in rows):
        return 3, rows
    # A row that is only here for a STALE gate is neither failing nor unreadable:
    # every verdict WAS established and every one is green. Returning 3 would
    # claim nobody measured it, which is false and sends a coordinator to the
    # wrong remedy (#4206). The report names it; the exit code stays honest.
    return 0, rows


def report(rows: list[Row]) -> str:
    """The human report. Empty for an empty sweep -- never an all-clear string.

    An empty string is what lets a caller print nothing at all on the quiet
    path, which is what keeps a tool run every sweep from becoming noise.
    """
    if not rows:
        return ""
    out = []
    failing = [r for r in rows if r.verdict == FAILING]
    unreadable = [r for r in rows if r.verdict == UNREADABLE]
    if failing:
        out.append("FAILING -- armed and not green. Disarm before repairing:")
        out.append("  (a fix pushed to a still-armed PR is itself the green "
                   "thing that merges it, at a head nobody reviewed)")
        for r in failing:
            out.append(f"  #{r.number}  head {r.head}  armed {r.armed_for} ago"
                       f"  (ci-wait.py exit {r.rc})")
    if unreadable:
        if out:
            out.append("")
        out.append("UNREADABLE -- could not read the verdict. NOT established "
                   "as green; ask again:")
        for r in unreadable:
            out.append(f"  #{r.number}  head {r.head}  armed {r.armed_for} ago"
                       f"  (ci-wait.py exit {r.rc})")
    stale = [r for r in rows if getattr(r, "corpus_gate", None) == "STALE"]
    if stale:
        if out:
            out.append("")
        out.append("STALE CORPUS GATE -- green on every REQUIRED context, and "
                   "auto-merge refuses anyway (#4206):")
        out.append("  (the cited corpus PR has merged since the gate last ran. "
                   "The gate is advisory, so no required")
        out.append("   context is red -- but a failing non-required check makes "
                   "mergeStateStatus UNSTABLE, which")
        out.append("   enablePullRequestAutoMerge refuses. Re-fire it: "
                   "tools/armed-prs.py --refire-stale-corpus-gate)")
        for r in stale:
            out.append(f"  #{r.number}  head {r.head}  armed {r.armed_for} ago"
                       f"  (ci-wait.py exit {r.rc})")
    return "\n".join(out)


def main() -> int:
    p = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--all", action="store_true",
                   help="also print the armed PRs that are green or running")
    p.add_argument("--json", action="store_true", dest="as_json",
                   help="print rows as JSON")
    p.add_argument("--refire-stale-corpus-gate", action="store_true",
                   dest="refire",
                   help="re-fire the cited-corpus-PR gate on every armed PR whose "
                        "gate is STALE, by toggling a label (never a commit). Only "
                        "STALE: an UNKNOWN gate was never established as stale (#4206)")
    args = p.parse_args()

    prs = _gh_pr_list()
    all_armed = armed(prs)
    rc, rows = sweep(prs)

    if args.as_json:
        print(json.dumps(
            {"armed": len(all_armed), "exit": rc,
             "rows": [r.__dict__ for r in rows]}, indent=2))
        return rc

    quiet = len(all_armed) - len(rows)
    print(f"{len(all_armed)} armed pull request(s): {quiet} green or still "
          f"running, {len(rows)} needing attention")
    text = report(rows)
    if text:
        print()
        print(text)
    if args.refire:
        fired = refire_stale(rows)
        if not fired:
            print("\nno armed pull request has a STALE corpus gate -- nothing "
                  "re-fired")
        for number, ok, why in fired:
            if ok:
                print(f"\nre-fired the corpus gate on #{number} (label toggled; "
                      "the head did not move)")
            else:
                # Loud: the remove may have succeeded where the re-add failed,
                # which leaves the PR missing a label the coordinator queries on.
                print(f"\ncould NOT re-fire the corpus gate on #{number}: {why}",
                      file=sys.stderr)

    if args.all and all_armed:
        flagged = {r.number for r in rows}
        rest = [a for a in all_armed if a["number"] not in flagged]
        if rest:
            print()
            print("quiet (green or still running):")
            print("  " + " ".join(f"#{a['number']}" for a in rest))
    return rc


if __name__ == "__main__":
    sys.exit(main())
