#!/usr/bin/env python3
"""The ready queue is ordered by `priority:`, and every consumer of it says so.

Four documents decide which issue an agent picks next. Before #4476 they said
"self-select from the `status: ready` queue" with no ordering, so selection fell to
whatever each agent found legible in the moment -- measured over the seven days to
2026-09-22 as 70 of 158 merged PRs (44%) on process/tooling work against a 19%
process backlog.

This guard pins the two properties that fix has to keep:

  1. the TRIAGER assigns a priority to every issue it marks ready, judged on blast
     radius rather than on category (a docs issue may be urgent or low);
  2. every CONSUMER of the ready queue reads priority as the order, and says that a
     pick below the top carries a recorded reason.

It asserts over parsed structure -- the four priority levels as a set, and each
consumer file's own ordering sentence -- not over one phrasing, so a reword that
keeps the property passes and a revert to "self-select, no order" fails.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent

LEVELS = ["urgent", "high", "medium", "low"]

TRIAGER = ROOT / ".claude/agents/triager.md"
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


def levels_in(text: str) -> set[str]:
    """The priority levels a document actually names, as `priority: <level>`."""
    return {m.group(1) for m in re.finditer(r"priority:\s*(urgent|high|medium|low)\b", text)}


# --- the triager owns assignment ------------------------------------------------
tri = TRIAGER.read_text()

check("the triager document exists and is readable", bool(tri.strip()))

check("the triager names all four priority levels",
      levels_in(tri) == set(LEVELS),
      f"named: {sorted(levels_in(tri))}, expected: {sorted(LEVELS)}")

check("the triager requires a priority on every issue it marks ready",
      re.search(r"every\b[^.\n]{0,60}\b(`?status: ?ready`?|ready)\b[^.\n]{0,60}"
                r"(gets|carries|needs|also gets)\b[^.\n]{0,40}priority", tri, re.I)
      is not None
      or re.search(r"every\b[^.\n]{0,80}priority[^.\n]{0,40}\b(exactly one|one)\b", tri, re.I)
      is not None,
      "no sentence binds `status: ready` to receiving a priority")

# Judged on impact, NOT on category -- this is the property the user asked for:
# a documentation issue may be urgent, and a runner gap may be low.
check("the triager judges priority on impact rather than on issue category",
      re.search(r"not\b[^.\n]{0,30}\bon category\b|rather than\b[^.\n]{0,20}\bcategory\b"
                r"|blast radius", tri, re.I) is not None,
      "nothing says priority is judged on blast radius rather than category")

check("the triager states a docs issue can be high priority AND low priority",
      re.search(r"doc\w*[^.\n]{0,80}\burgent\b[^.\n]{0,80}\blow\b", tri, re.I) is not None,
      "the category-independence example (docs can be urgent or low) is missing")

check("the triager distinguishes urgent (blocking) from merely severe",
      re.search(r"urgent\b[^.\n]{0,60}\bblock", tri, re.I) is not None,
      "`urgent` is not tied to blocking other work")

# --- every consumer reads it ----------------------------------------------------
# The ordering claim must tie priority to PICKING AN ISSUE, not merely use the word.
# `al-runner-workflow` has a pre-existing heading "Orchestrator loop (priority order)"
# about STAGE order (merge before review before implement) -- a substring check matches
# it and passes on a file nobody edited. Require the queue/pick context, or the explicit
# level chain, both of which a stage-order heading cannot supply.
ORDERS = re.compile(
    r"highest\s+`?priority[`:]*\s*(one|first|issue)"          # "highest priority first/one"
    r"|urgent`?\s*>\s*`?high`?\s*>\s*`?medium"               # the level chain
    # NOT a co-occurrence test. An earlier version matched `status: ready` and the word
    # "priority" within 80 chars on one line, which cannot separate "order by priority"
    # from "IGNORE priority" -- both passed 23/23, reverting the whole point of the
    # change (found in review of #4477). Match the ordering RELATION instead. Deleting
    # this alternative is not the fix either: it is load-bearing for the true sentence
    # "Workers self-select from the `status: ready` queue in `priority:` order".
    r"|(order|sort|rank)\w*\s+(by|on)\s+`?priority"
    r"|`?priority:?`?\s*order"
    r"|by\s+`?priority",
    re.I)
REASON = re.compile(
    r"(below|lower|under)\b[^.\n]{0,60}(the\s+)?(top|highest)[^.\n]{0,120}"
    r"(reason|why|one line|comment)"
    r"|(reason|why|one line|comment)[^.\n]{0,120}(below|lower)\b[^.\n]{0,40}(top|highest)",
    re.I | re.S)

# Assert on the SENTENCE THAT SELECTS, not on the file. A file-wide search passes while
# every individual ordering sentence is removable, because a sibling keeps the match
# alive -- measured here: stripping the pick step still left two other matches and the
# check stayed green. So find the paragraph that tells an agent which issue to take, and
# require the ordering to live in THAT paragraph.
SELECTS = re.compile(
    r"^[^\n]*(?:next unclaimed issue|issue is ready to work|claim the next"
    r"|self-select|queue is shared|orders the queue|claim the oldest"
    r"|orders the ready queue|highest `?priority)[^\n]*$",
    re.I | re.M)


# A line merely QUOTING a selection phrase is not a selection point. `autonomous-cycle`
# discusses review-vs-implementation ordering and quotes "an issue is ready to work"
# inside that argument; treating it as an instruction makes the guard demand priority
# text in a paragraph that selects nothing. An instruction is imperative or numbered --
# it tells the agent to take/claim/find something -- so require that too.
INSTRUCTS = re.compile(r"\b(take|claim|find|pick|select|self-select)\b", re.I)


def selecting_paragraphs(txt: str) -> list[str]:
    """Each line that INSTRUCTS which issue to pick, plus the 12 lines after it."""
    out = []
    lines = txt.splitlines()
    for i, line in enumerate(lines):
        if SELECTS.match(line) and INSTRUCTS.search("\n".join(lines[i:i + 3])):
            out.append("\n".join(lines[i:i + 12]))
    return out


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
    # EVERY selecting paragraph must order by priority, not just one of them. "At least
    # one" is satisfiable while a sibling carries the property, so removing the ordering
    # from the paragraph an agent actually reads at claim time stays green -- measured
    # here by stripping the `al-runner-workflow` pick step, which left the separate
    # "workers self-select" sentence matching and the check passing.
    unordered = [par for par in paras if not ORDERS.search(par)]
    check(f"{rel} orders the ready queue by priority AT EVERY POINT OF SELECTION",
          not unordered,
          f"{len(unordered)} of {len(paras)} selecting paragraph(s) do not mention priority "
          f"order; first offender starts: {unordered[0].splitlines()[0][:90]!r}"
          if unordered else "")

# The recorded-reason rule is the user's chosen exception mechanism. It belongs on
# the documents an agent reads when PICKING, not on work-cycle.md, which dispatches.
for f in (CONSUMERS[0], CONSUMERS[1], CONSUMERS[2]):
    rel = f.relative_to(ROOT)
    txt = f.read_text()
    check(f"{rel} says a pick below the top carries a recorded reason",
          REASON.search(txt) is not None,
          "no sentence requires a reason when going lower than the highest priority")

# Sibling-folding is exempt: the user named this explicitly as the one case that
# needs no note, because the PR is already being written.
for f in (CONSUMERS[0], CONSUMERS[1]):
    rel = f.relative_to(ROOT)
    txt = f.read_text()
    check(f"{rel} exempts sibling-folding from the reason requirement",
          re.search(r"(fold\w*|sibling)[^.\n]{0,120}(exempt|any priority|no note|needs no)",
                    txt, re.I | re.S) is not None,
          "folding siblings into an in-flight PR is not stated as exempt")

if failures:
    print(f"\nFAILED: {len(failures)} check(s): {failures}")
    sys.exit(1)
print(f"\nPASSED: {len(ran)} check(s)")
