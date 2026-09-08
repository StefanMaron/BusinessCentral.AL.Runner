#!/usr/bin/env bash
# gen-metadata-ground-truth.sh — produce the ground truth the metadata-equivalence harness
# measures against: BC's OWN metadata-emitter output for the Microsoft apps listed in
# tests/expectations/metadata-equivalence/apps.json. Issue #3533.
#
# Runs once per BC build. Business Foundation is ~3s, System Application ~14s, so this is a
# provisioning cost, not a per-run one. Re-running is cheap and idempotent; a bundle is keyed
# on the BC build so a new artifact gets its own directory rather than overwriting.
#
# Usage:
#   tools/gen-metadata-ground-truth.sh                       # every app in apps.json
#   tools/gen-metadata-ground-truth.sh --artifacts <dir>     # a specific BC build
#   tools/gen-metadata-ground-truth.sh --out <dir>           # somewhere other than the default
#   tools/gen-metadata-ground-truth.sh --app "System Application"
#
# Exit codes: 0 every requested app produced a bundle; 1 at least one did not (and says which).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS=""
OUT="${AL_RUNNER_METADATA_GROUND_TRUTH:-}"
ONLY=""
CONFIG=Release

while [ $# -gt 0 ]; do
  case "$1" in
    --artifacts) ARTIFACTS="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --app) ONLY="$2"; shift 2 ;;
    -c|--configuration) CONFIG="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument '$1'" >&2; exit 2 ;;
  esac
done

ARTIFACTS_ROOT="${AL_RUNNER_ARTIFACTS_ROOT:-$HOME/.local/share/al-runner/artifacts}"

if [ -z "$ARTIFACTS" ]; then
  # Newest cached build. The runner's own default selection is per-major; here any single
  # build is a legitimate subject, because a bundle records the build it came from and the
  # harness compares each bundle against the very .app it was generated from.
  ARTIFACTS="$(ls -d "$ARTIFACTS_ROOT"/*/ 2>/dev/null | sort -V | tail -1 || true)"
  ARTIFACTS="${ARTIFACTS%/}"
fi
if [ -z "$ARTIFACTS" ] || [ ! -d "$ARTIFACTS" ]; then
  echo "no BC artifacts found under $ARTIFACTS_ROOT — provision one first (al-runner provision)." >&2
  exit 1
fi
BC_BUILD="$(basename "$ARTIFACTS")"

if [ -z "$OUT" ]; then
  OUT="$(dirname "$ARTIFACTS_ROOT")/metadata-ground-truth"
fi

echo "[ground-truth] BC build $BC_BUILD -> $OUT/$BC_BUILD"

dotnet build "$REPO_ROOT/tools/metadata-ground-truth/MetadataGroundTruth.csproj" \
  -c "$CONFIG" -p:ServiceTierPath="$ARTIFACTS" --nologo -v quiet
TOOL="$REPO_ROOT/tools/metadata-ground-truth/bin/$CONFIG/net8.0/metadata-ground-truth.dll"

failed=0
covered=0
APPS="$(python3 -c '
import json, sys
for a in json.load(open(sys.argv[1]))["apps"]:
    print(a["publisher"] + "\t" + a["name"])
' "$REPO_ROOT/tests/expectations/metadata-equivalence/apps.json")"

while IFS=$'\t' read -r publisher name; do
  [ -n "$name" ] || continue
  if [ -n "$ONLY" ] && [ "$ONLY" != "$name" ]; then continue; fi

  pkg="$(find "$ARTIFACTS" -name "${publisher}_${name}_*.app" -print -quit 2>/dev/null || true)"
  if [ -z "$pkg" ]; then
    echo "::error::no ${publisher}_${name} package under $ARTIFACTS" >&2
    failed=1
    continue
  fi
  version="$(basename "$pkg" .app)"
  version="${version##*_}"
  dest="$OUT/$BC_BUILD/${publisher}_${name}_${version}"
  mkdir -p "$dest"

  args=(--app "$pkg" --out "$dest" --artifacts "$ARTIFACTS")

  echo "[ground-truth] $publisher $name $version"
  if dotnet "$TOOL" "${args[@]}"; then
    covered=$((covered + 1))
  else
    echo "::error::ground truth generation FAILED for ${publisher}_${name} — the harness will " \
         "report this app as uncovered rather than compare less of it" >&2
    rm -rf "$dest"
    failed=1
  fi
done <<< "$APPS"

echo "[ground-truth] $covered app(s) bundled under $OUT/$BC_BUILD"
exit $failed
