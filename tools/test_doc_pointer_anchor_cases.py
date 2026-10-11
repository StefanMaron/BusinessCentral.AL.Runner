#!/usr/bin/env python3
"""Executed cases for the doc-pointer guard's anchor resolution (#4480).

tools/test_doc_pointers.py passes on today's tree, which says nothing about whether it
WOULD fail on a dangling `docs/<file>.md#<anchor>` citation in a .cs comment, or whether
it over-refuses a correct one. Each case here builds a tiny throwaway tree, points the
guard at it and reads what the guard reports, so both directions are pinned:

  * a dangling citation reds -- in AlRunner/ AND in AlRunner.Tests/ (the location a
    citation lives in is never what exempts it, #4480);
  * a missing DOCUMENT and a present document with a missing ANCHOR are reported as
    different things, because they send a reader to different fixes;
  * a correct citation passes whatever the heading looks like once GitHub has slugged
    it: punctuation, inline code, a duplicate heading's `-1` suffix, an `<a name>` that
    precedes the heading text. The "green control" half -- a guard that reds every
    unusual heading is trained out of by its readers.

The expected slugs below are GitHub's, written out by hand, not computed by the code under
test.

Run: python3 tools/test_doc_pointer_anchor_cases.py
"""
from __future__ import annotations

import contextlib
import io
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import test_doc_pointers as g  # noqa: E402  (the guard under test)

DOC = """\
# Title

## Plain heading

## Heading, with: punctuation (and parens) #123

### `Code` span heading

## Repeated

## Repeated

## <a name="explicit-name"></a>Text after an explicit anchor

```bash
# Not a heading: this sits inside a fence
```

## [Link text](https://example.com/x) heading

## The page `<Methods>` subtree

## Closed heading ##

## See [the page](https://example.com) and `<X>` here

## Email <a id="email-anchor"></a>

## Order matters #<a id="order-anchor"></a>

#nospace heading text

    # indented code line

```text
~~~
# Fenced after the wrong closer
```

## After the fences
"""

# The throwaway trees cite a doc directory spelled in two pieces: this file is itself under
# the guard's scan, and a literal path here would be read as a pointer to follow.
D = "doc" + "s/"
DOC_D = D + "d.md"

FAILS: list[str] = []


def run_guard(files: dict[str, str]) -> tuple[bool, str]:
    """Run check_code_pointers over a throwaway tree. True = the guard passed."""
    with tempfile.TemporaryDirectory() as root:
        for path, text in files.items():
            full = os.path.join(root, path)
            os.makedirs(os.path.dirname(full), exist_ok=True)
            with open(full, "w", encoding="utf-8") as fh:
                fh.write(text)
        saved = (g.ROOT, g.FAILURES[:])
        g.ROOT = root
        g.FAILURES.clear()
        g._ANCHOR_CACHE.clear()
        buf = io.StringIO()
        try:
            with contextlib.redirect_stdout(buf):
                g.check_code_pointers()
            return (not g.FAILURES), buf.getvalue()
        finally:
            g.ROOT = saved[0]
            g.FAILURES[:] = saved[1]
            g._ANCHOR_CACHE.clear()


def expect(name: str, ok: bool, files: dict[str, str], message: str = "") -> None:
    passed, out = run_guard(files)
    verdict_ok = passed == ok and (message in out)
    print(f"  {'ok  ' if verdict_ok else 'FAIL'} {name}")
    if not verdict_ok:
        print(f"         wanted guard {'pass' if ok else 'FAIL'}"
              f"{' containing ' + repr(message) if message else ''}; got:")
        for line in out.splitlines():
            print(f"           {line}")
        FAILS.append(name)


def cite(anchor: str, tree: str = "AlRunner/Thing.cs", doc: str = DOC_D) -> dict[str, str]:
    return {doc: DOC, tree: f"// see {doc}#{anchor}\nclass Thing {{}}\n"}


def main() -> int:
    print("doc-pointer anchor cases")

    # --- the guard must RED on a dangling citation, wherever it is written --------
    expect("dangling anchor in AlRunner/ reds", False, cite("no-such-section"),
           "anchor #no-such-section is not in that file")
    expect("dangling anchor in AlRunner.Tests/ reds (a test file is not exempt)", False,
           cite("no-such-section", tree="AlRunner.Tests/ThingTests.cs"),
           "anchor #no-such-section is not in that file")
    expect("missing DOCUMENT is reported as a missing document, not a missing anchor", False,
           {"AlRunner/Thing.cs": f"// see {D}gone.md#whatever\n"},
           "file does not exist")

    # --- the guard must PASS a correct citation, however unusual the heading ------
    expect("plain heading slug", True, cite("plain-heading"))
    expect("punctuation is dropped, the parenthesised number kept",
           True, cite("heading-with-punctuation-and-parens-123"))
    expect("inline code span heading", True, cite("code-span-heading"))
    expect("first of two identical headings keeps the bare slug", True, cite("repeated"))
    expect("second of two identical headings is slug-1", True, cite("repeated-1"))
    expect("a third occurrence does not exist", False, cite("repeated-2"),
           "anchor #repeated-2 is not in that file")
    expect("explicit <a name> resolves", True, cite("explicit-name"))
    expect("a heading led by an <a name> slugs from its visible text", True,
           cite("text-after-an-explicit-anchor"))
    expect("link syntax in a heading slugs from the link text", True,
           cite("link-text-heading"))
    expect("a # line inside a code fence is not a heading", False,
           cite("not-a-heading-this-sits-inside-a-fence"),
           "is not in that file")

    # --- the rules added with the fixes, one case each ----------------------------
    expect("a code span keeps its text, tags included (GitHub: methods)", True,
           cite("the-page-methods-subtree"))
    expect("the tag-stripped slug of a code span is not an anchor", False,
           cite("the-page--subtree"), "anchor #the-page--subtree is not in that file")
    expect("a link BEFORE a code span is still reduced to its text", True,
           cite("see-the-page-and-x-here"))
    expect("closing #s are not part of the heading", True, cite("closed-heading"))
    expect("a closing-# slug with a trailing hyphen is not an anchor", False,
           cite("closed-heading-"), "is not in that file")
    expect("the space before a trailing <a id> survives: trailing hyphen", True,
           cite("email-"))
    expect("so the hyphen-less slug is not an anchor", False, cite("email"),
           "anchor #email is not in that file")
    expect("a # before a trailing tag is not a closing sequence (tags go after it)", True,
           cite("order-matters-"))
    expect("so stripping tags first would wrongly give the bare slug", False,
           cite("order-matters"), "anchor #order-matters is not in that file")
    expect("#word without a space is not a heading", False,
           cite("nospace-heading-text"), "is not in that file")
    expect("a 4-space-indented # is code, not a heading", False,
           cite("indented-code-line"), "is not in that file")
    expect("a fence opened with backticks is not closed by ~~~", False,
           cite("fenced-after-the-wrong-closer"), "is not in that file")
    expect("the heading after the fences is still found", True,
           cite("after-the-fences"))

    # --- the escape hatch is keyed on the marker, never on the location ------------
    expect("a marked fixture path is exempt", True,
           {"AlRunner.Tests/T.cs": f"x(\"{D}nope.md#y\"); // {g.NOT_A_POINTER}\n"
                                   f"// {DOC_D}#plain-heading\n",
            DOC_D: DOC})
    expect("an UNMARKED dangling path in the same test file still reds", False,
           {"AlRunner.Tests/T.cs": f"x(\"{D}nope.md#y\");\n", DOC_D: DOC},
           "file does not exist")

    if FAILS:
        print(f"\n{len(FAILS)} case(s) failed: {'; '.join(FAILS)}")
        return 1
    print("\nall doc-pointer anchor cases passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
