#!/usr/bin/env python3
"""Every ``` fence in the instruction files opens and closes.

A malformed close silently swallows prose. Measured (#4509): one commit welded
text onto a closing fence --

    ``` Treat both as a scale, not a contract,

-- and under CommonMark a closing fence carries only backticks and whitespace,
so that closes nothing. The block ran on for **76 more lines**, rendering four
bolded instructions as code, including "Build first; do not pass `--no-build`
here" and "Never report suite results in a PR body that you did not actually
run". A reviewer found it by rendering the file through GitHub's own API.

## Why this is worth a guard rather than care

The failure is invisible in every way an agent normally checks. The file parses,
every other guard passes (`test_agent_prescribed_commands.py` matches command
TEXT by regex, so it reads the swallowed commands happily either way), CI is
green, and `git diff` shows a one-line edit that looks correct in a terminal.
Only a renderer shows it, and no agent renders these files.

It cost one review round. The scan is two lines.

## Scope

`.claude/**/*.md` and `docs/*.md` -- the files agents are instructed from. A
malformed fence in a README is cosmetic; one in an agent definition or a rule
hides an instruction from the agent that must follow it.
"""
import glob
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

passes, failures = 0, []


def check(name, ok, detail=""):
    global passes
    if ok:
        passes += 1
        print(f"ok   - {name}")
    else:
        failures.append(name)
        print(f"FAIL - {name}: {detail}")


def problems(path):
    """(line, why) for each fence defect, or [] when the file is balanced.

    Fence LENGTH is tracked, per CommonMark: a closing fence must be at least as
    long as the one that opened the block, so a ``` inside a ```` block is
    content rather than a close. Without that, documenting a fence -- wrapping
    an example in four backticks -- reds this guard, and a guard that reds on a
    legitimate shape is one people disable. No such block exists in the scanned
    set today, which is exactly when it is cheap to get right.
    """
    found, open_at, open_len = [], 0, 0
    with open(path, encoding="utf-8", errors="replace") as fh:
        for n, line in enumerate(fh, 1):
            stripped = line.strip()
            if not stripped.startswith("```"):
                continue
            run = len(stripped) - len(stripped.lstrip("`"))
            trailing = stripped.lstrip("`").strip()
            if open_at:
                # Too short to close this block, or carrying an info string:
                # either way it is content, not a close.
                if run < open_len or trailing:
                    # Only a fence long enough to close is worth reporting --
                    # a shorter one is ordinary content inside the block.
                    if run >= open_len:
                        found.append(
                            (n, f"closing fence carries text: {stripped[:60]!r}"))
                    continue
                open_at, open_len = 0, 0
            else:
                open_at, open_len = n, run
    if open_at:
        found.append((open_at, "fence opened here is never closed"))
    return found


def main():
    files = sorted(glob.glob(os.path.join(ROOT, ".claude", "**", "*.md"),
                             recursive=True))
    files += sorted(glob.glob(os.path.join(ROOT, "docs", "*.md")))

    # Without this the glob could silently match nothing -- a pass over an empty
    # set, which is the shape `guards-need-a-third-state.md` refuses.
    check("there are instruction files to scan", len(files) > 20,
          f"found only {len(files)}; the glob may have moved")
    if len(files) <= 20:
        print(f"\nFAILED: {len(failures)} check(s): {failures}")
        return 1

    for path in files:
        rel = os.path.relpath(path, ROOT)
        found = problems(path)
        check(f"fences balanced: {rel}", not found,
              "; ".join(f"line {n}: {why}" for n, why in found))

    print("")
    if failures:
        print(f"FAILED: {len(failures)} check(s): {failures}")
        return 1
    print(f"PASSED: {passes} check(s) over {len(files)} file(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
