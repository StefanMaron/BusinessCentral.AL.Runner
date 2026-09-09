#!/usr/bin/env python3
"""Unit tests for the two BLOCKING PreToolUse hooks (#3707).

Both hooks live in `.claude/hooks/`, and no CI job globs that directory --
`pr-gate.yml`'s tools-tests job runs `tools/test_*.py` only. So the tests for
them live here, and drive the real scripts as subprocesses with a JSON
PreToolUse payload on stdin, which is the only interface the harness uses.

What "blocking" means, and why the assertions are on exit code 2 specifically:
a PreToolUse hook that exits 2 has its stderr fed back to the model and the tool
call is refused; exit 1 is a non-blocking error the model never sees. Asserting
"non-zero" would pass on a hook that does not actually block.

Run: python3 tools/test_agent_workflow_hooks.py
"""
from __future__ import annotations

import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
HOOKS = os.path.join(ROOT, ".claude", "hooks")
REFUSE = os.path.join(HOOKS, "refuse-stash-and-ci-waits.py")
NAV = os.path.join(HOOKS, "prefer-code-navigation.py")

# Synthetic, never this checkout: the suite itself runs from an agent worktree
# during development and from the main checkout in CI, so deriving either cwd
# from __file__ would make the coordinator cases pass or fail by location.
MAIN_CHECKOUT = "/home/runner/work/BusinessCentral.AL.Runner/BusinessCentral.AL.Runner"
WORKTREE = MAIN_CHECKOUT + "/.claude/worktrees/fbk-9-issue-1234"

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def fire(hook: str, command: str, *, background: bool = False, cwd: str = "",
         tool: str = "Bash", env_extra: dict | None = None,
         agent_type: str = "") -> subprocess.CompletedProcess:
    tool_input: dict = {"command": command}
    if background:
        tool_input["run_in_background"] = True
    payload = {"tool_name": tool, "tool_input": tool_input}
    if cwd:
        payload["cwd"] = cwd
    if agent_type:
        payload["agent_type"] = agent_type
    env = dict(os.environ)
    # The invoking session's own identity must never leak into a case asserting
    # what happens WITHOUT one.
    for k in ("AL_RUNNER_AGENT_ID", "CLAUDE_AGENT_ID", "AL_RUNNER_HOOK_CONTEXT"):
        env.pop(k, None)
    env.update(env_extra or {})
    return subprocess.run([sys.executable, hook], input=json.dumps(payload),
                          capture_output=True, text=True, env=env)


def blocks(hook: str, command: str, must_say: str, **kw):
    r = fire(hook, command, **kw)
    ok = r.returncode == 2 and must_say in r.stderr
    return ok, f"exit={r.returncode} stderr={r.stderr.strip()[:160]!r}"


def allows(hook: str, command: str, **kw):
    r = fire(hook, command, **kw)
    return r.returncode == 0, f"exit={r.returncode} stderr={r.stderr.strip()[:160]!r}"


print("git stash -- blocked in every form, in every context")
STASH_BLOCKED = [
    ("bare git stash", "git stash"),
    ("git stash push with a pathspec", "git stash push -- AlRunner/Program.cs"),
    ("git stash pop", "git stash pop"),
    ("git stash apply", "git stash apply stash@{0}"),
    ("git stash drop", "git stash drop"),
    ("git stash list (a stack shared with every other loop)", "git stash list"),
    ("git -C <worktree> stash", "git -C .claude/worktrees/fbk-1-issue-1 stash"),
    ("stash after &&", "git add -A && git stash"),
    ("stash in a later segment", "echo hi; git stash save wip"),
]
for name, cmd in STASH_BLOCKED:
    ok, d = blocks(REFUSE, cmd, "refs/stash")
    check(name, ok, d)

r = fire(REFUSE, "git stash")
check("the stash refusal names the patch alternative", "git diff HEAD >" in r.stderr,
      r.stderr[:200])
check("the stash refusal cites the rule",
      "no-git-stash-with-worktrees" in r.stderr, r.stderr[:200])

print("\ngit stash -- shapes that must NOT be blocked")
STASH_ALLOWED = [
    ("grepping the docs for the phrase", "command grep -rn 'git stash' .claude/rules"),
    ("rg for the phrase", "rg --hidden 'git stash' ."),
    ("echoing a sentence about it", "echo 'never run git stash here'"),
    ("an ordinary git command", "git status --short"),
    ("a branch whose name contains stash", "git checkout -b agent/fbk-1/stash-docs"),
]
for name, cmd in STASH_ALLOWED:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)

print("\nbackgrounded CI waits -- blocked")
CI_WAIT_BLOCKED = [
    ("gh run watch", "gh run watch 12345"),
    ("gh pr checks --watch", "gh pr checks 3707 --watch"),
    ("ci-wait.py with a positive timeout", "tools/ci-wait.py 3707 --timeout 900"),
    ("ci-wait.py with no timeout at all", "python3 tools/ci-wait.py 3707"),
    ("a sleep loop polling gh run view",
     "while true; do gh run view 123 --json status; sleep 30; done"),
    ("a sleep loop polling gh pr checks",
     "for i in 1 2 3; do gh pr checks 3707; sleep 60; done"),
]
for name, cmd in CI_WAIT_BLOCKED:
    ok, d = blocks(REFUSE, cmd, "--timeout 0", background=True)
    check(name, ok, d)

r = fire(REFUSE, "gh run watch 1", background=True)
check("the CI-wait refusal explains that a backgrounded child dies with the turn",
      "dies with the turn" in r.stderr, r.stderr[:200])
check("the CI-wait refusal cites the rule",
      "no-backgrounding-long-commands" in r.stderr, r.stderr[:200])

print("\nbackgrounded work that is NOT a CI wait -- allowed (detached runner runs)")
BACKGROUND_ALLOWED = [
    ("a detached runner run", "al-runner run --bundle app.json --out results.trx"),
    ("a detached dotnet run", "dotnet run --project AlRunner -c Release -- run --bundle x.json"),
    ("a detached test sweep", "dotnet test AlRunner.Tests --filter FullyQualifiedName~Foo"),
    ("gh run view as a single read", "gh run view 123 --json conclusion"),
    ("ci-wait.py --timeout 0 is one pass, not a wait", "tools/ci-wait.py 3707 --timeout 0"),
]
for name, cmd in BACKGROUND_ALLOWED:
    ok, d = allows(REFUSE, cmd, background=True)
    check(name, ok, d)

print("\nthe same CI-wait shapes in the FOREGROUND -- allowed (only backgrounding is refused)")
for name, cmd in [
    ("foreground gh run watch", "gh run watch 12345"),
    ("foreground ci-wait with a timeout", "tools/ci-wait.py 3707 --timeout 600"),
]:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)

print("\nnon-Bash tools are never the business of either hook")
for hook in (REFUSE, NAV):
    r = fire(hook, "git stash", tool="Read")
    check(f"{os.path.basename(hook)} ignores a non-Bash tool", r.returncode == 0,
          f"exit={r.returncode}")

print("\ncode navigation -- BLOCKS in an agent context")
NAV_BLOCKED = [
    ("grep over the C# tree", "command grep -rn 'GetDataAccessForTableCore' AlRunner"),
    ("rg over the C# tree", "rg -n 'IsProcessingOnly' --glob '*.cs' AlRunner"),
    ("sed a range out of a C# file", "sed -n '600,680p' AlRunner/Patches/NavReportSync.cs"),
    ("cat a C# file", "cat AlRunner/Patches/RecordPatches.cs"),
    ("head a C# file", "head -60 AlRunner/BcRuntime.cs"),
]
for name, cmd in NAV_BLOCKED:
    ok, d = blocks(NAV, cmd, "context-pack.py", cwd=WORKTREE)
    check(name, ok, d)

# The signal the payload itself carries, measured on harness 2.1.266: a dispatched
# subagent's payload has agent_type="impl-agent" and a cwd of the PROJECT ROOT, so
# a cwd test alone would never fire for the agents this hook exists for.
for _t in ("impl-agent", "reviewer"):
    ok, d = blocks(NAV, "cat AlRunner/BcRuntime.cs", "context-pack.py",
                   cwd=MAIN_CHECKOUT, agent_type=_t)
    check("agent_type=" + _t + " blocks even from the project root", ok, d)

r = fire(NAV, "cat AlRunner/BcRuntime.cs", cwd=MAIN_CHECKOUT, agent_type="impl-agent",
         env_extra={"AL_RUNNER_HOOK_CONTEXT": "coordinator"})
check("a coordinator env var inherited by a dispatched agent does NOT disarm the block",
      r.returncode == 2, f"exit={r.returncode}")

r = fire(NAV, "cat AlRunner/Program.cs", cwd=MAIN_CHECKOUT, agent_type="orchestrator")
check("agent_type=orchestrator stays advisory", r.returncode == 0, f"exit={r.returncode}")

ok, d = blocks(NAV, "cat AlRunner/BcRuntime.cs", "context-pack.py",
               env_extra={"AL_RUNNER_AGENT_ID": "fbk-9"})
check("AL_RUNNER_AGENT_ID alone puts the hook in blocking mode", ok, d)

ok, d = blocks(NAV, "cat " + WORKTREE + "/AlRunner/BcRuntime.cs", "context-pack.py")
check("a worktree path in the command puts the hook in blocking mode", ok, d)

ok, d = blocks(NAV, "cat AlRunner/BcRuntime.cs", "context-pack.py",
               cwd=WORKTREE.replace("/", "\\"))
check("a Windows-separator cwd is recognised too", ok, d)

print("\ncode navigation -- ADVISORY outside an agent context, never blocking there")
for name, cmd in [
    ("the coordinator greps the C# tree", "command grep -rn 'Foo' AlRunner"),
    ("the coordinator reads a C# file", "cat AlRunner/Program.cs"),
]:
    r = fire(NAV, cmd, cwd=MAIN_CHECKOUT)
    check(name, r.returncode == 0 and "context-pack.py" in r.stderr,
          f"exit={r.returncode} fired={'context-pack.py' in r.stderr}")

r = fire(NAV, "cat AlRunner/Program.cs", cwd=WORKTREE,
         env_extra={"AL_RUNNER_HOOK_CONTEXT": "coordinator"})
check("AL_RUNNER_HOOK_CONTEXT=coordinator downgrades the block to a reminder",
      r.returncode == 0 and "context-pack.py" in r.stderr,
      f"exit={r.returncode} stderr={r.stderr[:120]!r}")

print("\ncode navigation -- the escape hatch, and what stays allowed in an agent context")
r = fire(NAV, "command grep -rn 'Foo' AlRunner   # hook:allow-grep", cwd=WORKTREE)
check("an explicit # hook:allow-grep marker downgrades the block",
      r.returncode == 0, f"exit={r.returncode}")

NAV_ALLOWED = [
    ("grepping a log", "command grep -n 'error' /tmp/run.log"),
    ("grepping a TRX", "command grep -c 'outcome' results.trx"),
    ("reading a results JSON", "cat scratchpad/results.json"),
    ("searching markdown", "rg --hidden 'clean status' .claude"),
    ("searching AL sources", "command grep -n 'SaveAsXml' tests/al-language/foo.al"),
    ("build output trimmed with tail", "dotnet build AlRunner -c Release | tail -5"),
    ("writing a C# file with a heredoc", "cat > AlRunner/New.cs <<EOF\nclass X {}\nEOF"),
    ("git log over the C# tree", "git log --oneline -5 -- AlRunner"),
]
for name, cmd in NAV_ALLOWED:
    ok, d = allows(NAV, cmd, cwd=WORKTREE)
    check(name, ok, d)

print("\nboth hooks are actually registered -- a hook nothing invokes blocks nothing")
settings = json.load(open(os.path.join(ROOT, ".claude", "settings.json"), encoding="utf-8"))
registered = [h.get("command", "")
              for entry in settings.get("hooks", {}).get("PreToolUse", [])
              if entry.get("matcher") == "Bash"
              for h in entry.get("hooks", [])]
for script in ("refuse-stash-and-ci-waits.py", "prefer-code-navigation.py"):
    check("settings.json registers " + script,
          any(script in c for c in registered), str(registered))

print()
if FAILURES:
    print(f"{len(FAILURES)} failing check(s)")
    sys.exit(1)
print("all checks passed")
