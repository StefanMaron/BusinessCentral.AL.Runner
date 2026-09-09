# Table trigger metadata — `NCLMetaTable`'s five `Is<Trigger>Defined` flags

Issue #3556. The table twin of [page trigger metadata](page-rowset-triggers.md#page-trigger-metadata) (#3447), and it decides more: these flags do not merely answer a metadata reader, they decide whether BC dispatches a table's write triggers at all.

## What BC does

`IsInsertTriggerDefined`, `IsModifyTriggerDefined`, `IsOnAfterModifyTriggerDefined`, `IsDeleteTriggerDefined` and `IsRenameTriggerDefined` are each `(DefinedTriggers & TableTriggers.X) != 0`, and `NCLMetaTable.DefinedTriggers` is **reflection**, not a read of the table metadata document (`Microsoft.Dynamics.Nav.Ncl.dll`, identical body on 27.0, 27.5 and 28.4):

```csharp
tableTriggers |= IsTriggerImplemented<NavRecord>("OnInsert", isPublic: false) ? 1 : 0;   // …Modify, Delete, Rename
foreach (NCLTableExtension ext in orderedExtensionObjects)
{
    if ((tableTriggers & Insert) == 0 && (ext.IsTriggerImplemented<NavRecordExtension>("OnBeforeInsert", true)
        || ext.IsTriggerImplemented<NavRecordExtension>("OnInsert", true)
        || ext.IsTriggerImplemented<NavRecordExtension>("OnAfterInsert", true)))
        tableTriggers |= Insert;
    // …Modify, the separate OnAfterModify bit, Delete, Rename — same shape
}
```

Two properties of that body are what the runner has to reproduce and are easy to get wrong:

- **`OnAfterModify` is a bit of its own**, set only by an extension declaring `OnAfterModify`, and the base-table arm never sets it. It gates `NavRecord.PerformJITLoadIfNecessaryAsync`, not a trigger.
- **Any one of the three names sets the bit.** An extension declaring only `OnAfterInsert` sets `Insert`, which is what makes the flag "does this write need the trigger pipeline", not "does someone declare this exact trigger".

## Why a false flag skips a trigger, and why only half of them

`NavRecord.ALInsertAsync` (and its Modify / Delete / Rename siblings) puts the extension `OnBefore<X>` loop, the base table's own `On<X>` trigger and the extension `On<X>` loop **inside** the flag's guard, and leaves the `OnAfter<X>` loop **outside** it:

```csharp
if (runApplicationTrigger && metaTable.IsInsertTriggerDefined)
{
    if (orderedTableExtensions.Count > 0) await …ForEachAsync(ext => ext.OnBeforeInsert(), …);
    if (__IsAsync) await OnInsertAsync(); else OnInsert();
    if (orderedTableExtensions.Count > 0) await …ForEachAsync(ext => ext.OnInsert(), …);
}
await NavGlobalTriggers.InsertAsync(this, runGlobalTrigger);
bool result = await recordImplementation.InsertRecordAsync(errorLevel);
if (result)
{
    if (runApplicationTrigger)
        if (orderedTableExtensions.Count > 0) await …ForEachAsync(ext => ext.OnAfterInsert(), …);
    …
}
```

That asymmetry is the whole observable shape of the defect. With the flag false, a tableextension's `OnAfterInsert` ran and its `OnBeforeInsert` did not — measured on the runner before the fix, on a table declaring no trigger of its own: `OnBeforeInsert,OnAfterInsert` expected, `OnAfterInsert` observed, and the same for Modify, Delete and Rename. On a base table that *does* declare `OnInsert`, the flag was already true and the extension's `OnBeforeInsert` ran, which is what pins the flag as the discriminator rather than extension dispatch being broken.

## What the runner does

`NCLMetaTable.get_DefinedTriggers` is Cecil-replaced (`AlRunner/Patches/RecordPatches.TableTriggerMetadata.cs`, installed from `AlRunner/Infrastructure/NclCecilRewrite.Records.cs`). `NCLMetaTable` lives in `Ncl.dll`, the runtime engine, so `precompiled-dll-respect.md` allows it; no AL-business-logic body is touched.

The replacement keeps BC's algorithm — the same five bits read **by name** off BC's own enum, the same trigger-name triples in the same order, the same `…Async` fallback and `DeclaringType` comparison, transcribed through the `CheckTrigger` helper shared with the page twin. Three things differ, all because the runner hand-builds this metatable rather than loading it:

| | why |
|---|---|
| the extension list is the **union** of BC's own `orderedExtensionObjects` and the runner's tableextension registry | the runner fills the registry and never that list; the union keeps a BC-loaded metatable answering exactly as BC would |
| the extension list is the **same** `_extensionIdsByBaseTable` / `FindTableExtensionType` pair `RegisterParsedTableExtensions` instantiates from | the flag and the extension instances a write dispatches to cannot then disagree about which extensions exist |
| the answer is cached only once **every** extension's compiled class resolves | BC caches unconditionally on first read; the runner resolves those types lazily from the loaded assemblies, so an unconditional cache would freeze an answer taken before the test assembly loaded |

A tableextension whose compiled class never resolves produces one `[warn]` line per (table, extension) rather than a quietly smaller mask. `NCLMetaApplicationObject.ApplicationObjectClrType` gained a `TableExtension` arm at the same time: BC reads that property on an `NCLTableExtension` too, and without an arm such a receiver fell to the table default and resolved `Record{extId}`.

## Evidence

- **Real BC**: corpus codeunit 60433, `tests/al-language/tableextensiontrigger/` (corpus PR [#307](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests/pull/307)) — six arms, 6/6 on BC 28.4.53241.0 in a container before the PR was opened.
- **Runner mechanism**: `AlRunner.Tests/TableTriggerMetadataTests.cs` over fixture `TableTriggerMetadata`, asserting the exact mask per table from `AL_RUNNER_TABLE_TRIGGER_AUDIT=1`.

`AL_RUNNER_TABLE_TRIGGER_AUDIT=1` prints one line per table at process exit — an inspection channel like `AL_RUNNER_HOOK_AUDIT`, not a diagnosis:

```
[table-trigger-audit] table=70740 clr=Record70740 ext=TableExtension70742 triggers=Insert,Modify,Delete,OnAfterModify
[table-trigger-audit] table=70741 clr=Record70741 ext=- triggers=Rename
```
