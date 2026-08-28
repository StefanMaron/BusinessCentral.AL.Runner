#!/usr/bin/env bash
# Tests for check_closing_reference.sh -- #2121 (missing closing reference)
# and #2128 (unintended closing reference), the same script covering both
# directions of the same bug class.
#
# Run directly: bash .github/scripts/test_check_closing_reference.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/check_closing_reference.sh"

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

# --- #2121: the missing direction --------------------------------------------

assert_exit "a plain Closes #N line passes" 0 "fix: something" "Closes #123"
assert_exit "Fixes, case-insensitive, passes" 0 "fix: something" "fixes #123"
assert_exit "Resolves with repo prefix passes" 0 "fix: something" "Resolves owner/repo#123"
assert_exit "closing line without a # passes" 0 "fix: something" "Closes 123"
assert_exit "closing line with trailing period passes" 0 "fix: something" "Closes #123."

assert_exit "no closing reference and no escape hatch fails" 1 "fix: something" \
  "This PR fixes a bug in the classifier. No issue number here at all."

assert_exit "empty body fails" 1 "fix: something" ""

assert_exit "escape hatch WITH a reason passes" 0 "docs: fix typo" \
  "No linked issue: this only fixes a typo in README.md."

assert_exit "escape hatch WITHOUT a reason fails" 1 "docs: fix typo" \
  "No linked issue:"

assert_exit "escape hatch marker with only whitespace after the colon fails" 1 "docs: fix typo" \
  "No linked issue:    "

# --- #2128: the unintended direction -----------------------------------------

# The actual #2127 incident, reproduced: a declared target plus a stray
# closing keyword elsewhere naming a DIFFERENT issue, embedded in a sentence
# that explicitly says it should NOT close it. GitHub ignores the negation;
# this script must not.
assert_exit "the real #2127 sentence: negated close of a different issue fails" 1 \
  "fix: something unrelated" \
  "Closes #2126

This does not close #2125 -- that report stays open pending its own reproduction."

assert_exit "a body whose only closing keyword names an issue other than the declared target fails" 1 \
  "fix: something" \
  "Closes #2121

This change also closes #999 as a side effect, though that is not the point of this PR."

assert_exit "stray closing keyword with no canonical declaration at all fails" 1 \
  "fix: something" \
  "This fixes #2125 somewhere in a sentence, with no standalone trailer line."

assert_exit "stray keyword restating the SAME declared target is allowed" 0 \
  "fix: something" \
  "Closes #2121

This closes #2121 for good."

assert_exit "closing keyword in the TITLE naming an undeclared issue fails" 1 \
  "fix: something that also closes #2125" \
  "Closes #2121"

assert_exit "a body mentioning another issue WITHOUT a keyword passes" 0 \
  "fix: something" \
  "Closes #2121

See #2125 for background -- that investigation found the root cause."

assert_exit "reference via possessive form without a keyword passes" 0 \
  "fix: something" \
  "Closes #2121

#2125's investigation found the root cause of this."

# --- Multiple canonical targets: our own PR closes two issues at once -------

assert_exit "two standalone Closes lines both pass as declared targets" 0 \
  "fix: something" \
  "Closes #2121
Closes #2128

Fixes the missing and unintended closing-reference directions together."

assert_exit "one of two declared targets referenced again in prose is allowed" 0 \
  "fix: something" \
  "Closes #2121
Closes #2128

This PR resolves #2128 by adding a script that also covers #2121."

# --- Clean ordinary PR passes -------------------------------------------------

assert_exit "ordinary PR with a clean Closes line and unrelated prose passes" 0 \
  "fix: something" \
  "Closes #2121

This adds a script and a test. See CONTRIBUTING.md for details."

echo ""
echo "$pass passed, $fail failed"
if [ "$fail" -ne 0 ]; then
  exit 1
fi
