#!/usr/bin/env python3
"""Tests for tools/arm-check.py.

Each fixture fails exactly one check and asserts a non-zero exit, so no single failing
check can be lost in the aggregate; the production ci-wait wrapper and fetch path are
exercised, not replaced. Run: python tools/test_arm_check.py
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

_pvspec = importlib.util.spec_from_file_location("pr_verdict", os.path.join(HERE, "pr-verdict.py"))
pv = importlib.util.module_from_spec(_pvspec)
sys.modules["pr_verdict"] = pv
_pvspec.loader.exec_module(pv)

_spec = importlib.util.spec_from_file_location("arm_check", os.path.join(HERE, "arm-check.py"))
ac = importlib.util.module_from_spec(_spec)
sys.modules["arm_check"] = ac
_spec.loader.exec_module(ac)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


HEAD = "1f2e3d4c5b6a79880123456789abcdef01234567"
OTHER = "9988776655443322110099887766554433221100"
PATCH = "0123456789ab"
OTHER_PATCH = "ba9876543210"
REPO = "StefanMaron/BusinessCentral.AL.Runner"
CORPUS_URL = "https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/226"
BASELINE = "tests/expectations/count-baseline/test-count-baseline.json"


def env(*, branch="agent/fbk-1/issue-3680", body="Closes #1", files=("tools/x.py",),
        merge_tree_rc=0, corpus_merged=True, publish_running=0, comments=None,
        patch=PATCH, ci_rc=0, fetch_rc=0, corpus_states=None, heads=None):
    """One fake world. Every knob below is exactly one check's input."""
    # `heads` lets the PR's head change between reads, which is what happens when
    # somebody pushes while a pass is running.
    head_reads = list(heads or [])
    pr_json = {"headRefOid": HEAD, "baseRefName": "main", "headRefName": branch,
               "body": body}
    comments = comments if comments is not None else [
        {"body": "Agent review (fbk-1).\n\nVerdict: MERGE " + pv.stamp(HEAD, PATCH, "full"),
         "created_at": "2026-09-08T10:00:00Z", "id": 1,
         "author_association": "OWNER", "user": {"login": "maintainer"}}]
    # Every fixture comment is an OWNER unless it says otherwise: the author
    # filter is exercised by its own fixture below, not by every other one.
    comments = [dict({"author_association": "OWNER", "user": {"login": "maintainer"}}, **c)
                for c in comments]

    def runner(argv, **kw):
        joined = " ".join(argv)
        if joined.startswith("gh pr view"):
            if head_reads:
                return 0, json.dumps(dict(pr_json, headRefOid=head_reads.pop(0)))
            return 0, json.dumps(pr_json)
        if "/issues/" in joined and joined.endswith("comments --paginate"):
            return 0, json.dumps(comments)
        if "/pulls/" in joined and joined.endswith("/files --paginate"):
            return 0, json.dumps([{"filename": f} for f in files])
        if "BusinessCentral.AL.Language.Tests/pulls/" in joined:
            num = joined.rsplit("/", 1)[-1]
            merged = corpus_states[num] if corpus_states else corpus_merged
            return 0, json.dumps({"merged": merged,
                                  "state": "closed" if merged else "open"})
        if "actions/workflows/publish.yml/runs" in joined:
            return 0, json.dumps({"total_count": publish_running, "workflow_runs": []})
        if argv[:2] == ["git", "remote"]:
            return 0, "origin\thttps://github.com/%s.git (fetch)" % REPO
        if argv[:2] == ["git", "fetch"]:
            return fetch_rc, "" if fetch_rc == 0 else "fatal: could not read from remote"
        if argv[:2] == ["git", "merge-tree"]:
            return merge_tree_rc, "" if merge_tree_rc == 0 else "CONFLICT (content): x"
        if argv[:2] == ["git", "rev-parse"]:
            return 0, HEAD
        return 1, "unexpected: " + joined

    def evaluate(repo, pr, **kw):
        # Forward every keyword the production call passes -- expect_head among
        # them. A fixture that silently drops one tests a weaker tool than ships.
        kw.pop("runner", None)
        return pv.evaluate(repo, pr, runner=runner,
                           patch_id_of=lambda *a, **k: (patch, ""), **kw)

    def ci_wait(pr, repo):
        return ci_rc, f"ci-wait stub exit {ci_rc}"

    return dict(runner=runner, evaluate=evaluate, ci_wait=ci_wait)


def run(**kw):
    return ac.run("1", REPO, **env(**kw))


code, lines = run()
check("a clean world exits 0", code == 0, f"{code}\n" + "\n".join(lines))
check("...and prints six labelled checks",
      sum(1 for l in lines if l.startswith(("PASS ", "FAIL "))) == 6, "\n".join(lines))
check("...and every one is a PASS", all(not l.startswith("FAIL ") for l in lines),
      "\n".join(lines))
check("...and it ends with an arm-check stamp",
      lines[-1].endswith(pv.stamp(HEAD, PATCH, "arm-check")), lines[-1])
check("...whose stamped line parses back as a MERGE verdict",
      pv.parse_verdict(lines[-1])[0] is not None
      and pv.parse_verdict(lines[-1])[0].kind == "arm-check", lines[-1])

code, lines = run(branch="feature/outside-contributor")
check("a branch outside the agent pool fails", code != 0, str(code))
check("...and the failing line names the branch",
      any(l.startswith("FAIL ") and "feature/outside-contributor" in l for l in lines),
      "\n".join(lines))

code, lines = run(branch="agent/fbk-1/issue-3680")
check("the pool name is reported", any("fbk" in l for l in lines), "\n".join(lines))

code, lines = run(merge_tree_rc=1)
check("a conflicting merge-tree fails", code != 0, str(code))

code, lines = run(ci_rc=2)
check("ci-wait exit 2 (no verdict yet) fails", code != 0, str(code))
code, lines = run(ci_rc=1)
check("ci-wait exit 1 (red) fails", code != 0, str(code))
code, lines = run(ci_rc=3)
check("ci-wait exit 3 (undetermined) fails", code != 0, str(code))

code, lines = run(body=f"Closes #1\n\nCorpus-PR: {CORPUS_URL}", corpus_merged=False)
check("an unmerged corpus PR fails", code != 0, str(code))
code, lines = run(body=f"Closes #1\n\nCorpus-PR: {CORPUS_URL}", corpus_merged=True)
check("a merged corpus PR passes", code == 0, "\n".join(lines))
code, lines = run(body=f"Closes #1\n\nCorpus-PR: [#226]({CORPUS_URL})", corpus_merged=False)
check("a markdown-linked Corpus-PR line is not a declaration, so nothing is looked up",
      code == 0, "\n".join(lines))

code, lines = run(files=("tests/al-language", "AlRunner/x.cs"))
check("a pin bump without the count-baseline fails", code != 0, str(code))
code, lines = run(files=("tests/al-language", BASELINE))
check("a pin bump with the count-baseline passes", code == 0, "\n".join(lines))

code, lines = run(publish_running=1)
check("a release run in progress fails", code != 0, str(code))

code, lines = run(comments=[{"body": "Verdict: FIX-FIRST (no negative case) "
                                     + pv.stamp(HEAD, PATCH, "full"),
                             "created_at": "2026-09-08T10:00:00Z", "id": 1}])
check("a FIX-FIRST verdict fails, whatever else is green", code != 0, str(code))

code, lines = run(comments=[{"body": "Orchestrator review\n\nLooks fine to me.",
                             "created_at": "2026-09-08T10:00:00Z", "id": 1}])
check("no parseable verdict fails", code != 0, str(code))

# The rebase case this split exists for: the head moved, the patch-id did not.
code, lines = run(comments=[{"body": "Verdict: MERGE " + pv.stamp(OTHER, PATCH, "full"),
                             "created_at": "2026-09-08T10:00:00Z", "id": 1}])
check("a moved head with an unchanged patch-id passes (a rebase)", code == 0,
      "\n".join(lines))

code, lines = run(comments=[{"body": "Verdict: MERGE " + pv.stamp(OTHER, OTHER_PATCH, "full"),
                             "created_at": "2026-09-08T10:00:00Z", "id": 1}])
check("a moved head AND a moved patch-id fails (a new push)", code != 0, str(code))

code, lines = run(comments=[{"body": "Verdict: MERGE " + pv.stamp(HEAD, OTHER_PATCH, "full"),
                             "created_at": "2026-09-08T10:00:00Z", "id": 1}])
check("same head, different patch-id fails", code != 0, str(code))

code, lines = run(comments=[{"body": "Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"),
                            "created_at": "2026-09-08T10:00:00Z", "id": 1,
                            "author_association": "NONE", "user": {"login": "passer-by"}}])
check("a verdict from an author without write access fails", code != 0, str(code))

# ci-wait.py takes no --repo, so on another repository check (c) would report on
# THIS repository's PR of the same number.
code, lines = ac.run("1", "someone/else", **env())
check("a non-default --repo is refused, not answered", code == 3, str(code))
check("...and nothing was checked",
      not any(l.startswith(("PASS ", "FAIL ")) for l in lines), str(lines))
check("...and the refusal names ci-wait.py as the reason",
      any("ci-wait.py" in l for l in lines), str(lines))

# (B) a failed fetch means merge-tree would compare cached refs.
code, lines = run(fetch_rc=128)
check("a failed git fetch is refused, not checked", code == 3, str(code))
check("...and no check line is printed at all",
      not any(l.startswith(("PASS ", "FAIL ")) for l in lines), str(lines))

# (C) the checks and the stamp must describe ONE commit.
code, lines = run(heads=[HEAD, OTHER])
check("a head that moves mid-pass exits 2", code == 2, f"{code} {lines}")
check("...and says so", any("moved" in l for l in lines), str(lines))
check("...and prints no stamp", not any(l.startswith("Verdict:") for l in lines), str(lines))

# (E) EVERY Corpus-PR declaration, not just the first.
TWO_CORPUS = ("Closes #1"
              + chr(10) + chr(10) + "Corpus-PR: " + CORPUS_URL
              + chr(10) + "Corpus-PR: "
              + "https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/294")
code, lines = run(body=TWO_CORPUS, corpus_states={"226": True, "294": False})
check("a second, unmerged corpus PR fails even when the first merged", code != 0, str(code))
check("...and both are named", any("294" in l and "226" in l for l in lines), str(lines))
code, lines = run(body=TWO_CORPUS, corpus_states={"226": True, "294": True})
check("two merged corpus PRs pass", code == 0, str(lines))

# No fixture that fails a check may ever print a stamp -- the stamp is what a
# reviewer pastes, so printing one on a failure is how a FIX-FIRST gets armed.
for kw in (dict(merge_tree_rc=1), dict(ci_rc=1), dict(publish_running=1),
           dict(branch="feature/x")):
    code, lines = run(**kw)
    check(f"no stamp is printed when {list(kw)[0]} fails",
          not any(l.startswith("Verdict:") for l in lines), "\n".join(lines))


# --- the PRODUCTION wrappers, not the fixtures ---------------------------
#
# Every check above replaces the ci-wait wrapper, so an implementation whose
# wrapper always reported success would stay green here. These drive the real
# _default_ci_wait against a stand-in ci-wait.py.

import subprocess  # noqa: E402
import tempfile  # noqa: E402

FAKE_CI_WAIT = ("import sys" + chr(10)
                + "print('stand-in ci-wait, argv=' + ' '.join(sys.argv[1:]))" + chr(10)
                + "sys.exit(int(sys.argv[-1]))" + chr(10))

def stand_in_ci_wait(directory: str, code: int) -> None:
    open(os.path.join(directory, "ci-wait.py"), "w", encoding="utf-8").write(
        "import sys" + chr(10) + "print('stand-in ci-wait')" + chr(10)
        + f"sys.exit({code})" + chr(10))


with tempfile.TemporaryDirectory() as d:
    real_here = ac.HERE
    try:
        ac.HERE = d
        stand_in_ci_wait(d, 0)
        rc, out = ac._default_ci_wait("1", REPO)
        check("the production ci-wait wrapper passes exit 0 through", rc == 0, f"{rc} {out}")
        check("...and returns the child's output", "stand-in ci-wait" in (out or ""), str(out))

        for want in (1, 2, 3):
            stand_in_ci_wait(d, want)
            rc, out = ac._default_ci_wait("1", REPO)
            check(f"the production ci-wait wrapper passes exit {want} through", rc == want,
                  f"{rc} {out}")
            kw = env()
            kw["ci_wait"] = ac._default_ci_wait
            code, lines = ac.run("1", REPO, **kw)
            check(f"arm-check refuses through the real wrapper when ci-wait exits {want}",
                  code != 0, f"{code} {lines}")

        stand_in_ci_wait(d, 0)
        kw = env()
        kw["ci_wait"] = ac._default_ci_wait
        code, lines = ac.run("1", REPO, **kw)
        check("...and arms through the real wrapper when ci-wait exits 0", code == 0,
              f"{code} {lines}")
    finally:
        ac.HERE = real_here


# --- the arming section points at these tools ----------------------------

skill = open(os.path.join(ROOT, ".claude", "skills", "orchestrating-a-session", "SKILL.md"),
             encoding="utf-8").read()
arming = skill.split("A reviewer that approves a PR arms auto-merge")[-1][:6000]
check("the arming section names tools/pr-verdict.py", "tools/pr-verdict.py" in arming)
check("the arming section names tools/arm-check.py", "tools/arm-check.py" in arming)
check("it still names the command being gated", "gh pr merge" in arming)
check("the recorded-SHA paragraph now names the patch-id",
      "patch-id" in arming or "patch " in arming, arming[:200])
check("it says the batch-conflict condition is not one arm-check runs",
      "batch" in arming.split("tools/arm-check.py")[-1][:900], arming[:200])

# One arming instruction, in one place: autonomous-cycle used to carry a verbatim
# copy of the block above, so the two skills could drift while both read as
# authoritative.
auto = open(os.path.join(ROOT, ".claude", "skills", "autonomous-cycle", "SKILL.md"),
            encoding="utf-8").read()
check("autonomous-cycle no longer restates the arming conditions",
      "Record the SHA you armed against" not in auto)
check("...and points at orchestrating-a-session instead", "orchestrating-a-session" in auto)
check("...and does not carry its own copy of the merge-tree condition",
      "git merge-tree --write-tree --messages origin/<base>" not in auto)

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
