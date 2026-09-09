#!/usr/bin/env python3
"""Shape checks for .github/workflows/issue-label-hygiene.yml (#3678).

The workflow edits labels on real issues, so the properties that keep it safe
cannot be verified by running it -- a run against live issues IS the thing
being guarded. What can be verified is the shape it must keep, and every check
below is one sentence of the design that a later edit could silently drop:

  * it fires on `closed` only, so a reopened issue never loses the labels its
    next claimant just set;
  * it never touches an open issue, guarded twice -- by the trigger type and
    by the payload's own state;
  * it removes exactly the labels the payload says are there, which is what
    makes a second run a no-op rather than an error;
  * the pull-request half runs only for a MERGED, same-repository pull request
    and checks out the DEFAULT BRANCH, never the merged branch's own code,
    because it holds a write token;
  * its job names produce no status-check context the branch ruleset requires.

The workflow is parsed, not grepped: a `run:` block that breaks the file's YAML
would leave every string check below passing against a workflow that can never
run (the trap tools/test_corpus_pin_advance.py records).

Run directly: python3 tools/test_issue_label_hygiene_workflow.py
"""
from __future__ import annotations

import importlib.util
import os
import sys

import yaml

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WF = os.path.join(ROOT, ".github", "workflows", "issue-label-hygiene.yml")

failures: list[str] = []
passes = 0


def check(desc: str, ok: bool, detail: str = "") -> None:
    global passes
    if ok:
        print(f"ok   - {desc}")
        passes += 1
    else:
        print(f"FAIL - {desc}" + (f": {detail}" if detail else ""))
        failures.append(desc)


def required_context_names() -> list[str]:
    """The contexts the ruleset requires, read from the guard that owns them."""
    path = os.path.join(ROOT, ".github", "scripts", "check_required_contexts.py")
    spec = importlib.util.spec_from_file_location("check_required_contexts", path)
    if spec is None or spec.loader is None:
        return []
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    names = list(getattr(mod, "DEFAULT_REQUIRED_CONTEXTS", []))
    names += list(getattr(mod, "PENDING_REQUIRED_CONTEXTS", []))
    return names


check("the workflow file exists", os.path.isfile(WF), WF)

doc = None
if os.path.isfile(WF):
    try:
        doc = yaml.safe_load(open(WF, encoding="utf-8"))
    except yaml.YAMLError as exc:
        # Reported as a failing check rather than a traceback: a traceback stops
        # every later check, and a suite that stops early looks like one with
        # less to say.
        check("the workflow is valid YAML (not merely grep-able)", False,
              str(exc).replace("\n", " ")[:200])
    else:
        check("the workflow is valid YAML (not merely grep-able)", isinstance(doc, dict))

if isinstance(doc, dict):
    # `on:` parses as the boolean True under YAML 1.1, hence the two lookups.
    triggers = doc.get("on", doc.get(True)) or {}

    check("it triggers on issues", isinstance(triggers.get("issues"), dict), repr(triggers))
    issue_types = (triggers.get("issues") or {}).get("types")
    check("...on the closed type and nothing else -- reopened is a separate "
          "type, so a reopened issue cannot lose its labels here",
          issue_types == ["closed"], repr(issue_types))

    pr_types = (triggers.get("pull_request") or {}).get("types")
    check("it triggers on pull_request closed and nothing else",
          pr_types == ["closed"], repr(pr_types))

    perms = doc.get("permissions") or {}
    check("it asks for issues: write", perms.get("issues") == "write", repr(perms))
    check("...and contents: read, not write", perms.get("contents") == "read", repr(perms))
    check("...and nothing else -- no pull-requests or packages scope",
          set(perms) <= {"issues", "contents"}, repr(perms))

    jobs = doc.get("jobs") or {}
    strip = jobs.get("strip-labels-on-close") or {}
    release = jobs.get("release-part-of-issues") or {}

    check("the label-strip job exists", bool(strip))
    check("the Part-of release job exists", bool(release))

    strip_if = str(strip.get("if", ""))
    check("the strip job runs only for an issues event",
          "github.event_name == 'issues'" in strip_if, strip_if)
    check("...and only when the issue is CLOSED -- the second, payload-side "
          "guard that it never touches an open issue",
          "github.event.issue.state == 'closed'" in strip_if, strip_if)

    strip_run = "\n".join(
        str(s.get("run", "")) + "\n" + yaml.safe_dump(s.get("env", {}) or {})
        for s in (strip.get("steps") or [])
    )
    check("the strip job reads the labels off the event payload, so it removes "
          "exactly what is there and a second run is a no-op",
          "github.event.issue.labels" in strip_run, strip_run[:200])
    check("...selecting the status prefix", 'startswith("status:")' in strip_run)
    check("...and the agent prefix", 'startswith("agent:")' in strip_run)
    check("...and removing them, never adding one",
          "--remove-label" in strip_run and "--add-label" not in strip_run)
    check("the strip job neither closes nor comments -- label hygiene only",
          "gh issue close" not in strip_run and "gh issue comment" not in strip_run)

    release_if = str(release.get("if", ""))
    check("the release job runs only for a pull_request event",
          "github.event_name == 'pull_request'" in release_if, release_if)
    check("...only when the pull request actually MERGED",
          "github.event.pull_request.merged == true" in release_if, release_if)
    check("...and only from a branch in this repository, because a fork token "
          "is read-only and would fail on the first label edit",
          "github.event.pull_request.head.repo.full_name == github.repository"
          in release_if, release_if)

    steps = release.get("steps") or []
    checkouts = [s for s in steps if str(s.get("uses", "")).startswith("actions/checkout")]
    check("the release job checks the repository out (it runs a script from it)",
          len(checkouts) == 1, repr([s.get("uses") for s in steps]))
    if checkouts:
        with_ = checkouts[0].get("with") or {}
        ref = str(with_.get("ref", ""))
        check("...pinned to the DEFAULT BRANCH, never the merged pull request's "
              "own code, because this job holds a write token",
              "default_branch" in ref, ref)
        check("...and with no head or merge ref anywhere in that pin",
              "pull_request.head" not in ref and "merge" not in ref, ref)
        check("...with persist-credentials off",
              with_.get("persist-credentials") is False, repr(with_))

    release_run = "\n".join(str(s.get("run", "")) for s in steps)
    check("the release job reads the Part of lines through the shared extractor, "
          "so the gate and the merge action cannot disagree about a body",
          "part_of_references.sh" in release_run)
    check("...and puts the issue back on the ready queue",
          '--add-label "status: ready"' in release_run, release_run[-400:])
    check("...leaving an issue claimed by another agent alone",
          "foreign" in release_run)
    check("the release job neither closes nor comments -- the merge pass owns "
          "saying what landed",
          "gh issue close" not in release_run and "gh issue comment" not in release_run)

    check("nothing in this workflow uses pull_request_target",
          "pull_request_target" not in open(WF, encoding="utf-8").read())

    required = required_context_names()
    names = [str(j.get("name") or jid) for jid, j in jobs.items()]
    clashes = [n for n in names if n in required]
    check("no job here produces a context the branch ruleset requires -- a new "
          "required context nobody added to the ruleset blocks every merge",
          not clashes, repr(clashes))

print("")
print(f"{passes} passed, {len(failures)} failed")
sys.exit(1 if failures else 0)
