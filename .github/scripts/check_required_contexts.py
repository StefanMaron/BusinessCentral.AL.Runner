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

Usage
-----
    check_required_contexts.py [workflows-dir]

`workflows-dir` defaults to .github/workflows next to this script. The required
context list can be overridden with the REQUIRED_CONTEXTS environment variable
(newline- or comma-separated) — that exists so the unit tests can drive synthetic
fixtures, not as a production knob.

Exit codes
----------
    0  every required context is safe
    1  at least one required context is cancellable, or is produced by nothing
    2  the check could not run at all (no workflows dir, unparseable YAML)
"""
from __future__ import annotations

import os
import sys

import yaml

# The contexts the `main` branch ruleset requires. Re-derive with:
#   gh api repos/StefanMaron/BusinessCentral.AL.Runner/rulesets/15001420 \
#     --jq '.rules[] | select(.type=="required_status_checks")
#           | .parameters.required_status_checks[].context'
# Measured 2026-09-05: exactly these two, matched by context NAME only (the
# ruleset entries carry no integration_id), which is why a required job may move
# between workflow files as long as its `name:` is unchanged.
DEFAULT_REQUIRED_CONTEXTS = ["All BC versions passed", "Tests updated"]

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


def required_contexts() -> list[str]:
    raw = os.environ.get("REQUIRED_CONTEXTS")
    if not raw:
        return list(DEFAULT_REQUIRED_CONTEXTS)
    parts = [p.strip() for chunk in raw.split("\n") for p in chunk.split(",")]
    return [p for p in parts if p]


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


def main(argv: list[str]) -> int:
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
    for ctx in required_contexts():
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
        print(
            "\nFix: move the jobs that need the same-SHA trigger types into a workflow "
            "that produces no required context, or drop cancel-in-progress from the "
            "workflow that produces this one.",
            file=sys.stderr,
        )
        return 1

    names = ", ".join(required_contexts())
    print(f"required contexts cannot be left cancelled on the head commit: {names}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
