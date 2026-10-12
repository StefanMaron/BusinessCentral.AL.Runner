#!/usr/bin/env python3
"""Fail when a packed engine variant lacks an asset its own al-runner.deps.json names (#5461).

An engine variant (tools/<tfm>/any/variants/<build>/) is entered by re-exec into a shadow
directory that mirrors the install and overlays the variant (NclShadowRuntime.MirrorInstallDirectory),
and hostfxr then resolves the variant's own al-runner.deps.json against that directory. An asset
the deps.json names that is in NEITHER the variant folder NOR the install folder is not there at
run time. On Windows the BC 29 variant named runtimes/win/lib/net10.0/System.Diagnostics.EventLog.dll
while the package carried only runtimes/win/lib/net8.0/..., the rid-specific asset silently went
missing, and the engine died at startup with a TypeLoadException.

What is checked, and why only this:
  - libraries of type "package" and "project" -- files the package ships. "reference" libraries are
    the BC service-tier assemblies, resolved from the user's artifact cache at run time and
    deliberately absent (check-nupkg-contents.sh asserts exactly that).
  - where each asset is expected on disk follows the SDK's own layout: a "runtime" or "native" asset
    is flattened to its file name at the app root, a "resources" asset sits at <locale>/<file name>,
    a "runtimeTargets" asset keeps the relative path it is named by.
  - present = in the variant folder or at the same relative path in the install folder, the same
    union the shadow-dir mirror builds.

Usage: check_variant_assets.py <path-to-nupkg>

Exit codes (guards-need-a-third-state.md): 0 every variant is whole, 1 an asset is missing,
3 nothing could be measured (unreadable package, no variant carrying a deps.json, an unreadable
deps.json). A package with zero variants is exit 3, never 0: nothing was checked.
"""

from __future__ import annotations

import json
import posixpath
import re
import sys
import zipfile

SHIPPED_LIBRARY_TYPES = ("package", "project")
VARIANT_DEPS = re.compile(r"^(tools/[^/]+/any)/variants/([^/]+)/al-runner\.deps\.json$")


def expected_paths(deps: dict) -> list[str]:
    """Relative paths, from the app root, of every file the deps.json says the package ships."""
    libraries = deps.get("libraries") or {}
    out: set[str] = set()
    for target in (deps.get("targets") or {}).values():
        for name, lib in target.items():
            if (libraries.get(name) or {}).get("type") not in SHIPPED_LIBRARY_TYPES:
                continue
            for section in ("runtime", "native"):
                for path in (lib.get(section) or {}):
                    out.add(posixpath.basename(path))
            for path, meta in (lib.get("resources") or {}).items():
                out.add(f"{meta['locale']}/{posixpath.basename(path)}")
            for path in (lib.get("runtimeTargets") or {}):
                out.add(path)
    return sorted(out)


def missing_assets(deps: dict, variant_files: set[str], install_files: set[str]) -> list[str]:
    """Expected paths present in neither the variant's file set nor the install's."""
    return [p for p in expected_paths(deps) if p not in variant_files and p not in install_files]


def files_under(names: set[str], prefix: str) -> set[str]:
    return {n[len(prefix):] for n in names if n.startswith(prefix) and not n.endswith("/")}


def check_package(path: str) -> tuple[int, list[str]]:
    try:
        with zipfile.ZipFile(path) as z:
            names = set(z.namelist())
            variants = sorted((m.group(1), m.group(2)) for n in names if (m := VARIANT_DEPS.match(n)))
            if not variants:
                return 3, [f"no tools/<tfm>/any/variants/<build>/al-runner.deps.json in {path}: "
                           "nothing was checked"]
            lines: list[str] = []
            status = 0
            for tool_dir, build in variants:
                variant_prefix = f"{tool_dir}/variants/{build}/"
                try:
                    deps = json.loads(z.read(variant_prefix + "al-runner.deps.json"))
                except (ValueError, KeyError) as exc:
                    return 3, [f"{variant_prefix}al-runner.deps.json is unreadable ({exc}): cannot measure"]
                variant_files = files_under(names, variant_prefix)
                # The install's file set: everything under the tool dir EXCEPT the variants themselves.
                install_files = {f for f in files_under(names, tool_dir + "/")
                                 if not f.startswith("variants/")}
                gone = missing_assets(deps, variant_files, install_files)
                if gone:
                    status = 1
                    lines.append(f"variant {build}: {len(gone)} asset(s) named by its al-runner.deps.json "
                                 "are in neither the variant folder nor the install folder:")
                    lines.extend(f"    {g}" for g in gone)
                else:
                    lines.append(f"variant {build}: every asset its al-runner.deps.json names is present")
            return status, lines
    except (OSError, zipfile.BadZipFile) as exc:
        return 3, [f"cannot read {path} as a package ({exc}): nothing was checked"]


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: check_variant_assets.py <path-to-nupkg>", file=sys.stderr)
        return 3
    status, lines = check_package(argv[1])
    for line in lines:
        print(("::error::" if line.startswith("variant") and "asset(s)" in line else "") + line)
    return status


if __name__ == "__main__":
    sys.exit(main(sys.argv))
