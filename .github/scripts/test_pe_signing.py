"""
Tests for pe_signing.py -- #2248.

The byte-level tests build minimal, deterministic synthetic PE images rather
than relying on real DLLs from the NuGet cache or the .NET SDK install: the
whole point of these tests is pinning the *offset arithmetic* the parser uses
to find the Certificate Table entry for both the 32-bit (PE32) and 64-bit
(PE32+) optional-header shapes, and a hand-built fixture lets each test
assert an exact, known byte layout instead of hoping some machine-local file
happens to be in the right shape (NuGet-cache paths are version- and
machine-specific and would make this test non-portable to CI).

The CLI tests exercise the actual subprocess entry points (list-unsigned,
verify) against a small directory tree, proving the argparse wiring and the
ref/refint exclusion end to end, not just the internal functions.

Run directly: python3 .github/scripts/test_pe_signing.py
"""

import importlib.util
import os
import struct
import subprocess
import sys
import tempfile
import unittest
import unittest.mock
from pathlib import Path

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
SCRIPT_PATH = os.path.join(SCRIPT_DIR, 'pe_signing.py')

spec = importlib.util.spec_from_file_location('pe_signing', SCRIPT_PATH)
pe_signing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pe_signing)


def build_fake_pe(magic: int, cert_table_size: int, num_rva_and_sizes: int = 16) -> bytes:
    """Construct a minimal, structurally valid PE image with a controllable
    optional-header magic (0x10b = PE32, 0x20b = PE32+) and Certificate Table
    (data directory index 4) Size field. Only the fields the parser reads are
    populated meaningfully; everything else is zeroed.
    """
    assert magic in (0x10B, 0x20B)

    dos_header = bytearray(64)
    dos_header[0:2] = b"MZ"
    e_lfanew = 64
    struct.pack_into("<I", dos_header, 0x3C, e_lfanew)

    pe_signature = b"PE\x00\x00"

    # COFF header: Machine, NumberOfSections, TimeDateStamp,
    # PointerToSymbolTable, NumberOfSymbols, SizeOfOptionalHeader, Characteristics
    if magic == 0x10B:
        # PE32 standard fields (28) + Windows-specific fields (68) = 96
        # bytes before the DataDirectory array.
        standard_fields_size = 28
        windows_fields_size = 68
    else:
        # PE32+ standard fields (24, no BaseOfData) + Windows-specific
        # fields (88, ImageBase/SizeOfStack*/SizeOfHeap* widened to 8) = 112.
        standard_fields_size = 24
        windows_fields_size = 88

    data_directory_offset = standard_fields_size + windows_fields_size
    data_directories_size = num_rva_and_sizes * 8
    size_of_optional_header = data_directory_offset + data_directories_size

    coff_header = struct.pack(
        "<HHIIIHH",
        0x8664,  # Machine (AMD64 -- arbitrary, unused by the parser)
        1,       # NumberOfSections
        0,       # TimeDateStamp
        0,       # PointerToSymbolTable
        0,       # NumberOfSymbols
        size_of_optional_header,
        0,       # Characteristics
    )

    optional_header = bytearray(size_of_optional_header)
    struct.pack_into("<H", optional_header, 0, magic)
    struct.pack_into("<I", optional_header, standard_fields_size, num_rva_and_sizes)

    # NumberOfRvaAndSizes lives at the END of the Windows-specific fields,
    # immediately before DataDirectory -- but the parser doesn't read it (it
    # trusts size_of_optional_header instead), so leaving it zeroed above and
    # only writing it here for documentation purposes is fine. Overwrite is
    # a no-op duplicate write, kept for clarity that the field exists there.
    struct.pack_into(
        "<I",
        optional_header,
        data_directory_offset - 8,
        num_rva_and_sizes,
    )

    for i in range(num_rva_and_sizes):
        entry_offset = data_directory_offset + i * 8
        va = 0
        size = cert_table_size if i == pe_signing._CERT_TABLE_DIRECTORY_INDEX else 0
        struct.pack_into("<II", optional_header, entry_offset, va, size)

    return bytes(dos_header) + pe_signature + coff_header + bytes(optional_header)


def build_fake_pe_with_certificate_bytes(magic: int, certificate_bytes: bytes) -> bytes:
    """Like build_fake_pe, but backs the Certificate Table entry with REAL
    bytes appended right after the header -- arbitrary bytes, not a
    well-formed WIN_CERTIFICATE/PKCS#7 Authenticode blob. Used by
    PresenceCheckOnlyTests (#2284) to prove pe_signing's presence check
    accepts any nonzero-size Certificate Table entry, regardless of whether
    the bytes it points at are an actual signature.
    """
    assert magic in (0x10B, 0x20B)
    header = build_fake_pe(magic, cert_table_size=len(certificate_bytes))
    header_len = len(header)

    data_directory_offset = 96 if magic == 0x10B else 112
    optional_header_start = 64 + 4 + 20  # dos header + "PE\0\0" + COFF header
    entry_offset = (
        optional_header_start
        + data_directory_offset
        + pe_signing._CERT_TABLE_DIRECTORY_INDEX * 8
    )

    patched = bytearray(header)
    # VirtualAddress (a raw file offset for the Security directory, per the
    # comment on _CERT_TABLE_DIRECTORY_INDEX) points at the appended bytes.
    struct.pack_into("<II", patched, entry_offset, header_len, len(certificate_bytes))
    return bytes(patched) + certificate_bytes


class PresenceCheckOnlyTests(unittest.TestCase):
    """Documents the exact defect #2284 exists to cover: pe_signing's checks
    read only the Certificate Table's Size field. A table backed by bytes
    that are not a valid Authenticode signature at all -- arbitrary garbage,
    not even a well-formed WIN_CERTIFICATE header -- is still reported as
    signed. That is why publish.yml needs a REAL Get-AuthenticodeSignature
    check in addition to this one, not instead of it (this check stays,
    scoped to the ~84 files the workflow never touches -- see the
    module docstring)."""

    def test_verify_reports_signed_for_arbitrary_certificate_table_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            garbage = b"\x00\xde\xad\xbe\xef not a WIN_CERTIFICATE or a PKCS#7 blob at all"
            path = root / "fake-signed.dll"
            path.write_bytes(build_fake_pe_with_certificate_bytes(0x20B, garbage))

            # The internal check agrees...
            self.assertTrue(pe_signing.is_signed(path))

            # ...and so does the CLI gate that publish.yml runs as its
            # release-path check: it reports this file as signed and OK,
            # even though "signed" here means nothing more than "Certificate
            # Table Size is nonzero".
            result = subprocess.run(
                [sys.executable, SCRIPT_PATH, "verify", str(root)],
                capture_output=True, text=True,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("OK", result.stdout)


class CertificateTableSizeTests(unittest.TestCase):
    """Byte-level offset-arithmetic tests, PE32 and PE32+ each covered with
    both a signed and an unsigned fixture (positive/negative pair)."""

    def _write(self, tmp: str, name: str, data: bytes) -> Path:
        path = Path(tmp) / name
        path.write_bytes(data)
        return path

    def test_pe32_unsigned_reads_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = self._write(tmp, "a.dll", build_fake_pe(0x10B, cert_table_size=0))
            self.assertEqual(pe_signing.read_certificate_table_size(path), 0)
            self.assertFalse(pe_signing.is_signed(path))

    def test_pe32_signed_reads_nonzero_size(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = self._write(tmp, "a.dll", build_fake_pe(0x10B, cert_table_size=4096))
            self.assertEqual(pe_signing.read_certificate_table_size(path), 4096)
            self.assertTrue(pe_signing.is_signed(path))

    def test_pe32plus_unsigned_reads_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = self._write(tmp, "a.dll", build_fake_pe(0x20B, cert_table_size=0))
            self.assertEqual(pe_signing.read_certificate_table_size(path), 0)
            self.assertFalse(pe_signing.is_signed(path))

    def test_pe32plus_signed_reads_nonzero_size(self):
        # This is the shape every DLL this repo ships is actually built as
        # (AnyCPU/x64 -> PE32+) -- the case that matters most in practice.
        with tempfile.TemporaryDirectory() as tmp:
            path = self._write(tmp, "a.dll", build_fake_pe(0x20B, cert_table_size=10504))
            self.assertEqual(pe_signing.read_certificate_table_size(path), 10504)
            self.assertTrue(pe_signing.is_signed(path))

    def test_too_few_data_directories_is_treated_as_unsigned(self):
        # NumberOfRvaAndSizes < 5 means there's no room for a Security entry
        # at all -- no signature is possible, so this is "unsigned", not
        # "unclassifiable".
        with tempfile.TemporaryDirectory() as tmp:
            path = self._write(tmp, "a.dll", build_fake_pe(0x20B, cert_table_size=0, num_rva_and_sizes=2))
            self.assertEqual(pe_signing.read_certificate_table_size(path), 0)
            self.assertFalse(pe_signing.is_signed(path))

    def test_non_pe_file_is_unclassifiable_not_unsigned(self):
        # A random byte blob must never be reported as "unsigned" -- that
        # would be a false positive in the sign/verify catalog. It must be
        # skipped (None), distinctly from a real unsigned PE (False).
        with tempfile.TemporaryDirectory() as tmp:
            path = self._write(tmp, "not-a-pe.dll", b"this is not a PE file at all, just text bytes")
            self.assertIsNone(pe_signing.read_certificate_table_size(path))
            self.assertIsNone(pe_signing.is_signed(path))

    def test_truncated_dos_header_is_unclassifiable(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = self._write(tmp, "truncated.dll", b"MZ\x00\x00")
            self.assertIsNone(pe_signing.read_certificate_table_size(path))


class ScanExclusionTests(unittest.TestCase):
    """scan() must skip ref/refint directory components -- MSBuild's
    compile-only reference-assembly output, never shipped, so flagging it
    would just be catalog noise with no shipped consequence."""

    def test_ref_and_refint_directories_are_excluded(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "bin").mkdir()
            (root / "bin" / "app.dll").write_bytes(build_fake_pe(0x20B, 0))
            (root / "obj" / "ref").mkdir(parents=True)
            (root / "obj" / "ref" / "app.dll").write_bytes(build_fake_pe(0x20B, 0))
            (root / "obj" / "refint").mkdir(parents=True)
            (root / "obj" / "refint" / "app.dll").write_bytes(build_fake_pe(0x20B, 0))

            found = {str(p.relative_to(root)) for p in pe_signing.scan([root])}
            self.assertEqual(found, {os.path.join("bin", "app.dll")})


class ListUnsignedCliTests(unittest.TestCase):
    """End-to-end subprocess tests against the actual CLI entry point."""

    def _make_tree(self, root: Path):
        (root / "signed.dll").write_bytes(build_fake_pe(0x20B, cert_table_size=12345))
        (root / "unsigned.dll").write_bytes(build_fake_pe(0x20B, cert_table_size=0))
        (root / "not-a-pe.dll").write_bytes(b"plain text, not a PE image")
        (root / "obj" / "ref").mkdir(parents=True)
        (root / "obj" / "ref" / "unsigned-ref.dll").write_bytes(build_fake_pe(0x20B, cert_table_size=0))

    def test_list_unsigned_reports_only_the_real_unsigned_pe(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._make_tree(root)

            result = subprocess.run(
                [sys.executable, SCRIPT_PATH, "list-unsigned", str(root), "--relative-to", str(root)],
                capture_output=True, text=True, check=True,
            )
            lines = [l for l in result.stdout.splitlines() if l.strip()]
            self.assertEqual(lines, ["unsigned.dll"])

    def test_list_unsigned_writes_catalog_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._make_tree(root)
            catalog = root / "catalog.txt"

            subprocess.run(
                [sys.executable, SCRIPT_PATH, "list-unsigned", str(root),
                 "--relative-to", str(root), "--catalog", str(catalog)],
                capture_output=True, text=True, check=True,
            )
            self.assertEqual(catalog.read_text().strip(), "unsigned.dll")

    def test_verify_fails_when_unsigned_pe_present(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._make_tree(root)

            result = subprocess.run(
                [sys.executable, SCRIPT_PATH, "verify", str(root)],
                capture_output=True, text=True,
            )
            self.assertEqual(result.returncode, 1)
            self.assertIn("unsigned.dll", result.stderr)
            # The non-PE file and the ref/ file must not be blamed.
            self.assertNotIn("not-a-pe.dll", result.stderr)
            self.assertNotIn("unsigned-ref.dll", result.stderr)

    def test_verify_passes_when_everything_signed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "signed-a.dll").write_bytes(build_fake_pe(0x20B, cert_table_size=999))
            (root / "signed-b.exe").write_bytes(build_fake_pe(0x10B, cert_table_size=1))

            result = subprocess.run(
                [sys.executable, SCRIPT_PATH, "verify", str(root)],
                capture_output=True, text=True,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("OK", result.stdout)


class EmptyScanTests(unittest.TestCase):
    """#2298: a scan that classified NOTHING must not report success.

    `verify` used to print "OK: all 0 .dll/.exe file(s) checked are
    Authenticode-signed." and exit 0 for a directory holding no PE files at
    all -- hit for real while verifying the v2.10.0 release, where a failed
    `dotnet tool install` had left the target directory empty and the gate
    reported OK against nothing. A verifier that cannot tell "0 files, all
    good" from "40 files, all good" is not a gate. Same for `list-unsigned`:
    a catalog built from a scan that saw no PE files is not "nothing needed
    signing", it is "nothing was looked at".

    Both directions are covered here -- the empty/unclassifiable cases must
    fail, and the populated cases in the same class must still succeed, so a
    fix that simply always fails is caught.
    """

    def _verify(self, *roots):
        return subprocess.run(
            [sys.executable, SCRIPT_PATH, "verify", *[str(r) for r in roots]],
            capture_output=True, text=True,
        )

    def _list_unsigned(self, root, catalog=None):
        argv = [sys.executable, SCRIPT_PATH, "list-unsigned", str(root),
                "--relative-to", str(root)]
        if catalog is not None:
            argv += ["--catalog", str(catalog)]
        return subprocess.run(argv, capture_output=True, text=True)

    # --- verify: the vacuous passes that must now fail -------------------

    def test_verify_fails_on_directory_with_no_pe_files_at_all(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = self._verify(tmp)

            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertNotIn("OK", result.stdout)
            # The directory has to be named, so a caller can tell "nothing to
            # check" apart from "everything checked out".
            self.assertIn(tmp, result.stderr)
            self.assertIn("no .dll/.exe files", result.stderr)

    def test_verify_fails_when_every_candidate_is_not_a_pe_image(self):
        # 3 files matching the extension, none of them a PE image: the old
        # code counted only classifiable files, so this also printed
        # "all 0 ... checked". The message must distinguish this case from
        # the empty-directory one, because the fix is different (a broken
        # build output vs. a missing one).
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for name in ("a.dll", "b.dll", "c.exe"):
                (root / name).write_bytes(b"not a PE image at all, just text")

            result = self._verify(root)

            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertNotIn("OK", result.stdout)
            self.assertIn("3 .dll/.exe file(s)", result.stderr)
            self.assertIn("none of them parsed as a PE image", result.stderr)

    def test_verify_fails_when_the_only_pe_files_are_in_an_excluded_dir(self):
        # ref/ and refint/ are skipped by scan(), so a tree whose ONLY PE
        # files live there is verified against nothing -- same vacuous pass.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "obj" / "ref").mkdir(parents=True)
            (root / "obj" / "ref" / "app.dll").write_bytes(
                build_fake_pe(0x20B, cert_table_size=4096))

            result = self._verify(root)

            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn("no .dll/.exe files", result.stderr)

    def test_verify_fails_on_a_root_that_does_not_exist(self):
        # os.walk() on a missing path yields nothing and raises nothing, so
        # a typo'd or never-created root verified clean before this fix.
        with tempfile.TemporaryDirectory() as tmp:
            missing = Path(tmp) / "never-created"

            result = self._verify(missing)

            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertNotIn("OK", result.stdout)
            self.assertIn(str(missing), result.stderr)
            self.assertIn("does not exist", result.stderr)

    def test_verify_fails_when_one_of_several_roots_does_not_exist(self):
        # publish.yml passes four roots. A single missing one must not be
        # masked by the others having content.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            good = root / "good"
            good.mkdir()
            (good / "signed.dll").write_bytes(build_fake_pe(0x20B, cert_table_size=999))
            missing = root / "variants-staging"

            result = self._verify(good, missing)

            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn(str(missing), result.stderr)
            self.assertIn("does not exist", result.stderr)

    # --- verify: the control cases that must still pass ------------------

    def test_verify_still_reports_ok_and_the_real_count_for_signed_pes(self):
        # The other direction: a fix that just always fails is caught here,
        # and the reported count must be the real one (2), not a constant --
        # "all 0" is exactly the string this issue is about.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "signed-a.dll").write_bytes(build_fake_pe(0x20B, cert_table_size=999))
            (root / "signed-b.exe").write_bytes(build_fake_pe(0x10B, cert_table_size=1))
            # Unclassifiable and excluded files alongside them must not stop
            # the OK, since two real PE files WERE checked.
            (root / "not-a-pe.dll").write_bytes(b"plain text")
            (root / "obj" / "ref").mkdir(parents=True)
            (root / "obj" / "ref" / "skipped.dll").write_bytes(build_fake_pe(0x20B, 0))

            result = self._verify(root)

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("OK: all 2 .dll/.exe file(s)", result.stdout)

    def test_verify_of_a_single_signed_file_root_still_passes(self):
        # scan() accepts a file path directly; that path must not be caught
        # by the emptiness guard.
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "signed.dll"
            path.write_bytes(build_fake_pe(0x20B, cert_table_size=7))

            result = self._verify(path)

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("OK: all 1 .dll/.exe file(s)", result.stdout)

    # --- list-unsigned: same distinction ---------------------------------

    def test_list_unsigned_fails_on_directory_with_no_pe_files_and_writes_no_catalog(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            catalog = root / "catalog.txt"

            result = self._list_unsigned(root, catalog)

            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn(str(root), result.stderr)
            self.assertIn("no .dll/.exe files", result.stderr)
            # An empty catalog must not be left behind for the signing action
            # to consume -- the failure has to be the only outcome.
            self.assertFalse(catalog.exists(), "an empty catalog was written anyway")

    def test_list_unsigned_fails_on_a_root_that_does_not_exist(self):
        with tempfile.TemporaryDirectory() as tmp:
            missing = Path(tmp) / "never-created"

            result = self._list_unsigned(missing)

            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn(str(missing), result.stderr)
            self.assertIn("does not exist", result.stderr)

    def test_list_unsigned_still_succeeds_with_an_empty_listing_when_all_are_signed(self):
        # The distinction that makes this fix correct rather than merely
        # strict: zero UNSIGNED files in a tree that really was scanned is a
        # legitimate, successful outcome (an already-signed tree), and stays
        # exit 0 with an empty catalog.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "signed.dll").write_bytes(build_fake_pe(0x20B, cert_table_size=4096))
            catalog = root / "catalog.txt"

            result = self._list_unsigned(root, catalog)

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout.strip(), "")
            self.assertTrue(catalog.exists())
            self.assertEqual(catalog.read_text().strip(), "")


class ListUnsignedCrossDriveTests(unittest.TestCase):
    """#2286: when a path can't be made relative to --relative-to (the
    Windows cross-drive case), _cmd_list_unsigned must fail loudly instead
    of silently emitting an absolute path -- azure/artifact-signing-action
    joins every catalog entry onto the workspace root before Resolve-Path,
    so an absolute entry can never resolve on the signing runner. A real
    cross-drive path can't be produced on a single-drive Linux CI runner, so
    this drives the failure by making os.path.relpath raise ValueError
    directly, the same way it does for a real cross-drive pair on Windows.
    """

    def _make_tree(self, root: Path):
        (root / "unsigned.dll").write_bytes(build_fake_pe(0x20B, cert_table_size=0))

    def test_relpath_valueerror_fails_loudly_naming_path_and_reason(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._make_tree(root)
            args = pe_signing.argparse.Namespace(
                roots=[str(root)],
                relative_to=str(root),
                exclude_dir=[],
                catalog=None,
            )

            def raising_relpath(_path, _start):
                raise ValueError("path is on mount 'D:', start on mount 'C:'")

            captured_stderr = __import__("io").StringIO()
            with unittest.mock.patch.object(
                pe_signing.os.path, "relpath", side_effect=raising_relpath
            ), unittest.mock.patch("sys.stderr", captured_stderr):
                with self.assertRaises(SystemExit) as cm:
                    pe_signing._cmd_list_unsigned(args)

            self.assertEqual(cm.exception.code, 1)
            message = captured_stderr.getvalue()
            self.assertIn("unsigned.dll", message)
            self.assertIn("relative to", message)
            self.assertIn("different drive", message)

    def test_normal_tree_without_the_valueerror_still_emits_relative_paths(self):
        # The un-mocked control case, in the same class as the failure case,
        # so both directions of this behaviour live together.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._make_tree(root)
            args = pe_signing.argparse.Namespace(
                roots=[str(root)],
                relative_to=str(root),
                exclude_dir=[],
                catalog=None,
            )

            captured_stdout = __import__("io").StringIO()
            with unittest.mock.patch("sys.stdout", captured_stdout):
                result = pe_signing._cmd_list_unsigned(args)

            self.assertEqual(result, 0)
            self.assertEqual(captured_stdout.getvalue().strip(), "unsigned.dll")


if __name__ == '__main__':
    unittest.main()
