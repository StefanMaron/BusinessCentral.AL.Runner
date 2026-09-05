#!/usr/bin/env python3
"""Detect whether Windows PE files (.dll/.exe) carry an Authenticode signature.

This is a presence check only: it reads the Certificate Table entry (data
directory index 4, IMAGE_DIRECTORY_ENTRY_SECURITY) in the PE optional header
and treats a nonzero Size as "signed". It does NOT validate that the
signature is well-formed or that it chains to a trusted root -- a file whose
Certificate Table holds a nonzero size but arbitrary, non-Authenticode bytes
still reads as "signed" here (see test_pe_signing.py's
PresenceCheckOnlyTests, #2284).

publish.yml's sign-and-pack job runs a real chain-of-trust check
(Get-AuthenticodeSignature, on the Windows signing runner, right after the
signing action) in addition to this one -- but only over the files THAT RUN
signed (catalog.txt), not over the whole tree this script scans. The ~84
files this workflow deliberately never touches carry Microsoft's or a third
party's own signature, and an expired or untimestamped third-party
certificate would fail a validity check for a reason that isn't this repo's
defect, so this presence check is still the only gate applied to those.

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

BOTH subcommands exit 1 when a root does not exist, and when the walk
classified ZERO PE files -- an empty directory, a tree whose only .dll/.exe
files are unparseable, or one whose only PE files sit in an excluded
ref/refint directory (#2298). verify used to print
"OK: all 0 .dll/.exe file(s) checked are Authenticode-signed." and exit 0 for
all of those, which is the failure class #2284 fixed one layer up: a check
that cannot distinguish "I looked and everything was signed" from "I did not
look at anything". An empty scan is a hard failure, not a lenient pass and
not a warning -- every caller of this script is a release gate, and the one
in publish.yml that builds the signing catalog already refuses to "succeed at
signing nothing" for the same reason. Note what is NOT affected: zero
*unsigned* files in a tree that really was scanned remains a normal success
for list-unsigned (an already-signed tree writes a legitimately empty
catalog); it is zero *scanned* files that fails.

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


def _missing_roots(roots: list[Path]) -> list[Path]:
    """Roots that do not exist on disk. os.walk() yields nothing and raises
    nothing for a missing directory, so without this check a typo'd or
    never-created root produces an empty scan that reads exactly like a clean
    one (#2298)."""
    return [root for root in roots if not root.exists()]


def _report_missing_roots(roots: list[Path]) -> bool:
    """Print a ::error:: line per missing root and return True if any were
    missing (the caller then exits non-zero without scanning)."""
    missing = _missing_roots(roots)
    for root in missing:
        print(
            f"::error::scan root does not exist: {root}. Nothing would be "
            "scanned under it, and an empty scan must not be reported as a "
            "clean one (#2298).",
            file=sys.stderr,
        )
    return bool(missing)


def _classify(roots: list[Path], exclude_dirs: frozenset[str] = DEFAULT_EXCLUDE_DIRS):
    """Walk the roots once and return (candidates, classified):

      candidates  -- every .dll/.exe path scan() yielded, sorted
      classified  -- the (path, is_signed) pairs among them that actually
                     parse as a PE image (is_signed() returned a bool)

    Keeping both counts is what lets the callers below tell "the directory
    held nothing at all" apart from "it held .dll files that are not PE
    images" -- different causes, different fixes, and both of them used to
    print the same "all 0 ... checked" success line."""
    candidates = sorted(scan(roots, exclude_dirs))
    classified = []
    for path in candidates:
        signed = is_signed(path)
        if signed is not None:
            classified.append((path, signed))
    return candidates, classified


def _report_empty_scan(roots: list[Path], candidates: list[Path], consequence: str) -> None:
    """Print the ::error:: line for a scan that classified zero PE files.

    #2298: `verify` printed "OK: all 0 .dll/.exe file(s) checked are
    Authenticode-signed." and exited 0 in this situation -- hit for real
    while verifying the v2.10.0 release, where a failed `dotnet tool install`
    had left the target directory empty. "Nothing to check" is not
    "everything checked out"; a gate that cannot distinguish 0 files from 40
    is not a gate. Same reasoning as the catalog step in publish.yml, which
    already refuses to "succeed at signing nothing"."""
    roots_text = ", ".join(str(root) for root in roots)
    if not candidates:
        detail = (
            f"no .dll/.exe files found under {roots_text} "
            f"(ref/refint directories are excluded from the scan)"
        )
    else:
        detail = (
            f"found {len(candidates)} .dll/.exe file(s) under {roots_text}, "
            f"but none of them parsed as a PE image"
        )
    print(
        f"::error::{detail}. Nothing was classified as signed or unsigned, so "
        f"{consequence} Failing loudly instead of reporting success over an "
        "empty scan (#2298).",
        file=sys.stderr,
    )


def _cmd_list_unsigned(args: argparse.Namespace) -> int:
    exclude_dirs = DEFAULT_EXCLUDE_DIRS | set(args.exclude_dir)
    roots = [Path(r) for r in args.roots]
    relative_to = Path(args.relative_to).resolve()

    if _report_missing_roots(roots):
        return 1

    candidates, classified = _classify(roots, exclude_dirs)
    if not classified:
        # Note the distinction this does NOT break: zero *unsigned* files in
        # a tree that really was scanned stays a successful, empty catalog
        # (an already-signed tree). What fails here is zero *scanned* files --
        # a catalog built from a scan that saw nothing signable.
        _report_empty_scan(
            roots,
            candidates,
            "the signing catalog would be built from a scan that examined "
            "nothing, and the signing step would have no files to sign.",
        )
        return 1

    unsigned_paths = [path for path, signed in classified if not signed]

    lines = []
    for path in unsigned_paths:
        resolved = path.resolve()
        try:
            rel = os.path.relpath(resolved, relative_to)
        except ValueError as exc:
            # azure/artifact-signing-action joins every catalog entry onto
            # the workspace root before calling Resolve-Path (#2286), so an
            # absolute path here can never resolve on the signing runner --
            # measured in a real release run: both a literal absolute path
            # (doubled: D:\a\repo\repo\D:\a\repo\repo\out\bcdb.exe) and a Git
            # Bash /d/a/... path (mangled the same way) failed. A catalog
            # entry must be relative to the workspace; this file is on a
            # different drive from --relative-to, so it cannot be expressed
            # that way and cannot be signed from this catalog. Fail loudly
            # here instead of emitting a form the signing action can't read.
            print(
                f"::error::{resolved} cannot be made relative to {relative_to} "
                f"({exc}). A signing catalog entry must be relative to the "
                "workspace -- this file is on a different drive and cannot "
                "be signed from this catalog.",
                file=sys.stderr,
            )
            raise SystemExit(1) from exc
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

    if _report_missing_roots(roots):
        return 1

    candidates, classified = _classify(roots)
    if not classified:
        _report_empty_scan(
            roots,
            candidates,
            "this gate verified nothing at all.",
        )
        return 1

    total_pe = len(classified)
    unsigned = [path for path, signed in classified if not signed]

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
