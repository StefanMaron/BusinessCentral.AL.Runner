#!/usr/bin/env python3
"""check-open-prs-before-claiming.md must discriminate by ACCOUNT before it rates a signal (#3275).

The rule used to open on one premise -- "every loop pushes under one GitHub
account" -- and rated the assignee worthless on it. That is true of two loops on
one account and false across accounts, where the assignee is the whole answer and
`branch-and-pr.md` already says stop. Measured 2026-09-22: five remote
`agent/fbk-*/...` branches and three `agent: fbk-*` labels, from a second
agent-running account.

What is pinned is the POPULATION of the rule's claim table, not any sentence's
wording: a substring test passes for a rename and for the name in a comment, so
this guard reads the markdown table and asserts over its parsed rows.

Three properties, each of which a revert to the single-account premise breaks:

  1. BOTH cases are rated -- one row for two loops on the SAME account, one for a
     loop on a DIFFERENT account. A single-account rule has only the first.
  2. The two rows DISAGREE about the assignee. A table naming both cases and
     giving them one verdict has not discriminated; it has just added a word.
  3. The discriminator is stated BEFORE the per-signal ratings, because a reader
     who reaches the ratings without knowing which case they are in applies the
     wrong one -- which is the near-miss in #3275.

Plus the cross-reference that makes the rule stop contradicting its sister:
`branch-and-pr.md` owns the assignee boundary and this rule must defer to it
rather than argue against it.
"""
import re
import sys

failures: list[str] = []
ran: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    ran.append(name)
    print(f"  {'ok  ' if ok else 'FAIL'} {name}")
    if not ok:
        if detail:
            print(f"      {detail}")
        failures.append(name)


RULE = ".claude/rules/check-open-prs-before-claiming.md"
text = open(RULE, encoding="utf-8").read()


def table_rows(body: str) -> list[list[str]]:
    """Every markdown table row in `body`, as stripped cell lists.

    Header and separator rows are dropped: a separator is all dashes/colons, and
    the header is whatever precedes it in that table.
    """
    rows: list[list[str]] = []
    pending: list[str] | None = None        # a row held back until we know if a separator follows

    def flush() -> None:
        """Emit the held row. A row is held only to see whether a SEPARATOR comes
        next, which would make it a header; anything else -- another row, a blank
        line, the end of the file -- means it was data. Dropping it on a non-table
        line instead of flushing loses the LAST row of every table, which is
        exactly the row a new case gets appended as (found by this guard's own
        first revision, #3275)."""
        nonlocal pending
        if pending is not None:
            rows.append(pending)
            pending = None

    for line in body.splitlines():
        s = line.strip()
        if not (s.startswith("|") and s.endswith("|")):
            flush()
            continue
        cells = [c.strip() for c in s.strip("|").split("|")]
        if all(re.fullmatch(r":?-{2,}:?", c) for c in cells if c):
            pending = None          # separator: the row before it was the header, so drop it
            continue
        flush()
        pending = cells
    flush()
    return rows


ROWS = table_rows(text)
check("the rule still contains markdown tables to read", len(ROWS) > 0,
      f"parsed {len(ROWS)} data rows")

# --- property 1: both account cases are RATED -------------------------------
#
# Keyed on the distinction rather than on one spelling: a row is about the
# same-account case if it says the accounts are the same, and about the
# cross-account case if it says they differ. Either spelling of "account" counts,
# so a rewrite is free to rephrase as long as it still rates both.
SAME = re.compile(r"\bsame\b[^|]{0,40}\baccount\b|\bone\b[^|]{0,20}\baccount\b"
                  r"|\bown\b[^|]{0,20}\baccount\b", re.I)
# The adjective must modify the ACCOUNT, not a loop that happens to precede one:
# "another loop on the **same** account" is the SAME-account row and must not match
# here. So nothing between the adjective and `account` may itself be `same`/`one`/
# `account` -- caught by this guard against its own rule text, first revision (#3275).
DIFF = re.compile(r"\b(different|another|other|second|cross[- ])\b"
                  r"(?:(?!\b(?:same|one|own|account)\b)[^|])"
                  r"{0,40}\baccount\b|\bcross-account\b", re.I)


def rows_matching(pat: re.Pattern) -> list[list[str]]:
    return [r for r in ROWS if any(pat.search(c) for c in r)]


same_rows = rows_matching(SAME)
diff_rows = rows_matching(DIFF)

check("a table row rates the case where the other claimant is on the SAME account",
      len(same_rows) > 0,
      "no row distinguishes a same-account claimant; the rule rates one case only")
check("a table row rates the case where the other claimant is on a DIFFERENT account",
      len(diff_rows) > 0,
      "no row distinguishes a cross-account claimant -- this is the single-account premise")

# --- property 2: the two rows DISAGREE about the assignee -------------------
#
# Naming both cases and giving them one verdict is not discrimination. The
# same-account row must say the assignee settles NOTHING; the cross-account row
# must say it settles the question.
NOTHING = re.compile(r"\bnothing\b|\bcannot\b|\bcan't\b|\bno(t| )\s*answer|\buseless\b", re.I)
EVERYTHING = re.compile(r"\beverything\b|\bauthoritative\b|\bdecides?\b|\bsettles?\b"
                        r"|\bexactly who\b|\bthe answer\b|\breal boundary\b", re.I)

same_says_nothing = [r for r in same_rows if any(NOTHING.search(c) for c in r)]
diff_says_answer = [r for r in diff_rows if any(EVERYTHING.search(c) for c in r)]

check("the same-account row says the assignee answers nothing",
      len(same_says_nothing) > 0,
      f"same-account rows found: {same_rows}")
check("the cross-account row says the assignee IS the answer",
      len(diff_says_answer) > 0,
      f"cross-account rows found: {diff_rows}")

# The rows must be DISTINCT rows: one row carrying both verdicts would satisfy
# the two checks above while discriminating nothing.
check("the two verdicts sit on different rows",
      any(a != b for a in same_says_nothing for b in diff_says_answer),
      "one row matched both cases; that rates a single case in two words")

# --- property 3: the discriminator comes FIRST ------------------------------
#
# The near-miss in #3275 was an agent that read the ratings without knowing which
# case it was in. So "which account is the other claimant on" must be asked
# before the first per-signal rating is offered.
ASK = re.compile(r"(read|check|ask|establish|determine)[^.\n]{0,80}\b(account|login)\b", re.I)
ask_at = min((m.start() for m in ASK.finditer(text)), default=None)

SIGNAL_TABLE = [r for r in ROWS if any(re.search(r"\bassignee\b", c, re.I) for c in r)]
check("a per-signal table still rates the assignee", len(SIGNAL_TABLE) > 0)

first_rating = text.lower().find("| assignee")
if first_rating == -1:
    first_rating = text.lower().find("assignee |")
check("the rule tells the reader to establish WHICH ACCOUNT before rating signals",
      ask_at is not None and first_rating != -1 and ask_at < first_rating,
      f"instruction at {ask_at}, first signal rating at {first_rating}")

# --- the sister rule it must stop contradicting -----------------------------
check("the rule defers to branch-and-pr.md on the assignee boundary",
      "branch-and-pr.md" in text and
      bool(re.search(r"branch-and-pr\.md[^\n]{0,200}", text)),
      "no reference to the rule that owns the assignee boundary")

# The branch prefix is the same mechanism orchestrating-a-session already uses,
# and it is stronger than the label because another loop cannot rewrite it.
check("the rule names the BRANCH PREFIX as a claim signal",
      re.search(r"branch\s+prefix", text, re.I) is not None,
      "the prefix is the cross-account discriminator that cannot be relabelled")

print()
if failures:
    print(f"FAILED: {len(failures)} check(s): {failures}")
    sys.exit(1)
print(f"PASSED: {len(ran)} check(s)")
