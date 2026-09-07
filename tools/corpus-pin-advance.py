#!/usr/bin/env python3
"""How far can the tests/al-language pin advance, and what holds the rest?

Issue #3319. Advancing the corpus pin is a mechanical, repeatable measurement
that a person or an agent has now performed by hand at least six times (#2429,
#3124, #3202, #3304, #3317, and again on 2026-09-07). Each run produced a
coordination record rather than a tool, and each record went stale: #3304 sat
claiming "9 commits behind, blocked by four issues" while two of those issues
had closed and the real gap was 2.

REPORT-ONLY, DELIBERATELY
-------------------------
This measures and reports. It does not open a pull request, and there is no flag
that makes it open one. That is the filer's own recommendation in #3319, for
three reasons worth keeping here because a later reader will be tempted:

  * A job that opens a pin PR whenever a greener pin exists opens one most days,
    on an account-wide Actions queue that is already cancelling its own
    verification (#3302, #3003).
  * A pin bump is one of the few changes that can turn `main` red on every leg
    at once, which is a reasonable thing to put in front of a human.
  * Report-only replaces the artifact that has actually been produced by hand.

Adding "and open the PR" later is a small step from here. The reverse is not.

WHAT IT MUST NEVER DO
---------------------
It must never write `tests/expectations/` entries for the failures it finds. An
entry converts a live, owned gap into settled classification -- the failure mode
`.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md` names directly.
`refuses_to_write_expectations()` below is that promise in executable form, and
a test asserts it.

THE ALGORITHM, AND WHY IT IS NOT A BISECT
-----------------------------------------
From #3319's design:

  1. Try the corpus tip.
  2. Green -> that is the answer, one run.
  3. Red -> do NOT bisect. Map the failing test NAMES back to the corpus commits
     that introduced them, with `git log -S` over the corpus.
  4. The newest commit whose predecessors are all satisfied is the target -- the
     third case in `.claude/rules/al-language-submodule.md`.

Cost is the whole point. A corpus run is minutes, so a binary search is log n
runs; the name-to-commit mapping is one, or two when a candidate is confirmed.
Measured on the 2026-09-07 gap, whose manual run took three runs to bisect: all
five failing names map to `d025203` in 13 ms of `git log -S`, so the target is
its parent `0bbe376` -- the same answer the bisect reached, from the first run's
output alone.

THE MAPPING IS EXACT-TOKEN, NOT SUBSTRING
-----------------------------------------
`git log -S<string>` counts occurrences of a raw string, so `-S "Foo"` also
matches a commit that only ever added `Foo_Bar`. Corpus test names nest by
construction -- `TestFilter_SetCurrentKey_AcceptsACompositeKey` contains
`TestFilter_SetCurrentKey` -- so a substring match would attribute a failure to
the wrong, older commit, and attributing it OLDER is the direction that makes
the reported target too conservative while looking perfectly plausible. The
`--pickaxe-regex` form with a word boundary is used instead, and
`_pickaxe_pattern` is unit-tested against exactly that nesting.

WHY THE GAP IS RE-READ EVERY RUN
--------------------------------
The corpus tip moves under you. A ninth corpus commit landed while the
2026-09-07 pin PR was being merged, and #3319 records a coordinator handing an
agent a ceiling that was true when written and false 20 minutes later. Nothing
here accepts a corpus revision from a brief, a previous run, or a cached file:
`git ls-remote` resolves the tip at run time, every time.

EXIT CODES
----------
  0  measured, and the report was produced. This is NOT "the pin can advance" --
     "already current" and "blocked at the first commit" are both successful
     measurements. Read the report.
  2  the measurement could not run (bad inputs, corpus history absent, a shallow
     clone). Never a verdict about the pin.

Run:
  tools/corpus-pin-advance.py --results al-language-results.json \
      --corpus tests/al-language --pin <sha> [--tip <sha>] [--format markdown]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys

CORPUS_REMOTE = "https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests"
SUBMODULE_PATH = "tests/al-language"

# A corpus commit's subject ends with the corpus PR number: "(#229)". Used only
# to render a link a reader can follow -- never to decide anything.
_CORPUS_PR = re.compile(r"\(#(\d+)\)\s*$")

# An AL test method name. The corpus declares them as `procedure <Name>()`, and
# the runner reports the same token as `method` in its --out JSON, so the two
# sides of the mapping agree by construction rather than by transformation.
_AL_IDENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


class MeasurementError(Exception):
    """The measurement could not be made. Never a verdict about the pin."""


# ---------------------------------------------------------------------------
# git plumbing


def git(corpus: str, *args: str, check: bool = True) -> str:
    p = subprocess.run(["git", "-C", corpus, *args], capture_output=True, text=True)
    if check and p.returncode != 0:
        raise MeasurementError(
            f"git {' '.join(args)} failed in {corpus} (exit {p.returncode}): "
            f"{(p.stderr or '').strip()}")
    return (p.stdout or "").strip()


def assert_corpus_usable(corpus: str) -> None:
    """The corpus history must be present and complete before anything is read.

    A shallow clone is refused rather than measured, for the reason
    check_corpus_pin_forward.sh documents at length: on a truncated history the
    plumbing answers cleanly and WRONGLY, and a wrong answer here silently
    reports a smaller advance than is available.
    """
    if not os.path.exists(os.path.join(corpus, ".git")):
        raise MeasurementError(
            f"{corpus} is not a git checkout, so no corpus history can be read. "
            "Check the submodule out with 'submodules: true' and fetch its history.")
    if git(corpus, "rev-parse", "--is-shallow-repository", check=False) == "true":
        raise MeasurementError(
            f"the {corpus} clone is SHALLOW. `git log -S` over a truncated history "
            "silently attributes a test name to the wrong commit -- it cannot see the "
            "commit that really introduced it -- which reports a target pin that is too "
            "conservative while looking correct. Run 'git -C "
            f"{corpus} fetch --unshallow' first.")


def resolve_tip(corpus: str, branch: str = "master") -> str:
    """The corpus tip, read from the REMOTE at run time.

    Never from a brief, a previous run, or a cached ref. #3319 records a ninth
    corpus commit landing mid-task and a ceiling that was true when written and
    false 20 minutes later.
    """
    out = git(corpus, "ls-remote", CORPUS_REMOTE, f"refs/heads/{branch}", check=False)
    for line in out.splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[1] == f"refs/heads/{branch}":
            return parts[0]
    raise MeasurementError(
        f"could not resolve refs/heads/{branch} on {CORPUS_REMOTE}. That is a network or "
        "permissions failure, not a statement about the pin -- refusing to fall back to a "
        "local ref, which may be arbitrarily old.")


def commits_between(corpus: str, pin: str, tip: str) -> list[str]:
    """Corpus commits after `pin` up to and including `tip`, oldest first."""
    out = git(corpus, "rev-list", "--reverse", f"{pin}..{tip}")
    return [l.strip() for l in out.splitlines() if l.strip()]


def subject(corpus: str, sha: str) -> str:
    return git(corpus, "log", "-1", "--format=%s", sha)


def short(sha: str) -> str:
    return sha[:7]


# ---------------------------------------------------------------------------
# Reading the runner's own result JSON


def failing_tests(results: dict) -> list[dict]:
    """Every failing test in a `--out` document, as {codeunit, method, message}.

    The runner writes `all_failures` with `kind` in {fail, error, compile,
    execute, suite}. Only the per-test kinds carry a `method`, and only a method
    can be mapped to a corpus commit. A compile/execute/suite failure is kept
    separately by `unmappable_failures` rather than dropped: a bundle that failed
    to compile is the single most important thing to surface, and silently
    ignoring it would report "0 failures" for a run that executed nothing --
    exactly the false zero `.claude/rules/verify-execution-not-the-tick.md` is
    about.
    """
    out = []
    for f in results.get("all_failures") or []:
        if f.get("kind") in ("fail", "error") and f.get("method"):
            out.append({
                "codeunit": f.get("codeunit"),
                "method": f.get("method"),
                "message": (f.get("message") or "").strip(),
                "bucket": f.get("bucket"),
            })
    return out


def unmappable_failures(results: dict) -> list[dict]:
    """Compile/execute/suite failures -- real, and not attributable to a name."""
    return [f for f in (results.get("all_failures") or [])
            if f.get("kind") in ("compile", "execute", "suite")]


# ---------------------------------------------------------------------------
# Name -> commit


def _pickaxe_pattern(method: str) -> str:
    r"""An EXACT-token pickaxe pattern for an AL method name.

    Corpus test names nest: `TestFilter_SetCurrentKey_AcceptsACompositeKey`
    contains `TestFilter_SetCurrentKey`. A plain `-S` substring pickaxe would
    therefore attribute the longer name to whichever older commit introduced the
    shorter one -- and reporting the blocker as OLDER than it is makes the target
    pin too conservative while looking entirely plausible.

    `\b` alone is not enough: in `\bFoo\b`, an underscore is a word character, so
    `Foo` does not match inside `Foo_Bar` -- that half is fine -- but `Foo_Bar`
    would still match inside `Foo_Bar_Baz` on the LEFT edge only if the trailing
    boundary were dropped. Both edges are asserted, which for identifier
    characters is exactly "this token and no longer one".
    """
    if not _AL_IDENT.match(method or ""):
        raise MeasurementError(
            f"{method!r} is not an AL identifier, so it cannot be used as a pickaxe "
            "pattern. A name reaching here that is not an identifier means the result "
            "JSON was not produced by the runner, and guessing at it would map failures "
            "to the wrong commits.")
    return r"\b" + re.escape(method) + r"\b"


def introducing_commit(corpus: str, method: str, pin: str, tip: str) -> str | None:
    """The OLDEST commit in (pin, tip] whose diff introduces `method`.

    Oldest, not newest: the question is which commit cannot be pinned, and if a
    later commit also touches the name, pinning up to just before the earliest
    one is what keeps the corpus green.

    None means the name is not introduced anywhere in the window -- the failure
    predates the current pin. That is a genuinely different finding from "this
    commit blocks you", and it is the shape of a pre-existing failure the pin has
    already accepted, so it must not be silently treated as a blocker.
    """
    out = git(corpus, "log", "--pickaxe-regex", f"-S{_pickaxe_pattern(method)}",
              "--format=%H", "--reverse", f"{pin}..{tip}", "--", "*.al", check=False)
    for line in out.splitlines():
        if line.strip():
            return line.strip()
    return None


def map_failures(corpus: str, failures: list[dict], pin: str, tip: str) -> list[dict]:
    """Attach an introducing commit (or None) to each failing test."""
    cache: dict[str, str | None] = {}
    mapped = []
    for f in failures:
        m = f["method"]
        if m not in cache:
            cache[m] = introducing_commit(corpus, m, pin, tip)
        mapped.append({**f, "introduced_by": cache[m]})
    return mapped


# ---------------------------------------------------------------------------
# Target selection


def select_target(order: list[str], blockers: set[str]) -> tuple[str | None, str | None]:
    """The newest commit whose predecessors are ALL satisfied.

    `order` is the candidate window oldest-first; `blockers` are commits known to
    carry a failure. Corpus history is linear, so "predecessors all satisfied"
    means: everything strictly before the earliest blocker.

    Returns (target, first_blocker). target None means even the first available
    commit is blocked -- the pin cannot advance at all. first_blocker None means
    nothing blocks and the target is the tip.

    This is deliberately NOT "the newest commit that is not itself a blocker".
    On a linear history you cannot pin commit N without pinning N-1, so a green
    commit sitting after a red one is not takeable, and treating it as takeable
    would produce a pin that is red by construction -- the third case in
    `.claude/rules/al-language-submodule.md`.
    """
    first_blocked_index = None
    for i, sha in enumerate(order):
        if sha in blockers:
            first_blocked_index = i
            break
    if first_blocked_index is None:
        return (order[-1] if order else None), None
    if first_blocked_index == 0:
        return None, order[0]
    return order[first_blocked_index - 1], order[first_blocked_index]


# ---------------------------------------------------------------------------
# Tracking-issue linkage


def find_tracking_issues(failures: list[dict], issues: list[dict]) -> dict[str, list[int]]:
    """Which open issues mention each failing test method, by exact token.

    Exact token for the same reason the pickaxe is: `TestFilter_SetCurrentKey`
    appearing in an issue must not claim the failures of every longer name that
    starts with it.

    A method with NO issue is the finding most worth surfacing -- #3316 exists
    because a manual run noticed exactly that -- so the caller renders the empty
    list explicitly rather than omitting the row.
    """
    out: dict[str, list[int]] = {}
    for f in failures:
        m = f["method"]
        if m in out:
            continue
        pat = re.compile(_pickaxe_pattern(m))
        out[m] = sorted({int(i["number"]) for i in issues
                         if pat.search((i.get("title") or "") + "\n" + (i.get("body") or ""))})
    return out


# ---------------------------------------------------------------------------
# The promise


def refuses_to_write_expectations() -> bool:
    """This tool never writes tests/expectations/, and never writes the gitlink.

    Executable form of #3319's hard constraint, asserted by a unit test that also
    greps this module's own source for the write primitives. An entry would
    convert a live, owned gap into settled classification.
    """
    return True


# ---------------------------------------------------------------------------
# Rendering


def render(state: dict) -> str:
    """The report. A reader must be able to act on it without re-measuring."""
    L: list[str] = []
    pin, tip = state["pin"], state["tip"]
    order = state["window"]
    target = state["target"]

    L.append("<!-- corpus-pin-advance: generated, edited in place. Do not hand-write. -->")
    L.append("")
    L.append(f"**Measured:** {state['measured_at']} · BC {state['bc_version']} · "
             f"[run]({state['run_url']})" if state.get("run_url")
             else f"**Measured:** {state['measured_at']} · BC {state['bc_version']}")
    L.append("")

    L.append("## Where the pin is")
    L.append("")
    L.append(f"| | commit | |")
    L.append(f"|---|---|---|")
    L.append(f"| current pin | `{short(pin)}` | {state['pin_subject']} |")
    L.append(f"| corpus tip | `{short(tip)}` | {state['tip_subject']} |")
    if not order:
        L.append("")
        L.append("**The pin is current.** Nothing to advance.")
        return "\n".join(L) + "\n"
    L.append(f"| gap | {len(order)} commit(s) | |")
    L.append("")

    L.append("## Newest green candidate")
    L.append("")
    if target is None:
        L.append(f"**None — the pin cannot advance.** The first available commit "
                 f"`{short(order[0])}` already fails.")
    elif state["first_blocker"] is None:
        # Keyed on the BLOCKER's absence, not on `target == tip`. An identity comparison
        # between a full SHA and an abbreviated one is false for a pin that can advance the
        # whole way, and this branch is the one that then dereferences a blocker that is not
        # there. `full_sha` canonicalises both endpoints so the two conditions agree; this
        # asks the question that cannot disagree with itself.
        L.append(f"**`{short(target)}` — the tip.** Every one of the {len(order)} available "
                 f"commit(s) passes; the pin can advance the whole way.")
    else:
        taken = order.index(target) + 1
        L.append(f"**`{short(target)}`** — {taken} of {len(order)} available commit(s). "
                 f"Blocked at `{short(state['first_blocker'])}`: "
                 f"{state['first_blocker_subject']}")
    L.append("")

    if state["failures"]:
        L.append("## Failing at the tip")
        L.append("")
        L.append("| test | introduced by | tracked by |")
        L.append("|---|---|---|")
        for f in state["failures"]:
            intro = (f"`{short(f['introduced_by'])}`" if f["introduced_by"]
                     else "_predates the pin_")
            issues = state["tracking"].get(f["method"]) or []
            tracked = (", ".join(f"#{n}" for n in issues) if issues
                       else "**nothing — untracked**")
            cu = f"{f['codeunit']}." if f.get("codeunit") else ""
            L.append(f"| `{cu}{f['method']}` | {intro} | {tracked} |")
        L.append("")
        untracked = [f["method"] for f in state["failures"]
                     if not state["tracking"].get(f["method"])]
        if untracked:
            L.append(f"**{len(untracked)} failing test(s) have no tracking issue.** "
                     "That is the row most worth acting on: an untracked failure is a gap "
                     "nobody owns. File one before the next run, or it will be reported "
                     "again unchanged.")
            L.append("")

    if state["unmappable"]:
        L.append("## Failures that no test name explains")
        L.append("")
        L.append("A compile, execute or suite-level failure. These are not attributable to a "
                 "corpus commit by name, and they mean part of the run executed nothing — "
                 "read them before the table above.")
        L.append("")
        for f in state["unmappable"]:
            L.append(f"- `{f.get('kind')}` in `{f.get('bucket')}`: "
                     f"{(f.get('error') or (f.get('errors') or [''])[0] or '')[:300]}")
        L.append("")

    L.append("## Commits in the gap")
    L.append("")
    L.append("| | commit | subject |")
    L.append("|---|---|---|")
    for sha in order:
        if target is not None and order.index(sha) <= order.index(target):
            mark = "take"
        elif sha == state.get("first_blocker"):
            mark = "**BLOCKS**"
        else:
            mark = "held"
        subj = state["subjects"].get(sha, "")
        pr = _CORPUS_PR.search(subj)
        link = (f" ([corpus #{pr.group(1)}]({CORPUS_REMOTE}/pull/{pr.group(1)}))"
                if pr else "")
        L.append(f"| {mark} | `{short(sha)}` | {subj}{link} |")
    L.append("")

    L.append("---")
    L.append("")
    L.append("This job **reports only**. It does not open a pull request, does not move the "
             "gitlink, and never adds a `tests/expectations/` entry for anything above — an "
             "entry would convert a live, owned gap into settled classification "
             "(`.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md`). To take the "
             "candidate, bump `tests/al-language` and "
             "`tests/expectations/count-baseline/test-count-baseline.json` in one PR.")
    L.append("")
    L.append("Generated by `tools/corpus-pin-advance.py` (issue #3319).")
    return "\n".join(L) + "\n"


# ---------------------------------------------------------------------------


def full_sha(corpus: str, rev: str) -> str:
    """Both endpoints canonicalised to a full 40-char SHA before anything compares them.

    `git rev-list` emits full SHAs, so an abbreviated `--pin`/`--tip` -- which is what a human
    types and what every hand-written pin record in this repository uses -- makes `target ==
    tip` false for a run where the pin CAN advance the whole way. That renders as "blocked"
    with no blocker, and dereferencing the absent blocker is a crash rather than a wrong
    number, which is the only reason it was caught. Canonicalising once, here, is what keeps
    every downstream comparison an identity comparison.
    """
    out = git(corpus, "rev-parse", f"{rev}^{{commit}}", check=False)
    if not re.fullmatch(r"[0-9a-f]{40}", out or ""):
        raise MeasurementError(
            f"{rev!r} does not resolve to a commit in {corpus}. A revision that cannot be "
            "resolved must not be measured against -- the gap would be computed from the "
            "wrong endpoint and reported as a fact.")
    return out


def measure(corpus: str, pin: str, tip: str, results: dict, issues: list[dict],
            bc_version: str, measured_at: str, run_url: str = "") -> dict:
    assert_corpus_usable(corpus)
    pin, tip = full_sha(corpus, pin), full_sha(corpus, tip)
    window = commits_between(corpus, pin, tip)
    fails = map_failures(corpus, failing_tests(results), pin, tip)
    blockers = {f["introduced_by"] for f in fails if f["introduced_by"]}

    # A compile/execute/suite failure is not attributable to a name, so it cannot
    # nominate a blocker -- but it must not be read as "everything passed"
    # either. The safe reading is that the tip is not takeable at all.
    unmappable = unmappable_failures(results)
    if unmappable and window:
        blockers.add(window[0])

    target, first_blocker = select_target(window, blockers)
    subjects = {sha: subject(corpus, sha) for sha in window}
    return {
        "pin": pin, "tip": tip,
        "pin_subject": subject(corpus, pin),
        "tip_subject": subject(corpus, tip),
        "window": window, "subjects": subjects,
        "failures": fails, "unmappable": unmappable,
        "target": target, "first_blocker": first_blocker,
        "first_blocker_subject": subjects.get(first_blocker or "", ""),
        "tracking": find_tracking_issues(fails, issues),
        "bc_version": bc_version, "measured_at": measured_at, "run_url": run_url,
    }


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--results", required=True,
                    help="the runner's --out JSON from a run at the corpus TIP")
    ap.add_argument("--corpus", default=SUBMODULE_PATH)
    ap.add_argument("--pin", required=True, help="the pin currently on main")
    ap.add_argument("--tip", default="",
                    help="corpus tip; resolved from the remote when omitted")
    ap.add_argument("--issues", default="",
                    help="JSON array of open issues [{number,title,body}] for linkage")
    ap.add_argument("--bc-version", default="unknown")
    ap.add_argument("--measured-at", default="")
    ap.add_argument("--run-url", default="")
    ap.add_argument("--format", choices=("markdown", "json"), default="markdown")
    a = ap.parse_args(argv)

    try:
        with open(a.results, encoding="utf-8") as fh:
            results = json.load(fh)
        issues = json.loads(open(a.issues, encoding="utf-8").read()) if a.issues else []
        tip = a.tip or resolve_tip(a.corpus)
        state = measure(a.corpus, a.pin, tip, results, issues,
                        a.bc_version, a.measured_at or "unknown", a.run_url)
    except MeasurementError as e:
        print(f"::error::corpus-pin-advance: {e}", file=sys.stderr)
        return 2
    except (OSError, json.JSONDecodeError) as e:
        print(f"::error::corpus-pin-advance: could not read inputs: {e}", file=sys.stderr)
        return 2

    if a.format == "json":
        print(json.dumps(state, indent=2))
    else:
        print(render(state), end="")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
