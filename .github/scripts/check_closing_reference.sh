#!/usr/bin/env bash
# Validates that a PR body's closing references are correct in BOTH directions.
# See #2121 (missing direction) and #2128 (unintended direction).
#
# This repo squash-merges, and GitHub folds the PR title + body into the
# squash commit message (see .claude/rules/branch-and-pr.md). GitHub's
# closing-reference parser (Closes/Fixes/Resolves + one of "#N",
# "owner/repo#N", or a full issue/PR URL, case-insensitive) matches ANYWHERE
# in that message and does not understand surrounding prose -- negation,
# qualification, "not", none of it changes what fires on merge. A bare
# number with no "#" is NOT one of the forms GitHub honors, so this script
# must not treat it as one either -- see the REF_HASH/REF_URL comment below
# for the false positive that shipped when an earlier version of this script
# made the "#" optional. Two failure modes follow from what GitHub DOES
# honor:
#
#   1. Missing (#2121): the PR body has no closing reference at all, so the
#      linked issue never auto-closes and is left labeled in-progress
#      forever. Real instances: #2046, #1642, #1640.
#   2. Unintended (#2128): the PR body contains a closing keyword next to an
#      issue number the PR does NOT intend to close -- often written to
#      explicitly say it does NOT close that issue. GitHub closes it anyway.
#      Real instance: PR #2127's body said "This does not close #2125" and
#      merge commit fe789a13 closed #2125 regardless.
#
# This script treats a closing reference as INTENDED only when it appears on
# its own line (a "canonical" trailer line, e.g. "Closes #123", optionally
# with trailing punctuation) -- exactly the convention
# .claude/rules/branch-and-pr.md already asks for. A PR may declare more than
# one canonical line (a fix that legitimately closes two issues at once).
# Any closing-keyword-plus-issue-number text found elsewhere in the body --
# i.e. embedded in a sentence, not standing alone on its own line -- is a
# STRAY reference: it will still close that issue on merge, so it fails the
# check unless the issue it names is already one of the canonical targets
# (a harmless restatement, not a different, unintended one).
#
# If the body declares no canonical targets at all, an escape hatch covers
# PRs that genuinely have no linked issue (a docs typo, a release-mechanics
# change, a revert): a line of the form "No linked issue: <reason>". The
# reason is mandatory -- a bare opt-out flag would get pasted in reflexively,
# defeating the point of documenting why there's nothing to link.
#
# Inputs (environment variables, both required, may be empty strings):
#   PR_TITLE - the pull request's title (checked only for the escape hatch
#              being irrelevant here; closing references in the title are
#              treated the same as stray body text, since GitHub honors them
#              there too)
#   PR_BODY  - the pull request's body/description
#
# Exits non-zero with a message on stderr (via ::error::) naming the problem
# and how to fix it.

set -uo pipefail

: "${PR_TITLE?PR_TITLE is required (may be empty)}"
: "${PR_BODY?PR_BODY is required (may be empty)}"

KEYWORDS='close|closes|closed|fix|fixes|fixed|resolve|resolves|resolved'

# Issue reference forms GitHub's closing-keyword parser actually honors:
#   - "#N"            (# is REQUIRED -- "Closes 3" is plain English, not a
#                       reference; GitHub does not act on a bare number, so
#                       this script must not either. A prior version of this
#                       script made the "#" optional, which flagged ordinary
#                       sentences like "This fixes 3 bugs in the parser" as
#                       an unintended close of issue #3 -- a false positive
#                       reported after #2129 first shipped.)
#   - "owner/repo#N"  (cross-repo reference, also honored)
#   - a full issue/PR URL, e.g. https://github.com/owner/repo/issues/123 --
#     honored, and the likeliest accidental trigger of the three since
#     people paste issue links into PR bodies constantly.
#
# Deliberately NOT treated as a reference: "GH-N". That shorthand only
# becomes a live GitHub reference if the repository has configured a custom
# autolink for the "GH-" prefix (Settings > General > Autolink references) --
# it is not a built-in, always-on form the way "#N" and the full URL are.
# This repo has no autolinks configured (`gh api repos/.../autolinks` -> `[]`
# at the time this was written), so "GH-N" does not close anything here, and
# treating it as if it did would reintroduce the exact false-positive class
# this comment is describing above. Revisit if this repo ever configures one.
REF_HASH='(?:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)?#[0-9]+'
REF_URL='https?://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/(?:issues|pull)/[0-9]+'
REF="(?:${REF_HASH}|${REF_URL})"

CANONICAL_LINE_RE="^[[:space:]]*(?:${KEYWORDS})[[:space:]]+${REF}[[:space:]]*[.]?[[:space:]]*\$"
STRAY_MATCH_RE="\\b(?:${KEYWORDS})[[:space:]]+${REF}"
ESCAPE_LINE_RE='^[[:space:]]*No linked issue:[[:space:]]*(.*)$'

extract_number() {
  # Pull the trailing digits out of a matched fragment like
  # "closes owner/repo#2125" or "fixes https://github.com/o/r/issues/2125".
  # The issue/PR number is always the LAST run of digits in the fragment,
  # whether it came after a bare "#", an "owner/repo#", or the final path
  # segment of a URL -- any earlier digit runs are just noise from an
  # owner or repo name that happens to contain digits.
  printf '%s' "$1" | command grep -oE '[0-9]+' | tail -1
}

declared_targets=""
is_declared() {
  local n="$1" t
  for t in $declared_targets; do
    [ "$t" = "$n" ] && return 0
  done
  return 1
}

# --- Pass 1: collect canonical declared targets from PR_BODY, line by line --

while IFS= read -r line; do
  if printf '%s' "$line" | command grep -qiP "$CANONICAL_LINE_RE"; then
    match=$(printf '%s' "$line" | command grep -oiP "(?:${KEYWORDS})[[:space:]]+${REF}")
    num=$(extract_number "$match")
    [ -n "$num" ] && declared_targets="$declared_targets $num"
  fi
done <<< "$PR_BODY"

# --- Pass 2: find any stray closing reference (title + non-canonical lines) -

stray_number=""
stray_source=""

check_stray_in_text() {
  local text="$1" source="$2" line
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    if printf '%s' "$line" | command grep -qiP "$CANONICAL_LINE_RE"; then
      continue
    fi
    if printf '%s' "$line" | command grep -qiP "$STRAY_MATCH_RE"; then
      local matches num
      matches=$(printf '%s' "$line" | command grep -oiP "$STRAY_MATCH_RE")
      while IFS= read -r m; do
        num=$(extract_number "$m")
        if [ -n "$num" ] && ! is_declared "$num"; then
          stray_number="$num"
          stray_source="$source"
          return 0
        fi
      done <<< "$matches"
    fi
  done <<< "$text"
  return 1
}

check_stray_in_text "$PR_BODY" "body" || check_stray_in_text "$PR_TITLE" "title"

if [ -n "$stray_number" ]; then
  echo "::error::This PR's $stray_source contains a closing keyword (one of Close/Closes/Closed/Fix/Fixes/Fixed/Resolve/Resolves/Resolved, case-insensitive) next to issue number $stray_number, written inline in a sentence rather than as its own standalone 'Closes #N' trailer line. This repo squash-merges the PR title + body into the merge commit message, and GitHub's closing-reference parser fires on that pattern ANYWHERE in the message -- it does not understand negation or surrounding qualification. Whatever the sentence says, this will close issue $stray_number on merge (this happened for real: PR #2127's body said a sentence did NOT close an issue, and merge commit fe789a13 closed it anyway). If issue $stray_number is not one of this PR's declared canonical targets, refer to it WITHOUT a closing keyword instead, e.g. 'see #$stray_number' or 'its investigation found ...', which still creates the cross-reference on merge without the closing behavior. If it IS meant to close, add its own standalone 'Closes #$stray_number' line." >&2
  exit 1
fi

if [ -z "$declared_targets" ]; then
  escape_reason=""
  escape_found=""
  while IFS= read -r line; do
    if [[ "$line" =~ $ESCAPE_LINE_RE ]]; then
      escape_found="1"
      reason="${BASH_REMATCH[1]}"
      # trim whitespace
      reason="$(echo "$reason" | command sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
      [ -n "$reason" ] && escape_reason="$reason"
    fi
  done <<< "$PR_BODY"

  if [ -n "$escape_reason" ]; then
    echo "No canonical closing reference, but the escape hatch is present with a reason: $escape_reason"
    exit 0
  fi

  if [ -n "$escape_found" ]; then
    echo "::error::This PR's body has a 'No linked issue:' marker but no reason after the colon. The reason is the point of the escape hatch -- state why this PR has nothing to link (e.g. 'No linked issue: fixes a typo in README.md'), don't paste the marker in reflexively." >&2
    exit 1
  fi

  echo "::error::This PR's body has no closing reference (Closes/Fixes/Resolves #N on its own line) and no escape hatch. Without one, the linked issue never auto-closes on merge and is left labeled in-progress forever (this happened for real: #2046, #1642, #1640). Add a standalone 'Closes #N' line naming the issue this PR closes, or, if this PR genuinely has no linked issue (a docs typo, a release-mechanics change, a revert), add a 'No linked issue: <reason>' line stating why." >&2
  exit 1
fi

echo "Closing reference OK: declared target(s):$declared_targets"
