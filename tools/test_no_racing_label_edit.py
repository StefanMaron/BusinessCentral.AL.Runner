#!/usr/bin/env python3
"""No agent recipe or workflow may pass --add-label and --remove-label to one `gh issue edit` (#3960).

`gh issue edit` does not compute a label set and send one PATCH. It dispatches
the additions and the removals as two concurrent goroutines on one errgroup with
no ordering between them -- cli/cli v2.98.0, pkg/cmd/pr/shared/editable_http.go,
two `wg.Go` calls running `addLabelsToLabelable` and `removeLabelsFromLabelable`
-- and exits 0 whichever lands first. So a label named in both lists is a coin
flip, and the losing mutation is discarded with no error anywhere.

Measured: issue #1883 was claimed with the single-call form and its timeline
records two `labeled` events at 2026-09-11T20:05:42Z and ZERO `unlabeled`. It
then carried `status: ready` and `status: in-progress` at once, which is the
input that made #3930's release job remove the label it meant to add.

Why this file exists rather than a note in the recipe: a recipe is prose, and no
CI job executes prose, so a wrong one ships green and every agent that reads it
reproduces the defect (#3955). This test is the executable half.

The check is deliberately shape-based, not literal-based. A single invocation
whose add-list and remove-list merely happen not to overlap today is one edited
literal away from the race, so the rule is "never both kinds of flag in one
invocation" rather than "never the same label in both" -- the second cannot be
checked without evaluating shell variables, and it is the weaker property.

Two known-safe constructions are exempt, each for a stated reason:

  * `.github/workflows/issue-label-hygiene.yml` builds its removal list in a
    bash array and adds `status: ready` in the same call, but explicitly filters
    `status: ready` OUT of the removals (PR #3959). It is checked here for that
    filter rather than for the flag pair, so re-widening the filter fails this
    test instead of silently restoring the race.
  * tools/test_*.py files asserting the ABSENCE of the shape necessarily contain
    both flag names as string literals.

Run directly: python3 tools/test_no_racing_label_edit.py
"""
from __future__ import annotations

import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

failures: list[str] = []
passes = 0


def check(label: str, ok: bool, detail: str = "") -> None:
    global passes
    if ok:
        passes += 1
        print(f"PASS  {label}")
    else:
        failures.append(label)
        print(f"FAIL  {label}")
        if detail:
            for line in str(detail).splitlines()[:12]:
                print(f"      {line}")


def tracked_files() -> list[str]:
    out = subprocess.run(
        ["git", "-C", ROOT, "ls-files"],
        capture_output=True, text=True, check=True,
    ).stdout.split("\n")
    return [f for f in out if f]


# A shell invocation can be split over backslash-continued lines, so those are
# joined before the scan. Without this a two-line racing command reads as two
# innocent ones -- the false negative that looks exactly like a pass.
def logical_lines(text: str) -> list[tuple[int, str]]:
    rows: list[tuple[int, str]] = []
    lineno = 1
    buf = ""
    start = 1
    for raw in text.split("\n"):
        if not buf:
            start = lineno
        if raw.endswith("\\"):
            buf += raw[:-1] + " "
        else:
            rows.append((start, buf + raw))
            buf = ""
        lineno += 1
    if buf:
        rows.append((start, buf))
    return rows


# An INVOCATION, not a mention. Prose that names both flags in one sentence --
# including the rule text explaining why never to combine them -- is not a
# command, and a scan that cannot tell the two apart fires on its own
# documentation. So the line must actually start a `gh issue edit` before the
# flag pair counts. Anchoring on the command also means a racing call is caught
# wherever it appears: a fenced code block, a YAML `run:` body, or a shell script.
_CMD = re.compile(r"gh\s+issue\s+edit\b")


def is_racing_invocation(line: str) -> bool:
    m = _CMD.search(line)
    if not m:
        return False
    rest = line[m.start():]
    return "--add-label" in rest and "--remove-label" in rest


# This file, and any sibling asserting the shape's absence, quote both flags.
EXEMPT_SELF = {
    "tools/test_no_racing_label_edit.py",
    "tools/test_issue_label_hygiene_workflow.py",
    "tools/test_issue_label_hygiene_behaviour.py",
}
# The workflow's safety is the explicit status: ready filter, asserted separately below.
EXEMPT_WORKFLOW = ".github/workflows/issue-label-hygiene.yml"

offenders: list[str] = []
scanned = 0
for rel in tracked_files():
    if rel in EXEMPT_SELF or rel == EXEMPT_WORKFLOW:
        continue
    path = os.path.join(ROOT, rel)
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            text = fh.read()
    except (OSError, IsADirectoryError):
        continue
    if "--add-label" not in text or "--remove-label" not in text:
        continue
    scanned += 1
    for lineno, line in logical_lines(text):
        if is_racing_invocation(line):
            offenders.append(f"{rel}:{lineno}: {line.strip()[:160]}")

check(
    "no tracked file passes --add-label and --remove-label in one invocation -- "
    "gh dispatches them as unordered concurrent mutations and drops the loser",
    not offenders,
    "\n".join(offenders),
)

# The scan above is a negative, and a negative from a pattern nobody has seen
# fire is indistinguishable from a pattern that never matches. Prove the matcher
# on the exact string the fix removed from .claude/agents/impl-agent.md.
HISTORICAL = (
    'gh issue edit <N> --add-label "agent: <AGENT-ID>" --add-label '
    '"status: in-progress" --remove-label "status: ready" --add-assignee @me '
    "--repo StefanMaron/BusinessCentral.AL.Runner"
)
hits = [ln for _, ln in logical_lines(HISTORICAL) if is_racing_invocation(ln)]
check(
    "the matcher fires on the pre-fix claim recipe -- otherwise the clean scan "
    "above proves only that the pattern never matches anything",
    len(hits) == 1,
    repr(hits),
)

# ...and on a backslash-continued spelling, which is the shape a reformat would
# produce and a line-at-a-time scan would miss.
CONTINUED = 'gh issue edit 42 --add-label "b" \\\n  --remove-label "a"\n'
joined = [ln for _, ln in logical_lines(CONTINUED) if is_racing_invocation(ln)]
check(
    "the matcher fires across a backslash continuation",
    len(joined) == 1,
    repr(joined),
)

check(
    "a file with only one kind of flag does not trip the matcher",
    not [ln for _, ln in logical_lines('gh issue edit 42 --add-label "x"\n')
         if is_racing_invocation(ln)],
)

# The exemption that matters most, because getting it wrong makes the guard fire
# on the rule that documents it -- which is how a correct guard gets deleted.
check(
    "prose naming both flags is not an invocation",
    not is_racing_invocation(
        "Never pass `--add-label` and `--remove-label` to one `gh issue edit`."
    ),
)
check(
    "...but a real command later on the same line still is",
    is_racing_invocation(
        'Do not do this: gh issue edit 42 --add-label "a" --remove-label "b"'
    ),
)
check(
    "the two-call replacement shape passes",
    not any(is_racing_invocation(ln) for _, ln in logical_lines(
        'gh issue edit 42 --add-label "status: in-progress" --add-assignee @me\n'
        'gh issue edit 42 --remove-label "status: ready"\n'
    )),
)

# The one live single-call site. It is safe only because status: ready is
# filtered out of the removals, so that filter is what gets asserted.
wf_path = os.path.join(ROOT, EXEMPT_WORKFLOW)
if os.path.exists(wf_path):
    with open(wf_path, encoding="utf-8", errors="replace") as fh:
        wf = fh.read()
    check(
        "issue-label-hygiene.yml still filters `status: ready` out of the labels "
        "it removes -- it adds that label in the same call, so dropping the "
        "filter restores the #3930 race",
        re.search(r'select\(\s*\.\s*!=\s*"status: ready"\s*\)', wf) is not None,
        "the `select(. != \"status: ready\")` filter is gone from the release job",
    )
else:
    check("issue-label-hygiene.yml is present to be checked", False, wf_path)

# The replacement recipe must actually be in the file agents read, in the safe
# order. Absence here would mean the guard passes over a file that no longer
# tells anyone what to do instead.
recipe_path = os.path.join(ROOT, ".claude", "agents", "impl-agent.md")
with open(recipe_path, encoding="utf-8", errors="replace") as fh:
    recipe = fh.read()

add_idx = recipe.find('--add-label "status: in-progress"')
rm_idx = recipe.find('--remove-label "status: ready"')
check(
    "the claim recipe still adds `status: in-progress` and removes `status: ready`",
    add_idx != -1 and rm_idx != -1,
    f"add={add_idx} remove={rm_idx}",
)
check(
    "...with the ADDITION first: a lost second call leaves both labels, which is "
    "visible and self-correcting, where a lost removal-first call leaves the "
    "issue with no status label and out of every queue that would find it",
    add_idx != -1 and rm_idx != -1 and add_idx < rm_idx,
    f"add at {add_idx}, remove at {rm_idx}",
)

print("")
print(f"{passes} passed, {len(failures)} failed ({scanned} file(s) carried both flag names)")
sys.exit(1 if failures else 0)
