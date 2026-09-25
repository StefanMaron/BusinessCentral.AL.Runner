#!/usr/bin/env bash
# app-package-pipeline.sh — compile every package fixture under
# tools/metadata-ground-truth/fixtures/ with BC's own compiler, on one BC build, and judge each
# result against the fixture's expected.json (#4495).
#
# The compile is tools/metadata-ground-truth, the same path the metadata-equivalence ground
# truth uses; the verdict is tools/check-app-compile-bundle.py. Placement and cost:
# docs/app-package-compile-pipeline.md.
#
# Usage:
#   tools/app-package-pipeline.sh --artifacts <bc-artifact-dir> [--out <dir>]
#
# Exit codes: 0 every fixture passed; 1 at least one compiled wrong or failed to compile;
# 3 nothing could be measured (no artifacts, no fixtures, the tool did not build).

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS=""
OUT=""
CONFIG=Release

while [ $# -gt 0 ]; do
  case "$1" in
    --artifacts) ARTIFACTS="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument '$1'" >&2; exit 3 ;;
  esac
done

if [ -z "$ARTIFACTS" ] || [ ! -d "$ARTIFACTS" ]; then
  echo "::error::app-package-pipeline: --artifacts must name a BC artifact directory (got '$ARTIFACTS')" >&2
  exit 3
fi
[ -n "$OUT" ] || OUT="$(mktemp -d)"
mkdir -p "$OUT"

FIXTURES_DIR="$REPO_ROOT/tools/metadata-ground-truth/fixtures"
mapfile -t FIXTURES < <(find "$FIXTURES_DIR" -mindepth 1 -maxdepth 1 -type d | sort)
if [ "${#FIXTURES[@]}" -eq 0 ]; then
  echo "::error::app-package-pipeline: no fixtures under $FIXTURES_DIR — this run would measure nothing" >&2
  exit 3
fi

if ! dotnet build "$REPO_ROOT/tools/metadata-ground-truth/MetadataGroundTruth.csproj" \
    -c "$CONFIG" -p:ServiceTierPath="$ARTIFACTS" --nologo -v quiet; then
  echo "::error::app-package-pipeline: tools/metadata-ground-truth did not build against $ARTIFACTS" >&2
  exit 3
fi
TOOL="$REPO_ROOT/tools/metadata-ground-truth/bin/$CONFIG/net8.0/metadata-ground-truth.dll"

worst=0
for fx in "${FIXTURES[@]}"; do
  name="$(basename "$fx")"
  pkg="$OUT/$name.app"
  bundle="$OUT/$name"
  echo "[app-package-pipeline] $name on $(basename "$ARTIFACTS")"
  if ! python3 "$REPO_ROOT/tools/pack-app-fixture.py" "$fx" "$pkg"; then
    echo "::error::app-package-pipeline: $name could not be packed" >&2
    [ "$worst" -eq 1 ] || worst=3
    continue
  fi
  dotnet "$TOOL" --app "$pkg" --out "$bundle" --artifacts "$ARTIFACTS"
  tool_rc=$?
  python3 "$REPO_ROOT/tools/check-app-compile-bundle.py" "$bundle" "$fx/expected.json"
  check_rc=$?
  # A tool failure that left no bundle is still a measured failure of this package's compile,
  # not an unmeasurable one: the tool ran and said no.
  if [ "$check_rc" -eq 3 ] && [ "$tool_rc" -ne 0 ]; then check_rc=1; fi
  if [ "$check_rc" -ne 0 ]; then
    echo "::error::app-package-pipeline: $name failed on $(basename "$ARTIFACTS") (tool rc=$tool_rc, check rc=$check_rc)" >&2
  fi
  if [ "$check_rc" -eq 1 ]; then worst=1
  elif [ "$check_rc" -eq 3 ] && [ "$worst" -eq 0 ]; then worst=3
  fi
done

exit "$worst"
