#!/usr/bin/env bash
# Refuses a pull request that moves the tests/al-language pin BACKWARD (#3288).
#
# THE GAP
# -------
# The corpus is this project's ratchet. .claude/skills/autonomous-cycle states
# its purpose directly: every behaviour pinned there "is validated on real BC on
# every push and can never silently regress afterwards." Nothing enforced the
# second half. A pull request whose tests/al-language pin is BEHIND the pin on
# the branch it merges into passes every required context, and merging it
# un-pins every corpus commit in between -- suites a real service tier has
# already adjudicated.
#
# It is silent in a way an ordinary regression is not: the un-pinned suites do
# not fail, they stop existing, and the next green run reports success over a
# smaller set. Nothing else catches it, either:
#
#   * No other required context compares the two pins.
#   * The count baseline does not. A backward pin means FEWER tests, and a
#     smaller baseline updated to match is an internally consistent state. The
#     baseline pins the count against the pin; it never pins the pin against its
#     own history.
#   * mergeStateStatus cannot. A pin change is a one-line tree diff, and two
#     branches moving it in opposite directions is not a textual conflict when
#     only one of them touched it.
#   * A green CI run means "green at the revision this PR pins" -- which is
#     exactly the older revision. The tests that would have gone red are the
#     ones being removed.
#
# Caught for real on PR #3181, which was green and CLEAN while about to drop
# corpus #199 and #201. That PR did nothing wrong: it bumped the pin forward
# from the main that existed when it was written, and main's pin advanced
# afterwards. Nothing rebased it and nothing told it to. That is why this is a
# guard rather than a note in a rules file -- it is a merge-order accident, not
# an authoring mistake, and it gets likelier as more agents run in parallel.
#
# THE FOUR VERDICTS, AND WHY THERE ARE FOUR RATHER THAN THREE
# -----------------------------------------------------------
# The issue's truth table has three rows. Implementing exactly three is the trap
# that would make this guard worse than not shipping it:
#
#   pin untouched (base == head)          -> 0  pass
#   base pin IS an ancestor of head pin   -> 0  pass, a genuine bump
#   otherwise (backward, or divergent)    -> 1  fail
#   the corpus history is not present     -> 3  CANNOT DETERMINE
#
# `git merge-base --is-ancestor` cannot tell the third row from the fourth, and
# it fails in the DIRECTION THAT BLOCKS. Measured, not assumed:
#
#   * When an endpoint is absent from the clone, it exits 128 with a fatal --
#     which a naive `|| exit 1` converts into "your PR moves the pin backward".
#   * Worse, when BOTH endpoints are present as objects but the history between
#     them has been truncated -- exactly what a shallow submodule clone leaves
#     behind -- it exits **1**, cleanly, for a pin that genuinely IS a forward
#     bump. That exit 1 is byte-for-byte the signal a real backward pin
#     produces. Unshallowing the same clone flips the same command to 0.
#
# So a guard reading the exit code alone tells an author their PR moves the
# corpus pin backward when it does not, on every PR, until somebody works out
# that the clone depth was the problem. pr-gate.yml's header sets the bar for
# anything that gates: it must not be able to fail for an environmental reason.
# A missing fetch is an environmental reason. Hence a fourth verdict with its own
# exit code and its own message, which the workflow surfaces as a hard error
# about the CHECKOUT rather than about the pin -- loud, actionable, and never
# confusable with the defect this exists to catch.
#
# Exit 3 is deliberately not exit 0. "Cannot determine" must not read as "fine":
# a guard that passes when it could not measure anything is the green-tick-
# meaning-nothing-was-read failure that pr_changed_files.sh and
# pr_commit_messages.sh also refuse.
#
# THE ENDPOINTS
# -------------
# Both come from the event payload, never from the checked-out HEAD, for the
# reason pr_changed_files.sh documents at length (#3261): actions/checkout leaves
# refs/pull/N/merge checked out -- a merge commit whose FIRST PARENT is the base
# branch. Here that matters even more sharply than it does for a file diff. The
# merge ref's tree carries the MERGED pin, and git resolves a gitlink conflict
# toward the newer commit, so reading the pin from HEAD reads something that is
# not the pull request's pin at all, and a backward pin becomes invisible -- the
# guard would pass on precisely the case it exists to catch. A symbolic ref is
# therefore refused outright rather than resolved.
#
# Inputs (environment variables, both required):
#   BASE_SHA  - github.event.pull_request.base.sha
#   HEAD_SHA  - github.event.pull_request.head.sha
#
# Optional:
#   SUBMODULE_PATH - defaults to tests/al-language
#
# Exit codes
#   0  the pin is unchanged, or it moved strictly forward
#   1  the pin moved backward, or the two pins have diverged
#   2  the check could not run: a missing input, or an endpoint that is not a
#      commit SHA present in this checkout
#   3  the answer cannot be determined from this checkout -- the corpus history
#      needed to compare the two pins is not present. NOT a pass and NOT a
#      backward-pin verdict.

set -uo pipefail

SUBMODULE_PATH="${SUBMODULE_PATH:-tests/al-language}"

die_usage() {
  echo "::error::check_corpus_pin_forward.sh: $1" >&2
  exit 2
}

die_undetermined() {
  echo "::error::check_corpus_pin_forward.sh: $1" >&2
  exit 3
}

# --- Inputs ------------------------------------------------------------------
#
# "Unset" is distinguished from "set but empty", the same way pr_changed_files.sh
# and check_corpus_linkage.sh do: an unset input means the caller never computed
# it, and a guard handed nothing checks nothing and passes.
for var in BASE_SHA HEAD_SHA; do
  if [ -z "${!var+set}" ]; then
    die_usage "$var is required. Pass \${{ github.event.pull_request.base.sha }} and \${{ github.event.pull_request.head.sha }}."
  fi
  if [ -z "${!var}" ]; then
    die_usage "$var was passed but is empty. An empty endpoint makes this guard read a pin that is not this pull request's."
  fi
done

for var in BASE_SHA HEAD_SHA; do
  v="${!var}"
  case "$v" in
    *[!0-9a-fA-F]*)
      die_usage "$var must be a commit SHA from the pull_request event payload, not '$v'. Under actions/checkout, HEAD is refs/pull/N/merge, whose tree carries the MERGED submodule pin rather than this pull request's -- so reading the pin from a symbolic ref hides exactly the backward pin this guard exists to catch. Pass \${{ github.event.pull_request.head.sha }}."
      ;;
  esac
  if [ "${#v}" -lt 7 ]; then
    die_usage "$var='$v' is too short to be a commit SHA."
  fi
  if ! git cat-file -e "${v}^{commit}" 2>/dev/null; then
    die_usage "$var='$v' is not a commit in this checkout. actions/checkout needs fetch-depth: 0 for both endpoints of a pull request comparison to be present."
  fi
done

# --- Read both pins from the superproject trees ------------------------------
#
# git ls-tree against an explicit commit, so neither answer depends on what is
# checked out or on the state of the working tree.
read_pin() {
  local rev="$1" line
  line="$(git ls-tree "$rev" "$SUBMODULE_PATH" 2>/dev/null)" || return 1
  # mode object-type object-sha<TAB>path ; a submodule is mode 160000, type commit
  case "$line" in
    160000\ commit\ *) printf '%s' "$line" | awk '{print $3}' ;;
    *) return 1 ;;
  esac
}

base_pin="$(read_pin "$BASE_SHA")" || base_pin=""
head_pin="$(read_pin "$HEAD_SHA")" || head_pin=""

if [ -z "$base_pin" ] && [ -z "$head_pin" ]; then
  echo "Neither $BASE_SHA nor $HEAD_SHA carries a $SUBMODULE_PATH submodule; there is no corpus pin to compare."
  exit 0
fi

# One side carrying no submodule is a structural change to the repository, not a
# pin bump, and this guard has no basis for a verdict on it.
if [ -z "$base_pin" ] || [ -z "$head_pin" ]; then
  die_undetermined "the $SUBMODULE_PATH submodule is present at one endpoint and absent at the other (base='${base_pin:-<absent>}', head='${head_pin:-<absent>}'). Adding or removing the corpus submodule is not a pin bump, and this guard cannot judge it. A human reviewer should."
fi

# --- Case 1: untouched -------------------------------------------------------

if [ "$base_pin" = "$head_pin" ]; then
  echo "The $SUBMODULE_PATH pin is unchanged by this pull request ($head_pin). Nothing to compare."
  exit 0
fi

# --- The comparison needs the corpus history ---------------------------------

if [ ! -e "$SUBMODULE_PATH/.git" ]; then
  die_undetermined "the $SUBMODULE_PATH submodule is not checked out, so the corpus history needed to compare $base_pin with $head_pin is not present. This is a CHECKOUT problem, not a verdict about the pin: add 'submodules: true' to actions/checkout and fetch the corpus history before running this guard."
fi

# Both pins must be present as objects before the ancestry question means
# anything. A missing one exits 128 from merge-base, which must never be reported
# as a backward pin.
for pin in "$base_pin" "$head_pin"; do
  if ! git -C "$SUBMODULE_PATH" cat-file -e "${pin}^{commit}" 2>/dev/null; then
    die_undetermined "corpus commit $pin is not present in the $SUBMODULE_PATH clone, so the ancestry between the two pins cannot be established. This is a CHECKOUT problem, not a verdict about the pin -- a shallow submodule clone produces exactly this. Fetch the corpus history (git -C $SUBMODULE_PATH fetch --unshallow, or fetch the two revisions) before running this guard."
  fi
done

# The dangerous case: BOTH objects present, history truncated between them.
# merge-base --is-ancestor then answers a clean 1 -- indistinguishable from a
# real backward pin -- for a pin that is genuinely a forward bump. So a shallow
# clone disqualifies the measurement outright rather than being read as a
# verdict.
if [ "$(git -C "$SUBMODULE_PATH" rev-parse --is-shallow-repository 2>/dev/null)" = "true" ]; then
  die_undetermined "the $SUBMODULE_PATH clone is SHALLOW, so ancestry between $base_pin and $head_pin cannot be established. In a shallow clone 'git merge-base --is-ancestor' returns a clean 'not an ancestor' for commits that ARE ancestors, which is byte-for-byte the signal a genuine backward pin produces -- so this guard refuses to answer rather than blame the author for the clone depth. Run 'git -C $SUBMODULE_PATH fetch --unshallow' before this check."
fi

# --- Cases 2 and 3: the ancestry question ------------------------------------

is_ancestor() {
  # 0 = yes, 1 = no, anything else = git could not answer, which is never a
  # verdict here.
  local rc=0
  git -C "$SUBMODULE_PATH" merge-base --is-ancestor "$1" "$2" 2>/dev/null || rc=$?
  if [ "$rc" -ne 0 ] && [ "$rc" -ne 1 ]; then
    die_undetermined "'git merge-base --is-ancestor $1 $2' failed with exit $rc inside $SUBMODULE_PATH rather than answering yes or no. That is a broken measurement, not a backward pin."
  fi
  return $rc
}

if is_ancestor "$base_pin" "$head_pin"; then
  ahead="$(git -C "$SUBMODULE_PATH" rev-list --count "${base_pin}..${head_pin}" 2>/dev/null || echo '?')"
  echo "The $SUBMODULE_PATH pin moves forward: the base pin $base_pin is an ancestor of the head pin $head_pin ($ahead corpus commit(s) ahead). Nothing already validated is being un-pinned."
  exit 0
fi

# Not a forward bump. Which of the two failing shapes is it? They need different
# remedies, so they get different messages.
REMEDY="Re-pin the corpus forward before merging:
    git fetch origin main
    git checkout origin/main -- $SUBMODULE_PATH
  ...or, if this PR genuinely needs a different corpus revision, rebase the pin
  onto the one main carries so nothing already validated is dropped. Then update
  tests/expectations/count-baseline/test-count-baseline.json to match."

if is_ancestor "$head_pin" "$base_pin"; then
  dropped="$(git -C "$SUBMODULE_PATH" log --oneline "${head_pin}..${base_pin}" 2>/dev/null || true)"
  count="$(git -C "$SUBMODULE_PATH" rev-list --count "${head_pin}..${base_pin}" 2>/dev/null || echo '?')"
  echo "::error::This pull request moves the $SUBMODULE_PATH corpus pin BACKWARD, from $base_pin on the base branch to $head_pin. The head pin is an ANCESTOR of the base pin, so merging this would un-pin $count corpus commit(s) already validated against a real BC service tier -- and it would do so silently: those suites do not fail, they stop existing, and the next green run reports success over a smaller set. The corpus is this project's ratchet (.claude/skills/autonomous-cycle); this is the one operation that breaks it. $REMEDY" >&2
  if [ -n "$dropped" ]; then
    echo "Corpus commits that would be un-pinned:" >&2
    printf '%s\n' "$dropped" >&2
  fi
  exit 1
fi

merge_base="$(git -C "$SUBMODULE_PATH" merge-base "$base_pin" "$head_pin" 2>/dev/null || true)"
echo "::error::This pull request's $SUBMODULE_PATH corpus pin has diverged from the base branch's. Neither $base_pin (base) nor $head_pin (head) is an ancestor of the other${merge_base:+, and their most recent common corpus commit is $merge_base}. That is not a forward bump: merging it would un-pin whatever the base branch reached along its own line. A corpus pin should only ever advance along the corpus's master branch, so this usually means the pin was taken from a corpus branch that was never merged. $REMEDY" >&2
if [ -n "$merge_base" ]; then
  dropped="$(git -C "$SUBMODULE_PATH" log --oneline "${merge_base}..${base_pin}" 2>/dev/null || true)"
  if [ -n "$dropped" ]; then
    echo "Corpus commits on the base branch's line that would be un-pinned:" >&2
    printf '%s\n' "$dropped" >&2
  fi
fi
exit 1
