#!/usr/bin/env bash
# Fails when a pull request's diff carries CHANGELOG.md (#3677).
#
# CHANGELOG.md is generated from squash-commit messages after merge, by
# .github/scripts/generate_changelog.py, and edited in a PR it produces merge
# conflicts that block the queue. .claude/rules/no-changelog-edits.md said so in
# prose; this makes it a check. There is no escape hatch and no bypass label:
# the two workflows that legitimately write the file (sync-changelog-unreleased.yml
# and publish.yml) push straight to main and never open a pull request, so
# nothing that is meant to change it comes through here.
#
# The path is matched WHOLE, not as a substring: docs/CHANGELOG.md and
# CHANGELOG.md.bak are different files and this must not fire on them.
#
# Input (environment variable, required, and it must not be empty):
#   CHANGED_FILES  - the PR's changed paths, one per line
#
# Exit codes
#   0  the diff does not carry CHANGELOG.md
#   1  it does
#   3  the file list was never measured -- unset, empty or whitespace-only. Not a
#      pass: a guard handed nothing checks nothing and reports success while doing
#      it (.claude/rules/guards-need-a-third-state.md). Not exit 1 either, because
#      "your PR edits CHANGELOG.md" and "the diff did not get computed" need
#      different remedies. Most siblings in this directory spell the same state 2;
#      resolve_corpus_ref.sh uses 3, which is the spelling
#      .claude/rules/guards-need-a-third-state.md sets out.

set -uo pipefail

TARGET="CHANGELOG.md"

# "unset" and "set but empty" are deliberately both refusals, which is where this
# differs from check_corpus_linkage.sh: pr_changed_files.sh already refuses to
# print an empty list for a pull request, because a pull request always changes
# at least one file. So an empty list arriving here is a broken measurement
# upstream, never a legitimately empty diff.
if [ -z "${CHANGED_FILES+set}" ]; then
  echo "::error::check_changelog_untouched.sh: CHANGED_FILES is required. Pass the PR's changed paths, one per line, as .github/scripts/pr_changed_files.sh prints them. Refusing to report a verdict on a file list that was never computed." >&2
  exit 3
fi

if [ -z "${CHANGED_FILES//[[:space:]]/}" ]; then
  echo "::error::check_changelog_untouched.sh: CHANGED_FILES is empty. A pull request always changes at least one file, so this is a broken measurement rather than an empty pull request -- and a guard handed an empty list passes without checking anything. Refusing to report a verdict." >&2
  exit 3
fi

while IFS= read -r f; do
  f="${f#"${f%%[![:space:]]*}"}"   # strip leading whitespace
  f="${f%"${f##*[![:space:]]}"}"   # strip trailing whitespace
  [ -z "$f" ] && continue
  f="${f#./}"
  if [ "$f" = "$TARGET" ]; then
    echo "::error::This PR changes $TARGET. That file is GENERATED from squash-commit messages after merge, by .github/scripts/generate_changelog.py, so an edit in a pull request is either overwritten or -- more often -- becomes a merge conflict that blocks every other PR in the queue behind it. .claude/rules/no-changelog-edits.md: never stage, edit or include CHANGELOG.md in a PR. Remedy: revert it, with 'git checkout origin/main -- $TARGET' followed by a commit, and let the generator write the entry from your commit message. Nothing about your change needs a CHANGELOG edit to be recorded." >&2
    exit 1
  fi
done <<< "$CHANGED_FILES"

echo "This diff does not change $TARGET."
exit 0
