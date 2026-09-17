#!/usr/bin/env python3
"""Every CI step that ASSERTS something must be reachable (#4255).

A guard over a workflow step has three levels, and pinning the first two says
nothing about the third:

  text          is the script spelled this way?   an Assert.Contains over the YAML
  effect        does it do the right thing?       #4253's extracted-and-executed harness
  reachability  does CI ever run it?              this file

A step whose `if:` is false is SKIPPED, and **a skipped step reports success** -- so a
guard can pin every character of a step and prove every branch of its logic while the
step never runs. Measured on `main` at d439c1e: appending `&& false` to the
runner-extras step's `if:`, which disables the whole of tests/runner-extras on every
leg, left `Failed: 0, Passed: 440` and all 36 tools/test_*.py green.

#4256 closed this for ONE step by hand. This file closes it for the population, which
is DERIVED rather than listed: a step whose `run:` emits `::error::` and propagates a
non-zero exit is a step whose failure is the only thing asserting a property. Write a
new guard step and it is in the population automatically.

REVIEWED cannot go stale in either direction -- an unreviewed conditional step FAILS,
and an entry naming a step that is gone FAILS -- which is what separates it from a
registry nobody maintains. There is no state in which forgetting it is quiet.

Trap: parse the YAML, never grep it. `continue-on-error` appears inside a COMMENT in
bc-tests.yml's `Resolve BC versions` run block, describing a tier that was removed, and
a text scan reads it as a step key -- the #3527 literal-stripping family arriving in
YAML. Read as a parsed step key it is correctly absent. The same scan, done by hand
against step-level `if:` only, found 4 conditional verification steps; the derived
population finds 7, because three sit under a JOB-level `if:`.

Trap: this guard is itself a CI step, so it is subject to its own subject. The
assertion at the bottom pins the step that runs it, because a reachability guard that
is skipped is the purest form of the defect.

Run directly: python3 tools/test_workflow_verification_step_reachability.py
Exit 0 = measured and fine, 1 = measured and broken, 3 = could not measure.
"""
from __future__ import annotations

import os
import re
import sys

import yaml

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WF_DIR = os.path.join(ROOT, ".github", "workflows")

# The `test` job's own disarm switch. Safe only because `Resolve BC versions` sets
# REQ=True unconditionally, which `matrix_required_is_unconditional` below pins --
# the workflow's own comment records an informational tier that "had failed on every
# single run since the matrix landed without anyone being told".
TEST_JOB_COE = "${{ !matrix.target.required }}"

# The smoke/pack jobs exist to check the PACKAGE, which a single-leg dispatch does not
# build. bc-leg-rerun.yml passes a filter; a pull request and a push pass ''.
SINGLE_LEG_DISPATCH = "inputs.bc-version-filter == ''"

# (workflow, step name) -> the reachability profile reviewed, and why it is acceptable.
#
# Four keys, because four different things skip or disarm a step: its own `if:`, its
# JOB's `if:`, and a `continue-on-error` at either level. All are compared
# whitespace-normalised -- YAML line-wrapping is not a semantic change -- and `None`
# means genuinely absent, which is never the same as an empty expression.
#
# A `continue-on-error` spelled as a literal `true` needs no entry and can never get
# one: it always fails. Only an EXPRESSION is reviewable, because what it evaluates to
# is not knowable from the file.
REVIEWED: dict[tuple[str, str], dict[str, str | None]] = {
    ("bc-tests.yml", "Run al-language corpus"): {
        "step_if": None, "job_if": None, "step_coe": None, "job_coe": TEST_JOB_COE,
        "why": "The corpus run itself. Nothing may narrow it; the job-level switch is "
               "pinned because it is the one thing that could stop its failures gating.",
    },
    ("bc-tests.yml", "Run runner-extras (if present)"): {
        "step_if": "${{ always() && hashFiles('tests/runner-extras/**/*.al') != '' }}",
        "job_if": None, "step_coe": None, "job_coe": TEST_JOB_COE,
        "why": "Skips only when the suite tree is absent, which is a legitimate checkout "
               "shape rather than a narrowing, and always() keeps it running after an "
               "earlier red step. Anything else here silently drops ALL of "
               "tests/runner-extras -- the mutation #4255 measured.",
    },
    ("bc-tests.yml", "Run tableextension-eviction suites as ordered bundles"): {
        "step_if": "${{ always() && hashFiles('tests/runner-extras-tableext-eviction/**/*.al') != '' }}",
        "job_if": None, "step_coe": None, "job_coe": TEST_JOB_COE,
        "why": "Same shape over the #4079/#4080 eviction pairs' own root. This ordered "
               "invocation is the only place their cross-bundle claim is exercised.",
    },
    ("bc-tests.yml", "Run dep-tableext-platform-base as ordered bundles"): {
        "step_if": "${{ always() && hashFiles('tests/runner-extras/dep-tableext-platform-base-main/**/*.al') != '' }}",
        "job_if": None, "step_coe": None, "job_coe": TEST_JOB_COE,
        "why": "#4256 pins this same expression from the C# side, with a per-step message "
               "and a glob-reachability check this file does not attempt. Both compare "
               "against the YAML on every run, so neither can drift silently.",
    },
    ("bc-tests.yml", "Run cold-cache binary smoke test"): {
        "step_if": None, "job_if": SINGLE_LEG_DISPATCH, "step_coe": None, "job_coe": None,
        "why": "Job `smoke` builds and probes the packaged binary, which a single-leg "
               "bc-leg-rerun dispatch does not produce. Every gating path -- pull "
               "request, push to main, the floor, the release -- passes an empty filter, "
               "so this runs wherever its verdict is required.",
    },
    ("bc-tests.yml", "Assert a package was actually produced"): {
        "step_if": None, "job_if": SINGLE_LEG_DISPATCH, "step_coe": None, "job_coe": None,
        "why": "Job `pack`, same reasoning as the smoke job above.",
    },
    ("bc-tests.yml", "Assert every BC-minor engine variant made it into the package"): {
        "step_if": None, "job_if": SINGLE_LEG_DISPATCH, "step_coe": None, "job_coe": None,
        "why": "Job `pack`, same reasoning. This is the step that would notice a missing "
               "engine variant, so a widened job condition would hide exactly that.",
    },
    ("pr-gate.yml", "Run every scripts/tests/*.test.py"): {
        "step_if": "steps.changed.outputs.run == 'true'",
        "job_if": None, "step_coe": None, "job_coe": None,
        "why": "Gated on the job's own change detection, which is that job's purpose. The "
               "step still refuses an empty glob, so a skip cannot be confused with a run "
               "that asserted nothing.",
    },
}

failures: list[str] = []
unmeasurable: list[str] = []
passes = 0


def check(desc: str, ok: bool, detail: str = "") -> None:
    global passes
    if ok:
        print(f"ok   - {desc}")
        passes += 1
    else:
        print(f"FAIL - {desc}" + (f": {detail}" if detail else ""))
        failures.append(desc)


def norm(expr: object) -> str | None:
    """Whitespace-normalise a condition. None stays None -- absent is not ''."""
    return None if expr is None else re.sub(r"\s+", " ", str(expr).strip())


def strip_bash_comments(run: str) -> str:
    """Drop whole-line `#` comments before looking for markers.

    Detection only, and it errs toward KEEPING a line: over-including a step costs one
    reviewed entry, while under-including one drops it from the population silently.
    """
    return "\n".join(l for l in run.split("\n") if not l.lstrip().startswith("#"))


def is_verification_step(run: str) -> bool:
    """A step whose failure is the only thing asserting a property.

    Both halves are needed: `::error::` alone also matches a step that merely reports,
    and an `exit` alone matches every script. `exit 0` is excluded -- propagating
    success is not a verdict.
    """
    code = strip_bash_comments(run)
    if "::error::" not in code:
        return False
    return re.search(r"(?m)^\s*exit\s+(?!0\s*$)[\"']?\$?\w", code) is not None


def coe_state(value: object) -> str:
    """absent | literal-false | literal-true | expression.

    The three-way split is the point. A literal `true` is always a defect on a
    verification step; an EXPRESSION cannot be evaluated from the file at all, so it is
    reviewed and pinned rather than guessed in either direction.
    """
    if value is None:
        return "absent"
    if isinstance(value, bool):
        return "literal-true" if value else "literal-false"
    text = str(value).strip()
    if "${{" in text:
        return "expression"
    return "literal-true" if text.lower() not in ("false", "0", "") else "literal-false"


def collect() -> list[dict]:
    found: list[dict] = []
    names = sorted(n for n in os.listdir(WF_DIR) if n.endswith((".yml", ".yaml")))
    if not names:
        unmeasurable.append(f"no workflow files under {WF_DIR}")
        return found
    for name in names:
        path = os.path.join(WF_DIR, name)
        try:
            with open(path, encoding="utf-8") as fh:
                doc = yaml.safe_load(fh)
        except yaml.YAMLError as exc:
            unmeasurable.append(f"{name} does not parse as YAML: {exc}")
            continue
        if not isinstance(doc, dict):
            unmeasurable.append(f"{name} did not parse to a mapping")
            continue
        jobs = doc.get("jobs")
        if not isinstance(jobs, dict):
            continue
        for job_id, job in jobs.items():
            if not isinstance(job, dict):
                continue
            steps = job.get("steps")
            if not isinstance(steps, list):
                continue
            for step in steps:
                if not isinstance(step, dict):
                    continue
                run = step.get("run")
                if not isinstance(run, str) or not is_verification_step(run):
                    continue
                found.append({
                    "wf": name, "job": str(job_id),
                    "name": str(step.get("name") or "<unnamed>"),
                    "step_if": norm(step.get("if")), "job_if": norm(job.get("if")),
                    "step_coe_state": coe_state(step.get("continue-on-error")),
                    "job_coe_state": coe_state(job.get("continue-on-error")),
                    "step_coe": norm(step.get("continue-on-error")),
                    "job_coe": norm(job.get("continue-on-error")),
                })
    return found


steps = collect()

if unmeasurable:
    for u in unmeasurable:
        print(f"UNMEASURABLE - {u}")
    print("\ncould not measure -- refusing to report a verdict (guards-need-a-third-state.md)")
    sys.exit(3)

if not steps:
    print("UNMEASURABLE - found no verification steps in any workflow. Either every "
          "::error:: guard step is gone, or is_verification_step stopped matching; both "
          "mean this file is asserting nothing.")
    sys.exit(3)

print(f"# {len(steps)} verification step(s) derived from .github/workflows/\n")

for s in steps:
    where = f"{s['wf']} / {s['name']}"
    entry = REVIEWED.get((s["wf"], s["name"]))

    literal_true = [
        lvl for lvl, st in (("step", s["step_coe_state"]), ("job", s["job_coe_state"]))
        if st == "literal-true"
    ]
    check(f"{where}: is not disarmed by a literal continue-on-error", not literal_true,
          f"{'/'.join(literal_true)}-level `continue-on-error: true` -- the step runs, "
          "prints ::error::, and the leg stays GREEN, so nothing it checks can fail CI "
          "(#4255). If deliberate, say in the same commit what else asserts this "
          "property, because nothing here would.")

    profile = [
        ("step `if:`", s["step_if"], (entry or {}).get("step_if")),
        ("job `if:`", s["job_if"], (entry or {}).get("job_if")),
    ]
    for lvl, state, actual, expected in (
        ("step", s["step_coe_state"], s["step_coe"], (entry or {}).get("step_coe")),
        ("job", s["job_coe_state"], s["job_coe"], (entry or {}).get("job_coe")),
    ):
        if state == "expression":
            profile.append((f"{lvl} `continue-on-error:`", actual, expected))

    if all(actual is None for _, actual, _ in profile):
        check(f"{where}: unconditional, so nothing can skip or disarm it", True)
        continue

    if entry is None:
        stated = ", ".join(f"{lbl}={actual!r}" for lbl, actual, _ in profile if actual)
        check(f"{where}: its reachability profile has been reviewed and pinned", False,
              f"{stated} -- this step ASSERTS something and can be SKIPPED or disarmed, "
              "and a skipped step reports SUCCESS. Add an entry to REVIEWED in this file "
              "recording the profile and why the step may carry one (#4255).")
        continue

    for label, actual, expected in profile:
        check(f"{where}: its {label} is what was reviewed", actual == expected,
              f"\n  expected: {expected!r}\n  actual:   {actual!r}\n"
              "  A step that is skipped, or whose failure is swallowed, reports SUCCESS -- "
              "so this edit disarms the step with nothing else going red. If it is "
              "deliberate, update REVIEWED in the same commit and say why the step should "
              "still run where its verdict is required (#4255).")

    check(f"{where}: its entry says WHY that profile is acceptable",
          bool(str(entry.get("why") or "").strip()),
          "an entry with no reason is a pin without a review")

live = {(s["wf"], s["name"]) for s in steps}
stale = sorted(k for k in REVIEWED if k not in live)
check("every REVIEWED entry still names a live verification step", not stale,
      f"{stale} -- renamed, made unconditional, or no longer a verification step. A pin "
      "for a step that is gone is what a broken detector looks like from the inside, so "
      "this is a failure rather than a tidy-up.")

with open(os.path.join(WF_DIR, "bc-tests.yml"), encoding="utf-8") as fh:
    bc_tests = fh.read()
check("bc-tests.yml marks every matrix leg required, which is the only reason the "
      "`test` job's continue-on-error expression is safe",
      re.search(r"(?m)^\s*REQ=True\s*$", bc_tests) is not None
      and re.search(r"(?m)^\s*REQ=False", bc_tests) is None,
      "`continue-on-error: ${{ !matrix.target.required }}` disarms EVERY verification "
      "step in that job for any leg marked not-required. The workflow's own comment "
      "records an informational tier that 'had failed on every single run since the "
      "matrix landed without anyone being told' (#4255).")

with open(os.path.join(WF_DIR, "pr-gate.yml"), encoding="utf-8") as fh:
    gate = yaml.safe_load(fh)
runner = [
    (j, st)
    for j in gate.get("jobs", {}).values() if isinstance(j, dict)
    for st in (j.get("steps") or []) if isinstance(st, dict)
    and "tools/test_*.py" in str(st.get("run", ""))
]
check("the step that runs this file is itself unconditional and not disarmed -- a "
      "reachability guard that is skipped is the purest form of the defect",
      len(runner) == 1
      and runner[0][1].get("if") is None and runner[0][0].get("if") is None
      and coe_state(runner[0][1].get("continue-on-error")) in ("absent", "literal-false")
      and coe_state(runner[0][0].get("continue-on-error")) in ("absent", "literal-false"),
      f"{len(runner)} matching step(s)" + (
          f"; step if={runner[0][1].get('if')!r} job if={runner[0][0].get('if')!r}"
          if runner else ", so this file may run nowhere"))

print("")
print(f"{passes} passed, {len(failures)} failed")
sys.exit(1 if failures else 0)
