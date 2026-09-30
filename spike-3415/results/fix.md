_Filed by Claude Code (main session) from the #4935 investigation._

## Problem

`BcAppSymbolCache.TryReadSourceFile` (`AlRunner/Patches/BcAppSymbolCache.cs`) calls `File.ReadAllBytes` on a whole dependency `.app` and builds a `ZipArchive` over the byte array, just to read one source file. For Base Application that means 98 MB per call.

#4462 (`9de433d4`) added a caller: `EnsureRealXmlPortMetadata` → `TryBuildDependencyXmlPortMetadata`. On a suite that touches Base App xmlports 1220, 1600, 1602, 1603, 1610 and 1611, that is six whole-file reads. Together they leave roughly 600 MB of large-object-heap garbage, which Server GC doesn't collect before the process reaches its peak.

## Measured in #4935

Details and commands are in https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/4935#issuecomment-5874284998. The runs were on a 12-core machine against BC 28.1.49838.54424, with a warm cache and the same test outcome on each side.

- **Where the jump happens:**
  - At #4462's parent, `67570499`, peak RSS is 3.43–3.47 GB.
  - At `9de433d4`, it is 4.41–4.51 GB.
  - Wall time and pass count don't change between the two.
- **Reads of the Base App `.app`:** 402 MB in total on v2.11.0 and 1,034 MB on main, measured with `strace`.
- **With the streaming read below applied on top of main:**
  - Peak RSS drops from 4.79–4.99 GB to 4.23–4.45 GB.
  - Wall time and pass count are unchanged.
  - Base App reads drop from 1,034 MB to 572 MB.

## Fix

- Read the entry by streaming. `AppLoader.OpenAppZip` already does this: it streams from a `FileStream` through `NavxZipView`, handles the NAVX header offset, and falls back for `.NEA` packages. Make it `internal` and use it in `TryReadSourceFile`.
- Split the entry lookup into a `TryReadSourceFromZip(ZipArchive, wanted)` helper. Keep the byte-array path only for the nested R2R `.app` case.
- Other callers of this reader: `RecordPatches.ActionRunObjectKindFromSource` (#4664) and `DependencyReportMetadata`.

## Acceptance criteria

- [ ] Allocation test: `TryReadSourceFile` on a fixture `.app` padded with a large stored entry allocates far less than the file size and still returns the right source text. It goes red if `File.ReadAllBytes` is restored.
- [ ] The streamed path returns the same text as before for a NAVX-prefixed `.app`, a `.NEA` runtime package, and a nested R2R wrapper.
- [ ] `DependencyXmlPortMetadataTests` and the report-metadata tests stay green.
- [ ] Before/after peak RSS on one suite in the PR body.
- [ ] Do NOT edit `CHANGELOG.md`.
