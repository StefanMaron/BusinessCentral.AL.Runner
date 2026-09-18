#!/usr/bin/env bash
# Tests for run_guard_suites.sh -- #4359: pr-gate.yml's `python3 "$s" || rc=1`
# mapped EVERY non-zero to 1, so a guard that CAUGHT a defect and a guard that
# MEASURED NOTHING produced identical output and an identical exit code.
#
# These drive the real script with stub suites that exit 0, 1, 3 and codes that
# are none of those, in both orders, because the ordering is where a naive fix
# loses a verdict: a 3 after a 1 must not overwrite the 1, and a 1 after a 3
# must not hide the 3.
#
# Run directly: bash .github/scripts/test_run_guard_suites.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/run_guard_suites.sh"

pass=0
fail=0

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# --- Stub suites -------------------------------------------------------------
# Both flavours the script dispatches on, so the .py branch and the bash branch
# are each exercised rather than one standing in for the other.

make_py() {  # make_py <name> <exit-code>
  printf '#!/usr/bin/env python3\nimport sys\nprint("stub %s speaking")\nsys.exit(%s)\n' "$1" "$2" \
    > "$TMP/$1.py"
}

make_sh() {  # make_sh <name> <exit-code>
  printf '#!/usr/bin/env bash\necho "stub %s speaking"\nexit %s\n' "$1" "$2" > "$TMP/$1.sh"
}

make_py green 0
make_py red 1
make_py unmeasured 3
make_py weird 2
make_py oomkilled 137
make_py notfound 127
make_sh shell_green 0
make_sh shell_red 1
make_sh shell_unmeasured 3

run() {  # run <suite...> -> sets OUT and RC
  OUT="$("$SCRIPT" "stub suites" "$@" 2>&1)"
  RC=$?
}

check_rc() {  # check_rc <desc> <expected>
  if [ "$RC" = "$2" ]; then
    echo "ok   - $1 (exit $RC)"
    pass=$((pass + 1))
  else
    echo "FAIL - $1: expected exit $2, got $RC"
    echo "$OUT" | sed 's/^/       | /'
    fail=$((fail + 1))
  fi
}

check_says() {  # check_says <desc> <pattern>
  if printf '%s' "$OUT" | command grep -qE "$2"; then
    echo "ok   - $1"
    pass=$((pass + 1))
  else
    echo "FAIL - $1: output does not match /$2/"
    echo "$OUT" | sed 's/^/       | /'
    fail=$((fail + 1))
  fi
}

check_silent() {  # check_silent <desc> <pattern that must NOT appear>
  if printf '%s' "$OUT" | command grep -qE "$2"; then
    echo "FAIL - $1: output unexpectedly matches /$2/"
    echo "$OUT" | sed 's/^/       | /'
    fail=$((fail + 1))
  else
    echo "ok   - $1"
    pass=$((pass + 1))
  fi
}

# --- The all-green case, which is the one that blocks the repository ----------

run "$TMP/green.py" "$TMP/shell_green.sh"
check_rc "all suites pass" 0
check_says "the summary counts the passes" "2 passed, 0 failed, 0 unmeasured, 0 abnormal"
check_silent "a clean run raises no warning annotation" "::warning::"
check_silent "a clean run raises no error annotation" "::error::"

# --- The defect itself: a 3 must be named, and must not read as a failure -----

run "$TMP/unmeasured.py"
check_rc "a lone could-not-measure still fails the job" 1
check_says "a 3 is reported as UNMEASURED, naming the suite" "UNMEASURED .*unmeasured\.py"
check_says "a 3 says its exit code, so the log is self-describing" "exit 3"
check_says "a 3 raises a warning annotation, not an error" "::warning::.*COULD NOT MEASURE"
check_says "a 3 says it is not this PR's fault" "NOT a defect in this pull request"
check_silent "a 3 is never counted among the FAILED suites" "::error::.*FAILED"

# A 1 and a 3 must be DISTINGUISHABLE -- this is the whole issue. Under the old
# `|| rc=1` both produced byte-identical output and exit 1, so this pair of
# assertions is what a reversion reds.
run "$TMP/red.py"
check_rc "a lone failure fails the job" 1
check_says "a 1 is reported as FAIL" "FAIL .*red\.py.*exit 1"
check_says "a 1 raises an error annotation" "::error::.*FAILED"
check_silent "a 1 is never described as could-not-measure" "UNMEASURED|COULD NOT MEASURE"

# --- Ordering: neither verdict may swallow the other -------------------------

run "$TMP/red.py" "$TMP/unmeasured.py"
check_rc "failure THEN could-not-measure fails" 1
check_says "...and the failure survives the later 3" "::error::.*FAILED: .*red\.py"
check_says "...and the 3 is still named" "::warning::.*unmeasured\.py"

run "$TMP/unmeasured.py" "$TMP/red.py"
check_rc "could-not-measure THEN failure fails" 1
check_says "...and the failure is named despite the earlier 3" "::error::.*FAILED: .*red\.py"
check_says "...and the 3 is still named" "::warning::.*unmeasured\.py"

run "$TMP/green.py" "$TMP/unmeasured.py" "$TMP/green.py"
check_rc "a 3 between two passes still fails" 1
check_says "...and the passes are counted" "2 passed, 0 failed, 1 unmeasured"

# --- Every suite failing: no verdict may be lost when several share a bucket --

run "$TMP/red.py" "$TMP/shell_red.sh"
check_rc "two failures fail" 1
check_says "both failures are named, not just the last" "::error::2 .*FAILED: .*red\.py .*shell_red\.sh"

run "$TMP/unmeasured.py" "$TMP/shell_unmeasured.sh"
check_rc "two could-not-measures fail" 1
check_says "both 3s are named" "::warning::2 .*COULD NOT MEASURE \(exit 3\): .*unmeasured\.py .*shell_unmeasured\.sh"

# --- An exit code that is NONE of 0/1/3 gets its own bucket -------------------
# Folding these into FAIL would rebuild the reported defect one level up: an
# OOM-killed suite would be indistinguishable from one that caught a defect.

for pair in "weird 2" "oomkilled 137" "notfound 127"; do
  set -- $pair
  run "$TMP/$1.py"
  check_rc "exit $2 fails the job" 1
  check_says "exit $2 is reported as ABNORMAL, naming the code" "ABNORMAL .*$1\.py.*exit $2"
  check_says "exit $2 raises an error annotation of its own" "::error::.*ABNORMALLY: .*$1\.py"
  check_silent "exit $2 is not misreported as a plain FAIL" "::error::.*[0-9]+ .* suite\(s\) FAILED"
  check_silent "exit $2 is not misreported as could-not-measure" "COULD NOT MEASURE"
done

# All three buckets at once, so none of them masks another.
run "$TMP/green.py" "$TMP/red.py" "$TMP/unmeasured.py" "$TMP/weird.py"
check_rc "a mixture fails" 1
check_says "the summary counts each bucket separately" "1 passed, 1 failed, 1 unmeasured, 1 abnormal"
check_says "the mixture names the failure" "::error::.*FAILED: .*red\.py"
check_says "the mixture names the could-not-measure" "::warning::.*unmeasured\.py"
check_says "the mixture names the abnormal exit" "::error::.*ABNORMALLY: .*weird\.py"

# --- The empty-glob guard the inline loop had, preserved ---------------------
# An empty run reporting success is the "green tick meaning nothing ran" the
# jobs exist to prevent, so it must not be exit 0 and must not be exit 1 either
# (that is a suite verdict); it is a usage error.

OUT="$("$SCRIPT" "stub suites" 2>&1)"; RC=$?
check_rc "no suites at all is a usage error, not a pass" 2
check_says "...and says the job would otherwise pass having run nothing" "would pass without running anything"

OUT="$("$SCRIPT" 2>&1)"; RC=$?
check_rc "no label at all is a usage error" 2

# --- Summary -----------------------------------------------------------------

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ] || exit 1
exit 0
