#!/usr/bin/env python3
"""Suites that build throwaway git repositories must not inherit the user's git config (#4001).

On a box with `commit.gpgsign=true` every scratch `git commit` went through the
user's signer: three tools/ suites died at their first commit, and
.github/scripts/test_pr_changed_files.sh blocked while the signer waited for an
approval prompt. CI runners have no signing config, so CI never saw it.

This guard gives CI that config: it runs every scratch-repo suite under a global
config that forces signing through a program that always fails. A suite that
isolates itself (tools/scratch_git_config.py, or the `export` in the shell suite)
drops that file and passes; one that does not fails here the same way it fails
on a signing box.

The census half keeps the list honest: every test suite that runs `git commit`
must be in SUITES, so a new scratch-repo suite cannot skip the guard by omission.

Run: python3 tools/test_scratch_repo_git_isolation.py
"""
from __future__ import annotations

import glob
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

# path (relative to ROOT) -> the line the suite prints only when it fully passed
SUITES = {
    "tools/test_agent_self_freshness.py": "all checks passed",
    "tools/test_corpus_checkout.py": "all corpus-checkout tests passed",
    "tools/test_preflight.py": "all checks passed",
    "tools/test_stale_origin_main_diff_recipe.py": "recipe-execution checks passed",
    ".github/scripts/test_pr_changed_files.sh": "failed: 0",
}

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


# A `commit` verb handed to git: an argv element ("commit" / 'commit') in Python,
# or `git [-C dir] commit` in shell. Comment lines are skipped.
_ARGV_COMMIT = re.compile(r"""["']commit["']""")
_SHELL_COMMIT = re.compile(r"""\bgit\b(\s+-C\s+\S+)*\s+commit\b""")


def commits_in_scratch_repo(path: str) -> bool:
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            s = line.strip()
            if s.startswith("#"):
                continue
            if _ARGV_COMMIT.search(line) and ("git" in line or "_g(" in line):
                return True
            if _SHELL_COMMIT.search(line):
                return True
    return False


print("census: every suite that commits in a scratch repository is guarded")
candidates = sorted(glob.glob(os.path.join(ROOT, "tools", "test_*.py"))
                    + glob.glob(os.path.join(ROOT, ".github", "scripts", "test_*")))
check("the census glob matched suites at all", len(candidates) > 10, str(len(candidates)))
found = {os.path.relpath(p, ROOT).replace(os.sep, "/") for p in candidates
         if os.path.abspath(p) != os.path.abspath(__file__) and commits_in_scratch_repo(p)}
check("every committing suite is in SUITES", found <= set(SUITES),
      f"unguarded: {sorted(found - set(SUITES))}")
check("every entry in SUITES still commits (no stale entry)", set(SUITES) <= found,
      f"stale: {sorted(set(SUITES) - found)}")


with tempfile.TemporaryDirectory(prefix="scratch-git-isolation-") as tmp:
    hostile = os.path.join(tmp, "hostile-gitconfig")
    failing_signer = "false"
    with open(hostile, "w", encoding="utf-8") as fh:
        fh.write("[commit]\n\tgpgsign = true\n[tag]\n\tgpgsign = true\n"
                 f"[gpg]\n\tformat = openpgp\n\tprogram = {failing_signer}\n"
                 "[user]\n\tname = hostile\n\temail = hostile@example.invalid\n")
    env = dict(os.environ, GIT_CONFIG_GLOBAL=hostile)
    env.pop("GIT_CONFIG_NOSYSTEM", None)

    print("control: the hostile config really breaks an unisolated scratch commit")
    probe = os.path.join(tmp, "probe")
    subprocess.run(["git", "init", "-q", probe], env=env, check=True, capture_output=True)
    ctl = subprocess.run(["git", "-C", probe, "commit", "-q", "--allow-empty", "-m", "x"],
                         env=env, capture_output=True, text=True)
    # Without this the guard could pass because the config did nothing.
    check("an unisolated commit fails under the hostile config", ctl.returncode != 0,
          f"rc={ctl.returncode} -- the hostile config did not bite; every result below is vacuous")

    print("each scratch-repo suite passes under the hostile global config")
    for rel, success_line in SUITES.items():
        path = os.path.join(ROOT, rel)
        argv = ["bash", path] if rel.endswith(".sh") else [sys.executable, path]
        try:
            p = subprocess.run(argv, cwd=ROOT, env=env, capture_output=True, text=True,
                               encoding="utf-8", errors="replace", timeout=300,
                               stdin=subprocess.DEVNULL)
            tail = "\n".join((p.stdout + p.stderr).strip().splitlines()[-6:])
            check(f"{rel} passes with a signing global config",
                  p.returncode == 0 and success_line in p.stdout,
                  f"rc={p.returncode}\n{tail}")
        except subprocess.TimeoutExpired:
            check(f"{rel} passes with a signing global config", False,
                  "timed out after 300s (a signer waiting for input looks like this)")

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
