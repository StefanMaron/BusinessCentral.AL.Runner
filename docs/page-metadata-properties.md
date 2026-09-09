# Page Metadata's eleven `<Properties>`-derived columns

`AlRunner/Patches/RecordPatches.PageMetadataProperties.cs` answers eleven columns of the
"Page Metadata" (2000000138) virtual table from BC's own compiled page document, rather than
from `NavValue.GetDefaultNavValue` (issue #3601). This is the derivation behind the claims in
that file's header; the file itself carries only the claim and a pointer here.

<a id="the-measurement"></a>

## The measurement that motivated the fix

Measured at `main` `9a0dd368` (issue #3562's tracking comment): eleven of the table's twelve
defaulted columns are stated outright in BC's own emitted `<Properties>` document —
`RefreshOnActivate` in 236 of 236 pages, `ALNamespace` in 236 of 236, `InherentEntitlements`
in 94, `InherentPermissions` in 92, `APIVersion` in 39 on Base App, `DataCaptionExpr.` in 32,
`EntityName`/`EntitySetName` in 10, `APIPublisher`/`APIGroup` in 8, `ChangeTrackingAllowed`
in 2. `AppID` is the twelfth and is not stated on the document at all — see below.

<a id="one-object-not-two"></a>

## Why one object serves both page origins, and why not the two obvious places

#3063 established the pattern one element down, on `<SourceObject>`: a page this run
source-compiled and a page declared by a precompiled dependency `.app` already converge on
ONE object, BC's own `MetaPageDefinition`, loaded through `EnsureRealPageMetadata`'s
`NCLMetaForm.LoadMetadata()`. This file reads the same object one layer up —
`MetaPageDefinition.Properties` (a `MetaPageProperties`) for ten of the eleven, and
`MetaPageDefinition.ALNamespace` itself for the eleventh.

Verified against BC 28.1's decompiled
`PageDataProvider.<GetValuesWithinRangeForKeyField>d__3.MoveNext()`: it reads
`properties.RefreshOnActivate` / `.APIPublisher` / `.APIGroup` / `.APIVersion` /
`.EntitySetName` / `.EntityName` / `.DataCaptionExpr` / `.ChangeTrackingAllowed` and
`item.ALNamespace` (via `GetNormalizedNamespace`, which is `NavText.Create(fieldValue)` — no
further transform) directly off this same object. So there is nothing left for either of the
two row sources `EnumerateKnownPageMetadata` already walks (`ParsedPage` for a source-compiled
page, `BcAppSymbolCache.PageSymbol` for a dependency `.app`) to add — feeding the two
independently would make "the table answers differently depending on where a page came from"
the default outcome, which is the failure mode #3063 already rejected for the nine
`<SourceObject>` columns.

<a id="inherent-permissions"></a>

## `InherentPermissions` / `InherentEntitlements` are not a direct field read

BC's real column value is not `properties.InherentPermissions` printed as text — it is that
raw declared mask, expanded and formatted through two of BC's own runtime-engine methods:

- `PermissionDefinition.ExpandDirectPermissionToIndirect(PermissionMask)` — sets the matching
  INDIRECT bit for every DIRECT bit a page declares. `NCLMetaForm.LoadMetadata()` does exactly
  this before storing `InherentPermissionsAndEntitlements`.
- `MetadataDataProvider.CreatePermissionMaskString(PermissionMask)` — renders the mask as the
  letters columns 30/31 carry: uppercase for a DIRECT bit, lowercase for an INDIRECT-only one,
  empty for `PermissionMask.None`.

Both are internal members of internal-but-loadable types in `Ncl.dll`, resolved by reflection
the same way `PageDataProvider.GenerateSourceTableViewString` is in
`RecordPatches.PageMetadataSourceObject.cs`.

A page can only ever declare `X` (Execute) for either property — verified against the real AL
compiler: `InherentPermissions = rimd;` on a page is rejected with `AL0195: Invalid permission
kind. Expected: 'X'`, while the identical declaration compiles on a table. So the expand step
never changes the *observable* letters for a page (a direct bit always wins the uppercase
branch before its own indirect twin is checked) — it is still BC's real call graph, not a
shortcut that happens to agree with it today, and the day BC allows more than `X` on a page
this stops being a coincidence.

<a id="data-caption-expr"></a>

## `DataCaptionExpr.` is a fixed placeholder, not the AL source text

Measured against the real AL compiler (BC 28.1): a page declaring `DataCaptionExpression =
'Probe Page Fixture';` and one declaring `DataCaptionExpression = 'Some Other Totally
Different Text Value XYZ';` both compile to the identical `<Properties
DataCaptionExpr="DataCaptionExprCode" .../>`. BC compiles the expression to a generated method
and the document merely names that there is one, not what it evaluates to.
`properties.DataCaptionExpr` is exactly that fixed string, so reading it verbatim is correct;
there is no formatting step to reproduce. Corpus PR #298 pins this on a real service tier
(merged, all eight cloud legs green — see the runner PR body for the run).

<a id="api-version-default"></a>

## `APIVersion`'s "declares none" default is `"beta"`, not empty

`Microsoft.Dynamics.Nav.Types.MetaPageProperties.APIVersion`'s own getter (decompiled, BC
28.1) carries `[DefaultValue("beta")]` and returns the literal `"beta"` when the backing field
is null — every other string column on this type returns `""` or a raw possibly-null field.
Confirmed against a live AL-compiler probe: a page declaring no `APIVersion` attribute at all
still reads `"beta"` through this property. `RecordPatches.PageMetadataProperties.cs` needs no
special case for this — it falls out of reading `properties.APIVersion` directly — but a test
asserting the "declares none" default as empty would be wrong for this one column. Corpus PR
#298's negative control pins `"beta"`, adjudicated on a real service tier.

<a id="app-id"></a>

## `AppID` is deliberately not here

BC's real column reads `MetadataDataProvider.GetAppId(metaFormById)`, computed from the
page's owning app-identity/app-group, not from `<Properties>` at all — the one column of the
twelve the measurement above found unstated on the document. It stays on
`NavValue.GetDefaultNavValue` in `RecordPatches.PageMetadataVirtualTable.cs`, unchanged.
