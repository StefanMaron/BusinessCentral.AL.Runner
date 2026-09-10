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
# The corpus runs 16 legs and requires 8; a red OnPrem leg leaves the PR
# mergeable with a non-passing check, which is `unstable`. Reading that as
# NOT-MERGEABLE would refuse a corpus PR the merge bar accepts.
check("open + unstable is MERGEABLE -- the failing check is not a required one",
      cps.classify(payload(mergeable_state="unstable"))[0] == "MERGEABLE",
      str(cps.classify(payload(mergeable_state="unstable"))))
check("...and says so, so nobody reads it as all sixteen legs green",
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

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
