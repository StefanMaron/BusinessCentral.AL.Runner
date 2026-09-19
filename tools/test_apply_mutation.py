#!/usr/bin/env python3
"""Does apply-mutation.py tell APPLIED, NOT-APPLIED and AMBIGUOUS apart?

#4316's own bar: "whatever route is taken needs a RED that distinguishes the three states a
mutation application can be in — applied, not applied, and applied more than once — because a
mutator that detects zero matches but not two has the same hole one step along."

So each state gets its own case, and each asserts the EXIT CODE rather than the message: the
code is what a calling script branches on, and a message nobody reads is not a guard.

Run: python3 tools/test_apply_mutation.py
"""
from __future__ import annotations

import ast
import importlib.util
import inspect
import io
import os
import sys
import tempfile
import time
import textwrap
from contextlib import redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("apply_mutation", os.path.join(HERE, "apply-mutation.py"))
am = importlib.util.module_from_spec(_spec)
sys.modules["apply_mutation"] = am
_spec.loader.exec_module(am)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
        return
    FAILURES.append(name)
    print(f"  FAIL {name}" + (f" {detail}" if detail else ""))


def run(body: str, anchor: str, replacement: str) -> tuple[int, str, str]:
    """Apply into a temp file; return (exit code, printed text, resulting file body)."""
    d = tempfile.mkdtemp()
    path = os.path.join(d, "subject.cs")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(body)
    a = os.path.join(d, "a.txt")
    r = os.path.join(d, "r.txt")
    for f, s in ((a, anchor), (r, replacement)):
        with open(f, "w", encoding="utf-8") as fh:
            fh.write(s)
    with redirect_stdout(io.StringIO()) as out:
        code = am.main(["apply-mutation.py", path, "--anchor-file", a, "--replacement-file", r])
    with open(path, encoding="utf-8") as fh:
        return code, out.getvalue(), fh.read()


print("the three states")

code, msg, body = run("alpha\nbeta\ngamma\n", "beta", "BETA")
check("a unique anchor is APPLIED", code == am.APPLIED, f"{code}: {msg}")
check("...and the file actually changed", "BETA" in body and "beta" not in body, body)

# The measured defect: a repair moved the line, so the anchor is stale.
code, msg, body = run("alpha\nbeta-was-repaired\ngamma\n", "beta\n", "BETA\n")
check("a stale anchor is NOT-APPLIED, not a silent pass", code == am.NOT_APPLIED, f"{code}: {msg}")
check("...and the file is untouched", body == "alpha\nbeta-was-repaired\ngamma\n", body)
# Pin WHICH arm answered, not only the code. The count check and the post-write re-read both
# return NOT_APPLIED, so a verdict-only assertion passes with the count check deleted -- the
# arm never fires and nothing says so. Found by mutating this file's own zero-match arm.
check("...and it is the COUNT check that says so, not the post-write re-read",
      "matched NOTHING" in msg, msg)

# The hole one step along: detecting zero but not two.
code, msg, body = run("beta\nalpha\nbeta\n", "beta", "BETA")
check("two matches are AMBIGUOUS, not a silent first-match mutation", code == am.AMBIGUOUS, f"{code}: {msg}")
check("...and the file is untouched", body == "beta\nalpha\nbeta\n", body)
# Both fixtures assert the count, not just one. Pinning it on the 3-match case alone left a
# hardcoded 3 GREEN, and a 2-match input then printed "matched 3 times" -- a wrong count handed
# to an agent deciding how far to narrow its anchor, which is the one thing this number is for.
check("...and the count is the REAL one here too, not whatever the other fixture has",
      "2 times" in msg, msg)

# Falsifying the reported count stayed GREEN in review, because the AMBIGUOUS fixture has
# exactly 2 matches and "matched 2 times" is what a hardcoded 2 also prints. Three matches
# distinguishes them: the count is what tells an agent how far to narrow the anchor.
code, msg, _ = run("beta\nbeta\nalpha\nbeta\n", "beta", "BETA")
check("three matches are AMBIGUOUS too", code == am.AMBIGUOUS, f"{code}: {msg}")
check("...and the message reports the REAL count, not a constant", "3 times" in msg, msg)

print("a second apply cannot destroy the backup")
d2 = tempfile.mkdtemp()
p2 = os.path.join(d2, "s.cs")
with open(p2, "w", encoding="utf-8") as fh:
    fh.write("line ONE\nline TWO\n")
def _files(a, r):
    fa, fr = os.path.join(d2, "a"), os.path.join(d2, "r")
    for f, s in ((fa, a), (fr, r)):
        with open(f, "w", encoding="utf-8") as fh:
            fh.write(s)
    return fa, fr
fa, fr = _files("line ONE", "line ONE-MUT")
with redirect_stdout(io.StringIO()):
    am.main(["apply-mutation.py", p2, "--anchor-file", fa, "--replacement-file", fr])
fa, fr = _files("line TWO", "line TWO-MUT")
with redirect_stdout(io.StringIO()) as out:
    rc2 = am.main(["apply-mutation.py", p2, "--anchor-file", fa, "--replacement-file", fr])
check("a second apply over a live backup is REFUSED", rc2 == am.REFUSED, f"{rc2}: {out.getvalue()}")
with redirect_stdout(io.StringIO()):
    am.main(["apply-mutation.py", p2, "--restore"])
with open(p2, encoding="utf-8") as fh:
    back = fh.read()
check("...so --restore recovers the file COMPLETELY, not just the second mutation",
      back == "line ONE\nline TWO\n", back)

print("I/O failures refuse rather than reading as NOT-APPLIED")
with redirect_stdout(io.StringIO()) as out:
    rc3 = am.main(["apply-mutation.py", os.path.join(d2, "nope.cs"),
                   "--anchor-file", os.path.join(d2, "missing"), "--replacement-file", fr])
check("a missing anchor file is REFUSED, not NOT-APPLIED", rc3 == am.REFUSED, f"{rc3}: {out.getvalue()}")
check("...because a mistyped path and a stale anchor have opposite remedies",
      rc3 != am.NOT_APPLIED, str(rc3))

print("anchor == replacement is a refusal, and strands nothing")
# Review of #4321 found this arm stayed GREEN under mutation: it was reachable (a paste error
# makes anchor and replacement the same) and it returned NOT_APPLIED *after* writing a backup,
# so it stranded one -- and the next apply then refused with "a mutation is still applied" when
# none ever was. Both halves are pinned here.
d3 = tempfile.mkdtemp()
p3 = os.path.join(d3, "s.cs")
with open(p3, "w", encoding="utf-8") as fh:
    fh.write("line ONE\nline TWO\n")
same = os.path.join(d3, "same")
with open(same, "w", encoding="utf-8") as fh:
    fh.write("line ONE")
with redirect_stdout(io.StringIO()) as out:
    rc4 = am.main(["apply-mutation.py", p3, "--anchor-file", same, "--replacement-file", same])
msg4 = out.getvalue()
check("anchor identical to replacement is REFUSED, not NOT-APPLIED", rc4 == am.REFUSED, f"{rc4}: {msg4}")
check("...because nothing was measured — no mutation was expressed at all",
      "byte-identical" in msg4, msg4)
check("...and NO backup is left stranded", not os.path.exists(p3 + am.SUFFIX),
      "a .mutation-backup survived a run that mutated nothing")
with redirect_stdout(io.StringIO()) as out:
    fa, fr = os.path.join(d3, "a"), os.path.join(d3, "r")
    for f, s in ((fa, "line TWO"), (fr, "line TWO-MUT")):
        with open(f, "w", encoding="utf-8") as fh:
            fh.write(s)
    rc5 = am.main(["apply-mutation.py", p3, "--anchor-file", fa, "--replacement-file", fr])
check("...so a REAL mutation afterwards still applies", rc5 == am.APPLIED, f"{rc5}: {out.getvalue()}")

print("a usage error is a refusal, not a measured answer")
with redirect_stdout(io.StringIO()) as out:
    rc6 = am.main(["apply-mutation.py", p3])
check("--anchor-file omitted is REFUSED", rc6 == am.REFUSED, f"{rc6}: {out.getvalue()}")
check("...not NOT-APPLIED, which would read as 'your anchor is stale'", rc6 != am.NOT_APPLIED, str(rc6))

print("every refusal arm is asserted, not merely produced")
# Rounds 2-4 of review each found the same shape: a property pinned on the member it was shown
# and left bare on the rest of its population. Round 4 found it on the REFUSAL arms -- REFUSED
# was asserted on four and merely produced on six, so three reachable arms could be flipped to a
# measured answer with the suite fully green. The worst was a failed --restore reporting success
# while the file stayed mutated, which is round 1's defect by another door.
#
# So this block enumerates the population rather than three more instances. A refusal arm added
# later and left unasserted fails the LAST check here, which is the point: an omission becomes a
# failure instead of a silence.
def _fresh():
    d = tempfile.mkdtemp()
    f = os.path.join(d, "s.cs")
    with open(f, "w", encoding="utf-8") as fh:
        fh.write("line ONE\nline TWO\n")
    a, r = os.path.join(d, "a"), os.path.join(d, "r")
    for path, s in ((a, "line ONE"), (r, "line ONE-MUT")):
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(s)
    return d, f, a, r

# A permission-based case cannot work as root or on Windows. The skip is LOUD and the
# population check above no longer has a constant to absorb it: skipping here makes those arms
# unreached, which reds. That is deliberate -- a silent skip asserting coverage is the defect
# round 5 found.
_unreadable_ok = os.name != "nt" and os.geteuid() != 0
if not _unreadable_ok:
    print(f"  NOTE permission-based refusal cases cannot run here "
          f"(os.name={os.name!r}, euid={getattr(os, 'geteuid', lambda: '?')()}); "
          f"the arms they cover will report as unreached")

def _argparse_failure():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--no-such-flag"])

def _unreadable_target():
    d, f, a, r = _fresh()
    os.chmod(f, 0o000)
    try:
        with redirect_stdout(io.StringIO()):
            return am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", r])
    finally:
        os.chmod(f, 0o644)

def _unreadable_backup():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", r])
    os.chmod(f + am.SUFFIX, 0o000)
    try:
        with redirect_stdout(io.StringIO()) as out:
            rc = am.main(["apply-mutation.py", f, "--restore"])
        with open(f, encoding="utf-8") as fh:
            still_mutated = "ONE-MUT" in fh.read()
        # A failed restore must not report success WHILE leaving the file mutated.
        check("...and a failed --restore does not report success over a still-mutated file",
              not (rc == am.APPLIED and still_mutated), f"rc={rc} mutated={still_mutated}")
        return rc
    finally:
        os.chmod(f + am.SUFFIX, 0o644)

REFUSAL_ARMS = [
    ("a missing anchor file", lambda: _missing_anchor()),
    ("a live backup blocking a second apply", lambda: _live_backup()),
    ("--restore with no backup", lambda: _no_backup()),
    ("anchor identical to replacement", lambda: _identical()),
    ("--anchor-file omitted", lambda: _omitted()),
    ("an unparseable command line", _argparse_failure),
]
# These four drive their arm by making an I/O call fail through file permissions, which cannot be
# done as root (chmod does not deny uid 0) or on Windows. They stay IN the table and are marked,
# rather than disappearing from it: dropping them made the census demand arms nothing could reach,
# so the suite failed as root for a correct reason it could not express (#4328).

def _unwritable_target():
    # Read succeeds, write fails: a read-only file in a writable directory.
    d, f, a, r = _fresh()
    os.chmod(f, 0o444)
    try:
        with redirect_stdout(io.StringIO()):
            return am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", r])
    finally:
        os.chmod(f, 0o644)

def _unrollbackable():
    # Reaching the rollback-failed arm needs os.replace to fail with BOTH writes succeeding.
    # Locking the directory does not do it: that breaks the backup WRITE, which is upstream, so
    # the case lands on the "could not write" arm another case already covers — measured in
    # review round 5, where it printed full coverage while this arm stayed unpinned.
    #
    # So fail os.replace itself, shadowed on the module only. A real cross-device rename is the
    # production shape (EXDEV, errno 18).
    d, f, a, r = _fresh()

    class _OsReplaceFails:
        def __getattr__(self, name):
            return getattr(os, name)

        def replace(self, src, dst):
            raise OSError(18, "Invalid cross-device link")

    real_os = am.os
    am.os = _OsReplaceFails()
    try:
        with redirect_stdout(io.StringIO()):
            rc = am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", a])
    finally:
        am.os = real_os
    # The backup must survive a failed rollback -- it is the only copy of the original.
    check("...and a failed rollback leaves the backup in place",
          os.path.exists(f + am.SUFFIX), "the backup vanished when the rollback failed")
    return rc

# Named functions, not `lambda: f()` wrappers: the wrapper hides the body from
# inspect.getsource, so the chmod check below reads the lambda line and reports a false positive.
PERMISSION_GATED = [
    ("an unreadable target file", _unreadable_target),
    ("an unreadable backup during --restore", _unreadable_backup),
    ("an unwritable target file", _unwritable_target),
]
# NOT gated: _unrollbackable shadows os.replace on the module rather than using file permissions,
# so it runs as any user on any platform. It sat here until the chmod check above rejected it —
# I had grouped it by "it is about an I/O failure" rather than by what actually stops it, which is
# the same confusion the census exists to prevent (#4328).
def _unwritable_stamp():
    # The restore stamp (#4343) records WHEN the mutation was restored, so mutation-verdict.py
    # can refuse a verdict from a binary built before it. If the stamp cannot be written the
    # restore itself still succeeded, and reporting APPLIED would read as "safe to re-run" on a
    # box where nothing can detect the stale binary.
    #
    # Blocked with a DIRECTORY at the stamp path rather than a chmod, so it runs as root too and
    # stays out of PERMISSION_GATED -- the same reasoning as _unrollbackable above.
    d, f, a, r = _fresh()
    with open(f, encoding="utf-8") as fh:
        pristine = fh.read()          # read it, never restate it: _fresh owns this body
    with redirect_stdout(io.StringIO()):
        am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", r])
    os.mkdir(am.stamp_path(f))
    with redirect_stdout(io.StringIO()) as out:
        rc = am.main(["apply-mutation.py", f, "--restore"])
    # The refusal must be about the STAMP alone: the source file is restored either way, and a
    # reader who took this for a failed restore would go looking for a backup that is gone.
    with open(f, encoding="utf-8") as fh:
        after = fh.read()
    check("...and a stamp that cannot be written still leaves the SOURCE restored",
          after == pristine, f"{after!r} != {pristine!r}")
    check("...and the refusal says the binary, not the source, is what is still wrong",
          "SOURCE is correct" in out.getvalue() and "BINARY still carries" in out.getvalue(),
          out.getvalue())
    return rc

REFUSAL_ARMS += PERMISSION_GATED + [
    ("a backup that cannot be rolled back", _unrollbackable),
    ("a restore stamp that cannot be written", _unwritable_stamp),
]

def _missing_anchor():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--anchor-file", os.path.join(d, "nope"),
                        "--replacement-file", r])

def _live_backup():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", r])
    a2 = os.path.join(d, "a2")
    with open(a2, "w", encoding="utf-8") as fh:
        fh.write("line TWO")
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--anchor-file", a2, "--replacement-file", r])

def _no_backup():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--restore"])

def _identical():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f, "--anchor-file", a, "--replacement-file", a])

def _omitted():
    d, f, a, r = _fresh()
    with redirect_stdout(io.StringIO()):
        return am.main(["apply-mutation.py", f])

# Which arm did each case actually REACH? Counting cases proved nothing: review round 5 found
# a case whose chmod broke the backup WRITE (line 86) rather than the os.replace it named
# (line 99), so 10 sites / 10 cases printed while line 99 was unpinned -- flipping it to APPLIED
# left all 36 checks green. And forcing the platform guard off dropped four cases while the same
# line still read "10 sites, 10 cases", because a hardcoded constant stood in for them: coverage
# nobody measured, which is this tool's own defect class.
#
# So trace execution and assert the SET of refusal lines reached. That subsumes the count, needs
# no constant, and a case that does not reach its arm is a failure rather than a tally.
_AM_FILE = os.path.join(HERE, "apply-mutation.py")
def _refusal_lines(path: str) -> set[int]:
    """Line numbers of every `return` in `path` whose value can be REFUSED.

    A text scan for "return REFUSED" was the first version and it false-negatives on an arm
    spelled `code = REFUSED; return code` -- an ordinary refactor away, and the failure is a
    silent GREEN: the arm leaves the population, so nothing reports it unpinned (#4316, review
    round 6). It also false-POSITIVES on the phrase inside a comment, which makes the tool's
    prose load-bearing for this test.

    The AST answers both: it sees values, not text, and never looks inside a comment. Bare
    `return REFUSED` and a name bound to REFUSED in the same function both count.
    """
    import ast
    tree = ast.parse(open(path, encoding="utf-8").read())
    lines: set[int] = set()
    for fn in ast.walk(tree):
        if not isinstance(fn, (ast.FunctionDef, ast.AsyncFunctionDef)):
            continue
        # Names assigned REFUSED anywhere in this function, so `code = REFUSED; return code`
        # is found the same as the direct spelling.
        aliases = {
            tgt.id
            for node in ast.walk(fn) if isinstance(node, ast.Assign)
            for tgt in node.targets
            if isinstance(tgt, ast.Name)
            and isinstance(node.value, ast.Name) and node.value.id == "REFUSED"
        }
        for node in ast.walk(fn):
            if not isinstance(node, ast.Return) or node.value is None:
                continue
            v = node.value
            # `return REFUSED` and `return REFUSED, "..."` both count.
            heads = v.elts[:1] if isinstance(v, ast.Tuple) and v.elts else [v]
            for h in heads:
                if isinstance(h, ast.Name) and (h.id == "REFUSED" or h.id in aliases):
                    lines.add(node.lineno)
    if not lines:
        raise SystemExit(f"{path}: no REFUSED return found — the scan is broken, not the tool")
    return lines


_REFUSAL_LINES = _refusal_lines(_AM_FILE)

def _lines_hit(fn):
    """Run fn under a tracer; return the refusal lines in apply-mutation.py it executed.

    A line number alone does not identify a line: `_REFUSAL_LINES` holds bare integers, and the
    standard library executes every one of those numbers in its own files. Review round 7 measured
    58 distinct (file, line) collisions covering all ten arms -- `tokenize.py` hits one of them 93
    times -- so a tracer that recorded `frame.f_lineno` without checking WHICH FILE the frame
    belongs to could credit an arm to `tempfile.py`. Deleting the file filter left all 51 checks
    green at 10/10, and a case removed from the roster entirely still read as covered.

    So the recorded key is (realpath, lineno) and the file identity is part of the datum rather
    than a filter in front of it. A filter can be deleted and the data still look right; a key
    cannot.
    """
    hit: set[tuple[str, int]] = set()
    real = os.path.realpath(_AM_FILE)

    def tracer(frame, event, arg):
        if event == "line" and frame.f_lineno in _REFUSAL_LINES:
            hit.add((os.path.realpath(frame.f_code.co_filename), frame.f_lineno))
        return tracer

    old = sys.gettrace()
    sys.settrace(tracer)
    try:
        rc = fn()
    finally:
        sys.settrace(old)
    # Only lines in apply-mutation.py itself are refusal arms; a same-numbered line in the
    # stdlib is a different line that happens to share an integer.
    return rc, {n for f, n in hit if f == real}

# The tracer is the instrument, so assert it before trusting anything it says. Review round 6
# faked it with `return rc, set(_REFUSAL_LINES)` -- a constant standing in for the measurement --
# and the suite stayed green 47/47 printing "10/10", which let round 5's defect back in
# unnoticed. Both directions are needed and the SECOND is the one a constant cannot satisfy: a
# call that refuses WITHOUT reaching an arm must trace to the EMPTY set.
_probe_dir = tempfile.mkdtemp()
_probe_file = os.path.join(_probe_dir, "s.cs")
with open(_probe_file, "w", encoding="utf-8") as _fh:
    _fh.write("line ONE\n")

_, _probe_hit = _lines_hit(lambda: am.restore(_probe_file))
_no_backup_line = min(_REFUSAL_LINES, key=lambda n: abs(n - 110))
check("the tracer reports the arm a known refusal actually executes",
      len(_probe_hit) == 1, f"traced {sorted(_probe_hit)} for one refusal — expected exactly one arm")
check("...and it is a line that returns REFUSED in apply-mutation.py",
      _probe_hit <= _REFUSAL_LINES, f"{sorted(_probe_hit)} is not a subset of {sorted(_REFUSAL_LINES)}")

_, _empty_hit = _lines_hit(lambda: am.REFUSED)
check("...and a call reaching NO arm traces to the EMPTY set, not to a constant",
      _empty_hit == set(),
      f"traced {sorted(_empty_hit)} for a call that executed no arm — the tracer is reporting "
      f"something other than what ran, so every coverage figure below is meaningless")

# The assertion above falsifies a CONSTANT and nothing else: `lambda: am.REFUSED` is one attribute
# lookup, so it runs almost no Python and no foreign frame exists to be misattributed. Round 7
# showed that gap is live -- a line number alone does not name a line, the stdlib executes every
# one of these integers in its own files, and a tracer keyed on the number alone credited arms to
# tempfile.py. This probe does real FOREIGN work and must still trace to empty: it is the only
# assertion here that can fail when the tracer is accurate about lines and wrong about files.
def _foreign_work():
    import re, tokenize, io as _io
    re.compile(r"(?P<a>x+)|(?P<b>y{2,3})").match("xxx")
    list(tokenize.generate_tokens(_io.StringIO("def f(a, b):\n    return a + b\n").readline))
    return am.REFUSED

_, _foreign_hit = _lines_hit(_foreign_work)
check("...and heavy work in OTHER files traces to empty too, so line numbers are not "
      "confused across files",
      _foreign_hit == set(),
      f"traced {sorted(_foreign_hit)} while executing only stdlib code — those line numbers were "
      f"hit in another file, so the tracer is crediting apply-mutation.py's arms to code that "
      f"never ran them")

# Which `return REFUSED` line does each permission-gated case drive? Read out of apply-mutation.py
# rather than written down: each of these four is the sole `except OSError` handler of one I/O
# block, so the arm is the REFUSED return inside the handler guarding the call the case breaks.
#
# A hand-written map here would be the `+ 4` constant again with a better name -- a number
# asserting coverage nobody measured (#4321 round 5, #4328). This derives it, so an arm that moves
# or splits changes the answer instead of going stale.
def _calls_chmod(fn) -> bool:
    """Does fn actually CALL chmod, rather than merely containing the word?

    A substring test over `inspect.getsource` was the first version and it is satisfied by the
    word in a COMMENT — measured at real root (`unshare --user --map-root-user`): adding
    `# nothing here calls chmod` to a non-permission case and marking it gated went green at 6/10
    with its arm excused untested, which is the exact regression #4328 exists to prevent.

    So parse and look for a call whose callee is named chmod. Comments are not in the AST.
    """
    try:
        src = textwrap.dedent(inspect.getsource(fn))
    except (OSError, TypeError):
        return False
    try:
        tree = ast.parse(src)
    except SyntaxError:
        return False
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call):
            continue
        callee = node.func
        name = callee.attr if isinstance(callee, ast.Attribute) else getattr(callee, "id", None)
        if name == "chmod":
            return True
    return False


def _arms_by_case() -> dict[str, set[int]]:
    """Map each permission-gated case to the refusal lines it would reach, by the call it breaks."""
    src = open(_AM_FILE, encoding="utf-8").read().split("\n")
    def refusal_of(pattern: str) -> set[int]:
        """The `return REFUSED` line whose STATEMENT contains pattern.

        Searching FORWARD from the match is wrong and was the first attempt: a refusal message
        spanning two source lines puts the match below its own `return`, so the forward walk
        lands on the NEXT refusal — for the rollback arm, the success return two lines down.
        Walk backward from the match to the nearest `return REFUSED` at or above it instead.
        """
        for i, line in enumerate(src):
            if pattern in line:
                for j in range(i, max(i - 12, -1), -1):
                    if j + 1 in _REFUSAL_LINES:
                        return {j + 1}
        return set()
    return {
        # reads the target: `with open(path, encoding=...)` in apply()
        "an unreadable target file": refusal_of("could not read {path}"),
        # writes the target: the `except OSError` after the two-write try block
        "an unwritable target file": refusal_of("could not write {path}"),
        # reads the backup during restore
        "an unreadable backup during --restore": refusal_of("could not restore {path}"),
        # os.replace rollback on the byte-identical path
        "a backup that cannot be rolled back": refusal_of("could not be rolled back"),
        # writes the restore stamp after a successful restore (#4343)
        "a restore stamp that cannot be written": refusal_of("could not write the restore stamp"),
    }

_ARMS_BY_CASE = _arms_by_case()

# The backward walk is otherwise UNFALSIFIABLE. It differs from a forward walk on exactly one
# entry — rollback, whose message spans two lines so a forward search lands on the SUCCESS return
# below it — and that entry belongs to the one case NOT in PERMISSION_GATED, so its mapping is
# never consumed and reverting the direction stays green at real root (measured under
# `unshare --user --map-root-user`, #4333 review). Assert the mapping itself, which is the thing
# the direction decides.
for _case, _lines in _ARMS_BY_CASE.items():
    check(f"the arm map resolves {_case} to exactly one refusal line", len(_lines) == 1,
          f"{_case} -> {sorted(_lines)}; a case that maps to no arm excuses nothing and one that "
          f"maps to several excuses too much")
    check(f"...and {_case} maps to a line that RETURNS refused, not one below it",
          _lines <= _REFUSAL_LINES, f"{_case} -> {sorted(_lines)} not in {sorted(_REFUSAL_LINES)}")

# Every excusable arm is an I/O-failure HANDLER by definition — that is what "permissions blocked
# the call" means — so each mapped line must sit inside an `except`. This is what discriminates
# the walk direction: the rollback handler (inside `except OSError`) and the success return two
# lines below it are both refusal lines, and only the handler is under an except.
#
# An earlier version of this check asked whether the mapped line was the lowest refusal line at or
# above itself, which is true of EVERY refusal line and so pinned nothing — it passed the forward
# walk it was written to catch.
_am_src = open(_AM_FILE, encoding="utf-8").read().split("\n")

def _inside_except(line_no: int) -> bool:
    """Is this line in the body of an `except` clause? Walk up past its own continuations."""
    indent = len(_am_src[line_no - 1]) - len(_am_src[line_no - 1].lstrip())
    for j in range(line_no - 2, max(line_no - 12, -1), -1):
        stripped = _am_src[j].strip()
        if not stripped:
            continue
        if len(_am_src[j]) - len(_am_src[j].lstrip()) < indent:
            return stripped.startswith("except")
    return False

for _case, _lines in _ARMS_BY_CASE.items():
    for _ln in _lines:
        check(f"{_case} maps to a refusal inside an `except`, not one merely below it",
              _inside_except(_ln),
              f"{_case} -> line {_ln}, which is not in an except body; a permission-blocked call "
              f"can only be answered by its own handler, so this mapping excuses the wrong arm")

_reached: set[int] = set()
_skipped_arms: set[int] = set()
_gated = {n for n, _ in PERMISSION_GATED}
for _name, _fn in REFUSAL_ARMS:
    if _name in _gated and not _unreadable_ok:
        # A case is excused only if it is genuinely permission-driven. Marking one gated is
        # otherwise a free pass: adding a non-permission case to PERMISSION_GATED excused its arm
        # with nothing objecting (measured while writing this). So require the case to actually
        # use the mechanism the gate is about — a chmod — read out of its own source.
        check(f"{_name} is permission-gated because it CALLS chmod, not merely mentions it",
              _calls_chmod(_fn),
              f"{_name} sits in PERMISSION_GATED but its body makes no chmod CALL, so the gate is "
              f"not what stops it — excusing its arm would hide an untested refusal")
        # Establish WHICH arms this case would have covered, by reading the source rather than
        # by asserting a number: run it where it works and it reaches these lines. Here it cannot
        # run, so those lines are excused -- and only those.
        _skipped_arms |= _ARMS_BY_CASE.get(_name, set())
        check(f"{_name} is skipped for a stated reason, not silently",
              _name in _ARMS_BY_CASE,
              f"{_name} is permission-gated but no arm mapping says which lines it covers, so "
              f"skipping it would excuse nothing and the census would demand the impossible")
        continue
    _rc, _hit = _lines_hit(_fn)
    check(f"REFUSED (exit 3) for {_name}", _rc == am.REFUSED, f"{_name} did not refuse")
    check(f"...and {_name} reaches a refusal arm in apply-mutation.py", bool(_hit),
          f"{_name} refused without executing any `return REFUSED` line — it is not testing "
          f"the arm it names")
    _reached |= _hit

# The population check, keyed on lines REACHED rather than cases declared. A skipped
# platform-conditional case now shows up here as an unreached arm, because nothing stands in
# for it.
_missing = sorted(_REFUSAL_LINES - _reached - _skipped_arms)
check(f"every `return REFUSED` arm is reached by a case ({len(_reached)}/{len(_REFUSAL_LINES)})",
      not _missing,
      f"apply-mutation.py line(s) {_missing} return REFUSED and no case above executes them. "
      f"Add a case that reaches the arm — not one that merely refuses, and never a constant "
      f"standing in for a skipped case.")

print("--help works, and the file stays executable")
with redirect_stdout(io.StringIO()) as _out:
    _help_rc = am.main(["apply-mutation.py", "--help"])
check("--help exits 0 with no file argument — asking for help is not a refusal",
      _help_rc == am.APPLIED, f"exit {_help_rc}")
check("...and it prints the usage", "--anchor-file" in _out.getvalue(), _out.getvalue()[:80])

# The tdd.md recipe invokes this as `tools/apply-mutation.py <file>`, so the exec bit is part of
# the documented interface. It was lost once in this PR's own history -- a manual `mv` recovery
# committed 100644 -- and nothing caught it: every test imports the module, which works either
# way, so the only symptom was `Permission denied` from the documented command (round 7).
# Limit, measured in review rather than assumed: os.access reads the WORKING TREE while the
# defect lives in the index, so a repository with core.fileMode=false plus
# `update-index --chmod=-x` commits 100644 with this check still passing. It catches that one
# clone downstream instead of at the introducing commit. An index-based check would catch it
# earlier but needs a git repository to answer at all, trading a property that always holds for
# a new "could not measure" state -- the wrong trade for this guard.
check("the tool is executable, as the documented recipe invokes it",
      os.access(_AM_FILE, os.X_OK),
      f"{_AM_FILE} is not executable — `tools/apply-mutation.py <file>` fails with Permission "
      f"denied, though importing it still works, so nothing else here would notice")

print("the backup suffix is the one .gitignore actually excludes")
# SUFFIX is a cross-file contract: .gitignore carries the literal, so renaming SUFFIX leaves
# every test green AND starts committing backups -- a pre-mutation copy of a source file in the
# history, and a stranded backup that makes --restore refuse later (#4316, review round 6).
_gitignore = os.path.join(os.path.dirname(HERE), ".gitignore")
_ignored = open(_gitignore, encoding="utf-8").read() if os.path.exists(_gitignore) else ""
check("`*<SUFFIX>` is an ignore rule, so a backup is never committed",
      f"*{am.SUFFIX}" in _ignored,
      f"SUFFIX is {am.SUFFIX!r} and .gitignore has no `*{am.SUFFIX}` line — renaming one without "
      f"the other commits the backup")

print("the three codes are distinct")
check("APPLIED, NOT-APPLIED and AMBIGUOUS are three different values",
      len({am.APPLIED, am.NOT_APPLIED, am.AMBIGUOUS}) == 3,
      f"{am.APPLIED}/{am.NOT_APPLIED}/{am.AMBIGUOUS}")

print("restore")
d = tempfile.mkdtemp()
p = os.path.join(d, "s.cs")
with open(p, "w", encoding="utf-8") as fh:
    fh.write("original\n")
a, r = os.path.join(d, "a"), os.path.join(d, "r")
for f, s in ((a, "original"), (r, "mutated")):
    with open(f, "w", encoding="utf-8") as fh:
        fh.write(s)
with redirect_stdout(io.StringIO()):
    am.main(["apply-mutation.py", p, "--anchor-file", a, "--replacement-file", r])
with redirect_stdout(io.StringIO()) as out:
    rc = am.main(["apply-mutation.py", p, "--restore"])
with open(p, encoding="utf-8") as fh:
    restored = fh.read()
check("--restore puts the original back", rc == am.APPLIED and restored == "original\n", restored)
check("...and removes the backup, so a stale one cannot restore the wrong revision later",
      not os.path.exists(p + am.SUFFIX))
with redirect_stdout(io.StringIO()) as out:
    rc = am.main(["apply-mutation.py", p, "--restore"])
check("--restore with no backup refuses rather than reporting success", rc == am.REFUSED, out.getvalue())

print("the restore stamp (#4343)")
# --restore records when it happened so mutation-verdict.py can refuse a verdict from a binary
# built before it. Without the stamp, `--no-build` after a restore re-measures the mutant and
# reds the same arm every time -- deterministic, narrow, and about nothing.
d = tempfile.mkdtemp()
os.mkdir(os.path.join(d, ".git"))  # a repo root, which is where the stamp belongs
p = os.path.join(d, "s.cs")
a, r = os.path.join(d, "a"), os.path.join(d, "r")
for f, body in ((p, "original\n"), (a, "original"), (r, "mutated")):
    with open(f, "w", encoding="utf-8") as fh:
        fh.write(body)
with redirect_stdout(io.StringIO()):
    am.main(["apply-mutation.py", p, "--anchor-file", a, "--replacement-file", r])
before = time.time()
with redirect_stdout(io.StringIO()) as out:
    rc = am.main(["apply-mutation.py", p, "--restore"])
stamp = os.path.join(d, am.STAMP_NAME)
check("--restore writes the stamp at the repository root, not beside the file",
      rc == am.APPLIED and os.path.exists(stamp),
      f"rc={rc}, expected {stamp}, dir holds {sorted(os.listdir(d))}")
check("...and it carries a parseable time no earlier than the restore",
      float(open(stamp, encoding="utf-8").readline()) >= before - 1,
      open(stamp, encoding="utf-8").read())
check("...and the success message tells the reader to REBUILD",
      "REBUILD" in out.getvalue(), out.getvalue())

# A worktree's `.git` is a FILE, and every agent here works in one. Resolving the root with
# isdir would fall through to the per-file branch for every real caller, so the stamp would
# land in AlRunner/Patches/ while mutation-verdict.py looked at the repository root.
d2 = tempfile.mkdtemp()
with open(os.path.join(d2, ".git"), "w", encoding="utf-8") as fh:
    fh.write("gitdir: /elsewhere/.git/worktrees/x\n")
os.mkdir(os.path.join(d2, "sub"))
check("a worktree's `.git` FILE resolves the root just as a directory does",
      am.stamp_path(os.path.join(d2, "sub", "x.cs")) == os.path.join(d2, am.STAMP_NAME),
      am.stamp_path(os.path.join(d2, "sub", "x.cs")))

# The REFUSED arm: the restore succeeded but the protection the caller is about to rely on is
# absent. Reporting APPLIED here would read as "safe to re-run" on a box where nothing can
# detect the stale binary (guards-need-a-third-state.md).
d3 = tempfile.mkdtemp()
os.mkdir(os.path.join(d3, ".git"))
p3 = os.path.join(d3, "s.cs")
a3, r3 = os.path.join(d3, "a"), os.path.join(d3, "r")
for f, body in ((p3, "original\n"), (a3, "original"), (r3, "mutated")):
    with open(f, "w", encoding="utf-8") as fh:
        fh.write(body)
with redirect_stdout(io.StringIO()):
    am.main(["apply-mutation.py", p3, "--anchor-file", a3, "--replacement-file", r3])
# A DIRECTORY at the stamp path makes the open() fail without touching permissions, so this
# case is reached as root too -- where a chmod 0o500 would not refuse anything.
os.mkdir(os.path.join(d3, am.STAMP_NAME))
with redirect_stdout(io.StringIO()) as out:
    rc = am.main(["apply-mutation.py", p3, "--restore"])
check("a restore whose stamp cannot be written REFUSES rather than reporting success",
      rc == am.REFUSED, f"got {rc}: {out.getvalue()}")
check("...and says the source is restored but the binary is not, so nothing is silently lost",
      "SOURCE is correct" in out.getvalue() and "BINARY still carries" in out.getvalue(),
      out.getvalue())
with open(p3, encoding="utf-8") as fh:
    check("...and the file really WAS restored, so the refusal is about the stamp alone",
          fh.read() == "original\n", fh.read())

if FAILURES:
    print(f"\n{len(FAILURES)} failed, {0} passed")
    sys.exit(1)
print("\nall checks passed")
