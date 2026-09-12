#!/usr/bin/env bash
# Tests for part_of_references.sh -- #3678.
#
# The extraction the issue-label-hygiene workflow runs on a merged PR body.
# It decides which issues get put back on the ready queue, so a false
# positive relabels an issue nobody worked on and a false negative leaves the
# issue exactly as invisible as it was before this existed (finding b-8: 5 of
# the 8 open in-progress issues sat behind a merged "Part of" PR nobody
# relabelled).
#
# The shape it accepts must be the shape check_closing_reference.sh's branch
# check accepts, or a PR can pass the gate and still be ignored at merge --
# the two cases at the end of this file pin that pair.
#
# Run directly: bash .github/scripts/test_part_of_references.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/part_of_references.sh"
GATE="$SCRIPT_DIR/check_closing_reference.sh"

pass=0
fail=0

assert_out() {
  local desc="$1" expected="$2" body="$3" actual
  actual=$(PR_BODY="$body" bash "$SCRIPT" 2>/dev/null | tr '\n' ' ' | sed -e 's/[[:space:]]*$//')
  if [ "$actual" = "$expected" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected '$expected', got '$actual'"
    fail=$((fail + 1))
  fi
}

assert_out "a standalone Part of line is extracted" "3678" "Closes #123

Part of #3678"

assert_out "case-insensitive, with a trailing period" "3678" "part of #3678."

assert_out "a colon form is extracted" "3678" "Part of: #3678"

assert_out "CRLF line endings do not hide it" "3678" \
  "$(printf 'Closes #123\r\nPart of #3678\r\n')"

assert_out "two Part of lines are both extracted, in order" "3673 3678" "Part of #3673
Part of #3678"

assert_out "the same issue twice is reported once" "3678" "Part of #3678
Part of #3678"

assert_out "an inline mention is NOT extracted" "" "Closes #123

This is part of #3678, landing the first half."

assert_out "a Closes line is NOT extracted" "" "Closes #3678"

assert_out "an owner/repo Part of line is NOT extracted -- this repo's issues only" "" \
  "Part of other-owner/other-repo#3678"

assert_out "a body with nothing to extract prints nothing" "" "Closes #123

Ordinary prose about #456."

assert_out "an empty body prints nothing" "" ""

# #3934: the gate accepts prose after the number, so this must extract it.
assert_out "prose after the number is extracted" "3678" \
  "Part of #3678 — the NavDataTransfer cluster (7 of 69)."
assert_out "a number glued to letters is NOT extracted" "" "Part of #3678abc"

# --- the pair: what the gate accepts, this must extract ----------------------
#
# Both halves read the same convention out of the same PR body, so a shape
# accepted by one and dropped by the other is a silent hole. These two run the
# GATE as well, on the branch whose issue the body names.

gate_rc() {
  PR_TITLE="fix: something" PR_BODY="$1" PR_HEAD_REF="agent/fbk-2/issue-3678" \
    bash "$GATE" >/dev/null 2>&1
}

body="Closes #123

Part of #3678"
if gate_rc "$body" && [ "$(PR_BODY="$body" bash "$SCRIPT")" = "3678" ]; then
  echo "ok   - the gate accepts and the extractor reports the same standalone line"
  pass=$((pass + 1))
else
  echo "FAIL - gate/extractor disagree on a standalone Part of line"
  fail=$((fail + 1))
fi

body="Part of #3678 — the first half; the rest stays open."
if gate_rc "$body" && [ "$(PR_BODY="$body" bash "$SCRIPT")" = "3678" ]; then
  echo "ok   - the gate accepts and the extractor reports a Part of line with trailing prose"
  pass=$((pass + 1))
else
  echo "FAIL - gate/extractor disagree on a Part of line with trailing prose"
  fail=$((fail + 1))
fi

body="Closes #123

This is part of #3678, landing the first half."
if ! gate_rc "$body" && [ -z "$(PR_BODY="$body" bash "$SCRIPT")" ]; then
  echo "ok   - the gate rejects and the extractor drops the same inline mention"
  pass=$((pass + 1))
else
  echo "FAIL - gate/extractor disagree on an inline part-of mention"
  fail=$((fail + 1))
fi

echo ""
echo "$pass passed, $fail failed"
if [ "$fail" -ne 0 ]; then
  exit 1
fi
