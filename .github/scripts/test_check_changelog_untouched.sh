#!/usr/bin/env bash
# Tests for check_changelog_untouched.sh -- the CHANGELOG.md gate (#3677).
#
# The three states get equal weight: a diff carrying CHANGELOG.md fails, a diff
# without it passes, and a file list that was never measured refuses rather than
# passing. The last is the one worth testing -- a guard handed nothing checks
# nothing, and reports success while doing it (guards-need-a-third-state.md).
#
# Run directly: bash .github/scripts/test_check_changelog_untouched.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/check_changelog_untouched.sh"

pass=0
fail=0

# Invoked through `bash` rather than executed, the way pr-gate.yml invokes it:
# the exec bit is not a property this repository's guards depend on.
assert_exit() {
  local desc="$1" expected_rc="$2" files="$3"
  local rc
  CHANGED_FILES="$files" bash "$SCRIPT" >/dev/null 2>&1
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

assert_unset_exit() {
  local desc="$1" expected_rc="$2"
  local rc
  ( unset CHANGED_FILES; bash "$SCRIPT" >/dev/null 2>&1 )
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

assert_stderr_contains() {
  local desc="$1" files="$2" needle="$3"
  local out
  out="$(CHANGED_FILES="$files" bash "$SCRIPT" 2>&1 >/dev/null)"
  if printf '%s' "$out" | command grep -qF -- "$needle"; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: stderr does not mention '$needle'"
    fail=$((fail + 1))
  fi
}

# --- The diff carries CHANGELOG.md: must fail --------------------------------
# Position in the list is asserted three ways because reading only the first
# path, or stopping at the first match of something else, is how a scan like
# this fails silently.

assert_exit "CHANGELOG.md alone fails" 1 "CHANGELOG.md"
assert_exit "CHANGELOG.md first in the list fails" 1 \
  "$(printf 'CHANGELOG.md\nAlRunner/BcCompiler.cs\ndocs/scope.md\n')"
assert_exit "CHANGELOG.md in the middle of the list fails" 1 \
  "$(printf 'AlRunner/BcCompiler.cs\nCHANGELOG.md\ndocs/scope.md\n')"
assert_exit "CHANGELOG.md last in the list fails" 1 \
  "$(printf 'AlRunner/BcCompiler.cs\ndocs/scope.md\nCHANGELOG.md\n')"
assert_exit "a leading ./ on the path still fails" 1 "./CHANGELOG.md"

# The failure message is the whole remedy here -- there is no escape hatch and
# no label -- so it must name the rule and the generator that owns the file.
assert_stderr_contains "the failure names the rule" "CHANGELOG.md" \
  ".claude/rules/no-changelog-edits.md"
assert_stderr_contains "the failure names the generator" "CHANGELOG.md" \
  ".github/scripts/generate_changelog.py"

# --- The diff does not carry it: must pass -----------------------------------
# Matched as a whole path, not as a substring. Each of these is a real path
# shape a substring match would fire on, and firing on one of them is how a
# gate with no escape hatch becomes something authors have to be waved past.

assert_exit "an ordinary diff passes" 0 \
  "$(printf 'AlRunner/Patches/RecordPatches.cs\nAlRunner.Tests/RecordPatchesTests.cs\n')"
assert_exit "a nested CHANGELOG.md is a different file and passes" 0 \
  "docs/CHANGELOG.md"
assert_exit "a path merely starting with the name passes" 0 "CHANGELOG.md.bak"
assert_exit "a path merely ending with the name passes" 0 "vendor/OLD-CHANGELOG.md"
assert_exit "the lowercase spelling is a different git path and passes" 0 \
  "changelog.md"
assert_exit "the generator itself may be changed" 0 \
  ".github/scripts/generate_changelog.py"
assert_exit "the rule file itself may be changed" 0 \
  ".claude/rules/no-changelog-edits.md"

# --- The list was never measured: must refuse --------------------------------
# Not a pass, and deliberately not the same code as a real violation either: an
# author whose PR touches CHANGELOG.md and one whose file list never got
# computed need different remedies. check_corpus_linkage.sh reads an EMPTY list
# as "nothing to check, exit 0"; here it cannot be, because pr_changed_files.sh
# already refuses to print an empty list for a pull request (a PR always changes
# at least one file), so an empty list reaching this script means the
# measurement broke upstream.

assert_unset_exit "an unset CHANGED_FILES refuses" 3
assert_exit "an empty CHANGED_FILES refuses" 3 ""
assert_exit "a whitespace-only CHANGED_FILES refuses" 3 "$(printf '\n  \n')"

echo
echo "passed: $pass  failed: $fail"
[ "$fail" -eq 0 ]
