#!/usr/bin/env python3
"""Judge one metadata-ground-truth bundle against its fixture's expected.json (#4495).

    tools/check-app-compile-bundle.py <bundle-dir> <expected.json>

The bundle is what tools/metadata-ground-truth wrote for a package: manifest.json carries
emit.Errors, emit.Objects and a census (nested
records serialize PascalCase; the top level is camelCase) by document kind. expected.json names what the fixture
declares: {"tables": N, "objects": M}.

Three checks, and all three are needed:
  * emit.Errors == 0. NOT implied by the counts: the compile runs with continueBuildOnError,
    so a package failing on a missing resource or an unset help URL still hands over every
    object that did emit -- measured on the third-party-shaped fixture, 6 objects and all 3
    tables beside one AL1081. A check on "objects > 0 and tables present" passes that run.
  * census MetaTable == expected tables. A preprocessor symbol that was not replayed drops a
    table with ZERO errors, so an error count cannot see that one.
  * emit.Objects == expected objects, the same argument for every other kind.

Exit codes: 0 all three hold; 1 at least one does not (each printed); 3 could not measure --
a missing or unreadable manifest.json/expected.json, or one missing a field it must carry.
"""
import json
import sys


def load(path, what):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        raise Unmeasurable(f"{what} not found: {path}")
    except (OSError, ValueError) as e:
        raise Unmeasurable(f"{what} unreadable: {path}: {e}")


class Unmeasurable(Exception):
    pass


def need_int(obj, key, what):
    v = obj.get(key) if isinstance(obj, dict) else None
    if not isinstance(v, int) or isinstance(v, bool):
        raise Unmeasurable(f"{what} has no integer '{key}'")
    return v


def judge(bundle_dir, expected_path):
    """Return (code, lines)."""
    try:
        manifest = load(f"{bundle_dir}/manifest.json", "bundle manifest")
        expected = load(expected_path, "expected.json")
        emit = manifest.get("emit") if isinstance(manifest, dict) else None
        errors = need_int(emit, "Errors", "manifest.emit")
        objects = need_int(emit, "Objects", "manifest.emit")
        census = manifest.get("census")
        if not isinstance(census, dict):
            raise Unmeasurable("manifest has no census")
        want_tables = need_int(expected, "tables", "expected.json")
        want_objects = need_int(expected, "objects", "expected.json")
    except Unmeasurable as e:
        return 3, [f"UNMEASURED: {e}"]

    tables = census.get("MetaTable", 0)
    app = manifest.get("app", {}).get("Name", "?")
    build = manifest.get("bcBuild", "?")
    failures = []
    if errors != 0:
        failures.append(
            f"emit reported {errors} error(s). The object and table counts below do not "
            "clear this: a failed package still hands over every object that did emit.")
    if tables != want_tables:
        failures.append(f"{tables} table metadata document(s), expected {want_tables}.")
    if objects != want_objects:
        failures.append(f"{objects} object(s) emitted, expected {want_objects}.")
    head = f"{app} on BC {build}: errors={errors} objects={objects} tables={tables}"
    if failures:
        return 1, [f"FAIL {head}"] + [f"  - {f}" for f in failures]
    return 0, [f"PASS {head}"]


def main(argv):
    if len(argv) != 3:
        print("usage: check-app-compile-bundle.py <bundle-dir> <expected.json>", file=sys.stderr)
        return 3
    code, lines = judge(argv[1], argv[2])
    for line in lines:
        print(line, file=sys.stderr if code else sys.stdout)
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv))
