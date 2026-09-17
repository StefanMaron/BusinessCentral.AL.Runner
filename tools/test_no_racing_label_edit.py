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
    `status: ready` OUT of the removals (PR #3959). Instead of the flag pair, it
    is checked by RUNNING that step against a fixture issue and asserting the
    command it resolves to names no label in both lists, so re-widening the
    filter fails this test instead of silently restoring the race. Executed
    rather than matched in the source, because a YAML comment quoting the
    filter satisfied the old source match with the real filter deleted (#4301).
  * tools/test_*.py files asserting the ABSENCE of the shape necessarily contain
    both flag names as string literals.

Scope: this checks `gh issue edit` COMMAND TEXT only. It cannot see
`mcp__github__issue_write`, the claim route web and remote sessions use because
they have no `gh` at all (`github-access.md`). That path is measured and does not
race (#3980): its `labels` is one array, "Labels to apply to this issue", applied
by one `PATCH /repos/{owner}/{repo}/issues/{n}` with set semantics, and
`pkg/github/issues.go` contains no `errgroup`, `wg.Go` or `go func(` at all --
nor the `addLabelsToLabelable`/`removeLabelsFromLabelable` delta mutations the
`gh` race is built from (github/github-mcp-server @ 7d13a7ad). So the uncovered
route is safe rather than unguarded, and a pattern matching MCP call text would
be a false-positive source guarding nothing.

Its hazard is a different one this test does not attempt to cover: PATCH set
semantics mean `labels` is the complete final list, so sending one label drops
the rest. That is a deterministic lost update, not a race, and the server errors
when the applied set does not match the requested one.

Run directly: python3 tools/test_no_racing_label_edit.py
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import label_hygiene_harness as harness  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

failures: list[str] = []
unmeasurable: list[str] = []
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


# The third state, exit 3: measured-and-fine and could-not-measure must not
# share a code, or a box that answered nothing reports that everything is well
# (`guards-need-a-third-state.md`). Deliberately not a FAIL either -- a missing
# `jq` is not the workflow racing, and sending a reader to the wrong remedy is
# how a correct guard gets deleted.
def cannot_measure(label: str, detail: str = "") -> None:
    unmeasurable.append(label)
    print(f"UNMEASURABLE  {label}")
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

# The one live single-call site, and the EXEMPT_WORKFLOW entry above is what
# makes this suite skip it. So the exemption is discharged by running the
# release step's own shell against a fixture issue and reading the command it
# actually issues -- never by matching the filter's spelling in the source.
#
# #4301: the source match was the whole check here, and a YAML comment quoting
# `select(. != "status: ready")` satisfied it with the real filter deleted --
# 10 passed, exit 0, over a workflow that races. A `run:` block resolved out of
# parsed YAML and executed cannot be written by a comment.
#
# Trap: the label the edit ADDS is derived from a first run, never named here.
# A check spelling `status: ready` itself still passes -- vacuously -- the day
# the workflow adds some other label, which is this same false green one step
# along.
if not os.path.exists(os.path.join(ROOT, EXEMPT_WORKFLOW)):
    check("issue-label-hygiene.yml is present to be checked", False, EXEMPT_WORKFLOW)
elif harness.missing_tools():
    cannot_measure(
        "the exempted workflow's single `gh issue edit` names no label in both "
        "its add-list and its remove-list",
        "cannot run the workflow's shell: "
        + ", ".join(harness.missing_tools()) + " not found",
    )
else:
    RELEASE = harness.run_block("release-part-of-issues")

    def resolved_edits(label_names: list[str]) -> tuple[list[str], str, list[str]]:
        """The `gh issue edit` calls the release step makes for an issue with these labels.

        Returns the fixture it used as well, so the non-vacuity check below reads
        the labels the step was ACTUALLY handed rather than a list recomputed
        beside the call -- a recomputed one stays green when the call is changed
        to pass something else, which is the check going vacuous unnoticed.
        """
        _rc, out, calls = harness.invoke(
            RELEASE,
            {
                "PR_NUMBER": "999",
                "PR_BODY": "Part of #42",
                "PR_HEAD_REF": "agent/fbk-2/issue-42",
                "PR_LABELS": json.dumps(["agent: fbk-2"]),
                "EDIT_RC": "0",
            },
            issue_json={"state": "OPEN",
                        "labels": [{"name": n} for n in label_names]},
        )
        return ([c for c in calls if c.startswith("issue edit")], out,
                list(label_names))

    BASE = ["status: in-progress", "agent: fbk-2", "bug"]
    first, first_out, _ = resolved_edits(BASE)
    added = harness.flag_values(first[0], "add-label") if len(first) == 1 else []

    if len(first) != 1 or not added:
        # Not a FAIL: the release step no longer resolves to one `gh issue edit`
        # carrying an --add-label, so there is nothing here to call racing or
        # safe. What it does mean is that the EXEMPT_WORKFLOW skip is now
        # resting on nothing, which is a person's call rather than a verdict.
        cannot_measure(
            "the exempted workflow's single `gh issue edit` names no label in both "
            "its add-list and its remove-list",
            f"expected one `gh issue edit ... --add-label X`, got {first!r}\n{first_out}",
        )
    else:
        second, second_out, second_fixture = resolved_edits(BASE + added)
        removed = harness.flag_values(second[0], "remove-label") if len(second) == 1 else []
        both = sorted(set(added) & set(removed))
        check(
            "the exempted workflow's single `gh issue edit` names no label in both "
            "its add-list and its remove-list -- gh dispatches the two as unordered "
            "concurrent mutations and drops the loser (#3930)",
            len(second) == 1 and not both,
            f"adds {added!r} and removes {removed!r}; both: {both!r}\n"
            f"resolved: {second!r}\n{second_out}",
        )
        check(
            "...measured against an issue that actually carries every label being "
            "added, so the check above cannot pass vacuously -- an unfiltered jq "
            "would have put each of them in the removals",
            set(added) <= set(second_fixture),
            f"added={added!r} was not all present in the fixture={second_fixture!r}",
        )

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
tail = f", {len(unmeasurable)} unmeasurable" if unmeasurable else ""
print(f"{passes} passed, {len(failures)} failed{tail} "
      f"({scanned} file(s) carried both flag names)")
# A real failure outranks an unmeasurable one: exit 1 says the shape is present,
# exit 3 says this box could not establish it either way.
sys.exit(1 if failures else (3 if unmeasurable else 0))
