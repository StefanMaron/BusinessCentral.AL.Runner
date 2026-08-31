#!/usr/bin/env python3
"""Detect whether Windows PE files (.dll/.exe) carry an Authenticode signature.

This is a presence check only: it reads the Certificate Table entry (data
directory index 4, IMAGE_DIRECTORY_ENTRY_SECURITY) in the PE optional header
and treats a nonzero Size as "signed". It does NOT validate that the
signature is well-formed or that it chains to a trusted root -- that needs a
real crypto stack (signtool / Get-AuthenticodeSignature on Windows,
osslsigncode on Linux) and is deliberately out of scope here. See
publish.yml's sign-and-pack job for the chain-of-trust check, which runs on
the real Windows signing runner where that tooling exists.

Used two ways from publish.yml (#2248):

  list-unsigned <root>... [--relative-to DIR] [--exclude-dir NAME]...
      Prints one path per unsigned PE file per line, relative to --relative-to
      (default: cwd). Feeds azure/artifact-signing-action's files-catalog
      input -- the signing step must sign ONLY currently-unsigned files
      (signing over Microsoft's/a third party's existing signature would
      replace it with ours), so this listing IS the file list the signing
      step consumes, per the issue's design.

  verify <root>...
      Exits 1 and lists every unsigned .dll/.exe it finds under the given
      root(s); exits 0 otherwise. This is the release-path verification gate:
      it must find zero unsigned PE files in the packed nupkg contents.

Both subcommands share the same walk: recurse into `.dll`/`.exe` files,
skipping any path with a `ref` or `refint` directory component (MSBuild's
compile-only reference-assembly output -- never copied into a publish/pack
output, so flagging them as "unsigned" would be noise with no shipped
consequence), and silently skip any file that doesn't parse as a valid PE
(wrong DOS/PE signature, e.g. the Linux Win32Stubs `.so` files or a stray
non-binary file with a `.dll`-like name should never occur, but this is
defensive either way -- a non-PE file is not something Authenticode signing
applies to).
"""

from __future__ import annotations

import argparse
import os
import struct
import sys
from pathlib import Path
from typing import Iterator, Optional

PE_EXTENSIONS = (".dll", ".exe")
DEFAULT_EXCLUDE_DIRS = frozenset({"ref", "refint"})

# IMAGE_DIRECTORY_ENTRY_SECURITY -- the Certificate Table. Its VirtualAddress
# field is unusually a raw file offset (not an RVA, unlike every other data
# directory entry), which doesn't matter for a presence check since only the
# Size field is read here.
_CERT_TABLE_DIRECTORY_INDEX = 4

# Offset of the DataDirectory array from the start of the Optional Header,
# for each optional-header "magic" value. PE32 standard+Windows-specific
# fields total 96 bytes before DataDirectory; PE32+ (64-bit) drops the
# 4-byte BaseOfData field but widens ImageBase/SizeOfStack*/SizeOfHeap* to
# 8 bytes each, netting 112 bytes. See the PE/COFF spec (or this module's
# test file for a byte-level derivation of both).
_DATA_DIRECTORY_OFFSET_BY_MAGIC = {
    0x10B: 96,   # PE32
    0x20B: 112,  # PE32+ (64-bit)
}


class NotAPeFile(Exception):
    """Raised internally when a file doesn't parse as a PE image; callers
    of read_certificate_table_size() see this reflected as a None return,
    not this exception directly."""


def read_certificate_table_size(path: Path) -> Optional[int]:
    """Return the Certificate Table directory's Size field, or None if the
    file isn't a recognizable PE image (DOS/PE signature mismatch, missing
    optional header, unrecognized optional-header magic, or too few data
    directories to reach the Security entry). A return of 0 means "valid PE,
    no Authenticode signature"; a return of None means "not something this
    check can classify as signed or unsigned at all"."""
    try:
        with open(path, "rb") as f:
            dos_header = f.read(64)
            if len(dos_header) < 64 or dos_header[0:2] != b"MZ":
                return None
            (e_lfanew,) = struct.unpack_from("<I", dos_header, 0x3C)

            f.seek(e_lfanew)
            pe_sig = f.read(4)
            if pe_sig != b"PE\x00\x00":
                return None

            coff_header = f.read(20)
            if len(coff_header) < 20:
                return None
            (size_of_optional_header,) = struct.unpack_from("<H", coff_header, 16)
            if size_of_optional_header == 0:
                # Object files (.obj) have no optional header; not a signable image.
                return None

            optional_header = f.read(size_of_optional_header)
            if len(optional_header) < 2:
                return None
            (magic,) = struct.unpack_from("<H", optional_header, 0)
            data_dir_offset = _DATA_DIRECTORY_OFFSET_BY_MAGIC.get(magic)
            if data_dir_offset is None:
                return None

            entry_offset = data_dir_offset + _CERT_TABLE_DIRECTORY_INDEX * 8
            if len(optional_header) < entry_offset + 8:
                # Fewer than 5 data directories present at all -- no room for
                # a Security entry, so there is no certificate table.
                return 0

            _virtual_address, size = struct.unpack_from(
                "<II", optional_header, entry_offset
            )
            return size
    except OSError:
        return None


def is_signed(path: Path) -> Optional[bool]:
    """True/False if path is a classifiable PE image, None if it isn't (see
    read_certificate_table_size)."""
    size = read_certificate_table_size(path)
    if size is None:
        return None
    return size > 0


def scan(roots: list[Path], exclude_dirs: frozenset[str] = DEFAULT_EXCLUDE_DIRS) -> Iterator[Path]:
    """Yield every .dll/.exe file under the given root(s) (files are yielded
    as-is if they already match), skipping any path with an excluded
    directory component."""
    for root in roots:
        if root.is_file():
            if root.suffix.lower() in PE_EXTENSIONS:
                yield root
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in exclude_dirs]
            for name in filenames:
                if os.path.splitext(name)[1].lower() in PE_EXTENSIONS:
                    yield Path(dirpath) / name


def _cmd_list_unsigned(args: argparse.Namespace) -> int:
    exclude_dirs = DEFAULT_EXCLUDE_DIRS | set(args.exclude_dir)
    roots = [Path(r) for r in args.roots]
    relative_to = Path(args.relative_to).resolve()

    unsigned_paths = []
    for path in sorted(scan(roots, exclude_dirs)):
        signed = is_signed(path)
        if signed is False:
            unsigned_paths.append(path)

    lines = []
    for path in unsigned_paths:
        try:
            rel = os.path.relpath(path.resolve(), relative_to)
        except ValueError:
            # Different drive on Windows -- fall back to the absolute path.
            rel = str(path.resolve())
        lines.append(rel)

    output = "\n".join(lines)
    if args.catalog:
        Path(args.catalog).write_text(output + ("\n" if output else ""), encoding="utf-8")
        print(f"Wrote {len(lines)} unsigned file path(s) to {args.catalog}", file=sys.stderr)
    else:
        if output:
            print(output)
    return 0


def _cmd_verify(args: argparse.Namespace) -> int:
    roots = [Path(r) for r in args.roots]
    unsigned = []
    total_pe = 0
    for path in sorted(scan(roots)):
        signed = is_signed(path)
        if signed is None:
            continue
        total_pe += 1
        if not signed:
            unsigned.append(path)

    if unsigned:
        print(
            f"::error::{len(unsigned)} unsigned PE file(s) found "
            f"(out of {total_pe} .dll/.exe checked):",
            file=sys.stderr,
        )
        for path in unsigned:
            print(f"  {path}", file=sys.stderr)
        return 1

    print(f"OK: all {total_pe} .dll/.exe file(s) checked are Authenticode-signed.")
    return 0


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    p_list = sub.add_parser(
        "list-unsigned", help="list unsigned .dll/.exe files under the given root(s)"
    )
    p_list.add_argument("roots", nargs="+")
    p_list.add_argument(
        "--relative-to",
        default=".",
        help="print paths relative to this directory (default: cwd)",
    )
    p_list.add_argument(
        "--exclude-dir",
        action="append",
        default=[],
        help="additional directory name to exclude (repeatable)",
    )
    p_list.add_argument(
        "--catalog",
        default=None,
        help="write the listing to this file instead of stdout "
        "(azure/artifact-signing-action's files-catalog format)",
    )
    p_list.set_defaults(func=_cmd_list_unsigned)

    p_verify = sub.add_parser(
        "verify", help="fail if any .dll/.exe under the given root(s) is unsigned"
    )
    p_verify.add_argument("roots", nargs="+")
    p_verify.set_defaults(func=_cmd_verify)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
