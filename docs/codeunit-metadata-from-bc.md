# CodeUnit Metadata columns from BC's own document

`CodeUnit Metadata` (2000000137) has 11 columns. Five are built from the runner's own object
inventory; three are read from the metadata document BC's emitter produces for each codeunit
(#3606); three are left at BC's default, for reasons that are not "not implemented yet".

One of those three — `RequiredTestIsolation` — **is** stated in the document, and reading it
is wrong. A real service tier answers `None` for every codeunit. That is the section worth
reading first, because the obvious implementation is the incorrect one.

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
| 10 | RequiredTestIsolation | `RequiredTestIsolation` | `GetOptionValue(10, (int)RequiredTestIsolation)` | **BC's default** (see below) |
| 11 | AL Namespace | `AL Namespace` | `GetNormalizedNamespace(11, …ALNamespace)` | **BC's document** |

`GetNormalizedNamespace` is `NavText.Create(fieldValue)` — a passthrough despite the name.

<a id="requiredtestisolation"></a>

## `RequiredTestIsolation`: BC answers `None` for every codeunit, so the runner defaults it

This column is **stated in the document** and must not be read from it. That is the one
counter-intuitive fact on this page, and it cost a full implementation to find out.

### What a service tier answers

Corpus PR 296 asked eight cloud legs (27.0, 27.3, 27.5, 28.0, 28.1, 28.2, 28.3, 28.4) what
this column reports for three shapes. Identical answer on all eight:

| codeunit | declares | tier answers |
|---|---|---|
| `ALT Iso Runner Disabled` | `Subtype = TestRunner`, `TestIsolation = Disabled` | `None` |
| `ALT Codeunit Meta Probe` | ordinary codeunit, no `Subtype` | `None` |
| the test codeunit itself | `Subtype = Test` | `None` |

The decisive one is the first: a codeunit that **explicitly declares** `TestIsolation =
Disabled` still reports `None`. So the column does not track the declaration, and a
document-read implementation — which by construction answers `Disabled` there — is wrong.

The failing assertion, from the BC 28.1 leg:

```
FAIL Record_CodeunitMetadata_Get_TestRunnerCodeunits_ReportEachDeclaredTestIsolation
     Expected:<1> (Integer). Actual:<None> (Integer).
     A TestRunner declaring TestIsolation = Disabled must report RequiredTestIsolation::Disabled.
```

### Why, in Ncl.dll

It **is** the XML parser, and the name mismatch is the whole mechanism.

`CodeUnitDataProvider.GetValuesWithinRangeForKeyField` fills slot 9 from the `NCLMetaCodeunit`:

```csharp
buffer[9] = codeUnitDataProvider.GetOptionValue(10, (int)metaCodeunitById.RequiredTestIsolation);
```

`NCLMetaCodeunit.RequiredTestIsolation` **is** assigned — by `NCLMetaCodeunit.LoadMetadata()`,
which copies it straight off the parsed document object:

```csharp
protected override void LoadMetadata()
{
    base.LoadMetadata();
    MetaCodeunit metaCodeunit = base.ObjectLoader.XmlMetadataLoader.GetMetaCodeunit(...);
    ...
    RequiredTestIsolation = metaCodeunit.RequiredTestIsolation;
}
```

So the value comes from `Types.dll`'s `MetaCodeunit(XmlNode)`, which defaults the field and then
matches attributes by name and length:

```csharp
case 21:                                  // name.Length
    if (name == "RequiredTestIsolation")
        RequiredTestIsolation = (TestCodeunitRequiredTestIsolation)Enum.Parse(...);
```

**The AL compiler emits the attribute as `TestIsolation`** — 13 characters — so the length-21
branch never fires and the field keeps its constructor default `0` = `None`. Measured across the
cached emitted documents: **2,167 occurrences of `TestIsolation="…"` and 0 of
`RequiredTestIsolation`.** That matches the corpus failure exactly: `Expected:<1>` (Disabled)
`Actual:<None>`.

**BC's default is therefore the faithful answer**, and `NavValue.GetDefaultNavValue` already
gives it. This is not a known gap: there is nothing to implement.

**What to re-check if this ever changes**: whether BC's parser label and the compiler's emitted
attribute name still disagree. That is the governing fact — not whether anything assigns the
property, which is a stably-false-looking irrelevance.

### What was tried, and why it is recorded here

The first implementation of #3606 converted this column from the document's `TestIsolation`
attribute, and a service tier refuted it on all eight cloud legs. The conversion, its
runner-side tests and the two corpus assertions were removed rather than adjusted.

An earlier revision of this page then blamed the wrong mechanism. It claimed the property was
"never assigned" and that its backing field had "zero writes anywhere in `Ncl.dll`", citing an
empty `find_usages`. **Both statements are false.** `find_usages` on a
`<Property>k__BackingField` does not see writes routed through the compiler-generated setter, so
the empty result was a tool artifact rather than a finding — a false negative of the same family
`CLAUDE.md` documents for `grep -E` and `rg`. A Mono.Cecil scan over every method body found the
write immediately, on every BC version this repository tests — the method is byte-identical across them.

Two lessons, cheap to state and expensive to relearn:

- **An empty result from one tool is not evidence.** Confirm it with a differently-shaped query
  before building an argument on it — the rule this repository already applies to search.
- **Establishing which path is *unused* says nothing about what the used path answers.** The
  earlier reasoning correctly found the parser's name mismatch, then discarded it on the strength
  of the false zero. It was the answer all along.

<a id="what-the-compiler-emits"></a>

## What the compiler actually emits, per declaration shape

This is a measurement of the **document**, which is a different thing from what the column
answers — see [`#requiredtestisolation`](#requiredtestisolation), where a tier answers `None`
whatever this table says. It is kept because it explains the figure #3606 was filed on, and
because the next person to notice `TestIsolation` sitting in the document will otherwise
re-derive it.

Measured on BC 28.1.49838.53910 by compiling one codeunit of each shape and dumping the
captured document with `AL_RUNNER_TRACE_OBJECT_METADATA=2`:

| declaration | `TestIsolation` attribute |
|---|---|
| `Subtype = Normal`, property absent | `"Disabled"` |
| `Subtype = TestRunner`, property absent | `"Disabled"` |
| `Subtype = TestRunner`, property declared | the declared member |
| `Subtype = Test` | **absent** |

So the compiler supplies `Disabled` for everything except a `Subtype = Test` codeunit.

That identifies #3606's "stated for 1,658 of 1,690" figure: the 32 Base Application documents
with no `TestIsolation` attribute are the test codeunits, not codeunits that "declined to
state" a property others stated.

**None of it reaches the column.** A tier answers `None` for all four rows above, including
the third — which is exactly what makes the document a bad source for this column and is why
`RequiredTestIsolation` is defaulted rather than converted.

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

Corpus PR 296 (`StefanMaron/BusinessCentral.AL.Language.Tests`), on eight cloud legs.

**Green, and what the two converted columns rest on:**

- `Record_CodeunitMetadata_Get_InherentPermissionsAndEntitlements_ReadIndependently` — a
  codeunit declaring `InherentPermissions = X` reports `'X'` while its entitlement column
  stays empty, and the reverse for the sibling; a codeunit declaring neither reports both
  empty.
- `Record_CodeunitMetadata_Get_ALNamespace_ReportsTheDeclaringFilesNamespace` — a namespaced
  codeunit reports its full dotted namespace, an un-namespaced one the empty string.

Both fail against the runner as it stood before #3606, with concrete wrong values: `''` where
`X` is declared, `''` where a namespace is declared.

**Red on all eight, and removed:** two `RequiredTestIsolation` tests, which asserted the
column tracks the declared property. It does not — see
[`#requiredtestisolation`](#requiredtestisolation). They were dropped from the corpus PR
rather than inverted to assert `None`: pinning an observation without a mechanism is what
`.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md` forbids, and the mechanism that
*was* established (a property `Ncl.dll` never assigns) is a fact about the runtime engine
rather than about AL, so it has no AL assertion that would fail if it were different.
