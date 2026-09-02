#!/usr/bin/env python3
"""PreToolUse nudge: point shell text-search at the code-navigation tools.

Measured 2026-09-02 across 17 subagents in one session: 3,237 Bash calls, of
which 2,716 (84%) were grep/sed/cat/head/find over the source tree.
`tools/lsp-query.py` was called ONCE in total and `graphify` twice.

The cost driver is the NUMBER of round trips, not the size of any one result --
the whole conversation is re-sent on every call, so 200 small greps cost far
more than 20 targeted ones.

This hook is ADVISORY. It never blocks a call; it prints one short reminder so
an agent does not have to remember, or discover, the cheaper path. It fires only
for text search aimed at C# under AlRunner/, which is where the navigation tools
actually answer better than grep does.
"""
import json
import re
import sys

TEXT_SEARCH = re.compile(r'(?:^|[|;&]\s*)\s*(?:grep|rg|ag)\b')
# Only nudge when the search is plausibly aimed at the C# sources.
TARGETS_CS = re.compile(r'AlRunner[\w./-]*|--include[= ]\S*\.cs|\*\.cs|\.cs\b')
# Things that are legitimately grep's job, not a symbol lookup.
NOT_A_SYMBOL_LOOKUP = re.compile(
    r'\.(log|json|trx|txt|md|xml|al)\b|/tmp/|scratchpad|git log|gh \w|dmesg|journalctl')

MESSAGE = (
    "Code-navigation reminder (advisory, nothing was blocked).\n"
    "For 'where is this defined' / 'what calls this' in AlRunner/*.cs, these answer in ONE\n"
    "call what grep needs several to approximate, and without comment/string false positives:\n"
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
    if not TEXT_SEARCH.search(cmd):
        return 0
    if not TARGETS_CS.search(cmd):
        return 0
    if NOT_A_SYMBOL_LOOKUP.search(cmd):
        return 0
    print(MESSAGE, file=sys.stderr)
    # Exit 0: advisory only. Never block -- grep over C# is sometimes exactly right,
    # and a hook that blocks would cost more than the greps it prevents.
    return 0


if __name__ == "__main__":
    sys.exit(main())
