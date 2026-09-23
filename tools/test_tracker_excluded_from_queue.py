#!/usr/bin/env python3
"""A `type: tracker` issue is excluded from every ready-queue pick, and never Closed.

A tracker is a set of related items, not one unit of work. `status: ready` on one means
*the items are available*, not *this issue closes* -- so it sorts to the top of its
priority band and every reader must recognise and skip it by hand. That recognition
failed on #3389: an agent took it in good faith and wrote `Closes #3389`, which would
have shut a nine-mechanism record having addressed two. `reject-deferred-scope` cannot
catch that shape, because it keys on a deferral's DESTINATION rather than on whether a
PR finished what it closes (`branch-and-pr.md`).

The label `type: tracker` exists. This guard pins the two properties that make it act:

  1. every paragraph that tells an agent which issue to PICK excludes trackers, and
     excludes them by NEGATION -- so a recipe inverted to select only trackers fails;
  2. every consumer states what a tracker is, and that a PR touching one declares
     `Part of`, never `Closes`.

It asserts at the point of selection rather than file-wide, because three guards on this
shape were defeated in one day by their own mutations: a file-wide search matched an
unrelated pre-existing heading and passed on files nobody had edited; "at least one
paragraph mentions it" stayed satisfied by a sibling sentence while the pick step itself
lost the property; and a check reading a construct's PRESENCE rather than its ARGUMENT
accepted a recipe inverted to select exactly the wrong thing.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent

LABEL = "type: tracker"

CONSUMERS = [
    ROOT / ".claude/skills/al-runner-workflow/SKILL.md",
    ROOT / ".claude/skills/autonomous-cycle/SKILL.md",
    ROOT / ".claude/skills/orchestrating-a-session/SKILL.md",
    ROOT / ".claude/commands/work-cycle.md",
]

failures: list[str] = []
ran: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    ran.append(name)
    print(f"  {'ok  ' if ok else 'FAIL'} {name}")
    if not ok:
        if detail:
            print(f"       {detail}")
        failures.append(name)


# --- finding the point of selection ---------------------------------------------
# Shared with tools/test_priority_queue_ordering.py, deliberately: both guards must
# judge the SAME paragraphs, or one of them is asserting about text the other does not
# consider a pick step, and a selection point could satisfy each while satisfying
# neither in the place an agent actually reads.
SELECTS = re.compile(
    r"^[^\n]*(?:next unclaimed issue|issue is ready to work|claim the next"
    r"|self-select|queue is shared|orders the queue|claim the oldest"
    r"|orders the ready queue|highest `?priority)[^\n]*$",
    re.I | re.M)

# A line merely QUOTING a selection phrase is not an instruction. Require an imperative
# verb in the first three lines too.
INSTRUCTS = re.compile(r"\b(take|claim|find|pick|select|self-select)\b", re.I)


def selecting_paragraphs(txt: str) -> list[str]:
    """Each line that INSTRUCTS which issue to pick, plus the 20 lines after it."""
    out = []
    lines = txt.splitlines()
    for i, line in enumerate(lines):
        if SELECTS.match(line) and INSTRUCTS.search("\n".join(lines[i:i + 3])):
            out.append("\n".join(lines[i:i + 20]))
    return out


def instruction_sentences(txt: str) -> list[str]:
    """The INSTRUCTION itself -- the selecting line and the 5 after it.

    The 20-line window above is the right scope for "does this section tell a reader
    about trackers". It is the wrong scope for "does the sentence that tells an agent
    which issue to take say so", because supporting material three lines down satisfies
    it while the instruction loses the property -- failure mode 2, and measured here:
    stripping `excluding every type: tracker issue` from the
    `orchestrating-a-session` pick sentence left the 20-line check GREEN on the jq block
    and the definition paragraph beneath it. An agent reading only the bolded
    instruction would then take a tracker.
    """
    out = []
    lines = txt.splitlines()
    for i, line in enumerate(lines):
        if SELECTS.match(line) and INSTRUCTS.search("\n".join(lines[i:i + 3])):
            out.append("\n".join(lines[i:i + 6]))
    return out


# --- what counts as excluding a tracker -----------------------------------------
# The property is a NEGATION applied to the tracker label at the point of selection,
# in whatever form that paragraph uses: a jq clause, or prose. Both must carry the
# negation, because a paragraph naming the label without one is the inverted recipe --
# "select the trackers" reads as tracker-aware to any presence test and does exactly
# the wrong thing (failure mode 3; the measured defeat of a sibling guard).
#
# jq form. The shipped clause is
#     | select([.labels[].name] | map(. == "type: tracker") | any | not)
# so the negation is the trailing `not`. `map(...) | any` WITHOUT it selects only
# trackers, and that must not match -- which is why the `not` is inside the pattern
# rather than checked separately.
JQ_EXCLUDES = re.compile(
    r"map\(\s*\.\s*==\s*[\"']type:\s*tracker[\"']\s*\)\s*\|\s*any\s*\|\s*not"
    # `all(. != "type: tracker")` and `| not` after a contains-test are equally valid
    # spellings of the same negation; a reword to either must stay green.
    r"|all\(\s*\.\s*!=\s*[\"']type:\s*tracker[\"']\s*\)"
    r"|index\(\s*[\"']type:\s*tracker[\"']\s*\)\s*\|\s*not"
    r"|contains\(\s*\[\s*[\"']type:\s*tracker[\"']\s*\]\s*\)\s*\|\s*not",
    re.I)

# Prose form: the label named, with an exclusion verb bound to it on the same sentence.
# Matched in either order, because "skip a `type: tracker` issue" and "a `type: tracker`
# issue is never picked" say the same thing. The verb list is what carries the negation.
EXCLUDE_VERB = (r"(?:exclud\w+|skip\w*|never (?:pick|take|claim|select)\w*|not (?:a )?"
                r"candidat\w+|drop\w*|filter\w*\s+out|omit\w*|leave\w*\s+out"
                r"|do(?:es)? not (?:appear|sort|count|enter)|out of the queue"
                r"|no(?:t)? (?:eligible|claimable|selected)|is never (?:picked|taken|claimed))")
PROSE_EXCLUDES = re.compile(
    rf"`?type:\s*tracker`?[^.\n]{{0,160}}{EXCLUDE_VERB}"
    rf"|{EXCLUDE_VERB}[^.\n]{{0,160}}`?type:\s*tracker`?",
    re.I)


def excludes_trackers(par: str) -> bool:
    return bool(JQ_EXCLUDES.search(par) or PROSE_EXCLUDES.search(par))


# --- the inversion this guard exists to refuse -----------------------------------
# A recipe that names the label and keeps only what matches selects EXACTLY the wrong
# set. Detect it directly rather than relying on the absence of a negation: a paragraph
# could carry a correct prose sentence beside an inverted jq clause, and the exclusion
# check above would pass on the prose while the recipe an agent runs is inverted.
# A paragraph carries a RUNNABLE queue read when it lists issues and pipes them through a
# filter -- `gh issue list` with a `--jq`/`jq` clause, in any order. This is what an agent
# copies and executes, as distinct from the prose around it.
RECIPE = re.compile(r"gh issue list[\s\S]{0,600}?(?:--jq|\|\s*jq)", re.I)

# A recipe that reads the READY QUEUE specifically: `gh issue list` naming `status: ready`
# and piping through a filter. Anchored on the label so a `gh issue list` doing something
# else -- auditing labels, sweeping untriaged issues -- is not required to exclude
# trackers, which would be a false red on a command that is not a pick.
#
# The window after the jq marker is a fixed length rather than a search for a closing
# delimiter: the jq program's own OPENING quote is the first `'` in the text, so a
# `...?(?:'|...)` terminator stops before the filter body and every recipe reads as
# unfiltered -- a false red that looks exactly like the defect (measured here on both
# files that carry one).
READY_RECIPE = re.compile(
    r"gh issue list[\s\S]{0,400}?status: ?ready[\s\S]{0,600}?(?:--jq|\|\s*jq)[\s\S]{0,700}",
    re.I)

JQ_INVERTED = re.compile(
    r"map\(\s*\.\s*==\s*[\"']type:\s*tracker[\"']\s*\)\s*\|\s*any\s*\)"
    r"|map\(\s*\.\s*==\s*[\"']type:\s*tracker[\"']\s*\)\s*\|\s*any\s*$"
    r"|any\(\s*\.\s*==\s*[\"']type:\s*tracker[\"']\s*\)\s*\)",
    re.I | re.M)


for f in CONSUMERS:
    rel = f.relative_to(ROOT)
    txt = f.read_text() if f.exists() else ""
    check(f"{rel} exists", bool(txt.strip()))
    if not txt.strip():
        continue

    paras = selecting_paragraphs(txt)
    check(f"{rel} has a paragraph that selects the next issue",
          len(paras) > 0,
          "no line tells an agent which issue to take; the selection point moved or was renamed")

    # EVERY selecting paragraph, not at least one. A sibling carrying the property keeps
    # an "any" check green while the paragraph an agent reads at claim time loses it.
    missing = [p for p in paras if not excludes_trackers(p)]
    check(f"{rel} excludes `{LABEL}` AT EVERY POINT OF SELECTION",
          not missing,
          f"{len(missing)} of {len(paras)} selecting paragraph(s) do not exclude trackers; "
          f"first offender starts: {missing[0].splitlines()[0][:95]!r}"
          if missing else "")

    # The argument, not the construct -- a presence test passes an inverted recipe.
    #
    # FILE-WIDE, deliberately, and this is the one check that is not scoped to a selecting
    # paragraph. An inverted clause is harmful wherever it sits: `work-cycle.md` builds the
    # queue in its Step A recipe, which the paragraph detector does not reach (its only
    # selection point is the dispatch prompt 14 lines below), so a paragraph-scoped check
    # left an `any(. == "type: tracker")` there completely unguarded -- found by writing a
    # second inversion rather than by re-running the first, which had landed in a file
    # where the detector did reach.
    #
    # Scoping widely is safe HERE and would not be for the exclusion checks above: those
    # ask "is the property present", where a wide scope lets a sibling satisfy them
    # (failure modes 1 and 2). This one asks "is something harmful present", where a wide
    # scope can only find more.
    inverted = JQ_INVERTED.search(txt)
    check(f"{rel} does not INVERT the tracker filter anywhere",
          inverted is None,
          f"a recipe keeps only `{LABEL}` issues rather than removing them: "
          f"{inverted.group(0)[:95]!r}" if inverted else "")

    # A RUNNABLE recipe must carry the clause itself -- prose beside it does not filter a
    # queue. Measured while writing this guard: deleting the jq clause from the
    # `al-runner-workflow` pick step left the check GREEN, because the tracker prose three
    # paragraphs down sits inside the same window and satisfied the exclusion test. An
    # agent pasting that recipe gets trackers back while the document says otherwise, so
    # the two must be pinned separately.
    # The instruction itself, not the section around it.
    instr = instruction_sentences(txt)
    silent = [s for s in instr if not excludes_trackers(s)]
    check(f"{rel} names the exclusion IN THE INSTRUCTION that selects, not only nearby",
          not silent,
          f"{len(silent)} of {len(instr)} instruction(s) leave trackers out of the "
          f"sentence an agent reads at claim time; first: "
          f"{silent[0].splitlines()[0][:95]!r}" if silent else "")

    # Every runnable recipe in the file, not only those the paragraph detector reaches.
    # `work-cycle.md`'s Step A recipe builds the queue 14 lines above the nearest selecting
    # line, so a paragraph-scoped version of this check never saw it.
    #
    # Restricted to recipes that read the READY QUEUE -- a `gh issue list` filtered to
    # something else (a label audit, a triage sweep over untriaged issues) is not a pick
    # and must not be forced to exclude trackers.
    recipes = [m.group(0) for m in READY_RECIPE.finditer(txt)]
    unfiltered = [r for r in recipes if not JQ_EXCLUDES.search(r)]
    check(f"{rel} carries the exclusion INSIDE every runnable ready-queue recipe",
          not unfiltered,
          f"{len(unfiltered)} of {len(recipes)} runnable ready-queue recipe(s) have no "
          f"tracker clause in the recipe itself" if unfiltered else "")

    # File-wide, and deliberately so: the definition is reference material a reader
    # consults once, not something the pick step must repeat. Scoping it to the
    # paragraph would force the same sentence into five places.
    check(f"{rel} says a PR touching a tracker declares `Part of`, never `Closes`",
          re.search(r"`?Part of`?[^.\n]{0,120}\bnever\b[^.\n]{0,40}`?Closes`?"
                    r"|\bnever\b[^.\n]{0,40}`?Closes`?[^.\n]{0,120}`?Part of`?",
                    txt, re.I) is not None,
          "no sentence binds a tracker to `Part of` rather than `Closes`")

    # The word `tracker`, not the literal label, because a document that has just named
    # `type: tracker` in its recipe reasonably writes "a tracker is ..." in the sentence
    # that defines it. Requiring the label inside the defining sentence pins a phrasing
    # rather than the property, and it red an honest wording here.
    check(f"{rel} says what a tracker is",
          re.search(r"\btracker\b[^.\n]{0,200}\b(a set|set of|several|group of|"
                    r"many|multiple|collection|not one unit|not a unit)\b"
                    r"|\b(a set|set of|not one unit|not a unit)\b[^.\n]{0,200}"
                    r"\btracker\b",
                    txt, re.I) is not None,
          "nothing defines a tracker as a set rather than one unit of work")


# --- the reason the label is needed at all ---------------------------------------
# `reject-deferred-scope` keys on a deferral's destination, so it cannot see a PR that
# closes a tracker without saying where the rest went. If a later editor believes CI
# catches this, the label stops being applied. Pin the statement on the rule that owns
# the gate, so it is written where someone reading about `Closes` finds it.
rule = (ROOT / ".claude/rules/branch-and-pr.md").read_text()
# `[\s\S]`, not `[^.]`: the sentence that states the blind spot is separated from the
# gate's name by prose containing full stops, so a period-terminated window misses a
# statement that IS present -- a false red, which is the same defect as a false green
# one step along (`guards-need-a-third-state.md`).
check("branch-and-pr.md states reject-deferred-scope cannot catch a closed tracker",
      re.search(r"reject-deferred-scope[\s\S]{0,400}?\b(?:destination|DESTINATION)\b", rule)
      is not None,
      "the gate's blind spot is not stated where a reader deciding Closes-vs-Part-of looks")

check("branch-and-pr.md names `type: tracker` as the shape that gate is blind to",
      re.search(r"`?type:\s*tracker`?[\s\S]{0,400}?`?Part of`?", rule) is not None,
      "nothing tells a reader choosing Closes-vs-Part-of that a tracker takes `Part of`")

if failures:
    print(f"\nFAILED: {len(failures)} check(s): {failures}")
    sys.exit(1)
print(f"\nPASSED: {len(ran)} check(s)")
