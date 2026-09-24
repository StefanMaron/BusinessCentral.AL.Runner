#!/usr/bin/env python3
"""Unit tests for check_agent_mcp_tools.py, run against synthetic agent
definitions in a temporary directory.

Written against fixtures rather than only the repo's real agents so the check
stays proven after someone legitimately edits those agents -- the same pattern
as test_check_pr_check_triggers.sh.

The `Requires-Tool:` cases (#4304) cover the second direction: a rule declares
that some agent must be able to call some tool, and the check holds the named
agent's allowlist to it. They are table-driven over the property rather than
over the one real instance, so the fixtures name a fictional `srv` server and
never `mcp__github__search_issues` -- a suite that only pinned today's instance
would pass a guard that special-cased it.

Usage: python3 test_check_agent_mcp_tools.py
Exits 0 when every case passes, 1 on the first failure.
"""

import json
import pathlib
import subprocess
import sys
import tempfile

SCRIPT = pathlib.Path(__file__).resolve().parent / "check_agent_mcp_tools.py"

# Sentinel: this case does not pass --mcp-config at all, which is the
# ordinary invocation and must stay a pass (the genuinely-absent row).
NO_MCP_FLAG = object()

CASES = []


def case(name, expected_exit, files, expect_in_stderr=None, rules=None,
         expect_not_in_stderr=None, mcp=NO_MCP_FLAG):
    CASES.append((name, expected_exit, files, expect_in_stderr, rules,
                  expect_not_in_stderr, mcp))


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


# ---------------------------------------------------------------------------
# Direction 2 (#4304): a rule REQUIRES a tool the definition never mentions.
#
# Direction 1 is blind to this by construction -- there is no mention to check --
# so every case below passes the unextended script. The first two are the
# discriminating pair: identical rule text, allowlists differing only in the
# required tool.
# ---------------------------------------------------------------------------

REQ = "Requires-Tool: fixture mcp__srv__find_things\n"

case(
    "a required tool that IS allowlisted -> pass",
    0,
    {"fixture.md": agent("Bash, mcp__srv__find_things", "Body mentions nothing.")},
    rules={"r.md": f"Scan the queue.\n\n{REQ}\nThat is why.\n"},
)

case(
    "a required tool that is NOT allowlisted -> fail, naming agent and tool",
    1,
    {"fixture.md": agent("Bash, Read", "Body mentions nothing.")},
    expect_in_stderr="mcp__srv__find_things",
    rules={"r.md": f"Scan the queue.\n\n{REQ}\nThat is why.\n"},
)

case(
    "the failure names the RULE file, so a reader knows which duty is unmet",
    1,
    {"fixture.md": agent("Bash, Read", "Body mentions nothing.")},
    expect_in_stderr="r.md",
    rules={"r.md": f"Scan the queue.\n\n{REQ}"},
)

case(
    "an agent with no tools: key inherits everything, so the duty is satisfied",
    0,
    {"fixture.md": agent(None, "Body mentions nothing.")},
    rules={"r.md": REQ},
)

# --- Meaning-PRESERVING rewrites: the marker must survive a good edit. -------
# Each of these is a reformatting a human would make without changing what the
# rule says. A key that reds on any of them is a key that punishes good edits.

for label, text in [
    ("as a markdown list item", f"- {REQ}"),
    ("as a nested list item", f"  - {REQ}"),
    ("inside a blockquote", f"> {REQ}"),
    ("indented under a paragraph", f"    {REQ}"),
    ("with a trailing free-text reason", "Requires-Tool: fixture mcp__srv__find_things"
     "   # because bodies are not titles\n"),
    ("with extra internal whitespace", "Requires-Tool:   fixture   mcp__srv__find_things\n"),
    ("surrounded by prose above and below",
     f"Some prose first.\n\n{REQ}\nAnd some prose after.\n"),
]:
    case(
        f"meaning-preserving: {label} -> still detected (fails on a bad allowlist)",
        1,
        {"fixture.md": agent("Bash, Read", "Body mentions nothing.")},
        expect_in_stderr="mcp__srv__find_things",
        rules={"r.md": text},
    )

# --- Meaning-DROPPING truncations: a gutted marker must NOT read as satisfied.
# The mirror mutation the bar asks for: keep the phrase, drop the claim. Each
# of these still contains the literal text "Requires-Tool:", so any check that
# greps for the phrase alone would call them declarations and pass.

for label, text, expect in [
    ("the tool is missing", "Requires-Tool: fixture\n", "malformed"),
    ("both operands are missing", "Requires-Tool:\n", "malformed"),
    ("only whitespace follows", "Requires-Tool:    \n", "malformed"),
]:
    case(
        f"meaning-dropping: {label} -> unmeasurable (exit 3), never a pass",
        3,
        {"fixture.md": agent("Bash, Read", "Body mentions nothing.")},
        expect_in_stderr=expect,
        rules={"r.md": text},
    )

case(
    "meaning-dropping: the agent name is a typo -> unmeasurable, not a silent pass",
    3,
    {"fixture.md": agent("Bash, Read", "Body mentions nothing.")},
    expect_in_stderr="has no definition",
    rules={"r.md": "Requires-Tool: fixtrue mcp__srv__find_things\n"},
)

# --- A real unsatisfied requirement OUTRANKS an unmeasurable one. -----------
# Exit 1 and exit 3 mean different things; a run carrying both must report the
# one that is a defect in the tree, not the one that is a defect in a marker.

case(
    "a genuine failure alongside a malformed marker reports exit 1, not 3",
    1,
    {"fixture.md": agent("Bash, Read", "Body mentions nothing.")},
    expect_in_stderr="mcp__srv__find_things",
    rules={"a.md": REQ, "b.md": "Requires-Tool: fixture\n"},
)

# --- Prose ABOUT the marker is not a declaration. ---------------------------
# This repository documents its own markers constantly, so the guard must not
# fire on a sentence that merely says the words.

case(
    "a mid-sentence mention is discussion, not a declaration -> pass",
    0,
    {"fixture.md": agent("Bash, Read", "Body mentions nothing.")},
    rules={"r.md": "Write a Requires-Tool: fixture mcp__srv__find_things line to declare it.\n"},
)

case(
    "markers are found in nested subdirectories, not just the top level",
    1,
    {"fixture.md": agent("Bash, Read", "Body mentions nothing.")},
    expect_in_stderr="mcp__srv__find_things",
    rules={"skill/SKILL.md": REQ},
)

case(
    "no markers anywhere -> pass, and the run says zero requirements held",
    0,
    {"fixture.md": agent("Bash, Read", "Body mentions nothing.")},
    rules={"r.md": "A rule with no marker at all.\n"},
)


# --- #4449: a required tool whose SERVER no .mcp.json provides ----------------
#
# The allowlist half of this was #4304 and is checked above. This half is the
# next layer: the entry is present and grants nothing, because no configured
# server supplies that namespace. An allowlist entry for a tool nothing
# provides passes every "is it listed?" check and resolves to nothing.
#
# The rows are the three from guards-need-a-third-state.md. Row 1 is the
# constraint that stops the fix trading one defect for another: a genuinely
# absent .mcp.json must stay a PASS, because the file is gitignored and
# per-machine, so it is absent on every CI run and in every worktree. A guard
# that failed there would red the whole repository for a correct state.

case(
    "#4449 required tool, server configured -> pass",
    0,
    {"fixture.md": agent("Bash, mcp__srv__find_things", "A fixture.")},
    rules={"r.md": REQ},
    mcp={"mcpServers": {"srv": {"type": "stdio", "command": "x", "args": []}}},
)

case(
    "#4449 required tool, server ABSENT from .mcp.json -> unmeasurable (3)",
    3,
    {"fixture.md": agent("Bash, mcp__srv__find_things", "A fixture.")},
    rules={"r.md": REQ},
    mcp={"mcpServers": {"other": {"type": "stdio", "command": "x", "args": []}}},
    expect_in_stderr="no configured MCP server provides",
)

case(
    "#4449 no .mcp.json at all -> pass (absent is legitimate, not unmeasurable)",
    0,
    {"fixture.md": agent("Bash, mcp__srv__find_things", "A fixture.")},
    rules={"r.md": REQ},
    mcp=None,
)

case(
    "#4449 unreadable .mcp.json -> unmeasurable (3), NOT folded into absent",
    3,
    {"fixture.md": agent("Bash, mcp__srv__find_things", "A fixture.")},
    rules={"r.md": REQ},
    mcp="{ this is not json",
    expect_in_stderr="could not be read",
)

case(
    "#4449 empty mcpServers -> unmeasurable (3), an empty map is not an absent file",
    3,
    {"fixture.md": agent("Bash, mcp__srv__find_things", "A fixture.")},
    rules={"r.md": REQ},
    mcp={"mcpServers": {}},
    expect_in_stderr="no configured MCP server provides",
)

# The server question is asked ONLY of a Requires-Tool target. A tool the body
# merely MENTIONS is direction 1 (#2395), whose subject is the allowlist; an
# agent may document a tool for an environment other than the one the check
# runs in, and failing that would make the guard refuse every local session
# whose .mcp.json is legitimately narrower than the docs.
case(
    "#4449 body-mentioned tool with no server entry -> still pass",
    0,
    {"fixture.md": agent("Bash, mcp__srv__do_thing", "Call mcp__srv__do_thing.")},
    rules={"r.md": "A rule with no marker at all.\n"},
    mcp={"mcpServers": {"other": {"type": "stdio", "command": "x", "args": []}}},
)

def run():
    failures = 0
    for (name, expected_exit, files, expect_in_stderr, rules, expect_not,
         mcp) in CASES:
        with tempfile.TemporaryDirectory() as tmp:
            d = pathlib.Path(tmp) / "agents"
            d.mkdir()
            for fname, content in files.items():
                (d / fname).write_text(content, encoding="utf-8")
            cmd = [sys.executable, str(SCRIPT), str(d)]
            rules_dir = pathlib.Path(tmp) / "rules"
            rules_dir.mkdir()
            for fname, content in (rules or {}).items():
                target = rules_dir / fname
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(content, encoding="utf-8")
            cmd += ["--requirements-dir", str(rules_dir)]
            if mcp is not NO_MCP_FLAG:
                mcp_path = pathlib.Path(tmp) / ".mcp.json"
                if mcp is not None:
                    mcp_path.write_text(json.dumps(mcp), encoding="utf-8")
                cmd += ["--mcp-config", str(mcp_path)]
            r = subprocess.run(cmd, capture_output=True, text=True)
            ok = r.returncode == expected_exit
            if ok and expect_in_stderr:
                ok = expect_in_stderr in r.stderr
            if ok and expect_not:
                ok = expect_not not in r.stderr
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
