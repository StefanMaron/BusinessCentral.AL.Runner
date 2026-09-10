#!/usr/bin/env python3
"""Matrix-documentation drift + agent worktree-path guards (#3426).

Ported from AlRunner.Tests/BcMatrixDocumentationDriftTests.cs (#2883) and
AlRunner.Tests/AgentWorktreePathCollisionGuardTests.cs (#3014), which read .md
files but ran only on the BC matrix. Since #3421 a docs-only pull request skips
that matrix, so both guards fired on the merge commit's push to main instead of
on the pull request that introduced the drift -- a red main rather than a red
PR. That is not hypothetical: this guard produced a red main on 2026-09-09 and
separately blocked #3650 and #3648 on their own new doc lines, in every case
after the fact. pr-gate.yml runs every tools/test_*.py on every pull request,
discovered by glob, so here it gates before merge.

What each check measures, and against what:

  * Version lists in prose            -> .github/bc-versions.txt, pr-bc-versions.txt
  * The NotAMatrixClaim allowlist     -> itself; a dead entry fails
  * The corpus's eight-version claim  -> tests/al-language/.github/workflows/ci.yml
  * "AlRunner.Tests runs on N legs"   -> the unit-test prefixes bc-tests.yml derives
  * The impl-agent definition         -> both version files, positively
  * The aggregate required check name -> .github/workflows/test-matrix.yml
  * Worktree path templates           -> .claude/agents/**.md, rendered

Every check must see a non-empty match set, so a regex that drifts to match
nothing fails instead of passing vacuously.

Run: python3 tools/test_matrix_docs_drift.py
"""
from __future__ import annotations

import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GITHUB_DIR = os.path.join(ROOT, ".github")

FAILURES: list[str] = []


def check(name: str, offenders: list[str], seen: int, what: str,
          note: str = "") -> None:
    """Report one check. seen == 0 is a failure: a guard that matched nothing.

    `note` is explanatory prose printed after the offenders; it is deliberately
    NOT part of the list, so the reported count is the number of real offenders."""
    if seen == 0:
        print(f"  FAIL {name}: matched nothing -- the guard is not guarding anything")
        FAILURES.append(name)
        return
    if offenders:
        print(f"  FAIL {name} ({len(offenders)} of {seen} {what}):")
        for o in offenders:
            print(f"         {o}")
        if note:
            for line in note.splitlines():
                print(f"         {line}")
        FAILURES.append(name)
    else:
        print(f"  ok   {name} ({seen} {what})")


def rel(path: str) -> str:
    return os.path.relpath(path, ROOT).replace(os.sep, "/")


def read(path: str) -> str:
    with open(path, encoding="utf-8") as fh:
        return fh.read()


# --- the documents an agent reads to decide what CI measured -----------------
# Deliberately the whole of docs/ and .claude/ rather than a named list: #2883's
# four stale spots were spread across a rule, an agent definition and a doc, and
# a named list is exactly the thing that would not have grown to cover the next.

def doc_files() -> list[str]:
    out = []
    for rel_root in ("docs", ".claude"):
        root = os.path.join(ROOT, rel_root)
        if not os.path.isdir(root):
            continue
        for f in glob.glob(os.path.join(root, "**", "*.md"), recursive=True):
            r = rel(f)
            # archive/ is frozen by declaration -- it records what was true then.
            # worktrees/ is other agents' checkouts, not this tree's prose.
            if "/archive/" in r or r.startswith(".claude/worktrees/"):
                continue
            out.append(f)
    readme = os.path.join(ROOT, "README.md")
    if os.path.exists(readme):
        out.append(readme)
    return sorted(out)


def prefixes(filename: str) -> list[str]:
    """BC version prefixes from a .github version file, comments stripped."""
    path = os.path.join(GITHUB_DIR, filename)
    if not os.path.exists(path):
        raise SystemExit(f"expected {filename} at {path}")
    out = []
    for line in read(path).splitlines():
        if line.lstrip().startswith("#"):
            continue
        out.extend(line.split())
    return out


def version_key(v: str) -> tuple[int, ...]:
    return tuple(int(p) for p in v.split("."))


# A run of three or more BC version prefixes written out in prose -- "27.0, 27.5
# and 28.4", "27.3, 28.0, 28.1, 28.2 or 28.3". Three is the floor on purpose: a
# pair like "27.5 and 28.3" is overwhelmingly a past measurement ("green on BC
# 27.5 and 28.3"), which #2883 itself calls out as legitimate.
VERSION_RUN = re.compile(
    r"\b2[0-9]\.[0-9]+(?:\s*(?:,|,?\s*(?:and|or)|/)\s*\b2[0-9]\.[0-9]+){2,}")

_MEMBER = re.compile(r"2[0-9]\.[0-9]+")


def members_of(version_run: str) -> list[str]:
    return _MEMBER.findall(version_run)


def canonical(versions) -> str:
    return " ".join(sorted(dict.fromkeys(versions), key=version_key))


# Version runs that are NOT a claim about a matrix, keyed by (file, the run's
# canonical member set). Each needs a reason, and a dead entry fails
# check_allowlist_has_no_dead_entries -- an allowlist nobody prunes is how the
# next stale claim gets in wearing a waiver.
NOT_A_MATRIX_CLAIM = [
    ("docs/limitations.md", "27.0 27.3 27.5 28.1 28.2 28.4",
     "the BC artifacts that happened to be cached on the machine that measured the "
     "install-seeding column check -- a historical observation, not the matrix"),
    (".claude/rules/precompiled-dll-respect.md", "27.0 27.5 28.1 28.4",
     "the artifact directories that happened to be provisioned on the machine that "
     "checked whether TestPageClient.dll ships -- a historical observation of where the "
     "DLL was found, not a claim that those are the matrix legs (#3799)"),
    ("docs/incidents/precompiled-dll-respect.md", "27.0 27.5 28.1 28.4",
     "the same historical observation as the rule it documents -- which artifact "
     "directories were checked for TestPageClient.dll and Framework.UI.dll (#3799)"),
    ("docs/upstream-corpus-workflow.md", "27.1 27.2 27.4",
     "the 27 minors the corpus does NOT run; naming the gap is the point of the sentence"),
    ("docs/virtual-tables-allobj.md", "27.0 27.3 27.5 28.2 28.3",
     "the corpus legs that had reported when the upstream Install-subtype assertion was "
     "adjudicated -- a historical observation of which legs answered, not the matrix"),
    # The two halves of one measured version split (#3640). Corpus run 34328827788
    # answered differently on the two families for closing a page after a refused
    # write that a successful write had followed; the arms were then split so each
    # half is green on all eight, confirmed by run 34331496862. Both rows record
    # which legs gave which answer on a particular run -- a historical measurement,
    # and the whole finding. Rewriting either into a matrix set would delete the
    # observation the table exists for.
    ("docs/testpage-write-buffer.md", "28.0 28.1 28.2 28.3 28.4",
     "the legs that closed the page cleanly after a refused-then-successful write, "
     "corpus run 34328827788 -- a measured half of a version split, not the matrix"),
    ("docs/testpage-write-buffer.md", "27.0 27.3 27.5",
     "the legs that raised \"The record that you tried to open is not available.\" on "
     "the same write sequence, corpus run 34328827788 -- the other measured half"),
    ("docs/runtime-packages.md", "27.5 28.1 28.4",
     "the three BC compilers that built the three genuine third-party runtime packages measured "
     "for #3537 -- a historical measurement of which builds were compared, not the matrix"),
]


# --- version lists written out in prose --------------------------------------

def check_version_lists_in_docs() -> None:
    """Every BC version list in an agent-facing document must be one of the three
    sets the version files define: the full matrix, the pull-request subset, or
    the difference (the legs a PR does not run)."""
    all_v = canonical(prefixes("bc-versions.txt"))
    pr_v = canonical(prefixes("pr-bc-versions.txt"))
    dropped = canonical([p for p in prefixes("bc-versions.txt")
                         if p not in set(prefixes("pr-bc-versions.txt"))])
    allowed = {all_v, pr_v, dropped}
    waived = {(f, v) for f, v, _ in NOT_A_MATRIX_CLAIM}

    offenders, seen = [], 0
    for path in doc_files():
        r = rel(path)
        for i, line in enumerate(read(path).splitlines(), start=1):
            for m in VERSION_RUN.finditer(line):
                seen += 1
                canon = canonical(members_of(m.group(0)))
                if canon in allowed or (r, canon) in waived:
                    continue
                offenders.append(f'{r}:{i}: "{m.group(0)}" -> {{{canon}}}')

    check("version lists in docs name only a set derived from the version files",
          offenders, seen, "version runs",
          note="-- these documents write out a BC version list that is none of the three sets\n"
               "   the version files define. Either the document is stale (the #2883 shape) or\n"
               "   the run is a historical measurement that belongs in NOT_A_MATRIX_CLAIM with\n"
               "   a reason.\n"
               f"     full matrix (.github/bc-versions.txt):     {all_v}\n"
               f"     pull request (.github/pr-bc-versions.txt): {pr_v}\n"
               f"     a PR does not run:                         {dropped}")


def check_allowlist_has_no_dead_entries() -> None:
    """A waiver that no longer matches anything is a waiver nobody re-read. Fail
    rather than carry it, so the list cannot silently become permission for a
    claim that has since changed."""
    offenders = []
    for file, versions, why in NOT_A_MATRIX_CLAIM:
        path = os.path.join(ROOT, file)
        if not os.path.exists(path):
            offenders.append(f"NOT_A_MATRIX_CLAIM names {file}, which does not exist.")
            continue
        found = any(canonical(members_of(m.group(0))) == versions
                    for m in VERSION_RUN.finditer(read(path)))
        if not found:
            offenders.append(
                f'NOT_A_MATRIX_CLAIM waives "{versions}" in {file} ({why}), but no such '
                "version list is there any more. Delete the entry.")
    check("the NotAMatrixClaim allowlist has no dead entries",
          offenders, len(NOT_A_MATRIX_CLAIM), "entries")


# --- the corpus's own matrix, read out of the submodule ----------------------

def check_corpus_version_claim() -> None:
    """#2883's original defect, guarded: docs/upstream-corpus-workflow.md states
    which BC versions the corpus CI runs, and that statement must equal the
    include list in the corpus's own ci.yml at the pin this repository carries.

    Skipped when the submodule is not checked out -- pr-gate.yml's tools-tests
    job uses a bare actions/checkout, so on a pull request this check reports
    SKIP rather than a vacuous pass. It still runs on any checkout that has the
    submodule, which is every BC matrix leg."""
    corpus_ci = os.path.join(ROOT, "tests", "al-language", ".github", "workflows", "ci.yml")
    if not os.path.exists(corpus_ci):
        print("  SKIP the corpus version claim matches the submodule's own workflow: "
              "tests/al-language is not checked out here -- nothing to compare against")
        return

    # The matrix the corpus dispatches when no workflow_dispatch override is
    # given: the else-branch JSON in its `prepare` job. Read the bc_version
    # values out of it rather than counting include entries, so an added version
    # is caught by NAME.
    text = read(corpus_ci)
    idx = text.find("\n          else\n")
    else_branch = text[idx + 1:] if idx >= 0 else ""
    corpus_versions = re.findall(r'"bc_version":"(2[0-9]\.[0-9]+)"', else_branch)

    offenders = []
    if len(corpus_versions) < 2:
        check("the corpus version claim matches the submodule's own workflow",
              [f"could not read the corpus matrix out of {rel(corpus_ci)} -- the guard "
               "would pass vacuously, which is the failure it exists to prevent."],
              1, "claims")
        return

    doc = os.path.join(ROOT, "docs", "upstream-corpus-workflow.md")
    doc_text = read(doc)
    expected = canonical(corpus_versions)
    stated = [canonical(members_of(m.group(0))) for m in VERSION_RUN.finditer(doc_text)]

    if expected not in stated:
        offenders.append(
            "docs/upstream-corpus-workflow.md must write out the corpus's own matrix "
            f"({expected}) -- that is the list an agent counts green legs against. "
            f"Version lists it does state: {' | '.join(stated) if stated else '(none)'}")

    # And the count claim next to it. "eight" is a word, not a number, in that sentence.
    number_words = {2: "two", 3: "three", 4: "four", 5: "five", 6: "six",
                    7: "seven", 8: "eight", 9: "nine", 10: "ten"}
    word = number_words.get(len(corpus_versions))
    if word is None:
        offenders.append(f"the corpus now runs {len(corpus_versions)} versions -- "
                         "extend number_words.")
    else:
        flowed = re.sub(r"\s+", " ", doc_text)
        if f"**{word} BC versions" not in flowed:
            offenders.append(
                f'docs/upstream-corpus-workflow.md must say "**{word} BC versions" -- the '
                f"corpus dispatches {len(corpus_versions)} of them.")

    check("the corpus version claim matches the submodule's own workflow",
          offenders, len(corpus_versions), "corpus versions")


# --- what runs on a leg, as opposed to how many legs there are ---------------

CLAIMS_EVERY_LEG = re.compile(
    r"(each|all|every)\s+(of\s+)?(the\s+)?"
    r"(\d+|two|three|four|five|six|seven|eight)?\s*legs?\b", re.IGNORECASE)


def unit_prefixes() -> list[str]:
    """The unit legs: the newest minor of each major, as bc-tests.yml derives them."""
    by_major: dict[str, list[str]] = {}
    for p in prefixes("bc-versions.txt"):
        by_major.setdefault(p.split(".")[0], []).append(p)
    return sorted((max(v, key=version_key) for v in by_major.values()), key=version_key)


def check_no_doc_claims_the_suite_runs_on_every_leg() -> None:
    """AlRunner.Tests runs on the UNIT legs only -- the newest minor of each major
    (#2674, re-measured by #3141). A document claiming the C# suite runs on every
    leg tells an agent a red C# test will be caught by whichever leg it reads."""
    offenders, seen = [], 0
    for path in doc_files():
        for i, line in enumerate(read(path).splitlines(), start=1):
            idx = line.find("AlRunner.Tests")
            if idx < 0:
                continue
            seen += 1
            # Only the text that FOLLOWS the suite's name on that line -- an
            # unrelated earlier clause about legs is not a claim about the suite.
            m = CLAIMS_EVERY_LEG.search(line[idx:])
            if m:
                offenders.append(f'{rel(path)}:{i}: "{m.group(0).strip()}"')

    check("no document claims the C# suite runs on every BC leg",
          offenders, seen, "AlRunner.Tests mentions",
          note=f"-- AlRunner.Tests runs on the unit legs only ({canonical(unit_prefixes())}) -- "
               "the newest minor of each major, two legs, not every leg of whichever matrix ran.")


def check_impl_agent_names_the_pr_and_unit_legs() -> None:
    """The positive half of the check above: the impl-agent definition -- the one
    document every implementation agent loads before it pushes -- must name the
    legs a pull request actually runs and the legs that carry the C# suite, both
    derived from the version files. Without this, deleting the sentence would
    satisfy the negative check."""
    path = os.path.join(ROOT, ".claude", "agents", "impl-agent.md")
    text = read(path)
    pr_canon = canonical(prefixes("pr-bc-versions.txt"))
    unit = unit_prefixes()

    runs = {canonical(members_of(m.group(0))) for m in VERSION_RUN.finditer(text)}
    offenders = []
    if pr_canon not in runs:
        offenders.append(
            f"impl-agent.md must write out the pull-request legs ({pr_canon}); "
            f"version lists it does state: {' | '.join(sorted(runs)) if runs else '(none)'}")

    # The unit legs are a pair, which VERSION_RUN deliberately does not match, so
    # assert the pair literally instead of widening a regex that would then
    # swallow every historical "green on BC 27.5 and 28.3".
    pair = f"{unit[0]} and {unit[1]}"
    if pair not in text:
        offenders.append(f'impl-agent.md must name the unit legs as "{pair}".')
    for named in (".github/pr-bc-versions.txt", ".github/bc-versions.txt"):
        if named not in text:
            offenders.append(f"impl-agent.md must name {named}, the file the legs come from.")

    check("impl-agent.md names the pull-request legs and the unit legs",
          offenders, 4, "claims")


# --- the aggregate required check, by name -----------------------------------

def check_no_document_names_the_retired_aggregate_check() -> None:
    """#3200 renamed the aggregate required check because a narrowed matrix under
    the old name asserted something the run had not measured. A document still
    naming the old one sends an agent looking for a context that no longer
    reports -- indistinguishable, from the outside, from one that has not
    started yet."""
    # Sourced from the workflow, so this cannot pin a name the ruleset no longer
    # requires.
    matrix = read(os.path.join(GITHUB_DIR, "workflows", "test-matrix.yml"))
    m = re.search(r"^\s*name:\s*(BC test matrix passed|All BC versions passed)\s*$",
                  matrix, re.MULTILINE)
    if not m:
        check("no document names the retired aggregate check",
              ["test-matrix.yml must still name its aggregate job."], 1, "names")
        return
    current = m.group(1)
    # The name this replaced. Written out because a guard for a retired string
    # needs the string; this file is not under docs/, so carrying it here is inert.
    retired = "All BC versions passed" if current == "BC test matrix passed" \
        else "BC test matrix passed"

    offenders = [rel(f) for f in doc_files() if retired in read(f)]
    check("no document names the retired aggregate check",
          offenders, len(doc_files()), "documents",
          note=f'-- the aggregate required check is "{current}"; "{retired}" is retired and '
               "no longer reports.")


# --- agent worktree path templates (#3014) -----------------------------------
#
# An agent's worktree path must be derived from the SAME two components as its
# branch name -- the identity AND the issue number -- so that two agents sharing
# an identity but working different issues cannot land in the same directory.
#
# The incident: two loops both claimed the slot stma-auto-1. Their branch names
# did NOT collide (agent/stma-auto-1/issue-3005 vs .../issue-3011 are distinct,
# because a branch name carries the issue number). The WORKTREE PATH collided,
# because impl-agent.md derived it from the identity alone: both rendered
# .claude/worktrees/stma-auto-1. A directory is what git commit and git push
# consult to decide which branch they act on, so the second loop's two commits
# (554 added lines across 9 files, belonging to a different issue) landed on the
# first loop's PR branch, and CI reported Test Matrix SUCCESS on the mixture.
#
# Scope is .claude/agents/ -- the directory whose files PRESCRIBE a worktree to
# create, so a future agent definition inherits the guard. Mentions elsewhere
# DESCRIBE existing directories and are deliberately out of scope.
#
# Within those files only a TEMPLATE is asserted over: a path token carrying at
# least one <...> placeholder, which is what makes it something an agent renders.
# A fully concrete token such as .claude/worktrees/stma-auto-1 is a citation of
# one historical directory -- impl-agent.md's explanation of the incident
# contains exactly that. Flagging those would train readers to ignore the guard.

AGENTS_DIR = os.path.join(ROOT, ".claude", "agents")
ISSUE_PLACEHOLDER = "<N>"

# The trailing character class stops at whatever ends a path in markdown prose or
# a fenced command -- whitespace, a backtick, a quote or a bracket.
WORKTREE_TOKEN = re.compile(r"\.claude/worktrees/([^\s`\"'’)\]]+)")


def worktree_path_tokens() -> list[tuple[str, int, str]]:
    out = []
    for path in sorted(glob.glob(os.path.join(AGENTS_DIR, "**", "*.md"), recursive=True)):
        for i, line in enumerate(read(path).splitlines(), start=1):
            for m in WORKTREE_TOKEN.finditer(line):
                out.append((rel(path), i, m.group(1)))
    return out


def prescribed_templates() -> list[tuple[str, int, str]]:
    """Templates only -- the tokens an agent is expected to render."""
    return [t for t in worktree_path_tokens() if "<" in t[2]]


def render(token: str, identity: str, issue: str) -> str:
    return token.replace("<AGENT-ID>", identity).replace(ISSUE_PLACEHOLDER, issue)


def check_worktree_templates_carry_the_issue_number() -> None:
    """The guard proper. Every prescribed worktree path must carry the
    issue-number placeholder, so it cannot be rendered without one."""
    tokens = prescribed_templates()
    if not tokens:
        check("every prescribed worktree path carries the issue number",
              [f"no `.claude/worktrees/<...>` TEMPLATE found under {rel(AGENTS_DIR)}. Either "
               "the agent definitions stopped prescribing a worktree, or this guard's regex "
               f"drifted. ({len(worktree_path_tokens())} worktree path token(s) seen in total.)"],
              0, "templates")
        return

    offenders = [f"{f}:{i}  .claude/worktrees/{t}"
                 for f, i, t in tokens if ISSUE_PLACEHOLDER not in t]
    check("every prescribed worktree path carries the issue number",
          offenders, len(tokens), "templates",
          note=f"-- a prescribed worktree path omitting the `{ISSUE_PLACEHOLDER}` issue "
               "placeholder means two agents sharing an identity but working different issues "
               "render the SAME directory -- the #3014 mechanism. Derive the path from "
               "identity AND issue, mirroring `agent/<AGENT-ID>/issue-<N>`.")


def check_same_identity_different_issues_differ() -> None:
    """The incident itself, rendered. Two DIFFERENT issues under the SAME identity
    must produce two different directories. This is the assertion that fails
    against the pre-fix wording: both sides render .claude/worktrees/stma-auto-1."""
    tokens = prescribed_templates()
    offenders = []
    for f, i, t in tokens:
        a, b = render(t, "stma-auto-1", "3005"), render(t, "stma-auto-1", "3011")
        if a == b:
            offenders.append(
                f"{f}:{i}: `.claude/worktrees/{t}` renders to `{a}` for BOTH issue 3005 and "
                "issue 3011 under identity 'stma-auto-1'. That is the #3014 collision: one "
                "loop committed and pushed into the other's checkout, onto the other's branch.")
    check("same identity, different issues render different worktrees",
          offenders, len(tokens), "templates")


def check_different_identities_same_issue_differ() -> None:
    """The converse, so the fix cannot be satisfied by a path that varies ONLY by
    issue and drops the identity -- the same defect wearing the other hat."""
    tokens = prescribed_templates()
    offenders = []
    for f, i, t in tokens:
        a, b = render(t, "stma-auto-1", "3005"), render(t, "stma-auto-2", "3005")
        if a == b:
            offenders.append(
                f"{f}:{i}: `.claude/worktrees/{t}` renders to `{a}` for both identity "
                "'stma-auto-1' and 'stma-auto-2' on issue 3005, so two loops on DIFFERENT "
                "slots would share a checkout.")
    check("different identities, same issue render different worktrees",
          offenders, len(tokens), "templates")


def check_impl_agent_verifies_the_branch() -> None:
    """The path fix makes a same-identity/different-issue collision impossible to
    express. This pins the independent check that catches the same incident from
    the other side, and catches it even when the directory is the right one:
    before its first commit an agent must verify the BRANCH its checkout is on,
    not only the toplevel directory.

    #3014's second loop was in a checkout whose HEAD was
    agent/stma-auto-1/issue-3005 while it worked issue 3011. `git rev-parse
    --show-toplevel` -- the only verification the definition asked for -- would
    have looked correct to it. Comparing HEAD against its own issue number would
    not have."""
    text = read(os.path.join(AGENTS_DIR, "impl-agent.md"))
    required = {
        "git rev-parse --show-toplevel":
            "impl-agent.md no longer tells the agent to verify the worktree directory "
            "before its first commit.",
        "git rev-parse --abbrev-ref HEAD":
            "impl-agent.md tells the agent to verify its working DIRECTORY before the first "
            "commit but not its BRANCH. That is the #3014 hole: an agent sharing a checkout "
            "sees the directory it expected and a branch belonging to somebody else's issue, "
            "and commits onto it.",
    }
    offenders = [why for needle, why in required.items() if needle not in text]
    check("impl-agent.md verifies the branch, not just the directory, before committing",
          offenders, len(required), "checks")


# --- self-tests of the matching logic, so a wrong rule cannot pass silently ---

def check_matching_logic() -> None:
    cases = {
        "a three-version run is a version list":
            VERSION_RUN.search("27.0, 27.5 and 28.4").group(0) == "27.0, 27.5 and 28.4",
        "'or' joins a run too":
            VERSION_RUN.search("27.3, 28.0, 28.1, 28.2 or 28.3") is not None,
        "slashes join a run too":
            VERSION_RUN.search("27.0/27.3/27.5") is not None,
        "a PAIR is deliberately not a version list":
            VERSION_RUN.search("green on BC 27.5 and 28.3") is None,
        "canonical sorts numerically, not lexically":
            canonical(["28.4", "27.10", "27.3"]) == "27.3 27.10 28.4",
        "canonical de-duplicates":
            canonical(["27.0", "27.0", "28.4"]) == "27.0 28.4",
        "a worktree template is a token carrying a placeholder":
            prescribed_templates_of("`.claude/worktrees/<AGENT-ID>-issue-<N>`")
            == ["<AGENT-ID>-issue-<N>"],
        "a concrete historical path is NOT a template":
            prescribed_templates_of("both rendered .claude/worktrees/stma-auto-1 that day")
            == [],
        "a template missing <N> renders the same for two issues":
            render("<AGENT-ID>", "a", "1") == render("<AGENT-ID>", "a", "2"),
        "a template carrying both renders differently for two issues":
            render("<AGENT-ID>-issue-<N>", "a", "1") != render("<AGENT-ID>-issue-<N>", "a", "2"),
        "'runs on each leg' is a claim about every leg":
            CLAIMS_EVERY_LEG.search("AlRunner.Tests runs on each leg") is not None,
        "'runs on two legs' alone is not":
            CLAIMS_EVERY_LEG.search("AlRunner.Tests runs on 27.5 and 28.4") is None,
    }
    offenders = [name for name, ok in cases.items() if not ok]
    check("matching-logic self-test", offenders, len(cases), "cases")


def prescribed_templates_of(text: str) -> list[str]:
    """The template filter applied to one string -- used by the self-test above."""
    return [m.group(1) for m in WORKTREE_TOKEN.finditer(text) if "<" in m.group(1)]


def main() -> int:
    print(f"matrix-documentation drift + worktree-path guards, repo root {ROOT}")
    check_matching_logic()
    check_version_lists_in_docs()
    check_allowlist_has_no_dead_entries()
    check_corpus_version_claim()
    check_no_doc_claims_the_suite_runs_on_every_leg()
    check_impl_agent_names_the_pr_and_unit_legs()
    check_no_document_names_the_retired_aggregate_check()
    check_worktree_templates_carry_the_issue_number()
    check_same_identity_different_issues_differ()
    check_different_identities_same_issue_differ()
    check_impl_agent_verifies_the_branch()
    if FAILURES:
        print(f"\n{len(FAILURES)} check(s) failed: {', '.join(FAILURES)}")
        return 1
    print("\nall matrix-documentation and worktree-path checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
