#!/usr/bin/env python3
"""Unit tests for shared-scratchpad-guard.py.

The hook is advisory, so "did it fire" is the whole observable behaviour. The
cases below are drawn from real commands in this repository's session
scratchpads -- the ones that caused #2980 must fire, and the ordinary reads that
happen on every task must not, because a hook that cries on everything gets
ignored and then it is not a mechanism at all.

Usage: python3 .claude/hooks/test_shared_scratchpad_guard.py
"""
import json
import pathlib
import subprocess
import sys

HOOK = pathlib.Path(__file__).resolve().parent / "shared-scratchpad-guard.py"
SP = "/tmp/claude-1000/-home-stefan-Documents-Repos-Comunity-BusinessCentral-AL-Runner/2a9b731a/scratchpad"

CASES = []


def case(name, command, should_fire, tool="Bash"):
    CASES.append((name, command, should_fire, tool))


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
    for name, command, should_fire, tool in CASES:
        payload = json.dumps({"tool_name": tool, "tool_input": {"command": command}})
        r = subprocess.run([sys.executable, str(HOOK)], input=payload,
                           capture_output=True, text=True)
        fired = "Shared-scratchpad warning" in r.stderr
        if r.returncode != 0:
            print(f"  FAIL {name}: hook exited {r.returncode}, must always be 0")
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
