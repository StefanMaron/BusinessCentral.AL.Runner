#!/usr/bin/env python3
"""Job summary for .github/workflows/ms-bucket.yml (issue #2724).

The manual Microsoft-bucket run's deliverable is one readable block: total / pass / fail /
error, wall time, and every line of the runner's output that means the number is not what
it looks like. Two of those are known at the time of writing:

  * a bundle lost to COMPILE FAIL vanishes from the totals (#2715 — #2721 adds a
    "NOT RUN: N bundle(s)" line to the aggregate);
  * a run resumed after a watchdog abort under-reports unless the earlier attempts are
    carried forward (#2716 — #2720), which the runner announces with "resume:" lines and a
    "(carried from earlier attempt(s): …)" line in the summary.

So the caveat scan is deliberately broad and verbatim: any line matching one of the
markers below is copied into the summary as-is. A reader must never have to open the log
to learn that 9,496 became 7,000 because a codeunit hung.

Exit code: 0 when the run produced a number (runner exit 0 or 1 AND a JUnit file), 1
otherwise — so the workflow step goes red exactly when there is no measurement, and stays
green for the expected outcome of a bucket with thousands of failing tests.

Usage:
  ms-bucket-summary.py --log run.log --rc N --elapsed SECONDS --bucket B --bc-version V
                       --test-data true|false [--reader TAG] [--junit junit.xml] [--out FILE]
                       [--step-summary FILE]
Appends the same markdown to --step-summary, defaulting to $GITHUB_STEP_SUMMARY when set.
"""
import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

# Every line matching one of these is a caveat, copied verbatim. Anchored where the runner's
# own text is stable (Reporter.cs summary block, AbortResume.cs messages, the per-bundle
# status lines), tolerant of leading whitespace.
# The markers Program.cs puts on a bundle-level suite-error line; kept in step with
# AlRunner/Infrastructure/BundleFailureStage.cs, which classifies the same set.
BUNDLE_ERROR_MARKERS = (
    "EMIT-TIMEOUT", "EMIT-FAIL", "EMIT-EXCLUDED", "EMIT-ZERO", "PARTIAL-EMIT-DROP",
    "AL-DIAGNOSTIC-FAIL", "COMPILE-FAIL", "EXEC-FAIL", "TEST-TIMEOUT-ABORT",
)

CAVEAT_PATTERNS = (
    # Reporter.PrintPerTest's per-bundle header: "=== <bundle> — COMPILE FAIL ===" (em dash).
    # This used to be anchored as r"^\s*COMPILE FAIL\b", which matches nothing the runner
    # actually prints — the bundle's NAME never reached the summary (#2779). The test that
    # covered it fed a hand-written line in a shape the runner does not emit.
    re.compile(r"^\s*=== .* (?:COMPILE|EXEC) FAIL ==="),
    # The suite-error lines under that header — the actual diagnosis, e.g.
    # "Tests-ERM: EXEC-FAIL: the backup reader failed (exit 1): block 116504 …". Without
    # these the summary said a bundle failed and never said why, which is what made the
    # first ms-bucket run take several steps to diagnose.
    re.compile(r"^\s*\S.*?: (?:" + "|".join(BUNDLE_ERROR_MARKERS) + r")\b"),
    re.compile(r"^\s*NOT RUN:"),
    re.compile(r"^\s*resume:"),
    re.compile(r"carried from earlier attempt"),
    re.compile(r"^\s*compile-fail:\s*[1-9]"),
    re.compile(r"^\s*exec-fail:\s*[1-9]"),
)

SUMMARY_START = "al-runner — test run summary"

RC_MEANING = {
    0: "all tests passed",
    1: "at least one test failed or errored (the expected outcome for a Microsoft bucket)",
    2: "a bundle could not execute (process-level error)",
    3: "a bundle could not compile",
    4: "count-baseline mismatch",
    134: "crash (SIGABRT)",
    139: "crash (SIGSEGV)",
}


def parse_junit_totals(xml_text):
    """Totals from the JUnit root: tests/failures/errors/skipped, plus derived passed."""
    root = ET.fromstring(xml_text)
    if root.tag != "testsuites":
        raise ValueError(f"expected a <testsuites> root, got <{root.tag}>")
    totals = {k: int(root.attrib.get(k, "0")) for k in ("tests", "failures", "errors", "skipped")}
    totals["passed"] = totals["tests"] - totals["failures"] - totals["errors"] - totals["skipped"]
    return totals


def scan_log(log_text):
    """Caveat lines (verbatim) and the runner's own summary block, if it printed one."""
    lines = log_text.splitlines()
    caveats = [l.rstrip() for l in lines if any(p.search(l) for p in CAVEAT_PATTERNS)]
    summary_block = ""
    for i, line in enumerate(lines):
        if line.startswith(SUMMARY_START):
            block = []
            for l in lines[i:]:
                if block and not l.strip():
                    break
                block.append(l.rstrip())
            summary_block = "\n".join(block)
    return {"caveats": caveats, "summary_block": summary_block}


def measured(rc, have_junit):
    """A run produced a number only if the runner finished normally and wrote JUnit."""
    return have_junit and rc in (0, 1)


def fmt_elapsed(seconds):
    seconds = int(seconds)
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    return f"{h}h {m}m {s}s" if h else f"{m}m {s}s"


def compose(meta, totals, scan, rc, elapsed_s):
    with_td = "with --test-data" if meta.get("test_data") else "without --test-data"
    out = [f"## Microsoft bucket `{meta['bucket']}` on BC {meta['bc_version']} ({with_td})", ""]
    rc_text = RC_MEANING.get(rc, "unexpected exit code")
    out.append(f"Runner exit code {rc} — {rc_text}. Wall time {fmt_elapsed(elapsed_s)} for the runner step.")
    if meta.get("reader"):
        # BusinessCentral.DbReader, not .BakReader: that repository was renamed and the old
        # name survives only through GitHub's rename redirect. This line is read by a human
        # deciding where to go look, so an old name sends them to a redirect that may not be
        # there later.
        out.append(f"Backup reader: BusinessCentral.DbReader {meta['reader']}.")
    out.append("")
    if totals is None:
        out += ["**No number: no JUnit file was produced.** The runner did not get as far as running tests"
                " — read the log artifact (`run.log`) and the caveats below.", ""]
    else:
        out += ["| total | pass | fail | error | skipped |",
                "|---|---|---|---|---|",
                f"| {totals['tests']} | {totals['passed']} | {totals['failures']} | {totals['errors']} | {totals['skipped']} |",
                ""]
        if not measured(rc, True):
            out += [f"**Treat the table with suspicion:** exit code {rc} means the run did not finish normally;"
                    " the JUnit covers only what ran before that.", ""]
    if scan["caveats"]:
        out += ["### Caveats (verbatim from the runner)", "",
                "Each line below means the table is not one clean run's number:", "", "```"]
        out += scan["caveats"]
        out += ["```", ""]
    else:
        out += ["No caveats: no COMPILE FAIL / EXEC FAIL / NOT RUN / resume lines in the runner output.", ""]
    if scan["summary_block"]:
        out += ["<details><summary>Runner summary block</summary>", "", "```"]
        out.append(scan["summary_block"])
        out += ["```", "", "</details>", ""]
    return "\n".join(out)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--log", required=True)
    ap.add_argument("--rc", type=int, required=True)
    ap.add_argument("--elapsed", type=int, required=True)
    ap.add_argument("--bucket", required=True)
    ap.add_argument("--bc-version", required=True)
    ap.add_argument("--test-data", required=True, help="true|false")
    ap.add_argument("--reader", default="")
    ap.add_argument("--junit", default=None)
    ap.add_argument("--out", default=None)
    ap.add_argument("--step-summary", default=None,
                    help="also append the markdown here; defaults to $GITHUB_STEP_SUMMARY when set")
    a = ap.parse_args(argv)

    with open(a.log, encoding="utf-8", errors="replace") as f:
        scan = scan_log(f.read())

    totals = None
    if a.junit and os.path.exists(a.junit):
        with open(a.junit, encoding="utf-8") as f:
            totals = parse_junit_totals(f.read())

    meta = {"bucket": a.bucket, "bc_version": a.bc_version,
            "test_data": a.test_data.strip().lower() == "true", "reader": a.reader}
    md = compose(meta, totals, scan, a.rc, a.elapsed)

    print(md)
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            f.write(md + "\n")
    step = a.step_summary or os.environ.get("GITHUB_STEP_SUMMARY")
    if step:
        with open(step, "a", encoding="utf-8") as f:
            f.write(md + "\n")

    return 0 if measured(a.rc, totals is not None) else 1


if __name__ == "__main__":
    sys.exit(main())
