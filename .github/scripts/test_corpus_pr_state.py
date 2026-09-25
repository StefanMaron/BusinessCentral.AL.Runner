#!/usr/bin/env python3
"""Unit tests for corpus_pr_state.py (#3674).

Every payload below is the shape `GET /repos/{o}/{r}/pulls/{n}` returns, and the
five named ones were MEASURED on 2026-09-10 against the corpus repository rather
than imagined -- #319 open and clean, #305 merged, and #85 / #137 / #153 closed
without merging. The mapping from `mergeable_state` to a verdict is the part of
this module that can be wrong in a way nothing else notices, so it is pinned
against real answers.

Run: python3 .github/scripts/test_corpus_pr_state.py
"""
from __future__ import annotations

import importlib.util
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

_spec = importlib.util.spec_from_file_location(
    "corpus_pr_state", os.path.join(HERE, "corpus_pr_state.py"))
cps = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = cps
_spec.loader.exec_module(cps)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def payload(**kw):
    base = {"state": "open", "merged": False, "draft": False,
            "mergeable": True, "mergeable_state": "clean", "head": "0" * 40}
    base.update(kw)
    return base


print("corpus_pr_state.py -- classifying one corpus pull request")

# --- the five measured payloads ---------------------------------------------
MEASURED = {
    319: payload(state="open", merged=False, mergeable=True,
                 mergeable_state="clean", head="321ac71a04c4797723dd7c90875a315b42f5b356"),
    305: payload(state="closed", merged=True, mergeable=None,
                 mergeable_state="unknown", head="45b2b2a38491109fb8495cb37e9caa4615fde57e"),
    85: payload(state="closed", merged=False, mergeable=True,
                mergeable_state="blocked", head="957bced842e753f7f40d184bbe3b8267acaa509f"),
    137: payload(state="closed", merged=False, mergeable=True,
                 mergeable_state="blocked", head="9638c7a4d7d85692c3ec83229812a0af5addfa96"),
    153: payload(state="closed", merged=False, mergeable=True,
                 mergeable_state="blocked", head="b31bd4f428b9f5b9b4d128bfdfe59c239278063b"),
}

check("an open corpus PR whose required legs are green is MERGEABLE (measured on #319)",
      cps.classify(MEASURED[319])[0] == "MERGEABLE", str(cps.classify(MEASURED[319])))
check("a merged corpus PR is MERGED even though its mergeable fields read unknown "
      "(measured on #305)",
      cps.classify(MEASURED[305])[0] == "MERGED", str(cps.classify(MEASURED[305])))
for _n in (85, 137, 153):
    check(f"corpus #{_n}, closed without merging, is CLOSED-UNMERGED -- not read off "
          "mergeable_state, which still says 'blocked'",
          cps.classify(MEASURED[_n])[0] == "CLOSED-UNMERGED", str(cps.classify(MEASURED[_n])))

# --- the rest of the mergeable_state space ----------------------------------
check("open + blocked is NOT-MERGEABLE (a required BC leg is red or has not reported)",
      cps.classify(payload(mergeable_state="blocked"))[0] == "NOT-MERGEABLE",
      str(cps.classify(payload(mergeable_state="blocked"))))
check("...and the reason names the required legs rather than saying 'blocked'",
      "leg" in cps.classify(payload(mergeable_state="blocked"))[1].lower(),
      str(cps.classify(payload(mergeable_state="blocked"))))
check("open + dirty is NOT-MERGEABLE, and says conflict",
      cps.classify(payload(mergeable=False, mergeable_state="dirty"))[0] == "NOT-MERGEABLE"
      and "conflict" in cps.classify(payload(mergeable=False,
                                             mergeable_state="dirty"))[1].lower(),
      str(cps.classify(payload(mergeable=False, mergeable_state="dirty"))))
check("mergeable=false alone is NOT-MERGEABLE even when mergeable_state looks fine",
      cps.classify(payload(mergeable=False, mergeable_state="clean"))[0] == "NOT-MERGEABLE",
      str(cps.classify(payload(mergeable=False, mergeable_state="clean"))))
check("open + behind is NOT-MERGEABLE",
      cps.classify(payload(mergeable_state="behind"))[0] == "NOT-MERGEABLE",
      str(cps.classify(payload(mergeable_state="behind"))))
check("a draft corpus PR is NOT-MERGEABLE",
      cps.classify(payload(draft=True, mergeable_state="draft"))[0] == "NOT-MERGEABLE",
      str(cps.classify(payload(draft=True, mergeable_state="draft"))))
check("open + has_hooks is MERGEABLE (clean, with a webhook configured)",
      cps.classify(payload(mergeable_state="has_hooks"))[0] == "MERGEABLE",
      str(cps.classify(payload(mergeable_state="has_hooks"))))
# The corpus requires only its cloud legs; a red OnPrem leg leaves the PR
# mergeable with a non-passing check, which is `unstable`. Reading that as
# NOT-MERGEABLE would refuse a corpus PR the merge bar accepts.
check("open + unstable is MERGEABLE -- the failing check is not a required one",
      cps.classify(payload(mergeable_state="unstable"))[0] == "MERGEABLE",
      str(cps.classify(payload(mergeable_state="unstable"))))
check("...and says so, so nobody reads it as every leg green",
      "required" in cps.classify(payload(mergeable_state="unstable"))[1].lower(),
      str(cps.classify(payload(mergeable_state="unstable"))))

# --- the third state (guards-need-a-third-state.md) --------------------------
check("mergeable=null is UNREADABLE, never NOT-MERGEABLE -- GitHub is still computing it",
      cps.classify(payload(mergeable=None, mergeable_state="unknown"))[0] == "UNREADABLE",
      str(cps.classify(payload(mergeable=None, mergeable_state="unknown"))))
check("a mergeable_state GitHub has not documented is UNREADABLE, not a guess",
      cps.classify(payload(mergeable_state="something_new"))[0] == "UNREADABLE",
      str(cps.classify(payload(mergeable_state="something_new"))))
check("a payload missing the fields is UNREADABLE",
      cps.classify({})[0] == "UNREADABLE", str(cps.classify({})))
check("a payload that is not a mapping at all is UNREADABLE",
      cps.classify(None)[0] == "UNREADABLE", str(cps.classify(None)))
check("an unexpected `state` is UNREADABLE",
      cps.classify(payload(state="weird"))[0] == "UNREADABLE",
      str(cps.classify(payload(state="weird"))))

# ===========================================================================
# Reading the declarations out of a body -- delegated, never re-parsed
# ===========================================================================
print("\ncorpus_pr_state.py -- which corpus PRs a body declares")

GOOD = "Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/226"
GOOD2 = "Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/227"

_nums, _why = cps.corpus_pr_numbers("Closes #1\n\n" + GOOD + "\n")
check("one well-formed Corpus-PR line yields its number", _nums == [226] and not _why,
      f"{_nums!r} {_why!r}")

# resolve_corpus_ref.sh refuses two, because a RUN measures one corpus. This
# module answers per corpus PR, so two is an ordinary answer here.
_nums, _why = cps.corpus_pr_numbers(GOOD + "\n" + GOOD2 + "\n")
check("two well-formed lines yield both numbers (unlike the resolver, which must pick one)",
      _nums == [226, 227] and not _why, f"{_nums!r} {_why!r}")

_nums, _why = cps.corpus_pr_numbers("Closes #1\n\nCorpus-NA: tooling only\n")
check("a body with no Corpus-PR line yields no numbers, and that is not an error",
      _nums == [] and not _why, f"{_nums!r} {_why!r}")

_nums, _why = cps.corpus_pr_numbers(
    "Corpus-PR: [#228](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/228)\n")
check("a MALFORMED Corpus-PR line refuses rather than reading as no declaration",
      _nums is None and _why, f"{_nums!r} {_why!r}")
check("...and the refusal quotes the line, so the author can see what was wrong",
      _nums is None and "228" in (_why or ""), f"{_why!r}")

# The regex lives in check_corpus_linkage.sh and nowhere else. Point this at a
# path that does not exist and it must refuse -- a module carrying its own copy
# would answer anyway, which is the silent second-source failure
# resolve_corpus_ref.sh's header describes.
_nums, _why = cps.corpus_pr_numbers(GOOD, script=os.path.join(HERE, "no_such_script.sh"))
check("with check_corpus_linkage.sh missing it refuses -- proof the regex is not duplicated here",
      _nums is None and "check_corpus_linkage.sh" in (_why or ""), f"{_nums!r} {_why!r}")

# --- the prefilter, which must change no answer ------------------------------
#
# check_corpus_linkage.sh spawns two greps PER LINE, so a real PR body is parsed
# in a minute on Windows and in milliseconds on the runner. Reducing the body to
# its marker lines is what makes ci-wait.py's line cheap -- and it is only safe
# because both extraction modes anchor on the marker at line start, so a line
# neither can match cannot change either answer.
FILLER = "\n".join(f"some prose line {i}" for i in range(30))

check("the prefilter keeps a marker line",
      cps.marker_lines_only(FILLER + "\n" + GOOD + "\n" + FILLER) == GOOD,
      repr(cps.marker_lines_only(FILLER + "\n" + GOOD)))
check("...case-insensitively",
      cps.marker_lines_only("corpus-pr: x") == "corpus-pr: x",
      repr(cps.marker_lines_only("corpus-pr: x")))
check("...and keeps a MALFORMED marker line, so the refusal below still fires",
      "228" in cps.marker_lines_only(
          FILLER + "\nCorpus-PR: [#228](https://x/pull/228)\n"),
      repr(cps.marker_lines_only(FILLER + "\nCorpus-PR: [#228](https://x/pull/228)")))
check("...and drops a body with nothing to parse",
      cps.marker_lines_only(FILLER + "\nCorpus-NA: tooling only\n") == "",
      repr(cps.marker_lines_only(FILLER)))

_padded, _why = cps.corpus_pr_numbers(FILLER + "\n" + GOOD + "\n" + FILLER + "\n")
check("a marker buried in 60 lines of prose reads exactly as the compact body does",
      _padded == [226] and not _why, f"{_padded!r} {_why!r}")

# A mid-sentence mention is one of the six near-misses the gate refuses. It
# survives the prefilter -- a superset -- and the script still does not count it,
# so the well-formed line beside it is the only declaration.
_mixed, _why = cps.corpus_pr_numbers(
    "The `Corpus-PR:` for this is #226, see below.\n" + GOOD + "\n")
check("a mid-sentence 'Corpus-PR:' mention beside a good line does not refuse the body",
      _mixed == [226] and not _why, f"{_mixed!r} {_why!r}")

_bad, _why = cps.corpus_pr_numbers(
    FILLER + "\nCorpus-PR: [#228](https://github.com/StefanMaron/"
    "BusinessCentral.AL.Language.Tests/pull/228)\n" + FILLER)
check("a malformed line buried in prose still refuses",
      _bad is None and _why, f"{_bad!r} {_why!r}")

# ===========================================================================
# The whole answer for a body, and the line it prints
# ===========================================================================
print("\ncorpus_pr_state.py -- the per-body answer")


def faker(by_number, calls=None):
    """A stand-in for the gh read: number -> payload, or None for a failed read."""
    def _fetch(number, **kw):
        if calls is not None:
            calls.append(number)
        got = by_number.get(number)
        if got is None:
            return None, f"could not read corpus pull request #{number}"
        return got, ""
    return _fetch


_entries, _why = cps.states_for_body(GOOD, fetch=faker({226: MEASURED[319]}))
check("a body declaring one corpus PR gets one entry", len(_entries) == 1 and not _why,
      f"{_entries!r} {_why!r}")
check("...carrying the state and the corpus PR's head",
      _entries[0].state == "MERGEABLE" and _entries[0].head.startswith("321ac71a"),
      repr(_entries[0]))
check("...and formats as one line naming the number, the state and eight sha characters",
      cps.format_line(_entries[0]).startswith("corpus PR #226: MERGEABLE (head 321ac71a)"),
      cps.format_line(_entries[0]))

_failed, _ = cps.states_for_body(GOOD, fetch=faker({}))
check("a failed read is UNREADABLE, never a state", _failed[0].state == "UNREADABLE",
      repr(_failed[0]))
check("...and its line says so without inventing a head",
      "UNREADABLE" in cps.format_line(_failed[0])
      and "head unknown" in cps.format_line(_failed[0]), cps.format_line(_failed[0]))

# ===========================================================================
# Exit codes
# ===========================================================================
print("\ncorpus_pr_state.py -- exit codes")


def run_main(body, by_number, unset_body=False):
    saved_body = os.environ.get("PR_BODY")
    if unset_body:
        os.environ.pop("PR_BODY", None)
    else:
        os.environ["PR_BODY"] = body
    buf = io.StringIO()
    out, err = sys.stdout, sys.stderr
    try:
        sys.stdout = buf
        sys.stderr = buf
        rc = cps.main(["corpus_pr_state.py"], fetch=faker(by_number))
    finally:
        sys.stdout, sys.stderr = out, err
        if saved_body is None:
            os.environ.pop("PR_BODY", None)
        else:
            os.environ["PR_BODY"] = saved_body
    return rc, buf.getvalue()


_rc, _out = run_main("Closes #1\n", {})
check("a body with no declaration passes", _rc == 0, f"rc={_rc} {_out!r}")
check("...and says NONE rather than printing nothing", "NONE" in _out, _out)

_rc, _out = run_main(GOOD, {226: MEASURED[305]})
check("a MERGED corpus PR passes", _rc == 0, f"rc={_rc} {_out!r}")

_rc, _out = run_main(GOOD, {226: MEASURED[319]})
check("an open corpus PR whose required legs are green passes -- it lands in the same "
      "coordinator step as this PR", _rc == 0, f"rc={_rc} {_out!r}")

_rc, _out = run_main(GOOD, {226: MEASURED[153]})
check("a corpus PR closed without merging FAILS", _rc == 1, f"rc={_rc} {_out!r}")
check("...and the message says what to do about it",
      "reopen" in _out.lower() or "open a new" in _out.lower(), _out)

_rc, _out = run_main(GOOD, {226: payload(mergeable_state="blocked")})
check("a corpus PR with a red or unreported required leg FAILS", _rc == 1, f"rc={_rc} {_out!r}")

_rc, _out = run_main(GOOD, {226: payload(mergeable=None, mergeable_state="unknown")})
check("an unreadable corpus PR is exit 3, never a pass", _rc == 3, f"rc={_rc} {_out!r}")

# A definite failure beside an unreadable one is still a definite failure: exit 1
# is a verdict, exit 3 is a refusal, and hiding the verdict behind the refusal
# would lose the actionable half.
_rc, _out = run_main(GOOD + "\n" + GOOD2,
                     {226: MEASURED[153], 227: payload(mergeable=None,
                                                       mergeable_state="unknown")})
check("a definite failure outranks an unreadable sibling", _rc == 1, f"rc={_rc} {_out!r}")
check("...and both lines are printed, so neither is lost",
      "#226" in _out and "#227" in _out, _out)

_rc, _out = run_main(
    "Corpus-PR: [#228](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/228)",
    {})
check("a malformed declaration is exit 3, not a pass", _rc == 3, f"rc={_rc} {_out!r}")

_rc, _out = run_main("", {}, unset_body=True)
check("an UNSET PR_BODY is a usage error (2), not a body that declares nothing",
      _rc == 2, f"rc={_rc} {_out!r}")

# ===========================================================================
# The gh call itself
# ===========================================================================
print("\ncorpus_pr_state.py -- the read")

_calls: list = []


def _runner(results):
    def _run(args):
        _calls.append(list(args))
        return results[min(len(_calls) - 1, len(results) - 1)]
    return _run


_calls.clear()
_got, _why = cps.fetch_pull(226, run=_runner([(0, '{"state":"open","merged":false,'
                                                 '"draft":false,"mergeable":true,'
                                                 '"mergeable_state":"clean","head":"abc"}')]),
                            sleep=lambda s: None)
check("the read asks the corpus repository, by pull number",
      _got is not None and any("BusinessCentral.AL.Language.Tests/pulls/226" in " ".join(a)
                               for a in _calls), f"{_got!r} {_calls!r}")

_calls.clear()
_got, _why = cps.fetch_pull(
    226,
    run=_runner([(0, '{"state":"open","merged":false,"draft":false,"mergeable":null,'
                     '"mergeable_state":"unknown","head":"abc"}'),
                 (0, '{"state":"open","merged":false,"draft":false,"mergeable":true,'
                     '"mergeable_state":"clean","head":"abc"}')]),
    sleep=lambda s: None)
check("a null `mergeable` is retried rather than reported -- GitHub computes it "
      "asynchronously", len(_calls) > 1 and cps.classify(_got)[0] == "MERGEABLE",
      f"{len(_calls)} call(s) {_got!r}")

_calls.clear()
_got, _why = cps.fetch_pull(226, run=_runner([(1, "gh: not found")]), sleep=lambda s: None)
check("a failed gh call returns no payload and a reason", _got is None and _why,
      f"{_got!r} {_why!r}")

_calls.clear()
_got, _why = cps.fetch_pull(226, run=_runner([(0, "not json at all")]), sleep=lambda s: None)
check("an unparseable answer is a failed read, not an empty payload",
      _got is None and _why, f"{_got!r} {_why!r}")

# ===========================================================================
# The corpus's required legs, read from its ruleset (#4593)
#
# On 2026-09-25 the owner added `BC 28.5 / test` to the corpus ruleset, and
# every place on this side that wrote "eight" went stale the same day. The set
# is now read where it lives. A read that fails is the third state, never an
# empty list -- an empty required set would make every leg "not required".
# ===========================================================================
print("\ncorpus_pr_state.py -- the corpus's required legs (#4593)")

# The shape `gh api repos/<corpus>/rules/branches/master --jq <filter>` printed
# on 2026-09-25, the day the ninth context was added.
RULESET_2026_09_25 = ('["BC 27.0 / test","BC 27.3 / test","BC 27.5 / test",'
                      '"BC 28.0 / test","BC 28.1 / test","BC 28.2 / test",'
                      '"BC 28.3 / test","BC 28.4 / test","BC 28.5 / test"]')

_calls.clear()
_req, _why = cps.required_contexts(run=_runner([(0, RULESET_2026_09_25)]))
check("the required contexts are read from the corpus ruleset, not a constant",
      _req is not None and len(_req) == 9 and "BC 28.5 / test" in _req, f"{_req!r} {_why!r}")
check("...by asking the corpus repository's ruleset for its master branch",
      any("BusinessCentral.AL.Language.Tests/rules/branches/master" in " ".join(a)
          for a in _calls), repr(_calls))

_calls.clear()
_req8, _ = cps.required_contexts(run=_runner([(0, RULESET_2026_09_25.replace(
    ',"BC 28.5 / test"', ''))]))
check("...and a different ruleset gives a different answer, so nothing is hardcoded",
      _req8 is not None and len(_req8) == 8 and "BC 28.5 / test" not in _req8, repr(_req8))

for _label, _res in (("a failed gh call", (1, "HTTP 403")),
                     ("an unparseable answer", (0, "<html>")),
                     ("a ruleset naming no required checks", (0, "[]")),
                     ("a non-list answer", (0, '{"context":"BC 27.0 / test"}'))):
    _calls.clear()
    _r, _w = cps.required_contexts(run=_runner([_res]))
    check(f"{_label} is the third state (None and a reason), never an empty set",
          _r is None and bool(_w), f"{_r!r} {_w!r}")

check("a leg the ruleset requires and the head never reported is named",
      cps.missing_legs(["BC 27.0 / test", "BC 28.5 / test"],
                       ["BC 27.0 / test", "BC OnPrem 27.0 / test"]) == ["BC 28.5 / test"])
check("...and nothing is missing when every required leg reported",
      cps.missing_legs(["BC 27.0 / test"], ["BC 27.0 / test", "prepare"]) == [])


def _legs(missing, why=""):
    def _f(head):
        return missing, why
    return _f


_e, _ = cps.states_for_body(GOOD, fetch=faker({226: payload(mergeable_state="blocked")}),
                            legs=_legs(["BC 28.5 / test"]))
check("a blocked corpus PR names the required leg that never reported on its head -- "
      "the #402 sequencing trap",
      _e[0].state == "NOT-MERGEABLE" and "BC 28.5 / test" in _e[0].detail, repr(_e[0]))
check("...and says the branch has to pick up master's ci.yml",
      "master" in _e[0].detail, repr(_e[0]))

_e, _ = cps.states_for_body(GOOD, fetch=faker({226: payload(mergeable_state="blocked")}),
                            legs=_legs(None, "HTTP 403"))
check("a leg read that fails leaves the verdict NOT-MERGEABLE (GitHub's own answer) "
      "and says the leg list could not be read",
      _e[0].state == "NOT-MERGEABLE" and "could not" in _e[0].detail, repr(_e[0]))

_e, _ = cps.states_for_body(GOOD, fetch=faker({226: MEASURED[319]}),
                            legs=_legs(["BC 28.5 / test"]))
check("a MERGEABLE corpus PR is not second-guessed by the leg read",
      _e[0].state == "MERGEABLE" and "28.5" not in _e[0].detail, repr(_e[0]))

check("no leg count survives in the module's prose or messages",
      not any(w in open(os.path.join(HERE, "corpus_pr_state.py")).read().lower()
              for w in ("eight cloud", "eight onprem", "eight required", "sixteen legs")))

# ===========================================================================
# The stale check run (#4206)
#
# `A cited corpus PR must be able to merge` is a status check, so it evaluates
# on push/edited/labeled and NOT when the cited corpus PR changes state. Under
# corpus-first merging the corpus PR merges AFTER the runner PR's last push, so
# the stored conclusion stays `failure` for a condition that has just become
# true -- and a failing non-required check makes mergeStateStatus UNSTABLE,
# which `enablePullRequestAutoMerge` refuses.
#
# Measured on PR #4203 (the issue's third instance, both timestamps read from
# the API): the gate ran at 20:04:40Z and concluded `failure`; corpus PR #371
# merged at 20:21:08Z; a re-fire at 20:50:08Z concluded `success` and the PR
# merged at 20:51:49Z. 46 minutes in which every instrument said green.
# ===========================================================================
print("\ncorpus_pr_state.py -- is the stored check run stale? (#4206)")

check("the gate's context name is exported, so callers do not spell it themselves",
      getattr(cps, "GATE_CONTEXT", None) == "A cited corpus PR must be able to merge",
      repr(getattr(cps, "GATE_CONTEXT", None)))

_MERGED = cps.Entry(371, "MERGED", "a" * 40, "a real BC service tier adjudicated this claim")
_MERGEABLE = cps.Entry(371, "MERGEABLE", "a" * 40, "open, no conflict")
_BAD = cps.Entry(371, "NOT-MERGEABLE", "a" * 40, "a required BC leg is red")
_UNREADABLE = cps.Entry(371, "UNREADABLE", "", "GitHub would not answer")


def _stale(entries, conclusion):
    return cps.stale_gate_verdict(entries, conclusion)


# --- the case the issue is about -------------------------------------------
_verdict, _why = _stale([_MERGED], "failure")
check("a MERGED corpus PR under a stored `failure` is STALE -- the exact shape "
      "that refused #4135, #4202 and #4203",
      _verdict == "STALE", f"{_verdict!r} {_why!r}")
check("the stale line names the re-trigger, because that is the whole remedy",
      _verdict == "STALE" and ("edit" in _why.lower() or "label" in _why.lower()),
      repr(_why))

_verdict, _why = _stale([_MERGEABLE], "failure")
check("a MERGEABLE corpus PR under a stored `failure` is STALE too -- the corpus "
      "PR went green after the gate ran", _verdict == "STALE", f"{_verdict!r} {_why!r}")

# --- the cases that must NOT be called stale --------------------------------
_verdict, _why = _stale([_BAD], "failure")
check("a corpus PR that genuinely cannot merge is CURRENT, not stale -- the red "
      "tick is telling the truth", _verdict == "CURRENT", f"{_verdict!r} {_why!r}")

_verdict, _why = _stale([_MERGED], "success")
check("a MERGED corpus PR under a stored `success` is CURRENT -- nothing to do",
      _verdict == "CURRENT", f"{_verdict!r} {_why!r}")

_verdict, _why = _stale([], "success")
check("a body citing no corpus PR is CURRENT, never stale",
      _verdict == "CURRENT", f"{_verdict!r} {_why!r}")

# --- the third state (guards-need-a-third-state.md) -------------------------
_verdict, _why = _stale([_UNREADABLE], "failure")
check("an UNREADABLE corpus PR is UNKNOWN, never STALE -- claiming a red tick is "
      "stale on a corpus PR nobody could read would clear a gate on no measurement",
      _verdict == "UNKNOWN", f"{_verdict!r} {_why!r}")

_verdict, _why = _stale([_MERGED], None)
check("no stored conclusion at all is UNKNOWN, never CURRENT -- the check may "
      "simply not have reported yet", _verdict == "UNKNOWN", f"{_verdict!r} {_why!r}")

_verdict, _why = _stale([_MERGED], "")
check("an empty stored conclusion is UNKNOWN, not a conclusion",
      _verdict == "UNKNOWN", f"{_verdict!r} {_why!r}")

# A mixed body: one corpus PR merged, one genuinely red. The red one is the
# reason the gate fails, so the tick is CURRENT and re-firing would change
# nothing. Getting this backwards would send a coordinator round a re-trigger
# loop that can never clear.
_verdict, _why = _stale([_MERGED, _BAD], "failure")
check("one merged and one genuinely-red corpus PR is CURRENT -- the red one is "
      "why the gate fails, and a re-fire cannot clear it",
      _verdict == "CURRENT", f"{_verdict!r} {_why!r}")

# UNKNOWN outranks STALE: an unreadable entry beside a merged one means the
# gate's verdict cannot be attributed, so neither answer is established.
_verdict, _why = _stale([_MERGED, _UNREADABLE], "failure")
check("an unreadable entry beside a merged one is UNKNOWN, not STALE",
      _verdict == "UNKNOWN", f"{_verdict!r} {_why!r}")

# --- the reported line ------------------------------------------------------
_line = cps.format_stale_line("STALE", "corpus PR #371 has merged since this check ran")
check("the STALE line is prefixed so a reader can see it is about the tick, not "
      "the corpus PR", "stale" in _line.lower() and "#371" in _line, repr(_line))
check("a CURRENT verdict prints nothing -- a report that names every PR is ignored",
      cps.format_stale_line("CURRENT", "") == "", repr(cps.format_stale_line("CURRENT", "")))
check("an UNKNOWN verdict DOES print, because nobody measured it",
      cps.format_stale_line("UNKNOWN", "GitHub would not answer") != "",
      repr(cps.format_stale_line("UNKNOWN", "GitHub would not answer")))

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
