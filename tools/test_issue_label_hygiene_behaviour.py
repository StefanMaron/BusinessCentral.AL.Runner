#!/usr/bin/env python3
"""Runs the label-hygiene workflow's own shell against a stubbed `gh` (#3678).

The shape test next door (test_issue_label_hygiene_workflow.py) pins the
trigger, the permissions and the guards. This one pins what the steps DO: it
lifts each step's `run:` block out of the workflow, executes it with the event
payload the workflow would have received, and asserts on the `gh` command it
would have issued -- against a stub, never against a live issue, because a run
against live issues is exactly the thing that cannot be undone.

That makes the two halves of the same claim testable:

  * a closed issue loses every `status:` and `agent:` label it carries, and
    nothing else -- and a second run edits nothing, which is idempotency
    demonstrated rather than asserted in a comment;
  * a merged PR releases the issue its BRANCH names, only when that issue is
    open, only when the PR declared "Part of #N" for it, and only when no
    other loop's `agent:` label is on it.

Run directly: python3 tools/test_issue_label_hygiene_behaviour.py
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile

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


def run_block(job_id: str) -> str:
    doc = yaml.safe_load(open(WF, encoding="utf-8"))
    for step in doc["jobs"][job_id]["steps"]:
        if step.get("run"):
            return str(step["run"])
    raise AssertionError(f"no run: step in job {job_id}")


GH_STUB = r"""#!/usr/bin/env bash
# Records what the workflow asked of `gh`, and answers `issue view` from a
# fixture. Never talks to GitHub.
printf '%s\n' "$*" >> "$GH_LOG"
if [ "${1:-}" = "issue" ] && [ "${2:-}" = "view" ]; then
  if [ -n "${ISSUE_JSON:-}" ]; then
    printf '%s' "$ISSUE_JSON"
    exit 0
  fi
  exit 1
fi
# EDIT_RC lets a test drive the API-failure branch: #3930's release job issued
# an edit that produced no label and still reported success, and a step that
# cannot fail on its own edit cannot be told from one that had nothing to do.
if [ "${1:-}" = "issue" ] && [ "${2:-}" = "edit" ]; then
  exit "${EDIT_RC:-0}"
fi
exit 0
"""


def invoke(block: str, env: dict[str, str], issue_json: dict | None = None
           ) -> tuple[int, str, list[str]]:
    """Run one step's shell with a stubbed gh; return rc, stdout, gh calls."""
    tmp = tempfile.mkdtemp(prefix="label-hygiene-")
    try:
        bindir = os.path.join(tmp, "bin")
        os.makedirs(bindir)
        stub = os.path.join(bindir, "gh")
        with open(stub, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(GH_STUB)
        os.chmod(stub, 0o755)
        log = os.path.join(tmp, "gh.log")

        full = dict(os.environ)
        full.update(env)
        # Forward slashes: on Windows this suite runs under Git Bash, which
        # reads a backslash in an argument as an escape and silently turns the
        # path into a filename that does not exist.
        full["GH_LOG"] = log.replace(os.sep, "/")
        full["PATH"] = bindir + os.pathsep + full["PATH"]
        if issue_json is not None:
            full["ISSUE_JSON"] = json.dumps(issue_json)

        script = os.path.join(tmp, "step.sh")
        with open(script, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(block)

        # The resolved path, not the bare name: on Windows, letting the
        # exec resolve "bash" through PATH lands on a different bash from the
        # one shutil.which reports, and that one answers "No such file or
        # directory" for a script it cannot read a drive-letter path for.
        proc = subprocess.run([BASH, script.replace(os.sep, "/")], cwd=ROOT, env=full,
                              capture_output=True, text=True)
        calls = []
        if os.path.exists(log):
            # jq emits CRLF on Windows, so a label read out of it carries a
            # trailing CR into the argument the stub records. The workflow only
            # ever runs on ubuntu; dropping the CR here keeps this suite honest
            # on a Windows box without pretending the runner behaves that way.
            raw = open(log, encoding="utf-8", newline="").read().replace(chr(13), "")
            calls = [ln for ln in raw.splitlines() if ln]
        return proc.returncode, proc.stdout + proc.stderr, calls
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


BASH = shutil.which("bash")
missing = [t for t in ("bash", "jq") if not shutil.which(t)]
if missing:
    # Loud, not skipped. This suite runs under the required "tools/ unit tests"
    # context, where the only codes available are 0 and non-zero, so a skip
    # would report "measured, and fine" for a run that measured nothing --
    # the failure guards-need-a-third-state.md exists to prevent. ubuntu-latest
    # ships both, so this can only fire on a box that cannot answer.
    print(f"FAIL - cannot run the workflow's shell: {', '.join(missing)} not found")
    print("")
    print("0 passed, 1 failed")
    sys.exit(1)

STRIP = run_block("strip-labels-on-close")
RELEASE = run_block("release-part-of-issues")

# --- the close path ----------------------------------------------------------

rc, out, calls = invoke(STRIP, {
    "ISSUE": "4242",
    "LABELS": json.dumps(["status: in-progress", "agent: fbk-2",
                          "area: project-process", "bug"]),
})
check("closing an issue removes its status: and agent: labels", rc == 0 and len(calls) == 1, out)
call = calls[0] if calls else ""
check("...naming the issue", "issue edit 4242" in call, call)
check("...removing the status label", '--remove-label status: in-progress' in call, call)
check("...removing the agent label", '--remove-label agent: fbk-2' in call, call)
check("...and leaving every other label alone",
      "area: project-process" not in call and "bug" not in call, call)
check("...and never adding one", "--add-label" not in call, call)

rc, out, calls = invoke(STRIP, {
    "ISSUE": "4242",
    "LABELS": json.dumps(["area: project-process"]),
})
check("a second run on the same issue calls gh at all -- idempotent, not merely "
      "harmless", rc == 0 and calls == [], f"rc={rc} calls={calls} {out}")

rc, out, calls = invoke(STRIP, {"ISSUE": "4242", "LABELS": "[]"})
check("an issue with no labels at all is a clean no-op", rc == 0 and calls == [], out)

# --- the merge path ----------------------------------------------------------

BRANCH = "agent/fbk-2/issue-42"


def release(body: str, issue: dict | None, head: str = BRANCH,
            pr_labels: list[str] | None = None, edit_rc: int = 0):
    return invoke(RELEASE, {
        "PR_NUMBER": "999",
        "PR_BODY": body,
        "PR_HEAD_REF": head,
        "PR_LABELS": json.dumps(pr_labels if pr_labels is not None else ["agent: fbk-2"]),
        "EDIT_RC": str(edit_rc),
    }, issue_json=issue)


open_issue = {"state": "OPEN",
              "labels": [{"name": "status: in-progress"}, {"name": "agent: fbk-2"},
                         {"name": "area: project-process"}]}

rc, out, calls = release("Closes #123\n\nPart of #42", open_issue)
edits = [c for c in calls if c.startswith("issue edit")]
check("a merged PR declaring Part of its branch issue releases that issue",
      rc == 0 and len(edits) == 1, f"rc={rc} {calls} {out}")
edit = edits[0] if edits else ""
check("...back onto the ready queue", '--add-label status: ready' in edit, edit)
check("...dropping the in-progress status", '--remove-label status: in-progress' in edit, edit)
check("...dropping its own agent label", '--remove-label agent: fbk-2' in edit, edit)
check("...and keeping the area label", "area:" not in edit, edit)

rc, out, calls = release("Closes #42", open_issue)
check("a PR that CLOSES its branch issue relabels nothing -- the close path owns it",
      rc == 0 and not [c for c in calls if c.startswith("issue edit")], f"{calls} {out}")

rc, out, calls = release("Closes #123\n\nPart of #3673", open_issue)
check("a Part of naming a tracking issue rather than the branch's own relabels "
      "nothing -- an epic on the ready queue invites an agent to claim the epic",
      rc == 0 and not [c for c in calls if c.startswith("issue edit")], f"{calls} {out}")
check("...and says so in the log", "left to the merge pass" in out, out)

closed_issue = {"state": "CLOSED", "labels": [{"name": "status: in-progress"}]}
rc, out, calls = release("Part of #42", closed_issue)
check("an issue that is already closed is left to the close path",
      rc == 0 and not [c for c in calls if c.startswith("issue edit")], f"{calls} {out}")

foreign = {"state": "OPEN",
           "labels": [{"name": "status: in-progress"}, {"name": "agent: fbk-1"}]}
rc, out, calls = release("Part of #42", foreign)
check("an issue carrying ANOTHER loop's agent label is left untouched -- "
      "preservation comes first",
      rc == 0 and not [c for c in calls if c.startswith("issue edit")], f"{calls} {out}")

rc, out, calls = release("Part of #42", {"state": "OPEN", "labels": []})
edits = [c for c in calls if c.startswith("issue edit")]
check("an open issue with no labels still gets status: ready",
      rc == 0 and len(edits) == 1 and "--add-label status: ready" in edits[0],
      f"{calls} {out}")

rc, out, calls = release("Part of #42", open_issue, head="feature/some-work")
check("a branch outside the agent/<id>/issue-N shape relabels nothing",
      rc == 0 and calls == [], f"{calls} {out}")

# #3792 / #3934: the gate accepts both of these, so the merge-time reader must too.
rc, out, calls = release("Part of #42", open_issue, head="agent/fbk-2/issue-42-codeunit")
check("a suffixed agent/<id>/issue-N-<step> branch still releases issue N",
      rc == 0 and len([c for c in calls if c.startswith("issue edit 42 ")]) == 1,
      f"{calls} {out}")

rc, out, calls = release("Part of #42 — the first half; the rest stays open.", open_issue)
check("a Part of line with prose after the number releases the issue",
      rc == 0 and len([c for c in calls if c.startswith("issue edit 42 ")]) == 1,
      f"{calls} {out}")

rc, out, calls = release("Closes #123\n\nThis is part of #42, landing half.", open_issue)
check("an INLINE part-of mention is not a declaration and relabels nothing",
      rc == 0 and not [c for c in calls if c.startswith("issue edit")], f"{calls} {out}")

rc, out, calls = release("Part of #42", None)
check("an unreadable issue number is reported, not retried into a failure",
      rc == 0 and not [c for c in calls if c.startswith("issue edit")], f"{calls} {out}")

# --- #3930: the release job must not race an add against a remove -------------
#
# #1883 arrived at its "Part of" merge carrying `status: ready` AND
# `status: in-progress` at once -- a claim had added in-progress without
# removing ready. The `jq` selects every `status:`/`agent:` label, so
# `status: ready` landed in the --remove-label list, and the trailing
# --add-label named it too. `gh issue edit` dispatches addLabels and
# removeLabels as two CONCURRENT GraphQL mutations (cli/cli v2.98.0,
# pkg/cmd/pr/shared/editable_http.go: two wg.Go calls, no ordering), so the
# outcome of naming one label in both is a race. On #1883 remove won: GitHub's
# timeline for 2026-09-12T07:33:53Z records three `unlabeled` events and zero
# `labeled` ones, and the issue came out with no status label while the job
# reported success.

conflicted = {"state": "OPEN",
              "labels": [{"name": "status: ready"},
                         {"name": "status: in-progress"},
                         {"name": "agent: fbk-2"},
                         {"name": "bug"}]}
rc, out, calls = release("Part of #42", conflicted)
edits = [c for c in calls if c.startswith("issue edit")]
edit = edits[0] if edits else ""
check("an issue already carrying status: ready is not asked to remove and add it "
      "in one edit -- gh races the two mutations",
      rc == 0 and len(edits) == 1 and "--remove-label status: ready" not in edit,
      f"rc={rc} {calls} {out}")
check("...and it still ends on the ready queue",
      "--add-label status: ready" in edit or "status: ready" in str(conflicted), edit)
check("...while the labels that really must go still go",
      "--remove-label status: in-progress" in edit
      and "--remove-label agent: fbk-2" in edit, edit)
check("...and the area/bug labels are still untouched", "bug" not in edit, edit)

# --- #3930: every path must say what it decided -------------------------------
#
# Two runs 36 minutes apart, one correct and one not, produced logs that could
# not be told apart: the only evaluated line either printed was "Releasing #N",
# which both printed. Nothing said what the inputs were, and nothing said
# whether the edit landed. Each check below is one question the log could not
# answer at the time #3930 was filed.

DECISION = "label-hygiene decision:"

rc, out, calls = release("Part of #42", open_issue)
check("the release path prints a machine-greppable decision line", DECISION in out, out)
check("...naming the branch issue it resolved", "branch_issue=42" in out, out)
check("...saying the Part of declaration was found", "declared=1" in out, out)
check("...naming the issue state it read", "state=OPEN" in out, out)
check("...saying no foreign agent label blocked it", "foreign=none" in out, out)
check("...and reporting the edit's exit status", "edit_rc=0" in out, out)

rc, out, calls = release("Part of #42", open_issue, head="feature/some-work")
check("a branch that names no issue still prints a decision line", DECISION in out, out)
check("...recording that no issue was resolved", "branch_issue=none" in out, out)

rc, out, calls = release("Closes #42", open_issue)
check("a PR declaring no Part of still prints a decision line", DECISION in out, out)
check("...recording that nothing was declared", "declared=0" in out, out)

rc, out, calls = release("Part of #42", closed_issue)
check("a closed issue still prints a decision line", DECISION in out, out)
check("...recording the state that stopped it", "state=CLOSED" in out, out)

rc, out, calls = release("Part of #42", foreign)
check("a foreign agent label still prints a decision line", DECISION in out, out)
check("...naming the label that blocked it", "foreign=agent: fbk-1" in out, out)

rc, out, calls = release("Part of #42", None)
check("an unreadable issue still prints a decision line", DECISION in out, out)
check("...recording that the state could not be read", "state=unreadable" in out, out)

# --- #3930: a failed edit must fail the step ----------------------------------
#
# The step runs under `set -uo pipefail` with no -e, so a non-zero `gh issue
# edit` on the last line was the step's own exit status by accident. That held
# only while the edit WAS the last line; anything appended after it -- the
# verification below, for one -- would have silently swallowed the failure.

rc, out, calls = release("Part of #42", open_issue, edit_rc=1)
check("a failed gh issue edit fails the step rather than reporting success",
      rc != 0, f"rc={rc} {out}")
check("...and says the edit failed, with its status", "edit_rc=1" in out, out)

print("")
print(f"{passes} passed, {len(failures)} failed")
sys.exit(1 if failures else 0)
