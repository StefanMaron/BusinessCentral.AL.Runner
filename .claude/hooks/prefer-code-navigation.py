#!/usr/bin/env python3
"""PreToolUse nudge: point shell text-search at the code-navigation tools.

Measured 2026-09-02 across 17 subagents in one session: 3,237 Bash calls, of
which 2,716 (84%) were grep/sed/cat/head/find over the source tree.
`tools/lsp-query.py` was called ONCE in total and `graphify` twice.

Re-measured later the same day over 11 agents and 1,048 tool calls: 940 Bash,
of which 533 were sed/cat/head READS averaging 2.0 KB and only 5 were grep/rg.
602 of 940 Bash calls (64%) were read/search through the shell; the navigation
tools were used 14 times.

That re-measurement found a hole in this hook. It matched only grep/rg/ag, so
it was guarding the 5 search calls and ignoring the 533 reads -- the dominant
cost -- even though the paragraph above named sed/cat/head from the start. It
also missed `command grep`, which is the form CLAUDE.md tells everyone to use
here (plain `grep` is a shell function that rejects -E/--include). Both are
fixed below.

The cost driver is the NUMBER of round trips, not the size of any one result --
the whole conversation is re-sent on every call, so 500 small reads cost far
more than 50 targeted ones.

It fires for reads and searches aimed at C# under AlRunner/, which is where the
navigation tools answer better than the shell does, and it has two strengths
(#3707):

  * in an IMPL or REVIEWER context it BLOCKS (exit 2, stderr fed back);
  * anywhere else -- the coordinator session in the main checkout -- it stays
    advisory, exit 0, because grep over the C# tree is sometimes exactly right
    there and a hook that blocks legitimate work gets switched off wholesale.

The context comes from the payload's own `agent_type`, measured on harness
2.1.266: a dispatched subagent's payload carries `agent_type: "impl-agent"` and a
`cwd` of the PROJECT ROOT -- not of the agent's worktree -- so a cwd test alone
would never fire for the agents this hook is for. `AL_RUNNER_AGENT_ID` /
`CLAUDE_AGENT_ID`, and a `.claude/worktrees/` path in the cwd or the command,
are kept as secondary signals for a loop that runs as its own session.

`AL_RUNNER_HOOK_CONTEXT=coordinator` downgrades a session to advisory but
deliberately does NOT outrank `agent_type`: the environment is inherited by
every subagent the session dispatches, so letting it win would disarm the hook
for exactly the agents it guards. The per-call escape is a `# hook:allow-grep`
marker in the command, which always wins.

Tested by tools/test_prefer_code_navigation.py (firing) and
tools/test_agent_workflow_hooks.py (blocking, context and the escape hatch).
CI globs tools/test_*.py only, which is why neither suite lives beside the hook.
"""
import json
import os
import re
import sys

BLOCK = 2
ALLOW = 0

# The payload's own answer, measured on harness 2.1.266. `orchestrator` is not
# here: the coordinator's own greps stay advisory.
BLOCKING_AGENT_TYPES = {"impl-agent", "reviewer"}
# Both separators, because the cwd arrives Windows-shaped on a Windows box and
# POSIX-shaped in CI, and the same hook has to recognise each.
WORKTREE_PATH = re.compile(r'[\\/]\.claude[\\/]worktrees[\\/]')
# One call's opt-out. Without one, an agent whose grep really is the right tool
# has no move except to stop using the hook.
ALLOW_MARKER = re.compile(r'#\s*hook:allow-grep\b')

# `command grep` is the spelling CLAUDE.md mandates here, so it must match too.
TEXT_SEARCH = re.compile(r'(?:^|[|;&]\s*)\s*(?:command\s+)?(?:grep|rg|ag)\b')
# Reading source through the shell is the larger cost, and it was unguarded.
READ_VERB = re.compile(
    r'(?:^|[|;&]\s*)\s*(?:command\s+)?(?:sed|cat|head|tail|awk|less|more)\b')
# A read only counts when it actually names a C# file -- otherwise
# `dotnet build AlRunner | tail -5` would nudge on every build.
NAMES_CS_FILE = re.compile(r'\S*\.cs\b')
# Writing a C# file is not a lookup: heredocs, redirections and in-place sed.
WRITES_CS = re.compile(r'<<|>>?\s*\S*\.cs\b|\bsed\s+(?:-\w+\s+)*-i\b')
# Only nudge when the search is plausibly aimed at the C# sources.
TARGETS_CS = re.compile(r'AlRunner[\w./-]*|--include[= ]\S*\.cs|\*\.cs|\.cs\b')
# Things that are legitimately grep's job, not a symbol lookup.
NOT_A_SYMBOL_LOOKUP = re.compile(
    r'\.(log|json|trx|txt|md|xml|al)\b|/tmp/|scratchpad|git log|gh \w|dmesg|journalctl')

ADVISORY_HEADER = "Code-navigation reminder (advisory, nothing was blocked).\n"
BLOCK_HEADER = (
    "BLOCKED: shell read/search over AlRunner/*.cs in an agent context\n"
    "(CLAUDE.md, 'Code navigation: use these before grepping').\n"
    "WHY: the cost is the NUMBER of round trips -- every call re-sends the whole\n"
    "conversation -- and a grep hit over 81,000 lines of C# costs several follow-up reads to\n"
    "interpret, with comment and string false positives you then discount by hand.\n"
    "Append `# hook:allow-grep` to this command if the shell really is the right tool here.\n"
)
MESSAGE = (
    "For reading or searching AlRunner/*.cs, these answer in ONE call what a sequence of\n"
    "sed/cat/head/grep approximates, and without comment/string false positives:\n"
    "  tools/lsp-query.py symbol  <Name>      # definition\n"
    "  tools/lsp-query.py callers <Name>      # call sites   (exit 2 = server failed, NOT 'none')\n"
    "  tools/context-pack.py <Name> [<Name>...]   # definition + callers + context, one round trip\n"
    "  cd AlRunner && graphify update . && graphify query \"<Name> callers\"\n"
    "Phrase graphify queries as bare symbols, never as English questions.\n"
    "The LSP tool itself is disabled inside subagents on this build -- these scripts are the\n"
    "supported substitute. Keep using grep for logs, JSON, markdown and AL sources -- but\n"
    "note `grep` here is a shell FUNCTION that rejects -E/--include with \"unknown option\n"
    "'-G'\" and exits 0 with NO OUTPUT, which reads exactly like 'no matches'. Use\n"
    "`command grep` or `rg` before believing an empty result."
)


def is_agent_context(payload: dict, cmd: str, env) -> bool:
    """Whether this call is an impl or reviewer agent's, so the hook blocks."""
    if str(payload.get("agent_type") or "").strip().lower() in BLOCKING_AGENT_TYPES:
        return True
    if (env.get("AL_RUNNER_HOOK_CONTEXT") or "").strip().lower() == "coordinator":
        return False
    if (env.get("AL_RUNNER_AGENT_ID") or env.get("CLAUDE_AGENT_ID") or "").strip():
        return True
    cwd = payload.get("cwd") or ""
    return bool(WORKTREE_PATH.search(cwd) or WORKTREE_PATH.search(cmd))


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return ALLOW
    if payload.get("tool_name") != "Bash":
        return ALLOW
    cmd = (payload.get("tool_input") or {}).get("command", "") or ""

    searching = bool(TEXT_SEARCH.search(cmd)) and bool(TARGETS_CS.search(cmd))
    reading = bool(READ_VERB.search(cmd)) and bool(NAMES_CS_FILE.search(cmd))
    if not (searching or reading):
        return ALLOW
    if WRITES_CS.search(cmd):
        return ALLOW
    if NOT_A_SYMBOL_LOOKUP.search(cmd):
        return ALLOW

    blocking = (is_agent_context(payload, cmd, os.environ)
                and not ALLOW_MARKER.search(cmd))
    print((BLOCK_HEADER if blocking else ADVISORY_HEADER) + MESSAGE, file=sys.stderr)
    return BLOCK if blocking else ALLOW


if __name__ == "__main__":
    sys.exit(main())
