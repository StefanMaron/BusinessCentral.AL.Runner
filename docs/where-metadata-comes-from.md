# Where a table's metadata comes from

The runner needs BC's own answer for every object it serves — key counts, per-field `Editable`,
`DataClassification`, relations. **Where that answer comes from depends on what the app ships**, and
the three shipping shapes are genuinely different problems with different costs. This document names
them, because the distinction was being rediscovered per issue instead of written down once.

The route a table actually took is observable:

```
AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=1    # one line per built table: [table-metadata] <id> source=<route>
AL_RUNNER_TRACE_PAGE_METADATA_SOURCE=1     # one line per built page:  [page-metadata]  <id> source=<route>
```

Exactly `"1"` or `"2"` for the table flag, exactly `"1"` for the page one — any other value,
including `"true"`, is a silent no-op in both. A table has two route values, `bc-document` and
`derived`. A page has **three**: `bc-document`, `derived` (the AL-text derivation) and `symbol` (a
precompiled dependency's `SymbolReference.json`, the middle row of the table below). The third has no
table-side analogue and is not folded into `derived`, because the compiled-vs-precompiled split is
precisely what the page trace exists to measure.

Neither flag has a **failure route**, and neither may grow one: **availability** decides which route
an object takes, and a failure is never *re-routed* to a derivation — substituting a weaker answer on
error is what `loud-failures.md` exists to prevent.

**A failure there is only PARTLY loud, and this page must not imply otherwise.** The site sits
inside `BuildNCLMetaTable`'s catch, which **#3590** narrowed: a refusal naming what could not be
read — `BcShapeGapException`, `BcAppSymbolReadException`, a typed `RunnerOutOfScopeException` —
reaches the caller and names the table. An **ordinary** construction failure is still absorbed
into `return null` for both routes alike, because every caller has a legitimate not-found branch
for a table that cannot exist.

So for that remaining class, a genuine load failure and "no document was available" are still
**indistinguishable from the trace**: a table that failed to build shows as `derived` exactly like
one that never had a document. `RecordPatches.NclMetaTableBuilder.cs` states the split at the site
itself.

**The page trace inherits exactly the same limit, and states it rather than implying it away.**
`TryGetBcPageControlDocument` memoises a null when the document does not parse, so a page whose
document genuinely failed to load takes the derivation arm and is traced `derived` — the same value a
page that never had a document gets. Neither trace can tell you a document loaded *successfully*;
both tell you only which route was taken. A trace implying the stronger guarantee would be worse than
no trace.

## The three shapes

| the app ships | source? | metadata XML? | what the runner does | cost |
|---|---|---|---|---|
| **source only** | yes | no | compiles it; the emitter's document is captured and consumed | one compile, paid once per app per BC version |
| **source + a prebuilt DLL** (Base Application, most Microsoft apps) | yes | **no** | reads `SymbolReference.json` — **does not compile** | a symbol-file read |
| **a runtime package** (`.NEA`) | no | **yes** | reads `/bin/*.xml` straight out of the package | a container decode |

The middle row is the one that keeps getting mis-stated, so it is worth being exact.

### An R2R `.app` ships source AND no metadata

**Where to look, because the obvious check contradicts this.** The source is in the **nested inner
`.app`**; the outer R2R wrapper holds 9 entries and **zero** `.al` files. So a naive unzip of the
outer package reports no source at all and looks like it disproves the row above. Open the inner
package. (Re-measured independently on build `28.4.53241.54407`: **8,096** `.al` files, 0 `bin/`
entries — a one-file drift between 28.4 siblings, which is version drift and not a contradiction.)

Measured on `Microsoft_Base Application_28.4.53241.53989.app`: **8,095 source files, 0 `/bin`
metadata documents.** So "ships a DLL" does not mean "ships no source" — it means the source is
there but was compiled by Microsoft ahead of time, and **we do not want to compile it again**.

That is a deliberate choice, not a limitation. `.claude/rules/no-base-app-in-csharp-tests.md` has the
measurements: loading the Base Application floor costs about **70 seconds cold and 6 seconds warm per
runner invocation**. For an app that already ships a working DLL, recompiling it would be spending a
compile to learn what `SymbolReference.json` already states.

**And for Base Application specifically it is not merely expensive — it does not work at all.** From
`tests/expectations/metadata-equivalence/apps.json`, which is why that app is absent from the
equivalence harness:

> Base Application is deliberately ABSENT. Its emit needs a `PublicKeyToken=null` copy of
> `Microsoft.AspNetCore.StaticFiles` that no BC artifact ships, and without it the emitter produces
> **ZERO objects** (issue #3549).

So the symbol-file route is not a cost-saving fallback for this app. It is the only route that
produces anything. (Compiling Base Application is separately expensive — roughly 2 minutes and ~9 GB
peak RSS — but that is the *cost*, not the reason, and stating the cost as the reason has misled a
reader of this repository before.)

**So for this shape the answer is the symbol file, and the symbol file is held to a standard rather
than treated as second-class.**

## The symbol-file path is not a fallback of last resort

The standard, from #3533:

> Where we have the code, derive metadata faithfully by compiling it with BC's own compiler. Where we
> do not, fall back to `SymbolReference.json` — and that fallback must produce the **same answer** the
> source-backed generator would, for every piece of information the symbol file can express.

That is testable rather than aspirational, and it is tested: any app the runner source-compiles
yields **both** derivations in the same run — BC's metadata XML from `CaptureOutputter`, and ours
from `SymbolReference.json` — so the standard is a differential assertion over the same tables.

**It is enforced on every leg, not run by hand.** `MetadataEquivalenceHarnessTests` runs in
`bc-tests.yml` behind the step *"Generate BC metadata ground truth"*, which is explicitly **not**
`continue-on-error`. The ground truth is regenerated per leg rather than checked in, for two reasons
the workflow states: it is Microsoft's compiler output over Microsoft's source, which is not ours to
redistribute; and it belongs to one exact BC build, so a checked-in copy would either churn on every
Microsoft release or go stale silently — *"which is the failure mode this whole harness exists to
remove."*

Two properties make it hard to weaken by accident, and both matter more than the assertion itself:

- **`allowlist.json` fails in BOTH directions** — on any difference not listed, *and* on any listed
  entry that no longer matches anything. So a reader fix must delete its entries in the same change,
  and a BC version that introduces a new property surfaces as a new undeclared difference rather
  than passing silently. Every entry carries exactly one reason **kind** — `issue`, `outOfScope`,
  `oracleLimitation`, `cannotExpress` — because the four need different evidence.
- **`apps.json` makes coverage mandatory.** Declaring an app there means the harness *fails* if no
  ground-truth bundle exists for it, so it cannot quietly measure less than it claims. Currently
  Business Foundation and System Application.

This is what replaced trial and error. A claim about the symbol path expressed inside this harness
cannot rot silently; the same claim as a standalone test can.

The gap that measurement found was one-directional every time: BC sets a real value, the runner took
a constructor default.

| property | wrong | BC said | we said |
|---|---|---|---|
| `Editable` | 1,262 / 2,036 | `False` | `True` |
| `DataClassification` | 1,561 / 2,036 | `SystemMetadata` / `EndUserPseudonymousIdentifiers` | `CustomerContent` |

#3545 fixed two defined meanings of an *absent* property the reader was ignoring: `Editable` defaults
to `true`, and a field's `DataClassification` inherits the table's. Both issues are closed.

**What the symbol file cannot express, it cannot express**, and no amount of reader work changes
that — which is why the compile path exists for apps that ship source without a DLL, and why the
runtime-package path exists for packages that ship metadata without source.

## Why this matters for the conversion work

`#3562` tracks converting every metadata consumer to BC's captured document. It has two halves, and
conflating them makes the tracker read as finished when it is half done:

- **Objects the runner compiles** — done, merged, verified. These take the `bc-document` route.
- **Dependencies** — where nearly all of the error surface lives, and the un-started half. `#3549` is
  the issue, with 20 more blocked behind it.

Measured, so it is not a worry anyone needs to re-raise: a source-compiled dependency's document is
**consumed, not merely captured**. It is persisted as a sidecar, replayed by `DependencyLoader`, and
the table takes the `bc-document` route on a cold run *and* on a cache HIT.

Measured too, since **#3750** gave pages the same instrument. On a bundle declaring the Base
Application floor and reading the Page Control Field virtual table, one cold run traced **2,759
pages**: **1** `bc-document` — exactly the one page the bundle source-compiles — and **2,758**
`symbol`, every precompiled Base Application page. So the split attributes to compiled-vs-precompiled
on the nose, and `PageControlFieldFromBcDocument` demonstrably **has a live consumer**: the one page
that had a document took the document route rather than falling through.

Two honest riders on that number. **Zero pages took `derived`** in that run — every source-compiled
page got a document, so the AL-text arm was never taken; it is reachable and covered by
`PageMetadataSourceTraceTests`, but this bundle does not exercise it. And per the #3590 limit above, a
`derived` count can never be read as "no document was available" alone. **Pageextensions still have no
route trace of their own** — their controls are merged into the base page's rows, so they are counted
under the base page's route rather than separately.

## The sequencing, and why

The repository owner's call, recorded here because it is a judgement about what the runner is for
rather than a technical constraint:

> **Runtime packages are a side story. Get the regular cases working first.**

The regular cases are the first two rows of the table — an app that ships source, and an app that
ships source plus a DLL where we read symbols instead of recompiling. Between them they cover
essentially every real project. The runtime-package reader is merged (#3537, #3733) and is not going
anywhere; **executing** from a runtime package remains unproven and is tracked separately as #3732.

## Sister material

- `.claude/rules/no-base-app-in-csharp-tests.md` — what loading the Base Application floor costs, and
  why a fixture declares `platform` and never `application`
- `docs/object-metadata-capture.md` — how the emitter's document is captured
- `docs/metadata-equivalence.md` — the differential harness that holds the two derivations to each other
- `docs/runtime-packages.md` — the `.NEA` container, and what reading one does and does not prove
