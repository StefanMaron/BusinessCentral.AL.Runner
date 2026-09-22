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

<a id="the-method-table"></a>

## The method table: what BC emits, and which half the symbol file can supply

`MetaRuntimeInfo.Methods` (and `#methodsDictionary`, the same collection under a second
signature) is the `<Methods>` subtree of a `<CodeUnit>` document. The runner derives it for a
codeunit whose loaded assembly proves the derivation is complete, and omits it otherwise
(#3788, after #3963 established why the symbol file alone cannot).

### BC emits the ATTRIBUTED methods, not the methods

Measured on BC 28.1.49838.53910 over System Application + Business Foundation, 558 codeunit
documents:

| | |
|---|---:|
| documents with a `<Methods>` subtree | 145 |
| `<Method>` elements | 326 |
| …carrying **no** attribute | **0** |
| `EventPublisherAttribute` | 169 |
| `EventSubscriberAttribute` | 140 |
| `InherentPermissionsMethodAttribute` | 17 |

### One of the three kinds is invisible to `SymbolReference.json`

`SymbolReference.json` is an app's consumer-facing API surface, so it states no `local` method
— and an AL event subscriber is always `local`. Of the 140 subscriber methods BC emits for
System Application, the symbol file states **0**, by id *and* by name. The same applies to the
`InherentPermissions` methods BC emits for local methods (codeunits 306, 307, 309, 8705).

The publishers, by contrast, reproduce **exactly**: measured per codeunit on 28.1, the symbol
file's publisher list equals BC's publisher subsequence — id, name and order — for **70 of 70**
codeunits that have one.

### Why a short subtree is worse than no subtree

`MetadataObjectDiff` pairs `Methods` **positionally**: it is absent from `PairByIdMembers`, and
`MetaMethod` spells its id `MethodId`, which `IdPropertyNames` does not list. So a subtree
missing the subscribers does not merely under-report — from the first missing element on it
puts a *different* method in BC's slot, which is the runner asserting an association it has no
evidence for (`.claude/rules/loud-failures.md`).

Modelled over both apps at 28.1:

| policy | codeunits emitted | methods | exact | **fabricated slots** | one-directional absences |
|---|---:|---:|---:|---:|---:|
| absence (before #3788) | 0 | 0 | — | 0 | 326 |
| symbol file alone | 76 | 168 | 66 | **8** | 133 |
| **symbol file + assembly witness** | 66 | 152 | 66 | **0** | 174 |

### The assembly is a witness, not a data source

The dependency's own R2R assembly carries `[NavEventSubscriberAttribute]` on exactly the methods
the symbol file cannot see, and `MethodIdAttribute` reproduces BC's `<Method ID>` for 140 of 140
subscribers. What it cannot supply is BC's **order**.

BC's document order is the **AL source declaration order** — measured 70 of 70 against the `.al`
sources shipped inside the `.app`, on the codeunits with two or more emitted methods. The
assembly's metadata-table order is alphabetical. Every ordering hypothesis tried scored at best
22 of 70:

| hypothesis | matches |
|---|---:|
| alphabetical | 22 / 70 |
| assembly metadata-table order | 22 / 70 |
| kind-grouped, then table order | 20 / 70 |
| `MethodId` ascending (signed) | 18 / 70 |
| `MethodId` ascending (unsigned) | 17 / 70 |
| **AL source declaration order** | **70 / 70** |

Recovering source order would mean parsing the AL shipped in the `.app`, which is the parser
treadmill #3491 describes. So the assembly answers only the question it answers exactly and
cheaply — *does this codeunit declare a subscriber?* — and the symbol file supplies the data for
the codeunits it answers completely.

**Cost: no new load.** `DependencyLoader.LoadAll` runs before the `AddBcAppPath` loop at both
`Program.cs` call sites, so the assemblies are already in the AppDomain;
`RegisterAppAssemblies` is the one place holding `(assemblies, appPath)` together and is where
the scan is driven from. The scan reads already-mapped metadata through `AssemblyTypeIndex` —
**34-35 ms** for System Application's 17 MiB assembly and its 533 `Codeunit` types, once per
assembly per process.

### Cross-version

The policy was modelled on three builds — two genuinely distinct binaries, across the 27.x/28.x
boundary:

| build | emitted | methods | exact | fabricated | absences (was) |
|---|---:|---:|---:|---:|---:|
| 27.5.46862.53931 | 66 | 151 | 66 | **0** | 169 (320) |
| 28.1.49838.53910 | 66 | 152 | 66 | **0** | 174 (326) |
| 28.4.53241.54407 | 66 | 153 | 66 | **0** | 175 (328) |

### The third state

The witness answers yes / no / **unknown**, and unknown is deliberately not spelled as no. An
app whose assemblies never loaded — a platform symbol-only app, or a load that fell back to
service-tier DLL dispatch — has measured nothing about its subscribers, and reading that
silence as "no subscribers" restores exactly the 8 fabrications above
(`.claude/rules/guards-need-a-third-state.md`). Unknown abstains.

### The `InherentPermissions` method attribute

BC writes **five** attributes on an `InherentPermissionsMethodAttribute` element, not one. The
four beyond `Name` come from the AL attribute's own positional arguments —
`InherentPermissions(ObjectType, ObjectId, Mask[, Scope])` — and until #4339 both method-table
renderers wrote `Name` alone.

| attribute | source | how it is derived |
|---|---|---|
| `InherentPermissionObjectType` | argument 0 | **verbatim**; `TableData`, `Codeunit`, `Page` |
| `InherentPermissionObjectId` | argument 1 | **verbatim**; the symbol file already states a number, because the AL compiler resolved `Database::"No. Series Line"` before writing it |
| `InherentPermissionPermissionValue` | argument 2 | the shared letter decode, [`#how-a-permission-mask-is-spelled`](#how-a-permission-mask-is-spelled) |
| `InherentPermissionScope` | argument 3 | the **ordinal** of BC's `InherentPermissionsScope`, whose decompiled body is `{ Both, Permissions, Entitlements }`; absent ⇒ 0 |

**What the shipped apps could and could not settle.** Over Business Foundation + System
Application at 28.1.49838.53910 (`Microsoft.Dynamics.Nav.Ncl.dll` sha256 `49b11d9b…`), BC emits
24 of these elements across 11 codeunits and **0 on pages**. Joining each back to its symbol
entry, the mapping above reproduces all four values on **17 of 17** with zero disagreements.
The other **7** — codeunits 306, 307, 309 and 8705 — have no symbol entry at all, because the
methods carrying them are `local`; they are not rendered, for the same reason no event
subscriber is ([`#one-of-the-three-kinds-is-invisible-to-symbolreferencejson`](#one-of-the-three-kinds-is-invisible-to-symbolreferencejson)).

Two things those 24 elements **cannot** establish, because they do not vary:

- **Scope is not a constant.** All 24 read `0`, so a hardcoded `0` would agree with every one
  of them. `Both` and an absent argument are the same ordinal, which is what hides the mistake.
  Base Application states the only shipped four-argument instance (codeunit 386
  `SetGLRegisterNo`, `'Both'`) — and it too reads 0.
- **BC does emit this on a page.** The bundle's `0 on pages` is a property of those two apps,
  not of BC's emitter: of Base Application's 2,610 pages exactly **one** states an
  `InherentPermissions` method (99000833 "Check Prod. Order Status", `SalesLineShowWarning`),
  and neither measured app has any.

**What settled both: a probe app through BC's own compiler.** A synthetic app declaring
`InherentPermissions` methods on a page *and* a codeunit, emitted through
`tools/gen-metadata-ground-truth.sh` at 28.1.49838.53910 (`[emit] success=True objects=3
errors=0`):

| declared | document | ObjectType | ObjectId | PermissionValue | Scope |
|---|---|---|---:|---:|---:|
| `(TableData, "Probe Table", 'r')` | CodeUnit | `TableData` | 70000 | 32 | 0 |
| `(TableData, "Probe Table", 'r', ::Permissions)` | CodeUnit | `TableData` | 70000 | 32 | **1** |
| `(TableData, "Probe Table", 'r', ::Entitlements)` | CodeUnit | `TableData` | 70000 | 32 | **2** |
| `(Codeunit, ::"Probe Codeunit", 'X')` | CodeUnit | **`Codeunit`** | 70001 | 16 | 0 |
| `(Page, ::"Probe Page", 'X')` | CodeUnit | **`Page`** | 70002 | 16 | 0 |
| `(TableData, "Probe Table", 'rm')` | **PageDefinition** | `TableData` | 70000 | 160 | 0 |
| `(TableData, "Probe Table", 'rimd', ::Both)` | **PageDefinition** | `TableData` | 70000 | 480 | 0 |

So the page renderer writes the same four, and the scope ordinals are BC's own enum order.

**An unreadable value refuses all four** rather than contributing a default, because three of
four attributes would put a partial association in BC's slot — the failure the whole `<Methods>`
derivation exists to avoid (`.claude/rules/guards-need-a-third-state.md`). Four shapes take four
paths: an argument list shorter than three, a non-numeric object id, a mask letter outside
`RIMDX`, and a scope name outside BC's three.

<a id="the-parameter-list"></a>

## The parameter list: BC writes the runtime's spelling, the symbol file states AL's

Every `<Method>` BC emits carries a `<Parameters>` element — all 326 at 28.1.49838.53910, of
which 291 have children and 35 are written `<Parameters />` because the method declares none.
Across both apps that is **574 `<Parameter>` elements**, each with five attributes plus a sixth,
`Length`, on the 19 whose AL type declares one.

#3788 landed the method table without them, because the two sides disagree in **both** fields a
`<Parameter>` is made of. Codeunit 304 `GetNoSeriesLine`, Business Foundation:

| | BC's emitted `<Parameter>` | `SymbolReference.json` |
|---|---|---|
| name | `hideErrorsAndWarnings` | `"Name": "HideErrorsAndWarnings"` |
| a boolean | `RuntimeType="bool"` | `"TypeDefinition": { "Name": "Boolean" }` |
| a `Code[20]` | `RuntimeType="NavCode" Length="20"` | `"TypeDefinition": { "Name": "Code[20]" }` |
| a `var` record | `RuntimeType="INavRecordHandle"`, `RuntimeAttributes="[NavObjectId(ObjectId=309)],[NavByReferenceAttribute]"` | `"IsVar": true`, `Subtype.Id = 309` |

#4084 filed both as derivations with a proof path rather than architectural limits, and refused
to guess either: *"a casing rule guessed wrong produces a `<Parameter>` element that looks right
and names something else."*

### How it was measured

The ground-truth bundles `tools/gen-metadata-ground-truth.sh` produces are BC's own emitter
output. Joining them to each app's shipped `SymbolReference.json` on **(codeunit id, method id)**
gives both spellings of the same parameter, and the join is checked on all six values at once.

The join is restricted to id matches. Matching by **name** instead adds one pair and it is wrong:
codeunit 8705 `UpdateFeatureUptakeStatus` exists as both a `local` method BC emits and a public
method the symbol file states, so a name match pairs BC's 1-parameter overload with the symbol
file's 5-parameter one. That single pair is the fabricated-slot hazard in miniature.

Four builds, whose `Microsoft.Dynamics.Nav.Ncl.dll` are four **distinct** binaries — so these
are four independent measurements rather than one wearing four labels:

| build | `Ncl.dll` sha256 | joined methods | `<Parameter>` elements | exact | disagreements | unmapped AL types |
|---|---|---:|---:|---:|---:|---:|
| 27.5.46862.53931 | `affa03c9` | 164 | 322 | 322 | 0 | 0 |
| 28.1.49838.53910 | `49b11d9b` | 168 | 328 | 328 | 0 | 0 |
| 28.1.49838.54308 | `6f2cf682` | 168 | 328 | 328 | 0 | 0 |
| 28.4.53241.54407 | `108b8c6b` | 169 | 330 | 330 | 0 | 0 |

**1,308 observations, 1,308 exact, zero disagreements.** That is what licenses the derivation;
anything less would have licensed only the subset it reproduced.

Of 28.1's 328, the **302** on the 66 codeunits the runner actually emits are the allowlisted
`MetaMethod.Parameters.<presence>` ×302 the issue recorded, plus 5 on the page side.

### The casing rule is a pure first-character lowercase

Not "camelCase" — the first character is lowercased and **nothing else is touched**. The two
rules agree on 325 of 328 parameters, so the population that decides it is the three whose AL
identifier has two or more leading capitals:

| codeunit | AL identifier | BC writes | a word-aware rule writes |
|---:|---|---|---|
| 8951 | `AFSOperationResponse` | `aFSOperationResponse` | `afsOperationResponse` ❌ |
| 9017 | `AADObjectID` | `aADObjectID` | `aadObjectID` ❌ |
| 600 | `IDataArchiveProvider` | `iDataArchiveProvider` | `iDataArchiveProvider` (agrees) |

Two of the three refute the word-aware rule. #4084's own comment had noticed the same shape in
the assembly string heap (`aBSClientImpl`, `aADObjectID`) and deliberately declined to close on
it, because 14,439 identifiers of every kind are not a parameter population — these three are.

`AlRunner.Tests/CodeunitMethodParameterDerivationTests` pins both halves, and its casing test
fails loudly if the discriminating shape ever stops occurring, since both rules would then pass.

### AL type → `RuntimeType`, and the three shapes that are not what a reader guesses

Every AL base type name the shipped apps exercise on an emitted method, with its count over the
four builds. **There is deliberately no superset**: these 28 are exactly what the mapping covers,
so no entry rests on a guess about a shape BC never showed us.

| AL | `RuntimeType` | n | AL | `RuntimeType` | n |
|---|---|---:|---|---|---:|
| `Boolean` | `bool` | 376 | `JsonArray` | `NavJsonArray` | 12 |
| `Text` | `NavText` | 188 | `RecordId` | `NavRecordId` | 12 |
| `Record` | `INavRecordHandle` | 176 | `Interface` | `NavInterfaceHandle` | 12 |
| `Guid` | `System.Guid` | 140 | `Code` | `NavCode` | 11 |
| `Integer` | `int` | 132 | `RecordRef` | `NavRecordRef` | 9 |
| `Enum` | `NavOption` | 84 | `Decimal` | `Decimal18` | 8 |
| `Codeunit` | `NavCodeunitHandle` | 32 | `Time` | `NavTime` | 8 |
| `Date` | `NavDate` | 20 | `Variant` | `NavVariant` | 8 |
| `List` | `NavList<…>` | 16 | `JsonObject` | `NavJsonObject` | 8 |
| `Dictionary` | `NavDictionary<…>` | 8 | `JsonToken` | `NavJsonToken` | 8 |
| `DotNet` | `NavDotNet` | 8 | `ObjectType` | `NavObjectType` | 8 |
| `Action` | `FormResult` | 4 | `ClientType` | `NavClientType` | 4 |
| `DateTime` | `NavDateTime` | 4 | `InStream` | `NavInStream` | 4 |
| `ModuleInfo` | `NavModuleInfo` | 4 | `HttpRequestMessage` | `NavHttpRequestMessage` | 4 |

Three decisions the table alone does not carry, each measured rather than reasoned:

1. **An object handle is not wrapped in `ByRef<>` when `var`.** A `var` record stays
   `INavRecordHandle` and gains `,[NavByReferenceAttribute]` in `RuntimeAttributes`; a `var`
   scalar does the opposite, becoming `ByRef<bool>` with an empty `RuntimeAttributes`.
2. **`Interface` takes neither attribute.** It is a handle whose `Subtype` has no `Id`, and BC
   writes `RuntimeAttributes=""` on all three instances **even though all three are `var`**. A
   rule keying `NavByReferenceAttribute` on `IsVar` alone disagrees with BC on exactly those
   three — codeunits 600, 2611 and 3917.
3. **A generic argument loses its length.** `List of [Code[250]]` is
   `ByRef<NavList<NavCode>>`, not `NavList<NavCode[250]>`.

And two that carry no information: `Temporary` on a record changes nothing BC writes (measured on
the four `"Temporary": true` parameters), and `IsArray` is `"False"` on all 574.

### Why the derivation is all-or-nothing per method

`MetadataObjectDiff` pairs a `<Parameters>` element's children **positionally** — the same
property that made a short `<Methods>` subtree worse than none. A list that silently omitted the
one parameter it could not spell would put every later parameter in a different slot, which is
the runner asserting an association it has no evidence for
(`.claude/rules/loud-failures.md`). So one unrenderable parameter withdraws the whole element and
the method keeps the honest one-directional absence it had before.

Two shapes refuse, and neither is reachable from the shipped apps — which is why
`AlRunner.Tests/CodeunitMethodParameterRenderingTests` drives them from a fixture instead:

- **An AL type the mapping does not cover.** Zero occur on an emitted method in either app.
- **An array parameter.** BC writes `IsArray="True"` and a runtime type nothing here has
  observed. The one array-typed parameter in either app — codeunit 9556
  `GetRecordsFromTableId`, `"Text"` with `"ArrayDimensions": [10]` — sits on a method carrying no
  attribute, so BC emits nothing for it. Note its base type `Text` **is** mapped, so a rule
  checking only the type name would render it, wrongly.

An **empty** list is a real answer rather than a refusal: BC writes `<Parameters />` for a method
declaring none, and the symbol file states no `Parameters` key at all for every one of the 15
such methods in the emitted population.

### The cache sits between this derivation and its observable

`BcAppSymbolCache`'s key is `path|hash|v<CacheVersion>|shape:<PayloadShape>`. Adding `Parameters`
to `CodeunitMethodSymbol` moved `PayloadShape` from `b0e764a4c854cda6` to `459a9a54bd6fa396`
(measured off both builds through the `PayloadShapeForTests` seam), so the on-disk cache re-keys
itself and **no `CacheVersion` bump is needed** — the same measurement #3788 made for
`AttributedMethods`.

A change to the **derivation** moves none of those terms, though, so a warm box replays the
previous parse. Measured while writing this: the word-aware-casing mutation ran **green** against
a warm `~/.cache/al-runner/bc-symbols` entry holding the correct `aFSOperationResponse`, and went
red the moment that one entry was deleted. `CodeunitMethodParameterDerivationTests` therefore
overrides `CacheRoots` to a root private to the run. `.claude/rules/local-test-scope.md` has the
general form; CI never sees it, because every leg provisions fresh.

<a id="the-event-publisher-attribute"></a>

## The event publisher attribute: slot 1 means two different things

BC writes three flags on an `<EventPublisherAttribute>` element beyond `Name`, and all three
come from the AL attribute's **positional arguments**, which `SymbolReference.json` already
states. The trap is that the argument lists differ by attribute name, and they differ at the
same index:

| AL attribute | arg 0 | arg 1 | arg 2 |
|---|---|---|---|
| `IntegrationEvent` | `IncludeSender` | **`GlobalVarAccess`** | `Isolated` |
| `InternalEvent` | `IncludeSender` | **`Isolated`** | — |
| `BusinessEvent` | `IncludeSender` | **`Isolated`** | — |

So a rule reading "argument 1 is `GlobalVarAccess`" is right for the 128 `IntegrationEvent`
elements BC states it on and wrong for the 28 `InternalEvent` ones, where it silently writes
the isolation flag into `GlobalVarAccess`'s slot. That is a fix which looks finished at 82%
correct (#4443).

The table is read off the AL compiler's own resource keys in
`Microsoft.Dynamics.Nav.CodeAnalysis.dll`, which name one argument each and are exhaustive for
these three attributes — `IntegrationEvent_{IncludeSender,GlobalVarAccess,Isolated}`,
`InternalEvent_{IncludeSender,Isolated}`, `BusinessEvent_{IncludeSender,Isolated}`. There is no
`InternalEvent_GlobalVarAccess` or `BusinessEvent_GlobalVarAccess` key. The same binary carries
the split as two separate position constants, `EventPublisherIsolatedPosition` and
`EventPublisherIsolatedPositionIntegrationEvent`.

**Why `GlobalVarAccess` exists on only one of the three** is BC's own constructor rather than a
convention: `NavEventAttribute..ctor` computes
`AllowGlobalVarAccess = allowGlobalVarAccess & (EventType == NavEventType.Integration)`, so the
value is masked to false for every other kind and the emitter writes no attribute for it.

### What the shipped apps measure

Over Business Foundation + System Application at 28.1.49838.53910, joining all **157** emitted
`<EventPublisherAttribute>` elements to the same method's `Attributes` entry:

| | `IncludeSender` | `GlobalVarAccess` | `Isolated` |
|---|---:|---:|---:|
| BC states it | 157 | 129 | 9 |
| joined to a symbol entry, and agreeing | **156** | **128** | **9** |
| disagreeing | 0 | 0 | 0 |

The one element short of 157 states no arguments at all and has no symbol entry to join to.
`GlobalVarAccess` is stated on every `IntegrationEvent` and on no `InternalEvent`; `Isolated`
splits 6 at index 2 on `IntegrationEvent` and 3 at index 1 on `InternalEvent`.

**`Isolated` is written if and only if the AL attribute states the argument**, not merely when
it is true: of the 9, **8 are `True` and 1 is `False`**. An emitter omitting every false would
be wrong on that one. The same condition governs `GlobalVarAccess`; `IncludeSender` alone is
unconditional, carrying the AL default `False` where the attribute states no argument.

### What the shipped apps cannot measure, and the probe that settled it

Three shapes do not occur in either app, so the 157 elements above cannot adjudicate them: an
`InternalEvent` whose slot 0 is `True`, any `BusinessEvent` at all, and an `InternalEvent`
carrying both flags. A probe app declaring all ten shapes, compiled and emitted through BC's own
compiler at 28.1.49838.53910 (`[decl] errors=0`, `[emit] success=True objects=1 errors=0`):

| AL declaration | `Name` | `IncludeSender` | `GlobalVarAccess` | `Isolated` |
|---|---|---|---|---|
| `[IntegrationEvent(false, false)]` | `IntegrationEvent` | `False` | `False` | *absent* |
| `[IntegrationEvent(false, true)]` | `IntegrationEvent` | `False` | **`True`** | *absent* |
| `[IntegrationEvent(true, true, true)]` | `IntegrationEvent` | `True` | `True` | `True` |
| `[IntegrationEvent(false, false, false)]` | `IntegrationEvent` | `False` | `False` | **`False`** |
| `[InternalEvent(false)]` | `InternalEvent` | `False` | *absent* | *absent* |
| `[InternalEvent(true)]` | `InternalEvent` | **`True`** | *absent* | *absent* |
| `[InternalEvent(false, true)]` | `InternalEvent` | `False` | *absent* | `True` |
| `[InternalEvent(true, true)]` | `InternalEvent` | `True` | *absent* | `True` |
| `[BusinessEvent(false)]` | `BusinessEvent` | `False` | *absent* | *absent* |
| `[BusinessEvent(true, true)]` | `BusinessEvent` | **`True`** | *absent* | **`True`** |

The name-keyed rule reproduces all three flags on **10 of 10**, zero disagreements. Three of
those rows are load-bearing and were wrong in the runner before #4443:

- **`[InternalEvent(true)]` emits `IncludeSender="True"`.** Slot 0 is the sender argument here
  too, so an `InternalEvent` does have one. All 28 shipped elements state it as `False`, which
  is why a rule hardcoding `false` agreed with every one of them.
- **`BusinessEvent` follows `InternalEvent`'s shape, not `IntegrationEvent`'s.** Its isolation
  is slot 1, and it gets no `GlobalVarAccess`. Neither shipped app declares one, so nothing
  measured it.
- **A stated `Isolated="False"` is written, not omitted**, which distinguishes "the AL
  attribute states false" from "the AL attribute states nothing".

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
