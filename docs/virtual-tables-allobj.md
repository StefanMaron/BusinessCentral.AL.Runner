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
| Codeunit | the `Subtype` member name, **or the empty string when it is `Normal`** | `subtype == CodeunitSubType.Normal ? string.Empty : EnumHelper<CodeunitSubType>.EnumToString(subtype)` |
| PageExtension, TableExtension, EnumExtension, PermissionSetExtension, ReportExtension | the **target object's id**, as a decimal string | `appGroup.GetObjectSummary(...)?.Summary?.TargetObjectId.ToString(InvariantCulture)` |
| Report, XmlPort, System, everything else | the empty string | no branch assigns `text` |

`EnumHelper<T>.EnumToString` returns the enum member's own name (via `DefinedEnumToString`),
falling back to the numeric value only for an undefined one — so the strings are spelled
exactly as the AL property is spelled: `RoleCenter`, `Install`, `Temporary`, `CRM`.

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

A `null` from any of those means "declares none", which the AL defaults turn into `Normal`
for a table or query and into the empty string for a codeunit — matching BC.

### Known gap: the five *extension kinds

BC answers a `PageExtension` / `TableExtension` / `EnumExtension` /
`PermissionSetExtension` / `ReportExtension` row's Object Subtype with the **target
object's id** as a decimal string, read from `NavAppGroup.GetObjectSummary`. The runner has
no equivalent of that app-group object summary, so those kinds carry a null subtype through
the inventory and land on the empty string — the value they had before #2326.

This is deliberate rather than overlooked. The runner does resolve extension targets
elsewhere, but that is a different fact reached a different way, and writing it into this
column would put a value there that BC derives from a structure the runner does not model.
Tracked separately; see the issue linked from the PR that added this document.
