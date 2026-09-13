# BC symbol cache versions (`BcAppSymbolCache.CacheVersion`)

`AlRunner/Patches/BcAppSymbolCache.cs` persists what it parses out of each `.app`'s
`SymbolReference.json` under `~/.cache/al-runner/bc-symbols`. This page holds the history of
the `CacheVersion` integer that is part of that cache key. The rules for the next bump stay at
the constant; this page is the evidence behind them and the record of why each integer was
taken, which is what makes a collision diagnosable after the fact.

`AlRunner.Tests/BcAppSymbolCacheVersionHistoryTests.cs` holds this page to the code: the newest
entry below must equal the constant, and no integer from v3 up may be missing.

## The key and when to bump

The key is `path|hash:<content hash>|v<CacheVersion>|shape:<PayloadShape>`
(`BcAppSymbolCache.BuildKey`).

- **The shape changed** (a member was added to, removed from or retyped on anything reachable
  from `CachePayload`): `PayloadShape` (#2335) re-keys the cache on its own. A bump is not
  required, though several entries below bump anyway to state a parse change that came with it.
- **The parse changed but the shape did not** (the same fields now carry different values read
  out of unchanged bytes): the integer is the only thing that can tell the old payload from the
  new one. Without a bump, a warm box deserializes the old payload cleanly and replays the old
  answer. That is a wrong answer, not a cache miss, and CI never sees it because every leg
  provisions a fresh cache.
- **Two branches taking the same integer** read each other's entries, and it fails the same
  quiet way. v39 was first written as v38 and collided with #3791. Re-read the constant on
  `origin/main` immediately before pushing a bump; a rebase merges the text and cannot tell
  you the number is already taken.

## Version history

v1 and v2 predate this record.

- **v3**: added Queries to the parsed payload (generic NCLMetaQuery builder).

- **v4**: added Objects — the flat (kind, id, name) inventory that feeds the AllObj system virtual table (2000000038). See RecordPatches.AllObjVirtualTable.cs.

- **v5**: added Reports — caption / ProcessingOnly / UseRequestPage / data-item tree, feeding the Report Metadata (2000000139) and Report Data Items (2000000203) virtual tables. See RecordPatches.ReportMetadataVirtualTable.cs.

- **v6**: report data items now have their #appId# module qualifier stripped from RelatedTable. Any parse CHANGE needs a bump, not just a shape change — the on-disk payload is keyed on this, so a v5 cache written by the buggy parse stays valid and silently replays the old result.

- **v7**: Objects carry their Caption property, feeding the AllObjWithCaption system virtual table (2000000058). See RecordPatches.AllObjWithCaptionVirtualTable.cs.

- **v8**: Reports carry their per-data-item Columns and their ReferenceSourceFileName, which together let DependencyReportMetadata synthesize the runtime metadata XML a precompiled dependency's report ships no compiled form of.

- **v9**: ParsedTable gained LookupPageName / DrillDownPageName for the Table Metadata (2000000136) virtual table. A v8 payload deserialises cleanly with both null, so without this bump every cached dependency would report "declares no lookup page" for tables that plainly declare one — a silent wrong answer, not a cache miss.

- **v10**: added Pages — just Id/Name/SourceTable, feeding RecordPatches.TryGetDependencySourceTableIdForPage (issue #1719): a plain `Page X` variable over a precompiled dependency's page needs its SourceTable to bind Rec, and the runner's own AL-source page parser never sees a page it did not compile.

- **v11**: PageSymbol gained SourceTableTemporary. A v10 payload deserialises with it defaulted to false, so without this bump a temporary-source-table page (Page 700 "Error Messages") would silently get a NON-temporary Rec, and its own body's Rec.Copy(source, shareTable: true) would throw NavNCLArgumentException — a correctness regression, not a cache miss.

- **v12**: EnumSymbol gained Captions (issue #1775 — Format(<enum value>) on a dependency's enum must return the declared Caption, not the member name). A v11 payload deserialises with Captions null, which AlEnumOptionMetadata already treats as "no captions captured" (falls back to member name for every value) — silently wrong for any dependency enum whose Caption differs from its name, not a cache miss.

- **v13**: PageSymbol gained PageType/Caption/Editable/InsertAllowed/ModifyAllowed/DeleteAllowed/CardPageName and its field-control tree (Controls), feeding the "Page Metadata" (2000000138) and "Page Control Field" (2000000192) virtual tables for a page that lives in a precompiled dependency .app (issues #1769 / #1779). A v12 payload deserialises with PageType null / Controls empty / CardPageName null, which the Page Metadata provider would read as "declares no PageType" (defaults to Card, right only by coincidence) and "declares no CardPageId" (CardPageID = 0, which is exactly the value Base App "Page Management".GetDefaultCardPageID uses to decide a table has no card page at all — a real behavioral divergence, not a display nit), and the Page Control Field provider would read as "no controls" — silent wrong answers, not a cache miss, hence the bump.

- **v14**: QueryColumnSymbol gained Method (issue #2137 — a query column's Method = Sum/Count/Average/Min/Max property). A v13 payload deserialises with Method null, which RecordPatches.NclMetaQueryBuilder.AddColumn already treats as "no aggregation method declared" (skips setting FieldTotalingMethod, leaving AggregationType at its default None) — so a v13 cache entry would silently make ProjectQueryRows treat an aggregated column as an ordinary one again, returning raw ungrouped rows: the exact #2137 bug reintroduced on any machine whose symbol cache predates this change, not a cache miss, hence the bump.

- **v15**: AL's quoted identifiers inside a `filter(...)` are now re-quoted for BC's filter grammar, in BOTH a CalcFormula's where-condition and a report data item's DataItemTableView — `filter("Initial Entry")` is cached as `'Initial Entry'`, not as the AL text (issue #2305). A v14 payload deserialises perfectly well and replays the AL spelling, which reaches the runtime as a literal with double quotes in it, matches no option member, and throws NavInvalidFilterExpressionException out of CalcFields or out of the report's first Next() — a wrong answer replayed from cache on any machine whose symbol cache predates this change, not a cache miss.

- **v16**: EnumSymbol gained DefaultImplementations / UnknownImplementations, the enum-level fallbacks BC's NCLEnumMetadata.GetImplementationCodeunitId uses when a value declares no Implementation of its own (issue #2306). A v15 payload deserialises with both null, which reads as "the enum declares none" — so a cached dependency would keep failing every enum-to-interface cast with "Unable to cast enum ... to interface at index 0", the exact #2306 bug, rather than missing the cache.

- **v17**: AppSymbols gained Profiles / AppId / AppName, the rows of the "All Profile" (2000000178) virtual table and the declaring app each row is attributed to (issue #2317). A v16 payload deserialises with all three null, which reads as "this .app declares no profiles" — so a cached dependency would leave All Profile empty and every read of it would keep raising "There is no All Profile within the filter", the exact #2317 bug replayed from cache rather than a cache miss.

- **v18**: AppSymbols gained PermissionSets — the (owning app id, role id, caption, assignable) tuples the Metadata Permission Set (2000000250) virtual table serves (issue #2313). A v17 payload deserialises with the list null, which reads as "this app declares no permission sets" — so a cached System Application would keep answering `MetadataPermissionSet.Get(<null guid>, 'SUPER')` with "does not exist", the exact #2313 bug, rather than missing the cache.

- **v19**: TryParseQueryDataItem now strips the module qualifier off RelatedTable (issue #2295), same normalization CollectReportDataItems already applied to report dataitems. A v18 payload has the qualified `#<appId>#TableName` form baked into RelatedTable, which ResolveTableIdByName never matches — so a cached query over a dependency table would keep failing to build its NCLMetaQuery design and NRE on Open()/SetRange(), the exact #2295 bug replayed from cache rather than a cache miss.

- **v20**: added Reports' data-item DataItemLink / DataItemLinkReference / PrintOnlyIfDetail, without which a nested data item of a precompiled report has no join at all (#2436).

- **v21**: PageSymbol gained Parts — a precompiled dependency page's subpage PART controls (issue #2467), each with its raw (unresolved) SubPageLink text. A v20 payload deserialises with Parts empty, which DependencyPageMetadataXml would read as "this page has no parts" — every TestPage part on that page refusing out-of-scope again, silently reverting to the pre-fix behaviour rather than a cache miss.

- **v22**: ObjectSymbol gained TableNo / SingleInstance / Subtype for Codeunits — the columns the CodeUnit Metadata (2000000137) virtual table reports (issue #2544). A v21 payload deserialises with all three at their defaults, which reads as "every dependency codeunit declares no TableNo, is not SingleInstance, and is Subtype Normal" — a silent wrong answer for Base Application codeunits rather than a cache miss.

- **v23**: PageSymbol gained AutoSplitKey / MultipleNewLines / DelayedInsert — the three <SourceObject> flags the AL compiler writes alongside SourceTable, which DependencyPageMetadataXml was dropping (issue #2550). A v22 payload deserialises with all three false, which reads as "no dependency page uses AutoSplitKey" — and BC's client half of AutoSplitKey then silently does not run, so the first new row on such a page lands at line no. 0 and the second fails on a duplicate primary key. A wrong answer replayed from cache rather than a cache miss, which is why this needs the bump.

- **v24**: PageSymbol gained MemberIdToName / MemberIdToActionRefTarget and AppSymbols gained PageExtensions (issues #2723 / #2517) — the declared AL name of every action and control of a precompiled page (and pageextension), keyed by BC's own member id, which is what lets RunnerPageInstance.FindTrigger run its FORWARD (mangle-and-compare) match on a page the runner never AL-source-parsed. A v23 payload deserialises with both maps null, which RecordPatches.TryGetPageMemberName reads as "the dependency knows no members" — every spaced-name trigger on every Base Application page silently back on the lossy backward scan, the exact pre-fix behaviour replayed from cache rather than a cache miss.

- **v25**: ParsedTable gained TableTypeName (#2725). A v24 payload deserialises it as null, which reads as TableType = Normal — and a Base Application CRM table (e.g. 5341 "CRM Account") would then be served from a plain temp store instead of BC's own CrmTestDataProvider through the registered test connection. A wrong answer replayed from cache rather than a cache miss, so this needs the bump.

- **v26**: ParsedField gained RelationArms / RelationValidate (#2528) — a precompiled table's TableRelation, re-parsed from the SymbolReference.json property text. A v25 payload deserialises them as null/true, which reads as "this field has no relation": FieldRef.Relation answers 0 and Validate() accepts a value with no matching related row. That is a wrong ANSWER replayed from cache rather than a cache miss, so it needs the bump.

- **v27**: PermissionSetSymbol gained Permissions / IncludedPermissionSets / Access (#2910) — a v26 payload deserialises them as null, which reads as "this permission set grants nothing and includes nothing", so BC composes an empty set instead of the real one.

- **v28**: the SAME RelationArms field now carries MORE of what the SAME SymbolReference.json already said (#2518). Until this bump the parser refused any arm whose where(...) named a `field(...)` link and dropped the WHOLE relation, so 826 Base Application 28.1 relations — Customer.City among them — were cached as RelationArms = null. That deserialises as "this field declares no TableRelation": FieldRef.Relation answers 0, RapidStart's Relation Table ID stays 0, and Validate() skips the relation check. The schema did not change, so a v27 payload loads without error and replays the pre-fix wrong answer instead of missing — which is precisely what the bump is for.

- **v29**: the same shape once more, one level up in the name (#2851). RelationArms and CalcFormula now carry a NAMESPACE-QUALIFIED table name, which the parser refused for having 3+ parts (relation) or never resolved at all (CalcFormula) — 8 Base Application 28.1 relations cached as RelationArms = null and 4 FlowFields with a source table that matches nothing. Same reason for the bump as v28: the schema is unchanged, so a v28 payload loads WITHOUT error and replays those pre-fix wrong answers rather than missing.

- **v30**: TryParseTableSymbol now READS DataPerCompany instead of hardcoding true (#2938). ParsedTable also gained DataClassificationName / ExternalName in the same change, and those two are a shape change PayloadShape already keys on — but the DataPerCompany fix is not: it is the same schema parsed differently, so a v29 payload would load without error and replay the hardcoded true. That is 41 of Base Application 28.1's 1523 tables (the symbol file states AL's false as "0") handed to the Table Metadata (2000000136) DataPerCompany column, and to everything else reading ParsedTable, as per-company when they are global. A wrong answer replayed from cache rather than a cache miss — the exact case this integer exists for.

- **v31**: PageSymbol and PageExtensionSymbol gained MemberIdToRunObject (#2931) — the RunObject / RunPageOnRec an ACTION of a precompiled page declares. A v30 payload deserialises it as null, which RecordPatches.TryGetActionRunObject reads as "this action declares no RunObject" — and a TestPage that invokes one is then refused as declaring no effect at all, the exact pre-fix behaviour replayed from cache rather than a cache miss. That is a wrong ANSWER, so it needs the bump.

- **v32**: ActionRunObjectSymbol gained the PARSED RunPageLink and its declared entry count (#2942) — it used to record only that a link was PRESENT, which was enough to refuse the action and is not enough to apply it. Both halves of the rule above are true of this one at once. The record's SHAPE changed and ActionRunObjectSymbol is reachable from CachePayload, so PayloadShape already gives it a different key on its own; and the PARSE changed too, because the RunPageLink property text was read for presence only and is now read for content. The bump is the explicit statement of the second half, which no structural hash can see. Without the key changing, a stale payload would answer DeclaredRunPageLinkEntries = 0 — which RunnerPageInstance.ResolveRunTargetFromSymbols reads as "this action declares no link", opening the target on its WHOLE table.

- **v33**: two parse changes in this file that a structural hash cannot see, plus one record shape change that it can (#3267). #3248 taught SplitPropertyEntries to strip AL preprocessor directives and both link/view parsers to KEEP what they could not read, which changes the VALUES parsed out of unchanged bytes without changing any record's shape — a payload written before it replays the old, wider answer from a warm cache for as long as the .app's content hash holds. This change then fixed the action path's declared-entry count to use that same directive-aware splitter, which likewise changes a parsed value only. ActionRunObjectSymbol also gained UnreadableRunPageLinkEntries, and that half PayloadShape would have keyed on by itself; the bump is the explicit statement of the two halves it would not.

- **v34**: ReportDataItemSymbol gained MaxIteration (#3370) — a precompiled report data item's declared loop bound, which CollectReportDataItems never read. Both halves of the rule above are true at once, as they were for v32. The record's SHAPE changed and ReportDataItemSymbol is reachable from CachePayload, so PayloadShape already keys a fresh payload differently on its own; and the PARSE changed too, because a property that was never looked at now is. The bump states the second half, which no structural hash can see. Without a new key a stale payload answers MaxIteration = 0 for every data item — which BC's DataItemIterator reads as "no limit", so a `MaxIteration = 1` loop over the Integer virtual table runs to that table's end instead of once and the test never finishes. A wrong answer replayed from cache rather than a cache miss. Since #3485 that end is BC's own [-1000000000..1000000000] rather than a materialised window of 101,001 rows, so the loop no longer terminates in any useful time at all.

- **v35**: ParsedField gained Editable / DataClassificationName / EnumTypeId / EnumTypeName (#3545) — three properties the symbol file states on every field and this reader threw away. Both halves of the rule above hold at once. The record's SHAPE changed, so PayloadShape keys a fresh payload on its own; and the PARSE changed twice over, because DataClassificationName is stored EFFECTIVE rather than declared — a field silent about it takes its owner's, which is a different value read out of unchanged bytes. That second half is what needs the integer: it was measured here, where a payload written by an earlier build of this same change (declared-only, same shape) was replayed warm and put 154 of System Application's fields back on CustomerContent while the harness said the reader had been fixed. A wrong answer from cache, wearing a green build.

- **v36**: TryParseEnumSymbol stopped dropping an enum that declares no `Values` array (#3594). Same trap as v35's second half, and it bit the same way: the record SHAPE is unchanged — an EnumSymbol with an empty option list is shaped exactly like one with a full list — so PayloadShape cannot see that three enums per System Application payload went from absent to present. Measured warm on 28.1.49838.54308 before the bump: the harness still reported 15 MetaField.EnumTypeId differences naming enum 8889, from a payload written by the previous parse.

- **v37**: ParsedKey gained Clustered / Unique / SumIndexFieldIds and ParsedTable gained PrimaryKey (#3568) — the key name and properties the symbol file states and this reader discarded. The record shape changed, so PayloadShape would key a fresh payload on its own; the integer is here because the PRIMARY key changed meaning without changing shape. It used to reach the builder as a bare id list under a hardcoded "PK", and now carries its declared name, so a warm payload written by the previous parse replays keys that are shaped identically and named wrongly — the v35 trap exactly.

- **v38**: PageSymbol gained the PageProperties the symbol file states and EmitPageXml never read (#3784) — Extensible, RefreshOnActivate, UsageCategory, HelpLink, IsPreview, the four ML strings, the two inherent masks — plus three-state forms of InsertAllowed / ModifyAllowed / DeleteAllowed. Both halves of the v32 rule are true at once. The record SHAPE changed, which PayloadShape keys on by itself; the integer is here for the PARSE change it cannot see, which is the trio: they used to arrive as `bool` collapsing "the AL states nothing" into "the AL states the default", and a payload written by the previous parse replays that collapse from a warm cache — a wrong ANSWER (BC's emitter writes the attribute precisely when the AL states the property), not a cache miss.

- **v39**: a codeunit's SingleInstance is read with SymbolBool rather than a hand-rolled match on the word "true" (#3790). Same trap as v35, v36 and v38 a fourth time — the record SHAPE is unchanged, ObjectSymbol.SingleInstance is a bool either way, so PayloadShape cannot see that 38 of System Application 28.1's 533 codeunits went from false to true. Without the bump a warm box replays the old parse and CodeUnit Metadata keeps answering SingleInstance = false for every single-instance codeunit in a precompiled dependency.

  This one was written as v38 first and COLLIDED: #3791 took 38 for the page payload above while #3785 was in flight, and both were correct in isolation. That is the hazard this integer carries and ordinary code does not — when the shape is unchanged the integer is the ONLY discriminator, so two payload meanings sharing one number is not a bookkeeping slip but a warm box replaying the wrong parse, silently, on both. Re-read this constant on origin/main immediately before pushing a bump; a rebase resolves the text and cannot tell you the number is already taken.

- **v40**: PageSymbol gained MemberIdToDeclaredProperties (#2460) — the Enabled / Visible an ACTION of a precompiled page declares, which CollectMemberNames walked past. The record SHAPE changed and ActionDeclaredPropertiesSymbol is reachable from CachePayload, so PayloadShape keys a fresh payload on its own; the integer is here for the PARSE half, which is the same trap as v35, v36, v38 and v39. A v39 payload deserialises the new dictionary as null, which RecordPatches.TryGetDependencyActionDeclaredProperty reads as "this action declares neither" — EvaluateProperty's AL default of true, which is exactly the pre-fix wrong answer, replayed from a warm cache rather than missing. That is 1,129 Enabled and 1,101 Visible declarations across Base Application 28.1's 25,184 actions.

  40 was confirmed free immediately before pushing, per v39's own warning: origin/main read 39, and no open agent branch carried a value above 38.

- **v41**: an enum value stating no Ordinal is read as 0 rather than the previous ordinal plus one (#3805). The same trap as v35, v36, v38, v39 and v40, and v36 is this very method: EnumSymbol.Indexes is a List<int> either way, so PayloadShape cannot see that System Application 2616 "Printer Paper Kind" went from 67 distinct ordinals across 68 values to 68. Without the bump a warm box replays the old parse and hands out a DUPLICATE ordinal — TryGet's merge dedupes on ordinal, so the collision drops a value rather than mis-numbering it. Measured on 28.1 and 28.4: 681 and 684 values state no Ordinal, and none of the 3,649 (3,701) states one explicitly as zero.

  41 was confirmed free immediately before pushing, per v39's own warning: origin/main read 40, and a sweep of all 252 remote branches carrying this file found none above 40.

- **v42**: a permission set stating no Assignable is read as FALSE rather than true (#2417, #3806) — what BC's own MetaPermissionSet.Create answers, since it assigns the property only when the attribute is present. The same trap as v35, v36, v38, v39, v40 and v41: PermissionSetSymbol.Assignable is a bool either way, so PayloadShape cannot see that the VALUE changed, and a warm box would replay Assignable=true — the exact pre-fix wrong answer, from cache, on a green build. Measured across the real 28.1 .app symbol files: 436 permission sets, of which 3 state no Assignable — Base Application 208 "D365 Basic - Edit" and 209 "D365 Basic - Read" (a Properties array with no Assignable key) and System Application 68 "System Execute - Basic" (no Properties key at all).

  42 was confirmed free immediately before pushing, per v39's own warning: origin/main read 41, and a sweep of every remote branch carrying this file found none above 41.

- **v43**: a FlowFilter's or FlowField's TableRelation is read rather than dropped (#2789). The same trap as v35 through v42: ParsedField.RelationArms is a list either way, so PayloadShape cannot see that 196 FlowFilter and 8 FlowField fields of Base Application 28.1 (counted from its SymbolReference.json) went from no relation to one. Without the bump a warm box replays the gated parse and `FieldRef.Relation()` answers 0 on those fields, the exact pre-fix answer, from cache.

  43 was confirmed free immediately before pushing: origin/main read 42, and a sweep of every remote branch carrying this file found none above 42.

## Changes that deliberately did not bump

- No CacheVersion bump of its own for PageSymbol.TableView (#2820), deliberately — the numbered bumps above belong to other changes (v28 to #2518, v29 to #2973), and this one rides whatever the current integer is without moving it. That member is reachable from CachePayload, so PayloadShape (issue #2335, merged as #2856) already gives it a different cache key than any payload written without it — the stale-entry hazard every entry in the version history describes is closed by construction, and bumping as well would only be ceremony. CacheVersion means what RecordShapeFingerprint's own summary says it means: the PARSE changed while the SHAPE did not, which no structural hash can see — v28 and v29 are both exactly that case, and this change is the other one. Verified rather than assumed: a cold run of this build wrote fresh entries and a warm second run read them back, on the SHARED ~/.cache/al-runner/bc-symbols with no --cache isolation, and the precompiled-page corpus arm (Base App page 1710) passed in both.

- **#1820** (`CachePayload.ContentHash`): the key switched from length and mtime to a
  content hash, so an old payload can never be found under a new key. The change is to cache-key
  validation, not to what the parse extracts. The note stays at `CachePayload`.

- **#3809** (`PageExtensionSymbol.MemberIdToOrigin`): a shape change, so `PayloadShape` re-keyed
  it, measured as `ccb081fbed1589bf` -> `37a9f2f5cf79ffd1` with `CacheVersion` unchanged at 42.
  The note stays at the member.
