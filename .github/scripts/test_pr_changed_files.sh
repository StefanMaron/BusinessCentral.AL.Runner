#!/usr/bin/env bash
# Tests for pr_changed_files.sh -- the changed-file list a gating workflow reads.
#
# The centre of this suite is one case: REPRODUCE_THE_COLLAPSE. It builds a real
# repository laid out the way GitHub lays a pull request out -- a base branch
# that moved after the PR branched, and a refs/pull/N/merge commit checked out
# as HEAD -- and asserts BOTH halves:
#
#   * the bug is real: `git diff --name-only <base.sha>...HEAD` attributes a
#     commit that landed on the base branch to the pull request, silently and
#     with no error, because merge-base(base.sha, mergeref) == base.sha;
#   * the fix works: pr_changed_files.sh, given both endpoints explicitly,
#     returns exactly the pull request's own files from that same checkout.
#
# Asserting only the second half would leave the test passing against a script
# that never had the bug to begin with, and would not tell the next reader why
# the endpoints matter.
#
# Run directly: bash .github/scripts/test_pr_changed_files.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/pr_changed_files.sh"
WORKFLOW_DIR="$(cd "$SCRIPT_DIR/../workflows" && pwd)"

pass=0
fail=0

ok()   { echo "ok   - $1"; pass=$((pass + 1)); }
bad()  { echo "FAIL - $1"; fail=$((fail + 1)); }

check_eq() {
  local desc="$1" expected="$2" got="$3"
  if [ "$expected" = "$got" ]; then ok "$desc"; else
    bad "$desc: expected '$expected', got '$got'"
  fi
}

# --- A repository shaped like a real pull request ----------------------------
#
#   A ---- M          base branch (main); M landed AFTER the PR branched
#    \      \
#     P ---- R        P is the PR head, R is refs/pull/N/merge = merge(M, P)
#
# base.sha in the webhook payload is A: the base branch tip when the event was
# delivered. actions/checkout checks out R. That is the whole setup.

REPO="$(mktemp -d)"
trap 'rm -rf "$REPO"' EXIT

git -C "$REPO" init -q -b main
git -C "$REPO" config user.email test@example.com
git -C "$REPO" config user.name Test

mkdir -p "$REPO/AlRunner/Patches" "$REPO/docs"
echo one > "$REPO/docs/start.md"
git -C "$REPO" add -A && git -C "$REPO" commit -qm "A: base"
A=$(git -C "$REPO" rev-parse HEAD)

git -C "$REPO" checkout -q -b pr
echo pr > "$REPO/docs/pr-only.md"
git -C "$REPO" add -A && git -C "$REPO" commit -qm "P: the pull request"
P=$(git -C "$REPO" rev-parse HEAD)

git -C "$REPO" checkout -q main
echo intervening > "$REPO/AlRunner/Patches/Intervening.cs"
git -C "$REPO" add -A && git -C "$REPO" commit -qm "M: landed on main meanwhile"
M=$(git -C "$REPO" rev-parse HEAD)

git -C "$REPO" checkout -q -b mergeref "$M"
git -C "$REPO" merge -q --no-ff -m "Merge pull request" "$P"
R=$(git -C "$REPO" rev-parse HEAD)
git -C "$REPO" checkout -q --detach "$R"   # what actions/checkout leaves behind

# --- The collapse, asserted rather than described ----------------------------

check_eq "merge-base(base.sha, merge ref) is base.sha, so the three-dot range collapses" \
  "$A" "$(git -C "$REPO" merge-base "$A" "$R")"

collapsed=$(git -C "$REPO" diff --name-only "$A"...HEAD)
check_eq "the old form attributes a base-branch commit to the pull request" \
  "$(printf 'AlRunner/Patches/Intervening.cs\ndocs/pr-only.md')" "$collapsed"

if printf '%s\n' "$collapsed" | command grep -q '^AlRunner/Patches/Intervening.cs$'; then
  ok "the wrongly attributed file is one the corpus-linkage guard treats as in scope"
else
  bad "the wrongly attributed file should have been an in-scope path"
fi

# --- The fix -----------------------------------------------------------------

got=$(cd "$REPO" && BASE_SHA="$A" HEAD_SHA="$P" "$SCRIPT" 2>/dev/null)
check_eq "explicit endpoints return exactly the pull request's own files" \
  "docs/pr-only.md" "$got"

rc=0
(cd "$REPO" && BASE_SHA="$A" HEAD_SHA="$P" "$SCRIPT" >/dev/null 2>&1) || rc=$?
check_eq "a non-empty diff exits 0" "0" "$rc"

# The same answer regardless of which commit happens to be checked out: the
# result must depend on the endpoints, not on the working tree.
git -C "$REPO" checkout -q --detach "$M"
got=$(cd "$REPO" && BASE_SHA="$A" HEAD_SHA="$P" "$SCRIPT" 2>/dev/null)
check_eq "the answer does not depend on what is checked out" "docs/pr-only.md" "$got"
git -C "$REPO" checkout -q --detach "$R"

# --- Refusals: every one of these used to be a plausible-looking wrong answer -

assert_rc() {
  local desc="$1" expected="$2"; shift 2
  local rc=0
  (cd "$REPO" && env "$@" "$SCRIPT" >/dev/null 2>&1) || rc=$?
  check_eq "$desc" "$expected" "$rc"
}

assert_rc "HEAD_SHA=HEAD is refused, not silently expanded" 2 \
  BASE_SHA="$A" HEAD_SHA=HEAD
assert_rc "a branch name is refused" 2 BASE_SHA="$A" HEAD_SHA=pr
assert_rc "a refs/ spelling is refused" 2 BASE_SHA="$A" HEAD_SHA=refs/pull/1/merge
assert_rc "BASE_SHA=HEAD is refused too" 2 BASE_SHA=HEAD HEAD_SHA="$P"
assert_rc "an empty HEAD_SHA is refused" 2 BASE_SHA="$A" HEAD_SHA=
assert_rc "an empty BASE_SHA is refused" 2 BASE_SHA= HEAD_SHA="$P"
assert_rc "a hex string that is not a commit here is refused" 2 \
  BASE_SHA="$A" HEAD_SHA=0123456789abcdef0123456789abcdef01234567
assert_rc "an abbreviation too short to be a SHA is refused" 2 \
  BASE_SHA="$A" HEAD_SHA=abc

rc=0
(cd "$REPO" && env -u HEAD_SHA BASE_SHA="$A" "$SCRIPT" >/dev/null 2>&1) || rc=$?
check_eq "an unset HEAD_SHA is a usage error, not a pass" "2" "$rc"

rc=0
(cd "$REPO" && env -u BASE_SHA HEAD_SHA="$P" "$SCRIPT" >/dev/null 2>&1) || rc=$?
check_eq "an unset BASE_SHA is a usage error, not a pass" "2" "$rc"

# An empty diff cannot be a real pull request, so it must not read as "nothing
# in scope changed" to the guard downstream.
rc=0
(cd "$REPO" && BASE_SHA="$P" HEAD_SHA="$P" "$SCRIPT" >/dev/null 2>&1) || rc=$?
check_eq "an empty diff exits 1 rather than printing nothing and passing" "1" "$rc"

# --- The wiring: the workflows must actually use this ------------------------
#
# A tested script the gating job does not call is decoration, and the defect
# this file exists for lived in the YAML, not in any script. These two cases are
# what connect the suite above to what CI really runs.

if command grep -q 'pr_changed_files.sh' "$WORKFLOW_DIR/pr-gate.yml"; then
  ok "pr-gate.yml's corpus-linkage job uses pr_changed_files.sh"
else
  bad "pr-gate.yml no longer calls pr_changed_files.sh -- the tested path is not the shipped one"
fi

# No workflow may end a git diff range at the checked-out HEAD. That is the
# defect, spelled the way it was actually written, and it is invisible in review
# because the result looks like a correct answer. publish.yml's
# `git log <tag>..HEAD` is untouched by this: it scans a release tag range, not
# a pull request diff.
offenders=$(command grep -rn 'git diff' "$WORKFLOW_DIR" \
              | command grep -E '\.{2,3}HEAD([^A-Za-z_-]|$)' || true)
if [ -z "$offenders" ]; then
  ok "no workflow diffs a pull request against the checked-out HEAD"
else
  bad "a workflow ends a git diff range at HEAD, which collapses under refs/pull/N/merge:
$offenders"
fi

echo
echo "passed: $pass, failed: $fail"
[ "$fail" -eq 0 ]
