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
| Business Foundation | 96 | clean, 5.8–6.2 s | **70** (was 55 before #3875 — see “The platform floor”) |
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

Base Application needed a second shim of the same shape, for a different assembly —
`Microsoft.AspNetCore.StaticFiles`, which no BC artifact ships. See the next section; it is the
same mechanism and the same one-file fix.

### Base Application: a cost question after all (#3876)

**This section said the opposite until #3876 measured it**, and the correction matters more
than the fix: three places in this repository recorded Base Application's metadata emit as
permanently impossible, each citing the other two. The narrow claim they rested on was true and
the conclusion drawn from it was not.

**What is true:** BC ships no copy of `Microsoft.AspNetCore.StaticFiles` in its artifacts, and
without one the declaration phase raises `AL0451` + `AL0185` and the emit produces zero objects.

**What does not follow:** that the assembly is unobtainable. It is an ordinary part of the
**ASP.NET Core reference pack**, and staging that one file takes the emit from nothing to
`errors=0`, `objects=7850`, **7,842 documents** — measured four times now, twice by the agent
that filed #3876, once by the agent that measured the mechanism, and once through the shipped
staging rather than a flag:

```
[decl]  15084ms  errors=0
[emit] 227480ms  success=True objects=7850 errors=0
[bundle] 7842 document(s)
EXIT=0  wall=257s  peak VmHWM=9,263,480 kB = 8.83 GiB
```

`AlRunner.csproj` and `MetadataGroundTruth.csproj` now stage it into `dotnet-shims` beside the
Newtonsoft shim above, by `PackageReference` on `Microsoft.AspNetCore.App.Ref` 8.0.30.

#### Why the `PublicKeyToken=null` in the error message misled three documents

`AL0451` names `'Microsoft.AspNetCore.StaticFiles, PublicKeyToken=null'`, and all three records
read that as *"BC needs a null-token build of this assembly"*. It is not a requirement; it is
how BC renders **the absence of a token in the AL declaration**. From
`DotNetUtilities.GetPublicKeyToken`:

```csharp
if (publicKeyToken == null || publicKeyToken.Length == 0)
    return "null";
```

The shipped source agrees. Base Application's `src/Modules/System/DotNetAliases/dotnet.al` has
exactly three `assembly()` declarations and exactly one `PublicKeyToken` line — on `Ncl`, at
line 7. Line 23 declares `assembly(Microsoft.AspNetCore.StaticFiles)` with **no** token.

So the ref pack's ordinary strong-named copy (`PublicKeyToken=adb9793829ddae60`) binds. The
mechanism is **asymmetric matching, not a token-blind resolver** — the distinction matters,
because the token-blind reading would mean any assembly of the right name satisfies the
reference. From the AL compiler's `AssemblyLocatorBase.IsAssemblyCompatible`:

```csharp
if (!searchName.Name.Equals(assemblyBeingEvaluated.Name))
    return false;
byte[] publicKeyToken = searchName.PublicKeyToken;
if (publicKeyToken != null && publicKeyToken.Length != 0
    && !IsPublicKeyTokenCompatible(publicKeyToken, assemblyBeingEvaluated.PublicKeyToken))
    return false;
```

| the search name | the candidate's token | result |
|---|---|---|
| carries no token | anything | **not checked** |
| carries a token | different | rejected |
| wrong simple name | anything | rejected |

A wrong token is still rejected and a wrong simple name always is; the resolver is the **AL
compiler's** (`Microsoft.Dynamics.Nav.CodeAnalysis.dll`), not `Ncl.dll`'s. Measured directly
against that locator across 4 distinct compiler binaries × 4 ref-pack versions, 16/16 uniform
(#3876).

#### The reference pack is not an SDK component, so it arrives as a package

This is the part a future edit is most likely to get wrong. Unlike `Microsoft.NETCore.App.Ref`
and `NETStandard.Library.Ref`, which `BcCompiler.EnumerateDotNetRefAssemblyDirs` finds under
`$DOTNET_ROOT/packs`, **`Microsoft.AspNetCore.App.Ref` is not laid down by the .NET SDK** —
verified absent from `$DOTNET_ROOT/packs` on a box that has four versions of it cached in
`~/.nuget`. CI legs use `actions/setup-dotnet` with `8.0.x`, which would not produce it either.

So a hardcoded `~/.nuget` path passes locally and fails on CI. The `PackageReference` +
`GeneratePathProperty` + `ExcludeAssets="all"` pattern is what makes NuGet restore it on any
machine, and `AlRunner.Tests/DotNetShimProbingTests` pins that the ref-pack enumeration never
supplies this assembly, so the staged shim stays the only route.

One wrinkle worth knowing: the pack ships Roslyn analyzers, and `ExcludeAssets="all"` does not
keep them out of the `Analyzer` item group. They then throw **882 `AD0001` warnings** per build
looking for ASP.NET Core types nothing here references. Both projects drop them in a
`DropAspNetCoreRefPackAnalyzers` target. Measure that on a **clean** rebuild — an incremental
one skips `CoreCompile` and reports zero whatever the target does.

#### What this does not change: the app is still not in `apps.json`

The compile is now possible; whether to spend it on every unit-test leg is a separate,
deliberate decision, and #3876 does not take it. `tools/gen-metadata-ground-truth.sh` reads
`tests/expectations/metadata-equivalence/apps.json` and runs on the unit-test legs
(`bc-tests.yml`), where Business Foundation costs ~3 s and System Application ~14 s. Base
Application costs **257 s and 8.83 GiB peak RSS**. A standard `ubuntu-latest` runner has 16 GB,
so it fits — but alongside everything else on that leg the margin is thin, and an OOM there
presents as a killed step with no diagnostic rather than a legible failure. That is a sizing
call for a human, so `apps.json` still omits the app — now saying *why it is omitted*, rather
than that it is impossible.

## The platform floor

Both apps here compile against `System.app` — BC's platform symbols — and neither says so in a
way the resolver used to read. That made the reference silently absent, and #3875 is the fix.

**What the manifests actually declare.** Parsed from every platform package's
`NavxManifest.xml`, across six artifact builds from 27.3 to 28.4 (identical in all six):

| package | `Platform=` | `Application=` | `<Dependency>` entries |
|---|---|---|---|
| System (`System.app`) | — | — | **0** |
| System Application | `<major>.0.0.0` | — | **0** |
| Business Foundation | `<major>.0.0.0` | — | 1 (System Application) |
| Base Application | `<major>.0.0.0` | — | 2 |
| Application | `<major>.0.0.0` | — | 3 |

So **System Application names no dependency at all**, and its only statement of what it needs is
the `Platform` attribute. `DependencyResolver.Visit` turns that attribute into a synthetic
`Microsoft/System` root (`AppLoader.ImplicitRoots`) — but until #3875 it skipped that step for
the Microsoft platform apps themselves, on the premise that their floors cycle
(`Application → Base Application → Application …`).

**That premise does not hold for the floor that matters.** No platform app declares
`Application=` at all; `System.app` declares neither a floor nor a dependency, so following a
`Platform` floor terminates in one step. The cycle the premise describes lives in the
`<Dependencies>` array, which `Visit`'s own colour-marker detector already handles. The
exemption therefore prevented no cycle and cost both apps their platform symbols:

| app | before #3875 | after | BC's own emitter |
|---|---|---|---|
| System Application | `specsLen=0` → 2,587 declaration diagnostics → `METADATA-EMIT-ZERO`, run aborts | **1,218** documents | 1,218 |
| Business Foundation | `specsLen=1` → **55** documents, **reported as success** | **70** documents | 70 |

Business Foundation is the one worth remembering. It survived because 55 of its 70 objects need
nothing from the platform; the other 15 — the ones referencing `Field`, `Table Metadata` and the
other platform tables — silently fell back to the SymbolReference hand-derivation, under a green
run with a cache entry written. At default verbosity that run printed **zero** `AL0185` lines.

So #3875 follows a platform app's `Platform` floor and still refuses its `Application` floor,
which is the half that could cycle if a future build ever declared one.
`ProvisioningCheck.ScanDependencyEdges` carries the matching guard and is narrowed identically —
an edge resolution walks but provisioning does not would under-fetch `System.app`, which is how
#3719's unattributable `EMIT-ZERO` arrives.

**Measuring this needs a bundle that declares no floor of its own.** A bundle whose `app.json`
carries `application` or `platform` already injects `Microsoft/System` into the closure, so the
shortfall does not reproduce and Business Foundation yields 70 either way. The 55 appears only
when the app's own floor is the sole route to the platform.

**What is still silent.** #3875 removed one *cause* of a partial compile; it did not make a
partial compile loud. `DependencyMetadataProducer` sees how many documents appeared and never
how many BC should have produced, so it has no local comparison to make — tracked by #2247.

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
