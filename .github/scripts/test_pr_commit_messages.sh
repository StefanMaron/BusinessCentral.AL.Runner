#!/usr/bin/env bash
# Unit tests for pr_commit_messages.sh, against a stubbed `gh` on PATH.
#
# The script fetches the text that becomes the squash-merge commit body for two
# jobs that now GATE the merge (#3165), so its two non-obvious properties are
# worth proving here rather than the first time a merge is refused: an empty
# fetch is a failure and not a pass, and a transient API error is retried instead
# of blocking a pull request over one bad round trip.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNDER_TEST="$SCRIPT_DIR/pr_commit_messages.sh"
FAILURES=0

check() {
  local name="$1" cond="$2" detail="${3:-}"
  if [ "$cond" = "0" ]; then
    echo "  ok   $name"
  else
    echo "  FAIL $name $detail"
    FAILURES=$((FAILURES + 1))
  fi
}

# Each case gets a throwaway PATH entry holding a `gh` that behaves as the case
# needs. STUB_STATE_FILE counts invocations so a "fails once, then succeeds" stub
# is expressible.
make_stub() {  # make_stub <dir> <body-of-gh>
  mkdir -p "$1"
  {
    echo '#!/usr/bin/env bash'
    echo 'n=0'
    echo '[ -f "$STUB_STATE_FILE" ] && n=$(cat "$STUB_STATE_FILE")'
    echo 'n=$((n + 1)); echo "$n" > "$STUB_STATE_FILE"'
    cat
  } > "$1/gh" <<< "$2"
  chmod +x "$1/gh"
}

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# --- 1. the happy path returns exactly what gh printed
make_stub "$TMP/ok" 'printf "%s\n" "feat: a thing" "body line one"'
out=$(PATH="$TMP/ok:$PATH" STUB_STATE_FILE="$TMP/ok.n" \
      PR_COMMITS_RETRY_DELAY=0 bash "$UNDER_TEST" 1234 owner/repo)
rc=$?
check "a successful fetch exits 0" "$rc" "(rc=$rc)"
[ "$out" = "feat: a thing
body line one" ] && r=0 || r=1
check "...and prints every line the API returned" "$r" "got: $out"
[ "$(cat "$TMP/ok.n")" = "1" ] && r=0 || r=1
check "...without retrying a call that worked" "$r" "calls: $(cat "$TMP/ok.n")"

# --- 2. an EMPTY answer is a failure, not a quiet pass. A PR always has a commit.
make_stub "$TMP/empty" 'printf ""'
out=$(PATH="$TMP/empty:$PATH" STUB_STATE_FILE="$TMP/empty.n" \
      PR_COMMITS_RETRY_DELAY=0 bash "$UNDER_TEST" 1234 owner/repo 2>&1)
rc=$?
check "an empty fetch exits non-zero" "$([ "$rc" -ne 0 ] && echo 0 || echo 1)" "(rc=$rc)"
case "$out" in *"not an empty branch"*) r=0 ;; *) r=1 ;; esac
check "...and says the fetch failed rather than the branch being empty" "$r" "$out"
[ "$(cat "$TMP/empty.n")" = "3" ] && r=0 || r=1
check "...having retried the documented number of times" "$r" "calls: $(cat "$TMP/empty.n")"

# --- 3. whitespace-only is the same as empty; a guard handed " " checks " "
make_stub "$TMP/ws" 'printf "  \n \n"'
PATH="$TMP/ws:$PATH" STUB_STATE_FILE="$TMP/ws.n" PR_COMMITS_RETRY_DELAY=0 \
  bash "$UNDER_TEST" 1234 owner/repo >/dev/null 2>&1
rc=$?
check "a whitespace-only fetch exits non-zero too" \
      "$([ "$rc" -ne 0 ] && echo 0 || echo 1)" "(rc=$rc)"

# --- 4. a TRANSIENT failure is retried, not reported as a blocked merge
make_stub "$TMP/flaky" '
if [ "$n" -lt 2 ]; then
  echo "dial tcp 140.82.121.5:443: i/o timeout" >&2
  exit 1
fi
printf "%s\n" "fix: the second attempt"'
out=$(PATH="$TMP/flaky:$PATH" STUB_STATE_FILE="$TMP/flaky.n" \
      PR_COMMITS_RETRY_DELAY=0 bash "$UNDER_TEST" 1234 owner/repo)
rc=$?
check "a transient failure followed by success exits 0" "$rc" "(rc=$rc)"
[ "$out" = "fix: the second attempt" ] && r=0 || r=1
check "...and returns the successful attempt's output" "$r" "got: $out"

# --- 5. a persistent failure still fails, and names what gh said
make_stub "$TMP/dead" 'echo "HTTP 503" >&2; exit 1'
out=$(PATH="$TMP/dead:$PATH" STUB_STATE_FILE="$TMP/dead.n" \
      PR_COMMITS_RETRY_DELAY=0 bash "$UNDER_TEST" 1234 owner/repo 2>&1)
rc=$?
check "a persistent failure exits non-zero" "$([ "$rc" -ne 0 ] && echo 0 || echo 1)" "(rc=$rc)"
case "$out" in *"HTTP 503"*) r=0 ;; *) r=1 ;; esac
check "...and quotes what gh actually said" "$r" "$out"

# --- 6. a missing PR number is exit 2 -- could not run, distinct from a verdict
PATH="$TMP/ok:$PATH" STUB_STATE_FILE="$TMP/args.n" bash "$UNDER_TEST" >/dev/null 2>&1
rc=$?
check "no arguments is exit 2, not a failure verdict" \
      "$([ "$rc" -eq 2 ] && echo 0 || echo 1)" "(rc=$rc)"

echo
if [ "$FAILURES" -ne 0 ]; then
  echo "FAILED: $FAILURES check(s)"
  exit 1
fi
echo "all checks passed"
