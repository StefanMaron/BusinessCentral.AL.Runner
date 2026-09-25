#!/usr/bin/env python3
"""Does check-app-compile-bundle.py tell a good compile, a wrong one and an unmeasured one apart?
And does pack-app-fixture.py ship the fixture's entry names verbatim? (#4495)

Each case asserts the EXIT CODE, which is what app-package-pipeline.sh branches on.

The case that matters most is `errors_with_full_counts`: a bundle carrying every expected object
and table beside one emit error. That is the shape the third-party-shaped fixture produced for
real with a percent-encoded layout it could not find, so a checker judging counts alone passes a
failed compile.

Run: python3 tools/test_check_app_compile_bundle.py
"""
from __future__ import annotations

import importlib.util
import io
import json
import os
import struct
import sys
import tempfile
import zipfile
from contextlib import redirect_stderr, redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
FAILURES: list[str] = []


def load(name: str, file: str):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


checker = load("check_app_compile_bundle", "check-app-compile-bundle.py")
packer = load("pack_app_fixture", "pack-app-fixture.py")


def check(name: str, cond: bool, detail: str = "") -> None:
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -- {detail}"))
    if not cond:
        FAILURES.append(name)


def bundle(tmp: str, *, errors=0, objects=6, tables=3, census=None, raw=None) -> str:
    d = tempfile.mkdtemp(dir=tmp)
    if raw is not None:
        with open(os.path.join(d, "manifest.json"), "w") as f:
            f.write(raw)
        return d
    manifest = {
        "schema": 1,
        "app": {"Name": "Fixture", "Publisher": "AL Runner Fixtures"},
        "bcBuild": "28.4.0.0",
        "emit": {"Success": errors == 0, "Objects": objects, "WithMetadata": objects,
                 "Errors": errors, "EmitMs": 1},
        "census": census if census is not None else {"MetaTable": tables, "CodeUnit": objects - tables},
    }
    with open(os.path.join(d, "manifest.json"), "w") as f:
        json.dump(manifest, f)
    return d


def expected(tmp: str, body) -> str:
    fd, p = tempfile.mkstemp(dir=tmp, suffix=".json")
    with os.fdopen(fd, "w") as f:
        f.write(body if isinstance(body, str) else json.dumps(body))
    return p


def run(b: str, e: str) -> tuple[int, str]:
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        rc = checker.main(["check", b, e])
    return rc, out.getvalue() + err.getvalue()


def main() -> int:
    with tempfile.TemporaryDirectory() as tmp:
        want = expected(tmp, {"tables": 3, "objects": 6})

        rc, text = run(bundle(tmp), want)
        check("clean_compile_passes", rc == 0, f"rc={rc} {text}")

        rc, text = run(bundle(tmp, errors=1), want)
        check("errors_with_full_counts_fails", rc == 1, f"rc={rc} {text}")
        check("errors_message_says_counts_do_not_clear_it",
              "do not clear this" in text, text)

        rc, text = run(bundle(tmp, objects=5, tables=2), want)
        check("a_missing_table_with_zero_errors_fails", rc == 1, f"rc={rc} {text}")
        check("missing_table_is_named", "2 table metadata document(s), expected 3" in text, text)

        rc, text = run(bundle(tmp, objects=7, tables=3, census={"MetaTable": 3, "CodeUnit": 4}), want)
        check("an_extra_object_fails", rc == 1, f"rc={rc} {text}")

        rc, text = run(bundle(tmp, objects=6, census={"CodeUnit": 6}), want)
        check("no_metatable_in_census_counts_as_zero_tables", rc == 1, f"rc={rc} {text}")

        # The third state: nothing measured must never be the success code.
        rc, _ = run(os.path.join(tmp, "no-such-bundle"), want)
        check("missing_manifest_is_unmeasured", rc == 3, f"rc={rc}")
        rc, _ = run(bundle(tmp, raw="{ not json"), want)
        check("unparseable_manifest_is_unmeasured", rc == 3, f"rc={rc}")
        rc, _ = run(bundle(tmp, raw=json.dumps({"emit": {"errors": 0, "objects": 6}, "census": {}})), want)
        check("camelcase_emit_fields_are_unmeasured_not_zero", rc == 3, f"rc={rc}")
        rc, _ = run(bundle(tmp), os.path.join(tmp, "no-such-expected.json"))
        check("missing_expected_is_unmeasured", rc == 3, f"rc={rc}")
        rc, _ = run(bundle(tmp), expected(tmp, {"tables": 3}))
        check("expected_without_objects_is_unmeasured", rc == 3, f"rc={rc}")
        rc, _ = run(bundle(tmp), expected(tmp, {"tables": True, "objects": 6}))
        check("boolean_is_not_a_count", rc == 3, f"rc={rc}")

        # The packer: entry names verbatim, expected.json left out, the NAVX header readable.
        fx = os.path.join(tmp, "fx")
        os.makedirs(os.path.join(fx, "src"))
        os.makedirs(os.path.join(fx, "layout"))
        for rel, body in {"NavxManifest.xml": "<Package/>", "src/A.Table.al": "table 1 A { }",
                          "layout/My%20Report.rdlc": "<Report/>", "expected.json": "{}"}.items():
            with open(os.path.join(fx, rel), "w") as f:
                f.write(body)
        data = packer.pack(fx)
        check("navx_magic", data[:4] == b"NAVX", repr(data[:8]))
        off = struct.unpack("<I", data[4:8])[0]
        names = zipfile.ZipFile(io.BytesIO(data[off:])).namelist()
        check("percent_encoded_entry_is_verbatim", "layout/My%20Report.rdlc" in names, str(names))
        check("expected_json_not_packed", "expected.json" not in names, str(names))

        os.remove(os.path.join(fx, "src", "A.Table.al"))
        try:
            packer.pack(fx)
            check("no_source_is_refused", False, "packed a fixture with no src/*.al")
        except FileNotFoundError:
            check("no_source_is_refused", True)

        # The shipped fixture itself must be packable and carry the quirks it exists to test.
        real = os.path.join(HERE, "metadata-ground-truth", "fixtures", "third-party-shaped")
        data = packer.pack(real)
        off = struct.unpack("<I", data[4:8])[0]
        z = zipfile.ZipFile(io.BytesIO(data[off:]))
        names = z.namelist()
        check("fixture_ships_an_encoded_entry", any("%20" in n for n in names), str(names))
        manifest = z.read("NavxManifest.xml").decode("utf-8-sig")
        check("fixture_is_not_microsoft_published", 'Publisher="Microsoft"' not in manifest)
        check("fixture_declares_a_preprocessor_symbol", "<PreprocessorSymbol>" in manifest)
        with open(os.path.join(real, "expected.json")) as f:
            exp = json.load(f)
        check("fixture_expected_is_well_formed",
              isinstance(exp.get("tables"), int) and isinstance(exp.get("objects"), int), str(exp))

    print(f"\n{len(FAILURES)} failure(s)")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
