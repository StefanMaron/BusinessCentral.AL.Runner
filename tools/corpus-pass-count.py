#!/usr/bin/env python3
"""Did a corpus PR's tests actually RUN? Per-leg distinct counts, in one call.

A green tick does not prove execution. Corpus PR #220 carried 32 new tests, all
16 legs green, and **none of the 32 ever ran** -- BC's per-object codegen had
failed and the other 2639 tests carried the run. Checking that the tests you
added actually executed is therefore a real step, not ceremony.

Doing that check by hand has produced a wrong answer -- always *zero*, always in
the shape of a result -- by four separate mechanisms (#3311). This tool exists
because a rule tells an agent what to do and a tool does it for them:

  tools/corpus-pass-count.py <run-id> <prefix>

It answers three questions that a grep conflates into one number:

  * how many DISTINCT test names matching <prefix> passed, per leg;
  * which legs ran the codeunit at all, versus which never saw it;
  * whether a zero means "the codeunit did not run here" or "your pattern
    matched nothing anywhere" -- different answers, and conflating them is the
    whole defect this closes.

Measured properties of the corpus log this parser is built on (run 34079169063,
corpus PR #227, and run 34073808538 for the failing spellings):

  BC 27.x:  `    PASS  Name`                 two spaces, no timing
  BC 28.x:  `      PASS Name (675ms)`        one space, a duration
  BC 27.x:  `    FAIL  Name - detail`
  BC 28.x:  `      FAIL Name (308ms)`

A pattern written against either fixed spelling reports 0 on the other half of
the matrix while those legs are green and executing. `PASS +<prefix>` -- a `+`
quantifier, never a literal run of spaces -- matches both.

The eight OnPrem legs run a different, much smaller suite (29 tests on that run
against 2915 cloud) and have run none of the recent cloud additions. They are
green for unrelated reasons, so a 0 there is expected and is reported as
`not-run`, not as a failure.

Run: tools/corpus-pass-count.py 34079169063 TestPart_
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
import time

CORPUS = "StefanMaron/BusinessCentral.AL.Language.Tests"

# One space or many, so both the 27.x and the 28.x spelling match. A `+`, never
# a fixed count -- the fixed count IS the bug (#3311 variant 1).
_RESULT = re.compile(r"\b(PASS|FAIL)\s+([A-Za-z_][A-Za-z0-9_]*)")

# The harness's own per-leg total, used as an INDEPENDENT second query: it tells
# a leg that ran a suite apart from one that ran nothing at all.
_TOTALS = re.compile(
    r"(\d+) total, (\d+) passed, (\d+) failed, (\d+) skipped")

TRANSIENT = ("i/o timeout", "connection reset", "502 Bad", "dial tcp",
             "could not connect", "TLS handshake")


def gh(args: list[str], attempts: int = 4) -> tuple[int, str]:
    """Run gh, retrying transient network failures. Returns (rc, stdout)."""
    last = ""
    for i in range(attempts):
        p = subprocess.run(["gh", *args], capture_output=True, text=True)
        out = (p.stdout or "") + (p.stderr or "")
        # mise prints a banner on stdout; drop it so JSON parses.
        out = "\n".join(l for l in out.split("\n") if not l.startswith("mise "))
        if not any(t.lower() in out.lower() for t in TRANSIENT):
            return p.returncode, out.strip()
        last = out
        time.sleep(3 * (i + 1))
    return 1, last.strip()


def parse_leg(log: str, prefix: str) -> dict:
    """Distinct PASS/FAIL names matching `prefix`, plus the harness's own total.

    Distinct NAMES, not matching lines: a retried or re-echoed line would
    otherwise inflate the figure, and an inflated count is as wrong as a zero.

    `total` is None when the log carries no summary line -- which is what a leg
    that never reached the test phase looks like, and is exactly the case that
    must not be reported as "ran the suite, found none of yours".
    """
    passed: set[str] = set()
    failed: set[str] = set()
    all_names: set[str] = set()
    for verdict, name in _RESULT.findall(log):
        all_names.add(name)
        if not name.startswith(prefix):
            continue
        (passed if verdict == "PASS" else failed).add(name)

    total = None
    for m in _TOTALS.finditer(log):
        total = {"total": int(m.group(1)), "passed": int(m.group(2)),
                 "failed": int(m.group(3)), "skipped": int(m.group(4))}
    return {"passed": sorted(passed), "failed": sorted(failed),
            "suite_names": len(all_names), "total": total}


def classify(leg: dict) -> str:
    """Why is this leg's count what it is?

    The distinction this tool exists for. A zero has three different meanings and
    a bare grep gives all three the same answer:

      ran      - the codeunit executed here
      failed   - it executed and something did not pass
      not-run  - this leg ran a suite, and the codeunit was not part of it
      no-suite - this leg never reached the test phase; a 0 here says nothing
                 about your tests at all
    """
    if leg["failed"]:
        return "failed"
    if leg["passed"]:
        return "ran"
    if leg["total"] is None and leg["suite_names"] == 0:
        return "no-suite"
    return "not-run"


def fetch_jobs(run_id: str) -> list[dict]:
    rc, out = gh(["api", f"repos/{CORPUS}/actions/runs/{run_id}/jobs?per_page=100",
                  "--jq", '.jobs[] | "\\(.id)\t\\(.name)\t\\(.conclusion)"'])
    if rc != 0:
        print(f"could not list jobs for run {run_id}:\n{out}", file=sys.stderr)
        sys.exit(3)
    jobs = []
    for line in out.splitlines():
        parts = line.split("\t")
        if len(parts) == 3 and "/ test" in parts[1]:
            jobs.append({"id": parts[0], "name": parts[1], "conclusion": parts[2]})
    return jobs


def fetch_log(job_id: str) -> str:
    """A job's raw log.

    `--allow-escape-sequences` is REQUIRED and its absence is a silent false
    zero of its own: the corpus log carries ANSI colour, and without the flag gh
    writes **nothing** to stdout and still exits 0. Redirected to a file that is
    an empty file and a "no matches" answer, not an error anyone notices.
    """
    rc, out = gh(["api", "--allow-escape-sequences",
                  f"repos/{CORPUS}/actions/jobs/{job_id}/logs"])
    if rc != 0 or not out.strip():
        return ""
    return out


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Per-leg distinct pass counts for a corpus run, done right.")
    ap.add_argument("run_id", help="corpus Actions run id")
    ap.add_argument("prefix", help="test-name prefix, read from the .al file -- never guessed")
    ap.add_argument("--repo", default=CORPUS, help=argparse.SUPPRESS)
    args = ap.parse_args()

    globals()["CORPUS"] = args.repo

    jobs = fetch_jobs(args.run_id)
    if not jobs:
        print(f"run {args.run_id} has no '/ test' legs -- wrong run id, or it "
              f"never got past prepare", file=sys.stderr)
        return 3

    rows = []
    for job in jobs:
        log = fetch_log(job["id"])
        if not log:
            rows.append((job, None, "log-unavailable"))
            continue
        leg = parse_leg(log, args.prefix)
        rows.append((job, leg, classify(leg)))

    width = max(len(j["name"]) for j in jobs)
    ran = [r for r in rows if r[2] == "ran"]
    failed = [r for r in rows if r[2] == "failed"]
    notrun = [r for r in rows if r[2] == "not-run"]

    print(f"run {args.run_id}  prefix {args.prefix!r}\n")
    for job, leg, kind in rows:
        if leg is None:
            print(f"  {job['name']:<{width}}  log-unavailable")
            continue
        suite = (f"suite {leg['total']['passed']}/{leg['total']['total']}"
                 if leg["total"] else "no suite total in log")
        n = len(leg["passed"])
        note = {"ran": "", "failed": f" ({len(leg['failed'])} FAILED)",
                "not-run": "  <- codeunit not in this leg's suite",
                "no-suite": "  <- leg never reached the test phase"}[kind]
        print(f"  {job['name']:<{width}}  {n:>4} distinct PASS  [{suite}]{note}")

    print()
    counts = {len(r[1]['passed']) for r in ran}
    if failed:
        for job, leg, _ in failed:
            print(f"FAILING on {job['name']}: {', '.join(leg['failed'])}")
        print()

    if not ran and not failed:
        # THE case this tool exists for. Every leg zero is not "the tests did
        # not run" until you have shown the prefix appears somewhere.
        anywhere = any(r[1] and r[1]["suite_names"] for r in rows)
        print(f"NO leg ran any test matching {args.prefix!r}.")
        if anywhere:
            print("  Legs DID run suites, so either the codeunit genuinely did "
                  "not execute\n  or the prefix is wrong. Read it out of the "
                  ".al file -- do not infer it from\n  the feature name (#3311 "
                  "variant 2). Then re-check.")
        else:
            print("  No leg produced any PASS/FAIL line at all, so this says "
                  "nothing about\n  your tests -- the run did not reach the "
                  "test phase.")
        return 1

    print(f"{len(ran)} leg(s) ran the codeunit; {len(notrun)} ran a suite "
          f"without it; {len(failed)} had failures.")
    if len(counts) > 1:
        print(f"WARNING: legs disagree on how many passed: {sorted(counts)} -- "
              f"a leg short of the others\n  is a real finding, not a log-format "
              f"artifact (this tool matches both spellings).")
        return 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
