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
            "Page" => TryRenderPageExtensionDeltas(symbols, extensionId),
            "Table" => TryRenderTableExtensionDeltas(appPath, extensionId),
            // Every other object type: the runner tracks no extension declarations for it, so
            // "no such extension" is the honest answer rather than an empty document.
            _ => null,
        };
    }

    private static string? TryRenderPageExtensionDeltas(
        BcAppSymbolCache.AppSymbols symbols, int extensionId)
    {
        var ext = symbols.PageExtensions?.FirstOrDefault(e => e.Id == extensionId);
        if (ext is null) return null;

        var doc = NewDeltasDocument(ext.Id, ext.Name, out var root);

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
            wrapper.AppendChild(member);
            root.AppendChild(wrapper);
        }

        return doc.OuterXml;
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
