#!/usr/bin/env python3
"""Decide whether a pull request changes documentation only (#2890).

Reads changed paths from stdin, one per line -- the output of
pr_changed_files.sh -- and prints one GITHUB_OUTPUT line:

    docs-only=true      every path ends in ".md"
    docs-only=false     anything else changed

"Documentation" is exactly a path ending in ".md", nowhere else. That is the
narrowest definition that covers docs/, README.md, CHANGELOG.md and the
.claude/ rules and skills, and it cannot misfire on the things that look
like prose but are read by code: app.json, the expectations manifests,
docs/archive/coverage.yaml.

Exit codes
  0  classified (either answer)
  2  no paths were given. An empty list is a broken measurement, not an empty
     pull request, and the caller must NOT skip the matrix on it.
"""

import sys


def is_documentation(path: str) -> bool:
    return path.endswith(".md")


def classify(paths):
    paths = [p.strip() for p in paths if p.strip()]
    if not paths:
        return None, []
    non_docs = [p for p in paths if not is_documentation(p)]
    return not non_docs, non_docs


def main() -> int:
    docs_only, non_docs = classify(sys.stdin.read().splitlines())
    if docs_only is None:
        print(
            "::error::docs_only.py: no changed paths were given. An empty diff is a "
            "broken measurement, not a documentation-only pull request; refusing to "
            "classify it.",
            file=sys.stderr,
        )
        return 2
    if docs_only:
        print("docs-only=true")
        print("Every changed path ends in .md: the BC test matrix is not needed.", file=sys.stderr)
    else:
        print("docs-only=false")
        print(
            f"{len(non_docs)} changed path(s) are not documentation, e.g. {non_docs[0]}: "
            "the full BC test matrix runs.",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
