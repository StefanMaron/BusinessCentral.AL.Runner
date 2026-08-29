#!/usr/bin/env bash
# Tests for check_pr_check_triggers.sh -- #2159: pr-check.yml's
# reject-ci-skip-directives and reject-bad-closing-references jobs both read
# the PR title/body, but the workflow's `pull_request` trigger did not
# include `edited`, so editing a PR's title or body after those checks
# passed retriggered nothing.
#
# What this proves: the checker script correctly detects presence/absence
# of each individually-required trigger type against synthetic fixture
# workflow files, in both directions (a fixture missing 'edited' fails, one
# missing an unrelated pre-existing type also fails, one with everything
# present passes). It also runs the checker against the REAL
# .github/workflows/pr-check.yml to prove that file currently satisfies it.
#
# What this does NOT prove: that GitHub Actions actually reruns the two
# guard jobs when a PR is edited post-hoc -- that is a claim about GitHub's
# own trigger semantics, documented at
# https://docs.github.com/actions/using-workflows/events-that-trigger-workflows#pull_request,
# not something a script run in this repo's own CI can exercise end to end.
# This test only proves the workflow FILE declares the type; it cannot open
# a real PR, edit its body, and observe a rerun.
#
# Run directly: bash .github/scripts/test_check_pr_check_triggers.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/check_pr_check_triggers.sh"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

TMPDIR_FIXTURES="$(mktemp -d)"
trap 'rm -rf "$TMPDIR_FIXTURES"' EXIT

pass=0
fail=0

assert_exit() {
  local desc="$1" expected_rc="$2" fixture_path="$3"
  local rc
  "$SCRIPT" "$fixture_path" >/dev/null 2>&1
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

write_fixture() {
  local name="$1" types="$2"
  local path="$TMPDIR_FIXTURES/$name"
  cat > "$path" <<EOF
name: PR Check

on:
  pull_request:
    branches: [main]
    types: [$types]

jobs:
  noop:
    runs-on: ubuntu-latest
    steps:
      - run: echo noop
EOF
  echo "$path"
}

# --- The exact #2159 regression: 'edited' missing, everything else present --

missing_edited=$(write_fixture "missing-edited.yml" "opened, synchronize, reopened, labeled, unlabeled")
assert_exit "fixture missing 'edited' fails" 1 "$missing_edited"

# --- Every required type present passes -------------------------------------

all_present=$(write_fixture "all-present.yml" "opened, synchronize, reopened, labeled, unlabeled, edited")
assert_exit "fixture with all required types passes" 0 "$all_present"

# --- Order and spacing don't matter, just presence ---------------------------

reordered=$(write_fixture "reordered.yml" "edited,opened,unlabeled,reopened,labeled,synchronize")
assert_exit "fixture with reordered/no-space types still passes" 0 "$reordered"

# --- A DIFFERENT pre-existing type going missing also fails (proves this  --
# --- isn't just grepping for the literal string 'edited') -------------------

missing_labeled=$(write_fixture "missing-labeled.yml" "opened, synchronize, reopened, unlabeled, edited")
assert_exit "fixture missing pre-existing 'labeled' also fails" 1 "$missing_labeled"

# --- A file with no recognizable types line at all is a hard error (exit 2, --
# --- distinct from a failed check) -------------------------------------------

cat > "$TMPDIR_FIXTURES/no-types-line.yml" <<'EOF'
name: Something else entirely
on:
  push:
    branches: [main]
jobs:
  noop:
    runs-on: ubuntu-latest
    steps:
      - run: echo noop
EOF
assert_exit "file with no types line errors distinctly" 2 "$TMPDIR_FIXTURES/no-types-line.yml"

# --- A nonexistent path is also a hard error ---------------------------------

assert_exit "nonexistent file errors distinctly" 2 "$TMPDIR_FIXTURES/does-not-exist.yml"

# --- Substring false positives: a type name that's a substring of another  --
# --- (there are none among these six, but 'label' vs 'labeled' is exactly --
# --- the shape of bug this guards against) -----------------------------------

label_not_labeled=$(write_fixture "label-not-labeled.yml" "opened, synchronize, reopened, label, unlabeled, edited")
assert_exit "'label' does not satisfy the requirement for 'labeled'" 1 "$label_not_labeled"

# --- The real workflow file this script exists to guard ---------------------

assert_exit "the actual .github/workflows/pr-check.yml satisfies the check" 0 \
  "$REPO_ROOT/.github/workflows/pr-check.yml"

echo ""
echo "$pass passed, $fail failed"
if [ "$fail" -ne 0 ]; then
  exit 1
fi
