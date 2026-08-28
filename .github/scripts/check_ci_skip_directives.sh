#!/usr/bin/env bash
# Fails when a PR's title or body contains a literal CI-skip directive. See #2116.
#
# Extracted into its own script (out of pr-check.yml's inline `run:` block) so
# this can be unit-tested directly -- pr-check.yml's job just calls it.
#
# This repo squash-merges, and GitHub's default squash commit message is
# "<title> (#N)" followed by the PR body (see .claude/rules/branch-and-pr.md).
# GitHub matches several CI-skip spellings ANYWHERE in a commit message, not
# just in a dedicated trailer -- so a directive in EITHER the title or the
# body, even just describing it in prose, lands in the merge commit and
# silently skips every workflow on that commit. This happened for real:
# #2115's own PR body explained sync-changelog-unreleased.yml's use of
# "[skip ci]" in its own commit, and that explanation -- once squashed into
# 7a3c3535's commit message -- skipped the one required check on main, along
# with the sync workflow's own first run.
#
# Inputs (environment variables, both required, may be empty strings):
#   PR_TITLE - the pull request's title
#   PR_BODY  - the pull request's body/description
#
# Exits non-zero with a message on stderr (via ::error::) explaining the
# mechanism and how to write the directive anyway (escaped) if a PR
# genuinely needs to document it, same as this script's own PR did.

set -uo pipefail

: "${PR_TITLE?PR_TITLE is required (may be empty)}"
: "${PR_BODY?PR_BODY is required (may be empty)}"

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

if [ -n "$FOUND" ]; then
  echo "::error::This PR's $FOUND contains a CI-skip directive (one of [skip ci], [ci skip], [no ci], [skip actions], [actions skip], ***NO_CI***). This repo squash-merges, and GitHub folds the PR title + body into the squash commit message -- a directive anywhere in either one skips EVERY workflow on the resulting merge commit, including the one required check on main (this happened for real: #2116). If you genuinely need to WRITE ABOUT the directive (e.g. documenting this exact mechanism, as this script's own PR had to), break the literal match so it survives the squash without triggering it: insert a zero-width space (U+200B) inside the brackets -- '[skip' + U+200B + 'ci]' -- or describe it in prose without the literal bracketed form (e.g. \"a skip-ci directive\") instead." >&2
  exit 1
fi

echo "No CI-skip directive found in the PR title or body."
