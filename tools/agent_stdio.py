#!/usr/bin/env python3
"""Make stdout/stderr UTF-8 so a tool cannot die while PRINTING (#3589).

`sys.stdout` is built by the interpreter before any tool code runs, from the
console code page or `PYTHONIOENCODING`, and no `encoding=` argument inside a
tool reaches it. On this repository's Windows boxes that codec is cp1252 for
both a pipe and a file redirect, so:

  * `print()` of any text carrying a character cp1252 cannot encode raises
    `UnicodeEncodeError`. Measured twice on 2026-09-09: `pr-body.py` died on
    the robot emoji in a PR body's Claude Code footer AFTER its write had
    already landed and verified -- so the caller saw a crash for a successful
    edit -- and `ci-wait.py` died printing a failing-log excerpt.
  * a character cp1252 CAN encode goes out as a cp1252 byte: an em dash as the
    single byte 0x97, which a caller reading the tool's output as UTF-8 (the
    normal shape for `subprocess.run(..., encoding="utf-8")`) cannot decode.

Both are fixed by the same reconfiguration, and both directions are asserted in
`tools/test_agent_stdio.py`. `errors="replace"` is the second half: after this,
an unencodable character is a `?`, never an exception -- a tool that has done
the work must never lose its answer on the way to the terminal.

This is the display counterpart to #3434's fix, which named UTF-8 on the
subprocess decodes and file reads/writes (`tools/test_text_encoding.py` guards
that shape). It is deliberately its own module so every tool gets the same one
line, imported the same way the `agent_self_freshness` guard is.
"""
from __future__ import annotations

import sys


def enable_utf8_stdio() -> None:
    """Reconfigure sys.stdout/sys.stderr to UTF-8 with errors="replace".

    Never raises. A stream can legitimately be `None` (pythonw), lack
    `reconfigure` (a `StringIO` installed by a test harness, or any non-
    `TextIOWrapper` substitute), or refuse it -- and a helper that threw in any
    of those states would take down the harnesses that import these tools.
    Idempotent, so calling it from an imported module and again from a caller
    costs nothing.
    """
    for name in ("stdout", "stderr"):
        stream = getattr(sys, name, None)
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            # A stream that will not be reconfigured is left as it was: the
            # tool then behaves exactly as it did before this module existed,
            # which is worse than UTF-8 but is never worse than not running.
            pass
