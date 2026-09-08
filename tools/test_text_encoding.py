#!/usr/bin/env python3
"""Every subprocess text decode under tools/ names UTF-8 explicitly (#3434).

`text=True` with no `encoding=` decodes with the locale codec. On Windows that
is cp1252, so `gh`'s JSON and a BC CI log come back mangled -- a `pr-body.py`
anchor carrying an em dash then matches 0 times, and a log grep reads as "no
matches". The behavioural proof lives in test_pr_body.py; this is the shape
guard, so the next tool added here cannot reintroduce it silently.

Run: python3 tools/test_text_encoding.py [dir]
"""
from __future__ import annotations

import ast
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = sys.argv[1] if len(sys.argv) > 1 else HERE

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def text_mode(call: ast.Call) -> bool:
    for kw in call.keywords:
        if kw.arg in ("text", "universal_newlines"):
            return not (isinstance(kw.value, ast.Constant) and kw.value.value is False)
    return False


def named(call: ast.Call) -> str:
    f = call.func
    return f"{getattr(f.value, 'id', '?')}.{f.attr}" if isinstance(f, ast.Attribute) else "?"


print("subprocess text decodes under tools/ are UTF-8, not the locale codec")
scanned = 0
for fn in sorted(os.listdir(ROOT)):
    # Test files drive real shell scripts whose output is ASCII by construction;
    # the rule here is about the tools an agent's verdict depends on.
    if not fn.endswith(".py") or fn.startswith("test_"):
        continue
    path = os.path.join(ROOT, fn)
    with open(path, encoding="utf-8") as fh:
        tree = ast.parse(fh.read(), filename=path)
    scanned += 1
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call) or named(node) not in (
                "subprocess.run", "subprocess.Popen", "subprocess.check_output"):
            continue
        if not text_mode(node):
            continue
        kwargs = {kw.arg for kw in node.keywords}
        check(f"{fn}:{node.lineno} {named(node)} names an encoding",
              "encoding" in kwargs, sorted(k for k in kwargs if k))

check("something was scanned", scanned >= 5, f"{scanned} file(s)")

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
