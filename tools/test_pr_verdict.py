#!/usr/bin/env python3
"""Tests for tools/pr-verdict.py: the verdict grammar and its exit codes.

Exit 0 is the only answer that lets a PR be armed, so every fixture that is not an exact
MERGE on the current head and patch asserts a non-zero exit. Legacy comment shapes without a
verdict line, a stale head, a foreign author, a misplaced marker and a malformed newer
comment are all fixtures here. Run: python tools/test_pr_verdict.py
"""
from __future__ import annotations

import importlib.util
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

_spec = importlib.util.spec_from_file_location("pr_verdict", os.path.join(HERE, "pr-verdict.py"))
pv = importlib.util.module_from_spec(_spec)
sys.modules["pr_verdict"] = pv
_spec.loader.exec_module(pv)

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

def instantiate(grammar: str, decision: str = "MERGE", reason: str | None = None,
                head: str = HEAD, patch: str = PATCH, kind: str = "full") -> str:
    """A concrete line built FROM the grammar constant, not written beside it.

    reviewer.md is checked against GRAMMAR and GRAMMAR is instantiated here, so
    the prose, the constant and VERDICT_RE are one chain: renaming a placeholder
    fails loudly instead of leaving a hand-written example agreeing with a regex
    the documentation no longer describes.
    """
    line = (grammar
            .replace("MERGE|FIX-FIRST|HOLD", decision)
            .replace(" (<reason, only for FIX-FIRST/HOLD>)",
                     f" ({reason})" if reason else "")
            .replace("<full 40-char sha>", head)
            .replace("<diff fingerprint, 12 hex>", patch)
            .replace("full|arm-check", kind))
    assert "<" not in line and "|" not in line, (
        "a placeholder in GRAMMAR was renamed and this helper no longer fills it: " + line)
    return line


DOCUMENTED = instantiate(pv.GRAMMAR)

# The five header styles found in the window. None carries a verdict line, so
# each must be unparseable -- that is the state this PR is changing, and the
# tool must never guess a verdict out of prose.
LEGACY = [
    "Review of #3401\n\nLooks good, the proving test asserts a value. Ship it.",
    f"Review - head {HEAD[:8]}\n\nNo findings. Meets the merge bar.",
    "Orchestrator review\n\nMerge bar met; arming auto-merge.",
    "Reviewer pass\n\nOne nit, not blocking. Approve.",
    "Agent review\n\nHold: the corpus PR has not merged yet.",
]


# --- grammar --------------------------------------------------------------

def parsed(body: str):
    return pv.parse_verdict(body)


v, bad = parsed(DOCUMENTED)
check("the documented line parses", v is not None, f"malformed={bad!r}")
if v is not None:
    check("decision", v.decision == "MERGE", v.decision)
    check("head", v.head == HEAD, v.head)
    check("patch", v.patch == PATCH, v.patch)
    check("kind", v.kind == "full", v.kind)
    check("MERGE carries no reason", v.reason is None, repr(v.reason))

v, bad = parsed(f"Some findings.\n\nVerdict: FIX-FIRST (the negative case is not covered) "
                f"— head {HEAD} — patch {PATCH} — kind: full")
check("FIX-FIRST with a reason parses", v is not None and v.decision == "FIX-FIRST", f"{v} {bad}")
check("its reason is kept", v is not None and v.reason == "the negative case is not covered",
      repr(v.reason if v else None))

v, bad = parsed(f"Verdict: HOLD (corpus PR not merged) — head {HEAD} — patch {PATCH} "
                "— kind: arm-check")
check("HOLD parses, kind arm-check",
      v is not None and v.decision == "HOLD" and v.kind == "arm-check", f"{v} {bad}")

for i, body in enumerate(LEGACY):
    v, bad = parsed(body)
    check(f"legacy style {i + 1} carries no verdict", v is None, repr(body.splitlines()[0]))

v, bad = parsed(f"{DOCUMENTED}\n\nThanks for the quick turnaround!")
check("a verdict line that is not the last line does not match", v is None, str(v))

v, bad = parsed(f"{DOCUMENTED}\n\n   \n")
check("trailing blank lines are ignored", v is not None, f"malformed={bad!r}")

v, bad = parsed(DOCUMENTED + "\r\n")
check("a CRLF body parses", v is not None, f"malformed={bad!r}")

v, bad = parsed(f"Verdict: MERGE (all good) — head {HEAD} — patch {PATCH} — kind: full")
check("MERGE with a reason is malformed, not a MERGE", v is None and bad is not None, f"{v} {bad}")

v, bad = parsed(f"Verdict: FIX-FIRST — head {HEAD} — patch {PATCH} — kind: full")
check("FIX-FIRST without a reason is malformed", v is None and bad is not None, f"{v} {bad}")

v, bad = parsed(f"Verdict: MERGE — head {HEAD[:8]} — patch {PATCH} — kind: full")
check("an abbreviated head does not match", v is None, str(v))

v, bad = parsed(f"Verdict: MERGE — head {HEAD} — patch {PATCH[:11]} — kind: full")
check("an 11-hex patch id does not match", v is None, str(v))

v, bad = parsed(f"Verdict: MERGE — head {HEAD} — patch {PATCH} — kind: quick")
check("an unknown kind does not match", v is None, str(v))

v, bad = parsed(f"**Verdict: MERGE** — head {HEAD} — patch {PATCH} — kind: full")
check("a bolded marker does not match", v is None, str(v))

# --stamp round-trips through the parser: the reviewer pastes exactly this.
line = "Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full")
v, bad = parsed(line)
check("--stamp output round-trips (MERGE)",
      v is not None and v.head == HEAD and v.patch == PATCH, line)
line = "Verdict: HOLD (waiting on the corpus PR) " + pv.stamp(HEAD, PATCH, "arm-check")
v, bad = parsed(line)
check("--stamp output round-trips (HOLD, arm-check)",
      v is not None and v.kind == "arm-check" and v.decision == "HOLD", line)


# --- newest wins ----------------------------------------------------------

def comment(body: str, when: str, cid: int = 0, assoc: str = "OWNER", login: str = "maintainer"):
    return {"body": body, "created_at": when, "id": cid,
            "author_association": assoc, "user": {"login": login}}


older = "Verdict: FIX-FIRST (missing negative case) " + pv.stamp(OTHER, OTHER_PATCH, "full")
newer = "Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full")
v, bad = pv.newest_verdict([comment(older, "2026-09-01T10:00:00Z", 1),
                            comment(newer, "2026-09-02T10:00:00Z", 2)])
check("the newest verdict wins", v is not None and v.decision == "MERGE", str(v))
v, bad = pv.newest_verdict([comment(newer, "2026-09-02T10:00:00Z", 2),
                            comment(older, "2026-09-03T10:00:00Z", 1)])
check("newest by created_at, not by list order",
      v is not None and v.decision == "FIX-FIRST", str(v))


# --- exit codes -----------------------------------------------------------

def fake(pr_json: dict | None, comments: list | None, *, boom: bool = False):
    """A runner keyed on the command prefix. Nothing here touches the network."""
    def runner(argv, **kw):
        if boom:
            raise OSError("gh is not installed")
        joined = " ".join(argv)
        if joined.startswith("gh pr view"):
            return (0, json.dumps(pr_json)) if pr_json is not None else (1, "no such PR")
        if "/comments" in joined:
            return (0, json.dumps(comments)) if comments is not None else (1, "404")
        return (1, "unexpected: " + joined)
    return runner


PRJSON = {"headRefOid": HEAD, "baseRefName": "main", "headRefName": "agent/fbk-1/issue-3680"}


def result(comments, *, pr_json=PRJSON, patch=PATCH, boom=False):
    return pv.evaluate("o/r", "1", runner=fake(pr_json, comments, boom=boom),
                       patch_id_of=lambda *a, **k: (patch, ""))


r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"), "2026-09-02T10:00:00Z")])
check("MERGE on the current head and patch exits 0", r.exit_code == 0, str(r.exit_code))
check("...and reports both as unmoved", r.head_ok and r.patch_ok, f"{r.head_ok} {r.patch_ok}")

r = result([comment("Verdict: MERGE " + pv.stamp(OTHER, PATCH, "full"), "2026-09-02T10:00:00Z")])
check("MERGE on a stale head exits 2", r.exit_code == 2, str(r.exit_code))
check("...and says the head moved, not the patch", (not r.head_ok) and r.patch_ok,
      f"{r.head_ok} {r.patch_ok}")
check("...and the output names the head", any("head" in l for l in r.lines), str(r.lines))

r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, OTHER_PATCH, "full"), "2026-09-02T10:00:00Z")])
check("MERGE on a changed patch exits 2", r.exit_code == 2, str(r.exit_code))
check("...and says the patch moved", r.head_ok and not r.patch_ok, f"{r.head_ok} {r.patch_ok}")

r = result([comment("Verdict: FIX-FIRST (no negative case) " + pv.stamp(HEAD, PATCH, "full"),
                    "2026-09-02T10:00:00Z")])
check("FIX-FIRST exits 1", r.exit_code == 1, str(r.exit_code))

r = result([comment("Verdict: HOLD (corpus PR open) " + pv.stamp(HEAD, PATCH, "full"),
                    "2026-09-02T10:00:00Z")])
check("HOLD exits 1", r.exit_code == 1, str(r.exit_code))

r = result([comment("Verdict: FIX-FIRST (x) " + pv.stamp(OTHER, OTHER_PATCH, "full"),
                    "2026-09-02T10:00:00Z")])
check("a stale FIX-FIRST still exits 1, never 0", r.exit_code == 1, str(r.exit_code))

r = result([comment(b, "2026-09-0%dT10:00:00Z" % (i + 1)) for i, b in enumerate(LEGACY)])
check("five legacy comments and no verdict line exits 3", r.exit_code == 3, str(r.exit_code))

r = result([])
check("no comments at all exits 3", r.exit_code == 3, str(r.exit_code))

r = result([comment(f"Verdict: MERGE (all good) — head {HEAD} — patch {PATCH} "
                    "— kind: full", "2026-09-02T10:00:00Z")])
check("a malformed verdict line exits 3, not 0", r.exit_code == 3, str(r.exit_code))
check("...and says malformed rather than absent",
      any("malformed" in l.lower() for l in r.lines), str(r.lines))

# A malformed line NEWER than a readable verdict must not leave the older one
# standing: a FIX-FIRST that forgot its reason would otherwise be armed as the
# MERGE below it, which is the mis-arming the grammar exists to stop.
STALE_MERGE_UNDER_MALFORMED = [
    comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"), "2026-09-01T10:00:00Z", 1),
    comment(f"Verdict: FIX-FIRST — head {HEAD} — patch {PATCH} — kind: full",
            "2026-09-02T10:00:00Z", 2)]
r = result(STALE_MERGE_UNDER_MALFORMED)
check("a malformed line newer than a readable MERGE exits 3, not 0", r.exit_code == 3,
      f"{r.exit_code} {r.lines}")
check("...and says the malformed line is the newer one",
      any("NEWER" in l for l in r.lines), str(r.lines))

v, bad = pv.newest_verdict(STALE_MERGE_UNDER_MALFORMED)
check("newest_verdict reports the older verdict AND the newer malformed line",
      v is not None and bad is not None, f"{v} {bad}")

v, bad = parsed(f"**Verdict: MERGE** — head {HEAD} — patch {PATCH} — kind: full")
check("a bolded marker is reported as malformed, not absent", bad is not None, repr(bad))

# A verdict line with the signature UNDER it: the comment carries a verdict, so
# treating it as carrying none would leave the older MERGE below as the newest
# actionable verdict -- a newer HOLD read as an earlier MERGE.
v, bad = parsed(instantiate(pv.GRAMMAR, "HOLD", "the corpus PR is open")
                + "\n\n-- posted by an automated reviewer")
check("a verdict followed by a signature is malformed, not absent",
      v is None and bad is not None and "LAST" in (bad or ""), f"{v} {bad}")

SIGNED_HOLD_OVER_MERGE = [
    comment(instantiate(pv.GRAMMAR), "2026-09-01T10:00:00Z", 1),
    comment(instantiate(pv.GRAMMAR, "HOLD", "the corpus PR is open")
            + "\n\n-- posted by an automated reviewer", "2026-09-02T10:00:00Z", 2)]
r = result(SIGNED_HOLD_OVER_MERGE)
check("a newer signed HOLD never leaves the older MERGE standing", r.exit_code == 3,
      f"{r.exit_code} {r.lines}")

# (C) every check in a multi-check pass must describe ONE commit.
r = pv.evaluate("o/r", "1", runner=fake(PRJSON, []),
                patch_id_of=lambda *a, **k: (PATCH, ""), expect_head=OTHER)
check("a head that moved since the caller's own read exits 2", r.exit_code == 2,
      str(r.exit_code))
check("...and is flagged as moved-during-check, not as a stale verdict",
      r.head_moved_during_check, str(r.head_moved_during_check))

# (B) a failed fetch must not be answered from cached refs.
def fetch_fails(argv, **kw):
    if argv[:2] == ["git", "remote"]:
        return 0, "origin	https://github.com/o/r.git (fetch)"
    if argv[:2] == ["git", "fetch"]:
        return 128, "fatal: could not read from remote repository"
    return 0, HEAD


pid, note = pv.patch_id_for_pr("o/r", "1", HEAD, "main", fetch_fails)
check("a failed git fetch yields no fingerprint", pid is None, str(pid))
check("...and says so rather than comparing cached refs",
      "fetch" in note and "cached" in note, note)

# This is a PUBLIC repository and both the head SHA and the patch-id are public,
# so a well-formed MERGE line is composable by anyone. Write access is the
# boundary the tool enforces.
UNTRUSTED = [comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"),
                     "2026-09-02T10:00:00Z", 1, assoc="NONE", login="passer-by")]
r = result(UNTRUSTED)
check("a MERGE from an author with no write access never exits 0", r.exit_code == 3,
      f"{r.exit_code} {r.lines}")
check("...and says on stderr whose comment was ignored",
      any("passer-by" in l for l in r.stderr_lines), str(r.stderr_lines))

r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"),
                    "2026-09-02T10:00:00Z", 1, assoc="CONTRIBUTOR", login="drive-by")])
check("CONTRIBUTOR is not write access either", r.exit_code == 3, str(r.exit_code))

for assoc in pv.TRUSTED_ASSOCIATIONS:
    r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"),
                        "2026-09-02T10:00:00Z", 1, assoc=assoc)])
    check(f"{assoc} may hand down a verdict", r.exit_code == 0, str(r.exit_code))

r = result([{"body": "Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"),
             "created_at": "2026-09-02T10:00:00Z", "id": 1}])
check("a comment with no author_association at all is not trusted", r.exit_code == 3,
      str(r.exit_code))

# An edited comment keeps its original created_at, so updated_at has to count.
r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"), "2026-09-02T10:00:00Z", 1),
            dict(comment("Verdict: FIX-FIRST (edited in later) " + pv.stamp(HEAD, PATCH, "full"),
                         "2026-09-01T10:00:00Z", 2), updated_at="2026-09-03T10:00:00Z")])
check("an edited comment is ordered by updated_at, so its FIX-FIRST wins",
      r.exit_code == 1, f"{r.exit_code} {r.lines}")

r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"), "2026-09-02T10:00:00Z")],
           pr_json=None)
check("an unreadable PR exits 3", r.exit_code == 3, str(r.exit_code))

r = result(None)
check("unreadable comments exit 3", r.exit_code == 3, str(r.exit_code))

r = result([comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"), "2026-09-02T10:00:00Z")],
           boom=True)
check("a runner that raises exits 3, not a traceback", r.exit_code == 3, str(r.exit_code))

r = pv.evaluate("o/r", "1",
                runner=fake(PRJSON, [comment("Verdict: MERGE " + pv.stamp(HEAD, PATCH, "full"),
                                             "2026-09-02T10:00:00Z")]),
                patch_id_of=lambda *a, **k: (None, "git diff produced nothing"))
check("an uncomputable patch id exits 3, never 0", r.exit_code == 3, str(r.exit_code))

# The whole-suite invariant: nothing but a MERGE on the current head and patch
# exits 0, because exit 0 is what arms auto-merge.
greens = 0
for comments in ([comment("Verdict: MERGE " + pv.stamp(OTHER, PATCH, "full"), "2026-09-02T10:00:00Z")],
                 [comment("Verdict: FIX-FIRST (x) " + pv.stamp(HEAD, PATCH, "full"),
                          "2026-09-02T10:00:00Z")],
                 [comment(LEGACY[0], "2026-09-02T10:00:00Z")], [],
                 STALE_MERGE_UNDER_MALFORMED, UNTRUSTED):
    if result(comments).exit_code == 0:
        greens += 1
check("no non-MERGE fixture exits 0", greens == 0, f"{greens} green(s)")


# --- the patch fingerprint ------------------------------------------------

DIFF = (b"diff --git a/x b/x\nindex 1111111..2222222 100644\n--- a/x\n+++ b/x\n"
        b"@@ -10,3 +10,4 @@ def f():\n-a\n+    b\n")
SHIFTED = (DIFF.replace(b"@@ -10,3 +10,4 @@ def f():", b"@@ -99,3 +99,4 @@ def g():")
               .replace(b"index 1111111..2222222", b"index aaaaaaa..bbbbbbb"))
DEDENTED = DIFF.replace(b"+    b", b"+b")


def brunner_for(diff):
    def brunner(argv, input=None):
        return (0, diff) if argv[:2] == ["git", "diff"] else (1, b"")
    return brunner


base, _ = pv.compute_patch_fingerprint("origin/main", HEAD, brunner=brunner_for(DIFF))
shifted, _ = pv.compute_patch_fingerprint("origin/main", HEAD, brunner=brunner_for(SHIFTED))
dedented, _ = pv.compute_patch_fingerprint("origin/main", HEAD, brunner=brunner_for(DEDENTED))
check("the fingerprint is 12 hex", bool(base) and len(base) == 12, str(base))
check("a rebase (line numbers and blob hashes move) keeps it", base == shifted,
      f"{base} {shifted}")
check("a whitespace-only difference CHANGES it -- git patch-id would not",
      base != dedented, f"{base} {dedented}")

pid, note = pv.compute_patch_fingerprint("origin/main", HEAD, brunner=brunner_for(b""))
check("an empty diff is None, never a value", pid is None, str(pid))


# (H) the same claim through real git, not through a canned diff.
import subprocess  # noqa: E402
import tempfile  # noqa: E402


def git(d, *args):
    return subprocess.run(["git", "-C", d, *args], capture_output=True, text=True,
                          encoding="utf-8", errors="replace")


with tempfile.TemporaryDirectory() as d:
    git(d, "init", "-q", "-b", "main")
    git(d, "config", "user.email", "t@example.invalid")
    git(d, "config", "user.name", "t")
    src = os.path.join(d, "m.py")
    open(src, "w", encoding="utf-8").write("def f(x):\n    if x:\n        return 1\n    return 2\n")
    git(d, "add", "-A"); git(d, "commit", "-qm", "base")
    root = git(d, "rev-parse", "HEAD").stdout.strip()

    # A behaviour change: the return moves OUT of the conditional. Indentation
    # only -- which is exactly what git patch-id cannot see.
    open(src, "w", encoding="utf-8").write("def f(x):\n    if x:\n        return 1\n    return 3\n")
    git(d, "add", "-A"); git(d, "commit", "-qm", "a")
    a = git(d, "rev-parse", "HEAD").stdout.strip()
    git(d, "reset", "-q", "--hard", root)
    open(src, "w", encoding="utf-8").write("def f(x):\n    if x:\n        return 1\n        return 3\n")
    git(d, "add", "-A"); git(d, "commit", "-qm", "b")
    b = git(d, "rev-parse", "HEAD").stdout.strip()

    fa, na = pv.compute_patch_fingerprint(root, a, repo_dir=d)
    fb, nb = pv.compute_patch_fingerprint(root, b, repo_dir=d)
    check("real git: a fingerprint is produced", bool(fa) and bool(fb), f"{fa} {fb} {na} {nb}")
    check("real git: two edits differing only in indentation do NOT share a fingerprint",
          fa != fb, f"{fa} {fb}")
    pid_a = subprocess.run(["git", "-C", d, "patch-id", "--stable"],
                           input=subprocess.run(["git", "-C", d, "diff", root + "..." + a],
                                                capture_output=True).stdout,
                           capture_output=True).stdout.split()[0]
    pid_b = subprocess.run(["git", "-C", d, "patch-id", "--stable"],
                           input=subprocess.run(["git", "-C", d, "diff", root + "..." + b],
                                                capture_output=True).stdout,
                           capture_output=True).stdout.split()[0]
    check("real git: git patch-id --stable DOES share one, which is why it is not used",
          pid_a == pid_b, f"{pid_a} {pid_b}")


# --- --stamp's stdout is pasted, so it carries the stamp and nothing else --

import contextlib  # noqa: E402  (used only by the CLI checks below)
import io  # noqa: E402

_out, _err = io.StringIO(), io.StringIO()
with contextlib.redirect_stdout(_out), contextlib.redirect_stderr(_err):
    rc = pv.main(["1", "--repo", "o/r", "--stamp", "--no-freshness-fetch"],
                 runner=fake(PRJSON, []), patch_id_of=lambda *a, **k: (PATCH, ""))
check("--stamp exits 0", rc == 0, f"{rc} {_err.getvalue()!r}")
check("--stamp prints exactly the stamp on stdout",
      _out.getvalue().strip() == pv.stamp(HEAD, PATCH, "full"), repr(_out.getvalue()))
check("--stamp's freshness notes go to stderr, not stdout",
      "note:" not in _out.getvalue(), repr(_out.getvalue()))
check("...and the notes are not simply lost", "note:" in _err.getvalue(),
      repr(_err.getvalue()))

_out, _err = io.StringIO(), io.StringIO()
with contextlib.redirect_stdout(_out), contextlib.redirect_stderr(_err):
    rc = pv.main(["1", "--repo", "o/r", "--stamp", "--kind", "arm-check",
                  "--no-freshness-fetch"],
                 runner=fake(PRJSON, []), patch_id_of=lambda *a, **k: (PATCH, ""))
check("--kind arm-check is carried into the stamp",
      _out.getvalue().strip().endswith("kind: arm-check"), repr(_out.getvalue()))


# (F) a session with no gh can still stamp: --head skips the PR read, --patch
# skips the fingerprint too. The runner below fails the test if gh is reached.
def no_gh(argv, **kw):
    raise AssertionError("gh must not be called on the --head/--patch path: " + " ".join(argv))


_out, _err = io.StringIO(), io.StringIO()
with contextlib.redirect_stdout(_out), contextlib.redirect_stderr(_err):
    rc = pv.main(["1", "--repo", "o/r", "--stamp", "--head", HEAD, "--patch", PATCH,
                  "--no-freshness-fetch"], runner=no_gh)
check("--stamp --head --patch needs no gh at all", rc == 0, f"{rc} {_err.getvalue()!r}")
check("...and prints the same stamp",
      _out.getvalue().strip() == pv.stamp(HEAD, PATCH, "full"), repr(_out.getvalue()))
check("...and its parse round-trips",
      pv.parse_verdict("Verdict: MERGE " + _out.getvalue().strip())[0] is not None,
      repr(_out.getvalue()))

for bad_argv, why in ((["1", "--head", HEAD], "--head without --stamp"),
                      (["1", "--stamp", "--patch", "nothex"], "a non-hex --patch"),
                      (["1", "--stamp", "--head", HEAD[:8]], "an abbreviated --head")):
    try:
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            pv.main(bad_argv, runner=no_gh)
        ok = False
    except SystemExit as exc:
        ok = exc.code != 0
    check(f"{why} is refused", ok)


# --- prose and parser cannot drift ---------------------------------------

reviewer = open(os.path.join(ROOT, ".claude", "agents", "reviewer.md"), encoding="utf-8").read()
check("reviewer.md carries the grammar verbatim", pv.GRAMMAR in reviewer, "GRAMMAR=" + pv.GRAMMAR)
check("the grammar instantiated is what VERDICT_RE accepts",
      pv.parse_verdict(instantiate(pv.GRAMMAR))[0] is not None
      and pv.parse_verdict(instantiate(pv.GRAMMAR, "HOLD", "a reason",
                                       kind="arm-check"))[0] is not None,
      instantiate(pv.GRAMMAR))
check("reviewer.md tells the reviewer how to produce the line",
      "tools/pr-verdict.py --stamp" in reviewer)
check("reviewer.md says the verdict is the last line",
      "last line" in reviewer.lower())

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
