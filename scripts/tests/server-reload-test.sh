#!/usr/bin/env bash
# Regression guard for issue #1860, investigated and REFUTED against this
# codebase (see PR #1886). #1860 described a --server bundle RELOAD that
# introduces a NEW tableextension on a table already recorded wired by an
# earlier request in the SAME session, and claimed the extension's field
# OnValidate trigger never gets wired.
#
# _fieldTriggersWiredTables (AlRunner/Patches/RecordPatches.NclMetaTableBuilder.cs)
# does record a table as "done" the first time its BASE Record CLR type
# resolves, regardless of whether every tableextension for it has loaded yet.
# IF that cache survived across --server requests, a reload introducing a new
# tableextension on an already-recorded table WOULD be skipped by the
# ContainsKey guard before WireExtensionValidateHandlers could run again —
# that is the shape #1860 described. But it does not survive: RunAllBundlesForServer
# (AlRunner/Program.cs) calls BcRuntime.ResetForNewBundleReload() unconditionally
# once per runTests request, which clears _fieldTriggersWiredTables (among ~20
# other per-bundle caches) via RecordPatches.ResetForReload(). So the
# interleaving #1860 describes cannot occur on this codebase today.
#
# This script therefore pins the reset in place rather than proving a live
# bug: it goes RED if a future change makes the reload path conditional or
# incremental (e.g. for performance) and skips that reset.
#
# RED-experiment recipe, to keep this claim checkable: comment out ONLY the
# `_fieldTriggersWiredTables.Clear()` line inside RecordPatches.ResetForReload()
# (AlRunner/Patches/RecordPatches.cs), rebuild Release, and re-run this
# script. Request 2 must then fail with `Expected 'validated:payload' but got
# ''` — that failure is the falsifiable proof this test is guarding against.
#
# Fixture: tests/runner-extras/server-reload-dep (table only, no extension) +
# tests/runner-extras/server-reload-main (tableextension with a field
# OnValidate trigger + the proving test). Request 1 sends ONLY the dep path;
# request 2 sends [dep, main]. RED (reset broken): request 2's test fails or
# errors because Validate("Extra") never runs the trigger, so Log stays empty
# and the assertion mismatches. GREEN (reset intact): request 2 passes.
#
# Usage:
#   scripts/tests/server-reload-test.sh [--runner "CMD"] [extra runner args...]
#
# Requires: jq. Exit 0 = the reload wiring holds; exit 1 = it doesn't.
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

cp -r "$REPO/tests/runner-extras/server-reload-dep" "$WORK/dep"
cp -r "$REPO/tests/runner-extras/server-reload-main" "$WORK/main"

mkfifo "$FIFO"
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

# Sends one runTests request for the given source paths and echoes the
# summary line. Response stream is `{"type":"test"}* {"type":"summary"}`.
request_and_summary() {
    local before summaries paths_json
    before=$(grep -c '"type":"summary"' "$OUT" 2>/dev/null || true)
    paths_json=$(printf '"%s",' "$@")
    paths_json="[${paths_json%,}]"
    printf '{"command":"runTests","sourcePaths":%s}\n' "$paths_json" > "$FIFO"
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

echo "── request 1: dep alone — wires SRW Item's base with NO extension yet ──"
summary=$(request_and_summary "$WORK/dep") || exit 1
exit_code=$(jq -r '.exitCode' <<<"$summary")
if [[ "$exit_code" == "0" ]]; then
    pass "dep-only request completed, exitCode 0"
else
    fail "expected exitCode 0 loading the dep alone, got: $summary"
fi

echo "── request 2 (RELOAD): dep+main — main's tableextension must still wire ──"
summary=$(request_and_summary "$WORK/dep" "$WORK/main") || exit 1
exit_code=$(jq -r '.exitCode' <<<"$summary")
failed=$(jq -r '.failed + .errors' <<<"$summary")
total=$(jq -r '.total' <<<"$summary")
if [[ "$exit_code" == "0" && "$failed" == "0" && "$total" -ge 1 ]]; then
    pass "exitCode 0, $total test(s), 0 failed — reload extension trigger fired"
else
    fail "expected exitCode 0 with the reload extension's OnValidate trigger firing, got: $summary"
    grep '"type":"test"' "$OUT" | tail -6 >&2
fi

if (( FAILURES > 0 )); then
    echo "$FAILURES assertion(s) failed" >&2
    exit 1
fi
echo "all server-reload assertions passed"
