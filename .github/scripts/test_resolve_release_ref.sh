#!/usr/bin/env bash
# Tests for resolve_release_ref.sh -- the #2060 fix: publish.yml must push the
# release commit back to whatever ref was dispatched (not a hardcoded "main"),
# and must reject an undispatchable ref (anything but a branch) in the plan
# job, before the ~40-minute test matrix runs.
#
# Run directly: bash .github/scripts/test_resolve_release_ref.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/resolve_release_ref.sh"

pass=0
fail=0

assert_contains() {
  local haystack="$1" needle="$2" desc="$3"
  if [[ "$haystack" == *"$needle"* ]]; then
    echo "ok - $desc"
    pass=$((pass + 1))
  else
    echo "NOT ok - $desc"
    echo "    expected to find: $needle"
    echo "    in: $haystack"
    fail=$((fail + 1))
  fi
}

assert_not_contains() {
  local haystack="$1" needle="$2" desc="$3"
  if [[ "$haystack" != *"$needle"* ]]; then
    echo "ok - $desc"
    pass=$((pass + 1))
  else
    echo "NOT ok - $desc"
    echo "    expected NOT to find: $needle"
    echo "    in: $haystack"
    fail=$((fail + 1))
  fi
}

assert_eq() {
  local actual="$1" expected="$2" desc="$3"
  if [ "$actual" = "$expected" ]; then
    echo "ok - $desc"
    pass=$((pass + 1))
  else
    echo "NOT ok - $desc"
    echo "    expected: $expected"
    echo "    actual:   $actual"
    fail=$((fail + 1))
  fi
}

# --- Ordinary path: dispatched against main, no existing tag -----------------
out=$(VERSION="1.0.23" REF_TYPE="branch" REF_NAME="main" SHA="abc123" \
      TAG_EXISTS_REMOTELY="false" "$SCRIPT" 2>/dev/null)
rc=$?
assert_eq "$rc" "0" "main dispatch: exits 0"
assert_contains "$out" "ref=abc123" "main dispatch: ref is the dispatched SHA"
assert_contains "$out" "branch=main" "main dispatch: push target is 'main'"
assert_contains "$out" "tag-exists=false" "main dispatch: tag-exists is false"

# --- The #2060 case: dispatched against a release branch ---------------------
out=$(VERSION="2.5.1" REF_TYPE="branch" REF_NAME="release/2.5.1" SHA="def456" \
      TAG_EXISTS_REMOTELY="false" "$SCRIPT" 2>/dev/null)
rc=$?
assert_eq "$rc" "0" "release-branch dispatch: exits 0"
assert_contains "$out" "ref=def456" "release-branch dispatch: ref is the dispatched SHA"
# The core #2060 assertion: the push target is the DISPATCHED branch, not the
# hardcoded literal "main".
assert_contains "$out" "branch=release/2.5.1" "release-branch dispatch: push target is the dispatched branch"
assert_not_contains "$out" "branch=main" "release-branch dispatch: push target is NOT the literal 'main'"

# --- Fail fast: dispatched against a tag, not a branch ------------------------
out=$(VERSION="1.0.23" REF_TYPE="tag" REF_NAME="v1.0.0" SHA="ghi789" \
      TAG_EXISTS_REMOTELY="false" "$SCRIPT" 2>&1 >/dev/null)
rc=$?
assert_eq "$rc" "1" "tag dispatch: exits non-zero (fails before the matrix runs)"
assert_contains "$out" "not a branch" "tag dispatch: error explains why"

out=$(VERSION="1.0.23" REF_TYPE="tag" REF_NAME="v1.0.0" SHA="ghi789" \
      TAG_EXISTS_REMOTELY="false" "$SCRIPT" 2>/dev/null)
assert_eq "$out" "" "tag dispatch: no GITHUB_OUTPUT lines are emitted on failure"

# --- Retry path: the tag already exists on origin -----------------------------
out=$(VERSION="1.0.23" REF_TYPE="branch" REF_NAME="main" SHA="abc123" \
      TAG_EXISTS_REMOTELY="true" "$SCRIPT" 2>/dev/null)
rc=$?
assert_eq "$rc" "0" "retry: exits 0"
assert_contains "$out" "ref=v1.0.23" "retry: ref is the existing tag"
assert_contains "$out" "tag-exists=true" "retry: tag-exists is true"
assert_not_contains "$out" "branch=" "retry: no push target is emitted (nothing gets pushed)"

# --- Required env vars are actually enforced ----------------------------------
out=$(REF_TYPE="branch" REF_NAME="main" SHA="abc123" TAG_EXISTS_REMOTELY="false" \
      "$SCRIPT" 2>&1 >/dev/null)
rc=$?
assert_eq "$rc" "1" "missing VERSION: exits non-zero"

echo ""
echo "$pass passed, $fail failed"
if [ "$fail" -ne 0 ]; then
  exit 1
fi
