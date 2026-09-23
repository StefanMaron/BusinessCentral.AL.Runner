#!/usr/bin/env python3
"""Runs one step of `.github/workflows/issue-label-hygiene.yml` against a stubbed `gh`.

Lifted out of test_issue_label_hygiene_behaviour.py at #4301, when a second
suite needed the same thing. Two callers, two different questions:

  * test_issue_label_hygiene_behaviour.py -- what the steps DO: which labels a
    close removes, which issue a merged "Part of" releases, what each decision
    line says.
  * test_no_racing_label_edit.py -- that the ONE single-call `gh issue edit` in
    this repository resolves to a command naming no label in both its add-list
    and its remove-list. That suite exempts this workflow from its shape scan,
    and this is what the exemption rests on.

One copy rather than two, because a second bash-executing stub is the one
nobody re-reads when the first is fixed.

Nothing here talks to GitHub: `gh` is a stub on PATH that records what it was
asked and answers `issue view` from a fixture.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WF = os.path.join(ROOT, ".github", "workflows", "issue-label-hygiene.yml")

BASH = shutil.which("bash")

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
  # UNASSIGN_RC is separate from EDIT_RC because the release step issues TWO
  # edits and the label one runs first: with a single variable an EDIT_RC=1
  # exits the step before the assignee edit is ever reached, so its own failure
  # branch is unreachable and a silent swallow there reads as green (#4497).
  case "$*" in
    *--remove-assignee*) exit "${UNASSIGN_RC:-0}" ;;
  esac
  exit "${EDIT_RC:-0}"
fi
exit 0
"""


def missing_tools() -> list[str]:
    """What is absent on this box that makes the workflow's shell unrunnable.

    A caller that gets a non-empty list has measured NOTHING and must say so --
    never report its success state (`guards-need-a-third-state.md`).
    """
    absent = [t for t in ("bash", "jq") if not shutil.which(t)]
    try:
        import yaml  # noqa: F401
    except ImportError:
        absent.append("python3 yaml")
    return absent


def run_block(job_id: str) -> str:
    """The first `run:` block of the named job, out of the parsed YAML.

    Parsed, not grepped: a `run:` body read off the raw source cannot be told
    apart from a YAML comment quoting it, which is the defect #4301 was filed
    for.
    """
    import yaml

    doc = yaml.safe_load(open(WF, encoding="utf-8"))
    for step in doc["jobs"][job_id]["steps"]:
        if step.get("run"):
            return str(step["run"])
    raise AssertionError(f"no run: step in job {job_id}")


def invoke(block: str, env: dict[str, str], issue_json: dict | None = None,
           read_labels=None) -> tuple[int, str, list[str]]:
    """Run one step's shell with a stubbed gh; return rc, stdout, gh calls.

    `read_labels` makes this the ONLY channel the fixture travels down. It is
    called with the environment block the step was actually executed with, and
    what it returns is appended to the result as a fourth element -- see
    `drive_step`, which is what callers should use.
    """
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
        # encoding= explicitly, not bare text=True: the decode would otherwise
        # use the locale codec, and a PR body carrying an em dash (one of the
        # behaviour suite's own Part-of fixtures does) is undecodable under
        # cp1252. tools/test_text_encoding.py holds this for every non-test
        # module under tools/ -- which this became the moment it stopped living
        # inside a test_*.py file.
        proc = subprocess.run([BASH, script.replace(os.sep, "/")], cwd=ROOT, env=full,
                              capture_output=True, text=True,
                              encoding="utf-8", errors="replace")
        calls = []
        if os.path.exists(log):
            # jq emits CRLF on Windows, so a label read out of it carries a
            # trailing CR into the argument the stub records. The workflow only
            # ever runs on ubuntu; dropping the CR here keeps this suite honest
            # on a Windows box without pretending the runner behaves that way.
            raw = open(log, encoding="utf-8", newline="").read().replace(chr(13), "")
            calls = [ln for ln in raw.splitlines() if ln]
        if read_labels is None:
            return proc.returncode, proc.stdout + proc.stderr, calls
        # Read back from `full`, the environment the subprocess was handed --
        # not from `env` or `issue_json`, which are the caller's copies. A
        # reader given the executed environment cannot be shown a fixture the
        # step did not run with.
        return (proc.returncode, proc.stdout + proc.stderr, calls,
                _read_back(read_labels, full))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def _read_back(read_labels, executed_env: dict[str, str]
               ) -> tuple[list[str] | None, str]:
    """(label names, "") -- or (None, why) when the channel could not be read.

    Returns rather than raises: a fixture the caller cannot read back is a
    measurement that did not happen, and a caller must be able to spell that as
    its third state rather than as a failure of the thing under test
    (`guards-need-a-third-state.md`).
    """
    try:
        names = read_labels(executed_env)
    except Exception as exc:  # any malformed channel, not one chosen shape
        return None, f"{type(exc).__name__}: {exc}"
    if not isinstance(names, list) or not all(isinstance(n, str) for n in names):
        return None, f"channel did not yield a list of label names: {names!r}"
    return names, ""


def drive_step(block: str, env: dict[str, str], issue_json: dict | None,
               read_labels) -> tuple[int, str, list[str], list[str] | None, str]:
    """Run a step and read its label fixture back out of the same execution.

    The two used to be separate calls, so a caller could drive the step with one
    fixture and read the non-vacuity fixture out of another -- a false green over
    a genuinely racing workflow, because the race check saw labels not including
    the one under test while the non-vacuity check was satisfied by the other
    fixture (#4315). Here there is one fixture and one call, so the mismatch has
    no expression: `read_labels` is handed the environment block the step was
    executed with, which the caller never holds.

    `read_labels(executed_env)` takes ONE argument for that reason. A two-argument
    form would have to be passed the caller's `issue_json` alongside, which is the
    second channel this exists to remove.
    """
    rc, out, calls, (fixture, why) = invoke(block, env, issue_json=issue_json,
                                            read_labels=read_labels)
    return rc, out, calls, fixture, why


def flag_values(call: str, flag: str) -> list[str]:
    """Every value of `--<flag> V` in a space-joined command line.

    A label contains spaces (`status: in-progress`), so splitting the line on
    whitespace cannot recover one. A value therefore runs to the next ` --` or
    to the end of the line -- which is also why the match is not a plain
    substring test: `--remove-label status: ready` is a prefix of
    `--remove-label status: readiness`, and only one of those is the label.
    """
    return [m.group(1) for m in
            re.finditer(rf"--{re.escape(flag)}\s+(.*?)(?=\s+--|\s*$)", call)]
