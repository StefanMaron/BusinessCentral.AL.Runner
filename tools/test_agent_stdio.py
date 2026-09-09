#!/usr/bin/env python3
"""Every tools/ CLI prints GitHub-sourced text without dying on the console codec (#3589).

`sys.stdout` is built by the interpreter before any of this code runs, from the
console code page or `PYTHONIOENCODING`. On this Windows box that is cp1252 for
both a pipe and a file redirect, so `print()` of a PR body carrying the robot
emoji every agent-authored footer contains raises `UnicodeEncodeError` -- measured
twice in one day, once in `pr-body.py` AFTER the write had already landed (so the
caller saw a crash for a successful edit) and once in `ci-wait.py` while printing
a failing-log excerpt.

Two halves, both needed:
  * a shape guard, so the next tool added here cannot reintroduce it silently;
  * a behavioural check per CLI, in a child interpreter -- the only portable
    lever, since `PYTHONIOENCODING` is read before `main()` exists.

Run: python3 tools/test_agent_stdio.py
"""
from __future__ import annotations

import ast
import importlib.util
import io
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("agent_stdio", os.path.join(HERE, "agent_stdio.py"))
_mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_mod)
ast_stdio = _mod

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


# --------------------------------------------------------------------------
# The helper itself: a no-op on anything it cannot reconfigure, never a raise.
# A helper that throws would take down every test harness that imports a tool.
# --------------------------------------------------------------------------
print("agent_stdio.enable_utf8_stdio is safe on every stream shape")


class _Recorder:
    def __init__(self) -> None:
        self.calls: list[dict] = []

    def reconfigure(self, **kw) -> None:
        self.calls.append(kw)


class _NoReconfigure:
    pass


class _Raiser:
    def reconfigure(self, **kw):
        raise io.UnsupportedOperation("not seekable")


def with_streams(out, err):
    saved = (sys.stdout, sys.stderr)
    sys.stdout, sys.stderr = out, err
    try:
        return ast_stdio.enable_utf8_stdio()
    finally:
        sys.stdout, sys.stderr = saved


rec_out, rec_err = _Recorder(), _Recorder()
with_streams(rec_out, rec_err)
check("reconfigures stdout as UTF-8 with errors=replace",
      rec_out.calls == [{"encoding": "utf-8", "errors": "replace"}], str(rec_out.calls))
check("reconfigures stderr as UTF-8 with errors=replace",
      rec_err.calls == [{"encoding": "utf-8", "errors": "replace"}], str(rec_err.calls))

ok = True
for out, err in ((_NoReconfigure(), _Recorder()), (_Raiser(), _Recorder()),
                 (None, None), (_Recorder(), _Raiser())):
    try:
        with_streams(out, err)
    except Exception as e:  # pragma: no cover - the failure this asserts against
        ok = False
        check("no-op stream shape %s raised" % type(out).__name__, False, repr(e))
check("a stream without reconfigure, one that raises, and None are all no-ops", ok)

second = _Recorder()
with_streams(second, _Recorder())
with_streams(second, _Recorder())
check("calling it twice is harmless", len(second.calls) == 2, str(second.calls))


# --------------------------------------------------------------------------
# Shape guard: every executable tool calls it at module level, before any print.
# --------------------------------------------------------------------------
print("\nevery tools/ CLI calls enable_utf8_stdio() at module level")

# agent_stdio.py is the helper; agent_self_freshness.py is import-only (no
# `__main__` block and no print of its own -- it returns notes for its caller to
# print), so the guard below does not reach either, by construction rather than
# by allowlist.
SELF = ("agent_stdio.py",)


def has_main_block(tree: ast.Module) -> bool:
    for node in tree.body:
        if (isinstance(node, ast.If) and isinstance(node.test, ast.Compare)
                and isinstance(node.test.left, ast.Name) and node.test.left.id == "__name__"):
            return True
    return False


def calls_enable(tree: ast.Module) -> bool:
    """A module-level call to agent_stdio.enable_utf8_stdio(), guarded or not."""
    for node in tree.body:
        for sub in ast.walk(node):
            if not isinstance(sub, ast.Call):
                continue
            f = sub.func
            if isinstance(f, ast.Attribute) and f.attr == "enable_utf8_stdio":
                return True
    return False


scanned = 0
for fn in sorted(os.listdir(HERE)):
    if not fn.endswith(".py") or fn.startswith("test_") or fn in SELF:
        continue
    path = os.path.join(HERE, fn)
    with open(path, encoding="utf-8") as fh:
        tree = ast.parse(fh.read(), filename=path)
    if not has_main_block(tree):
        continue
    scanned += 1
    check(f"{fn} calls enable_utf8_stdio() at module level", calls_enable(tree))
check("the guard actually scanned the tools (not zero files)", scanned >= 8, f"scanned={scanned}")


# --------------------------------------------------------------------------
# Behavioural: each CLI, imported under a cp1252 stdout, prints U+1F916 and
# U+2014 as UTF-8 bytes instead of raising. This is line 1269 of ci-wait.py
# (`print("\n".join(tail))` over a GitHub log) and line 749/753 of pr-body.py
# (`print(d)` over a PR body), reduced to the one thing that decides it.
# --------------------------------------------------------------------------
print("\nunder PYTHONIOENCODING=cp1252, importing a tool makes print() survive")

CHILD = r'''
import importlib.util, sys
pre = (sys.stdout.encoding or "").lower().replace("-", "")
if pre != "cp1252":
    # PYTHONIOENCODING is honoured on every platform, so this is a broken test
    # environment, never a reason to skip -- a skip here would make the whole
    # check unable to fail.
    sys.stderr.write("PRECONDITION-FAIL: stdout encoding is %s\n" % pre)
    raise SystemExit(3)
spec = importlib.util.spec_from_file_location("tool_under_test", sys.argv[1])
mod = importlib.util.module_from_spec(spec)
# Registered before execution: a dataclass defined in a module absent from
# sys.modules raises in dataclasses' own type resolution (preflight.py,
# comment-density.py), which would be a harness artifact, not a finding.
sys.modules["tool_under_test"] = mod
spec.loader.exec_module(mod)
print("robot \U0001f916 dash \u2014 end")
sys.stdout.flush()
'''
assert CHILD.isascii(), "CHILD must survive an ASCII argv"

ROBOT = b"\xf0\x9f\xa4\x96"
DASH = b"\xe2\x80\x94"
CP1252_DASH = b"\x97"

TOOLS = ["pr-body.py", "ci-wait.py", "preflight.py", "corpus-pin.py",
         "corpus-pass-count.py", "corpus-pin-advance.py", "context-pack.py",
         "lsp-query.py", "agent_scratchpad.py", "agent-cost.py", "comment-density.py"]

env = dict(os.environ, PYTHONIOENCODING="cp1252", PYTHONUTF8="0")
for tool in TOOLS:
    child = subprocess.run([sys.executable, "-c", CHILD, os.path.join(HERE, tool)],
                           capture_output=True, env=env)
    detail = ascii(child.stdout[-200:]) + " " + ascii(child.stderr[-400:])
    check(f"{tool}: exit 0 under a cp1252 stdout", child.returncode == 0, detail)
    check(f"{tool}: the robot emoji reaches stdout as UTF-8",
          ROBOT in child.stdout, detail)
    check(f"{tool}: the em dash reaches stdout as UTF-8, not as cp1252 0x97",
          DASH in child.stdout and CP1252_DASH not in child.stdout, detail)

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
