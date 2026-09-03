#!/usr/bin/env python3
"""Unit tests for scripts/trxtime.py.

VSTest writes .NET's round-trip timestamp format: seven fractional digits, and `Z` for a
UTC clock. `datetime.fromisoformat` only became liberal in Python 3.11 — before that it
accepts a fraction of exactly 3 or 6 digits and rejects `Z` outright — so both TRX gates
crashed with `Invalid isoformat string` on any locally produced file under an older
interpreter (macOS ships 3.9) while CI's 3.12 parsed the identical file.

I could not run 3.9 here, so these do not measure that interpreter. They pin the two things
that make the claim checkable without it: every timestamp shape a TRX can contain parses,
and the NORMALISED string matches the grammar a pre-3.11 fromisoformat accepts, spelled out
as a regex rather than left to whatever the current interpreter happens to tolerate.

Run: python3 scripts/tests/trxtime.test.py
"""
import importlib.util
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parent.parent / "trxtime.py"
_spec = importlib.util.spec_from_file_location("trxtime", SCRIPT_PATH)
trxtime = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(trxtime)


# Every shape a TRX startTime/endTime is known to take, and the exact instant each denotes.
SHAPES = [
    # .NET round-trip: seven fractional digits with an offset. The one that crashed 3.9.
    ("2026-09-03T19:56:06.1234567+02:00", datetime(2026, 9, 3, 19, 56, 6, 123456,
                                                   timezone(timedelta(hours=2)))),
    # Seven digits with a Z suffix — the other half of the same crash.
    ("2026-09-03T19:56:06.1234567Z", datetime(2026, 9, 3, 19, 56, 6, 123456, timezone.utc)),
    # Seven digits, no zone at all.
    ("2026-09-03T19:56:06.1234567", datetime(2026, 9, 3, 19, 56, 6, 123456)),
    # Exactly six: already legal everywhere, and must not be altered.
    ("2026-09-03T19:56:06.123456+02:00", datetime(2026, 9, 3, 19, 56, 6, 123456,
                                                  timezone(timedelta(hours=2)))),
    # Five: padded, not truncated. Five is as unparseable on 3.9 as eight.
    ("2026-09-03T19:56:06.12345Z", datetime(2026, 9, 3, 19, 56, 6, 123450, timezone.utc)),
    # Three: legal on 3.9 too, and must keep its value.
    ("2026-09-03T19:56:06.123Z", datetime(2026, 9, 3, 19, 56, 6, 123000, timezone.utc)),
    # No fraction at all.
    ("2026-09-03T19:56:06Z", datetime(2026, 9, 3, 19, 56, 6, tzinfo=timezone.utc)),
    ("2026-09-03T19:56:06", datetime(2026, 9, 3, 19, 56, 6)),
]


class ParseTests(unittest.TestCase):
    def test_every_trx_timestamp_shape_parses_to_the_right_instant(self):
        for raw, expected in SHAPES:
            with self.subTest(raw=raw):
                self.assertEqual(trxtime.parse_trx_time(raw), expected)

    def test_z_and_explicit_utc_offset_are_the_same_instant(self):
        self.assertEqual(trxtime.parse_trx_time("2026-09-03T19:56:06.1234567Z"),
                         trxtime.parse_trx_time("2026-09-03T19:56:06.1234567+00:00"))

    def test_a_duration_between_two_stamps_survives_the_normalisation(self):
        start = trxtime.parse_trx_time("2026-09-03T19:56:06.0000000Z")
        end = trxtime.parse_trx_time("2026-09-03T19:56:08.5000000Z")
        self.assertEqual((end - start).total_seconds(), 2.5)


class NormalizeTests(unittest.TestCase):
    def test_normalised_form_matches_the_grammar_a_pre_311_parser_accepts(self):
        """The claim this whole module rests on, stated as a grammar rather than as
        'the interpreter I happen to be running did not complain'."""
        for raw, _ in SHAPES:
            with self.subTest(raw=raw):
                normalized = trxtime.normalize_trx_time(raw)
                self.assertRegex(normalized, trxtime.LEGACY_ISO)

    def test_a_seven_digit_fraction_is_truncated_to_six(self):
        self.assertEqual(trxtime.normalize_trx_time("2026-09-03T19:56:06.1234567"),
                         "2026-09-03T19:56:06.123456")

    def test_a_short_fraction_is_padded_to_six(self):
        self.assertEqual(trxtime.normalize_trx_time("2026-09-03T19:56:06.12"),
                         "2026-09-03T19:56:06.120000")

    def test_a_trailing_z_becomes_an_explicit_utc_offset(self):
        self.assertTrue(trxtime.normalize_trx_time("2026-09-03T19:56:06Z").endswith("+00:00"))

    def test_an_explicit_offset_is_left_alone(self):
        self.assertTrue(trxtime.normalize_trx_time("2026-09-03T19:56:06-05:00").endswith("-05:00"))

    def test_the_grammar_rejects_what_a_pre_311_parser_rejects(self):
        """The negative direction: LEGACY_ISO is only useful as a check if it actually
        excludes the two forms that crashed."""
        self.assertNotRegex("2026-09-03T19:56:06.1234567+02:00", trxtime.LEGACY_ISO)
        self.assertNotRegex("2026-09-03T19:56:06Z", trxtime.LEGACY_ISO)


if __name__ == "__main__":
    unittest.main(verbosity=2)
