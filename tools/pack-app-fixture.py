#!/usr/bin/env python3
"""Pack a fixture directory into a NAVX .app the way a shipped package is laid out.

    tools/pack-app-fixture.py <fixture-dir> <out.app>

Every file under <fixture-dir> becomes one zip entry at its relative path, VERBATIM: a file
named `TP%20Fixture%20Report.rdlc` on disk ships as that entry name, which is the point of
the fixture (#3530, category 3). `expected.json` is the fixture's own assertion file and is
not packed. The zip is prefixed with the 8-byte NAVX header the runner and
tools/metadata-ground-truth both read (magic + little-endian offset of the zip).

Exit codes: 0 packed; 2 bad usage; 3 the fixture is not packable (no NavxManifest.xml, or no
src/*.al), which is refused rather than packed into a package that would compile nothing.
"""
import io
import os
import struct
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import agent_stdio as _stdio
except Exception:  # pragma: no cover - a copy detached from its sibling module
    _stdio = None
if _stdio is not None:
    _stdio.enable_utf8_stdio()

NOT_PACKED = {"expected.json"}


def pack(fixture: str) -> bytes:
    if not os.path.isfile(os.path.join(fixture, "NavxManifest.xml")):
        raise FileNotFoundError(f"{fixture}: no NavxManifest.xml, so this is not a package fixture")
    entries = []
    for root, _dirs, files in os.walk(fixture):
        for name in files:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, fixture).replace(os.sep, "/")
            if rel in NOT_PACKED:
                continue
            entries.append((rel, full))
    if not any(rel.startswith("src/") and rel.endswith(".al") for rel, _ in entries):
        raise FileNotFoundError(f"{fixture}: no src/*.al, so a compile of this package would be empty")
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for rel, full in sorted(entries):
            z.write(full, rel)
    return b"NAVX" + struct.pack("<I", 8) + buf.getvalue()


def main(argv):
    if len(argv) != 3:
        print(__doc__.strip().splitlines()[2].strip(), file=sys.stderr)
        return 2
    try:
        data = pack(argv[1])
    except FileNotFoundError as e:
        print(f"pack-app-fixture: REFUSED: {e}", file=sys.stderr)
        return 3
    os.makedirs(os.path.dirname(os.path.abspath(argv[2])), exist_ok=True)
    with open(argv[2], "wb") as f:
        f.write(data)
    print(f"pack-app-fixture: {argv[1]} -> {argv[2]}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
