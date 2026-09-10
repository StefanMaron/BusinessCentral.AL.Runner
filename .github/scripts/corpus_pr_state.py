#!/usr/bin/env python3
"""What happened to the corpus pull request this PR body cites? (#3674)

check_corpus_linkage.sh checks that a `Corpus-PR:` line was DECLARED. Nothing
checked what became of the pull request it names, and the retrospective measured
the consequence: 4 of 68 post-gate declarations merged while their corpus PR was
still open (#3428/#3430 landed 19.6 h and 18.6 h before corpus #272/#273), and
four merged runner PRs (#2318, #2638, #2778, #2837) cite corpus PRs #85, #137
and #153 that closed WITHOUT merging -- so those surfaces never received a
service-tier verdict and nothing flags them today.

This module is the mechanical read, and it is the ONLY one: pr-gate.yml runs it
as a script, tools/ci-wait.py imports it and prints its lines beside the PR
verdict, and tools/corpus-citation-sweep.py uses it over merged PRs. One answer,
three callers.

THE SIX ANSWERS
---------------
    NONE             the body declares no corpus PR (the ordinary case)
    MERGED           the corpus PR merged; a real service tier adjudicated it
    MERGEABLE        open, no conflict, every REQUIRED leg green
    NOT-MERGEABLE    open with a red or unreported required leg, conflicting,
                     behind, or still a draft -- or closed... no: see below
    CLOSED-UNMERGED  closed without merging; the claim has no verdict at all
    UNREADABLE       the measurement did not happen (guards-need-a-third-state.md)

WHY MERGEABLE IS A PASS AND NOT A FAILURE
-----------------------------------------
The issue was written when the runner repository still pinned the corpus, and it
asked for a gate that fails while the corpus PR is merely OPEN. #3737 dropped the
pin, so a runner PR now measures the head of the corpus PR it names and the two
land in the SAME coordinator step. Failing on an open-and-green corpus PR would
therefore fail every well-formed PR for the whole window between opening it and
merging it -- the noisy direction check_corpus_linkage.sh's header warns about,
where the guard gets pasted past. What is left, and what has actually gone wrong,
is a corpus PR that CANNOT merge: red or unreported required legs, a conflict, or
one that closed unmerged.

The ARMING bar is stricter and stays stricter: `orchestrating-a-session`'s arming
list requires MERGED before auto-merge is armed, because by then the coordinator
has merged the corpus PR itself. CI cannot require that -- the runner PR's checks
run long before the coordinator gets there -- so the two bars are deliberately
different, and this module reports the state rather than one verdict for both.

WHAT DECIDES `mergeable_state`
------------------------------
GitHub's own answer, not a leg count of our own. `blocked` is what a REQUIRED
status check that is red or has not reported produces, which on the corpus is one
of the eight cloud legs; `unstable` is a non-required check failing, which on the
corpus is one of the eight OnPrem legs, and the merge bar accepts that. Measured
2026-09-10 through the REST API, which is the same call
.github/actions/resolve-corpus-ref/action.yml already makes cross-repo with
`github.token`:

    #319  open   merged=false mergeable=true  mergeable_state=clean
    #305  closed merged=true  mergeable=null  mergeable_state=unknown
    #85   closed merged=false mergeable=true  mergeable_state=blocked
    #137  closed merged=false mergeable=true  mergeable_state=blocked
    #153  closed merged=false mergeable=true  mergeable_state=blocked

Two things that mapping has to get right, and both are pinned in
test_corpus_pr_state.py. A merged PR reports `mergeable: null` and
`mergeable_state: unknown`, so `merged` must be read FIRST or every merged corpus
PR reads UNREADABLE. And #85/#137/#153 still report `mergeable_state: blocked`
long after closing, so `state` must be read before `mergeable_state` or a
withdrawn corpus PR reads as a merely-red one.

`mergeable: null` is GitHub's documented "the merge commit is still being
computed, ask again", so it is retried here and only becomes UNREADABLE after the
retries.

WHY THE `Corpus-PR:` REGEX IS NOT IN THIS FILE
----------------------------------------------
check_corpus_linkage.sh owns it, and owning it once is the point: the shape it
accepts is the shape authors are told to write, and a second parser here would
disagree with the gate silently -- a body the gate calls well-formed that this
module does not see would report NONE, which is a pass. So both of that script's
extraction modes are invoked rather than reimplemented, exactly as
resolve_corpus_ref.sh does. The marker-count-versus-URL-count comparison is the
same five lines as in resolve_corpus_ref.sh; it is duplicated rather than shared
because that script answers a different question (which ONE corpus a run
measures, refusing two) and returning per-PR answers here would change its
contract.

Usage
-----
    PR_BODY="$(gh pr view <N> --json body --jq .body)" \
        python3 .github/scripts/corpus_pr_state.py

Reads PR_BODY from the environment. It may be empty, but it must be passed: an
unset body means the caller never read one, and a body nobody read declares
nothing, which would pass every pull request in the repository.

Exit codes
----------
    0  every cited corpus PR is MERGED or MERGEABLE, or none is cited
    1  at least one is NOT-MERGEABLE or CLOSED-UNMERGED
    2  usage -- PR_BODY was not passed at all
    3  at least one could not be read, and none of them failed outright

Exit 1 outranks exit 3 when both occur: a definite failure is a verdict and the
unreadable one is a refusal, and every line is printed either way, so nothing is
hidden by the ordering.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass

CORPUS_REPO = "StefanMaron/BusinessCentral.AL.Language.Tests"
HERE = os.path.dirname(os.path.abspath(__file__))
LINKAGE = os.path.join(HERE, "check_corpus_linkage.sh")

# A definite failure, a refusal, and a pass. Ordered so `max` over the codes a
# body produced picks exit 1 over exit 3 over 0.
_RANK = {"NONE": 0, "MERGED": 0, "MERGEABLE": 0, "UNREADABLE": 3,
         "NOT-MERGEABLE": 1, "CLOSED-UNMERGED": 1}


@dataclass
class Entry:
    """One corpus pull request, as this module sees it."""

    number: int
    state: str
    head: str = ""
    detail: str = ""


def format_line(entry: Entry) -> str:
    """The one line every caller prints. tools/ci-wait.py prints exactly this."""
    head = entry.head[:8] if entry.head else ""
    where = f"head {head}" if head else "head unknown"
    tail = f" -- {entry.detail}" if entry.detail else ""
    return f"corpus PR #{entry.number}: {entry.state} ({where}){tail}"


def _bash() -> str | None:
    """The bash that runs check_corpus_linkage.sh, or None.

    On CI this is just `bash`. On a Windows agent box python may be launched from
    PowerShell, where Git's bash is often not on PATH even though git is -- so the
    two standard Git-for-Windows locations are tried before giving up. Giving up
    is a refusal, never an empty answer.
    """
    found = shutil.which("bash")
    if found:
        return found
    for candidate in (r"C:\Program Files\Git\bin\bash.exe",
                      r"C:\Program Files\Git\usr\bin\bash.exe"):
        if os.path.isfile(candidate):
            return candidate
    return None


def _linkage(mode: str, body: str, script: str) -> tuple[str | None, str]:
    """One extraction mode of check_corpus_linkage.sh. (stdout, reason-if-failed)."""
    if not os.path.isfile(script):
        return None, (f"cannot find check_corpus_linkage.sh at {script}; it owns the "
                      "Corpus-PR regex, and refusing is the point -- a copy of that "
                      "regex here would answer while disagreeing with the gate")
    shell = _bash()
    if shell is None:
        return None, ("no bash found to run check_corpus_linkage.sh, which owns the "
                      "Corpus-PR regex; refusing rather than parsing the body here")
    env = dict(os.environ)
    env["PR_BODY"] = body
    try:
        p = subprocess.run([shell, script, mode], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", env=env)
    except Exception as exc:  # noqa: BLE001 - a broken invocation is a refusal
        return None, f"could not run check_corpus_linkage.sh {mode}: {exc!r}"
    if p.returncode != 0:
        return None, (f"check_corpus_linkage.sh {mode} exited {p.returncode}: "
                      f"{(p.stderr or '').strip()[:200]}")
    return p.stdout, ""


def corpus_pr_numbers(body: str, script: str | None = None
                      ) -> tuple[list[int] | None, str]:
    """(the corpus PR numbers this body declares, reason) -- None means REFUSED.

    An empty list is an answer: this body declares nothing. None is not -- a
    malformed `Corpus-PR:` line, or a parse that could not run, must never come
    back looking like "no declaration", because that is a pass.
    """
    script = script or LINKAGE
    urls_out, why = _linkage("--print-corpus-pr-urls", body, script)
    if urls_out is None:
        return None, why
    markers_out, why = _linkage("--print-corpus-pr-marker-lines", body, script)
    if markers_out is None:
        return None, why

    urls = [u for u in urls_out.splitlines() if u.strip()]
    markers = [m for m in markers_out.splitlines() if m.strip()]
    if len(markers) > len(urls):
        bad = "; ".join(m.strip() for m in markers)
        return None, (f"this body carries {len(markers)} 'Corpus-PR:' line(s) but only "
                      f"{len(urls)} of them is a well-formed corpus pull request URL, so "
                      f"the state of the corpus PR you named cannot be read. Write it as "
                      f"one line: Corpus-PR: https://github.com/{CORPUS_REPO}/pull/<N>. "
                      f"Lines carrying the marker: {bad}")

    numbers: list[int] = []
    for url in urls:
        tail = url.strip().rstrip("/").rsplit("/", 1)[-1]
        if not tail.isdigit():
            return None, (f"could not read a pull request number out of {url!r}, which "
                          "check_corpus_linkage.sh called well-formed -- that is a "
                          "disagreement between the two, not something a body can cause")
        numbers.append(int(tail))
    return numbers, ""


def classify(payload) -> tuple[str, str]:
    """(state, detail) for one corpus pull request payload. Pure.

    Order is load-bearing and both orderings below are measured, not assumed:
    `merged` before anything else (a merged PR reports mergeable=null), and
    `state` before `mergeable_state` (a closed-unmerged PR keeps reporting
    `blocked` indefinitely).
    """
    if not isinstance(payload, dict):
        return "UNREADABLE", "the corpus PR payload was not an object"
    if "state" not in payload or "merged" not in payload:
        return "UNREADABLE", ("the corpus PR payload carries no state/merged fields, so "
                              "nothing about it was actually measured")

    if payload.get("merged") is True:
        return "MERGED", "a real BC service tier adjudicated this claim"

    state = str(payload.get("state") or "").lower()
    if state == "closed":
        return "CLOSED-UNMERGED", (
            "it closed WITHOUT merging, so no service tier ever adjudicated this claim. "
            "Reopen it, or open a new corpus PR and cite that one")
    if state != "open":
        return "UNREADABLE", f"unexpected pull request state {state!r}"

    mergeable = payload.get("mergeable")
    mstate = str(payload.get("mergeable_state") or "").lower()

    if mergeable is None:
        return "UNREADABLE", ("GitHub has not finished computing this corpus PR's merge "
                              "state (mergeable: null); ask again")
    if mergeable is False:
        return "NOT-MERGEABLE", "it conflicts with the corpus master branch"

    if mstate in ("clean", "has_hooks"):
        return "MERGEABLE", "open, no conflict, every required BC leg green"
    if mstate == "unstable":
        return "MERGEABLE", ("open and mergeable; a check that is NOT required is failing "
                             "-- on the corpus that is one of the eight OnPrem legs, which "
                             "the merge bar does not gate on")
    if mstate == "dirty":
        return "NOT-MERGEABLE", "it conflicts with the corpus master branch"
    if mstate == "blocked":
        return "NOT-MERGEABLE", ("a required BC leg is red or has not reported yet, so no "
                                 "service tier has adjudicated this claim green")
    if mstate == "behind":
        return "NOT-MERGEABLE", "it is behind the corpus master branch and must be updated"
    if mstate == "draft" or payload.get("draft") is True:
        return "NOT-MERGEABLE", "it is still a draft"
    return "UNREADABLE", (f"GitHub answered mergeable_state {mstate!r}, which this script "
                          "does not know how to read; refusing to guess")


def _gh(args: list[str]) -> tuple[int, str]:
    p = subprocess.run(["gh", *args], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    out = (p.stdout or "") + (p.stderr or "")
    # mise prints a banner on stdout; drop it so the JSON parses (CLAUDE.md).
    out = "\n".join(l for l in out.split("\n") if not l.startswith("mise "))
    return p.returncode, out.strip()


def fetch_pull(number: int, run=None, attempts: int = 3, sleep=time.sleep
               ) -> tuple[dict | None, str]:
    """(payload, reason) for one corpus pull request. None means the read failed.

    Retries a `mergeable: null`, which is GitHub still computing the merge commit
    rather than an answer. Everything else is returned as read.
    """
    run = run or _gh
    jq = ('{state:.state, merged:.merged, draft:.draft, mergeable:.mergeable, '
          'mergeable_state:.mergeable_state, head:.head.sha}')
    last = ""
    for i in range(max(1, attempts)):
        rc, out = run(["api", f"repos/{CORPUS_REPO}/pulls/{number}", "--jq", jq])
        if rc != 0:
            return None, (f"could not read corpus pull request #{number} from "
                          f"{CORPUS_REPO}: {out[:200]}")
        try:
            payload = json.loads(out)
        except Exception:
            return None, (f"could not parse the API answer for corpus pull request "
                          f"#{number}: {out[:200]}")
        if not isinstance(payload, dict):
            return None, f"unexpected payload shape for corpus pull request #{number}"
        if payload.get("mergeable") is not None or payload.get("merged") is True \
                or str(payload.get("state") or "").lower() != "open":
            return payload, ""
        last = "mergeable was still null"
        if i + 1 < max(1, attempts):
            sleep(2 * (i + 1))
    return payload, last


def states_for_body(body: str, fetch=None, script: str | None = None
                    ) -> tuple[list[Entry], str]:
    """(one Entry per cited corpus PR, refusal reason).

    A non-empty reason with an empty list means the body could not be read at
    all -- a malformed declaration, or a parse that could not run. That is the
    third state, not "nothing declared".
    """
    fetch = fetch or fetch_pull
    numbers, why = corpus_pr_numbers(body, script=script)
    if numbers is None:
        return [], why
    entries: list[Entry] = []
    for number in numbers:
        payload, read_why = fetch(number)
        if payload is None:
            entries.append(Entry(number, "UNREADABLE", "", read_why))
            continue
        state, detail = classify(payload)
        if state == "UNREADABLE" and read_why:
            detail = f"{detail} ({read_why})"
        entries.append(Entry(number, state, str(payload.get("head") or ""), detail))
    return entries, ""


def main(argv: list[str], fetch=None) -> int:
    if "PR_BODY" not in os.environ:
        print("::error::corpus_pr_state.py: PR_BODY is required (it may be empty, but it "
              "must be passed). A body nobody read declares nothing, which would pass "
              "every pull request in the repository.", file=sys.stderr)
        return 2

    body = os.environ["PR_BODY"]
    entries, why = states_for_body(body, fetch=fetch)
    if why:
        print(f"::error::corpus PR: UNREADABLE -- {why}", file=sys.stderr)
        return 3
    if not entries:
        print("corpus PR: NONE -- this body declares no Corpus-PR line, so there is no "
              "corpus pull request to wait for.")
        return 0

    worst = 0
    for entry in entries:
        line = format_line(entry)
        rank = _RANK.get(entry.state, 3)
        if rank == 0:
            print(line)
        else:
            print(f"::error::{line}", file=sys.stderr)
        # 1 outranks 3: a definite failure is a verdict, a refusal is not.
        worst = 1 if (worst == 1 or rank == 1) else max(worst, rank)

    if worst == 1:
        print("\nThis pull request cites a corpus pull request that cannot merge as it "
              "stands. .claude/rules/bc-behavior-tests-go-upstream.md: the corpus PR is "
              "what puts the claim in front of a real BC service tier, and a runner PR "
              "merged ahead of it ships a claim nothing adjudicated (#3674). Fix the "
              "corpus PR, or change the declaration to the corpus PR that carries the "
              "proving test.", file=sys.stderr)
    elif worst == 3:
        print("\nThe corpus pull request could not be read, so this check has no verdict "
              "-- it is not a pass. Re-run it once GitHub answers.", file=sys.stderr)
    return worst


if __name__ == "__main__":
    sys.exit(main(sys.argv))
