#!/usr/bin/env python3
"""Unit tests for tools/pr-attribution.py's classification and exit code.

The defect (#3942): PR #3927 was green on every required check, `mergeable:
MERGEABLE`, `git merge-tree` CLEAN -- and `gh pr merge` refused it, because the
`main` ruleset sets `require_extra_approval_for_unattributed_changes` and one of
its commits was authored by another real GitHub account. It is not a check, so
no check-reading tool can see it, and a coordinator learned at merge time.

So the property under test is **which author lists mean an approval will be
required**, and the value of the tool is entirely in getting the NEGATIVE case
right. The issue's own first draft keyed on "any login other than the pushing
identity" and would have fired on every pull request this loop writes -- its
commits carry `{login:"", name:"Test"}` and `{login:"claude"}`, and #3943 was
CLEAN carrying both. A check that fires on everything is worse than no check:
"expect a block" becomes the default reading, and the one PR that genuinely
needs an approval is indistinguishable from the noise.

Three things a plausible-looking rewrite gets wrong, one test group each:

  * the loop's own two logins must NOT be flagged (`classify(): the negative
    case`), or the tool is noise;
  * a real foreign account MUST be flagged, and the report must name only it
    (`classify(): the positive case`);
  * an author list carrying **no `login` field at all** is the third state, not
    a pass. The GitHub MCP `pull_request_read` / REST `/pulls/<N>/commits`
    shape reports `commit.author = {name, email, date}` and no login, so a
    fetcher pointed at it cannot tell a real account from an unresolvable one.
    Reporting that as "safe to arm" is exactly what
    `guards-need-a-third-state.md` exists to prevent.

`classify()` is a pure function over the author list, deliberately separate
from whatever fetched it: the fetch needs a live `gh`, the logic does not, and
the logic is the part that carries the defect.

Run: python3 tools/test_pr_attribution.py
"""
from __future__ import annotations

import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "pr_attribution", os.path.join(HERE, "pr-attribution.py"))
pa = importlib.util.module_from_spec(_spec)
sys.modules["pr_attribution"] = pa
_spec.loader.exec_module(pa)

FAILURES: list[str] = []
PASSED = 0


def check(name: str, cond: bool, detail: str = "") -> None:
    global PASSED
    if cond:
        PASSED += 1
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


# The two measured author lists, from #3942's own correction comment. One
# BLOCKED pull request and one CLEAN one differing in exactly one entry is what
# located the discriminator; a single positive instance had produced the wrong
# one.
LOOP_AUTHORS = [
    {"login": "", "name": "Test"},
    {"login": "claude", "name": "Claude Opus 5 (1M context)"},
]
FOREIGN = {"login": "SShadowS", "name": "Torben Leth"}
PR3927_AUTHORS = LOOP_AUTHORS + [FOREIGN]


# --------------------------------------------------------------------------
# classify(): the negative case -- #3943, CLEAN, merged normally
#
# Every check here fails on the issue's own first-draft rule, which is the
# point: this group is what stops the tool being switched off within a day.
# --------------------------------------------------------------------------
print("classify(): the negative case (this loop's own commits)")

_r = pa.classify(LOOP_AUTHORS, viewer="StefanMaron")
check("#3943's exact author list is OK -- it was CLEAN and merged",
      _r.verdict == pa.OK, f"{_r.verdict}: {_r.reason}")
check("...and nothing is flagged on it", _r.flagged == [], repr(_r.flagged))

check("an empty login alone is OK",
      pa.classify([{"login": "", "name": "Test"}]).verdict == pa.OK)
check("a null login (the REST shape's unresolvable author) is OK, same as empty",
      pa.classify([{"login": None, "name": "Test"}]).verdict == pa.OK)
check("'claude' alone is OK",
      pa.classify([{"login": "claude", "name": "Claude"}]).verdict == pa.OK)
check("'Claude' is OK too -- GitHub logins compare case-insensitively",
      pa.classify([{"login": "Claude", "name": "Claude"}]).verdict == pa.OK)
check("surrounding whitespace on a benign login does not make it foreign",
      pa.classify([{"login": " claude ", "name": "Claude"}]).verdict == pa.OK)

check("the authenticated account's own commit is OK -- a change attributed to "
      "the pusher is not unattributed by definition",
      pa.classify([{"login": "StefanMaron", "name": "Stefan Maron"}],
                  viewer="StefanMaron").verdict == pa.OK)
check("...and the viewer comparison is case-insensitive",
      pa.classify([{"login": "stefanmaron", "name": "Stefan Maron"}],
                  viewer="StefanMaron").verdict == pa.OK)


# --------------------------------------------------------------------------
# classify(): the positive case -- #3927, BLOCKED
# --------------------------------------------------------------------------
print("classify(): the positive case (a foreign real account)")

_r = pa.classify(PR3927_AUTHORS, viewer="StefanMaron")
check("#3927's exact author list requires an approval",
      _r.verdict == pa.APPROVAL_REQUIRED, f"{_r.verdict}: {_r.reason}")
check("...and ONLY the foreign author is flagged, not the two benign ones",
      [f["login"] for f in _r.flagged] == ["SShadowS"], repr(_r.flagged))
check("...and the flagged entry carries the human name, which is what makes "
      "the report legible",
      _r.flagged and _r.flagged[0].get("name") == "Torben Leth",
      repr(_r.flagged))

check("a second agent-running account is NOT benign (#3275: FBakkensen runs an "
      "fbk-* pool, and its commits are still another account to this ruleset)",
      pa.classify([{"login": "FBakkensen", "name": "Flemming Bakkensen"}],
                  viewer="StefanMaron").verdict == pa.APPROVAL_REQUIRED)

check("with no viewer read, the authenticated account is reported too -- "
      "over-reporting is the safe direction and the report says why",
      pa.classify([{"login": "StefanMaron", "name": "Stefan Maron"}],
                  viewer=None).verdict == pa.APPROVAL_REQUIRED)

check("the remedy names a human approval",
      "approv" in pa.classify(PR3927_AUTHORS).reason.lower())
check("the remedy never proposes self-approving",
      "self-approv" not in pa.classify(PR3927_AUTHORS).reason.lower()
      or "never self-approv" in pa.classify(PR3927_AUTHORS).reason.lower())


# --------------------------------------------------------------------------
# classify(): the third state -- could not tell
#
# `guards-need-a-third-state.md`: an unmeasurable answer must not be spelled as
# the success answer. Each of these would otherwise read as "safe to arm".
# --------------------------------------------------------------------------
print("classify(): the third state")

check("a failed fetch (None) is UNREADABLE, never OK",
      pa.classify(None).verdict == pa.UNREADABLE)
check("an empty author list is UNREADABLE -- every pull request has at least "
      "one commit, so zero means the shape was not understood",
      pa.classify([]).verdict == pa.UNREADABLE)

# The shape trap the brief names: `gh pr view --json commits` reports
# `authors[].login`; the REST/MCP commit listing reports
# `commit.author = {name,email,date}` and no login at all. A classifier handed
# the second cannot distinguish Torben Leth from an unresolvable author -- and
# `.get("login", "")` would silently call every one of them benign.
_mcp_shape = [{"name": "Torben Leth", "email": "t@example.com",
               "date": "2026-09-11T20:00:00Z"}]
_r = pa.classify(_mcp_shape, viewer="StefanMaron")
check("an author entry with NO login key is UNREADABLE, not OK -- this is the "
      "REST/MCP shape, and treating a missing key as an empty login calls a "
      "real account benign",
      _r.verdict == pa.UNREADABLE, f"{_r.verdict}: {_r.reason}")
check("...and the refusal names the remedy (which read reports logins)",
      "--json commits" in _r.reason, _r.reason)
check("...and the refusal is distinguishable from the OK a null login gives, "
      "on lists that differ only in whether the key is present",
      pa.classify([{"name": "x"}]).verdict
      != pa.classify([{"login": None, "name": "x"}]).verdict)

check("a non-dict entry is UNREADABLE, never a crash",
      pa.classify(["SShadowS"]).verdict == pa.UNREADABLE)

check("a definite finding outranks an unreadable sibling -- a foreign login "
      "that WAS read stays a finding, and the answer is still not OK",
      pa.classify([FOREIGN, {"name": "no login key"}]).verdict
      == pa.APPROVAL_REQUIRED)


# --------------------------------------------------------------------------
# run(): the exit code over several pull requests
# --------------------------------------------------------------------------
print("run():")

def _fake(mapping):
    return lambda n: mapping[n]

_rc, _rows = pa.run([1, 2], fetch=_fake({1: LOOP_AUTHORS, 2: LOOP_AUTHORS}),
                    viewer="StefanMaron")
check("every PR clean is exit 0", _rc == 0, f"rc={_rc}")
check("...and a clean sweep reports no rows", _rows == [], repr(_rows))

_rc, _rows = pa.run([1, 2], fetch=_fake({1: LOOP_AUTHORS, 2: PR3927_AUTHORS}),
                    viewer="StefanMaron")
check("one PR needing an approval is exit 1", _rc == 1, f"rc={_rc}")
check("...and only that PR is reported",
      [r.number for r in _rows] == [2], repr(_rows))

_rc, _rows = pa.run([1, 2], fetch=_fake({1: LOOP_AUTHORS, 2: None}),
                    viewer="StefanMaron")
check("an unreadable PR with nothing flagged is exit 3, never 0",
      _rc == 3, f"rc={_rc}")

_rc, _rows = pa.run([1, 2], fetch=_fake({1: PR3927_AUTHORS, 2: None}),
                    viewer="StefanMaron")
check("a finding outranks an unreadable one at run level too", _rc == 1,
      f"rc={_rc}")

_rc, _rows = pa.run([], fetch=_fake({}), viewer="StefanMaron")
check("an empty sweep is exit 0 with no rows", _rc == 0 and _rows == [])

# A fetch that raises must not abort the sweep: one broken PR hiding every
# other PR's answer behind it is the same defect one level up.
_rc, _rows = pa.run([1, 2], fetch=lambda n: (_ for _ in ()).throw(RuntimeError("boom"))
                    if n == 1 else LOOP_AUTHORS, viewer="StefanMaron")
check("a fetch that raises is that PR's third state, and the sweep continues",
      _rc == 3 and [r.number for r in _rows] == [1], f"rc={_rc} {_rows}")


# --------------------------------------------------------------------------
# report(): quiet when there is nothing to say
# --------------------------------------------------------------------------
print("report():")

check("an empty sweep prints nothing at all -- a tool run every sweep that "
      "always prints becomes noise", pa.report([]) == "")

_rc, _rows = pa.run([3927], fetch=_fake({3927: PR3927_AUTHORS}),
                    viewer="StefanMaron")
_text = pa.report(_rows)
check("the report names the pull request", "#3927" in _text, _text)
check("the report names the flagged login", "SShadowS" in _text, _text)
check("the report names the flagged human name", "Torben Leth" in _text, _text)
check("the report does not name the benign authors",
      "claude" not in _text.lower(), _text)


# --------------------------------------------------------------------------
# fetch_authors(): the one thing a unit test can pin about the live read
#
# This session has no `gh`, so an end-to-end read is not available. What IS
# available, and is the half that carries the defect, is WHICH read it makes:
# a fetcher pointed at `/pulls/<N>/commits` gets a shape with no `login` field
# and every real account in it then looks benign. A fake `gh` on PATH records
# the argv and answers with the real `gh pr view --json commits` shape.
# --------------------------------------------------------------------------
print("fetch_authors():")

import json as _json
import stat as _stat
import subprocess as _sub
import tempfile as _tmp


def _with_fake_gh(body: str, rc: int = 0):
    """Run fetch_authors(3927) with a `gh` that prints `body` and exits `rc`."""
    d = _tmp.mkdtemp(prefix="pr-attribution-test-")
    argv_log = os.path.join(d, "argv.txt")
    script = os.path.join(d, "gh")
    with open(script, "w", encoding="utf-8") as fh:
        fh.write("#!/usr/bin/env python3\n"
                 "import sys\n"
                 f"open({argv_log!r}, 'w').write(' '.join(sys.argv[1:]))\n"
                 f"sys.stdout.write({body!r})\n"
                 f"sys.exit({rc})\n")
    os.chmod(script, os.stat(script).st_mode | _stat.S_IEXEC | _stat.S_IXGRP
             | _stat.S_IXOTH)
    old_path = os.environ.get("PATH", "")
    os.environ["PATH"] = d + os.pathsep + old_path
    try:
        got = pa.fetch_authors(3927)
    finally:
        os.environ["PATH"] = old_path
    argv = open(argv_log, encoding="utf-8").read() if os.path.exists(argv_log) else ""
    return got, argv


_got, _argv = _with_fake_gh(_json.dumps(PR3927_AUTHORS))
check("fetch_authors parses the gh pr view shape into {login, name} entries",
      _got == PR3927_AUTHORS, repr(_got))
check("...and it asks for `--json commits`, the ONLY read that reports a login "
      "(the REST /pulls/<N>/commits shape does not)",
      "--json commits" in _argv, _argv)
check("...and it projects authors[].login, not commit.author",
      ".commits[].authors[]" in _argv and "login" in _argv, _argv)
check("...and the fetched list classifies end to end",
      pa.classify(_got, viewer="StefanMaron").verdict == pa.APPROVAL_REQUIRED)

_got, _ = _with_fake_gh("", rc=1)
check("a gh that exits non-zero yields None -- the third state, never []",
      _got is None, repr(_got))

_got, _ = _with_fake_gh("not json at all")
check("an unparseable body yields None, never a crash", _got is None, repr(_got))

# CLAUDE.md: mise prints a banner on stdout, so a capture gets it alongside the
# value. A fetcher that fails on it reports UNREADABLE for every pull request on
# a mise box -- honest, and useless.
_got, _ = _with_fake_gh("mise ~/.config/mise/config.toml tools: gh@2.100.0\n"
                        + _json.dumps(LOOP_AUTHORS))
check("a mise banner on stdout does not break the parse",
      _got == LOOP_AUTHORS, repr(_got))

_got, _ = _with_fake_gh(_json.dumps({"login": "x"}))
check("a JSON object where a list was expected yields None", _got is None,
      repr(_got))


print()
_total = PASSED + len(FAILURES)
print(f"Failed: {len(FAILURES)}, Passed: {PASSED}, Total: {_total}")
if FAILURES:
    for f in FAILURES:
        print(f"  - {f}")
    sys.exit(1)
print("all pr-attribution tests passed")
