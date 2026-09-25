// RecordPatches.ExtensionRuntimeDeltasMetadataEquivalence — the runner-side accessor the
// metadata-equivalence harness needs for MetadataRuntimeDeltas (#3782 step 8, #3809).
//
// BC emits one <MetadataRuntimeDeltas> document per EXTENSION object; this renders the runner's
// side of it. Three things a later editor gets wrong, each with its derivation in
// docs/metadata-equivalence.md#metadataruntimedeltas:
//
//   - THE KEY IS (OBJECT TYPE, ID), NOT ID. The 28.1 bundles carry a tableextension 774 and a
//     pageextension 774, both "Plan User Details", with different documents — so keying on the
//     id alone answers one question for two objects, as the previous call site did.
//   - A RENDER, NOT AN OBJECT. AllDeltas is get-only over a private list and the only public
//     constructor is parameterless, so BC's own FromXml is the sole route to a populated
//     instance — the same reason TryBuildEnumMetadataEquivalenceXml renders (#3807).
//   - A VALUE THE RUNNER DOES NOT DERIVE IS LEFT OFF, NEVER DEFAULTED. Writing BC's
//     ControlGUID, SourceExtensionType or *TranslationKey hashes without reading them from the
//     symbol file would manufacture agreement, which is what this harness exists to catch.

using System.Globalization;
using System.Xml;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The runner's extension-runtime-delta document for one extension object, as the
    /// <c>&lt;MetadataRuntimeDeltas&gt;</c> BC's own
    /// <c>NavAppObjectMetadataRuntimeDeltas.FromXml</c> parses — or <c>null</c> when no
    /// registered dependency declares an extension of that object type with that id.
    ///
    /// <para><b>Null and an empty document are different answers, deliberately.</b> Null means
    /// "no such extension", matching BC's loader, which only calls its retriever when a matching
    /// Extension-format summary exists. An empty <c>&lt;MetadataRuntimeDeltas/&gt;</c> means "this
    /// extension exists and contributes no runtime delta" — which is what BC emits for all six
    /// tableextensions in the 28.1 bundles, every one of which declares only
    /// <c>Obsolete=Moved/Removed</c> fields.</para>
    /// </summary>
    internal static string? TryBuildExtensionRuntimeDeltasXml(string objectType, int extensionId)
    {
        // EnumerateRegisteredBcAppSymbols, never BcAppSymbolCache.Get: it is what makes the
        // vanished-.app / unreadable-.app split, so a package that disappeared between
        // registration and now is skipped with a warning while one that is present but
        // unreadable RAISES rather than reading as "declares no extensions"
        // (RecordPatches.DependencyAppSymbolWalk.cs; #3143). The walk hands over the parsed
        // symbols, so nothing below re-reads the package.
        foreach (var (appPath, symbols) in
                 EnumerateRegisteredBcAppSymbols("extension runtime deltas (metadata equivalence)"))
        {
            var xml = TryBuildExtensionRuntimeDeltasXmlFrom(symbols, appPath, objectType, extensionId);
            if (xml is not null) return xml;
        }
        return null;
    }

    /// <summary>
    /// The same render, scoped to one .app. Split out so a test can drive it against a fixture
    /// package without registering it in the process-global dependency state.
    /// </summary>
    internal static string? TryBuildExtensionRuntimeDeltasXmlForApp(
        string appPath, string objectType, int extensionId)
        => TryBuildExtensionRuntimeDeltasXmlFrom(
            BcAppSymbolCache.Get(appPath), appPath, objectType, extensionId);

    private static string? TryBuildExtensionRuntimeDeltasXmlFrom(
        BcAppSymbolCache.AppSymbols symbols, string appPath, string objectType, int extensionId)
    {
        // The object type decides which declaration list is even asked, which is what makes a
        // tableextension and a pageextension on one id two questions rather than one.
        return objectType switch
        {
            "Page" => TryRenderPageExtensionDeltas(symbols, appPath, extensionId),
            "Table" => TryRenderTableExtensionDeltas(appPath, extensionId),
            // Every other object type: the runner tracks no extension declarations for it, so
            // "no such extension" is the honest answer rather than an empty document.
            _ => null,
        };
    }

    private static string? TryRenderPageExtensionDeltas(
        BcAppSymbolCache.AppSymbols symbols, string appPath, int extensionId)
    {
        var ext = symbols.PageExtensions?.FirstOrDefault(e => e.Id == extensionId);
        if (ext is null) return null;

        var doc = NewDeltasDocument(ext.Id, ext.Name, out var root);
        // Built lazily: only a pageextension that adds a Rec-bound control needs the join.
        Dictionary<string, int>? sourceTableFieldIds = null;

        // FIRST, and only when the extension states object-level properties: BC opens the
        // document with <PagePropertiesChange>, and the equivalence differ pairs AllDeltas
        // POSITIONALLY, so a document missing it compares every later element against its
        // neighbour (#3926 group 3).
        //
        // MEASURED over the 45 captured ground-truth documents on four BC builds
        // (27.5.46862.53931, 28.1.49838.53910, 28.1.49838.54308, 28.4.53241.54407): nine such
        // elements, always at index 0, in exactly two shapes.
        //
        //   ext  774  Properties [Editable=0]                  <PagePropertiesChange Editable="0"/>
        //   ext 2516  Properties [Obsolete{State,Reason,Tag}]   <PagePropertiesChange/>
        //   ext 4318  Properties [Obsolete{State,Reason,Tag}]   <PagePropertiesChange/>
        //   ext 2515, 9862, 324, 6635  Properties []            ABSENT
        //
        // The absent row is load-bearing: emitting this unconditionally would add a spurious
        // first element to four of the seven pageextensions carrying deltas and shift THOSE the
        // other way. Editable is written verbatim -- the symbol states the string "0" and BC
        // writes Editable="0". The Obsolete* trio produces the element and contributes no
        // attribute, so the test for it asserts on Attributes() being empty rather than on a
        // value. A null bag is a payload predating the field and is treated as "states none",
        // which is the pre-#3926 behaviour rather than a guess.
        //
        // TRAP: only attributes BC is MEASURED to write belong here. Passing the whole bag
        // through would write ObsoleteState/ObsoleteReason/ObsoleteTag attributes BC does not
        // emit, manufacturing a difference the harness would then report against BC.
        if (ext.ObjectProperties is { Count: > 0 } objectProperties)
        {
            var change = doc.CreateElement("PagePropertiesChange", MetaObjectsNamespace);
            if (objectProperties.TryGetValue("Editable", out var editable)
                && editable is { Length: > 0 })
                change.SetAttribute("Editable", editable);
            root.AppendChild(change);
        }

        // MemberIdToName merges the two change containers; MemberIdToOrigin is what survives that
        // merge, because BC puts an added action in <ActionAdd> and an added control in
        // <ControlAdd> and neither the id nor the name says which. A null map (a cached payload
        // predating the field) renders an EMPTY document rather than guessing.
        //
        // ORDER ON Origin.Sequence — AL declaration order, never member id. The equivalence
        // differ pairs AllDeltas POSITIONALLY, so a reordered render reports correctly-rendered
        // members as wrong ones (docs/metadata-equivalence.md#deltas-declaration-order). Sequence
        // rather than Dictionary enumeration order, which is documented as unspecified.
        foreach (var (memberId, origin) in (ext.MemberIdToOrigin ?? new()).OrderBy(kv => kv.Value.Sequence))
        {
            // An id in the origin map but not the name map cannot be rendered: the member has no
            // declared name. Skipped rather than emitted nameless.
            if (!ext.MemberIdToName.TryGetValue(memberId, out var memberName)) continue;

            var wrapper = doc.CreateElement(origin.IsAction ? "ActionAdd" : "ControlAdd", MetaObjectsNamespace);

            // THREE attributes are REQUIRED — a document missing any one of them does not parse
            // at all, because BC Enum.Parses it and throws ArgumentNullException on a null. The
            // set was MEASURED by stripping one attribute at a time from BC's own document for
            // pageextension 774 inside the engine collection and asking FromXml: wrapper
            // ParentContainer, wrapper Operation, and the member's xsi:type. Nothing else is —
            // not SemanticKind, not ControlGUID, not any *TranslationKey.
            //
            // Measure it inside the harness if you re-check it. The same strip run OUTSIDE the
            // bc-engine-serial collection reports every attribute as required, because the
            // unstripped baseline already fails there on a WindowsLanguageHelper static-init
            // fault — every row is then a measurement of the fault (#3809).
            //
            // ParentContainer is an ENUM to BC's reader, not free text: an anchor outside its
            // member set throws ArgumentException and loses the whole document. The AL anchor
            // names either an AREA or a SIBLING MEMBER, and only the first is a container — and
            // then under BC's RUNTIME spelling, which is a different word from AL's for five of
            // the ten action areas (BcContainerForAlArea). A sibling anchor keeps the
            // kind-implied fallback; the container BC writes for it is the sibling's own, which
            // SymbolReference does not state from this side (#3926). What the anchor means per
            // change kind is in docs/metadata-equivalence.md#deltas-required-attributes.
            var container = origin.Anchor is { Length: > 0 }
                ? BcContainerForAlArea(origin.Anchor, origin.IsAction)
                : null;
            wrapper.SetAttribute("ParentContainer",
                container ?? (origin.IsAction ? "ActionItems" : "ContentArea"));
            wrapper.SetAttribute("Operation", ContentOperation(origin.ChangeKind));

            // Not required by the reader, but SymbolReference does determine it: the container
            // the member was declared in is what BC writes here on all 11 documents.
            wrapper.SetAttribute("SemanticKind", origin.IsAction ? "Action" : "Content");

            // AnchorName only for the kinds whose Anchor names a SIBLING (addbefore/addafter),
            // and only when it IS a sibling: an anchor naming an area is the container, and BC
            // writes no AnchorName for one — measured over all 111 delta elements in the four
            // cached builds' bundles, of which 32 carry an AnchorName and NOT ONE of those 32 is
            // an AL area name. BC's AnchorId — a hash beside it — is left off either way.
            if (!string.IsNullOrEmpty(origin.Anchor) && origin.ChangeKind is 3 or 4
                && container is null)
                wrapper.SetAttribute("AnchorName", origin.Anchor);

            var member = doc.CreateElement(origin.IsAction ? "Actions" : "Controls", MetaObjectsNamespace);
            // xsi:type is what tells BC's reader which Delta subclass to construct; without it
            // FromXml throws rather than skipping the element.
            member.SetAttribute("type", XsiNamespace,
                origin.IsAction ? "ActionDefinition" : "ControlDefinition");
            member.SetAttribute("ID", memberId.ToString(CultureInfo.InvariantCulture));
            member.SetAttribute("Name", memberName);
            SetStatedMemberAttributes(member, origin.DeclaredProperties);
            if (!origin.IsAction
                && TryBoundRecFieldName(origin.DeclaredProperties) is { } boundField)
            {
                sourceTableFieldIds ??= SourceTableFieldIdsByName(symbols, appPath, ext.TargetObjectName);
                if (sourceTableFieldIds.TryGetValue(boundField, out var fieldId))
                    member.SetAttribute("DataColumnName", fieldId.ToString(CultureInfo.InvariantCulture));
            }
            wrapper.AppendChild(member);
            root.AppendChild(wrapper);
        }

        return doc.OuterXml;
    }

    /// <summary>
    /// The delta-member attributes SymbolReference.json STATES, read off the member's own
    /// <c>Properties</c> bag — the group-1 members of #3926. A property the bag does not state
    /// leaves its attribute off, exactly as before.
    ///
    /// <para><b>Observably equivalent</b> for each attribute written here, because BC's emitter
    /// writes the same value from the same source. MEASURED against BC's own emitted documents
    /// for all five pageextensions carrying deltas in the 28.1.49838.53910 Business Foundation +
    /// System Application bundles — every pair below is an exact match, with no exceptions in
    /// the measured population (docs/metadata-equivalence.md#deltas-stated-member-attributes).</para>
    ///
    /// <list type="bullet">
    /// <item><c>ApplicationArea</c> and <c>Image</c> pass through VERBATIM — 7 and 3 pairs.</item>
    /// <item><c>CaptionML</c>/<c>ToolTipML</c> are AL's <c>Caption</c>/<c>ToolTip</c> under an
    /// <c>ENU=</c> prefix — 4 and 6 pairs. BC's emitter writes a one-language ML string, so the
    /// prefix is the whole transform; the text is unchanged.</item>
    /// <item><c>Visible</c>/<c>Enabled</c> ONLY for a boolean LITERAL — see the trap.</item>
    /// </list>
    ///
    /// <para><b>The trap, and why the literal split is not an optimisation.</b> For an
    /// EXPRESSION, BC does not write the AL name: ext 2515 states <c>Enabled "IsSaas"</c> and BC
    /// writes <c>px2515px2515IsSaas</c>; ext 324 states <c>CopilotActionsVisible</c> against BC's
    /// <c>px324px324CopilotActionsVisible</c>. That is a mangled reference to the extension's own
    /// global, built from the extension id, and SymbolReference states neither the mangling nor
    /// which globals it applies to. Writing the bare AL name would be a WRONG value derived from
    /// a right input — the one thing this harness had none of before (#3926) — so an expression
    /// is left off and stays an allowlisted difference. A literal has no such gap: all three of
    /// pageextension 774's hidden controls state <c>Visible "false"</c> and BC writes
    /// <c>false</c>. Those 3 literals and 7 expressions are the WHOLE stated population across
    /// both bundles, so the split is exhaustive there rather than a sample.</para>
    ///
    /// <para>What is deliberately NOT read here, though #3926's group-1 list names it:
    /// <c>RunObjectType</c>, <c>TargetID</c>, <c>RunObjectSrcTable</c> and <c>PushAction</c>.
    /// The symbol file states a bare object NAME (<c>RunObject: "AppSource Product List"</c>) and
    /// BC states the RESOLVED type and id (<c>Page</c>, <c>2515</c>) — a different fact, needing
    /// the page inventory this layer does not have, for the reason
    /// <see cref="BcAppSymbolCache.ActionRunObjectSymbol"/>'s own summary gives.</para>
    ///
    /// <para><c>RunPageMode</c> is read ONLY when stated: every stated value in the four captured
    /// builds is what BC writes (5 of 5). An unstated one is left off, because BC writes
    /// <c>Edit</c> on the three trigger actions stating nothing and that default rule is
    /// unmeasured.</para>
    /// </summary>
    private static void SetStatedMemberAttributes(
        XmlElement member, Dictionary<string, string>? declared)
    {
        if (declared is null) return;

        Pass("ApplicationArea", "ApplicationArea");
        Pass("Image", "Image");
        Ml("Caption", "CaptionML");
        Ml("ToolTip", "ToolTipML");
        BooleanLiteral("Visible", "Visible");
        BooleanLiteral("Enabled", "Enabled");
        Pass("RunPageMode", "RunPageMode");

        void Pass(string stated, string attribute)
        {
            if (Stated(stated) is { } v) member.SetAttribute(attribute, v);
        }

        // BC's emitter renders an AL Caption/ToolTip as a one-language ML string. The ENU tag is
        // what a .app's own SymbolReference carries its single language under; the Translations/
        // entries are a separate surface this render does not reach.
        void Ml(string stated, string attribute)
        {
            if (Stated(stated) is { } v) member.SetAttribute(attribute, "ENU=" + v);
        }

        void BooleanLiteral(string stated, string attribute)
        {
            // The accepted set is exactly `true`/`false`, which is how the compiler writes these
            // two for a pageextension member, and it is written through UNCHANGED rather than
            // normalised. Deliberately NOT extended to the `1`/`0` spelling this file accepts for
            // other boolean properties: the measured population states no `1`, `0` or `true` at
            // all (only `false`, 3 times), so an accepted `1` would rest on an extrapolation, and
            // BC's one `Visible="1"` is on an actionref whose symbol entry states no Properties —
            // BC-computed, so it is not evidence about a stated value either. Anything else is an
            // expression the runner cannot resolve to BC's mangled global name — left off, per
            // the trap above.
            if (Stated(stated) is not { } v) return;
            var t = v.Trim();
            if (t is "true" or "false") member.SetAttribute(attribute, t);
        }

        string? Stated(string name)
            => declared.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    /// <summary>
    /// The field name a control's stated <c>SourceExpression</c> binds on <c>Rec</c> —
    /// <c>Rec."User Plans"</c> or <c>Rec.Plans</c> — or null for anything else (a global, an
    /// expression, another record variable), which then gets no <c>DataColumnName</c>.
    /// </summary>
    private static string? TryBoundRecFieldName(Dictionary<string, string>? declared)
    {
        if (declared is null || !declared.TryGetValue("SourceExpression", out var se)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            se.Trim(), "^Rec\\.(?:\"(?<q>[^\"]+)\"|(?<b>[A-Za-z_][A-Za-z0-9_]*))$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["b"].Value;
    }

    /// <summary>
    /// Field ids by name for the SourceTable of the page named <paramref name="targetPageName"/>,
    /// base fields plus every tableextension's, all read from the SAME .app as the
    /// pageextension. BC's <c>DataColumnName</c> is that field id as text — measured 16 of 16
    /// on four builds (docs/metadata-equivalence.md#deltas-stated-member-attributes).
    ///
    /// <para>Deliberately same-app only: a target page or table declared in another app answers
    /// an empty map, so the attribute is left off rather than resolved against an inventory
    /// this render has not been measured against.</para>
    /// </summary>
    private static Dictionary<string, int> SourceTableFieldIdsByName(
        BcAppSymbolCache.AppSymbols symbols, string appPath, string targetPageName)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var page = symbols.Pages?.FirstOrDefault(p =>
            string.Equals(p.Name, targetPageName, StringComparison.OrdinalIgnoreCase));
        if (page is null || page.SourceTableId <= 0) return result;
        var table = symbols.Tables?.FirstOrDefault(t => t.TableId == page.SourceTableId);
        if (table is null) return result;

        foreach (var f in table.Fields) result.TryAdd(f.FieldName, f.FieldId);
        foreach (var te in BcAppSymbolCache.GetTableExtensions(appPath))
            if (string.Equals(te.TargetTableName, table.TableName, StringComparison.OrdinalIgnoreCase))
                foreach (var f in te.Fields) result.TryAdd(f.FieldName, f.FieldId);
        return result;
    }

    private static string? TryRenderTableExtensionDeltas(string appPath, int extensionId)
    {
        var ext = BcAppSymbolCache.GetTableExtensions(appPath)
            .FirstOrDefault(e => e.ExtensionId == extensionId);
        if (ext is null) return null;

        // Deliberately EMPTY, and this is a measurement rather than a shortcut. A tableextension's
        // added fields and keys are not runtime deltas: BC folds them into the target table's own
        // MetaTable document, which this harness already compares. All six tableextensions in the
        // 28.1 bundles emit an empty <MetadataRuntimeDeltas/>, and every one of them declares only
        // Obsolete=Moved/Removed fields — fields that no longer exist at runtime to have a delta.
        // Rendering a FieldAdd here would disagree with BC on six of the eleven documents.
        NewDeltasDocument(ext.ExtensionId, ext.ExtensionName, out var root);
        return root.OwnerDocument!.OuterXml;
    }

    /// <summary>
    /// AL's <c>ChangeKind</c> as the <c>ContentMetadataChangeKind</c> name BC's delta readers
    /// <c>Enum.Parse</c>: <c>ContentFirst</c>, <c>ContentLast</c>, <c>ContentAfter</c>,
    /// <c>ContentBefore</c> — AL's addfirst / addlast / addafter / addbefore.
    ///
    /// <para>MEASURED against BC's own emitted documents for the five pageextensions in the 28.1
    /// bundles rather than assumed from the enum's declaration order: 1 is ContentFirst (ext 324
    /// "Prompting", ext 2515 "Promoted"), 2 is ContentLast (ext 9862, ext 4318), 3 is
    /// ContentBefore (ext 774's four ControlAdds) and 4 is ContentAfter (ext 2515 anchored on
    /// "Refresh"). The enum's own ordinal order is First, Last, After, Before, so reading the
    /// mapping off the declaration would swap 3 and 4.</para>
    ///
    /// <para>An unrecognised kind falls back to <c>ContentLast</c>, which is AL's own default
    /// for an <c>add</c> with no positional keyword. That is a real answer rather than a guess
    /// dressed as one: the alternative is an attribute BC cannot parse, which loses the whole
    /// document instead of one member's position.</para>
    /// </summary>
    private static string ContentOperation(int changeKind) => changeKind switch
    {
        1 => "ContentFirst",
        2 => "ContentLast",
        3 => "ContentBefore",
        4 => "ContentAfter",
        _ => "ContentLast",
    };

    /// <summary>
    /// The <c>ParentContainer</c> BC writes for an AL <c>Anchor</c> that names an AREA, or
    /// <c>null</c> when the anchor names something else — a sibling member, or a word from
    /// neither vocabulary.
    ///
    /// <para><b>AL and the runtime are two different vocabularies and the anchor is in the first
    /// one.</b> AL source names the area (<c>ActionAreaKind</c> / <c>AreaKind</c>, in
    /// Microsoft.Dynamics.Nav.CodeAnalysis); the runtime names the container
    /// (<c>ActionContainerType</c> / <c>ControlContainerType</c>, in
    /// Microsoft.Dynamics.Nav.Types), and five of the ten action areas and four of the five
    /// control areas spell it differently — <c>addlast(Navigation)</c> is
    /// <c>ParentContainer="RelatedInformation"</c>. Passing the anchor through unchanged is
    /// therefore wrong for those, and so is falling back to the kind-implied container.</para>
    ///
    /// <para>MEASURED, not inferred: BC's own
    /// <c>CodeAnalysis.Emit.MetadataEmitterHelper.GetContainerType</c> — the method its emitter
    /// applies — invoked for every member of both enums on 27.5.46862.53931. Corroborated in a
    /// second binary by <c>Ncl.dll</c>'s <c>NavDesignerUtil.ActionContainerTypeToAreaKind</c>,
    /// which is the same relation read backwards and agrees on all seven pairs it covers.
    /// <c>ActionAreaKind.None</c> and <c>AreaKind.Navigation</c> both make that method throw
    /// <c>InvalidOperationException</c>, so neither is a container and both land in the null
    /// branch here.</para>
    ///
    /// <para>Deliberately a literal table rather than a reflective call into
    /// <c>Microsoft.Dynamics.Nav.CodeAnalysis</c>: this file must not take a load-time dependency
    /// on an assembly the runner does not otherwise need.
    /// <c>ExtensionRuntimeDeltasBcMappingTests</c> is what stops the table drifting from BC —
    /// it invokes <c>GetContainerType</c> itself and fails when the two disagree.</para>
    /// </summary>
    private static string? BcContainerForAlArea(string anchor, bool isAction) => isAction
        ? anchor switch
        {
            "Processing" => "ActionItems",
            "Reporting" => "Reports",
            "Navigation" => "RelatedInformation",
            "Creation" => "NewDocumentItems",
            "Embedding" => "HomeItems",
            "Sections" => "ActivityButtons",
            "Promoted" => "Promoted",
            "SystemActions" => "SystemActions",
            "Prompting" => "Prompting",
            "PromptGuide" => "PromptGuide",
            _ => null,
        }
        : anchor switch
        {
            "Content" => "ContentArea",
            "FactBoxes" => "FactBoxArea",
            "RoleCenter" => "RoleCenterArea",
            "Prompt" => "PromptArea",
            "PromptOptions" => "PromptOptionsArea",
            _ => null,
        };

    /// <summary>
    /// <see cref="BcContainerForAlArea"/>, for the drift test that holds its table to BC's own
    /// <c>MetadataEmitterHelper.GetContainerType</c>.
    /// </summary>
    internal static string? BcContainerForAlAreaForTests(string anchor, bool isAction)
        => BcContainerForAlArea(anchor, isAction);

    private const string MetaObjectsNamespace = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// An empty <c>&lt;MetadataRuntimeDeltas&gt;</c> root carrying the extension's own id and
    /// name, in the namespace BC's <c>MetadataRuntimeDeltasXName</c> resolves to.
    /// </summary>
    private static XmlDocument NewDeltasDocument(int id, string name, out XmlElement root)
    {
        var doc = new XmlDocument();
        root = doc.CreateElement("MetadataRuntimeDeltas", MetaObjectsNamespace);
        doc.AppendChild(root);

        // Declare xsi ON THE ROOT, with that exact prefix, exactly as BC's emitter does.
        // XmlDocument otherwise invents a prefix at the point of first use — measured as
        // `d3p1:type` on the member element — and BC's
        // Types.Metadata.ElementDefinition.RuntimeTypeCtor resolves the type from the attribute
        // it looks up by the `xsi` prefix, so an equivalent-but-differently-prefixed document
        // fails with InvalidOperationException("ActionBaseDefinition"): the abstract base, because
        // nothing told it which concrete subtype to build. Namespace-equivalent is not enough here.
        var xsiDecl = doc.CreateAttribute("xmlns", "xsi", "http://www.w3.org/2000/xmlns/");
        xsiDecl.Value = XsiNamespace;
        root.Attributes.Append(xsiDecl);

        root.SetAttribute("ID", id.ToString(CultureInfo.InvariantCulture));
        root.SetAttribute("Name", name);
        return doc;
    }
}
