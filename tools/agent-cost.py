#!/usr/bin/env python3
"""Where did a session's subagents spend their tool calls?

The figure in CLAUDE.md sat stale for a long time because nobody re-measured it.
This script exists so that number is cheap to refresh and hard to be wrong about.

It reads the subagent transcripts a Claude Code session writes under its task
directory and reports, per tool and per shell-command kind, how many calls were
made and how much output came back. It never prints transcript content, so it is
safe to run from inside a session without flooding the context.

Usage
-----
    tools/agent-cost.py <tasks-dir>            # summary across all agents
    tools/agent-cost.py <tasks-dir> --per-agent
    tools/agent-cost.py <tasks-dir> --top 20

The tasks directory is the one holding `<agentId>.output` files; a session's
Agent tool results name it.

Reading the numbers
-------------------
The cost driver is the NUMBER of calls, not the bytes: every tool call re-sends
the whole accumulated conversation, so call count compounds while a single large
result does not. A high `read/search %` with a low `context-pack`/`lsp-query`
count is the pattern this repo keeps regressing into.
"""
from __future__ import annotations

import argparse
import collections
import glob
import json
import os
import re
import sys

KINDS = [
    ("context-pack", r"context-pack\.py"),
    ("lsp-query", r"lsp-query\.py"),
    ("graphify", r"\bgraphify\b"),
    ("grep/rg", r"(?:^|[|;&]\s*)\s*(?:grep|rg|ag)\b"),
    ("read (sed/cat/head)", r"\b(?:sed|awk|head|tail|cat|wc)\b"),
    ("find/ls", r"\b(?:find|ls)\b"),
    ("dotnet test", r"dotnet test"),
    ("dotnet build", r"dotnet build"),
    ("run the runner", r"dotnet run|al-runner\.dll"),
    ("git", r"\bgit\b"),
    ("gh", r"\bgh\b"),
    ("python", r"\bpython3?\b"),
    ("other", ""),
]
SEARCHY = {"grep/rg", "read (sed/cat/head)", "find/ls"}
NAVY = {"context-pack", "lsp-query", "graphify"}


def size(x) -> int:
    if x is None:
        return 0
    if isinstance(x, str):
        return len(x)
    if isinstance(x, list):
        return sum(size(i) for i in x)
    if isinstance(x, dict):
        return sum(size(v) for v in x.values())
    return len(str(x))


def classify(cmd: str) -> str:
    for name, rx in KINDS:
        if rx and re.search(rx, cmd):
            return name
    return "other"


def scan(path: str):
    tools = collections.Counter()
    kinds = collections.Counter()
    vol = collections.Counter()
    pending: dict[str, str] = {}
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            try:
                rec = json.loads(line)
            except Exception:
                continue
            content = (rec.get("message") or {}).get("content")
            if not isinstance(content, list):
                continue
            for c in content:
                if not isinstance(c, dict):
                    continue
                if c.get("type") == "tool_use":
                    name = c.get("name", "?")
                    tools[name] += 1
                    if name == "Bash":
                        k = classify((c.get("input") or {}).get("command", "") or "")
                        kinds[k] += 1
                        pending[c.get("id")] = k
                elif c.get("type") == "tool_result":
                    k = pending.get(c.get("tool_use_id"))
                    if k:
                        vol[k] += size(c.get("content"))
    return tools, kinds, vol


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("tasks_dir")
    ap.add_argument("--per-agent", action="store_true")
    ap.add_argument("--top", type=int, default=12)
    args = ap.parse_args()

    files = sorted(glob.glob(os.path.join(args.tasks_dir, "a*.output")))
    if not files:
        print(f"no agent transcripts (a*.output) under {args.tasks_dir}", file=sys.stderr)
        return 1

    tot_tools = collections.Counter()
    tot_kinds = collections.Counter()
    tot_vol = collections.Counter()
    per = []
    for p in files:
        t, k, v = scan(p)
        tot_tools.update(t)
        tot_kinds.update(k)
        tot_vol.update(v)
        calls = sum(t.values())
        searchy = sum(k[s] for s in SEARCHY)
        navy = sum(k[s] for s in NAVY)
        per.append((calls, os.path.basename(p)[:-7], searchy, navy))

    print(f"agents: {len(files)}   tool calls: {sum(tot_tools.values())}")
    print(f"\n{'tool':22s} {'calls':>7s}")
    for name, n in tot_tools.most_common(args.top):
        print(f"{name:22s} {n:7d}")

    grand = sum(tot_vol.values()) or 1
    print(f"\n{'bash command kind':24s} {'calls':>7s} {'MB out':>9s} {'% out':>7s} {'avg KB':>8s}")
    for name, _ in KINDS:
        if tot_kinds[name] or tot_vol[name]:
            print(f"{name:24s} {tot_kinds[name]:7d} {tot_vol[name] / 1e6:9.2f} "
                  f"{tot_vol[name] / grand * 100:6.1f}% "
                  f"{tot_vol[name] / max(tot_kinds[name], 1) / 1024:7.1f}")

    searchy = sum(tot_kinds[s] for s in SEARCHY)
    navy = sum(tot_kinds[s] for s in NAVY)
    bash = sum(tot_kinds.values()) or 1
    print(f"\nread/search via shell : {searchy} of {bash} bash calls ({searchy / bash * 100:.0f}%)")
    print(f"code-navigation tools : {navy}"
          + ("   <-- the pattern this repo regresses into" if navy * 20 < searchy else ""))

    if args.per_agent:
        print(f"\n{'agent':24s} {'calls':>7s} {'read/search':>12s} {'nav':>5s}")
        for calls, aid, s, n in sorted(per, reverse=True):
            print(f"{aid[:24]:24s} {calls:7d} {s:12d} {n:5d}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
