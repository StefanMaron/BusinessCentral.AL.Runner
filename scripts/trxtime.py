"""Parse a VSTest TRX timestamp on any Python >= 3.9.

`datetime.fromisoformat` only became liberal in 3.11. Before that it accepts a fractional
second of EXACTLY 3 or 6 digits and rejects a `Z` suffix outright. VSTest writes .NET's
round-trip format, which is SEVEN digits, and writes `Z` for a UTC clock — so every gate
here that reads a TRX crashed with `Invalid isoformat string` on any locally produced file
under an older interpreter (macOS ships 3.9), while CI's 3.12 parsed the identical file.

Normalising before handing the string over costs nothing on a new interpreter and makes the
old one work. The reports that use this sum whole seconds and bucket in tenths, so the
discarded 100-nanosecond tick cannot change an answer.
"""
import re
from datetime import datetime

# .NET's round-trip format: 2026-09-03T19:56:06.1234567+02:00, or with `Z`, or with no zone.
_FRACTION = re.compile(r"\.(\d+)")
_TRAILING_Z = re.compile(r"Z$")

# Exactly what a pre-3.11 `fromisoformat` accepts, so a test can assert the normalised form
# rather than only that the current interpreter happens to parse it.
LEGACY_ISO = re.compile(
    r"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d{6})?([+-]\d{2}:\d{2})?$")


def normalize_trx_time(value):
    """The same instant, spelled the way every Python >= 3.9 accepts.

    The fraction is padded or truncated to exactly six digits rather than only truncated: a
    five-digit fraction is as unparseable on 3.9 as an eight-digit one.
    """
    value = _TRAILING_Z.sub("+00:00", value.strip())
    return _FRACTION.sub(lambda m: "." + m.group(1)[:6].ljust(6, "0"), value, count=1)


def parse_trx_time(value):
    """Parse a VSTest TRX timestamp into a datetime."""
    return datetime.fromisoformat(normalize_trx_time(value))
