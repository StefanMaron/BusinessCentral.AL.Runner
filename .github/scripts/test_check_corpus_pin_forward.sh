#!/usr/bin/env bash
# Tests for check_corpus_pin_forward.sh -- the guard that stops a pull request
# moving the tests/al-language pin BACKWARD (#3288).
#
# WHY THESE TESTS LOOK THE WAY THEY DO
# ------------------------------------
# The guard's whole value is in telling four situations apart, and three of them
# produce a non-zero exit from `git merge-base --is-ancestor`. So a suite that
# only asserted "backward fails" would pass against a script that fails for
# every bump, which would block every corpus PR in the repository -- a worse
# outcome than not shipping the guard at all. Each case below therefore pins a
# SPECIFIC exit code, and the two "prove, not pass" cases at the bottom assert
# that a stub which always exits 0, and a stub which always exits 1, are both
# caught by this suite.
#
# The four situations, and why the last one is not a footnote:
#
#   pin untouched                     -> 0
#   base pin is an ancestor of head   -> 0   a genuine forward bump
#   otherwise (backward / divergent)  -> 1   the defect
#   corpus history not present        -> 3   CANNOT DETERMINE, and loudly
#
# The fourth is measured, not hypothetical, and it is the reason this guard is
# dangerous to write naively. Reproduced in REPRODUCE_THE_SHALLOW_LIE below:
# in a shallow clone where BOTH pins are present as objects but the history
# between them has been truncated, `git merge-base --is-ancestor <base> <head>`
# exits 1 for a pin that genuinely IS a forward bump. That exit 1 is byte-for-
# byte the same signal a real backward pin produces. A guard that reads the exit
# code alone therefore reports "your PR moves the corpus pin backward" to an
# author whose PR does nothing of the sort, on every PR, until somebody works
# out that the clone depth was the problem. `.claude/rules/ci-verdicts.md` puts
# it directly: a guard that can go red for something the author did not do does
# not belong on the gating side. So "cannot determine" gets its own exit code
# and its own message, and the shallow case must never be reported as backward.
#
# Run directly: bash .github/scripts/test_check_corpus_pin_forward.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/check_corpus_pin_forward.sh"
WORKFLOW_DIR="$(cd "$SCRIPT_DIR/../workflows" && pwd)"

pass=0
fail=0

ok()  { echo "ok   - $1"; pass=$((pass + 1)); }
bad() { echo "FAIL - $1"; fail=$((fail + 1)); }

check_eq() {
  local desc="$1" expected="$2" got="$3"
  if [ "$expected" = "$got" ]; then ok "$desc"; else
    bad "$desc: expected '$expected', got '$got'"
  fi
}

# Runs the script under test in $SUPER with the given environment, returning its
# exit code and capturing combined output into $LAST_OUTPUT.
LAST_OUTPUT=""
run_guard() {
  local rc=0
  LAST_OUTPUT="$(cd "$SUPER" && env "$@" bash "$SCRIPT" 2>&1)" || rc=$?
  return $rc
}

assert_rc() {
  local desc="$1" expected="$2"; shift 2
  local rc=0
  run_guard "$@" || rc=$?
  check_eq "$desc" "$expected" "$rc"
}

assert_output_has() {
  local desc="$1" needle="$2"
  if printf '%s' "$LAST_OUTPUT" | command grep -qF -- "$needle"; then
    ok "$desc"
  else
    bad "$desc: output did not contain '$needle'. Got: $LAST_OUTPUT"
  fi
}

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# --- A corpus repository with a real, linear history -------------------------
#
#   C0 --- C1 --- C2        master
#            \
#             D1            a divergent branch, no ancestry either way with C2
#
# C1 stands for the pin main carried when a PR branched; C2 for the pin main
# carries now. The BACKWARD-PIN shape is head=C1, base=C2 -- a PR still carrying
# the older pin while its base has advanced.
#
# This shape is NOT attributed to a specific pull request here. An earlier
# version of these fixtures and of the script header named PR #3181 as the case
# "caught for real"; that was measured and found to be wrong -- #3181's pin moved
# strictly forward at every revision it had, and corpus #199 and #201 were
# ancestors of its base pin throughout. check_corpus_pin_forward.sh's header
# carries the full measurement. The shape below is still exactly the defect the
# guard exists for; it just has no real-world instance to point at yet.

CORPUS="$TMP/corpus"
git init -q -b master "$CORPUS"
git -C "$CORPUS" config user.email test@example.com
git -C "$CORPUS" config user.name Test

echo t0 > "$CORPUS/spec.al"
git -C "$CORPUS" add -A && git -C "$CORPUS" commit -qm "C0: corpus root"
C0=$(git -C "$CORPUS" rev-parse HEAD)

echo t1 >> "$CORPUS/spec.al"
git -C "$CORPUS" commit -qam "C1: an earlier corpus commit"
C1=$(git -C "$CORPUS" rev-parse HEAD)

echo t2 >> "$CORPUS/spec.al"
git -C "$CORPUS" commit -qam "C2: a later corpus commit"
C2=$(git -C "$CORPUS" rev-parse HEAD)

git -C "$CORPUS" checkout -q -b divergent "$C1"
echo d1 > "$CORPUS/other.al"
git -C "$CORPUS" add -A && git -C "$CORPUS" commit -qm "D1: a divergent corpus commit"
D1=$(git -C "$CORPUS" rev-parse HEAD)
git -C "$CORPUS" checkout -q master

# --- A superproject shaped like a real pull request --------------------------
#
# The submodule is added once; the pin is then moved by writing the gitlink
# directly with `git update-index --cacheinfo`, which is exactly what a pin bump
# is at the tree level and avoids needing a working submodule checkout per case.

SUPER="$TMP/super"
git init -q -b main "$SUPER"
git -C "$SUPER" config user.email test@example.com
git -C "$SUPER" config user.name Test
git -C "$SUPER" config protocol.file.allow always

mkdir -p "$SUPER/tests"
echo "runner" > "$SUPER/README.md"
git -C "$SUPER" add -A && git -C "$SUPER" commit -qm "super: root"
# Captured BEFORE the submodule is added: an endpoint that predates the corpus
# submodule entirely, which is the one shape that legitimately has no pin.
SUPER_ROOT=$(git -C "$SUPER" rev-parse HEAD)

# protocol.file.allow must be passed with -c on the command itself; setting it
# in the repository config is NOT consulted for the submodule's own clone, which
# fails with "transport 'file' not allowed". Errors here are deliberately NOT
# swallowed -- swallowing this one produced a fixture with no submodule at all,
# and every case below then passed the script's "nothing to compare" path while
# looking like it had tested something.
if ! git -C "$SUPER" -c protocol.file.allow=always \
       submodule add -q "file://$CORPUS" tests/al-language; then
  echo "FAIL - fixture setup: could not add the corpus submodule" >&2
  exit 1
fi
git -C "$SUPER" add -A && git -C "$SUPER" commit -qm "super: add corpus submodule"

# The fixture is worthless if the submodule did not actually land, so assert it
# rather than discovering it as thirteen confusing failures further down.
if [ "$(git -C "$SUPER" ls-files -s tests/al-language | awk '{print $1}')" != "160000" ]; then
  echo "FAIL - fixture setup: tests/al-language is not a gitlink" >&2
  exit 1
fi

# Writes commit $1 as the tests/al-language gitlink and commits it, printing the
# resulting superproject commit SHA.
# `git commit` prints "nothing to commit" on STDOUT when the pin is already the
# requested one, so every git call here is silenced explicitly -- letting that
# text into the command substitution produced a multi-line "SHA" that the script
# under test then correctly refused, turning six real cases into usage errors.
# --allow-empty keeps a repeated pin a distinct commit, which the untouched-pin
# case needs.
pin_to() {
  git -C "$SUPER" update-index --cacheinfo "160000,$1,tests/al-language" >/dev/null 2>&1
  git -C "$SUPER" commit -q --allow-empty -m "pin corpus at $1" >/dev/null 2>&1
  git -C "$SUPER" rev-parse HEAD
}

BASE_AT_C2=$(pin_to "$C2")     # main today: the newer pin
HEAD_AT_C1=$(pin_to "$C1")     # a PR still carrying the older pin -- the backward shape
HEAD_AT_C2=$(pin_to "$C2")     # a PR that did not touch the pin
HEAD_AT_D1=$(pin_to "$D1")     # a PR carrying a divergent corpus commit
BASE_AT_C1=$(pin_to "$C1")     # main at the older pin, for the forward-bump case

# Make the corpus history reachable inside the superproject's submodule clone,
# the way the shipped job's fetch step does.
git -C "$SUPER/tests/al-language" fetch -q origin '+refs/heads/*:refs/remotes/origin/*'

# --- The three verdicts ------------------------------------------------------

assert_rc "pin untouched (base == head) passes" 0 \
  BASE_SHA="$BASE_AT_C2" HEAD_SHA="$HEAD_AT_C2"
assert_output_has "an untouched pin says so rather than claiming it checked ancestry" "unchanged"

assert_rc "a genuine forward bump passes" 0 \
  BASE_SHA="$BASE_AT_C1" HEAD_SHA="$HEAD_AT_C2"
assert_output_has "a forward bump names the direction it verified" "ancestor"

# The centre of the suite: the backward shape. The head pin is an ancestor of the
# base pin, so merging it would un-pin every corpus commit in between -- suites
# already validated against a real service tier.
assert_rc "a BACKWARD pin fails (head pin is an ancestor of base pin)" 1 \
  BASE_SHA="$BASE_AT_C2" HEAD_SHA="$HEAD_AT_C1"
assert_output_has "the backward failure is a GitHub error annotation" "::error::"
# head=C1, base=C2, so the corpus commit that would be un-pinned is C2 -- the
# one main reached and this PR's pin does not.
assert_output_has "the backward failure names the corpus commit that would be dropped" \
  "C2: a later corpus commit"

assert_rc "a DIVERGENT pin fails (neither commit is an ancestor of the other)" 1 \
  BASE_SHA="$BASE_AT_C2" HEAD_SHA="$HEAD_AT_D1"
assert_output_has "the divergent failure is a GitHub error annotation" "::error::"

# A divergent pin is not a backward one and must not be described as one -- the
# remedy differs (rebase the pin forward vs. work out where the pin came from).
if printf '%s' "$LAST_OUTPUT" | command grep -qF "diverged"; then
  ok "the divergent case is reported as divergent, not as backward"
else
  bad "the divergent case should say the pins diverged. Got: $LAST_OUTPUT"
fi

# --- REPRODUCE_THE_SHALLOW_LIE ----------------------------------------------
#
# Both halves are asserted, the same way test_pr_changed_files.sh asserts the
# range collapse: that raw git really does answer wrongly here, and that the
# script does not.

SHALLOW_SUPER="$TMP/shallow-super"
cp -r "$SUPER" "$SHALLOW_SUPER"
rm -rf "$SHALLOW_SUPER/tests/al-language"
git -C "$SHALLOW_SUPER" -c protocol.file.allow=always clone -q --depth 1 \
  "file://$CORPUS" "$SHALLOW_SUPER/tests/al-language"
# Bring both endpoints in as objects WITHOUT their connecting history, which is
# the state actions/checkout leaves behind when it clones a submodule shallowly.
git -C "$SHALLOW_SUPER/tests/al-language" fetch -q --depth 1 origin "$C1" 2>/dev/null
git -C "$SHALLOW_SUPER/tests/al-language" fetch -q --depth 1 origin "$C2" 2>/dev/null

both_present=0
git -C "$SHALLOW_SUPER/tests/al-language" cat-file -e "${C1}^{commit}" 2>/dev/null || both_present=1
git -C "$SHALLOW_SUPER/tests/al-language" cat-file -e "${C2}^{commit}" 2>/dev/null || both_present=1
check_eq "the shallow submodule really does have BOTH pins as objects" "0" "$both_present"

raw_rc=0
git -C "$SHALLOW_SUPER/tests/al-language" merge-base --is-ancestor "$C1" "$C2" 2>/dev/null || raw_rc=$?
check_eq "raw git answers 'not an ancestor' for a pin that IS a forward bump" "1" "$raw_rc"

# ...and that wrong answer is indistinguishable from the genuine backward case
# above, which is exactly why the guard may not read the exit code alone.
SUPER_REAL="$SUPER"
SUPER="$SHALLOW_SUPER"

assert_rc "a forward bump in a shallow clone is CANNOT-DETERMINE, not a bogus failure" 3 \
  BASE_SHA="$BASE_AT_C1" HEAD_SHA="$HEAD_AT_C2"
assert_output_has "cannot-determine explains the clone depth rather than blaming the author" \
  "shallow"
# The one thing it must never do is pass silently -- that would leave the guard
# reporting green while having checked nothing, on every PR.
if [ "$LAST_OUTPUT" != "${LAST_OUTPUT/::error::/}" ] || \
   [ "$LAST_OUTPUT" != "${LAST_OUTPUT/::warning::/}" ]; then
  ok "cannot-determine is annotated in the CI log rather than silent"
else
  bad "cannot-determine produced no ::error:: or ::warning:: annotation. Got: $LAST_OUTPUT"
fi

SUPER="$SUPER_REAL"

# --- A pin whose object is absent entirely ----------------------------------
#
# Distinct from the shallow case above: there the objects are present and only
# the history between them is missing. Here an endpoint is not in the clone at
# all, which makes merge-base exit 128 with a fatal -- the exit code a naive
# `|| exit 1` converts into a false "moves the pin backward" accusation.
#
# Built as its own superproject whose submodule clone genuinely lacks the commit,
# rather than by writing a synthetic gitlink: `git update-index --cacheinfo`
# refuses a path that is not already in the index (it wants --add), so the
# synthetic version silently left the pin untouched and the case passed against
# the shallow branch above instead of the one it names.

ABSENT_SUPER="$TMP/absent-super"
git init -q -b main "$ABSENT_SUPER"
git -C "$ABSENT_SUPER" config user.email test@example.com
git -C "$ABSENT_SUPER" config user.name Test

echo "runner" > "$ABSENT_SUPER/README.md"
git -C "$ABSENT_SUPER" add -A && git -C "$ABSENT_SUPER" commit -qm "super: root"

# The submodule is cloned from a corpus that only has C0..C1 -- so C2, which the
# head pin below names, is genuinely not an object in it.
PARTIAL="$TMP/partial-corpus"
git clone -q --no-local "$CORPUS" "$PARTIAL"
git -C "$PARTIAL" checkout -q "$C1"
git -C "$PARTIAL" branch -q -f master "$C1"
git -C "$PARTIAL" checkout -q master
git -C "$PARTIAL" reflog expire --expire=now --all
git -C "$PARTIAL" gc -q --prune=now 2>/dev/null || true

if ! git -C "$ABSENT_SUPER" -c protocol.file.allow=always \
       submodule add -q "file://$PARTIAL" tests/al-language; then
  echo "FAIL - fixture setup: could not add the partial corpus submodule" >&2
  exit 1
fi
git -C "$ABSENT_SUPER" add -A
git -C "$ABSENT_SUPER" commit -qm "super: add partial corpus submodule"
ABSENT_BASE=$(git -C "$ABSENT_SUPER" rev-parse HEAD)

# Confirm the fixture really is what it claims before asserting anything on it.
if git -C "$ABSENT_SUPER/tests/al-language" cat-file -e "${C2}^{commit}" 2>/dev/null; then
  bad "fixture setup: the partial corpus clone unexpectedly has C2; the absent-object case would prove nothing"
else
  ok "the fixture's corpus clone genuinely lacks the head pin's commit"
fi

git -C "$ABSENT_SUPER" update-index --cacheinfo "160000,$C2,tests/al-language"
git -C "$ABSENT_SUPER" commit -q -m "pin corpus at a commit this clone does not have"
ABSENT_HEAD=$(git -C "$ABSENT_SUPER" rev-parse HEAD)

SUPER="$ABSENT_SUPER"
assert_rc "a pin whose object is absent is CANNOT-DETERMINE, not backward" 3 \
  BASE_SHA="$ABSENT_BASE" HEAD_SHA="$ABSENT_HEAD"
assert_output_has "an absent pin object is reported as a checkout problem" "not present"
SUPER="$SUPER_REAL"

# --- Endpoints: the #3261 lesson, applied here -------------------------------
#
# Both endpoints must come from the event payload. Under actions/checkout, HEAD
# is refs/pull/N/merge -- a merge commit whose FIRST PARENT is the base branch --
# so reading the pin from HEAD reads main's pin, not the PR's, and a backward pin
# becomes invisible. Refusing a symbolic ref is what makes that unmissable.

assert_rc "HEAD_SHA=HEAD is refused rather than silently reading the merge ref's pin" 2 \
  BASE_SHA="$BASE_AT_C2" HEAD_SHA=HEAD
assert_rc "BASE_SHA=HEAD is refused too" 2 BASE_SHA=HEAD HEAD_SHA="$HEAD_AT_C1"
assert_rc "a branch name is refused" 2 BASE_SHA="$BASE_AT_C2" HEAD_SHA=main
assert_rc "a refs/ spelling is refused" 2 BASE_SHA="$BASE_AT_C2" HEAD_SHA=refs/pull/1/merge
assert_rc "an empty HEAD_SHA is refused" 2 BASE_SHA="$BASE_AT_C2" HEAD_SHA=
assert_rc "an empty BASE_SHA is refused" 2 BASE_SHA= HEAD_SHA="$HEAD_AT_C1"
assert_rc "an abbreviation too short to be a SHA is refused" 2 \
  BASE_SHA="$BASE_AT_C2" HEAD_SHA=abc
assert_rc "a hex string that is not a commit here is refused" 2 \
  BASE_SHA="$BASE_AT_C2" HEAD_SHA=0123456789abcdef0123456789abcdef01234567

rc=0
(cd "$SUPER" && env -u HEAD_SHA BASE_SHA="$BASE_AT_C2" bash "$SCRIPT" >/dev/null 2>&1) || rc=$?
check_eq "an unset HEAD_SHA is a usage error, not a pass" "2" "$rc"

rc=0
(cd "$SUPER" && env -u BASE_SHA HEAD_SHA="$HEAD_AT_C1" bash "$SCRIPT" >/dev/null 2>&1) || rc=$?
check_eq "an unset BASE_SHA is a usage error, not a pass" "2" "$rc"

# --- A SUBMODULE_PATH naming nothing must not be a silent pass ---------------
#
# The "neither endpoint carries a submodule" branch prints "there is no corpus
# pin to compare" and exits 0. That branch is reachable for a reason that has
# nothing to do with the repository having no submodule: SUBMODULE_PATH defaults
# to a HARDCODED string, and nothing tied that string to what .gitmodules
# actually declares. Rename the submodule, or mistype the default, and the guard
# passes every pull request forever while reporting a green tick -- the
# green-run-that-measured-nothing shape this script's own header refuses
# elsewhere by spending exit 3 on it.
#
# The endpoints below are the real backward-pin case, so a guard that is looking
# at the right path answers 1. Anything that answers 0 here is answering about a
# path it never found.
assert_rc "a SUBMODULE_PATH that names no submodule is not a silent pass" 3 \
  BASE_SHA="$BASE_AT_C2" HEAD_SHA="$HEAD_AT_C1" SUBMODULE_PATH="tests/nonexistent"
assert_output_has "the misconfigured path is named in the message" "tests/nonexistent"

# The mirror of the case above: a repository that genuinely declares no
# submodule at either endpoint has no pin to compare and must still PASS. Without
# this, hardening the misconfigured-path case would turn every pull request on a
# submodule-free history into a hard error -- trading a silent never-fire for a
# loud always-fire, which is not an improvement.
assert_rc "an endpoint predating the submodule is still a legitimate pass" 0 \
  BASE_SHA="$SUPER_ROOT" HEAD_SHA="$SUPER_ROOT"
assert_output_has "the no-submodule pass says so" "there is no corpus pin to compare"

# And the default really is the path this repository declares. The case above
# proves a wrong path is loud; this proves the shipped default is not that wrong
# path. Read out of .gitmodules rather than restated, so renaming the submodule
# without updating the script fails here.
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
default_path="$(sed -n 's/^SUBMODULE_PATH="\${SUBMODULE_PATH:-\(.*\)}"$/\1/p' "$SCRIPT" | head -1)"
check_eq "the script has a readable default SUBMODULE_PATH" "tests/al-language" "$default_path"

if [ -f "$REPO_ROOT/.gitmodules" ]; then
  if git config -f "$REPO_ROOT/.gitmodules" --get-regexp '^submodule\..*\.path$' \
       | awk '{print $2}' | command grep -qxF "$default_path"; then
    ok "the default SUBMODULE_PATH is a submodule this repository really declares"
  else
    bad "the default SUBMODULE_PATH ('$default_path') is not declared in .gitmodules -- the guard would find no pin and pass every PR"
  fi
else
  bad "no .gitmodules at $REPO_ROOT, so the default SUBMODULE_PATH cannot be verified"
fi

# The verdict must depend on the endpoints, never on what happens to be checked
# out -- this is the property that makes the refusals above meaningful.
git -C "$SUPER" checkout -q --detach "$BASE_AT_C2"
assert_rc "a backward pin is still caught whatever is checked out" 1 \
  BASE_SHA="$BASE_AT_C2" HEAD_SHA="$HEAD_AT_C1"
git -C "$SUPER" checkout -q main

# --- Prove, not pass: both stub directions must be caught --------------------
#
# .claude/rules/tdd.md asks whether the suite would still pass against an
# implementation that always returns the same answer. Rather than leaving that
# to the reader, it is asserted: a stub that always exits 0 and a stub that
# always exits 1 are each run through the cases above and must FAIL this suite.

STUB_DIR="$TMP/stubs"
mkdir -p "$STUB_DIR"
printf '#!/usr/bin/env bash\nexit 0\n' > "$STUB_DIR/always0.sh"
printf '#!/usr/bin/env bash\nexit 1\n' > "$STUB_DIR/always1.sh"
chmod +x "$STUB_DIR/always0.sh" "$STUB_DIR/always1.sh"

stub_rc() {
  local stub="$1"; shift
  local rc=0
  (cd "$SUPER" && env "$@" bash "$stub" >/dev/null 2>&1) || rc=$?
  echo "$rc"
}

# always-0 must be caught by the backward case, which is the one that matters.
got=$(stub_rc "$STUB_DIR/always0.sh" BASE_SHA="$BASE_AT_C2" HEAD_SHA="$HEAD_AT_C1")
if [ "$got" != "1" ]; then
  ok "a stub that always exits 0 fails this suite's backward case"
else
  bad "a stub that always exits 0 would pass the backward case -- the suite proves nothing"
fi

# always-1 must be caught by BOTH passing cases, not just one.
got=$(stub_rc "$STUB_DIR/always1.sh" BASE_SHA="$BASE_AT_C2" HEAD_SHA="$HEAD_AT_C2")
if [ "$got" != "0" ]; then
  ok "a stub that always exits 1 fails this suite's pin-untouched case"
else
  bad "a stub that always exits 1 would pass the pin-untouched case"
fi

got=$(stub_rc "$STUB_DIR/always1.sh" BASE_SHA="$BASE_AT_C1" HEAD_SHA="$HEAD_AT_C2")
if [ "$got" != "0" ]; then
  ok "a stub that always exits 1 fails this suite's forward-bump case"
else
  bad "a stub that always exits 1 would pass the forward-bump case"
fi

# --- The wiring: a tested script the gating job does not call is decoration ---

if command grep -q 'check_corpus_pin_forward.sh' "$WORKFLOW_DIR/pr-gate.yml"; then
  ok "pr-gate.yml calls check_corpus_pin_forward.sh"
else
  bad "pr-gate.yml does not call check_corpus_pin_forward.sh -- the tested path is not the shipped one"
fi

# The endpoints must reach the script from the event payload. If the job ever
# stops passing them, every case above is testing something CI does not run.
pin_job=$(command sed -n '/^  require-forward-corpus-pin:/,/^  [a-z]/p' "$WORKFLOW_DIR/pr-gate.yml")
if [ -z "$pin_job" ]; then
  bad "pr-gate.yml has no require-forward-corpus-pin job"
elif printf '%s' "$pin_job" | command grep -q 'github.event.pull_request.head.sha'; then
  ok "the pin job passes the PR head SHA from the event payload"
else
  bad "the pin job does not pass github.event.pull_request.head.sha"
fi

# The guard is worthless if the corpus history is not fetched before it runs:
# every PR would land on the cannot-determine path and the job would never
# actually compare anything.
if printf '%s' "$pin_job" | command grep -qE 'unshallow|--filter|fetch'; then
  ok "the pin job fetches corpus history rather than relying on a shallow clone"
else
  bad "the pin job does not fetch corpus history -- it would never reach a verdict"
fi

echo
echo "passed: $pass, failed: $fail"
[ "$fail" -eq 0 ]
