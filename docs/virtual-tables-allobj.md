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

**This is a runner model, not measured BC behaviour.** On a real tenant AllObj is tenant-wide
and lists every installed app's objects, related or not. The runner treats each app group as its
own tenant holding that group and its declared dependencies, because unrelated app groups sharing
one runner process is not a state a service tier has.

So the three object-inventory tables filter at insert time instead:

| table | populator |
|---|---|
| AllObj (2000000038) | `PopulateAllObjVirtualTable` |
| AllObjWithCaption (2000000058) | `PopulateAllObjWithCaptionVirtualTable` |
| Table Metadata (2000000136) | `PopulateTableMetadataVirtualTable` |

An object is left out when **both** of these hold:

1. Its owning app group is known. The run loops call `RegisterAppGroupSourceDirs` with each
   suite's source dirs before parsing, and `RecordSourceObjectOwners` gives every object in a
   file the app group whose registered dir contains it (the longest match). It is **not** the
   nearest `app.json`: a suite compiles a sub-folder carrying its own `app.json` into itself, and
   that app group's objects must stay listed for it. A dir shared by two groups, and a
   `(kind, id)` declared by two different groups, have no owner and are always listed.
2. That app is not the executing app group and not in its declared dependency closure.
   The executing app group is the app id of `BcRuntime.CurrentTestAssembly`; the closure
   follows each source app's `app.json` `dependencies` transitively.

An object with no recorded owner is always listed. That covers platform objects, and the
precompiled `.app` objects the next section leaves unowned.

Table Metadata's cached row list (`EnumerateKnownTableMetadata`) stays process-wide; the filter
runs on the way into each store, so one group's filtered view is never cached for another.

Each store is pinned to the app group that first populated it (`PinInventoryScope`). The
"already inserted" sets are add-only, so if a store were handed out to a second app group its
rows would still carry the first group's objects. That case refuses with an
`app-group-visibility` shape gap rather than answering with the wrong inventory. In measured
CLI and `--server` runs each app group gets a fresh store, so the refusal has not fired.

Proven by `tests/runner-extras/app-group-visibility-{a,b,c}` (C depends on A, so A's table is
visible to C and B's is not) and `AlRunner.Tests/AppGroupObjectVisibilityTests`.

<a id="shared-id-declarers"></a>

### An id several app groups declare (#4767, #4834, #4844)

When two app groups each declare the same `(kind, id)`, each has its own object, and
`RecordPatches.AppGroupScopeFor` decides which one the executing group gets:

| executing group | gets |
|---|---|
| declares the id | its own object |
| declares none, and exactly one declarer is in its dependency closure | that declarer's object, which is the one its code compiled against |
| declares none, and no declarer is in its closure | the process-wide object; its code cannot name the id |
| declares none, and two declarers are in its closure | `RunnerOutOfScopeException` in every test that reaches the id |

The same resolution decides event dispatch. A subscriber reaches a publisher of a shared id only
when the subscriber's group resolves to that publisher's group. A group that depends on X
therefore subscribes to X's object, never to Y's.

The last row is loud on purpose. BC does not install two apps that declare the same object id
into one tenant, so nothing says which object such a group's code names. The load-time
field-trigger walk skips that id, and the test that touches it fails with the id and both
declarers (`AppGroupObjectVisibilityTests.Cli_GroupDependingOnTwoDeclarersOfOneId_*`).

<a id="precompiled-package-visibility"></a>

### Precompiled dependency packages (#4448)

Every `.app` a bundle resolves is registered process-wide (`_bcAppPaths`), and bundles on one
command line accumulate them, so without an owner a package only one app group declares was
listed to every group. `RecordPatches.BuildPackageVisibility` gives those objects one:

- **The owner is the package's own app id**, from its `SymbolReference.json`, the same source
  `BuildObjectOwnerIndex` uses for the App Package ID column.
- **Only a claimed package is hideable**: one that some app group's closure reaches. A package no
  group reaches keeps no owner and stays listed, as before.
- **The Microsoft floor is never hideable**: packages named `Application`, `System`, `Base
  Application`, `System Application` or `Business Foundation` (publisher Microsoft) and their
  closure. Every tenant has them, and their install code writes rows whose `TableRelation`
  targets AllObjWithCaption (Retention Policy Setup, table 405), so hiding them fails the
  install of every group that reaches them only implicitly.
- **An id two packages declare** has no single owner and stays listed.
- **The closure follows packages too**: a group's `dependencies`, then each package's own
  manifest dependencies, resolved by app id and else by (name, publisher), the order
  `DependencyResolver.TryFind` uses.
- **A source owner decides first**; the package lookup applies only to an id no source declares.

**Code sees its own app's closure.** A bundle's dependency install triggers and event subscribers
fire under whichever app group is executing, including one that does not declare that dependency.
`PinInventoryScope` and `CurrentVisibleAppClosure` widen the group's closure by the closure of every
app whose code is executing: each registered AL assembly with a frame on the call stack
(`BcRuntime.AppIdsOnCallStack`). Without it, an install or a subscriber that reads AllObj for its own
table fails in every non-declaring group (a retention-policy registration is that shape). Rows a
widened read inserts stay in that provider's add-only store, which is what `main` did for them.

Proven by `tests/runner-extras/app-group-visibility-b` (the `*_PrecompiledDepOfUnrelatedGroup_*`
tests: nothing of `xmlport-precompiled-dep-metadata`'s or `app-group-visibility-install-dep`'s
package is listed to B), `app-group-visibility-install-dep` (the declaring group lists its own
package, and the package's install trigger errors unless it sees its own table), `app-group-visibility-subscriber` (an unrelated group raises the event the
package subscribes to, and the subscriber finds its own table), `app-group-visibility-floor` (Base Application stays listed to a group that reaches no floor app),
and `AlRunner.Tests/PackageObjectVisibilityTests`. The runner-extras half is meaningful only in the
combined `tests/runner-extras` run, where every one of those packages is registered;
`app-group-visibility-floor` and `app-group-visibility-subscriber` fail when run on their own, because
nothing registers Base Application or the package there.

<a id="multi-bundle-metatable-cache"></a>

## A bundle's own table answering as if it had no fields (#4450)

The section above is about an object being hidden from a group that should not see it. This one
is the opposite failure and a different mechanism: a group's **own** table answering as though it
did not exist, in a run with several bundles.

`EnsureTableInMetadataCache` is `_metaTableCache.GetOrAdd(tableId, BuildNCLMetaTable)`, and
`BuildNCLMetaTable` returns `null` for a table it cannot find in `_parsedTables`. A
`ConcurrentDictionary` caches that `null` like any other value, and `GetOrAdd` never replaces an
existing entry — including `PopulateNclMetadataCache`'s own `GetOrAdd`.

In a **single-bundle** run that is correct: every source dir is registered before anything asks,
so a `null` means the table genuinely does not exist and caching it is the right answer.

With **several bundles on one command line** the absence is temporary. Bundle A can ask about
bundle B's table — an `AllObj.Get`, a `Table Metadata.Get`, a `Field.Get`, any inventory lookup —
before B's source dir has been registered. `BuildNCLMetaTable` correctly returns `null` at that
instant, `GetOrAdd` caches it, and nothing evicts it when B's sources arrive. From then on B's own
table answers `null` for the rest of the process.

**It is positional, not identity-based.** Reverse the two paths on the command line and the
failure moves to whichever bundle now runs second. Each bundle alone passes.

### What it looked like

Silent. `Field.Get(<B's own table>, 1)` returned `false` — no exception, no
`RunnerOutOfScopeException`, no `VirtualTableShapeGap` — because `PopulateFieldVirtualTable` skips
any source table whose metatable is `null`. Measured on three sibling bundles: only the **first**
bundle's table was ever inserted into any of the three Field providers.

The `PinInventoryScope` / `CheckInventoryScope` refusal does not catch it, for a reason unrelated
to the defect: that guard refuses a store populated under one app group and read under another,
and here each bundle gets its OWN provider, so the pinned and current app groups always agree and
it correctly stays silent. Its silence says nothing about this defect either way.

### The fix

`EvictCachedNullsForNewlyParsedTables`, called from `AddSourceDirs` whenever a batch actually
parsed something, drops every `_metaTableCache` entry whose value is `null` and whose table is now
in `_parsedTables`. Only `null` entries are dropped, so a live `NCLMetaTable` that R2R-precompiled
callers hold baked offsets into is never replaced under them
(`.claude/rules/precompiled-dll-respect.md`).

`EvictCachedMetaTableForBaseTable` (#2463) is the same statement for a tableextension parsed after
its base table's metatable was built, and #3590 is the same hazard on this method's **exception**
arm — it kept a refusal out of the cached null and deliberately left the "genuinely absent" arm
alone, which is a correct reading for one bundle and wrong for several.

### Where it is proven

`tests/runner-extras/metatable-cache-null-{first,second}`, run as two independent bundles by
`bc-tests.yml`'s *Run metatable-cache-null as two independent bundles* step. The two suites
declare no dependency on each other, which is what makes them sibling app groups rather than a
dependency pair.

That step exists because no other CI invocation can observe this: the combined
`tests/runner-extras` run builds **one** bundle, so there is no earlier bundle to do the
poisoning, and the `dep-tableext-platform-base` pair is a *dependency* pair whose dep loads as a
package rather than as an unrelated sibling.

<a id="multi-bundle-metatable-cache-app-registration"></a>

### The same absence, arriving through a dependency .app (#4783)

A source dir is not the only thing that makes an object exist. `BuildNCLMetaTable` also finds a
table a registered dependency `.app` declares, and the page, report and xmlport builders consult
the registered `.app` set the same way (`HasDependencyPageMetadata`, `KnownReportIdSet`,
`KnownXmlPortIdSet`). So bundle A asking about table 61600 before bundle B's precompiled
dependency is registered cached a `null` that `EvictCachedNullsForNewlyParsedTables` never looked
at: nothing was *parsed*, a `.app` was *registered*. B's own use of the table then raised
`NavMetadataNotFoundException`.

`EvictCachedNullsOnAppRegistration`, called at the end of `AddBcAppPath`, drops every `null`
entry of those four caches, process-wide and per app group. Every null rather than only the new
`.app`'s ids, because an absence that is still true costs one rebuild. Queries are left alone:
`BuildNCLMetaQuery` reads parsed source only. Proven by `bc-tests.yml`'s *Run
precompiled-dep-cache-null as ordered bundles* step and
`AlRunner.Tests/AppRegistrationEvictsCachedNullsTests.cs`.

<a id="populate-cost"></a>

## What a handout costs (#4851, #4859)

AllObj and AllObjWithCaption are populated into a runner store on every data-access handout, and
a handout happens each time AL opens a fresh record of either table. Microsoft's Test Runner
opens `Record AllObj` several times per test, so the corpus hands AllObj out thousands of times
per run. Until #4851 each handout walked the whole inventory — Base and System Application
included — only to insert nothing; AllObjWithCaption did the same until #4859.

Both tables share one mechanism, `RecordPatches.ObjectInventoryStore.cs`:

- **The rows**, per table and `ObjectInventoryKey` — the inventory stamp, the executing app group
  and the visible-app set. The rows are built once per key, as finished value arrays. A store gets
  a shallow copy of each array. The NavValues are immutable and shared, as BC's own
  `VirtualDataProvider.AddSystemFieldValues` shares its system values across virtual rows.
- **The top-up**, per store and key: a store that has already taken a key's rows is handed out
  without touching the inventory again.
- **The store itself, across a test-codeunit boundary** (#4859). `ResetPerTestState` drops every
  store, so each codeunit used to copy the rows in again. A store AL has not written to is now
  parked at the boundary, and the next codeunit's first handout takes it back when a fresh store
  would receive exactly the same rows. `IsReusableObjectInventoryStore` is that rule: the same
  metatable, filled only under the current stamp, already holding the current key's rows, and
  nothing else. The last clause matters because a store can hold rows from more than one
  visible-app set: a read made with another app's code on the call stack widens the set, and
  that store then holds rows a narrower read must not see. The row count tells them apart.
  Writes are seen through `NoteTransactionWriteForTable`, which every AL write entry point
  reaches before it writes; a written store is never parked. For Rename that entry point is
  `NavRecord.RenameAsync(DataError, bool, bool, NavValue[])`, the funnel every rename surface
  ends in — not `ALRenameAsync`, which the AL compiler's `ALRename` never calls (#4877).

The **stamp** (`ObjectInventoryStamp`) has one term per input the inventory, the visibility
filter, the owner index and AllObjWithCaption's captions read: the bundle and `.app`
registration epochs, the app-group generation, the module and enum registries, and the size of
each parsed-object and caption registry. A reload moves an epoch. Within one bundle the
registries only grow, which a count sees. The lazy `EnsureSystemEnumsRegistered` path moves the
stamp only for platform enums the symbols did not supply; a registration after a lookup was not
observed on a platform-only fixture (#4855 review), and the registry's mutation counter
(`AlEnumMetadataRegistry.Version`) is the term that would notice it. A dependency `.app` deleted
from disk mid-process is not a term: its rows stay until the next reload, as they did before
#4851. `ResetForReload` also drops the rows and the parked stores outright. **Trap:** a new
source read by `EnumerateKnownAlObjects`, `IsHiddenFromCurrentAppGroup`, `BuildObjectOwnerIndex`
or `SourceCaptionFor` needs a term in the stamp. Without one, the two tables stop listing what
that source adds until something else changes.

The tests read the `AL_RUNNER_PERF=1` lines `<table>.Handout`, `.InventoryWalk`, `.TopUp` and
`.Reuse`:

- `AlRunner.Tests/AllObjPopulateCostTests.cs`: one walk, one fill and one reuse per table across
  two codeunits of many handouts, on a cold and a warm run; a store AL wrote to is not carried
  into the next codeunit, for each of Record Insert/Modify/Delete/Rename, RecordRef
  Insert/Modify/Delete, ModifyAll and DeleteAll, with the leaking path named; a `--server`
  second request that renames objects answers with the new names.
- `AlRunner.Tests/RenameWriteNoteTests.cs`: a Rename inside `asserterror` is rolled back, and a
  Rename moves `Database.LastUsedRowVersion` (#4877).
- `AlRunner.Tests/ObjectInventoryStoreReuseRuleTests.cs`: each clause of the reuse rule,
  including the widened store no platform-only fixture can produce.
- `AlRunner.Tests/AllObjInventoryStampTests.cs`: a registered, replaced or extended enum moves the
  stamp.
