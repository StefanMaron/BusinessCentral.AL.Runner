#!/usr/bin/env python3
"""Every `setup-dotnet` site must declare an SDK reaching global.json's floor (#4211).

#4210 pinned the floor in `global.json` and declared `9.0.x` at eight
`setup-dotnet` sites. Nothing pinned the second half: a reviewer reverted all
three `publish.yml` sites to `'8.0.x'` and 433 checks stayed green, leaving a
comment reading "Declaring 9.0.x" directly above `dotnet-version: '8.0.x'`.

The floor is READ from global.json, never transcribed -- a second copy agrees
until one is edited and nothing says which, which is the drift this guard
exists to prevent. `tools/preflight.py`'s toolchain check reads it the same way
for the same reason.

Third state (guards-need-a-third-state.md), and the deliberate part: an absent
or unparseable `global.json` REFUSES rather than passing. #4210 added that file
and `.github/actions/provision-bc/action.yml` builds `AlRunner.slnx`, so its
absence is a broken measurement and not a legitimate one -- the "genuinely
absent thing stays a pass" constraint does not apply, because nothing here can
be measured without it. A run that found zero sites refuses for the same
reason: this repository has nine, and zero is what a moved directory or a
changed spelling produces.

Run: python3 tools/test_setup_dotnet_floor.py
Exit: 0 measured and fine, 1 measured and broken, 3 could not measure.
"""
from __future__ import annotations

import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GLOBAL_JSON = os.path.join(ROOT, "global.json")

# Both shapes of site, because the ninth one is in an action rather than a
# workflow: .github/actions/provision-bc/action.yml runs `dotnet build
# AlRunner.slnx` and declared 8.0.x alone at the time this guard was written.
SEARCH_DIRS = [
    os.path.join(ROOT, ".github", "workflows"),
    os.path.join(ROOT, ".github", "actions"),
]

# A `setup-dotnet` step, then its `dotnet-version:` -- either inline
# (`dotnet-version: '8.0.x'`) or a block scalar listing one version per line.
SETUP_DOTNET = re.compile(r"^[ \t]*-?[ \t]*uses:[ \t]*\S*actions/setup-dotnet@", re.M)
VERSION_KEY = re.compile(r"^(\s*)dotnet-version:\s*(.*)$")
VERSION_TOKEN = re.compile(r"(\d+)\.(?:\d+|x)")


def global_json_floor(text: str):
    """The SDK major pinned by global.json, or None when it cannot be read.

    None is deliberately not 0 and not a default: "the pin says 9" and "there is
    no readable pin" are different findings and the caller reports which it had.
    Same contract as preflight.py's function of this name.
    """
    try:
        doc = json.loads(text)
    except Exception:
        return None
    if not isinstance(doc, dict):
        return None
    version = (doc.get("sdk") or {}).get("version")
    if not isinstance(version, str):
        return None
    try:
        return int(version.split(".", 1)[0])
    except ValueError:
        return None


def declared_majors(lines: list[str], start: int):
    """The SDK majors a `setup-dotnet` step at `lines[start]` declares.

    Returns (majors, line_number, status) where status is one of:
      "declared"   -- a `dotnet-version:` was found and parsed
      "absent"     -- the step has no `dotnet-version:` key at all
      "unparsed"   -- the key is there and yielded no version token

    The split is the whole point. "absent" is a legitimate pass: setup-dotnet
    with no version resolves from global.json, which is by definition at the
    floor. "unparsed" REFUSES -- a key whose value this scanner could not read
    is a broken measurement, and folding it into the pass is how a guard reports
    its success state for something nobody measured. The first draft of this
    function did exactly that and stayed green through a three-site mutation.
    """
    for i in range(start + 1, len(lines)):
        line = lines[i]
        # Stop at the next step in the same list. Anchored with [ \t] rather than
        # \s for the same reason SETUP_DOTNET is.
        if re.match(r"^[ \t]*-[ \t]+(uses|name|run|with|shell):", line):
            return [], -1, "absent"
        m = VERSION_KEY.match(line)
        if not m:
            continue
        indent, rest = m.group(1), m.group(2).strip()
        majors = []
        if rest in ("|", ">", "|-", ">-"):
            for j in range(i + 1, len(lines)):
                body = lines[j]
                if body.strip() and (len(body) - len(body.lstrip())) <= len(indent):
                    break
                majors += [int(tok) for tok in VERSION_TOKEN.findall(body)]
        else:
            majors += [int(tok) for tok in VERSION_TOKEN.findall(rest)]
        return majors, i + 1, ("declared" if majors else "unparsed")
    return [], -1, "absent"


def sites():
    """Every (path, line, majors) triple. Raises OSError on an unreadable file."""
    found = []
    for base in SEARCH_DIRS:
        for dirpath, _dirs, names in os.walk(base):
            for name in sorted(names):
                if not name.endswith((".yml", ".yaml")):
                    continue
                path = os.path.join(dirpath, name)
                with open(path, encoding="utf-8") as fh:
                    text = fh.read()
                lines = text.splitlines()
                for m in SETUP_DOTNET.finditer(text):
                    idx = text.count("\n", 0, m.start())
                    majors, vline, status = declared_majors(lines, idx)
                    found.append((os.path.relpath(path, ROOT), idx + 1, vline,
                                  majors, status))
    return found


def main() -> int:
    try:
        with open(GLOBAL_JSON, encoding="utf-8") as fh:
            floor = global_json_floor(fh.read())
    except OSError as exc:
        print(f"UNMEASURABLE: cannot read global.json ({exc}).")
        print("  The floor is read from that file and there is no second copy to fall back")
        print("  on. #4210 added it and provision-bc builds AlRunner.slnx, so its absence is")
        print("  a broken measurement, not a legitimate one.")
        return 3
    if floor is None:
        print("UNMEASURABLE: global.json is present but declares no readable sdk.version.")
        print("  Expected {\"sdk\": {\"version\": \"<major>.<minor>.<patch>\"}}.")
        return 3

    try:
        found = sites()
    except OSError as exc:
        print(f"UNMEASURABLE: cannot read a workflow or action file ({exc}).")
        return 3

    if not found:
        print("UNMEASURABLE: no actions/setup-dotnet site found under "
              f"{', '.join(os.path.relpath(d, ROOT) for d in SEARCH_DIRS)}.")
        print("  This repository has nine. Zero is what a moved directory or a changed")
        print("  action spelling produces, and it must not read as 'every site is fine'.")
        return 3

    unparsed = [(p, l) for p, l, v, m, s in found if s == "unparsed"]
    if unparsed:
        print(f"UNMEASURABLE: {len(unparsed)} setup-dotnet site(s) declare a "
              "`dotnet-version:` this guard could not parse:")
        for path, step_line in unparsed:
            print(f"  {path}:{step_line}")
        print("  A version that cannot be read is not a version that satisfies the floor.")
        return 3

    failures = []
    for path, step_line, vline, majors, status in found:
        if status == "absent":
            # No `dotnet-version:` at all: setup-dotnet then resolves from
            # global.json, which is by definition at the floor. A legitimate
            # absence, and per guards-need-a-third-state.md it stays a pass.
            continue
        if max(majors) < floor:
            failures.append(
                f"{path}:{vline}: declares {sorted(set(majors))}, "
                f"none reaching global.json's floor of {floor}")

    if failures:
        print(f"FAIL: {len(failures)} of {len(found)} setup-dotnet site(s) declare no SDK "
              f"reaching global.json's floor ({floor}):")
        for f in failures:
            print(f"  {f}")
        print()
        print("  AlRunner.slnx needs a 9+ SDK to be parsed at all: an 8.0-only runner fails")
        print("  with `error MSB4068: The element <Solution> is unrecognized` (#4200).")
        print("  Add the floor beside the target-framework SDK, or drop dotnet-version")
        print("  entirely and let setup-dotnet resolve global.json.")
        return 1

    print(f"PASS: all {len(found)} setup-dotnet site(s) satisfy global.json's floor ({floor}).")
    return 0


# --------------------------------------------------------------------------
# The refusal paths, driven in throwaway trees.
#
# A refusal path with no test is indistinguishable from a never-fire path,
# which is the defect itself (guards-need-a-third-state.md). This ran for real
# while the guard was written: the first draft folded an unparseable
# `dotnet-version:` into the pass, and the three-site publish.yml mutation
# stayed GREEN through it.
#
# The `control` case is the load-bearing one -- five refusals that fire for
# everything prove nothing. It is what says the guard still passes a tree that
# is actually fine.

CASES = [
    # (name, global.json text or None, write a site, make its version unreadable, want)
    ("global.json absent",             None,                    True,  False, 3),
    ("global.json unparseable",        "{ not json",            True,  False, 3),
    ("global.json has no sdk.version", '{"sdk":{}}',            True,  False, 3),
    ("zero sites found",               '{"sdk":{"version":"9.0.100"}}', False, False, 3),
    ("dotnet-version unparseable",     '{"sdk":{"version":"9.0.100"}}', True,  True,  3),
    ("control: a satisfying site",     '{"sdk":{"version":"9.0.100"}}', True,  False, 0),
]


def _self_test() -> int:
    import shutil
    import subprocess
    import tempfile

    failed = 0
    for name, gj, with_site, unreadable, want in CASES:
        tree = tempfile.mkdtemp()
        try:
            os.makedirs(os.path.join(tree, "tools"))
            shutil.copy(os.path.abspath(__file__), os.path.join(tree, "tools"))
            if gj is not None:
                with open(os.path.join(tree, "global.json"), "w", encoding="utf-8") as fh:
                    fh.write(gj)
            wf = os.path.join(tree, ".github", "workflows")
            os.makedirs(wf)
            os.makedirs(os.path.join(tree, ".github", "actions"))
            if with_site:
                token = "not-a-version" if unreadable else "9.0.x"
                with open(os.path.join(wf, "x.yml"), "w", encoding="utf-8") as fh:
                    fh.write("jobs:\n  a:\n    steps:\n"
                             "      - uses: actions/setup-dotnet@v5\n        with:\n"
                             "          dotnet-version: |\n            " + token + "\n")
            env = dict(os.environ, AL_RUNNER_SETUP_DOTNET_FLOOR_CHILD="1")
            r = subprocess.run(
                [sys.executable, os.path.join(tree, "tools", os.path.basename(__file__))],
                capture_output=True, text=True, env=env, timeout=60)
            ok = r.returncode == want
            failed += not ok
            first = (r.stdout + r.stderr).strip().splitlines()
            print(f"{'ok  ' if ok else 'FAIL'} {name}: exit {r.returncode} (want {want})"
                  f" | {first[0] if first else '<no output>'}")
        finally:
            shutil.rmtree(tree, ignore_errors=True)
    print()
    print(f"Total: {len(CASES)}, Errors: 0, Failed: {failed}, Passed: {len(CASES) - failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    rc = main()
    # The self-test runs on every invocation, not behind a flag: pr-gate.yml
    # discovers this file by glob and runs it with no arguments, so a flag-gated
    # self-test would gate nothing.
    # A child spawned by _self_test must not run _self_test itself -- it would
    # copy this file into a fresh tree and spawn another, without bound. The
    # first draft did, and took the box to 52 live processes before being
    # killed. The guard is the environment marker, not the recursion depth.
    if rc == 0 and not os.environ.get("AL_RUNNER_SETUP_DOTNET_FLOOR_CHILD"):
        print()
        print("--- refusal paths ---")
        rc = _self_test()
    sys.exit(rc)
