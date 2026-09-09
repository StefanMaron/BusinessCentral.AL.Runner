#!/usr/bin/env python3
"""PreToolUse warning: a Bash command is about to use a SHARED scratchpad path.

Three agents in one session each knew the scratchpad was shared and were caught
anyway (#2980), because the sharing is invisible at the moment of use: the path
in the prompt looks private, `gh pr create --body-file` reads whatever is there,
and `cp -r` onto an existing clone succeeds. This hook is the part that fires at
that moment.

It warns on a scratchpad path with no `agent-` owner directory in it -- writing
one, reading one into a `gh`/`git` command, or cloning onto one. It does NOT
warn when the path already carries an agent directory, which is what makes it a
usable signal rather than noise on every command.

ADVISORY, exit 0, never blocks. Blocking would be wrong here: a shared path is
sometimes exactly right (reading another agent's log to diagnose a collision is
the obvious case), and a hook that blocks legitimate work gets switched off,
taking the warning with it. The refusal that CAN block lives in
`tools/agent_scratchpad.py check`, where a caller opts into it explicitly.

Tested by tools/test_shared_scratchpad_guard.py. The suite lives there rather than
beside the hook because `pr-gate.yml`'s tools-tests job globs `tools/test_*.py`
only, so a test next to the hook gates nothing (#3707).
"""
import json
import os
import re
import sys

# The session scratchpad, as the harness names it: /tmp/claude-<uid>/<slug>/<uuid>/scratchpad
SCRATCHPAD_PATH = re.compile(r'(/tmp/claude-\d+/[^\s"\':;|&]*?/scratchpad)(/[^\s"\':;|&)]*)?')

# A path is SAFE once it names an agent directory anywhere below the scratchpad.
OWNED = re.compile(r'/agent-[A-Za-z0-9][A-Za-z0-9._-]*(?:/|$)')

# Verbs that make a shared path dangerous rather than merely present. A bare
# `ls <scratchpad>` or `cat` of a log is fine and must not nudge -- the cost of
# this hook is paid on every Bash call, so it has to be quiet.
DANGEROUS = re.compile(
    r'(?:^|[|;&]\s*)\s*(?:command\s+)?'
    r'(?:cp|mv|rm|git\s+clone|git\s+-C|tee|mkdir)\b'
    r'|>\s*\S*'                                  # any redirection into a path
    r'|<<\s*[\'"]?\w+'                           # heredoc
    r'|--body-file|--body-path|-F\s|--file\b'    # gh reading a staged body
)

MESSAGE = (
    "Shared-scratchpad warning (advisory, nothing was blocked).\n"
    "This command uses a session-scratchpad path with no `agent-<id>/` owner in it.\n"
    "That directory is SHARED by every agent of this session -- it looks per-agent and\n"
    "is not. Measured 2026-09-07: 200 entries in one session's scratchpad, ONE of them\n"
    "namespaced. It has published PRs #2973/#2974/#3181 with another agent's body and a\n"
    "wrong `Closes #N`, buried a bug report inside a closed issue (#3073), and made a\n"
    "full-corpus run silently omit the tests it was measuring (#2980).\n"
    "Use a private path:\n"
    "  p=$(tools/agent_scratchpad.py path pr-body.md --agent-id <YOUR-ID>)\n"
    "  tools/agent_scratchpad.py check <path> --agent-id <YOUR-ID>   # exit 1 if shared\n"
    "  tools/agent_scratchpad.py scan                                # what is unowned\n"
    "Reading another agent's file on purpose is fine -- this is a reminder, not a rule."
)


def flagged_paths(cmd: str) -> list:
    """Scratchpad paths in `cmd` that name no agent directory."""
    out = []
    for m in SCRATCHPAD_PATH.finditer(cmd):
        full = m.group(0)
        if OWNED.search(full):
            continue
        # The scratchpad root alone, with no child, is usually `ls`/`cd` -- only
        # interesting when a dangerous verb is about to write into it.
        out.append(full)
    return out


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0
    if payload.get("tool_name") != "Bash":
        return 0
    cmd = (payload.get("tool_input") or {}).get("command", "") or ""
    if not SCRATCHPAD_PATH.search(cmd):
        return 0
    if not DANGEROUS.search(cmd):
        return 0
    paths = flagged_paths(cmd)
    if not paths:
        return 0
    print(MESSAGE, file=sys.stderr)
    print("Shared path(s) in this command:", file=sys.stderr)
    for p in sorted(set(paths))[:5]:
        print(f"  {p}", file=sys.stderr)
    # Exit 0: advisory only, for the reason in the module docstring.
    return 0


if __name__ == "__main__":
    sys.exit(main())
