#!/usr/bin/env bash
# Fails when a PR's title or body contains a literal CI-skip directive. See #2116.
#
# Extracted into its own script (out of pr-check.yml's inline `run:` block) so
# this can be unit-tested directly -- pr-check.yml's job just calls it.
#
# This repo squash-merges. Measured rather than assumed (#2491):
# `squash_merge_commit_message` is COMMIT_MESSAGES and
# `squash_merge_commit_title` is COMMIT_OR_PR_TITLE
# (`gh api repos/StefanMaron/BusinessCentral.AL.Runner`), so the merge
# commit's body is the concatenated BRANCH COMMIT MESSAGES and its subject is
# a commit subject or the PR title. GitHub matches several CI-skip spellings
# ANYWHERE in a commit message, not just in a dedicated trailer -- so a
# directive in a commit message, the title, or the body, even just describing
# it in prose, can land in the merge commit and silently skip every workflow
# on that commit. The commit-message route is the one that most reliably
# reaches it, and it was unchecked until #2491; title and body stay checked
# because the squash title is drawn from them and because a directive there
# is never intentional. This happened for real:
# #2115's own PR body explained sync-changelog-unreleased.yml's use of
# "[skip ci]" in its own commit, and that explanation -- once squashed into
# 7a3c3535's commit message -- skipped the one required check on main, along
# with the sync workflow's own first run.
#
# Inputs (environment variables, both required, may be empty strings):
#   PR_TITLE   - the pull request's title
#   PR_BODY    - the pull request's body/description
#   PR_COMMITS - every commit message on the branch, concatenated (optional;
#                defaults to empty so the script stays callable with just a
#                title and body)
#
# Exits non-zero with a message on stderr (via ::error::) explaining the
# mechanism and how to write the directive anyway (escaped) if a PR
# genuinely needs to document it, same as this script's own PR did.

set -uo pipefail

: "${PR_TITLE?PR_TITLE is required (may be empty)}"
: "${PR_BODY?PR_BODY is required (may be empty)}"
# Optional and additive: callers that predate #2491 pass only a title and body.
PR_COMMITS="${PR_COMMITS-}"

# Every spelling GitHub honors, case-insensitively:
# https://docs.github.com/actions/managing-workflow-runs/skipping-workflow-runs
PATTERN='\[skip ci\]|\[ci skip\]|\[no ci\]|\[skip actions\]|\[actions skip\]|\*\*\*no_ci\*\*\*'

FOUND=""
if printf '%s' "$PR_TITLE" | grep -qiE "$PATTERN"; then
  FOUND="title"
fi
if printf '%s' "$PR_BODY" | grep -qiE "$PATTERN"; then
  FOUND="${FOUND:+$FOUND and }body"
fi
if printf '%s' "$PR_COMMITS" | grep -qiE "$PATTERN"; then
  FOUND="${FOUND:+$FOUND and }commit message"
fi

if [ -n "$FOUND" ]; then
  echo "::error::This PR's $FOUND contains a CI-skip directive (one of [skip ci], [ci skip], [no ci], [skip actions], [actions skip], ***NO_CI***). This repo squash-merges with squash_merge_commit_message=COMMIT_MESSAGES, so the branch's commit messages become the merge commit body and a commit subject or the PR title becomes its subject -- a directive in any of them skips EVERY workflow on the resulting merge commit, including the one required check on main (this happened for real: #2116). A directive in a COMMIT MESSAGE has to be fixed there (git commit --amend, or an interactive reword, then force-push); editing the PR body will not remove it. If you genuinely need to WRITE ABOUT the directive (e.g. documenting this exact mechanism, as this script's own PR had to), break the literal match so it survives the squash without triggering it: insert a zero-width space (U+200B) inside the brackets -- '[skip' + U+200B + 'ci]' -- or describe it in prose without the literal bracketed form (e.g. \"a skip-ci directive\") instead." >&2
  exit 1
fi

echo "No CI-skip directive found in the PR title, body or commit messages."
