#!/usr/bin/env python3
"""Fails when an agent definition tells the agent to use an MCP tool that its
own frontmatter `tools:` allowlist filters out.

The gap this guards, from #2395: `.claude/agents/impl-agent.md` carried a
50-line section headed "Reading BC's own code: use the `bc-decompiler` MCP
server", with the context-alias table, the search_members -> memberId ->
get_decompiled_source workflow and measured timings. Its `tools:` line listed
only the `mcp__github__*` tools. An explicit allowlist is exhaustive, so every
`mcp__bc-decompiler__*` tool was filtered out of the session and every call
returned "No such tool available". `triager.md` had the same pair of defects.

Nothing announced the contradiction. An implementation agent found it by
calling a tool the instructions told it to call, and fell back to running
ilspycmd by hand -- correct, but minutes per question instead of sub-second.

The check: for every agent definition that declares a `tools:` allowlist, every
concrete `mcp__server__tool` name mentioned in its body must appear in that
allowlist, and every `mcp__server__*` mentioned in its body must be backed by at
least one entry for that server. An agent with no `tools:` key inherits every
tool and is skipped.

Usage: check_agent_mcp_tools.py [agents-dir]
Defaults to .claude/agents relative to the repository root.
Exits 0 and prints a confirmation when every mention is satisfied.
Exits 1 with an ::error:: line per unsatisfied mention.
Exits 2 if the directory holds no agent definitions at all -- a distinct code
from "check failed", meaning the check could not run.
"""

import pathlib
import re
import sys

# Server names carry hyphens ("bc-decompiler"); tool names do not.
CONCRETE = re.compile(r"mcp__([A-Za-z0-9_-]+)__([A-Za-z0-9_]+)")
WILDCARD = re.compile(r"mcp__([A-Za-z0-9_-]+)__\*")


def split_front_matter(text):
    """Return (frontmatter, body). Both empty strings when there is no
    frontmatter, so a definition without one is treated as declaring no
    allowlist -- which is the permissive reading, and the safe one."""
    if not text.startswith("---"):
        return "", text
    end = text.find("\n---", 3)
    if end == -1:
        return "", text
    return text[3:end], text[end + 4 :]


def declared_tools(front_matter):
    """The `tools:` value as a list, or None when the key is absent.

    Deliberately line-oriented rather than a YAML parse: these files carry
    unquoted colons and em dashes in `description:`, which a strict YAML
    loader rejects outright, and the value we need is always one line.
    """
    for line in front_matter.splitlines():
        if line.startswith("tools:"):
            return [t.strip() for t in line[len("tools:") :].split(",") if t.strip()]
    return None


def check_file(path):
    front_matter, body = split_front_matter(path.read_text(encoding="utf-8"))
    tools = declared_tools(front_matter)
    if tools is None:
        return []  # inherits every tool

    servers_covered = {
        name for t in tools if (m := CONCRETE.fullmatch(t)) for name in [m.group(1)]
    }
    problems = []

    for server in sorted(set(WILDCARD.findall(body))):
        if server not in servers_covered:
            problems.append(
                f"{path}: the body tells the agent to use mcp__{server}__* but the "
                f"tools: allowlist has no entry for the {server} server, so every one "
                f"of those calls returns 'No such tool available'"
            )

    mentioned = {f"mcp__{s}__{t}" for s, t in CONCRETE.findall(body)}
    for name in sorted(mentioned - set(tools)):
        problems.append(
            f"{path}: the body mentions {name} but it is not in the tools: allowlist"
        )

    return problems


def main(argv):
    root = pathlib.Path(argv[1]) if len(argv) > 1 else (
        pathlib.Path(__file__).resolve().parents[2] / ".claude" / "agents"
    )
    files = sorted(root.glob("*.md"))
    if not files:
        print(f"::error::no agent definitions found in {root}", file=sys.stderr)
        return 2

    problems = [p for f in files for p in check_file(f)]
    for p in problems:
        print(f"::error::{p}", file=sys.stderr)
    if problems:
        print(
            f"\n{len(problems)} unsatisfied MCP tool mention(s) across {len(files)} "
            "agent definition(s). Add the tool names to that agent's tools: line, or "
            "stop telling the agent to use them.",
            file=sys.stderr,
        )
        return 1

    print(f"OK: every MCP tool mentioned in {len(files)} agent definition(s) is allowlisted")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
