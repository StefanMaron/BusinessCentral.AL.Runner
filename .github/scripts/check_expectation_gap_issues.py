#!/usr/bin/env python3
"""A PR that closes a gap issue may not leave the known-gap entry declaring it (#3089).

`tests/expectations/known-gaps-*.json` entries are `expect-fail-known-gap`: the
surface is in scope, real BC does it, the runner does not yet, and `Issue` links
the open work. The manifest is deliberately loud in BOTH directions at RUN time
-- a test that passes while an entry declares a known gap fails the run with
"Test passed cleanly but manifest declares expect-fail-known-gap ... Remove the
entry", and a test that fails without an entry fails with "add an entry".

That machinery works. What nothing checked is the moment the two get out of
step: a PR fixes the gap, closes the issue, and forgets to delete the entry.
Nothing in the PR is wrong on its own, so it merges green -- and the NEXT run of
`main` is red, on a commit whose author has moved on. That happened twice in one
hour on 2026-09-05: #2795 (closed by PR #2809) left an entry in
known-gaps-testpage-boolean-spelling.json, and #2805 (closed by PR #2825) left
two in known-gaps-start-session-isolation.json. Reported after the fact as #2844
and #2858 and fixed by deleting the entries (#2845, #2859); #2858 diagnosed the
mechanism and closed without a guard.

Those two are why the shape is known. They are NOT incidents this gate would
have prevented -- see the next section, which says so with the measurements,
because an earlier version of this file claimed otherwise.

THE TWO ORDERINGS, AND WHICH ONE THE GATE COVERS
------------------------------------------------

Manifest/issue drift arrives in one of two orders, and the blocking gate below
covers exactly one of them.

FORWARD -- the entry is already in the checkout when the closing PR is checked.
The PR text and the manifest then contradict each other inside a single diff,
and the gate is red before anyone merges. This is the ordering the gate exists
for, and the ordinary one: an entry normally predates the fix that closes its
issue.

INVERSE -- the entry lands after the closing PR's own check has run. Nothing
evaluated at that PR's check time can see an entry that is not there yet, so the
gate cannot help. Only the non-gating sweep further down covers it, on some
LATER PR, and only once the linked issue has actually closed.

Both 2026-09-05 incidents were the inverse ordering. Measured, not reasoned:

  - Both known-gaps files were created by PR #2808's merge at 16:56:43Z, and
    #2808 closes nothing -- so it declared no closing reference for the gate to
    compare its own new entries against.
  - PR #2825 (closes #2805) merged at 16:48:28Z, eight minutes BEFORE the entry
    existed anywhere.
  - PR #2809 (closes #2795) had exactly one PR Check, created 16:29:20Z, 27
    minutes before #2808 merged. `main`'s ruleset sets
    strict_required_status_checks_policy=false, so the base moving underneath it
    re-triggered nothing.
  - Replaying each PR's real title, body and commit messages against the
    manifest as it stood at its OWN check time (base 43d85ea6, which held 12
    expect-fail-known-gap entries and neither of the two files) exits 0 for
    both.
  - The sweep would not have caught them that hour either: #2808's last PR Check
    ran at 16:39:50Z, when #2795 and #2805 were both still open -- they closed
    at 17:29:30Z and 16:48:29Z.

So this gate is not credited with a prevented incident. It closes the forward
ordering, which nothing checked before, and the sweep reports the inverse one.

WHAT THIS CHECKS, AND WHAT IT DELIBERATELY DOES NOT
---------------------------------------------------

Blocking (this script's default mode, and it needs NO network):

    If this PR closes issue N, and an expect-fail-known-gap entry links issue N,
    fail.

That is not a heuristic. The PR asserts the gap is fixed; the manifest asserts
it is not. Exactly one of them is right, they are in the same diff, and the
author is the person who can settle it -- by deleting the entry, by re-targeting
it at the issue that actually tracks the remaining work, or by dropping the
closing reference.

"Blocking" here means the job goes red, not that the merge is refused. This job
is not a required status check on `main` -- the ruleset requires only "All BC
versions passed" and "Tests updated" -- so tools/ci-wait.py's is_required()
answers False for it, auto-merge ignores it, and it cannot turn a ci-wait
verdict red. It annotates, and a human reads the annotation. That is
pre-existing and shared with the sibling closing-reference jobs in the same
workflow; nothing in this script can change it, and making the job required is a
separate decision for whoever owns the ruleset.

NOT blocking, and on purpose (`--report-closed-issues`):

    An entry linking an issue that is already closed, in a PR that has nothing
    to do with it.

#2858 makes the point that constrains this: a closed issue does not by itself
prove the entry is stale. An issue can be closed as a duplicate, or closed while
the gap remains. Six entries pointed at closed issues at the time and only one
was actually failing. So that direction is a lead worth surfacing as a warning
annotation, never a verdict worth failing a build on -- and it is the only half
that touches the network, which is the other reason it does not gate.

A CHECK THAT CANNOT CHECK MUST NOT REPORT A PASS
------------------------------------------------

Every way this could quietly become decoration exits 2 instead of 0: a manifest
directory that is not there, a file that will not parse, an entry whose `Issue`
cannot be resolved to a real issue, a `known-gaps-*.json` holding entries that
are not known gaps (a prefix/Mode disagreement would silence the whole file),
and a run where the PR title, body and commit messages are all empty (which
means the fetch failed -- every PR has a title and at least one commit). A
passing run prints how many entries it actually scanned, so a green tick is
never mute about its scope.

Inputs (environment variables, all optional individually, but not all-empty):
  PR_TITLE   - the pull request's title
  PR_BODY    - the pull request's body
  PR_COMMITS - every commit message on the branch, concatenated. This repo's
               squash setting is squash_merge_commit_message=COMMIT_MESSAGES, so
               a closing keyword in a commit message closes the issue even
               though it never appears in the PR body (#2491).
  GITHUB_REPOSITORY - owner/repo that a bare "#N" resolves against.

Usage:
  check_expectation_gap_issues.py [MANIFEST_DIR] [--report-closed-issues]

Exit codes:
  0  no entry links an issue this PR closes (or, in report mode, always)
  1  an entry links an issue this PR closes -- delete or re-target it
  2  the check could not be performed; see the ::error:: line
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
DEFAULT_MANIFEST_DIR = os.path.join(REPO_ROOT, "tests", "expectations")
DEFAULT_REPO = "StefanMaron/BusinessCentral.AL.Runner"

# --------------------------------------------------------------------------
# Closing references. These three constants are kept byte-identical to
# tools/pr-body.py's (themselves a port of check_closing_reference.sh), and
# test_check_expectation_gap_issues.py asserts that parity -- three copies of a
# parse that silently disagree is how a guard ends up checking a different set
# of issues from the one GitHub will actually close.
# --------------------------------------------------------------------------

KEYWORDS = "close|closes|closed|fix|fixes|fixed|resolve|resolves|resolved"
# "#N" (the "#" is REQUIRED -- "fixes 3 bugs" is prose, and treating a bare number
# as a reference is a false positive that shipped once already), "owner/repo#N",
# or a full issue/PR URL. Deliberately NOT "GH-N": that only becomes live with a
# configured autolink, and this repo has none.
REF_HASH = r"(?:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)?#[0-9]+"
REF_URL = r"https?://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/(?:issues|pull)/[0-9]+"
REF = f"(?:{REF_HASH}|{REF_URL})"

# Unlike check_closing_reference.sh, this scan does NOT distinguish a canonical
# trailer line from a stray inline reference. That distinction is about where the
# author DECLARED intent, which is that script's business. This one only cares
# what GitHub will actually close on merge, and GitHub closes both.
CLOSING_RE = re.compile(rf"\b(?:{KEYWORDS})[ \t]+(?P<ref>{REF})", re.I)

_URL_RE = re.compile(
    r"^https?://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/(?:issues|pull)/([0-9]+)$",
    re.I)
_HASH_RE = re.compile(r"^(?:([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+))?#([0-9]+)$")


class ManifestError(Exception):
    """The manifest could not be read well enough for the check to mean anything."""


def _parse_ref(ref: str, default_owner: str, default_repo: str
               ) -> tuple[str, str, int] | None:
    """(owner, repo, number) for "#N", "owner/repo#N" or a full issue/PR URL."""
    ref = ref.strip()
    m = _URL_RE.match(ref)
    if m:
        return m.group(1), m.group(2), int(m.group(3))
    m = _HASH_RE.match(ref)
    if m:
        return (m.group(1) or default_owner), (m.group(2) or default_repo), int(m.group(3))
    return None


def default_owner_repo() -> tuple[str, str]:
    slug = os.environ.get("GITHUB_REPOSITORY") or DEFAULT_REPO
    owner, _, repo = slug.partition("/")
    return owner, repo


def closing_references(text: str, source: str
                       ) -> list[tuple[tuple[str, str, int], str, str]]:
    """Every ((owner, repo, number), source, line) GitHub would close from `text`."""
    owner, repo = default_owner_repo()
    found: list[tuple[tuple[str, str, int], str, str]] = []
    for line in (text or "").split("\n"):
        for m in CLOSING_RE.finditer(line):
            triple = _parse_ref(m.group("ref"), owner, repo)
            if triple is not None:
                found.append((triple, source, line.strip()))
    return found


# --------------------------------------------------------------------------
# The manifest
# --------------------------------------------------------------------------

@dataclass(frozen=True)
class GapEntry:
    source_file: str
    codeunit: str
    method: str
    issue: str
    owner: str
    repo: str
    number: int

    @property
    def key(self) -> tuple[str, str, int]:
        # GitHub owner/repo names are case-insensitive; issue numbers are not names.
        return (self.owner.lower(), self.repo.lower(), self.number)


def load_known_gap_entries(manifest_dir: str) -> list[GapEntry]:
    """Every expect-fail-known-gap entry under `manifest_dir` (non-recursive).

    Raises ManifestError rather than returning a short list: a guard that
    silently reads fewer entries than the manifest holds is worse than no guard,
    because it looks like coverage.
    """
    if not os.path.isdir(manifest_dir):
        raise ManifestError(
            f"expectation manifest directory '{manifest_dir}' does not exist. That is a "
            "broken checkout or a wrong path, not an empty manifest -- refusing to report "
            "a pass without having read anything.")

    entries: list[GapEntry] = []
    for name in sorted(f for f in os.listdir(manifest_dir) if f.endswith(".json")):
        path = os.path.join(manifest_dir, name)
        if not os.path.isfile(path):
            continue
        try:
            with open(path, encoding="utf-8") as fh:
                doc = json.load(fh)
        except (OSError, json.JSONDecodeError) as exc:
            raise ManifestError(
                f"{name}: could not be read as JSON ({exc}). The runner's own loader aborts "
                "on a malformed expectation file, and so does this check.") from exc
        if not isinstance(doc, list):
            raise ManifestError(
                f"{name}: top level must be a JSON array of expectation objects "
                f"(got {type(doc).__name__}).")

        in_file = 0
        for i, raw in enumerate(doc):
            if not isinstance(raw, dict):
                raise ManifestError(f"{name}: entry {i} is not a JSON object.")
            if (raw.get("Mode") or "") != "expect-fail-known-gap":
                continue
            in_file += 1
            cu = str(raw.get("CodeunitName") or "?")
            method = str(raw.get("Method") or "?")
            issue = raw.get("Issue")
            if not isinstance(issue, str) or not issue.strip():
                raise ManifestError(
                    f"{name}: expect-fail-known-gap entry {cu}.{method} has no 'Issue'. That "
                    "mode means 'real BC does this, the runner does not yet, and Issue tracks "
                    "the work' -- without the link there is nothing for this check to compare "
                    "against, and nothing tracking the gap.")
            triple = _parse_ref(issue, *default_owner_repo())
            if triple is None:
                raise ManifestError(
                    f"{name}: expect-fail-known-gap entry {cu}.{method} has Issue '{issue}', "
                    "which is not a resolvable GitHub issue reference (expected a full "
                    ".../issues/N URL, or owner/repo#N).")
            entries.append(GapEntry(name, cu, method, issue.strip(), *triple))

        if in_file == 0 and len(doc) > 0 and name.startswith("known-gaps-"):
            raise ManifestError(
                f"{name}: the file name says known-gaps but not one of its {len(doc)} "
                "entries is expect-fail-known-gap. tests/expectations/README.md requires the "
                "file prefix and the entry Mode to agree, and a disagreement silences this "
                "whole file for the check below -- which is the failure mode the check exists "
                "to prevent. Move the entries into the file matching their Mode.")

    return entries


# --------------------------------------------------------------------------
# The non-blocking sweep -- the only part that touches the network
# --------------------------------------------------------------------------

def issue_state(owner: str, repo: str, number: int) -> str | None:
    """'open' / 'closed', or None when the state could not be determined.

    None is never treated as evidence of anything: the caller says out loud that
    it could not check, and does not change its verdict either way.
    """
    try:
        proc = subprocess.run(
            ["gh", "api", f"repos/{owner}/{repo}/issues/{number}", "--jq", ".state"],
            capture_output=True, text=True, timeout=30)
        if proc.returncode == 0 and proc.stdout.strip() in ("open", "closed"):
            return proc.stdout.strip()
    except (OSError, subprocess.SubprocessError):
        pass

    req = urllib.request.Request(
        f"https://api.github.com/repos/{owner}/{repo}/issues/{number}",
        headers={"Accept": "application/vnd.github+json",
                 "User-Agent": "check_expectation_gap_issues"})
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            state = json.load(resp).get("state")
            return state if state in ("open", "closed") else None
    except (urllib.error.URLError, OSError, ValueError, json.JSONDecodeError):
        return None


def report_closed_issues(entries: list[GapEntry]) -> int:
    """Warn about entries linking an already-closed issue. NEVER fails the job."""
    if not entries:
        print("No expect-fail-known-gap entries in the manifest -- nothing to sweep.")
        return 0

    by_issue: dict[tuple[str, str, int], list[GapEntry]] = {}
    for e in entries:
        by_issue.setdefault(e.key, []).append(e)

    closed = unresolved = 0
    for key in sorted(by_issue):
        group = by_issue[key]
        owner, repo, number = group[0].owner, group[0].repo, group[0].number
        state = issue_state(owner, repo, number)
        where = ", ".join(sorted({f"{e.source_file} ({e.codeunit}.{e.method})" for e in group}))
        if state is None:
            unresolved += 1
            print(f"::warning::Could not determine the state of {owner}/{repo}#{number}, so "
                  f"the stale-entry sweep did NOT run for it. Entries: {where}. This half of "
                  "the check is advisory and never fails the job -- but an unreachable API is "
                  "not a clean bill of health either, so it says so rather than staying quiet.")
        elif state == "closed":
            closed += 1
            print(f"::warning::{owner}/{repo}#{number} is CLOSED, but {len(group)} "
                  f"expect-fail-known-gap entr{'y' if len(group) == 1 else 'ies'} still link "
                  f"it: {where}. A closed issue does not by itself prove the entry is stale "
                  "(#2858: it may have closed as a duplicate, or with the gap still open), so "
                  "this is a lead and not a verdict -- check whether the test now passes, then "
                  "either delete the entry or re-target it at the issue tracking what remains.")

    print(f"Swept {len(by_issue)} linked issue(s) across {len(entries)} entr"
          f"{'y' if len(entries) == 1 else 'ies'}: {closed} closed, {unresolved} unresolved.")
    return 0


# --------------------------------------------------------------------------

def main(argv: list[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    report = "--report-closed-issues" in args
    positional = [a for a in args if not a.startswith("-")]
    manifest_dir = positional[0] if positional else DEFAULT_MANIFEST_DIR

    try:
        entries = load_known_gap_entries(manifest_dir)
    except ManifestError as exc:
        print(f"::error::{exc}", file=sys.stderr)
        return 2

    if report:
        return report_closed_issues(entries)

    title = os.environ.get("PR_TITLE", "")
    body = os.environ.get("PR_BODY", "")
    commits = os.environ.get("PR_COMMITS", "")
    if not (title + body + commits).strip():
        print("::error::PR_TITLE, PR_BODY and PR_COMMITS are all empty. Every pull request has "
              "a title and at least one commit, so this is a failure to fetch them, not a PR "
              "with nothing in it -- refusing to report a pass without having read the text "
              "whose closing references decide this check.", file=sys.stderr)
        return 2

    refs = (closing_references(body, "body")
            + closing_references(title, "title")
            + closing_references(commits, "commit message"))
    closing: dict[tuple[str, str, int], tuple[str, str]] = {}
    for triple, source, line in refs:
        closing.setdefault((triple[0].lower(), triple[1].lower(), triple[2]), (source, line))

    offenders = [(e, closing[e.key]) for e in entries if e.key in closing]

    if offenders:
        for entry, (source, line) in offenders:
            print(
                f"::error file=tests/expectations/{entry.source_file}::This PR closes "
                f"{entry.owner}/{entry.repo}#{entry.number} (from the PR {source}: "
                f"\"{line}\"), but tests/expectations/{entry.source_file} still declares "
                f"expect-fail-known-gap for {entry.codeunit}.{entry.method} linking that same "
                "issue. The PR says the gap is fixed and the manifest says it is not; exactly "
                "one is right, and both are in this diff. Once this merges the manifest goes "
                "loud in the other direction and the NEXT run of main fails with \"Test passed "
                "cleanly but manifest declares expect-fail-known-gap ... Remove the entry\" -- "
                "which is how main went red twice on 2026-09-05 (#2844, #2858). Settle it "
                "here: delete the entry if this fix makes that test pass, re-target it at the "
                "OPEN issue tracking whatever remains, or drop the closing reference if this "
                "PR does not actually close the issue.",
                file=sys.stderr)
        one = len(offenders) == 1
        print(f"::error::{len(offenders)} expect-fail-known-gap entr{'y' if one else 'ies'} "
              f"link{'s' if one else ''} an issue this PR closes.", file=sys.stderr)
        return 1

    print(f"Checked {len(entries)} expect-fail-known-gap entr"
          f"{'y' if len(entries) == 1 else 'ies'} in {manifest_dir} against "
          f"{len(closing)} closing reference(s) declared by this PR: no overlap.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
