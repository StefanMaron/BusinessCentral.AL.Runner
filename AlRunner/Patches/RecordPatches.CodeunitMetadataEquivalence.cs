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
//   properties BcAppSymbolCache.ObjectSymbol carries, the same values CodeUnit Metadata
//   (2000000137) answers from — into BC's document shape. What BC's constructor then reads out
//   of it is the runner's answer, arrived at without BC's emitter.
//
// WHAT IS AND IS NOT STATED HERE
//   Eight attributes, because eight are what the runner derives: ID, Name, TableNo,
//   SingleInstance, Subtype, and — since #3788 — ALNamespace, InherentEntitlements and
//   InherentPermissions. The last three come from SymbolReference.json the same way the first
//   five do: the namespace from the Namespaces TREE PATH the object was reached through (533 of
//   533 exact against BC's attribute on System Application 28.1.49838.53910), the two masks from
//   the object's own Properties bag.
//
//   Plus the <Methods> subtree, CONDITIONALLY — see AppendMethodsSubtree below. It is written
//   only for a codeunit whose loaded assembly proves the symbol file's view of it is complete,
//   and omitted for every other, including every codeunit of an app that was never scanned.
//
//   What is still ABSENT is absent honestly, because the symbol file does not state it:
//   TestIsolation, EventSubscriberInstance and MetadataVersion. The harness reports each as a
//   difference and the allowlist declares why, which is the point of the exercise. Writing BC's
//   own value into any of them would manufacture agreement.
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
    /// <para><b>Only the derived attributes are written</b> — eight since #3788. A value the
    /// runner does not derive is left off the element rather than defaulted, so BC's constructor
    /// applies its own default and the difference the harness then reports is a true statement
    /// about what the runner does not know. See this file's header.</para>
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

        // Each of the three is OMITTED when the codeunit states none, the same rule TableNo
        // follows above: BC's emitter omits the attribute rather than writing a default, and its
        // own constructor then applies that default. Writing "" or "0" would state a value where
        // BC states absence.
        if (!string.IsNullOrEmpty(row.ALNamespace))
            root.SetAttribute("ALNamespace", row.ALNamespace);
        if (TryDecodePermissionMaskLetters(row.InherentEntitlements, out var entitlements))
            root.SetAttribute("InherentEntitlements", entitlements.ToString(CultureInfo.InvariantCulture));
        if (TryDecodePermissionMaskLetters(row.InherentPermissions, out var permissions))
            root.SetAttribute("InherentPermissions", permissions.ToString(CultureInfo.InvariantCulture));

        AppendMethodsSubtree(doc, root, row);

        return doc.OuterXml;
    }

    /// <summary>
    /// The <c>&lt;Methods&gt;</c> subtree — BC's emitted method table — written only when the
    /// runner can prove its view of it is COMPLETE, and omitted entirely otherwise.
    ///
    /// <para><b>The gate is the assembly witness, never the list's own length</b> (#3788).
    /// SymbolReference.json states every event PUBLISHER exactly — by id, by name and in BC's
    /// document order — and no event SUBSCRIBER at all, because the file is an app's
    /// consumer-facing API surface and an AL subscriber is always <c>local</c>. So the symbol
    /// file cannot tell a complete list from a short one, and a short one is worse than none:
    /// <c>MetadataObjectDiff</c> pairs <c>Methods</c> POSITIONALLY, so the first missing element
    /// puts every later one in a different method's slot — the runner asserting an association
    /// it has no evidence for (loud-failures.md).</para>
    ///
    /// <para>Measured with this gate over three BC builds — 27.5.46862.53931, 28.1.49838.53910
    /// and 28.4.53241.54407 — 66 codeunits render on each, all exact, zero wrong slots; without
    /// it, 28.1 renders 76 codeunits of which 8 carry a wrong slot. See
    /// docs/codeunit-metadata-from-bc.md#the-method-table.</para>
    /// </summary>
    private static void AppendMethodsSubtree(XmlDocument doc, XmlElement root, CodeunitMetaRow row)
    {
        // Three conditions, all required, and the middle one is the third state: an app whose
        // assemblies were never scanned proves nothing, which is not the same as proving there
        // are no subscribers (guards-need-a-third-state.md).
        if (!row.MethodsProvenComplete) return;
        if (row.AttributedMethods is not { Count: > 0 }) return;

        var methods = doc.CreateElement("Methods", root.NamespaceURI);
        root.AppendChild(methods);
        foreach (var method in row.AttributedMethods)
        {
            var element = doc.CreateElement("Method", root.NamespaceURI);
            element.SetAttribute("ID", method.Id.ToString(CultureInfo.InvariantCulture));
            element.SetAttribute("Name", method.Name);
            // BC writes the attribute kind as a child element of <MethodAttributes>, not as an
            // attribute of <Method> — the shape MetaMethod's own reader expects.
            //
            // Name is REQUIRED, and its absence is not a difference but a crash: BC's own
            // MetaCodeunit(XmlNode) throws NullReferenceException on an attribute element that
            // has none. Measured against the live constructor — a bare <EventPublisherAttribute>
            // threw, Name alone was enough, and BC's own fuller form (IncludeSender,
            // GlobalVarAccess) also parsed. The value is the AL attribute identifier the symbol
            // file states, so it is carried rather than reconstructed.
            var attributes = doc.CreateElement("MethodAttributes", root.NamespaceURI);
            var kind = doc.CreateElement(method.Kind, root.NamespaceURI);
            kind.SetAttribute("Name", method.AttributeName);
            if (method.Kind == "EventPublisherAttribute")
            {
                // Both come from the attribute's POSITIONAL arguments in the symbol file and
                // reproduce BC's own values for 149 of 149 publishers — see
                // BcAppSymbolCache.ReadPublisherFlags, which owns the mapping and its
                // measurement. Written unconditionally, in BC's own "True"/"False" spelling,
                // because BC's emitter writes them on every publisher element rather than
                // omitting the false case.
                // IncludeSender unconditionally, Isolated ONLY when true. That asymmetry is
                // BC's, measured on System Application 28.1: its emitter writes IncludeSender on
                // all 149 publisher elements and Isolated on only 9 (8 True, 1 False). Writing
                // Isolated="False" on the other 140 would state a value where BC states absence
                // — the manufactured-agreement failure this projection exists to avoid — and the
                // deserialized object reads the same either way, because BC's own reader
                // defaults an absent Isolated to false.
                kind.SetAttribute("IncludeSender", method.IncludeSender ? "True" : "False");
                if (method.Isolated) kind.SetAttribute("Isolated", "True");
            }
            attributes.AppendChild(kind);
            element.AppendChild(attributes);
            methods.AppendChild(element);
        }
    }

    /// <summary>
    /// The AL mask letter string a symbol file states (<c>"X"</c>, <c>"x"</c>, <c>"RM"</c>, …)
    /// as the numeric <c>PermissionMask</c> BC's emitter writes — the exact inverse of
    /// <c>ReadPermissionMaskString</c>, and it shares that method's
    /// <c>PermissionMaskLetters</c> constant so the two cannot drift apart.
    ///
    /// <para><b>Case is significant and must not be normalised.</b> Uppercase is the direct bit,
    /// lowercase the indirect bit at n+5 — so <c>"X"</c> is 16 (Execute) and <c>"x"</c> is 512
    /// (IndirectExecute). Both occur in Microsoft's own packages: System Application
    /// 28.1.49838.53910 states <c>"X"</c> on 480 codeunits and <c>"x"</c> on codeunit 2516
    /// "AppSource Json Utilities", and BC's emitter answers 16 and 512 for exactly those.
    /// See docs/codeunit-metadata-from-bc.md#permission-mask-spelling.</para>
    ///
    /// <para>Returns false — the attribute is then omitted — for a null, empty or whitespace
    /// value, and for a mask that decodes to 0, because BC's emitter omits the attribute for
    /// <c>PermissionMask.None</c> rather than writing it.</para>
    /// </summary>
    /// <remarks>
    /// A letter the table does not name is a symbol file stating something this conversion
    /// cannot read, so it raises the same shape gap the other direction raises rather than
    /// silently contributing no bit — a mask short one letter is indistinguishable from a
    /// narrower permission the codeunit really declared (loud-failures.md).
    /// </remarks>
    private static bool TryDecodePermissionMaskLetters(string? letters, out int mask)
    {
        if (TryDecodePermissionMaskLettersCore(letters, out mask, out var unreadable)) return mask != 0;

        throw CodeunitMetadataShapeGap(
            $"SymbolReference.json states an inherent mask '{letters}', whose character '{unreadable}' is "
            + $"not one of the permission letters '{PermissionMaskLetters}' in either case — the "
            + "runner cannot read a mask it cannot spell, and dropping the character would answer "
            + "a narrower permission than the codeunit declares");
    }

    /// <summary>
    /// The mask arithmetic alone, with the unreadable-letter POLICY left to the caller: true
    /// when every character decoded, false with <paramref name="unreadableLetter"/> naming the
    /// first one that did not. Case is significant — see
    /// <see cref="TryDecodePermissionMaskLetters"/>, which owns the full claim and its
    /// citation.
    ///
    /// <para>Two callers, two policies, and the split is deliberate (#3933). The codeunit and
    /// query directions THROW, because a <c>RunnerOutOfScopeException</c> there reaches the AL
    /// author as the test's failure message. <c>EmitInherentMask</c> omits and says, because a
    /// throw out of <see cref="TryBuildDependencyPageMetadata"/> is swallowed into a null
    /// metadata document and silently demotes the whole TestPage — measured on that builder,
    /// see the "ORDER IS LOAD-BEARING" note in DependencyPageMetadataXml.cs. Sharing the
    /// arithmetic and not the policy is what keeps a loud failure loud on both paths.</para>
    /// </summary>
    private static bool TryDecodePermissionMaskLettersCore(
        string? letters, out int mask, out char unreadableLetter)
    {
        mask = 0;
        unreadableLetter = '\0';
        if (string.IsNullOrWhiteSpace(letters)) return true;

        foreach (var c in letters.Trim())
        {
            var direct = PermissionMaskLetters.IndexOf(c);
            if (direct >= 0) { mask |= 1 << direct; continue; }

            var indirect = PermissionMaskLetters.IndexOf(char.ToUpperInvariant(c));
            if (indirect >= 0 && char.IsLower(c)) { mask |= 1 << (indirect + PermissionMaskLetters.Length); continue; }

            unreadableLetter = c;
            mask = 0;
            return false;
        }

        return true;
    }
}
