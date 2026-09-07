// RecordPatches.AlPageParser — parses AL `page` / `pageextension` declarations
// into ParsedPage records keyed by page ID. Mirror of AlSourceParser for tables.
//
// We only need the (id, name, base-id-for-extensions) tuple — the cache slot
// just has to be non-null so NCLMetadata.GetMetaApplicationObjectInternal
// finds an entry. Field/action/group layout is irrelevant: every page-level
// property getter on NCLMetaForm reads `metadataAppGroupPageDefinition.Item`
// which is a default struct on a hand-built skeleton; those getters aren't
// reached by the metadata lookup path itself.
// Parsed from BC's own AL syntax tree (#1696). The old implementation guessed each object's
// extent with SliceObjectText, which scanned forward for the next `page|table|codeunit|…`
// keyword — a list that omitted `enum`, `interface`, `controladdin`, `permissionset` and
// friends, so any of those following a page put the NEXT object's body inside this page's
// slice, where SourceTable / InsertAllowed / field(...) could all match against it. Object
// extent is now structural.
//
// PageType / per-control Visible/Editable/Enabled/SourceExpression are ALSO captured now
// (issues #1769 / #1779) — they feed the "Page Metadata" (2000000138) and "Page Control
// Field" (2000000192) virtual tables. See RecordPatches.PageMetadataVirtualTable.cs and
// RecordPatches.PageControlFieldVirtualTable.cs.
using Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Register()-time sweep folded into RecordPatches.ParseAllRegisteredSourceFiles (#1903)
    // — that shared loop calls TryParsePageFile alongside the other seven extractors, one
    // file read per file, instead of this file doing its own separate directory walk.

    private static void TryParsePageFile(string text)
    {
        var objects = ParseAlObjects(text);

        // Pages and pageextensions go into SEPARATE dictionaries, mirroring
        // _parsedReports / _parsedReportExtensions. AL gives `page` and `pageextension`
        // separate id namespaces, so a page 50100 and a pageextension 50100 may both exist —
        // and while they shared one dictionary the extension (written second) won, bringing
        // SourceTableName = "" and an empty control map with it. The real page's source table
        // and every one of its control→field bindings vanished silently: GetSourceTableIdForPage
        // answered 0 and GetPageControlFieldMap answered empty (#1710). Write order therefore
        // no longer carries any meaning here.
        foreach (var obj in objects)
        {
            if (obj is not NavSyntax.PageSyntax p) continue;
            if (ObjectIdOf(p) is not int id) continue;
            var props = p.PropertyList;
            var (fieldMap, controls) = ParsePageControls(id, p.Layout);
            var pageTypeText = Unquote(PropValue(props, "PageType")?.ToString()?.Trim() ?? "");
            _parsedPages[id] = new ParsedPage(id, IdentText(p.Name), IsExtension: false,
                // Absent SourceTable is the empty string, not null — callers distinguish
                // "declares none" from "never parsed" via IsPageParsed.
                SourceTableName: Unquote(PropValue(props, "SourceTable")?.ToString()?.Trim() ?? ""),
                ControlIdToFieldName: fieldMap,
                // AL's default when the property is absent is TRUE, so only an explicit
                // `false` flips it. Drives ITestPage.Creatable via NavTestPageBase.New().
                InsertAllowed: !PropIs(props, "InsertAllowed", "false"),
                // AL's default is false — only an explicit `true` flips it. See issue #1719:
                // a page-variable's Rec must be built temporary when this is true, or its
                // own AL body's Rec.Copy(source, shareTable: true) refuses ("both records
                // must be temporary").
                SourceTableTemporary: PropIs(props, "SourceTableTemporary", "true"),
                // MS docs: PageType defaults to Card when the property is absent.
                PageType: pageTypeText.Length > 0 ? pageTypeText : "Card",
                Editable: !PropIs(props, "Editable", "false"),
                ModifyAllowed: !PropIs(props, "ModifyAllowed", "false"),
                DeleteAllowed: !PropIs(props, "DeleteAllowed", "false"),
                Controls: controls,
                // Page reference stated BY NAME, resolved against the run's own page
                // inventory at Page Metadata row-build time — same deferred-resolution rule
                // Table Metadata uses for LookupPageId/DrillDownPageId. Null means "declares
                // none", which Page Metadata reports as CardPageID = 0 (a real, meaningful
                // value: Base App "Page Management".GetDefaultCardPageID reads it to decide
                // whether a table has a card page at all).
                CardPageName: PageRefText(PropValue(props, "CardPageId")),
                MemberIdToName: ParseMemberNames(id, p),
                MemberIdToActionRefTarget: ParseActionRefTargets(id, p),
                DeclaredSystemActions: ParseDeclaredSystemActions(p));
        }

        foreach (var obj in objects)
        {
            if (obj is not NavSyntax.PageExtensionSyntax pe) continue;
            if (ObjectIdOf(pe) is not int id) continue;
            // An extension has no source table of its own — it inherits the base page's — but it
            // DOES declare field controls, via addfirst/addlast. Those are PageFieldSyntax nodes
            // exactly like a base page's, just hanging off PageExtensionLayoutSyntax, and they
            // used to be dropped on the floor (#1711): every pageextension stored an empty map,
            // so a TestPage driven through an extension-added control could not resolve it.
            // GetPageControlFieldMap merges them into the BASE page's map, which is where a
            // TestPage looks.
            var (extFieldMap, extControls) = ParsePageControls(id, pe.Layout);
            _parsedPageExtensions[id] = new ParsedPage(id, IdentText(pe.Name), IsExtension: true,
                SourceTableName: string.Empty,
                ControlIdToFieldName: extFieldMap,
                InsertAllowed: !PropIs(pe.PropertyList, "InsertAllowed", "false"),
                BaseName: Unquote(pe.BaseObject?.ToString()?.Trim() ?? ""),
                Controls: extControls,
                MemberIdToName: ParseMemberNames(id, pe),
                MemberIdToActionRefTarget: ParseActionRefTargets(id, pe));
        }
    }

    /// <summary>
    /// Member id → declared AL NAME for every named field control and action of one page or
    /// pageextension, in the DECLARING object's own id space. This is the reverse index
    /// trigger dispatch needs (issue #1968): the emitted C# trigger method carries the name
    /// only in MANGLED form (<c>"Spaced Stamp"</c> → <c>Spaced_Stamp_a45_OnAction</c>), and
    /// un-mangling is ambiguous — <c>Spaced_Stamp</c> reads back identically for the AL names
    /// <c>"Spaced Stamp"</c> and <c>Spaced_Stamp</c>, which hash to DIFFERENT member ids. The
    /// AL source is the one place the true name still exists, so the id is derived from it
    /// here, forward, the same way BC's own IdSpace does.
    /// <para>Unlike <see cref="ParsePageControls"/> this walk keeps every NAMED control —
    /// non-Rec-bound and compound-expression fields included — because a trigger can hang off
    /// any of them; the Rec.-bound scope limit over there is about field BINDING, not naming.
    /// </para>
    /// </summary>
    /// <summary>
    /// The NAMES this page declares inside <c>area(SystemActions)</c> — <c>systemaction(OK)</c>,
    /// <c>systemaction(Cancel)</c>, <c>systemaction(Generate)</c> and the rest.
    ///
    /// <para>Issue #3283. Declaring one is not additive for OK: measured on real BC
    /// 28.4.53241.0 (corpus codeunit 60338 "TBA Tests", arms c and d), a <c>PromptDialog</c>
    /// that declares <c>systemaction(OK)</c> has NO built-in <c>OK</c> for
    /// <c>TestPage.OK()</c> to resolve, while the same page without the declaration does. The
    /// platform's reason is visible in <c>PromptDialogBuilder.BuildPromptActions</c>: it adds
    /// an <c>ExitAction</c> for OK only on the else-branch, and a declared one is built by
    /// <c>ActionBuilder</c> as a <c>NoopAction</c>/<c>InvokePageTriggerAction</c>, which
    /// <c>TestPageProxy.GetBuiltInAction</c> does not accept. Cancel is unaffected because
    /// <c>BeginBuildActionBar</c> adds a form-level Cancel exit action unconditionally.</para>
    ///
    /// <para><c>PageSystemActionSyntax</c> is a SIBLING of <c>PageActionSyntax</c> (both derive
    /// from <c>PageActionWithTriggersBaseSyntax</c>), not a subclass, so
    /// <see cref="ParseMemberNames"/>'s <c>OfType&lt;PageActionSyntax&gt;()</c> never saw
    /// these.</para>
    /// </summary>
    private static HashSet<string> ParseDeclaredSystemActions(SyntaxNode obj)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var systemAction in obj.DescendantNodes().OfType<NavSyntax.PageSystemActionSyntax>())
        {
            var name = IdentText(systemAction.Name);
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Whether <paramref name="pageId"/>'s AL source declares <c>systemaction(<paramref
    /// name="systemActionName"/>)</c>.
    ///
    /// <para>FALSE for a page this run did not parse from AL — a precompiled page from a
    /// dependency .app, whose SymbolReference.json the runner does not read system actions out
    /// of. That keeps today's permissive answer for those rather than inventing a refusal from
    /// a lookup miss, the same choice <see cref="TryGetAnyPageType"/>'s null makes.</para>
    /// </summary>
    internal static bool PageDeclaresSystemAction(int pageId, string systemActionName)
        => _parsedPages.TryGetValue(pageId, out var page)
           && page.DeclaredSystemActions.Contains(systemActionName);

    private static Dictionary<int, string> ParseMemberNames(int declaringObjectId, SyntaxNode obj)
    {
        var map = new Dictionary<int, string>();
        void Add(string name)
        {
            if (name.Length == 0) return;
            // TryAdd, not indexer: a field and an action of the SAME name hash to the same
            // member id and carry the same name — first writer wins, the value is identical.
            map.TryAdd(IdSpace.GetMemberId(declaringObjectId, name), name);
        }

        foreach (var field in obj.DescendantNodes().OfType<NavSyntax.PageFieldSyntax>())
            Add(IdentText(field.Name));
        foreach (var action in obj.DescendantNodes().OfType<NavSyntax.PageActionSyntax>())
            Add(IdentText(action.Name));
        return map;
    }

    /// <summary>
    /// Member id → the NAME of the action an <c>actionref</c> points at, for every actionref
    /// this page or pageextension declares, in the DECLARING object's own id space.
    ///
    /// <para>Issue #2113. An <c>actionref(X_Promoted; X)</c> is a delegating REFERENCE: on real
    /// BC invoking it is the same command as invoking <c>X</c>. It carries no
    /// <c>trigger OnAction</c> of its own — the AL grammar gives it nowhere to put one
    /// (<c>PageActionRefSyntax</c> has <c>Name</c>, <c>Target</c> and a property list, and
    /// unlike <c>PageActionSyntax</c> it does NOT derive from
    /// <c>PageActionWithTriggersBaseSyntax</c>) — so the emitted <c>*_OnAction</c> method
    /// belongs to the TARGET action and hashes from the TARGET's name. Without this map a
    /// TestPage <c>Invoke()</c> on the actionref found no method whose member id matched the
    /// actionref's own id and reported the page as "declaring no OnAction trigger" for an
    /// action that plainly declares one.</para>
    ///
    /// <para>The target is stored by NAME rather than by id because the two may live in
    /// DIFFERENT id spaces: a pageextension's <c>addlast(Promoted)</c> actionref can point at
    /// an action declared on the BASE page, whose member id hashes from the base page's object
    /// id, not the extension's. Resolution therefore has to re-derive the id per candidate
    /// declaring object — see RunnerPageInstance.FindTriggerByName.</para>
    /// </summary>
    private static Dictionary<int, string> ParseActionRefTargets(int declaringObjectId, SyntaxNode obj)
    {
        var map = new Dictionary<int, string>();
        foreach (var actionRef in obj.DescendantNodes().OfType<NavSyntax.PageActionRefSyntax>())
        {
            var name = IdentText(actionRef.Name);
            var target = IdentText(actionRef.Target);
            if (name.Length == 0 || target.Length == 0) continue;
            // TryAdd, not the indexer: two actionrefs of the same name cannot legally coexist
            // on one object, so a duplicate here would be a parse artifact — keeping the first
            // is at worst what the pre-#2113 behaviour already was for both.
            map.TryAdd(IdSpace.GetMemberId(declaringObjectId, name), target);
        }
        return map;
    }

    /// <summary>
    /// The NAME of the action the <c>actionref</c> <paramref name="memberId"/> delegates to on
    /// the page or pageextension <paramref name="declaringObjectId"/>, or null when that member
    /// is not an actionref (or the object was never AL-source-parsed). See
    /// <see cref="ParseActionRefTargets"/>.
    /// </summary>
    internal static string? TryGetActionRefTarget(int declaringObjectId, int memberId, bool isExtension)
    {
        var dict = isExtension ? _parsedPageExtensions : _parsedPages;
        if (dict.TryGetValue(declaringObjectId, out var parsed))
            return parsed.MemberIdToActionRefTarget.TryGetValue(memberId, out var target) ? target : null;
        // Precompiled-dependency fallback — same rule and same reason as TryGetPageMemberName
        // below (#2723): a promoted actionref on a Base Application page points at a target
        // whose name only the dependency's SymbolReference.json still carries.
        var depTargets = isExtension
            ? TryGetDependencyPageExtensionSymbol(declaringObjectId)?.MemberIdToActionRefTarget
            : TryGetDependencyPageSymbol(declaringObjectId)?.MemberIdToActionRefTarget;
        return depTargets != null && depTargets.TryGetValue(memberId, out var depTarget) ? depTarget : null;
    }

    /// <summary>
    /// The declared AL name of member <paramref name="memberId"/> on the page or
    /// pageextension <paramref name="declaringObjectId"/>, or null when neither the runner's
    /// own AL source parse nor any loaded dependency .app's SymbolReference.json declares the
    /// member. <paramref name="isExtension"/> picks the id namespace — a page and a
    /// pageextension may share an object number (#1710), and the caller always knows which
    /// one it is dispatching against.
    /// <para><b>Precompiled-dependency fallback (issues #2723 / #2517):</b> this used to
    /// answer from <c>_parsedPages</c> / <c>_parsedPageExtensions</c> only, so for a page
    /// shipping precompiled in a dependency .app (every Base Application page) it answered
    /// null, and <c>RunnerPageInstance.FindTriggerOnTarget</c> fell to its BACKWARD scan —
    /// un-mangle the emitted method name and re-hash. That direction is lossy by
    /// construction (#1968): <c>Assign_Serial_Noa46_a45_OnAction</c> un-mangles to
    /// <c>Assign_Serial_Noa46</c>, which hashes to a different id than
    /// <c>"Assign Serial No."</c>, so every action or control whose AL name contains a space,
    /// <c>.</c>, <c>&amp;</c> (or is a C# keyword) was unreachable on every precompiled page:
    /// <c>OnAction</c> refused as "declares no OnAction trigger" (955 failures in Microsoft's
    /// BaseApp surface), <c>OnValidate</c> silently skipped. The dependency's own
    /// SymbolReference.json states every member's declared name keyed by BC's own member id
    /// — the same file <see cref="GetPageControlFieldMap"/> (#2088) and
    /// <see cref="GetInsertAllowedForPage"/> already fall back to — so the forward
    /// mangle-and-compare arm now applies to precompiled pages too. Source-parsed wins for an
    /// object the parser saw, matching every other reader in this file.</para>
    /// </summary>
    internal static string? TryGetPageMemberName(int declaringObjectId, int memberId, bool isExtension)
    {
        var dict = isExtension ? _parsedPageExtensions : _parsedPages;
        if (dict.TryGetValue(declaringObjectId, out var parsed))
            return parsed.MemberIdToName.TryGetValue(memberId, out var name) ? name : null;
        var depNames = isExtension
            ? TryGetDependencyPageExtensionSymbol(declaringObjectId)?.MemberIdToName
            : TryGetDependencyPageSymbol(declaringObjectId)?.MemberIdToName;
        return depNames != null && depNames.TryGetValue(memberId, out var depName) ? depName : null;
    }

    /// <summary>
    /// Whether the page permits inserts (AL's <c>InsertAllowed</c>, default TRUE when the
    /// property is absent). Drives ITestPage.Creatable, which BC's NavTestPageBase.New()
    /// checks before inserting.
    /// <para>Checks the runner's own AL-source-parsed pages first, then (issue #2088's sibling
    /// defect — this method had the SAME "_parsedPages only" gap as
    /// <see cref="GetPageControlFieldMap"/>, called right alongside it at every TestPage/part
    /// construction site) a loaded dependency .app's SymbolReference.json, which already
    /// carries InsertAllowed for the "Page Metadata" virtual table (#1769). A page unknown to
    /// either source defaults to true — AL's own default.</para>
    /// </summary>
    internal static bool GetInsertAllowedForPage(int pageId)
    {
        if (_parsedPages.TryGetValue(pageId, out var page)) return page.InsertAllowed;
        return TryGetDependencyPageSymbol(pageId)?.InsertAllowed ?? true;
    }

    /// <summary>
    /// <paramref name="pageId"/>'s declared <c>PageType</c> — the runner's own AL-source-parsed
    /// pages first, then a loaded dependency .app's SymbolReference.json. Null when neither
    /// knows the page, which callers must NOT read as "Card": AL's absent-property default is
    /// applied where the page IS known (both sources already do it), so null here means the
    /// page is unknown and a caller that has to branch on the type should say so rather than
    /// pick one.
    /// <para>Issue #2931's consumer is RunnerPageInstance.TargetPageOpensModally: whether an
    /// action's RunObject target opens as a dialog is decided by the TARGET's PageType, so this
    /// is asked about a page other than the one being driven.</para>
    /// </summary>
    internal static string? TryGetAnyPageType(int pageId)
    {
        if (_parsedPages.TryGetValue(pageId, out var page)) return page.PageType;
        return TryGetDependencyPageSymbol(pageId)?.PageType;
    }

    /// <summary>
    /// Resolve an object NAME that is known to be a PAGE only if nothing else of that name
    /// exists — for a <c>RunObject</c> a precompiled .app states as a bare name with no object
    /// type beside it (#2931).
    ///
    /// <para>Returns the page id, and the OTHER object kinds the same name resolves to. The
    /// second half is not defensive: measured on Base Application 28.1, <b>73 names are shared
    /// between a page and a report / codeunit / xmlport / query</b> — "Chart of Accounts",
    /// "Blanket Sales Order", "Account Schedule" and 70 more each name both a page and a
    /// report. Treating "resolves to a page" as "is a page" would open a page for an action
    /// whose AL says <c>RunObject = Report "Chart of Accounts"</c>: a silent wrong answer, and
    /// exactly what <c>loud-failures.md</c> exists to prevent. The caller refuses an ambiguous
    /// name by name instead.</para>
    ///
    /// <para>The page half uses the same index as Table Metadata's LookupPageId /
    /// DrillDownPageId and Page Metadata's CardPageId, so a name resolvable there resolves the
    /// same way here.</para>
    /// </summary>
    internal static (int PageId, IReadOnlyList<string> OtherKinds) ResolveObjectNameAsPage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (0, Array.Empty<string>());

        var pageId = 0;
        var otherKinds = new List<string>();
        foreach (var (kind, id, objectName, _) in EnumerateKnownAlObjects())
        {
            // Exact (case-insensitive) match, NOT the space-stripping NamesEqual used for
            // SourceTable/BaseName elsewhere in this file: that one exists because AL writes
            // a table reference with and without quotes, and stripping spaces there is safe.
            // Here it would manufacture ambiguity — "Purchase Statistics" and a hypothetical
            // "PurchaseStatistics" are different objects — and this method's whole job is to
            // decide whether a name is ambiguous. Same comparison BuildObjectIndexes uses.
            if (id <= 0 || !string.Equals(objectName, name, StringComparison.OrdinalIgnoreCase)) continue;
            var normalised = NormalizeObjectTypeName(kind);
            switch (normalised)
            {
                case "page":
                    if (pageId == 0) pageId = id;
                    break;
                // An EXTENSION is not a RunObject target — `RunObject = Page X` never names a
                // pageextension — so those must not count as an ambiguity that blocks a page
                // that resolves cleanly. Same for a table or an enum, which the AL grammar does
                // not allow after RunObject at all.
                case "pageextension":
                case "reportextension":
                case "queryextension":
                case "tableextension":
                case "enumextension":
                case "table":
                case "enum":
                    break;
                default:
                    if (!otherKinds.Contains(normalised)) otherKinds.Add(normalised);
                    break;
            }
        }
        return (pageId, otherKinds);
    }

    /// <summary>
    /// The <c>RunObject</c> that member <paramref name="memberId"/> of the page or pageextension
    /// <paramref name="declaringObjectId"/> declares, as a loaded dependency .app's
    /// SymbolReference.json states it — or null when it declares none (or the object ships no
    /// symbol file here).
    ///
    /// <para>Deliberately symbol-file only. A page the runner COMPILED itself has BC's own
    /// compiled <c>ActionDefinition</c>, which carries the target already resolved to an object
    /// KIND and a numeric id; re-deriving that from AL text would be a second, weaker answer to
    /// a question the compiler has already answered exactly. This exists for the other case —
    /// a page shipped precompiled in a dependency .app, for which the runner has no compiled
    /// action metadata at all (#2460).</para>
    /// </summary>
    internal static BcAppSymbolCache.ActionRunObjectSymbol? TryGetActionRunObject(
        int declaringObjectId, int memberId, bool isExtension)
    {
        var map = isExtension
            ? TryGetDependencyPageExtensionSymbol(declaringObjectId)?.MemberIdToRunObject
            : TryGetDependencyPageSymbol(declaringObjectId)?.MemberIdToRunObject;
        return map != null && map.TryGetValue(memberId, out var spec) ? spec : null;
    }

    /// <summary>
    /// Whether the AL source parser has seen this PAGE at all. Lets callers tell
    /// "the page genuinely declares no SourceTable" (BC's SourceTable==0 case, a legal
    /// AL page) apart from "we never parsed this page", which is a runner gap and must
    /// be reported loudly rather than answered with a default.
    /// <para>A pageextension of the same number is deliberately NOT an answer here: it is a
    /// different object in a different id namespace, and letting one stand in for a page is
    /// what #1710 was.</para>
    /// </summary>
    internal static bool IsPageParsed(int pageId) => _parsedPages.ContainsKey(pageId);

    /// <summary>
    /// Whether a parsed page declares a SourceTable in AL. False for a parsed page with
    /// no SourceTable property (BC returns a null NCLMetaTable for those).
    /// </summary>
    internal static bool PageDeclaresSourceTable(int pageId)
        => _parsedPages.TryGetValue(pageId, out var page)
           && !string.IsNullOrWhiteSpace(page.SourceTableName);

    internal static int GetSourceTableIdForPage(int pageId)
    {
        if (!_parsedPages.TryGetValue(pageId, out var page) || string.IsNullOrWhiteSpace(page.SourceTableName))
            return 0;

        foreach (var table in _parsedTables.Values)
            if (NamesEqual(table.TableName, page.SourceTableName))
                return table.TableId;

        // (#2452) The loop above only sees tables THIS bundle AL-source-parsed. A
        // bundle-declared page's SourceTable may instead name a table that ships
        // PRECOMPILED in a loaded dependency .app (e.g. Base Application "Resource")
        // — that table is never in _parsedTables until something asks for it by name.
        // TryPopulateParsedTableByName is the SAME by-name dependency lookup
        // BuildMetaCalcFormula/BcAppFallback already use for FlowField CalcFormula
        // source-table resolution; this is a second caller; not new lookup machinery.
        var byName = TryPopulateParsedTableByName(page.SourceTableName);
        if (byName != null)
            return byName.TableId;

        return 0;
    }

    /// <summary>
    /// Every AL-source-parsed <c>pageextension</c> that extends <paramref name="pageId"/>, by
    /// its OWN object id — the id space a control/action it declares is hashed in (see the
    /// remarks on <see cref="GetPageControlFieldMap"/>). <paramref name="pageId"/> is resolved
    /// to a NAME via the runner's own AL-source-parsed pages first, then (issue #1923) a loaded
    /// dependency .app's SymbolReference.json (<see cref="RecordPatches.TryGetAnyPageName"/>) —
    /// a pageextension over a page that ships PRECOMPILED (e.g. Base Application "Item
    /// Attributes") is exactly as much a real extension as one over a page compiled in this
    /// bundle, and the trigger-dispatch gap that motivated this method (#1923) hit that arm
    /// hardest: nothing threw at all, so a test only caught the miss on the effect the missing
    /// action was supposed to have.
    /// </summary>
    internal static List<int> GetPageExtensionIdsForPage(int pageId)
    {
        var baseName = TryGetAnyPageName(pageId);
        if (string.IsNullOrEmpty(baseName)) return new List<int>();

        var result = new List<int>();
        foreach (var ext in _parsedPageExtensions.Values)
            if (NamesEqual(ext.BaseName, baseName))
                result.Add(ext.Id);
        // Issue #2723's pageextension arm: a pageextension that itself ships PRECOMPILED in a
        // dependency .app (Base Application's "Activity Log Extension" over "Activity Log",
        // its approval extensions over the Job Queue pages, …) declares actions with
        // triggers exactly like a source-parsed one, compiled onto its own
        // PageExtension{id} type, which is already loaded and which
        // RunnerPageInstance.GetOrCreateExtensionInstance already knows how to construct —
        // but this method never listed it, so FindTrigger never searched it and every such
        // action was refused as "the page declares no OnAction trigger". A source-parsed
        // extension of the same number wins (the runner's own parse outranks a dependency
        // symbol everywhere else in this file); otherwise the dependency's declared
        // TargetObject name is matched with the same NamesEqual rule as BaseName above.
        foreach (var depExtId in DependencyPageExtensionIdsForPage(baseName))
            if (!_parsedPageExtensions.ContainsKey(depExtId) && !result.Contains(depExtId))
                result.Add(depExtId);
        result.Sort();
        return result;
    }

    /// <summary>
    /// <paramref name="pageId"/>'s declared NAME — the runner's own AL-source-parsed pages
    /// first, then (for a page that ships precompiled in a dependency .app, never AL-source-
    /// parsed here) the dependency's SymbolReference.json. Null when neither knows the page.
    /// </summary>
    internal static string? TryGetAnyPageName(int pageId)
    {
        if (_parsedPages.TryGetValue(pageId, out var page)) return page.Name;
        return TryGetDependencyPageSymbol(pageId)?.Name;
    }

    /// <summary>
    /// Control id → source-table field number for every field control on the page, INCLUDING
    /// the ones contributed by pageextensions that extend it.
    /// <para>An extension's controls are keyed in the EXTENSION's own id space, because BC's
    /// IdSpace.GetMemberId hashes the id of the object the member is DECLARED in. Verified,
    /// not assumed: a bundle with `page 64300 "PXP Card"` and `pageextension 64301` adding
    /// `field(NoteField; Rec."Note")` made BC ask LiveNavTestPage.GetField for control
    /// 788108655 == GetMemberId(64301, "NoteField"); GetMemberId(64300, "NoteField") is
    /// 321499490 and never appears.</para>
    /// <para><b>Precompiled-dependency fallback (issue #2088):</b> a page that ships
    /// precompiled in a dependency .app (Base Application, System Application, an ISV
    /// extension) is never AL-source-parsed here, so it is never in <c>_parsedPages</c> —
    /// that used to mean this method answered an empty map for it regardless of what its
    /// controls are actually bound to, and every field control read on such a page refused
    /// with <c>testpage-control-binding</c>, even ones the dependency's own
    /// SymbolReference.json states are plain <c>Rec.Field</c> bindings. That file is the
    /// SAME source the "Page Control Field" virtual table (#1779) already reads for exactly
    /// this data, so a page miss here now falls back to it via the shared
    /// <see cref="ResolveDependencyControlField"/> resolver — one control resolution rule for
    /// both consumers, not a second hand-rolled one. Pageextensions are not folded into this
    /// fallback: a pageextension that extends a dependency-only base page is itself
    /// AL-source-parsed (or it too ships precompiled and gets its own dependency-symbol
    /// entry), and <see cref="GetPageExtensionIdsForPage"/> already resolves the base page's
    /// name through the same dependency fallback for that separate, existing path.</para>
    /// </summary>
    internal static IReadOnlyDictionary<int, int> GetPageControlFieldMap(int pageId)
    {
        if (_parsedPages.TryGetValue(pageId, out var page))
        {
            if (string.IsNullOrWhiteSpace(page.SourceTableName))
                return new Dictionary<int, int>();

            var table = _parsedTables.Values.FirstOrDefault(t => NamesEqual(t.TableName, page.SourceTableName));
            if (table == null) return new Dictionary<int, int>();

            var result = new Dictionary<int, int>();
            BindControls(page.ControlIdToFieldName, table, result);
            // Only extensions of THIS page. Binding every extension's controls onto every page
            // would fabricate bindings that the AL never declared.
            foreach (var ext in _parsedPageExtensions.Values)
                if (NamesEqual(ext.BaseName, page.Name))
                    BindControls(ext.ControlIdToFieldName, table, result);
            return result;
        }

        var symbol = TryGetDependencyPageSymbol(pageId);
        if (symbol == null || symbol.SourceTableId == 0 || symbol.Controls == null || symbol.Controls.Count == 0)
            return new Dictionary<int, int>();

        if (!_parsedTables.TryGetValue(symbol.SourceTableId, out var depTable))
        {
            TryPopulateParsedTableFromBcApps(symbol.SourceTableId);
            _parsedTables.TryGetValue(symbol.SourceTableId, out depTable);
        }
        if (depTable == null) return new Dictionary<int, int>();

        var depResult = new Dictionary<int, int>();
        foreach (var control in symbol.Controls)
        {
            var (_, fieldNo) = ResolveDependencyControlField(control.SourceExpression, symbol.SourceTableId, depTable);
            if (fieldNo != 0) depResult[control.Id] = fieldNo;
        }
        return depResult;

        static void BindControls(IReadOnlyDictionary<int, string> controls, ParsedTable table, Dictionary<int, int> result)
        {
            // GetAllFieldsIncludingExtensions, not table.Fields alone: a control bound to a
            // field a tableextension added (source-parsed here, in a sibling app, or
            // precompiled in a dependency .app) must resolve exactly like one bound to the
            // table's own field — see #2490.
            var allFields = RecordPatches.GetAllFieldsIncludingExtensions(table);
            foreach (var kvp in controls)
            {
                var field = allFields.FirstOrDefault(f => NamesEqual(f.FieldName, kvp.Value));
                if (field != null) result[kvp.Key] = field.FieldId;
            }
        }
    }

    /// <summary>
    /// The OTHER controls of a PRECOMPILED dependency page that declare the same
    /// <c>SourceExpression</c> TEXT as <paramref name="controlId"/> — the siblings the AL
    /// compiler collapsed onto one registered <c>&lt;Expression&gt;</c> (issue #3211).
    ///
    /// <para>A page that binds two controls to one page global gets ONE expression object,
    /// named after whichever control the compiler reached first, and every other control
    /// points at it through its own <c>DataColumnName</c>. On a page the runner compiled
    /// itself that attribute is readable off the merged metadata
    /// (<c>RunnerPageInstance.TryGetSourceExpression</c> uses it directly); on a page that
    /// ships precompiled in a dependency .app it is not, because
    /// <see cref="TryGetDependencyPageMetadataXml"/> reconstructs no control tree. What the
    /// dependency DOES state, per control, is the AL binding text itself — the same field
    /// <see cref="GetPageControlFieldMap"/> and the "Page Control Field" virtual table
    /// already read — and the compiler's dedup key IS that text, so the siblings can be
    /// named exactly rather than guessed.</para>
    ///
    /// <para>Naming a sibling asserts nothing on its own: the caller still only accepts a
    /// sibling whose <c>Control{id}</c> key the page ACTUALLY registered, so a control whose
    /// binding was never registered at all stays unresolved and still refuses loudly.
    /// Comparison is case-insensitive because AL identifiers are, so two spellings of one
    /// variable name are one binding.</para>
    /// </summary>
    internal static IReadOnlyList<int> DependencyControlsSharingSourceExpression(int pageId, int controlId)
    {
        var symbol = TryGetDependencyPageSymbol(pageId);
        if (symbol?.Controls == null || symbol.Controls.Count == 0) return Array.Empty<int>();

        string? text = null;
        foreach (var control in symbol.Controls)
            if (control.Id == controlId) { text = control.SourceExpression; break; }
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();

        var siblings = new List<int>();
        foreach (var control in symbol.Controls)
            if (control.Id != controlId
                && string.Equals(control.SourceExpression, text, StringComparison.OrdinalIgnoreCase))
                siblings.Add(control.Id);
        return siblings;
    }

    /// <summary>
    /// Every field control of a SOURCE-PARSED page, base plus matching pageextensions,
    /// for the "Page Control Field" (2000000192) virtual table. Same base+extension merge
    /// rule as <see cref="GetPageControlFieldMap"/> (only extensions of THIS page), same
    /// Rec.-bound-only scope as <see cref="ParsePageControls"/> — see that method's remarks.
    /// <para>Sequence is assigned here, at merge time, 1-based in enumeration order (base
    /// page controls first, then each matching extension's, in registration order) — never
    /// trusted from the per-object parse pass, since a base page and an extension each start
    /// their own local layout walk at 1 and merging them naively would produce duplicate
    /// Sequence values.</para>
    /// </summary>
    internal static List<PageControlRow> GetSourceParsedPageControlRows(int pageId)
    {
        var result = new List<PageControlRow>();
        if (!_parsedPages.TryGetValue(pageId, out var page)) return result;

        int seq = 0;
        void AddAll(IReadOnlyList<PageControlRow> controls)
        {
            foreach (var c in controls)
                result.Add(c with { Sequence = ++seq });
        }

        AddAll(page.Controls);
        foreach (var ext in _parsedPageExtensions.Values)
            if (NamesEqual(ext.BaseName, page.Name))
                AddAll(ext.Controls);
        return result;
    }

    internal static int[] GetPrimaryKeyFieldIdsForTable(int tableId)
        => _parsedTables.TryGetValue(tableId, out var table)
            ? table.PkFieldIds.ToArray()
            : Array.Empty<int>();

    /// <summary>
    /// Every field control of one page layout (a base page's <c>layout</c> or a
    /// pageextension's <c>layout</c>/<c>addfirst</c>/<c>addlast</c> block), plus the
    /// Rec.-bound subset of them as a control-id → field-name map (for
    /// <see cref="GetPageControlFieldMap"/>, unchanged from before this method existed).
    /// <para>Field controls are collected from the whole layout subtree at once, which covers
    /// arbitrary <c>area</c> / <c>group</c> / <c>cuegroup</c> / <c>repeater</c> nesting. Scoping
    /// to <c>Layout</c> also means the <c>actions</c> section cannot contribute (an action is a
    /// structurally different node), and a <c>part(...)</c> is a leaf here — the page it
    /// references is a separate object with its own tree, so its fields can never leak in.</para>
    /// <para><b>Scope limitation, deliberate:</b> a control only becomes a
    /// <see cref="PageControlRow"/> when its source expression is exactly <c>Rec.Something</c>.
    /// A field control bound to anything else (a compound expression, a local/global variable)
    /// is omitted entirely rather than guessed at — same "omit, never fabricate" rule the
    /// Table/Report Metadata providers use for a page/table/report they cannot resolve.
    /// <c>modify(...)</c> property overrides on an inherited control (pageextension) are NOT
    /// applied here either: the row reflects the control's own declaring object, not any
    /// extension that later modifies its Visible/Editable. Real BC would show the overridden
    /// value; this is a known, narrower gap than what existed before (no rows at all).</para>
    /// <para><paramref name="declaringObjectId"/> is the object the controls are DECLARED in —
    /// the page for a base layout, the PAGEEXTENSION for controls it adds. That is what BC's
    /// IdSpace.GetMemberId hashes; see GetPageControlFieldMap for the live evidence.</para>
    /// </summary>
    private static (Dictionary<int, string> FieldMap, List<PageControlRow> Controls) ParsePageControls(
        int declaringObjectId, SyntaxNode? layout)
    {
        var fieldMap = new Dictionary<int, string>();
        var controls = new List<PageControlRow>();
        if (layout == null) return (fieldMap, controls);

        int seq = 0;
        foreach (var field in layout.DescendantNodes().OfType<NavSyntax.PageFieldSyntax>())
        {
            var controlName = IdentText(field.Name);
            if (controlName.Length == 0) continue;

            string fieldName = string.Empty;
            if (field.Expression is NavSyntax.MemberAccessExpressionSyntax access
                && access.Expression is NavSyntax.IdentifierNameSyntax receiver
                && string.Equals(Unquote(receiver.Identifier.ValueText ?? ""), "Rec",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Only a source expression that is exactly Rec.Something counts. The old regex
                // looked for the text "Rec." anywhere after the semicolon, so
                // `field(Total; Rec.Amount + 1)` bound the control to Amount — a control that
                // is not bound to that field at all. A compound expression yields no binding.
                fieldName = IdentText(access.Name as NavSyntax.IdentifierNameSyntax);
            }
            if (fieldName.Length == 0) continue;   // scope limitation — see remarks above

            var controlId = IdSpace.GetMemberId(declaringObjectId, controlName);
            fieldMap[controlId] = fieldName;

            seq++;
            controls.Add(new PageControlRow(
                controlId, controlName, fieldName,
                SourceExpressionText: field.Expression?.ToString()?.Trim() ?? string.Empty,
                VisibleExpr: PropValue(field.PropertyList, "Visible")?.ToString()?.Trim(),
                EditableExpr: PropValue(field.PropertyList, "Editable")?.ToString()?.Trim(),
                EnabledExpr: PropValue(field.PropertyList, "Enabled")?.ToString()?.Trim(),
                Sequence: seq));
        }

        return (fieldMap, controls);
    }

    private static bool NamesEqual(string left, string right)
        => string.Equals(left.Replace(" ", ""), right.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One field control resolved from a page's (or pageextension's) layout — the Rec.-bound
/// subset only, see <see cref="RecordPatches"/>.ParsePageControls remarks. Feeds the
/// "Page Control Field" (2000000192) virtual table.
/// </summary>
internal sealed record PageControlRow(
    int ControlId,
    string ControlName,
    string FieldName,
    string SourceExpressionText,
    string? VisibleExpr,
    string? EditableExpr,
    string? EnabledExpr,
    int Sequence);

internal record ParsedPage(
    int Id,
    string Name,
    bool IsExtension,
    string SourceTableName,
    IReadOnlyDictionary<int, string> ControlIdToFieldName,
    bool InsertAllowed = true,
    /// <summary>The object a pageextension extends; empty for a plain page.</summary>
    string BaseName = "",
    /// <summary>AL's <c>SourceTableTemporary</c> property; see issue #1719.</summary>
    bool SourceTableTemporary = false,
    /// <summary>AL's <c>PageType</c> property; MS docs default is "Card". Feeds the
    /// "Page Metadata" (2000000138) virtual table (#1769).</summary>
    string PageType = "Card",
    bool Editable = true,
    bool ModifyAllowed = true,
    bool DeleteAllowed = true,
    /// <summary>Rec.-bound field controls of this page's OWN layout (excludes extensions);
    /// see <see cref="RecordPatches"/>.GetSourceParsedPageControlRows for the merged view.</summary>
    IReadOnlyList<PageControlRow>? Controls = null,
    /// <summary>AL's <c>CardPageId</c> property, as the last name segment of the page
    /// reference (unresolved — see <see cref="RecordPatches"/>.PageMetadataVirtualTable.cs).
    /// Null when the page declares none.</summary>
    string? CardPageName = null,
    /// <summary>Member id → declared AL name for every named field control and action of this
    /// object, in its own id space — see <see cref="RecordPatches"/>.ParseMemberNames (#1968).</summary>
    IReadOnlyDictionary<int, string>? MemberIdToName = null,
    /// <summary>Member id of every <c>actionref</c> this object declares → the NAME of the
    /// action it points at — see <see cref="RecordPatches"/>.ParseActionRefTargets (#2113).</summary>
    IReadOnlyDictionary<int, string>? MemberIdToActionRefTarget = null,
    /// <summary>The names declared inside <c>area(SystemActions)</c> — see
    /// <see cref="RecordPatches"/>.ParseDeclaredSystemActions (#3283).</summary>
    IReadOnlySet<string>? DeclaredSystemActions = null)
{
    // Positional records can't give a collection parameter a literal default that isn't a
    // constant, so a null Controls (constructed via the shorter historical call sites/tests,
    // if any ever appear) is normalized to empty rather than NRE-ing every consumer.
    public IReadOnlyList<PageControlRow> Controls { get; init; } = Controls ?? Array.Empty<PageControlRow>();
    public IReadOnlyDictionary<int, string> MemberIdToName { get; init; }
        = MemberIdToName ?? new Dictionary<int, string>();
    public IReadOnlyDictionary<int, string> MemberIdToActionRefTarget { get; init; }
        = MemberIdToActionRefTarget ?? new Dictionary<int, string>();
    // OrdinalIgnoreCase: AL identifiers are case-insensitive, and this set is looked up with
    // the literal "OK"/"Cancel" spellings while the source may say `systemaction(ok)`.
    public IReadOnlySet<string> DeclaredSystemActions { get; init; }
        = DeclaredSystemActions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
