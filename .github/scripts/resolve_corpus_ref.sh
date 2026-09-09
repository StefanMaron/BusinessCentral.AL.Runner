#!/usr/bin/env bash
# Which corpus commit does this run measure? (#3737)
#
# tests/al-language stopped being a pinned gitlink. A run resolves the corpus at
# the head of `master`, or at the head of the corpus pull request the PR body's
# `Corpus-PR:` line names. This script answers only the FIRST half of that -- it
# reads the body and says WHICH corpus pull request, if any, was declared. The
# ref that follows from it needs the corpus PR's state (open / merged / closed),
# which is a network call and lives in the composite action.
#
# WHY THE PARSING IS NOT DONE HERE
# --------------------------------
# check_corpus_linkage.sh owns the `Corpus-PR:` regex, and it gates: the shape it
# accepts is the shape authors are told to write (`bc-behavior-tests-go-upstream.md`
# lists six near-misses it refuses, each pinned in its unit tests). A second regex
# here would be a second source of truth, and the failure it produces is silent --
# a body the gate calls well-formed that this script does not see, resolving master
# while the author believes their corpus PR is under test.
#
# So both modes below are that script's, invoked rather than reimplemented:
#
#   --print-corpus-pr-urls          every WELL-FORMED Corpus-PR URL, canonicalised
#   --print-corpus-pr-marker-lines  every line CARRYING the marker, well-formed or not
#
# THE THIRD STATE, AND WHY IT IS NOT "master" (`guards-need-a-third-state.md`)
# ---------------------------------------------------------------------------
# A body with no Corpus-PR line resolves master. That is the ordinary case and it
# must stay a pass. A body whose Corpus-PR line is MALFORMED must not resolve
# master, because then a typo and a deliberate omission produce the same corpus
# and nothing says so -- the author reads a green run as their corpus PR passing
# when the run never fetched it. That is exit 3.
#
# Two well-formed lines are exit 3 for the same reason: this script cannot pick
# one, and picking the first would silently discard the other.
#
# Inputs (environment):
#   PR_BODY   the pull request's body. Required; it MAY be empty (a push to main
#             has no body), but it must be PASSED -- an unset variable means the
#             caller never read it, and a resolver handed nothing would resolve
#             master for every pull request in the repository.
#
# Output, on stdout, one line, always exactly one of:
#   corpus-pr=            no declaration; the caller resolves master
#   corpus-pr=<N>         resolve the head of corpus pull request N
#
# Exit codes
#   0  answered (either line above)
#   2  usage -- PR_BODY was not passed at all
#   3  could not determine: a malformed Corpus-PR line, or more than one

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINKAGE="$SCRIPT_DIR/check_corpus_linkage.sh"

if [ -z "${PR_BODY+set}" ]; then
  echo "::error::resolve_corpus_ref.sh: PR_BODY is required (it may be empty, but it must be passed). An unset body would resolve master for every pull request, including the ones that declare a corpus PR." >&2
  exit 2
fi

if [ ! -x "$LINKAGE" ] && [ ! -f "$LINKAGE" ]; then
  echo "::error::resolve_corpus_ref.sh: cannot find $LINKAGE, which owns the Corpus-PR regex. Without it this script has nothing to parse with, and guessing is exactly what it exists not to do." >&2
  exit 3
fi

urls="$(PR_BODY="$PR_BODY" bash "$LINKAGE" --print-corpus-pr-urls)"
urls_rc=$?
markers="$(PR_BODY="$PR_BODY" bash "$LINKAGE" --print-corpus-pr-marker-lines)"
markers_rc=$?

if [ "$urls_rc" -ne 0 ] || [ "$markers_rc" -ne 0 ]; then
  echo "::error::resolve_corpus_ref.sh: check_corpus_linkage.sh failed to parse the body (exit $urls_rc / $markers_rc). That is a broken measurement, not a body without a declaration, so this refuses rather than resolving master." >&2
  exit 3
fi

# `command grep -c` counts lines; an empty string is zero lines, not one.
count() { [ -z "$1" ] && printf '0' || printf '%s' "$1" | command grep -c '' ; }

n_urls="$(count "$urls")"
n_markers="$(count "$markers")"

if [ "$n_markers" -gt "$n_urls" ]; then
  echo "::error::resolve_corpus_ref.sh: this body carries $n_markers 'Corpus-PR:' line(s) but only $n_urls of them is a well-formed corpus pull request URL. Refusing to resolve a corpus: a malformed line would otherwise resolve master, which is also what NO line resolves, so the run would silently measure something other than the corpus PR you named. Write it as one line: Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/<N>" >&2
  printf '%s\n' "$markers" >&2
  exit 3
fi

if [ "$n_urls" -gt 1 ]; then
  echo "::error::resolve_corpus_ref.sh: this body declares $n_urls corpus pull requests, and a run measures exactly one corpus. Refusing to pick one -- picking the first would silently discard the rest." >&2
  printf '%s\n' "$urls" >&2
  exit 3
fi

if [ "$n_urls" -eq 0 ]; then
  echo "corpus-pr="
  exit 0
fi

# The URL mode prints canonically, without a trailing slash, so ${url##*/} is the
# number. That normalisation is the reason this does not strip one itself.
url="$(printf '%s' "$urls" | head -n 1)"
number="${url##*/}"

if ! printf '%s' "$number" | command grep -qE '^[0-9]+$'; then
  echo "::error::resolve_corpus_ref.sh: could not read a pull request number out of '$url'. check_corpus_linkage.sh called that URL well-formed, so this is a disagreement between the two and not something a body can cause -- refusing rather than resolving master." >&2
  exit 3
fi

echo "corpus-pr=$number"
