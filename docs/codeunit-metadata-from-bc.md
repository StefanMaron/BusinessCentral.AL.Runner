# CodeUnit Metadata columns from BC's own document

`CodeUnit Metadata` (2000000137) has 11 columns. Five are built from the runner's own object
inventory; four are read from the metadata document BC's emitter produces for each codeunit
(#3606); two are left at BC's default, for reasons that are not "not implemented yet".

The code is `AlRunner/Patches/RecordPatches.CodeunitMetadataFromBcDocument.cs`, wired into the
row builder in `RecordPatches.CodeunitMetadataVirtualTable.cs`. This document holds the
measurements behind it, and the two facts that are easy to get backwards.

<a id="the-eleven-columns"></a>

## The eleven columns and where each comes from

BC's own row builder is `Microsoft.Dynamics.Nav.Runtime.CodeUnitDataProvider.GetValuesWithinRangeForKeyField`
(Ncl.dll), which fills an eleven-slot buffer in this order:

| # | column | AL name | BC fills it from | runner |
|---|---|---|---|---|
| 1 | ID | `ID` | the object number | inventory |
| 2 | Name | `Name` | the object name | inventory |
| 3 | TableNo | `TableNo` | `NCLMetaCodeunit.TableId` | inventory (#3546 is open against it) |
| 4 | SingleInstance | `SingleInstance` | `IsSingleInstance \|\| id == 1` | inventory |
| 5 | SubType | `SubType` | `GetOptionValue(5, (int)Subtype)` | inventory + option string (#3080) |
| 6 | App ID | `App ID` | `MetadataDataProvider.GetAppId(metaCodeunit)` | **BC's default** |
| 7 | InherentPermissions | `InherentPermissions` | `CreatePermissionMaskString(…Permissions)` | **BC's document** |
| 8 | InherentEntitlements | `InherentEntitlements` | `CreatePermissionMaskString(…Entitlements)` | **BC's document** |
| 9 | TestType | `TestType` | `GetOptionValue(9, (int)TestType)` | **BC's default** |
| 10 | RequiredTestIsolation | `RequiredTestIsolation` | `GetOptionValue(10, (int)RequiredTestIsolation)` | **BC's document** |
| 11 | AL Namespace | `AL Namespace` | `GetNormalizedNamespace(11, …ALNamespace)` | **BC's document** |

`GetNormalizedNamespace` is `NavText.Create(fieldValue)` — a passthrough despite the name.

<a id="the-attribute-name-trap"></a>

## The attribute-name trap: `TestIsolation` versus `RequiredTestIsolation`

The AL compiler emits `TestIsolation="Disabled"`. BC's own document parser,
`Microsoft.Dynamics.Nav.Types.Metadata.MetaCodeunit(XmlNode)`, switches on attribute name and
compares against the string **`RequiredTestIsolation`** — a name the compiler never writes. So
BC's XML path leaves `MetaCodeunit.RequiredTestIsolation` at its field initializer, `0` /
`None`, for every codeunit, including one that declares `TestIsolation = Codeunit`.

That is a fact about BC's XML path, and it is **not** the path a service tier's row travels.
`CodeUnitDataProvider` reads `NclMetadata.GetMetaCodeunitById(id).RequiredTestIsolation` — an
`NCLMetaCodeunit` built from the published app's compiled attribute, not from this document.
The runner therefore reads the name the **compiler** writes, so its answer tracks the
declaration, which is what the tier reports.

This is the same asymmetry `SubType` has, where the value reaching the column is what the
compiler wrote rather than what the AL author declared —
`RecordPatches.CodeunitMetadataVirtualTable.cs`'s `AlSubtypeTheCompilerDoesNotEmit` records
that one.

If BC ever renames either attribute, corpus PR 296's `RequiredTestIsolation` tests go red on
that version, and `ReadRequiredTestIsolationOrdinal` is what changes.

<a id="what-the-compiler-emits"></a>

## What the compiler actually emits, per declaration shape

Measured on BC 28.1.49838.53910 by compiling one codeunit of each shape and dumping the
captured document with `AL_RUNNER_TRACE_OBJECT_METADATA=2`:

| declaration | `TestIsolation` attribute |
|---|---|
| `Subtype = Normal`, property absent | `"Disabled"` |
| `Subtype = TestRunner`, property absent | `"Disabled"` |
| `Subtype = TestRunner`, property declared | the declared member |
| `Subtype = Test` | **absent** |

So `Disabled` is supplied for everything except a `Subtype = Test` codeunit, and it is the
**Test subtype**, not an omitted property, that reaches the column's `None` member.

That identifies #3606's "stated for 1,658 of 1,690" figure: the 32 Base Application documents
with no `TestIsolation` attribute are the test codeunits. It also falsifies the obvious
reading of that figure — that the 32 are codeunits which "declined to state" a property others
stated — which is the assumption the first draft of the corpus test was written against and
which a service tier would have rejected.

**Two AL constraints make most of this unreachable from AL**, which is why the runner-side
mechanism tests in `AlRunner.Tests/CodeunitMetadataDocumentColumnTests.cs` exist alongside the
corpus tests:

- `AL0223` — `TestIsolation` is accepted only when `Subtype = TestRunner`.
- `AL0195` — on a codeunit, `InherentPermissions` and `InherentEntitlements` accept only `X`.

<a id="permission-mask-spelling"></a>

## How a permission mask is spelled

`MetadataDataProvider.CreatePermissionMaskString` (Ncl.dll) returns `NavText.Empty` for
`PermissionMask.None`; otherwise it walks bits 0..4 and emits `permissions[n]` for a direct
bit, or `permissions[n] + 32` — the lowercase letter — for the matching indirect bit at `n+5`.
It sizes its stack buffer with `PopCount((num >> 5) | (num & 0x1F))`, so one permission never
contributes two characters.

`permissions` is a static `char[5]` initialized from a blob. Decoded from BC 28.1's
`<PrivateImplementationDetails>`:

```
52 00 49 00 4D 00 44 00 58 00   ->  'R' 'I' 'M' 'D' 'X'
```

`PermissionMask` itself (Types.dll) numbers `Read=1, Insert=2, Modify=4, Delete=8,
Execute=16`, then `IndirectRead=32` … `IndirectExecute=512`, plus flags above that
(`HasExpDate=4096`, `IgnoreWildcard=32768`) which no permission column spells.

The attribute BC emits is the mask's **numeric** value — `InherentPermissions="16"` for
`InherentPermissions = X` — and BC's own parser reads it with
`Enum.Parse(typeof(PermissionMask), value)`, which accepts a numeric string or a member name.
The runner accepts both for the same reason, and refuses anything it cannot read rather than
answering the empty string, which would be indistinguishable from a codeunit declaring no
permission.

<a id="the-two-columns-left-at-bcs-default"></a>

## The two columns left at BC's default

Neither is waiting on this conversion; neither is in the document to convert.

**`App ID`** is not an object property. BC fills it with
`MetadataDataProvider.GetAppId(metaCodeunit)` — the id of the app the object was *published*
from, which is per-run state a compiler cannot emit. Answering it needs per-object app
attribution, the same data #2326 tracks for `AllObj`'s "Object Subtype".

**`TestType`** is emitted **0 times** in Base Application's 1,690 codeunit documents, and BC
does not read it from the document either. `MetaCodeunit`'s ctor **derives** it, in a tail
block after the attribute loop:

```
if (SubType == CodeunitSubType.Test && TestType == 0)
    TestType = TestCodeunitTestType.UnitTest;      // i.e. 1
```

So a service tier reports `UnitTest` for a test codeunit that declares nothing, which is a
derivation rather than a stated value. Reproducing it is a separate claim about BC needing its
own corpus test, and is deliberately not part of #3606.

<a id="what-adjudicated-this"></a>

## What adjudicated this

Corpus PR 296 (`StefanMaron/BusinessCentral.AL.Language.Tests`) adds four tests and seven
fixtures to `record/TestCodeunitMetadataVirtualTable.al`:

- `Record_CodeunitMetadata_Get_TestRunnerCodeunits_ReportEachDeclaredTestIsolation`
- `Record_CodeunitMetadata_Get_TestSubtypeCodeunit_ReportsADifferentTestIsolationFromAPlainOne`
- `Record_CodeunitMetadata_Get_InherentPermissionsAndEntitlements_ReadIndependently`
- `Record_CodeunitMetadata_Get_ALNamespace_ReportsTheDeclaringFilesNamespace`

All four fail against the runner as it stood before #3606, with concrete wrong values: `None`
where `Disabled` is declared, `''` where `X` is declared, `''` where a namespace is declared.
