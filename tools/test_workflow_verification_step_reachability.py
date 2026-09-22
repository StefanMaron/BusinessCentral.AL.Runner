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

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

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
    ("bc-tests.yml", "Run rnce as two independent bundles"): {
        "step_if": "${{ always() && hashFiles('tests/runner-extras/rnce-second/**/*.al') != '' }}",
        "job_if": None, "step_coe": None, "job_coe": TEST_JOB_COE,
        "why": "#4452's page/report cached-null pair. Skips only when that suite tree is "
               "absent, and the glob is asserted to resolve against real files by "
               "MetaObjectCacheNullBundleShapeTests -- a glob matching nothing would SKIP "
               "the step, and a skipped step reports success. This is the only invocation "
               "that runs two INDEPENDENT bundles reaching the page metadata cache, so "
               "narrowing it retires the coverage silently.",
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
    ("bc-tests.yml", "Run metatable-cache-null as two independent bundles"): {
        "step_if": "${{ always() && hashFiles('tests/runner-extras/metatable-cache-null-second/**/*.al') != '' }}",
        "job_if": None, "step_coe": None, "job_coe": TEST_JOB_COE,
        "why": "#4450. Same profile and same reason as the dep-tableext entry above: "
               "AlRunner.Tests/MetaTableCacheNullBundleShapeTests pins this exact expression "
               "from the C# side AND resolves the glob against real files, which is the check "
               "this file does not attempt -- a hashFiles() matching nothing SKIPS the step, "
               "and a skipped step reports success. Both compare against the YAML on every "
               "run, so neither can drift silently.",
    },
    ("bc-tests.yml", "Run app-group-visibility c+b as ordered bundles"): {
        "step_if": "${{ always() && hashFiles('tests/runner-extras/app-group-visibility-c/**/*.al') != '' }}",
        "job_if": None, "step_coe": None, "job_coe": TEST_JOB_COE,
        "why": "#4457. Same profile and same reason as the two entries above: "
               "AlRunner.Tests/AppGroupVisibilitySiblingSourceDepStepTests pins this exact "
               "expression from the C# side, resolves the glob against real files, and "
               "EXECUTES the step's verification block against log fixtures -- so a guard "
               "disarmed by `exit 0`, `missing=0` or a dropped `( |$)` reds there rather "
               "than passing forever. This is the only invocation in CI whose bundle "
               "declares a dependency on a sibling dir that is NOT itself passed as a "
               "bundle, which is what routes through BuildSiblingSourceDeps at all; "
               "narrowing it makes that registration unexercised again.",
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


# A verdict is `exit <non-zero>` wherever a COMMAND may start, not only at the start of
# a line: `; exit 1`, `|| { ...; exit 1; }`, `&& exit 1` and a `case` arm's `*) ...;
# exit 1 ;;` are all ordinary shapes. Anchoring at `^` missed every one of them, so a
# guard step written as a one-liner joined no population and got no entry, silently
# (#4296). Trap: `bc-tests.yml / Resolve BC versions` carries THREE case-arm verdicts
# and was in the population only because it also has start-of-line `exit 1` lines --
# delete those and the step vanished from the census with nothing to say so.
_EXIT = re.compile(
    r"""(?:^|[;&|{(]|\b(?:then|else|do)\b)\s*exit\s+(?P<code>["']?\$?\w+)""", re.M)


def is_verification_step(run: str) -> bool:
    """A step whose failure is the only thing asserting a property.

    Both halves are needed: `::error::` alone also matches a step that merely reports,
    and an `exit` alone matches every script. `exit 0` is excluded -- propagating
    success is not a verdict.
    """
    code = strip_bash_comments(run)
    if "::error::" not in code:
        return False
    return any(m.group("code").strip("\"'") != "0" for m in _EXIT.finditer(code))


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

    # A pin is only worth what the comparison behind it is worth. `step_coe`/`job_coe`
    # reach `profile` only when coe_state says `expression`, so a coe_state that stops
    # discriminating drops them silently -- blinding it to `absent` took the run from 57
    # checks to 51 at exit 0, and reported the corpus step, whose job-level switch is the
    # one thing that could stop its failures gating, as "unconditional, so nothing can
    # skip or disarm it" (#4296). An entry that declares a pin now asserts it was read.
    for lvl, state, declared in (
        ("step", s["step_coe_state"], (entry or {}).get("step_coe")),
        ("job", s["job_coe_state"], (entry or {}).get("job_coe")),
    ):
        if declared is not None:
            check(f"{where}: its pinned {lvl} `continue-on-error:` is still read as an "
                  "expression, so the pin is actually compared",
                  state == "expression",
                  f"REVIEWED pins {lvl} `continue-on-error: {declared}`, but coe_state now "
                  f"reads that value as {state!r}, so the pin below is never compared and "
                  "this step's disarm switch stops being checked while the run still "
                  "reports success (#4296).")

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

# ---------------------------------------------------------------------------------
# `continue-on-error: ${{ !matrix.target.required }}` disarms EVERY verification step in
# the `test` job for any leg resolved not-required, so "every leg is required" is the one
# premise the whole file above rests on. It is checked by RESOLVING the matrix, not by
# reading the source that computes it: the previous `REQ=True` grep was wrong in both
# directions -- `REQ="$INFORMATIONAL"` reinstated an informational tier at 57 passed /
# exit 0, while a trailing comment on the same line went red (#4296).
#
# `matrix`, not `versions-json`: versions-json is flattened to bare version STRINGS and
# carries no `required` key at all. `matrix` is what the `test` job reads.
#
# Trap: `bash -e` is GitHub's own default shell for a `run:` block. Keep it -- a plain
# `bash` here would measure a script CI never executes.
RESOLVE_STEP = "Resolve BC versions"
DOTNET_STUB = """#!/bin/sh
# `dotnet run --project tools/DownloadArtifacts -- resolve-version <prefix> dummy`.
# Stubbed so the matrix resolves without a network round trip; only REQ is under test.
want=0
for a in "$@"; do
  if [ "$want" = "1" ]; then echo "$a.99999.12345"; exit 0; fi
  [ "$a" = "resolve-version" ] && want=1
done
exit 1
"""


def prefixes(rel: str) -> list[str]:
    """The prefixes the workflow itself reads: `grep -v '^#' | tr '\\n' ' '`.

    Trap: both files put every prefix on ONE space-separated line, so counting lines
    answers 1 and the count floor below then fails for a reason that has nothing to do
    with the matrix. Split on whitespace, as the workflow does.
    """
    with open(os.path.join(ROOT, rel), encoding="utf-8") as fh:
        body = "\n".join(ln for ln in fh.read().splitlines()
                         if not ln.lstrip().startswith("#"))
    return body.split()


def resolved_matrix(event: str) -> tuple[list[dict] | None, str]:
    """Run `Resolve BC versions` for one event and read its `matrix` output back.

    Returns (entries, detail); entries is None when the measurement could not be TAKEN,
    which stays distinct from a measured failure (guards-need-a-third-state.md).
    """
    try:
        with open(os.path.join(WF_DIR, "bc-tests.yml"), encoding="utf-8") as fh:
            doc = yaml.safe_load(fh)
    except (OSError, yaml.YAMLError) as exc:
        return None, f"bc-tests.yml could not be read: {exc}"
    runs = [st.get("run") for j in (doc.get("jobs") or {}).values() if isinstance(j, dict)
            for st in (j.get("steps") or []) if isinstance(st, dict)
            and st.get("name") == RESOLVE_STEP]
    if len(runs) != 1 or not isinstance(runs[0], str):
        return None, (f"expected exactly one step named {RESOLVE_STEP!r} in bc-tests.yml, "
                      f"found {len(runs)} -- renamed or removed, so the premise below "
                      "cannot be measured rather than being satisfied")
    if not shutil.which("bash"):
        return None, "no bash on PATH, so the resolve script cannot be executed"
    with tempfile.TemporaryDirectory() as tmp:
        binned = os.path.join(tmp, "bin")
        os.mkdir(binned)
        stub = os.path.join(binned, "dotnet")
        with open(stub, "w", encoding="utf-8") as fh:
            fh.write(DOTNET_STUB)
        os.chmod(stub, 0o755)
        out = os.path.join(tmp, "github_output")
        open(out, "w", encoding="utf-8").close()
        env = dict(os.environ, PATH=binned + os.pathsep + os.environ.get("PATH", ""),
                   GITHUB_OUTPUT=out, GATING_EVENT=event, BC_VERSION_FILTER="")
        try:
            proc = subprocess.run(["bash", "-e", "-c", runs[0]], cwd=ROOT, env=env,
                                  capture_output=True, text=True, timeout=300)
        except (OSError, subprocess.SubprocessError) as exc:
            return None, f"the resolve script could not be run for {event}: {exc}"
        with open(out, encoding="utf-8") as fh:
            lines = [ln for ln in fh if ln.startswith("matrix=")]
    if not lines:
        return None, (f"the resolve script emitted no `matrix=` output for {event} "
                      f"(rc={proc.returncode}); tail: {proc.stderr.strip()[-400:]!r}")
    try:
        entries = json.loads(lines[-1].split("=", 1)[1])["target"]
    except (ValueError, KeyError, TypeError) as exc:
        return None, f"the `matrix=` output for {event} did not parse: {exc}"
    if not isinstance(entries, list):
        return None, f"the `matrix=` target for {event} is not a list"
    return entries, f"rc={proc.returncode}"


for event, source in (("push", ".github/bc-versions.txt"),
                      ("pull_request", ".github/pr-bc-versions.txt")):
    entries, detail = resolved_matrix(event)
    if entries is None:
        unmeasurable.append(f"could not resolve the {event} matrix: {detail}")
        continue
    try:
        want = len(prefixes(source))
    except OSError as exc:
        unmeasurable.append(f"{source} could not be read: {exc}")
        continue
    # The count floor is what stops this passing vacuously: "every entry is required" is
    # true of an empty list, and of a one-leg list that dropped seven legs silently.
    check(f"the resolved {event} matrix has one leg per {source} prefix",
          len(entries) == want,
          f"resolved {len(entries)} leg(s), {source} lists {want} prefix(es) -- so the "
          "required-ness check below would be measuring a matrix that is not the one CI "
          "runs")
    not_required = [e.get("bc-version") for e in entries if e.get("required") is not True]
    check(f"every leg of the resolved {event} matrix is marked required",
          not not_required,
          f"{not_required} resolve to required=false, and "
          "`continue-on-error: ${{ !matrix.target.required }}` therefore disarms EVERY "
          "verification step in the `test` job on those legs: they run, print ::error::, "
          "and the leg stays GREEN. The workflow's own comment records an informational "
          "tier that 'had failed on every single run since the matrix landed without "
          "anyone being told' (#4255).")

# ---------------------------------------------------------------------------------
# The detectors are the only reason any of the above has a population to check, and a
# detector that stops discriminating shrinks the run at exit 0 rather than failing it.
# So they are pinned directly: blinding coe_state to `absent` dropped 57 checks to 51
# and reported the corpus step as unconditional; both tables below go red instead.
DETECTOR_CASES: list[tuple[str, str, bool]] = [
    ("a start-of-line exit", 'echo "::error::bad"\nexit 1', True),
    ("a one-liner `if`", 'if [ ! -f x ]; then echo "::error::gone"; exit 1; fi', True),
    ("a `||` brace group", 'foo || { echo "::error::failed"; exit 1; }', True),
    ("an `&&` chain", 'test -f x && echo "::error::present" && exit 1', True),
    ("a `case` arm", 'case "$v" in\n  ok) ;;\n  *) echo "::error::bad"; exit 1 ;;\nesac',
     True),
    ("a step with no ::error:: marker", 'echo nope\nexit 1', False),
    ("a step that only reports", 'echo "::error::just saying"', False),
    ("a step whose only exit is 0", 'echo "::error::x"\nexit 0', False),
    ("an exit inside a comment only", '# echo "::error::x"; exit 1\necho hi', False),
]
for label, script, want in DETECTOR_CASES:
    check(f"is_verification_step sees a verdict written as {label}"
          if want else f"is_verification_step does not claim {label}",
          is_verification_step(script) is want,
          f"got {is_verification_step(script)!r}, expected {want!r} for {script!r} -- a "
          "detector that stops matching drops steps from the population SILENTLY, which "
          "is the shape this whole file exists to refuse (#4296).")

COE_CASES: list[tuple[object, str]] = [
    (None, "absent"),
    (True, "literal-true"),
    (False, "literal-false"),
    ("${{ !matrix.target.required }}", "expression"),
    ("true", "literal-true"),
    ("false", "literal-false"),
]
for value, want in COE_CASES:
    check(f"coe_state reads {value!r} as {want}", coe_state(value) == want,
          f"got {coe_state(value)!r} -- a coe_state that stops discriminating drops the "
          "continue-on-error pins from every profile and shrinks the run at exit 0, "
          "reporting a disarmable step as unconditional (#4296).")

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
for u in unmeasurable:
    print(f"UNMEASURABLE - {u}")
print(f"{passes} passed, {len(failures)} failed, {len(unmeasurable)} unmeasurable")
if failures:
    sys.exit(1)
if unmeasurable:
    print("could not measure -- refusing to report a verdict (guards-need-a-third-state.md)")
    sys.exit(3)
sys.exit(0)
