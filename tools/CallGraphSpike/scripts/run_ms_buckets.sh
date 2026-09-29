#!/usr/bin/env bash
# Density of static reachability over Microsoft's BaseApp test buckets: one graph per bucket
# (the shared platform and library apps plus that bucket).
#
# usage: run_ms_buckets.sh <src dir from extract_sources.py> <out.json> buckets|proc-buckets [flags...]
#        then: python3 ms_report.py <out.json>
set -euo pipefail
SRC="$1"; OUT="$2"; MODE="$3"; shift 3
HERE="$(cd "$(dirname "$0")/.." && pwd)"
dotnet build "$HERE/CallGraphSpike.csproj" -c Release -v q > /dev/null
A=()
for app in "Base Application" "System Application" "Business Foundation" "System" "Application Test Library" \
           "Any" "Library Assert" "Library Variable Storage" "Test Runner" "Permissions Mock" \
           "Tests-TestLibraries" "System Application Test Library" "Business Foundation Test Libraries" "Library-NoTransactions"; do
  [ -d "$SRC/$app" ] || { echo "refusing: $SRC/$app missing" >&2; exit 3; }
  A+=(--app "$app=$SRC/$app")
done
for d in "$SRC"/Tests-*; do
  b="$(basename "$d")"
  case "$b" in Tests-TestLibraries|Tests-Local) continue;; esac
  A+=(--bucket "$b=$d")
done
exec dotnet "$HERE/bin/Release/net8.0/CallGraphSpike.dll" "$MODE" "${A[@]}" --out "$OUT" "$@"
