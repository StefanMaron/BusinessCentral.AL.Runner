#!/usr/bin/env bash
# Tests for check_ci_skip_directives.sh -- #2116: a PR title/body containing a
# literal CI-skip directive silently disables every workflow on the eventual
# squash-merge commit, because GitHub folds the PR title + body into that
# commit's message and matches the directive anywhere in it.
#
# Run directly: bash .github/scripts/test_check_ci_skip_directives.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/check_ci_skip_directives.sh"

pass=0
fail=0

assert_exit() {
  local desc="$1" expected_rc="$2" title="$3" body="$4"
  local rc
  PR_TITLE="$title" PR_BODY="$body" "$SCRIPT" >/dev/null 2>&1
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

# --- Every spelling GitHub honors, lands in either title or body -------------

assert_exit "bracket [skip ci] in body" 1 "fix: something" "does something\n[skip ci]\nmore text"
assert_exit "bracket [ci skip] in body" 1 "fix: something" "[ci skip]"
assert_exit "bracket [no ci] in body" 1 "fix: something" "[no ci]"
assert_exit "bracket [skip actions] in body" 1 "fix: something" "[skip actions]"
assert_exit "bracket [actions skip] in body" 1 "fix: something" "[actions skip]"
assert_exit "***NO_CI*** in body" 1 "fix: something" "***NO_CI***"
assert_exit "***no_ci*** lowercase in body" 1 "fix: something" "***no_ci***"
assert_exit "[SKIP CI] uppercase in body" 1 "fix: something" "[SKIP CI]"
assert_exit "directive in the TITLE, not just the body" 1 "fix: something [skip ci]" "clean body"

# --- The actual #2116 incident: a directive mentioned in PROSE ---------------

assert_exit "#2115's real trigger: describing the directive in prose" 1 \
  "fix(changelog): sync workflow" \
  "The sync commit carries \"[skip ci]\" so it does not retrigger the matrix."

# --- Clean PRs pass -----------------------------------------------------------

assert_exit "ordinary clean title and body" 0 "fix: something" "This PR fixes a bug in the classifier."
assert_exit "empty title and body" 0 "" ""

# --- The escape hatch this script's own failure message recommends -----------

zwsp=$(printf '\xe2\x80\x8b')  # U+200B zero-width space
assert_exit "zero-width-space-escaped directive does not trip the check" 0 \
  "fix: document the skip mechanism" \
  "Insert a zero-width space inside the brackets: [skip${zwsp}ci]"

assert_exit "prose mention without literal brackets does not trip the check" 0 \
  "fix: document the skip mechanism" \
  "This describes a skip-ci directive without using the literal bracketed form."

echo ""
echo "$pass passed, $fail failed"
if [ "$fail" -ne 0 ]; then
  exit 1
fi
