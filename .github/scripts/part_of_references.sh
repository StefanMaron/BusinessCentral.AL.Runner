#!/usr/bin/env bash
# Prints the issue numbers a PR body declares with a standalone "Part of #N"
# line, one per line, in body order, deduplicated. Prints nothing and exits 0
# when there are none.
#
# #3678. "Part of #N" is the half of the branch convention that says an issue
# is NOT finished: the PR lands part of it and N stays open. Nothing read that
# line, so N kept whatever labels its implementation agent left behind -- 5 of
# the 8 open in-progress issues in the 30-day retrospective window sat behind
# a merged "Part of" PR that nobody relabelled (finding b-8). The
# issue-label-hygiene workflow reads this and puts each such issue back on the
# ready queue.
#
# The accepted shape is deliberately the one check_closing_reference.sh's
# branch check accepts, so the gate and the merge-time action cannot disagree
# about what a body declared: the marker stands alone on its own line, an
# optional single punctuation separator, "#N", and then anything that does not
# continue the number (prose after it is allowed, #3934). An inline "this is
# part of #N" is prose about the issue and is NOT a declaration --
# test_part_of_references.sh pins both halves of that pair.
#
# owner/repo#N and full URLs are deliberately NOT accepted: the workflow
# relabels issues in THIS repository, and a cross-repo reference would send a
# label edit at an issue number that means something else here.
#
# Input (environment variable):
#   PR_BODY - the merged pull request's body (required, may be empty)

set -uo pipefail

: "${PR_BODY?PR_BODY is required (may be empty)}"

# Same separator shape as check_closing_reference.sh: optional whitespace, an
# optional single punctuation mark, optional whitespace.
SEP='[[:space:]]*[,;:]?[[:space:]]*'
LINE_RE="^[[:space:]]*Part of${SEP}#[0-9]+(?![0-9A-Za-z_])"

seen=""
while IFS= read -r line; do
  printf '%s' "$line" | command grep -qiP "$LINE_RE" || continue
  num=$(printf '%s' "$line" | command grep -oP '#\K[0-9]+' | head -1)
  [ -n "$num" ] || continue
  case " $seen " in
    *" $num "*) continue ;;
  esac
  seen="$seen $num"
  printf '%s\n' "$num"
done <<< "$PR_BODY"
