#!/usr/bin/env python3
"""Unit tests for the two BLOCKING PreToolUse hooks (#3707).

Both hooks live in `.claude/hooks/`, which `pr-gate.yml`'s tools-tests job does
not glob -- it runs `tools/test_*.py` only. So the tests for them live here,
where that job discovers them directly, and drive the real scripts as
subprocesses with a JSON PreToolUse payload on stdin, the only interface the
harness uses.

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

# #4288: the flag is not what decides whether a wait runs in the background. The
# harness moves a FOREGROUND command to the background at a hard 600s cap -- measured
# over every transcript on this box: 66 CI waits were auto-backgrounded and all 66 had
# run_in_background unset, so a refusal gated on that flag refused none of them. The
# declared `timeout` does not raise the cap: 600000, 900000, 1600000, 2400000, 3000000
# and 3600000 ms all produced `within its 600s timeout`.
HARNESS_BACKGROUND_CAP_S = 600

print("\nCI waits the HARNESS will background -- refused whatever the flag says (#4288)")
CI_WAIT_OVER_CAP = [
    ("ci-wait.py --timeout 1500, the shape measured on PR #4286",
     "tools/ci-wait.py 4286 --timeout 1500 > ci.txt 2>&1; echo \"ci-wait exit=$?\""),
    ("ci-wait.py --timeout 2400, the commonest shape in the transcripts",
     "python3 tools/ci-wait.py 3110 --timeout 2400 2>&1 | tail -40"),
    ("ci-wait.py with no --timeout at all defaults above the cap",
     "tools/ci-wait.py 3707"),
    ("--timeout=1500 in the equals spelling",
     "tools/ci-wait.py 4286 --timeout=1500"),
    ("a `timeout` wrapper does not make it a read",
     "timeout 2700 tools/ci-wait.py 3023 --timeout 3000"),
    ("gh run watch blocks with no deadline of its own",
     "gh run watch 33966349085 --repo o/r --exit-status --interval 30"),
    ("gh pr checks --watch likewise", "gh pr checks 3707 --watch"),
]
for name, cmd in CI_WAIT_OVER_CAP:
    ok, d = blocks(REFUSE, cmd, "--timeout 0")
    check(name, ok, d)

r = fire(REFUSE, "tools/ci-wait.py 4286 --timeout 1500")
check("the over-cap refusal says the harness backgrounds it, not the agent",
      "harness" in r.stderr.lower(), r.stderr[:240])
check("the over-cap refusal names the 600s cap",
      str(HARNESS_BACKGROUND_CAP_S) in r.stderr, r.stderr[:240])
check("the over-cap refusal warns the notification's exit code is the wrapper's",
      "wrapper" in r.stderr.lower(), r.stderr[:240])

# GREEN CONTROL. A hook that refuses every CI wait would pass every arm above and
# would also break the one form the rules mandate. These must stay allowed, and they
# are what distinguishes a correct hook from an over-refusing one.
print("\nCI reads UNDER the cap -- still allowed, in the foreground (the green control)")
CI_WAIT_UNDER_CAP = [
    ("--timeout 0 is one pass, the mandated form", "tools/ci-wait.py 3707 --timeout 0"),
    ("--timeout 0 with a redirect and an exit echo",
     "tools/ci-wait.py 4286 --timeout 0 > ci.txt 2>&1; echo \"ci-wait exit=$?\"; cat ci.txt"),
    ("--timeout=0 in the equals spelling", "tools/ci-wait.py 3707 --timeout=0"),
    ("--timeout 120, comfortably under the cap", "tools/ci-wait.py 3707 --timeout 120"),
    ("--timeout 599, the last value under the cap", "tools/ci-wait.py 3707 --timeout 599"),
    ("a single gh run view read", "gh run view 123 --json conclusion"),
    ("gh pr checks without --watch", "gh pr checks 3707"),
    ("a poll loop of --timeout 0 reads is the documented shape",
     "for i in 1 2 3; do tools/ci-wait.py 4286 --timeout 0 && break; command sleep 30; done"),
]
for name, cmd in CI_WAIT_UNDER_CAP:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)

# The boundary itself: 600 is at the cap and must be refused, 599 allowed above.
ok, d = blocks(REFUSE, "tools/ci-wait.py 3707 --timeout 600", "--timeout 0")
check("--timeout 600 is AT the cap and refused", ok, d)

# Non-CI background work is untouched by this widening.
print("\nnon-CI work is unaffected by the cap rule")
for name, cmd in [
    ("a long foreground corpus run", "dotnet run --project AlRunner -- run --bundle x.json"),
    ("a long foreground test sweep", "dotnet test AlRunner.Tests"),
    ("a detached runner run", "al-runner run --bundle app.json"),
]:
    ok, d = allows(REFUSE, cmd)
    check(name, ok, d)
    ok, d = allows(REFUSE, cmd, background=True)
    check(name + " (backgrounded)", ok, d)

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
    # tdd.md asks for the `Total:` line from every mutation run, and the natural
    # way to get it is a grep over `dotnet test` stdout. That greps no file (#3994).
    ("test output filtered with grep",
     "dotnet test AlRunner.Tests --filter X 2>&1 | command grep -E 'Failed:'"),
    ("test output filtered by project path",
     "dotnet test AlRunner.Tests/AlRunner.Tests.csproj --no-build | command grep Total"),
    ("dotnet run output filtered", "dotnet run --project AlRunner | command grep PASS"),
    ("writing a C# file with a heredoc", "cat > AlRunner/New.cs <<EOF\nclass X {}\nEOF"),
    ("git log over the C# tree", "git log --oneline -5 -- AlRunner"),
]
# The dotnet exemption is PIPE-connected only: `dotnet test` appearing anywhere in
# the string would make the literal text an untraceable opt-out (#3994 review).
NAV_BLOCKED_DESPITE_DOTNET = [
    ("dotnet test in a trailing comment",
     "command grep -rn 'Editable' AlRunner/ # after dotnet test"),
    ("a read chained after a build",
     "dotnet build AlRunner; sed -n '1,50p' AlRunner/Program.cs"),
    ("a source grep chained with &&",
     "dotnet test AlRunner.Tests && command grep -n 'Foo' AlRunner/Patches/Bar.cs"),
]
for name, cmd in NAV_ALLOWED:
    ok, d = allows(NAV, cmd, cwd=WORKTREE)
    check(name, ok, d)

for name, cmd in NAV_BLOCKED_DESPITE_DOTNET:
    ok, d = blocks(NAV, cmd, "context-pack.py", cwd=WORKTREE)
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
