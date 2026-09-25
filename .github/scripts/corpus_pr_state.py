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
of its cloud legs; `unstable` is a non-required check failing, which on the
corpus is one of its OnPrem legs, and the merge bar accepts that. Which legs are
required is the corpus ruleset's answer, read by `required_contexts` below and
never written here (#4593). Measured
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

THE STORED CONCLUSION GOES STALE, AND NOTHING RE-EVALUATES IT (#4206)
--------------------------------------------------------------------
This module is run by a STATUS CHECK, which evaluates on `push`, `edited` and
`labeled` -- never when the corpus pull request it names changes state. The
merge order makes that a systematic problem rather than a race: a runner PR
asserting BC behaviour merges AFTER its corpus PR
(bc-behavior-tests-go-upstream.md step 5), so the corpus PR merges after the
runner PR's last push, and the stored `failure` outlives the condition it
measured.

The consequence is not a bad merge -- this check is advisory and out of the
branch ruleset -- but a refused one. A failing NON-required check makes
`mergeStateStatus` read UNSTABLE, and `enablePullRequestAutoMerge` refuses that
with `Pull request is in unstable status`. Every instrument a coordinator reads
says green: `tools/ci-wait.py` exits 0 because no REQUIRED context is red, and
it prints `corpus PR #N: MERGED` from this module's own fresh read, in the same
breath as GitHub refusing the merge.

`stale_gate_verdict` below is the third instrument, comparing this module's
fresh answer against the conclusion the gate STORED. Measured three times in one
session: #4135/#348, #4202/#368, and #4203/#371 -- gate `failure` at 20:04:28Z,
corpus PR merged 20:21:08Z, fresh `success` at 20:50:05Z after a body edit.

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

# pr-gate.yml's job name for the check this module backs. Exported so callers
# compare against the same string this module answers about, rather than each
# spelling it themselves -- a typo in a caller's copy makes the stale read below
# answer UNKNOWN forever, which is quiet (see stale_gate_verdict).
GATE_CONTEXT = "A cited corpus PR must be able to merge"

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


def stale_gate_verdict(entries, conclusion: str | None) -> tuple[str, str]:
    """Is the STORED gate conclusion still true of the corpus PRs now? (#4206)

    `A cited corpus PR must be able to merge` is a status check, so it evaluates
    on `push`, `edited` and `labeled` and NOT when the corpus PR it names changes
    state. Under corpus-first merging (bc-behavior-tests-go-upstream.md step 5)
    the corpus PR merges AFTER the runner PR's last push, so the stored `failure`
    outlives the condition that produced it -- and a failing NON-required check
    makes `mergeStateStatus` read UNSTABLE, which `enablePullRequestAutoMerge`
    refuses. The merge is then blocked by a tick whose subject has already
    passed, while every instrument a coordinator reads says green.

    Pure: it compares an answer already fetched against a conclusion already
    read, and performs no I/O of its own. Three answers, per
    guards-need-a-third-state.md:

        STALE    every cited corpus PR is fine NOW and the stored tick is red.
                 Re-firing the check will clear it.
        CURRENT  the tick and the corpus PRs agree, or one corpus PR genuinely
                 cannot merge -- so re-firing changes nothing.
        UNKNOWN  the comparison did not happen: no stored conclusion, or a
                 corpus PR nobody could read.

    UNKNOWN outranks STALE deliberately. Calling a tick stale is an assertion
    that the gate would pass if re-run, and an UNREADABLE corpus PR is precisely
    the case where nobody established that -- resolving it toward STALE would
    tell a coordinator to clear a gate on a measurement that never happened.
    """
    if not conclusion:
        return "UNKNOWN", ("the gate has no stored conclusion on this head yet, so "
                           "there is nothing to compare the corpus PR against")

    entries = list(entries or [])
    if any(e.state == "UNREADABLE" for e in entries):
        bad = ", ".join(f"#{e.number}" for e in entries if e.state == "UNREADABLE")
        return "UNKNOWN", (f"corpus PR {bad} could not be read, so whether this check's "
                           f"{conclusion!r} is still true was never established")

    if str(conclusion).lower() != "failure":
        return "CURRENT", ""

    # A red tick with nothing cited cannot be about a corpus PR at all.
    if not entries:
        return "CURRENT", ""

    blocking = [e for e in entries if _RANK.get(e.state, 3) == 1]
    if blocking:
        # The red tick is telling the truth about at least one corpus PR, so a
        # re-fire produces the same red. Saying STALE here would send a
        # coordinator round a re-trigger loop that can never clear.
        return "CURRENT", ""

    now = ", ".join(f"#{e.number} is {e.state}" for e in entries)
    return "STALE", (f"this check last concluded 'failure', but {now} now. It is a "
                     "status check, so it does not re-evaluate when the corpus PR "
                     "moves -- edit the body or apply a label to re-fire it")


def format_stale_line(verdict: str, why: str) -> str:
    """The line a caller prints for `stale_gate_verdict`, or "" for CURRENT.

    CURRENT prints nothing on purpose. This line appears beside every verdict
    tools/ci-wait.py gives, and a report that names every pull request is one
    nobody reads -- the same reasoning tools/armed-prs.py applies to a still-
    running armed PR.
    """
    if verdict == "STALE":
        return (f"corpus gate: STALE -- {why}")
    if verdict == "UNKNOWN":
        return (f"corpus gate: UNKNOWN -- {why}")
    return ""


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


def marker_lines_only(body: str) -> str:
    """`body` reduced to the lines that could possibly carry the marker.

    A PREFILTER, not a parser. Both extraction modes of check_corpus_linkage.sh
    anchor on `^[[:space:]]*Corpus-PR:`, so every line either of them can match
    contains `corpus-pr:` case-insensitively; keeping exactly those lines cannot
    change either mode's output, including the counts the refusal below compares.
    Lines it keeps that the script then ignores (a mid-sentence mention, say) are
    harmless -- a superset is safe in the direction that matters.

    It exists because that script spawns two `grep` processes PER LINE, which is
    milliseconds on the Linux runner and about 0.4s each on Windows: measured
    2026-09-10 on this box, a 200-line body took 87.7s to parse and the same body
    reduced to its one marker line took 1.2s. Without this, tools/ci-wait.py
    would take a minute to print one line beside a verdict it answers in a
    second, and the sweep over 68 bodies would take hours.
    """
    return "\n".join(l for l in body.splitlines() if "corpus-pr:" in l.lower())


def corpus_pr_numbers(body: str, script: str | None = None
                      ) -> tuple[list[int] | None, str]:
    """(the corpus PR numbers this body declares, reason) -- None means REFUSED.

    An empty list is an answer: this body declares nothing. None is not -- a
    malformed `Corpus-PR:` line, or a parse that could not run, must never come
    back looking like "no declaration", because that is a pass.
    """
    script = script or LINKAGE
    body = marker_lines_only(body)
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
                             "-- on the corpus that is one of the OnPrem legs, which "
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


# The corpus's default branch; its ruleset is the merge bar. The required legs
# are READ from it, never listed or counted here: the owner added `BC 28.5 / test`
# on 2026-09-25 and every "eight" on this side went stale that day (#4593).
CORPUS_BASE = "master"
_REQUIRED_JQ = ('[.[] | select(.type == "required_status_checks") '
                '| .parameters.required_status_checks[].context]')


def required_contexts(run=None, repo: str = CORPUS_REPO, branch: str = CORPUS_BASE
                      ) -> tuple[list[str] | None, str]:
    """(the corpus's required status contexts, reason) -- None means NOT MEASURED.

    Talks to api.github.com, so every failure is the third state and never an
    empty list: an empty required set would make every leg "not required", which
    is the pass direction (guards-need-a-third-state.md). A ruleset that answers
    with no required checks at all is treated the same way -- the corpus has had
    required legs throughout, so zero means the read did not see them.
    """
    run = run or _gh
    rc, out = run(["api", f"repos/{repo}/rules/branches/{branch}", "--jq", _REQUIRED_JQ])
    if rc != 0:
        return None, f"could not read {repo}'s ruleset for {branch}: {out[:200]}"
    try:
        names = json.loads(out)
    except Exception:
        return None, f"could not parse {repo}'s ruleset answer: {out[:200]}"
    if not isinstance(names, list) or not all(isinstance(n, str) for n in names):
        return None, f"unexpected ruleset answer shape from {repo}: {out[:200]}"
    if not names:
        return None, (f"{repo}'s ruleset for {branch} answered with no required status "
                      "checks, which is not the corpus's configuration -- the read did "
                      "not see them")
    return sorted(dict.fromkeys(names)), ""


def missing_legs(required: list[str], reported: list[str]) -> list[str]:
    """The required contexts with no check run at all among `reported`. Pure."""
    seen = set(reported)
    return [name for name in required if name not in seen]


def missing_required_legs(head: str, run=None) -> tuple[list[str] | None, str]:
    """(required legs with no check run on `head`, reason) -- None means not measured."""
    run = run or _gh
    required, why = required_contexts(run=run)
    if required is None:
        return None, why
    rc, out = run(["api", "--paginate",
                   f"repos/{CORPUS_REPO}/commits/{head}/check-runs?per_page=100",
                   "--jq", ".check_runs[].name"])
    if rc != 0:
        return None, f"could not list check runs on corpus head {head[:8]}: {out[:200]}"
    return missing_legs(required, [l.strip() for l in out.splitlines() if l.strip()]), ""


def describe_blocked(head: str, missing: list[str] | None, why: str) -> str:
    """The clause appended to a `blocked` detail. Detail only: the verdict is GitHub's."""
    if missing is None:
        return f"which required legs have not reported could not be read ({why})"
    if not missing:
        return "every required leg reported on this head, so at least one is red or still running"
    return (f"required leg(s) with no check run on head {head[:8]}: {', '.join(missing)} -- "
            f"a branch whose ci.yml does not dispatch a leg never produces it; it reports "
            f"once the branch runs on a ci.yml that does, so update it from {CORPUS_BASE} "
            f"after the leg has landed there")


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


def states_for_body(body: str, fetch=None, script: str | None = None, legs=None
                    ) -> tuple[list[Entry], str]:
    """(one Entry per cited corpus PR, refusal reason).

    A non-empty reason with an empty list means the body could not be read at
    all -- a malformed declaration, or a parse that could not run. That is the
    third state, not "nothing declared".

    `legs` names the required legs a `blocked` corpus PR never reported. It
    defaults to the live read only when `fetch` does too: a caller injecting
    `fetch` is offline and must not reach the network through a side door.
    """
    if legs is None and fetch is None:
        legs = missing_required_legs
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
        head = str(payload.get("head") or "")
        if (legs is not None and state == "NOT-MERGEABLE" and head
                and str(payload.get("mergeable_state") or "").lower() == "blocked"):
            missing, legs_why = legs(head)
            detail = f"{detail}; {describe_blocked(head, missing, legs_why)}"
        entries.append(Entry(number, state, head, detail))
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
