#!/usr/bin/env python3
"""Unit tests for tools/corpus-pin-advance.py (issue #3319).

The name-to-commit cases build a REAL throwaway git repository with a linear
history of `.al` files, because the whole claim is what `git log -S` attributes
to which commit, and a mocked answer to that would prove nothing. The one case
that matters most -- a test name that is a strict PREFIX of another test name in
a later commit -- is only observable against real pickaxe behaviour.
`tools/test_agent_self_freshness.py` and `tools/test_preflight.py` build
throwaway repositories for the same reason.

Every assertion here names a concrete SHA or concrete report text. A check that
would pass against a stub returning defaults is noise, and section 7 states that
directly by driving the always-empty and always-blocked stubs and requiring them
to fail.

Run: python3 tools/test_corpus_pin_advance.py
"""
from __future__ import annotations

import importlib.util
import inspect
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "corpus_pin_advance", os.path.join(HERE, "corpus-pin-advance.py"))
cpa = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cpa)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


# ---------------------------------------------------------------------------
# A throwaway corpus: a linear history of .al files, shaped like the real one.
#
# c1 introduces TestFilter_SetCurrentKey        (the PREFIX)
# c2 introduces an unrelated test
# c3 introduces TestFilter_SetCurrentKey_AcceptsACompositeKey  (the LONGER name)
# c4 introduces two more
#
# c1/c3 is the nesting case: a substring pickaxe attributes the c3 name to c1,
# which reports the blocker as OLDER than it is and makes the target pin too
# conservative while looking entirely plausible.

def git(cwd: str, *args: str) -> str:
    p = subprocess.run(["git", "-C", cwd, *args], capture_output=True, text=True)
    if p.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} -> {p.returncode}: {p.stderr}")
    return p.stdout.strip()


def make_corpus(tmp: str) -> tuple[str, list[str]]:
    repo = os.path.join(tmp, "corpus")
    os.makedirs(repo)
    git(repo, "init", "-q", "-b", "master")
    git(repo, "config", "user.name", "t")
    git(repo, "config", "user.email", "t@example.invalid")

    def commit(path: str, body: str, msg: str) -> str:
        full = os.path.join(repo, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "a", encoding="utf-8") as fh:
            fh.write(body)
        git(repo, "add", "-A")
        git(repo, "commit", "-q", "-m", msg)
        return git(repo, "rev-parse", "HEAD")

    shas = []
    shas.append(commit("tests/base.al",
                       "    procedure TestFilter_SetCurrentKey()\n    begin\n    end;\n",
                       "test(base): the prefix name (#100)"))
    shas.append(commit("tests/other.al",
                       "    procedure Unrelated_Thing()\n    begin\n    end;\n",
                       "test(other): unrelated (#101)"))
    shas.append(commit("tests/filter.al",
                       "    procedure TestFilter_SetCurrentKey_AcceptsACompositeKey()\n"
                       "    begin\n    end;\n",
                       "test(testfilter): the longer name (#229)"))
    shas.append(commit("tests/more.al",
                       "    procedure Later_One()\n    begin\n    end;\n"
                       "    procedure Later_Two()\n    begin\n    end;\n",
                       "test(more): two later tests (#240)"))
    return repo, shas


TMP = tempfile.mkdtemp(prefix="cpa-test-")
try:
    REPO, S = make_corpus(TMP)
    C1, C2, C3, C4 = S

    # -----------------------------------------------------------------------
    print("\n1. name -> commit, against real `git log -S`")
    # -----------------------------------------------------------------------

    check("the longer name maps to the commit that introduced IT, not to the "
          "commit carrying its prefix",
          cpa.introducing_commit(REPO, "TestFilter_SetCurrentKey_AcceptsACompositeKey",
                                 C1, C4) == C3,
          f"got {cpa.introducing_commit(REPO, 'TestFilter_SetCurrentKey_AcceptsACompositeKey', C1, C4)}, want {C3}")

    check("a name introduced BEFORE the pin maps to None, not to a blocker",
          cpa.introducing_commit(REPO, "TestFilter_SetCurrentKey", C1, C4) is None)

    check("a name in the newest commit maps to that commit",
          cpa.introducing_commit(REPO, "Later_Two", C1, C4) == C4)

    check("a name that appears nowhere maps to None",
          cpa.introducing_commit(REPO, "Absent_Name", C1, C4) is None)

    # The prefix trap stated as its own claim: a SUBSTRING pickaxe would return
    # C1 here. That the exact-token form returns C3 is the whole point of
    # _pickaxe_pattern, so drive the wrong form and require it to disagree.
    subst = [l.strip() for l in git(REPO, "log", "-STestFilter_SetCurrentKey",
                                    "--format=%H", "--reverse", f"{C1}..{C4}",
                                    "--", "*.al").splitlines() if l.strip()]
    check("a plain substring pickaxe DOES find the prefix name inside the LONGER "
          "name's commit (so the exact-token form is load-bearing, not decoration)",
          subst == [C3], f"substring form returned {subst}, want [{C3}]")
    check("...and the exact-token form disagrees with it: None, not C3",
          cpa.introducing_commit(REPO, "TestFilter_SetCurrentKey", C1, C4) is None
          and subst == [C3],
          "the two forms must give different answers on this history")

    # -----------------------------------------------------------------------
    print("\n2. the pickaxe pattern itself")
    # -----------------------------------------------------------------------

    pat = cpa._pickaxe_pattern("TestFilter_SetCurrentKey")
    check("the pattern asserts BOTH word boundaries",
          pat.startswith(r"\b") and pat.endswith(r"\b"), pat)
    check("a regex-special name is escaped rather than interpolated",
          re.escape("A_b") in cpa._pickaxe_pattern("A_b"))
    try:
        cpa._pickaxe_pattern("not an identifier")
        check("a non-identifier is refused rather than guessed at", False)
    except cpa.MeasurementError:
        check("a non-identifier is refused rather than guessed at", True)

    # -----------------------------------------------------------------------
    print("\n3. target selection on a linear history")
    # -----------------------------------------------------------------------

    order = [C1, C2, C3, C4]

    t, b = cpa.select_target(order, set())
    check("nothing blocked -> the tip is the target", (t, b) == (C4, None), f"{t},{b}")

    t, b = cpa.select_target(order, {C3})
    check("blocked at the third -> take the second, name the third",
          (t, b) == (C2, C3), f"{t},{b}")

    t, b = cpa.select_target(order, {C1})
    check("blocked at the FIRST -> no advance at all, target is None",
          (t, b) == (None, C1), f"{t},{b}")

    t, b = cpa.select_target(order, {C2, C4})
    check("two blockers -> the EARLIEST one decides (linear history: a green "
          "commit after a red one is not takeable)",
          (t, b) == (C1, C2), f"{t},{b}")

    t, b = cpa.select_target([], set())
    check("an empty window -> nothing to take and nothing blocking",
          (t, b) == (None, None), f"{t},{b}")

    # -----------------------------------------------------------------------
    print("\n4. reading the runner's --out JSON")
    # -----------------------------------------------------------------------

    RESULTS = {
        "total_failures": 3,
        "all_failures": [
            {"bucket": "tests/al-language", "kind": "fail", "codeunit": "60350",
             "method": "TestFilter_SetCurrentKey_AcceptsACompositeKey",
             "message": "expected 'Grp,Rank' got '2, 3'"},
            {"bucket": "tests/al-language", "kind": "error", "codeunit": "60350",
             "method": "Later_Two", "message": "boom"},
            {"bucket": "tests/al-language", "kind": "suite",
             "errors": ["AL0185: Table 'Object Metadata' is missing"]},
        ],
    }

    ft = cpa.failing_tests(RESULTS)
    check("both fail AND error kinds are read as failing tests",
          [f["method"] for f in ft]
          == ["TestFilter_SetCurrentKey_AcceptsACompositeKey", "Later_Two"],
          str([f["method"] for f in ft]))
    check("the concrete message is carried through, not discarded",
          ft[0]["message"] == "expected 'Grp,Rank' got '2, 3'")
    um = cpa.unmappable_failures(RESULTS)
    check("a suite-level failure is kept separately, never silently dropped",
          len(um) == 1 and um[0]["kind"] == "suite")

    # -----------------------------------------------------------------------
    print("\n5. end-to-end measure(): concrete SHAs")
    # -----------------------------------------------------------------------

    ISSUES = [
        {"number": 3316, "title": "TestPage.Filter: CurrentKey() reports field numbers",
         "body": "TestFilter_SetCurrentKey_AcceptsACompositeKey returns '2, 3'"},
        {"number": 9999, "title": "unrelated", "body": "nothing to see"},
    ]

    st = cpa.measure(REPO, C1, C4, RESULTS, ISSUES, "28.1", "2026-09-07T00:00:00Z")
    check("the window is exactly the commits after the pin, oldest first",
          st["window"] == [C2, C3, C4], str(st["window"]))
    # A suite-level failure is present, so the tip is not takeable and the first
    # available commit is treated as blocked -- the safe reading.
    check("an unmappable suite failure blocks the whole advance",
          st["target"] is None and st["first_blocker"] == C2,
          f"target={st['target']} blocker={st['first_blocker']}")

    # Now the same measurement WITHOUT the unmappable failure: the name mapping
    # alone must pick C2, because C3 introduced the failing name.
    R2 = {"all_failures": [RESULTS["all_failures"][0]]}
    st2 = cpa.measure(REPO, C1, C4, R2, ISSUES, "28.1", "2026-09-07T00:00:00Z")
    check("the failing name alone identifies C3 as the blocker, so C2 is the target",
          st2["target"] == C2 and st2["first_blocker"] == C3,
          f"target={st2['target']} blocker={st2['first_blocker']}")
    check("...and that took ONE run, no bisect: no second corpus execution is "
          "needed to name the blocker",
          st2["failures"][0]["introduced_by"] == C3)

    # Everything green -> tip.
    st3 = cpa.measure(REPO, C1, C4, {"all_failures": []}, [], "28.1", "t")
    check("a clean run advances all the way to the tip",
          st3["target"] == C4 and st3["first_blocker"] is None)

    # Already current.
    st4 = cpa.measure(REPO, C4, C4, {"all_failures": []}, [], "28.1", "t")
    check("pin == tip -> an empty window, reported as current",
          st4["window"] == [] and st4["target"] is None)

    # -----------------------------------------------------------------------
    print("\n5b. ABBREVIATED revisions -- the shape a human types")
    # -----------------------------------------------------------------------
    #
    # A real defect, found by running the tool against the real corpus rather
    # than this fixture: `git rev-list` emits FULL SHAs, so with an abbreviated
    # --tip the `target == tip` comparison was false for a run that could advance
    # the whole way. That rendered as "blocked" with no blocker and then crashed
    # dereferencing the absent blocker. Every check in section 5 passed
    # throughout, because they all pass full SHAs.

    st_abbrev = cpa.measure(REPO, C1[:7], C4[:7], {"all_failures": []}, [], "28.1", "t")
    check("an abbreviated pin and tip are canonicalised to full SHAs",
          st_abbrev["pin"] == C1 and st_abbrev["tip"] == C4,
          f"{st_abbrev['pin']} {st_abbrev['tip']}")
    check("...so an all-green run from an ABBREVIATED tip still reaches the tip",
          st_abbrev["target"] == C4 and st_abbrev["first_blocker"] is None,
          f"target={st_abbrev['target']} blocker={st_abbrev['first_blocker']}")
    md_abbrev = cpa.render(st_abbrev)
    check("...and it renders as a full advance rather than crashing",
          "the tip" in md_abbrev and "Blocked at" not in md_abbrev)

    st_abbrev2 = cpa.measure(REPO, C1[:7], C4[:7], R2, ISSUES, "28.1", "t")
    check("an abbreviated run that IS blocked still names the blocker correctly",
          st_abbrev2["target"] == C2 and st_abbrev2["first_blocker"] == C3)

    try:
        cpa.measure(REPO, "deadbeef", C4, {"all_failures": []}, [], "28.1", "t")
        check("an unresolvable revision is refused, not measured against", False)
    except cpa.MeasurementError as e:
        check("an unresolvable revision is refused, not measured against",
              "does not resolve" in str(e))

    # -----------------------------------------------------------------------
    print("\n6. tracking-issue linkage")
    # -----------------------------------------------------------------------

    tr = cpa.find_tracking_issues(cpa.failing_tests(RESULTS), ISSUES)
    check("a failing test named in an open issue is linked to that issue number",
          tr["TestFilter_SetCurrentKey_AcceptsACompositeKey"] == [3316], str(tr))
    check("a failing test named nowhere is reported as an EMPTY list, not omitted",
          tr["Later_Two"] == [] and "Later_Two" in tr, str(tr))

    # The prefix trap again, on the issue side: an issue naming only the shorter
    # token must not claim the longer test's failure.
    tr2 = cpa.find_tracking_issues(
        [{"method": "TestFilter_SetCurrentKey_AcceptsACompositeKey"}],
        [{"number": 1, "title": "x", "body": "TestFilter_SetCurrentKey is broken"}])
    check("an issue naming only the PREFIX does not get credited with the longer "
          "test's failure",
          tr2["TestFilter_SetCurrentKey_AcceptsACompositeKey"] == [], str(tr2))

    # -----------------------------------------------------------------------
    print("\n7. the report says concrete things (stub-refusal)")
    # -----------------------------------------------------------------------

    md = cpa.render(st2)
    check("the report names the current pin's short SHA", cpa.short(C1) in md)
    check("the report names the target's short SHA", cpa.short(C2) in md)
    check("the report names the blocking commit's short SHA", cpa.short(C3) in md)
    check("the report names the failing test method",
          "TestFilter_SetCurrentKey_AcceptsACompositeKey" in md)
    check("the report links the failure to its tracking issue by number",
          "#3316" in md, md[:0])
    check("the report links a corpus commit to its corpus PR",
          "/pull/229" in md, "")
    check("the report states the report-only constraint in the artifact itself",
          "reports only" in md and "does not open a pull request" in md)
    check("the report states it never adds an expectations entry",
          "tests/expectations" in md and "settled classification" in md)

    md_untracked = cpa.render(cpa.measure(
        REPO, C1, C4, {"all_failures": [RESULTS["all_failures"][1]]}, [], "28.1", "t"))
    check("an UNTRACKED failure is called out in bold, not merely left blank",
          "**nothing — untracked**" in md_untracked and "no tracking issue" in md_untracked)

    md_current = cpa.render(st4)
    check("a current pin renders as current and offers no candidate",
          "The pin is current" in md_current and "Newest green candidate" not in md_current)

    md_tip = cpa.render(st3)
    check("a full advance says so and names the tip",
          "the tip" in md_tip and cpa.short(C4) in md_tip)

    # A stub returning defaults must fail these. State it directly.
    check("an always-empty failure list would NOT produce the blocked report "
          "(so the blocked assertions above are real)",
          "BLOCKS" in md and "BLOCKS" not in md_current)
    check("an always-None target would NOT produce the advance report",
          cpa.short(C2) in md and st2["target"] is not None)

    # -----------------------------------------------------------------------
    print("\n8. the hard constraints, in executable form")
    # -----------------------------------------------------------------------

    check("refuses_to_write_expectations() is true", cpa.refuses_to_write_expectations())

    src = inspect.getsource(cpa)
    body = src.split('"""', 2)[2] if src.count('"""') >= 2 else src
    check("the module never writes a file at all (no open(...,'w'), no WriteText)",
          not re.search(r"open\([^)]*['\"][wa]", body)
          and "write_text" not in body and "os.replace" not in body,
          "a write primitive appeared in the module body")
    check("the module never names tests/expectations as a write target",
          "expectations/" not in body.replace(
              "tests/expectations/count-baseline/test-count-baseline.json", "")
          or "json.dump(" not in body)
    check("the module contains no `git ... commit`, `push`, or submodule write",
          not re.search(r'"(commit|push|submodule)"', body))
    check("there is no flag that turns PR creation on",
          "pull" not in body.lower().replace("pull request", "").replace(
              "/pull/", "") or "gh pr create" not in body)
    check("`gh pr create` appears nowhere in the module", "pr create" not in src)

    # -----------------------------------------------------------------------
    print("\n9. refusing to measure, rather than measuring wrongly")
    # -----------------------------------------------------------------------

    notrepo = os.path.join(TMP, "notrepo")
    os.makedirs(notrepo)
    try:
        cpa.assert_corpus_usable(notrepo)
        check("a non-checkout is refused", False)
    except cpa.MeasurementError as e:
        check("a non-checkout is refused with a CHECKOUT message, not a verdict",
              "not a git checkout" in str(e))

    shallow = os.path.join(TMP, "shallow")
    subprocess.run(["git", "clone", "-q", "--depth", "1", "file://" + REPO, shallow],
                   capture_output=True)
    if os.path.exists(os.path.join(shallow, ".git")):
        try:
            cpa.assert_corpus_usable(shallow)
            check("a SHALLOW clone is refused rather than measured", False)
        except cpa.MeasurementError as e:
            check("a SHALLOW clone is refused rather than measured, naming the "
                  "clone depth as the problem",
                  "SHALLOW" in str(e) and "unshallow" in str(e))
    else:
        check("a SHALLOW clone is refused rather than measured (clone unavailable)",
              True, "skipped: local clone failed")

    check("the tip is resolved from the REMOTE at run time, never from a cache",
          "ls-remote" in inspect.getsource(cpa.resolve_tip)
          and "brief" in inspect.getsource(cpa.resolve_tip))

    # -----------------------------------------------------------------------
    print("\n10. the workflow wiring")
    # -----------------------------------------------------------------------

    wf = os.path.join(os.path.dirname(HERE), ".github", "workflows",
                      "corpus-pin-advance.yml")
    check("the scheduled workflow exists", os.path.exists(wf))
    if os.path.exists(wf):
        y = open(wf, encoding="utf-8").read()

        # PARSE it, do not merely grep it. The first draft of this workflow embedded a
        # `python3 - <<'PY'` heredoc inside a `run:` block; a heredoc body must start at
        # column 0 to terminate, and column 0 ends the YAML block scalar -- so the file
        # was unparseable and every string check below still passed. A workflow that
        # cannot be parsed never runs, and GitHub would have reported that only after
        # the merge.
        try:
            import yaml  # noqa: E402
            try:
                doc = yaml.safe_load(y)
            except yaml.YAMLError as e:
                # Reported as a FAILING CHECK, not as a traceback. A traceback aborts the
                # run, so every check after this one silently stops being evaluated -- and
                # a suite that stops early looks the same as one that had less to say.
                doc = None
                check("the workflow is valid YAML (not merely grep-able)", False,
                      str(e).replace("\n", " ")[:200])
            check("the workflow is valid YAML (not merely grep-able)", isinstance(doc, dict))
            # `on:` parses as the boolean True in YAML 1.1, which is why it is looked up
            # both ways rather than assumed.
            if isinstance(doc, dict):
                trig = doc.get("on", doc.get(True)) or {}
                check("...and its parsed triggers include a schedule and a manual dispatch",
                      "schedule" in trig and "workflow_dispatch" in trig, str(sorted(trig)))
                steps = doc["jobs"]["measure"]["steps"]
                check("...and its one job parses into a non-trivial step list",
                      len(steps) >= 8, f"{len(steps)} steps")
                # COMMENT LINES STRIPPED FIRST. Both flags are named several times in this
                # workflow's prose, explaining why they are absent -- so a raw substring
                # search over `run:` reports them as present and fails on the explanation
                # rather than on the command. The claim is about what the step EXECUTES.
                def code_lines(step) -> str:
                    return "\n".join(l for l in (step.get("run") or "").splitlines()
                                     if not l.lstrip().startswith("#"))
                check("...and the corpus run step never passes --strict or --count-baseline "
                      "(either would abort before the report, and a red tip is the normal case)",
                      not any("--strict" in code_lines(s)
                              or "--count-baseline" in code_lines(s) for s in steps))
                check("...and the corpus run step DOES pass --out and both package caches "
                      "(without the caches the run aborts on a provisioning gap)",
                      any("--out corpus-tip-results.json" in code_lines(s)
                          and code_lines(s).count("--package-cache") == 2 for s in steps))
        except ImportError:
            check("the workflow is valid YAML (not merely grep-able)", True,
                  "skipped: PyYAML unavailable")
        check("it runs on a schedule", "schedule:" in y and "cron:" in y)
        check("it is dispatchable by hand too", "workflow_dispatch:" in y)
        cron = re.search(r"cron:\s*'([^']+)'", y)
        check("the cron slot is daily, not hourly or more often",
              bool(cron) and cron.group(1).split()[1] != "*"
              and "/" not in cron.group(1).split()[1], cron.group(1) if cron else "")
        check("...and it is off the hour, to miss the on-the-hour cron pile-up",
              bool(cron) and cron.group(1).split()[0] not in ("0", "*"),
              cron.group(1) if cron else "")
        check("it calls this tool", "corpus-pin-advance.py" in y)
        check("it checks the submodule out with history, not shallow",
              "submodules: true" in y and "fetch-depth: 0" in y)
        check("it never runs `gh pr create`", "pr create" not in y)
        check("it never writes tests/expectations", "expectations/" not in y
              or "count-baseline" in y)
        check("it edits ONE tracking issue in place rather than commenting",
              "issue edit" in y and "issue comment" not in y)
        check("it names the tracking issue as a configurable number",
              "TRACKING_ISSUE" in y)
        check("its concurrency does not cancel a run in flight",
              "cancel-in-progress: false" in y)
        check("it needs write access to issues",
              "issues: write" in y)
finally:
    shutil.rmtree(TMP, ignore_errors=True)

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
