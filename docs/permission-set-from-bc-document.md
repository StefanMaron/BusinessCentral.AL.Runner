# Permission sets from BC's own metadata document

Issue #3609, rank 8 and last of the conversion chain #3562 tracks. Companion to
[`docs/object-metadata-from-bc.md`](object-metadata-from-bc.md) (tables, #3552) and
[`docs/page-metadata-properties.md`](page-metadata-properties.md) (pages, #3601).

The runner derived a source-compiled permission set's declaration by running a regular
expression over AL source text (`RecordPatches.AlPermissionSetParser.cs`). BC's own
`Compilation.Emit` had already produced a `<PermissionSet>` document stating the same
things — and stating two of them *resolved*, which the derivation could not do at all.

<a id="what-bc-states"></a>

## What BC actually emits

Measured on BC 28.1.49838.53910 with `AL_RUNNER_TRACE_OBJECT_METADATA=2`, over a probe
bundle declaring two permission sets. Reproduce it by pointing the runner at any bundle
with a `permissionset` object and reading the `[object-metadata]` lines.

```xml
<PermissionSet MetadataVersion="130000" ID="70702" Name="PP Derived" ALNamespace=""
               Access="Internal" Assignable="1" CaptionML="ENU=PP derived caption"
               IncludedPermissionSets="70701" ExcludedPermissionSets="70704"
               CaptionTranslationKey="PermissionSet 1722699319 - Property 2879900210"
               xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
  <Permissions>
    <Permission Type="0" ID="70700" Value="449" />
    <Permission Type="5" ID="70703" Value="16" />
  </Permissions>
</PermissionSet>
```

Three properties of that document are what make the conversion worth doing:

| | AL source states | BC's document states |
|---|---|---|
| a permission's object | a NAME — `tabledata "PP Thing"` | `Type="0" ID="70700"`, resolved |
| a permission's mask | letters — `= RIMD`, `= Rimd` | `Value="15"`, `Value="449"`, computed |
| include/exclude edges | quoted NAMES | `IncludedPermissionSets="70701"`, resolved ids |

<a id="mask-encoding"></a>

## The mask encoding agrees with the derivation's

`MaskFromAlLetters` reimplemented BC's `PermissionMask` by hand: R=1, I=2, M=4, D=8, X=16,
with the lowercase *indirect* variants at 32/64/128/256/512. Measured against BC's own
output, the two agree exactly:

| AL | BC's `Value` | arithmetic |
|---|---|---|
| `RIMD` | 15 | 1 + 2 + 4 + 8 |
| `Rimd` | 449 | 1 + 64 + 128 + 256 |
| `X` | 16 | 16 |

So this route is not a *different* answer — it is the same answer without the
transcription, which is the reason it can be preferred unconditionally when available.

<a id="tabledata"></a>

## The defect this removes: a silently dropped `tabledata` grant

`ResolveSourcePermissionEntries` resolved each permission's object name against
`ParsedObjectDecls`. That collection does not carry **tables** — they are parsed by the
table pipeline, not the declaration sweep — so `AlKeywordForPermissionObject` returns
`null` for PermissionObject ordinals 0 (`tabledata`) and 1 (`table`):

```csharp
var kind = AlKeywordForPermissionObject(e.ObjectTypeOrdinal);
if (kind == null) continue;               // <- the drop
if (!byName.TryGetValue((kind, e.ObjectName), out var id))
{
    if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA") == "1")
        Console.Error.WriteLine(...);     // <- the diagnostic, never reached for a table
    continue;
}
```

The `continue` sits **above** the diagnostic, so the loss was invisible at every verbosity,
including `AL_RUNNER_DIAG_PERMMETA=1`. A permission set whose grants are all on tables —
the overwhelmingly common shape — reached BC's permission metadata layer with an empty
`Permissions` list, and every test over it stayed green.

The known limit was documented in the method's own doc comment and rested on "every
tabledata grant that matters in practice comes from a precompiled dependency". That is true
of Base Application and not of a bundle under test: Microsoft's Tests-SINGLESERVER bucket
declares `permissionset 134611 TestSet` in source, which is the case #2357 existed for.

Measured end to end on the probe bundle, `AL_RUNNER_DIAG_PERMMETA=1`, Base Application
closure loaded, before and after the conversion:

```
declared permissions: 17419      (before)
declared permissions: 17421      (after)
```

The delta is exactly the probe's two source-declared `tabledata` grants. The other 17,419
come from precompiled dependencies, which state ids in `SymbolReference.json` and never
took the broken path.

<a id="excluded-permission-sets"></a>

## `ExcludedPermissionSets`: the count is right, the inference from it was not

Issue #3609 records `ExcludedPermissionSets` as hardcoded null and states that BC does not
state it either — 0 occurrences across 258 Base Application permission sets — concluding
that conversion cannot fix it.

Re-measured independently over
`Microsoft_Base Application_28.1.49838.53910.app`, counting `permissionset` declarations in
its `src/` tree:

| | count |
|---|---|
| permission set source files | 258 |
| declaring `ExcludedPermissionSets` | **0** |
| declaring `IncludedPermissionSets` | 46 |

The zero is real. What it measures is **Base Application's own AL**, not BC's emitter. A
probe that declares the property gets it back on the document:

```xml
<PermissionSet ID="70702" ... ExcludedPermissionSets="70704" IncludedPermissionSets="70701" ...>
```

(Declaring the *same* set in both lists is rejected by the compiler with `AL0740`, so the
probe uses two distinct sets.)

So conversion **does** carry `ExcludedPermissionSets` for anything the runner compiles from
source, and the populator's hardcoded null is retired on that route. It stays null on the
precompiled route, where the runner reads `SymbolReference.json`, which does not carry
exclude edges at all.

<a id="what-survives"></a>

## What survives conversion — nothing becomes dead code

The issue expected `AlPermissionSetParser.cs` (176 lines) and part of
`PermissionMetadataPopulator.cs` (884) to retire. Measured, neither can:

| symbol | why it stays |
|---|---|
| `TryParsePermissionSetFile`, `_parsedPermissionSets` | the registry is keyed **by id**, so something must say which ids this run declares before a document can be fetched |
| `ParsedAlPermissionSet.AppId` / `.AppName` | BC's document does **not** state the owning app; it comes from the owning `app.json` via `ResolveOwningApp`, and both the "Metadata Permission Set" App ID column and `BuildKnownAppNameIndex` read it |
| `ParsePermissionEntries`, `MaskFromAlLetters`, `ParseIncludedPermissionSets` | `Emit` runs only on a compile-cache **MISS**, so the derivation is the live fallback on any warm run whose replay supplied no document |
| `ResolveSourcePermissionEntries`, `AlKeywordForPermissionObject` | same fallback path |

What conversion removes is the derivation's **role as the answer**, not its code. Whenever
BC's document is available — which is every cold run — the name→id resolution, the
hand-rolled mask table and the include-name lookup stop deciding what a source-compiled
permission set means.

That is the honest scope of the change, and it is smaller than the issue anticipated in
line count while being larger in effect, because the path it replaces was losing data.

<a id="scope"></a>

## What this does not do

Permission **enforcement** is out of scope for the runner
([`docs/limitations.md`](limitations.md)) — these tables answer lookups only. Nothing here
changes what AL code is permitted to do; it changes what the metadata tables report about
declared permission sets.

`PermissionSetExtension` is a separate `SymbolKind` whose documents use a different root
element (`MetadataRuntimeDeltas`, not `PermissionSet`) and is not read by this code.
