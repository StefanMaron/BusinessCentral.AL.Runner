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
    /// The runner's extension-runtime-delta object for one extended object id, or null.
    ///
    /// <para><b>It is null for every id today, and that null is the measurement.</b>
    /// <see cref="RunnerXmlMetadataLoader.GetExtensionDeltasForAppObject"/> is the one runner
    /// member typed to return BC's <c>NavAppObjectMetadataRuntimeDeltas</c>, and it returns
    /// <c>null!</c> by construction: the runner has no published-app extension pipeline, and
    /// that comment records null as BC's own "no deltas" value. So this accessor exists to
    /// report the absence per object rather than to hide it — the harness turns each null into
    /// a named unbuildable entry naming #3809.</para>
    ///
    /// <para><b>Why it goes through the loader rather than short-circuiting to null.</b> If the
    /// runner ever starts tracking extension deltas, this begins returning objects and the
    /// comparison starts measuring them with no edit here. A hardcoded null would have to be
    /// noticed and removed, which is the kind of thing nobody notices.</para>
    /// </summary>
    internal static object? TryGetRuntimeDeltasMetadataEquivalence(int extendedObjectId)
    {
        var loader = new RunnerXmlMetadataLoader();
        var method = typeof(RunnerXmlMetadataLoader).GetMethod("GetExtensionDeltasForAppObject")
            ?? throw new InvalidOperationException(
                "RunnerXmlMetadataLoader.GetExtensionDeltasForAppObject is gone. It is the only " +
                "runner member typed as NavAppObjectMetadataRuntimeDeltas, so without it the " +
                "runner side of the MetadataRuntimeDeltas comparison cannot be located at all.");

        // ApplicationObjectId's ctor takes (ObjectType, int). The ordinal is read off BC's own
        // enum by NAME rather than hardcoded: a numeric literal here would silently point at a
        // different object type if BC ever renumbers, and the comparison would then report a
        // null that means "wrong id" rather than "no deltas".
        var objectIdType = method.GetParameters()[0].ParameterType;
        var objectTypeEnum = objectIdType.GetConstructors()
            .Select(c => c.GetParameters())
            .FirstOrDefault(ps => ps.Length == 2 && ps[0].ParameterType.IsEnum)?[0].ParameterType
            ?? throw new InvalidOperationException(
                "ApplicationObjectId has no (enum, int) constructor — BC's shape changed.");
        var objectId = Activator.CreateInstance(
            objectIdType,
            new object?[] { Enum.Parse(objectTypeEnum, "Page"), extendedObjectId });

        return method.Invoke(loader, new object?[] { objectId, null });
    }

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
    /// <c>ALNamespace</c> and the enum-level <c>CaptionML</c> are absent for exactly that
    /// reason: <see cref="BcAppSymbolCache.EnumSymbol"/> carries neither (#3807).</para>
    ///
    /// <para><b>Four members ARE derived (#3807)</b>, all four stated verbatim in
    /// <c>SymbolReference.json</c> and all four expressible in the shape BC's own reader
    /// parses: <c>Extensible</c>, <c>DefaultImplementation</c> and <c>UnknownImplementation</c>
    /// as attributes on this root, and a value's <c>Implementation</c> as an attribute on
    /// <c>&lt;Value&gt;</c>. That last one was the issue's open question and this is its answer
    /// — <c>MetaEnumValue(XmlNode)</c> reads an <c>Implementation</c> attribute into
    /// <c>InterfaceImplementation</c>. An enum declaring none of them still states none, so the
    /// harness's "left off, never defaulted" contract above is unchanged.</para>
    /// </summary>
    internal static string? TryBuildEnumMetadataEquivalenceXml(int enumId)
    {
        if (!AlRunner.AlEnumMetadataRegistry.TryGet(enumId, out var entry)) return null;

        var doc = new XmlDocument();
        var root = doc.CreateElement("Enum");
        doc.AppendChild(root);
        root.SetAttribute("ID", entry.Id.ToString(CultureInfo.InvariantCulture));
        root.SetAttribute("Name", entry.Name);

        // "1"/"0", never "true"/"false": BC reads this through MetaBase.Int32Value, which is
        // Int32.Parse with only "" and "undefined" mapped to 0, so a boolean spelling makes
        // MetaEnum's constructor THROW rather than default.
        //
        // CONDITIONAL, matching BC's emitter rather than BC's default: of 144 emitted enum
        // documents in 28.1 and 28.4, 31 state "1", 99 state "0" and 12 base enums state the
        // attribute not at all — the same 12 whose SymbolReference.json omits the property.
        // Writing "0" for those would state something BC leaves off, which is the manufactured
        // agreement this file's header forbids, in the direction that is hardest to notice.
        if (entry.Extensible is { } extensible)
            root.SetAttribute("Extensible", extensible ? "1" : "0");

        // Both enum-level implementation fallbacks (#2306), comma-joined: BC splits on
        // MetaBase.SplitChar and Int32.Parse's each part. Null means "declares none" and stays
        // OFF the document, because an empty attribute would still be an assertion.
        if (entry.DefaultImplementations is { Length: > 0 } defaults)
            root.SetAttribute("DefaultImplementation", string.Join(",", defaults));
        if (entry.UnknownImplementations is { Length: > 0 } unknowns)
            root.SetAttribute("UnknownImplementation", string.Join(",", unknowns));

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

            // The value's own `Implementation` (#2306, rendered at #3807) — already parsed and
            // previously dropped. This IS the shape BC's emitter writes: enum 1465's emitted
            // document states <Value Name="Aes" Ordinal="0" Implementation="1467">, and
            // MetaEnumValue(XmlNode) reads that attribute into InterfaceImplementation.
            // An empty list means "declares none" and stays off, because writing an empty
            // attribute sets InterfaceImplementation to Empty explicitly rather than leaving
            // BC's own default — the same statement here, but not on a value BC did state.
            if (i < entry.Implementations.Length && entry.Implementations[i] is { Length: > 0 } valueImplementations)
                value.SetAttribute("Implementation", string.Join(",", valueImplementations));

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
