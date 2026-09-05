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
assert_exit "closing line without a # is NOT a reference -- fails as missing (bare number, GitHub does not act on it)" 1 "fix: something" "Closes 123"
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

# --- Reference-form correctness: only what GitHub actually honors ----------
# A prior version of this script made the "#" optional in its ref pattern,
# which flagged ordinary English like "This fixes 3 bugs in the parser" as
# an unintended close of issue #3. GitHub does not act on a bare number --
# only "#N", "owner/repo#N", and a full issue/PR URL are real closing
# references -- so these are locked in as regression tests.

assert_exit "bare number after a keyword is NOT a closing reference -- passes" 0 \
  "fix: something" \
  "Closes #2121

This fixes 3 bugs in the parser."

assert_exit "a second ordinary bare-number sentence also passes" 0 \
  "fix: something" \
  "Closes #2121

That closes 2 open questions."

assert_exit "inline #N after a keyword, naming an undeclared issue, fails" 1 \
  "fix: something" \
  "Closes #2121

This also fixes #999 in passing."

assert_exit "inline owner/repo#N after a keyword, naming an undeclared issue, fails" 1 \
  "fix: something" \
  "Closes #2121

This resolves other-owner/other-repo#999 as a side effect."

assert_exit "a standalone owner/repo#N canonical line is recognized as a declared target" 0 \
  "fix: something" \
  "Closes other-owner/other-repo#999"

assert_exit "inline full GitHub issue URL after a keyword, naming an undeclared issue, fails" 1 \
  "fix: something" \
  "Closes #2121

This also fixes https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/999, a pasted link."

assert_exit "inline full GitHub PR URL after a keyword, naming an undeclared issue, fails" 1 \
  "fix: something" \
  "Closes #2121

This also closes https://github.com/StefanMaron/BusinessCentral.AL.Runner/pull/999."

assert_exit "a standalone full-URL canonical line is recognized as a declared target" 0 \
  "fix: something" \
  "Closes https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/999"

# GH-N is deliberately NOT treated as a closing reference: it only becomes
# one if this repo configures a custom autolink for that prefix, which it
# does not (`gh api repos/.../autolinks` -> `[]`). Locking this in so a
# future change doesn't start flagging it without its own RED/GREEN case.
assert_exit "GH-N after a keyword is NOT treated as a closing reference -- passes" 0 \
  "fix: something" \
  "Closes #2121

This also fixes GH-999 (no autolink configured for that prefix in this repo)."

# --- CRLF line endings must not break canonical-line detection --------------
# GitHub bodies arriving through the API can carry \r\n. A stray trailing
# \r would break an anchored "$" match if [[:space:]] didn't absorb it.

assert_exit "canonical Closes line survives a CRLF line ending" 0 \
  "fix: something" \
  "$(printf 'Closes #2121\r\n\r\nOrdinary prose.\r\n')"

assert_exit "canonical Closes line with trailing period survives CRLF" 0 \
  "fix: something" \
  "$(printf 'Closes #2121.\r\n\r\nOrdinary prose.\r\n')"

assert_exit "canonical Closes line with trailing space survives CRLF" 0 \
  "fix: something" \
  "$(printf 'Closes #2121 \r\n\r\nOrdinary prose.\r\n')"

assert_exit "two canonical Closes lines both survive CRLF line endings" 0 \
  "fix: something" \
  "$(printf 'Closes #2121\r\nCloses #2128\r\n\r\nOrdinary prose.\r\n')"

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

# --- #2491: the commit-message route -----------------------------------------
#
# The PR body is not the only text GitHub reads. This repository's squash
# setting is squash_merge_commit_message=COMMIT_MESSAGES, so the branch's
# commit messages ARE the merge commit's body. PR #2486 proved it: its
# declared closing references (via closingIssuesReferences) were #2478 and
# #2480, a COMMIT MESSAGE said "It does not close #2479", and merge commit
# 28cdcf65 closed #2479. The body-only check passed that PR.

assert_exit_commits() {
  local desc="$1" expected_rc="$2" title="$3" body="$4" commits="$5"
  local rc
  PR_TITLE="$title" PR_BODY="$body" PR_COMMITS="$commits" "$SCRIPT" >/dev/null 2>&1
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

# The reproducer, in the shape it actually occurred.
assert_exit_commits "the #2486 shape: a commit message saying it does NOT close an undeclared issue fails" 1 \
  "fix: something" \
  "Closes #2478
Closes #2480" \
  "fix: the environment-key half

Scope note: this addresses the environment-key half of #2479. It does not close #2479
 -- the issue's own repro still shows the baseline going missing."

assert_exit_commits "a bare closing keyword in a commit message for an undeclared issue fails" 1 \
  "fix: something" "Closes #123" "fix: something else

Fixes #456"

# Negative direction, and the one that keeps this from being a blanket ban on
# the word: a commit message may restate a target the body already declared.
assert_exit_commits "a commit message restating a DECLARED target passes" 0 \
  "fix: something" "Closes #123" "fix: something

Closes #123"

assert_exit_commits "a commit message referring to an issue WITHOUT a closing keyword passes" 0 \
  "fix: something" "Closes #123" "fix: something

Investigated alongside #456; see that issue for the remaining half."

assert_exit_commits "an ordinary multi-commit branch with clean messages passes" 0 \
  "fix: something" "Closes #123" "fix: first commit

test: add coverage for the first commit

docs: note the behaviour change"

# Additive: the script must behave identically when PR_COMMITS is unset, so a
# caller that predates this change is not broken by it.
assert_exit "PR_COMMITS unset still passes a clean body" 0 "fix: something" "Closes #123"
assert_exit "PR_COMMITS unset still fails a stray body reference" 1 "fix: something" \
  "Closes #123

This does not close #456."

echo ""
echo "$pass passed, $fail failed"
if [ "$fail" -ne 0 ]; then
  exit 1
fi
