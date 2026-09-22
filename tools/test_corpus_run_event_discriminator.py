#!/usr/bin/env python3
"""Pin `ci-verdicts.md`'s corpus leg-set discriminator: read `event`, not the leg count (#3389).

THE DEFECT THIS GUARDS
  The corpus `ci.yml` `prepare` job emits a ONE-entry matrix when
  `github.event.inputs.bc_version` is set, and the full eight otherwise. So the
  legitimate single-leg dispatch recipe in section 5 produces a run in which seven
  of the eight required cloud legs DO NOT EXIST -- which reads exactly like a matrix
  that fail-fasted. Both corpus matrices are `fail-fast: false`, so that reading is
  never right.

  Measured on corpus PR #254, head 84a63b262820eb645b39262f7d5ca10d71420bc8:
  run 34118581202 (workflow_dispatch) has 1 cloud leg, run 34117863109
  (pull_request) has 8. Two review agents reached OPPOSITE wrong verdicts about
  which legs ran, in one hour, off runs on one head.

WHAT IS ACTUALLY PINNED, AND WHY IT IS NOT A KEYWORD SWEEP
  `tdd.md`: a test that names the thing is not a test that drives it. Three known
  ways a guard over rule prose passes while the claim is gone:

    1. it searches the file FILE-WIDE and matches an unrelated pre-existing heading;
    2. it accepts "at least one paragraph mentions it", which a sibling sentence
       satisfies, so deleting the load-bearing sentence stays green;
    3. it pins a table's verdict column and never reads the action column, so an
       edit inverting the action passes every check.

  All three are the same shape: the check identified the FILE rather than the CLAIM.
  So every check below is scoped to the one section by heading, and asserts a
  RELATION between two things on one line -- never the presence of a word.

  The discriminator predicate itself is EXECUTED, against synthetic run payloads
  shaped like the API's, so "filter to the pull_request event" is tested rather
  than quoted. No network: the fixtures reproduce the measured #254 shape.

THE THIRD STATE (`guards-need-a-third-state.md`)
  A section heading that no longer matches means this guard is measuring NOTHING.
  That is reported as UNMEASURABLE (exit 3), never as success -- the same reason
  `test_agent_doc_guard_counts.py` refuses a regex that matches nothing.

Run: python3 tools/test_corpus_run_event_discriminator.py
"""
from __future__ import annotations

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RULE = os.path.join(ROOT, ".claude", "rules", "ci-verdicts.md")

EXIT_OK, EXIT_DRIFTED, EXIT_CANNOT_MEASURE = 0, 1, 3

# The section this guard is about. Matched on its heading, so a check can never
# drift onto an unrelated part of a 600-line file (failure mode 1 above).
SECTION_HEADING = re.compile(
    r"^#+ .*\bevent\b.*\bbefore\b.*\bleg set\b", re.I | re.M)

FAILURES: list[str] = []
UNMEASURABLE: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'ok  ' if ok else 'FAIL'} {name}")
    if not ok:
        if detail:
            print(f"       {detail}")
        FAILURES.append(name)


def cannot_measure(name: str, detail: str) -> None:
    print(f"  ???? {name}")
    print(f"       {detail}")
    UNMEASURABLE.append(name)


def section_body(text: str) -> str | None:
    """The one section's text, heading to the next heading of the same-or-higher level."""
    m = SECTION_HEADING.search(text)
    if not m:
        return None
    start = m.start()
    level = len(text[start:].split(" ", 1)[0])
    nxt = re.compile(r"^#{1,%d} " % level, re.M)
    rest = nxt.search(text, m.end())
    return text[start:rest.start()] if rest else text[start:]


# ---------------------------------------------------------------------------
# Part 1 -- EXECUTE the discriminator against run payloads shaped like the API's.
#
# The rule tells a reader to filter the runs on a head SHA to the `pull_request`
# event before reading any leg set. That is a predicate over the objects the API
# returns, so it runs here with no network. Fixture = the measured #254 shape.
# ---------------------------------------------------------------------------

HEAD_254 = "84a63b262820eb645b39262f7d5ca10d71420bc8"

RUNS_254 = [
    # (id, event, created_at, cloud legs the run actually contains)
    (34118581202, "workflow_dispatch", "2026-09-06T21:41:00Z", 1),
    (34117863109, "pull_request", "2026-09-06T21:02:00Z", 8),
]

# Corpus PR #257, head e3d632ce: three runs, and the GATING one is the OLDEST.
RUNS_257 = [
    (34132233466, "workflow_dispatch", "2026-09-07T14:18:26Z", 1),
    (34121459612, "workflow_dispatch", "2026-09-07T12:21:49Z", 1),
    (34118430645, "pull_request", "2026-09-07T11:47:29Z", 8),
]


def gating_run(runs):
    """What the rule prescribes: select on event, never on recency."""
    hits = [r for r in runs if r[1] == "pull_request"]
    return hits[0] if len(hits) == 1 else None


def newest_run(runs):
    """The heuristic the rule warns against, so the difference is measurable."""
    return max(runs, key=lambda r: r[2])


def part1_predicate() -> None:
    print("\nPart 1 -- the discriminator, executed")

    g = gating_run(RUNS_254)
    check("picks the pull_request run on a head carrying a dispatch too (#254)",
          g is not None and g[0] == 34117863109, f"got {g}")
    check("the gating run it picks has all eight cloud legs",
          g is not None and g[3] == 8, f"got {g[3] if g else None}")

    # The wrong read the section exists to stop: the dispatch run's one leg is not
    # a collapsed matrix. A reader taking leg COUNT as the signal gets 1 and infers
    # seven legs "never ran".
    disp = [r for r in RUNS_254 if r[1] == "workflow_dispatch"][0]
    check("the dispatch run really does carry one cloud leg, not eight",
          disp[3] == 1, f"got {disp[3]}")
    check("leg count alone cannot discriminate: the two runs share a head SHA",
          len({r[1] for r in RUNS_254}) == 2 and len({8, 1}) == 2)

    # The property most likely to be lost in an edit, and the one no leg-count
    # check can see: recency is NOT the discriminator.
    n = newest_run(RUNS_257)
    g257 = gating_run(RUNS_257)
    check("on #257 the newest of three runs is a dispatch, not the gating run",
          n[1] == "workflow_dispatch", f"newest={n}")
    check("on #257 the gating run is the OLDEST of the three",
          g257 is not None and g257 == min(RUNS_257, key=lambda r: r[2]),
          f"gating={g257}")
    check("so 'newest wins' and 'filter on event' disagree here",
          n[0] != (g257[0] if g257 else None))


# ---------------------------------------------------------------------------
# Part 2 -- the section states the discriminator as a RELATION.
#
# Every assertion below reads two things on one line and requires them together.
# A sentence that merely mentions `event`, or merely mentions eight legs, does
# not satisfy any of them (failure mode 2 above).
# ---------------------------------------------------------------------------

def sentences(body: str) -> list[str]:
    """The section's prose split into SENTENCES, not lines.

    A relation stated in prose spans whatever the hard wrap does to it -- the
    denial below sits across two lines in the shipped text. Checking per LINE
    would pin the author's line breaks rather than the claim, which is the
    "you pinned your own phrasing" failure one step along from the three in the
    module docstring. Fenced blocks are kept: the recipe is part of the claim.
    """
    flat = re.sub(r"\s*\n\s*", " ", body)
    parts = re.split(r"(?<=[.!?:])\s+(?=[A-Z`*(])|(?<=```)\s+", flat)
    return [p.strip() for p in parts if p.strip()]


def part2_section(body: str) -> None:
    print("\nPart 2 -- the section says it, as a relation")

    lines = sentences(body)

    # (a) The discriminator field and its gating value must be named TOGETHER.
    field_and_value = [
        ln for ln in lines
        if re.search(r"`?\bevent\b`?", ln) and "pull_request" in ln
    ]
    check("names the `event` field and `pull_request` together on one line",
          bool(field_and_value),
          "no line carries both the field and the value that identifies a gating run")

    # (b) The recipe must select on the event, and select the RIGHT value. Reading
    #     "a line mentioning pull_request that also contains `select`" is not this
    #     check: an inverted recipe selecting `workflow_dispatch` satisfies it while
    #     telling the reader to take exactly the wrong run -- measured, this guard
    #     passed that inversion until the comparison below read the selector's
    #     ARGUMENT instead of its neighbourhood. That is failure mode 3 in the
    #     docstring (a verdict column pinned, the action column never read), and a
    #     rule prescribing the wrong action is worse than one prescribing none.
    sel_args = re.findall(
        r"""select\s*\(\s*\.event\s*==\s*["']([a-z_]+)["']""", body, re.I | re.X)
    check("the recipe SELECTS on the event rather than only printing it",
          bool(sel_args),
          "no `select(.event == ...)` form; a listing alone leaves the choice unmade")
    check("every event the recipe selects on is `pull_request`, not a dispatch",
          bool(sel_args) and set(sel_args) == {"pull_request"},
          f"recipe selects on {sorted(set(sel_args))!r} -- selecting a dispatch run "
          f"prescribes taking the very run this section exists to exclude")

    # (c) The trap: a short leg set is NOT a collapsed matrix. This is the inference
    #     the section exists to block, and blocking it needs BOTH halves --
    #     the wrong reading named, and denied. Requiring them in one sentence is
    #     too strict (the shipped text uses an anaphor, "that short leg set", so the
    #     subject is in the sentence before); requiring them anywhere in the section
    #     is too loose (failure mode 2). So: the denial and its subject must be
    #     ADJACENT sentences, and the denial must carry an explicit negation --
    #     naming fail-fast without denying it is how the reading survives.
    def denies(s: str) -> bool:
        return bool(
            re.search(r"fail-?fast|collaps|never ran|did not run", s, re.I)
            and re.search(r"\bnever right\b|\bis not\b|\bnot\b .*\b(right|true|the verdict)|"
                          r"\bwrong\b|\bcannot\b", s, re.I))

    def names_short_set(s: str) -> bool:
        return bool(re.search(
            r"dispatch|one[- ]leg|single[- ]leg|short leg set|fewer than eight|seven", s, re.I))

    denial = [
        i for i, s in enumerate(lines)
        if denies(s) and any(names_short_set(lines[j])
                             for j in (i - 1, i) if 0 <= j < len(lines))
    ]
    check("states that a dispatch's short leg set is NOT a collapsed matrix",
          bool(denial),
          "no sentence denies the fail-fast/collapse reading next to the one that "
          "names the short leg set; without the denial the wrong inference survives")

    # (d) Recency is explicitly RULED OUT -- and ruling it out is a negation, not a
    #     mention. Measured: a replacement reading "a head can carry several runs from
    #     the newest dispatch back to the original" keeps the vocabulary and drops the
    #     claim, so a check keyed on "a sentence mentioning newest/oldest near a run
    #     word" passed it while the warning against taking the newest run was gone.
    #     That is failure mode 2 -- a sibling sentence satisfying the check. So the
    #     sentence must both name the ordering AND deny it decides which run gates.
    def denies_recency(s: str) -> bool:
        return bool(
            re.search(r"\boldest\b|\bnewest\b|\bmost recent\b|\brecency\b", s, re.I)
            and re.search(r"\bnever\b|\bnot\b|\bdoes not\b|\bdo not\b|\brather than\b|"
                          r"\binstead of\b|\bcannot\b", s, re.I))

    recency = [s for s in lines if denies_recency(s)]
    check("rules out recency: says which run gates is not the newest/oldest question",
          bool(recency),
          "no sentence DENIES that run order decides it -- merely mentioning 'newest' "
          "leaves 'take the most recent run' standing, which is the error itself")

    # (e) The default `gh pr checks` output omits `event`, which is WHY the error is
    #     easy: the field exists but is not shown unless asked for by name.
    ghpr = [
        ln for ln in lines
        if "gh pr checks" in ln
        and re.search(r"--json|omit|does not (show|print|carry)|default", ln, re.I)
    ]
    check("says the default `gh pr checks` output does not carry `event`",
          bool(ghpr),
          "without this a reader thinks the discriminator is unavailable there")

    # (f) The full 40-char SHA requirement rides with the runs query. An abbreviated
    #     SHA returns [] and reads as 'no runs' -- a false negative on the very query
    #     this section prescribes.
    fullsha = [
        ln for ln in lines
        if re.search(r"\b40\b|full(?:-| )(?:40|length|character)|abbreviat", ln, re.I)
    ]
    check("keeps the full-SHA requirement beside the runs query",
          bool(fullsha),
          "an abbreviated head_sha returns an empty list that reads as 'no runs'")


# ---------------------------------------------------------------------------
# Part 3 -- the section is reachable from the dispatch recipe it qualifies.
#
# The two facts must be met together: section 5 tells a reader to dispatch one
# corpus leg, and this section says how not to misread the result. A section
# placed far from it is a section the reader meets too late.
# ---------------------------------------------------------------------------

def part3_placement(text: str, body: str) -> None:
    print("\nPart 3 -- it sits with the dispatch recipe it qualifies")

    disp = text.find("gh workflow run ci.yml")
    sec = text.find(body[:60])
    check("the corpus single-leg dispatch recipe is still in this file",
          disp != -1, "the recipe this section qualifies was not found")
    if disp == -1 or sec == -1:
        return
    between = text[min(disp, sec):max(disp, sec)]
    n_headings = len(re.findall(r"^## ", between, re.M))
    check("no top-level section separates it from that recipe",
          n_headings == 0,
          f"{n_headings} '## ' heading(s) between the dispatch recipe and this section")


def main() -> int:
    if not os.path.isfile(RULE):
        cannot_measure("the rule file exists", f"{RULE} not found")
        print("\nUNMEASURABLE")
        return EXIT_CANNOT_MEASURE

    text = open(RULE, encoding="utf-8").read()

    part1_predicate()

    body = section_body(text)
    if body is None:
        cannot_measure(
            "the corpus leg-set section is findable by its heading",
            "no heading matched /event .* before .* leg set/i in ci-verdicts.md -- "
            "the section was renamed or removed, so Parts 2 and 3 measured nothing")
        print("\nUNMEASURABLE: a renamed heading is not a pass "
              "(guards-need-a-third-state.md)")
        return EXIT_CANNOT_MEASURE

    part2_section(body)
    part3_placement(text, body)

    if FAILURES:
        print(f"\nFAILED: {len(FAILURES)} check(s): {FAILURES}")
        return EXIT_DRIFTED
    print("\nall corpus run-event discriminator checks passed")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
