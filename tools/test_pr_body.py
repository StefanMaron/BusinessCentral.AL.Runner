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
UNMEASURED: list[str] = []


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
            # UTF-8, because the real `gh` reads a --body-file as UTF-8; reading it
            # with the locale codec would make this fake agree with a broken write.
            with open(path, encoding="utf-8") as f:
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
# UTF-8, matching what the tool now reads and what any file an agent hands it
# actually is; the payload below carries a non-ASCII character.
with os.fdopen(_fd, "w", encoding="utf-8") as _f:
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
print("\n#4393: a code fence is not a declaration site")
# --------------------------------------------------------------------------
# The parity suite above asserts the STRAY verdict, which is too coarse to see
# the same-character rule: a body whose FIRST fenced clause is a stray exits 1
# whatever the later lines do, so a mutation removing that rule left the whole
# suite green. Found by mutation, not by inspection.
#
# These assert on declared_targets, the finer observable: a fence rule that
# closes too eagerly lets a LATER clause out of the fence, where it is recorded
# as declared -- which is the #4393 defect itself, one line deeper in the body.

FENCE_DECL_CASES = [
    # (name, body, must_not_be_declared)
    ("backtick fence", "Closes #2783\n\n```\nclosed #2125\n```\n", 2125),
    ("tilde fence", "Closes #2783\n\n~~~\nclosed #2125\n~~~\n", 2125),
    ("info string", "Closes #2783\n\n```text\nclosed #2125\n```\n", 2125),
    ("unclosed fence", "Closes #2783\n\n```\nclosed #2125\n", 2125),
    # The three rules the exit code cannot discriminate. The first clause names
    # the ALREADY-DECLARED target so it is exempt, making the SECOND clause the
    # subject of the assertion.
    ("same-character: backticks do not close a tilde fence",
     "Closes #2125\n\n~~~\nclosed #2125\n```\nclosed #2126\n~~~\n", 2126),
    ("length: a shorter run does not close a longer fence",
     "Closes #2125\n\n````\nclosed #2125\n```\nclosed #2126\n````\n", 2126),
    ("whole line: a marker with trailing text does not close",
     "Closes #2125\n\n```\nclosed #2125\n```x\nclosed #2126\n```\n", 2126),
]
for name, body, unwanted in FENCE_DECL_CASES:
    check(f"#4393 ({name}): the fenced clause is not declared",
          unwanted not in pb.declared_targets(body),
          str(pb.declared_targets(body)))

# GREEN CONTROLS at the same observable. Every case above passes for a tracker
# that calls every line fenced, or that never closes a fence; these are the ones
# that do not. Over-refusal fails a CORRECT PR, which is the expensive direction.
FENCE_CONTROL_CASES = [
    ("no fence at all", "Closes #2783\n", 2783),
    ("indented three spaces", "   Closes #2783\n", 2783),
    ("after a closed fence", "```\nexample\n```\n\nCloses #2783\n", 2783),
    ("between two fences", "```\na\n```\n\nCloses #2783\n\n```\nb\n```\n", 2783),
    ("after a closed tilde fence", "~~~\nexample\n~~~\n\nCloses #2783\n", 2783),
    ("after a closed fence with an info string", "```bash\necho hi\n```\n\nCloses #2783\n", 2783),
    ("after a nested fence closes", "````\n```\na\n```\n````\n\nCloses #2783\n", 2783),
    # STATED GAP: indented code blocks (CommonMark 4.4) are deliberately not
    # modelled -- "   Closes #2783" is a legal trailer and the boundary is one
    # invisible space away, so demoting it would fail a correct PR.
    ("KNOWN GAP: an indented code block still declares", "Closes #2783\n\nprose:\n\n    closed #2125\n", 2125),
]
for name, body, wanted in FENCE_CONTROL_CASES:
    check(f"#4393 control ({name}): #{wanted} IS declared",
          wanted in pb.declared_targets(body),
          str(pb.declared_targets(body)))

# A fenced clause naming an already-declared target is a restatement, exempt via
# the declared list exactly as an inline one is -- which is why a PR may document
# its own closing reference in a fence. This PR's body has that shape.
_b = "Closes #2783\n\n```\nCloses #2783\n```\n"
check("#4393 a fenced restatement of the declared target is not a stray",
      pb.stray_closing_reference(_b, pb.declared_targets(_b)) is None,
      str(pb.stray_closing_reference(_b, pb.declared_targets(_b))))

# --------------------------------------------------------------------------
print("\n#4393: what counts as INDENTATION before a fence marker")
# --------------------------------------------------------------------------
# Found in review. The shell gate used [[:space:]] and this port used [ \t];
# those differ on CR, VT and FF, so a line led by one of the three opened a fence
# here and not there -- with THIS side permissive, which is the dangerous
# direction: it would declare a hidden target while the gate called the same line
# a stray. The shell was narrowed to match, per CommonMark 0.31.2 section 4.5
# ("up to three spaces of indentation" -- spaces, section 2.1, not whitespace
# generally); a bare CR is a line terminator, and VT and FF have no
# block-indentation semantics at all.
#
# Pinned on BOTH sides: this file holds the Python half, and the shell half lives
# in test_check_closing_reference.sh. The parity sweep alone would not have found
# it -- its cases were all built from ordinary text.
#
# The probe shape is an UNCLOSED fence on purpose. A lead character before a
# fence that is later closed is invisible: the closing marker ends the block
# either way. Unclosed, the lead decides whether a following trailer is swallowed.

# CR, VT, FF are NOT indentation: no fence opens, so the trailer declares.
for _name, _lead in [("CR", "\r"), ("VT", "\v"), ("FF", "\f")]:
    _b = f"Closes #2783\n\n{_lead}```\nCloses #2125\n"
    check(f"#4393 a bare {_name} before a fence marker is not indentation",
          2125 in pb.declared_targets(_b), str(pb.declared_targets(_b)))

# CONTROLS: space and tab ARE indentation, so the fence opens, runs to end of
# body and swallows the trailer. Without these, narrowing the class to nothing
# would pass every case above.
for _name, _lead in [("a space", " "), ("a tab", "\t"), ("three spaces", "   ")]:
    _b = f"Closes #2783\n\n{_lead}```\nCloses #2125\n"
    check(f"#4393 control: {_name} before a fence marker IS indentation",
          2125 not in pb.declared_targets(_b), str(pb.declared_targets(_b)))

# An ordinary CRLF body is unaffected -- CR as a LINE ENDING is normal and
# common; only a BARE CR inside a line is not indentation. Asserted through
# norm(), which is what every real caller applies first (GitHub stores bodies
# with CRLF), so this is the path that actually runs.
_b = "Closes #2783\r\n\r\n```\r\nclosed #2125\r\n```\r\n"
check("#4393 control: an ordinary CRLF body declares only the real trailer",
      pb.declared_targets(pb.norm(_b)) == [2783],
      str(pb.declared_targets(pb.norm(_b))))

# ...and the fenced clause in that same body is still a stray, so CRLF does not
# reopen the #4393 hole by a different route.
_n = pb.norm(_b)
check("#4393 control: the fenced clause in a CRLF body is still a stray",
      (pb.stray_closing_reference(_n, pb.declared_targets(_n)) or (None,))[0] == 2125,
      str(pb.stray_closing_reference(_n, pb.declared_targets(_n))))

# #4396 is that deliberate change. The line this replaced pinned raw CRLF as
# declaring NOTHING and said a later change to CANONICAL_LINE_RE should be
# deliberate; GitHub's own behaviour is what makes it so. Two merged PRs on this
# repository carry a wholly CRLF body whose trailer reads "Closes #N\r", and
# GitHub closed the named issue on merge both times: PR #3967 -> #3964, PR #3899
# -> #3881 (closingIssuesReferences, read back from the API). So a trailing CR is
# part of a REAL declaration, the permissive shell gate had it right, and this
# port -- which every caller reaches through norm() but which check_body() also
# applies to a --body-file the caller may hand it raw -- had it wrong.
check("#4396 raw un-normalised CRLF declares the target, as GitHub does",
      pb.declared_targets(_b) == [2783], str(pb.declared_targets(_b)))

# STATED GAP (raised in review): CommonMark 4.5 says a BACKTICK fence's info
# string may not contain a backtick, so "```x`y" is not a fence opener. Both
# implementations treat it as one, so they AGREE -- no parity risk, and the only
# cost is a loud false positive. Pinned rather than fixed, to keep this diff to
# the defect it is about.
_b = "Closes #2783\n\n```x`y\nCloses #2125\n"
check("#4393 STATED GAP: a backtick in the info string still opens a fence",
      2125 not in pb.declared_targets(_b), str(pb.declared_targets(_b)))

# --------------------------------------------------------------------------
print("\n#4396: SEP and CANONICAL_LINE_RE must admit the same whitespace as the gate")
# --------------------------------------------------------------------------
# The gate writes both constants with [[:space:]]; this port wrote them with
# [ \t]. Those differ on CR, VT and FF, so the two disagreed about what a
# declaration is -- with the GATE permissive, and the gate turned out to be the
# one that matches GitHub.
#
# The direction was settled by GitHub, not by argument. A trailing CR is not a
# hypothetical: a body stored with CRLF line endings puts one at the end of
# EVERY line, including the trailer, and CANONICAL_LINE_RE is $-anchored. Two
# merged PRs here prove GitHub honours it -- #3967 ("Closes #3964\r" -> closed
# #3964) and #3899 ("Closes #3881\r" -> closed #3881). Narrowing the shell to
# match this port would have FAILED both of those correct PRs; widening this
# port makes both files agree with GitHub instead of with each other.
#
# Why an explicit class and not \s: \s admits \n, and STRAY_RE runs against the
# WHOLE body rather than a line at a time, so \s would let one clause span a
# line break and match a keyword against the next line's number. The shell is
# immune to that only because grep is line-oriented; this port is not.
for _n, _c in [("CR", "\r"), ("VT", "\v"), ("FF", "\f")]:
    check(f"#4396 a {_n} before the keyword still declares",
          pb.declared_targets(f"{_c}Closes #123") == [123],
          str(pb.declared_targets(f"{_c}Closes #123")))
    check(f"#4396 a {_n} inside the keyword/number separator still declares",
          pb.declared_targets(f"Closes{_c}#123") == [123],
          str(pb.declared_targets(f"Closes{_c}#123")))
    check(f"#4396 a trailing {_n} after the reference still declares",
          pb.declared_targets(f"Closes #123{_c}") == [123],
          str(pb.declared_targets(f"Closes #123{_c}")))

# GREEN CONTROLS. Every row above passes for a class widened to "anything", so
# these are the rows that fail if the class stops discriminating. A newline must
# NOT be admitted, or a clause spans lines and STRAY_RE fires on the wrong number.
check("#4396 control: a plain trailer still declares",
      pb.declared_targets("Closes #123") == [123], str(pb.declared_targets("Closes #123")))
check("#4396 control: space and tab still declare",
      pb.declared_targets(" Closes #123") == [123] and pb.declared_targets("\tCloses #123") == [123],
      "")
check("#4396 control: prose with a keyword is still not a declaration",
      pb.declared_targets("This does not close #123.") == [],
      str(pb.declared_targets("This does not close #123.")))
# The newline control asserts on STRAY_RE ITSELF, not on
# stray_closing_reference(): that function splits the body on "\n" and runs the
# regex one line at a time, so it is structurally immune to a cross-line match
# and answers None whatever WS admits. Asserting through it measured the
# splitting, not the class -- a mutation widening WS to "\s" left it green while
# STRAY_RE really did match "closes\n#999" across the break. Pin the property
# where it lives, so the row fails if WS ever admits a newline.
check("#4396 control: a newline is NOT separator whitespace, so no clause spans lines",
      pb.STRAY_RE.search("Some prose that closes\n#999 later.") is None,
      str(pb.STRAY_RE.search("Some prose that closes\n#999 later.")))
# ...and the same shape WITH a real separator character does fire, so the row
# above is pinned to the newline rather than to the sentence being unmatchable.
check("#4396 control: the same shape with a CR instead of the newline IS a stray",
      (pb.stray_closing_reference("Some prose that closes\r#999 later.", []) or (None,))[0] == 999,
      str(pb.stray_closing_reference("Some prose that closes\r#999 later.", [])))
check("#4396 control: STRAY_RE matches the CR shape it is pinned against",
      pb.STRAY_RE.search("Some prose that closes\r#999 later.") is not None, "")

# The real merged bodies, reduced to their load-bearing shape: a wholly CRLF
# body declares its trailer and nothing else.
_crlf = "Closes #3964\r\n\r\nSome prose.\r\n"
check("#4396 a wholly CRLF body declares its trailer (PR #3967's shape)",
      pb.declared_targets(_crlf) == [3964], str(pb.declared_targets(_crlf)))
check("#4396 control: that CRLF body raises no stray",
      pb.stray_closing_reference(_crlf, pb.declared_targets(_crlf)) is None,
      str(pb.stray_closing_reference(_crlf, pb.declared_targets(_crlf))))

# --------------------------------------------------------------------------
print("\nparity with .github/scripts/check_closing_reference.sh")
# --------------------------------------------------------------------------
# The server-side gate is that shell script. If this Python port drifts from it,
# pr-body.py starts passing bodies pr-gate.yml rejects (or the reverse), and the
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
    # #3094: the colon separator. GitHub honors "closes: #N" and neither
    # implementation saw it, so this pair of files agreed with each other and
    # both disagreed with GitHub -- which is the one failure mode a parity
    # suite cannot catch by construction unless the case is actually listed
    # here. It closed #2942 for real when PR #2951 merged.
    ("colon declaration", "Closes: #2783\n"),
    ("colon inline foreign keyword", "Closes #2783\n\nThis does not close: #2125.\n"),
    ("colon with no space", "Closes #2783\n\nThis does not close:#2125.\n"),
    ("semicolon inline foreign keyword", "Closes #2783\n\nSuperseded; fixes; #2125 stays open.\n"),
    ("colon cross-repo inline", "Closes #2783\n\nNot this one, resolved: owner/repo#2125\n"),
    ("colon url inline", "Closes #2783\n\nThis does not close: https://github.com/o/r/issues/2125\n"),
    ("colon restatement of a declared target", "Closes #2783\n\nIt closes: #2783 indeed.\n"),
    # #4393: a clause alone on a line inside a code fence used to match
    # CANONICAL_LINE_RE and be recorded as a DECLARED TARGET -- in BOTH files, so
    # they agreed with each other while both let an undeclared issue close
    # silently. That is the #3094 failure mode again, and the only cure is
    # listing the cases. Each row pins a distinct CommonMark 4.5 rule, because a
    # tracker modelling only backticks passes the first and leaks the rest.
    ("fenced bare clause", "Closes #2783\n\n```\nclosed #2125\n```\n"),
    ("tilde-fenced bare clause", "Closes #2783\n\n~~~\nclosed #2125\n~~~\n"),
    ("fenced bare clause with an info string", "Closes #2783\n\n```text\nclosed #2125\n```\n"),
    ("nested fence keeps the inner clause fenced", "Closes #2783\n\n````\n```\nclosed #2125\n```\n````\n"),
    ("unclosed fence runs to end of body", "Closes #2783\n\n```\nclosed #2125\n"),
    ("a tilde fence is not closed by backticks", "Closes #2783\n\n~~~\nclosed #2125\n```\nclosed #2126\n~~~\n"),
    ("fenced colon-form clause", "Closes #2783\n\n```\ncloses: #2125\n```\n"),
    # The exemption survives: a fenced clause naming an already-declared target
    # is a restatement. This PR's own body has that shape.
    ("fenced restatement of the declared target", "Closes #2783\n\n```\nCloses #2783\n```\n"),
    # GREEN CONTROLS. Every row above passes for a tracker that calls every line
    # fenced; these are the rows that do not. Over-refusal fails a CORRECT PR,
    # which is the expensive direction here.
    ("declaration AFTER a closed fence", "```\nexample\n```\n\nCloses #2783\n"),
    ("declaration BETWEEN two fences", "```\na\n```\n\nCloses #2783\n\n```\nb\n```\n"),
    ("declaration after a closed tilde fence", "~~~\nexample\n~~~\n\nCloses #2783\n"),
    # STATED GAP: indented code blocks (CommonMark 4.4) are not modelled, because
    # "   Closes #2783" is a legal trailer and the boundary is one space wide.
    # Listed so the two files are pinned to agreeing about the gap too.
    ("indented code block is not modelled", "Closes #2783\n\nprose:\n\n    closed #2125\n"),
    # #4396: CR, VT and FF. The gate's [[:space:]] admitted them and this port's
    # [ \t] did not, so the two disagreed about what a declaration is -- and the
    # existing rows are all ordinary text, which is why a 700-body sweep of them
    # found nothing. A wholly CRLF body is the reachable instance: it puts a CR
    # at the end of every line, trailer included.
    ("CRLF body declares its trailer", "Closes #2783\r\n\r\nSome prose.\r\n"),
    ("CR before the keyword", "\rCloses #2783\n"),
    ("CR inside the separator", "Closes\r#2783\n"),
    ("VT inside the separator", "Closes\v#2783\n"),
    ("FF before the keyword", "\fCloses #2783\n"),
    ("CR-separated foreign keyword is a stray", "Closes #2783\n\nThis does not close\r#2125.\n"),
    ("CR-separated restatement of a declared target", "Closes #2783\n\nIt closes\r#2783 indeed.\n"),
]

# Both branches below already SAID "NOT a pass" and then let the run end at "all checks passed",
# exit 0 -- the message knew the answer and the exit code contradicted it. Exit 0 is the worst
# place to put a third state: 1 at least makes a sweep stop and look, while 0 is what a passing
# run returns, so nothing ever looks (guards-need-a-third-state.md, #4355).
if not shutil.which("bash") or not os.path.exists(SH):
    UNMEASURED.append(
        "bash or check_closing_reference.sh is unavailable, so the shell/python parity of the "
        "closing-reference port was not verified here. Every other check in this file ran.")
    print("  UNMEASURED parity: bash or check_closing_reference.sh unavailable")
else:
    probe = subprocess.run(["bash", "-c", "printf 'a' | command grep -qP 'a'"],
                           capture_output=True)
    if probe.returncode != 0:
        UNMEASURED.append(
            "this grep has no -P, so the shell side of the parity check could not run. Every "
            "other check in this file ran.")
        print("  UNMEASURED parity: grep has no -P here")
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

# --------------------------------------------------------------------------
print("\ntext is UTF-8, never the locale codec (#3434)")
# --------------------------------------------------------------------------
# GitHub bodies are UTF-8 by definition. Every decode and encode in pr-body.py
# must say so: on Windows the locale codec is cp1252, so an em dash read back
# from `gh` becomes three mojibake characters and an anchor copied verbatim out
# of the body matches 0 times, while the write path re-encodes it to a byte
# `gh --body-file` cannot read as UTF-8. The verify-by-re-reading guard cannot
# see that, because both sides decode wrong identically.
#
# The kwargs checks below are the ones that hold on any machine; the behavioural
# checks under them only distinguish fixed from broken where the ambient codec
# is not already UTF-8, which is why the child-interpreter run at the end forces
# one that is not.

EM = "an em dash — here"

class _Args:
    """The three attributes build_new_body reads off argparse's namespace."""

    def __init__(self, body_file=None, append=None, append_file=None):
        self.body_file, self.append, self.append_file = body_file, append, append_file




def record_kwargs(store, real):
    def spy(*a, **kw):
        store.append(kw)
        return real(*a, **kw)
    return spy


calls: list[dict] = []


class _Done:
    """What gh() reads off subprocess.run; no `gh` binary need exist."""

    returncode, stdout, stderr = 0, "{}", ""


def _spy(*a, **kw):
    calls.append(kw)
    return _Done()


_real_run, pb.subprocess.run = pb.subprocess.run, _spy
try:
    pb.gh(["--version"], attempts=1, sleep=lambda s: None)
finally:
    pb.subprocess.run = _real_run
check("gh() decodes stdout as UTF-8",
      bool(calls) and calls[0].get("encoding") == "utf-8", str(calls[:1]))
check("gh() decodes strictly, so a bad byte is loud",
      bool(calls) and calls[0].get("errors") == "strict", str(calls[:1]))

fd_calls: list[dict] = []
_real_fdopen, pb.os.fdopen = pb.os.fdopen, record_kwargs(fd_calls, pb.os.fdopen)
try:
    tmp = pb.write_body_tempfile(EM)
finally:
    pb.os.fdopen = _real_fdopen
try:
    written = open(tmp, "rb").read()
finally:
    os.unlink(tmp)
check("write_body_tempfile encodes as UTF-8",
      bool(fd_calls) and fd_calls[0].get("encoding") == "utf-8", str(fd_calls[:1]))
check("write_body_tempfile writes UTF-8 bytes",
      b"\xe2\x80\x94" in written and b"\x97" not in written, ascii(written))

open_calls: list[dict] = []
_real_open, pb.open = open, record_kwargs(open_calls, open)
bf = os.path.join(tempfile.mkdtemp(), "body.md")
with open(bf, "wb") as f:
    f.write((EM + "\n").encode("utf-8"))
try:
    body, _ = pb.build_new_body("", _Args(body_file=bf), [])
    open_calls_body = list(open_calls)
    open_calls.clear()
    appended, _ = pb.build_new_body("orig", _Args(append_file=bf), [])
finally:
    del pb.open
check("--body-file is read as UTF-8",
      bool(open_calls_body) and open_calls_body[0].get("encoding") == "utf-8",
      str(open_calls_body[:1]))
check("--append-file is read as UTF-8",
      bool(open_calls) and open_calls[0].get("encoding") == "utf-8", str(open_calls[:1]))
check("--body-file keeps the em dash", "—" in body, ascii(body))
check("--append-file keeps the em dash", "—" in appended, ascii(appended))

# The behavioural half, under a codec that is not UTF-8. A child interpreter is
# the only portable lever: PYTHONIOENCODING reaches only sys.std*, and from
# 3.11 the TextIOWrapper default is resolved in C, out of reach of a monkeypatch.
CHILD = r'''
import importlib.util, json, locale, os, subprocess, sys, tempfile
enc = locale.getpreferredencoding(False)
if enc.lower().replace("-", "") in ("utf8", "cp65001"):
    print(json.dumps({"skipped": enc})); raise SystemExit(0)
spec = importlib.util.spec_from_file_location("pr_body", sys.argv[1])
pb = importlib.util.module_from_spec(spec); spec.loader.exec_module(pb)
payload = json.dumps({"body": "an em dash \u2014 here"}, ensure_ascii=False).encode("utf-8")
real = subprocess.run
pb.subprocess.run = lambda args, **kw: real(
    [sys.executable, "-c", "import sys;sys.stdout.buffer.write(%r)" % payload], **kw)
out = {"encoding": enc}
try:
    out["read"] = pb.parse_body_json(*pb.gh(["pr", "view", "1", "--json", "body"]))
except Exception as e:
    out["read"] = "raised: %s" % type(e).__name__
try:
    p = pb.write_body_tempfile("an em dash \u2014 here")
    out["written"] = open(p, "rb").read().decode("utf-8", "replace"); os.unlink(p)
except Exception as e:
    out["written"] = "raised: %s" % type(e).__name__
sys.stdout.buffer.write(json.dumps(out).encode("utf-8"))
'''
# argv is decoded with the filesystem encoding, which under LC_ALL=C on Linux is
# ASCII: a literal em dash here would reach the child as surrogates and raise
# before it could report anything. The escape survives as source text instead.
assert CHILD.isascii(), "CHILD must survive an ASCII argv"
env = dict(os.environ, PYTHONUTF8="0", PYTHONCOERCECLOCALE="0", LC_ALL="C", LANG="C")
env.pop("PYTHONIOENCODING", None)
child = subprocess.run([sys.executable, "-c", CHILD, os.path.join(HERE, "pr-body.py")],
                       capture_output=True, env=env)
try:
    verdict = json.loads(child.stdout.decode("utf-8"))
except Exception:
    verdict = None
if verdict is None:
    check("non-UTF-8 locale: child ran", False,
          ascii(child.stdout[-300:]) + ascii(child.stderr[-300:]))
elif "skipped" in verdict:
    print("  SKIP non-UTF-8 locale: this box gives %s even under LC_ALL=C "
          "(NOT a pass -- the kwargs checks above are what gate here)" % verdict["skipped"])
else:
    check("non-UTF-8 locale (%s): gh output keeps the em dash" % verdict["encoding"],
          verdict["read"] == EM, ascii(verdict["read"]))
    check("non-UTF-8 locale (%s): the body file keeps the em dash" % verdict["encoding"],
          verdict["written"].strip() == EM, ascii(verdict["written"]))

# --------------------------------------------------------------------------
# #3589: the whole tool, end to end, under a stdout the console codec owns.
# `print(d)` at the two --dry-run / writing sites encodes the PR BODY, and a
# body carrying the robot emoji of the Claude Code footer killed the tool on
# this box AFTER the write had landed and verified -- so the caller saw a crash
# for a successful edit. A child interpreter is the only lever: PYTHONIOENCODING
# is read by the interpreter before main() exists.
# --------------------------------------------------------------------------
CP1252_CHILD = r'''
import importlib.util, json, sys
pre = (sys.stdout.encoding or "").lower().replace("-", "")
if pre != "cp1252":
    # PYTHONIOENCODING is honoured on every platform, so anything else here is a
    # broken test environment, never a reason to skip.
    sys.stderr.write("PRECONDITION-FAIL: stdout encoding is %s" % pre)
    raise SystemExit(3)
spec = importlib.util.spec_from_file_location("pr_body", sys.argv[1])
pb = importlib.util.module_from_spec(spec)
sys.modules["pr_body"] = pb
spec.loader.exec_module(pb)
pb._freshness = None       # no origin/main question here; this is about printing
body = "footer \U0001f916 with an em dash \u2014 in it. " * 12   # over the --min-bytes floor
pb.gh = lambda args, attempts=4: (0, json.dumps({"body": body}, ensure_ascii=False))
raise SystemExit(pb.main(["1", "--repo", "R", "--dry-run", "--append", "tail"]))
'''
assert CP1252_CHILD.isascii(), "CP1252_CHILD must survive an ASCII argv"
ROBOT_UTF8 = "\U0001f916".encode("utf-8")
DASH_UTF8 = "—".encode("utf-8")
DASH_CP1252 = "—".encode("cp1252")
_env = dict(os.environ, PYTHONIOENCODING="cp1252", PYTHONUTF8="0")
_child = subprocess.run([sys.executable, "-c", CP1252_CHILD, os.path.join(HERE, "pr-body.py")],
                        capture_output=True, env=_env)
_detail = ascii(_child.stdout[-200:]) + " " + ascii(_child.stderr[-400:])
check("#3589: --dry-run exits 0 with a cp1252 stdout and an emoji in the body",
      _child.returncode == 0, _detail)
check("#3589: the printed diff carries the emoji as UTF-8",
      ROBOT_UTF8 in _child.stdout, _detail)
check("#3589: the printed diff carries the em dash as UTF-8, not as cp1252 0x97",
      DASH_UTF8 in _child.stdout and DASH_CP1252 not in _child.stdout, _detail)

print()
# A real failure outranks an unmeasured half, for the reason #4346 states: both can occur at once,
# and reporting 3 then would hide a measured negative behind "could not measure".
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
if UNMEASURED:
    for _note in UNMEASURED:
        print(f"  UNMEASURED {_note}")
    print(f"{len(UNMEASURED)} check group(s) could not be measured; nothing here is a pass")
    sys.exit(3)
print("all checks passed")
