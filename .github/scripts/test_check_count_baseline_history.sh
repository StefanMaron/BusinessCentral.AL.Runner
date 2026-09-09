#!/usr/bin/env bash
# Tests for check_count_baseline_history.sh -- the pin-bump history entry guard
# (#3591).
#
# The decisive case is FIRST and named for the PR it replays. A guard that has
# never been seen to fire on the real omission it was built for is not armed,
# whatever else it asserts, so PR #3588's actual pre-fix diff shape --
# tests/al-language plus test-count-baseline.json, no history.md -- is pinned
# here as its own case rather than left implicit in a generic "missing entry"
# test.
#
# The other half carries equal weight. This guard fires on the al-language pin
# bump and NOTHING else, because the README requires the entry for exactly that
# case ("Bumped the corpus pin -- ... and add an entry to history.md"). Measured
# on main at e03dbfc1: of the last 12 commits touching test-count-baseline.json,
# 10 carried a history.md change and 2 did not, and BOTH of those two
# (b071a9d4, 01338b5e) added a runner-extras GROUP line and never touched the
# submodule pin. Neither was a violation. So a runner-extras-only baseline edit
# must pass, and the skip cases below are what keeps this guard from nagging on
# the two-in-twelve shape that is legitimately silent.
#
# Run directly: bash .github/scripts/test_check_count_baseline_history.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/check_count_baseline_history.sh"

BASELINE="tests/expectations/count-baseline/test-count-baseline.json"
HISTORY="tests/expectations/count-baseline/history.md"
PIN="tests/al-language"

pass=0
fail=0

assert_exit() {
  local desc="$1" expected_rc="$2" files="$3" body="${4-}"
  local rc
  CHANGED_FILES="$files" PR_BODY="$body" "$SCRIPT" >/dev/null 2>&1
  rc=$?
  if [ "$rc" = "$expected_rc" ]; then
    echo "ok   - $desc"
    pass=$((pass + 1))
  else
    echo "FAIL - $desc: expected exit $expected_rc, got $rc"
    fail=$((fail + 1))
  fi
}

# --- The case this guard exists for ------------------------------------------
#
# PR #3588 bumped the pin, passed all 13 required checks green, and had no
# history.md entry. Caught by a reviewer, not by CI; fixed afterwards by
# 26ddc371. This is that diff, byte for byte in path terms.
#
# Reconstructing it means taking the squash 325b24ec's paths and SUBTRACTING
# history.md. That is not a shortcut for walking to a parent commit, and it is
# worth knowing why before anyone tries: 26ddc371 is a branch commit whose
# parent is 64990eed, and it is NOT an ancestor of 325b24ec -- the squash
# discarded the branch history, so no parent walk from main reaches the pre-fix
# state at all. Subtraction is the only reconstruction available, which is
# exactly why the pre-fix shape is pinned here as literal paths rather than
# derived from git at test time.

assert_exit "#3588's actual omission fires the guard (pin + baseline, no history)" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")"

# The same omission with the paths in the other order, because reading only the
# first path would miss it.
assert_exit "the omission fires with the paths reversed" 1 \
  "$(printf '%s\n%s\n' "$BASELINE" "$PIN")"

# The realistic shape of a fix PR that folds in its pin bump: lots of unrelated
# files, the pin and the baseline among them, no history entry.
assert_exit "a fold-in fix PR omitting the entry fires" 1 \
  "$(printf 'AlRunner/Patches/RecordPatches.DateVirtualTable.cs\nAlRunner.Tests/DateVirtualTableTests.cs\n%s\n%s\ndocs/scope.md\n' "$PIN" "$BASELINE")"

# A pin bump that does not touch the baseline at all is still a pin bump, and
# the README's requirement is attached to the bump, not to the count moving.
# Every one of the 31 pin bumps since history.md existed moved both.
assert_exit "a pin bump with no baseline edit and no history entry fires" 1 "$PIN"

# --- The same bumps, done correctly ------------------------------------------

assert_exit "#3588 with its history entry passes" 0 \
  "$(printf '%s\n%s\n%s\n' "$PIN" "$BASELINE" "$HISTORY")"
assert_exit "a fold-in fix PR with its entry passes" 0 \
  "$(printf 'AlRunner/Patches/RecordPatches.DateVirtualTable.cs\n%s\n%s\n%s\n' "$PIN" "$BASELINE" "$HISTORY")"
assert_exit "a pin bump with only a history entry passes" 0 \
  "$(printf '%s\n%s\n' "$PIN" "$HISTORY")"

# A REVERT moves the gitlink like any other change, so it needs an entry like
# any other change -- arguably more, since "why did the pin go back" is the
# least recoverable reasoning of all. The guard cannot tell a revert from a bump
# and is not supposed to; this pins that it does not accidentally special-case
# one. (check_corpus_pin_forward.sh is what refuses a BACKWARD pin; the two
# guards are independent and a revert meets both.)
assert_exit "a revert of a pin bump still needs an entry" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")"
assert_exit "a revert of a pin bump with its entry passes" 0 \
  "$(printf '%s\n%s\n%s\n' "$PIN" "$BASELINE" "$HISTORY")"

# The pin and its entry buried in a large diff. Reading only the first or last
# few paths would get this wrong in either direction, so both the trigger and
# the satisfying path are placed away from the ends.
assert_exit "a large diff carrying both the pin and its entry passes" 0 \
  "$(for i in $(seq 1 20); do echo "AlRunner/Patches/File$i.cs"; done
     printf '%s\n' "$PIN"
     for i in $(seq 21 30); do echo "AlRunner.Tests/Test$i.cs"; done
     printf '%s\n' "$HISTORY"
     for i in $(seq 31 40); do echo "docs/doc$i.md"; done)"
assert_exit "a large diff carrying the pin but no entry fires" 1 \
  "$(for i in $(seq 1 20); do echo "AlRunner/Patches/File$i.cs"; done
     printf '%s\n' "$PIN"
     for i in $(seq 21 40); do echo "AlRunner.Tests/Test$i.cs"; done)"

# --- The two measured exceptions, which must stay silent ---------------------
#
# b071a9d4 and 01338b5e, the two of the last twelve that carried no history
# change. Both added one runner-extras group line and neither moved the pin.
# The README asks for a history entry under "Bumped the corpus pin"; adding or
# removing a runner-extras group line is a different row of that table with no
# such requirement. Firing here would be the noisy failure that gets pasted
# past, so these are pinned as decisions.

assert_exit "b071a9d4's shape: a runner-extras group line, no pin move, passes" 0 \
  "$(printf 'AlRunner/Patches/RecordPatches.QueryJoin.cs\ntests/runner-extras/query-dataitem-filter-precompiled-dep/app.json\n%s\n' "$BASELINE")"
assert_exit "01338b5e's shape: a runner-extras group line, no pin move, passes" 0 \
  "$(printf 'AlRunner/Patches/MockTestPage.cs\n%s\n' "$BASELINE")"
assert_exit "a baseline edit alone, with no pin move, passes" 0 "$BASELINE"

# --- Everything else stays quiet ---------------------------------------------

assert_exit "an empty diff is skipped" 0 ""
assert_exit "an unrelated code PR is skipped" 0 \
  "$(printf 'AlRunner/Patches/MockTestPage.cs\nAlRunner.Tests/MockTestPageTests.cs\n')"
assert_exit "a docs-only PR is skipped" 0 "docs/limitations.md"
assert_exit "a history.md edit on its own is skipped" 0 "$HISTORY"
assert_exit "an expectations manifest edit is skipped" 0 "tests/expectations/known-gaps-ui.json"
assert_exit "the count-baseline README is not the baseline" 0 \
  "tests/expectations/count-baseline/README.md"

# A path that merely CONTAINS the submodule path as a prefix is not the pin.
# tests/al-language is a gitlink: git reports it as exactly that one path, never
# with anything after it, so a path underneath it is either a stale diff or a
# different file -- and a prefix match would make every corpus file look like a
# pin bump.
assert_exit "a path under the submodule is not the pin gitlink" 0 \
  "tests/al-language/tests/al-language/app.json"
# The mirror: a sibling directory sharing the prefix.
assert_exit "a sibling path sharing the pin's prefix is not the pin" 0 \
  "tests/al-language-notes/README.md"
# And for the baseline, the same shape.
assert_exit "a longer path sharing the baseline's prefix is not the baseline" 0 \
  "tests/expectations/count-baseline/test-count-baseline.json.bak"

# --- The opt-out ---------------------------------------------------------------
#
# Measured on main at e03dbfc1: 31 of 31 pin bumps since history.md was created
# (c133aa97, #2879) carried an entry, so nothing observed needs this. It exists
# because the cost of being wrong is asymmetric -- an unconditional guard that
# meets a legitimate silent bump blocks a merge with no way past it except
# writing a sentence that says nothing, which is the reflexive-paste failure in
# the other direction. Same mandatory-reason idiom as check_corpus_linkage.sh's
# Corpus-NA, for the same reason: a bare marker is the same as no guard.

assert_exit "the opt-out with a real reason passes" 0 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" \
  "Count-Baseline-History-NA: the pin moves only to un-skip a test the previous bump had already described"
assert_exit "the opt-out keyword is case-insensitive" 0 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" \
  "count-baseline-history-na: spelled out properly, and this is the reason"
assert_exit "leading whitespace before the opt-out is tolerated" 0 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" \
  "   Count-Baseline-History-NA: a real reason, indented"
assert_exit "the opt-out among other body prose passes" 0 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" \
  "Closes #1

Count-Baseline-History-NA: a real reason, in a normal body

More prose after it."

assert_exit "the opt-out with no reason fails" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" "Count-Baseline-History-NA:"
assert_exit "the opt-out with only whitespace fails" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" "Count-Baseline-History-NA:      "
assert_exit "the opt-out with a placeholder reason fails (n/a)" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" "Count-Baseline-History-NA: n/a"
assert_exit "the opt-out with a placeholder reason fails (none)" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" "Count-Baseline-History-NA: none"
assert_exit "the opt-out with a placeholder reason fails (TBD)" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" "Count-Baseline-History-NA: TBD"

# The canonical-line requirement, same reasoning as check_corpus_linkage.sh and
# check_closing_reference.sh: a declaration stands on its own line where a
# reviewer reads it, and the marker and its reason share that line. Every one of
# these shapes went red on a real PR for the Corpus-PR marker (#3330); they are
# pinned here so this marker cannot drift away from that behaviour.
assert_exit "the opt-out buried mid-sentence does not count" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" \
  "I considered a Count-Baseline-History-NA: line but wrote the entry instead."
assert_exit "a bold opt-out marker is not the marker" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" \
  "**Count-Baseline-History-NA:** a real enough reason"
assert_exit "the opt-out marker inside backticks does not count" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" \
  "The \`Count-Baseline-History-NA:\` marker would apply here, arguably."
assert_exit "the marker and its reason on two separate lines do not count" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" \
  "Count-Baseline-History-NA:
the reason lives down here"

# The opt-out is scoped to this guard and must not be satisfied by another
# guard's marker, nor satisfy one.
assert_exit "a Corpus-NA line does not satisfy this guard" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" \
  "Corpus-NA: CI plumbing; this asserts nothing about BC"

# Writing the entry makes the opt-out irrelevant rather than contradictory: a PR
# that does both is honouring the rule, and there is nothing to complain about.
assert_exit "an entry plus an opt-out is not a conflict" 0 \
  "$(printf '%s\n%s\n%s\n' "$PIN" "$BASELINE" "$HISTORY")" \
  "Count-Baseline-History-NA: belt and braces"

# --- Usage errors are not passes ---------------------------------------------
#
# A guard handed nothing checks nothing and exits 0, which is a green tick
# meaning nobody read anything. check_corpus_linkage.sh and
# pr_commit_messages.sh both refuse that; so does this.

rc=0
PR_BODY="x" "$SCRIPT" >/dev/null 2>&1 || rc=$?
if [ "$rc" = "2" ]; then
  echo "ok   - a missing CHANGED_FILES is a usage error, not a pass"
  pass=$((pass + 1))
else
  echo "FAIL - a missing CHANGED_FILES should exit 2, got $rc"
  fail=$((fail + 1))
fi

rc=0
CHANGED_FILES="$PIN" "$SCRIPT" >/dev/null 2>&1 || rc=$?
if [ "$rc" = "2" ]; then
  echo "ok   - a missing PR_BODY is a usage error, not a pass"
  pass=$((pass + 1))
else
  echo "FAIL - a missing PR_BODY should exit 2, got $rc"
  fail=$((fail + 1))
fi

# An EMPTY body is a legitimate state (a PR with no description at all), and it
# must fire rather than error -- the omission is still an omission.
assert_exit "an empty body on a pin bump still fires" 1 \
  "$(printf '%s\n%s\n' "$PIN" "$BASELINE")" ""

# --- The paths this guard names must exist -----------------------------------
#
# The guard matches three literal paths. If one is renamed and the guard is not,
# it silently stops firing -- the same silent-pass failure it was built to close,
# one level up. Assert them against the working tree.

REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
for p in "$BASELINE" "$HISTORY" "$PIN"; do
  if [ -e "$REPO_ROOT/$p" ]; then
    echo "ok   - the guard's path $p exists in the tree"
    pass=$((pass + 1))
  else
    echo "FAIL - the guard names $p, which is not in the tree -- rename, or the guard is dead"
    fail=$((fail + 1))
  fi
done

# And the README sentence the guard enforces must still say so. If that
# requirement is dropped, this guard should go with it, not outlive it.
if command grep -qi 'add an entry to' "$REPO_ROOT/tests/expectations/count-baseline/README.md" \
   && command grep -qi 'history\.md' "$REPO_ROOT/tests/expectations/count-baseline/README.md"; then
  echo "ok   - the count-baseline README still requires a history.md entry"
  pass=$((pass + 1))
else
  echo "FAIL - the count-baseline README no longer requires a history.md entry; this guard should be removed with it"
  fail=$((fail + 1))
fi

echo
echo "passed: $pass, failed: $fail"
[ "$fail" -eq 0 ]
