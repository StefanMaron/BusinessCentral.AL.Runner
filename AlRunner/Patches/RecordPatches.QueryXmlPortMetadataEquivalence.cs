// RecordPatches.QueryXmlPortMetadataEquivalence — the two runner-side accessors the
// metadata-equivalence harness needs for Query and XmlPort (#3782, steps 3 and 4 of 8).
//
// WHY A FILE RATHER THAN A CALL
//   The harness compares one BC type against itself: BC's emitter document on one side, the
//   runner's derivation on the other, both read by the same BC reader. What each kind can
//   offer as "the runner's derivation" differs, and getting it wrong produces a green
//   comparison that measured nothing — step 1 lost a cycle to exactly that. The two accessors
//   below therefore differ in kind, and the difference is the finding:
//
//     Query   — BuildMetaQueryDesign already produces a real Types.Metadata.MetaQuery from
//               SymbolReference.json. It is handed over AS THE OBJECT, no rendering involved.
//     XmlPort — the runner derives no xmlport structure at all, so the accessor renders the
//               four values it does derive and states nothing else. See below.
//
// THE CIRCULARITY BOTH ACCESSORS AVOID
//   AlObjectMetadataRegistry (queries) and AlXmlPortMetadataRegistry (xmlports) hold BC's OWN
//   emitter output, captured at compile. Feeding either to the harness would compare BC
//   against BC: zero differences, nothing measured. Both accessors below refuse that route
//   explicitly rather than by luck — see QueryMetadataEquivalenceDesign's guard.
//
// See docs/metadata-equivalence.md#queries and #xmlports.

using System.Globalization;
using System.Xml;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The runner's own <c>Types.Metadata.MetaQuery</c> design object for one query — the
    /// SymbolReference derivation, never BC's captured document.
    ///
    /// <para>Returns null when the runner cannot build one, which the caller must report
    /// rather than absorb: that null IS #3499's defect, reaching AL as a bare
    /// NullReferenceException from inside BC's own <c>ALSetFilter</c>.</para>
    ///
    /// <para><b>Refuses rather than falls back</b> when BC's own document is registered for
    /// this id. <see cref="BuildMetaQueryDesign"/> prefers that document (#3608), which is the
    /// right call at runtime and the wrong one here: it is the very document the harness holds
    /// as ground truth, so the comparison would be BC against BC and would report perfect
    /// agreement having measured nothing. Precompiled dependency queries — the only ones a
    /// ground-truth bundle carries — never have one, so this throws only if that stops being
    /// true, which is a finding and not a fallback.</para>
    /// </summary>
    internal static object? TryBuildQueryMetadataEquivalenceDesign(int queryId)
    {
        if (HasBcQueryMetadataDocument(queryId))
            throw new InvalidOperationException(
                $"query {queryId} has a BC-emitted metadata document registered, so " +
                "BuildMetaQueryDesign would return BC's own answer. Comparing that against the " +
                "ground truth compares BC with BC and reports zero differences having measured " +
                "nothing (#3782). A ground-truth bundle holds precompiled dependency queries, " +
                "which are never emitted here — so this is a finding, not a case to fall back on.");

        EnsureQueryBuilderReflection();
        return BuildMetaQueryDesign(queryId);
    }

    /// <summary>
    /// The runner's derivation for one xmlport, as the <c>&lt;XmlPort&gt;</c> document BC's
    /// <c>Types.Metadata.MetaXmlPort(XmlDocument, …)</c> parses — or null when the runner knows
    /// no xmlport with that id.
    ///
    /// <para><b>Two values, and that is the finding rather than an omission.</b> Measured
    /// against System Application's SymbolReference.json on BC 28.1.49838.53910: the runner
    /// retains <c>(Kind, Id, Name, Caption)</c> for every non-codeunit object
    /// (<see cref="BcAppSymbolCache.ObjectSymbol"/>), and nothing anywhere in the runner
    /// derives xmlport STRUCTURE. The symbol file states an xmlport's <c>Properties</c> and
    /// <c>Variables</c> but no node tree at all, while BC's emitted document for those same
    /// four xmlports carries 91 <c>&lt;Node&gt;</c> elements between them. Caption is not
    /// written because BC states it as <c>&lt;CaptionML&gt;</c> with a language id, a shape the
    /// symbol file's flat string does not carry.</para>
    ///
    /// <para>A value the runner does not derive is <b>left off</b> rather than defaulted, so
    /// BC's own constructor applies its own default and the reported difference is a true
    /// statement about what the runner does not know. Writing BC's value into any of them would
    /// manufacture agreement — which is the whole reason this renders the runner's own state
    /// instead of reading AlXmlPortMetadataRegistry, where BC's captured answer is sitting.</para>
    /// </summary>
    internal static string? TryBuildXmlPortMetadataEquivalenceXml(int xmlPortId)
    {
        var name = TryGetXmlPortSymbolName(xmlPortId);
        if (name is null) return null;

        var doc = new XmlDocument();
        var root = doc.CreateElement("XmlPort");
        doc.AppendChild(root);

        // BC's reader is a switch over the UPPERCASED child element name and throws
        // ArgumentException on anything it does not know, so only names it accepts appear here.
        void El(string elementName, string value)
        {
            var e = doc.CreateElement(elementName);
            e.InnerText = value;
            root.AppendChild(e);
        }

        El("ID", xmlPortId.ToString(CultureInfo.InvariantCulture));
        El("Name", name);
        return doc.OuterXml;
    }

    /// <summary>
    /// The name the runner knows for an xmlport declared by a registered dependency .app, or
    /// null. Reads the same <see cref="BcAppSymbolCache.ObjectSymbol"/> set AllObj (2000000038)
    /// reports from, which is the runner's whole xmlport knowledge for a precompiled app.
    /// </summary>
    private static string? TryGetXmlPortSymbolName(int xmlPortId)
    {
        foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols("objects (metadata equivalence, XmlPort)"))
            foreach (var o in symbols.Objects)
                if (o.Id == xmlPortId && NormalizeObjectTypeName(o.Kind) == "xmlport")
                    return o.Name;
        return null;
    }
}
