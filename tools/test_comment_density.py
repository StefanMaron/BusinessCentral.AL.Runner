#!/usr/bin/env python3
"""Unit tests for tools/comment-density.py's line classifier.

The classifier is the whole tool: every number #3260 quotes, and every
before/after a reduction pass reports, is whatever this function decided. A
classifier that miscounts in a stable direction produces a self-consistent set
of wrong figures and nothing contradicts it -- which is exactly the shape
`verify-execution-not-the-tick.md` warns about, so the fixtures below are the
cases where a naive `line.strip().startswith("//")` gets a DIFFERENT answer
from this one.

Run: python3 tools/test_comment_density.py
"""
from __future__ import annotations

import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "comment_density", os.path.join(HERE, "comment-density.py"))
cd = importlib.util.module_from_spec(_spec)
# Registered before exec: @dataclass resolves its own module out of sys.modules,
# and raises AttributeError on a module that is not in there yet.
sys.modules["comment_density"] = cd
_spec.loader.exec_module(cd)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def counts(src: str):
    return cd.classify(src)


def expect(name: str, src: str, *, comment: int, code: int, blank: int) -> None:
    c = counts(src)
    check(name,
          (c.comment, c.code, c.blank) == (comment, code, blank),
          f"got comment={c.comment} code={c.code} blank={c.blank}, "
          f"want comment={comment} code={code} blank={blank}")


print("line kinds")

expect("a // line is a comment", "// hello\n", comment=1, code=0, blank=0)
expect("a /// doc line is a comment", "/// <summary>x</summary>\n",
       comment=1, code=0, blank=0)
expect("a plain statement is code", "var x = 1;\n", comment=0, code=1, blank=0)
expect("whitespace is blank", "   \n\t\n", comment=0, code=0, blank=2)

print()
print("a trailing comment counts as CODE, not comment")
# Deliberate and conservative: it under-counts comment mass, so a measured
# reduction is never flattered by the classifier.
expect("code with a trailing //", "var x = 1; // why\n", comment=0, code=1, blank=0)
expect("code with a trailing /* */", "var x = 1; /* why */\n",
       comment=0, code=1, blank=0)

print()
print("block comments")
expect("a whole /* */ block is comment",
       "/* one\n   two\n   three */\n", comment=3, code=0, blank=0)
expect("a blank line INSIDE a block is comment, not blank",
       "/* one\n\n   three */\n", comment=3, code=0, blank=0)
expect("code after a block ends on the same line is code",
       "/* x */ var y = 2;\n", comment=0, code=1, blank=0)
# A line holding only comments -- even two of them -- is a comment line; the
# second block then carries over. Written out because the naive expectation is
# "reopening implies code", and it does not.
expect("a line of two blocks is comment, and the second carries over",
       "/* a */ /* b\n   c */\n", comment=2, code=0, blank=0)
expect("...but real code before the reopened block makes it a code line",
       "var x = 1; /* a */ /* b\n   c */\n", comment=1, code=1, blank=0)

print()
print("// and /* inside a literal do not open a comment")
# This is the case a naive scanner gets wrong, and it is common in this
# repository: refusal messages, doc-link constants, regex patterns.
expect("a // in a string is code",
       'var s = "// not a comment";\n', comment=0, code=1, blank=0)
expect("a /* in a string is code",
       'var s = "/* not a comment";\n', comment=0, code=1, blank=0)
expect("a doc-link constant is code, and does not swallow the next line",
       'const string D = "docs/limitations.md#anchor";\nvar y = 2;\n',
       comment=0, code=2, blank=0)
expect("a verbatim @\"...\" containing // is code",
       'var s = @"C:\\\\a//b";\nvar y = 2;\n', comment=0, code=2, blank=0)
expect("a doubled quote inside a verbatim string does not end it",
       'var s = @"say ""// hi"" ok";\nvar y = 2;\n', comment=0, code=2, blank=0)
expect("an escaped quote does not end a regular string",
       'var s = "a\\" // still string";\nvar y = 2;\n', comment=0, code=2, blank=0)
expect("a // in a char literal is code",
       "var c = '/';\nvar y = 2;\n", comment=0, code=2, blank=0)

print()
print("share is over NON-BLANK lines")
c = counts("// a\n// b\nvar x = 1;\n\n\n")
check("blank lines are excluded from the share",
      abs(c.share - (2 / 3 * 100)) < 1e-9, f"got {c.share}")
check("an all-blank file reports 0.0 rather than dividing by zero",
      counts("\n\n\n").share == 0.0, str(counts("\n\n\n").share))

print()
print("the walker skips build output")
check("obj/ and bin/ are skipped", {"obj", "bin"} <= cd.SKIP_DIRS, str(cd.SKIP_DIRS))
check("graphify-out is skipped too", "graphify-out" in cd.SKIP_DIRS, str(cd.SKIP_DIRS))

print()
print("regression: the real pilot files reproduce their reported figures")
# Guards the reduction claim in the PR for #3260 against a later classifier
# change silently restating it. Anchored to the file, not to a literal, so it
# tracks further reduction instead of pinning one moment.
REPO = os.path.dirname(HERE)
for rel, ceiling in [
    ("AlRunner/Patches/BlobStoreIsolationPatches.cs", 60.0),
    ("AlRunner/Patches/RecordPatches.ObjectMetadataSystemTable.cs", 60.0),
    ("AlRunner/Infrastructure/AlCoverageTracker.cs", 60.0),
]:
    path = os.path.join(REPO, rel)
    if not os.path.exists(path):
        check(f"{rel} exists", False, "file missing")
        continue
    with open(path, encoding="utf-8") as fh:
        share = cd.classify(fh.read()).share
    check(f"{os.path.basename(rel)} stays under {ceiling}% comment",
          share < ceiling, f"got {share:.1f}%")

print()
if FAILURES:
    print(f"FAILED: {len(FAILURES)} check(s): {', '.join(FAILURES)}")
    sys.exit(1)
print("all checks passed")
