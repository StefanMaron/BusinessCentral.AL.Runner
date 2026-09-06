#!/usr/bin/env bash
# Prints the paths a pull request changes, one per line, for a workflow that has
# to decide something from the PR's diff without asking api.github.com.
#
# WHY THIS IS A SCRIPT AND NOT FOUR LINES OF YAML
# -----------------------------------------------
# The four lines of YAML were wrong, and wrong silently. pr-gate.yml's
# corpus-linkage job computed:
#
#     base="${{ github.event.pull_request.base.sha }}"
#     files=$(git diff --name-only "$base"...HEAD)
#
# with a comment claiming the three dots measured "changes introduced BY this
# PR, from the merge base". That is true only when HEAD is the PR HEAD.
# actions/checkout does not check out the PR head: by default it checks out
# refs/pull/N/merge, a merge commit whose first parent is the base branch. So
# base.sha is an ANCESTOR of HEAD, merge-base(base.sha, HEAD) == base.sha, and
# the three-dot range collapses into a plain two-dot one. Every commit that
# landed on the base branch between base.sha and the merge ref's base parent
# gets attributed to the pull request.
#
# Measured on PR #3261 itself -- base 65e5e562, head e28b9f24 -- against a merge
# ref recomputed on main at 3cd3f06b: the correct endpoints give 15 files, the
# collapsed range gives 32, and eight of the extras are AlRunner/Patches/ files
# the PR never touched. Run through check_corpus_linkage.sh, the 15-file list
# exits 0 (nothing in scope, correctly) and the 32-file list exits 1, demanding
# a corpus declaration from a PR that has nothing to declare -- with no way for
# the author to see which file supposedly triggered it, because it is not in
# their diff. The gap grows as the base branch moves: at main 598f628a, two
# commits earlier, the same PR inflated 15 -> 20.
#
# The exposure is not a narrow race. base.sha is frozen in the webhook payload
# when the event is delivered, while GitHub recomputes refs/pull/N/merge against
# the CURRENT base branch; any delay between the two -- a queue this account has
# been measured 33 runs deep, or a re-run replaying an old payload -- widens the
# gap. It grows over the life of a PR rather than being a startup transient.
#
# So the endpoints are the whole point of this file, and they are the part a
# reviewer cannot check by eye. Both endpoints must be explicit commit SHAs from
# the event payload, and this script REFUSES anything else rather than quietly
# producing a superset: passing HEAD_SHA=HEAD -- the exact defect above -- exits
# 2 with a message, instead of returning a plausible-looking wrong answer.
# test_pr_changed_files.sh reproduces the collapse against a real repository
# with a real merge ref, so the claim above is asserted rather than described.
#
# require-tests.yml and pr-gate.yml's scripts-changed job already used the
# correct BASE...HEAD form inline; they are the precedent this follows, and they
# are the only other places in .github/workflows/ that diff a PR.
#
# Inputs (environment variables, both required):
#   BASE_SHA  - github.event.pull_request.base.sha
#   HEAD_SHA  - github.event.pull_request.head.sha
#
# Output: the changed paths on stdout, one per line.
#
# Exit codes
#   0  printed a non-empty list of changed paths
#   1  the diff was empty -- a pull request always changes at least one file, so
#      this means the diff did not measure what it was meant to. A guard handed
#      an empty list checks nothing and passes, which is the green-tick-meaning-
#      nothing-was-read failure the callers exist to prevent.
#   2  could not run: an input was missing, an endpoint was not a commit SHA, an
#      endpoint is not present in this checkout (fetch-depth), or git failed.

set -uo pipefail

die_usage() {
  echo "::error::pr_changed_files.sh: $1" >&2
  exit 2
}

# Deliberately distinguishes "unset" from "set but empty", the same way
# check_corpus_linkage.sh does: an unset input means the caller never computed
# it, and a range with a missing endpoint measures something other than this
# pull request.
for var in BASE_SHA HEAD_SHA; do
  if [ -z "${!var+set}" ]; then
    die_usage "$var is required. Pass \${{ github.event.pull_request.base.sha }} and \${{ github.event.pull_request.head.sha }}."
  fi
  if [ -z "${!var}" ]; then
    die_usage "$var was passed but is empty. An empty endpoint makes git diff measure something other than this pull request."
  fi
done

for var in BASE_SHA HEAD_SHA; do
  v="${!var}"

  # A symbolic ref is rejected on purpose. Under actions/checkout, 'HEAD' is the
  # merge commit rather than the PR head, and a range ending there collapses to
  # a two-dot one -- see the header. Refusing it turns a silent superset into a
  # loud failure. Branch names, tags and refs/... spellings go the same way.
  case "$v" in
    *[!0-9a-fA-F]*)
      die_usage "$var must be a commit SHA from the pull_request event payload, not '$v'. Under actions/checkout, HEAD is refs/pull/N/merge -- a merge commit whose first parent is the base branch -- so a range ending at HEAD attributes every commit that landed on the base branch meanwhile to this pull request. Pass \${{ github.event.pull_request.head.sha }}."
      ;;
  esac
  if [ "${#v}" -lt 7 ]; then
    die_usage "$var='$v' is too short to be a commit SHA."
  fi
  if ! git cat-file -e "${v}^{commit}" 2>/dev/null; then
    die_usage "$var='$v' is not a commit in this checkout. actions/checkout needs fetch-depth: 0 for both endpoints of a pull request diff to be present."
  fi
done

# Three dots, with both endpoints explicit: the changes introduced BY this pull
# request, measured from merge-base(BASE_SHA, HEAD_SHA). Commits that landed on
# the base branch after the PR branched are on the other side of the merge base
# and are correctly excluded.
if ! files=$(git diff --name-only "${BASE_SHA}...${HEAD_SHA}"); then
  die_usage "git diff --name-only ${BASE_SHA}...${HEAD_SHA} failed."
fi

if [ -z "${files//[[:space:]]/}" ]; then
  echo "::error::pr_changed_files.sh: the diff between ${BASE_SHA} and ${HEAD_SHA} is empty. A pull request always changes at least one file, so this is a broken measurement rather than an empty pull request. Refusing to report a list nothing can be concluded from." >&2
  exit 1
fi

printf '%s\n' "$files"
