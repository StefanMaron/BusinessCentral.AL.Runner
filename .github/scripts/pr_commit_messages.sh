#!/usr/bin/env bash
# Print a pull request's commit messages -- every commit's headline and body, in
# the order GitHub concatenates them.
#
# Why this exists as a script rather than an inline `run:` block. This repository
# squash-merges with squash_merge_commit_message=COMMIT_MESSAGES (measured with
# `gh api repos/StefanMaron/BusinessCentral.AL.Runner`), so the branch's commit
# messages ARE the merge commit's body, and both the CI-skip guard and the
# closing-reference guard have to read them (#2491). Two jobs needed the same
# fetch, and they carried two copies of it.
#
# It is asked of the API rather than reconstructed from git: this returns exactly
# the commit list GitHub will concatenate, with no dependence on fetch-depth or on
# where the base branch has moved to.
#
# Retried, because both callers now produce REQUIRED status-check contexts
# (#3165). Without a retry a single transient API error blocks a merge for a
# reason that has nothing to do with the pull request. Retrying is safe here --
# the call is a read.
#
# A PR always has at least one commit, so empty output means the fetch failed. It
# exits non-zero rather than printing nothing: a guard handed an empty string
# checks an empty string and passes, which is the "green tick meaning nothing was
# read" failure this whole family of jobs exists to prevent.
#
# Usage: pr_commit_messages.sh <pr-number> [owner/repo]
#   PR_COMMITS_ATTEMPTS      attempts before giving up (default 3)
#   PR_COMMITS_RETRY_DELAY   seconds, multiplied by the attempt number (default 3)
set -uo pipefail

PR_NUMBER="${1:-}"
REPO="${2:-${GITHUB_REPOSITORY:-}}"
ATTEMPTS="${PR_COMMITS_ATTEMPTS:-3}"
DELAY="${PR_COMMITS_RETRY_DELAY:-3}"

if [ -z "$PR_NUMBER" ] || [ -z "$REPO" ]; then
  echo "::error::usage: pr_commit_messages.sh <pr-number> [owner/repo] (repo may come from GITHUB_REPOSITORY)" >&2
  exit 2
fi

last_err=""
i=1
while [ "$i" -le "$ATTEMPTS" ]; do
  if msgs=$(gh pr view "$PR_NUMBER" --repo "$REPO" \
              --json commits --jq '.commits[] | .messageHeadline, .messageBody' 2>&1); then
    if [ -n "${msgs//[[:space:]]/}" ]; then
      printf '%s\n' "$msgs"
      exit 0
    fi
    last_err="the API returned no commit message at all"
  else
    last_err="$msgs"
  fi
  if [ "$i" -lt "$ATTEMPTS" ]; then
    sleep $((DELAY * i))
  fi
  i=$((i + 1))
done

echo "::error::Could not read any commit message for PR #$PR_NUMBER in $REPO after $ATTEMPTS attempt(s): $last_err" >&2
echo "::error::Every PR has at least one commit, so this is a failure to fetch, not an empty branch -- refusing to report a pass without having read the text that becomes the merge commit." >&2
exit 1
