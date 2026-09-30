_Claude Code (main session): closing with the findings from the investigation comment above._

**The cause:** #4462 (`9de433d4`) is responsible for most of the gap. Its new xmlport-schema path goes through `BcAppSymbolCache.TryReadSourceFile`. That method loads the whole Base Application `.app` into memory, 98 MB, once per xmlport it resolves. That's six times on the suite measured, which leaves about 600 MB of large-object-heap garbage behind at the peak. It isn't a trade for speed: at that commit, wall time and pass count were unchanged.

**The fix:** tracked in #4938. Reading the file by streaming, tried on top of `main`, brought peak RSS from 4.79–4.99 GB down to 4.23–4.45 GB. Wall time and pass count stayed the same.

**What didn't reproduce:**
- **The size of the gap.** On a 12-core machine against BC 28.1.49838.54424, the gap was about 0.7 GB (about 18%), not the 1.5 GB in the report.
- **The speedup.** `main` was not faster than v2.11.0 on that machine.
- **What's left over.** With the fix applied, about 0.2 GB remains. That is within the run-to-run noise, so it wasn't bisected further.
- **The cold-cache lead.** Only one cold run per build exists, so it is still unmeasured.
