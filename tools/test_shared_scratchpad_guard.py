#!/usr/bin/env python3
"""Unit tests for shared-scratchpad-guard.py.

The hook is advisory for the coordinator and blocking (exit 2) in an impl-agent or
reviewer context, so "did it fire" and the exit code are the observables. The
cases below are drawn from real commands in this repository's session
scratchpads -- the ones that caused #2980 must fire, and the ordinary reads that
happen on every task must not, because a hook that cries on everything gets
ignored and then it is not a mechanism at all.

Usage: python3 tools/test_shared_scratchpad_guard.py
"""
import json
import pathlib
import subprocess
import sys

HOOK = pathlib.Path(__file__).resolve().parent.parent / ".claude" / "hooks" / "shared-scratchpad-guard.py"
SP = "/tmp/claude-1000/-home-stefan-Documents-Repos-Comunity-BusinessCentral-AL-Runner/2a9b731a/scratchpad"

# The loop's box keeps the scratchpad under ~/.cache, not /tmp; a /tmp-anchored
# pattern never fired there (#4534).
SP_HOME = ("/home/stefan/.cache/claude-tmp/claude-1000/"
           "-home-stefan-Documents-Repos-community-BusinessCentral-AL-Runner/918cc50e/scratchpad")

CASES = []


def case(name, command, should_fire, tool="Bash", agent_type="", should_block=False):
    CASES.append((name, command, should_fire, tool, agent_type, should_block))


# --- the incidents on #2980: these MUST fire ---
case("staging a PR body with a heredoc at a shared path",
     f"cat > {SP}/pr-body.md <<'EOF'\nCloses #2980\nEOF", True)
case("gh pr create reading a shared body file",
     f"gh pr create --title x --body-file {SP}/body.md", True)
case("cloning the corpus onto a shared path",
     f"git clone https://github.com/x/y {SP}/corpus", True)
case("git -C against a shared corpus clone",
     f"git -C {SP}/corpus checkout -b mybranch", True)
case("cp -r a probe bundle onto a shared path",
     f"cp -r bundle {SP}/probe", True)
case("redirecting a run log to a shared path",
     f"dotnet run --project AlRunner -- run x | tee {SP}/run.log", True)
case("rm on a shared path", f"rm -rf {SP}/corpus", True)

# --- a scratchpad OUTSIDE /tmp (#4534): must fire the same way ---
case("heredoc into a shared scratchpad under ~/.cache",
     f"cat > {SP_HOME}/review.md <<'EOF'\nverdict\nEOF", True)
case("gh pr comment reading a shared body under ~/.cache",
     f"gh pr comment 4532 --body-file {SP_HOME}/review.md", True)
case("an agent-owned path under ~/.cache stays silent",
     f"cat > {SP_HOME}/agent-reviewer-3/review.md <<'EOF'\nx\nEOF", False)
case("reading a shared log under ~/.cache stays silent",
     f"sed -n '1,20p' {SP_HOME}/ci.txt", False)

# --- in an impl-agent/reviewer context the write is REFUSED, not merely warned (#4534) ---
case("a reviewer writing review.md to the shared dir is blocked",
     f"cat > {SP_HOME}/review.md <<'EOF'\nverdict\nEOF", True,
     agent_type="reviewer", should_block=True)
case("an impl-agent rm -rf on a shared name is blocked",
     f"rm -rf {SP}/a.txt", True, agent_type="impl-agent", should_block=True)
case("the per-call escape downgrades the block to the warning",
     f"rm -rf {SP}/a.txt # hook:allow-shared-scratch", True, agent_type="reviewer")
case("a reviewer writing to its own agent directory is not blocked",
     f"cat > {SP_HOME}/agent-reviewer-3/review.md <<'EOF'\nx\nEOF", False,
     agent_type="reviewer")
case("a reviewer READING a shared log is not blocked",
     f"command grep -n FAIL {SP_HOME}/ci.txt", False, agent_type="reviewer")
case("the coordinator (no agent_type) is warned, never blocked",
     f"cat > {SP_HOME}/review.md <<'EOF'\nx\nEOF", True)

# --- already private: these must NOT fire, or the tool teaches nothing ---
case("heredoc into an agent-owned path",
     f"cat > {SP}/agent-stma-auto-23/pr-body.md <<'EOF'\nx\nEOF", False)
case("gh pr create with an agent-owned body file",
     f"gh pr create --body-file {SP}/agent-stma-auto-23/body.md", False)
case("cloning into an agent-owned directory",
     f"git clone https://github.com/x/y {SP}/agent-stma-auto-23/corpus", False)
case("a nested path under an agent directory",
     f"cp -r bundle {SP}/agent-impl-4/probe/run1", False)

# --- ordinary reads: must NOT fire, so the warning stays worth reading ---
case("listing the scratchpad", f"ls -la {SP}", False)
case("reading a log with sed", f"sed -n '1,50p' {SP}/leg-284.log", False)
case("grepping a shared log to diagnose", f"command grep -n FAIL {SP}/base-cold.log", False)
case("a command with no scratchpad path at all",
     "cat > /tmp/other/pr-body.md <<'EOF'\nx\nEOF", False)
case("a non-Bash tool is ignored",
     f"cat > {SP}/pr-body.md <<'EOF'\nx\nEOF", False, tool="Write")


def run():
    failures = 0
    for name, command, should_fire, tool, agent_type, should_block in CASES:
        body = {"tool_name": tool, "tool_input": {"command": command}}
        if agent_type:
            body["agent_type"] = agent_type
        r = subprocess.run([sys.executable, str(HOOK)], input=json.dumps(body),
                           capture_output=True, text=True)
        fired = "Shared-scratchpad warning" in r.stderr
        want_rc = 2 if should_block else 0
        if r.returncode != want_rc:
            print(f"  FAIL {name}: hook exited {r.returncode}, expected {want_rc}")
            failures += 1
        elif fired != should_fire:
            print(f"  FAIL {name}: fired={fired}, expected {should_fire}")
            failures += 1
        else:
            print(f"  ok   {name}")
    if failures:
        print(f"\n{failures} FAILED")
        return 1
    print(f"\nall {len(CASES)} passed")
    return 0


if __name__ == "__main__":
    sys.exit(run())
