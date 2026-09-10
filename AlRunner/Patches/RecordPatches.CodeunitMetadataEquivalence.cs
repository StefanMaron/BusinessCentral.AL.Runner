// RecordPatches.CodeunitMetadataEquivalence — the runner's codeunit derivation, rendered as
// the <CodeUnit> document BC's own MetaCodeunit(XmlNode) reads (#3782, step 2 of 8).
//
// WHY A PROJECTION RATHER THAN AN OBJECT
//   The metadata-equivalence harness compares one BC type against itself: BC's emitter
//   document on one side, the runner's derivation on the other, both deserialized by the same
//   BC constructor. Tables and pages each have a runner-built object to hand over
//   (NCLMetaTable, and the page XML DependencyPageMetadataXml emits). Codeunits have NEITHER:
//   nothing in the runner builds a Types.Metadata.MetaCodeunit, and the codeunit document in
//   AlObjectMetadataRegistry is BC's OWN emitter output, captured at compile
//   (RecordPatches.CodeunitMetadataFromBcDocument.cs says so at its head). Handing that
//   registry document to the harness would compare BC against BC and report zero differences
//   having measured nothing — the exact failure #3782 exists to remove, and the one step 1 hit
//   with MetaPageDefinition.
//
//   So this file renders the runner's genuinely independent derivation — the SymbolReference
//   properties BcAppSymbolCache.ObjectSymbol carries, the same five values CodeUnit Metadata
//   (2000000137) answers from — into BC's document shape. What BC's constructor then reads out
//   of it is the runner's answer, arrived at without BC's emitter.
//
// WHAT IS AND IS NOT STATED HERE
//   Five attributes, because five are what the runner derives: ID, Name, TableNo,
//   SingleInstance, Subtype. Everything else BC's emitter writes — ALNamespace,
//   InherentPermissions, InherentEntitlements, TestIsolation, EventSubscriberInstance, the
//   whole <Methods> subtree — is deliberately ABSENT, and absent is the honest rendering: the
//   symbol file does not state them, so the runner has no independent answer to offer. The
//   harness reports each as a difference and the allowlist declares why, which is the point of
//   the exercise. Writing BC's own value into any of them would manufacture agreement.
//
// See docs/metadata-equivalence.md#codeunits and tests/expectations/metadata-equivalence/.

using System.Globalization;
using System.Xml;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The runner's derivation for one codeunit, as the <c>&lt;CodeUnit&gt;</c> document BC's
    /// <c>Types.Metadata.MetaCodeunit(XmlNode)</c> parses — or null when the runner knows no
    /// codeunit with that id, which the caller must report rather than absorb.
    ///
    /// <para><b>Only the five derived attributes are written.</b> A value the runner does not
    /// derive is left off the element rather than defaulted, so BC's constructor applies its
    /// own default and the difference the harness then reports is a true statement about what
    /// the runner does not know. See this file's header.</para>
    /// </summary>
    internal static string? TryBuildCodeunitMetadataEquivalenceXml(int codeunitId)
    {
        var row = EnumerateKnownCodeunitMetadata().FirstOrDefault(r => r.Id == codeunitId);
        if (row is null) return null;

        var doc = new XmlDocument();
        // The emitter's own namespace. BC's reader resolves attributes namespace-agnostically,
        // but the document is compared as a document elsewhere in this programme, so it is
        // rendered the way BC renders it rather than in no namespace at all.
        var root = doc.CreateElement("CodeUnit", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        doc.AppendChild(root);

        root.SetAttribute("ID", row.Id.ToString(CultureInfo.InvariantCulture));
        root.SetAttribute("Name", row.Name);
        // TableNo is omitted when the codeunit declares none: BC's emitter omits it too (12 of
        // System Application's 533 documents carry it), so writing a 0 would state a value
        // where BC states absence and turn "declares none" into a fabricated disagreement.
        if (row.TableNo != 0)
            root.SetAttribute("TableNo", row.TableNo.ToString(CultureInfo.InvariantCulture));
        root.SetAttribute("SingleInstance", row.SingleInstance ? "1" : "0");
        root.SetAttribute("Subtype", row.Subtype);

        return doc.OuterXml;
    }
}
