#!/usr/bin/env python3
"""Unit tests for check_expectation_gap_issues.py (#3089).

Same pattern as test_check_required_contexts.py and
test_check_closing_reference.sh: every case builds the condition it is about
DELIBERATELY, in a temp directory, so the suite proves the guard rather than
describing whatever `tests/expectations/` happens to contain today. A suite
that only asserted over the shipped manifest would go green the moment the
manifest emptied out, which is precisely the vacuous pass this guard exists to
prevent elsewhere.

Run: python3 .github/scripts/test_check_expectation_gap_issues.py
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

_spec = importlib.util.spec_from_file_location(
    "check_expectation_gap_issues",
    os.path.join(HERE, "check_expectation_gap_issues.py"),
)
cegi = importlib.util.module_from_spec(_spec)
# Registering before exec_module is the documented importlib recipe, and it is
# load-bearing here: @dataclass resolves annotations through
# sys.modules[cls.__module__], which is None for an unregistered module.
sys.modules[_spec.name] = cegi
_spec.loader.exec_module(cegi)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


ISSUE = "https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues"


def gap(issue: str, *, codeunit: str = "TP Blank Temporal Tests",
        method: str = "TestPageField_Value_BlankRecBoundDate_ReadsEmptyString",
        mode: str = "expect-fail-known-gap") -> dict:
    e = {
        "codeunitId": 60830,
        "CodeunitName": codeunit,
        "Method": method,
        "Mode": mode,
        "Note": "constructed by test_check_expectation_gap_issues.py",
    }
    if issue is not None:
        e["Issue"] = issue
    return e


def run(files: dict[str, object] | None, *, title: str = "", body: str = "",
        commits: str = "", argv: list[str] | None = None,
        repo: str = "StefanMaron/BusinessCentral.AL.Runner",
        manifest_dir: str | None = None) -> tuple[int, str]:
    """Write a synthetic manifest, run the guard, return (rc, combined output).

    `files` maps a file name to either a JSON-serialisable object or a raw
    string (so a malformed-JSON case can be expressed).
    """
    env_keys = ("PR_TITLE", "PR_BODY", "PR_COMMITS", "GITHUB_REPOSITORY")
    saved = {k: os.environ.get(k) for k in env_keys}
    tmp = None
    try:
        if manifest_dir is None:
            tmp = tempfile.TemporaryDirectory()
            manifest_dir = tmp.name
            for name, content in (files or {}).items():
                text = content if isinstance(content, str) else json.dumps(content, indent=2)
                with open(os.path.join(manifest_dir, name), "w", encoding="utf-8") as fh:
                    fh.write(text)
        os.environ["PR_TITLE"] = title
        os.environ["PR_BODY"] = body
        os.environ["PR_COMMITS"] = commits
        os.environ["GITHUB_REPOSITORY"] = repo
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            rc = cegi.main([manifest_dir] + list(argv or []))
        return rc, out.getvalue() + err.getvalue()
    finally:
        if tmp is not None:
            tmp.cleanup()
        for k, v in saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v


# ---------------------------------------------------------------------------
print("The gate: a PR that closes issue N may not leave an entry linking N")
# ---------------------------------------------------------------------------

rc, out = run({"known-gaps-blank-temporal.json": [gap(f"{ISSUE}/2361")]},
              body="Fixes the thing.\n\nCloses #2361\n")
check("a canonical 'Closes #N' with an entry linking N fails", rc == 1, f"rc={rc}")
check("...and the message names the manifest file",
      "known-gaps-blank-temporal.json" in out, out)
check("...and the method, so the author knows which entry to delete",
      "TestPageField_Value_BlankRecBoundDate_ReadsEmptyString" in out, out)
check("...and the issue number", "2361" in out, out)

rc, out = run({"known-gaps-blank-temporal.json": [gap(f"{ISSUE}/2361")]},
              body="Closes #3089\n")
check("an entry linking an OPEN issue this PR does not close passes",
      rc == 0, f"rc={rc}: {out}")

rc, out = run({"known-gaps-blank-temporal.json": [gap(f"{ISSUE}/9999")]},
              body="Closes #2361\n")
check("re-targeting the entry to a different issue in the same PR passes",
      rc == 0, f"rc={rc}: {out}")

rc, out = run({"known-gaps-blank-temporal.json": []},
              body="Closes #2361\n")
check("deleting the entry outright in the same PR passes",
      rc == 0, f"rc={rc}: {out}")

rc, out = run({"known-gaps-a.json": [gap(f"{ISSUE}/1")],
               "known-gaps-b.json": [gap(f"{ISSUE}/2361", codeunit="Other Cu")]},
              body="Closes #1\nCloses #2361\n")
check("a PR declaring several targets is checked against all of them",
      rc == 1 and "known-gaps-a.json" in out and "known-gaps-b.json" in out,
      f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]},
              title="fix(testpage): closes #2361", body="No linked issue: title only\n")
check("a closing reference in the PR TITLE is caught (GitHub honors it there too)",
      rc == 1, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]},
              body="Closes #3089\n",
              commits="fix(testpage): read blank dates\n\nIt also fixes #2361 along the way.\n")
check("a STRAY reference in a commit message is caught (route B, #2491)",
      rc == 1 and "commit message" in out.lower(), f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]},
              body="Closes StefanMaron/BusinessCentral.AL.Runner#2361\n")
check("the owner/repo#N form is caught", rc == 1, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]},
              body=f"Closes {ISSUE}/2361\n")
check("the full-URL form is caught", rc == 1, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]},
              body="Closes #3089\n\nThis fixes 2361 rendering glitches in the parser.\n")
check("a bare number with no '#' is not a reference (the #2129 false positive)",
      rc == 0, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap("https://github.com/other/repo/issues/2361")]},
              body="Closes #2361\n")
check("a bare '#N' does not match an entry linking ANOTHER repository's issue N",
      rc == 0, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap("https://github.com/other/repo/issues/2361")]},
              body="Closes other/repo#2361\n")
check("...but the explicit cross-repo form does match it", rc == 1, f"rc={rc}: {out}")

rc, out = run({"oos-http.json": [{"codeunitId": 1, "CodeunitName": "Cu", "Method": "M",
                                  "Mode": "expect-oos", "Reason": "external-http"}],
               "divergence-session.json": [{"codeunitId": 2, "CodeunitName": "Cu2", "Method": "M",
                                            "Mode": "expect-divergence", "Reason": "r",
                                            "Doc": "docs/scope.md#jobs"}]},
              body="Closes #2361\n")
check("expect-oos and expect-divergence entries are never flagged (only known-gaps carry an Issue)",
      rc == 0, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]},
              body="No linked issue: a docs typo\n")
check("a PR with no closing reference at all passes", rc == 0, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]},
              body="See #2361 for the background.\n\nCloses #3089\n")
check("referring to the gap issue WITHOUT a closing keyword passes",
      rc == 0, f"rc={rc}: {out}")


# ---------------------------------------------------------------------------
print()
print("Anti-fail-open: a check that cannot check must not report a pass")
# ---------------------------------------------------------------------------

rc, out = run({"known-gaps-x.json": [gap(None)]}, body="Closes #2361\n")
check("an expect-fail-known-gap entry with no Issue is a hard error, not a skip",
      rc == 2 and "Issue" in out, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap("TBD")]}, body="Closes #2361\n")
check("an Issue field that is not a resolvable issue URL is a hard error",
      rc == 2, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": "{ not json"}, body="Closes #2361\n")
check("malformed JSON in the manifest is a hard error", rc == 2, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]}, body="", title="", commits="")
check("blank title AND body AND commit messages is a hard error, not a vacuous pass",
      rc == 2, f"rc={rc}: {out}")

rc, out = run(None, body="Closes #2361\n",
              manifest_dir=os.path.join(tempfile.gettempdir(), "cegi-does-not-exist-3089"))
check("a missing manifest directory is a hard error (a broken checkout, not an empty manifest)",
      rc == 2, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361", mode="expect-oos")]},
              body="Closes #2361\n")
check("a known-gaps-*.json file holding no expect-fail-known-gap entry is a hard error "
      "(the prefix/Mode disagreement would otherwise silence the whole file)",
      rc == 2, f"rc={rc}: {out}")

rc, out = run({"oos-http.json": [{"codeunitId": 1, "CodeunitName": "Cu", "Method": "M",
                                  "Mode": "expect-oos", "Reason": "external-http"}]},
              body="Closes #2361\n")
check("a manifest with no known-gaps-*.json file at all passes, and SAYS it checked nothing",
      rc == 0 and "0 expect-fail-known-gap" in out, f"rc={rc}: {out}")

rc, out = run({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]}, body="Closes #3089\n")
check("a passing run reports how many entries it actually scanned",
      "1 expect-fail-known-gap" in out, out)


# ---------------------------------------------------------------------------
print()
print("The non-blocking sweep: entries linking an already-closed issue")
# ---------------------------------------------------------------------------

def run_report(files: dict[str, object], states: dict[tuple[str, str, int], str | None]):
    original = cegi.issue_state
    cegi.issue_state = lambda owner, repo, n: states.get((owner, repo, n))
    try:
        return run(files, body="Closes #3089\n", argv=["--report-closed-issues"])
    finally:
        cegi.issue_state = original


key = ("StefanMaron", "BusinessCentral.AL.Runner", 2361)

rc, out = run_report({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]}, {key: "closed"})
check("an entry linking a CLOSED issue is reported as a warning", "::warning" in out, out)
check("...naming the issue and the entry",
      "2361" in out and "known-gaps-x.json" in out, out)
check("...and does NOT fail the job (a closed issue does not by itself prove staleness)",
      rc == 0, f"rc={rc}: {out}")

rc, out = run_report({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]}, {key: "open"})
check("an entry linking an OPEN issue produces no warning",
      rc == 0 and "::warning" not in out, f"rc={rc}: {out}")

rc, out = run_report({"known-gaps-x.json": [gap(f"{ISSUE}/2361")]}, {})
check("an unreachable API is LOUD about not having checked, and still does not fail",
      rc == 0 and "::warning" in out and "could not" in out.lower(), f"rc={rc}: {out}")
check("...and says explicitly that the sweep did not run for that entry",
      "2361" in out, out)


# ---------------------------------------------------------------------------
print()
print("Drift guards")
# ---------------------------------------------------------------------------

_pb_spec = importlib.util.spec_from_file_location(
    "pr_body", os.path.join(REPO_ROOT, "tools", "pr-body.py"))
pb = importlib.util.module_from_spec(_pb_spec)
sys.modules[_pb_spec.name] = pb
_pb_spec.loader.exec_module(pb)

check("KEYWORDS matches tools/pr-body.py's", cegi.KEYWORDS == pb.KEYWORDS,
      f"{cegi.KEYWORDS!r} vs {pb.KEYWORDS!r}")
check("REF_HASH matches tools/pr-body.py's", cegi.REF_HASH == pb.REF_HASH,
      f"{cegi.REF_HASH!r} vs {pb.REF_HASH!r}")
check("REF_URL matches tools/pr-body.py's", cegi.REF_URL == pb.REF_URL,
      f"{cegi.REF_URL!r} vs {pb.REF_URL!r}")

# Which workflow runs which half is load-bearing after #3198 split the guards
# that must block (pr-gate.yml) from the advisory ones (pr-check.yml). The
# deterministic half of this guard meets pr-gate.yml's stated rule of thumb --
# a failure is a real defect in the PR, it cannot fail environmentally, it is
# cheap -- and the half that calls api.github.com does not, so they must not be
# in the same file. Asserting the split, not just "some workflow mentions it",
# is what stops the blocking half sliding back into the advisory file, where
# #3116, #3112 and #3095 each merged with a red job.
def _workflow(name):
    return open(os.path.join(REPO_ROOT, ".github", "workflows", name),
                encoding="utf-8").read()

_gate, _adv = _workflow("pr-gate.yml"), _workflow("pr-check.yml")

check("pr-gate.yml runs the guard (or every assertion above is decoration)",
      "check_expectation_gap_issues.py" in _gate, "")
check("the blocking half is NOT the sweep (pr-gate.yml must not go online)",
      "--report-closed-issues" not in _gate, "")
check("pr-check.yml runs the non-blocking sweep",
      "--report-closed-issues" in _adv, "")
check("the advisory file does not also run the blocking half",
      "check_expectation_gap_issues.py --report-closed-issues" in _adv
      and "check_expectation_gap_issues.py\n" not in _adv, "")
check("this very test suite is run by github-scripts-tests' glob",
      ".github/scripts/test_*.py" in _gate, "")

# The shipped manifest: not a substitute for the synthetic cases above (it would
# go green on an empty directory), but it does prove the extraction still works
# against the real schema, and that the gate FIRES on real entries.
shipped = os.path.join(REPO_ROOT, "tests", "expectations")
rc, out = run(None, body="No linked issue: reading the shipped manifest\n",
              manifest_dir=shipped)
check("the shipped tests/expectations/ manifest parses cleanly", rc == 0, f"rc={rc}: {out}")

entries = cegi.load_known_gap_entries(shipped)
print(f"  note shipped manifest carries {len(entries)} expect-fail-known-gap entr"
      f"{'y' if len(entries) == 1 else 'ies'}")
fired = []
for e in entries:
    rc, out = run(None, body=f"Closes {e.owner}/{e.repo}#{e.number}\n", manifest_dir=shipped)
    fired.append((e.number, rc == 1))
check("the gate fires for every issue the shipped manifest actually links",
      all(ok for _, ok in fired), str(fired))

# ...and that assertion must not be able to pass by finding nothing: if the
# directory ships a known-gaps-*.json at all, it has to have yielded entries.
_has_gap_file = any(f.startswith("known-gaps-") and f.endswith(".json")
                    for f in os.listdir(shipped))
check("shipped known-gaps-*.json files yield entries (else the check above is vacuous)",
      (not _has_gap_file) or len(entries) > 0, f"gap files={_has_gap_file}, entries={len(entries)}")


print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
