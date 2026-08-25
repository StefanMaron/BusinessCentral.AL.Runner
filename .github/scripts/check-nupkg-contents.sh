#!/usr/bin/env bash
# Fails if the packed tool nupkg ships BC service-tier / Aspose / Graph binaries that
# are supposed to come from the user's own artifact cache at runtime, never from the
# package itself (see Directory.Build.targets' StripBcAppClosureFromCopyLocal target
# for the full mechanism, and AlRunner.csproj's "BC Service Tier DLL provisioning"
# comment — the runner never auto-downloads these; the user's artifact cache supplies
# them). v2.4.0 shipped 167 such files, 92.8 MB compressed / 196 MB uncompressed,
# including Aspose.PDF.dll (60.2 MB) and Aspose.Words.dll (28.0 MB) — this script is
# what would have caught that before release.
#
# Two checks, deliberately independent:
#   1. A deny-list of today's known offenders, by filename pattern.
#   2. A total package-size ceiling, so a FUTURE unwanted dependency that doesn't match
#      any name below still fails the build instead of silently regrowing the package.
set -euo pipefail

pkg="${1:?usage: check-nupkg-contents.sh <path-to-nupkg> [size-ceiling-bytes]}"
# Bytes. The fixed package is ~15 MB compressed; the ceiling leaves headroom for
# legitimate growth (new PackageReferences, more Win32-stub RIDs, …) while still
# catching a many-MB regression like Aspose/Graph/BusinessCentral being re-added.
size_ceiling_bytes="${2:-31457280}" # 30 MiB

actual_size=$(stat -c%s "$pkg")
echo "nupkg size: $actual_size bytes ($((actual_size / 1024 / 1024)) MiB), ceiling: $size_ceiling_bytes bytes"

listing=$(unzip -l "$pkg")

deny_patterns=(
  'Microsoft\.Dynamics\.[^/]*\.dll$'
  'Microsoft\.BusinessCentral\.[^/]*\.dll$'
  'Aspose\.[^/]*\.dll$'
  'Microsoft\.Graph[^/]*\.dll$'
)
# No allow-list exception. Microsoft.Dynamics.Nav.Ncl.dll used to be exempted here
# because Program.cs Cecil-rewrote it in place at the exact bin path CoreCLR's TPA probe
# needed to already know about, and TPA is computed once at native-host startup — so
# stripping the file made the process fall through to the RAW, un-rewritten copy in the
# artifact dir and crash at startup. NclShadowRuntime.cs now resolves that without
# shipping the file: it builds a runner-owned shadow directory (symlinks to this
# install, plus the Cecil-rewritten Ncl.dll copied from the user's own BC artifact
# cache) and re-execs into it, so a fresh process's TPA legitimately includes it. See
# Directory.Build.targets and NclShadowRuntime.cs's class doc for the full mechanism.

violations=()
for pat in "${deny_patterns[@]}"; do
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    violations+=("$line")
  done < <(echo "$listing" | grep -E "$pat" || true)
done

status=0
if [[ ${#violations[@]} -gt 0 ]]; then
  echo "::error::nupkg contains BC/Aspose/Graph binaries that must be resolved from the user's artifact cache at runtime, not shipped:"
  printf '  %s\n' "${violations[@]}"
  status=1
fi

if [[ "$actual_size" -gt "$size_ceiling_bytes" ]]; then
  echo "::error::nupkg size $actual_size bytes exceeds ceiling $size_ceiling_bytes bytes"
  status=1
fi

if [[ "$status" -eq 0 ]]; then
  echo "OK: nupkg contents and size are within bounds."
fi
exit "$status"
