#!/usr/bin/env python3
"""The guards pinning impl-agent.md must stay green when two green PRs merge cleanly (#4248).

THE DEFECT
  A guard pinned a hand-written count of `tools/test_*.py` in `.claude/agents/impl-agent.md`.
  Two PRs each adding a guard each bump that number the same way (55 -> 56). Identical
  hunks are not a conflict, so git merges them cleanly; the merged tree holds 57 files and
  says 56, and `main` goes red along with every PR branched from it. Each PR's own CI was
  green and correct: it measured `refs/pull/N/merge` against the base of its day. Nothing
  short of "require branches to be up to date" catches it after the fact, so the remedy is
  to stop writing a figure that every guard-adding PR must edit (owner's direction, #4539).

WHAT THIS DOES
  Replays that sequence in a scratch repository for every `tools/test_agent_doc_guard_*.py`:
  base green -> branch A adds a guard and is brought green -> branch B adds a different guard
  and is brought green -> A and B merge -> the guard must still be green. "Brought green"
  models an author following the guard's own FAIL line (`doc says N, tree has M`), which is
  exactly what both PRs in #4248 did.

  The scratch repository is a real `git init`, not a `git archive` extraction (#4248's
  first comment: guards that shell out to git fail under an archive and read as findings).
  The populations are reproduced as empty files with the real names; the guard and the doc
  are copied verbatim.

CONTROLS, run every time, so a green here cannot be vacuous
  * a synthetic guard pinning a count the same way MUST come back red on the merge, once per
    population directory the branches grow -- the replay can still see the class in each;
  * a synthetic guard that crashes MUST come back UNMEASURED -- a traceback is not a verdict.

Exit (guards-need-a-third-state.md):
  0  every real guard survives the merge and every control behaves
  1  a real guard is green on each branch and red on their clean merge -- the #4248 class
  3  nothing measured: no guard found, git unavailable, a guard not green on the unmerged
     base, a branch that could not be brought green, or a control that misbehaved

Run: python3 tools/test_agent_doc_claims_survive_a_clean_merge.py
"""
from __future__ import annotations

import glob
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import scratch_git_config  # noqa: E402

DOC = os.path.join(".claude", "agents", "impl-agent.md")
GUARD_GLOB = os.path.join("tools", "test_agent_doc_guard_*.py")
# Directories whose file NAMES the guards may read; reproduced as empty files.
POPULATIONS = ["tools", os.path.join(".github", "scripts")]
# Each branch grows every population, and both kinds the .github/scripts warning splits.
PROBES = [
    os.path.join("tools", "test_zz_merge_probe_{branch}.py"),
    os.path.join(".github", "scripts", "test_zz_merge_probe_{branch}.py"),
    os.path.join(".github", "scripts", "test_zz_merge_probe_{branch}.sh"),
]
DRIFT = re.compile(r"doc says (\d+), tree has (\d+)")

PASS, RED, UNMEASURED = "pass", "red", "unmeasured"


class Unmeasured(Exception):
    pass


def git(repo: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True)


def git_must(repo: str, *args: str) -> None:
    r = git(repo, *args)
    if r.returncode != 0:
        raise Unmeasured(f"git {' '.join(args)} failed: {r.stderr.strip()}")


def run_guard(repo: str, guard_rel: str) -> tuple[int, str]:
    r = subprocess.run([sys.executable, os.path.join(repo, guard_rel)],
                       capture_output=True, text=True, cwd=repo)
    out = r.stdout + r.stderr
    if "Traceback (most recent call last)" in out:
        raise Unmeasured(f"{guard_rel} crashed:\n{out.strip()}")
    return r.returncode, out


def bring_green(repo: str, guard_rel: str, doc_rel: str) -> None:
    """Do what an author does with the guard's FAIL line: write the tree's figure.

    The figure sits in a bold span, alone (`**55**`) or inside a phrase
    (`**8 `.py` and 13 `.sh`**`). Each candidate span is tried and kept only if the guard
    then stops reporting that drift, so an unrelated bold number is never the one rewritten.
    """
    rc, out = run_guard(repo, guard_rel)
    if rc == 0:
        return
    drifts = DRIFT.findall(out) if rc == 1 else []
    if not drifts:
        raise Unmeasured(f"{guard_rel} is not green on a branch and says no fixable drift "
                         f"(rc={rc}):\n{out.strip()}")
    path = os.path.join(repo, doc_rel)
    for stated, actual in drifts:
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        number = re.compile(r"(?<![\d.])" + stated + r"(?![\d.])")
        for span in re.finditer(r"\*\*.+?\*\*", text):
            if not number.search(span.group(0)):
                continue
            fixed = number.sub(actual, span.group(0), count=1)
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(text[:span.start()] + fixed + text[span.end():])
            _, out = run_guard(repo, guard_rel)
            if f"doc says {stated}, tree has {actual}" not in out:
                break
        else:
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(text)
            raise Unmeasured(f"cannot apply '{stated} -> {actual}': no bold figure in "
                             f"{doc_rel} clears that drift")
    rc, out = run_guard(repo, guard_rel)
    if rc != 0:
        raise Unmeasured(f"{guard_rel} still not green after its own remedy (rc={rc}):\n{out}")


def replay(repo: str, guard_rel: str, doc_rel: str) -> str:
    """Base -> two green branches each adding a guard -> merge. PASS / RED / raises."""
    rc, out = run_guard(repo, guard_rel)
    if rc != 0:
        raise Unmeasured(f"{guard_rel} is not green on the unmerged base (rc={rc}):\n{out}")

    for branch in ("a", "b"):
        git_must(repo, "checkout", "-q", "-b", branch, "main")
        for rel in PROBES:
            probe = os.path.join(repo, rel.format(branch=branch))
            os.makedirs(os.path.dirname(probe), exist_ok=True)
            with open(probe, "w", encoding="utf-8") as fh:
                fh.write("")
        bring_green(repo, guard_rel, doc_rel)
        git_must(repo, "add", "-A")
        git_must(repo, "commit", "-q", "-m", f"branch {branch}")

    git_must(repo, "checkout", "-q", "a")
    merged = git(repo, "merge", "-q", "--no-edit", "b")
    if merged.returncode != 0:
        # A textual conflict is caught before merge; that is not the silent class.
        return PASS
    rc, out = run_guard(repo, guard_rel)
    if rc == 0:
        return PASS
    if rc == 1:
        return RED
    raise Unmeasured(f"{guard_rel} on the merge answered rc={rc}:\n{out}")


def scratch_repo(files: dict[str, str | None]) -> str:
    """files: repo-relative path -> content, or None for 'copy from ROOT'."""
    repo = tempfile.mkdtemp(prefix="doc-claims-merge-")
    for rel, content in files.items():
        dst = os.path.join(repo, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if content is None:
            shutil.copyfile(os.path.join(ROOT, rel), dst)
        else:
            with open(dst, "w", encoding="utf-8") as fh:
                fh.write(content)
    git_must(repo, "init", "-q", "-b", "main")
    git_must(repo, "config", "user.name", "merge-replay")
    git_must(repo, "config", "user.email", "merge-replay@example.invalid")
    git_must(repo, "add", "-A")
    git_must(repo, "commit", "-q", "-m", "base")
    return repo


def population_placeholders() -> dict[str, str | None]:
    files: dict[str, str | None] = {}
    for d in POPULATIONS:
        full = os.path.join(ROOT, d)
        if not os.path.isdir(full):
            raise Unmeasured(f"population directory missing: {d}")
        for n in os.listdir(full):
            if os.path.isfile(os.path.join(full, n)):
                files[os.path.join(d, n)] = ""
    return files


# A count pin on one population directory: {rx} captures the doc's figure, {d} is counted.
PINNING_CONTROL_GUARD = r'''
import os, re, sys
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
text = open(os.path.join(root, "doc.md"), encoding="utf-8").read()
stated = int(re.search(r"{rx}", text).group(1))
actual = sum(1 for n in os.listdir(os.path.join(root, *{d!r}.split("/")))
             if n.startswith("test_"))
if stated != actual:
    print(f"FAIL: doc says {{stated}}, tree has {{actual}}", file=sys.stderr)
    sys.exit(1)
'''

# One pinning control per population the probes grow; each must go red on the merge.
PINNING_CONTROLS = [
    ("tools", r"runs \*\*(\d+)\*\* guards"),
    (".github/scripts", r"holds a further \*\*(\d+)\*\*"),
]


def controls() -> list[str]:
    problems = []
    base = {"tools/test_one.py": "", "tools/test_two.py": "",
            ".github/scripts/test_three.sh": "",
            "doc.md": "The loop runs **2** guards; scripts holds a further **1**.\n"}
    for d, rx in PINNING_CONTROLS:
        pinned = dict(base)
        pinned["tools/control_guard.py"] = PINNING_CONTROL_GUARD.format(rx=rx, d=d)
        repo = scratch_repo(pinned)
        try:
            verdict = replay(repo, "tools/control_guard.py", "doc.md")
            if verdict != RED:
                problems.append(f"{d} pinning control answered {verdict}, expected red -- the "
                                "replay can no longer see the #4248 class there, so a green "
                                "here means nothing")
        except Unmeasured as e:
            problems.append(f"{d} pinning control was unmeasured: {e}")
        finally:
            shutil.rmtree(repo, ignore_errors=True)

    crash = dict(base)
    crash["tools/control_guard.py"] = "raise RuntimeError('control crash')\n"
    repo = scratch_repo(crash)
    try:
        verdict = replay(repo, "tools/control_guard.py", "doc.md")
        problems.append(f"crashing control answered {verdict}, expected unmeasured")
    except Unmeasured:
        pass
    finally:
        shutil.rmtree(repo, ignore_errors=True)
    return problems


def main() -> int:
    scratch_git_config.isolate()
    if shutil.which("git") is None:
        print("UNMEASURED: git is not on PATH", file=sys.stderr)
        return 3

    guards = sorted(os.path.relpath(p, ROOT) for p in glob.glob(os.path.join(ROOT, GUARD_GLOB)))
    if not guards:
        print(f"UNMEASURED: no guard matches {GUARD_GLOB}", file=sys.stderr)
        return 3

    problems = controls()
    if problems:
        for p in problems:
            print(f"UNMEASURED: {p}", file=sys.stderr)
        return 3

    red, unmeasured = [], []
    for guard in guards:
        files = population_placeholders()
        files[DOC] = None
        files[guard] = None
        repo = scratch_repo(files)
        try:
            verdict = replay(repo, guard, DOC)
            print(f"  {verdict:<5} {guard}")
            if verdict == RED:
                red.append(guard)
        except Unmeasured as e:
            unmeasured.append(f"{guard}: {e}")
        finally:
            shutil.rmtree(repo, ignore_errors=True)

    if unmeasured:
        for u in unmeasured:
            print(f"UNMEASURED: {u}", file=sys.stderr)
        return 3
    if red:
        for g in red:
            print(f"FAIL: {g} is green on each of two branches and red on their clean merge. "
                  f"It pins a figure every guard-adding PR must edit identically, so two such "
                  f"PRs red `main` together (#4248). Pin a property that does not move with "
                  f"the population instead of a count (#4539).", file=sys.stderr)
        return 1
    print(f"PASS: {len(guards)} impl-agent.md guard(s) survive a clean merge of two green PRs; "
          f"every control behaved")
    return 0


if __name__ == "__main__":
    sys.exit(main())
