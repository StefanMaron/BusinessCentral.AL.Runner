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
//     XmlPort — TryBuildDependencyXmlPortMetadata reconstructs the whole document from the
//               .app itself (SymbolReference.json plus the embedded AL source, parsed by BC's
//               own AL parser), and the accessor forwards to it. It rendered only (ID, Name)
//               until #4467, because when this file was written nothing in the runner derived
//               xmlport structure; #3797 changed that and this projection was not moved with
//               it. See below.
//
// THE CIRCULARITY BOTH ACCESSORS AVOID
//   AlObjectMetadataRegistry (queries) and AlXmlPortMetadataRegistry (xmlports) hold BC's OWN
//   emitter output, captured at compile. Feeding either to the harness would compare BC
//   against BC: zero differences, nothing measured. Both accessors below refuse that route
//   explicitly rather than by luck — see QueryMetadataEquivalenceDesign's guard.
//
// See docs/metadata-equivalence.md#queries and #xmlports.

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
    /// <c>Types.Metadata.MetaXmlPort(XmlDocument, …)</c> parses — or null when no registered
    /// dependency declares that xmlport, or its node tree cannot be recovered.
    ///
    /// <para>A thin forwarder to <see cref="TryBuildDependencyXmlPortMetadata"/> for the reason
    /// <see cref="TryBuildReportMetadataEquivalenceXml"/> is one: the harness must measure the
    /// document runner consumers actually read — the one
    /// <c>RunnerXmlMetadataLoader.GetMetaObjectXmlMetadata</c> hands BC — not a test-only
    /// rendering that can agree or differ for reasons no AL caller ever sees (#4467).</para>
    ///
    /// <para><b>This is a derivation, not a capture, and that distinction is the whole point
    /// of the projection.</b> <see cref="TryBuildDependencyXmlPortMetadata"/> reconstructs the
    /// document from two sources shipped inside the .app — object properties from
    /// <c>SymbolReference.json</c>, and the node tree parsed out of the <c>.app</c>'s own
    /// embedded AL source with BC's AL parser. It never reads
    /// <c>AlXmlPortMetadataRegistry</c>, which holds BC's emit-captured schema: feeding the
    /// harness that would compare BC against BC and report agreement having measured nothing.
    /// A value the derivation cannot recover stays off the document rather than being filled
    /// in from the captured answer, so a reported difference remains a true statement about
    /// what the runner does not know.</para>
    ///
    /// <para>Until #4467 this wrote only <c>ID</c> and <c>Name</c>, because at the time nothing
    /// in the runner derived xmlport structure. #3797 landed the derivation on the AL-observable
    /// path and this projection was not moved with it, so the harness kept reporting 322
    /// differences over System Application's four xmlports — 182 of them
    /// <c>Nodes.&lt;presence&gt;</c> — against a runner that had the answers.</para>
    /// </summary>
    internal static string? TryBuildXmlPortMetadataEquivalenceXml(int xmlPortId)
        => TryBuildDependencyXmlPortMetadata(xmlPortId);
}
