#!/usr/bin/env python3
"""Unit tests for tools/pr-body.py.

Every guard in pr-body.py exists because an unguarded PR-body edit destroyed
PR #2790's body (see that file's header), so every guard here gets a test that
FAILS IF THE GUARD IS REMOVED. A guard whose test still passes without it is the
same "check that cannot fail" the tool was written to replace.

No test touches the network. The payloads are the shape
`gh pr view <N> --json body` returns, and the PR #2790 excerpt below was captured
from the live PR on 2026-09-05 (post-damage, reconstructed body).

Run: python3 tools/test_pr_body.py
"""
from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("pr_body", os.path.join(HERE, "pr-body.py"))
pb = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(pb)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


# --------------------------------------------------------------------------
# Captured payloads
# --------------------------------------------------------------------------

# Excerpt of PR #2790's body, captured 2026-09-05. Real text, including the two
# INLINE references to #2783 the reconstruction left in it -- which is itself a
# case worth asserting on, below.
PR2790_BODY = """\
> **Note on this body.** The original text was destroyed by the autonomous cycle agent
> (`stma-auto-1`) shortly before merge: a scripted edit fetched the body, the fetch returned empty
> during a network failure, and the script uploaded its addendum over the top. That also removed the
> `Closes #2783` line, which is why the issue had to be closed by hand.

## What this fixes

`RecordRef.Open` was not scope-checked against the app's compilation target at runtime, so a
`"target": "Cloud"` bundle could open OnPrem and internal system tables that a real BC service tier
refuses.

## Measured

corpus 2500/2500 exit 0 (`pass-oos: 2`, `pass-known-gap: 12`, `pass-divergence: 1`) ·
`tests/runner-extras` 256/256 exit 0 · targeted units 12/12. Zero delta on both suites.

Closes #2783 — *(recorded for the record; the issue was closed manually, since this line was absent
from the body at merge time)*
"""

# The same PR as it stood BEFORE the damage: a canonical trailer line, no inline
# references. This is the baseline every "an edit must not lose this" test uses.
GOOD_BODY = """\
## What this fixes

`RecordRef.Open` was not scope-checked against the app's compilation target at runtime, so a
`"target": "Cloud"` bundle could open OnPrem and internal system tables that a real BC service tier
refuses.

Two independent things had disabled BC's own gate, and fixing either one alone leaves it dead.

## Measured

corpus 2500/2500 exit 0 · `tests/runner-extras` 256/256 exit 0 · targeted units 12/12.
**`tests/expectations/count-baseline/test-count-baseline.json` is untouched.**

Closes #2783
"""

# What the broken script actually uploaded over it: its addendum, applied to "".
DESTROYED_BODY = """\
> **Note.** Reviewed after merge: the fix is correct, and the follow-up is filed.
"""


def envelope(body: str) -> tuple[int, str]:
    """What `gh pr view N --json body` returns on success."""
    return 0, json.dumps({"body": body})


class Reader:
    """A fake `gh pr view --json body`, driven by a scripted list of responses."""

    def __init__(self, *responses: tuple[int, str]):
        self.responses = list(responses)
        self.calls = 0

    def __call__(self) -> tuple[int, str]:
        self.calls += 1
        return self.responses[min(self.calls - 1, len(self.responses) - 1)]


def fetch(*responses, min_bytes=200, double_read=True):
    r = Reader(*responses)
    return pb.fetch_body(r, min_bytes, double_read=double_read, attempts=2,
                         sleep=lambda s: None), r


def fetch_err(*responses, min_bytes=200, double_read=True) -> str | None:
    """The FetchError message, or None if the fetch was (wrongly) accepted."""
    try:
        fetch(*responses, min_bytes=min_bytes, double_read=double_read)
        return None
    except pb.FetchError as e:
        return str(e)


# --------------------------------------------------------------------------
print("fetch guards -- the ones #2790 needed")
# --------------------------------------------------------------------------

# THE incident. `--jq .body` collapses "the call failed" and "the body is empty"
# into the same empty stdout; reading the JSON envelope keeps them distinct.
err = None
try:
    pb.parse_body_json(0, "")
except pb.FetchError as e:
    err = str(e)
check("an empty stdout from a rc=0 gh call is a FETCH FAILURE, not an empty body",
      err is not None and "not JSON" in err, str(err))

err = fetch_err(envelope(""))
check("a genuinely empty body is refused as a baseline", err is not None, str(err))
check("...and the message says EMPTY, not something vaguer",
      err is not None and "EMPTY" in err, str(err))

err = fetch_err((0, json.dumps({"body": None})))
check("a null body is refused too", err is not None, str(err))

# --min-bytes 0 disables the length floor, so ONLY the emptiness guard can refuse
# these. Without that isolation the empty-body test would still pass with the
# emptiness guard deleted, because the length floor would catch it -- and a test
# that passes for the wrong reason is how a guard quietly stops existing.
err = fetch_err(envelope(""), min_bytes=0)
check("an empty body is refused even with the length floor turned off",
      err is not None and "EMPTY" in err, str(err))
err = fetch_err(envelope("   \n  \n"), min_bytes=0)
check("a whitespace-only body is refused the same way",
      err is not None and "EMPTY" in err, str(err))

err = fetch_err((1, "Post \"https://api.github.com/graphql\": dial tcp 140.82.121.6:443: i/o timeout"))
check("a network failure is refused, never read as an empty body",
      err is not None and "gh exited" in err, str(err))

err = fetch_err((0, '{"data": {}}'))
check("a response with no 'body' key is refused",
      err is not None and "no 'body' key" in err, str(err))

err = fetch_err(envelope("Closes #2783\n"), min_bytes=200)
check("a body under --min-bytes is refused as implausibly short", err is not None, str(err))
check("...and the message names the flag that would allow it",
      err is not None and "--min-bytes" in err, str(err))

body, r = fetch(envelope(GOOD_BODY))
check("a plausible body is accepted", body.startswith("## What this fixes"), body[:40])
check("...after TWO reads, so a truncated response cannot become the baseline",
      r.calls == 2, f"calls={r.calls}")

# Every attempt reads a full body then a truncated one, so no attempt ever gets
# two matching reads -- a genuine "the response is being truncated" situation
# rather than a single blip (which SHOULD be retried, and is, below).
class Alternating:
    def __init__(self, a, b):
        self.a, self.b, self.calls = a, b, 0

    def __call__(self):
        self.calls += 1
        return self.a if self.calls % 2 else self.b


err = None
try:
    pb.fetch_body(Alternating(envelope(GOOD_BODY), envelope(GOOD_BODY[:400])), 200,
                  attempts=2, sleep=lambda s: None)
except pb.FetchError as e:
    err = str(e)
check("two disagreeing reads are refused", err is not None and "disagree" in err, str(err))

body, r = fetch(envelope(GOOD_BODY), double_read=False)
check("--single-read does exactly one read", r.calls == 1, f"calls={r.calls}")

# GitHub stores bodies with CRLF and strips the trailing newline. Without
# normalisation every anchor spanning a line break misses and every verification
# fails.
body, _ = fetch(envelope(GOOD_BODY.replace("\n", "\r\n")))
check("CRLF from the API is normalised to LF", "\r" not in body, repr(body[:60]))
check("...and the normalised read equals the LF form", body == pb.norm(GOOD_BODY), "")

# A transient failure followed by a good read must still succeed -- the network
# here times out often, and a tool that gives up on the first blip is a tool
# nobody uses.
r = Reader((1, "dial tcp: i/o timeout"), envelope(GOOD_BODY))
body = pb.fetch_body(r, 200, attempts=3, sleep=lambda s: None)
check("a transient first failure is retried, not fatal", body.startswith("## What"), body[:40])


# --------------------------------------------------------------------------
print("\nanchors -- a miss is an error, never a silent no-op")
# --------------------------------------------------------------------------

def edit_err(body, edits) -> str | None:
    try:
        pb.apply_edits(body, edits)
        return None
    except pb.PreconditionFailed as e:
        return str(e)


err = edit_err(GOOD_BODY, [pb.Edit("this text is not in the body", "x")])
check("an anchor that is not found FAILS", err is not None, str(err))
check("...and the message says it was found 0 times",
      err is not None and "found 0 time" in err, str(err))

new, results = pb.apply_edits(GOOD_BODY, [pb.Edit("is untouched", "is updated in this PR")])
check("a found anchor is actually replaced", "is updated in this PR" in new, "")
check("...and the original text is gone", "is untouched" not in new, "")
check("...and the result is reported as ok", results[0].ok, results[0].line())

err = edit_err(GOOD_BODY, [pb.Edit("exit 0", "exit zero")])   # occurs twice
check("an anchor found twice when once was expected FAILS", err is not None, str(err))
check("...and the message says how many times it was found",
      err is not None and "found 2 time" in err, str(err))
check("...and points at --replace-count",
      err is not None and "--replace-count" in err, str(err))

new, _ = pb.apply_edits(GOOD_BODY, [pb.Edit("exit 0", "exit zero", count=2)])
check("--replace-count 2 replaces both occurrences", new.count("exit zero") == 2,
      str(new.count("exit zero")))

err = edit_err(GOOD_BODY, [pb.Edit("exit 0", "exit zero", count=3)])
check("--replace-count 3 against 2 occurrences FAILS", err is not None, str(err))

# An anchor spanning a line break must work against a CRLF body, or the tool is
# unusable on exactly the multi-line claims it is meant to correct.
crlf = pb.norm(GOOD_BODY.replace("\n", "\r\n"))
new, _ = pb.apply_edits(crlf, [pb.Edit("## Measured\n\ncorpus", "## Measured\n\nCORPUS")])
check("a multi-line anchor matches a body that arrived as CRLF", "CORPUS" in new, "")


# --------------------------------------------------------------------------
print("\nclosing references -- the damage #2790 actually did")
# --------------------------------------------------------------------------

check("a standalone trailer is a declared target",
      pb.declared_targets(GOOD_BODY) == [2783], str(pb.declared_targets(GOOD_BODY)))
check("owner/repo#N on its own line is a declared target too",
      pb.declared_targets("body\n\nFixes StefanMaron/BusinessCentral.AL.Runner#42\n") == [42], "")
check("a full issue URL on its own line is a declared target",
      pb.declared_targets("Resolves https://github.com/o/r/issues/99\n") == [99], "")
check("an inline reference is NOT a declaration",
      pb.declared_targets("this closes #55 eventually\n") == [], "")
check("a bare number is not a reference at all (no false positive on prose)",
      pb.stray_closing_reference("this fixes 3 bugs in the parser", []) is None, "")

# The real captured body: no canonical trailer, two inline mentions of #2783.
check("the real post-damage #2790 body declares nothing canonically",
      pb.declared_targets(PR2790_BODY) == [], str(pb.declared_targets(PR2790_BODY)))
stray = pb.stray_closing_reference(PR2790_BODY, [])
check("...and its inline mentions are flagged as strays",
      stray is not None and stray[0] == 2783, str(stray))


def cb(orig, new, **kw):
    """check_body, returning the failure message or None."""
    opts = dict(require_closes=[], must_contain=[], must_not_contain=[],
                max_shrink_bytes=200, max_shrink_frac=0.10,
                force_shrink=False, allow_drop_closes=False)
    opts.update(kw)
    try:
        pb.check_body(orig, new, **opts)
        return None
    except pb.PreconditionFailed as e:
        return str(e)


# Isolated from the shrink guard on purpose: the replacement is the SAME LENGTH,
# so the only thing that can fail is the closing-reference guard.
dropped = GOOD_BODY.replace("Closes #2783", "See at #2783")   # same length, no keyword
check("the two bodies are the same length, so only one guard can fire",
      len(dropped) == len(GOOD_BODY), f"{len(dropped)} vs {len(GOOD_BODY)}")
err = cb(GOOD_BODY, dropped)
check("an edit that drops a declared closing reference FAILS", err is not None, str(err))
check("...and the message names the issue that would stop auto-closing",
      err is not None and "#2783" in err, str(err))
check("...and names the flag that would permit it",
      err is not None and "--allow-drop-closes" in err, str(err))
check("--allow-drop-closes permits it", cb(GOOD_BODY, dropped, allow_drop_closes=True) is None,
      str(cb(GOOD_BODY, dropped, allow_drop_closes=True)))

err = cb(GOOD_BODY, GOOD_BODY, require_closes=[9999])
check("--closes N fails when N is not declared", err is not None, str(err))
check("--closes N passes when it is", cb(GOOD_BODY, GOOD_BODY, require_closes=[2783]) is None, "")

# The other direction: introducing a keyword next to an issue we do not mean to
# close. Same length again, so the shrink guard cannot be what fires.
stray_body = GOOD_BODY.replace("Two independent things had disabled BC's own gate,",
                               "This does not close #2125 and it also does not")
err = cb(GOOD_BODY, stray_body)
check("introducing a closing keyword next to another issue FAILS", err is not None, str(err))
check("...and names that issue", err is not None and "#2125" in err, str(err))
check("...and says the parser ignores negation",
      err is not None and "negation" in err, str(err))

restated = GOOD_BODY.replace("Two independent things had disabled BC's own gate,",
                             "It closes #2783, and the gate had been disabled,")
check("restating an ALREADY-DECLARED target inline is not a stray",
      cb(GOOD_BODY, restated) is None, str(cb(GOOD_BODY, restated)))


# --------------------------------------------------------------------------
print("\nshrink, and the #2790 scenario end to end")
# --------------------------------------------------------------------------

# Keep the trailer so the closing-reference guard cannot be what fires.
half = GOOD_BODY[:len(GOOD_BODY) // 2] + "\n\nCloses #2783\n"
err = cb(GOOD_BODY, half)
check("a large shrink FAILS", err is not None, str(err))
check("...and the message states the threshold in bytes",
      err is not None and "threshold of" in err, str(err))
check("...and names the flag that would allow it",
      err is not None and "--force-shrink" in err, str(err))
check("--force-shrink allows it", cb(GOOD_BODY, half, force_shrink=True) is None, "")

small = GOOD_BODY.replace("corpus 2500/2500 exit 0 · ", "")
check("a small shrink is allowed without a flag", cb(GOOD_BODY, small) is None,
      str(cb(GOOD_BODY, small)))
check("...and it really was a shrink", len(small) < len(GOOD_BODY),
      f"{len(small)} vs {len(GOOD_BODY)}")

# The whole incident, run through the checks that did not exist at the time.
err = cb(GOOD_BODY, DESTROYED_BODY)
check("the actual #2790 edit (711-ish bytes over a 4 KB body) is REFUSED",
      err is not None, str(err))
check("...for losing the closing reference", err is not None and "#2783" in err, str(err))
check("...and for the shrink", err is not None and "shrinks the body" in err, str(err))


# --------------------------------------------------------------------------
print("\nclaims that must keep holding (--must-contain / --must-not-contain)")
# --------------------------------------------------------------------------

err = cb(GOOD_BODY, GOOD_BODY, must_contain=["corpus 2500/2500"])
check("--must-contain passes when the claim is there", err is None, str(err))
err = cb(GOOD_BODY, GOOD_BODY, must_contain=["corpus 2600/2600"])
check("--must-contain FAILS when it is not", err is not None, str(err))

# The rebase case: a body claiming in bold that the baseline is untouched, on a
# head commit that changed it.
err = cb(GOOD_BODY, GOOD_BODY,
         must_not_contain=["`tests/expectations/count-baseline/test-count-baseline.json` is untouched"])
check("--must-not-contain FAILS on a claim that no longer matches the diff",
      err is not None, str(err))
check("...and says a reviewer trusts that sentence in order to skip checking",
      err is not None and "SKIP checking" in err, str(err))


# --------------------------------------------------------------------------
print("\nverification after upload -- the write's exit code is not evidence")
# --------------------------------------------------------------------------

INTENDED = GOOD_BODY.replace("is untouched", "is updated in this PR")


def verify(write_rc, what_is_there, orig=GOOD_BODY, intended=INTENDED):
    seen = {}

    def writer(text):
        seen["text"] = text
        return write_rc, "" if write_rc == 0 else "dial tcp 140.82.121.6:443: i/o timeout"

    def refetch():
        if what_is_there is None:
            raise pb.FetchError("the fetched body is EMPTY")
        return pb.norm(what_is_there)

    return pb.upload_and_verify(pb.norm(orig), pb.norm(intended), writer, refetch), seen


out, seen = verify(0, INTENDED)
check("a write that lands is verified green", out.code == pb.EXIT_OK, f"code={out.code}")
check("...and what was uploaded is what was intended", seen["text"] == pb.norm(INTENDED), "")

# The real case from the same night: `gh` reported `dial tcp ... i/o timeout` on a
# call that had already succeeded, and the retry said "already merged".
out, _ = verify(1, INTENDED)
check("a write reporting failure that ACTUALLY LANDED is green, not a false alarm",
      out.code == pb.EXIT_OK, f"code={out.code}")
check("...and says so, so nobody retries a write that already succeeded",
      any("despite the write" in l for l in out.lines), str(out.lines))

out, _ = verify(0, GOOD_BODY)
check("a write that did NOT land gets its own code, not success",
      out.code == pb.EXIT_UPLOAD_FAILED, f"code={out.code}")
check("...and says nothing was lost", any("Nothing was lost" in l for l in out.lines),
      str(out.lines))

out, _ = verify(0, "something else entirely, neither one nor the other")
check("a body that is neither original nor intended is a VERIFICATION FAILURE",
      out.code == pb.EXIT_VERIFY_FAILED, f"code={out.code}")
check("...and the diff is printed so the state is actionable",
      any("actually on GitHub" in l for l in out.lines), str(out.lines))

out, _ = verify(0, None)
check("a verification that cannot read the body is a failure, not a pass",
      out.code == pb.EXIT_VERIFY_FAILED, f"code={out.code}")
check("...and says the state is UNKNOWN", any("UNKNOWN" in l for l in out.lines),
      str(out.lines))

# GitHub hands the body back with CRLF; without normalisation every single write
# would report a verification failure.
out, _ = verify(0, INTENDED.replace("\n", "\r\n"))
check("CRLF coming back from the API is not mistaken for a verification failure",
      out.code == pb.EXIT_OK, f"code={out.code}")


# --------------------------------------------------------------------------
print("\nthe CLI, end to end (fake gh, no network)")
# --------------------------------------------------------------------------

class FakeGh:
    """A fake `gh` holding one PR body, so main() can be driven end to end."""

    def __init__(self, body=GOOD_BODY, view_rc=0, view_out=None, edit_rc=0, edit_lands=True):
        self.body = pb.norm(body)
        self.view_rc, self.view_out = view_rc, view_out
        self.edit_rc, self.edit_lands = edit_rc, edit_lands
        self.edits = 0

    def __call__(self, args, attempts=4, sleep=None):
        if args[:2] == ["pr", "view"]:
            if self.view_out is not None:
                return self.view_rc, self.view_out
            return 0, json.dumps({"body": self.body})
        if args[:2] == ["pr", "edit"]:
            self.edits += 1
            path = args[args.index("--body-file") + 1]
            with open(path) as f:
                text = f.read()
            if self.edit_lands:
                self.body = pb.norm(text)
            return self.edit_rc, ""
        raise AssertionError(f"unexpected gh call: {args}")


def run(argv, fake):
    # time.sleep is patched out too: main()'s fetch retries back off for real
    # seconds, and a test suite that sleeps is a test suite nobody runs.
    real, pb.gh = pb.gh, fake
    real_sleep, pb.time.sleep = pb.time.sleep, lambda s: None
    try:
        return pb.main(argv)
    finally:
        pb.gh = real
        pb.time.sleep = real_sleep


f = FakeGh()
rc = run(["2790", "--replace", "is untouched", "is updated in this PR"], f)
check("a normal edit exits 0", rc == pb.EXIT_OK, f"rc={rc}")
check("...and the body on the server changed", "is updated in this PR" in f.body, "")
check("...and the closing reference survived", "Closes #2783" in f.body, "")

f = FakeGh(view_out=json.dumps({"body": ""}))
rc = run(["2790", "--replace", "is untouched", "x"], f)
check("THE INCIDENT: an empty fetch refuses to write", rc == pb.EXIT_FETCH_FAILED, f"rc={rc}")
check("...and no write was attempted at all", f.edits == 0, f"edits={f.edits}")

f = FakeGh(view_rc=1, view_out="dial tcp 140.82.121.6:443: i/o timeout")
rc = run(["2790", "--replace", "is untouched", "x"], f)
check("a failed fetch refuses to write", rc == pb.EXIT_FETCH_FAILED, f"rc={rc}")
check("...and no write was attempted", f.edits == 0, f"edits={f.edits}")

f = FakeGh()
rc = run(["2790", "--replace", "not in this body at all", "x"], f)
check("an anchor miss is a precondition failure", rc == pb.EXIT_PRECONDITION, f"rc={rc}")
check("...and nothing was written", f.edits == 0, f"edits={f.edits}")

f = FakeGh()
rc = run(["2790", "--replace", "is untouched", "is untouched"], f)
check("an edit that changes nothing is NOTHING-TO-DO, distinct from success",
      rc == pb.EXIT_NOTHING_TO_DO, f"rc={rc}")
check("...and nothing was written", f.edits == 0, f"edits={f.edits}")

f = FakeGh()
rc = run(["2790", "--dry-run", "--replace", "is untouched", "is updated"], f)
check("--dry-run exits 0", rc == pb.EXIT_OK, f"rc={rc}")
check("...and writes nothing", f.edits == 0, f"edits={f.edits}")

f = FakeGh()
rc = run(["2790", "--check", "--must-contain", "Closes #2783"], f)
check("--check passes when the claim holds", rc == pb.EXIT_OK, f"rc={rc}")
check("...and never writes", f.edits == 0, f"edits={f.edits}")

f = FakeGh()
rc = run(["2790", "--check", "--must-not-contain", "is untouched"], f)
check("--check fails on a body that disagrees with its own diff",
      rc == pb.EXIT_PRECONDITION, f"rc={rc}")

f = FakeGh()
rc = run(["2790", "--check"], f)
check("a --check that asserts NOTHING is refused (it could not fail)",
      rc == pb.EXIT_PRECONDITION, f"rc={rc}")

f = FakeGh()
rc = run(["2790"], f)
check("no edit and no --check is refused", rc == pb.EXIT_PRECONDITION, f"rc={rc}")

f = FakeGh(edit_lands=False)
rc = run(["2790", "--replace", "is untouched", "is updated in this PR"], f)
check("a write that silently did not land exits UPLOAD FAILED",
      rc == pb.EXIT_UPLOAD_FAILED, f"rc={rc}")

f = FakeGh(edit_rc=1)
rc = run(["2790", "--replace", "is untouched", "is updated in this PR"], f)
check("a write that reported failure but landed exits 0", rc == pb.EXIT_OK, f"rc={rc}")

# The replacement body here is perfectly legal on its own -- it keeps the
# closing reference and is the same size -- so the ONLY thing that can refuse
# this call is the rule that a whole-body replacement may not be combined with
# anchors. Without that rule the anchored edit is silently discarded and the
# write goes through, which is the class of silent no-op this tool exists to
# stop.
_fd, _bodyfile = tempfile.mkstemp(prefix="pr-body-test-", suffix=".md")
with os.fdopen(_fd, "w") as _f:
    _f.write(GOOD_BODY.replace("corpus 2500/2500", "corpus 2501/2501"))
try:
    f = FakeGh()
    rc = run(["2790", "--body-file", _bodyfile, "--replace", "is untouched", "is updated"], f)
    check("--body-file plus anchors is refused rather than silently dropping the anchors",
          rc == pb.EXIT_PRECONDITION, f"rc={rc}")
    check("...and nothing was written", f.edits == 0, f"edits={f.edits}")

    f = FakeGh()
    rc = run(["2790", "--body-file", _bodyfile], f)
    check("--body-file on its own still goes through the guards and writes",
          rc == pb.EXIT_OK and "corpus 2501/2501" in f.body, f"rc={rc}")
finally:
    os.unlink(_bodyfile)

f = FakeGh()
rc = run(["2790", "--append", "\n> Note: rebased onto main.\n"], f)
check("an append that keeps everything is allowed (with the comment warning)",
      rc == pb.EXIT_OK, f"rc={rc}")
check("...and the closing reference is still there afterwards",
      "Closes #2783" in f.body, f.body[-200:])

f = FakeGh()
rc = run(["2790", "--append", "\nSee: this fixes #2125 as well.\n"], f)
check("an append introducing a foreign closing keyword is refused",
      rc == pb.EXIT_PRECONDITION, f"rc={rc}")
check("...and nothing was written", f.edits == 0, f"edits={f.edits}")


# --------------------------------------------------------------------------
print("\nparity with .github/scripts/check_closing_reference.sh")
# --------------------------------------------------------------------------
# The server-side gate is that shell script. If this Python port drifts from it,
# pr-body.py starts passing bodies pr-check.yml rejects (or the reverse), and the
# local check stops meaning anything.

SH = os.path.join(HERE, "..", ".github", "scripts", "check_closing_reference.sh")
CASES = [
    ("plain declaration", "Some text.\n\nCloses #2783\n"),
    ("no reference at all", "Some text with no reference.\n"),
    ("inline foreign keyword", "Closes #2783\n\nThis does not close #2125.\n"),
    ("inline restatement of a declared target", "Closes #2783\n\nIt closes #2783 indeed.\n"),
    ("prose with a bare number", "Closes #2783\n\nThis fixes 3 bugs in the parser.\n"),
    ("cross-repo declaration", "Fixes StefanMaron/BusinessCentral.AL.Runner#42\n"),
    ("url reference inline", "Closes #2783\n\nsee https://github.com/o/r/issues/77 fixes https://github.com/o/r/issues/77\n"),
]

if not shutil.which("bash") or not os.path.exists(SH):
    print("  SKIP parity: bash or check_closing_reference.sh unavailable "
          "(this is NOT a pass -- the port is unverified in this environment)")
else:
    probe = subprocess.run(["bash", "-c", "printf 'a' | command grep -qP 'a'"],
                           capture_output=True)
    if probe.returncode != 0:
        print("  SKIP parity: grep has no -P here (NOT a pass -- port unverified)")
    else:
        for name, body in CASES:
            env = dict(os.environ, PR_TITLE="a title", PR_BODY=body, PR_COMMITS="")
            p = subprocess.run(["bash", SH], capture_output=True, text=True, env=env)
            sh_ok = p.returncode == 0
            declared = pb.declared_targets(body)
            py_stray = pb.stray_closing_reference(body, declared)
            # The shell script also fails a body with NO declared target and no
            # escape hatch; pr-body.py expresses that as --closes / closes-survive
            # rather than as an unconditional rule, so parity is asserted on the
            # STRAY verdict, which is the part both must agree on.
            sh_stray = "closing keyword" in (p.stderr or "")
            check(f"parity ({name}): stray verdict agrees",
                  bool(py_stray) == sh_stray,
                  f"py={py_stray} sh_rc={p.returncode} sh_stderr={(p.stderr or '')[:120]}")
            if sh_ok and declared:
                check(f"parity ({name}): declared targets agree",
                      f"declared target(s): {' '.join(str(n) for n in declared)}" in p.stdout
                      or all(str(n) in p.stdout for n in declared),
                      p.stdout.strip()[:160])

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
