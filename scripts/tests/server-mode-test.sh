#!/usr/bin/env bash
# Integration test for --server mode's multi-bundle (app + test-app) contract.
#
# Drives a real `al-runner --server` daemon over its stdin/stdout NDJSON
# protocol (docs/server-mode.md) using the tests/runner-extras/server-multibundle
# fixture pair, and asserts three behaviours that each regressed independently:
#
#   1. A runTests request naming [dep, main] runs the main bundle's CRUD tests
#      against the table defined in the DEP bundle's AL source. (A per-bundle
#      cache reset used to wipe the dep's parsed table schema first — every
#      test died with "no NCLMetaTable for table 64300".)
#   2. After a schema edit in the dep bundle, the next request must MISS the
#      AL-output cache. (An Id:Version-only dep cache key served the main
#      bundle's STALE compiled assembly — a runtime NavNCLFieldNotFoundException
#      where a fresh compile correctly fails.)
#   3. That fresh compile must fail LOUDLY: exitCode 3 with an EMIT-EXCLUDED
#      compilation error naming the dropped object. (The server emit path used
#      to drop the object and report a green exitCode 0 with its tests missing.)
#
# Usage:
#   scripts/tests/server-mode-test.sh [--runner "CMD"] [extra runner args...]
#
#   --runner CMD   Command line that invokes the runner (default:
#                  "dotnet run --no-build --project AlRunner -c Release --framework net8.0 --").
#   Everything else (e.g. --package-cache DIR, --bc-version X) is passed to the
#   server invocation verbatim.
#
# Requires: jq. Exit 0 = all assertions hold; exit 1 = at least one failed.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RUNNER="dotnet run --no-build --project $REPO/AlRunner -c Release --framework net8.0 --"
if [[ "${1:-}" == "--runner" ]]; then
    RUNNER="$2"
    shift 2
fi
EXTRA_ARGS=("$@")

READY_TIMEOUT="${SERVER_TEST_READY_TIMEOUT:-300}"
RUN_TIMEOUT="${SERVER_TEST_RUN_TIMEOUT:-600}"

WORK="$(mktemp -d)"
FIFO="$WORK/in.fifo"
OUT="$WORK/out.ndjson"
LOG="$WORK/server.log"
SERVER_PID=""
FAILURES=0

cleanup() {
    if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
        printf '{"command":"shutdown"}\n' > "$FIFO" 2>/dev/null || true
        for _ in 1 2 3 4 5; do kill -0 "$SERVER_PID" 2>/dev/null || break; sleep 1; done
        kill "$SERVER_PID" 2>/dev/null || true
    fi
    [[ -n "${HOLDER_PID:-}" ]] && kill "$HOLDER_PID" 2>/dev/null || true
    rm -rf "$WORK"
}
trap cleanup EXIT

fail() { echo "FAIL: $1" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok: $1"; }

# The fixture pair is copied so assertion 2 can mutate the dep table's schema
# without touching the checked-in suite.
cp -r "$REPO/tests/runner-extras/server-multibundle-dep" "$WORK/dep"
cp -r "$REPO/tests/runner-extras/server-multibundle-main" "$WORK/main"

mkfifo "$FIFO"
# Private AL-output cache: assertion 2 is ABOUT cache behaviour, so the test
# must own the cache directory it warms.
# shellcheck disable=SC2086
$RUNNER --server --cache "$WORK/al-out" "${EXTRA_ARGS[@]}" < "$FIFO" > "$OUT" 2> "$LOG" &
SERVER_PID=$!
sleep infinity > "$FIFO" &
HOLDER_PID=$!

echo "waiting for server readiness (timeout ${READY_TIMEOUT}s)..."
waited=0
until grep -q '"ready"[[:space:]]*:[[:space:]]*true' "$OUT" 2>/dev/null; do
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "server died during startup — last stderr:" >&2
        tail -30 "$LOG" >&2
        exit 1
    fi
    if (( waited >= READY_TIMEOUT )); then
        echo "server not ready within ${READY_TIMEOUT}s — last stderr:" >&2
        tail -30 "$LOG" >&2
        exit 1
    fi
    sleep 1; waited=$((waited + 1))
done

# Sends one runTests request for the fixture pair and echoes the summary line.
# The response stream is `{"type":"test"}* {"type":"summary"}` (one final
# summary per request), so waiting for the NEXT summary line is the request
# boundary.
request_and_summary() {
    local before summaries
    before=$(grep -c '"type":"summary"' "$OUT" 2>/dev/null || true)
    printf '{"command":"runTests","sourcePaths":["%s","%s"]}\n' "$WORK/dep" "$WORK/main" > "$FIFO"
    local waited=0
    while :; do
        summaries=$(grep -c '"type":"summary"' "$OUT" 2>/dev/null || true)
        if (( summaries > before )); then
            grep '"type":"summary"' "$OUT" | tail -1
            return 0
        fi
        if ! kill -0 "$SERVER_PID" 2>/dev/null; then
            echo "server died mid-request — last stderr:" >&2
            tail -30 "$LOG" >&2
            return 1
        fi
        if (( waited >= RUN_TIMEOUT )); then
            echo "no summary within ${RUN_TIMEOUT}s — last stderr:" >&2
            tail -30 "$LOG" >&2
            return 1
        fi
        sleep 1; waited=$((waited + 1))
    done
}

echo "── assertion 1: dep-table CRUD runs green through --server ──"
summary=$(request_and_summary) || exit 1
exit_code=$(jq -r '.exitCode' <<<"$summary")
failed=$(jq -r '.failed + .errors' <<<"$summary")
total=$(jq -r '.total' <<<"$summary")
if [[ "$exit_code" == "0" && "$failed" == "0" && "$total" -ge 4 ]]; then
    pass "exitCode 0, $total tests, 0 failed"
else
    fail "expected exitCode 0 with >=4 passing tests, got: $summary"
    grep '"type":"test"' "$OUT" | tail -6 >&2
fi

echo "── assertions 2+3: schema edit must MISS the cache and fail loudly ──"
# Remove the Description field the main bundle's tests reference. A correct
# runner recompiles (cache key changed with the dep's content) and the emit
# drops the test codeunit -> exitCode 3 + EMIT-EXCLUDED. A stale-cache runner
# instead reruns the OLD assembly: runtime FieldNotFound, exitCode 1 — or,
# without the emit guard, a green exitCode 0 with the tests silently missing.
python3 - "$WORK/dep/SmbItem.Table.al" <<'EOF'
import sys
p = sys.argv[1]
s = open(p).read()
needle = '        field(2; Description; Text[100]) { }\n'
assert needle in s, "fixture drifted: Description field line not found"
open(p, 'w').write(s.replace(needle, ''))
EOF
summary=$(request_and_summary) || exit 1
exit_code=$(jq -r '.exitCode' <<<"$summary")
compile_errors=$(jq -r '(.compilationErrors // []) | tostring' <<<"$summary")
if [[ "$exit_code" == "3" ]]; then
    pass "exitCode 3 (compile failure, not stale-cache runtime failure)"
else
    fail "expected exitCode 3 after dep schema edit, got: $summary"
fi
if grep -q "EMIT-EXCLUDED" <<<"$compile_errors"; then
    pass "compilationErrors carry EMIT-EXCLUDED naming the dropped object"
else
    fail "expected an EMIT-EXCLUDED compilation error, got: $compile_errors"
fi

if (( FAILURES > 0 )); then
    echo "$FAILURES assertion(s) failed" >&2
    exit 1
fi
echo "all server-mode assertions passed"
