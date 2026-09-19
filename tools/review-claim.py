#!/usr/bin/env python3
"""Is this PR already reviewed, or being reviewed right now? (#4284)

`check-open-prs-before-claiming.md` gives three signals for IMPLEMENTATION
ownership -- the assignee, the `agent:` label, and an open PR carrying
`Closes #N`, the last deciding. There is no equivalent for REVIEW. A PR at
`status: review-ready` reads identically whether nobody is reviewing it or
three agents already are, because that label means *ready for review* and is
never rewritten while a review is in flight.

WHY A COMPLETION-TIME SIGNAL IS THE WRONG HALF -- measured, not assumed

Over the 60 most recent pull requests on 2026-09-19, 59 carry at least one
`Verdict:` comment. Counting only repeats on the IDENTICAL head SHA, where
nothing about the PR changed between the two passes and the second is therefore
redundant by construction:

    18 of 59 PRs (31%)  carry >=2 verdicts on one head -- 19 such pairs
    17 of the 19 pairs  are less than 15 minutes apart; median 5.5 min
     2 of the 19 pairs  are 28.5 and 29.1 minutes apart

A review takes about 15 minutes here (`orchestrating-a-session`: ~15.6 min/PR).
So in 17 of 19 cases the second reviewer STARTED while the first was still
running, and a label or comment written when a review FINISHES would have been
written too late to prevent any of them. #4306 is the clean instance: two
verdicts on head 4cfe233e, 123 seconds apart.

That is why the claim is posted at START. It is deliberately the same shape as
the issue-claiming compare-and-swap in `check-open-prs-before-claiming.md`:
claim, re-read, release if someone else's claim appeared.

    Reviewing: <agent-id> — head <full 40-char sha>

posted as a plain comment before the review begins, which puts it in the same
comment stream as the `Verdict:` line, so ONE read answers both "is anyone
reviewing this" and "has anyone reviewed this". The coordinator's sweep listing
carries neither today.

WHAT THIS IS NOT

It is not a lock, and it must never become one. A second independent pass is
sometimes exactly right: the fourth pass on #4281 produced findings the first
three did not, and the second reviewer on #4306 picked mutations overlapping
neither the first's nor the author's. The defect is that the duplication is
UNCHOSEN and UNBOUNDED -- nobody decided to spend four passes. So this reports;
it never refuses, has no --force to route around, and `--post` will happily
write a second claim beside an existing one. What changes is that spending the
second pass is now a decision someone made.

THE ANSWERS (`guards-need-a-third-state.md`)

    0  FREE       -- no live claim and no verdict on this head; review away
    1  CLAIMED    -- someone is reviewing it now, or has already reviewed this
                     exact head. Not an error: it is the signal. Read the
                     printed lines and decide.
    3  UNREADABLE -- the comments could not be fetched or parsed. NOT exit 0:
                     "nobody is reviewing it" and "I could not find out" send a
                     coordinator to opposite actions, and only one of them is
                     safe to act on (#4284).

Exit 2 is deliberately unused, so that a `--timeout`-style "still running" from
a neighbouring tool can never be read as a verdict here.

A CLAIM IS ABOUT A HEAD, AND IT EXPIRES

A claim naming a head that is no longer the PR's head is reported as STALE and
does not hold: the commit it was about is gone, so a fresh pass is exactly what
should happen. A claim older than --max-age minutes (default 45, three times the
measured 15.6 min/PR) is reported as EXPIRED for the same reason -- a reviewer
that died mid-pass must not lock a PR forever, which is the failure mode the
abandoned-draft clause in `check-open-prs-before-claiming.md` exists to avoid.

Usage
    tools/review-claim.py --pr 4306                     # read the signal
    tools/review-claim.py --pr 4306 --post --agent-id stma-auto-2
    tools/review-claim.py --pr 4306 --head <sha>        # a head you already read

Run the guard: python3 tools/test_review_claim.py
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import subprocess
import sys

# A claim line carries an em dash, and a reviewer's agent id is free text.
# Printing either through the console codec raises UnicodeEncodeError on a
# cp1252 box -- the failure `ci-verdicts.md` records for a tool copied without
# this sibling, where the exit code then means something else entirely.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    _stdio.enable_utf8_stdio()

FREE, CLAIMED, UNREADABLE = 0, 1, 3

# Start of line, bare, no leading whitespace and no bold -- the same anchor
# `review-verdict.py` uses and for the same reason: indentation is how markdown
# marks quoted content, so a reviewer quoting an earlier claim inside a list or
# block quote must not read as issuing one now.
CLAIM = re.compile(r'^Reviewing:(?P<rest>.*)$')
VERDICT = re.compile(r'^Verdict:(?P<rest>.*)$')

# `— head <sha>`: the em dash is what the agent definitions specify, but a
# hyphen is accepted because the two are indistinguishable to a reader and an
# agent retyping the line will produce either.
HEAD = re.compile(r'[—-]\s*head\s+(?P<sha>[0-9a-fA-F]+)\s*$')

DEFAULT_MAX_AGE_MIN = 45


def _parse_line(pattern: re.Pattern, line: str) -> tuple[str, str] | None:
    """Return (payload, head) for a marker line, or None when it is unusable."""
    m = pattern.match(line)
    if not m:
        return None
    rest = m.group("rest").strip()
    h = HEAD.search(rest)
    if not h:
        return None
    sha = h.group("sha")
    if len(sha) != 40:
        # An abbreviated head cannot be compared against `headRefOid` without
        # guessing, and a claim about a head nobody can identify holds nothing.
        return None
    return rest[:h.start()].strip(), sha.lower()


def scan(comments: list[dict]) -> tuple[list[dict], list[dict]]:
    """Every well-formed claim and verdict, oldest first."""
    claims: list[dict] = []
    verdicts: list[dict] = []
    for c in comments:
        # GitHub returns comment bodies with CRLF. Strip the \r here rather
        # than at the point of use: a carriage return left on the end of a SHA
        # makes a compare against `headRefOid` fail while the two look
        # identical printed.
        body = (c.get("body") or "").replace("\r\n", "\n")
        for line in body.split("\n"):
            got = _parse_line(CLAIM, line)
            if got:
                claims.append({"agent": got[0], "head": got[1],
                               "at": c.get("created_at", ""), "line": line.strip()})
                continue
            got = _parse_line(VERDICT, line)
            if got:
                verdicts.append({"decision": got[0], "head": got[1],
                                 "at": c.get("created_at", ""), "line": line.strip()})
    return claims, verdicts


def _age_minutes(stamp: str, now: datetime.datetime) -> float | None:
    try:
        when = datetime.datetime.fromisoformat(stamp.replace("Z", "+00:00"))
    except (ValueError, AttributeError):
        return None
    return (now - when).total_seconds() / 60.0


def classify(claims: list[dict], verdicts: list[dict], head: str,
             max_age: float, now: datetime.datetime) -> list[dict]:
    """Every reason this PR is claimed, in the order a reader should see them.

    A claim is LIVE only while it is about the CURRENT head and young enough
    that the reviewer holding it can still be running. Everything else is
    reported with the reason it does not hold, because "there is a claim and it
    is stale" and "there is no claim" are different facts about the PR and a
    reader deciding whether to spend a review needs to tell them apart.
    """
    head = (head or "").lower()
    out: list[dict] = []
    done_heads = {v["head"] for v in verdicts}
    for c in claims:
        if c["head"] != head:
            state = "STALE"
        elif c["head"] in done_heads:
            # The reviewer holding it has posted its verdict on this head, so
            # it is no longer running. The verdict below is the live signal.
            state = "DONE"
        else:
            age = _age_minutes(c["at"], now)
            if age is None:
                # An unparseable timestamp is not evidence the claim expired.
                # Treat it as live: over-reporting CLAIMED costs a second look,
                # while under-reporting costs two reviewers on one PR.
                state = "LIVE"
            elif age > max_age:
                state = "EXPIRED"
            else:
                state = "LIVE"
        out.append(dict(c, kind="claim", state=state))
    for v in verdicts:
        out.append(dict(v, kind="verdict",
                        state="THIS-HEAD" if v["head"] == head else "OLDER-HEAD"))
    return out


def holds(rows: list[dict]) -> bool:
    """True when something about this PR should make a reviewer stop and think."""
    return any((r["kind"] == "claim" and r["state"] == "LIVE")
               or (r["kind"] == "verdict" and r["state"] == "THIS-HEAD")
               for r in rows)


def _gh(args: list[str]) -> tuple[int, str, str]:
    try:
        out = subprocess.run(args, capture_output=True, text=True,
                             encoding="utf-8", errors="replace")
    except OSError as exc:  # no `gh` at all -- every web and remote session
        return 127, "", (f"could not run `gh`: {exc}. Without it, read the "
                         "comments yourself and pipe them in with --stdin "
                         "(`github-access.md`).")
    return out.returncode, out.stdout, out.stderr


def fetch(pr: str, repo: str) -> list[dict]:
    rc, stdout, stderr = _gh(["gh", "api", f"repos/{repo}/issues/{pr}/comments",
                              "--paginate"])
    if rc:
        print(f"could not read comments for PR {pr}: {stderr.strip()}",
              file=sys.stderr)
        sys.exit(UNREADABLE)
    try:
        return json.loads(stdout or "[]")
    except json.JSONDecodeError as exc:
        # A truncated or non-JSON response is a failed measurement, not a
        # claim-free PR: returning [] here would print FREE and send a second
        # reviewer at a PR someone may well be reviewing.
        print(f"could not parse the comments for PR {pr}: {exc}", file=sys.stderr)
        sys.exit(UNREADABLE)


def fetch_head(pr: str, repo: str) -> str:
    rc, stdout, stderr = _gh(["gh", "pr", "view", pr, "--repo", repo,
                              "--json", "headRefOid", "--jq", ".headRefOid"])
    if rc:
        print(f"could not read the head of PR {pr}: {stderr.strip()}",
              file=sys.stderr)
        sys.exit(UNREADABLE)
    sha = stdout.strip()
    if not re.fullmatch(r"[0-9a-fA-F]{40}", sha):
        # `mise` prints a banner on stdout, so a capture can hold anything.
        # Comparing claims against a non-SHA would report every claim STALE,
        # which is FREE wearing a measurement's clothes (`CLAUDE.md` § 3b).
        print(f"the head of PR {pr} did not read as a 40-char SHA: {sha!r}",
              file=sys.stderr)
        sys.exit(UNREADABLE)
    return sha.lower()


def post(pr: str, repo: str, agent: str, head: str) -> int:
    line = f"Reviewing: {agent} — head {head}"
    body = (f"{line}\n\nA review pass by `{agent}` is starting on this head. "
            "This is a signal, not a lock: a second pass is fine and sometimes "
            "right, but it should be a decision someone made rather than an "
            "accident (#4284). `tools/review-claim.py --pr <N>` reads it.\n\n"
            "*Comment by an agent on the account holder's behalf.*")
    rc, _, stderr = _gh(["gh", "pr", "comment", pr, "--repo", repo, "--body", body])
    if rc:
        print(f"could not post the claim: {stderr.strip()}", file=sys.stderr)
        return UNREADABLE
    print(line)
    return FREE


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--pr", required=True)
    ap.add_argument("--repo", default="StefanMaron/BusinessCentral.AL.Runner")
    ap.add_argument("--head", help="the head to judge claims against; read from "
                                   "the PR when omitted")
    ap.add_argument("--stdin", action="store_true",
                    help="read the comments JSON array on stdin instead of "
                         "calling `gh` (requires --head)")
    ap.add_argument("--max-age", type=float, default=DEFAULT_MAX_AGE_MIN,
                    help=f"minutes before a claim expires (default "
                         f"{DEFAULT_MAX_AGE_MIN:g})")
    ap.add_argument("--post", action="store_true", help="post a claim")
    ap.add_argument("--agent-id", help="required with --post")
    args = ap.parse_args()

    if args.post and not args.agent_id:
        ap.error("--post needs --agent-id: a claim nobody can attribute holds nothing")
    if args.stdin and not args.head:
        ap.error("--stdin needs --head: claims are judged against a head, and "
                 "reading it would need the `gh` call --stdin exists to avoid")

    if args.stdin:
        try:
            comments = json.loads(sys.stdin.read() or "[]")
        except json.JSONDecodeError as exc:
            print(f"could not parse the comments on stdin: {exc}", file=sys.stderr)
            return UNREADABLE
        head = args.head.lower()
    else:
        head = (args.head or fetch_head(args.pr, args.repo)).lower()
        comments = fetch(args.pr, args.repo)

    claims, verdicts = scan(comments)
    now = datetime.datetime.now(datetime.timezone.utc)
    rows = classify(claims, verdicts, head, args.max_age, now)

    if args.post:
        return post(args.pr, args.repo, args.agent_id, head)

    if not holds(rows):
        # Say what was read, so a FREE that came from an empty comment list is
        # distinguishable from one that read past stale claims.
        print(f"FREE: PR {args.pr} head {head[:8]} -- no live claim and no "
              f"verdict on this head ({len(claims)} claim(s), "
              f"{len(verdicts)} verdict(s) read)")
        for r in rows:
            print(f"  ({r['state'].lower()}) {r['line']}")
        return FREE

    print(f"CLAIMED: PR {args.pr} head {head[:8]} -- a review pass here would "
          f"be the second. Spend it deliberately or pick another PR (#4284).")
    for r in rows:
        print(f"  [{r['state']}] {r['line']}")
    return CLAIMED


if __name__ == "__main__":
    sys.exit(main())
