#!/usr/bin/env python3
"""Execute `ci-verdicts.md`'s source-echo filter against the defect it claims to catch.

The rule's claim (`.claude/rules/ci-verdicts.md`, "a job log echoes the run:
block as source"):

    An Actions log prints the `run:` block as SOURCE before any stdout, so a
    `grep -c` over a job log counts INTENT, not execution. Filter the escape
    out, or match on output the script cannot contain.

The pinning bar is not "the command parses". It is: reproduce a log in which
the echoed source and the real output differ, then confirm the filtered read
gets the right answer and the unfiltered one does not.

Why this is worth a test rather than a `Recipe-unpinned:` marker -- the input is
a byte string, so nothing here needs a network, a job id, or a live run.
"""
import subprocess
import sys

ESC = "\x1b[36;1m"
failures: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'ok  ' if ok else 'FAIL'} {name}")
    if not ok:
        if detail:
            print(f"      {detail}")
        failures.append(name)


# A job log in the shape the rule describes: GitHub echoes the `run:` block,
# each echoed line carrying the cyan-bold escape, and only THEN the real stdout.
# Here the script mentions `attempt` three times in source and the loop aborts
# before running, so real execution produced zero `attempt` lines.
LOG = (
    f"2026-01-01T00:00:00.0000000Z {ESC}for i in 1 2 3; do\n"
    f"2026-01-01T00:00:00.0000001Z {ESC}  echo \"attempt $i of 3\"\n"
    f"2026-01-01T00:00:00.0000002Z {ESC}done\n"
    f"2026-01-01T00:00:00.0000003Z {ESC}echo \"attempt bookkeeping done\"\n"
    "2026-01-01T00:00:01.0000000Z ##[error]the loop never started\n"
)


def grep_count(text: str, pattern: str, filter_escape: bool) -> int:
    """The rule's recipe, run through a real shell rather than reimplemented."""
    pipeline = "command grep -v $'\\x1b\\[36;1m' | " if filter_escape else ""
    pipeline += f"command grep -c {pattern} || true"
    out = subprocess.run(["bash", "-c", pipeline], input=text,
                         capture_output=True, text=True).stdout.strip()
    return int(out or 0)


unfiltered = grep_count(LOG, "attempt", filter_escape=False)
filtered = grep_count(LOG, "attempt", filter_escape=True)

# The defect: the naive read reports execution that never happened.
# `grep -c` counts matching LINES: two of the four echoed lines carry the word.
check("the unfiltered grep counts the echoed SOURCE, not execution",
      unfiltered == 2, f"expected 2 echoed matching lines, got {unfiltered}")
check("...which is the false positive the rule exists to stop",
      unfiltered > 0)

# The remedy: filtering the escape leaves only real stdout, which has none.
check("the filtered grep reports the truth: zero attempts ran",
      filtered == 0, f"expected 0, got {filtered}")

# The two forms must DISAGREE here, or the recipe would be pointless.
check("the two forms disagree on this log, which is what makes the recipe worth stating",
      unfiltered != filtered, f"unfiltered={unfiltered} filtered={filtered}")

# The discriminating half: the filter must not eat real output. A log whose
# stdout genuinely contains the word must still count it.
REAL = (
    f"2026-01-01T00:00:00.0000000Z {ESC}echo \"attempt 1 of 1\"\n"
    "2026-01-01T00:00:01.0000000Z attempt 1 of 1\n"
)
check("the filter keeps genuine stdout rather than removing every match",
      grep_count(REAL, "attempt", filter_escape=True) == 1,
      f"got {grep_count(REAL, 'attempt', filter_escape=True)}")

# And the rule must still tell the reader to do this.
rule = open(".claude/rules/ci-verdicts.md", encoding="utf-8").read()
check("the rule still carries the escape-filtering recipe",
      "36;1m" in rule)

print()
if failures:
    print(f"FAILED: {len(failures)} check(s): {failures}")
    sys.exit(1)
print("all source-echo recipe checks passed")
