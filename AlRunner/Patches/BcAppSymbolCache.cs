using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

internal static partial class BcAppSymbolCache
{
    // v3: added Queries to the parsed payload (generic NCLMetaQuery builder).
    // v4: added Objects — the flat (kind, id, name) inventory that feeds the AllObj
    //     system virtual table (2000000038). See RecordPatches.AllObjVirtualTable.cs.
    // v5: added Reports — caption / ProcessingOnly / UseRequestPage / data-item tree,
    //     feeding the Report Metadata (2000000139) and Report Data Items (2000000203)
    //     virtual tables. See RecordPatches.ReportMetadataVirtualTable.cs.
    // v6: report data items now have their #appId# module qualifier stripped from
    //     RelatedTable. Any parse CHANGE needs a bump, not just a shape change — the
    //     on-disk payload is keyed on this, so a v5 cache written by the buggy parse
    //     stays valid and silently replays the old result.
    // v7: Objects carry their Caption property, feeding the AllObjWithCaption system
    //     virtual table (2000000058). See RecordPatches.AllObjWithCaptionVirtualTable.cs.
    // v8: Reports carry their per-data-item Columns and their ReferenceSourceFileName,
    //     which together let DependencyReportMetadata synthesize the runtime metadata XML
    //     a precompiled dependency's report ships no compiled form of.
    // v9: ParsedTable gained LookupPageName / DrillDownPageName for the Table Metadata
    // (2000000136) virtual table. A v8 payload deserialises cleanly with both null, so
    // without this bump every cached dependency would report "declares no lookup page"
    // for tables that plainly declare one — a silent wrong answer, not a cache miss.
    // v10: added Pages — just Id/Name/SourceTable, feeding
    // RecordPatches.TryGetDependencySourceTableIdForPage (issue #1719): a plain `Page X`
    // variable over a precompiled dependency's page needs its SourceTable to bind Rec, and
    // the runner's own AL-source page parser never sees a page it did not compile.
    // v11: PageSymbol gained SourceTableTemporary. A v10 payload deserialises with it
    // defaulted to false, so without this bump a temporary-source-table page (Page 700
    // "Error Messages") would silently get a NON-temporary Rec, and its own body's
    // Rec.Copy(source, shareTable: true) would throw NavNCLArgumentException — a correctness
    // regression, not a cache miss.
    // v12: EnumSymbol gained Captions (issue #1775 — Format(<enum value>) on a
    // dependency's enum must return the declared Caption, not the member name). A v11
    // payload deserialises with Captions null, which AlEnumOptionMetadata already treats
    // as "no captions captured" (falls back to member name for every value) — silently
    // wrong for any dependency enum whose Caption differs from its name, not a cache miss.
    // v13: PageSymbol gained PageType/Caption/Editable/InsertAllowed/ModifyAllowed/
    // DeleteAllowed/CardPageName and its field-control tree (Controls), feeding the "Page
    // Metadata" (2000000138) and "Page Control Field" (2000000192) virtual tables for a
    // page that lives in a precompiled dependency .app (issues #1769 / #1779). A v12
    // payload deserialises with PageType null / Controls empty / CardPageName null, which
    // the Page Metadata provider would read as "declares no PageType" (defaults to Card,
    // right only by coincidence) and "declares no CardPageId" (CardPageID = 0, which is
    // exactly the value Base App "Page Management".GetDefaultCardPageID uses to decide a
    // table has no card page at all — a real behavioral divergence, not a display nit),
    // and the Page Control Field provider would read as "no controls" — silent wrong
    // answers, not a cache miss, hence the bump.
    // v14: QueryColumnSymbol gained Method (issue #2137 — a query column's Method =
    // Sum/Count/Average/Min/Max property). A v13 payload deserialises with Method null,
    // which RecordPatches.NclMetaQueryBuilder.AddColumn already treats as "no aggregation
    // method declared" (skips setting FieldTotalingMethod, leaving AggregationType at its
    // default None) — so a v13 cache entry would silently make ProjectQueryRows treat an
    // aggregated column as an ordinary one again, returning raw ungrouped rows: the exact
    // #2137 bug reintroduced on any machine whose symbol cache predates this change, not a
    // cache miss, hence the bump.
    // v15: AL's quoted identifiers inside a `filter(...)` are now re-quoted for BC's filter
    // grammar, in BOTH a CalcFormula's where-condition and a report data item's
    // DataItemTableView — `filter("Initial Entry")` is cached as `'Initial Entry'`, not as
    // the AL text (issue #2305). A v14 payload deserialises perfectly well and replays the
    // AL spelling, which reaches the runtime as a literal with double quotes in it, matches
    // no option member, and throws NavInvalidFilterExpressionException out of CalcFields or
    // out of the report's first Next() — a wrong answer replayed from cache on any machine
    // whose symbol cache predates this change, not a cache miss.
    // v16: EnumSymbol gained DefaultImplementations / UnknownImplementations, the enum-level
    // fallbacks BC's NCLEnumMetadata.GetImplementationCodeunitId uses when a value declares no
    // Implementation of its own (issue #2306). A v15 payload deserialises with both null,
    // which reads as "the enum declares none" — so a cached dependency would keep failing
    // every enum-to-interface cast with "Unable to cast enum ... to interface at index 0",
    // the exact #2306 bug, rather than missing the cache.
    // v17: AppSymbols gained Profiles / AppId / AppName, the rows of the "All Profile"
    // (2000000178) virtual table and the declaring app each row is attributed to (issue
    // #2317). A v16 payload deserialises with all three null, which reads as "this .app
    // declares no profiles" — so a cached dependency would leave All Profile empty and
    // every read of it would keep raising "There is no All Profile within the filter",
    // the exact #2317 bug replayed from cache rather than a cache miss.
    // v18: AppSymbols gained PermissionSets — the (owning app id, role id, caption,
    // assignable) tuples the Metadata Permission Set (2000000250) virtual table serves
    // (issue #2313). A v17 payload deserialises with the list null, which reads as "this
    // app declares no permission sets" — so a cached System Application would keep
    // answering `MetadataPermissionSet.Get(<null guid>, 'SUPER')` with "does not exist",
    // the exact #2313 bug, rather than missing the cache.
    // v19: TryParseQueryDataItem now strips the module qualifier off RelatedTable (issue
    // #2295), same normalization CollectReportDataItems already applied to report dataitems.
    // A v18 payload has the qualified `#<appId>#TableName` form baked into RelatedTable, which
    // ResolveTableIdByName never matches — so a cached query over a dependency table would
    // keep failing to build its NCLMetaQuery design and NRE on Open()/SetRange(), the exact
    // #2295 bug replayed from cache rather than a cache miss.
    // v20: added Reports' data-item DataItemLink / DataItemLinkReference / PrintOnlyIfDetail,
    // without which a nested data item of a precompiled report has no join at all (#2436).
    // v21: PageSymbol gained Parts — a precompiled dependency page's subpage PART controls
    // (issue #2467), each with its raw (unresolved) SubPageLink text. A v20 payload
    // deserialises with Parts empty, which DependencyPageMetadataXml would read as "this
    // page has no parts" — every TestPage part on that page refusing out-of-scope again,
    // silently reverting to the pre-fix behaviour rather than a cache miss.
    // v22: ObjectSymbol gained TableNo / SingleInstance / Subtype for Codeunits — the columns
    // the CodeUnit Metadata (2000000137) virtual table reports (issue #2544). A v21 payload
    // deserialises with all three at their defaults, which reads as "every dependency codeunit
    // declares no TableNo, is not SingleInstance, and is Subtype Normal" — a silent wrong
    // answer for Base Application codeunits rather than a cache miss.
    // v23: PageSymbol gained AutoSplitKey / MultipleNewLines / DelayedInsert — the three
    // <SourceObject> flags the AL compiler writes alongside SourceTable, which
    // DependencyPageMetadataXml was dropping (issue #2550). A v22 payload deserialises with
    // all three false, which reads as "no dependency page uses AutoSplitKey" — and BC's
    // client half of AutoSplitKey then silently does not run, so the first new row on such a
    // page lands at line no. 0 and the second fails on a duplicate primary key. A wrong
    // answer replayed from cache rather than a cache miss, which is why this needs the bump.
    // v24: PageSymbol gained MemberIdToName / MemberIdToActionRefTarget and AppSymbols gained
    // PageExtensions (issues #2723 / #2517) — the declared AL name of every action and control
    // of a precompiled page (and pageextension), keyed by BC's own member id, which is what
    // lets RunnerPageInstance.FindTrigger run its FORWARD (mangle-and-compare) match on a page
    // the runner never AL-source-parsed. A v23 payload deserialises with both maps null,
    // which RecordPatches.TryGetPageMemberName reads as "the dependency knows no members" —
    // every spaced-name trigger on every Base Application page silently back on the lossy
    // backward scan, the exact pre-fix behaviour replayed from cache rather than a cache miss.
    // v25: ParsedTable gained TableTypeName (#2725). A v24 payload deserialises it as null,
    // which reads as TableType = Normal — and a Base Application CRM table (e.g. 5341 "CRM
    // Account") would then be served from a plain temp store instead of BC's own
    // CrmTestDataProvider through the registered test connection. A wrong answer replayed
    // from cache rather than a cache miss, so this needs the bump.
    // v26: ParsedField gained RelationArms / RelationValidate (#2528) — a precompiled table's
    // TableRelation, re-parsed from the SymbolReference.json property text. A v25 payload
    // deserialises them as null/true, which reads as "this field has no relation": FieldRef.Relation
    // answers 0 and Validate() accepts a value with no matching related row. That is a wrong ANSWER
    // replayed from cache rather than a cache miss, so it needs the bump.
    // v27: PermissionSetSymbol gained Permissions / IncludedPermissionSets / Access (#2910) —
    // a v26 payload deserialises them as null, which reads as "this permission set grants
    // nothing and includes nothing", so BC composes an empty set instead of the real one.
    // v28: the SAME RelationArms field now carries MORE of what the SAME SymbolReference.json
    // already said (#2518). Until this bump the parser refused any arm whose where(...) named
    // a `field(...)` link and dropped the WHOLE relation, so 826 Base Application 28.1
    // relations — Customer.City among them — were cached as RelationArms = null. That
    // deserialises as "this field declares no TableRelation": FieldRef.Relation answers 0,
    // RapidStart's Relation Table ID stays 0, and Validate() skips the relation check. The
    // schema did not change, so a v27 payload loads without error and replays the pre-fix
    // wrong answer instead of missing — which is precisely what the bump is for.
    // v29: the same shape once more, one level up in the name (#2851). RelationArms and
    // CalcFormula now carry a NAMESPACE-QUALIFIED table name, which the parser refused for
    // having 3+ parts (relation) or never resolved at all (CalcFormula) — 8 Base Application
    // 28.1 relations cached as RelationArms = null and 4 FlowFields with a source table that
    // matches nothing. Same reason for the bump as v28: the schema is unchanged, so a v28
    // payload loads WITHOUT error and replays those pre-fix wrong answers rather than missing.
    // v30: TryParseTableSymbol now READS DataPerCompany instead of hardcoding true (#2938).
    // ParsedTable also gained DataClassificationName / ExternalName in the same change, and
    // those two are a shape change PayloadShape already keys on — but the DataPerCompany fix
    // is not: it is the same schema parsed differently, so a v29 payload would load without
    // error and replay the hardcoded true. That is 41 of Base Application 28.1's 1523 tables
    // (the symbol file states AL's false as "0") handed to the Table Metadata (2000000136)
    // DataPerCompany column, and to everything else reading ParsedTable, as per-company when
    // they are global. A wrong answer replayed from cache rather than a cache miss — the exact
    // case this integer exists for.
    // No CacheVersion bump of its own for PageSymbol.TableView (#2820), deliberately — the
    // numbered bumps above belong to other changes (v28 to #2518, v29 to #2973), and this one
    // rides whatever the current integer is without moving it. That member is reachable from
    // CachePayload, so PayloadShape below (issue #2335, merged as #2856) already gives it a
    // different cache key than any payload written without it — the stale-entry hazard every
    // note above describes is closed by construction, and bumping as well would only be
    // ceremony. CacheVersion means what RecordShapeFingerprint's own summary says it means: the
    // PARSE changed while the SHAPE did not, which no structural hash can see — v28 and v29 are
    // both exactly that case, and this change is the other one. Verified rather than assumed: a
    // cold run of this build wrote fresh entries and a warm second run read them back, on the
    // SHARED ~/.cache/al-runner/bc-symbols with no --cache isolation, and the precompiled-page
    // corpus arm (Base App page 1710) passed in both.
    // v31: PageSymbol and PageExtensionSymbol gained MemberIdToRunObject (#2931) — the
    // RunObject / RunPageOnRec an ACTION of a precompiled page declares. A v30 payload
    // deserialises it as null, which RecordPatches.TryGetActionRunObject reads as "this action
    // declares no RunObject" — and a TestPage that invokes one is then refused as declaring no
    // effect at all, the exact pre-fix behaviour replayed from cache rather than a cache miss.
    // That is a wrong ANSWER, so it needs the bump.
    // v32: ActionRunObjectSymbol gained the PARSED RunPageLink and its declared entry count
    // (#2942) — it used to record only that a link was PRESENT, which was enough to refuse the
    // action and is not enough to apply it. Both halves of the rule above are true of this one
    // at once. The record's SHAPE changed and ActionRunObjectSymbol is reachable from
    // CachePayload, so PayloadShape already gives it a different key on its own; and the PARSE
    // changed too, because the RunPageLink property text was read for presence only and is now
    // read for content. The bump is the explicit statement of the second half, which no
    // structural hash can see. Without the key changing, a stale payload would answer
    // DeclaredRunPageLinkEntries = 0 — which RunnerPageInstance.ResolveRunTargetFromSymbols
    // reads as "this action declares no link", opening the target on its WHOLE table.
    // v33: two parse changes in this file that a structural hash cannot see, plus one record
    // shape change that it can (#3267). #3248 taught SplitPropertyEntries to strip AL
    // preprocessor directives and both link/view parsers to KEEP what they could not read,
    // which changes the VALUES parsed out of unchanged bytes without changing any record's
    // shape — a payload written before it replays the old, wider answer from a warm cache for
    // as long as the .app's content hash holds. This change then fixed the action path's
    // declared-entry count to use that same directive-aware splitter, which likewise changes a
    // parsed value only. ActionRunObjectSymbol also gained UnreadableRunPageLinkEntries, and
    // that half PayloadShape would have keyed on by itself; the bump is the explicit statement
    // of the two halves it would not.
    // v34: ReportDataItemSymbol gained MaxIteration (#3370) — a precompiled report data
    // item's declared loop bound, which CollectReportDataItems never read. Both halves of
    // the rule above are true at once, as they were for v32. The record's SHAPE changed and
    // ReportDataItemSymbol is reachable from CachePayload, so PayloadShape already keys a
    // fresh payload differently on its own; and the PARSE changed too, because a property
    // that was never looked at now is. The bump states the second half, which no structural
    // hash can see. Without a new key a stale payload answers MaxIteration = 0 for every
    // data item — which BC's DataItemIterator reads as "no limit", so a `MaxIteration = 1`
    // loop over the Integer virtual table runs to that table's end instead of once and the
    // test never finishes. A wrong answer replayed from cache rather than a cache miss.
    // Since #3485 that end is BC's own [-1000000000..1000000000] rather than a materialised
    // window of 101,001 rows, so the loop no longer terminates in any useful time at all.
    // v35: ParsedField gained Editable / DataClassificationName / EnumTypeId / EnumTypeName
    // (#3545) — three properties the symbol file states on every field and this reader threw
    // away. Both halves of the rule above hold at once. The record's SHAPE changed, so
    // PayloadShape keys a fresh payload on its own; and the PARSE changed twice over, because
    // DataClassificationName is stored EFFECTIVE rather than declared — a field silent about
    // it takes its owner's, which is a different value read out of unchanged bytes. That
    // second half is what needs the integer: it was measured here, where a payload written by
    // an earlier build of this same change (declared-only, same shape) was replayed warm and
    // put 154 of System Application's fields back on CustomerContent while the harness said
    // the reader had been fixed. A wrong answer from cache, wearing a green build.
    //
    // v36: TryParseEnumSymbol stopped dropping an enum that declares no `Values` array (#3594).
    // Same trap as v35's second half, and it bit the same way: the record SHAPE is unchanged —
    // an EnumSymbol with an empty option list is shaped exactly like one with a full list — so
    // PayloadShape cannot see that three enums per System Application payload went from absent
    // to present. Measured warm on 28.1.49838.54308 before the bump: the harness still reported
    // 15 MetaField.EnumTypeId differences naming enum 8889, from a payload written by the
    // previous parse.
    //
    // v37: ParsedKey gained Clustered / Unique / SumIndexFieldIds and ParsedTable gained
    // PrimaryKey (#3568) — the key name and properties the symbol file states and this reader
    // discarded. The record shape changed, so PayloadShape would key a fresh payload on its
    // own; the integer is here because the PRIMARY key changed meaning without changing shape.
    // It used to reach the builder as a bare id list under a hardcoded "PK", and now carries
    // its declared name, so a warm payload written by the previous parse replays keys that
    // are shaped identically and named wrongly — the v35 trap exactly.
    // v38: PageSymbol gained the PageProperties the symbol file states and EmitPageXml never
    // read (#3784) — Extensible, RefreshOnActivate, UsageCategory, HelpLink, IsPreview, the
    // four ML strings, the two inherent masks — plus three-state forms of InsertAllowed /
    // ModifyAllowed / DeleteAllowed. Both halves of the v32 rule are true at once. The record
    // SHAPE changed, which PayloadShape keys on by itself; the integer is here for the PARSE
    // change it cannot see, which is the trio: they used to arrive as `bool` collapsing
    // "the AL states nothing" into "the AL states the default", and a payload written by the
    // previous parse replays that collapse from a warm cache — a wrong ANSWER (BC's emitter
    // writes the attribute precisely when the AL states the property), not a cache miss.
    // v39: a codeunit's SingleInstance is read with SymbolBool rather than a hand-rolled match
    // on the word "true" (#3790). Same trap as v35, v36 and v38 a fourth time — the record
    // SHAPE is unchanged, ObjectSymbol.SingleInstance is a bool either way, so PayloadShape
    // cannot see that 38 of System Application 28.1's 533 codeunits went from false to true.
    // Without the bump a warm box replays the old parse and CodeUnit Metadata keeps answering
    // SingleInstance = false for every single-instance codeunit in a precompiled dependency.
    //
    // This one was written as v38 first and COLLIDED: #3791 took 38 for the page payload above
    // while #3785 was in flight, and both were correct in isolation. That is the hazard this
    // integer carries and ordinary code does not — when the shape is unchanged the integer is
    // the ONLY discriminator, so two payload meanings sharing one number is not a bookkeeping
    // slip but a warm box replaying the wrong parse, silently, on both. Re-read this constant
    // on origin/main immediately before pushing a bump; a rebase resolves the text and cannot
    // tell you the number is already taken.
    // v40: PageSymbol gained MemberIdToDeclaredProperties (#2460) — the Enabled / Visible an
    // ACTION of a precompiled page declares, which CollectMemberNames walked past. The record
    // SHAPE changed and ActionDeclaredPropertiesSymbol is reachable from CachePayload, so
    // PayloadShape keys a fresh payload on its own; the integer is here for the PARSE half,
    // which is the same trap as v35, v36, v38 and v39. A v39 payload deserialises the new
    // dictionary as null, which RecordPatches.TryGetDependencyActionDeclaredProperty reads as
    // "this action declares neither" — EvaluateProperty's AL default of true, which is exactly
    // the pre-fix wrong answer, replayed from a warm cache rather than missing. That is 1,129
    // Enabled and 1,101 Visible declarations across Base Application 28.1's 25,184 actions.
    //
    // 40 was confirmed free immediately before pushing, per v39's own warning: origin/main read
    // 39, and no open agent branch carried a value above 38.
    // v41: an enum value stating no Ordinal is read as 0 rather than the previous ordinal plus
    // one (#3805). The same trap as v35, v36, v38, v39 and v40, and v36 is this very method:
    // EnumSymbol.Indexes is a List<int> either way, so PayloadShape cannot see that System
    // Application 2616 "Printer Paper Kind" went from 67 distinct ordinals across 68 values to
    // 68. Without the bump a warm box replays the old parse and hands out a DUPLICATE ordinal —
    // TryGet's merge dedupes on ordinal, so the collision drops a value rather than mis-numbering
    // it. Measured on 28.1 and 28.4: 681 and 684 values state no Ordinal, and none of the 3,649
    // (3,701) states one explicitly as zero.
    //
    // 41 was confirmed free immediately before pushing, per v39's own warning: origin/main read
    // 40, and a sweep of all 252 remote branches carrying this file found none above 40.
    // v42: a permission set stating no Assignable is read as FALSE rather than true (#2417,
    // #3806) — what BC's own MetaPermissionSet.Create answers, since it assigns the property
    // only when the attribute is present. The same trap as v35, v36, v38, v39, v40 and v41:
    // PermissionSetSymbol.Assignable is a bool either way, so PayloadShape cannot see that the
    // VALUE changed, and a warm box would replay Assignable=true — the exact pre-fix wrong
    // answer, from cache, on a green build. Measured across the real 28.1 .app symbol files:
    // 436 permission sets, of which 3 state no Assignable — Base Application 208 "D365 Basic -
    // Edit" and 209 "D365 Basic - Read" (a Properties array with no Assignable key) and System
    // Application 68 "System Execute - Basic" (no Properties key at all).
    //
    // 42 was confirmed free immediately before pushing, per v39's own warning: origin/main read
    // 41, and a sweep of every remote branch carrying this file found none above 41.
    private const int CacheVersion = 42;
    private static readonly ConcurrentDictionary<string, AppSymbols> ProcessCache = new(StringComparer.OrdinalIgnoreCase);
    // Issue #1820's path -> content-hash memo now lives in
    // RunnerFingerprint._fileContentHashes (#2955), because AppLoader's persisted r2r-chunks
    // cache needs the same answer for the same packages on the same run and must not grow a
    // second memo — or a second hashing convention — to get it. The rationale is unchanged
    // and moved with it; ComputeAppContentHash below is still the name this layer calls.

    // Test-only instrumentation: counts genuine Parse() invocations (i.e. an on-disk cache
    // MISS that required reparsing SymbolReference.json), PER full .app path — not a single
    // global counter, because this project's xunit.runner.json runs test collections in
    // parallel (parallelizeTestCollections=true) and every BcAppSymbolCache*Tests class is
    // its own collection, all sharing this static type; a plain global counter would be
    // incremented by unrelated concurrent tests' own Get() calls, making a "before/after"
    // delta unreliable. Keying by path means a test using its own uniquely-named temp .app
    // observes only ITS OWN Parse() calls, immune to what any other collection is doing.
    // Exists so BcAppSymbolCacheContentAddressedKeyTests can assert "the second Get() call,
    // from a simulated fresh process, was a real disk HIT" deterministically — PerfTrace/
    // stderr capture was considered and rejected for the same parallel-collections reason
    // (Console.Error and environment variables are process-global).
    private static readonly ConcurrentDictionary<string, int> ParseInvocationCountByPath = new(StringComparer.OrdinalIgnoreCase);

    internal static int ParseInvocationCountForTests(string appPath)
        => ParseInvocationCountByPath.TryGetValue(Path.GetFullPath(appPath), out var count) ? count : 0;

    /// <summary>
    /// Clears the in-memory ProcessCache (and the content-hash memo) — for tests that
    /// simulate multiple independent process runs inside one xunit process (a real CI leg
    /// always starts with an empty ProcessCache). Never touches the on-disk cache, and never
    /// touches <see cref="ParseInvocationCountByPath"/> — that counter is meant to persist
    /// across a test's simulated "process restarts" so it can observe totals across them.
    /// </summary>
    internal static void ResetProcessCacheForTests()
    {
        ProcessCache.Clear();
        RunnerFingerprint.ClearFileContentHashMemoForTests();
    }

    internal sealed record AppSymbols(List<ParsedTable> Tables, List<EnumSymbol> Enums, List<QuerySymbol> Queries,
        List<ObjectSymbol> Objects, List<ReportSymbol> Reports, List<PageSymbol> Pages,
        // Profiles the .app declares, plus the app's own identity — both feed the
        // "All Profile" (2000000178) virtual table, whose rows are per-app and carry the
        // declaring app's id and name as columns of their own (#2317). AppId is also what
        // attributes a permission set to its owning app (#2313), so nothing here parses the
        // symbol reference's identity twice.
        List<ProfileSymbol>? Profiles = null, string? AppId = null, string? AppName = null,
        // Trailing + optional on purpose: every existing construction site keeps compiling
        // unchanged, and an older cache payload deserialises as null (read as "not stated",
        // never as "declares none" — see PermissionSetSymbol).
        List<PermissionSetSymbol>? PermissionSets = null,
        // Precompiled pageextensions with their member-name maps (#2723) — see
        // PageExtensionSymbol. Guarded by the v24 CacheVersion bump, so null only ever means
        // "this .app declares none", never "an older payload".
        List<PageExtensionSymbol>? PageExtensions = null);

    /// <summary>
    /// One profile as SymbolReference.json states it. <c>ProfileId</c> is the profile object's
    /// AL name, which is also what the platform uses as "All Profile"."Profile ID" (a Code[30]
    /// — e.g. Base Application's <c>ORDER PROCESSOR</c>).
    ///
    /// <para><c>RoleCenterPageName</c> is the <c>RoleCenter</c> property verbatim: a page NAME
    /// (<c>"Order Processor Role Center"</c>), not an id, so the consumer resolves it against
    /// the run's page inventory — the same shape as a page's CardPageId.</para>
    ///
    /// <para><c>Enabled</c> defaults to true and <c>Promoted</c> to false because that is AL's
    /// own default for a profile that declares neither, not a guess: 16 of the platform apps'
    /// 44 profiles state no <c>Enabled</c> property at all and every one of them is enabled on
    /// a real tier.</para>
    /// </summary>
    internal sealed record ProfileSymbol(
        string ProfileId, string? Caption, string? Description, string? RoleCenterPageName,
        bool Enabled, bool Promoted);

    /// <summary>
    /// One entry of a permission set's <c>Permissions</c> array, exactly as
    /// SymbolReference.json states it: <c>{ "PermissionObject": &lt;kind&gt;, "Id": &lt;object id&gt;,
    /// "Value": &lt;mask&gt; }</c>.
    ///
    /// <para><paramref name="ObjectType"/> is the SymbolReference <c>PermissionObject</c> ordinal.
    /// It maps ONTO BC's own <c>ObjectType</c> as an identity: measured on BC 28.1, CodeAnalysis's
    /// <c>PermissionObjectKind</c> and AL.Common's <c>ObjectType</c> agree on both ordinal AND name
    /// for every kind a permission set can name — TableData=0, Table=1, Report=3, Codeunit=5,
    /// Xmlport=6, Page=8, Query=9, System=10. A guessed mapping here would be a silent wrong
    /// answer no test could catch, so it was checked rather than assumed.</para>
    ///
    /// <para><c>PermissionObject</c> is ABSENT from the JSON when it is 0 (TableData) — the
    /// commonest case by far — so a reader that skips entries without it drops most of the data.
    /// Defaulted, not required.</para>
    ///
    /// <para><paramref name="Value"/> is BC's <c>PermissionMask</c>: Read=1, Insert=2, Modify=4,
    /// Delete=8, Execute=16, with the Indirect* variants at 32..512. Not decoded here — it is
    /// handed to BC's own composer untouched.</para>
    /// </summary>
    internal sealed record PermissionSymbol(int ObjectType, int ObjectId, int Value);

    /// <summary>
    /// One <c>permissionset</c> object a dependency .app declares, as its
    /// SymbolReference.json states it — three of the four columns of the Metadata Permission
    /// Set (2000000250) virtual table (issue #2313). The fourth, "App ID", comes from
    /// <see cref="AppSymbols.AppId"/>, since every permission set in one symbol reference
    /// belongs to the same app.
    ///
    /// <c>Caption</c> is null when the permission set declares no <c>Caption</c> property.
    /// <c>Assignable</c> mirrors the declared <c>Assignable</c> property; AL's own default
    /// for a permissionset that states none is <c>true</c>, which is what the parse applies.
    /// </summary>
    internal sealed record PermissionSetSymbol(
        int Id, string Name, string? Caption, bool Assignable,
        // #2910: the permission rows themselves, plus what the set includes and its access
        // modifier. BC composes the effective permissions from these (includes expansion,
        // exclusions, extension merge) — the runner only transcribes them.
        IReadOnlyList<PermissionSymbol>? Permissions = null,
        IReadOnlyList<string>? IncludedPermissionSets = null,
        string? Access = null,
        // #3609: the same two edge lists as already-resolved OBJECT IDS. SymbolReference.json
        // states include edges as NAMES (IncludedPermissionSets above) and states no exclude
        // edges at all; BC's own emitted metadata document states BOTH as ids. A symbol built
        // from a document fills these and leaves the name list null; one built from a
        // SymbolReference fills the name list and leaves these null. BuildIncludeList prefers
        // ids when present, because a name still has to survive PermissionSetIdByName's
        // lookup and an id does not.
        IReadOnlyList<int>? IncludedPermissionSetIds = null,
        IReadOnlyList<int>? ExcludedPermissionSetIds = null);

    /// <summary>
    /// A precompiled dependency's page, as far as SymbolReference.json states it — just
    /// enough to bind a plain page variable's Rec (issue #1719). <c>SourceTableId</c> is 0
    /// when the page declares no SourceTable (a legal AL page with no bound record).
    /// <c>SourceTableTemporary</c> matters for the SAME bind: Page 700 "Error Messages"
    /// declares <c>SourceTableTemporary = true</c>, and its own SetRecords body does
    /// <c>Rec.Copy(TempErrorMessage, true)</c> — real BC's Copy(shareTable: true) requires
    /// BOTH sides temporary, so a page whose SourceTable is declared temporary needs its
    /// bound Rec built temporary too, not just any record of the right table.
    /// </summary>
    internal sealed record PageSymbol(
        int Id, string Name, int SourceTableId, bool SourceTableTemporary,
        // Everything below feeds the "Page Metadata" (2000000138) virtual table (#1769).
        // AL defaults: PageType = Card (MS docs), Editable/InsertAllowed/ModifyAllowed/
        // DeleteAllowed = true, all only when the symbol file states nothing to the contrary.
        string PageType = "Card", string? Caption = null,
        bool Editable = true, bool InsertAllowed = true, bool ModifyAllowed = true, bool DeleteAllowed = true,
        // Field controls with a real SourceExpression, feeding "Page Control Field"
        // (2000000192) (#1779). Unlike the source-parsed path (Rec.-bound only, see
        // RecordPatches.AlPageParser.cs), the symbol file states EVERY field control's
        // SourceExpression verbatim, Rec.-bound or not — see TryParsePageSymbol.
        List<PageControlSymbol>? Controls = null,
        // AL's CardPageId property, stated by the symbol file as the target page's NAME
        // (verified: Base Application 28.1's "Customer List" carries
        // CardPageID = "Customer Card", not a numeric id) — resolved against the run's page
        // inventory at Page Metadata row-build time, same as the source-parsed path.
        string? CardPageName = null,
        // The three <SourceObject> flags the AL compiler writes alongside SourceTable, all
        // three defaulting to false in AL. Measured on Base Application 28.1's
        // SymbolReference.json: of its 2610 pages, 234 state AutoSplitKey, 116 state
        // MultipleNewLines and 303 state DelayedInsert, as "1"/"0" property values.
        bool AutoSplitKey = false, bool MultipleNewLines = false, bool DelayedInsert = false,
        // Subpage PART controls (issue #2467), feeding DependencyPageMetadataXml's
        // reconstructed <Content>. Unlike Controls above, a part's binding is resolved
        // entirely from THIS XML (RunnerPageInstance.TryGetPartDefinition reads
        // form.MetadataHelper.InfoPartDefinitions, built by BC's own metadata loader from
        // Content), not from IL — see TryParsePageSymbol / CollectPagePartSymbols.
        List<PagePartSymbol>? Parts = null,
        // Member id -> declared AL NAME of every action (any Kind: group, action, separator,
        // actionref, customaction, systemaction, fileuploadaction) and every control the
        // symbol file lists, in this page's own id space (issues #2723 / #2517). The id is
        // the file's own "Id" — BC's IdSpace.GetMemberId(pageId, name), the same number the
        // compiled test code hands LiveNavTestPage.GetAction/GetField — so nothing is
        // re-derived here. This is the declared-name source RunnerPageInstance.FindTrigger's
        // forward (mangle-and-compare) match needs; without it a precompiled page's members
        // only had the lossy backward un-mangle, which can never recover "Assign Serial No."
        // from Assign_Serial_Noa46_a45_OnAction. Mirrors ParsedPage.MemberIdToName for the
        // AL-source-parsed path (RecordPatches.ParseMemberNames, #1968).
        Dictionary<int, string>? MemberIdToName = null,
        // Member id of every Kind-4 actionref -> the NAME of the action it points at (the
        // file's own TargetName), mirroring ParsedPage.MemberIdToActionRefTarget (#2113).
        Dictionary<int, string>? MemberIdToActionRefTarget = null,
        // The page's SourceTableView, parsed out of the symbol file's own AL text (#2820).
        // Null when the page declares none. See ParseSourceTableView.
        PageTableViewSymbol? TableView = null,
        // Member id of every action that declares a RunObject -> what it declares (#2931).
        // See ActionRunObjectSymbol for why this is a NAME and not a resolved object id.
        Dictionary<int, ActionRunObjectSymbol>? MemberIdToRunObject = null,
        // The five further <SourceObject> properties the symbol file states (#2860), all
        // NULLABLE rather than defaulted, because for these five "the AL declares nothing"
        // and "the AL declares the default" are DIFFERENT documents and BC can tell them
        // apart. Measured on BC 28.1 by reading back the metadata the real AL compiler
        // captured for a page declaring each as its own AL default: it writes
        // LinksAllowed="1" PopulateAllFields="0" SaveValues="0" ShowFilter="1" — the
        // attribute is present precisely when the AL states the property, whatever the
        // value. The ShowFilter/SaveValues setters on BC's SourceObjectDefinition also raise
        // a Specified bit that Equals() compares and Freeze() clones, so collapsing the two
        // states is observable, not cosmetic.
        //
        // Counts on Base Application 28.1's own SymbolReference.json (2610 pages):
        // LinksAllowed 550 (548 "0", 2 "1"), DataCaptionFields 381, SaveValues 201
        // (196 "1", 5 "0"), ShowFilter 117 (115 "0", 2 "1"), PopulateAllFields 49
        // (46 "1", 3 "0"). 30 of those pages declare one of them with NO SourceTable at all.
        //
        // DataCaptionFields is a comma-separated list of FIELD NUMBERS, not names — the
        // symbol file and the compiled metadata share that representation (all 381 Base
        // Application values are numeric), so it needs no name resolution, only a shape
        // check. See RecordPatches.EmitSourceObjectPropertiesXml.
        bool? LinksAllowed = null, bool? ShowFilter = null, bool? SaveValues = null,
        bool? PopulateAllFields = null, string? DataCaptionFields = null,
        // #3784's PageProperties, all read straight off the same Properties array and none of
        // them derived. The three-state ones follow the #2860 rule above for the same reason:
        // BC's emitter writes the attribute precisely when the AL states the property, and its
        // reader's default differs from the value, so collapsing the two states is observable.
        //
        // Extensible and RefreshOnActivate are the exceptions and are BOOL, not bool?, because
        // BC's emitter writes them on ALL 236 pages measured whether the AL states them or not:
        // Extensible absent -> "1" (110 pages), RefreshOnActivate absent -> "0" (207). Their AL
        // defaults are therefore the whole answer for a silent page, so a third state would
        // encode a distinction the emitted document cannot carry.
        //
        // InsertAllowedStated / ModifyAllowedStated / DeleteAllowedStated sit ALONGSIDE the
        // bool trio further up rather than replacing it: the Page Metadata (2000000138) virtual
        // table wants the AL-defaulted answer (a page stating nothing IS insertable), while the
        // emitter wants to know whether the AL said so. Two questions, two fields, neither one
        // derivable from the other.
        //
        // InherentEntitlements / InherentPermissions are the AL permission LETTERS verbatim
        // ("X" on all 200 pages of Base + System + Business Foundation that state either);
        // decoding them to BC's Int32 mask is RecordPatches.EmitInherentMask's job, so nothing
        // is interpreted at parse time.
        bool Extensible = true, bool RefreshOnActivate = false,
        string? UsageCategory = null, string? HelpLink = null,
        string? AboutTitle = null, string? AboutText = null,
        string? AdditionalSearchTerms = null, string? InstructionalText = null,
        string? InherentEntitlements = null, string? InherentPermissions = null,
        bool IsPreview = false,
        bool? InsertAllowedStated = null, bool? ModifyAllowedStated = null,
        bool? DeleteAllowedStated = null, bool? DelayedInsertStated = null,
        bool? MultipleNewLinesStated = null,
        // Names of the booleans above the symbol file STATED but this could not read as a
        // boolean, with the value it stated ("PopulateAllFields=yes"). Null when there were
        // none, which is the case for every Microsoft-produced symbol file measured.
        //
        // It lives in the PAYLOAD rather than being written to stderr where it is detected,
        // and that is the whole point. Parsing sits behind a content-addressed on-disk cache,
        // so a Console.Error line written by the parser is emitted on a cache MISS and
        // silently lost on every warm run after — the failure mode AlPageMetadataRegistry's
        // header calls "the trap this whole class exists to avoid". Carrying it here means a
        // cache HIT replays it, which is also the truthful answer: the same bytes carry the
        // same unreadable value. RecordPatches.EmitSourceObjectPropertiesXml reports it.
        //
        // Why it is reported at all: an unreadable value and an absent property both produce
        // the same missing attribute, so absence cannot distinguish them, and silently
        // treating "I could not read this" as "the AL declares nothing" is the exact shape of
        // the defect #2860 is about, one level up.
        List<string>? UnreadableBooleanProperties = null,
        // Member id of every action declaring Enabled and/or Visible -> what it declares,
        // verbatim (issue #2460). The action-side twin of PageControlSymbol's EditableExpr /
        // VisibleExpr / EnabledExpr, and read off the SAME nodes CollectMemberNames already
        // walks for Name / TargetName / RunObject.
        //
        // Measured on Base Application 28.1.49838.53910: 25,184 actions over 2,610 pages, of
        // which 1,129 declare Enabled and 1,101 declare Visible — all of them answered true,
        // because the synthesized page metadata reconstructs no action tree for BC's own
        // ActionDefinition lookup to find. There is no Editable: AL does not give an action one.
        Dictionary<int, ActionDeclaredPropertiesSymbol>? MemberIdToDeclaredProperties = null);

    /// <summary>
    /// The <c>Enabled</c> / <c>Visible</c> one action DECLARES, exactly as the compiler wrote
    /// it — a literal, or the name of an expression the page's own IL registers. Null for
    /// either means the action declares that one not at all, which is AL's default of true.
    ///
    /// <para>Verbatim on purpose: resolving a page global needs live page state, which only
    /// <c>RunnerPageInstance.EvaluateProperty</c> has, and deciding literal-vs-expression here
    /// would fork a rule that already exists there.</para>
    /// </summary>
    internal sealed record ActionDeclaredPropertiesSymbol(string? Enabled, string? Visible);

    /// <summary>
    /// One action's <c>RunObject</c> declaration as SymbolReference.json states it.
    ///
    /// <para><c>ObjectName</c> is a bare object NAME with no type beside it — measured on Base
    /// Application 28.1, all 5,455 action <c>RunObject</c> property values are names
    /// (<c>"Purchase Statistics"</c>, <c>"Vendor Card"</c>), and not one states
    /// <c>Page</c>/<c>Report</c>/… even though the AL source does
    /// (<c>RunObject = Page "Purchase Statistics";</c>). The compiled page metadata BC itself
    /// loads DOES carry the resolved type and id (<c>ActionDefinition.RunObjectType</c> /
    /// <c>TargetID</c>), but an R2R .app ships no compiled metadata form of its objects, so for
    /// a precompiled page this is all there is and the consumer resolves the name against the
    /// run's own page inventory — see RunnerPageInstance.ResolveRunTargetFromSymbols.</para>
    ///
    /// <para><c>RunPageLink</c> is the action's link, parsed out of the same AL property text
    /// a part's <c>SubPageLink</c> comes in — the grammar is identical, so
    /// <c>ParseSubPageLink</c> reads both — and still unresolved: the field NAMES have to be
    /// turned into numbers against the TARGET's and the HOST's source tables, which needs the
    /// page inventory this layer does not have. RunnerPageInstance.ResolveRunTargetFromSymbols
    /// does that (issue #2942).</para>
    ///
    /// <para><c>DeclaredRunPageLinkEntries</c> is how many top-level entries the property text
    /// actually declared, which is NOT always <c>RunPageLink.Count</c>: an entry the parser does
    /// not understand is not applied. The consumer compares the two and refuses when they
    /// differ, because a link applied with one entry missing selects MORE rows than BC would —
    /// a silent wrong answer, not a smaller one. It is counted with the directive-aware
    /// splitter the parse itself uses, so a chunk that is nothing but an AL preprocessor
    /// directive is not miscounted as a declared entry (#3267).</para>
    ///
    /// <para><c>UnreadableRunPageLinkEntries</c> is the raw text of every entry
    /// <c>ParseSubPageLink</c> could NOT read, null when there were none. The count above
    /// already makes such a link refuse, so this does not change WHETHER it refuses — it
    /// makes the refusal name the text that could not be read instead of only a number, which
    /// is the difference between a message a developer can act on and one that sends them
    /// back to the symbol file. It rides in the PAYLOAD for the reason
    /// <see cref="PagePartSymbol.UnreadableSubPageLinkEntries"/> spells out: parsing sits
    /// behind a content-addressed on-disk cache, so anything written to stderr at parse time
    /// is emitted on a cache MISS and silently lost on every warm run after.</para>
    /// </summary>
    internal sealed record ActionRunObjectSymbol(
        string ObjectName,
        bool RunPageOnRec,
        int DeclaredRunPageLinkEntries,
        List<PageSubFormLinkSymbol>? RunPageLink = null,
        // Trailing + optional so the record still deserialises positionally; guarded by the
        // v33 CacheVersion bump, so null here only ever means "every entry was readable".
        List<string>? UnreadableRunPageLinkEntries = null)
    {
        internal bool HasRunPageLink => DeclaredRunPageLinkEntries > 0;
    }

    /// <summary>
    /// A precompiled dependency's <c>pageextension</c>, as far as SymbolReference.json states
    /// it (issue #2723's pageextension arm): its own object id, the NAME of the page it
    /// extends, and the same two member maps <see cref="PageSymbol"/> carries, in the
    /// EXTENSION's own id space — BC hashes a member an extension declares from the
    /// extension's object id, never the base page's (see RecordPatches.GetPageControlFieldMap).
    /// <para><c>TargetObjectName</c> is the file's <c>TargetObject</c> with any leading
    /// <c>#&lt;appid&gt;#</c> module qualifier stripped: Base Application 28.1 writes
    /// <c>"#63ca2fa4…#Accessible Companies"</c> for a System Application page and a bare
    /// <c>"Job Queue Entries"</c> for one of its own, and the runner resolves pages by NAME
    /// (RecordPatches.GetPageExtensionIdsForPage), never by app.</para>
    /// <para>Members come from <c>ActionChanges[].Actions</c> and <c>ControlChanges[].Controls</c>
    /// (recursively — an added group nests its actions), the two containers the compiler
    /// writes for <c>addfirst/addlast/addafter/addbefore</c>; a <c>modify(...)</c> change
    /// carries Properties only and contributes no member.</para>
    /// </summary>
    internal sealed record PageExtensionSymbol(
        int Id, string Name, string TargetObjectName,
        Dictionary<int, string> MemberIdToName,
        Dictionary<int, string> MemberIdToActionRefTarget,
        // Trailing + optional so a v26 payload still deserialises; guarded by the v27
        // CacheVersion bump, so null here only ever means "this extension declares none".
        Dictionary<int, ActionRunObjectSymbol>? MemberIdToRunObject = null,
        // Where each added member came from, which MemberIdToName loses: it merges
        // ActionChanges[].Actions and ControlChanges[].Controls into one map, and BC's emitted
        // runtime-delta document keeps them apart — an added action becomes <ActionAdd>, an
        // added control <ControlAdd>, each carrying the anchor and operation of the change that
        // declared it (#3809). Null means "this payload predates the field"; an id absent from
        // a non-null map was declared by neither container.
        //
        // No CacheVersion bump is needed for this, and that is MEASURED rather than assumed:
        // adding a member to a payload record changes PayloadShape, which is part of the cache
        // key, so the on-disk cache re-keys itself. ccb081fbed1589bf -> 37a9f2f5cf79ffd1, with
        // CacheVersion unchanged at 42.
        Dictionary<int, PageExtensionMemberOrigin>? MemberIdToOrigin = null);

    /// <summary>
    /// Where one member an AL <c>pageextension</c> adds came from, as SymbolReference.json
    /// states it — enough to place it in the runtime-delta document BC emits (#3809).
    /// </summary>
    /// <param name="IsAction">The declaring container: <c>ActionChanges[].Actions</c> (true) or
    /// <c>ControlChanges[].Controls</c> (false). Neither the member's id nor its name says
    /// which, and BC's document uses a different element for each.</param>
    /// <param name="Anchor">The change's <c>Anchor</c> verbatim — a member NAME, not an id, and
    /// null when the change states none. BC writes it as <c>AnchorName</c>, alongside an
    /// <c>AnchorId</c> the runner cannot derive.</param>
    /// <param name="ChangeKind">The change's AL <c>ChangeKind</c>. Measured against BC's own
    /// emitted documents for the five real pageextensions in the 28.1 bundles: 1 -&gt;
    /// <c>ContentFirst</c> (ext 324, 2515), 2 -&gt; <c>ContentLast</c> (ext 9862, 4318), 3 -&gt;
    /// <c>ContentBefore</c> (ext 774), 4 -&gt; <c>ContentAfter</c> (ext 2515) — i.e. AL's
    /// addfirst/addlast/addbefore/addafter. Kind 9 is a <c>modify(...)</c>, which adds no member
    /// and so never reaches this map.</param>
    /// <param name="Sequence">Zero-based position in AL declaration order, across both change
    /// containers. BC emits deltas in declaration order and the equivalence differ pairs them
    /// POSITIONALLY, so the order is load-bearing rather than cosmetic — sorting by member id
    /// instead reported pageextension 774's four controls as four wrong ids and names, and
    /// swapped two of ext 2515's change contexts (#3809). Stated explicitly because
    /// <c>Dictionary</c> does not guarantee insertion order.</param>
    internal sealed record PageExtensionMemberOrigin(bool IsAction, string? Anchor, int ChangeKind, int Sequence = 0);

    /// <summary>
    /// One subpage PART control of a precompiled dependency page, as SymbolReference.json
    /// states it. Identified not by a hardcoded Kind number but by the presence of a
    /// <c>RelatedPagePartId</c> element — verified against Base Application 28.1: all 1153
    /// controls carrying one are Kind 6, and no Kind-6 control lacks one, matching this
    /// file's existing convention of keying off a stated fact rather than an implementation
    /// detail (see CollectPageControlSymbols' Kind-8 comment for the same reasoning).
    /// <c>SubFormLink</c> is still raw AL text at this point — field-name -&gt; numeric-id
    /// resolution needs the PART's own table (a different page's SourceTable) alongside the
    /// HOST's, so it happens later in DependencyPageMetadataXml, which has both in scope.
    /// </summary>
    internal sealed record PagePartSymbol(
        int Id, string Name, int PagePartId,
        string? Caption, string? EditableExpr, string? EnabledExpr, string? VisibleExpr, string? ShowFilterExpr,
        List<PageSubFormLinkSymbol> SubFormLink,
        // The raw text of every SubPageLink entry ParseSubPageLink could NOT read (#2978).
        // Null when there were none, which is the case for all 1357 SubPageLink entries
        // across BC 28.1's Base Application, System Application, Business Foundation and
        // Application.
        //
        // It lives in the PAYLOAD rather than being written to stderr where it is detected,
        // for the reason PageSymbol.UnreadableBooleanProperties spells out: parsing sits
        // behind a content-addressed on-disk cache, so a Console.Error line is emitted on a
        // cache MISS and silently lost on every warm run after. Carrying it here means a
        // cache HIT replays it, which is also the truthful answer — the same bytes could not
        // be read either time.
        //
        // DependencyPageMetadataXml.EmitPartXml turns each one into a refusing SubFormLink
        // rather than dropping it: a two-condition link reduced to one filters the part LESS,
        // so the subpage shows rows the host row does not own.
        List<string>? UnreadableSubPageLinkEntries = null);

    /// <summary>
    /// One entry of a part's <c>SubPageLink</c> property, still as AL source text.
    /// <c>PartFieldName</c> is the part's own field (quotes stripped); <c>Kind</c> is
    /// "field"/"const"/"filter", exactly the AL keyword, lowercased; <c>Value</c> is
    /// everything inside the parens verbatim — a parent field name (quotes intact) for
    /// "field", the literal / expression text for "const"/"filter" (normalised to the
    /// compiled representation by RecordPatches.DependencyPageMetadataXml's
    /// EmitSubFormLinkXml, then applied by MockTestPage.SubPageLinks — #2469).
    /// </summary>
    internal sealed record PageSubFormLinkSymbol(string PartFieldName, string Kind, string Value,
        // True when this entry sits inside an AL `#if`/`#else` block in the property's source
        // text, so it MAY not be in the compiled app at all (#2978). See SplitPropertyEntries
        // for the measurement, and DependencyPageMetadataXml.EmitSubFormLinkXml for what the
        // difference buys: an unresolvable field name is a refusal for an unconditional entry
        // and an absence for a conditional one.
        bool Conditional = false);

    /// <summary>
    /// A precompiled dependency page's <c>SourceTableView</c>, parsed from the AL text
    /// SymbolReference.json records for it — <c>sorting(...) order(...) where(...)</c>, each
    /// clause optional. Field NAMES are still names here for the same reason
    /// <see cref="PageSubFormLinkSymbol"/> keeps them: resolving one to a field id needs the
    /// page's SourceTable, which DependencyPageMetadataXml has in scope and this parse does not.
    /// <para><c>Ascending</c> is null when the view states no <c>order(...)</c> — distinct from
    /// <c>true</c>, because BC's ApplySourceTableView only touches the record's ALAscending when
    /// the view SET it (<c>AscendingSetByView</c>).</para>
    /// </summary>
    internal sealed record PageTableViewSymbol(
        List<PageViewSortFieldSymbol> SortingFields, bool? Ascending, List<PageViewFilterSymbol> Filters,
        // The raw text of every where(...) entry — and every clause whose parenthesis never
        // closed — ParseSourceTableView could NOT read (#2978). Null when there were none,
        // which is the case for all 255 where-entries across the 417 SourceTableView pages of
        // BC 28.1's Base Application, System Application, Business Foundation and Application.
        //
        // Same payload-not-stderr reasoning as PagePartSymbol.UnreadableSubPageLinkEntries
        // above. DependencyPageMetadataXml.EmitSourceTableViewXml turns each one into a
        // refusing <TableFilters>, because a view shipped without one of its entries is WIDER
        // than the view the page declares: the page opens on rows the real view excludes, and
        // a test asserting over them passes against a record set BC would never give.
        List<string>? UnreadableEntries = null);

    /// <summary>
    /// One <c>where(...)</c> entry of a <c>SourceTableView</c>: the page's OWN source-table
    /// field, the AL keyword (<c>const</c> or <c>filter</c> — a view cannot use
    /// <c>field(...)</c>, which references a host record a page-level view has none of), and
    /// the value text verbatim.
    /// </summary>
    internal sealed record PageViewFilterSymbol(string FieldName, string Kind, string Value,
        // Same meaning as PageSubFormLinkSymbol.Conditional (#2978). No SourceTableView in BC
        // 27.5 or 28.1 W1 carries a directive today — all 547 where-entries are unconditional
        // — but the property text comes from the same compiler and through the same splitter,
        // so the two paths answer it the same way rather than one of them being surprised.
        bool Conditional = false);

    /// <summary>
    /// One <c>sorting(...)</c> field of a <c>SourceTableView</c>: the page's own source-table
    /// field name, in the order the view declares it.
    ///
    /// <para>This used to be a bare <c>string</c>, and the <c>sorting</c> arm used to be split
    /// by the bare <see cref="SplitTopLevelCommas"/> while the <c>where</c> arm beside it went
    /// through <see cref="SplitPropertyEntries"/> (#3271). The bare splitter does not strip the
    /// AL preprocessor directive LINES the compiler records verbatim inside a property's source
    /// text (#2978) — and, called without <c>preserveNewlines</c>, it flattens them into the
    /// entry beside them first. So <c>sorting(Bucket, #if not CLEAN25 "Zone Code", #endif
    /// "No.")</c> did not merely MISCOUNT: it produced the literal field names
    /// <c>#if not CLEAN25 "Zone Code</c> and <c>#endif "No.</c>, destroying a real name in
    /// the process.</para>
    ///
    /// <para><paramref name="Conditional"/> has the same meaning as on
    /// <see cref="PageViewFilterSymbol"/>: the entry sat inside an <c>#if</c> block, so nothing
    /// in the symbol file records whether the compiler kept it, and
    /// <c>DependencyPageMetadataXml.EmitSourceTableViewXml</c> resolves it against the app's own
    /// field inventory — applied when the name resolves, OMITTED when it does not.</para>
    ///
    /// <para>Omitting is a deliberate decision here and not an analogy to the filter arm, since
    /// a key is not a filter: dropping one field changes the ORDER rather than widening the
    /// rowset. It is still the right answer, because a guarded entry the app does not contain
    /// is not in the compiled page either — the compiled page really does declare the shorter
    /// key, so the shorter key is a reconstruction rather than an approximation. The
    /// alternative, refusing the whole key, leaves the page on the table's DEFAULT order, which
    /// is further from what BC does, not closer.</para>
    /// </summary>
    internal sealed record PageViewSortFieldSymbol(string FieldName, bool Conditional = false);

    /// <summary>
    /// One field control of a precompiled dependency page, as SymbolReference.json states
    /// it. <c>SourceExpression</c>/<c>Visible</c>/<c>Editable</c>/<c>Enabled</c> are the raw
    /// property text the compiler recorded — e.g. Base Application's Customer Card control
    /// "No." carries <c>Visible = "NoFieldVisible"</c> (a global Boolean variable name, not
    /// a literal), which is exactly what the real "Page Control Field" table's Text columns
    /// hold for a control whose Visible is driven by code rather than a constant.
    /// </summary>
    internal sealed record PageControlSymbol(
        int Id, string Name, string SourceExpression,
        string? VisibleExpr, string? EditableExpr, string? EnabledExpr, int Sequence);

    /// <summary>
    /// A precompiled dependency's report, as far as SymbolReference.json states it. Feeds
    /// the Report Metadata / Report Data Items virtual tables for reports the runner never
    /// compiles (Base Application, System Application, ISV apps).
    /// </summary>
    internal sealed record ReportSymbol(
        int Id, string Name, string? Caption, bool ProcessingOnly, bool UseRequestPage,
        string? WordMergeDataItem, List<ReportDataItemSymbol> DataItems,
        // Path of the report's AL source INSIDE the .app's embedded src/ tree, as the
        // symbol file states it. The runtime metadata synthesizer uses it to read back
        // that ONE file for the column source expressions the symbol file omits — see
        // DependencyReportMetadata.cs.
        string? ReferenceSourceFileName = null,
        // The Namespaces TREE PATH the report was reached through, not a property: the
        // symbol file states no such property, and Microsoft puts every report in the tree
        // — measured on 28.1.49838.53910, 660 of 660 reports across Base Application (659)
        // and System Application (1) sit under a namespace and both flat top-level Reports
        // arrays are empty (#3808).
        string? ALNamespace = null,
        // Carried as the AL mask LETTER string, decoded by the consumer through the shared
        // TryDecodePermissionMaskLetters — case-significant, so the value is NOT normalised
        // here. Measured on the report population specifically (same bundle): 1 of 660
        // reports states either mask, and it spells both "X". The decoder's indirect bits
        // are therefore unexercised on this kind, which is the reason to carry the letters
        // verbatim rather than rest on a population that happens not to need them.
        string? InherentEntitlements = null, string? InherentPermissions = null);

    /// <summary>One entry of a report's data-item tree, flattened in declaration order.</summary>
    internal sealed record ReportDataItemSymbol(
        int Id, string Name, string RelatedTable, int Indentation,
        string? DataItemTableView, string? RequestFilterFields,
        List<ReportColumnSymbol>? Columns = null,
        // The parent-child join. Without these two a nested data item has no restriction at
        // all and iterates its WHOLE table once per parent row — for report 411
        // "Vendor - Payment Receipt" that is hundreds of thousands of rows where BC produces
        // a handful. Invisible until a dataset was actually built from them (#2436).
        string? DataItemLink = null, string? DataItemLinkReference = null,
        bool PrintOnlyIfDetail = false,
        // The loop bound. BC treats 0 as NO LIMIT, so dropping a declared MaxIteration = 1
        // over the Integer virtual table runs the data item across that whole table instead of
        // once — every Number in [-1000000000..1000000000] since #3485 — which reads as a hang
        // (#3370).
        int MaxIteration = 0);

    /// <summary>
    /// One <c>column(Name; SourceExpr)</c> of a report data item, as SymbolReference.json
    /// states it. The symbol file carries the compiler-assigned <c>Id</c>, the column
    /// <c>Name</c> and its resolved <c>TypeName</c> — but NOT the source expression, which
    /// only the AL source has. That gap is why the synthesizer reads the report's own
    /// source file back out of the .app rather than inventing an expression.
    /// </summary>
    internal sealed record ReportColumnSymbol(int Id, string Name, string? TypeName);

    /// <summary>
    /// Flat (AL object kind, id, name, caption) tuple for one application object declared
    /// by a dependency .app. Read straight off the SymbolReference.json object arrays,
    /// which carry <c>Id</c> + <c>Name</c> for every kind — including the Codeunits /
    /// Pages / Reports / XmlPorts the typed parsing above deliberately ignores. Consumed
    /// by the AllObj (2000000038) and AllObjWithCaption (2000000058) virtual tables.
    ///
    /// <c>Caption</c> is null when the object declares no Caption property; AL's own
    /// default caption is then the object name, and applying that default is the
    /// consumer's job so the "not stated" and "stated as the name" cases stay distinct
    /// here.
    /// </summary>
    /// <remarks>
    /// <para>The three trailing properties are populated for <c>Codeunit</c> only, and feed the
    /// CodeUnit Metadata (2000000137) virtual table (issue #2544). Every other kind leaves them
    /// at their defaults — which is also what a codeunit declaring none of them means.
    /// <c>TableNo</c> is the reference AS WRITTEN (a bare id in text form, or a table name);
    /// resolving it to an id needs the run's table inventory, so the consumer does that.</para>
    /// <para><c>TargetObjectName</c> is populated for the *extension kinds only — the object
    /// the extension extends, module qualifier stripped, name AS STATED. AllObjWithCaption's
    /// Object Subtype reports its ID for those kinds; see
    /// docs/virtual-tables-allobj.md#object-subtype.</para>
    /// <para><c>ALNamespace</c> is the dotted path of the <c>Namespaces</c> tree the object was
    /// reached through, or null for one reached through the flat top-level array. NOT a property
    /// the symbol file states — it is the walk itself, which is why it is passed down rather
    /// than read out of a bag. <c>InherentEntitlements</c> / <c>InherentPermissions</c> ARE
    /// stated, as the AL mask LETTER string; see
    /// docs/codeunit-metadata-from-bc.md#permission-mask-spelling for the case rule, which the
    /// consumer decodes. All three are populated for <c>Codeunit</c> only (#3788).</para>
    /// </remarks>
    internal sealed record ObjectSymbol(string Kind, int Id, string Name, string? Caption = null,
        string? TableNo = null, bool SingleInstance = false, string? Subtype = null,
        string? TargetObjectName = null, string? ALNamespace = null,
        string? InherentEntitlements = null, string? InherentPermissions = null);

    // SymbolReference.json container name → the AllObj "Object Type" option name the
    // objects inside it map to. Matched against the live option string by name, so a
    // container whose kind this BC version's AllObj does not list is simply dropped.
    private static readonly (string Container, string Kind)[] ObjectContainers =
    {
        ("Tables", "Table"),
        ("Codeunits", "Codeunit"),
        ("Pages", "Page"),
        ("Reports", "Report"),
        ("XmlPorts", "XMLport"),
        ("Queries", "Query"),
        ("EnumTypes", "Enum"),
        ("TableExtensions", "TableExtension"),
        ("PageExtensions", "PageExtension"),
        ("ReportExtensions", "ReportExtension"),
        ("EnumExtensionTypes", "EnumExtension"),
        ("PermissionSets", "PermissionSet"),
        ("PermissionSetExtensions", "PermissionSetExtension"),
    };
    // Captions[i] is value i's declared Caption text, or null when it declares none
    // (issue #1775 — Format(<enum value>) must return the Caption, not the member
    // name, for enums coming from a prebuilt dependency .app too, not just enums
    // compiled from this bundle's own source).
    // DefaultImplementations / UnknownImplementations are the ENUM-level fallbacks, one list
    // per enum (indexed by interface-declaration index), not one per value — see issue #2306
    // and AlEnumOptionMetadata.GetImplementationCodeunitIdPublic.
    // Extensible is the AL `Extensible` property (#3807), read from the same Properties bag as
    // DefaultImplementation. NULLABLE, because "declares false" and "declares nothing" are
    // states BC's own emitter keeps apart: of 144 emitted enum documents in 28.1 and 28.4, 31
    // state "1", 99 state "0" and 12 base enums state the attribute NOT AT ALL — the same 12
    // whose SymbolReference.json omits the property. Collapsing null to false would make the
    // render state an attribute BC leaves off, which is a manufactured difference in the
    // direction this derivation exists to avoid.
    internal sealed record EnumSymbol(int Id, string Name, List<string> Options, List<int> Indexes, List<List<int>> Implementations, List<string?>? Captions = null,
        List<int>? DefaultImplementations = null, List<int>? UnknownImplementations = null,
        bool? Extensible = null);

    // Parsed query SymbolReference.json shape. A query is a tree of dataitems; the root
    // dataitem(s) live under the query's "Elements", nested dataitems under "DataItems".
    // Column/Filter Id is the BC-compiler-assigned column id baked into precompiled callers
    // (NavQuery.ValidateExpectedType(columnId,...)/GetColumnValueSafe) — it MUST be used verbatim.
    // InherentEntitlements / InherentPermissions (#3798) are carried as the AL mask LETTER
    // string, decoded by the consumer — case-significant, for the reason the codeunit sweep in
    // VisitSymbolContainer states. Measured on the query population specifically (BC
    // 28.4.53241.54407, Base Application + System Application): 6 of 161 queries state a mask
    // and all 6 spell it "X". No lowercase form occurs on this kind, so the decoder's indirect
    // bits are unexercised here — which is why the value is carried verbatim rather than
    // normalised, instead of resting on a population that happens not to need it.
    internal sealed record QuerySymbol(
        int Id, string Name, string? QueryType, string? Caption, string? OrderBy,
        int TopNumberOfRowsToReturn, List<QueryDataItemSymbol> DataItems,
        string? InherentEntitlements = null, string? InherentPermissions = null);

    // DataItemTableFilter (#3571) is the AL `DataItemTableFilter = <Field> = const(...)/
    // filter(...) [, ...]` property, carried verbatim ("Status = const(Open)"). It restricts the
    // dataitem's own table rows and needs no projected query column, so it is a different input
    // from ColumnFilter (#2418) and lands in a different place: MetaQueryDataItem.FieldFilters,
    // which BC's NCLMetaQuery.CreateTableFiltersAndMarksFromDataItemFieldFilters turns into the
    // dataitem's TableFiltersAndMarks. Parsed by RecordPatches.TryParseColumnFilterText, which
    // this property shares a grammar with.
    internal sealed record QueryDataItemSymbol(
        int Id, string Name, string RelatedTable, string? SqlJoinType, string? DataItemLink,
        List<QueryColumnSymbol> Columns, List<QueryColumnSymbol> Filters,
        List<QueryDataItemSymbol> DataItems, string? DataItemTableFilter = null);

    // SourceColumn is the field NAME on RelatedTable; Id is the BC column id; Caption optional.
    // Method (issue #2137) is the AL `Method = Sum/Count/Average/Min/Max` property, carried
    // verbatim from the column's Properties bag — "Sum"/"Count"/"Average"/"Min"/"Max", or null
    // for an unaggregated column. Names match Microsoft.Dynamics.Nav.Types.AggregationType's
    // member names exactly, so RecordPatches.NclMetaQueryBuilder.AddColumn can hand it straight
    // to SetProp's Enum.Parse without translation.
    // ColumnFilter (#2418) is the AL `ColumnFilter = <Field> = const(...)/filter(...) [, ...]`
    // property, carried verbatim ("AssignedQuantity = filter(> 0)") — parsed by
    // RecordPatches.TryParseColumnFilterText in RecordPatches.NclMetaQueryBuilder.BuildMetaQueryDesign
    // once every column's id is known (a ColumnFilter condition may name any query column of
    // the same dataitem, not just the column that declares the property).
    // ReverseSign (#2575) is the AL `ReverseSign = true;` boolean property — negates the
    // column's value (RecordPatches.NclMetaQueryBuilder.AddColumn sets the design metadata's
    // ReverseSign so NCLMetaQueryColumn.CreateFromDesignMetadata carries it through; the actual
    // negation happens where the value is produced, in RecordPatches.QueryProjection.cs /
    // AlRunner.QueryJoin.JoinExecutor).
    internal sealed record QueryColumnSymbol(int Id, string Name, string SourceColumn, string? Caption, string? Method, string? ColumnFilter, bool ReverseSign);

    // #1820: ContentHash replaces Length/LastWriteUtcTicks. The KEY (below, in Get) already
    // switched from mtime to a content hash, so an old Length/LastWriteUtcTicks payload can
    // never be found under a new key anyway (different key string -> different SHA-256 ->
    // different on-disk filename, see CachePath) — no CacheVersion bump needed, this changes
    // cache-key VALIDATION, not what Parse extracts from SymbolReference.json.
    private sealed record CachePayload(string ContentHash,
        List<ParsedTable> Tables, List<EnumSymbol> Enums, List<QuerySymbol> Queries,
        List<ObjectSymbol>? Objects, List<ReportSymbol>? Reports, List<PageSymbol>? Pages,
        List<ProfileSymbol>? Profiles = null, string? AppId = null, string? AppName = null,
        List<PermissionSetSymbol>? PermissionSets = null,
        List<PageExtensionSymbol>? PageExtensions = null);

    /// <summary>
    /// Parse a loose <c>SymbolReference.json</c> file (the raw module JSON, NOT a .app
    /// zip) into <see cref="AppSymbols"/>. Used for the bundle's own freshly-compiled
    /// query symbols, written by <c>BcCompiler.Emit</c>. Mirrors <see cref="Parse"/> but
    /// reads the JSON directly. No on-disk cache: the file is overwritten every run, and
    /// parsing a single small module is cheap.
    /// </summary>
    internal static AppSymbols GetFromJson(string jsonPath)
    {
        var tables = new Dictionary<int, ParsedTable>();
        var enums = new Dictionary<int, EnumSymbol>();
        var queries = new Dictionary<int, QuerySymbol>();
        var objects = new Dictionary<(string, int), ObjectSymbol>();
        var reports = new Dictionary<int, ReportSymbol>();
        var pages = new Dictionary<int, PageSymbol>();
        var pageExtensions = new Dictionary<int, PageExtensionSymbol>();
        var profiles = new Dictionary<string, ProfileSymbol>(StringComparer.OrdinalIgnoreCase);
        var permissionSets = new Dictionary<int, PermissionSetSymbol>();
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        VisitSymbolContainer(doc.RootElement, tables, enums, queries, objects, reports, pages, profiles, pageExtensions);
        CollectPermissionSets(doc.RootElement, permissionSets);
        var (appId, appName) = ReadAppIdentity(doc.RootElement);
        return new AppSymbols(tables.Values.ToList(), enums.Values.ToList(), queries.Values.ToList(),
            objects.Values.ToList(), reports.Values.ToList(), pages.Values.ToList(),
            profiles.Values.ToList(), appId, appName, permissionSets.Values.ToList(),
            pageExtensions.Values.ToList());
    }

    /// <summary>
    /// A hash of CachePayload's STRUCTURE — every member name and type reachable from it —
    /// computed once per process (issue #2335).
    ///
    /// <para>CacheVersion alone cannot tell two concurrent branches apart. bc-symbols is shared
    /// by every worktree of this repository, so when two branches each add a field and each bump
    /// the same integer, they read each other's entries — and it does not fail as a
    /// deserialization error, it fails as a payload that deserializes CLEANLY with the other
    /// branch's fields defaulted to null. That is the "wrong answer replayed from cache, not a
    /// cache miss" the version comments above already warn about, reproduced across branches
    /// instead of across time. It cost an agent about an hour once, and on 2026-09-05 two
    /// branches reached for the same next integer within hours of each other.</para>
    ///
    /// <para>The fingerprint cannot drift because nobody maintains it. CacheVersion stays, and
    /// keeps the job only a human can do: saying that the PARSE changed while the shape did
    /// not — new values in the same fields, which no structural hash can see.</para>
    /// </summary>
    private static readonly string PayloadShape =
        AlRunner.Infrastructure.RecordShapeFingerprint.Of(typeof(CachePayload));

    /// <summary>
    /// The one place the cache key is spelled. Get() and the test seam below both call it, so a
    /// test can never compute A key that is not the key Get() consults — the drift the
    /// CachePathForVersionForTests comment already argues against, closed by construction
    /// rather than by documentation.
    /// </summary>
    /// <param name="payloadShape">Defaults to the live fingerprint, which is what Get() passes.
    /// A test overrides it to reach the path an OLDER payload SHAPE wrote to — the only way to
    /// prove the fingerprint is load-bearing, since the key is hashed into a filename and a
    /// shape change is otherwise invisible from outside (#3788).</param>
    private static string BuildKey(string fullPath, string contentHash, int cacheVersion,
                                   string? payloadShape = null) =>
        $"{fullPath}|hash:{contentHash}|v{cacheVersion}|shape:{payloadShape ?? PayloadShape}";

    internal static AppSymbols Get(string appPath)
    {
        var fullPath = Path.GetFullPath(appPath);
        var contentHash = ComputeAppContentHash(fullPath);
        var key = BuildKey(fullPath, contentHash, CacheVersion);
        if (ProcessCache.TryGetValue(key, out var cachedInProcess))
            return cachedInProcess;

        var sw = Stopwatch.StartNew();
        var cachePath = CachePath(key);
        var cached = TryRead(cachePath, contentHash);
        if (cached != null)
        {
            PerfTrace.Log($"bc-symbols HIT {Path.GetFileName(appPath)} tables={cached.Tables.Count} enums={cached.Enums.Count} queries={cached.Queries.Count} {sw.ElapsedMilliseconds}ms");
            ProcessCache[key] = cached;
            return cached;
        }

        ParseInvocationCountByPath.AddOrUpdate(fullPath, 1, static (_, count) => count + 1);
        var parsed = Parse(appPath);
        TryWrite(cachePath, contentHash, parsed);
        PerfTrace.Log($"bc-symbols MISS {Path.GetFileName(appPath)} tables={parsed.Tables.Count} enums={parsed.Enums.Count} queries={parsed.Queries.Count} {sw.ElapsedMilliseconds}ms");
        ProcessCache[key] = parsed;
        return parsed;
    }

    // Test seam: mirrors Get()'s own key-string format and delegates to the SAME private
    // CachePath hashing formula, so a test that needs to know where an OLDER (or a
    // deliberately different) CacheVersion's entry lives on disk never duplicates that
    // formula itself. A hand-rolled copy of CachePath in a test would silently stop
    // testing anything the moment CachePath's hashing/layout changes — the copy would
    // still compute A path, just not the one Get() actually consults, and the test would
    // pass for the wrong reason (see BcAppSymbolCacheQueryMethodVersionTests, added for
    // issue #2137's CacheVersion bump, where exactly this drift risk was caught in
    // review). Exposing this one seam instead makes that drift impossible rather than
    // merely documented.
    internal static string CachePathForVersionForTests(string appPath, string contentHash, int cacheVersion)
        => CachePath(BuildKey(Path.GetFullPath(appPath), contentHash, cacheVersion));

    /// <summary>
    /// The same seam for the OTHER half of the key. Where a CacheVersion bump is the
    /// discriminator for a parse-only change, the payload-shape fingerprint is the one for a
    /// record-shape change — and a test proving a stale entry is not served needs the path that
    /// entry actually lives at (#3788).
    /// </summary>
    internal static string CachePathForShapeForTests(string appPath, string contentHash, int cacheVersion, string payloadShape)
        => CachePath(BuildKey(Path.GetFullPath(appPath), contentHash, cacheVersion, payloadShape));

    /// <summary>The current payload-shape fingerprint, for a test that needs to prove the key
    /// actually carries it and that it changes when the shape does.</summary>
    internal static string PayloadShapeForTests => PayloadShape;

    /// <summary>
    /// The key itself, for the one assertion that cannot be made through the path: the path is a
    /// hash, so "the key carries the shape" is invisible from outside. Exposing the key makes the
    /// wiring directly assertable instead of inferred from two paths differing.
    /// </summary>
    internal static string BuildKeyForTests(string appPath, string contentHash, int cacheVersion)
        => BuildKey(Path.GetFullPath(appPath), contentHash, cacheVersion);

    // The CURRENT CacheVersion, for a test that wants to prove a fresh entry landed at
    // exactly the path Get() would consult, without hardcoding the number (which would
    // then need updating every time an unrelated future field bump moves it).
    internal static int CacheVersionForTests => CacheVersion;

    /// <summary>
    /// Content hash (hex SHA-256) of a .app file's bytes — the cache-key component that
    /// replaced FileInfo.Length/LastWriteTimeUtc (#1820, same defect family as #1815: CI
    /// re-downloads every platform/test-toolkit .app on every run, so LastWriteTimeUtc is
    /// fresh even when the bytes are byte-for-byte identical to a prior run's, and an
    /// mtime-keyed entry persisted across CI runs would MISS unconditionally regardless of
    /// content). Delegates to RunnerFingerprint.ComputeContentHash — the same
    /// content-hash-of-bytes helper #1817 introduced for the AL-output/source-dep caches —
    /// rather than a second hashing convention; that helper already handles the
    /// missing-file "unknown" sentinel generically.
    ///
    /// Memoized per full path by <see cref="RunnerFingerprint.ComputeFileContentHashMemoized"/>
    /// — see that memo's comment for why a per-Get()-call hash would be a regression, not
    /// just correct, and for why the memo is shared with AppLoader rather than private here.
    /// </summary>
    internal static string ComputeAppContentHash(string appPath)
        => RunnerFingerprint.ComputeFileContentHashMemoized(appPath);

    private static AppSymbols? TryRead(string cachePath, string contentHash)
    {
        if (!File.Exists(cachePath)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<CachePayload>(File.ReadAllText(cachePath));
            if (payload == null || payload.ContentHash != contentHash)
                return null;
            return new AppSymbols(payload.Tables, payload.Enums, payload.Queries ?? new List<QuerySymbol>(),
                payload.Objects ?? new List<ObjectSymbol>(), payload.Reports ?? new List<ReportSymbol>(),
                payload.Pages ?? new List<PageSymbol>(),
                payload.Profiles ?? new List<ProfileSymbol>(), payload.AppId, payload.AppName,
                payload.PermissionSets ?? new List<PermissionSetSymbol>(),
                payload.PageExtensions ?? new List<PageExtensionSymbol>());
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"bc-symbols cache read failed {Path.GetFileName(cachePath)}: {ex.Message}");
            return null;
        }
    }

    // internal (not private): AlRunner.Tests exercises this directly to pin the
    // atomic-publish contract at this specific call site — see
    // BcAppSymbolCacheAtomicWriteTests.cs.
    internal static void TryWrite(string cachePath, string contentHash, AppSymbols symbols)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var payload = new CachePayload(contentHash, symbols.Tables, symbols.Enums, symbols.Queries, symbols.Objects, symbols.Reports, symbols.Pages, symbols.Profiles, symbols.AppId, symbols.AppName, symbols.PermissionSets, symbols.PageExtensions);
            // #1809 follow-up: cachePath is content-keyed (hash of the .app file),
            // so two subprocesses parsing the same app concurrently used to race a
            // plain File.WriteAllText into the same path. TryRead already treats any
            // exception (including a partial-JSON parse failure from a torn read) as
            // a cache miss, so this was never a crash — but it was a silent-ish
            // "MISS when it should have been a HIT" that reparses on every collision,
            // which is exactly the kind of intermittent-and-unexplained cost
            // parallelizing AlRunner.Tests's subprocess collections (#1809) makes
            // more likely to hit. Fix: publish atomically like every other
            // content-keyed cache in this codebase (AlCacheWriter.AtomicPublish —
            // temp file in the same directory + File.Move(overwrite:true)), so a
            // concurrent reader only ever sees "file absent" or "file complete",
            // never a torn write.
            AlCacheWriter.AtomicPublish(cachePath, tmp => File.WriteAllText(tmp, JsonSerializer.Serialize(payload)));
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"bc-symbols cache write failed {Path.GetFileName(cachePath)}: {ex.Message}");
        }
    }

    private static AppSymbols Parse(string appPath)
    {
        var tables = new Dictionary<int, ParsedTable>();
        var enums = new Dictionary<int, EnumSymbol>();
        var queries = new Dictionary<int, QuerySymbol>();
        var objects = new Dictionary<(string, int), ObjectSymbol>();
        var reports = new Dictionary<int, ReportSymbol>();
        var pages = new Dictionary<int, PageSymbol>();
        var pageExtensions = new Dictionary<int, PageExtensionSymbol>();
        var profiles = new Dictionary<string, ProfileSymbol>(StringComparer.OrdinalIgnoreCase);
        var permissionSets = new Dictionary<int, PermissionSetSymbol>();
        string? appId = null, appName = null;
        foreach (var json in ReadSymbolReferences(appPath))
        {
            using var doc = JsonDocument.Parse(json);
            VisitSymbolContainer(doc.RootElement, tables, enums, queries, objects, reports, pages, profiles, pageExtensions);
            CollectPermissionSets(doc.RootElement, permissionSets);
            // The .app's own identity, stated once at the root of its SymbolReference.json.
            // First one wins: ReadSymbolReferences can yield more than one module for a
            // package, and the package's own module is the one that comes first.
            if (appId == null)
                (appId, appName) = ReadAppIdentity(doc.RootElement);
        }
        return new AppSymbols(tables.Values.ToList(), enums.Values.ToList(), queries.Values.ToList(),
            objects.Values.ToList(), reports.Values.ToList(), pages.Values.ToList(),
            profiles.Values.ToList(), appId, appName, permissionSets.Values.ToList(),
            pageExtensions.Values.ToList());
    }

    /// <summary>
    /// Collect every <c>permissionset</c> a symbol reference declares, at the root and in
    /// every nested <c>Namespaces</c> container (BC 26+ nests application objects under
    /// namespaces, which is why a root-only read of <c>PermissionSets</c> finds two entries
    /// in the Base Application and none at all in the System Application — issue #2313).
    ///
    /// Kept as its own walk rather than another parameter on VisitSymbolContainer: only the
    /// Metadata Permission Set table reads these, and a second pass over an already-parsed
    /// JsonDocument costs nothing measurable next to the parse itself.
    ///
    /// The owning app id is NOT read here — <see cref="ReadAppIdentity"/> already takes it
    /// off the same root for <see cref="AppSymbols.AppId"/>, and every permission set in one
    /// symbol reference belongs to that app.
    /// </summary>
    private static void CollectPermissionSets(JsonElement root, Dictionary<int, PermissionSetSymbol> into)
    {
        Visit(root);

        void Visit(JsonElement container)
        {
            if (container.TryGetProperty("PermissionSets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (!el.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var id) || id <= 0)
                        continue;
                    var name = el.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    var props = SymbolProperties(el);
                    props.TryGetValue("Caption", out var caption);
                    // CLAIM: an absent `Assignable` is FALSE, which is what BC's own reader
                    // answers — not AL's source-language default of true. BC's
                    // Types.Metadata.MetaPermissionSet.Create assigns Assignable only inside
                    // `case 10: if (name == "Assignable")`, and MetaPermissionSet() initialises
                    // only Permissions/Included/Excluded, so an absent attribute leaves
                    // default(bool). Read off Microsoft.Dynamics.Nav.Types.dll 28.1.49838.53910;
                    // #3806 measured that reader answering False for set 68, and #2417 traced
                    // the true answer to a ModifyLength crash in the Aggregate Permission Set
                    // provider, whose Assignable=true filter real BC never lets that row past.
                    //
                    // TRAP: table 2000000250's field 4 carries `InitValue = true`, which is the
                    // initial value of a NEW AL record and says nothing about reading an emitted
                    // document. Citing it is what kept this wrong.
                    props.TryGetValue("Access", out var access);
                    into.TryAdd(id, new PermissionSetSymbol(
                        id, name, caption, SymbolBool(props, "Assignable"),
                        ReadPermissions(el),
                        ReadIncludedPermissionSets(props),
                        access));
                }
            }
            if (container.TryGetProperty("Namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Array)
                foreach (var ns in namespaces.EnumerateArray())
                    Visit(ns);
        }
    }

    /// <summary>
    /// A permission set's own <c>Permissions</c> array. An entry with no <c>PermissionObject</c>
    /// is TableData (0) — the JSON omits the property at its default, and that is the majority of
    /// all entries, so treating "absent" as "skip" would drop most of the data.
    /// </summary>
    private static IReadOnlyList<PermissionSymbol> ReadPermissions(JsonElement permissionSet)
    {
        if (!permissionSet.TryGetProperty("Permissions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<PermissionSymbol>();

        var list = new List<PermissionSymbol>();
        foreach (var el in arr.EnumerateArray())
        {
            if (!el.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var objectId)) continue;
            var kind = el.TryGetProperty("PermissionObject", out var kindProp) && kindProp.TryGetInt32(out var k) ? k : 0;
            var value = el.TryGetProperty("Value", out var valProp) && valProp.TryGetInt32(out var v) ? v : 0;
            list.Add(new PermissionSymbol(kind, objectId, value));
        }
        return list;
    }

    /// <summary>
    /// The `IncludedPermissionSets` property, whose value is the AL declaration's own list:
    /// each name double-quoted, comma separated — <c>"Azure AD Plan - Objects","Azure AD User - View"</c>.
    /// Names are returned unquoted; BC resolves them by role id.
    /// </summary>
    private static IReadOnlyList<string> ReadIncludedPermissionSets(IReadOnlyDictionary<string, string> props)
    {
        if (!props.TryGetValue("IncludedPermissionSets", out var raw) || string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        var names = new List<string>();
        foreach (var part in raw.Split(','))
        {
            var name = part.Trim().Trim('"').Trim();
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// The declaring app's id (GUID text) and name, as SymbolReference.json's root states
    /// them. Both are columns of an "All Profile" row ("App ID" / "App Name"), so a profile
    /// whose app the runner cannot identify is not answerable and is dropped rather than
    /// handed out under an invented app.
    /// </summary>
    private static (string? AppId, string? AppName) ReadAppIdentity(JsonElement root)
    {
        var id = root.TryGetProperty("AppId", out var idProp) ? idProp.GetString() : null;
        var name = root.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
        return (string.IsNullOrWhiteSpace(id) ? null : id, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <param name="alNamespace">The dotted path of the <c>Namespaces</c> tree this container was
    /// reached through, or null at the root. It is the ONLY source for a codeunit's ALNamespace:
    /// the symbol file states no such property, and Microsoft's own packages put every object in
    /// the tree — System Application 28.1's top-level <c>Codeunits</c> array has length 0 (#3788).</param>
    private static void VisitSymbolContainer(JsonElement container, Dictionary<int, ParsedTable> tables, Dictionary<int, EnumSymbol> enums, Dictionary<int, QuerySymbol> queries, Dictionary<(string, int), ObjectSymbol> objects, Dictionary<int, ReportSymbol> reports, Dictionary<int, PageSymbol> pages, Dictionary<string, ProfileSymbol> profiles, Dictionary<int, PageExtensionSymbol> pageExtensions, string? alNamespace = null)
    {
        // Flat (kind, id, name) sweep for AllObj. Independent of the typed parsing below
        // so a kind we do not model in depth still shows up as an existing object.
        foreach (var (containerName, kind) in ObjectContainers)
        {
            if (!container.TryGetProperty(containerName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var objId) || objId <= 0)
                    continue;
                var objName = el.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrEmpty(objName)) continue;
                var objProps = SymbolProperties(el);
                objProps.TryGetValue("Caption", out var objCaption);
                if (kind == "Codeunit")
                {
                    // The object-level properties CodeUnit Metadata reports as real columns.
                    // SymbolProperties is case-insensitive, so "TableNo"/"TableNO" both match.
                    objProps.TryGetValue("TableNo", out var cuTableNo);
                    objProps.TryGetValue("Subtype", out var cuSubtype);
                    // Carried as the AL mask LETTER string, decoded by the consumer. The letters
                    // are CASE-SIGNIFICANT — uppercase is the direct bit, lowercase the indirect
                    // bit at n+5 (docs/codeunit-metadata-from-bc.md#permission-mask-spelling) —
                    // so the VALUE must not be normalised here even though SymbolProperties
                    // matches the property NAME case-insensitively. System Application 28.1
                    // states "X" on 480 codeunits and "x" on codeunit 2516, which BC's emitter
                    // answers as 16 and 512 (#3788).
                    objProps.TryGetValue("InherentEntitlements", out var cuEntitlements);
                    objProps.TryGetValue("InherentPermissions", out var cuPermissions);
                    objects.TryAdd((kind, objId), new ObjectSymbol(kind, objId, objName, objCaption,
                        // Left as written; StripModuleQualifier is the consumer's job, the same
                        // split the query/report data-item RelatedTable reads already make.
                        TableNo: string.IsNullOrWhiteSpace(cuTableNo) ? null : cuTableNo.Trim(),
                        // AL's default is false; a stated value sets it. SymbolBool, not a
                        // hand-rolled "true" comparison: Microsoft's packages write "1", never
                        // the word — 38 of System Application 28.1's 533 codeunits state "1" and
                        // none states "true" — so matching only "true" answered false for every
                        // single-instance codeunit in a precompiled dependency (#3790). Every
                        // other boolean in this file already goes through SymbolBool.
                        SingleInstance: SymbolBool(objProps, "SingleInstance"),
                        Subtype: string.IsNullOrWhiteSpace(cuSubtype) ? null : cuSubtype.Trim(),
                        ALNamespace: alNamespace,
                        InherentEntitlements: string.IsNullOrWhiteSpace(cuEntitlements) ? null : cuEntitlements.Trim(),
                        InherentPermissions: string.IsNullOrWhiteSpace(cuPermissions) ? null : cuPermissions.Trim()));
                    continue;
                }
                objects.TryAdd((kind, objId), new ObjectSymbol(kind, objId, objName, objCaption,
                    TargetObjectName: ExtensionTargetName(kind, el)));
            }
        }

        if (container.TryGetProperty("Tables", out var tableArray) && tableArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var table in tableArray.EnumerateArray())
            {
                var parsed = TryParseTableSymbol(table);
                if (parsed != null && !tables.ContainsKey(parsed.TableId))
                    tables[parsed.TableId] = parsed;
            }
        }

        if (container.TryGetProperty("EnumTypes", out var enumTypes) && enumTypes.ValueKind == JsonValueKind.Array)
        {
            foreach (var enumType in enumTypes.EnumerateArray())
            {
                var parsed = TryParseEnumSymbol(enumType);
                if (parsed != null)
                    enums[parsed.Id] = parsed;
            }
        }

        if (container.TryGetProperty("Queries", out var queryArray) && queryArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in queryArray.EnumerateArray())
            {
                var parsed = TryParseQuerySymbol(q);
                if (parsed != null && !queries.ContainsKey(parsed.Id))
                    queries[parsed.Id] = parsed;
            }
        }

        if (container.TryGetProperty("Reports", out var reportArray) && reportArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in reportArray.EnumerateArray())
            {
                var parsed = TryParseReportSymbol(r, alNamespace);
                if (parsed != null && !reports.ContainsKey(parsed.Id))
                    reports[parsed.Id] = parsed;
            }
        }

        if (container.TryGetProperty("Pages", out var pageArray) && pageArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pageArray.EnumerateArray())
            {
                var parsed = TryParsePageSymbol(p);
                if (parsed != null && !pages.ContainsKey(parsed.Id))
                    pages[parsed.Id] = parsed;
            }
        }

        if (container.TryGetProperty("PageExtensions", out var pageExtArray) && pageExtArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var pe in pageExtArray.EnumerateArray())
            {
                var parsed = TryParsePageExtensionSymbol(pe);
                if (parsed != null && !pageExtensions.ContainsKey(parsed.Id))
                    pageExtensions[parsed.Id] = parsed;
            }
        }

        // Profiles are NOT in ObjectContainers above: a profile has no object id, so it can
        // never appear in AllObj. Its identity is its NAME, which is also its "Profile ID".
        if (container.TryGetProperty("Profiles", out var profileArray) && profileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var pr in profileArray.EnumerateArray())
            {
                var parsed = TryParseProfileSymbol(pr);
                if (parsed != null)
                    profiles.TryAdd(parsed.ProfileId, parsed);
            }
        }

        if (container.TryGetProperty("Namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Array)
        {
            foreach (var ns in namespaces.EnumerateArray())
            {
                // The tree path IS the value, joined with dots at whatever depth the object sits.
                // A namespace stating no Name cannot contribute a segment, so the path stops
                // rather than gaining an empty one — that would spell "System..Utilities".
                var segment = ns.TryGetProperty("Name", out var nsName) ? nsName.GetString() : null;
                var childNamespace = string.IsNullOrWhiteSpace(segment)
                    ? alNamespace
                    : string.IsNullOrEmpty(alNamespace) ? segment!.Trim() : alNamespace + "." + segment!.Trim();
                VisitSymbolContainer(ns, tables, enums, queries, objects, reports, pages, profiles, pageExtensions, childNamespace);
            }
        }
    }

    /// <summary>
    /// Parse one entry of a SymbolReference.json <c>Profiles</c> array. Nothing is inferred:
    /// the properties the file states are carried verbatim, and the two AL defaults applied
    /// here (<c>Enabled</c> = true, <c>Promoted</c> = false) are AL's own defaults for a
    /// profile that declares neither.
    ///
    /// <para>Only <c>ProfileDescription</c> feeds "All Profile".Description. A profile may
    /// also declare a <c>Description</c> property — Test Runner's TestRoleCenter profile does
    /// — but that is a DIFFERENT AL property and a service tier leaves the row's Description
    /// empty for it. Measured, not assumed: the corpus fixture ALT Profile SameApp declares
    /// Description and its row comes back with an empty Description on BC 27.0-28.4
    /// (BusinessCentral.AL.Language.Tests, TestAllProfileTable.al).</para>
    /// </summary>
    private static ProfileSymbol? TryParseProfileSymbol(JsonElement profile)
    {
        var name = profile.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var props = SymbolProperties(profile);
        props.TryGetValue("Caption", out var caption);
        props.TryGetValue("ProfileDescription", out var description);
        props.TryGetValue("RoleCenter", out var roleCenter);

        return new ProfileSymbol(
            name!, caption, description, roleCenter,
            Enabled: !SymbolBoolFalse(props, "Enabled"),
            Promoted: SymbolBool(props, "Promoted"));
    }

    /// <summary>
    /// Parse one entry of a SymbolReference.json <c>Pages</c> array. Only <c>SourceTable</c>
    /// is needed (issue #1719: binding a plain page variable's Rec) — everything else about
    /// a precompiled page (its control tree) is out of reach without parsing its AL source,
    /// which <see cref="RunnerPageInstance"/> already declines to do for a page the runner
    /// did not compile itself.
    /// <para><c>SourceTable</c>'s Properties value is the table's numeric ID as text (see
    /// e.g. Base Application's Page 700 "Error Messages": <c>SourceTable = "700"</c>), unlike
    /// <c>LookupPageId</c>/<c>DrillDownPageId</c> on a table, which are page NAMES — so this
    /// needs no name-to-id resolution pass.</para>
    /// </summary>
    private static PageSymbol? TryParsePageSymbol(JsonElement page)
    {
        if (!page.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var pageId) || pageId <= 0)
            return null;
        var name = page.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrEmpty(name)) return null;

        var props = SymbolProperties(page);
        int sourceTableId = props.TryGetValue("SourceTable", out var st) && int.TryParse(st, out var stId) ? stId : 0;
        bool sourceTableTemporary = SymbolBool(props, "SourceTableTemporary");

        props.TryGetValue("PageType", out var pageType);
        props.TryGetValue("Caption", out var caption);
        // SymbolProperties is case-insensitive, covering both "CardPageID" (observed on
        // Base Application 28.1) and any "CardPageId" spelling — same tolerance Table
        // Metadata already applies to LookupPageId/LookupPageID.
        props.TryGetValue("CardPageId", out var cardPageName);
        // AL defaults for these four: true, only an explicit "false"/"0" flips them — same
        // rule ParsePageControls uses for the source-parsed path.
        bool editable = !SymbolBoolFalse(props, "Editable");
        bool insertAllowed = !SymbolBoolFalse(props, "InsertAllowed");
        bool modifyAllowed = !SymbolBoolFalse(props, "ModifyAllowed");
        bool deleteAllowed = !SymbolBoolFalse(props, "DeleteAllowed");
        // These three default to FALSE in AL, so only an explicit "1"/"true" sets them —
        // SymbolBool, not the SymbolBoolFalse the four above use.
        bool autoSplitKey = SymbolBool(props, "AutoSplitKey");
        bool multipleNewLines = SymbolBool(props, "MultipleNewLines");
        bool delayedInsert = SymbolBool(props, "DelayedInsert");

        var controls = new List<PageControlSymbol>();
        var parts = new List<PagePartSymbol>();
        var memberNames = new Dictionary<int, string>();
        var actionRefTargets = new Dictionary<int, string>();
        var runObjects = new Dictionary<int, ActionRunObjectSymbol>();
        var actionDeclaredProperties = new Dictionary<int, ActionDeclaredPropertiesSymbol>();
        int seq = 0;
        if (page.TryGetProperty("Controls", out var controlsArr) && controlsArr.ValueKind == JsonValueKind.Array)
            foreach (var c in controlsArr.EnumerateArray())
            {
                CollectPageControlSymbols(c, controls, ref seq);
                CollectPagePartSymbols(c, parts);
                CollectMemberNames(c, "Controls", memberNames, actionRefTargets);
            }
        // Actions only (issue #2460): a CONTROL's Enabled/Visible already travels on
        // PageControlSymbol from CollectPageControlSymbols above, so passing the dictionary
        // down the Controls walk too would record the same declaration in two places and give
        // a later reader two sources that can disagree.
        if (page.TryGetProperty("Actions", out var actionsArr) && actionsArr.ValueKind == JsonValueKind.Array)
            foreach (var a in actionsArr.EnumerateArray())
                CollectMemberNames(a, "Actions", memberNames, actionRefTargets, runObjects, actionDeclaredProperties);

        props.TryGetValue("SourceTableView", out var sourceTableView);

        // #2860's five. SymbolBoolOrNull, not SymbolBool/SymbolBoolFalse: for these the
        // absence of the property is itself information BC acts on, so it must survive as
        // null rather than be folded into an AL default here. See PageSymbol's own note.
        List<string>? unreadableBooleans = null;
        var linksAllowed = SymbolBoolOrNull(props, "LinksAllowed", ref unreadableBooleans);
        var showFilter = SymbolBoolOrNull(props, "ShowFilter", ref unreadableBooleans);
        var saveValues = SymbolBoolOrNull(props, "SaveValues", ref unreadableBooleans);
        var populateAllFields = SymbolBoolOrNull(props, "PopulateAllFields", ref unreadableBooleans);
        props.TryGetValue("DataCaptionFields", out var dataCaptionFields);

        // #3784's PageProperties. Two AL defaults BC's emitter writes unconditionally, so
        // SymbolBool/SymbolBoolFalse (which fold absence into the default) are the right
        // readers here — unlike the three-state group below.
        bool extensible = !SymbolBoolFalse(props, "Extensible");
        bool refreshOnActivate = SymbolBool(props, "RefreshOnActivate");
        bool isPreview = SymbolBool(props, "IsPreview");

        // The three-state group, for the #2860 reason: BC's emitter writes each attribute
        // precisely when the AL states the property, whatever the value, and its reader's
        // default (InsertAllowed/ModifyAllowed/DeleteAllowed true, the other two false)
        // differs from at least one legal stated value in every case.
        var insertAllowedStated = SymbolBoolOrNull(props, "InsertAllowed", ref unreadableBooleans);
        var modifyAllowedStated = SymbolBoolOrNull(props, "ModifyAllowed", ref unreadableBooleans);
        var deleteAllowedStated = SymbolBoolOrNull(props, "DeleteAllowed", ref unreadableBooleans);
        var delayedInsertStated = SymbolBoolOrNull(props, "DelayedInsert", ref unreadableBooleans);
        var multipleNewLinesStated = SymbolBoolOrNull(props, "MultipleNewLines", ref unreadableBooleans);

        // Scalars, verbatim. Nothing is resolved, normalised or defaulted here: whether an
        // absent value means "write nothing" or "write the AL default" is the emitter's
        // question, and it differs per property (see EmitPageXml).
        props.TryGetValue("UsageCategory", out var usageCategory);
        props.TryGetValue("HelpLink", out var helpLink);
        props.TryGetValue("AboutTitle", out var aboutTitle);
        props.TryGetValue("AboutText", out var aboutText);
        props.TryGetValue("AdditionalSearchTerms", out var additionalSearchTerms);
        props.TryGetValue("InstructionalText", out var instructionalText);
        props.TryGetValue("InherentEntitlements", out var inherentEntitlements);
        props.TryGetValue("InherentPermissions", out var inherentPermissions);

        static string? OrNullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

        return new PageSymbol(pageId, name!, sourceTableId, sourceTableTemporary,
            string.IsNullOrWhiteSpace(pageType) ? "Card" : pageType!, caption,
            editable, insertAllowed, modifyAllowed, deleteAllowed, controls,
            string.IsNullOrWhiteSpace(cardPageName) ? null : cardPageName,
            autoSplitKey, multipleNewLines, delayedInsert, parts,
            memberNames, actionRefTargets,
            ParseSourceTableView(pageId, sourceTableView), runObjects,
            linksAllowed, showFilter, saveValues, populateAllFields,
            string.IsNullOrWhiteSpace(dataCaptionFields) ? null : dataCaptionFields,
            extensible, refreshOnActivate,
            OrNullIfBlank(usageCategory), OrNullIfBlank(helpLink),
            OrNullIfBlank(aboutTitle), OrNullIfBlank(aboutText),
            OrNullIfBlank(additionalSearchTerms), OrNullIfBlank(instructionalText),
            OrNullIfBlank(inherentEntitlements), OrNullIfBlank(inherentPermissions),
            isPreview,
            insertAllowedStated, modifyAllowedStated, deleteAllowedStated,
            delayedInsertStated, multipleNewLinesStated,
            unreadableBooleans, actionDeclaredProperties);
    }

    /// <summary>
    /// Parse one entry of a SymbolReference.json <c>PageExtensions</c> array into a
    /// <see cref="PageExtensionSymbol"/> — id, name, the extended page's name, and the
    /// member-name maps of every action/control the extension ADDS. Null for an entry with
    /// no usable id, name or target.
    /// </summary>
    private static PageExtensionSymbol? TryParsePageExtensionSymbol(JsonElement ext)
    {
        if (!ext.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var extId) || extId <= 0)
            return null;
        var name = ext.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
        var target = ext.TryGetProperty("TargetObject", out var targetProp) ? targetProp.GetString() : null;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(target)) return null;

        var memberNames = new Dictionary<int, string>();
        var actionRefTargets = new Dictionary<int, string>();
        var runObjects = new Dictionary<int, ActionRunObjectSymbol>();
        // Where each added member came from, recorded AS it is collected rather than inferred
        // afterwards: memberNames merges the two containers, and nothing in an id or a name says
        // which side it came from or under which change (#3809). The diff against memberNames'
        // keys is what attributes a member to the change currently being walked — CollectMemberNames
        // recurses, so a change can contribute several members at several depths.
        var origins = new Dictionary<int, PageExtensionMemberOrigin>();
        var sequence = 0;
        void RecordOrigin(HashSet<int> before, bool isAction, JsonElement change)
        {
            var anchor = change.TryGetProperty("Anchor", out var an) ? an.GetString() : null;
            var changeKind = change.TryGetProperty("ChangeKind", out var ck) && ck.TryGetInt32(out var k) ? k : 0;
            foreach (var id in memberNames.Keys)
                if (!before.Contains(id))
                    origins[id] = new PageExtensionMemberOrigin(isAction, anchor, changeKind, sequence++);
        }

        if (ext.TryGetProperty("ActionChanges", out var actionChanges) && actionChanges.ValueKind == JsonValueKind.Array)
            foreach (var change in actionChanges.EnumerateArray())
                if (change.TryGetProperty("Actions", out var added) && added.ValueKind == JsonValueKind.Array)
                    foreach (var a in added.EnumerateArray())
                    {
                        var before = new HashSet<int>(memberNames.Keys);
                        CollectMemberNames(a, "Actions", memberNames, actionRefTargets, runObjects);
                        RecordOrigin(before, isAction: true, change);
                    }
        if (ext.TryGetProperty("ControlChanges", out var controlChanges) && controlChanges.ValueKind == JsonValueKind.Array)
            foreach (var change in controlChanges.EnumerateArray())
                if (change.TryGetProperty("Controls", out var added) && added.ValueKind == JsonValueKind.Array)
                    foreach (var c in added.EnumerateArray())
                    {
                        var before = new HashSet<int>(memberNames.Keys);
                        CollectMemberNames(c, "Controls", memberNames, actionRefTargets);
                        RecordOrigin(before, isAction: false, change);
                    }

        return new PageExtensionSymbol(extId, name!, StripModuleQualifierPrefix(target!),
            memberNames, actionRefTargets, runObjects, origins);
    }

    /// <summary>
    /// The object an *extension symbol extends, as the .app's SymbolReference.json states it,
    /// with any <c>#&lt;appid&gt;#</c> module qualifier stripped. Null for a non-extension kind
    /// and when the file states no target.
    ///
    /// <para>MEASURED against Base Application 28.1, because the compiler does NOT spell this
    /// one element the same way in every container: <c>TableExtensions</c> (90),
    /// <c>PageExtensions</c> (156), <c>EnumExtensionTypes</c> (39) and
    /// <c>PermissionSetExtensions</c> (55) all carry <c>TargetObject</c> and every entry has
    /// one — while all 14 <c>ReportExtensions</c> carry <c>Target</c> instead and NOT ONE
    /// carries <c>TargetObject</c>. Reading only the first spelling leaves every
    /// reportextension's target null, which reaches AllObjWithCaption as an empty Object
    /// Subtype and looks exactly like the gap this exists to close. Both are NAMES; neither is
    /// an id.</para>
    /// </summary>
    private static string? ExtensionTargetName(string kind, JsonElement el)
    {
        if (!kind.EndsWith("Extension", StringComparison.Ordinal)) return null;
        var target = el.TryGetProperty("TargetObject", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(target))
            target = el.TryGetProperty("Target", out var t2) ? t2.GetString() : null;
        return string.IsNullOrWhiteSpace(target) ? null : StripModuleQualifierPrefix(target.Trim());
    }

    /// <summary>
    /// <c>"#63ca2fa44f034f2ba480172fef340d3f#Accessible Companies"</c> → <c>"Accessible
    /// Companies"</c>; a name with no leading <c>#…#</c> qualifier passes through unchanged.
    /// </summary>
    private static string StripModuleQualifierPrefix(string target)
    {
        if (target.Length > 1 && target[0] == '#')
        {
            var close = target.IndexOf('#', 1);
            if (close > 0) return target.Substring(close + 1);
        }
        return target;
    }

    /// <summary>
    /// Record one action/control node's (Id → Name), plus (Id → TargetName) when the node is
    /// an actionref, then recurse into its children under <paramref name="childKey"/>
    /// ("Actions" for the action tree, "Controls" for the layout tree). No Kind filter on
    /// purpose: a group, separator, systemaction, fileuploadaction or customaction is as much
    /// a named member with a member id as a plain action, and a name that has no emitted
    /// trigger method simply never matches — FindTriggerOnTarget compares against the methods
    /// that exist. TryAdd, not the indexer: a field and an action of the same name carry the
    /// same id and the same name, so first-writer-wins loses nothing.
    /// </summary>
    private static void CollectMemberNames(JsonElement node, string childKey,
        Dictionary<int, string> names, Dictionary<int, string> actionRefTargets,
        Dictionary<int, ActionRunObjectSymbol>? runObjects = null,
        Dictionary<int, ActionDeclaredPropertiesSymbol>? declaredProperties = null)
    {
        if (node.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var id) && id != 0
            && node.TryGetProperty("Name", out var nameProp) && nameProp.GetString() is { Length: > 0 } name)
        {
            names.TryAdd(id, name);
            if (node.TryGetProperty("TargetName", out var targetProp) && targetProp.GetString() is { Length: > 0 } target)
                actionRefTargets.TryAdd(id, target);
            if (runObjects != null && TryReadActionRunObject(node) is { } runObject)
                runObjects.TryAdd(id, runObject);
            // Only recorded when the node declares at least one of the two, so the dictionary
            // holds exactly the actions with something to say (1,129 + 1,101 of Base
            // Application 28.1's 25,184) and a MISS keeps meaning "declares neither" —
            // EvaluateProperty's AL default of true. An entry of two nulls would say the same
            // thing in a second spelling, and only the miss is checked downstream.
            if (declaredProperties != null && TryReadActionDeclaredProperties(node) is { } declared)
                declaredProperties.TryAdd(id, declared);
        }
        if (node.TryGetProperty(childKey, out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectMemberNames(child, childKey, names, actionRefTargets, runObjects, declaredProperties);
    }

    /// <summary>
    /// The <c>Enabled</c> / <c>Visible</c> an action node declares, or null when it declares
    /// neither (issue #2460). Verbatim — see <see cref="ActionDeclaredPropertiesSymbol"/>.
    /// </summary>
    private static ActionDeclaredPropertiesSymbol? TryReadActionDeclaredProperties(JsonElement node)
    {
        var props = SymbolProperties(node);
        var hasEnabled = props.TryGetValue("Enabled", out var enabled) && !string.IsNullOrWhiteSpace(enabled);
        var hasVisible = props.TryGetValue("Visible", out var visible) && !string.IsNullOrWhiteSpace(visible);
        if (!hasEnabled && !hasVisible) return null;
        return new ActionDeclaredPropertiesSymbol(hasEnabled ? enabled : null, hasVisible ? visible : null);
    }

    /// <summary>
    /// The <c>RunObject</c> an action node declares, with the two page-run properties that
    /// change what opening it means, or null when the node declares no <c>RunObject</c>.
    /// See <see cref="ActionRunObjectSymbol"/> for why the target is a name.
    /// </summary>
    private static ActionRunObjectSymbol? TryReadActionRunObject(JsonElement node)
    {
        var props = SymbolProperties(node);
        if (!props.TryGetValue("RunObject", out var runObject) || string.IsNullOrWhiteSpace(runObject))
            return null;
        var hasLink = props.TryGetValue("RunPageLink", out var link) && !string.IsNullOrWhiteSpace(link);
        var declaredEntries = 0;
        List<PageSubFormLinkSymbol>? parsedLink = null;
        List<string>? unreadableLink = null;
        if (hasLink)
        {
            // Counted with the SAME directive-aware splitter the parser uses, NOT a bare
            // SplitTopLevelCommas (#3267). The compiler records a property's SOURCE text with
            // its AL preprocessor directives in it, and a comma-split chunk can then be
            // directives and whitespace only -- `"A" = field("B"), #if X "C" = field("D"),
            // #endif` splits into three chunks of which the third is just `#endif`. Counting
            // that as a DECLARED entry made declaredEntries 3 against a correctly-read
            // parsed.Count of 2, and LinksFromSymbols refuses on that difference -- so a link
            // this parser read completely was refused for being incomplete. Directives inside
            // a RunPageLink are the same shipping shape #2978 measured on BC 27.5's part
            // SubPageLinks, read by the same grammar, so the count has to be blind to them the
            // same way the parse is.
            declaredEntries = SplitPropertyEntries(link!).Count;
            // Same grammar as a part's SubPageLink -- `"Field" = field("Other")`, `= const(X)`,
            // `= filter(A|B)`, comma-separated -- so the same parser reads it (issue #2942).
            // The list IS carried here, not discarded. #3270 discarded it on the reasoning
            // that "an unreadable entry is counted in declaredEntries but never reaches the
            // result, so this path already refuses through DeclaredRunPageLinkEntries !=
            // RunPageLink.Count". The first half is true and the conclusion does not follow,
            // because that comparison was not sound: the two sides were computed by two
            // splitters that disagree about AL preprocessor directives (see the count above).
            // A count that can be wrong in the FALSE-REFUSAL direction is not a channel to
            // rest a fail-closed decision on alone, so the refusal now has two independent
            // signals, and the one made of text can say WHICH entry defeated the parser
            // instead of only how many did.
            parsedLink = ParseSubPageLink(link, out unreadableLink);
        }
        return new ActionRunObjectSymbol(
            runObject!,
            // AL's default is false, so only an explicit "1"/"true" sets it — the compiler
            // writes RunPageOnRec = "1" for `RunPageOnRec = true`.
            SymbolBool(props, "RunPageOnRec"),
            declaredEntries,
            parsedLink,
            unreadableLink);
    }

    /// <summary>
    /// Recursively collect every "Kind 8" field control (identified by having a
    /// <c>SourceExpression</c> property, NOT a hardcoded Kind number — verified against
    /// Base Application 28.1: every one of its 36890 <c>SourceExpression</c>-bearing
    /// controls is Kind 8, and no other Kind ever carries one) out of a page's <c>Controls</c>
    /// tree, which nests group/repeater/cuegroup/etc. the same way a report's data items nest.
    /// Sequence is assigned here, depth-first in document order — the same order a real page
    /// renders its controls.
    /// </summary>
    private static void CollectPageControlSymbols(JsonElement control, List<PageControlSymbol> into, ref int sequence)
    {
        var props = SymbolProperties(control);
        if (props.TryGetValue("SourceExpression", out var srcExpr))
        {
            var name = control.TryGetProperty("Name", out var n) ? n.GetString() : null;
            int id = control.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var idv) ? idv : 0;
            if (!string.IsNullOrEmpty(name) && id != 0)
            {
                props.TryGetValue("Visible", out var visible);
                props.TryGetValue("Editable", out var editable);
                props.TryGetValue("Enabled", out var enabled);
                // 0-based, POST-increment (#3631). BC numbers its FindAll walk with
                // `Select((cd, i) => …)`, an index captured before the OrderBy that sorts by
                // control id, so the first control is 0. This path was left 1-based when
                // #3628 corrected the other two only because this file was being changed by
                // another PR at the time; the inconsistency is worse than either value,
                // because Page Control Field's Sequence then meant different things for a
                // source-compiled and a precompiled-dependency page and AL cannot see which
                // it has.
                into.Add(new PageControlSymbol(id, name!, srcExpr, visible, editable, enabled, sequence++));
            }
        }

        if (control.TryGetProperty("Controls", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectPageControlSymbols(child, into, ref sequence);
    }

    /// <summary>
    /// Recursively collect every subpage PART control (identified by a <c>RelatedPagePartId</c>
    /// element — see <see cref="PagePartSymbol"/>'s doc comment) out of a page's <c>Controls</c>
    /// tree, wherever it nests. All parts are collected into one flat list regardless of
    /// nesting depth — DependencyPageMetadataXml re-emits them as direct siblings under one
    /// synthesized container, which BC's own MetadataHelper.InfoPartDefinitions (itself a
    /// flat view built by walking the WHOLE Content tree) treats identically to however deep
    /// the real compiled page actually nested them.
    /// </summary>
    private static void CollectPagePartSymbols(JsonElement control, List<PagePartSymbol> into)
    {
        if (control.TryGetProperty("RelatedPagePartId", out var rel) && rel.ValueKind == JsonValueKind.Object
            && rel.TryGetProperty("Id", out var relId) && relId.TryGetInt32(out var partPageId) && partPageId > 0)
        {
            var name = control.TryGetProperty("Name", out var n) ? n.GetString() : null;
            int id = control.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var idv) ? idv : 0;
            if (!string.IsNullOrEmpty(name) && id != 0)
            {
                var props = SymbolProperties(control);
                props.TryGetValue("Caption", out var caption);
                props.TryGetValue("Editable", out var editable);
                props.TryGetValue("Enabled", out var enabled);
                props.TryGetValue("Visible", out var visible);
                props.TryGetValue("ShowFilter", out var showFilter);
                props.TryGetValue("SubPageLink", out var subPageLink);
                var links = ParseSubPageLink(subPageLink, out var unreadableLinks);
                into.Add(new PagePartSymbol(id, name!, partPageId,
                    string.IsNullOrEmpty(caption) ? null : caption,
                    string.IsNullOrEmpty(editable) ? null : editable,
                    string.IsNullOrEmpty(enabled) ? null : enabled,
                    string.IsNullOrEmpty(visible) ? null : visible,
                    string.IsNullOrEmpty(showFilter) ? null : showFilter,
                    links, unreadableLinks));
            }
        }

        if (control.TryGetProperty("Controls", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectPagePartSymbols(child, into);
    }

    /// <summary>
    /// Split a <c>SubPageLink</c> / <c>SourceTableView where(...)</c> property text into its
    /// comma-separated entries, stripping the AL PREPROCESSOR DIRECTIVE lines the compiler
    /// records verbatim inside the property's source text, and saying which entries sit
    /// inside a conditional block.
    ///
    /// <para>Measured, not anticipated. Across BC 27.5 and 28.1 W1 (every extension in the
    /// artifact: 7,646 pages, 3,655 SubPageLink entries, 981 pages declaring a
    /// SourceTableView with 547 where-entries) exactly six entries fail the entry regex, all
    /// six on BC 27.5 Base Application, all six because of a directive:</para>
    /// <code>
    /// page 76 "Resource Card"  / Control1906609707
    /// page 77 "Resource List"  / Control1906609707, Control1907012907
    ///     "No." = field("No."),
    ///     "Unit of Measure Filter" = field("Unit of Measure Filter"),
    /// #if not CLEAN25
    ///     "Service Zone Filter" = field("Service Zone Filter"),
    /// #endif
    ///     "Chargeable Filter" = field("Chargeable Filter")
    /// </code>
    /// <para>Comma-splitting that leaves <c>#if not CLEAN25\r\n"Service Zone Filter" = …</c>
    /// and <c>#endif\r\n"Chargeable Filter" = …</c>, neither of which the entry regex can
    /// read. Before this, both were dropped — so those three subpages filtered on TWO of
    /// their four conditions and showed rows the host resource does not own.</para>
    ///
    /// <para>An entry after a directive that only opens or closes a region (<c>#endif</c>,
    /// <c>#region</c>, <c>#endregion</c>, <c>#pragma</c>, <c>#define</c>, <c>#undef</c>) is
    /// unconditionally in the app and must parse — <c>"Chargeable Filter"</c> above is only
    /// unreadable because <c>#endif</c> happens to precede it, and BC 28.1, where the
    /// directive has been deleted from the source, carries it. An entry inside an
    /// <c>#if</c>/<c>#ifdef</c>/<c>#ifndef</c>/<c>#else</c>/<c>#elif</c> block is CONDITIONAL:
    /// this parse cannot evaluate the condition, so the caller resolves it against the app's
    /// own field inventory rather than refusing the page over it — see
    /// <c>DependencyPageMetadataXml.EmitSubFormLinkXml</c>.</para>
    ///
    /// <para>What the evidence says about <c>CLEAN25</c> specifically, since the runner does
    /// NOT encode it: the guarded entry names "Service Zone Filter", which the
    /// <c>Serv. Resource</c> tableextension adds to Resource in BOTH 27.5 and 28.1, so it
    /// resolves and the reconstruction applies it. Everything observable says that is right —
    /// Microsoft's shipped W1 builds do not define <c>CLEANnn</c>. 28.1 still carries
    /// <c>#if not CLEAN27</c> even though it is BC 28, which a defined symbol would make dead
    /// code; the two guards that read <c>CLEAN25</c>/<c>CLEAN26</c> in 27.5 read
    /// <c>CLEAN28</c> in 28.1, i.e. the version is bumped rather than the branch resolved;
    /// and 28.1 wraps the body of one such block in <c>#pragma warning disable AL0432</c>,
    /// which suppresses an obsolete-USAGE warning and is only needed on code that compiles.
    /// A defined symbol would flip that to over-filtering — narrower than BC, which fails a
    /// test loudly, not the silent widening this whole change is about.</para>
    ///
    /// <para>Nesting is tracked across entries, not per entry: in
    /// <c>#if X  A=…, B=…, #endif  C=…</c> only A carries the directive text, and B is inside
    /// the block just as much as A is.</para>
    /// </summary>
    private static List<(string Text, bool Conditional)> SplitPropertyEntries(string text)
    {
        var result = new List<(string, bool)>();
        var depth = 0;
        foreach (var rawEntry in SplitTopLevelCommas(text, preserveNewlines: true))
        {
            var body = new System.Text.StringBuilder();
            var sawBody = false;
            var conditional = depth > 0;
            foreach (var rawLine in rawEntry.Split('\n'))
            {
                var line = rawLine.Trim();      // also drops the '\r' of a CRLF pair
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    switch (DirectiveName(line))
                    {
                        case "if": case "ifdef": case "ifndef": depth++; break;
                        case "endif": if (depth > 0) depth--; break;
                        // #else / #elif keep the block open; #region / #endregion / #pragma /
                        // #define / #undef never gate anything.
                        default: break;
                    }
                    // Only directives BEFORE the entry's own text decide whether THIS entry is
                    // conditional; ones after it belong to whatever follows.
                    if (!sawBody) conditional = depth > 0;
                    continue;
                }
                if (sawBody) body.Append(' ');
                body.Append(line);
                sawBody = true;
            }
            if (sawBody) result.Add((body.ToString(), conditional));
        }
        return result;
    }

    /// <summary>The directive keyword of a line starting with '#', lowercased and without
    /// its arguments — <c>"#if not CLEAN25"</c> -&gt; <c>"if"</c>. AL allows whitespace
    /// between the '#' and the keyword.</summary>
    private static string DirectiveName(string line)
    {
        var i = 1;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        var start = i;
        while (i < line.Length && char.IsLetter(line[i])) i++;
        return line.Substring(start, i - start).ToLowerInvariant();
    }

    private static readonly System.Text.RegularExpressions.Regex SubPageLinkEntryRegex = new(
        @"^(?<left>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<kind>field|const|filter)\((?<val>.*)\)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parse a <c>SubPageLink</c> property's raw AL text — e.g. <c>"Document No." =
    /// field("No.")</c>, or a comma-separated list of such pairs — into (part field, kind,
    /// value) triples. Measured across Base Application 28.1's 1311 SubPageLink entries:
    /// 1168 are <c>field(...)</c>, 140 are <c>const(...)</c>, 3 are <c>filter(...)</c>; an
    /// entry this cannot parse is returned in <paramref name="unreadable"/> — NOT dropped
    /// (#2978), because a link shipped without one of its conditions filters the part less
    /// than the AL says and the subpage then shows rows the host row does not own; the three
    /// kinds the regex accepts are the only ones AL defines, so an unreadable entry means AL
    /// text this parser has never seen, not a link kind the runner declines.
    /// </summary>
    private static List<PageSubFormLinkSymbol> ParseSubPageLink(string? text, out List<string>? unreadable)
    {
        unreadable = null;
        var result = new List<PageSubFormLinkSymbol>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var (entry, conditional) in SplitPropertyEntries(text!))
        {
            var m = SubPageLinkEntryRegex.Match(entry);
            if (!m.Success)
            {
                (unreadable ??= new List<string>()).Add(entry);
                continue;
            }
            var partField = m.Groups["left"].Value.Trim('"');
            var kind = m.Groups["kind"].Value.ToLowerInvariant();
            var value = m.Groups["val"].Value.Trim();
            result.Add(new PageSubFormLinkSymbol(partField, kind, value, conditional));
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex ViewClauseRegex = new(
        @"\b(?<clause>sorting|order|where)\s*\(",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parse a page's <c>SourceTableView</c> property text — <c>sorting(f1, f2)
    /// order(descending) where("A" = const(X), B = filter(&lt;&gt; ''))</c>, every clause
    /// optional — into the typed shape <c>DependencyPageMetadataXml</c> re-emits as BC's own
    /// <c>&lt;SourceTableView&gt;</c> metadata element (issue #2820).
    ///
    /// <para>Measured across Base Application 28.1's SymbolReference.json: 386 pages declare a
    /// SourceTableView — 220 carry <c>where(...)</c>, 178 <c>sorting(...)</c> and 99
    /// <c>order(...)</c>; of the 235 filter entries 171 are <c>const(...)</c> and 64
    /// <c>filter(...)</c>, and not one is <c>field(...)</c>, which AL does not allow here.</para>
    ///
    /// <para>Returns null for a page declaring no view, and for text no clause could be read
    /// out of (reported on stderr) — the caller then emits no <c>&lt;SourceTableView&gt;</c>
    /// element at all, which is the same state as before this parse existed, rather than a
    /// manufactured narrower one.</para>
    ///
    /// <para>A <c>where(...)</c> entry this cannot read, and a clause whose parenthesis never
    /// closes, are recorded in <see cref="PageTableViewSymbol.UnreadableEntries"/> — NOT
    /// dropped (#2978). Dropping shipped a PARTIAL view, which is WIDER than the one the page
    /// declares: the page opens on rows the real view excludes, and the only trace was a
    /// Console.Error line the symbol cache loses on every warm run. The caller turns each one
    /// into a filter the page refuses to open on, matching what
    /// <c>EmitSourceTableViewXml</c> already does for a field name it cannot resolve.</para>
    /// </summary>
    private static PageTableViewSymbol? ParseSourceTableView(int pageId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var sorting = new List<PageViewSortFieldSymbol>();
        bool? ascending = null;
        var filters = new List<PageViewFilterSymbol>();
        List<string>? unreadable = null;
        var sawClause = false;

        foreach (System.Text.RegularExpressions.Match m in ViewClauseRegex.Matches(text!))
        {
            var open = m.Index + m.Length - 1;           // the '(' itself
            var close = MatchingCloseParen(text!, open);
            if (close < 0)
            {
                // Unbalanced. Dropping the clause silently was the worst of the #2978 shapes:
                // `sorting(X) where(Y = const(1)` came up SORTED as declared and UNFILTERED,
                // so the half that is easy to eyeball was right. A where/sorting/order clause
                // that could not be read is unreadable AL, whichever clause it is, so it goes
                // to the refusal channel regardless of which keyword opened it.
                (unreadable ??= new List<string>()).Add(text!.Substring(m.Index).Trim());
                continue;
            }
            var inner = text!.Substring(open + 1, close - open - 1);
            sawClause = true;

            switch (m.Groups["clause"].Value.ToLowerInvariant())
            {
                case "sorting":
                    // SplitPropertyEntries, not the bare SplitTopLevelCommas (#3271). Both arms
                    // of this switch read text the SAME compiler wrote, so both must strip the
                    // AL preprocessor directive lines it records verbatim inside a property's
                    // source (#2978). The bare splitter did not, AND it flattened the newlines
                    // that make a directive a LINE — so a guarded sorting() did not produce a
                    // short field list, it produced field names with directive text glued onto
                    // them (`#if not CLEAN25 "Zone Code`), which is also how it destroyed the
                    // real name on the line after the #endif.
                    foreach (var (entry, conditional) in SplitPropertyEntries(inner))
                    {
                        var fieldName = entry.Trim().Trim('"');
                        if (fieldName.Length > 0)
                            sorting.Add(new PageViewSortFieldSymbol(fieldName, conditional));
                    }
                    break;
                case "order":
                    // AL allows exactly `ascending` / `descending` here.
                    var dir = inner.Trim();
                    if (string.Equals(dir, "descending", StringComparison.OrdinalIgnoreCase)) ascending = false;
                    else if (string.Equals(dir, "ascending", StringComparison.OrdinalIgnoreCase)) ascending = true;
                    else Console.Error.WriteLine(
                        $"[BcAppSymbolCache] page {pageId} SourceTableView order() not understood, ignored: '{dir}'");
                    break;
                default:
                    foreach (var (entry, conditional) in SplitPropertyEntries(inner))
                    {
                        var em = SubPageLinkEntryRegex.Match(entry);
                        if (!em.Success)
                        {
                            (unreadable ??= new List<string>()).Add(entry);
                            continue;
                        }
                        filters.Add(new PageViewFilterSymbol(
                            em.Groups["left"].Value.Trim('"'),
                            em.Groups["kind"].Value.ToLowerInvariant(),
                            em.Groups["val"].Value.Trim(),
                            conditional));
                    }
                    break;
            }
        }

        if (!sawClause)
        {
            Console.Error.WriteLine(
                $"[BcAppSymbolCache] page {pageId} SourceTableView not understood, ignored: '{text!.Trim()}'");
            return null;
        }
        if (sorting.Count == 0 && ascending == null && filters.Count == 0 && unreadable == null) return null;
        return new PageTableViewSymbol(sorting, ascending, filters, unreadable);
    }

    /// <summary>Index of the ')' closing the '(' at <paramref name="open"/>, ignoring
    /// parentheses inside an AL quoted identifier or a single-quoted literal, or -1.</summary>
    private static int MatchingCloseParen(string s, int open)
    {
        var depth = 0;
        var inDouble = false;
        var inSingle = false;
        for (int i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }
            if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
            if (inDouble || inSingle) continue;
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Split a SubPageLink's comma-separated entries, ignoring a comma that falls inside a
    /// quoted field name or inside a nested <c>(...)</c> (e.g. <c>const(Database::"Purchase
    /// Header")</c> or <c>filter(Open | "X")</c> — neither is comma-separated internally in
    /// the corpus, but a nested nested-paren value like <c>field("A, B")</c> would otherwise
    /// split wrongly).
    /// </summary>
    /// <param name="preserveNewlines">Keep the entry's own line breaks instead of flattening
    /// them to spaces. <see cref="SplitPropertyEntries"/> needs them: an AL preprocessor
    /// directive is a LINE, and once the newline after <c>#if not CLEAN25</c> is a space the
    /// directive and the entry it guards are indistinguishable from one run-on entry — which
    /// is exactly how three BC 27.5 Base Application subpage links were silently losing half
    /// their conditions (#2978). Everything else keeps the flattening, which is what the
    /// entry regexes have always been handed.</param>
    private static IEnumerable<string> SplitTopLevelCommas(string text, bool preserveNewlines = false)
    {
        var normalized = preserveNewlines ? text : text.Replace("\r\n", " ").Replace('\n', ' ');
        var result = new List<string>();
        int depth = 0;
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && c == '(') depth++;
            else if (!inQuotes && c == ')') depth--;
            else if (!inQuotes && depth == 0 && c == ',')
            {
                result.Add(normalized[start..i]);
                start = i + 1;
            }
        }
        result.Add(normalized[start..]);
        return result;
    }

    private static bool SymbolBool(Dictionary<string, string> props, string name)
        => props.TryGetValue(name, out var v) && (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

    private static bool SymbolBoolFalse(Dictionary<string, string> props, string name)
        => props.TryGetValue(name, out var v) && (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The three-state form of <see cref="SymbolBool"/> for a property whose ABSENCE is
    /// itself information: null when the symbol file states nothing, otherwise the stated
    /// value. Both spellings the file uses are accepted ("1"/"0" in practice, "true"/"false"
    /// tolerated), matching SymbolBool/SymbolBoolFalse.
    ///
    /// <para>A value the file STATES but this cannot read as a boolean also answers null — the
    /// "state what the file states, never invent" rule, since inventing a default would be
    /// indistinguishable from the file stating that default. But the two nulls are different
    /// facts, and returning them identically with nothing said is the very shape of defect
    /// #2860 fixes one level up, so the unreadable one is recorded in
    /// <paramref name="unreadable"/> for the caller to carry into
    /// <see cref="PageSymbol.UnreadableBooleanProperties"/> and report. It is recorded rather
    /// than written here because this runs behind a content-addressed on-disk cache, where a
    /// stderr line survives only until the first warm run.</para>
    ///
    /// <para>The two-state siblings above have the same blind spot and are deliberately left
    /// alone: they cannot express it without changing what every existing caller sees, and no
    /// Microsoft-produced symbol file measured states a boolean in any form but "1"/"0".</para>
    /// </summary>
    private static bool? SymbolBoolOrNull(Dictionary<string, string> props, string name, ref List<string>? unreadable)
    {
        if (!props.TryGetValue(name, out var v) || string.IsNullOrWhiteSpace(v)) return null;
        if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
        (unreadable ??= new List<string>()).Add($"{name}={v}");
        return null;
    }

    /// <summary>
    /// Parse one entry of a SymbolReference.json <c>Reports</c> array into the subset the
    /// Report Metadata (2000000139) / Report Data Items (2000000203) virtual tables expose.
    /// This is the ONLY route to a precompiled dependency's report shape: an R2R app ships
    /// no metadata XML, and its 8000-file embedded <c>src/</c> is far too expensive to parse
    /// for this. The symbol file carries the data verbatim (Id, Name, Caption property, the
    /// full DataItems tree with per-item RelatedTable and Indentation), so nothing is
    /// inferred here — a shape the symbol file does not state is left null/absent for the
    /// caller to default, never invented.
    /// </summary>
    /// <param name="alNamespace">The dotted <c>Namespaces</c> tree path this report was reached
    /// through, or null at the root — the only source for its ALNamespace (#3808).</param>
    private static ReportSymbol? TryParseReportSymbol(JsonElement report, string? alNamespace)
    {
        if (!report.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var reportId) || reportId <= 0)
            return null;
        var name = report.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrEmpty(name)) return null;

        var props = SymbolProperties(report);
        props.TryGetValue("Caption", out var caption);
        props.TryGetValue("WordMergeDataItem", out var wordMergeDataItem);
        // #3808 — carried verbatim; the letters are case-significant (see ReportSymbol's comment).
        props.TryGetValue("InherentEntitlements", out var inherentEntitlements);
        props.TryGetValue("InherentPermissions", out var inherentPermissions);
        // AL defaults: ProcessingOnly false, UseRequestPage true. The symbol file only
        // states a property when the AL source declared it.
        bool processingOnly = props.TryGetValue("ProcessingOnly", out var po)
            && (po == "1" || string.Equals(po, "true", StringComparison.OrdinalIgnoreCase));
        bool useRequestPage = !(props.TryGetValue("UseRequestPage", out var urp)
            && (urp == "0" || string.Equals(urp, "false", StringComparison.OrdinalIgnoreCase)));

        var dataItems = new List<ReportDataItemSymbol>();
        CollectReportDataItems(report, indentation: 0, dataItems);

        var referenceSourceFileName = report.TryGetProperty("ReferenceSourceFileName", out var rsf)
            ? rsf.GetString()
            : null;

        return new ReportSymbol(reportId, name, caption, processingOnly, useRequestPage,
            wordMergeDataItem, dataItems, referenceSourceFileName,
            string.IsNullOrWhiteSpace(alNamespace) ? null : alNamespace.Trim(),
            string.IsNullOrWhiteSpace(inherentEntitlements) ? null : inherentEntitlements.Trim(),
            string.IsNullOrWhiteSpace(inherentPermissions) ? null : inherentPermissions.Trim());
    }

    /// <summary>
    /// A SymbolReference.json object reference is <c>#&lt;appIdNoHyphens&gt;#&lt;Name&gt;</c>
    /// whenever it crosses a module boundary, and a plain name within one. A report data
    /// item bound to a table from another module (System's Integer / Company / AllObj,
    /// which is most of them) therefore arrives qualified; leaving the prefix on makes the
    /// table unresolvable and silently drops the report. Same rule as TargetObject on a
    /// tableextension — see BcAppSymbolCache.TableExtensions.cs.
    /// </summary>
    private static string? StripModuleQualifier(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference[0] != '#') return reference;
        var secondHash = reference.IndexOf('#', 1);
        return secondHash >= 0 ? reference.Substring(secondHash + 1) : reference;
    }

    /// <summary>
    /// Flatten a report's data-item tree in declaration order. Nested data items live under
    /// each item's own <c>DataItems</c>; the symbol file also carries an explicit
    /// <c>Indentation</c> on nested entries, which is preferred when present so our depth
    /// count can never disagree with the compiler's own.
    /// </summary>
    private static void CollectReportDataItems(JsonElement container, int indentation, List<ReportDataItemSymbol> into)
    {
        if (!container.TryGetProperty("DataItems", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var di in arr.EnumerateArray())
        {
            var name = di.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var relatedTable = StripModuleQualifier(
                di.TryGetProperty("RelatedTable", out var rt) ? rt.GetString() : null);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(relatedTable)) continue;

            int indent = di.TryGetProperty("Indentation", out var ind) && ind.TryGetInt32(out var iv)
                ? iv
                : indentation;
            int dataItemId = di.TryGetProperty("Id", out var diId) && diId.TryGetInt32(out var idv) ? idv : 0;

            var props = SymbolProperties(di);
            props.TryGetValue("DataItemTableView", out var tableView);
            tableView = RecordPatches.TableViewText(tableView);
            props.TryGetValue("RequestFilterFields", out var filterFields);
            props.TryGetValue("DataItemLink", out var dataItemLink);
            props.TryGetValue("DataItemLinkReference", out var dataItemLinkReference);
            props.TryGetValue("PrintOnlyIfDetail", out var printOnlyIfDetail);
            // Absent, unparseable and "0" all mean the same thing to BC's loop, so they all
            // land on 0 — the value that means "no limit", never a fabricated bound.
            props.TryGetValue("MaxIteration", out var maxIterationText);
            int maxIteration = int.TryParse(
                maxIterationText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var mi) ? mi : 0;

            into.Add(new ReportDataItemSymbol(dataItemId, name, relatedTable, indent, tableView, filterFields,
                ParseReportColumns(di),
                RecordPatches.TableViewText(dataItemLink), dataItemLinkReference,
                printOnlyIfDetail is "1" or "true" or "True",
                maxIteration));
            CollectReportDataItems(di, indent + 1, into);
        }
    }

    /// <summary>
    /// A report data item's <c>Columns</c> array. Each entry states the compiler-assigned
    /// <c>Id</c>, the AL column <c>Name</c> and a <c>TypeDefinition</c> — the resolved AL
    /// type of the column's expression (e.g. <c>Code[20]</c>, <c>Decimal</c>). Only the
    /// leading type name is kept: the length suffix is a property of the expression's
    /// result, not of the report metadata's FieldType vocabulary.
    /// </summary>
    private static List<ReportColumnSymbol> ParseReportColumns(JsonElement dataItem)
    {
        var result = new List<ReportColumnSymbol>();
        if (!dataItem.TryGetProperty("Columns", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var col in arr.EnumerateArray())
        {
            var name = col.TryGetProperty("Name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            int id = col.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var i) ? i : 0;

            string? typeName = null;
            if (col.TryGetProperty("TypeDefinition", out var td)
                && td.TryGetProperty("Name", out var tn))
            {
                typeName = tn.GetString();
                var bracket = typeName?.IndexOf('[');
                if (bracket is > 0) typeName = typeName!.Substring(0, bracket.Value);
            }
            result.Add(new ReportColumnSymbol(id, name!, typeName));
        }
        return result;
    }

    private static QuerySymbol? TryParseQuerySymbol(JsonElement query)
    {
        if (!query.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var queryId))
            return null;
        var name = query.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? $"Query{queryId}" : $"Query{queryId}";
        var props = SymbolProperties(query);
        props.TryGetValue("QueryType", out var queryType);
        props.TryGetValue("Caption", out var caption);
        props.TryGetValue("OrderBy", out var orderBy);
        // #3798 — carried verbatim; the letters are case-significant (see QuerySymbol's comment).
        props.TryGetValue("InherentEntitlements", out var inherentEntitlements);
        props.TryGetValue("InherentPermissions", out var inherentPermissions);
        int top = 0;
        if (props.TryGetValue("TopNumberOfRows", out var topText) && int.TryParse(topText, out var t)) top = t;

        // Root dataitems live under "Elements"; nested ones under "DataItems".
        var dataItems = new List<QueryDataItemSymbol>();
        if (query.TryGetProperty("Elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
            foreach (var el in elements.EnumerateArray())
            {
                var di = TryParseQueryDataItem(el);
                if (di != null) dataItems.Add(di);
            }
        return new QuerySymbol(queryId, name, queryType, caption, orderBy, top, dataItems,
            string.IsNullOrWhiteSpace(inherentEntitlements) ? null : inherentEntitlements.Trim(),
            string.IsNullOrWhiteSpace(inherentPermissions) ? null : inherentPermissions.Trim());
    }

    private static QueryDataItemSymbol? TryParseQueryDataItem(JsonElement el)
    {
        var name = el.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        // #2295: a dataitem bound to a table from another module (Base Application's Item,
        // say) arrives module-qualified (#<appIdNoHyphens>#Item), same as a report dataitem —
        // see StripModuleQualifier's doc comment. Left qualified, ResolveTableIdByName never
        // matches it, BuildMetaQueryDesign abandons the build, and the query is constructed
        // with NCLMetaQuery=NULL — every ALSetRangeSafe/Open/Read on it then NREs.
        var relatedTable = StripModuleQualifier(
            el.TryGetProperty("RelatedTable", out var rtProp) ? rtProp.GetString() : null) ?? string.Empty;
        int id = el.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var i) ? i : 0;
        var props = SymbolProperties(el);
        props.TryGetValue("SqlJoinType", out var sqlJoinType);
        props.TryGetValue("DataItemLink", out var dataItemLink);
        // #3571. Read verbatim; the field-name → field-no resolution needs the dataitem's
        // RelatedTable, which only the query builder has.
        props.TryGetValue("DataItemTableFilter", out var dataItemTableFilter);

        var columns = ParseQueryColumns(el, "Columns");
        var filters = ParseQueryColumns(el, "Filters");

        var nested = new List<QueryDataItemSymbol>();
        if (el.TryGetProperty("DataItems", out var di) && di.ValueKind == JsonValueKind.Array)
            foreach (var child in di.EnumerateArray())
            {
                var c = TryParseQueryDataItem(child);
                if (c != null) nested.Add(c);
            }
        return new QueryDataItemSymbol(id, name, relatedTable, sqlJoinType, dataItemLink, columns, filters, nested, dataItemTableFilter);
    }

    private static List<QueryColumnSymbol> ParseQueryColumns(JsonElement dataItem, string arrayName)
    {
        var result = new List<QueryColumnSymbol>();
        if (!dataItem.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var col in arr.EnumerateArray())
        {
            int id = col.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var i) ? i : 0;
            var name = col.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
            var sourceColumn = col.TryGetProperty("SourceColumn", out var scProp) ? scProp.GetString() ?? string.Empty : string.Empty;
            var props = SymbolProperties(col);
            props.TryGetValue("Caption", out var caption);
            props.TryGetValue("Method", out var method); // issue #2137 — Method = Sum/Count/Average/Min/Max
            props.TryGetValue("ColumnFilter", out var columnFilter); // issue #2418
            var reverseSign = SymbolBool(props, "ReverseSign"); // issue #2575
            result.Add(new QueryColumnSymbol(id, name, sourceColumn, caption, method, columnFilter, reverseSign));
        }
        return result;
    }

    private static ParsedTable? TryParseTableSymbol(JsonElement table)
    {
        if (!table.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var tableId))
            return null;
        var tableName = table.TryGetProperty("Name", out var nameProp)
            ? nameProp.GetString() ?? $"Table{tableId}"
            : $"Table{tableId}";

        var fields = new List<ParsedField>();
        if (table.TryGetProperty("Fields", out var fieldsJson) && fieldsJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fieldsJson.EnumerateArray())
            {
                if (!field.TryGetProperty("Id", out var fidProp) || !fidProp.TryGetInt32(out var fieldId))
                    continue;
                var fieldName = field.TryGetProperty("Name", out var fnameProp)
                    ? fnameProp.GetString() ?? $"Field{fieldId}"
                    : $"Field{fieldId}";
                var typeName = SymbolTypeName(field.TryGetProperty("TypeDefinition", out var td) ? td : default);
                var props = SymbolProperties(field);
                var isFlowField = props.TryGetValue("FieldClass", out var fieldClass)
                    && string.Equals(fieldClass, "FlowField", StringComparison.OrdinalIgnoreCase);
                // #1716 — carry FlowFilter through too. The ~105 Base Application FlowFields
                // that read a flow filter reach their FlowFilter field through THIS path, and
                // FlowFieldsHelper dispatches on the value field's FieldClass; a FlowFilter
                // field arriving as Normal is read as a stored (always blank) value instead.
                var isFlowFilter = props.TryGetValue("FieldClass", out var fieldClass2)
                    && string.Equals(fieldClass2, "FlowFilter", StringComparison.OrdinalIgnoreCase);
                ParsedCalcFormula? calcFormula = null;
                if (isFlowField && props.TryGetValue("CalcFormula", out var calcFormulaText))
                    calcFormula = RecordPatches.TryParseCalcFormula($"CalcFormula = {calcFormulaText};");
                props.TryGetValue("OptionMembers", out var optionMembers);
                props.TryGetValue("InitValue", out var initValue);
                var isAutoIncrement = props.TryGetValue("AutoIncrement", out var autoIncrement)
                    && (autoIncrement == "1" || autoIncrement.Equals("true", StringComparison.OrdinalIgnoreCase));
                props.TryGetValue("MinValue", out var minValue); // #2495
                props.TryGetValue("MaxValue", out var maxValue);
                // #2528 — TableRelation, re-parsed from the property TEXT the same way
                // CalcFormula above is. Without this every field of every PRECOMPILED table
                // reported FieldRef.Relation = 0 and Validate() skipped the relation check, so
                // `Customer.Validate("Currency Code", 'NOSUCHCUR')` silently ACCEPTED a value
                // real BC refuses. 7,787 Base Application fields carry one.
                // ValidateTableRelation = "0" turns the check off while leaving the relation
                // itself readable (Customer.City is exactly that shape), so the two properties
                // are read independently — matching the AL-source path's own two lines.
                props.TryGetValue("TableRelation", out var tableRelation);
                // Gated on field class exactly as the AL-source path is
                // (RecordPatches.AlSourceParser.cs's `if (!isFlowField && !isFlowFilter && ...)`).
                // A FlowFilter's TableRelation is a LOOKUP hint for the filter's own UI, not a
                // stored value's referential constraint, and in Base Application 28.1 that is 204
                // fields (196 FlowFilter, 8 FlowField), ~144 of them with a relation this parser
                // accepts — "Item Statistics Buffer"."Item Filter" -> Item, "Analysis
                // Line"."Location Filter" -> Location, "Config. Line"."Company Filter" -> Company.
                // ParsedField.RelationArms feeds BOTH the Validate check AND the reverse index
                // NCLMetaTable_ComputeReferencingRelations builds for rename propagation, and that
                // index filters only on TableId >= 2000000000, not on field class — so without
                // this gate renaming an Item, Location or Company would pull FlowFilter
                // pseudo-columns into the cascade. The invariant this whole change is for is that
                // the source-parsed and symbol-read paths agree; ungated, they would disagree for
                // exactly these 204 fields, and the source path is the one the corpus validates.
                var relationArms = (!isFlowField && !isFlowFilter)
                    ? RecordPatches.TryParseRelationArmsText(tableRelation, fieldName)
                    : null;
                var relationValidate = !(props.TryGetValue("ValidateTableRelation", out var vtr)
                    && (vtr == "0" || vtr.Equals("false", StringComparison.OrdinalIgnoreCase)));
                // #3545 — Editable / DataClassification / the enum type. All three are stated
                // in the symbol file and all three were dropped here; see
                // docs/metadata-equivalence.md#three-symbol-properties-the-reader-dropped for
                // what each one costs AL and how the rules were measured.
                var editable = SymbolEditable(props);
                props.TryGetValue("DataClassification", out var fieldDataClassification);
                var (enumTypeId, enumTypeName) = SymbolEnumType(
                    field.TryGetProperty("TypeDefinition", out var td2) ? td2 : default);
                fields.Add(new ParsedField(fieldId, fieldName, typeName, SymbolTypeLength(typeName), isFlowField, calcFormula,
                    optionMembers, initValue, isAutoIncrement, IsFlowFilter: isFlowFilter,
                    RelationArms: relationArms, RelationValidate: relationValidate,
                    MinValue: minValue, MaxValue: maxValue,
                    Editable: editable,
                    DataClassificationName: string.IsNullOrWhiteSpace(fieldDataClassification)
                        ? null : fieldDataClassification.Trim(),
                    EnumTypeId: enumTypeId, EnumTypeName: enumTypeName));
            }
        }

        var pkFieldIds = new List<int>();
        var secondaryKeys = new List<ParsedKey>();
        ParsedKey? primaryKey = null;
        if (table.TryGetProperty("Keys", out var keysJson) && keysJson.ValueKind == JsonValueKind.Array)
        {
            var first = true;
            foreach (var key in keysJson.EnumerateArray())
            {
                var keyName = key.TryGetProperty("Name", out var keyNameProp)
                    ? keyNameProp.GetString() ?? "Key"
                    : "Key";
                var ids = new List<int>();
                if (key.TryGetProperty("FieldNames", out var fieldNames) && fieldNames.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fieldNameJson in fieldNames.EnumerateArray())
                    {
                        var fieldName = fieldNameJson.GetString();
                        var field = fields.FirstOrDefault(f =>
                            string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));
                        if (field != null) ids.Add(field.FieldId);
                    }
                }
                // #3568 — the key's own properties, which BC propagates into the live
                // NCLMetaKey via CreateFromMetaKey and this reader used to discard. The
                // symbol file states booleans as "1"/"0" and SumIndexFields as a
                // "Field<id>,Field<id>" list of field IDS — not names, which is how the AL
                // SOURCE form states the same property (RecordPatches.AlSourceParser).
                var keyProps = SymbolProperties(key);
                bool? clustered = keyProps.TryGetValue("Clustered", out var cl)
                    ? cl == "1" || string.Equals(cl, "true", StringComparison.OrdinalIgnoreCase)
                    : null;
                var unique = keyProps.TryGetValue("Unique", out var uq)
                    && (uq == "1" || string.Equals(uq, "true", StringComparison.OrdinalIgnoreCase));
                var siftIds = keyProps.TryGetValue("SumIndexFields", out var sift)
                    ? ParseFieldIdList(sift)
                    : null;
                var parsedKey = new ParsedKey(keyName, ids, clustered, unique, siftIds);
                if (first)
                {
                    pkFieldIds.AddRange(ids);
                    primaryKey = parsedKey;
                    first = false;
                }
                else if (ids.Count > 0)
                {
                    secondaryKeys.Add(parsedKey);
                }
            }
        }
        if (pkFieldIds.Count == 0 && fields.Count > 0)
            pkFieldIds.Add(fields[0].FieldId);

        var tableProps = SymbolProperties(table);
        var isTemporary = tableProps.TryGetValue("TableType", out var tableType)
            && string.Equals(tableType, "Temporary", StringComparison.OrdinalIgnoreCase);
        // Page-resolution properties for the Table Metadata (2000000136) virtual table. The
        // symbol file states these as the page's NAME, not its id, and is inconsistent about
        // the trailing casing — Base Application 28.1 carries both "LookupPageID" and
        // "LookupPageId" across different tables. SymbolProperties is case-insensitive, so
        // one lookup covers both spellings; the name is resolved to an id at row-build time.
        tableProps.TryGetValue("LookupPageId", out var lookupPageName);
        tableProps.TryGetValue("DrillDownPageId", out var drillDownPageName);
        // DataClassification / ExternalName feed the Table Metadata (2000000136) columns of the
        // same name (#2938). The symbol file is the ONLY route for a precompiled dependency —
        // an R2R .app ships no metadata XML — and it states both as plain text matching the
        // column's own option members / the external name verbatim. Measured on Base
        // Application 28.1: 1510 of its 1523 tables state DataClassification (1447
        // CustomerContent, 61 SystemMetadata, 2 OrganizationIdentifiableInformation) and 61
        // state ExternalName ("CDS BC Table Relation" -> dyn365bc_syntheticrelation).
        tableProps.TryGetValue("DataClassification", out var dataClassification);
        // #3545 — a field that declares no DataClassification takes the TABLE's. Done here,
        // after the table's own property is in hand, so ParsedField.DataClassificationName is
        // the value BC's emitter states rather than only what the field wrote.
        RecordPatches.ApplyOwnerDataClassification(fields, dataClassification);
        tableProps.TryGetValue("ExternalName", out var externalName);
        // DataPerCompany was hardcoded true here while the SOURCE-parsed path read the declared
        // property — the two paths writing the same column disagreed, so every precompiled table
        // declaring DataPerCompany = false was handed out as per-company. That is 41 of Base
        // Application 28.1's tables (the symbol file states the AL false as "0", the same
        // spelling ReplicateData uses). AL's default is true, so only the explicit opt-out is
        // read, exactly as TryParseTableFile does it (#2938).
        var dataPerCompany = !(tableProps.TryGetValue("DataPerCompany", out var dpc)
            && (dpc == "0" || dpc.Equals("false", StringComparison.OrdinalIgnoreCase)));
        return new ParsedTable(tableId, tableName, fields, pkFieldIds, secondaryKeys, isTemporary,
            DataPerCompany: dataPerCompany,
            LookupPageName: string.IsNullOrWhiteSpace(lookupPageName) ? null : lookupPageName,
            DrillDownPageName: string.IsNullOrWhiteSpace(drillDownPageName) ? null : drillDownPageName,
            TableTypeName: string.IsNullOrWhiteSpace(tableType) ? null : tableType.Trim(),
            DataClassificationName: string.IsNullOrWhiteSpace(dataClassification) ? null : dataClassification.Trim(),
            ExternalName: string.IsNullOrWhiteSpace(externalName) ? null : externalName.Trim(),
            PrimaryKey: primaryKey);
    }

    /// <summary>
    /// <see cref="TryParseEnumSymbol"/> for AlRunner.Tests. A seam rather than widening the
    /// parser's own accessibility: the one property worth pinning directly is that an enum
    /// declaring no <c>Values</c> survives (#3594), and reaching it through a real .app would
    /// make the test depend on which BC build is provisioned.
    /// </summary>
    internal static EnumSymbol? ParseEnumSymbolForTest(JsonElement enumType)
        => TryParseEnumSymbol(enumType);

    private static EnumSymbol? TryParseEnumSymbol(JsonElement enumType)
    {
        if (!enumType.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var id))
            return null;
        var name = enumType.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        // #3594 — an enum with NO `Values` array is a real enum that declares no values, not an
        // unreadable symbol. Extensible enums whose members all come from enumextensions are
        // written exactly this way: measured on System Application 28.1, 3 of its 141 enums
        // carry no `Values` at all, and enum 8889 "Email Connector" is one of them.
        //
        // Returning null here dropped those three from the registry entirely. That was latent
        // for as long as nothing resolved an enum BY ID — the ordinal/caption consumers only
        // ever ask about enums they already hold — and became observable the moment MetaField
        // began stating EnumTypeId, because BC's own metadata names 8889 on five System
        // Application tables and the runner then had no object under it. An empty value list is
        // the faithful answer: the enum exists, and it has no members of its own.
        var hasValues = enumType.TryGetProperty("Values", out var values)
                        && values.ValueKind == JsonValueKind.Array;

        var options = new List<string>();
        var indexes = new List<int>();
        var implementations = new List<List<int>>();
        var captions = new List<string?>();
        foreach (var value in hasValues
                     ? values.EnumerateArray().Cast<JsonElement>()
                     : Enumerable.Empty<JsonElement>())
        {
            var optionName = value.TryGetProperty("Name", out var optionNameProp)
                ? optionNameProp.GetString() ?? string.Empty
                : string.Empty;
            // #3805 — absent `Ordinal` means ZERO, the symbol file's absent-means-default
            // convention (the same one PermissionSymbol records for PermissionObject). It is
            // NOT "the previous ordinal plus one": the values are not necessarily emitted in
            // ordinal order, and System Application 2616 "Printer Paper Kind" emits them in
            // NAME order with Custom (ordinal 0) last, where the source-order rule answered 40
            // and collided with GermanStandardFanfold. Measured on 28.1 and 28.4: 681 values
            // state no Ordinal and this rule agrees with BC's emitted metadata on all of them.
            var ordinal = value.TryGetProperty("Ordinal", out var ordinalProp) && ordinalProp.TryGetInt32(out var explicitOrdinal)
                ? explicitOrdinal
                : 0;
            options.Add(optionName);
            indexes.Add(ordinal);
            var implementationIds = new List<int>();
            var props = SymbolProperties(value);
            if (props.TryGetValue("Implementation", out var implementationText))
            {
                foreach (var part in implementationText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(part, out var implementationId))
                        implementationIds.Add(implementationId);
                }
            }
            implementations.Add(implementationIds);
            // Issue #1775 — a value's declared Caption, same SymbolProperties read the
            // report/query/field Caption capture already uses elsewhere in this file.
            // Missing/empty means "declares none"; the consumer (AlEnumOptionMetadata.
            // GetCaptionFromIndex) falls back to the member name for that case.
            captions.Add(props.TryGetValue("Caption", out var captionText) && !string.IsNullOrEmpty(captionText)
                ? captionText
                : null);
        }
        // Enum-level fallbacks. Both are written the same way a value's Implementation is —
        // a comma-separated list of codeunit ids, one per interface the enum implements.
        var enumProps = SymbolProperties(enumType);
        // #3807 — Extensible is written as "1"/"0" in the same bag, and absent stays absent.
        // Measured on 28.1.49838.53910 and 28.4.53241.54407 (two distinct binaries, identical
        // counts): of 142 System Application + Business Foundation enums, 130 state the
        // property and 31 of those state "1". The runner carried it nowhere at all, so BC
        // applied its own default of false for all 142 — right for 111 by accident and wrong
        // for the 31 that are extensible.
        return new EnumSymbol(id, name, options, indexes, implementations, captions,
            SymbolCodeunitIdList(enumProps, "DefaultImplementation"),
            SymbolCodeunitIdList(enumProps, "UnknownValueImplementation"),
            SymbolBoolean(enumProps, "Extensible"));
    }

    /// <summary>
    /// The named property read as an AL boolean, or <c>null</c> when the enum declares it not
    /// at all — a third state the caller must keep, not collapse (see
    /// <see cref="EnumSymbol"/>'s <c>Extensible</c>).
    ///
    /// <para><b>Why this is not <c>SymbolBoolFalse</c>, which the page side uses for the
    /// SAME property name.</b> That reader folds absence into the AL default, and its comment
    /// gives the reason: BC's emitter writes a page's <c>Extensible</c> unconditionally, so
    /// absent and default-valued are the same document. For ENUMS the emitter does not — 12 of
    /// 144 emitted enum documents state no <c>Extensible</c> attribute at all (28.1 and 28.4) —
    /// so folding here would make the render state an attribute BC omits. The divergence is
    /// measured, not accidental.</para>
    ///
    /// <para>Both spellings are accepted because both occur: <c>SymbolReference.json</c> states
    /// <c>Extensible</c> as <c>"1"</c>/<c>"0"</c> on every one of the 130 System Application +
    /// Business Foundation enums that declare it (measured on 28.1 and 28.4), while AL source
    /// and several other property bags spell booleans <c>"true"</c>/<c>"false"</c>. An
    /// unrecognised spelling reads as <c>false</c> rather than <c>true</c> or null: it IS a
    /// declaration, so it is not the absent case, and the true direction is the one that
    /// changes what AL is allowed to do.</para>
    /// </summary>
    private static bool? SymbolBoolean(Dictionary<string, string> props, string propertyName)
    {
        if (!props.TryGetValue(propertyName, out var text) || string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim();
        return t == "1" || string.Equals(t, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The named property read as a comma-separated list of codeunit ids, or null
    /// when it is absent — "declares none", which is how most enums are written.</summary>
    private static List<int>? SymbolCodeunitIdList(Dictionary<string, string> props, string propertyName)
    {
        if (!props.TryGetValue(propertyName, out var text) || string.IsNullOrWhiteSpace(text))
            return null;
        var ids = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var id))
                ids.Add(id);
        return ids.Count > 0 ? ids : null;
    }

    /// <summary>
    /// A symbol-file <c>"Field11,Field12"</c> field-id list (#3568). Returns null when nothing
    /// parses, so an unrecognised spelling leaves the property absent rather than silently
    /// becoming an empty list, which would read as "declared, and empty".
    /// </summary>
    private static List<int>? ParseFieldIdList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var ids = new List<int>();
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.StartsWith("Field", StringComparison.OrdinalIgnoreCase)) t = t.Substring(5);
            if (int.TryParse(t, out var id)) ids.Add(id);
        }
        return ids.Count > 0 ? ids : null;
    }

    private static Dictionary<string, string> SymbolProperties(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!element.TryGetProperty("Properties", out var props) || props.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var prop in props.EnumerateArray())
        {
            if (!prop.TryGetProperty("Name", out var nameProp)) continue;
            var name = nameProp.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            if (prop.TryGetProperty("Value", out var valueProp))
                result[name] = valueProp.GetString() ?? string.Empty;
        }
        return result;
    }

    private static string SymbolTypeName(JsonElement typeDefinition)
    {
        if (typeDefinition.ValueKind != JsonValueKind.Object)
            return "Text";
        var name = typeDefinition.TryGetProperty("Name", out var nameProp)
            ? nameProp.GetString() ?? "Text"
            : "Text";
        if (string.Equals(name, "Enum", StringComparison.OrdinalIgnoreCase)
            && typeDefinition.TryGetProperty("Subtype", out var subtype)
            && subtype.ValueKind == JsonValueKind.Object
            && subtype.TryGetProperty("Name", out var enumNameProp))
            return $"Enum \"{enumNameProp.GetString() ?? string.Empty}\"";
        return name;
    }

    /// <summary>
    /// The field's declared <c>Editable</c>, or null when the symbol file states none — which
    /// AL reads as true (#3545). Measured over 3,848 table-declared field observations on
    /// four BC builds (plus 40 extension-added ones; #3603 has the populations):
    /// the symbol file states <c>Editable</c> only where it is <c>0</c>, and BC's own emitter
    /// answers <c>1</c> or nothing everywhere it is silent, with zero disagreements. So the
    /// only value worth carrying is the false, and null is passed on unchanged so
    /// MetaField's own null default decides — not a true this reader made up.
    /// </summary>
    private static bool? SymbolEditable(Dictionary<string, string> props)
    {
        if (!props.TryGetValue("Editable", out var v) || string.IsNullOrWhiteSpace(v)) return null;
        if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        return null;
    }

    /// <summary>
    /// The enum object a field's <c>TypeDefinition</c> names — <c>(id, name)</c> when
    /// <c>TypeDefinition.Name == "Enum"</c>, <c>(0, null)</c> otherwise. Measured: the
    /// presence of <c>Subtype.Id</c> and the presence of BC's own emitted
    /// <c>EnumTypeId</c> agree on 3,848 of 3,848 table-declared field observations, and the
    /// values agree wherever both are present (#3545). #3603 has what that population is and
    /// what it excludes.
    /// </summary>
    private static (int Id, string? Name) SymbolEnumType(JsonElement typeDefinition)
    {
        if (typeDefinition.ValueKind != JsonValueKind.Object) return (0, null);
        if (!typeDefinition.TryGetProperty("Name", out var nameProp)
            || !string.Equals(nameProp.GetString(), "Enum", StringComparison.OrdinalIgnoreCase))
            return (0, null);
        if (!typeDefinition.TryGetProperty("Subtype", out var subtype)
            || subtype.ValueKind != JsonValueKind.Object)
            return (0, null);
        var id = subtype.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var i) ? i : 0;
        var name = subtype.TryGetProperty("Name", out var snProp) ? snProp.GetString() : null;
        return (id, string.IsNullOrEmpty(name) ? null : name);
    }

    private static int SymbolTypeLength(string typeName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(typeName, @"\[(\d+)\]");
        return m.Success && int.TryParse(m.Groups[1].Value, out var length) ? length : 0;
    }

    private static IEnumerable<string> ReadSymbolReferences(string appPath)
    {
        var bytes = File.ReadAllBytes(appPath);
        foreach (var json in ReadSymbolReferencesFromBytes(bytes))
            yield return json;
    }

    private static IEnumerable<string> ReadSymbolReferencesFromBytes(byte[] bytes)
    {
        using var zip = OpenZipFromNavx(bytes);
        var symbol = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("SymbolReference.json", StringComparison.OrdinalIgnoreCase));
        if (symbol != null)
        {
            using var s = symbol.Open();
            using var reader = new StreamReader(s);
            yield return reader.ReadToEnd();
        }

        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (nested != null)
        {
            using var ns = nested.Open();
            using var ms = new MemoryStream();
            ns.CopyTo(ms);
            foreach (var json in ReadSymbolReferencesFromBytes(ms.ToArray()))
                yield return json;
        }
    }

    /// <summary>
    /// Read ONE AL source file out of a dependency .app's embedded <c>src/</c> tree.
    ///
    /// A published .app carries the app's full AL source, but a Base-Application-sized one
    /// holds ~8000 files — reading the tree to answer a question about a single object is
    /// why the report symbol parsing deliberately never did it. This is the targeted form:
    /// SymbolReference.json states each report's own <c>ReferenceSourceFileName</c>, so the
    /// caller already knows the one entry it wants and this opens exactly that.
    ///
    /// The stated path is app-root-relative (<c>src/Foo/Bar.Report.al</c>) while the zip
    /// nests it under its own prefix (<c>src/src/Foo/Bar.Report.al</c>), so entries are
    /// matched on suffix. Returns null when the app ships no source for it — a symbols-only
    /// .app is a legitimate shape, not an error.
    /// </summary>
    internal static string? TryReadSourceFile(string appPath, string referenceSourceFileName)
    {
        if (string.IsNullOrEmpty(referenceSourceFileName)) return null;
        var wanted = referenceSourceFileName.Replace('\\', '/').TrimStart('/');
        try
        {
            return TryReadSourceFromBytes(File.ReadAllBytes(appPath), wanted);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[BcAppSymbolCache] source read failed for {Path.GetFileName(appPath)}!{wanted}: {ex.Message}");
            return null;
        }
    }

    private static string? TryReadSourceFromBytes(byte[] bytes, string wanted)
    {
        using var zip = OpenZipFromNavx(bytes);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').EndsWith(wanted, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        // R2R wrapper .app: the real package (with its src/ tree) is nested one level in.
        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (nested == null) return null;
        using var ns = nested.Open();
        using var ms = new MemoryStream();
        ns.CopyTo(ms);
        return TryReadSourceFromBytes(ms.ToArray(), wanted);
    }

    private static ZipArchive OpenZipFromNavx(byte[] bytes)
    {
        var offset = bytes.Length >= 8
            && bytes[0] == (byte)'N' && bytes[1] == (byte)'A'
            && bytes[2] == (byte)'V' && bytes[3] == (byte)'X'
                ? (int)BitConverter.ToUInt32(bytes, 4)
                : 0;
        // A runtime package's payload is a .NEA container, not a bare zip (#3537). This
        // reader is the one that reaches SymbolReference.json, which a runtime package does
        // ship, so it has to see through that layer as well as AppLoader's does. The decode
        // itself lives in AppLoader so the format has one implementation.
        if (AlRunner.AppLoader.IsNeaContainer(bytes, offset))
        {
            var decoded = AlRunner.AppLoader.NeaDecode(bytes, offset + AlRunner.AppLoader.NeaHeaderLength);
            return new ZipArchive(new MemoryStream(decoded, writable: false), ZipArchiveMode.Read);
        }
        var ms = new MemoryStream(bytes, offset, bytes.Length - offset, writable: false);
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static string CachePath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        // #1821: was hardcoded to ~/.cache/al-runner/bc-symbols regardless of --cache;
        // now follows the same isolation root al-out already honoured.
        return Path.Combine(CacheRoots.Resolve("bc-symbols"), hash + ".json");
    }

}
