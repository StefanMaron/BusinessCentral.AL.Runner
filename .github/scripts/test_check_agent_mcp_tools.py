#!/usr/bin/env python3
"""Unit tests for check_agent_mcp_tools.py, run against synthetic agent
definitions in a temporary directory.

Written against fixtures rather than only the repo's real agents so the check
stays proven after someone legitimately edits those agents -- the same pattern
as test_check_pr_check_triggers.sh.

Usage: python3 test_check_agent_mcp_tools.py
Exits 0 when every case passes, 1 on the first failure.
"""

import pathlib
import subprocess
import sys
import tempfile

SCRIPT = pathlib.Path(__file__).resolve().parent / "check_agent_mcp_tools.py"

CASES = []


def case(name, expected_exit, files, expect_in_stderr=None):
    CASES.append((name, expected_exit, files, expect_in_stderr))


def agent(tools, body):
    front = "---\nname: fixture\ndescription: a fixture — with a colon: in it\n"
    if tools is not None:
        front += f"tools: {tools}\n"
    return front + "---\n\n" + body


case(
    "concrete tool mentioned in body and allowlisted -> pass",
    0,
    {"a.md": agent("Bash, mcp__srv__do_thing", "Call mcp__srv__do_thing to do the thing.")},
)

case(
    "concrete tool mentioned in body but missing from allowlist -> fail",
    1,
    {"a.md": agent("Bash, Read", "Call mcp__srv__do_thing to do the thing.")},
    expect_in_stderr="mcp__srv__do_thing",
)

case(
    "wildcard mentioned in body with a matching server entry -> pass",
    0,
    {"a.md": agent("Bash, mcp__srv__do_thing", "The mcp__srv__* tools answer fast.")},
)

case(
    "wildcard mentioned in body with no entry for that server -> fail",
    1,
    {"a.md": agent("Bash, mcp__other__thing", "The mcp__srv__* tools answer fast.")},
    expect_in_stderr="no entry for the srv server",
)

case(
    "this is exactly the #2395 shape: github allowlisted, bc-decompiler documented -> fail",
    1,
    {
        "a.md": agent(
            "Bash, Read, mcp__github__issue_read",
            "### Reading BC's own code: use the `bc-decompiler` MCP server\n"
            "Do not grep a decompile dump -- the mcp__bc-decompiler__* tools answer "
            "in well under a second.",
        )
    },
    expect_in_stderr="no entry for the bc-decompiler server",
)

case(
    "no tools: key at all means the agent inherits everything -> pass",
    0,
    {"a.md": agent(None, "Call mcp__srv__do_thing and the mcp__other__* tools freely.")},
)

case(
    "hyphenated server names are parsed as one server, not split -> pass",
    0,
    {"a.md": agent("mcp__bc-decompiler__get_il", "Use mcp__bc-decompiler__get_il here.")},
)

case(
    "several agents, one broken -> fail, and the message names that file",
    1,
    {
        "good.md": agent("mcp__srv__do_thing", "Use mcp__srv__do_thing."),
        "bad.md": agent("Bash", "Use mcp__srv__do_thing."),
    },
    expect_in_stderr="bad.md",
)

case("a directory with no agent definitions is exit 2, not exit 0", 2, {})


def run():
    failures = 0
    for name, expected_exit, files, expect_in_stderr in CASES:
        with tempfile.TemporaryDirectory() as tmp:
            d = pathlib.Path(tmp)
            for fname, content in files.items():
                (d / fname).write_text(content, encoding="utf-8")
            r = subprocess.run(
                [sys.executable, str(SCRIPT), str(d)],
                capture_output=True,
                text=True,
            )
            ok = r.returncode == expected_exit
            if ok and expect_in_stderr:
                ok = expect_in_stderr in r.stderr
            if ok:
                print(f"PASS  {name}")
            else:
                failures += 1
                print(f"FAIL  {name}")
                print(f"      expected exit {expected_exit}, got {r.returncode}")
                if expect_in_stderr:
                    print(f"      expected stderr to contain: {expect_in_stderr!r}")
                print(f"      stderr: {r.stderr.strip()[:400]}")

    print(f"\n{len(CASES) - failures}/{len(CASES)} passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(run())
