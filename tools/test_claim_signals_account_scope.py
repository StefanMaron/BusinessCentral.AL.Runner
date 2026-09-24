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

Property 2 reads the VERDICT CELL, located by its column header, not the row. A
row-wide search passes an inverted verdict whenever another column carries the
opposite vocabulary, and the action column legitimately does -- "nothing below
waives it" is correct prose that satisfied a check about the word "nothing".
Both directions are pinned, because a verdict carrying BOTH vocabularies has
taken no side and reading only the expected one lets that through.

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


class Row:
    """One data row, WITH the header it sits under.

    The header is kept rather than discarded because a verdict check has to read
    the verdict CELL: a row-wide search is satisfied by the right word in the
    wrong column, and the action column legitimately contains both vocabularies
    ("nothing below waives it" is the correct thing for it to say). Found by a
    third mutation that inverted the same-account verdict in place and left the
    guard GREEN on the very check that names it (#3275, review round 1).
    """

    def __init__(self, header: list[str], cells: list[str]) -> None:
        self.header = header
        self.cells = cells

    def cell_under(self, pat: re.Pattern) -> str | None:
        """The cell whose HEADER matches `pat` -- located by name, not by index,
        so inserting a column cannot silently move the check onto another one.
        None when this row has no such column, which a caller must not read as
        an empty cell: a missing column is unmeasured, not a failed match."""
        for i, h in enumerate(self.header):
            if pat.search(h):
                return self.cells[i] if i < len(self.cells) else None
        return None

    def any_cell(self, pat: re.Pattern) -> bool:
        return any(pat.search(c) for c in self.cells)

    def __repr__(self) -> str:
        return repr(self.cells)


def table_rows(body: str) -> list[Row]:
    """Every markdown table data row in `body`, each carrying its own header."""
    rows: list[Row] = []
    pending: list[str] | None = None        # a row held back until we know if a separator follows
    header: list[str] = []

    def flush() -> None:
        """Emit the held row. A row is held only to see whether a SEPARATOR comes
        next, which would make it a header; anything else -- another row, a blank
        line, the end of the file -- means it was data. Dropping it on a non-table
        line instead of flushing loses the LAST row of every table, which is
        exactly the row a new case gets appended as (found by this guard's own
        first revision, #3275)."""
        nonlocal pending
        if pending is not None:
            rows.append(Row(header, pending))
            pending = None

    for line in body.splitlines():
        s = line.strip()
        if not (s.startswith("|") and s.endswith("|")):
            flush()
            header = []
            continue
        cells = [c.strip() for c in s.strip("|").split("|")]
        if all(re.fullmatch(r":?-{2,}:?", c) for c in cells if c):
            header = pending or []  # separator: the row before it WAS the header
            pending = None
            continue
        flush()
        pending = cells
    flush()
    return rows


ROWS = table_rows(text)

# The column each verdict check must read. Both tables in this rule spell it as a
# question about what a signal TELLS you, which is what distinguishes it from the
# action column beside it.
VERDICT_COL = re.compile(r"what the assignee tells you|what it tells you", re.I)
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


def rows_matching(pat: re.Pattern) -> list[Row]:
    """Rows IDENTIFIED by a match anywhere in them. Row-wide is right here --
    which case a row is about may be written in any column -- and wrong for the
    verdict checks below, which read one named cell."""
    return [r for r in ROWS if r.any_cell(pat)]


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

def verdict_says(rows: list[Row], pat: re.Pattern) -> tuple[list[Row], list[Row]]:
    """Split `rows` into those whose VERDICT cell matches, and those with no
    verdict column at all. The second list is the third state: a row missing the
    column was never measured, and reporting it as a failed match would send a
    reader to reword a cell that is not there (`guards-need-a-third-state.md`)."""
    hit, unmeasured = [], []
    for r in rows:
        cell = r.cell_under(VERDICT_COL)
        if cell is None:
            unmeasured.append(r)
        elif pat.search(cell):
            hit.append(r)
    return hit, unmeasured


# Read the VERDICT CELL, not the row. A row-wide search passes an inverted
# verdict whenever any other column happens to carry the opposite vocabulary --
# and the action column legitimately does: "nothing below waives it" is correct
# prose that satisfied a check about the word "nothing" (#3275, review round 1).
same_says_nothing, same_unmeasured = verdict_says(same_rows, NOTHING)
diff_says_answer, diff_unmeasured = verdict_says(diff_rows, EVERYTHING)

check("every account-case row has a verdict column to read",
      not (same_unmeasured or diff_unmeasured),
      f"rows with no verdict column: {same_unmeasured + diff_unmeasured}")

check("the same-account row says the assignee answers nothing",
      len(same_says_nothing) > 0,
      f"same-account verdict cells: "
      f"{[r.cell_under(VERDICT_COL) for r in same_rows]}")
check("the cross-account row says the assignee IS the answer",
      len(diff_says_answer) > 0,
      f"cross-account verdict cells: "
      f"{[r.cell_under(VERDICT_COL) for r in diff_rows]}")

# Both directions, not just one (`tdd.md`: pin both sides of a boundary). A row
# whose verdict carries BOTH vocabularies has not taken a side, and reading only
# the side each row is supposed to assert would let that through.
same_also_everything, _ = verdict_says(same_rows, EVERYTHING)
diff_also_nothing, _ = verdict_says(diff_rows, NOTHING)
check("the same-account verdict does NOT also claim the assignee is decisive",
      not same_also_everything,
      f"same-account verdict cells: "
      f"{[r.cell_under(VERDICT_COL) for r in same_also_everything]}")
check("the cross-account verdict does NOT also claim the assignee answers nothing",
      not diff_also_nothing,
      f"cross-account verdict cells: "
      f"{[r.cell_under(VERDICT_COL) for r in diff_also_nothing]}")

# The rows must be DISTINCT rows: one row carrying both verdicts would satisfy
# the two checks above while discriminating nothing.
check("the two verdicts sit on different rows",
      any(a is not b for a in same_says_nothing for b in diff_says_answer),
      "one row matched both cases; that rates a single case in two words")

# --- property 2b: the ACTION column, which is what an agent acts on ---------
#
# The verdict checks above pin the DIAGNOSIS ("what the assignee tells you").
# An agent does not act on a diagnosis -- it acts on the instruction beside it,
# and the two are separate observables of one row. Inverting the action cell of
# the cross-account row to "carry on and claim it anyway" while leaving the
# verdict cell honest passed all 13 checks (#4474): the rule then told an agent
# to claim another account's issue and nothing went red.
#
# `one-red-proves-coverage-not-which-observable`: covering a row is not covering
# every cell that a reader acts on.
ACTION_COL = re.compile(r"what to do|what you do|action", re.I)

# STOP vocabulary must not be satisfied by the same-account row's legitimate
# "read on", so match the instruction rather than any cautious-sounding word.
# `halt` and `leave them` and `not yours` are here so this and OPENS_STOP accept
# the same vocabulary: a cell reading "**halt** -- branch-and-pr.md owns this
# boundary" satisfied the front-window check and failed this one, which is an
# inconsistency between two checks rather than a property of the cell (#4509).
STOP = re.compile(r"\bstop\b|\bhalt\b|\bdo not\b|\bdon't\b|\bnever\b"
                  r"|\bleave it\b|\bleave them\b|\bskip\b|\bhands? off\b"
                  r"|\bnot yours\b", re.I)
# What the same-account row says instead: the open-PR lookup resolves it.
# `below` was here and is removed (#4509): it adds no discrimination the other
# three lack, and is the only alternative satisfiable by a cross-reference to
# anything. "claim it immediately without further checks; see the note below"
# passed both same-account action checks -- no STOP match, and `below` alone
# satisfying PROCEED -- with the open-PR routing that is the row's whole purpose
# deleted.
PROCEED = re.compile(r"\bread on\b|\bopen[- ]PR\b|\bresolves? it\b", re.I)


def action_says(rows: list[Row], pat: re.Pattern) -> tuple[list[Row], list[Row]]:
    """Same shape as `verdict_says`, on the action column. A row with no action
    column is unmeasured, not failed -- a reader sent to reword a cell that does
    not exist cannot comply (`guards-need-a-third-state.md`)."""
    hit, unmeasured = [], []
    for r in rows:
        cell = r.cell_under(ACTION_COL)
        if cell is None:
            unmeasured.append(r)
        elif pat.search(cell):
            hit.append(r)
    return hit, unmeasured


diff_says_stop, diff_action_unmeasured = action_says(diff_rows, STOP)
same_says_proceed, same_action_unmeasured = action_says(same_rows, PROCEED)

check("every account-case row has an action column to read",
      not (same_action_unmeasured or diff_action_unmeasured),
      f"rows with no action column: "
      f"{same_action_unmeasured + diff_action_unmeasured}")
check("the cross-account row INSTRUCTS the reader to stop",
      len(diff_says_stop) > 0,
      f"cross-account action cells: "
      f"{[r.cell_under(ACTION_COL) for r in diff_rows]}")
check("the same-account row does NOT instruct the reader to stop",
      not action_says(same_rows, STOP)[0],
      f"same-account action cells: "
      f"{[r.cell_under(ACTION_COL) for r in same_rows]}")
check("the same-account row sends the reader on to the open-PR check",
      len(same_says_proceed) > 0,
      f"same-account action cells: "
      f"{[r.cell_under(ACTION_COL) for r in same_rows]}")

# The MIRROR of the STOP check, and the reason the verdict column has one: STOP
# matches a vocabulary, and the same vocabulary appears in instructions meaning
# the opposite. Each of these passed all 19 checks (#4509), every one telling an
# agent to claim another account's issue:
#
#   "**never mind** the boundary -- claim it and open your PR"     (never)
#   "**do not** hesitate -- claim it, the other account is not..."  (do not)
#   "**skip** the boundary check and claim it"                     (skip)
#   "**never** let branch-and-pr.md's boundary stop you; claim it"  (never, stop)
#   "**stop** if you like, but carry on and claim it anyway"        (stop)
#
# The last is the both-vocabularies shape the verdict column already guards. A
# longer STOP pattern cannot fix this -- what discriminates is whether the cell
# ALSO tells the reader to claim, so ask that directly.
# TWO checks, because one vocabulary matcher cannot close a class of paraphrase.
#
# STRUCTURE first: the cross-account cell must OPEN with a stop instruction.
# That is what catches an instruction phrased without the word `claim` at all --
# "take it over", "pick it up", "assign it to yourself", "the issue is free" --
# ten of which passed every check in an earlier revision (#4509). A vocabulary
# list cannot enumerate paraphrase; requiring the cell to begin by saying stop
# does not have to.
#
# VOCABULARY second, for the cells that open with a stop word and then reverse
# it: "**stop** if you like, but carry on and claim it anyway", "**do not** stop
# at branch-and-pr.md; take it". The structural check passes those, so CLAIM is
# what settles them.
#
# CLAIM had to be got wrong three times to arrive here, each in the same
# direction, and the history is the useful part:
#   * `\bclaim\b` fires on the NOUN -- "another account's claim".
#   * plus determiner lookbehinds -- still fires on "this/their/each/no claim".
#     Determiners are an open set.
#   * the verb phrase enumerated by OBJECT (`claim it|them|the issue|...`) --
#     drops eight harmful forms the previous one caught: "you may claim the
#     ticket", "feel free to claim", "claim away", "claim whichever you like".
#     Object noun phrases are an open set too, which is the same lesson one slot
#     along.
# What works is excluding the noun READINGS (a short, closed set of following
# words) rather than enumerating the verb's objects.
#
# THE RESIDUAL, stated once for all three checks rather than per check, because
# stating it per check is how it kept being understated (#4509, rounds 5-7).
#
# Three checks, each with its OWN open set, and none of them closable:
#
#   OPENS_STOP         a position: is a stop instruction near the front
#   CLAIM              a vocabulary: does the cell also say take it
#   NEGATED_DEFERENCE  a vocabulary: does it negate a deference verb
#
# A cross-account cell evades all three if it carries no take-it instruction from
# CLAIM's list and negates no verb from NEGATED_DEFERENCE's -- and synonym sets
# have no last member, so such cells exist by construction. The OPENING is
# independent: every KNOWN_UNCAUGHT cell below opens with a perfectly good stop
# word, which is why OPENS_STOP passes them. An earlier version of this sentence
# said "opens with no stop word" and was refuted by the three cells twenty lines
# beneath it (#4509, rev28). Seven rounds of review found five distinct families of
# them; each fix caught its family and none closed the class.
#
# What this guard is FOR, then: a rule edit that reverses the cross-account
# instruction in any of the ways anyone has yet written down reds here, and
# tools/test_claim_guard_corpus.py holds every such wording so a later narrowing
# cannot silently drop one. It is a ratchet over known bypasses, not a proof.
# Treat a green run as "no known bypass", never as "the row is safe".
# A stop instruction near the FRONT of the cell, not at character zero. Anchoring
# at zero false-reds four honest openings, measured: a leading "Per
# `branch-and-pr.md`, **stop**", a parenthetical, the sister-rule name first
# ("`branch-and-pr.md` says stop"), and `halt` as a synonym. A guard that blocks
# an honest reword gets routed around, which is the failure this suite's control
# arm exists to prevent.
#
# A window rather than "the first clause" because the cell CONTAINS `.` -- the
# sister rule is a `.md` filename -- so a `[^.;]` class stops at the wrong place.
# Reversal after the stop ("**stop** if you like, but carry on") is CLAIM's job,
# not this check's.
# 100, pinned in both directions by two corpus cells rather than chosen (#4509,
# rev25 flagged it as a free parameter nothing measured): an honest cell whose
# stop sits at char 91 after a leading subordinate clause must stay green, and a
# harmful cell burying its stop at char 122 must red.
#
# What this check tests is a POSITION, and harmfulness is a property of content
# (#4509, rev26). A cell whose stop sits INSIDE the window and then negates it --
# "so do not treat the assignee as binding" at char 80 -- satisfies this check
# and is as harmful as the buried one. Moving the number cannot fix that: one
# such bypass puts its stop at char 88, before the honest control's 91, so any
# window catching it false-reds the cell this window exists to protect.
#
# NEGATED_DEFERENCE below is what reads the content. This check still earns its
# place for the present-but-buried shape, which that key does not see -- but it
# is one of two, not the only thing CLAIM misses.
OPENS_STOP = re.compile(
    r"^.{0,100}?\b(?:stop|halt|do not|don't|never|leave it|leave them"
    r"|skip|hands off|not yours)\b", re.I | re.S)

CLAIM = re.compile(
    # The NOUN readings, excluded by what precedes or follows. A determiner
    # before `claim` makes it the noun and never the verb, so the lookbehind set
    # is safe here even though enumerating determiners failed as a positive test.
    r"(?<!whose )(?<!'s )(?<!\bthe )(?<!\ba )(?<!\bthis )(?<!\btheir )"
    r"(?<!\bno )(?<!\beach )(?<!\bthat )"
    # A NEGATED claim verb is the honest instruction: "never claim across
    # accounts" forbids exactly what "claim across accounts freely" commands,
    # and the two differ only by the negation (#4509, rev26). Bounded span so
    # "never mind the boundary -- claim it" is NOT exonerated: that negation
    # attaches to `mind`, not to `claim`, and sits further away.
    # Fixed-width lookbehinds, one per spelling, because the cell carries
    # markdown emphasis: `**never** claim` puts `** ` between the negation and
    # the verb, so a bare `(?<!never )` sees `** ` and misses (#4509, rev26).
    r"(?<!\bnever )(?<!\bnever\*\* )(?<!\bdo not )(?<!\bdo not\*\* )"
    r"(?<!\bdon't )(?<!\bdon't\*\* )(?<!\bnot )"
    r"\bclaim\b(?!\s+(?:signal|signals)\b)"
    r"(?!\s*(?:is|was|belongs|of\s+yours|holds|cannot|ownership)\b)"
    # A PERMISSION is as operative as an instruction, and the noun carries it:
    # "each claim across accounts is permitted", "no claim across accounts is
    # barred", "the claim is yours". The lookbehinds above correctly suppress the
    # noun, so these need matching in their own right (#4509, rev24).
    # A short span between subject and verb, because the operative sentence puts
    # one there: "each claim ACROSS ACCOUNTS is permitted".
    r"|\b(?:claim|issue|it|you)\b[^|\n]{0,28}?\b(?:is|are|were)\s+"
    r"(?:permitted|allowed|yours|free|fine|open to you)\b"
    # `permits`/`allows` only with a claim-ish object: "the other account's loop
    # is permitted to act here" is the HONEST form and must not fire (#4509, rev25).
    r"|\b(?:permits|allows)\s+(?:a claim|the claim|you|each claim|it|the issue)\b"
    # NO trailing preposition. `is barred by` welded `by` on, so deleting two
    # words from this suite's own harmful cell flipped it green -- a corpus cell
    # passing on one surface spelling while the class walks free, which is the
    # failure this suite exists to end, one level up (#4509, rev25).
    r"|\b(?:does not bar|is not barred|are not barred|is barred|does not bind"
    r"|is not binding|are not binding|there is binding)\b"
    r"|\bleaves (?:the issue open|each claim|the claim)\b"
    # `not yours to take` is the HONEST form, so the negation has to be excluded.
    r"|(?<!not )\byours to (?:take|make)\b"
    # The paraphrases, which use no form of `claim` at all.
    r"|\btake (?:it|them|the issue|over)\b|\bopen to you\b"
    r"|\bcarry on\b|\bgo ahead\b|\bopen your PR\b"
    r"|\bpick it up\b|\bwork on it\b|\bassign it to yourself\b|\breassign\b"
    r"|\bstart implementing\b|\bis free\b|\bfree to take\b", re.I)

# A stop token NEGATING a deference verb is the instruction to disregard the
# boundary, wherever in the cell it sits: "do not treat the assignee as binding",
# "do not let the assignee detain you", "do not feel bound". Honest cells negate
# an ACTION instead ("do not touch it", "never claim across accounts"), so the
# verb is what discriminates rather than the negation (#4509, rev26).
#
# This carries its OWN open set, over deference verbs, independent of CLAIM's.
# An earlier version of this comment said its failures "land in the residual
# declared above" -- false, and the third round running where an over-claim moved
# to whatever had just been added (#4509: rev25 on a spelling, rev26 on an
# offset, rev27 on this). CLAIM's residual is about a cell that opens with a stop
# and paraphrases past it; these bypasses open with NO stop word and carry NO
# claim vocabulary, so only this list sees them, and a verb outside it walks:
#
#   do not TREAT the assignee as binding     red   (committed cell)
#   do not REGARD the assignee as binding    green
#   do not CONSIDER / COUNT / READ ...       green
#   do not DEFER to the other loop           red   (committed cell)
#   do not YIELD / BOW / CONCEDE ...         green
#
# Deliberately NOT fixed by lengthening the list, which is what produced the
# three previous rounds. A synonym set is not closable, and a guard whose comment
# says otherwise is worse than one that states the gap: the comment is what a
# later editor believes.
NEGATED_DEFERENCE = re.compile(
    r"\b(?:do not|don't|never)\s+"
    r"(?:feel bound|treat|let|hold back|defer|wait|stop)\b", re.I)

check("the cross-account action cell does not negate the deference it states",
      not action_says(diff_rows, NEGATED_DEFERENCE)[0],
      f"cross-account action cells: "
      f"{[r.cell_under(ACTION_COL) for r in diff_rows]}")

diff_opens_stop = [r for r in diff_rows
                   if (r.cell_under(ACTION_COL) or "") and OPENS_STOP.search(r.cell_under(ACTION_COL))]
check("the cross-account action cell leads with a stop instruction",
      len(diff_opens_stop) == len([r for r in diff_rows if r.cell_under(ACTION_COL) is not None]),
      f"cross-account action cells: {[r.cell_under(ACTION_COL) for r in diff_rows]}")

# The same-account row needs the mirror too: "claim it now -- the open-PR check
# is optional" and "go ahead and claim it; the lookup resolves it later" both
# satisfy PROCEED through `open-PR` while deleting the routing (#4509, review).
# Bounded harm -- same account, no boundary crossed -- but the row's whole
# purpose is to send the reader to that check, not past it.
check("the same-account row does NOT tell the reader to claim it outright",
      not action_says(same_rows, CLAIM)[0],
      f"same-account action cells: "
      f"{[r.cell_under(ACTION_COL) for r in same_rows]}")

check("the cross-account row does NOT also tell the reader to claim it",
      not action_says(diff_rows, CLAIM)[0],
      f"cross-account action cells: "
      f"{[r.cell_under(ACTION_COL) for r in diff_rows]}")

# --- property 3: the discriminator comes FIRST ------------------------------
#
# The near-miss in #3275 was an agent that read the ratings without knowing which
# case it was in. So "which account is the other claimant on" must be asked
# before the first per-signal rating is offered.
ASK = re.compile(r"(read|check|ask|establish|determine)[^.\n]{0,80}\b(account|login)\b", re.I)
ask_at = min((m.start() for m in ASK.finditer(text)), default=None)

SIGNAL_TABLE = [r for r in ROWS if r.any_cell(re.compile(r"\bassignee\b", re.I))]
check("a per-signal table still rates the assignee", len(SIGNAL_TABLE) > 0)

first_rating = text.lower().find("| assignee")
if first_rating == -1:
    first_rating = text.lower().find("assignee |")
check("the rule tells the reader to establish WHICH ACCOUNT before rating signals",
      ask_at is not None and first_rating != -1 and ask_at < first_rating,
      f"instruction at {ask_at}, first signal rating at {first_rating}")

# --- the sister rule it must stop contradicting -----------------------------
# A bare mention is not a deferral. `"branch-and-pr.md" in text` passes for a
# sentence ARGUING AGAINST it -- "its assignee boundary is overstated and this
# rule deliberately overrides it" satisfied the old check (#4474). So read the
# sentence the name sits in and require deference vocabulary, and separately
# refuse override vocabulary anywhere near it.
DEFER = re.compile(r"\bdefer|\bper\b|\bstands?\b|\bholds?\b|\bowns?\b"
                   r"|\bnothing (?:below|here) waives\b|\bnever waives?\b"
                   r"|\bboundary\b[^.\n]{0,40}\b(stands?|holds?|applies)\b", re.I)
OVERRIDE = re.compile(r"\boverrid|\bwaive[sd]?\b|\boverstat|\bsupersede"
                      r"|\btakes? precedence over\b|\bignore\b", re.I)

# LINES, not sentences. Splitting on "." orphans the name -- `.md` ends a
# "sentence", so a naive sentence split yields ZERO fragments containing
# `branch-and-pr.md` and both checks below fail on an honest rule. A markdown
# table row is one line anyway, which is the unit that carries a deferral here;
# a bullet that wraps gets its continuation line joined.
lines = text.splitlines()
joined = []
for ln in lines:
    if joined and (ln.startswith("  ") or ln.startswith("\t")) and ln.strip():
        joined[-1] += " " + ln.strip()
    else:
        joined.append(ln)
sister = [s for s in joined if "branch-and-pr.md" in s]

# TWO sites name it, and pooling them meant neither was pinned: `any`/`none` over
# the pool let either site lose its deference while the other satisfied both
# checks (#4509). Measured -- each of these passed 19/19:
#   * delete the sister bullet's deference, leaving the bare filename;
#   * sister bullet -> "its assignee boundary is advisory only; this rule is the
#     operative one" (a genuine override carrying NO override token at all);
#   * remove the name from the cross-account action cell, keeping "**stop**".
# So split by WHERE the name appears and require each site to carry its own
# deference.
sister_row = [s for s in sister if s.lstrip().startswith("|")]
sister_bullet = [s for s in sister if not s.lstrip().startswith("|")]

check("the cross-account ACTION cell names branch-and-pr.md",
      bool(sister_row),
      f"lines naming it: {sister}")
check("...and the sister-rules list names it too",
      bool(sister_bullet),
      f"lines naming it: {sister}")
check("...and the sister-rules entry DEFERS rather than merely naming it",
      any(DEFER.search(s) for s in sister_bullet),
      f"sister-rules lines: {sister_bullet}")

# The negation carve-out asks whether a negation precedes an override WORD. It
# cannot ask which rule is being subordinated to which, so
# "nothing in it waives this rule's own precedence, so claim across accounts
# freely" is cleared by it while being an override in the operative direction
# (#4509). Reading the CLAIM instruction is what discriminates: a line that
# tells the reader to claim across accounts is an override whatever its
# negations say.
overrides = [s for s in sister
             if (OVERRIDE.search(s)
                 and not re.search(r"\b(nothing|never|not|no)\b[^.\n]{0,60}"
                                   r"(overrid|waive|supersede)", s, re.I))
             or CLAIM.search(s)]

check("the rule mentions branch-and-pr.md at all", bool(sister),
      "no reference to the rule that owns the assignee boundary")
check("...in a sentence that DEFERS to it rather than merely naming it",
      any(DEFER.search(s) for s in sister), f"sentences naming it: {sister}")
check("...and no sentence claims to override that boundary, or to claim past it",
      not overrides, f"override-flavoured sentences: {overrides}")

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
