#!/usr/bin/env python3
"""The cheap re-review pass: the mechanical arming conditions, one PR, no judgement.

    tools/arm-check.py <PR>

Run this instead of a full review when the diff is unchanged since the last full review.
Checks, each printed PASS/FAIL: (a) branch prefix is one this loop may own, (b) merge-tree
against the base is clean, (c) `tools/ci-wait.py <PR> --timeout 0` exits 0, (d) every
`Corpus-PR:` declared in the body is MERGED and a pin bump carries the count baseline,
(e) no `publish.yml` run is in progress, (f) `pr-verdict.py` finds a MERGE on this patch
(a moved head with the same patch is a rebase and passes; a moved patch refuses).

Exit 0 only when every check passes; then the last line is the verdict stamp
(`kind: arm-check`) to paste. Never arms, never comments.

Traps
-----
* Every check is bound to the head SHA read at start; a head that moves during the run
  makes the tool refuse (exit 2) rather than stamp a head it did not check.
* A failed `git fetch` refuses (exit 3); a check against cached refs is not a check.
* `--repo` other than the default refuses: `ci-wait.py` has no repository switch.

Why: most reviewer runs re-confirm an unchanged PR
(https://fbakkensen.github.io/al-runner-retro/#e-12).
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

try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
    print("note: tools/agent_stdio.py could not be imported alongside this copy of "
          "arm-check.py -- the verdict stamp carries em dashes, which a cp1252 "
          "console cannot print. Extract that file too.", file=sys.stderr)
if _stdio is not None:
    # Before any print, and in THIS file rather than only in the module it
    # imports: tools/test_agent_stdio.py checks each CLI for the call at module
    # level, because an import chain is not a guarantee the next edit keeps.
    _stdio.enable_utf8_stdio()

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

    # ci-wait.py takes no --repo and hardcodes this one, so on any other
    # repository check (c) would silently report on THIS repository's PR of the
    # same number -- a green about a different pull request. Refuse instead of
    # answering; teaching ci-wait.py a --repo is a change to that tool.
    if repo != REPO:
        return 3, [f"REFUSING to check PR #{pr} on {repo}: this tool only supports "
                   f"{REPO}, because ci-wait.py -- which decides the required-checks "
                   "check -- takes no --repo and would report on the same-numbered PR "
                   "of that repository instead. Nothing was checked."]

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
        for spec in (f"refs/pull/{pr}/head", base):
            rc, out = runner(["git", "fetch", "--quiet", remote, spec])
            if rc != 0:
                # merge-tree against a stale cached ref answers about older
                # code and looks exactly like a clean result.
                return 3, [f"REFUSING to check PR #{pr}: git fetch {remote} {spec} "
                           f"failed (rc={rc}), so a merge-tree here would compare "
                           "against cached refs. Nothing was checked. "
                           + (out.splitlines()[0][:160] if out else "")]
        rc, out = runner(["git", "rev-parse", "--verify", "--quiet",
                          f"{remote}/{base}^{{commit}}"])
        if rc != 0:
            return 3, [f"REFUSING to check PR #{pr}: {remote}/{base} does not resolve "
                       "after the fetch, so there is no base to compare against. "
                       "Nothing was checked."]
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
    # EVERY declaration: a body can carry more than one, and checking only the
    # first passes a PR whose second corpus PR is still open.
    nums = CORPUS_PR_LINE.findall(body)
    if nums:
        states = []
        for num in nums:
            data = _json(runner, ["gh", "api", f"repos/{corpus_repo}/pulls/{num}"],
                         default=None)
            merged = bool(data.get("merged")) if isinstance(data, dict) else False
            ok = ok and merged
            states.append(f"#{num} " + ("MERGED" if merged else "NOT merged"))
        detail = "corpus PR " + ", ".join(states)
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
    res = evaluate(repo, pr, runner=runner, expect_head=head)
    if res.head_moved_during_check:
        for label, ok, dtl in checks:
            lines.append(("PASS " if ok else "FAIL ") + f"{label}: {dtl}")
        lines.extend(res.lines)
        lines.append("NOT arm-able: the head moved while this pass was running, so "
                     "the checks above describe a commit that is no longer the head. "
                     "No stamp is printed.")
        return 2, lines
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
    if not res.patch:
        # Every passing path above computed a patch-id, so this cannot normally
        # happen -- and a stamp built from an empty one would be malformed, which
        # pr-verdict.py would then read as "no verdict" on the next pass.
        lines.append("NOT arm-able: every check passed but no patch-id is available, "
                     "so no verdict line can be stamped.")
        return 1, lines
    lines.append("All checks passed. Paste this as the last line of your review comment:")
    lines.append("Verdict: MERGE " + pv.stamp(head, res.patch, "arm-check"))
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
