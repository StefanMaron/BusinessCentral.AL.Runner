#!/usr/bin/env bash
# #1672: build the prebuilt libwin32_stubs.<rid>.so shims shipped beside win32_stubs.c
# so a Linux install with no C toolchain still resolves Win32 imports (see
# Win32Stubs.GetOrBuild's load order). Invoked by AlRunner.csproj's
# BuildWin32PrebuiltStubs MSBuild target, from AlRunner/ as the working directory.
#
# Kept as a standalone script (rather than an inline <Exec Command="bash -c ...">)
# because MSBuild's Exec task re-quotes inline commands in a way that breaks
# backtick/${var} shell substitution — see the #1672 PR description for the
# concrete failure (rid resolved empty) this sidesteps.
#
# Best-effort per compiler; a missing compiler for a given RID is not an error
# here — the caller (BuildWin32PrebuiltStubs) only packs whichever .so files
# actually got produced, and Win32Stubs.GetOrBuild's compile-from-source
# fallback (#1669's loud failure if even that has no compiler) covers the rest.
set -uo pipefail
cd "$(dirname "$0")"

case "$(uname -m)" in
  x86_64)  native_rid=linux-x64 ;;
  aarch64) native_rid=linux-arm64 ;;
  *)       native_rid= ;;
esac

if [ -n "$native_rid" ] && command -v cc >/dev/null 2>&1; then
  cc -shared -fPIC -o "libwin32_stubs.${native_rid}.so" win32_stubs.c
fi

if command -v aarch64-linux-gnu-gcc >/dev/null 2>&1; then
  aarch64-linux-gnu-gcc -shared -fPIC -o libwin32_stubs.linux-arm64.so win32_stubs.c
fi

exit 0
