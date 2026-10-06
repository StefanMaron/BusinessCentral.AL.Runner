#!/usr/bin/env python3
"""Preprocessor symbols the corpus expects a BC leg to define (#5382).

The corpus's own ci.yml passes BC27PLUS on every leg, BC28PLUS from 28 and BC29PLUS from 29, and its
tests branch on them (`#if BC29PLUS` asserts BC 29's behaviour, `#else` keeps 27/28). The corpus
declares them nowhere else, so a runner that passes none compiles the 27/28 branch on every version.

    corpus-version-symbols.py 29.0.54011.55816   ->   BC27PLUS,BC28PLUS,BC29PLUS

A version below 27 or one that does not parse exits 3 and prints nothing: no symbol list is not a
valid answer for a leg, and an empty --preprocessor-symbols would pass for "none needed".
"""
import re
import sys

FIRST = 27


def symbols(version: str) -> str:
    m = re.match(r"^(\d+)(?:\.|$)", version.strip())
    if not m or int(m.group(1)) < FIRST:
        raise ValueError(f"cannot derive corpus symbols from BC version '{version}'")
    return ",".join(f"BC{v}PLUS" for v in range(FIRST, int(m.group(1)) + 1))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: corpus-version-symbols.py <bc-version>", file=sys.stderr)
        sys.exit(3)
    try:
        print(symbols(sys.argv[1]))
    except ValueError as e:
        print(e, file=sys.stderr)
        sys.exit(3)
