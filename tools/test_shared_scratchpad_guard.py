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
# --- reads that merely CARRY a redirection or -F must not be refused (PR #4588 review):
# the block keys on the WRITE TARGET, never on the verb appearing somewhere in the command ---
case("a reviewer tailing a shared log with 2>&1 is not blocked",
     f"tail -5 {SP_HOME}/ci.txt 2>&1", False, agent_type="reviewer")
case("a reviewer grep -F over a shared log is not blocked",
     f"grep -F foo {SP_HOME}/other.log", False, agent_type="reviewer")
case("an impl-agent passing a shared file to a tool with 2>/dev/null is not blocked",
     f"tools/mutation-verdict.py {SP_HOME}/r1.txt 2>/dev/null", False, agent_type="impl-agent")
# (the advisory still names it -- `cp` is a whole-command verb -- but it does not block)
case("a reviewer copying FROM a shared file into its own directory is not blocked",
     f"cp {SP_HOME}/ci.txt {SP_HOME}/agent-reviewer-3/ci.txt", True, agent_type="reviewer")
case("a reviewer grepping a shared log into ITS OWN file is warned, not blocked",
     f"command grep FAIL {SP_HOME}/ci.txt > /x/agent-r3/fails.txt", True, agent_type="reviewer")
case("an impl-agent reading a shared log beside a heredoc of its own is not blocked",
     f"cat > /x/agent-i/a.md <<'EOF'\nx\nEOF\nhead {SP_HOME}/ci.txt", True, agent_type="impl-agent")
# --- ...while every write-target shape still is ---
case("a reviewer appending to a shared file with >> is blocked",
     f"echo x >> {SP_HOME}/review.md", True, agent_type="reviewer", should_block=True)
case("a reviewer tee-ing into a shared file is blocked",
     f"dotnet test | tee {SP_HOME}/run.log", True, agent_type="reviewer", should_block=True)
case("an impl-agent cp INTO a shared path is blocked",
     f"cp mine.md {SP_HOME}/pr-body.md", True, agent_type="impl-agent", should_block=True)
case("an impl-agent mv INTO a shared path is blocked",
     f"mv mine.md {SP_HOME}/pr-body.md", True, agent_type="impl-agent", should_block=True)
case("a reviewer posting a shared --body-file is blocked",
     f"gh pr comment 4532 --body-file {SP_HOME}/review.md", True,
     agent_type="reviewer", should_block=True)
case("a reviewer posting a shared --body-file=PATH is blocked",
     f"gh pr comment 4532 --body-file={SP_HOME}/review.md", True,
     agent_type="reviewer", should_block=True)
case("an impl-agent cloning onto a shared path is blocked",
     f"git clone https://github.com/x/y {SP_HOME}/corpus", True,
     agent_type="impl-agent", should_block=True)
case("a redirect to a shared file after && is blocked",
     f"cd /x && python3 t.py > {SP_HOME}/out.txt 2>&1", True,
     agent_type="reviewer", should_block=True)

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
case("an unparseable payload fails open but says so", None, False)
case("a non-Bash tool is ignored",
     f"cat > {SP}/pr-body.md <<'EOF'\nx\nEOF", False, tool="Write")


def run():
    failures = 0
    for name, command, should_fire, tool, agent_type, should_block in CASES:
        body = {"tool_name": tool, "tool_input": {"command": command}}
        if agent_type:
            body["agent_type"] = agent_type
        r = subprocess.run([sys.executable, str(HOOK)],
                           input="{not json" if command is None else json.dumps(body),
                           capture_output=True, text=True)
        if command is None and "could not read the hook payload" not in r.stderr:
            print(f"  FAIL {name}: no stderr note, got {r.stderr!r}")
            failures += 1
            continue
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
