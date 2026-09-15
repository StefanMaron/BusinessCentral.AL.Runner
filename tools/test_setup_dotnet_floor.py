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
SETUP_DOTNET = re.compile(r"^\s*-?\s*uses:\s*\S*actions/setup-dotnet@", re.M)
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

    Returns (majors, line_number). An empty list means the step declared no
    parseable version -- distinct from declaring a too-low one, and reported as
    such, because `setup-dotnet` with no version resolves from global.json and
    is therefore correct rather than broken.
    """
    for i in range(start + 1, min(start + 60, len(lines))):
        line = lines[i]
        # Stop at the next step in the same list.
        if re.match(r"^\s*-\s+(uses|name|run):", line) and i > start:
            return [], -1
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
                majors += [int(t) for t in VERSION_TOKEN.findall(body)]
        else:
            majors += [int(t) for t in VERSION_TOKEN.findall(rest)]
        return majors, i + 1
    return [], -1


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
                    majors, vline = declared_majors(lines, idx)
                    found.append((os.path.relpath(path, ROOT), idx + 1, vline, majors))
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

    failures = []
    for path, step_line, vline, majors in found:
        if not majors:
            # No version at all: setup-dotnet then resolves from global.json,
            # which is by definition at the floor. Correct, not broken.
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


if __name__ == "__main__":
    sys.exit(main())
