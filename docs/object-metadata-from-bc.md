# Building a table's metadata from BC's own document

[`docs/object-metadata-capture.md`](object-metadata-capture.md) covers the capture: BC's
emitter hands the runner a metadata document for every object it emits, and
`AlObjectMetadataRegistry` keeps them all. This file covers the first **consumer** — a table
the runner compiled gets its `NCLMetaTable` built by BC from that document, instead of by the
runner's AL-source derivation (issue #3552; issue #3562 tracks the remaining kinds).

<a id="the-seam"></a>

## The seam, and why it is not `CreateMetaTableFromXml`

`MetaTable.CreateMetaTableFromXml` is the obvious entry point and it is the wrong level.
`Types.Metadata.MetaTable` and `Runtime.NCLMetaTable` are different types in different
assemblies; there is no conversion between them, and `NCLMetaTable`'s only constructor is
protected and takes a **loader**, not a `MetaTable`. BC builds the runtime object from bytes
itself, through this chain (decompiled from `Microsoft.Dynamics.Nav.Ncl.dll`, 28.1):

| step | what it adds |
|---|---|
| `NCLMetaApplicationObject.Populate()` | calls `LoadMetadata()`, then sets `metadataLoaded` |
| `NCLMetaTable.LoadMetadata()` | `AssignFromMetaTable`, `metadataAppGroupMetaTable`, `InherentPermissionsAndEntitlements`, `RuntimeInfo` |
| `NCLMetaTable.LoadTableMetadata()` | `GetStaticTableMetadata` for BC's built-in system tables, otherwise `ObjectLoader.MetaObjectCache.GetMetaTable(objectId, appGroup)` |
| `MetaObjectCache.GetMetaTable` | `INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata`, then `MetaTable.CreateMetaTableFromXml(document, appGroup.GroupId)` |

The runner already implements that interface — `RunnerXmlMetadataLoader` — for reports, pages
and xmlports. Answering `ObjectType.Table` out of `AlObjectMetadataRegistry` is the whole of
what makes the chain reachable for a compiled table; BC does the construction.

<a id="populate-is-a-no-op-here"></a>

## `Populate()` is a Cecil no-op in this runner, so the entry point is `LoadMetadata()`

`NclCecilRewrite.Runtime.cs` replaces `NCLMetaApplicationObject.Populate()` with a void `ret`,
because it NREs on the hand-built skeleton metas the derivation produces. Calling it therefore
builds an **empty** table in silence. Measured while implementing this: `TableName` `""`,
`Fields` null, and the first record access NREs in `NCLMetaTable.get_PrimaryKey` — a failure
several layers away from its cause.

`LoadMetadata()` is what `Populate()` would have called and is not rewritten;
`NCLMetaApplicationObject.LoadMetadata`, the base call it starts with, has an empty body, so
entering one level down skips nothing. The runner sets `metadataLoaded` itself, which is the
other thing `Populate()` did.

<a id="reload-in-place"></a>

## The cached instance is reloaded, not replaced

`BuildNCLMetaTable` first runs during `AddSourceDir`, and `Emit` — which registers the
documents — runs after it. So a compiled table's first build sees an empty registry and takes
the derivation even though a document is about to exist.
`RecordPatches.RebuildTablesFromBcMetadataAll`, called from `BcRuntime.SetTestAssembly`
alongside the two wiring passes that exist for the same ordering reason, closes that gap.

It reloads **the cached instance**: supply the loader (the derivation's instance has none —
`CreateFromMetaTable` passes null), then call `LoadMetadata()` on it.

### Once per table, per bundle

`SetTestAssembly` runs **once per emitted assembly**, not once per run, so a sweep that reloads
every eligible table on every call reloads each one as many times as the bundle has assemblies.
That is not idempotent from BC's side: `AssignFromMetaTable` rebuilds the `NCLMetaField` array,
and a data provider already open over the table then raises
`NavObjectDefinitionChangedException` — *"the definition of the Descr field has changed; old
type: Integer, new type: Text"*, naming a field of a different table entirely.

Measured on `tests/runner-extras` before the ledger: table 60710 reloaded **eight** times, one
suite lost to `EXEC-FAIL`, and a query test in an unrelated suite failing as collateral. It
reproduced only on a warm shared cache root, and not cold, not on an isolated `--cache`, not
across builds, and not for that suite run alone — so a fresh-cache run cannot see it and
neither could CI.

`_bcDocumentBackedTables` records the ids already carrying the document and the sweep skips
them. Pinned by `EachTable_TakesBcsDocumentOnce_AcrossASiblingAppBundle`, over two sibling apps
under one path — the smallest bundle that emits two assemblies, and the shape
`tests/runner-extras` has. With the ledger removed that fixture loads its two tables 4 and 3
times. It is cleared in `RecordPatches.ResetForReload` alongside `_metaTableCache`, because it
names live instances that a `--watch` / `--server` cycle replaces.

Evicting and rebuilding was tried first and produced three `tests/runner-extras` regressions
against a clean `origin/main` baseline — a `[ConfirmHandler]` that stopped firing, a
source-field `OnLookup` trigger that stopped being found, and a cross-app `OnAfterValidate`
mutation that stopped propagating. Each was a consumer still holding the instance the table had
been cached as. Preserving the identity and reassigning only the metadata is what BC's own
`AssignFromMetaTable` does anyway.

<a id="what-it-changes"></a>

## What it changes, measured

Both routes trace in the same run, so this is one measurement rather than two. Fixture
`AlRunner.Tests/Fixtures/TableMetadataFromBcDocument`, BC 28.1:

| field | declared | derived | from BC's document |
|---|---|---|---|
| 1 `Entry No.` | nothing | `editable=True dataClassification=CustomerContent enumTypeId=0` | same |
| 2 `Description` | `Editable = false`, `EndUserIdentifiableInformation` | `editable=True dataClassification=CustomerContent` | `editable=False dataClassification=EndUserIdentifiableInformation` |
| 3 `Kind` | `Enum "TMD Kind"`, `SystemMetadata` | `dataClassification=CustomerContent enumTypeId=0 enumTypeName=` | `dataClassification=SystemMetadata enumTypeId=70680 enumTypeName=TMD Kind` |

AL-observable on the same shape: `FieldRef.Relation()` for `SystemCreatedBy` answered `0` and
now answers `2000000120`. That claim is adjudicated by a real service tier in corpus PR #292,
not here.

**Wherever a compile happened, the document wins — including a dependency's.**
`AlObjectMetadataRegistry` is keyed `(kind, id)` with no notion of which app compiled the
object, so a source-compiled dependency's table takes this route exactly like the app under
test's. Measured on a dependency shipping source and no DLL: table 70670 reported
`source=bc-document` with `editable=False`, `dataClassification=EndUserIdentifiableInformation`
and `enumTypeId=70670` — cold, and again across the dependency's own `source-cache HIT`, which
is the separate replay path (`DependencyLoader`'s `.object-metadata.json` sidecar) that could
have failed on its own. On the pinned corpus, three of the 175 tables taking the route are
outside the corpus app's own id range for the same reason.

What is still out of reach is a dependency shipped as a **precompiled `.app`**: it never
compiles here, so no document exists for it at all. That is #3549.

Across the pinned corpus (`19560bd3`), 172 of the corpus app's own 178 tables take the document
route and the whole suite stays green. The six that did not, at that pin, are the scope
boundary below — narrowed by #3600, which is what most of them now clear.

<a id="scope"></a>

## What still takes the derivation, and why

- **A base table a `tableextension` extends with a `modify(...)` block, or from a DIFFERENT
  app than the base table's own.** BC's document for a table is what **one** app's compiler
  emitted. A `modify(...)` block changes an existing field's properties only in the
  extension's own delta document (`<FieldChange>`), never in the base table's — the derivation
  applies neither today, so nothing regresses, but nothing is gained either (a separate,
  still-open gap: #3614 [modify(...) property changes are silently dropped by BOTH routes]). A
  cross-app extension's `<FieldAdd>` delta likewise never reaches a base document emitted by a
  DIFFERENT (and possibly earlier, possibly precompiled) compile — the service tier only
  merges it at publish time through `NavAppGroup`'s extension registry, which the runner does
  not populate. **A same-app, add-only extension has neither problem**: BC's compiler folds
  its fields AND keys straight into the base table's own document, so #3600 relaxed the guard
  for exactly that case, measured on the `ObjectMetadataCapture` fixture and confirmed on the
  pinned corpus itself — of its six `tableextension` declarations touching the corpus's own
  tables, four (60000 "ALT Universal", 60006 "ALT Keyed", 60809 "TXC Parent", 60819 "TXC Line")
  now take the document; two do not, one for each of the reasons above: 60002 "ALT Triggered"
  is modified by 60024's `modify("Watched Field")`, and 61001 "ALT Internal Table" (declared in
  the `al-language-internals-fixture` app) is extended cross-app by 60205 "ALT Internal Table
  Ext" (declared in the main corpus app) — a real cross-app case already living in the corpus,
  not a synthetic one.

  **The gate is per-extension source info (`RecordPatches._extensionSourceInfo`: the
  declaring app id plus whether it declares `modify(...)`), never the merged field count.**
  Fields and keys reach `MergeExtensionFields` through separate channels (#3216), and a
  key-only — or `modify(...)`-only — extension contributes no fields, so a count-based guard
  passes on it. Measured on a cross-app key-only extension over a source-compiled dependency's
  table: `RecordRef.KeyCount()` answered **3 where the derivation answers 4**, exit 0, no
  diagnostic, the extension's key simply gone. Nothing existing could have caught it — at
  corpus pin `19560bd3` all eight `tableextension` declarations add fields, so neither the
  corpus nor a same-app fixture reaches the branch. Pinned by
  `BaseTableWithAKeyOnlyExtensionInAnotherApp_KeepsTheDerivation`, and #3600 kept that test
  green while relaxing the same-app add-only case — see
  `SameAppAddOnlyExtension_TakesBcsDocument_WithItsFieldAndKeyIntact` and
  `SameAppExtensionDeclaringModify_StillTakesTheDerivation_AndKeepsItsAddedField` in
  `AlRunner.Tests/TableMetadataFromBcDocumentTests.cs`. An app boundary that cannot be resolved
  (a precompiled `.app`'s extension, or an AL-source extension/table whose `app.json` cannot
  be found) reads as non-matching, i.e. stays on the derivation, rather than being guessed safe.
- **Every table with no captured document** — a precompiled dependency's (#3549), a virtual
  system table, or a bundle served from an AL-output cache written before #3548.

Availability decides the route, and a failure is never re-routed to the derivation: a weaker
answer substituted on error is what `.claude/rules/loud-failures.md` exists to prevent. **How
far that failure travels differs by call site, and only one of the two is loud.** The post-emit
sweep has no handler over it and aborts bundle load naming the member. The cold build sits
inside `BuildNCLMetaTable`'s pre-existing `catch → Console.Error → return null`, which swallows
it into "no metatable" exactly as it does a derivation failure — and that write is
`[RecordPatches]`-tagged, so the default log filter drops it. That swallow predates this work
and is tracked as **#3590**; it is not a claim this page makes about the cold path.

<a id="reading-the-values-back"></a>

## Seeing which route ran, and what it produced

```
AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=1   # one line per built table: id, route
AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=2   # ...and one line per field
```

Level 2 prints `Editable`, `DataClassification`, `EnumTypeId` and `EnumTypeName`. All four live
on `Types.Metadata.MetaField`, reachable only through the original `MetaTable` the
`NCLMetaTable` was built from: `NCLMetaField` carries `DataClassification` but neither
`Editable` nor the enum type, and no AL surface exposes any of them for a table field. So this
trace is where those values are observable at all, which is why
`AlRunner.Tests/TableMetadataFromBcDocumentTests.cs` asserts through it — the two routes
produce the same *type*, so nothing downstream can be asked which one ran.
