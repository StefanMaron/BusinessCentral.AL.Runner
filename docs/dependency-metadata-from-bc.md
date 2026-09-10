# A dependency's table metadata from BC's own emitter

Issue #3549, "Producer A". The compiled-app half of the metadata chain (#3548, #3552) is
merged: a table the runner compiles gets its `NCLMetaTable` built by BC from BC's own emitted
document. This file records the **dependency** half — what was measured, what now works, and
what is deliberately still switched off.

Everything below was measured on this box against BC **28.1.49838** (engine `28.1.49838.53910`,
packages `28.1.49838.54308`) unless another build is named.

## The gap, and why it is not visible from the compiled-app path

`DependencyLoader.LoadOne` returns at Tier 1 (a precompiled sidecar DLL) or Tier 2 (R2R
`publishedartifacts/*.dll`) whenever compiled code is available, which for Microsoft's apps it
always is. **Tier 3, the source compile, is the only tier that runs BC's emitter** — and the
emitter is what produces the document `AlObjectMetadataRegistry` captures.

So a dependency's tables were described by the runner's `SymbolReference.json` hand-derivation
and never by BC's own answer, even though the package ships the source BC would need.

### Measured, AL-observable, on a real Microsoft dependency

Business Foundation table **310 "No. Series Relationship"**, read from AL through `RecordRef`:

| | route | `KeyCount` | `SystemCreatedBy.Relation` | `SystemModifiedBy.Relation` |
|---|---|---|---|---|
| before | `derived` | 3 | **0** | **0** |
| after | `bc-document` | 3 | **2000000120** | **2000000120** |

`2000000120` is the User table — BC's own answer, and the value #3549 names in its acceptance
criteria. The route is read with `AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=1`.

The mechanism behind the difference: BC's emitted document declares only the AL-declared fields
and keys, and BC's **loader** (`MetaTable.CreateMetaTableFromXml` → `AssignFromMetaTable`) adds
the platform system fields and their relations. Taking the document route means BC does that
work; deriving means the runner does, and the runner does not add the relations.

## Why the whole matrix is not simply switched on

`Compilation.Emit` is **atomic per module**: one object BC cannot emit yields zero documents for
the entire app, not a partial result. Measured per app:

| app | `.al` files | outcome | documents |
|---|---|---|---|
| Business Foundation | 96 | clean, 6.0–6.2 s | **55** (11 tables, 16 codeunits, 13 permission sets, 9 pages, 5 tableextensions, 1 enum) |
| System Application | 1,319 | clean since #3745, 10.9–13.1 s | **1,218** (138 tables, 533 codeunits, 224 pages, 164 permission sets, 142 enums, 7 queries, 5 runtime deltas, 4 xmlports, 1 report) |
| Base Application | 8,025 | not attempted — see below | — |

The feature stays **opt-in**, `AL_RUNNER_DEP_METADATA_FROM_BC` — the cost of the first compile
per (app, BC version) is real even though it is paid once:

- unset / `0` — off. Byte-identical to the behaviour before #3549; verified as a regression arm.
- `1` — every source-shipping dependency.
- `Business Foundation` (comma-separated names) — exactly those apps.

Since #3745 both source-shipping Microsoft apps the runner compiles — Business Foundation and
System Application — succeed, so `1` no longer means "a run that refuses to start"; naming apps
individually is now a cost choice rather than a way around a blocker.

### System Application: fixed in #3745 by staging the shim the ground truth already used

The failure was never in the AL. `tools/metadata-ground-truth/` compiled the same app cleanly
because it ships a **.NET reference shim** at the head of BC's probing paths; `BcCompiler` did
not, so one DotNet declaration failed to bind and `Emit`'s per-module atomicity turned that into
zero documents for all 1,319 files.

**The specific assembly, measured rather than inferred.** System Application's
`SamplingPerfProfilerImpl` calls `JsonSerializer.Deserialize(TextReader, Type)`. The service tier
ships only the **net6.0** build of `Newtonsoft.Json`, whose `TextReader` parameter is typed
against `System.Runtime 6.0.0.0`; no .NET 6 reference assemblies exist on a net8 box, so that
parameter type never resolves and the call raises **AL0133**. The **netstandard2.0** build of the
same Newtonsoft version binds against netstandard, which does resolve.

How it was isolated, on BC 28.1.49838: removing the shim directory from the ground-truth tool —
the only change — took it from `success=True objects=1218 errors=0` to
`success=False objects=0 errors=1` with that AL0133 named. Three other candidate differences
were tested and **ruled out**: the `System.app` version (28.0.53872.0 vs the 28.0.54265.0 the
runner resolves — 1,218 objects either way), the package's resources (stripping `addin/` gives
62 × AL0327, a different signature), and the DotNet probing *order*, which already matched.

`AlRunner.csproj` now stages that shim into a `dotnet-shims` directory as build **content**, so
it travels with `al-runner.dll` into every output that references the project, and
`BcCompiler.BuildDotNetProbingPaths` probes it **first** — ahead of the service tier, which is
load-bearing, because the tier is where the copy that does not bind lives.

**The 23 AL0185 "is missing" declaration errors this used to report were a consequence, not a
cause.** With the shim in place they become 62 × AL0327 for control-add-in resources the
producer's work directory does not carry, which are non-fatal: the emit produces all 1,218
documents anyway.

Note what this does **not** fix: Base Application's blocker is a different assembly
(`Microsoft.AspNetCore.StaticFiles` with `PublicKeyToken=null`, which no BC artifact ships at
all — verified absent from the 28.1 artifacts) and it remains excluded.

### Base Application: a hard blocker, not a cost question

Excluded in code by name, and it is **not** about the ~9 GB peak RSS of compiling it. Its emit
needs a `PublicKeyToken=null` copy of `Microsoft.AspNetCore.StaticFiles` that no BC artifact
ships, and without it the emitter produces **zero objects**.
`tests/expectations/metadata-equivalence/apps.json` records the same exclusion for the same
reason, and attributes it to this issue.

## Availability decides the route; a failed compile is loud

The constraint #3549 names as its main risk. Producing a document is a compile, so there are two
failure modes and they must never be spelled the same way:

| | answer |
|---|---|
| the package ships no source | return 0. The consumer's availability gate finds no document and keeps the derivation — the ordinary state of every symbol-only package (#3533, #3545). |
| the package ships source and the compile or emit failed | **throw** `DependencyLoadException`. |

`DependencyMetadataProducer.ReadSource` is the seam, and it separates the two by **answer
shape**: an empty list means the package ships no AL source, while a package that cannot be
read **throws** `METADATA-SOURCE-UNREADABLE` rather than returning empty. That is what keeps
"unavailable" from ever being inferred from a failure — an unreadable package cannot enter
through the absence door at all. #3590 is why that distinction is load-bearing rather than
stylistic:
`BuildNCLMetaTable` swallows a construction failure into a cached null with its log line
filtered out by default, so a failure that reached the consumer would be indistinguishable from
a document that was never produced.

A **zero-document emit is a throw**, not an empty cache entry: caching it would record "this app
has no metadata" permanently and leave every table on the derivation with nothing saying why.
That is what the System Application row above produces, loudly, rather than silently.

A **corrupt cache entry** is neither: it is deleted and recompiled, because it is a cache
problem rather than the app's build breaking.

## The cache

One entry per **(app id, app version, BC version)** under the `dep-metadata` cache root, in
`AlObjectMetadataRegistry`'s sidecar format. The BC version is part of the key because the
documents are one exact build's emitter output; a key without it serves one build's metadata to
another. Written through a temp file and moved into place, so an interrupted run leaves no
half-file.

Cost is paid once per app per BC version — a provisioning cost, not a per-run one. Business
Foundation: 6.0–6.2 s to produce, then a cache hit.

## Verified arms

All against `bfonly` (a bundle whose only dependency is Business Foundation), table 310:

| arm | result |
|---|---|
| cold, feature on | `WROTE … 55 document(s), 6248ms`; `bc-document`; `Relation=2000000120` |
| warm, same cache | `cache HIT … 55 document(s)`; `bc-document`; same values, no recompile |
| after deleting `dep-metadata/` | recompiled; `bc-document`; same values |
| cache entry corrupted to non-JSON | `cache entry unreadable … recompiling`; `bc-document`; same values |
| **feature off (default)** | `derived`; `Relation=0`; no producer activity — today's behaviour exactly |

## A table with a tableextension keeps the derivation, by design

Business Foundation table **230 "Source Code"** stays on `derived` even with the feature on, and
that is correct rather than a gap: `ObsoleteSourceCodeExt` extends it, and
`ShouldBuildTableFromBcDocument` deliberately keeps the derivation for a table carrying a
`modify(...)`-declaring or cross-app extension (#3600). BC's per-app document cannot see another
app's extension, so the merged view the runner builds is the more complete answer there.

Of Business Foundation's 11 captured tables, 5 carry an extension from the same app (230, 242,
308, 309, 6635) and the rest take the document route.

## Why this is not a corpus test, measured rather than asserted

The claim "a table's `SystemCreatedBy` relates to User (2000000120)" is plain BC behaviour and
belongs upstream — and **it is already there and already green**:
`tests/al-language/record/TestFieldDataClassificationVirtualTable.al`, codeunit 60965,
`RecordRef_SystemCreatedBy_Relation_IsTheUserTable`.

What this issue fixes is not that claim but the **route** by which the runner answers it, and
the corpus cannot reach the broken route. Three measurements, not an inference:

1. **A bundle-declared table already answers correctly.** A table declared in the app under
   test takes `bc-document` and answers `2000000120` on `main`, with the feature off — so the
   existing corpus test passes today and would still pass with this change reverted. It cannot
   fail for the reason this issue exists.
2. **A corpus-shaped precompiled dependency also already answers correctly.** The corpus's one
   genuine dependency package, `AL Language_AL Internals Test Fixture_1.0.0.0.app` (table 61001
   `ALT Internal Table`), was run through the runner as a real `.alpackages` dependency: route
   `bc-document`, `Relation=2000000120`, feature off. It carries **no** `publishedartifacts/*.dll`,
   so it takes Tier 3 — the source compile — which already runs BC's emitter and captures.
3. **No package the corpus ships is R2R.** Checked across all six in
   `tests/al-language/.alpackages/`, including its Base Application and Business Foundation:
   `R2R=False` for every one, and they carry AL source directly rather than a nested package.

The defect is specific to a dependency the runner loads from **compiled code** (Tier 1/Tier 2),
which is how BC's own artifacts ship and how every real project consumes Microsoft's apps. The
corpus has no such dependency, so a corpus test cannot distinguish fixed from broken — which is
what makes this a `Corpus-NA:` rather than a corpus PR.

Worth stating explicitly, because the opposite conclusion has been reached before on thinner
evidence (#2518, where a defect assumed precompiled-only reproduced on a source-compiled table
and corpus PR #165 merged green): the check here is not "the corpus compiles from source, so it
cannot reach this". It is that the specific assertion **was run** against the corpus's own
dependency package and came back already-correct.

## Reproducing

```bash
# ground truth for the harness, and proof the same app compiles cleanly outside the runner
tools/gen-metadata-ground-truth.sh --artifacts ~/.local/share/al-runner/artifacts/<build>

# the route a table actually took
AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=1 \
AL_RUNNER_DEP_METADATA_FROM_BC="Business Foundation" \
AL_RUNNER_TRACE_DEP_METADATA=1 \
  dotnet run --project AlRunner -- <bundle> --package-cache ~/.al-runner/platform-apps --cache <private>
```

`AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=2` additionally dumps per-field `editable`,
`dataClassification`, `enumTypeId` and `enumTypeName` for both routes, which is how the
per-field comparison in this file was made.

## Related

- **#3548** — the capture this consumes; **#3552 / #3584** — the compiled-app consumer.
- **#3568** — the 71 members where the SymbolReference derivation still disagrees with BC's
  emitter, measured by `MetadataEquivalenceHarnessTests`. `MetaField.Editable` and
  `MetaField.DataClassification` are **not** among them any more: #3545 (PR #3598) fixed both
  and deleted their allowlist entries, which is why this file does not claim them.
- **#3590** — `BuildNCLMetaTable` swallows a construction failure into a cached null.
- **#3600** — the tableextension guard on the document route.
- **#3533 / #3545** — the symbol-reference path, which stays correct and necessary for
  symbol-only packages and is untouched here.
