#!/usr/bin/env python3
"""Measure the comment/code split of a C# tree.

Exists so successive passes at issue #3260 measure the same way instead of
re-deriving a classifier each time and comparing incomparable numbers.

Classification, per physical line:

  blank    nothing but whitespace
  comment  the line's non-whitespace content is entirely comment: a `//` or
           `///` line, or a line wholly inside a `/* ... */` block
  code     anything else, including a line with a trailing `// ...` comment

A trailing comment counts as CODE, not comment. That is deliberate and
conservative: it under-counts comment mass rather than over-counting it, so a
reduction measured with this script is never flattered by it.

`//` and `/*` inside a string or char literal do not open a comment; the
scanner tracks string, verbatim-string, interpolated-string and char state so
that `var s = "// not a comment";` classifies as code.

Two modes. The default measures a TREE as it stands. `diff` measures the lines a
branch ADDS, which is the figure quoted in reviews; see `diff_counts` for why it
resolves a merge base rather than diffing against a ref.

Usage:
    tools/comment-density.py [ROOT ...] [--top N] [--json] [--min-lines N]
    tools/comment-density.py diff [--base REF] [--head REF] [--since REF]
                                  [--path P ...] [--json]
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from dataclasses import dataclass, asdict, field

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    # Before any print: stdout is built from the console codec, and cp1252
    # cannot encode the robot emoji in an agent-authored PR body (#3589).
    _stdio.enable_utf8_stdio()

SKIP_DIRS = {"obj", "bin", "graphify-out", ".git", "node_modules"}


@dataclass
class Counts:
    total: int = 0
    comment: int = 0
    code: int = 0
    blank: int = 0

    def add(self, other: "Counts") -> None:
        self.total += other.total
        self.comment += other.comment
        self.code += other.code
        self.blank += other.blank

    @property
    def share(self) -> float:
        """Comment share of NON-BLANK lines, which is the figure #3260 quotes."""
        nonblank = self.comment + self.code
        return (self.comment / nonblank * 100.0) if nonblank else 0.0


def classify(text: str) -> Counts:
    """Classify every physical line of a C# source file."""
    counts = Counts()
    in_block = False          # inside /* ... */
    for raw in text.splitlines():
        counts.total += 1
        stripped = raw.strip()
        if not stripped and not in_block:
            counts.blank += 1
            continue

        saw_code = False
        saw_comment = in_block
        i = 0
        n = len(raw)
        while i < n:
            ch = raw[i]
            if in_block:
                if ch == "*" and i + 1 < n and raw[i + 1] == "/":
                    in_block = False
                    i += 2
                    continue
                i += 1
                continue
            if ch == "/" and i + 1 < n and raw[i + 1] == "/":
                saw_comment = True
                break  # rest of the line is a line comment
            if ch == "/" and i + 1 < n and raw[i + 1] == "*":
                in_block = True
                saw_comment = True
                i += 2
                continue
            if ch == "@" and i + 1 < n and raw[i + 1] == '"':
                saw_code = True
                i = _skip_verbatim(raw, i + 2)
                continue
            if ch == '"':
                saw_code = True
                i = _skip_quoted(raw, i + 1, '"')
                continue
            if ch == "'":
                saw_code = True
                i = _skip_quoted(raw, i + 1, "'")
                continue
            if not ch.isspace():
                saw_code = True
            i += 1

        if saw_code:
            counts.code += 1
        elif saw_comment:
            counts.comment += 1
        elif stripped:
            counts.code += 1
        else:
            counts.blank += 1
    return counts


def _skip_quoted(line: str, i: int, quote: str) -> int:
    """Advance past a regular string/char literal opened just before `i`."""
    n = len(line)
    while i < n:
        if line[i] == "\\":
            i += 2
            continue
        if line[i] == quote:
            return i + 1
        i += 1
    return i


def _skip_verbatim(line: str, i: int) -> int:
    """Advance past a @"..." literal, where "" is an escaped quote."""
    n = len(line)
    while i < n:
        if line[i] == '"':
            if i + 1 < n and line[i + 1] == '"':
                i += 2
                continue
            return i + 1
        i += 1
    return i


def walk(roots: list[str]) -> list[str]:
    found: list[str] = []
    for root in roots:
        if os.path.isfile(root):
            found.append(root)
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for name in filenames:
                if name.endswith(".cs"):
                    found.append(os.path.join(dirpath, name))
    return sorted(found)


DEFAULT_PATHS = ["AlRunner/"]
DEFAULT_BASE = "origin/main"


class Unmeasurable(Exception):
    """The measurement could not be taken, as distinct from being zero.

    guards-need-a-third-state.md: an unresolvable ref and an empty diff must not
    answer alike. An empty diff is a real zero and returns one.
    """


@dataclass
class DiffCounts:
    base: str = ""          # the resolved merge-base COMMIT, not the ref asked for
    head: str = ""
    added: Counts = field(default_factory=Counts)
    files: int = 0
    hunks: int = 0


@dataclass
class DeltaCounts:
    before: DiffCounts = field(default_factory=DiffCounts)
    after: DiffCounts = field(default_factory=DiffCounts)
    comment: int = 0
    code: int = 0


def _git(repo: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", repo, *args],
                          capture_output=True, text=True)


def resolve_commit(repo: str, ref: str) -> str:
    """Resolve `ref` to a commit SHA, or raise Unmeasurable.

    `--verify -q` is not optional: without it `git rev-parse` ECHOES an unknown
    argument back on stdout and exits 128, so a caller comparing strings reports
    success for a commit git has never heard of (ci-verdicts.md).
    """
    p = _git(repo, "rev-parse", "--verify", "-q", f"{ref}^{{commit}}")
    sha = p.stdout.strip()
    if p.returncode != 0 or not sha:
        raise Unmeasurable(f"cannot resolve ref to a commit: {ref!r}")
    return sha


def merge_base(repo: str, base: str, head: str) -> str:
    p = _git(repo, "merge-base", base, head)
    sha = p.stdout.strip()
    if p.returncode != 0 or not sha:
        raise Unmeasurable(
            f"no merge base between {base!r} and {head!r}; unrelated histories "
            "have no common ancestor, so there is no 'what this branch added'")
    return sha


def _added_lines_per_hunk(repo: str, base_sha: str, head_sha: str,
                          paths: list[str]) -> tuple[list[list[str]], int]:
    """Added .cs lines, grouped per hunk, plus the number of files touched.

    Grouping matters: added lines from unrelated hunks are not one source file.
    Running a single scanner state across them lets an unterminated `/*` in an
    early hunk classify every later line as comment -- a silent, plausible
    over-count of exactly the quantity being measured.
    """
    p = _git(repo, "diff", "--unified=0", "--no-color", "--no-ext-diff",
             f"{base_sha}...{head_sha}", "--", *paths)
    if p.returncode != 0:
        raise Unmeasurable(f"git diff failed: {p.stderr.strip()}")

    hunks: list[list[str]] = []
    current: list[str] | None = None
    in_cs = False
    files: set[str] = set()
    for line in p.stdout.splitlines():
        if line.startswith("+++ "):
            name = line[4:]
            if name.startswith("b/"):
                name = name[2:]
            in_cs = name.endswith(".cs") and name != "/dev/null"
            if in_cs:
                files.add(name)
            current = None
            continue
        if line.startswith("--- ") or line.startswith("diff --git"):
            current = None
            continue
        if line.startswith("@@"):
            current = [] if in_cs else None
            if current is not None:
                hunks.append(current)
            continue
        if current is not None and line.startswith("+"):
            current.append(line[1:])
    return hunks, len(files)


def diff_counts(repo: str, base: str, head: str,
                paths: list[str] | None = None) -> DiffCounts:
    """Comment/code split of the lines `head` ADDS relative to `base`.

    THREE-DOT, against a merge base resolved to an explicit commit.

    `no-git-stash-with-worktrees.md` documents the opposite preference, and it is
    right for the question it asks -- "did a stale-ref soft reset stage other
    people's work as deletions?", where three-dot moves the merge base along with
    the reset and hides it. That question is about DELETIONS in a rewritten
    branch. This one is "what prose did this branch add?", where two-dot against
    a moving `origin/main` attributes every intervening merge to the branch: on
    #4347's own measurement it reported +13/+36 for a branch that added +10/+9.

    Resolving the merge base to a SHA also makes the answer quotable. A reviewer
    re-running this a day later against the same refs gets a different number,
    and nothing says why (#4347: "a number that four reviewers compute four ways
    is not a trend").
    """
    paths = list(paths or DEFAULT_PATHS)
    head_sha = resolve_commit(repo, head)
    base_sha = merge_base(repo, resolve_commit(repo, base), head_sha)

    hunks, files = _added_lines_per_hunk(repo, base_sha, head_sha, paths)
    total = Counts()
    for hunk in hunks:
        total.add(classify("\n".join(hunk)))
    return DiffCounts(base=base_sha, head=head_sha, added=total,
                      files=files, hunks=len(hunks))


def delta_counts(repo: str, base: str, before: str, after: str,
                 paths: list[str] | None = None) -> DeltaCounts:
    """What a second head added on top of a first, both measured from one base.

    #4347's finding needs this: PR #4336 arrived at +10/+9 and left review at
    +19/+9, so the absolute figure blends the author's contribution with the
    review's. Subtracting two three-dot measurements against a shared merge base
    is what separates them.
    """
    b = diff_counts(repo, base, before, paths)
    a = diff_counts(repo, base, after, paths)
    return DeltaCounts(before=b, after=a,
                       comment=a.added.comment - b.added.comment,
                       code=a.added.code - b.added.code)


def _ratio(comment: int, code: int) -> str:
    if code == 0:
        return "n/a (no code lines added)" if comment == 0 else f"{comment}:0"
    return f"{comment / code:.2f}:1"


def run_diff(args: argparse.Namespace) -> int:
    repo = args.repo or "."
    paths = args.path or DEFAULT_PATHS
    try:
        if args.since:
            d = delta_counts(repo, args.base, args.since, args.head, paths)
            payload = {
                "mode": "delta",
                "base": d.before.base,
                "before": {"head": d.before.head,
                           "comment": d.before.added.comment,
                           "code": d.before.added.code},
                "after": {"head": d.after.head,
                          "comment": d.after.added.comment,
                          "code": d.after.added.code},
                "delta": {"comment": d.comment, "code": d.code},
                "paths": paths,
            }
        else:
            r = diff_counts(repo, args.base, args.head, paths)
            payload = {
                "mode": "added",
                "base": r.base,
                "head": r.head,
                "comment": r.added.comment,
                "code": r.added.code,
                "files": r.files,
                "hunks": r.hunks,
                "paths": paths,
            }
    except Unmeasurable as exc:
        print(f"could not measure: {exc}", file=sys.stderr)
        return 3

    if args.json:
        print(json.dumps(payload, indent=2))
        return 0

    print(f"scope   {' '.join(paths)}")
    print(f"base    {payload['base']}  (merge base, three-dot)")
    if payload["mode"] == "delta":
        b, a, dl = payload["before"], payload["after"], payload["delta"]
        print(f"before  {b['head'][:12]}  +{b['comment']} comment  "
              f"+{b['code']} code   {_ratio(b['comment'], b['code'])}")
        print(f"after   {a['head'][:12]}  +{a['comment']} comment  "
              f"+{a['code']} code   {_ratio(a['comment'], a['code'])}")
        print(f"delta                 {dl['comment']:+d} comment  "
              f"{dl['code']:+d} code")
    else:
        print(f"head    {payload['head']}")
        print(f"files   {payload['files']} .cs file(s), "
              f"{payload['hunks']} hunk(s)")
        print(f"added   +{payload['comment']} comment  +{payload['code']} code"
              f"   {_ratio(payload['comment'], payload['code'])}")
    return 0


def main(argv: list[str]) -> int:
    if argv and argv[0] == "diff":
        ap = argparse.ArgumentParser(
            prog="comment-density.py diff",
            description="Comment/code split of the lines a branch ADDS, "
                        "three-dot against the merge base.")
        ap.add_argument("--base", default=DEFAULT_BASE,
                        help=f"branch point to measure from (default {DEFAULT_BASE}); "
                             "its merge base with --head is what is used")
        ap.add_argument("--head", default="HEAD", help="head to measure (default HEAD)")
        ap.add_argument("--since", metavar="REF",
                        help="report the DELTA from this earlier head to --head, "
                             "both measured from the same merge base")
        ap.add_argument("--path", action="append", metavar="P",
                        help=f"path scope, repeatable (default {' '.join(DEFAULT_PATHS)})")
        ap.add_argument("--repo", default=".", help="repository to read (default .)")
        ap.add_argument("--json", action="store_true", help="machine-readable output")
        return run_diff(ap.parse_args(argv[1:]))

    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("roots", nargs="*", default=["AlRunner"],
                    help="directories or files to scan (default: AlRunner)")
    ap.add_argument("--top", type=int, default=15,
                    help="how many worst-offender files to list (default 15)")
    ap.add_argument("--min-lines", type=int, default=200,
                    help="only rank files with at least this many lines (default 200)")
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    args = ap.parse_args(argv)

    roots = args.roots or ["AlRunner"]
    files = walk(roots)
    if not files:
        print(f"no .cs files under {roots}", file=sys.stderr)
        return 1

    per_file: dict[str, Counts] = {}
    total = Counts()
    for path in files:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            c = classify(fh.read())
        per_file[path] = c
        total.add(c)

    if args.json:
        print(json.dumps({
            "roots": roots,
            "files": len(files),
            "total": asdict(total) | {"share": round(total.share, 2)},
            "per_file": {p: asdict(c) | {"share": round(c.share, 2)}
                         for p, c in per_file.items()},
        }, indent=2))
        return 0

    print(f"{'/'.join(roots)}  {len(files)} files")
    print(f"  total   {total.total:>7,}")
    print(f"  comment {total.comment:>7,}")
    print(f"  code    {total.code:>7,}")
    print(f"  blank   {total.blank:>7,}")
    print()
    print(f"comment share of non-blank lines : {total.share:.1f}%")

    ranked = sorted(
        ((c, p) for p, c in per_file.items() if c.total >= args.min_lines),
        key=lambda t: t[0].share, reverse=True)[:args.top]
    if ranked:
        print()
        print(f"worst offenders (files of >= {args.min_lines} lines):")
        print(f"{'share':>7}  {'cmt/code':>10}  file")
        for c, p in ranked:
            print(f"{c.share:>6.1f}%  {c.comment:>4}/{c.code:<5}  {p}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
