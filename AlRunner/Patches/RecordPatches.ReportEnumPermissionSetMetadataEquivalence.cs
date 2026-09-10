// RecordPatches.ReportEnumPermissionSetMetadataEquivalence — the runner-side accessors the
// metadata-equivalence harness needs for Report, PermissionSet and Enum (#3782, steps 5-7).
//
// WHY THREE ACCESSORS RATHER THAN ONE
//   The harness compares one BC type against itself: BC's emitter document on one side, the
//   runner's derivation on the other, both read by the same BC reader. What each kind can
//   offer as "the runner's derivation" differs, and the difference is the finding:
//
//     Report        — an XML render already exists and is the document every runner consumer
//                     of report metadata reads (DependencyReportMetadata.cs). Handed over
//                     as-is; the harness reads it back with BC's own MetaReport constructor.
//     PermissionSet — BuildMetaPermissionSet already produces a real
//                     Types.Metadata.MetaPermissionSet from the SymbolReference-derived
//                     PermissionSetSymbol. Handed over AS THE OBJECT, no rendering.
//     Enum          — Types.Metadata.MetaEnum's properties are ALL get-only, so there is no
//                     object route: the runner's registry entry is rendered as the <Enum>
//                     document BC's own MetaEnum(XmlNode) parses.
//
// THE CIRCULARITY EACH ACCESSOR AVOIDS
//   For a PRECOMPILED dependency none of the three has a BC-captured document to fall back
//   on: AlReportMetadataRegistry and AlObjectMetadataRegistry are filled by the EMIT
//   pipeline, so they hold what the runner compiled and nothing a dependency .app shipped.
//   Every value below therefore comes from SymbolReference.json (plus, for a report's column
//   source expressions, the .app's own embedded AL source). See
//   docs/metadata-equivalence.md#reports, #permission-sets and #enums.
//
// THE MEMO THAT MADE THE FIRST MEASUREMENT WRONG — read before changing PermissionSet
//   PermissionSetIdByName() is a memoized static invalidated only inside
//   EnsurePermissionMetadataPopulated. Calling BuildMetaPermissionSet without driving that
//   entry point first measures an EMPTY name index, so every IncludedPermissionSets edge is
//   dropped and the comparison reports 94 differences the runner does not actually have.
//   That is why RunnerPermissionSetDeclarations() drives the population rather than reading
//   the inventory directly (#3782, step 6).

using System.Globalization;
using System.Xml;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The runner's derivation for one report, as the <c>&lt;Report&gt;</c> document BC's
    /// <c>Types.Metadata.MetaReport(XmlElement, …)</c> parses — or null when no registered
    /// dependency declares that report.
    ///
    /// <para>A thin forwarder to <see cref="TryBuildDependencyReportMetadata"/> ON PURPOSE:
    /// the harness must measure the document runner consumers actually read, not a
    /// test-only rendering, and naming it here keeps the harness from reaching for
    /// AlReportMetadataRegistry — which holds BC's own emit-captured output and would compare
    /// BC against BC.</para>
    /// </summary>
    internal static string? TryBuildReportMetadataEquivalenceXml(int reportId)
        => TryBuildDependencyReportMetadata(reportId);

    /// <summary>
    /// Every permission set the runner has a real declaration for, keyed by object id, after
    /// the runner's own population has run.
    ///
    /// <para><b>The population call is not optional.</b> See this file's header: without it
    /// the name-to-id index behind <c>IncludedPermissionSets</c> is empty and the comparison
    /// measures a memo no runner consumer ever sees.</para>
    /// </summary>
    internal static IReadOnlyDictionary<int, BcAppSymbolCache.PermissionSetSymbol>
        RunnerPermissionSetDeclarations()
    {
        EnsurePermissionMetadataPopulated();
        var byId = new Dictionary<int, BcAppSymbolCache.PermissionSetSymbol>();
        foreach (var (permissionSet, _, _) in EnumerateKnownPermissionSets())
            byId.TryAdd(permissionSet.Id, permissionSet);
        return byId;
    }

    /// <summary>
    /// The runner's own <c>Types.Metadata.MetaPermissionSet</c> for one declaration — the same
    /// object BC's <c>AssignFromMetaPermissionSet</c> consumes at runtime, so this measures
    /// what BC's permission composer actually receives.
    /// </summary>
    internal static object BuildPermissionSetMetadataEquivalenceObject(
        BcAppSymbolCache.PermissionSetSymbol declaration)
        => BuildMetaPermissionSet(declaration);

    /// <summary>
    /// The runner's derivation for one enum, as the <c>&lt;Enum&gt;</c> document BC's
    /// <c>Types.Metadata.MetaEnum(XmlNode)</c> parses — or null when the runner's enum
    /// registry knows no enum with that id.
    ///
    /// <para><b>Why a render and not an object.</b> Every property on BC's <c>MetaEnum</c> is
    /// get-only and its only other constructors are the parameterless one and a 10-argument
    /// positional one over BC-internal types, so there is no route that sets values on an
    /// instance. The XmlNode constructor is the one BC itself uses to read an emitted enum,
    /// and reading both sides through it makes the comparison one type against itself.</para>
    ///
    /// <para><b>A value the runner does not derive is LEFT OFF, never defaulted.</b> BC's own
    /// constructor then applies BC's default and the reported difference is a true statement
    /// about what the runner does not know. Writing BC's value into any element would
    /// manufacture agreement — which is the whole failure mode this harness exists to catch.
    /// Measured on BC 28.1.49838.53910: <c>Extensible</c> is absent here because
    /// <see cref="BcAppSymbolCache.EnumSymbol"/> does not carry it at all (#3807), not because
    /// it is unknowable.</para>
    /// </summary>
    internal static string? TryBuildEnumMetadataEquivalenceXml(int enumId)
    {
        if (!AlRunner.AlEnumMetadataRegistry.TryGet(enumId, out var entry)) return null;

        var doc = new XmlDocument();
        var root = doc.CreateElement("Enum");
        doc.AppendChild(root);
        root.SetAttribute("ID", entry.Id.ToString(CultureInfo.InvariantCulture));
        root.SetAttribute("Name", entry.Name);

        var values = doc.CreateElement("Values");
        root.AppendChild(values);
        for (int i = 0; i < entry.Options.Length; i++)
        {
            var value = doc.CreateElement("Value");
            value.SetAttribute("Name", entry.Options[i]);
            // Indexes[i] is the AL-declared ordinal and is NOT the position — BC's emitter
            // writes an enum's values in NAME order, so the two coincide only for an enum whose
            // names happen to sort by ordinal. That is why the harness pairs enum values by
            // Ordinal rather than by position (MetadataEquivalenceHarness.EnumDiffOptions).
            value.SetAttribute(
                "Ordinal",
                (i < entry.Indexes.Length ? entry.Indexes[i] : i).ToString(CultureInfo.InvariantCulture));

            // Caption null means "this value declares none" (#1775), which is a different
            // statement from "declares an empty one" — so nothing is written for a null and
            // BC's own default (the member name) applies on both sides.
            var caption = entry.Captions is not null && i < entry.Captions.Length
                ? entry.Captions[i]
                : null;
            if (caption is not null)
            {
                var captionMl = doc.CreateElement("CaptionML");
                captionMl.InnerText = "ENU=" + caption;
                value.AppendChild(captionMl);
            }

            values.AppendChild(value);
        }
        return doc.OuterXml;
    }
}
