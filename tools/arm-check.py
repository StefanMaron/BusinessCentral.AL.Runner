#!/usr/bin/env python3
"""The cheap re-review pass: everything the arming decision needs, no judgement.

Claim: most reviewer runs re-confirm a PR that has not changed. Of 84 runs
measured on one operator's side, 25 changed the PR; the rest re-confirmed an
already mergeable one at a median 12.6k output tokens each
(https://fbakkensen.github.io/al-runner-retro/#e-12). Re-reading a diff nobody
has touched is what that spends, and the mechanical preconditions in
`.claude/skills/orchestrating-a-session/SKILL.md` -- branch ownership, a clean
merge-tree, green required checks, corpus linkage, no release run -- are
checkable without reading anything.

So: when the diff is unchanged since your last full review, run this instead of
reviewing again, and stamp the verdict `kind: arm-check`. When the patch-id has
moved, this refuses and a full review is owed.

    tools/arm-check.py <PR> [--repo owner/repo]

Exit 0 only when every check passes; then, and only then, the last line is a
verdict stamp to paste. It never arms auto-merge and never comments -- the
reviewer does both, so that the actor and the check stay separate.

Trap: check (f) accepts a MERGE verdict whose head has moved as long as the
patch-id has not, because that pair means a rebase. `git patch-id --stable` is
invariant to line-number shifts but not to changed context lines, so a rebase
that had to resolve anything moves the id and lands you in a full review --
the safe direction, and the reason the id is checked rather than the SHA alone.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

_spec = importlib.util.spec_from_file_location("pr_verdict", os.path.join(HERE, "pr-verdict.py"))
pv = importlib.util.module_from_spec(_spec)
sys.modules.setdefault("pr_verdict", pv)
_spec.loader.exec_module(pv)

REPO = pv.REPO
CORPUS_REPO = "StefanMaron/BusinessCentral.AL.Language.Tests"
BASELINE = "tests/expectations/count-baseline/test-count-baseline.json"
SUBMODULE = "tests/al-language"

# The same shape .github/scripts/check_corpus_linkage.sh accepts: the marker and
# a bare full URL on one line. A markdown link is deliberately not a declaration
# there, so it is not one here either -- reading it as one would answer a
# question CI says was never asked (#3330).
CORPUS_PR_LINE = re.compile(
    r"^[ \t]*Corpus-PR:[ \t]*https?://github\.com/StefanMaron/"
    r"BusinessCentral\.AL\.Language\.Tests/pull/(\d+)/?[ \t]*\.?[ \t]*$",
    re.IGNORECASE | re.MULTILINE)


def _default_ci_wait(pr: str, repo: str) -> tuple[int, str]:
    """ci-wait.py as a subprocess, exit code passed through, never reimplemented."""
    p = subprocess.run([sys.executable, os.path.join(HERE, "ci-wait.py"), str(pr),
                        "--timeout", "0"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return p.returncode, ((p.stdout or "") + (p.stderr or "")).strip()


def _json(runner, argv, default=None):
    rc, out = runner(argv)
    if rc != 0 or not out:
        return default
    try:
        return pv.load_json(out)
    except ValueError:
        return default


def run(pr: str, repo: str = REPO, *, runner=None, evaluate=None, ci_wait=None,
        corpus_repo: str = CORPUS_REPO) -> tuple[int, list[str]]:
    """(exit code, output lines). One line per check, PASS/FAIL labelled."""
    runner = runner or pv._default_runner
    evaluate = evaluate or pv.evaluate
    ci_wait = ci_wait or _default_ci_wait
    lines: list[str] = []
    checks: list[tuple[str, bool, str]] = []

    pr_json = pv.read_pr(repo, pr, runner)
    if not pr_json or not pr_json.get("headRefOid"):
        return 3, [f"could not read PR #{pr} on {repo} -- nothing was checked."]
    head = pr_json["headRefOid"]
    base = pr_json.get("baseRefName") or "main"
    branch = pr_json.get("headRefName") or ""
    body = pr_json.get("body") or ""

    # (a) branch ownership. The branch PREFIX, never the author field: every
    # loop pushes under one account, so the author cannot say whose it is.
    owned = branch.startswith("agent/")
    identity = branch.split("/")[1] if owned and len(branch.split("/")) > 1 else ""
    pool = re.sub(r"-\d+$", "", identity)
    checks.append(("branch ownership", owned,
                   f"{branch or '(none)'}"
                   + (f" -- pool {pool or '?'}, identity {identity}" if owned
                      else " -- not an agent/ branch, so not ours to merge")))

    # (b) merge-tree against the base. Textual conflicts only, which is what the
    # arming list asks for; a semantic conflict is CI's question.
    remote = pv._remote_for(repo, runner)
    if not remote:
        checks.append(("merge-tree clean", False, f"no git remote points at {repo}"))
    else:
        runner(["git", "fetch", "--quiet", remote, f"refs/pull/{pr}/head"])
        runner(["git", "fetch", "--quiet", remote, base])
        rc, out = runner(["git", "merge-tree", "--write-tree", "--messages",
                          f"{remote}/{base}", head])
        checks.append(("merge-tree clean", rc == 0,
                       f"{remote}/{base} vs {head[:12]}"
                       + ("" if rc == 0 else " -- " + out.splitlines()[0][:120]
                          if out else " -- CONFLICT")))

    # (c) required checks, delegated whole: ci-wait.py owns that verdict and its
    # exit codes, and a second implementation would be a second place to be wrong.
    rc, out = ci_wait(pr, repo)
    checks.append(("required checks green", rc == 0,
                   f"ci-wait.py --timeout 0 exit {rc}"
                   + ("" if rc == 0 else " (0 is the only green; 2 means not yet reported)")))

    # (d) corpus linkage, in both directions the merge bar asks for.
    detail, ok = "", True
    m = CORPUS_PR_LINE.search(body)
    if m:
        num = m.group(1)
        data = _json(runner, ["gh", "api", f"repos/{corpus_repo}/pulls/{num}"], default=None)
        merged = bool(data.get("merged")) if isinstance(data, dict) else False
        ok = merged
        detail = f"corpus PR #{num} " + ("MERGED" if merged else "NOT merged")
    else:
        detail = "no Corpus-PR: declaration in the body"
    files = _json(runner, ["gh", "api", f"repos/{repo}/pulls/{pr}/files", "--paginate"],
                  default=None)
    names = [f.get("filename", "") for f in files] if isinstance(files, list) else None
    if names is None:
        ok, detail = False, detail + "; could not read the PR's file list"
    elif any(n == SUBMODULE or n.startswith(SUBMODULE + "/") for n in names):
        if BASELINE in names:
            detail += "; pin bump carries the count-baseline"
        else:
            ok = False
            detail += f"; pin bump WITHOUT {BASELINE}"
    checks.append(("corpus linkage", ok, detail))

    # (e) no release run in flight: publish.yml pushes a fast-forward, and a
    # merge during its run kills it. Queued counts as in flight -- it will start
    # on its own, and nothing here would notice when it does.
    running, unreadable = 0, False
    for status in ("in_progress", "queued"):
        data = _json(runner, ["gh", "api",
                              f"repos/{repo}/actions/workflows/publish.yml/runs"
                              f"?status={status}"], default=None)
        if not isinstance(data, dict):
            unreadable = True
            continue
        running += int(data.get("total_count") or 0)
    checks.append(("no release run in flight", running == 0 and not unreadable,
                   "publish.yml: could not be read" if unreadable
                   else f"publish.yml: {running} run(s) in_progress or queued"))

    # (f) the review verdict itself. A rebase (head moved, patch-id unchanged) is
    # the case this whole pass exists for and is accepted; anything else is not.
    res = evaluate(repo, pr, runner=runner)
    verdict_ok = res.exit_code == 0 or (res.exit_code == 2 and res.patch_ok)
    how = {0: "MERGE on this head and patch",
           1: "FIX-FIRST/HOLD",
           2: ("MERGE, head moved but the patch-id is unchanged (a rebase)"
               if res.patch_ok else "MERGE, but the diff itself changed"),
           3: "no parseable MERGE verdict"}.get(res.exit_code, "unknown")
    checks.append(("review verdict", verdict_ok,
                   f"pr-verdict.py exit {res.exit_code} -- {how}"))

    for label, ok, detail in checks:
        lines.append(("PASS " if ok else "FAIL ") + f"{label}: {detail}")
    if not all(ok for _, ok, _ in checks):
        failed = [label for label, ok, _ in checks if not ok]
        lines.append(f"NOT arm-able: {len(failed)} check(s) failed ({', '.join(failed)}). "
                     "No stamp is printed -- report this to the coordinator.")
        return 1, lines
    lines.append("All checks passed. Paste this as the last line of your review comment:")
    lines.append("Verdict: MERGE " + pv.stamp(res.head or head, res.patch or "", "arm-check"))
    return 0, lines


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pr")
    ap.add_argument("--repo", default=REPO)
    ap.add_argument("--no-freshness-fetch", action="store_true")
    args = ap.parse_args()

    refused, notes = pv.freshness_gate(
        [os.path.abspath(__file__), os.path.join(HERE, "pr-verdict.py")],
        remote_check=not args.no_freshness_fetch)
    for line in notes:
        print(line)
    if refused:
        return 3

    code, lines = run(args.pr, args.repo)
    for line in lines:
        print(line)
    return code


if __name__ == "__main__":
    sys.exit(main())
