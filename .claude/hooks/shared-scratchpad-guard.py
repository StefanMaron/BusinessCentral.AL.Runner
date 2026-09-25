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

ADVISORY (exit 0) for the coordinator. BLOCKING (exit 2) when the payload's
`agent_type` is `impl-agent` or `reviewer` AND a shared path is the WRITE TARGET --
a redirection target, a tee/cp/mv/rm/mkdir/touch destination, a `git clone`/`git -C`
directory, or a `gh ... --body-file` source: a reviewer wrote `review.md` into the
shared directory and posted another reviewer's draft verdict onto the wrong PR
(#4534). The escape for a deliberate write there is `# hook:allow-shared-scratch`
in the command. A READ is never blocked, even when the same command carries `2>&1`
or `grep -F` -- the advisory matches the whole command, the block matches the path's
role (PR #4588 review).

The path match is keyed on the harness's `claude-<uid>/<slug>/<session>/scratchpad`
shape under ANY root: the scratchpad is not always under /tmp (on the loop's box it
is ~/.cache/claude-tmp/claude-<uid>/...), and a /tmp-anchored pattern never fired
there (#4534).

Tested by tools/test_shared_scratchpad_guard.py. The suite lives there rather than
beside the hook because `pr-gate.yml`'s tools-tests job globs `tools/test_*.py`
only, so a test next to the hook gates nothing unless something delegates to it
(#3707).
"""
import json
import os
import re
import sys

# The session scratchpad, as the harness names it: <root>/claude-<uid>/<slug>/<uuid>/scratchpad
SCRATCHPAD_PATH = re.compile(r'((?:~|/)[^\s"\':;|&]*?/claude-\d+/[^\s"\':;|&]*?/scratchpad)(/[^\s"\':;|&)]*)?')

BLOCKING_AGENT_TYPES = {"impl-agent", "reviewer"}
ESCAPE = "# hook:allow-shared-scratch"

# A path is SAFE once it names an agent directory anywhere below the scratchpad.
OWNED = re.compile(r'/agent-[A-Za-z0-9][A-Za-z0-9._-]*(?:/|$)')

# Verbs that make a shared path dangerous rather than merely present. A bare
# `ls <scratchpad>` or `cat` of a log is fine and must not nudge -- the cost of
# this hook is paid on every Bash call, so it has to be quiet.
DANGEROUS = re.compile(
    r'(?:^|[|;&]\s*)\s*(?:command\s+)?'
    r'(?:cp|mv|rm|git\s+clone|git\s+-C|tee|mkdir)\b'
    r'|>>?\s*(?!&\d)(?!/dev/null\b)\S'           # redirection into a path (not 2>&1, >/dev/null)
    r'|<<\s*[\'"]?\w+'                           # heredoc
    r'|--body-file|--body-path|\bgh\b[^|;&]*?(?:-F\s|--file\b)'   # gh reading a staged body
)

# --- the blocking decision: is a shared path the TARGET of a write? ---
SEGMENT_SPLIT = re.compile(r'\|\||&&|[|;\n(]|\$\(')
REDIRECT_BEFORE = re.compile(r'(?:\d|&)?>>?\|?\s*$')
GH_FILE_BEFORE = re.compile(r'(?:--body-file|--body-path|--file|-F)(?:\s+|=)(?:\w+=@)?$')
ANY_ARG_WRITERS = {"tee", "rm", "mkdir", "touch", "truncate"}
DEST_WRITERS = {"cp", "mv", "rsync", "install", "ln"}
PREFIX_WORDS = {"command", "sudo", "env", "nohup", "time"}


def _segment(cmd: str, pos: int) -> tuple:
    """(start, text) of the simple command containing cmd[pos]."""
    start = 0
    for m in SEGMENT_SPLIT.finditer(cmd, 0, pos):
        start = m.end()
    end_m = SEGMENT_SPLIT.search(cmd, pos)
    end = end_m.start() if end_m else len(cmd)
    return start, cmd[start:end]


def _words(text: str) -> list:
    """Whitespace tokens with their offsets, quotes stripped, redirections dropped."""
    out = []
    skip_next = False
    for m in re.finditer(r'\S+', text):
        tok = m.group(0).strip('"\'')
        if skip_next:
            skip_next = False
            continue
        if re.fullmatch(r'(?:\d|&)?>>?\|?', tok) or tok.startswith("<<"):
            skip_next = True
            continue
        if re.match(r'(?:\d|&)?>>?', tok) or tok.startswith("<"):
            continue
        out.append((m.start(), m.end(), tok))
    return out


def is_write_target(cmd: str, pos: int) -> bool:
    """True when the scratchpad path starting at cmd[pos] is written, not read."""
    before = cmd[:pos].rstrip("'\"")
    if REDIRECT_BEFORE.search(before):
        return True
    seg_start, seg = _segment(cmd, pos)
    words = _words(seg)
    while words and (words[0][2] in PREFIX_WORDS or re.fullmatch(r'\w+=\S*', words[0][2])):
        words = words[1:]
    if not words:
        return False
    verb = words[0][2].rsplit("/", 1)[-1]
    if verb == "gh" and GH_FILE_BEFORE.search(before):
        return True
    rel = pos - seg_start
    idx = next((i for i, (b, e, _) in enumerate(words) if b <= rel < e), None)
    if idx is None:
        return False
    positional = [i for i, (_, _, t) in enumerate(words[1:], 1) if not t.startswith("-")]
    if verb in ANY_ARG_WRITERS:
        return idx in positional
    if verb in DEST_WRITERS:
        prev = words[idx - 1][2] if idx > 0 else ""
        return prev in ("-t", "--target-directory") or (positional and idx == positional[-1]
                                                          and len(positional) >= 2)
    if verb == "git" and len(words) > 1:
        if words[idx - 1][2] == "-C":
            return True
        if words[1][2] == "clone":
            return positional[-1] == idx and len(positional) >= 3
    return False

MESSAGE = (
    "Shared-scratchpad warning.\n"
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


def flagged_paths(cmd: str, written_only: bool = False) -> list:
    """Scratchpad paths in `cmd` that name no agent directory (only write targets
    when `written_only`)."""
    out = []
    for m in SCRATCHPAD_PATH.finditer(cmd):
        full = m.group(0)
        if OWNED.search(full):
            continue
        if written_only and not is_write_target(cmd, m.start()):
            continue
        out.append(full)
    return out


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except Exception as exc:
        # Fail open (a harness payload change must not block every Bash call), but
        # say so: a silent 0 here is "could not measure" reported as "fine".
        print(f"shared-scratchpad-guard: could not read the hook payload ({exc.__class__.__name__}); "
              "not checking this call.", file=sys.stderr)
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
    blocking = (str(payload.get("agent_type") or "").strip().lower() in BLOCKING_AGENT_TYPES
                and ESCAPE not in cmd)
    written = flagged_paths(cmd, written_only=True) if blocking else []
    blocking = bool(written)
    if blocking:
        print("BLOCKED: this command WRITES a shared scratchpad path (impl-agent/reviewer "
              f"context; append `{ESCAPE}` to override one call). Reads are never blocked.",
              file=sys.stderr)
    print(MESSAGE, file=sys.stderr)
    print("Shared path(s) written by this command:" if blocking
          else "Shared path(s) in this command:", file=sys.stderr)
    for p in sorted(set(written or paths))[:5]:
        print(f"  {p}", file=sys.stderr)
    return 2 if blocking else 0


if __name__ == "__main__":
    sys.exit(main())
