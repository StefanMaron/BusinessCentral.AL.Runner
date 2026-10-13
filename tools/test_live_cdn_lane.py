#!/usr/bin/env python3
"""A test that needs the live BC artifact CDN must not run in the required suite (#5530).

A real download inside `dotnet test` is a coin flip on somebody else's CDN: the spawn cap is
180 s, the CDN is a shared Azure front door, and a slow answer reddened a REQUIRED leg twice
(two different tests of ProvisionExplicitModesTests, on two different commits). The flake itself
cannot be reproduced on demand, so what is pinned here is the structure that removes it:

  1. every test that calls LiveCdnGate.Require() carries [Trait("Category", "LiveCdn")];
  2. the required unit-test step runs with --filter "Category!=LiveCdn", so those tests are not
     in it;
  3. an advisory step (continue-on-error, with a timeout) runs exactly that category, so the
     real-download path is still measured, visibly, without being able to redden a leg.

Each half alone is the defect: the trait with no filter changes nothing, the filter with no
advisory step silently stops measuring the real download, and a test added later without the
trait is the original problem arriving again.

Scope, stated because a green here is easy to over-read: it recognises a live-CDN test by the
LiveCdnGate.Require() call. A test that spawns the runner against the real CDN WITHOUT that gate
is not seen (#5422 tracks two such tests); converting one means routing it through the gate or
an offline CDN, after which this guard covers it.

Trap: parse the YAML, never grep it (#4255). A comment in bc-tests.yml quoting the filter would
satisfy a text match with the real filter deleted.

Run directly: python3 tools/test_live_cdn_lane.py
Exit 0 = measured and fine, 1 = measured and broken, 3 = could not measure.
"""
from __future__ import annotations

import glob
import os
import re
import sys

import yaml

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TESTS_DIR = os.path.join(ROOT, "AlRunner.Tests")
WF_DIR = os.path.join(ROOT, ".github", "workflows")

TRAIT = 'Trait("Category", "LiveCdn")'
REQUIRED_FILTER = "Category!=LiveCdn"
ADVISORY_FILTER = "Category=LiveCdn"

# These two name the gate without being tests that reach the CDN: the gate itself, and the facts
# that drive Require() through its injected probe (LiveCdnGate.OverrideForTests). Neither spawns a
# runner or opens a socket.
GATE_FILES = {"LiveCdnGate.cs", "LiveCdnGateTests.cs"}

SIGNATURE = re.compile(
    r"^\s*(?:public|internal|private|protected)\s+(?:static\s+)?(?:async\s+)?"
    r"[\w<>\[\],.?()\s]+?\s(\w+)\s*\([^)]*\)\s*$")


def live_cdn_tests(cs_text: str) -> list[tuple[str, bool]]:
    """(method name, carries the trait) for every method calling LiveCdnGate.Require()."""
    lines = cs_text.replace("\r\n", "\n").split("\n")
    found: dict[str, bool] = {}
    for i, line in enumerate(lines):
        if "LiveCdnGate.Require()" not in line or line.lstrip().startswith("//"):
            continue
        sig = next((j for j in range(i, -1, -1) if SIGNATURE.match(lines[j])), None)
        if sig is None:
            found[f"<unknown method near line {i + 1}>"] = False
            continue
        attrs = []
        j = sig - 1
        while j >= 0 and lines[j].strip().startswith(("[", "//", "///")):
            attrs.append(lines[j])
            j -= 1
        name = SIGNATURE.match(lines[sig]).group(1)
        found[name] = any(TRAIT in a for a in attrs)
    return list(found.items())


def script_without_comments(run: str) -> str:
    """The step's shell text with comment lines dropped: a comment quoting the filter must not
    satisfy a check on the command (the step's own header explains its filter in one)."""
    return "\n".join(l for l in run.split("\n") if not l.lstrip().startswith("#"))


def unit_test_steps(workflow: dict) -> list[dict]:
    steps = []
    for job in (workflow.get("jobs") or {}).values():
        for step in job.get("steps") or []:
            if "dotnet test AlRunner.Tests" in str(step.get("run", "")):
                steps.append(step)
    return steps


def workflow_findings(name: str, workflow: dict) -> tuple[list[str], int, int]:
    """(findings, required-lane steps, advisory steps) for one parsed workflow."""
    findings: list[str] = []
    required = advisory = 0
    for step in unit_test_steps(workflow):
        label = f"{name}: step {step.get('name')!r}"
        run = script_without_comments(str(step["run"]))
        if REQUIRED_FILTER in run:
            required += 1
        elif ADVISORY_FILTER in run:
            advisory += 1
            if step.get("continue-on-error") is not True:
                findings.append(f"{label} runs the live-CDN category but is not `continue-on-error: true`, "
                                "so a slow CDN would redden the leg again")
            if not isinstance(step.get("timeout-minutes"), int):
                findings.append(f"{label} runs the live-CDN category with no `timeout-minutes`, so a stalled "
                                "CDN holds the leg until the job timeout")
        else:
            findings.append(f"{label} runs AlRunner.Tests with neither --filter \"{REQUIRED_FILTER}\" nor "
                            f"--filter \"{ADVISORY_FILTER}\", so it would run the live-CDN tests as part of a "
                            "gating suite")
    return findings, required, advisory


def main() -> int:
    cs_files = sorted(p for p in glob.glob(os.path.join(TESTS_DIR, "*.cs"))
                      if os.path.basename(p) not in GATE_FILES)
    wf_files = sorted(glob.glob(os.path.join(WF_DIR, "*.yml")))
    if not cs_files or not wf_files:
        print(f"UNMEASURABLE: found {len(cs_files)} test sources under {TESTS_DIR} and "
              f"{len(wf_files)} workflows under {WF_DIR}; the scan is looking in the wrong place.")
        return 3

    failures: list[str] = []
    population = 0
    for path in cs_files:
        with open(path, encoding="utf-8") as fh:
            for method, has_trait in live_cdn_tests(fh.read()):
                population += 1
                if not has_trait:
                    failures.append(f"{os.path.basename(path)}: {method} calls LiveCdnGate.Require() but "
                                    f"carries no [{TRAIT}], so the required suite runs it")
    if population == 0:
        print("UNMEASURABLE: no test calls LiveCdnGate.Require(); either every live-CDN test is gone "
              "(delete this guard and the advisory step with them) or the scan no longer recognises the "
              "call shape.")
        return 3

    required_total = advisory_total = 0
    for path in wf_files:
        with open(path, encoding="utf-8") as fh:
            try:
                workflow = yaml.safe_load(fh)
            except yaml.YAMLError as ex:
                print(f"UNMEASURABLE: {os.path.basename(path)} did not parse: {ex}")
                return 3
        findings, required, advisory = workflow_findings(os.path.basename(path), workflow or {})
        failures += findings
        required_total += required
        advisory_total += advisory
    if required_total == 0:
        failures.append(f"no workflow step runs AlRunner.Tests with --filter \"{REQUIRED_FILTER}\"")
    if advisory_total == 0:
        failures.append(f"no workflow step runs --filter \"{ADVISORY_FILTER}\", so the real-download path is "
                        "no longer measured anywhere")

    if failures:
        for f in failures:
            print(f"FAIL  {f}")
        return 1
    print(f"PASS  {population} live-CDN test(s) all carry the trait; {required_total} required step(s) "
          f"exclude the category; {advisory_total} advisory step(s) run it.")
    return 0


def self_test() -> int:
    """The guard's own logic on synthetic input, so a refusal path that never fires is visible."""
    ok = True

    def expect(label: str, cond: bool) -> None:
        nonlocal ok
        print(("PASS  " if cond else "FAIL  ") + label)
        ok &= cond

    marked = f'''
    [SkippableFact]
    [{TRAIT}]
    public void Marked()
    {{
        LiveCdnGate.Require();
    }}
    /// <summary>doc</summary>
    [SkippableFact]
    public void Unmarked()
    {{
        LiveCdnGate.Require();
    }}
    // LiveCdnGate.Require();
    '''
    expect("marked and unmarked tests are told apart",
           live_cdn_tests(marked) == [("Marked", True), ("Unmarked", False)])
    expect("a trait on the NEXT method does not leak backwards",
           live_cdn_tests(f'[Fact]\npublic void A()\n{{\n  LiveCdnGate.Require();\n}}\n'
                          f'[{TRAIT}]\n[Fact]\npublic void B()\n{{\n}}\n') == [("A", False)])

    def wf(run: str, **extra: object) -> dict:
        return {"jobs": {"j": {"steps": [{"name": "s", "run": run, **extra}]}}}

    f, r, a = workflow_findings("w", wf(f'dotnet test AlRunner.Tests/x.csproj --filter "{REQUIRED_FILTER}"'))
    expect("required step with the filter is clean", (f, r, a) == ([], 1, 0))
    f, r, a = workflow_findings("w", wf(f'# --filter "{REQUIRED_FILTER}" is explained here\n'
                                        'dotnet test AlRunner.Tests/x.csproj'))
    expect("a comment quoting the filter does not satisfy the check", len(f) == 1 and (r, a) == (0, 0))
    f, r, a = workflow_findings("w", wf("dotnet test AlRunner.Tests/x.csproj"))
    expect("an unfiltered unit-test step is a finding", len(f) == 1 and (r, a) == (0, 0))
    f, r, a = workflow_findings("w", wf(f'dotnet test AlRunner.Tests/x.csproj --filter "{ADVISORY_FILTER}"',
                                        **{"continue-on-error": True, "timeout-minutes": 6}))
    expect("advisory step with continue-on-error and a timeout is clean", (f, r, a) == ([], 0, 1))
    f, _, _ = workflow_findings("w", wf(f'dotnet test AlRunner.Tests/x.csproj --filter "{ADVISORY_FILTER}"',
                                        **{"timeout-minutes": 6}))
    expect("advisory step without continue-on-error is a finding", len(f) == 1)
    f, _, _ = workflow_findings("w", wf(f'dotnet test AlRunner.Tests/x.csproj --filter "{ADVISORY_FILTER}"',
                                        **{"continue-on-error": True}))
    expect("advisory step without a timeout is a finding", len(f) == 1)
    return 0 if ok else 1


if __name__ == "__main__":
    rc = self_test()
    if rc != 0:
        sys.exit(1)
    sys.exit(main())
