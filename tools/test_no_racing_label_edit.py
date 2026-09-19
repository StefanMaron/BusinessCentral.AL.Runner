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


# The labels the step was ACTUALLY handed are read back by the harness, out of
# the environment block it executed the step with -- see harness.drive_step.
#
# #4305: the read used to return build()'s ARGUMENT, so a builder that stopped
# honouring it left the non-vacuity check below comparing the edit against a
# list nothing was driven with -- measured green (13 passed, exit 0) with the
# builder ignoring its input and the real race present.
#
# #4315: the read then took the fixture through a SECOND channel, alongside the
# one that drove the step, so a caller handing the two different values got a
# green over a genuinely racing workflow. `drive_step` runs the step and reads
# the fixture back inside one call, from the executed environment the caller
# never holds, so there is nothing to mismatch.
#
# Trap: there is no one expression that reads the labels, and picking either one
# for both jobs rebuilds the #4305 defect. In issue-label-hygiene.yml,
# `strip-labels-on-close` reads the names out of its `LABELS:` env value;
# `release-part-of-issues` is never given LABELS at all and reads them from
# `gh issue view --json state,labels`, which the harness answers from the
# ISSUE_JSON it serialised into that same environment. So the reader is part of
# each job's entry in JOBS, beside the builder that writes it -- but both now
# read the ONE environment the step ran with.


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
        "the exempted workflow's `gh issue edit` calls name no label in both "
        "their add-list and their remove-list",
        "cannot run the workflow's shell: "
        + ", ".join(harness.missing_tools()) + " not found",
    )
else:
    # BOTH jobs, because EXEMPT_WORKFLOW skips the whole FILE from the scan above,
    # so the exemption has to be discharged for every `gh issue edit` the file can
    # resolve to. `strip-labels-on-close` is safe today only because it adds
    # nothing -- an --add-label appended to it would need the same filter, and a
    # check reading only the release job would not say so.
    # (job id, build the fixture, read the labels back out of it). The third
    # entry is the channel that job's `run:` block reads -- see read_fixture.
    JOBS = [
        ("strip-labels-on-close",
         lambda labels: ({"ISSUE": "42", "LABELS": json.dumps(labels)}, None),
         lambda ran: json.loads(ran["LABELS"])),
        ("release-part-of-issues",
         lambda labels: ({"PR_NUMBER": "999", "PR_BODY": "Part of #42",
                          "PR_HEAD_REF": "agent/fbk-2/issue-42",
                          "PR_LABELS": json.dumps(["agent: fbk-2"]),
                          "EDIT_RC": "0"},
                         {"state": "OPEN",
                          "labels": [{"name": n} for n in labels]}),
         # ISSUE_JSON is what the stub answers `gh issue view` from, so this is
         # the labels the step was told the issue carries.
         lambda ran: [l["name"] for l in json.loads(ran["ISSUE_JSON"])["labels"]]),
    ]
    BASE = ["status: in-progress", "agent: fbk-2", "bug"]

    for job_id, build, read_labels in JOBS:
        block = harness.run_block(job_id)

        def resolved_edits(label_names: list[str]
                           ) -> tuple[list[str], str, list[str] | None, str]:
            """The `gh issue edit` calls this job makes for an issue with these labels.

            Also returns the fixture, read back by the harness out of the
            environment it EXECUTED the step with -- never `label_names`, which
            is what was requested rather than what the step was handed (#4305),
            and never a second value this function holds alongside (#4315).
            One call drives the step and reads it back, so the two cannot differ.
            """
            env, issue_json = build(label_names)
            _rc, out, calls, fixture, why = harness.drive_step(
                block, env, issue_json, read_labels)
            return ([c for c in calls if c.startswith("issue edit")], out,
                    fixture, why)

        first, first_out, _, _ = resolved_edits(BASE)
        if len(first) != 1:
            # Not a FAIL: the job no longer resolves to exactly one `gh issue edit`,
            # so there is nothing here to call racing or safe. What it does mean is
            # that the EXEMPT_WORKFLOW skip is now resting on nothing for this job,
            # which is a person's call rather than a verdict.
            cannot_measure(
                f"{job_id}'s `gh issue edit` names no label in both its add-list "
                "and its remove-list",
                f"expected exactly one `gh issue edit`, got {first!r}\n{first_out}",
            )
            continue

        added = harness.flag_values(first[0], "add-label")
        second, second_out, second_fixture, fixture_why = resolved_edits(BASE + added)
        removed = harness.flag_values(second[0], "remove-label") if len(second) == 1 else []
        both = sorted(set(added) & set(removed))
        check(
            f"{job_id}'s `gh issue edit` names no label in both its add-list and "
            "its remove-list -- gh dispatches the two as unordered concurrent "
            "mutations and drops the loser (#3930)",
            len(second) == 1 and not both,
            f"adds {added!r} and removes {removed!r}; both: {both!r}\n"
            f"resolved: {second!r}\n{second_out}",
        )
        if second_fixture is None:
            cannot_measure(
                f"...and {job_id} was measured against an issue carrying every "
                "label it adds",
                "could not read the fixture back through the channel the step "
                f"reads: {fixture_why}",
            )
        else:
            # A fixture carrying MORE than was asked for still satisfies this --
            # the claim is that every added label was present, not that the
            # fixture is exactly BASE + added. A fixture carrying LESS is the
            # vacuity this guards against.
            check(
                f"...and {job_id} was measured against an issue carrying every label it "
                "adds, so the check above cannot pass vacuously -- an unfiltered jq "
                "would have put each of them in the removals",
                set(added) <= set(second_fixture),
                f"added={added!r} was not all present in the fixture the step was "
                f"handed={second_fixture!r}",
            )

    # ---- what makes the two checks above one measurement rather than two ----
    #
    # #4315: they used to be driven by separate calls -- `invoke(env, issue_json)`
    # for the race check and a reader handed the caller's own copies for the
    # non-vacuity check. A caller passing a DIFFERENT fixture to each got both
    # checks green over a genuinely racing workflow: the race check saw labels
    # not including the one the edit adds, so found nothing in both lists, while
    # the non-vacuity check was satisfied by the other fixture.
    #
    # These arms pin the property that closed it. They exercise the harness seam
    # itself, not the workflow, because that is where the two channels were.
    PROBE_BLOCK = harness.run_block("release-part-of-issues")
    PROBE_ENV = {"PR_NUMBER": "999", "PR_BODY": "Part of #42",
                 "PR_HEAD_REF": "agent/fbk-2/issue-42",
                 "PR_LABELS": json.dumps(["agent: fbk-2"]), "EDIT_RC": "0"}
    PROBE_LABELS = ["status: in-progress", "agent: fbk-2", "bug"]
    PROBE_JSON = {"state": "OPEN",
                  "labels": [{"name": n} for n in PROBE_LABELS]}

    # 1. The reader is handed the environment the step EXECUTED with, not the
    #    caller's copies. That is the whole of the fix: a reader given the
    #    executed environment cannot be shown a fixture the step did not run
    #    with, so there is no second channel to disagree with the first.
    seen: list[dict] = []

    def _capture(ran):
        seen.append(dict(ran))
        return [l["name"] for l in json.loads(ran["ISSUE_JSON"])["labels"]]

    _rc, _out, probe_calls, probe_fixture, probe_why = harness.drive_step(
        PROBE_BLOCK, PROBE_ENV, PROBE_JSON, _capture)
    probe_edits = [c for c in probe_calls if c.startswith("issue edit")]
    # Every label the reader answered is one the step's own `gh issue edit`
    # named -- read off the resolved command, which is the step's behaviour
    # rather than anything this file held. The edit removes the status:/agent:
    # ones, so that subset is the overlap the two channels could disagree on.
    edit_removals = set(harness.flag_values(probe_edits[0], "remove-label")) if probe_edits else set()
    expected_removals = {n for n in PROBE_LABELS
                         if n.startswith("status:") or n.startswith("agent:")}
    check(
        "drive_step hands the reader the environment the step EXECUTED with, so "
        "the fixture the non-vacuity check inspects is the one that drove the "
        "race check -- two channels is what let mismatched inputs pass green (#4315)",
        len(seen) == 1
        and seen[0].get("ISSUE_JSON") == json.dumps(PROBE_JSON)
        and probe_fixture == PROBE_LABELS
        and edit_removals == expected_removals,
        f"reader saw {len(seen)} env(s); ISSUE_JSON="
        f"{(seen[0].get('ISSUE_JSON') if seen else None)!r}; "
        f"fixture={probe_fixture!r}; edit removed {sorted(edit_removals)!r}, "
        f"expected {sorted(expected_removals)!r}",
    )

    # 2. ...and the reader is given ONE argument, so a caller cannot hand it a
    #    fixture alongside. A two-argument reader would be passed the caller's
    #    own `issue_json`, which is exactly the second channel removed here --
    #    so the signature is load-bearing, not a style choice.
    import inspect  # noqa: E402  (local: only this arm needs it)
    try:
        params = list(inspect.signature(harness.drive_step).parameters)
    except (TypeError, ValueError) as exc:
        params = [f"<unreadable: {exc}>"]
    # A reader wanting a second argument gets NO fixture -- the seam has only
    # the executed environment to give it, so the call fails and comes back as
    # the unreadable-channel third state. It cannot quietly be handed the
    # caller's `issue_json` instead, which is the channel #4315 was about.
    two_arg_fixture, two_arg_why = harness.drive_step(
        PROBE_BLOCK, PROBE_ENV, PROBE_JSON, lambda ran, alongside: [])[3:]
    check(
        "...and a reader wanting a fixture ALONGSIDE the executed environment "
        "gets none -- the seam has only the one to give, so a second channel "
        "cannot be reintroduced by widening the reader (#4315)",
        params == ["block", "env", "issue_json", "read_labels"]
        and two_arg_fixture is None and "TypeError" in two_arg_why,
        f"drive_step params={params!r}; two-argument reader -> "
        f"({two_arg_fixture!r}, {two_arg_why!r})",
    )

    # 3. THE THIRD STATE SURVIVES. A channel the reader cannot read must come
    #    back as (None, why) so the caller spells it UNMEASURABLE and exits 3 --
    #    never as a FAIL, which would send a reader to the workflow for a fault
    #    in this file (`guards-need-a-third-state.md`). Both shapes: a reader
    #    that raises, and one that answers something that is not a label list.
    raised_fixture, raised_why = harness.drive_step(
        PROBE_BLOCK, PROBE_ENV, PROBE_JSON,
        lambda ran: json.loads(ran["NOT_A_KEY_THE_STEP_HAS"]))[3:]
    shaped_fixture, shaped_why = harness.drive_step(
        PROBE_BLOCK, PROBE_ENV, PROBE_JSON, lambda ran: "status: ready")[3:]
    check(
        "an unreadable channel still comes back as (None, why) rather than "
        "raising or answering a label list -- that is what the caller above "
        "spells UNMEASURABLE, and exit 3 rather than exit 1 depends on it",
        raised_fixture is None and "KeyError" in raised_why
        and shaped_fixture is None and "did not yield a list" in shaped_why,
        f"raised -> ({raised_fixture!r}, {raised_why!r}); "
        f"mis-shaped -> ({shaped_fixture!r}, {shaped_why!r})",
    )

    # 4. THE GREEN CONTROL. Everything above asserts the guard refuses
    #    something; a guard that refused EVERYTHING would pass all of them by
    #    construction. This is the arm that fails if the seam stops letting a
    #    genuinely-fine reading through: an honest reader on the real workflow
    #    yields a real label list, no refusal reason, and a resolved edit -- the
    #    ordinary case the two jobs above are measured with.
    ctl_rc, _ctl_out, ctl_calls, ctl_fixture, ctl_why = harness.drive_step(
        PROBE_BLOCK, PROBE_ENV, PROBE_JSON,
        lambda ran: [l["name"] for l in json.loads(ran["ISSUE_JSON"])["labels"]])
    check(
        "the control: an honest reader on the real workflow is NOT refused -- "
        "a seam that answered (None, why) for everything would satisfy every "
        "refusal arm above and measure nothing",
        ctl_fixture == PROBE_LABELS and ctl_why == "" and ctl_rc == 0
        and len([c for c in ctl_calls if c.startswith("issue edit")]) == 1,
        f"rc={ctl_rc} fixture={ctl_fixture!r} why={ctl_why!r} "
        f"edits={[c for c in ctl_calls if c.startswith('issue edit')]!r}",
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
