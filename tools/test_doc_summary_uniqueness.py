#!/usr/bin/env python3
"""One <summary> per doc-comment block in AlRunner/**/*.cs (#3827).

A contiguous run of `///` lines documents exactly one member, so it carries at
most one <summary>. Two means a summary was stranded: an extraction inserted
the new member's doc before the anchor without moving the old one, leaving the
member it was written for undocumented and the reader hitting the wrong
description first. Malformed doc XML that nothing else catches, because the
build emits no doc XML -- so 40 blocks across 32 files accumulated unnoticed,
one of them appearing during PR #3826.

Counts <summary> only. A block with <summary> + <param> + <returns> is correct
doc XML and is not this defect.

The measurement can fail to happen four ways, and each is a verdict distinct
from the success state (guards-need-a-third-state.md):

  * AlRunner/ absent               -> exit 3, cannot measure
  * no .cs files found under it    -> exit 3, a scan of nothing is not "clean"
  * a file unreadable              -> exit 3, naming it
  * no `///` blocks found at all   -> exit 3, the scanner matched nothing

A genuinely absent AlRunner/ is deliberately NOT a pass here: unlike an
optional declaration, this repository's whole subject is that directory, so its
absence means the check ran somewhere it cannot answer, not that there is
nothing to answer.

Run: python3 tools/test_doc_summary_uniqueness.py
"""
from __future__ import annotations

import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCAN_DIR = os.path.join(ROOT, "AlRunner")
SKIP_DIRS = {"bin", "obj", "graphify-out", ".git"}

EXIT_OK = 0
EXIT_OFFENDERS = 1
EXIT_CANNOT_MEASURE = 3


class CannotMeasure(Exception):
    """The scan could not be performed -- distinct from "the scan found nothing"."""


def blocks(lines: list[str]):
    """Yield (start_line, end_line, summary_count) for each contiguous /// run.

    Line numbers are 1-based and inclusive. A blank line or any non-/// line
    ends the run, which is what makes a run correspond to one member.
    """
    start = None
    count = 0
    for i, line in enumerate(lines):
        if line.lstrip().startswith("///"):
            if start is None:
                start = i
                count = 0
            count += line.count("<summary>")
        elif start is not None:
            yield (start + 1, i, count)
            start = None
    if start is not None:
        yield (start + 1, len(lines), count)


def cs_files(scan_dir: str) -> list[str]:
    if not os.path.isdir(scan_dir):
        raise CannotMeasure(
            f"{os.path.relpath(scan_dir, ROOT)} is not a directory -- "
            "nothing to scan, so this is not a clean result")
    found = []
    for dirpath, dirnames, filenames in os.walk(scan_dir):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in filenames:
            if name.endswith(".cs"):
                found.append(os.path.join(dirpath, name))
    if not found:
        raise CannotMeasure(
            f"no .cs files under {os.path.relpath(scan_dir, ROOT)} "
            "(excluding bin/ and obj/) -- a scan of zero files is not 'clean'")
    return sorted(found)


def scan(scan_dir: str = SCAN_DIR):
    """Return (offenders, files_scanned, doc_blocks_seen).

    Raises CannotMeasure when the measurement could not be performed at all.
    """
    offenders = []
    seen_blocks = 0
    files = cs_files(scan_dir)
    for path in files:
        try:
            with open(path, encoding="utf-8") as fh:
                lines = fh.read().splitlines()
        except OSError as exc:
            raise CannotMeasure(
                f"cannot read {os.path.relpath(path, ROOT)}: {exc}") from exc
        except UnicodeDecodeError as exc:
            raise CannotMeasure(
                f"cannot decode {os.path.relpath(path, ROOT)} as utf-8: {exc}") from exc
        for start, end, count in blocks(lines):
            seen_blocks += 1
            if count >= 2:
                offenders.append(
                    (os.path.relpath(path, ROOT).replace(os.sep, "/"),
                     start, end, count))
    if seen_blocks == 0:
        raise CannotMeasure(
            f"scanned {len(files)} file(s) and found no /// doc block at all -- "
            "the scanner matched nothing, which is not the same as finding no defect")
    return offenders, len(files), seen_blocks


# ---------------------------------------------------------------------------
# self-tests: the third state has to be proven to fire, or it is
# indistinguishable from a path that never fires (guards-need-a-third-state.md,
# point 5).
# ---------------------------------------------------------------------------

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name} {detail}")
        FAILURES.append(name)


def _counts(src: str) -> list[int]:
    return [c for _s, _e, c in blocks(src.splitlines())]


def self_tests() -> None:
    print("the block splitter")
    check("one /// run is one block",
          _counts("/// <summary>a</summary>\nvoid M();\n") == [1],
          str(_counts("/// <summary>a</summary>\nvoid M();\n")))
    check("a blank line separates two blocks",
          _counts("/// <summary>a</summary>\n\n/// <summary>b</summary>\n") == [1, 1],
          str(_counts("/// <summary>a</summary>\n\n/// <summary>b</summary>\n")))
    check("a code line separates two blocks",
          _counts("/// <summary>a</summary>\nvoid M();\n/// <summary>b</summary>\n") == [1, 1],
          str(_counts("/// <summary>a</summary>\nvoid M();\n/// <summary>b</summary>\n")))
    check("the defect: two summaries in ONE run counts 2",
          _counts("/// <summary>a</summary>\n/// <summary>b</summary>\nvoid M();\n") == [2],
          str(_counts("/// <summary>a</summary>\n/// <summary>b</summary>\nvoid M();\n")))
    check("a run ending at EOF is still emitted",
          _counts("/// <summary>a</summary>\n/// <summary>b</summary>\n") == [2],
          str(_counts("/// <summary>a</summary>\n/// <summary>b</summary>\n")))
    check("indented /// counts",
          _counts("    /// <summary>a</summary>\n    /// <summary>b</summary>\n") == [2],
          str(_counts("    /// <summary>a</summary>\n    /// <summary>b</summary>\n")))

    print()
    print("what the guard must NOT flag")
    ok_multi = ("/// <summary>Does a thing.</summary>\n"
                "/// <param name=\"x\">the x</param>\n"
                "/// <returns>a y</returns>\n")
    check("summary + param + returns is correct doc XML, count 1",
          _counts(ok_multi) == [1], str(_counts(ok_multi)))
    check("a // comment is not a doc block",
          _counts("// <summary>a</summary>\n// <summary>b</summary>\n") == [],
          str(_counts("// <summary>a</summary>\n// <summary>b</summary>\n")))
    check("a block with no summary at all is count 0, not an offender",
          _counts("/// <inheritdoc/>\nvoid M();\n") == [0],
          str(_counts("/// <inheritdoc/>\nvoid M();\n")))

    print()
    print("the third state fires (each is a verdict, not a pass)")
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        missing = os.path.join(tmp, "does-not-exist")
        try:
            scan(missing)
            check("an absent scan directory refuses", False, "returned a result")
        except CannotMeasure as exc:
            check("an absent scan directory refuses", True)
            check("...and its message names the cause",
                  "nothing to scan" in str(exc), str(exc))

        empty = os.path.join(tmp, "empty")
        os.makedirs(os.path.join(empty, "obj"))
        with open(os.path.join(empty, "obj", "Generated.cs"), "w") as fh:
            fh.write("/// <summary>a</summary>\n/// <summary>b</summary>\n")
        try:
            scan(empty)
            check("a directory whose only .cs files are under obj/ refuses",
                  False, "returned a result -- bin/obj exclusion made the scan vacuous")
        except CannotMeasure as exc:
            check("a directory whose only .cs files are under obj/ refuses", True)
            check("...and says a scan of zero files is not clean",
                  "not 'clean'" in str(exc), str(exc))

        nodocs = os.path.join(tmp, "nodocs")
        os.makedirs(nodocs)
        with open(os.path.join(nodocs, "A.cs"), "w") as fh:
            fh.write("class A { }\n")
        try:
            scan(nodocs)
            check("a tree with .cs files but no /// block at all refuses",
                  False, "returned a result")
        except CannotMeasure as exc:
            check("a tree with .cs files but no /// block at all refuses", True)
            check("...and distinguishes 'matched nothing' from 'found no defect'",
                  "matched nothing" in str(exc), str(exc))

        unreadable = os.path.join(tmp, "unreadable")
        os.makedirs(unreadable)
        with open(os.path.join(unreadable, "Ok.cs"), "w") as fh:
            fh.write("/// <summary>a</summary>\nclass A { }\n")
        bad = os.path.join(unreadable, "Bad.cs")
        with open(bad, "wb") as fh:
            fh.write(b"/// <summary>\xff\xfe not utf-8 </summary>\n")
        try:
            scan(unreadable)
            check("a file that cannot be decoded refuses", False, "returned a result")
        except CannotMeasure as exc:
            check("a file that cannot be decoded refuses", True)
            check("...and names the file it could not read",
                  "Bad.cs" in str(exc), str(exc))

        # And the positive control: a well-formed tree still measures.
        good = os.path.join(tmp, "good")
        os.makedirs(good)
        with open(os.path.join(good, "A.cs"), "w") as fh:
            fh.write("/// <summary>a</summary>\n/// <summary>b</summary>\nvoid M();\n"
                     "\n/// <summary>c</summary>\nvoid N();\n")
        offenders, nfiles, nblocks = scan(good)
        check("a measurable tree reports its offender rather than refusing",
              (len(offenders), nfiles, nblocks) == (1, 1, 2),
              f"offenders={offenders} files={nfiles} blocks={nblocks}")


def main() -> int:
    print("== self-tests ==")
    self_tests()
    if FAILURES:
        print()
        print(f"FAILED: {len(FAILURES)} self-test(s): {', '.join(FAILURES)}")
        print("The guard's own behaviour is wrong, so its verdict on the tree "
              "means nothing.")
        return EXIT_OFFENDERS

    print()
    print("== AlRunner/**/*.cs ==")
    try:
        offenders, nfiles, nblocks = scan()
    except CannotMeasure as exc:
        print(f"  CANNOT MEASURE: {exc}")
        print("exit 3: the scan did not happen. This is not a pass.")
        return EXIT_CANNOT_MEASURE

    if offenders:
        print(f"  FAIL {len(offenders)} doc block(s) carry two or more <summary> "
              f"elements ({nfiles} files, {nblocks} doc blocks scanned):")
        for path, start, end, count in offenders:
            print(f"         {path}:{start}-{end}  ({count} <summary>)")
        print()
        print("Each contiguous /// run documents ONE member. A second <summary> is "
              "a doc stranded from the member it was written for -- move it back "
              "onto that member; do not delete it to satisfy this check.")
        return EXIT_OFFENDERS

    print(f"  ok   no doc block carries two <summary> elements "
          f"({nfiles} files, {nblocks} doc blocks)")
    print()
    print("all checks passed")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
