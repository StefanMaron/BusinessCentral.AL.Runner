#!/usr/bin/env bash
# Tests for resolve_corpus_ref.sh -- which corpus does this run measure (#3737).
#
# The cases that matter are the ones where two different answers look the same
# from outside. "No declaration" and "a malformed declaration" must NOT both
# resolve master, and "one declaration" and "two" must not both resolve the first
# one. Everything below is one of those pairs, or the plain happy path.
#
# Run directly: bash .github/scripts/test_resolve_corpus_ref.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/resolve_corpus_ref.sh"

pass=0
fail=0

URL="https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/293"

# desc, expected exit, expected stdout (exact, or "-" to ignore), body
assert_resolves() {
  local desc="$1" want_rc="$2" want_out="$3" body="$4"
  local out rc
  out="$(PR_BODY="$body" bash "$SCRIPT" 2>/dev/null)"
  rc=$?
  if [ "$rc" != "$want_rc" ]; then
    echo "FAIL - $desc: expected exit $want_rc, got $rc (stdout: '$out')"
    fail=$((fail + 1))
    return
  fi
  if [ "$want_out" != "-" ] && [ "$out" != "$want_out" ]; then
    echo "FAIL - $desc: expected stdout '$want_out', got '$out'"
    fail=$((fail + 1))
    return
  fi
  echo "ok   - $desc"
  pass=$((pass + 1))
}

# --- No declaration: master, and that is a PASS ------------------------------
# The ordinary case, and `guards-need-a-third-state.md`'s constraint: a genuinely
# absent thing stays a pass. Only an UNMEASURABLE one becomes the third state.

assert_resolves "an empty body resolves master" 0 "corpus-pr=" ""
assert_resolves "a body with no marker resolves master" 0 "corpus-pr=" \
  "$(printf 'Closes #1\n\nSome prose about the fix.\n')"
assert_resolves "a Corpus-NA body resolves master" 0 "corpus-pr=" \
  "$(printf 'Closes #1\n\nCorpus-NA: runner-local exit-code claim; no BC behaviour asserted\n')"

# --- One well-formed declaration ---------------------------------------------

assert_resolves "one Corpus-PR line resolves that pull request" 0 "corpus-pr=293" \
  "$(printf 'Closes #1\n\nCorpus-PR: %s\n' "$URL")"
assert_resolves "a trailing slash still resolves the number" 0 "corpus-pr=293" \
  "$(printf 'Corpus-PR: %s/\n' "$URL")"
assert_resolves "a trailing period still resolves the number" 0 "corpus-pr=293" \
  "$(printf 'Corpus-PR: %s.\n' "$URL")"
assert_resolves "leading whitespace still resolves the number" 0 "corpus-pr=293" \
  "$(printf '   Corpus-PR: %s\n' "$URL")"
assert_resolves "the marker is case-insensitive" 0 "corpus-pr=293" \
  "$(printf 'corpus-pr: %s\n' "$URL")"
assert_resolves "a body with prose around the line still resolves it" 0 "corpus-pr=293" \
  "$(printf 'Closes #1\n\nWhat changed: things.\n\nCorpus-PR: %s\n\nMore prose.\n' "$URL")"

# --- Malformed: exit 3, NEVER master -----------------------------------------
# Each of these is a near-miss check_corpus_linkage.sh already refuses as
# MALFORMED rather than absent (#3330). Resolving master for any of them would
# make a typo indistinguishable from a deliberate omission.

assert_resolves "a markdown link is malformed, not absent" 3 "-" \
  "$(printf 'Corpus-PR: [#293](%s)\n' "$URL")"
assert_resolves "an angle-bracket autolink is malformed, not absent" 3 "-" \
  "$(printf 'Corpus-PR: <%s>\n' "$URL")"
assert_resolves "owner/repo#N shorthand is malformed, not absent" 3 "-" \
  "Corpus-PR: StefanMaron/BusinessCentral.AL.Language.Tests#293"
assert_resolves "a marker with the URL on the next line is malformed" 3 "-" \
  "$(printf 'Corpus-PR:\n%s\n' "$URL")"
assert_resolves "an issues link is malformed, not absent" 3 "-" \
  "Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/issues/293"
assert_resolves "this repository's own URL is malformed, not absent" 3 "-" \
  "Corpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Runner/pull/293"
assert_resolves "a bare marker with nothing after it is malformed" 3 "-" "Corpus-PR:"

# --- Ambiguous: exit 3, never "the first one" --------------------------------

assert_resolves "two well-formed declarations refuse rather than pick one" 3 "-" \
  "$(printf 'Corpus-PR: %s\nCorpus-PR: https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/294\n' "$URL")"

# One good line and one bad line is still a refusal: the counts differ, so the
# body carries a declaration nobody is measuring.
assert_resolves "one good and one malformed line refuses" 3 "-" \
  "$(printf 'Corpus-PR: %s\nCorpus-PR: [#294](%s)\n' "$URL" "$URL")"

# --- The one shape that resolves master and is NOT a refusal -----------------
# A BOLD marker does not match check_corpus_linkage.sh's marker regex either, so
# the gate reads it as ABSENT rather than malformed, and this resolves master to
# match. That is deliberate: the gate's regex is the single source of truth here,
# and a second, wider one in this file would make the gate and the resolver
# disagree about what a declaration is. Two things keep it from being silent --
# the gate blocks any in-scope pull request that declares nothing, and every run
# prints the corpus SHA and ref it resolved, so `(master)` in the log is visible
# to an author who believes they named a corpus PR.
assert_resolves "a bold marker reads as absent, exactly as the gate reads it" 0 "corpus-pr=" \
  "$(printf '**Corpus-PR:** %s\n' "$URL")"

# --- Usage -------------------------------------------------------------------

rc=0
env -u PR_BODY bash "$SCRIPT" >/dev/null 2>&1 || rc=$?
if [ "$rc" = "2" ]; then
  echo "ok   - an unset PR_BODY is a usage error, not 'resolve master'"
  pass=$((pass + 1))
else
  echo "FAIL - an unset PR_BODY should exit 2, got $rc"
  fail=$((fail + 1))
fi

echo
echo "passed: $pass, failed: $fail"
[ "$fail" -eq 0 ]
