#!/usr/bin/env bash
# Tests for check_deferred_scope.sh -- #4293.
#
# The gate flags a PR that declares "Closes #N" while routing remaining work
# AT issue N. Both directions cost, and they cost differently:
#
#   a false negative ships the defect this gate exists for -- the deferred
#     work is orphaned and nobody notices, because a closed issue looks
#     finished (three re-homings: #4249 -> #4255 -> #4292);
#   a false positive reds a PR that is handling scope CORRECTLY, and the
#     cheapest way for an author to clear it is to DELETE the sentence
#     recording the remainder -- which is the silent direction, and strictly
#     worse than the defect. #4291 and #4176 are both real bodies that must
#     stay green, and each fails a different plausible over-reach.
#
# Run directly: bash .github/scripts/test_check_deferred_scope.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/check_deferred_scope.sh"

pass=0
fail=0

# rc: expected exit code. Asserting the CODE and not merely "non-zero" is
# what keeps exit 3 (could not measure) distinguishable from exit 1
# (measured, and broken) -- guards-need-a-third-state.md.
assert_rc() {
  local desc="$1" want="$2" body="$3" got out
  out=$(PR_BODY="$body" bash "$SCRIPT" 2>&1); got=$?
  if [ "$got" = "$want" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $want, got $got"
    echo "       output: $(printf '%s' "$out" | head -c 200)"
    fail=$((fail + 1))
  fi
}

# Asserts the error names the right issue -- a gate that reds for the wrong
# number sends the author to the wrong remedy.
assert_names() {
  local desc="$1" want="$2" body="$3" out
  out=$(PR_BODY="$body" bash "$SCRIPT" 2>&1)
  if printf '%s' "$out" | command grep -q "$want"; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: output did not contain '$want'"
    echo "       output: $(printf '%s' "$out" | head -c 200)"
    fail=$((fail + 1))
  fi
}

echo "--- the two measured instances (#4293's whole subject) ---"

# Verbatim from PR #4253's body, the clause plus its closing line.
assert_rc "#4253's shape: 'belong in #4249's own follow-up' while closing #4249" 1 \
"## Not folded, and why

Both belong in #4249's own follow-up rather than in a test-strengthening PR.

Closes #4249"

# Verbatim from PR #4256's body.
assert_rc "#4256's shape: 'stay on #4255 for a follow-up' while closing #4255" 1 \
"**Not folded:** #4255's part 2 (the cross-bundle diagnostic string quoted two ways).
They stay on #4255 for a follow-up rather than being silently dropped.

Closes #4255"

assert_names "the error names the orphaned issue, not another number" "issue 4249" \
"Both belong in #4249's own follow-up rather than in a test-strengthening PR.

Closes #4249"

echo "--- the routing constructions, table-driven (each must RED) ---"

# Enumerating the class rather than the one instance handed over: a check
# keyed on a single measured phrase passes its dangerous siblings.
for clause in \
  "Both belong in #500's own follow-up." \
  "The rest stays on #500 for a follow-up." \
  "Item 2 remains on #500 for later." \
  "Those two are deferred to #500." \
  "The remainder is left on #500's own follow-up." \
  "They go to #500's part 2." \
  "That half belongs in #500's remainder." \
  "#500's second half stays open for now." \
  "#500's part 2 is not folded here."
do
  assert_rc "routes at a closing target: ${clause}" 1 "$clause

Closes #500"
done

echo "--- GREEN controls: the gate must not refuse the honest path ---"
# A guard that refuses everything passes every refusal arm. These are what
# separate a working gate from one that has started reding the correct shape,
# and the first four are real merged bodies.

assert_rc "#4291's shape: defers the same work but FILES its home first" 0 \
"#4255's part 2 carried two items and they stay on #4255's list until now.
Closing #4255 would have orphaned them a third time, so they are filed as **#4292** before this merges.

Closes #4255"

assert_rc "#4176's shape: 'This is #3482's second half' COMPLETES the issue" 0 \
"This is #3482's second half. Its first half is what I confirmed earlier.

Closes #3482"

assert_rc "the ordinary queue-scan paragraph deferring a DIFFERENT issue" 0 \
"**Not folded:** #4121 belongs in the page insert path, not this file. Its fix
stays on #4121 for a separate PR.

Closes #3514"

assert_rc "a body that defers nothing at all" 0 \
"Fixed the parser and added a proving test.

Closes #500"

assert_rc "a citation of the closed issue is not a deferral" 0 \
"#500's body predicted corpus #285 as the cause; the mechanism was #283's route finding.

Closes #500"

assert_rc "'not folded' with no routing verb near the closing target" 0 \
"**By defect, four keys** -- symbol -> only #500; observable -> nearest is #2700,
deliberately not folded because each needs materially different reasoning.

Closes #500"

assert_rc "Part of #N instead of Closes: nothing closes, so nothing orphans" 0 \
"The remainder stays on #500 for a follow-up.

Part of #500"

assert_rc "routing at a NON-closing issue while closing another" 0 \
"The rest belongs in #999's own follow-up.

Closes #500"

echo "--- the exemption is reachable, and is not a blanket pass ---"
# Pass 3 fires only on an explicit filing DESTINATION. Measured on #4291,
# whose body contains both "filed about ... #4253" (a citation) and
# "filed as **#4292**" (the home): exempting on the first would let an
# orphaning body pass by merely mentioning that something was filed.

assert_rc "exemption: 'tracked by #N' homes the remainder" 0 \
"The rest belongs in #500's own follow-up, tracked by #777.

Closes #500"

assert_rc "exemption: 'the follow-up is #N'" 0 \
"The remainder stays on #500 for later; the follow-up is #777.

Closes #500"

assert_rc "NO exemption from a bare citation of another issue" 1 \
"The rest belongs in #500's own follow-up. See #4239's body and the #3469 precedent.

Closes #500"

assert_rc "NO exemption from 'filed about #N' without a destination" 1 \
"The rest belongs in #500's own follow-up. An issue was filed about #777 one cycle later.

Closes #500"

echo "--- the third state: could not measure (guards-need-a-third-state.md) ---"

unset_rc=$(env -u PR_BODY bash "$SCRIPT" >/dev/null 2>&1; echo $?)
if [ "$unset_rc" = "3" ]; then
  echo "ok   - an UNSET PR_BODY exits 3, not 0: a body never read cannot be judged"
  pass=$((pass + 1))
else
  echo "FAIL - an unset PR_BODY should exit 3, got $unset_rc"
  fail=$((fail + 1))
fi

assert_rc "an EMPTY body is a legitimate pass, not the third state" 0 ""

echo "--- shape details ---"

assert_rc "case-insensitive" 1 "Both BELONG IN #500's OWN FOLLOW-UP.

closes #500"

assert_rc "a stray inline Closes is not read as a declaration here" 0 \
"The rest belongs in #500's own follow-up.

This PR closes #500 eventually."

assert_rc "CRLF line endings do not hide the declaration" 1 \
"$(printf 'Both belong in #500'"'"'s own follow-up.\r\n\r\nCloses #500\r\n')"

assert_rc "#5001 is not read as #500" 0 \
"Both belong in #5001's own follow-up.

Closes #500"

echo
echo "passed: $pass, failed: $fail"
[ "$fail" -eq 0 ] || exit 1
