// RecordPatches.ExtensionRuntimeDeltasMetadataEquivalence — the runner-side accessor the
// metadata-equivalence harness needs for MetadataRuntimeDeltas (#3782 step 8, #3809).
//
// WHAT THIS IS FOR
//   BC's emitter writes one <MetadataRuntimeDeltas> document per EXTENSION object —
//   tableextension, pageextension, permissionsetextension. Until this file the runner had
//   nothing to compare against any of them, so all 11 documents in the two 28.1 bundles were
//   reported as unbuildable and the comparison measured one side of a two-sided question.
//
// THE KEY IS (OBJECT TYPE, ID), NOT ID
//   BC's own NCLObjectXmlMetadataLoader.GetExtensionDeltasForAppObject matches an Extension-
//   format summary on `s.ObjectType == objectId.ObjectType && s.ObjectId == objectId.ObjectNumber`
//   — the extension's OWN id, paired with its object type. The pair is load-bearing rather than
//   decorative: Business Foundation + System Application 28.1 carry a tableextension 774 and a
//   pageextension 774, both named "Plan User Details", and BC emits a different document for
//   each (12 deltas vs an empty root). Keying on id alone answers one question for two objects.
//
// WHY A RENDER AND NOT AN OBJECT
//   NavAppObjectMetadataRuntimeDeltas exposes AllDeltas get-only over a private List<Delta> on
//   NavAppObjectMetadataDeltaCreator<Delta>, and its only public constructor is parameterless,
//   so no route sets deltas on an instance. Rendering the document BC's own FromXml reads is the
//   only way to produce a populated one — the same shape, and for the same reason, as
//   TryBuildEnumMetadataEquivalenceXml (#3807).
//
// A VALUE THE RUNNER DOES NOT DERIVE IS LEFT OFF, NEVER DEFAULTED
//   BC's emitted deltas carry ControlGUID, SourceExtensionType and several *TranslationKey
//   hashes, none of which SymbolReference.json states. They are omitted rather than filled with
//   a placeholder, so a reported difference is a true statement about what the runner does not
//   know. Writing any value would manufacture agreement, which is what this harness exists to
//   catch. Measured content of the real population, and what each side can state, is in
//   docs/metadata-equivalence.md#metadataruntimedeltas.

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

        // ActionChanges[].Actions and ControlChanges[].Controls are the two containers the
        // compiler writes for addfirst/addlast/addafter/addbefore, and TryParsePageExtensionSymbol
        // collects both into MemberIdToName — keyed, as BC keys them, in the EXTENSION's own id
        // space. A modify(...) change carries Properties only, so it adds no member here and
        // contributes no delta, which matches BC: its ControlChange delta is a MODIFY and is
        // deliberately not rendered as an add.
        //
        // MemberIdToOrigin is what survives that merge: BC's document puts an added action in
        // <ActionAdd> and an added control in <ControlAdd>, and neither the id nor the name says
        // which. A null map (a cached payload predating the field) renders an EMPTY document
        // rather than guessing every member into one of the two wrappers.
        //
        // DECLARATION ORDER, not id order. BC emits deltas in the order the AL declares them,
        // and the equivalence differ pairs AllDeltas POSITIONALLY — so a render sorted by id
        // reports every member of every reordered element as a difference. Measured: sorting by
        // id put pageextension 774's four controls in a different order from BC's and produced
        // ID and Name "differences" on controls the runner had in fact got exactly right, and
        // swapped ext 2515's two ActionChanges so their ContainerType and OperationType each
        // read as wrong. Both vanish under declaration order.
        //
        // Ordered on Origin.Sequence, which TryParsePageExtensionSymbol assigns as it walks the
        // file. Explicitly, not by relying on Dictionary enumeration order, which is documented
        // as unspecified.
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
            // WHAT THE ANCHOR MEANS DEPENDS ON THE CHANGE KIND, and only one case is derivable.
            // Measured across all five real pageextensions:
            //
            //   addfirst (1) — the Anchor IS the container. ext 324 Anchor "Prompting" ->
            //                  ParentContainer "Prompting"; ext 2515 "Promoted" -> "Promoted",
            //                  and BC writes no AnchorName. Derivable, and stated.
            //   addlast  (2) — the Anchor is a container too ("Processing"), but BC writes the
            //                  RESOLVED one ("ActionItems"). Resolving it needs the target page's
            //                  own layout, which this render does not have.
            //   add*      (3/4) — the Anchor is a SIBLING member, so BC writes it as AnchorName
            //                  and resolves ParentContainer from the target page ("ContentArea",
            //                  "ActionItems").
            //
            // ParentContainer is an ENUM to BC's reader, not free text: Enum.Parse throws
            // ArgumentException ("Requested value 'Processing' was not found") on anything
            // outside its member set, which loses the whole document. Across all 111 delta
            // elements in the four cached builds' bundles the observed set is exactly
            // {Prompting, ContentArea, ViewActions, ActionItems, Promoted, RelatedInformation}.
            //
            // So the Anchor is used only when it NAMES one of those containers — the addfirst
            // case, where BC does write it through verbatim (ext 324 "Prompting", ext 2515
            // "Promoted"). Otherwise the Anchor names a sibling member or an AL-level container
            // BC renames ("Processing" -> "ActionItems"), and resolving it needs the target
            // page's own layout, which this render does not have; the member's own kind gives
            // the container BC uses in the great majority of those cases.
            wrapper.SetAttribute("ParentContainer",
                !string.IsNullOrEmpty(origin.Anchor) && IsBcContainerName(origin.Anchor)
                    ? origin.Anchor
                    : origin.IsAction ? "ActionItems" : "ContentArea");
            wrapper.SetAttribute("Operation", ContentOperation(origin.ChangeKind));

            // Not required by the reader, but SymbolReference does determine it: the container
            // the member was declared in is what BC writes here on all 11 documents.
            wrapper.SetAttribute("SemanticKind", origin.IsAction ? "Action" : "Content");

            // AnchorName only for the kinds whose Anchor names a SIBLING (addbefore/addafter).
            // For addfirst BC writes none, and writing one would state a relationship the AL
            // does not declare. BC's AnchorId — a hash beside it — is left off either way.
            if (!string.IsNullOrEmpty(origin.Anchor) && origin.ChangeKind is 3 or 4)
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
    /// Whether <paramref name="name"/> is one of the container names BC's delta reader accepts
    /// for <c>ParentContainer</c>, which it <c>Enum.Parse</c>s.
    ///
    /// <para>The set is MEASURED, not declared: every <c>ParentContainer</c> value across all
    /// 111 delta elements in the four cached builds' bundles. An AL <c>Anchor</c> outside it —
    /// "Processing", or a sibling member's name — is not a container BC would accept, and
    /// passing one through makes the whole document fail to parse rather than one attribute
    /// differ.</para>
    ///
    /// <para>Deliberately a membership test rather than the full enum read out of
    /// <c>Microsoft.Dynamics.Nav.CodeAnalysis</c>: this file must not take a load-time dependency
    /// on an assembly the runner does not otherwise need, and a name outside this set is handled
    /// the same way whether it is genuinely absent from BC's enum or merely absent from here.</para>
    /// </summary>
    private static bool IsBcContainerName(string name) => name is
        "Prompting" or "ContentArea" or "ViewActions" or "ActionItems"
        or "Promoted" or "RelatedInformation";

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
