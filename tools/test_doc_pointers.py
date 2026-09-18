#!/usr/bin/env python3
"""Doc-pointer guard: every pointer INTO the documentation resolves (#3422).

A docs-only pull request skips the BC matrix (#3421), and with it the C# suite
that used to hold these checks (ProseRelocationPointerTests, #3402 / #3260).
pr-gate.yml runs every tools/test_*.py on every pull request, so the pointer
checks live here now and gate the day a doc changes.

Four surfaces, one question each -- does the target exist right now:

  1. `docs/<file>.md[#anchor]` mentions in every code tree -- CODE_TREES below,
     not AlRunner/ alone (#4322) -- comments and string literals alike; a refusal
     message carrying a doc link rots too. A line carrying a path that is a test
     FIXTURE rather than a pointer says so with `doc-pointer-fixture`, and a
     second check fails any such mark whose path actually resolves.
  2. Markdown links `[..](path[#anchor])` in docs/, .claude/, README.md, CLAUDE.md.
  3. ANCHORED bare mentions `docs/<file>.md#anchor` in those same markdown files.
     Unanchored bare mentions are deliberately not checked: prose narrates
     history (an archived coverage doc is cited as living under docs/archive/)
     and a path
     check there needs an allowlist, which is the failure mode this suite
     refuses. An #anchor is a claim about a section that exists now.
  4. `Doc` values in tests/expectations/**/*.json, the pointers a reader of a
     divergence entry follows.

Plus the #3260 relocation pairs: a doc created to hold one file's derivation
is still pointed at by that file. That list is a different mechanism from the
population glob above and is deliberately left alone by #4322: it asserts a
NAMED doc/source pair, so an entry is added when a derivation is moved out of a
file, not when a tree becomes scannable. No test-tree file has had a derivation
relocated out of it, so it gains no entry here; surface 1 now covers the test
tree's pointers, which is what #4322 was about.

Every surface must be non-empty, so a regex that drifts to match nothing
fails instead of passing vacuously.

And the POPULATION is asserted too (#4367), not just the pointers in it: deleting a
CODE_TREES entry used to take the scan from 7 trees to 6 and 713 pointers to 482 while
still exiting 0. Three checks pin it, in both directions -- every registered tree
exists and matches files, every tracked source file is in some entry or declared
UNSCANNED, and every UNSCANNED entry still names something. The second is measured
against `git ls-files`, which knows nothing about the tuple, so a tree added and never
registered fails here rather than quietly leaving the population.

Run: python3 tools/test_doc_pointers.py
"""
from __future__ import annotations

import fnmatch
import glob
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

FAILURES: list[str] = []


def check(name: str, offenders: list[str], seen: int, what: str) -> None:
    if seen == 0:
        print(f"  FAIL {name}: matched nothing -- the guard is not guarding anything")
        FAILURES.append(name)
        return
    if offenders:
        print(f"  FAIL {name} ({len(offenders)} of {seen} {what}):")
        for o in sorted(set(offenders)):
            print(f"         {o}")
        FAILURES.append(name)
    else:
        print(f"  ok   {name} ({seen} {what})")


def rel(path: str) -> str:
    return os.path.relpath(path, ROOT).replace(os.sep, "/")


def read(path: str) -> str:
    with open(path, encoding="utf-8") as fh:
        return fh.read()


def is_text(path: str) -> bool:
    """A file this guard can read. Skipping is right ONLY for a non-UTF-8 file.

    Deliberately not a blanket try/except around read(): that would also swallow a
    missing or unreadable file, and "I could not look" must never be spelled as
    "nothing to see" (guards-need-a-third-state.md). Anything other than a decode
    failure propagates.
    """
    try:
        read(path)
        return True
    except UnicodeDecodeError:
        return False


# --- anchors -----------------------------------------------------------------

def slug(heading: str) -> str:
    """GitHub's heading-to-anchor rule: lowercase, drop punctuation, spaces to dashes."""
    return "".join("-" if c == " " else c
                   for c in heading.lower() if c.isalnum() or c in " -_")


_EXPLICIT_ANCHOR = re.compile(r'<a\s+(?:id|name)="([^"]+)"', re.IGNORECASE)


def anchors_of(markdown: str) -> set[str]:
    """Both spellings in use under docs/: explicit <a id=".."> and heading slugs."""
    found = {a.lower() for a in _EXPLICIT_ANCHOR.findall(markdown)}
    for line in markdown.split("\n"):
        t = line.lstrip()
        if t.startswith("#"):
            # Strip a trailing explicit anchor so "### Title <a id=x></a>" slugs from "Title".
            text = re.sub(r"<a\s[^>]*>\s*</a>\s*$", "", t.lstrip("#").strip()).strip()
            found.add(slug(text))
    return found


_ANCHOR_CACHE: dict[str, set[str]] = {}


def has_anchor(md_path: str, anchor: str) -> bool:
    if md_path not in _ANCHOR_CACHE:
        _ANCHOR_CACHE[md_path] = anchors_of(read(md_path))
    return anchor.lower() in _ANCHOR_CACHE[md_path]


def resolve(target_rel: str, anchor: str | None) -> str | None:
    """None when the pointer resolves; otherwise why it does not."""
    full = os.path.join(ROOT, target_rel)
    if not os.path.exists(full):
        return "file does not exist"
    if anchor and full.endswith(".md") and not has_anchor(full, anchor):
        return f"anchor #{anchor} is not in that file"
    return None


# --- file sets ---------------------------------------------------------------

# Every tree whose files carry pointers a READER follows. AlRunner/ was the whole
# population until #4322 measured the rest: AlRunner.Tests/ alone is larger than the
# tree under guard, and held a pointer broken since it was written -- while the same
# anchor, cited from AlRunner/DependencyResolver.cs, was correct precisely because
# something checked it. A guard that stops seeing also stops complaining.
#
# The generated-code filter is load-bearing and must survive any further widening:
# without it a built checkout sweeps obj/, bin/ and __pycache__/, and every count
# reported here would be wrong in a way that still looks plausible.
GENERATED = ("/obj/", "/bin/", "/__pycache__/", "/node_modules/")

# `.github/scripts` takes a bare `*` because its scripts carry no single extension
# (.py, .sh, and no extension at all). That is why is_text() exists: a sweep of that
# tree meets a .pyc, and a UnicodeDecodeError exits 1 -- this guard's "a pointer is
# broken" code -- from a traceback that measured nothing (#4322).
#
# Measured, so neither line is credited with more than it holds: against today's
# .pyc files, is_text() and the /__pycache__/ entry are each sufficient alone and
# removing BOTH crashes. They are kept as two because they fail differently -- the
# path entry cannot help a binary outside __pycache__, and is_text() cannot stop a
# DECODABLE generated file from being counted as a pointer source.
#
# The patterns are per-tree rather than one global extension set because the trees
# genuinely differ: `scripts` mixes .js, .py and .sh, and `tools` holds three C#
# sub-projects beside its Python. A tree whose pattern misses its own source files
# is caught by check_code_trees_cover_the_repo(), not by anybody noticing.
CODE_TREES = (
    ("AlRunner", "*.cs"),
    ("AlRunner", "*.sh"),
    ("AlRunner.Tests", "*.cs"),
    ("AlRunner.Provisioning", "*.cs"),
    ("AlRunner.QueryJoin", "*.cs"),
    ("tools", "*.py"),
    ("tools", "*.sh"),
    ("tools", "*.cs"),
    ("scripts", "*.js"),
    ("scripts", "*.py"),
    ("scripts", "*.sh"),
    (".github/scripts", "*"),
    (".claude/hooks", "*.py"),
    # tests/runner-extras only: tests/expectations is surface 4 (its Doc values),
    # tests/archive is history by declaration like docs/archive/, and
    # tests/al-language is the read-only corpus, which must never be scanned here.
    ("tests/runner-extras", "*"),
)


# Every tracked source file is either inside a CODE_TREES entry or named here, and
# check_code_trees_cover_the_repo() fails when one is neither. UNSCANNED is the
# second half of the claim, not an escape hatch: an entry says "a reader does not
# follow a docs/ pointer out of this tree", which is a statement someone can be
# wrong about, so each carries the reason it is not merely inconvenient to scan.
UNSCANNED = (
    # History by declaration, exactly like docs/archive/ and for the same reason:
    # a pointer here describes the layout this tree had, so checking it would demand
    # the docs of a retired era still exist. It is the one tree whose pointers are
    # meant to rot. Everything else that is not source gets SCANNED rather than
    # exempted -- an exemption is a claim a reader has to trust, and the cheapest
    # number of those is as few as the repository actually needs.
    ("tests/archive", "the frozen pre-v2 bucket trees, retired at the v1->v2 cutover"),
)

# A tracked file with one of these extensions is source a reader can cite from. Not
# a taste question: it decides which files the coverage check below is allowed to
# ignore silently, so widening it hides trees rather than revealing them.
SOURCE_EXTENSIONS = (".cs", ".py", ".js", ".ts", ".sh", ".ps1")


def tracked_files() -> list[str] | None:
    """Every file git tracks, or None when git could not answer.

    None is the third state (guards-need-a-third-state.md): a repository-independent
    census is the whole point of the coverage check, and an empty list from a failed
    subprocess would report "every tree is registered" for a measurement that never
    happened.
    """
    try:
        out = subprocess.run(["git", "-C", ROOT, "ls-files", "-z"],
                             capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.SubprocessError):
        return None
    if out.returncode != 0:
        return None
    return [f for f in out.stdout.split("\0") if f]


def check_code_trees_are_populated() -> None:
    """Each registered tree exists and matches files -- the REMOVAL direction (#4367).

    Deleting an entry took the population from 7 trees/713 pointers to 6/482 with the
    guard still exiting 0, because nothing named the trees it was supposed to hold.
    A count floor was rejected in the issue: 713 moves whenever a legitimate citation
    is added, and it cannot say WHICH tree left. The enumeration is the claim, so the
    enumeration is what gets asserted.

    An entry matching zero files fails too: a tree that stops matching has left the
    population just as completely as one that was deleted, and is harder to see.
    """
    offenders = []
    for tree, pattern in CODE_TREES:
        if not os.path.isdir(os.path.join(ROOT, tree)):
            offenders.append(f"{tree}: registered in CODE_TREES but not a directory")
            continue
        if not any(f.startswith(os.path.join(ROOT, tree) + os.sep) for f in code_files()):
            offenders.append(f"{tree}/**/{pattern}: matches no file -- the entry guards nothing")
    check("every CODE_TREES entry names a tree that exists and matches files",
          offenders, len(CODE_TREES), "trees")


def check_code_trees_cover_the_repo() -> None:
    """Every tracked source file is scanned or declared -- the ADDITION direction (#4367).

    Measured against `git ls-files`, which knows nothing about CODE_TREES, so adding a
    source tree and forgetting to register it fails here rather than shrinking the
    guarded population by an amount nobody records. That direction was unmeasured when
    #4367 was filed and is the one that arrives on its own as the repository grows:
    AlRunner.QueryJoin/ was already tracked, already holding .cs, and already invisible.
    """
    tracked = tracked_files()
    if tracked is None:
        print("  FAIL CODE_TREES covers every tracked source tree: "
              "`git ls-files` failed -- the census could not be taken")
        FAILURES.append("CODE_TREES covers every tracked source tree")
        return
    offenders, seen = [], 0
    for f in tracked:
        if not f.endswith(SOURCE_EXTENSIONS):
            continue
        seen += 1
        if any(f.startswith(t + "/") and fnmatch.fnmatch(os.path.basename(f), p)
               for t, p in CODE_TREES):
            continue
        if any(f.startswith(t + "/") for t, _ in UNSCANNED):
            continue
        offenders.append(f"{f}: in no CODE_TREES entry and in no UNSCANNED entry -- "
                         "register the tree, or declare why a reader follows no pointer out of it")
    check("CODE_TREES covers every tracked source tree", offenders, seen, "tracked source files")


def check_unscanned_entries_are_live() -> None:
    """An UNSCANNED entry that no longer names anything is a stale exemption.

    Same failure as a fixture mark on a path that resolves: it reads as a reviewed
    decision while covering nothing, and the next tree to land under that prefix is
    exempted by an entry written about something else.
    """
    offenders = []
    for tree, why in UNSCANNED:
        if not os.path.isdir(os.path.join(ROOT, tree)):
            offenders.append(f"{tree}: declared UNSCANNED ({why}) but does not exist -- drop the entry")
        elif any(tree.startswith(t + "/") or tree == t for t, _ in CODE_TREES):
            offenders.append(f"{tree}: declared UNSCANNED ({why}) but is also inside CODE_TREES")
    check("every UNSCANNED entry names a tree that exists", offenders, len(UNSCANNED), "entries")


def code_files() -> list[str]:
    """Source files outside docs/ whose doc pointers a reader follows.

    Anchored at ROOT/<tree>, so .claude/worktrees/*/AlRunner.Tests/ is unreachable --
    verified rather than assumed (#4322): 34 worktrees each carry one, and the glob
    returns 0 of them. md_files() needs its worktree exclusion only because it globs
    `.claude/**`, which does descend into them.
    """
    out = []
    for tree, pattern in CODE_TREES:
        for f in glob.glob(os.path.join(ROOT, tree, "**", pattern), recursive=True):
            r = "/" + rel(f)
            if any(g in r for g in GENERATED):
                continue
            if os.path.isfile(f) and is_text(f):
                out.append(f)
    return sorted(set(out))


def md_files() -> list[str]:
    out = []
    for pattern in ("docs/**/*.md", ".claude/**/*.md"):
        for f in glob.glob(os.path.join(ROOT, pattern), recursive=True):
            r = rel(f)
            # archive/ is history by declaration; worktrees/ is other agents' checkouts.
            if "/archive/" in r or r.startswith(".claude/worktrees/"):
                continue
            out.append(f)
    for top in ("README.md", "CLAUDE.md"):
        if os.path.exists(os.path.join(ROOT, top)):
            out.append(os.path.join(ROOT, top))
    return sorted(out)


# The anchor must not swallow a sentence-ending period and must not end in one:
# `docs/x.md#a-b.` in running prose points at `#a-b`. Measured -- the first  # doc-pointer-fixture
# draft of this regex in the C# version reported three false failures for that.
DOC_MENTION = re.compile(
    r"docs/(?P<file>[A-Za-z0-9._-]+\.md)(#(?P<anchor>[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*))?")

# `docs/scope.md#anchor` in RunnerOutOfScopeException (and the rules quoting it)
# describes the SHAPE of a link, not a link to follow.
PLACEHOLDER_ANCHOR = "anchor"


def doc_mentions(text: str):
    for m in DOC_MENTION.finditer(text):
        anchor = m.group("anchor")
        if anchor == PLACEHOLDER_ANCHOR:
            anchor = None
        yield "docs/" + m.group("file"), anchor


# --- surfaces ----------------------------------------------------------------

# A test that feeds a doc path to the code under test writes a path that is
# deliberately not a pointer -- `docs/x.md#y` as a Doc= value, `docs/a.md` as a  # doc-pointer-fixture
# changed-file list. #4322 measured 18 of them across the newly-scanned trees, and
# they are why the widening cannot be a one-line glob change: naive, this guard
# fails on its OWN self-test, which cites `docs/x.md#a-b` to prove the regex stops  # doc-pointer-fixture
# at a sentence period.
#
# The line carrying such a path says so, and the marker is greppable so a reader can
# enumerate every exemption in one call. The alternative -- narrowing what counts as
# a pointer until the fixtures fall outside it -- is what the issue forbids, because
# the narrowing that excludes `docs/x.md#y` also excludes real pointers nobody has  # doc-pointer-fixture
# written yet.
NOT_A_POINTER = "doc-pointer-fixture"


def check_code_pointers() -> None:
    offenders, seen, exempt = [], 0, 0
    for src in code_files():
        for line in read(src).split("\n"):
            mentions = list(doc_mentions(line))
            if not mentions:
                continue
            if NOT_A_POINTER in line:
                exempt += len(mentions)
                continue
            for target, anchor in mentions:
                seen += 1
                why = resolve(target, anchor)
                if why:
                    offenders.append(
                        f"{rel(src)} -> {target}{'#' + anchor if anchor else ''}: {why}")
    check(f"code doc pointers resolve ({len(CODE_TREES)} trees, "
          f"{exempt} marked {NOT_A_POINTER})", offenders, seen, "pointers")


def check_fixture_marks_are_needed() -> None:
    """The marker is an escape hatch, so it is itself checked.

    A mark on a line whose pointer RESOLVES is the failure mode that makes this
    mechanism worse than no mechanism: it reads as "reviewed and exempt" while
    silently removing a live pointer from the guard. Such a line is either a
    mis-mark, or a pointer that became real and should now be checked -- both are
    the same remedy, drop the mark.
    """
    offenders, seen = [], 0
    for src in code_files():
        for n, line in enumerate(read(src).split("\n"), 1):
            if NOT_A_POINTER not in line:
                continue
            for target, anchor in doc_mentions(line):
                seen += 1
                if resolve(target, anchor) is None:
                    offenders.append(
                        f"{rel(src)}:{n} marks {target}{'#' + anchor if anchor else ''} "
                        f"{NOT_A_POINTER}, but it RESOLVES -- drop the mark so it is checked")
    check(f"every {NOT_A_POINTER} mark is on a path that really does not resolve",
          offenders, seen, "marked pointers")


RELOCATIONS = [
    ("docs/blob-store-isolation.md", "AlRunner/Patches/BlobStoreIsolationPatches.cs"),
    ("docs/bc-symbol-cache-versions.md", "AlRunner/Patches/BcAppSymbolCache.cs"),
]


def check_relocations() -> None:
    offenders = []
    for doc, source in RELOCATIONS:
        if not os.path.exists(os.path.join(ROOT, doc)):
            offenders.append(f"{doc} is missing; {source} was reduced on the promise it exists")
        elif not os.path.exists(os.path.join(ROOT, source)):
            offenders.append(f"{source} is missing; RELOCATIONS names it")
        elif doc not in read(os.path.join(ROOT, source)):
            offenders.append(f"{source} no longer points at {doc}: the derivation moved out "
                             "of that file and is unreachable from it")
    check("relocated derivations are still pointed at", offenders, len(RELOCATIONS), "pairs")


MD_LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")


def check_md_links() -> None:
    offenders, seen = [], 0
    for md in md_files():
        text = read(md)
        for m in MD_LINK.finditer(text):
            target = m.group(1)
            if target.startswith(("http://", "https://", "mailto:")):
                continue
            seen += 1
            path, _, anchor = target.partition("#")
            if path == "":
                target_rel = rel(md)
            else:
                target_rel = rel(os.path.normpath(os.path.join(os.path.dirname(md), path)))
            why = resolve(target_rel, anchor or None)
            if why:
                offenders.append(f"{rel(md)} -> ({target}): {why}")
    check("markdown links resolve", offenders, seen, "links")


def check_md_anchored_mentions() -> None:
    offenders, seen = [], 0
    for md in md_files():
        for target, anchor in doc_mentions(read(md)):
            if not anchor:
                continue
            seen += 1
            why = resolve(target, anchor)
            if why:
                offenders.append(f"{rel(md)} -> {target}#{anchor}: {why}")
    check("anchored docs/*.md#anchor mentions in markdown resolve",
          offenders, seen, "mentions")


def doc_values(obj):
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k == "Doc" and isinstance(v, str):
                yield v
            yield from doc_values(v)
    elif isinstance(obj, list):
        for x in obj:
            yield from doc_values(x)


def check_expectation_docs() -> None:
    offenders, seen = [], 0
    for path in glob.glob(os.path.join(ROOT, "tests", "expectations", "**", "*.json"), recursive=True):
        try:
            data = json.loads(read(path))
        except ValueError:
            continue  # the manifest loader owns malformed-JSON failures
        for value in doc_values(data):
            seen += 1
            target, _, anchor = value.partition("#")
            why = resolve(target, anchor or None)
            if why:
                offenders.append(f"{rel(path)} Doc={value}: {why}")
    check("tests/expectations Doc pointers resolve", offenders, seen, "Doc values")


# --- self-tests of the anchor logic, so a wrong slug rule cannot pass silently ---

def check_anchor_logic() -> None:
    md = ("# Title, With: Punctuation!\n"
          "## §3.5.1. Report rendering (layout + request page) <a id=\"report-rendering\"></a>\n"
          "<a name=\"legacy-name\"></a>\n"
          "text\n")
    a = anchors_of(md)
    cases = {
        "heading slug drops punctuation": "title-with-punctuation" in a,
        "explicit <a id> is an anchor": "report-rendering" in a,
        "explicit <a name> is an anchor": "legacy-name" in a,
        "heading with trailing <a id> still slugs its text":
            "351-report-rendering-layout--request-page" in a,
        "absent anchor is absent": "nope" not in a,
        "trailing period is not part of the anchor":
            # the self-test cites paths that must NOT exist.
            list(doc_mentions("see docs/x.md#a-b. Next")) == [("docs/x.md", "a-b")],  # doc-pointer-fixture
        "#anchor placeholder is not a pointer":
            list(doc_mentions("docs/scope.md#anchor")) == [("docs/scope.md", None)],
    }
    offenders = [name for name, ok in cases.items() if not ok]
    check("anchor logic self-test", offenders, len(cases), "cases")


def main() -> int:
    print(f"doc-pointer guard, repo root {ROOT}")
    check_anchor_logic()
    check_code_trees_are_populated()
    check_code_trees_cover_the_repo()
    check_unscanned_entries_are_live()
    check_code_pointers()
    check_fixture_marks_are_needed()
    check_relocations()
    check_md_links()
    check_md_anchored_mentions()
    check_expectation_docs()
    if FAILURES:
        print(f"\n{len(FAILURES)} check(s) failed: {', '.join(FAILURES)}")
        return 1
    print("\nall doc-pointer checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
