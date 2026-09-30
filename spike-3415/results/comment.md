_Posted by Claude Code (investigation agent for the main session)._

## Findings

I reproduced part of the gap and found one commit that accounts for most of it. It looks like a clean fix, not a speed trade-off.

### Reproduction

Same private suite (1,013 tests), BC 28.1.49838.54424, warm cache per binary, `--cache` outside any worktree, peak RSS from `wait4` `ru_maxrss` (the same number `time -v` reports). Both binaries built from source against `-p:_BCVersion=28.1.49838.54424`.

| | v2.11.0 (`25d5be22`) | main (`55387fa1`) |
|---|---|---|
| Peak RSS, default GC | 4.09 / 4.17 GB | 4.99 / 4.86 / 4.79 / 4.82 GB |
| Peak RSS, `DOTNET_GCHeapCount=1` + `DOTNET_GCConserveMemory=9` | 2.05 / 2.07 GB | 2.25 / 2.27 GB |
| Wall | 43-44 s | 45-48 s |
| Pass / fail | 867 / 146 | 869 / 144 |

The released v2.11.0 tool and the locally published main binary gave the same picture (4.02-4.24 GB vs 4.67-5.03 GB). On this machine the gap is about 0.7 GB (about 18%), not 1.5 GB, and main is not faster than v2.11.0 in wall time. I could not reproduce the 35% speedup. The gap is the same without `--test-data`, so the backup reader is not involved.

### Cause: #4462 (`9de433d4`)

Bisected by peak RSS over the commits touching runner code (no `--test-data`, two warm runs per point):

| commit | default GC | 1 heap |
|---|---|---|
| `67570499` (#4463, parent) | 3.47 / 3.43 GB | 1.56 GB |
| `9de433d4` (#4462) | 4.41 / 4.51 / 4.42 GB | 1.88 GB |

No change in wall time or pass count at that step.

What happens: #4462 made `EnsureRealXmlPortMetadata` build metadata for xmlports in precompiled dependencies. On this suite that fires for six Base Application xmlports (1220, 1600, 1602, 1603, 1610, 1611). Each one calls `BcAppSymbolCache.TryReadSourceFile`, which does `File.ReadAllBytes` on the whole Base Application `.app` (98 MB here) to read one source file. That is about 600 MB of large-object-heap garbage, and with Server GC on 12 heaps it is not collected before the peak. `strace` confirms it: the Base Application `.app` is opened 11 times at the parent commit and 17 times at `9de433d4`, and bytes read from it go from 402 MB (v2.11.0) to 1,034 MB (main). #4664 later added a third caller of the same reader (`ActionRunObjectKindFromSource`).

### Check: the fix applied on top of main

I changed `TryReadSourceFile` to open the `.app` with the existing streaming reader (`AppLoader.OpenAppZip`, made `internal`), which reads only the zip directory and the one entry. The in-memory path stays for the nested R2R `.app` case.

| | main | main + streaming read |
|---|---|---|
| Peak RSS, default GC | 4.99 / 4.86 / 4.79 / 4.82 GB | 4.30 / 4.23 / 4.45 GB |
| Peak RSS, 1 heap | 2.25 / 2.27 GB | 1.84 / 1.84 GB |
| Wall | 45-48 s | 45-46 s |
| Pass / fail | 869 / 144 | 869 / 144 |
| Base Application `.app` bytes read | 1,034 MB | 572 MB |

That recovers about 0.5 GB of the 0.7 GB gap with no loss of speed or tests. With one GC heap, main with the change uses less memory than v2.11.0. The remaining 0.2 GB with default GC is small next to the run-to-run noise (about 0.3 GB) and I did not bisect it. Streaming `ReadSymbolReferences` the same way made no further difference on warm runs.

### Not measured

- The owner's 3.8 GB vs 5.4 GB and 110 s vs 80 s. My absolute numbers are lower on both sides.
- The cold-cache lead. Cold first runs here: v2.11.0 117 s, main 134 s, main with the change 117 s. That is one run each, so treat it as a hint only.
- Where the remaining 572 MB of Base Application reads on main come from.
