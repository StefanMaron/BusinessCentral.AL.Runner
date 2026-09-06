#!/usr/bin/env python3
"""Fail when a REQUIRED status-check context can be left `cancelled` on the head commit.

The gap this guards (#2726)
---------------------------
A branch ruleset satisfies a required status check by looking at the newest
check run carrying that context name on the PR's head commit. A check run whose
conclusion is `cancelled` does not satisfy it — and unlike a failure, nothing in
the UI or in `gh pr checks` points at it as the reason the merge is refused.

`concurrency.cancel-in-progress: true` produces exactly that conclusion whenever
a second run of the same workflow enters the group. For a new PUSH that is
correct and deliberate: the superseded run is testing a commit nobody has any
more, and its check runs hang off that older SHA where no ruleset will read
them. The whole comment block at the top of pr-check.yml and test-matrix.yml
argues for it, and this guard does not touch it.

The damage comes from the `pull_request` event types that fire WITHOUT changing
the head SHA — `edited`, `labeled`, `unlabeled` and friends. Those start a second
run against the SAME commit, so the cancelled conclusion lands on the very SHA
the ruleset is evaluating. Observed live on PR #2722 at head d5c334c1: the
required `Tests updated` context existed three times, twice `success` and once
`cancelled`, `gh pr checks` reported every context SUCCESS, `tools/ci-wait.py`
correctly reported GREEN, and the merge was refused with nothing saying why.

So the rule is narrow and only about required contexts:

    A workflow that produces a REQUIRED status-check context may not combine
    `cancel-in-progress: true` with a `pull_request` trigger type that fires
    without changing the head SHA.

Either drop the same-SHA trigger types from that workflow (move the jobs that
need them to a workflow producing no required context), or drop
`cancel-in-progress`. Both are fine; leaving them together is not.

It also fails when a required context is produced by NO workflow at all, which
is the mirror-image outage: every PR blocks forever on a check that can never
report. That is the failure mode the comment above test-matrix.yml's `all-tests`
job warns about, and nothing enforced it until now.

The second gap this guards (#2785)
----------------------------------
The list of required contexts used to be hardcoded here and in tools/ci-wait.py,
and nothing compared either list against what the branch ruleset ACTUALLY
requires. A required status check added in the GitHub UI would therefore be
ignored by both: this guard would not check whether it is cancellable, and
ci-wait.py would keep certifying PRs green without waiting for it -- a merge gate
reporting green having not checked something required.

So the guard now reads the live rules and fails on drift in EITHER direction: a
context the ruleset requires that this list does not carry, or a context this
list carries that the ruleset no longer requires. It also fails when the two
hardcoded lists (here and in tools/ci-wait.py) disagree with each other.

It reads GET /repos/{owner}/{repo}/rules/branches/{branch}, the EFFECTIVE rules
for the branch, which reports only rulesets whose enforcement is active. That
choice removes a trap rather than documenting it: there is no ruleset id to get
wrong. Querying /rulesets/15039643 ("Copilot review for default branch") returns
an empty list -- it is disabled and carries no required_status_checks rule -- and
an agent following a comment that named it would conclude the list should be
emptied. Measured 2026-09-05, the branch-rules endpoint answers 200 even
unauthenticated on this public repo, so no token is needed in CI.

A lookup that FAILS is a failure, never "no differences". An unauthenticated 404
and an empty result are indistinguishable, and reading an error as agreement is
the failure mode this check exists to prevent. SKIP_RULESET_DRIFT_CHECK=1 runs
the rest of the guard offline on purpose, and prints that it has. CI sets it in
exactly one place: pr-gate.yml's invocation, which produces a REQUIRED context and
so must not be able to go red because api.github.com was unreachable. The
invocation in pr-check.yml sets no bypass and does the live comparison; that one
reports without gating.

Usage
-----
    check_required_contexts.py [workflows-dir]

`workflows-dir` defaults to .github/workflows next to this script. The required
context list can be overridden with the REQUIRED_CONTEXTS environment variable
(newline- or comma-separated) — that exists so the unit tests can drive synthetic
fixtures, not as a production knob. Setting it also skips the live-ruleset
comparison, because a synthetic context list has nothing to compare against.
PENDING_CONTEXTS overrides PENDING_REQUIRED_CONTEXTS the same way, and for the
same reason; an empty value means "none", which is what a fixture directory
needs.

The third gap this guards (#3165)
---------------------------------
Every guard in pr-check.yml used to be advisory: none of its twelve jobs was one
of the two contexts the ruleset required, so each could report red while the pull
request stayed mergeable, and three PRs merged that way. The gating jobs now live
in pr-gate.yml, which carries no `concurrency` block precisely because of the rule
above — the second of the two remedies it names. PENDING_REQUIRED_CONTEXTS is how
the code side lands before the by-hand ruleset edit rather than after it; see the
comment on that list.

Exit codes
----------
    0  every required context is safe
    1  at least one required context is cancellable, is produced by nothing, or
       the hardcoded list has drifted from the live ruleset
    2  the check could not run at all (no workflows dir, unparseable YAML)
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys
import time
import urllib.error
import urllib.request

import yaml

# The contexts the `main` branch ruleset requires. This list is no longer trusted
# on its own — resolve_contexts() below compares it against the live ruleset on
# every run and fails on drift in either direction (#2785). Re-derive by hand:
#   gh api repos/StefanMaron/BusinessCentral.AL.Runner/rules/branches/main \
#     --jq '[.[] | select(.type=="required_status_checks")
#            | .parameters.required_status_checks[].context]'
# That endpoint, not /rulesets/<id>: it reports the EFFECTIVE rules, so only
# ACTIVE rulesets appear and there is no id to get wrong.
# Measured 2026-09-05: exactly these two, matched by context NAME only (the
# ruleset entries carry no integration_id), which is why a required job may move
# between workflow files as long as its `name:` is unchanged.
#
# Renamed from "All BC versions passed" by #3141, together with the branch ruleset.
# The pull-request matrix now runs three of the eight BC versions, so the old name
# asserted something the run had not measured. Renaming a required status check is a
# two-sided edit that cannot be atomic: this file (plus tools/ci-wait.py and the job's
# `name:` in test-matrix.yml) moves in a pull request, the ruleset moves in the GitHub
# UI, and between the two this guard reports drift. That red is expected and advisory —
# pr-check.yml produces no required context — but the merge itself is blocked until the
# ruleset carries the new name, because until then the PR waits on a context no workflow
# reports any more.
DEFAULT_REQUIRED_CONTEXTS = [
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
]

# Contexts this repository INTENDS the branch ruleset to require, but which it
# does not require yet (#3165). Everything in pr-gate.yml is here.
#
# Why a second list exists at all. The ruleset is edited by hand in the GitHub
# UI; the code that produces those contexts arrives through a pull request. The
# two cannot land in the same instant, so there is a window, and both halves have
# to be green on both sides of it:
#
#   * Before the ruleset edit. Putting these names straight into
#     DEFAULT_REQUIRED_CONTEXTS makes the drift comparison below fail on every
#     PR ("this repo's lists carry context(s) the branch ruleset no longer
#     requires"). Putting them into tools/ci-wait.py's RULESET_CONTEXTS is
#     worse: that tuple is a FLOOR, and ci-wait.py returns exit 3 UNDETERMINED
#     whenever the live ruleset is a subset of it (#3002) -- so every agent's CI
#     wait in the repository would stop returning a verdict until a human
#     happened to edit the ruleset. Neither list may move first.
#   * After the ruleset edit. A name that has just become required must not make
#     the guard red for being "added" drift, or the edit itself breaks CI.
#
# So a PENDING name is analysed exactly like a required one -- it must be
# produced by a pull_request workflow, and it must not be cancellable on the head
# commit -- while the live-ruleset drift comparison tolerates it in EITHER state.
# That is the whole of the difference. It is NOT a way to park a context nothing
# checks.
#
# This list is meant to empty itself: once the ruleset requires a name, the guard
# prints a ::notice:: asking for it to be promoted into DEFAULT_REQUIRED_CONTEXTS
# here and into RULESET_CONTEXTS in tools/ci-wait.py. Nothing breaks while that
# is outstanding -- ci-wait.py reads the LIVE ruleset first and only falls back
# to its built-in tuple when that read fails, loudly -- so the promotion is
# tidying, not a race.
# Empty is this list's RESTING state, not the only legal one. The `main` ruleset
# requires all ten names in DEFAULT_REQUIRED_CONTEXTS above, so nothing that
# already gates is waiting here (#3199, measured 2026-09-06), and #3244 emptied
# it for a reason: every pending name is a name the live drift comparison in
# resolve_contexts() deliberately tolerates in EITHER state, so each entry is a
# hole in the #2785 check for as long as it lasts.
#
# #3244 also said what refills it -- "only while a new gating job is landing" --
# and that is the case for BOTH entries below. Each names a blocking job that
# pr-gate.yml already produces and the `main` ruleset does not require yet, so
# each is listed for exactly as long as that is true, and each comes out in the
# same pass that adds its name to the ruleset.
#
# Two entries, not one, and that is deliberate: #3255 and #3089 landed their
# gating jobs independently, and dropping either name while its job blocks would
# reopen precisely the #2785 hole this file exists to close -- a context the
# ruleset requires that nothing here analyses. Merging these two refills by
# keeping only one side is the mistake to avoid.
PENDING_REQUIRED_CONTEXTS: list[str] = [
    # #3255's gate. pr-gate.yml's require-corpus-linkage job produces this
    # context. Analysed here exactly like a required one -- produced by a
    # pull_request workflow, and not cancellable on the head commit -- while the
    # live drift comparison tolerates it in either state, which is what lets the
    # code half land before the by-hand ruleset edit. Promote it into
    # DEFAULT_REQUIRED_CONTEXTS above and into RULESET_CONTEXTS in
    # tools/ci-wait.py in the same pass that adds it to the ruleset.
    "AL-observable changes must declare corpus linkage",
    # #3089's gate. Deliberately NOT promoted into DEFAULT_REQUIRED_CONTEXTS or
    # into tools/ci-wait.py's RULESET_CONTEXTS: the live ruleset does not require
    # this name yet, and ci-wait.py treats RULESET_CONTEXTS as a FLOOR, returning
    # exit 3 UNDETERMINED whenever the live ruleset is a subset of it (#3002). So
    # promoting early would stop every agent's CI wait in this repository from
    # returning a verdict until a maintainer edited the ruleset by hand. Listed
    # here it is still ANALYSED like a required one -- produced by a pull_request
    # workflow, not cancellable on the head commit -- which is the whole point of
    # the seam.
    "A PR closing a gap issue must not leave its known-gap entry behind",
]

REPO = "StefanMaron/BusinessCentral.AL.Runner"
DEFAULT_BRANCH = "main"
BRANCH_RULES_URL = "https://api.github.com/repos/{repo}/rules/branches/{branch}"

# `pull_request` types that fire while the head SHA stays put, so a run they
# start collides with runs already reporting on that same SHA.
#
# Deliberately NOT in this set:
#   opened      - fires once, nothing to collide with
#   synchronize - this is what a new push is; the superseded run reports on the
#                 OLD sha, which no ruleset reads. Cancelling it is the point.
#   reopened    - same-SHA in principle, but it requires closing a PR and
#                 reopening it inside the lifetime of one run. Listing it would
#                 fail test-matrix.yml (default types, cancel-in-progress: true)
#                 for a case never observed, so this guard does not claim it.
#   closed      - no checks are gating a closed PR.
SAME_SHA_TYPES = {
    "edited",
    "labeled",
    "unlabeled",
    "assigned",
    "unassigned",
    "review_requested",
    "review_request_removed",
    "ready_for_review",
    "converted_to_draft",
    "locked",
    "unlocked",
    "milestoned",
    "demilestoned",
    "auto_merge_enabled",
    "auto_merge_disabled",
    "enqueued",
    "dequeued",
}

# GitHub's default when `on.pull_request` lists no `types:`.
DEFAULT_PR_TYPES = ["opened", "synchronize", "reopened"]


def _split(raw: str) -> list[str]:
    """Newline- or comma-separated, but NEVER both at once.

    A check-run name may contain a comma -- "PR body closing references must be
    correct, both directions" does -- so splitting on commas unconditionally
    turned one context into two, and the guard then reported the halves as
    'produced by NO workflow'. Newlines win when present: they are the only
    separator that cannot appear inside a name.
    """
    if "\n" in raw:
        parts = raw.split("\n")
    else:
        parts = raw.split(",")
    return [p.strip() for p in parts if p.strip()]


def required_contexts() -> list[str]:
    raw = os.environ.get("REQUIRED_CONTEXTS")
    if not raw:
        return list(DEFAULT_REQUIRED_CONTEXTS)
    return _split(raw)


def pending_contexts() -> list[str]:
    """PENDING_REQUIRED_CONTEXTS, overridable for this script's own unit tests.

    Presence of the variable is what counts, not truthiness: PENDING_CONTEXTS=""
    means "no pending contexts", which is what a synthetic fixture directory
    needs, and is a different statement from leaving it unset.
    """
    if "PENDING_CONTEXTS" in os.environ:
        return _split(os.environ["PENDING_CONTEXTS"])
    return list(PENDING_REQUIRED_CONTEXTS)


def repo_root() -> str:
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.abspath(os.path.join(here, "..", ".."))


def load_ci_wait(path: str | None = None):
    """Import tools/ci-wait.py as a module.

    Its RULESET_CONTEXTS is the other copy of the same list, and its
    contexts_from_branch_rules() is the single parser for the branch-rules
    payload — sharing it means the "an empty answer is UNKNOWN, not 'nothing
    required'" rule cannot be implemented one way here and another way there.
    """
    path = path or os.path.join(repo_root(), "tools", "ci-wait.py")
    spec = importlib.util.spec_from_file_location("ci_wait_for_required_contexts", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load {path}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def fetch_branch_rules(repo: str = REPO, branch: str = DEFAULT_BRANCH,
                       timeout: int = 20, attempts: int = 3):
    """GET /repos/{repo}/rules/branches/{branch}. Raises after the last attempt.

    Retried, because a single network hiccup must not be reported as ruleset
    drift — and must not be swallowed either. `GITHUB_TOKEN` is used when
    present only to buy the higher rate limit; the endpoint answers 200
    unauthenticated on a public repo.
    """
    req = urllib.request.Request(
        BRANCH_RULES_URL.format(repo=repo, branch=branch),
        headers={
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "check_required_contexts.py",
        },
    )
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    last: Exception | None = None
    for i in range(attempts):
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception as exc:  # noqa: BLE001 - re-raised below if it never succeeds
            last = exc
            if i + 1 < attempts:
                time.sleep(2 * (i + 1))
    raise last  # type: ignore[misc]


def resolve_contexts(fetch=None, ci_wait_path: str | None = None
                     ) -> tuple[list[str], list[str]]:
    """(the contexts to analyse, the drift problems found).

    The contexts returned are the LIVE ones when the lookup succeeded, so a
    context added to the ruleset is checked for cancellability on the very run
    that reports the drift — not only after somebody edits this file.
    """
    problems: list[str] = []
    fallback = list(DEFAULT_REQUIRED_CONTEXTS)

    try:
        cw = load_ci_wait(ci_wait_path)
    except Exception as exc:  # noqa: BLE001 - any failure here is a real problem
        return fallback, [
            f"could not load tools/ci-wait.py to cross-check its RULESET_CONTEXTS: "
            f"{exc!r} — that file is the merge gate agents actually run, so its copy "
            f"of this list cannot go unchecked (#2785)"
        ]

    cw_contexts = sorted(getattr(cw, "RULESET_CONTEXTS", ()) or ())
    early = sorted(set(cw_contexts) & set(pending_contexts()))
    if early:
        # Named separately from generic drift because the consequence is specific
        # and repository-wide: RULESET_CONTEXTS is ci-wait.py's FLOOR, and a floor
        # wider than the live ruleset makes every invocation return exit 3
        # UNDETERMINED rather than a verdict (#3002).
        problems.append(
            f"tools/ci-wait.py's RULESET_CONTEXTS carries {', '.join(repr(e) for e in early)}, "
            f"which is still PENDING here — that tuple is ci-wait.py's FLOOR, so listing a "
            f"context the live ruleset does not require yet makes ci-wait.py return exit 3 "
            f"UNDETERMINED on EVERY pull request until the ruleset is edited (#3002/#3165). "
            f"Promote a pending name only once the ruleset actually requires it"
        )
    elif cw_contexts != sorted(DEFAULT_REQUIRED_CONTEXTS):
        problems.append(
            f"tools/ci-wait.py's RULESET_CONTEXTS {cw_contexts} disagrees with "
            f"DEFAULT_REQUIRED_CONTEXTS {sorted(DEFAULT_REQUIRED_CONTEXTS)} here — "
            f"two copies of one list that can drift apart means fixing one still "
            f"leaves the merge gate reading the other"
        )

    # One parser for the branch-rules payload, so the rule that an empty answer
    # is UNKNOWN rather than "nothing is required" cannot be implemented one way
    # here and another way in the tool that gates the merge.
    parse = getattr(cw, "contexts_from_branch_rules", None)
    if parse is None:
        problems.append(
            "tools/ci-wait.py has no contexts_from_branch_rules(): the live ruleset "
            "cannot be parsed, and the merge gate that shares this parser is not the "
            "file this guard just loaded (#2785)"
        )
        return fallback, problems

    if os.environ.get("SKIP_RULESET_DRIFT_CHECK") == "1":
        print("live ruleset comparison skipped (SKIP_RULESET_DRIFT_CHECK=1)")
        return fallback, problems

    try:
        payload = (fetch or fetch_branch_rules)()
    except Exception as exc:  # noqa: BLE001 - a failed lookup must be loud
        problems.append(
            f"could not read the live branch rules for {DEFAULT_BRANCH!r}: {exc!r} — "
            f"treating a failed lookup as 'no drift' is exactly the failure mode this "
            f"check exists to prevent, since an unauthenticated 404 and an empty result "
            f"look the same (#2785). Set SKIP_RULESET_DRIFT_CHECK=1 to run this guard "
            f"offline on purpose"
        )
        return fallback, problems

    live = parse(payload)
    if live is None:
        problems.append(
            f"the live branch rules for {DEFAULT_BRANCH!r} carry no non-empty "
            f"required_status_checks rule. That is UNKNOWN, not 'nothing is required': "
            f"the disabled ruleset 15039643 answers exactly this way, and believing it "
            f"would empty the list that gates every merge (#2785)"
        )
        return fallback, problems

    live = list(live)
    pending = set(pending_contexts())
    arrived = sorted(set(live) & pending)
    if arrived:
        # Tolerated, and said out loud. This is the moment the ruleset edit
        # landed; the code side is already correct, and the only thing left is to
        # move the name out of the transitional list.
        print(f"::notice::the branch ruleset now requires "
              f"{', '.join(repr(a) for a in arrived)}, which this file still lists in "
              f"PENDING_REQUIRED_CONTEXTS. Nothing is broken -- ci-wait.py reads the live "
              f"ruleset first -- but promote them into DEFAULT_REQUIRED_CONTEXTS here and "
              f"into RULESET_CONTEXTS in tools/ci-wait.py, so the fallback list ci-wait.py "
              f"uses when that read FAILS is complete too (#3165).")
    added = sorted(set(live) - set(DEFAULT_REQUIRED_CONTEXTS) - pending)
    removed = sorted(set(DEFAULT_REQUIRED_CONTEXTS) - set(live))
    if added:
        problems.append(
            f"the branch ruleset requires context(s) this repo's lists do not carry: "
            f"{', '.join(repr(a) for a in added)} — add them to "
            f"DEFAULT_REQUIRED_CONTEXTS in this file and to RULESET_CONTEXTS in "
            f"tools/ci-wait.py, or ci-wait.py will keep reporting PRs green without "
            f"waiting for them (#2785)"
        )
    if removed:
        problems.append(
            f"this repo's lists carry context(s) the branch ruleset no longer requires: "
            f"{', '.join(repr(r) for r in removed)} — remove them from "
            f"DEFAULT_REQUIRED_CONTEXTS in this file and from RULESET_CONTEXTS in "
            f"tools/ci-wait.py, or ci-wait.py waits for a gate that no longer exists "
            f"(#2785)"
        )
    return live, problems


def ruleset_drift_problems(fetch=None, ci_wait_path: str | None = None) -> list[str]:
    """Just the drift problems — the shape the unit tests assert on."""
    return resolve_contexts(fetch=fetch, ci_wait_path=ci_wait_path)[1]


def load(path: str) -> dict:
    with open(path, encoding="utf-8") as fh:
        doc = yaml.safe_load(fh)
    return doc if isinstance(doc, dict) else {}


def triggers(wf: dict) -> dict:
    """`on:` from a workflow. PyYAML resolves the bare key `on` to True."""
    on = wf.get("on", wf.get(True))
    if isinstance(on, dict):
        return on
    if isinstance(on, str):
        return {on: None}
    if isinstance(on, list):
        return {k: None for k in on}
    return {}


def pull_request_types(wf: dict) -> list[str]:
    """The pull_request types this workflow reacts to, [] if it has no such trigger."""
    on = triggers(wf)
    if "pull_request" not in on:
        return []
    spec = on["pull_request"]
    if not isinstance(spec, dict) or "types" not in spec:
        return list(DEFAULT_PR_TYPES)
    types = spec["types"]
    if isinstance(types, str):
        return [types]
    return [str(t) for t in (types or [])]


def cancels_in_progress(scope: dict) -> bool:
    """True unless `concurrency` is absent or cancel-in-progress is literally false.

    An expression like ${{ github.ref != 'refs/heads/main' }} cannot be evaluated
    here, so it counts as cancelling — a guard that assumed the safe branch of an
    expression it cannot read would be worse than no guard.
    """
    conc = scope.get("concurrency")
    if conc is None:
        return False
    if not isinstance(conc, dict):
        # `concurrency: some-group` — a group with no cancel-in-progress queues.
        return False
    flag = conc.get("cancel-in-progress", False)
    if isinstance(flag, bool):
        return flag
    if isinstance(flag, str):
        return flag.strip().lower() not in ("", "false")
    return bool(flag)


def contexts_of(wf: dict, wf_dir: str) -> dict[str, dict]:
    """context name -> the job dict that produces it.

    A top-level job reports under its `name:` (falling back to the job id). A job
    that calls a reusable workflow reports each of the called workflow's jobs
    qualified as "<calling job id> / <called job name>" — the mechanism the
    comment above test-matrix.yml's `all-tests` job describes.
    """
    out: dict[str, dict] = {}
    jobs = wf.get("jobs") or {}
    if not isinstance(jobs, dict):
        return out
    for job_id, job in jobs.items():
        if not isinstance(job, dict):
            continue
        uses = job.get("uses")
        if isinstance(uses, str) and uses.startswith("./"):
            called_path = os.path.join(wf_dir, os.path.basename(uses))
            if os.path.isfile(called_path):
                called = load(called_path)
                for inner, inner_job in (called.get("jobs") or {}).items():
                    inner_name = inner
                    if isinstance(inner_job, dict) and inner_job.get("name"):
                        inner_name = str(inner_job["name"])
                    out[f"{job_id} / {inner_name}"] = job
                continue
        out[str(job.get("name") or job_id)] = job
    return out


def main(argv: list[str], fetch=None) -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    wf_dir = argv[1] if len(argv) > 1 else os.path.join(here, "..", "workflows")
    wf_dir = os.path.abspath(wf_dir)
    if not os.path.isdir(wf_dir):
        print(f"::error::workflows directory not found: {wf_dir}", file=sys.stderr)
        return 2

    files = sorted(
        os.path.join(wf_dir, f)
        for f in os.listdir(wf_dir)
        if f.endswith((".yml", ".yaml"))
    )
    if not files:
        print(f"::error::no workflow files in {wf_dir}", file=sys.stderr)
        return 2

    # context -> list of (workflow path, workflow dict, job dict)
    produced: dict[str, list[tuple[str, dict, dict]]] = {}
    for path in files:
        try:
            wf = load(path)
        except yaml.YAMLError as exc:
            print(f"::error::could not parse {path}: {exc}", file=sys.stderr)
            return 2
        if not pull_request_types(wf):
            continue
        for ctx, job in contexts_of(wf, wf_dir).items():
            produced.setdefault(ctx, []).append((path, wf, job))

    problems: list[str] = []

    # The REQUIRED_CONTEXTS override exists so the unit tests can drive synthetic
    # fixtures; a synthetic list has nothing to compare against, so the live
    # check stands down rather than fighting it (#2785).
    #
    # It is a REAL bypass, not just a test seam: this branch skips BOTH the live
    # ruleset comparison AND the ci-wait.py cross-check, and it used to do so in
    # total silence -- unlike SKIP_RULESET_DRIFT_CHECK, which at least prints.
    # A future agent unbreaking CI during a GitHub API outage by setting it in a
    # workflow would get a green job with the guard switched off and nothing in
    # the log saying so. Hence the ::warning::, and hence
    # test_check_required_contexts.py asserting -- across EVERY shipped workflow,
    # not one named file -- that no invocation sets REQUIRED_CONTEXTS or
    # PENDING_CONTEXTS, and that at least one sets no bypass at all.
    if os.environ.get("REQUIRED_CONTEXTS"):
        print("::warning::REQUIRED_CONTEXTS is set, so the live ruleset comparison "
              "and the tools/ci-wait.py cross-check are BOTH skipped. This guard is "
              "running against an overridden context list and cannot detect ruleset "
              "drift (#2785). Expected only in this script's own unit tests.")
        contexts = required_contexts()
    else:
        contexts, drift = resolve_contexts(fetch=fetch)
        problems.extend(drift)

    # A PENDING context is analysed exactly like a required one. That is what
    # makes the transitional list safe to have: the gating workflow is proven
    # non-cancellable and actually produced BEFORE the ruleset starts depending
    # on it, rather than after somebody notices a merge is refused (#3165).
    pending_only = [c for c in pending_contexts() if c not in contexts]
    contexts = list(contexts) + pending_only

    for ctx in contexts:
        sources = produced.get(ctx)
        if not sources:
            problems.append(
                f"required context {ctx!r} is produced by NO workflow reacting to "
                f"pull_request — every PR blocks forever on a check that never reports"
            )
            continue
        for path, wf, job in sources:
            risky = sorted(set(pull_request_types(wf)) & SAME_SHA_TYPES)
            if not risky:
                continue
            if cancels_in_progress(wf) or cancels_in_progress(job):
                problems.append(
                    f"required context {ctx!r} in {os.path.basename(path)} combines "
                    f"cancel-in-progress with same-SHA pull_request type(s) "
                    f"{', '.join(risky)} — a second run on the SAME commit cancels the "
                    f"first and leaves {ctx!r} 'cancelled', which blocks the merge with "
                    f"nothing reporting why (#2726)"
                )

    if problems:
        for p in problems:
            print(f"::error::{p}", file=sys.stderr)
        if any("cancel-in-progress" in p for p in problems):
            print(
                "\nFix: move the jobs that need the same-SHA trigger types into a "
                "workflow that produces no required context, or drop "
                "cancel-in-progress from the workflow that produces this one.",
                file=sys.stderr,
            )
        return 1

    names = ", ".join(c for c in contexts if c not in pending_only)
    print(f"required contexts cannot be left cancelled on the head commit: {names}")
    if pending_only:
        print("pending contexts (analysed the same way, not required yet): "
              + ", ".join(pending_only))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
