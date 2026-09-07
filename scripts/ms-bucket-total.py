#!/usr/bin/env python3
"""Combined total across every bucket of one ms-bucket.yml run (issue #3409).

`.github/workflows/ms-surface.yml` runs all 32 non-empty Microsoft BaseApp buckets in ONE
job, sequentially, and each bucket's own numbers are already in the job summary by the time
the next one starts (scripts/ms-bucket-summary.py, called inside the loop). This script adds
the thing that is only knowable at the end: one total across the whole set, and the verdict.

WHAT IT IS DRIVEN BY, AND WHY THAT MATTERS
------------------------------------------
The REQUESTED bucket list, not the files that happen to be on disk. A bucket whose runner
process died before writing a JUnit leaves no directory at all, so a script that globbed for
results would report a confident total over the buckets that worked and never mention the
ones that did not. Driven by the request, a missing bucket is a named row and a non-zero
exit.

THE VERDICT, unchanged from ms-bucket.yml's existing contract: exit 0 when every requested
bucket produced a number (runner exit 0 or 1 AND a JUnit file), exit 1 when one did not.
Thousands of FAILING tests are the expected outcome of a Microsoft bucket and are reported,
never failed on.

--expected-tests is a SHORTFALL check, not a baseline. A bundle lost to COMPILE FAIL vanishes
from the totals rather than failing them (#2715), so a run can finish, report a number, and
have quietly measured two thirds of the surface. A total well below what the set is known to
hold says so — as a warning, because Microsoft's own counts move between BC versions and a
version bump must not read as a defect.

Usage:
  ms-bucket-total.py --results DIR --buckets FILE --bc-version V --test-data true|false
                     [--expected-tests N] [--step-summary FILE]
"""
import argparse
import os
import sys
import xml.etree.ElementTree as ET

# A bucket's results live in <results>/<slug>/, where the slug is the bucket name with the
# characters a directory name cannot carry replaced. Kept in step with ms-bucket.yml's
# `tr ' /' '__'` — bucket names really do contain spaces (Tests-Cash Flow).
SLUG_TRANSLATION = str.maketrans({" ": "_", "/": "_"})

# Below this fraction of --expected-tests, say so. Microsoft's counts drift a little between
# BC versions; losing a bundle loses whole percent of the surface at once.
SHORTFALL_FRACTION = 0.95


def slug(bucket):
    return bucket.translate(SLUG_TRANSLATION)


def read_buckets(path):
    """The requested bucket list, in order, blanks dropped."""
    with open(path, encoding="utf-8") as f:
        return [line.strip() for line in f if line.strip()]


def parse_junit_totals(xml_text):
    """Totals from a JUnit <testsuites> root, plus derived passed."""
    root = ET.fromstring(xml_text)
    if root.tag != "testsuites":
        raise ValueError(f"expected a <testsuites> root, got <{root.tag}>")
    totals = {k: int(root.attrib.get(k, "0")) for k in ("tests", "failures", "errors", "skipped")}
    totals["passed"] = totals["tests"] - totals["failures"] - totals["errors"] - totals["skipped"]
    return totals


def _read_int(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return int(f.read().strip())
    except (OSError, ValueError):
        return default


def collect(results_dir, buckets):
    """One record per REQUESTED bucket, in request order. `measured` is the verdict input."""
    out = []
    for bucket in buckets:
        d = os.path.join(results_dir, slug(bucket))
        rc = _read_int(os.path.join(d, "rc.txt"))
        elapsed = _read_int(os.path.join(d, "elapsed.txt"), 0)
        junit = os.path.join(d, "junit.xml")
        totals = None
        if os.path.exists(junit):
            try:
                with open(junit, encoding="utf-8") as f:
                    totals = parse_junit_totals(f.read())
            except (OSError, ET.ParseError, ValueError):
                totals = None
        out.append({
            "bucket": bucket,
            "rc": rc,
            "elapsed": elapsed or 0,
            "totals": totals,
            # Identical rule to ms-bucket-summary.measured: a number exists only when the
            # runner finished normally AND wrote JUnit.
            "measured": totals is not None and rc in (0, 1),
        })
    return out


def fmt_elapsed(seconds):
    seconds = int(seconds)
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    return f"{h}h {m}m {s}s" if h else f"{m}m {s}s"


def sum_totals(records):
    keys = ("tests", "passed", "failures", "errors", "skipped")
    out = {k: 0 for k in keys}
    for r in records:
        if r["totals"] is None:
            continue
        for k in keys:
            out[k] += r["totals"][k]
    return out


def shortfall(total_tests, expected_tests):
    """(is_short, missing) against --expected-tests; (False, 0) when no expectation given."""
    if not expected_tests or expected_tests <= 0:
        return False, 0
    return total_tests < expected_tests * SHORTFALL_FRACTION, expected_tests - total_tests


def compose(records, meta):
    with_td = "with --test-data" if meta["test_data"] else "without --test-data"
    combined = sum_totals(records)
    unmeasured = [r["bucket"] for r in records if not r["measured"]]
    wall = sum(r["elapsed"] for r in records)

    out = [f"## Combined total — {len(records)} bucket(s) on BC {meta['bc_version']} ({with_td})", ""]
    if len(records) > 1:
        out += ["| bucket | total | pass | fail | error | skipped | time |",
                "|---|---|---|---|---|---|---|"]
        for r in records:
            t = r["totals"]
            if t is None:
                out.append(f"| `{r['bucket']}` | **no number** (runner exit {r['rc']}) | | | | | "
                           f"{fmt_elapsed(r['elapsed'])} |")
            else:
                out.append(f"| `{r['bucket']}` | {t['tests']} | {t['passed']} | {t['failures']} | "
                           f"{t['errors']} | {t['skipped']} | {fmt_elapsed(r['elapsed'])} |")
        out.append(f"| **total** | **{combined['tests']}** | **{combined['passed']}** | "
                   f"**{combined['failures']}** | **{combined['errors']}** | "
                   f"**{combined['skipped']}** | **{fmt_elapsed(wall)}** |")
        out.append("")
    else:
        out += [f"{combined['tests']} tests — {combined['passed']} pass, {combined['failures']} fail, "
                f"{combined['errors']} error, {combined['skipped']} skipped, "
                f"in {fmt_elapsed(wall)}.", ""]

    short, missing = shortfall(combined["tests"], meta["expected_tests"])
    if short:
        out += [f"**{combined['tests']} tests is short of the {meta['expected_tests']} this set is "
                f"expected to hold — {missing} unaccounted for.** A bundle lost to COMPILE FAIL "
                "vanishes from the totals rather than failing them (#2715), so read the per-bucket "
                "caveats above before quoting this number. Microsoft's counts also move between BC "
                "versions, which is why this is a warning and not a failure.", ""]
    elif meta["expected_tests"]:
        out += [f"Expected about {meta['expected_tests']} tests for this set; measured "
                f"{combined['tests']}.", ""]

    if unmeasured:
        out += [f"**No number from {len(unmeasured)} of {len(records)} bucket(s):** "
                + ", ".join(f"`{b}`" for b in unmeasured) + ".",
                "", "The combined total above covers only the buckets that produced one.", ""]
    else:
        out += [f"Every one of the {len(records)} requested bucket(s) produced a number.", ""]
    return "\n".join(out), combined, unmeasured


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--results", required=True, help="directory holding <bucket-slug>/ result dirs")
    ap.add_argument("--buckets", required=True, help="file listing the REQUESTED buckets, one per line")
    ap.add_argument("--bc-version", required=True)
    ap.add_argument("--test-data", required=True, help="true|false")
    ap.add_argument("--expected-tests", type=int, default=0)
    ap.add_argument("--step-summary", default=None,
                    help="also append the markdown here; defaults to $GITHUB_STEP_SUMMARY when set")
    a = ap.parse_args(argv)

    buckets = read_buckets(a.buckets)
    if not buckets:
        print(f"::error title=No buckets::{a.buckets} lists no bucket, so there is nothing to total")
        return 1

    records = collect(a.results, buckets)
    meta = {"bc_version": a.bc_version,
            "test_data": a.test_data.strip().lower() == "true",
            "expected_tests": a.expected_tests}
    md, combined, unmeasured = compose(records, meta)

    print(md)
    step = a.step_summary or os.environ.get("GITHUB_STEP_SUMMARY")
    if step:
        with open(step, "a", encoding="utf-8") as f:
            f.write(md + "\n")

    if unmeasured:
        print(f"::error title=No measurement ({len(unmeasured)} of {len(buckets)} bucket(s) on BC "
              f"{a.bc_version})::" + ", ".join(unmeasured)
              + " produced no number — read each one's block in the job summary.")
        return 1

    short, missing = shortfall(combined["tests"], a.expected_tests)
    if short:
        print(f"::warning title=Short of the expected surface::measured {combined['tests']} tests "
              f"against about {a.expected_tests} expected — {missing} unaccounted for.")
    print(f"::notice title=Combined total on BC {a.bc_version}::{combined['tests']} tests — "
          f"{combined['passed']} pass, {combined['failures']} fail, {combined['errors']} error, "
          f"{combined['skipped']} skipped, across {len(buckets)} bucket(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
