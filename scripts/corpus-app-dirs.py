#!/usr/bin/env python3
"""List every test app the al-language corpus offers, so CI never has to name one.

Issue #2984. `.github/workflows/bc-tests.yml` used to point the runner at exactly
one path, `tests/al-language/tests/al-language`. The corpus then gained a second
test app (`tests/al-language-onprem`, target OnPrem, for the `Scope = OnPrem`
system tables a Cloud-target app cannot name at all). Because the workflow named
one path, the submodule pin bump that pulls a new app in is green by
construction: the app is checked out, never executed, and the leg still reports
success. Nothing is even skipped visibly -- those tests never enter the run.

So the workflow enumerates instead. Adding a test app to the corpus is enough to
make this repository's CI execute it.

What counts as an app directory is deliberately the same rule the runner itself
uses in `ProgramSupport.LooksLikeSuite` (AlRunner/ProgramSupport/Suites.cs): a
directory that declares its own `app.json`, or uses the `src/` / `test/` split.
Descent stops at the first such directory on each branch, exactly as
`EnumerateSuitesBelow` does -- a suite's own sub-directories are part of that
suite, never separate apps. If the two rules disagreed, this script would hand
the runner a path the runner does not read as one app, which is the same class of
silent miscount the enumeration exists to prevent.

Each app is printed on its own line, sorted, to be passed to the runner as a
SEPARATE positional bundle root:

    mapfile -t CORPUS_APPS < <(python3 scripts/corpus-app-dirs.py tests/al-language)
    dotnet run ... -- "${CORPUS_APPS[@]}" --strict --count-baseline ...

Separate roots, not one root over the parent directory. Measured against corpus
PR #179 on 2026-09-06: pointing the runner at the corpus root compiles all three
apps into ONE bundle, and the OnPrem app's `Record "Object Metadata"` then fails
`AL0185: Table 'Object Metadata' is missing` -- which drops 55 objects from the
Cloud app as well and takes the whole corpus down with an EMIT-ZERO compile
failure. As separate roots the same three apps ran 2595 tests in 80.8s cold,
against 96.0s for the single-app invocation this replaces.

Exits 1, loudly, when it finds nothing: an enumeration that silently produces an
empty list would put the workflow straight back into "green because it ran
nothing".

Usage:
    corpus-app-dirs.py <corpus-root>
"""
import argparse
import os
import sys

# Same three markers as ProgramSupport.LooksLikeSuite.
_SPLIT_DIRS = ("src", "test")


def looks_like_app(path):
    """True when `path` is one app: its own app.json, or the src//test/ split."""
    if os.path.isfile(os.path.join(path, "app.json")):
        return True
    return any(os.path.isdir(os.path.join(path, d)) for d in _SPLIT_DIRS)


def enumerate_app_dirs(root):
    """Every app directory at or below `root`, stopping at the first on each branch.

    Mirrors ProgramSupport.EnumerateSuites: the root itself is checked first (a
    directory that IS one app is one app, however many category sub-directories
    it holds), otherwise the root is a container and we descend.
    """
    if not os.path.isdir(root):
        return []
    if looks_like_app(root):
        return [root]

    found = []

    def descend(d):
        try:
            children = sorted(
                c for c in os.listdir(d) if os.path.isdir(os.path.join(d, c))
            )
        except OSError:
            # An unreadable directory is not an app, and must not abort the walk --
            # the runner's own SafeDirectoryScan makes the same choice for the same
            # reason (#2206).
            return
        for name in children:
            # Dot-directories are never corpus apps: `.git`, `.github`, and the
            # `.alpackages` symbol caches that hold Microsoft's own app.json files.
            if name.startswith("."):
                continue
            child = os.path.join(d, name)
            if looks_like_app(child):
                found.append(child)
            else:
                descend(child)

    descend(root)
    return sorted(found)


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("root", help="corpus checkout root, e.g. tests/al-language")
    args = ap.parse_args(argv)

    if not os.path.isdir(args.root):
        print(
            f"corpus-app-dirs: '{args.root}' is not a directory. "
            "If this is the tests/al-language submodule, it is not checked out: "
            "run `git submodule update --init --recursive`.",
            file=sys.stderr,
        )
        return 1

    dirs = enumerate_app_dirs(args.root)
    if not dirs:
        print(
            f"corpus-app-dirs: no app directory found under '{args.root}'. "
            "An empty list would make the corpus leg pass by running nothing, so "
            "this is a hard failure. Expected at least one directory with an "
            "app.json (or a src//test/ split) below that root.",
            file=sys.stderr,
        )
        return 1

    for d in dirs:
        print(d)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
