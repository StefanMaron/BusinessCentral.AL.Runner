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

This hook is ADVISORY. It never blocks a call; it prints one short reminder so
an agent does not have to remember, or discover, the cheaper path. It fires for
reads and searches aimed at C# under AlRunner/, which is where the navigation
tools answer better than the shell does.
"""
import json
import re
import sys

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

MESSAGE = (
    "Code-navigation reminder (advisory, nothing was blocked).\n"
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


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0
    if payload.get("tool_name") != "Bash":
        return 0
    cmd = (payload.get("tool_input") or {}).get("command", "") or ""

    searching = bool(TEXT_SEARCH.search(cmd)) and bool(TARGETS_CS.search(cmd))
    reading = bool(READ_VERB.search(cmd)) and bool(NAMES_CS_FILE.search(cmd))
    if not (searching or reading):
        return 0
    if WRITES_CS.search(cmd):
        return 0
    if NOT_A_SYMBOL_LOOKUP.search(cmd):
        return 0
    print(MESSAGE, file=sys.stderr)
    # Exit 0: advisory only. Never block -- grep over C# is sometimes exactly right,
    # and a hook that blocks would cost more than the greps it prevents.
    return 0


if __name__ == "__main__":
    sys.exit(main())
