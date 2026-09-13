# AllObj (2000000038) and AllObjWithCaption (2000000058)

Reference for the two object-inventory virtual tables the runner projects. Both are
populated from one shared inventory, `RecordPatches.EnumerateKnownAlObjects`, so they can
never disagree about which objects exist.

The code lives in:

- `AlRunner/Patches/RecordPatches.AllObjVirtualTable.cs` — the inventory, the AllObj row
  builder, and the object-owner index.
- `AlRunner/Patches/RecordPatches.AllObjWithCaptionVirtualTable.cs` — the
  AllObjWithCaption row builder.

## The two tables do not have the same columns

Read out of the platform package's own source, `System.app` →
`src/Virtual Tables/AllObj.Table.al` and `AllObjWithCaption.Table.al`, on BC 28.1:

| field | AllObj (2000000038) | AllObjWithCaption (2000000058) |
|---|---|---|
| 1 | Object Type (option) | Object Type (option) |
| 3 | Object ID (Integer) | Object ID (Integer) |
| 4 | Object Name (Text[30]) | Object Name (Text[30]) |
| 20 | — | Object Caption (Text[249]) |
| 30 | — | **Object Subtype (Text[30])** |
| 60 | App Package ID (Guid) | App Package ID (Guid) |
| 61 | App Runtime Package ID (Guid) | App Runtime Package ID (Guid) |
| 62 | AL Namespace (Text[500]) | App ID (Guid) |
| 63 | — | AL Namespace (Text[500]) |

**`AllObj` has no `Object Subtype` column.** Issue #2326 was filed saying both tables carry
one; only AllObjWithCaption does. BC's own providers agree: `AllObjDataProvider`
fills a six-slot buffer, `AllObjWithCaptionDataProvider` fills nine. Note also that field
62 means different things in the two tables.

## Object Subtype

### What BC answers, per object kind

Decompiled from `Microsoft.Dynamics.Nav.Runtime.AllObjWithCaptionDataProvider.GetCaptionAndSubtype`
(BC 28.1, `Microsoft.Dynamics.Nav.Ncl.dll`). The method builds a local `string text =
string.Empty`, switches on the object type, and returns
`(text.Length == 0) ? emptySubtype : GetTruncatedTextValue(30, text)`.

| Object type | Object Subtype | source in BC |
|---|---|---|
| Table, TableData | the `TableType` member name | `EnumHelper<TableType>.EnumToString(metaTable.TableType)` |
| Page | the `PageType` member name | `EnumHelper<PageType>.EnumToString(metaForm.PageType)` |
| Query | the `QueryType` member name | `EnumHelper<QueryType>.EnumToString(metaQuery.QueryType)` |
| Codeunit | the `Subtype` member name, **or the empty string when it is `Normal`** — and `Install` never reaches here as `Install` (below) | `subtype == CodeunitSubType.Normal ? string.Empty : EnumHelper<CodeunitSubType>.EnumToString(subtype)` |
| PageExtension, TableExtension, EnumExtension, PermissionSetExtension, ReportExtension | the **target object's id**, as a decimal string | `appGroup.GetObjectSummary(...)?.Summary?.TargetObjectId.ToString(InvariantCulture)` |
| Report, XmlPort, System, everything else | the empty string | no branch assigns `text` |

`EnumHelper<T>.EnumToString` returns the enum member's own name (via `DefinedEnumToString`),
falling back to the numeric value only for an undefined one — so the strings are spelled
exactly as the AL property is spelled: `RoleCenter`, `Install`, `Temporary`, `CRM`.

### Install is empty, for a reason one level upstream

A codeunit declaring `Subtype = Install` reports the **empty string** here, and not because
Install is blanked. The AL compiler does not carry Install into object metadata at all:
`NCLMetaCodeunit.Subtype` returns the codeunit's `NavCodeunitOptionsAttribute` value — what
the compiler *wrote*, not what the author declared — and for an Install codeunit that is
`Normal`. `GetCaptionAndSubtype` therefore sees `Normal` and blanks it, so the value lands
on the empty string by two steps rather than one.

Both of the runner's row sources carry the *declared* property (the AL parser reads
`Subtype = Install;` from source; `BcAppSymbolCache` reads `"Subtype": "Install"` from
`SymbolReference.json`), so the translation happens in `ObjectSubtypeTextFor` — the same
constant, in the same position, as `ResolveCodeunitSubtypeOrdinal` does it for CodeUnit
Metadata's own `SubType` column. The measurement behind that constant, over 1,690 Base
Application codeunits, is on `AlSubtypeTheCompilerDoesNotEmit`.

Adjudicated directly: the first version of the upstream test asserted `'Install'`, and BC
27.0, 27.3, 27.5, 28.2 and 28.3 each answered the empty string — while the other seven
tests in the same prefix passed on every one of those legs.

### The Normal asymmetry

Only the **codeunit** branch special-cases its enum's default. A table declaring no
`TableType`, and a query declaring no `QueryType`, call `EnumToString` unconditionally and
therefore report the word `Normal`. Applying one rule to all three would be wrong in two
directions at once. The upstream corpus asserts both halves side by side (codeunit 60802,
`StefanMaron/BusinessCentral.AL.Language.Tests`).

### What the runner answers

`RecordPatches.ObjectSubtypeTextFor` applies the table above to whatever subtype the shared
inventory carries for an object. The inventory sources it from the property that already
feeds that object kind's own virtual table, so a subtype here and the value that table
reports cannot drift:

| kind | source-compiled | precompiled dependency `.app` |
|---|---|---|
| Table | `ParsedTable.TableTypeName` (Table Metadata's `TableType`) | `EnumerateBcAppTableSymbols`, same property |
| Page | `ParsedPage.PageType` (Page Metadata's `PageType`) | `BcAppSymbolCache.PageSymbol.PageType` |
| Query | `ParsedQuery.QueryType` | `BcAppSymbolCache.QuerySymbol.QueryType` |
| Codeunit | `ParsedAlObjectDecl.Subtype` (CodeUnit Metadata's `Subtype`) | `BcAppSymbolCache.ObjectSymbol.Subtype` |

Both codeunit sources state the *declared* property, so `ObjectSubtypeTextFor` applies the
`Install` → `Normal` translation before the blanking test — see above.

A `null` from any of those means "declares none", which the AL defaults turn into `Normal`
for a table or query and into the empty string for a codeunit — matching BC.

### The five *extension kinds: the target object's id (#3392)

BC answers a `PageExtension` / `TableExtension` / `EnumExtension` /
`PermissionSetExtension` / `ReportExtension` row's Object Subtype with the **target
object's id** as a decimal string, read from `NavAppGroup.GetObjectSummary`. This section
described that as a known gap until #3392; it is now implemented, and what follows is how.

The runner models no app-group object summary, so it cannot copy BC's route. What it does
instead is resolve **the same target** through the object inventory it already has, in two
steps that are deliberately separate:

1. **The inventory carries the target NAME.** Every extension's `extends` target reaches
   `EnumerateKnownAlObjects` through the same slot that carries a subtype for the other
   kinds — the two never coexist on one object, so one slot serves both. Source-parsed
   extensions read it off `ApplicationObjectExtensionSyntax.BaseObject`, which all five AL
   extension node types derive from; precompiled ones read it off the `.app`'s
   `SymbolReference.json` (see the spelling note below).
2. **`ObjectSubtypeTextFor` resolves that name to an id**, in the target kind's **own id
   namespace** — `ExtensionTargetObjectKind` is the mapping, and it is the part worth
   reading before editing. AL gives every object kind a separate id namespace, so
   resolving a pageextension's target among tables answers a plausible **wrong number**
   rather than nothing.

An unresolvable target answers the empty string, which is BC's own `?? string.Empty` on
that arm rather than a runner invention.

**The mapping is BC's list, not a suffix test.** `QueryExtension` is absent from BC's
switch arm *and* from AllObjWithCaption's Object Type option set; `ProfileExtension` is in
the option set but not in the arm. Both therefore keep the empty string, and
`AllObjWithCaptionObjectSubtypeTests` pins both — a `kind.EndsWith("Extension")`
simplification fails on exactly those two.

#### One element the AL compiler does not spell consistently

Measured against Base Application 28.1, because this is the trap that makes a
reportextension look like an unfixed gap:

| container | count | target element |
|---|---|---|
| `TableExtensions` | 90 | `TargetObject` |
| `PageExtensions` | 156 | `TargetObject` |
| `EnumExtensionTypes` | 39 | `TargetObject` |
| `PermissionSetExtensions` | 55 | `TargetObject` |
| **`ReportExtensions`** | **14** | **`Target`** — not one carries `TargetObject` |

Both are names, neither is an id. `BcAppSymbolCache.ExtensionTargetName` reads
`TargetObject` and falls back to `Target` for that reason; reading only the first spelling
leaves every reportextension's target null, which reaches this column as an empty Object
Subtype and is indistinguishable from the pre-fix behaviour.

The BC-behaviour claim is adjudicated upstream in corpus codeunit 60802
`"Test AllObj Virtual Table"`.

## App group visibility

Issue #2279. One runner process can compile and run several app groups: every app group under
one bundle root, every bundle on one command line, and every `sourcePaths` entry of one
`--server` request. The parsed-object registries behind `EnumerateKnownAlObjects` hold all of
them at once, because source dirs are registered for the whole bundle before any app group
runs, and resetting them per group breaks record access on app-defined tables (see
`BcRuntime.ResetForNewBundleReload`).

So the three object-inventory tables filter at insert time instead:

| table | populator |
|---|---|
| AllObj (2000000038) | `PopulateAllObjVirtualTable` |
| AllObjWithCaption (2000000058) | `PopulateAllObjWithCaptionVirtualTable` |
| Table Metadata (2000000136) | `PopulateTableMetadataVirtualTable` |

An object is left out when **both** of these hold:

1. Its declaring app is known. `RecordSourceObjectOwners` records, for every source file the
   runner parses, the app whose nearest `app.json` owns that file.
2. That app is not the executing app group and not in its declared dependency closure.
   The executing app group is the app id of `BcRuntime.CurrentTestAssembly`; the closure
   follows each source app's `app.json` `dependencies` transitively.

An object with no recorded owner is always listed. That covers precompiled dependency `.app`
objects and platform objects, which this filter has no evidence about. Objects of a precompiled
`.app` registered by a different bundle in the same process are therefore not filtered here.

Table Metadata's cached row list (`EnumerateKnownTableMetadata`) stays process-wide; the filter
runs on the way into each store, so one group's filtered view is never cached for another.

Each store is pinned to the app group that first populated it (`PinInventoryScope`). The
"already inserted" sets are add-only, so if a store were handed out to a second app group its
rows would still carry the first group's objects. That case refuses with an
`app-group-visibility` shape gap rather than answering with the wrong inventory. In measured
CLI and `--server` runs each app group gets a fresh store, so the refusal has not fired.

Proven by `tests/runner-extras/app-group-visibility-{a,b,c}` (C depends on A, so A's table is
visible to C and B's is not) and `AlRunner.Tests/AppGroupObjectVisibilityTests`.
