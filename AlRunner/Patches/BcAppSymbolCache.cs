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
    // v27: the SAME RelationArms field now carries MORE of what the SAME SymbolReference.json
    // already said (#2518). Until this bump the parser refused any arm whose where(...) named
    // a `field(...)` link and dropped the WHOLE relation, so 826 Base Application 28.1
    // relations — Customer.City among them — were cached as RelationArms = null. That
    // deserialises as "this field declares no TableRelation": FieldRef.Relation answers 0,
    // RapidStart's Relation Table ID stays 0, and Validate() skips the relation check. The
    // schema did not change, so a v26 payload loads without error and replays the pre-fix
    // wrong answer instead of missing — which is precisely what the bump is for.
    private const int CacheVersion = 27;
    private static readonly ConcurrentDictionary<string, AppSymbols> ProcessCache = new(StringComparer.OrdinalIgnoreCase);
    // Issue #1820 — path -> content-hash memo. ComputeAppContentHash needs to read the
    // WHOLE .app to hash it (unlike the FileInfo.Length/LastWriteTimeUtc stat it replaced,
    // which touched no file bytes), and Get() is called once per virtual-table lookup —
    // often a dozen or more times for the SAME dependency .app within a single run (Pages,
    // Tables, Enums, Objects, Reports, Queries each have their own call site). Caching the
    // hash per full path means that cost is paid once per unique .app per process, not once
    // per Get() call. Content doesn't change mid-run (nothing in this process writes to a
    // dependency .app), so no invalidation beyond "one entry per path" is needed.
    private static readonly ConcurrentDictionary<string, string> AppContentHashCache = new(StringComparer.OrdinalIgnoreCase);

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
        AppContentHashCache.Clear();
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
    internal sealed record PermissionSetSymbol(int Id, string Name, string? Caption, bool Assignable);

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
        Dictionary<int, string>? MemberIdToActionRefTarget = null);

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
        Dictionary<int, string> MemberIdToActionRefTarget);

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
        List<PageSubFormLinkSymbol> SubFormLink);

    /// <summary>
    /// One entry of a part's <c>SubPageLink</c> property, still as AL source text.
    /// <c>PartFieldName</c> is the part's own field (quotes stripped); <c>Kind</c> is
    /// "field"/"const"/"filter", exactly the AL keyword, lowercased; <c>Value</c> is
    /// everything inside the parens verbatim — a parent field name (quotes intact) for
    /// "field", the literal / expression text for "const"/"filter" (normalised to the
    /// compiled representation by RecordPatches.DependencyPageMetadataXml's
    /// EmitSubFormLinkXml, then applied by MockTestPage.SubPageLinks — #2469).
    /// </summary>
    internal sealed record PageSubFormLinkSymbol(string PartFieldName, string Kind, string Value);

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
        string? ReferenceSourceFileName = null);

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
        bool PrintOnlyIfDetail = false);

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
    /// <summary>
    /// <para>The three trailing properties are populated for <c>Codeunit</c> only, and feed the
    /// CodeUnit Metadata (2000000137) virtual table (issue #2544). Every other kind leaves them
    /// at their defaults — which is also what a codeunit declaring none of them means.
    /// <c>TableNo</c> is the reference AS WRITTEN (a bare id in text form, or a table name);
    /// resolving it to an id needs the run's table inventory, so the consumer does that.</para>
    /// </summary>
    internal sealed record ObjectSymbol(string Kind, int Id, string Name, string? Caption = null,
        string? TableNo = null, bool SingleInstance = false, string? Subtype = null);

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
    internal sealed record EnumSymbol(int Id, string Name, List<string> Options, List<int> Indexes, List<List<int>> Implementations, List<string?>? Captions = null,
        List<int>? DefaultImplementations = null, List<int>? UnknownImplementations = null);

    // Parsed query SymbolReference.json shape. A query is a tree of dataitems; the root
    // dataitem(s) live under the query's "Elements", nested dataitems under "DataItems".
    // Column/Filter Id is the BC-compiler-assigned column id baked into precompiled callers
    // (NavQuery.ValidateExpectedType(columnId,...)/GetColumnValueSafe) — it MUST be used verbatim.
    internal sealed record QuerySymbol(
        int Id, string Name, string? QueryType, string? Caption, string? OrderBy,
        int TopNumberOfRowsToReturn, List<QueryDataItemSymbol> DataItems);

    internal sealed record QueryDataItemSymbol(
        int Id, string Name, string RelatedTable, string? SqlJoinType, string? DataItemLink,
        List<QueryColumnSymbol> Columns, List<QueryColumnSymbol> Filters,
        List<QueryDataItemSymbol> DataItems);

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

    internal static AppSymbols Get(string appPath)
    {
        var fullPath = Path.GetFullPath(appPath);
        var contentHash = ComputeAppContentHash(fullPath);
        var key = $"{fullPath}|hash:{contentHash}|v{CacheVersion}";
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
        => CachePath($"{Path.GetFullPath(appPath)}|hash:{contentHash}|v{cacheVersion}");

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
    /// Memoized per full path in <see cref="AppContentHashCache"/> — see that field's
    /// comment for why a per-Get()-call hash would be a regression, not just correct.
    /// </summary>
    internal static string ComputeAppContentHash(string appPath)
    {
        var fullPath = Path.GetFullPath(appPath);
        return AppContentHashCache.GetOrAdd(fullPath, static p => RunnerFingerprint.ComputeContentHash(p));
    }

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
                    // AL's `Assignable` defaults to true; only an explicit false flips it
                    // (Base Application's "LOCAL" states `Assignable = false`, while
                    // "D365 Basic - Edit" states nothing and is assignable). Table
                    // 2000000250's own field 4 carries `InitValue = true` for the same reason.
                    into.TryAdd(id, new PermissionSetSymbol(id, name, caption, !SymbolBoolFalse(props, "Assignable")));
                }
            }
            if (container.TryGetProperty("Namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Array)
                foreach (var ns in namespaces.EnumerateArray())
                    Visit(ns);
        }
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

    private static void VisitSymbolContainer(JsonElement container, Dictionary<int, ParsedTable> tables, Dictionary<int, EnumSymbol> enums, Dictionary<int, QuerySymbol> queries, Dictionary<(string, int), ObjectSymbol> objects, Dictionary<int, ReportSymbol> reports, Dictionary<int, PageSymbol> pages, Dictionary<string, ProfileSymbol> profiles, Dictionary<int, PageExtensionSymbol> pageExtensions)
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
                    objProps.TryGetValue("SingleInstance", out var cuSingleInstance);
                    objProps.TryGetValue("Subtype", out var cuSubtype);
                    objects.TryAdd((kind, objId), new ObjectSymbol(kind, objId, objName, objCaption,
                        // Left as written; StripModuleQualifier is the consumer's job, the same
                        // split the query/report data-item RelatedTable reads already make.
                        TableNo: string.IsNullOrWhiteSpace(cuTableNo) ? null : cuTableNo.Trim(),
                        // AL's default is false; only an explicit "true" sets it.
                        SingleInstance: string.Equals(cuSingleInstance?.Trim(), "true",
                            StringComparison.OrdinalIgnoreCase),
                        Subtype: string.IsNullOrWhiteSpace(cuSubtype) ? null : cuSubtype.Trim()));
                    continue;
                }
                objects.TryAdd((kind, objId), new ObjectSymbol(kind, objId, objName, objCaption));
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
                var parsed = TryParseReportSymbol(r);
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
                VisitSymbolContainer(ns, tables, enums, queries, objects, reports, pages, profiles, pageExtensions);
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
        int seq = 0;
        if (page.TryGetProperty("Controls", out var controlsArr) && controlsArr.ValueKind == JsonValueKind.Array)
            foreach (var c in controlsArr.EnumerateArray())
            {
                CollectPageControlSymbols(c, controls, ref seq);
                CollectPagePartSymbols(c, parts);
                CollectMemberNames(c, "Controls", memberNames, actionRefTargets);
            }
        if (page.TryGetProperty("Actions", out var actionsArr) && actionsArr.ValueKind == JsonValueKind.Array)
            foreach (var a in actionsArr.EnumerateArray())
                CollectMemberNames(a, "Actions", memberNames, actionRefTargets);

        return new PageSymbol(pageId, name!, sourceTableId, sourceTableTemporary,
            string.IsNullOrWhiteSpace(pageType) ? "Card" : pageType!, caption,
            editable, insertAllowed, modifyAllowed, deleteAllowed, controls,
            string.IsNullOrWhiteSpace(cardPageName) ? null : cardPageName,
            autoSplitKey, multipleNewLines, delayedInsert, parts,
            memberNames, actionRefTargets);
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
        if (ext.TryGetProperty("ActionChanges", out var actionChanges) && actionChanges.ValueKind == JsonValueKind.Array)
            foreach (var change in actionChanges.EnumerateArray())
                if (change.TryGetProperty("Actions", out var added) && added.ValueKind == JsonValueKind.Array)
                    foreach (var a in added.EnumerateArray())
                        CollectMemberNames(a, "Actions", memberNames, actionRefTargets);
        if (ext.TryGetProperty("ControlChanges", out var controlChanges) && controlChanges.ValueKind == JsonValueKind.Array)
            foreach (var change in controlChanges.EnumerateArray())
                if (change.TryGetProperty("Controls", out var added) && added.ValueKind == JsonValueKind.Array)
                    foreach (var c in added.EnumerateArray())
                        CollectMemberNames(c, "Controls", memberNames, actionRefTargets);

        return new PageExtensionSymbol(extId, name!, StripModuleQualifierPrefix(target!), memberNames, actionRefTargets);
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
        Dictionary<int, string> names, Dictionary<int, string> actionRefTargets)
    {
        if (node.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var id) && id != 0
            && node.TryGetProperty("Name", out var nameProp) && nameProp.GetString() is { Length: > 0 } name)
        {
            names.TryAdd(id, name);
            if (node.TryGetProperty("TargetName", out var targetProp) && targetProp.GetString() is { Length: > 0 } target)
                actionRefTargets.TryAdd(id, target);
        }
        if (node.TryGetProperty(childKey, out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectMemberNames(child, childKey, names, actionRefTargets);
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
                sequence++;
                props.TryGetValue("Visible", out var visible);
                props.TryGetValue("Editable", out var editable);
                props.TryGetValue("Enabled", out var enabled);
                into.Add(new PageControlSymbol(id, name!, srcExpr, visible, editable, enabled, sequence));
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
                into.Add(new PagePartSymbol(id, name!, partPageId,
                    string.IsNullOrEmpty(caption) ? null : caption,
                    string.IsNullOrEmpty(editable) ? null : editable,
                    string.IsNullOrEmpty(enabled) ? null : enabled,
                    string.IsNullOrEmpty(visible) ? null : visible,
                    string.IsNullOrEmpty(showFilter) ? null : showFilter,
                    ParseSubPageLink(subPageLink)));
            }
        }

        if (control.TryGetProperty("Controls", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectPagePartSymbols(child, into);
    }

    private static readonly System.Text.RegularExpressions.Regex SubPageLinkEntryRegex = new(
        @"^(?<left>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<kind>field|const|filter)\((?<val>.*)\)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parse a <c>SubPageLink</c> property's raw AL text — e.g. <c>"Document No." =
    /// field("No.")</c>, or a comma-separated list of such pairs — into (part field, kind,
    /// value) triples. Measured across Base Application 28.1's 1311 SubPageLink entries:
    /// 1168 are <c>field(...)</c>, 140 are <c>const(...)</c>, 3 are <c>filter(...)</c>; an
    /// entry this cannot parse is dropped and reported on stderr; the three kinds the regex
    /// accepts are the only ones AL defines, so a drop here means AL text this parser has
    /// never seen, not a link kind the runner declines.
    /// </summary>
    private static List<PageSubFormLinkSymbol> ParseSubPageLink(string? text)
    {
        var result = new List<PageSubFormLinkSymbol>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var rawEntry in SplitTopLevelCommas(text))
        {
            var entry = rawEntry.Trim();
            if (entry.Length == 0) continue;
            var m = SubPageLinkEntryRegex.Match(entry);
            if (!m.Success)
            {
                Console.Error.WriteLine($"[BcAppSymbolCache] SubPageLink entry not understood, dropped: '{entry}'");
                continue;
            }
            var partField = m.Groups["left"].Value.Trim('"');
            var kind = m.Groups["kind"].Value.ToLowerInvariant();
            var value = m.Groups["val"].Value.Trim();
            result.Add(new PageSubFormLinkSymbol(partField, kind, value));
        }
        return result;
    }

    /// <summary>
    /// Split a SubPageLink's comma-separated entries, ignoring a comma that falls inside a
    /// quoted field name or inside a nested <c>(...)</c> (e.g. <c>const(Database::"Purchase
    /// Header")</c> or <c>filter(Open | "X")</c> — neither is comma-separated internally in
    /// the corpus, but a nested nested-paren value like <c>field("A, B")</c> would otherwise
    /// split wrongly).
    /// </summary>
    private static IEnumerable<string> SplitTopLevelCommas(string text)
    {
        var normalized = text.Replace("\r\n", " ").Replace('\n', ' ');
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
    /// Parse one entry of a SymbolReference.json <c>Reports</c> array into the subset the
    /// Report Metadata (2000000139) / Report Data Items (2000000203) virtual tables expose.
    /// This is the ONLY route to a precompiled dependency's report shape: an R2R app ships
    /// no metadata XML, and its 8000-file embedded <c>src/</c> is far too expensive to parse
    /// for this. The symbol file carries the data verbatim (Id, Name, Caption property, the
    /// full DataItems tree with per-item RelatedTable and Indentation), so nothing is
    /// inferred here — a shape the symbol file does not state is left null/absent for the
    /// caller to default, never invented.
    /// </summary>
    private static ReportSymbol? TryParseReportSymbol(JsonElement report)
    {
        if (!report.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var reportId) || reportId <= 0)
            return null;
        var name = report.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrEmpty(name)) return null;

        var props = SymbolProperties(report);
        props.TryGetValue("Caption", out var caption);
        props.TryGetValue("WordMergeDataItem", out var wordMergeDataItem);
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
            wordMergeDataItem, dataItems, referenceSourceFileName);
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

            into.Add(new ReportDataItemSymbol(dataItemId, name, relatedTable, indent, tableView, filterFields,
                ParseReportColumns(di),
                RecordPatches.TableViewText(dataItemLink), dataItemLinkReference,
                printOnlyIfDetail is "1" or "true" or "True"));
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
        return new QuerySymbol(queryId, name, queryType, caption, orderBy, top, dataItems);
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

        var columns = ParseQueryColumns(el, "Columns");
        var filters = ParseQueryColumns(el, "Filters");

        var nested = new List<QueryDataItemSymbol>();
        if (el.TryGetProperty("DataItems", out var di) && di.ValueKind == JsonValueKind.Array)
            foreach (var child in di.EnumerateArray())
            {
                var c = TryParseQueryDataItem(child);
                if (c != null) nested.Add(c);
            }
        return new QueryDataItemSymbol(id, name, relatedTable, sqlJoinType, dataItemLink, columns, filters, nested);
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
                fields.Add(new ParsedField(fieldId, fieldName, typeName, SymbolTypeLength(typeName), isFlowField, calcFormula,
                    optionMembers, initValue, isAutoIncrement, IsFlowFilter: isFlowFilter,
                    RelationArms: relationArms, RelationValidate: relationValidate,
                    MinValue: minValue, MaxValue: maxValue));
            }
        }

        var pkFieldIds = new List<int>();
        var secondaryKeys = new List<ParsedKey>();
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
                if (first)
                {
                    pkFieldIds.AddRange(ids);
                    first = false;
                }
                else if (ids.Count > 0)
                {
                    secondaryKeys.Add(new ParsedKey(keyName, ids));
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
        return new ParsedTable(tableId, tableName, fields, pkFieldIds, secondaryKeys, isTemporary,
            DataPerCompany: true,
            LookupPageName: string.IsNullOrWhiteSpace(lookupPageName) ? null : lookupPageName,
            DrillDownPageName: string.IsNullOrWhiteSpace(drillDownPageName) ? null : drillDownPageName,
            TableTypeName: string.IsNullOrWhiteSpace(tableType) ? null : tableType.Trim());
    }

    private static EnumSymbol? TryParseEnumSymbol(JsonElement enumType)
    {
        if (!enumType.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var id))
            return null;
        var name = enumType.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        if (!enumType.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        var options = new List<string>();
        var indexes = new List<int>();
        var implementations = new List<List<int>>();
        var captions = new List<string?>();
        var nextOrdinal = 0;
        foreach (var value in values.EnumerateArray())
        {
            var optionName = value.TryGetProperty("Name", out var optionNameProp)
                ? optionNameProp.GetString() ?? string.Empty
                : string.Empty;
            var ordinal = value.TryGetProperty("Ordinal", out var ordinalProp) && ordinalProp.TryGetInt32(out var explicitOrdinal)
                ? explicitOrdinal
                : nextOrdinal;
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
            nextOrdinal = ordinal + 1;
        }
        // Enum-level fallbacks. Both are written the same way a value's Implementation is —
        // a comma-separated list of codeunit ids, one per interface the enum implements.
        var enumProps = SymbolProperties(enumType);
        return new EnumSymbol(id, name, options, indexes, implementations, captions,
            SymbolCodeunitIdList(enumProps, "DefaultImplementation"),
            SymbolCodeunitIdList(enumProps, "UnknownValueImplementation"));
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
