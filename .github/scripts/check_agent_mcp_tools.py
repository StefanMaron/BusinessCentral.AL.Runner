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

The check runs in BOTH directions, because a capability can go missing either way
and only one of the two was ever caught.

DIRECTION 1 -- what the definition DOCUMENTS (#2395, above). For every agent
definition that declares a `tools:` allowlist, every concrete
`mcp__server__tool` name mentioned in its body must appear in that allowlist,
and every `mcp__server__*` mentioned in its body must be backed by at least one
entry for that server. An agent with no `tools:` key inherits every tool and is
skipped.

DIRECTION 2 -- what the RULES REQUIRE (#4304). A rule or skill can oblige an
agent to do something its allowlist cannot discharge, and direction 1 is blind
to it by construction: the definition never mentions the tool, so there is no
mention to check. That is not hypothetical. `search-for-the-same-defect-first.md`
tells an implementation agent to search the open queue "by its mechanism, not by
its title", asking for three of four keys that live in issue BODIES; the only
full-text search over bodies in a web session is `mcp__github__search_issues`,
and `impl-agent.md`'s allowlist did not carry it. Four agents across two agent
types reported the limitation in one day and each correctly declined to claim a
search it had not run -- while this script printed OK, because nothing compared
the duty against the allowlist.

So a rule or skill declares the obligation with a marker line, and this script
holds the named agent to it:

    Requires-Tool: <agent-name> <mcp__server__tool>   # why, in free text

The marker is deliberately explicit rather than inferred from prose. Inferring
"this paragraph obliges an agent to search" needs to read intent, would
false-alarm on every rule that merely MENTIONS a tool, and would make the guard
the thing people work around. An explicit line is greppable, reviewable in a
diff, and wrong only in ways a reader can see.

Usage: check_agent_mcp_tools.py [agents-dir] [--requirements-dir DIR ...]
Defaults to .claude/agents, scanning .claude/rules and .claude/skills for
markers, all relative to the repository root.
Exits 0 and prints a confirmation when every mention and every requirement holds.
Exits 1 with an ::error:: line per unsatisfied mention or requirement.
Exits 2 if the directory holds no agent definitions at all -- a distinct code
from "check failed", meaning the check could not run.
Exits 3 when a `Requires-Tool:` marker names an agent that has no definition, or
is malformed: the requirement could not be MEASURED, which
guards-need-a-third-state.md separates from both "fine" and "broken". Reporting
either of those would be a verdict nobody took -- a typo'd agent name would
otherwise read as a clean pass forever.
"""

import pathlib
import re
import sys

# Server names carry hyphens ("bc-decompiler"); tool names do not.
CONCRETE = re.compile(r"mcp__([A-Za-z0-9_-]+)__([A-Za-z0-9_]+)")
WILDCARD = re.compile(r"mcp__([A-Za-z0-9_-]+)__\*")

# A rule/skill declaring that some agent must be able to call some tool.
# Anchored at line start (after optional list/quote punctuation) so that prose
# ABOUT the marker -- this repository documents its own markers constantly --
# cannot be mistaken for one. Same reasoning as check_corpus_linkage.sh, whose
# `Corpus-PR:` line must also start its line: a mid-sentence mention is
# discussion, not a declaration.
REQUIRES = re.compile(
    r"^[ \t]*(?:[-*>]\s*)*Requires-Tool:[ \t]*(\S+)[ \t]+(\S+)",
    re.MULTILINE,
)
# Matches the marker word at line start regardless of what follows it, so a
# malformed declaration is reported rather than silently skipped: a marker the
# strict pattern misses would otherwise be indistinguishable from no marker.
REQUIRES_LOOSE = re.compile(r"^[ \t]*(?:[-*>]\s*)*Requires-Tool:(.*)$", re.MULTILINE)


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


def collect_requirements(dirs):
    """Every `Requires-Tool:` marker found under `dirs`, as
    (agent, tool, source_path) plus a list of malformed-marker complaints.

    A malformed marker is its own outcome rather than a silent skip: a line
    reading `Requires-Tool: impl-agent` with the tool missing declares an
    intent nobody can check, and treating it as absent is the third state
    spelled as the success state.
    """
    found, malformed = [], []
    for d in dirs:
        if not d.is_dir():
            continue
        for path in sorted(d.rglob("*.md")):
            text = path.read_text(encoding="utf-8")
            strict = {m.start() for m in REQUIRES.finditer(text)}
            for m in REQUIRES_LOOSE.finditer(text):
                if m.start() in strict:
                    continue
                malformed.append(
                    f"{path}: malformed Requires-Tool marker "
                    f"'Requires-Tool:{m.group(1).rstrip()}' -- the form is "
                    f"'Requires-Tool: <agent-name> <mcp__server__tool>'"
                )
            for m in REQUIRES.finditer(text):
                found.append((m.group(1), m.group(2), path))
    return found, malformed


def check_requirements(requirements, agent_tools):
    """Hold each declared requirement against the named agent's allowlist.

    Returns (problems, unmeasurable). The split is the point: a requirement on
    an agent that does not exist, or on one that declares no allowlist at all,
    has not been MEASURED -- reporting either as a pass would assert something
    nobody checked, and reporting them as failures would blame an agent for a
    typo in a rule.
    """
    problems, unmeasurable = [], []
    for agent, tool, source in requirements:
        if agent not in agent_tools:
            unmeasurable.append(
                f"{source}: Requires-Tool names agent '{agent}', which has no "
                f"definition in the agents directory, so the requirement for "
                f"{tool} could not be checked against anything"
            )
            continue
        tools = agent_tools[agent]
        if tools is None:
            # No `tools:` key means the agent inherits every tool, so the
            # requirement is satisfied -- the same permissive reading
            # check_file() already takes for direction 1.
            continue
        if tool not in tools:
            problems.append(
                f"{source}: requires {agent} to be able to call {tool}, but "
                f"{agent}'s tools: allowlist does not grant it. An explicit "
                f"allowlist is exhaustive, so that call returns 'No such tool "
                f"available' and the agent cannot do what this file tells it to do"
            )
    return problems, unmeasurable


def main(argv):
    args = argv[1:]
    req_dirs = []
    positional = []
    i = 0
    while i < len(args):
        if args[i] == "--requirements-dir":
            if i + 1 >= len(args):
                print(
                    "::error::--requirements-dir needs a directory", file=sys.stderr
                )
                return 3
            req_dirs.append(pathlib.Path(args[i + 1]))
            i += 2
        else:
            positional.append(args[i])
            i += 1

    repo = pathlib.Path(__file__).resolve().parents[2]
    root = pathlib.Path(positional[0]) if positional else (repo / ".claude" / "agents")
    if not req_dirs:
        req_dirs = [repo / ".claude" / "rules", repo / ".claude" / "skills"]

    files = sorted(root.glob("*.md"))
    if not files:
        print(f"::error::no agent definitions found in {root}", file=sys.stderr)
        return 2

    problems = [p for f in files for p in check_file(f)]

    agent_tools = {}
    for f in files:
        front, _ = split_front_matter(f.read_text(encoding="utf-8"))
        agent_tools[f.stem] = declared_tools(front)

    requirements, malformed = collect_requirements(req_dirs)
    req_problems, unmeasurable = check_requirements(requirements, agent_tools)
    problems += req_problems
    unmeasurable += malformed

    for p in problems:
        print(f"::error::{p}", file=sys.stderr)
    for u in unmeasurable:
        print(f"::error::unmeasurable: {u}", file=sys.stderr)

    if problems:
        print(
            f"\n{len(problems)} unsatisfied MCP tool mention(s)/requirement(s) across "
            f"{len(files)} agent definition(s). Add the tool names to that agent's "
            "tools: line, or stop telling the agent to use them.",
            file=sys.stderr,
        )
        return 1
    if unmeasurable:
        print(
            f"\n{len(unmeasurable)} Requires-Tool marker(s) could not be measured. "
            "This is not a pass: fix the marker or the agent name so the "
            "requirement can be checked.",
            file=sys.stderr,
        )
        return 3

    print(
        f"OK: every MCP tool mentioned in {len(files)} agent definition(s) is "
        f"allowlisted, and all {len(requirements)} Requires-Tool requirement(s) hold"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
