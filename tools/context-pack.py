#!/usr/bin/env python3
"""One round trip, many answers: definition + call sites + context for N symbols.

Why this exists
---------------
Measured 2026-09-02 across 17 subagents in a single session: 3,237 Bash calls,
of which 2,716 (84%) were grep/sed/cat/head/find over the source tree.
`tools/lsp-query.py` was called ONCE in total; `graphify` twice.

The cost driver is the NUMBER of round trips, not the size of any one result.
Every tool call re-sends the whole accumulated conversation, so 200 small greps
cost far more than 20 targeted ones. `lsp-query.py` already answers one question
per invocation well; this wraps it so a batch of questions costs ONE invocation
instead of one each.

Usage
-----
    tools/context-pack.py GetDataAccessForTableCore IsManualBindingCodeunitType
    tools/context-pack.py --context 25 BuildMetaField
    tools/context-pack.py --no-body SomeSymbol      # locations only, no source

Exit codes
----------
    0  every symbol resolved
    1  at least one symbol was not found (a real not-found you may rely on)
    2  the language server failed -- the result means NOTHING. Never read a 2 as
       "this symbol has no callers"; re-run, or fall back to grep and say so.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LSP_QUERY = os.path.join(REPO, "tools", "lsp-query.py")

LOC = re.compile(r"([\w./\\-]+\.cs):(\d+)")


def run_lsp(mode: str, symbol: str, timeout: int) -> tuple[int, str]:
    """Returns (exit_code, stdout). Exit 2 means the server failed."""
    try:
        p = subprocess.run(
            [sys.executable, LSP_QUERY, mode, symbol],
            cwd=REPO, capture_output=True, text=True, timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        return 2, f"(timed out after {timeout}s)"
    except FileNotFoundError:
        return 2, f"(tools/lsp-query.py not found at {LSP_QUERY})"
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def excerpt(path: str, line: int, ctx: int) -> str:
    full = path if os.path.isabs(path) else os.path.join(REPO, path)
    try:
        with open(full, encoding="utf-8", errors="replace") as fh:
            lines = fh.read().split("\n")
    except OSError as exc:
        return f"      (cannot read {path}: {exc})"
    lo, hi = max(0, line - 1 - ctx // 3), min(len(lines), line - 1 + ctx)
    out = []
    for n in range(lo, hi):
        mark = ">>" if n == line - 1 else "  "
        out.append(f"   {mark} {n + 1:5d}  {lines[n]}")
    return "\n".join(out)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("symbols", nargs="+")
    ap.add_argument("--context", type=int, default=18,
                    help="source lines to show at the definition (default 18)")
    ap.add_argument("--no-body", action="store_true", help="locations only")
    ap.add_argument("--timeout", type=int, default=60, help="per lsp-query call")
    args = ap.parse_args()

    worst = 0
    for sym in args.symbols:
        print(f"\n{'=' * 72}\n== {sym}\n{'=' * 72}")

        rc_def, out_def = run_lsp("symbol", sym, args.timeout)
        rc_ref, out_ref = run_lsp("callers", sym, args.timeout)
        worst = max(worst, rc_def, rc_ref)

        if rc_def == 2 or rc_ref == 2:
            print("  !! LANGUAGE SERVER FAILED (exit 2). This result means nothing --")
            print("     it is NOT evidence that the symbol is absent or uncalled.")

        print("\n-- definition --")
        print((out_def.strip() or "  (none)"))

        if not args.no_body and rc_def == 0:
            m = LOC.search(out_def)
            if m:
                print()
                print(excerpt(m.group(1), int(m.group(2)), args.context))

        print("\n-- call sites --")
        print((out_ref.strip() or "  (none)"))

    if worst == 2:
        print("\nNOTE: at least one lookup failed at the server (exit 2). Re-run before "
              "concluding anything about those symbols.", file=sys.stderr)
    return worst


if __name__ == "__main__":
    sys.exit(main())
