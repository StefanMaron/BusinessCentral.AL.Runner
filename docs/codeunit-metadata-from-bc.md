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
    TestType = metaCodeunit.TestType;
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
branch never fires and the field keeps its constructor default `0` = `None`. That matches the
corpus failure exactly: `Expected:<1>` (Disabled) `Actual:<None>`.

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
write immediately, present on 27.0, 28.1 and 28.4.

Two lessons, cheap to state and expensive to relearn:

- **An empty result from one tool is not evidence.** Confirm it with a differently-shaped query
  before building an argument on it — the rule this repository already applies to search.
- **Establishing which path is *unused* says nothing about what the used path answers.** The
  earlier reasoning correctly found the parser's name mismatch, then discarded it on the strength
  of the false zero. It was the answer all along.
