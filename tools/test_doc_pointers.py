#!/usr/bin/env python3
"""Doc-pointer guard: every pointer INTO the documentation resolves (#3422).

A docs-only pull request skips the BC matrix (#3421), and with it the C# suite
that used to hold these checks (ProseRelocationPointerTests, #3402 / #3260).
pr-gate.yml runs every tools/test_*.py on every pull request, so the pointer
checks live here now and gate the day a doc changes.

Four surfaces, one question each -- does the target exist right now:

  1. `docs/<file>.md[#anchor]` mentions in AlRunner/**/*.cs (comments and
     string literals alike; a refusal message carrying a doc link rots too).
  2. Markdown links `[..](path[#anchor])` in docs/, .claude/, README.md, CLAUDE.md.
  3. ANCHORED bare mentions `docs/<file>.md#anchor` in those same markdown files.
     Unanchored bare mentions are deliberately not checked: prose narrates
     history ("`docs/coverage.md` is archived under docs/archive/") and a path
     check there needs an allowlist, which is the failure mode this suite
     refuses. An #anchor is a claim about a section that exists now.
  4. `Doc` values in tests/expectations/**/*.json, the pointers a reader of a
     divergence entry follows.

Plus the #3260 relocation pairs: a doc created to hold one file's derivation
is still pointed at by that file.

Every surface must be non-empty, so a regex that drifts to match nothing
fails instead of passing vacuously.

Run: python3 tools/test_doc_pointers.py
"""
from __future__ import annotations

import glob
import json
import os
import re
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

def cs_files() -> list[str]:
    return [f for f in glob.glob(os.path.join(ROOT, "AlRunner", "**", "*.cs"), recursive=True)
            if "/obj/" not in rel(f) and "/bin/" not in rel(f)]


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
# `docs/x.md#a-b.` in running prose points at `#a-b`. Measured -- the first
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

def check_cs_pointers() -> None:
    offenders, seen = [], 0
    for cs in cs_files():
        for target, anchor in doc_mentions(read(cs)):
            seen += 1
            why = resolve(target, anchor)
            if why:
                offenders.append(f"{rel(cs)} -> {target}{'#' + anchor if anchor else ''}: {why}")
    check("AlRunner/**/*.cs doc pointers resolve", offenders, seen, "pointers")


RELOCATIONS = [
    ("docs/blob-store-isolation.md", "AlRunner/Patches/BlobStoreIsolationPatches.cs"),
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
            list(doc_mentions("see docs/x.md#a-b. Next")) == [("docs/x.md", "a-b")],
        "#anchor placeholder is not a pointer":
            list(doc_mentions("docs/scope.md#anchor")) == [("docs/scope.md", None)],
    }
    offenders = [name for name, ok in cases.items() if not ok]
    check("anchor logic self-test", offenders, len(cases), "cases")


def main() -> int:
    print(f"doc-pointer guard, repo root {ROOT}")
    check_anchor_logic()
    check_cs_pointers()
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
