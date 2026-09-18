#!/usr/bin/env bash
# Runs a set of guard suites and reports each one's verdict by BUCKET, so that a
# guard which CAUGHT a defect stays distinguishable from one that MEASURED
# NOTHING. See #4359.
#
# Extracted out of pr-gate.yml's inline `run:` blocks (tools-tests and
# github-scripts-tests) so the loop is unit-testable directly -- the same
# extraction as check_ci_skip_directives.sh and its siblings. The workflow jobs
# call this; test_run_guard_suites.sh drives it with stub suites.
#
# Usage:
#   run_guard_suites.sh <label> <suite> [<suite> ...]
#
# <label> names the family in the messages ("tools/test_*.py"). Each <suite> is
# dispatched by extension: *.py via python3, anything else via bash.
#
# Exit-code contract, which is the whole point of the script:
#
#   0  every suite passed
#   1  at least one suite failed or could not be measured -- the job fails
#   2  usage error (no suites given); a caller handing this nothing would
#      otherwise report success having run not one line
#
# and per suite, the four buckets it sorts a suite's own exit code into:
#
#   0        PASS         the suite measured, and is happy
#   1        FAIL         the suite measured, and caught something
#   3        UNMEASURED   the suite could not measure (guards-need-a-third-state.md)
#   other    ABNORMAL     the suite did not return a verdict at all: killed
#                         (137 = SIGKILL/OOM, 139 = SIGSEGV), a missing
#                         interpreter (127), or an exit code that means nothing
#                         in this convention (2, ...)
#
# WHY UNMEASURED STILL FAILS THE JOB, AND WHY THAT IS NOT THE BUG BEING FIXED.
# A guard that could not measure because the box lacks a tool is not a defect in
# the pull request under test, so it must not be the thing that FAILS the job on
# its own merits -- but it must never be invisible either, which is the state
# #4359 reports. This script separates the two concerns: the log always says
# which bucket each suite landed in and why, and the job's exit code remains
# "anything non-zero ⇒ fail" so no genuine failure can be lost behind an
# UNMEASURED. If the repository later decides an UNMEASURED should not fail CI,
# that is a one-line change HERE with a test beside it, rather than a property
# nobody can observe.
#
# ABNORMAL is deliberately its own bucket rather than being folded into FAIL.
# Folding it in would rebuild #4359's defect one level up: an OOM-killed suite
# and a suite that caught a real defect would print the same thing, and an agent
# reading the log would go looking for an assertion that never fired.

set -uo pipefail

label="${1-}"
if [ -z "$label" ]; then
  echo "::error::run_guard_suites.sh: usage: run_guard_suites.sh <label> <suite>..." >&2
  exit 2
fi
shift

if [ "$#" -eq 0 ]; then
  echo "::error::no $label found -- this job would pass without running anything" >&2
  exit 2
fi

failed=()
unmeasured=()
abnormal=()
passed=0

for s in "$@"; do
  echo "=== $s"
  case "$s" in
    *.py) python3 "$s" ;;
    *)    bash "$s" ;;
  esac
  rc=$?
  case "$rc" in
    0) passed=$((passed + 1)) ;;
    1) echo "--- FAIL $s (exit 1): the suite measured and caught something"
       failed+=("$s") ;;
    3) echo "--- UNMEASURED $s (exit 3): the suite could not measure -- this is NOT a defect in this pull request; see .claude/rules/guards-need-a-third-state.md"
       unmeasured+=("$s") ;;
    *) echo "--- ABNORMAL $s (exit $rc): not a verdict in this convention (0 pass / 1 fail / 3 could-not-measure) -- killed, crashed, or a code that means nothing here"
       abnormal+=("$s") ;;
  esac
done

echo
echo "=== $label summary: ${passed} passed, ${#failed[@]} failed, ${#unmeasured[@]} unmeasured, ${#abnormal[@]} abnormal"

if [ "${#unmeasured[@]}" -gt 0 ]; then
  # A ::warning:: rather than an ::error::: an UNMEASURED is a fact about the
  # BOX, not about the diff, so it must not read as this pull request's fault --
  # while still being impossible to miss in the annotations.
  echo "::warning::${#unmeasured[@]} $label suite(s) COULD NOT MEASURE (exit 3): ${unmeasured[*]} -- a tool they need is missing from this runner; they caught nothing and proved nothing"
fi
if [ "${#abnormal[@]}" -gt 0 ]; then
  echo "::error::${#abnormal[@]} $label suite(s) exited ABNORMALLY: ${abnormal[*]} -- neither a pass, a failure, nor a could-not-measure"
fi
if [ "${#failed[@]}" -gt 0 ]; then
  echo "::error::${#failed[@]} $label suite(s) FAILED: ${failed[*]}"
fi

if [ "${#failed[@]}" -gt 0 ] || [ "${#unmeasured[@]}" -gt 0 ] || [ "${#abnormal[@]}" -gt 0 ]; then
  exit 1
fi
exit 0
