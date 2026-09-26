// RecordPatches.DependencyPageMetadataXml — runtime PageDefinition XML for pages that live
// in a PRECOMPILED dependency .app, which the runner never source-compiles.
//
// THE GAP (issue #1939)
//   NavForm.GetMasterPage() -> NavGlobal.MetadataProvider.GetMasterPage(...) ->
//   GetMergedMasterPage() -> GetPageDefinition(id) is BC's only route to a page's real
//   PageProperties (PageType, SourceObject, ...). NavTestExecution.FindPageType reads
//   exactly one of those — form.MasterPage.PageProperties.PageType — to decide whether a
//   modal page's [ModalPageHandler] is a FilterPage/RequestPage/ModalPage handler, BEFORE
//   the handler ever runs. RunnerFormInit.ShouldResolveMasterPage only let that real lookup
//   run for a page the runner captured emit-time metadata XML for (AlPageMetadataRegistry —
//   populated only by BcCompiler.Emit, which never runs for a page shipped compiled inside a
//   dependency .app). Every other page got GetMasterPage() short-circuited to null, and
//   FindPageType NRE'd on the null MasterPage before the handler dispatch it exists to gate.
//
//   Same root cause, same fix shape, as DependencyReportMetadata.cs one file up: an R2R
//   .app ships no compiled metadata form of its objects (that only exists at real-BC
//   PUBLISH time, which the runner never performs), so the runner reconstructs a runtime
//   metadata document from what the .app DOES ship.
//
// WHAT IS RECONSTRUCTED, AND FROM WHAT
//   SymbolReference.json alone (via BcAppSymbolCache.PageSymbol) — the same typed slice
//   #1769/#1779 already parse for the Page Metadata virtual table. Nothing here is inferred
//   from behaviour or defaulted to something convenient: Id / Name / PageType / Caption /
//   Editable / SourceObject (SourceTable + SourceTableTemporary) come straight off the
//   symbol file's own Properties array, as do the SourceObject flags added since —
//   Insert/Modify/DeleteAllowed, AutoSplitKey, MultipleNewLines, DelayedInsert,
//   SourceTableView (#2820), and LinksAllowed / ShowFilter / SaveValues /
//   PopulateAllFields / DataCaptionFields (#2860, see EmitSourceObjectPropertiesXml).
//   #3784 added the <Properties> scalars on the same rule — Extensible (which this used to
//   HARDCODE to "1", a wrong answer on 105 of 236 measured pages), RefreshOnActivate,
//   UsageCategory, HelpLink, IsPreview, the four ML strings and the two inherent permission
//   masks; see EmitPagePropertiesXml, which also records which nearby members are
//   DERIVATIONS rather than reads and therefore deliberately still absent.
//
// WHAT IS DELIBERATELY OMITTED, AND WHY THAT IS SAFE HERE
//   Ordinary field Content/Controls, ActionContainers, ViewContainers,
//   AnalysisViewContainers — the page's full control tree and action ribbon (the action
//   half is #2460, a separate fix). Two independent reasons neither is needed for the gap
//   this file closes:
//     1. NavTestExecution.FindPageType — the NRE site — reads exactly one property,
//        form.MasterPage.PageProperties.PageType, which Properties above already states.
//     2. A precompiled page's control -> value BINDINGS are not read from this XML at all.
//        They come from the page's OWN CallInitializeComponentExtensionMethod /
//        RegisterSourceExpression IL inside the .app's DLL, gated by
//        RunnerFormInit.ShouldRunRealFormInit — a NARROWER, per-instance opt-in that only
//        the runner's own TestPage construction path sets (RunnerPageInstance.MarkRealInit).
//        A page AL opens itself via `SomePage.RunModal()` — this issue's shape — is never
//        marked, so that IL stays no-op'd exactly as it already was; this file's XML cannot
//        change that. Reconstructing a control tree from SymbolReference.json (whose
//        SourceExpression is AL text, e.g. `Rec."No."`, not the compiled field-number
//        DataColumnName the real XML carries) without a way to exercise it would only add
//        guessed data with no path to prove it faithful — the loud-failures rule's
//        anti-pattern. Field-level TestPage control resolution for a page the runner did not
//        compile itself stays out of scope (RunnerPageInstance.cs already documents this),
//        unchanged by this fix — a modal page whose handler drives a field it does not
//        recognise still refuses loudly, exactly as it did before.
//
//   Subpage PARTS (issue #2467) are the one exception, and reason 2 above is exactly why:
//   unlike a field control, a part's binding is NOT read from IL at all.
//   RunnerPageInstance.TryGetPartDefinition resolves a part entirely from
//   form.MetadataHelper.InfoPartDefinitions, itself built by BC's own
//   NCLMetaForm.LoadPageMetadata walking THIS file's Content — so a part genuinely is
//   reconstructable data, not guessed data, and EmitPartControlXml below adds it. The
//   SubFormLink field names it carries ARE resolved (to numeric ids, off the part's own and
//   the host's SourceTable — see EmitSubFormLinkXml), and its const(...)/filter(...) values
//   normalised to the compiler's own representation, because MockTestPage.SubPageLinks
//   consumes the compiled FieldID/FilterType/FilterValue shape, never AL text.
using System.Text;
using System.Xml;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string?> _depPageMetadataXml = new();

    /// <summary>
    /// Whether some loaded dependency .app's SymbolReference.json describes
    /// <paramref name="pageId"/> — the opt-in condition <see cref="Patches.RunnerFormInit"/>
    /// and <see cref="EnsureRealPageMetadata"/> widen for, alongside the runner's own
    /// AlPageMetadataRegistry entries.
    /// </summary>
    internal static bool HasDependencyPageMetadata(int pageId) => TryGetDependencyPageSymbol(pageId) != null;

    /// <summary>
    /// Runtime PageDefinition metadata XML for a page declared by a precompiled dependency,
    /// or null when no loaded dependency .app describes that page.
    ///
    /// <para>Result cached per id, INCLUDING the null, because the answer is a property of
    /// the loaded dependency set — and therefore only for as long as that set holds still.
    /// <see cref="InvalidateBcAppIndexes"/> drops this memo alongside every other index
    /// derived from <c>_bcAppPaths</c>, so both directions of a set change are covered:
    /// a registration that ADDS the .app declaring this page (<see cref="AddBcAppPath"/>),
    /// and a bundle roll that drops the previous bundle's registrations
    /// (<c>ResetForReload</c>). Issue #2889: without that, an id asked about before its
    /// declaring .app registered kept the memoized null for the life of the process, and
    /// <see cref="RunnerXmlMetadataLoader"/> answered "no metadata XML for this object" for
    /// a page whose metadata was readable in a registered symbol file.</para>
    /// </summary>
    internal static string? TryBuildDependencyPageMetadata(int pageId)
        => _depPageMetadataXml.GetOrAdd(pageId, BuildDependencyPageMetadata);

    private static string? BuildDependencyPageMetadata(int pageId)
    {
        var page = TryGetDependencyPageSymbol(pageId);
        if (page == null) return null;

        // The witness is asked HERE, where the .app that declared this page is still known, and
        // the verdict travels rather than the path — so EmitPageXml cannot ask the question
        // against the wrong app. Same shape as the codeunit row in
        // RecordPatches.CodeunitMetadataVirtualTable.cs.
        var appPath = TryGetDependencyPageAppPath(pageId);
        var methodsProvenComplete =
            appPath is not null && PageAssemblyProvesNoSubscriber(appPath, pageId);

        var xml = EmitPageXml(page, methodsProvenComplete,
            appPath is null ? null : DependencyAppContextSensitiveHelpUrl(appPath));
        Console.Error.WriteLine(
            $"[RecordPatches] dependency page metadata: synthesized Page {pageId} \"{page.Name}\" "
            + $"(PageType={page.PageType}, SourceTable={page.SourceTableId})");
        return xml;
    }

    private static string EmitPageXml(
        BcAppSymbolCache.PageSymbol page, bool methodsProvenComplete = false,
        string? manifestHelpUrl = null)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteStartElement("PageDefinition", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("MetadataVersion", "130000");
            w.WriteAttributeString("ID", page.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteAttributeString("Name", page.Name);
            w.WriteAttributeString("ALNamespace", string.Empty);
            // #4282. BC's emitter carries a page's caption as a ROOT attribute in its
            // MultiLanguage "ENU=<text>" form, and PageDefinition(XmlNode) reads it into
            // CaptionMLString from there; the <CaptionML> child element written below, inside
            // <Properties>, is NOT that member and leaves it empty. Measured over the 235
            // PageDefinition documents of Business Foundation + System Application at BC
            // 28.1.49838.53910: the symbol file states Caption on 196 pages, BC writes the
            // attribute on exactly those 196, and "ENU=" + the stated text equals BC's value on
            // all 196 with zero disagreements. Write-iff-stated, verbatim.
            //
            // Trap: this must stay ABOVE the <Properties> element. XmlWriter refuses an
            // attribute once the writer has entered element content, and the throw returns a
            // null document rather than a diagnostic -- the same failure the SourceObject
            // attribute ordering note below records.
            // IsNullOrEmpty, not IsNullOrWhiteSpace, and Caption skips the OrNullIfBlank its
            // neighbours use: pages 1433 and 9260 state a Caption of one SPACE and BC writes
            // CaptionML="ENU= ". Either tidy-up drops the attribute on both.
            if (!string.IsNullOrEmpty(page.Caption))
                w.WriteAttributeString("CaptionML", EnuMultiLanguage(page.Caption));

            w.WriteStartElement("Properties");
            w.WriteAttributeString("SourceExtensionType", "ModernDev");
            w.WriteAttributeString("PageType", page.PageType);
            w.WriteAttributeString("Editable", page.Editable ? "1" : "0");
            // #3784. Written unconditionally, carrying what the symbol file states — NOT the
            // literal "1" this used to write for every page, which was a WRONG answer rather
            // than a missing one on the 105 of 236 System Application + Business Foundation
            // pages BC's own emitter writes "0" for, all 105 of which state Extensible = "0"
            // in the symbol file with zero omissions. Same shape as Editable above it.
            w.WriteAttributeString("Extensible", page.Extensible ? "1" : "0");
            EmitPagePropertiesXml(w, page, manifestHelpUrl);
            if (!string.IsNullOrEmpty(page.Caption))
            {
                w.WriteStartElement("CaptionML");
                w.WriteStartElement("Caption");
                w.WriteAttributeString("Id", "1033");
                w.WriteString(page.Caption);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            // ALWAYS written, even for a page with no source table (issue #2451). Same
            // reason as the empty <Content> element below: MetaPageDefinition deserializes a
            // MISSING element to null rather than to an empty one, and BC dereferences this
            // one WITHOUT a null check —
            // MetadataProvider.MergePageAndTable reads
            // `masterPage.PageProperties.SourceObject.SourceTable > 0` as its first act.
            // Omitting it NREs inside BC's own metadata merge, which
            // RunnerPageInstance.TryCreateRecordless catches and turns into null, which
            // silently demotes the TestPage to the navigation mock — every action there
            // answers Enabled = true and Invoke() is a literal no-op.
            //
            // The real AL compiler writes it unconditionally too: across the 3187 page
            // metadata documents in this machine's dependency-compile sidecars,
            // <SourceObject> appears in all 3187, and in 1114 of them it carries no
            // SourceTable attribute at all — the empty form written here.
            w.WriteStartElement("SourceObject");
            if (page.SourceTableId > 0)
            {
                w.WriteAttributeString("SourceTable",
                    page.SourceTableId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (page.SourceTableTemporary)
                    w.WriteAttributeString("SourceTableTemporary", "1");
                // AutoSplitKey defaults to false in AL, so only a true one is written, and it
                // stays INSIDE this branch: all 3 pages of the 236 measured that state it also
                // declare a source table, and NeedsAutoSplitKey's reader only runs for a bound
                // page anyway.
                //
                // Measured by compiling a page declaring it and reading back the metadata the
                // compiler captured for it, on BC 28.1:
                //     <SourceObject AutoSplitKey="1" DelayedInsert="1"
                //                   MultipleNewLines="1" SourceTable="65940" />
                //
                // AutoSplitKey is the one with teeth. RunnerPageInstance.NeedsAutoSplitKey
                // reads form.MasterPage.PageProperties.SourceObject.AutoSplitKey, so
                // omitting it here read false for every page shipping precompiled in a
                // dependency .app, BC's client half of AutoSplitKey silently did not run,
                // and per the note in MockTestPage the first new row then lands at line
                // no. 0 and the second fails on a duplicate primary key.
                if (page.AutoSplitKey) w.WriteAttributeString("AutoSplitKey", "1");
                EmitSourceObjectIndirectPermissions(w, page);
            }
            // SourceTable and AutoSplitKey above only mean anything alongside a source table,
            // so a page without one gets the bare element the compiler itself emits — not
            // SourceTable="0", which would answer "table 0" to a question about a table the
            // page does not have.
            //
            // Everything below is OUTSIDE that branch on purpose — measured, not assumed; see
            // EmitSourceObjectPropertiesXml.
            //
            // ORDER IS LOAD-BEARING, and this is the whole reason the SourceTableView child
            // element moved below them: every attribute of <SourceObject> must be written
            // BEFORE its first child element, because XmlWriter refuses an attribute once the
            // writer has entered element content. Writing these five after
            // EmitSourceTableViewXml threw InvalidOperationException for exactly the pages
            // declaring a SourceTableView AND one of the five — Base Application 700 "Error
            // Messages" and 1710 "Deferral Lines - G/L", both `LinksAllowed = 0` plus a view —
            // and the throw came back as a NULL metadata document, so BC then NRE'd in
            // NCLMetaForm.GetFrozenPageDefinitionWithExtensionWithoutMergedMultiLanguage and
            // page 1710's view stopped filtering. The unit tests could not see it: their
            // fixture pages declare a view or one of the five, never both. Three corpus tests
            // did.
            EmitSourceObjectPropertiesXml(w, page);
            // The page's SourceTableView, which BC's own NavForm.ApplySourceTableView reads
            // from exactly here (issue #2820) — see EmitSourceTableViewXml. A CHILD ELEMENT,
            // so it must come after every attribute above.
            if (page.SourceTableId > 0 && page.TableView is { } view)
                EmitSourceTableViewXml(w, page, view);
            w.WriteEndElement(); // SourceObject
            w.WriteEndElement(); // Properties

            // Present-but-empty for the third time, and for the third identical reason:
            // MetadataProvider.LoadExpressionRelationTables iterates
            // `masterPage.Expressions` with no null check, so a missing element NREs one
            // statement after the SourceObject read above. The real compiler emits it on all
            // 3187 documents measured. No general control tree is reconstructed (see the
            // file header) — only parts (below), whose bindings are resolved from THIS XML,
            // not from an <Expressions> entry — so this deserializes to an empty collection,
            // which is what a page with no bound controls would have anyway.
            w.WriteStartElement("Expressions");
            w.WriteEndElement();

            // An empty-but-present Content element, not an absent one: NCLMetaForm.
            // LoadPageMetadata()'s own post-load check (EnsureNoControlIdAppearsMoreThanOnce)
            // unconditionally iterates page.Content.Containers, and MetaPageDefinition
            // deserializes a MISSING <Content> element to a null Content rather than an
            // empty one — so leaving the element out entirely NREs there, one call deeper
            // than the FindPageType gap this file exists to close.
            //
            // Issue #2467: Content now also carries the page's subpage PART controls, still
            // no ordinary field controls (the file header's reasoning for those is
            // unchanged — their VALUE BINDINGS are IL, not XML). A part is different:
            // RunnerPageInstance.TryGetPartDefinition resolves it entirely from THIS XML
            // (form.MetadataHelper.InfoPartDefinitions, itself built by BC's own
            // NCLMetaForm.LoadPageMetadata walking Content), so reconstructing it here closes
            // the gap at its actual source rather than working around it.
            w.WriteStartElement("Content");
            if (page.Parts is { Count: > 0 })
            {
                w.WriteStartElement("Containers");
                w.WriteAttributeString("xsi", "type", XsiNs, "ControlContainerDefinition");
                w.WriteAttributeString("ContainerType", "ContentArea");
                foreach (var part in page.Parts)
                    EmitPartControlXml(w, page, part);
                w.WriteEndElement(); // Containers
            }
            w.WriteEndElement(); // Content

            // LAST, which is where BC's own emitter puts it: over the 235 PageDefinition
            // documents of Business Foundation + System Application at 28.1.49838.53910, the 5
            // carrying <Methods> all end with it, after Triggers (#4267).
            EmitPageMethodsXml(w, page, methodsProvenComplete);

            w.WriteEndElement(); // PageDefinition
        }
        return sb.ToString();
    }

    private const string XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// One ENU text as the MultiLanguage attribute value BC's emitter writes, serialized by BC's
    /// own <c>MultiLanguageExtensions.ToMultiLanguageString</c>: a text containing <c>;</c>,
    /// <c>=</c> or <c>"</c> is quoted, which a bare <c>"ENU=" + text</c> is not, and BC's parser
    /// then splits it at the <c>;</c> (#4282, page 4312's AboutTextML).
    /// </summary>
    private static string EnuMultiLanguage(string text)
        => AlRunner.Patches.EnuMultiLanguageText.ToMultiLanguageString(text);

    /// <summary>
    /// The page's <c>&lt;Methods&gt;</c> subtree — BC's emitted method table — written only when
    /// the runner can prove its view of it is COMPLETE, and omitted entirely otherwise (#4267).
    ///
    /// <para><b>Observably equivalent:</b> BC's <c>ObjectMetadataEmitter</c> writes a page's
    /// ATTRIBUTED methods, and <c>SymbolReference.json</c> states every event PUBLISHER exactly —
    /// by id, by name and in BC's document order. Over Business Foundation + System Application at
    /// BC 28.1.49838.53910, BC emits <c>&lt;Methods&gt;</c> on 5 of 235 pages and those are exactly
    /// the 5 whose symbol entry states an event-publisher attribute, each with the id and name the
    /// symbol file states. The filter is <c>EmittedMethodAttributeKinds</c> and the reader is
    /// <c>ReadAttributedMethods</c>, both shared with the codeunit path, so one rule has one
    /// spelling. See docs/dependency-page-methods.md.</para>
    ///
    /// <para><b>The gate is the assembly witness, never the list's own length.</b> The symbol file
    /// states no event SUBSCRIBER — an AL subscriber is always <c>local</c> and the file is an
    /// app's consumer-facing API surface — and a page CAN host one: Base Application
    /// 28.1.49838.53910 ships <c>EventRecorder.Page.al</c>, which declares one.
    /// <c>MetadataObjectDiff</c> pairs <c>Methods</c> POSITIONALLY, so a short list puts every
    /// later element in a different method's slot, which is worse than stating nothing
    /// (loud-failures.md).</para>
    ///
    /// <para><b>Trap for a later editor:</b> 107 of those 235 pages state a non-empty
    /// <c>Methods</c> array, and that is NOT the number that get a subtree. 105 state only
    /// ordinary public procedures and 8 only <c>Scope</c>/<c>Obsolete</c>/<c>NonDebuggable</c>,
    /// none of which BC's emitter writes. Keying on the array being non-empty would manufacture a
    /// difference on 102 pages.</para>
    ///
    /// <para>The element shape is pinned against the codeunit renderer by
    /// <c>DependencyPageMethodSubtreeRenderingParityTests</c>, so the two cannot drift.</para>
    /// </summary>
    private static void EmitPageMethodsXml(
        XmlWriter w, BcAppSymbolCache.PageSymbol page, bool methodsProvenComplete)
    {
        // Two conditions, and the first is the third state: a page whose app's assemblies were
        // never scanned proves nothing, which is not the same as proving it has no subscriber
        // (guards-need-a-third-state.md).
        if (!methodsProvenComplete) return;
        if (page.AttributedMethods is not { Count: > 0 } methods) return;

        WriteMethodsSubtree(w, methods);
    }

    /// <summary>
    /// The <c>&lt;Methods&gt;</c> element BC's emitter writes, for a page or a codeunit alike —
    /// one renderer, because BC's document has one shape for both and two copies would be free to
    /// drift. <c>RecordPatches.CodeunitMetadataEquivalence.cs</c>'s <c>AppendMethodsSubtree</c>
    /// builds the same shape through <c>XmlDocument</c>; the parity test renders one list both
    /// ways and compares.
    ///
    /// <para><c>Name</c> on the attribute element is REQUIRED, not decorative: BC's own
    /// <c>MetaCodeunit(XmlNode)</c> throws <c>NullReferenceException</c> on an attribute element
    /// that has none. The publisher flags beyond it come from
    /// <see cref="RecordPatches.PublisherAttributes"/>, which owns which of the three BC writes
    /// and when.</para>
    /// </summary>
    private static void WriteMethodsSubtree(
        XmlWriter w, IReadOnlyList<BcAppSymbolCache.CodeunitMethodSymbol> methods)
    {
        w.WriteStartElement("Methods");
        foreach (var method in methods)
        {
            w.WriteStartElement("Method");
            w.WriteAttributeString(
                "ID", method.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteAttributeString("Name", method.Name);

            w.WriteStartElement("MethodAttributes");
            w.WriteStartElement(method.Kind);
            w.WriteAttributeString("Name", method.AttributeName);
            // The three publisher flags, each written only where BC writes it — the SHARED
            // definition RecordPatches owns, so this renderer and the codeunit one cannot drift
            // (#4443). GlobalVarAccess exists only on IntegrationEvent, and Isolated sits in a
            // different argument slot per attribute name.
            foreach (var (flagName, flagValue) in RecordPatches.PublisherAttributes(method))
                w.WriteAttributeString(flagName, flagValue);
            // BC writes these on a PAGE document exactly as on a codeunit one — measured, not
            // assumed: a probe app declaring an InherentPermissions method on both object kinds,
            // emitted through BC's own compiler at 28.1.49838.53910, produced the same five
            // attributes on the PageDefinition as on the CodeUnit. The shipped Microsoft apps
            // the ground-truth bundles cover happen to have no such page (Base Application has
            // exactly one, page 99000833), so the bundle's zero is a property of those apps and
            // never of BC's emitter (#4339).
            foreach (var (name, value) in InherentPermissionAttributes(method.InherentPermission))
                w.WriteAttributeString(name, value);
            w.WriteEndElement(); // the attribute kind
            w.WriteEndElement(); // MethodAttributes

            WriteParametersSubtree(w, method.Parameters);

            w.WriteEndElement(); // Method
        }
        w.WriteEndElement(); // Methods
    }

    /// <summary>
    /// The <c>&lt;Parameters&gt;</c> element BC writes on every <c>&lt;Method&gt;</c> it emits —
    /// the <c>XmlWriter</c> half of the pair whose <c>XmlDocument</c> half is
    /// <c>RecordPatches.AppendParametersSubtree</c>, and pinned against it by
    /// <c>DependencyPageMethodSubtreeRenderingParityTests</c> (#4084).
    ///
    /// <para><b>A null list withdraws the element entirely</b>, which is the honest one-directional
    /// absence the runner had before #4084: <c>DeriveMethodParameters</c> answers null when any one
    /// parameter is a shape the derivation has not measured against BC's emitter, and a partial
    /// list would mis-pair positionally. An EMPTY list writes <c>&lt;Parameters /&gt;</c>, which is
    /// what BC writes for a method declaring none.</para>
    /// </summary>
    private static void WriteParametersSubtree(
        XmlWriter w, IReadOnlyList<BcAppSymbolCache.MethodParameterSymbol>? parameters)
    {
        if (parameters is null) return;
        w.WriteStartElement("Parameters");
        foreach (var p in parameters)
        {
            w.WriteStartElement("Parameter");
            foreach (var (name, value) in RecordPatches.ParameterAttributes(p))
                w.WriteAttributeString(name, value);
            w.WriteEndElement(); // Parameter
        }
        w.WriteEndElement(); // Parameters
    }

    /// <summary>
    /// The <c>&lt;Properties&gt;</c> attributes SymbolReference.json states and this
    /// synthesizer used to drop (issue #3784), all of them a READ: the symbol file states the
    /// value and this writes it, with no resolution, derivation or default-guessing anywhere.
    ///
    /// <para>THE RULE, in two halves, because BC's emitter applies a different one per
    /// property and the difference is measurable. <c>RefreshOnActivate</c> is written
    /// UNCONDITIONALLY, carrying the AL default when the file states nothing, exactly like
    /// <c>Extensible</c> and <c>Editable</c> at the call site. Everything else is written IF
    /// AND ONLY IF the file states it, because BC's <c>PageProperties</c> raises a
    /// <c>…Specified</c> bit from the SETTER — <c>UsageCategorySpecified</c>,
    /// <c>InherentEntitlementsSpecified</c>, <c>InherentPermissionsSpecified</c> — which its
    /// <c>Equals()</c> compares, so writing a "default" for a silent page is a DIFFERENT
    /// document, not a harmless one.</para>
    ///
    /// <para>Measured on BC 28.4.53241.54407 over Business Foundation (11 pages) + System
    /// Application (225), by cross-tabulating each app's shipped SymbolReference.json against
    /// the PageDefinition documents <c>tools/gen-metadata-ground-truth.sh</c> produces from
    /// BC's own emitter. Every pair below agreed on all 236 pages:</para>
    /// <code>
    /// RefreshOnActivate    absent -> "0" (207 pages);  "1" -> "1" (29)
    /// UsageCategory        written iff stated, verbatim (68 pages, 6 distinct enum names)
    /// HelpLink             written iff stated, verbatim (6)
    /// IsPreview            written iff stated (1)
    /// AboutTitle/AboutText/AdditionalSearchTerms/InstructionalText
    ///                      -> the SAME attribute prefixed "ENU=" (21/21/28/6)
    /// InherentEntitlements/InherentPermissions   "X" -> "16"   (94/92)
    /// </code>
    ///
    /// <para>The DERIVED properties are not in this list: <c>HelpLink</c>,
    /// <c>DataCaptionExpr</c>, <c>AnalysisModeEnabled</c>, <c>CardFormID</c> and the no-PageType
    /// <c>IsPreview</c> each have their own emitter below (#4282,
    /// docs/dependency-page-properties.md). <c>OnAfterGetCurrentRecordEnabled</c> tracks trigger
    /// presence and is still not written.</para>
    /// </summary>
    private static void EmitPagePropertiesXml(
        XmlWriter w, BcAppSymbolCache.PageSymbol page, string? manifestHelpUrl)
    {
        // Unconditional, carrying the AL default for a page that states nothing — the same
        // shape as Extensible and Editable at the call site.
        w.WriteAttributeString("RefreshOnActivate", page.RefreshOnActivate ? "1" : "0");

        void Scalar(string name, string? stated)
        {
            if (!string.IsNullOrEmpty(stated)) w.WriteAttributeString(name, stated);
        }

        Scalar("UsageCategory", page.UsageCategory);
        EmitPageHelpLink(w, page, manifestHelpUrl);
        // #4282. BC does NOT write the AL expression here -- it writes the fixed marker
        // "DataCaptionExprCode" recording that the page HAS a caption expression, and the
        // expression itself compiles into the page's own IL. Measured over the same 235
        // documents: 32 pages state DataCaptionExpression, BC writes DataCaptionExpr on exactly
        // those 32, and the value is "DataCaptionExprCode" on all 32 with no other value
        // anywhere. So this is write-iff-stated with a constant, and writing the AL text would
        // be a different wrong answer rather than the missing one.
        if (!string.IsNullOrEmpty(page.DataCaptionExpression))
            w.WriteAttributeString("DataCaptionExpr", "DataCaptionExprCode");
        // #4282. A page stating NO PageType is not the same document as one stating Card: BC
        // writes IsPreview="0" and AnalysisModeEnabled="1" for it (below), measured on page 1998
        // and on a compiled probe -- docs/dependency-page-properties.md#no-pagetype.
        if (page.IsPreview) w.WriteAttributeString("IsPreview", "1");
        else if (!page.PageTypeStated) w.WriteAttributeString("IsPreview", "0");
        EmitPageAnalysisModeEnabled(w, page);
        EmitPageCardFormId(w, page);

        // BC's emitter writes these four as a MultiLanguage attribute, and its own
        // MultiLanguage parser reads "ENU=<text>" — the identical form EmitPartControlXml
        // already writes for a part's CaptionML. The symbol file states the bare text.
        void MultiLanguage(string name, string? stated)
        {
            if (!string.IsNullOrEmpty(stated)) w.WriteAttributeString(name, EnuMultiLanguage(stated));
        }

        MultiLanguage("AboutTitleML", page.AboutTitle);
        MultiLanguage("AboutTextML", page.AboutText);
        MultiLanguage("AdditionalSearchTermsML", page.AdditionalSearchTerms);
        MultiLanguage("InstructionalTextML", page.InstructionalText);

        EmitInherentMask(w, page, "InherentEntitlements", page.InherentEntitlements);
        EmitInherentMask(w, page, "InherentPermissions", page.InherentPermissions);
    }

    /// <summary>
    /// The page's <c>HelpLink</c>: <see cref="DeriveHelpLink"/> over what the page states and the
    /// declaring app's manifest URL, and no attribute at all when that is null (#4282, #4675).
    /// Matched BC on all 235 pages of Business Foundation + System Application at
    /// 28.1.49838.53910 (docs/dependency-page-properties.md#helplink).
    ///
    /// <para><b>Trap for a later editor:</b> <c>ContextSensitiveHelpPage</c> is concatenated
    /// unconditionally, even when it looks absolute, because that is what BC's emitter does.</para>
    /// </summary>
    private static void EmitPageHelpLink(
        XmlWriter w, BcAppSymbolCache.PageSymbol page, string? manifestHelpUrl)
    {
        var helpLink = DeriveHelpLink(page.HelpLink, page.ContextSensitiveHelpPage, manifestHelpUrl);
        if (helpLink != null) w.WriteAttributeString("HelpLink", helpLink);
    }

    /// <summary>
    /// <c>AnalysisModeEnabled</c>, which BC's emitter DERIVES for most pages (#4282).
    ///
    /// <para><b>Observably equivalent:</b> the stated value when the page states one, else
    /// <c>"1"</c> for a <c>List</c>/<c>Worksheet</c> page or a page stating no <c>PageType</c> at
    /// all, else nothing. That reproduces BC's attribute on all 235 pages of Business Foundation +
    /// System Application at 28.1.49838.53910 (94 written) and on a compiled probe covering every
    /// arm. See docs/dependency-page-properties.md#analysismodeenabled.</para>
    ///
    /// <para><b>Trap:</b> <c>PageType</c> on the symbol defaults to <c>"Card"</c>, so test
    /// <c>PageTypeStated</c>, never the type: BC gives a Card-by-default page the attribute and a
    /// page stating Card none.</para>
    /// </summary>
    private static void EmitPageAnalysisModeEnabled(XmlWriter w, BcAppSymbolCache.PageSymbol page)
    {
        var derived = !page.PageTypeStated
            || string.Equals(page.PageType, "List", StringComparison.OrdinalIgnoreCase)
            || string.Equals(page.PageType, "Worksheet", StringComparison.OrdinalIgnoreCase);
        if (page.AnalysisModeEnabled is { } stated)
            w.WriteAttributeString("AnalysisModeEnabled", stated ? "1" : "0");
        else if (derived)
            w.WriteAttributeString("AnalysisModeEnabled", "1");
    }

    /// <summary>
    /// <c>CardFormID</c>: the page's <c>CardPageId</c>, which the symbol file states as the target
    /// page's NAME and BC writes as its resolved id (#4282).
    ///
    /// <para><b>Observably equivalent:</b> resolved through <see cref="PageIdsByName"/>, the same
    /// inventory the Page Metadata virtual table resolves <c>CardPageId</c> against, so the two
    /// cannot disagree. Measured: the 10 Business Foundation + System Application pages stating it
    /// at 28.1.49838.53910 each resolve to exactly BC's id, and a compiled probe shows BC writes it
    /// for a numeric <c>CardPageId</c> and on a non-List page too.</para>
    ///
    /// <para>An unresolvable name is OMITTED and SAID: a guessed id would open the wrong card,
    /// and the absent attribute is what this wrote before.</para>
    /// </summary>
    private static void EmitPageCardFormId(XmlWriter w, BcAppSymbolCache.PageSymbol page)
    {
        var stated = page.CardPageName?.Trim();
        if (string.IsNullOrEmpty(stated)) return;

        var unquoted = stated.Length >= 2 && stated[0] == '"' && stated[^1] == '"' ? stated[1..^1] : stated;
        if (!int.TryParse(unquoted, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var cardId)
            && !PageIdsByName().TryGetValue(unquoted, out cardId))
        {
            Console.Error.WriteLine(
                $"[RecordPatches] page {page.Id} \"{page.Name}\": CardPageId \"{stated}\" names no page "
                + "the run knows — CardFormID omitted");
            return;
        }

        w.WriteAttributeString("CardFormID", cardId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The <c>&lt;SourceObject IndirectPermissions&gt;</c> vector BC compiles a page's AL
    /// <c>Permissions</c> property into (#4282): <c>"&lt;table id&gt;, &lt;mask&gt;, …, 0, 0"</c>
    /// in declared order, each table NAME resolved to its id.
    ///
    /// <para><b>Observably equivalent:</b> matches BC's string exactly on all 84 pages of
    /// Business Foundation + System Application at 28.1.49838.53910 that carry it, and on a
    /// compiled probe. See docs/dependency-page-properties.md#indirectpermissions.</para>
    ///
    /// <para><b>Two traps.</b> The mask is the INDIRECT bits whatever the letter's case —
    /// <c>RIMD</c> and <c>rimd</c> both give 480 — which is the opposite of
    /// <see cref="EmitInherentMask"/>'s case rule, so do not route this through
    /// <see cref="TryDecodePermissionMaskLettersCore"/>. And BC writes nothing for a page with
    /// no <c>SourceTable</c> even when it states <c>Permissions</c> (2 of 86 pages), which is why
    /// the caller sits inside the source-table branch.</para>
    ///
    /// <para>Any entry this cannot read or resolve withdraws the WHOLE attribute, loudly: a
    /// partial vector would grant a different permission set than the page declares.</para>
    /// </summary>
    private static void EmitSourceObjectIndirectPermissions(XmlWriter w, BcAppSymbolCache.PageSymbol page)
    {
        if (string.IsNullOrWhiteSpace(page.Permissions)) return;
        var vector = TryBuildIndirectPermissionsVector(page.Permissions, ResolveTableIdByName, out var unreadable);
        if (vector is null)
        {
            Console.Error.WriteLine(
                $"[RecordPatches] page {page.Id} \"{page.Name}\": Permissions entry {unreadable} "
                + "could not be read — IndirectPermissions omitted, so the page reads as declaring none");
            return;
        }
        w.WriteAttributeString("IndirectPermissions", vector);
    }

    /// <summary>
    /// The parse behind <see cref="EmitSourceObjectIndirectPermissions"/>, with the table
    /// resolver injected so it is testable without a registered app. Null, with the offending
    /// entry in <paramref name="unreadable"/>, when any entry is not
    /// <c>tabledata &lt;name|id&gt; = &lt;letters from RIMD&gt;</c> or names no known table.
    /// </summary>
    internal static string? TryBuildIndirectPermissionsVector(
        string stated, Func<string, int> resolveTableId, out string? unreadable)
    {
        unreadable = null;
        var parts = new List<string>();
        foreach (var entry in SplitOutsideQuotes(stated, ','))
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                entry.Trim(), @"^tabledata\s+(?<name>""[^""]*""|[^=\s]+)\s*=\s*(?<mask>[A-Za-z]+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) { unreadable = $"'{entry.Trim()}'"; return null; }

            var name = m.Groups["name"].Value.Trim('"');
            var tableId = int.TryParse(name, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var literal)
                ? literal : resolveTableId(name);
            if (tableId <= 0) { unreadable = $"'{entry.Trim()}' (no table named '{name}')"; return null; }

            var mask = 0;
            foreach (var c in m.Groups["mask"].Value)
            {
                var bit = char.ToLowerInvariant(c) switch { 'r' => 32, 'i' => 64, 'm' => 128, 'd' => 256, _ => 0 };
                if (bit == 0) { unreadable = $"'{entry.Trim()}' (letter '{c}')"; return null; }
                mask |= bit;
            }
            parts.Add(tableId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            parts.Add(mask.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (parts.Count == 0) { unreadable = "'(empty)'"; return null; }
        parts.Add("0");
        parts.Add("0");
        return string.Join(", ", parts);
    }

    private static IEnumerable<string> SplitOutsideQuotes(string text, char separator)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            else if (text[i] == separator && !quoted)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    /// <summary>
    /// One inherent-permission mask: the symbol file states AL permission LETTERS
    /// (<c>InherentEntitlements = X</c>) and BC's <c>PageProperties</c> holds an
    /// <c>Int32</c>, so the letters are decoded against
    /// <c>Microsoft.Dynamics.Nav.Types.PermissionMask</c> through the shared
    /// <see cref="TryDecodePermissionMaskLettersCore"/> — <c>Read 1, Insert 2, Modify 4,
    /// Delete 8, Execute 16</c>, and the same five again as INDIRECT bits at n+5 for a
    /// lowercase letter. That enum is what makes this a DECODE of a stated value rather than
    /// a guess at an absent one.
    ///
    /// <para><b>Case is significant and must not be normalised</b> (#3933). This decoded with
    /// <c>char.ToUpperInvariant</c> until then, so <c>"x"</c> answered 16 where BC answers 512
    /// and <c>"rX"</c> answered 17 where BC answers 48 — a page reading as holding a DIRECT
    /// permission it does not have. Latent when fixed: measured across every shipped app at BC
    /// 28.4.53241.54407, pages carry 210 masks and all 210 are <c>"X"</c>, so no Microsoft page
    /// reached it; an ISV page declaring one does. The codeunit direction has a real instance
    /// (System Application codeunit 2516), which is what settled the spelling — see
    /// docs/codeunit-metadata-from-bc.md#permission-mask-spelling.</para>
    ///
    /// <para>A letter this cannot read is OMITTED and SAID, never folded into 0 — the same
    /// choice, and the same reason, as the unreadable-boolean and wrong-shape-DataCaptionFields
    /// arms of <see cref="EmitSourceObjectPropertiesXml"/>. Both a mask of 0 and an absent
    /// attribute are answers BC can tell apart (the <c>Specified</c> bit its setter raises),
    /// so inventing either from a value nobody could read would be a silent wrong answer on a
    /// surface with no way to fail. It deliberately does NOT reuse the shared decoder's throw:
    /// on this builder a throw is the quieter answer, because it returns a null document and
    /// demotes the whole TestPage — the trade is stated once at
    /// <see cref="TryDecodePermissionMaskLettersCore"/>.</para>
    /// </summary>
    private static void EmitInherentMask(
        XmlWriter w, BcAppSymbolCache.PageSymbol page, string attribute, string? stated)
    {
        if (string.IsNullOrWhiteSpace(stated)) return;

        // The shared decoder's arithmetic, and NOT its throw — see
        // TryDecodePermissionMaskLettersCore for why the policy splits here.
        if (!TryDecodePermissionMaskLettersCore(stated, out var mask, out var unreadable))
        {
            Console.Error.WriteLine(
                $"[RecordPatches] page {page.Id} \"{page.Name}\": {attribute} \"{stated}\" "
                + $"states '{unreadable}', which is not one of the {PermissionMaskLetters} "
                + "permission letters BC reads in either case — omitted, so the page reads as "
                + "declaring none");
            return;
        }

        if (mask == 0) return;
        w.WriteAttributeString(attribute, mask.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The five further <c>&lt;SourceObject&gt;</c> properties the symbol file states and this
    /// synthesizer used to drop (issue #2860): <c>LinksAllowed</c>, <c>ShowFilter</c>,
    /// <c>SaveValues</c>, <c>PopulateAllFields</c> and <c>DataCaptionFields</c>.
    ///
    /// <para>THE RULE, AND WHY IT IS NOT "WRITE THE NON-DEFAULT ONES". Each attribute is
    /// written if and only if the symbol file states the property, carrying the value the
    /// symbol file states — including when that value IS the AL default. That is what the
    /// real AL compiler does, measured on BC 28.1 by compiling pages that declare these and
    /// reading back the metadata the compiler captured for each
    /// (<c>AL_RUNNER_TRACE_PAGE_METADATA=2</c>):</para>
    /// <code>
    /// // LinksAllowed=false ShowFilter=false SaveValues=true PopulateAllFields=true
    /// // DataCaptionFields="No.",Descr
    /// &lt;SourceObject DataCaptionFields="1,3" LinksAllowed="0" PopulateAllFields="1"
    ///               SaveValues="1" ShowFilter="0" SourceTable="64900" /&gt;
    ///
    /// // the same four declared as their AL DEFAULTS — still written
    /// &lt;SourceObject LinksAllowed="1" PopulateAllFields="0" SaveValues="0"
    ///               ShowFilter="1" SourceTable="64900" /&gt;
    ///
    /// // a page declaring none of them
    /// &lt;SourceObject SourceTable="64900" /&gt;
    ///
    /// // a page with NO SourceTable declaring three of them
    /// &lt;SourceObject LinksAllowed="0" SaveValues="1" ShowFilter="0" /&gt;
    /// </code>
    ///
    /// <para>WHY NOT INSIDE THE <c>SourceTable</c> BRANCH, unlike InsertAllowed/AutoSplitKey:
    /// the last measurement above. 30 Base Application 28.1 pages declare one of these five
    /// with no source table — wizards and NavigatePages declaring <c>LinksAllowed = false</c>
    /// or <c>ShowFilter = false</c>, and page 9991 "Code Coverage Setup" declaring
    /// <c>SaveValues = true</c> — and <c>NavForm.InitializeFromMetadata</c> reads
    /// <c>SourceObject.SaveValues</c> with no SourceTable guard.</para>
    ///
    /// <para>WHAT READS THEM. <c>PopulateAllFields</c> is the one with teeth:
    /// <c>NavForm.NewRecordAsync</c> passes
    /// <c>MasterPage.PageProperties.SourceObject.PopulateAllFields</c> as
    /// <c>NavRecord.InitializeFieldsFromFilters</c>' <c>includeNonPrimaryKeyFields</c>
    /// argument on EVERY new row, and BC's <c>SourceObjectDefinition(XmlNode)</c> constructor
    /// initialises the field to <c>false</c> before reading attributes — so the dropped
    /// attribute was not a missing value but a wrong one, <c>false</c> where BC answers
    /// <c>true</c>, for the 46 Base Application 28.1 pages declaring it.
    /// <c>SaveValues</c> is read by <c>NavForm.InitializeFromMetadata</c> into
    /// <c>NavForm.saveValues</c>, which gates
    /// <c>ApplySourceTableViewAndSavedValuesAsync</c>'s call to <c>ApplyLatestValuesAsync()</c>
    /// on the <c>NavForm.OpenForm()</c> route <c>RunnerModalDispatch.TryOpenForm</c> takes —
    /// and carrying it adds no new risk, because a page the runner SOURCE-compiles already
    /// gets <c>SaveValues="1"</c> from the real compiler and opens and closes through that
    /// same route today. <c>LinksAllowed</c>, <c>ShowFilter</c> and <c>DataCaptionFields</c>
    /// are referenced in Ncl only from <c>PageDataProvider</c>, the data provider behind the
    /// Page Metadata (2000000138) system table, which this runner substitutes wholesale
    /// (RecordPatches.PageMetadataVirtualTable.cs) — so those three have no reader here yet
    /// and are carried because the value is the symbol file's own, not because one was found.
    /// The virtual table's own missing columns are tracked separately.</para>
    /// </summary>
    private static void EmitSourceObjectPropertiesXml(XmlWriter w, BcAppSymbolCache.PageSymbol page)
    {
        void Flag(string name, bool? stated)
        {
            if (stated is { } value) w.WriteAttributeString(name, value ? "1" : "0");
        }

        Flag("LinksAllowed", page.LinksAllowed);
        Flag("ShowFilter", page.ShowFilter);
        Flag("SaveValues", page.SaveValues);
        Flag("PopulateAllFields", page.PopulateAllFields);

        // #3784's five, on exactly the same rule and for exactly the same reason — the only
        // difference is that these were already being written, just wrongly: the trio could
        // only ever emit a "0" (so an explicitly-stated `InsertAllowed = true`, which BC's
        // emitter writes on 18 of the 236 pages measured, was dropped), and all five sat
        // inside the `SourceTable > 0` branch, which silenced them entirely for the 5 System
        // Application pages that state one with no source table — 502 OAuth2ControlAddIn,
        // 2718 Page Summary Settings, 4326 Agent Creation Control, 7775 Copilot AI
        // Capabilities, 9260 Customer Experience Survey. BC's emitter writes the attributes
        // for all five, and BC's SourceObjectDefinition reader has no SourceTable guard.
        //
        // Cross-tabulated over those 236 pages (BC 28.4.53241.54407), symbol value -> emitted
        // attribute, with no disagreement in either direction:
        //     InsertAllowed    "0"->"0" 121, "1"->"1" 1,  absent->absent 114
        //     ModifyAllowed    "0"->"0" 80,  "1"->"1" 11, absent->absent 145
        //     DeleteAllowed    "0"->"0" 101, "1"->"1" 6,  absent->absent 129
        //     DelayedInsert    "1"->"1" 14,  "0"->"0" 1,  absent->absent 221
        //     MultipleNewLines "0"->"0" 3,   "1"->"1" 1,  absent->absent 232
        Flag("InsertAllowed", page.InsertAllowedStated);
        Flag("ModifyAllowed", page.ModifyAllowedStated);
        Flag("DeleteAllowed", page.DeleteAllowedStated);
        Flag("MultipleNewLines", page.MultipleNewLinesStated);
        Flag("DelayedInsert", page.DelayedInsertStated);

        // A boolean the symbol file STATED in a form the parser could not read comes through
        // as the same null as "not stated at all", and therefore as the same absent attribute
        // — so absence cannot distinguish them and the difference has to be said out loud.
        // Treating "I could not read this" as "the AL declares nothing" is the shape of the
        // defect this whole change fixes, and it is not allowed to reappear one level down.
        // Never observed on a Microsoft-produced symbol file; this fires only if the format
        // changes under us, which is exactly when silence would cost the most.
        if (page.UnreadableBooleanProperties is { Count: > 0 } unreadable)
            Console.Error.WriteLine(
                $"[RecordPatches] page {page.Id} \"{page.Name}\": SourceObject property value(s) "
                + string.Join(", ", unreadable)
                + " not readable as a boolean — omitted, so the page reads as declaring nothing there");

        if (page.DataCaptionFields is not { Length: > 0 } captionFields) return;

        // The only one of the five that is not a boolean, and the only one whose shape has to
        // be checked rather than passed through: BC reads DataCaptionFields as a
        // comma-separated list of FIELD NUMBERS. All 381 Base Application 28.1 pages stating
        // it state numbers, because the same compiler writes both the symbol file and the
        // compiled metadata — but a value that is not that shape cannot be turned into one
        // here (resolving field NAMES would need the source table's field inventory, which a
        // page declaring no source table does not have at all), so it is omitted and SAID.
        //
        // Omitting is itself a wrong answer — it reads as "this page declares no data caption
        // fields" — which is exactly why the diagnostic is not optional. Same choice, same
        // reason, as the SourceTableView Sorting arm below: nothing downstream can be made to
        // fail on this value, so the failure has to be reported rather than encoded.
        if (!IsFieldNumberList(captionFields))
        {
            Console.Error.WriteLine(
                $"[RecordPatches] page {page.Id} \"{page.Name}\": DataCaptionFields "
                + $"\"{captionFields}\" is not the comma-separated field-number list BC reads "
                + "— omitted, so the page reads as declaring none");
            return;
        }
        w.WriteAttributeString("DataCaptionFields", captionFields);
    }

    /// <summary>A non-empty comma-separated list of decimal field numbers, and nothing
    /// else — the shape BC's <c>DataCaptionFields</c> consumers parse.</summary>
    private static bool IsFieldNumberList(string value)
    {
        foreach (var part in value.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) return false;
            foreach (var c in trimmed)
                if (c < '0' || c > '9') return false;
        }
        return true;
    }

    /// <summary>
    /// One subpage PART control, as an <c>InfopartPageDefinition</c> — the shape the real AL
    /// compiler emits (measured against this machine's compiled-deps sidecars: every
    /// <c>&lt;SubFormLink&gt;</c> observed there carries <c>FilterGroup="4"</c>). Property
    /// attributes (Editable/Enabled/Visible/ShowFilter) are written RAW, exactly as
    /// PageControlSymbol already does for field controls — an AL-bound one resolves later
    /// through the page's own registered source expressions (real IL, not this XML); a
    /// literal true/false/number resolves directly. When the symbol file states none, the AL
    /// default BC's emitter writes (#4282).
    /// </summary>
    private static void EmitPartControlXml(XmlWriter w, BcAppSymbolCache.PageSymbol hostPage, BcAppSymbolCache.PagePartSymbol part)
    {
        w.WriteStartElement("Controls");
        w.WriteAttributeString("xsi", "type", XsiNs, "InfopartPageDefinition");
        w.WriteAttributeString("ID", part.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteAttributeString("Name", part.Name);
        w.WriteAttributeString("PagePartID", part.PagePartId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(part.Caption)) w.WriteAttributeString("CaptionML", EnuMultiLanguage(part.Caption));
        // #4282. Observably equivalent: BC's emitter writes these on EVERY part — the stated
        // value, else the part's host-page ApplicationArea (absent when neither states one), and
        // "true"/"true"/"true"/"1" for the four flags. Zero disagreements over every part of
        // Business Foundation + System Application on 27.5.46862.53931, 28.1.49838.53910,
        // 28.1.49838.54308 and 28.4.53241.54407, plus a compiled probe for the no-area and
        // group/FactBox arms: docs/dependency-page-properties.md#part-controls. Nothing at
        // runtime reads a part's ApplicationArea here (MetadataProviderElementRemoval disables
        // BC's filter), and each default is the value BC's reader already answers for absence.
        var applicationArea = part.ApplicationArea ?? hostPage.ApplicationArea;
        if (!string.IsNullOrEmpty(applicationArea)) w.WriteAttributeString("ApplicationArea", applicationArea);
        w.WriteAttributeString("Editable", part.EditableExpr ?? "true");
        w.WriteAttributeString("Enabled", part.EnabledExpr ?? "true");
        w.WriteAttributeString("Visible", part.VisibleExpr ?? "true");
        w.WriteAttributeString("ShowFilter", part.ShowFilterExpr ?? "1");
        if (!string.IsNullOrEmpty(part.AboutTitle)) w.WriteAttributeString("AboutTitleML", EnuMultiLanguage(part.AboutTitle));
        if (!string.IsNullOrEmpty(part.AboutText)) w.WriteAttributeString("AboutTextML", EnuMultiLanguage(part.AboutText));

        foreach (var link in part.SubFormLink)
            EmitSubFormLinkXml(w, hostPage, part, link);

        // #2978: a SubPageLink entry ParseSubPageLink could not read at all. Dropping it —
        // what this did before — left the part filtered on FEWER conditions than its AL
        // declares, so the subpage showed rows the host row does not own, and the only trace
        // was a Console.Error line the symbol cache loses on every warm run. Emit it as a
        // link BC refuses instead: FieldID 0 is what MockTestPage.SubPageLinks already
        // refuses BY NAME for every kind, the same fail-closed channel an unresolvable part
        // field already uses two lines above.
        if (part.UnreadableSubPageLinkEntries is { Count: > 0 } unreadable)
            foreach (var entry in unreadable)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] page {hostPage.Id} \"{hostPage.Name}\" part \"{part.Name}\": "
                    + $"SubPageLink entry not readable: '{entry}' — the part will refuse to open "
                    + "rather than show rows its link excludes");
                w.WriteStartElement("SubFormLink");
                w.WriteAttributeString("FilterGroup", "4");
                w.WriteAttributeString("FieldID", "0");
                w.WriteAttributeString("FilterType", "CONST");
                w.WriteAttributeString("FilterValue", XmlSafe(entry));
                w.WriteEndElement();
            }

        w.WriteEndElement(); // Controls
    }

    /// <summary>
    /// One <c>SubFormLink</c> entry, resolved from AL text to the shape BC's own compiled
    /// metadata carries (MockTestPage.SubPageLinks reads
    /// <c>InfopartPageDefinition.SubFormLink</c> as (FieldID, FilterType, FilterValue), never
    /// AL text). All three kinds filter for real: FIELD resolves both field names to numbers,
    /// CONST normalises its literal to the compiler's representation
    /// (<see cref="NormalizeConstLinkValue"/>), FILTER re-quotes its expression for BC's
    /// filter grammar (#2469). A FIELD entry whose parent field name this run cannot resolve
    /// to an id is written with a value that reliably trips MockTestPage.SubPageLinks' OWN
    /// existing refusal (a non-numeric FilterValue), and an unresolvable PART field id is
    /// written as 0, which that method refuses by name for every kind — an honest
    /// "testpage-part-link" out-of-scope refusal rather than a silently unfiltered part,
    /// which would show every row of the child table instead of only the parent's.
    /// </summary>
    private static void EmitSubFormLinkXml(
        XmlWriter w, BcAppSymbolCache.PageSymbol hostPage, BcAppSymbolCache.PagePartSymbol part,
        BcAppSymbolCache.PageSubFormLinkSymbol link)
    {
        int partTableId = RecordPatches.ResolveSourceTableIdForAnyPage(part.PagePartId);
        int? partFieldId = RecordPatches.TryResolveDependencyFieldId(partTableId, link.PartFieldName);
        var isFieldKind = string.Equals(link.Kind, "field", StringComparison.OrdinalIgnoreCase);
        var parentFieldName = isFieldKind ? link.Value.Trim('"') : null;
        int? parentFieldId = isFieldKind
            ? RecordPatches.TryResolveDependencyFieldId(hostPage.SourceTableId, parentFieldName!)
            : null;

        // #2978: an entry inside an AL `#if` block may or may not be in the compiled app, and
        // nothing in the symbol file records which — the compiler stores the property's SOURCE
        // text, directives and all. So a field name that does not resolve means two different
        // things depending on the entry: for an UNCONDITIONAL one it is a broken link and the
        // page must refuse to open (the arm below), and for a CONDITIONAL one it is the app
        // saying that AL is not in it, where refusing the page over a link it does not have
        // would be a wrong answer in the other direction.
        //
        // BC 27.5's Base Application pages 76 "Resource Card" and 77 "Resource List" are the
        // only real instance: `#if not CLEAN25 "Service Zone Filter" = field("Service Zone
        // Filter")`. That name resolves — the Serv. Resource tableextension adds it to
        // Resource in both 27.5 and 28.1 — so this applies it, and every observable signal
        // agrees that is right (see BcAppSymbolCache.SplitPropertyEntries for why CLEANnn
        // reads as undefined in Microsoft's shipped builds). If that ever turns out backwards
        // the page over-filters, which is narrower than BC and fails loudly, rather than the
        // silent widening this change exists to stop.
        if (link.Conditional && (partFieldId is null || (isFieldKind && parentFieldId is null)))
        {
            Console.Error.WriteLine(
                $"[RecordPatches] page {hostPage.Id} \"{hostPage.Name}\" part \"{part.Name}\": "
                + $"conditional SubPageLink entry \"{link.PartFieldName}\" omitted — the field it "
                + $"names is not in this app, so the AL directive guarding it compiled the entry out");
            return;
        }

        w.WriteStartElement("SubFormLink");
        w.WriteAttributeString("FilterGroup", "4");
        w.WriteAttributeString("FieldID",
            (partFieldId ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (isFieldKind)
        {
            w.WriteAttributeString("FilterType", "FIELD");
            // Unresolved renders as the field NAME, not a number — MockTestPage.SubPageLinks
            // int.TryParse()s this and refuses by name when it isn't numeric, which is
            // exactly the honest outcome an unresolved link deserves.
            w.WriteAttributeString("FilterValue",
                parentFieldId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? parentFieldName);
        }
        else if (string.Equals(link.Kind, "const", StringComparison.OrdinalIgnoreCase))
        {
            w.WriteAttributeString("FilterType", "CONST");
            w.WriteAttributeString("FilterValue", NormalizeConstLinkValue(link.Value));
        }
        else
        {
            // filter(...) — the expression in BC's filter grammar. AL quotes an identifier
            // with double quotes, BC's filter tokenizer only knows single-quoted literals, so
            // the same re-quoting the CalcFormula `filter(...)` path already needed (#2305)
            // applies here: filter(Open | "Bank Acc. Entry Applied") becomes
            // Open | 'Bank Acc. Entry Applied'.
            w.WriteAttributeString("FilterType", "FILTER");
            w.WriteAttributeString("FilterValue", FilterValueText(link.Value));
        }
        w.WriteEndElement(); // SubFormLink
    }

    /// <summary>
    /// The page's <c>SourceTableView</c>, in the shape BC's own metadata carries it —
    /// <c>&lt;SourceTableView&gt;</c> under <c>&lt;SourceObject&gt;</c>, holding an optional
    /// <c>&lt;Sorting&gt;</c> and one <c>&lt;TableFilters&gt;</c> element per
    /// <c>where(...)</c> entry. That is what <c>NavForm.ApplySourceTableView</c> reads, and
    /// <c>RunnerPageInstance.ApplySourceTableViewFilters</c> now calls it on every page open,
    /// so a precompiled page's view finally filters (issue #2820: Base Application page 7016
    /// "Sales Price List" declares <c>where("Price Type" = const(Sale))</c>, and its OnOpenPage
    /// evaluates that filter's value into an enum with no blank member).
    ///
    /// <para>Shape measured, not guessed — a page declaring
    /// <c>SourceTableView = sorting(Bucket, "No.") order(descending) where(Bucket = filter(1|2),
    /// Kind = const(Purchase))</c> compiled on BC 28.1 produces:</para>
    /// <code>
    /// &lt;SourceTableView&gt;
    ///   &lt;Sorting KeyFields="Field2,Field1" KeyFieldsSetByView="1" AscendingSetByView="1" Ascending="0" /&gt;
    ///   &lt;TableFilters FilterGroup="2" FieldID="2" FilterType="FILTER" FilterValue="1|2" /&gt;
    ///   &lt;TableFilters FilterGroup="2" FieldID="3" FilterType="CONST" FilterValue="2" /&gt;
    /// &lt;/SourceTableView&gt;
    /// </code>
    ///
    /// <para>Two deliberate differences from that compiler output, both observably
    /// equivalent:</para>
    /// <list type="bullet">
    /// <item>The compiler ALWAYS writes <c>&lt;Sorting&gt;</c>, with all-zero
    /// <c>*SetByView</c> flags when the view declares no sorting. ApplySourceTableView acts on
    /// the element only through those two flags, so a view with neither omits it here rather
    /// than writing an element that can do nothing.</item>
    /// <item>An enum/option <c>const(Member)</c> is written as the member NAME, where the
    /// compiler writes its ordinal — the same equivalence
    /// <see cref="NormalizeConstLinkValue"/> already documents and relies on for SubPageLinks:
    /// the value goes through <c>Record.SetFilter</c>, whose grammar resolves an option member
    /// by name as readily as by ordinal, and the runner has no ordinal table for a
    /// dependency's fields here.</item>
    /// </list>
    ///
    /// <para>A field name this run cannot resolve to an id is written as <c>FieldID="0"</c>,
    /// which BC's own <c>MetaTable.GetFieldByNo(0)</c> refuses with
    /// <c>NavNCLFieldNotFoundException</c> naming the table when the page opens — the same
    /// "fail loudly rather than show unfiltered rows" choice EmitSubFormLinkXml makes for a
    /// part link, and the reason this cannot degrade into a silently ignored filter.</para>
    /// </summary>
    private static void EmitSourceTableViewXml(
        XmlWriter w, BcAppSymbolCache.PageSymbol page, BcAppSymbolCache.PageTableViewSymbol view)
    {
        w.WriteStartElement("SourceTableView");

        // <Sorting> IS UNCONDITIONAL, and the "only when the page declares sorting" version of
        // this guard silently discarded the WHOLE view for half the pages that have one
        // (#3063). BC's own PageDataProvider.GenerateSourceTableViewString opens with
        //
        //     if (view == null || view.Sorting == null) return NavText.Empty;
        //
        // so a MetaViewDefinition with real TableFilters and a null Sorting formats as the
        // empty string — the WHERE segment is never reached. The runner emitted exactly that
        // document for any page declaring `where(...)` and no `sorting(...)`, so Page Metadata
        // reported "this page declares no view" for a page that declares one. Measured across
        // this machine's platform .apps: 211 of the 417 pages carrying a SourceTableView
        // declare a where with no sorting, Base Application 1710 "Deferral Lines - G/L"
        // (`where("Deferral Doc. Type" = const("G/L"))`) among them.
        //
        // The real AL compiler always writes the element, and for a page declaring only a
        // where it writes it EMPTY — verified with AL_RUNNER_TRACE_PAGE_METADATA=2 on a
        // source-compiled page declaring `SourceTableView = where("Kind Code" = const('X'))`:
        //
        //     <SourceTableView>
        //       <Sorting KeyFields="" KeyFieldsSetByView="0" AscendingSetByView="0" Ascending="1" />
        //       <TableFilters FilterGroup="2" FieldID="3" FilterType="CONST" FilterValue="X" />
        //     </SourceTableView>
        //
        // Those are the attribute values written below when the page declares no sorting, so
        // the two page origins produce the same document rather than merely a working one:
        // KeyFields absent, both SetByView flags off, Ascending on. BC reads KeyFields only
        // when KeyFieldsSetByView says to, so an unconditional element adds no ordering the
        // page did not declare — it only stops the filters being thrown away.
        {
            var keyFieldIds = new List<string>(view.SortingFields.Count);
            var unresolved = false;
            foreach (var sortField in view.SortingFields)
            {
                var id = RecordPatches.TryResolveDependencyFieldId(page.SourceTableId, sortField.FieldName);

                // #3271: an entry inside an AL `#if` block may not be in the compiled app at
                // all, and this app's own field inventory is the only evidence available —
                // same rule and same reasoning as the conditional filter arm below, and as
                // EmitSubFormLinkXml's. Omit it and keep the rest of the key: a guarded entry
                // the app does not contain is not in the compiled page either, so the shorter
                // key is what that page actually declares. Refusing the whole key instead
                // would leave the page on the table's DEFAULT order, which is further from
                // what BC does rather than closer.
                if (sortField.Conditional && id is null)
                {
                    Console.Error.WriteLine(
                        $"[RecordPatches] page {page.Id} \"{page.Name}\": conditional SourceTableView "
                        + $"sorting field \"{sortField.FieldName}\" omitted — the field it names is not "
                        + "in this app, so the AL directive guarding it compiled the entry out");
                    continue;
                }

                if (id is null) { unresolved = true; break; }
                // BC's own spelling for MetaTable.GetKeyFieldIds: "Field<id>", in view order.
                keyFieldIds.Add("Field" + id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            w.WriteStartElement("Sorting");
            if (keyFieldIds.Count > 0 && !unresolved)
            {
                w.WriteAttributeString("KeyFields", string.Join(",", keyFieldIds));
                w.WriteAttributeString("KeyFieldsSetByView", "1");
            }
            else if (unresolved)
            {
                // A sorting field the run cannot resolve would otherwise silently reorder the
                // page. Say so, and leave the key alone rather than set a wrong one — unlike a
                // filter, a key CANNOT be made to fail loudly through the metadata (BC reads
                // KeyFields only when KeyFieldsSetByView says to).
                Console.Error.WriteLine(
                    $"[RecordPatches] page {page.Id} \"{page.Name}\": SourceTableView sorting("
                    + string.Join(", ", view.SortingFields.Select(f => f.FieldName))
                    + $") not applied — a field name did not resolve against table {page.SourceTableId}");
            }
            else
            {
                // No sorting declared. The compiler still writes both attributes, empty and
                // off; matching it keeps the two page origins byte-identical here, and BC
                // ignores KeyFields entirely while KeyFieldsSetByView is "0".
                w.WriteAttributeString("KeyFields", string.Empty);
                w.WriteAttributeString("KeyFieldsSetByView", "0");
            }
            if (view.Ascending.HasValue)
            {
                w.WriteAttributeString("AscendingSetByView", "1");
                w.WriteAttributeString("Ascending", view.Ascending.Value ? "1" : "0");
            }
            else
            {
                // The compiler's own shape for a page that declares no order(...): the flag
                // off and Ascending nonetheless "1". Written rather than omitted so a page
                // reached through a dependency .app and the same page compiled from source
                // produce byte-identical <Sorting> attributes.
                w.WriteAttributeString("AscendingSetByView", "0");
                w.WriteAttributeString("Ascending", "1");
            }
            w.WriteEndElement(); // Sorting
        }

        foreach (var filter in view.Filters)
        {
            var fieldId = RecordPatches.TryResolveDependencyFieldId(page.SourceTableId, filter.FieldName);

            // #2978: an entry inside an AL `#if` block may not be in the compiled app at all,
            // and this app's own field inventory is the only evidence available — same rule,
            // same reasoning as EmitSubFormLinkXml's conditional arm. No SourceTableView in BC
            // 27.5 or 28.1 W1 carries a directive today; the arm exists so the two paths
            // through the same splitter cannot answer it differently.
            if (filter.Conditional && fieldId is null)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] page {page.Id} \"{page.Name}\": conditional SourceTableView "
                    + $"filter \"{filter.FieldName}\" omitted — the field it names is not in this "
                    + "app, so the AL directive guarding it compiled the entry out");
                continue;
            }

            if (fieldId is null)
                Console.Error.WriteLine(
                    $"[RecordPatches] page {page.Id} \"{page.Name}\": SourceTableView field "
                    + $"\"{filter.FieldName}\" did not resolve against table {page.SourceTableId} — "
                    + "the page will refuse to open rather than show unfiltered rows");

            w.WriteStartElement("TableFilters");
            w.WriteAttributeString("FilterGroup", "2");
            w.WriteAttributeString("FieldID",
                (fieldId ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (string.Equals(filter.Kind, "const", StringComparison.OrdinalIgnoreCase))
            {
                w.WriteAttributeString("FilterType", "CONST");
                w.WriteAttributeString("FilterValue", NormalizeConstLinkValue(filter.Value));
            }
            else
            {
                w.WriteAttributeString("FilterType", "FILTER");
                w.WriteAttributeString("FilterValue", FilterValueText(filter.Value));
            }
            w.WriteEndElement(); // TableFilters
        }

        // #2978: a where(...) entry — or a whole clause whose parenthesis never closed —
        // ParseSourceTableView could not read. Dropping it shipped a PARTIAL view, which is
        // WIDER than the one the page declares: the page opened on rows the real view
        // excludes, a test asserting over them passed against a record set BC never gives,
        // and the only trace was a Console.Error line the symbol cache loses on every warm
        // run. FieldID 0 makes BC's own MetaTable.GetFieldByNo(0) refuse the page with
        // NavNCLFieldNotFoundException instead — the identical fail-closed channel the
        // unresolvable-field-name case above already uses, for the identical reason.
        if (view.UnreadableEntries is { Count: > 0 } unreadable)
            foreach (var entry in unreadable)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] page {page.Id} \"{page.Name}\": SourceTableView entry not "
                    + $"readable: '{entry}' — the page will refuse to open rather than show rows "
                    + "its view excludes");
                w.WriteStartElement("TableFilters");
                w.WriteAttributeString("FilterGroup", "2");
                w.WriteAttributeString("FieldID", "0");
                w.WriteAttributeString("FilterType", "CONST");
                w.WriteAttributeString("FilterValue", XmlSafe(entry));
                w.WriteEndElement();
            }

        w.WriteEndElement(); // SourceTableView
    }

    /// <summary>
    /// The unreadable AL text, made safe to put in an XML attribute: characters
    /// <see cref="XmlConvert.IsXmlChar"/> rejects would make XmlWriter throw, and the whole
    /// point of this value is that the runner could NOT read it, so it cannot be assumed
    /// well-formed. Capped, because it is a diagnostic for whoever reads the synthesized
    /// metadata — BC never gets as far as reading it, since FieldID 0 refuses first.
    /// </summary>
    private static string XmlSafe(string text)
    {
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, 200));
        foreach (var c in text)
        {
            if (sb.Length >= 200) break;
            sb.Append(XmlConvert.IsXmlChar(c) ? (char.IsControl(c) ? ' ' : c) : ' ');
        }
        return sb.ToString();
    }

    /// <summary>
    /// A <c>const(...)</c> SubPageLink value, from the AL source text SymbolReference.json
    /// records to the shape BC's own compiler writes into a compiled page's
    /// <c>SubFormLink/@FilterValue</c> — which is what <c>MockTestPage.SubPageLinks</c>
    /// consumes for a source-compiled page, so both routes hand the part one representation.
    /// Measured on BC 28.1's compiler output (corpus codeunit 60324 "TSPL Tests"):
    /// <c>const(Database::"TSPL Header")</c> compiles to the table id, <c>const('SPECIAL')</c>
    /// on a Code field to the bare text <c>SPECIAL</c>, and an option member to its ordinal.
    /// <list type="bullet">
    /// <item><c>Database::"Some Table"</c> / <c>Database::SomeTable</c> → the table id,
    /// resolved by name across the loaded apps; left as written when no loaded app declares
    /// the table, so the filter fails loudly in BC's own parser naming the text rather than
    /// silently pinning the part to a wrong id.</item>
    /// <item><c>"Some Enum"::Member</c> / <c>Enum::"Member Name"</c> → the member NAME. The
    /// compiler would write the ordinal; the runner has no enum ordinal table for a
    /// dependency's fields at this point, and BC's filter grammar resolves an option/enum
    /// member by name as readily as by ordinal, so the name is an equivalent
    /// representation, not an approximation.</item>
    /// <item>A quoted literal (<c>"On Hold"</c>, <c>'SPECIAL'</c>) → the bare text, AL's
    /// doubled-quote escape resolved — <c>ConstValueText</c>'s rule, shared with the
    /// CalcFormula <c>const(...)</c> path.</item>
    /// <item>Anything else (a number, a bare identifier, true/false) → as written.</item>
    /// </list>
    /// </summary>
    internal static string NormalizeConstLinkValue(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return s;

        const string dbPrefix = "Database::";
        if (s.StartsWith(dbPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var tableName = ConstValueText(s.Substring(dbPrefix.Length));
            var tableId = ResolveTableIdByName(tableName);
            return tableId > 0 ? tableId.ToString(System.Globalization.CultureInfo.InvariantCulture) : s;
        }

        // <Enum>::<Member> — the enum name may be a quoted identifier containing anything
        // (including "::"), so find the separator OUTSIDE quotes rather than with IndexOf.
        var sep = TopLevelScopeSeparator(s);
        if (sep > 0) return ConstValueText(s.Substring(sep + 2));

        return ConstValueText(s);
    }

    /// <summary>Index of the first <c>::</c> in <paramref name="s"/> that is not inside a
    /// double-quoted AL identifier, or -1.</summary>
    private static int TopLevelScopeSeparator(string s)
    {
        var inQuotes = false;
        for (int i = 0; i + 1 < s.Length; i++)
        {
            if (s[i] == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && s[i] == ':' && s[i + 1] == ':') return i;
        }
        return -1;
    }
}
