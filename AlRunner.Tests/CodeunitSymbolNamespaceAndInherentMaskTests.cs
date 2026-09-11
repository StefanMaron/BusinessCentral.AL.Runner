// CodeunitSymbolNamespaceAndInherentMaskTests — a codeunit in a precompiled dependency states
// the AL namespace it lives under and the two inherent masks it declares.
//
// THE DEFECT THIS PINS (#3788, found by the CodeUnit metadata-equivalence comparison, #3782)
//   Three of the five members BC's emitter states and the runner did not are stated verbatim by
//   SymbolReference.json, and the runner walked past all three:
//
//     ALNamespace            the symbol file carries objects in a NESTED "Namespaces" tree, not
//                            the flat top-level "Codeunits" array. VisitSymbolContainer recursed
//                            through that tree and threw away the path it walked. Measured on
//                            System Application 28.1.49838.53910: the joined tree path equals
//                            BC's ALNamespace attribute for 533 of 533 codeunits, and the flat
//                            top-level "Codeunits" array is EMPTY (length 0) — every codeunit is
//                            reached through the tree.
//
//     InherentEntitlements   stated as an AL permission-mask letter string. BC's emitter writes
//     InherentPermissions    the NUMERIC mask. Same 533 codeunits: presence agrees 481/481 with
//                            zero one-sided occurrences in either direction.
//
// WHY THE MASK IS CASE-SENSITIVE, WHICH IS THE TRAP
//   RecordPatches.CodeunitMetadataFromBcDocument.cs already spells a mask in the other
//   direction: bit 0 Read, 1 Insert, 2 Modify, 3 Delete, 4 Execute, UPPERCASE for the direct
//   bit and lowercase for the indirect bit at n+5. So "X" is bit 4 = 16 (Execute) and "x" is
//   bit 9 = 512 (IndirectExecute) — two different values that differ only in case.
//
//   Both spellings occur in Microsoft's own package. On System Application 28.1.49838.53910,
//   480 codeunits state InherentEntitlements="X" and BC answers 16; codeunit 2516 "AppSource
//   Json Utilities" states "x" and BC answers 512. A case-INSENSITIVE parse — which is what
//   SymbolProperties does for the property NAME, and the obvious thing to reach for — answers
//   16 for that codeunit, a wrong value that no count of agreeing codeunits would reveal.
//
// WHY A RUNNER-SIDE MECHANISM TEST
//   The BC-behaviour claim is not what is at stake: BC's own emitter output IS the ground truth
//   here, and the metadata-equivalence harness compares against it on every unit-test leg. What
//   no AL test can reach is the PRECOMPILED-dependency route — an AL bundle's own codeunits are
//   source-parsed, so the symbol-file spelling never comes up. This file drives that route
//   directly, the same argument CodeunitSymbolSingleInstanceSpellingTests makes for #3790.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reaches the RecordPatches AL parse statics, which are process-wide and which xunit's
// parallel collections can clear or repopulate between a write and a read (#1696, #1712).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class CodeunitSymbolNamespaceAndInherentMaskTests : IDisposable
{
    private const int NestedTwoDeep = 61051;
    private const int NestedOneDeep = 61052;
    private const int NoNamespace = 61053;
    private const int DirectExecute = 61054;
    private const int IndirectExecute = 61055;
    private const int DeclaresNoMask = 61056;
    private const int ReadModifyMask = 61057;

    private readonly string _root;

    public CodeunitSymbolNamespaceAndInherentMaskTests()
    {
        _root = TestScratch.Dir("al-runner-codeunit-namespace-inherent");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The shapes a real symbol file presents. The nesting is what Microsoft's packages use:
    /// System Application 28.1's top-level <c>Codeunits</c> array is empty and all 533 of its
    /// codeunits sit inside the <c>Namespaces</c> tree, most of them two levels deep.
    /// <c>NoNamespace</c> sits in the top-level array so "reached through no namespace" stays
    /// distinguishable from "reached through one".
    /// </summary>
    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "0c3a8be2-51d7-4f16-93f2-0d7ac4b1e5a8",
          "Name": "Namespace And Inherent Mask Fixture",
          "Codeunits": [
            { "Id": {{NoNamespace}}, "Name": "No Namespace", "Properties": [] }
          ],
          "Namespaces": [
            {
              "Name": "System",
              "Codeunits": [
                { "Id": {{NestedOneDeep}}, "Name": "One Deep", "Properties": [] }
              ],
              "Namespaces": [
                {
                  "Name": "Utilities",
                  "Codeunits": [
                    { "Id": {{NestedTwoDeep}}, "Name": "Two Deep", "Properties": [] },
                    {
                      "Id": {{DirectExecute}},
                      "Name": "Direct Execute",
                      "Properties": [
                        { "Name": "InherentEntitlements", "Value": "X" },
                        { "Name": "InherentPermissions", "Value": "X" }
                      ]
                    },
                    {
                      "Id": {{IndirectExecute}},
                      "Name": "Indirect Execute",
                      "Properties": [
                        { "Name": "InherentEntitlements", "Value": "x" }
                      ]
                    },
                    {
                      "Id": {{DeclaresNoMask}},
                      "Name": "Declares No Mask",
                      "Properties": []
                    },
                    {
                      "Id": {{ReadModifyMask}},
                      "Name": "Read Modify",
                      "Properties": [
                        { "Name": "InherentPermissions", "Value": "RM" }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private void Register()
    {
        var appPath = Path.Combine(_root, "namespace-inherent.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(SymbolReference);
        }
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
    }

    /// <summary>
    /// Reads an attribute back off the projection the metadata-equivalence harness compares —
    /// the same <c>EnumerateKnownCodeunitMetadata</c> row CodeUnit Metadata (2000000137) answers
    /// from, so this asserts the column's own source rather than a second derivation written for
    /// the test.
    /// </summary>
    private static System.Xml.XmlElement Projection(int codeunitId)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(codeunitId);
        Assert.True(xml is not null, $"the runner derived no metadata for codeunit {codeunitId}");
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!;
    }

    /// <summary>
    /// The tree path becomes the ALNamespace, joined with dots, at whatever depth the codeunit
    /// sits — and a codeunit reached outside the tree states no attribute at all, because BC's
    /// emitter omits it rather than writing an empty string.
    /// </summary>
    [Fact]
    public void The_namespace_tree_path_a_codeunit_is_reached_through_becomes_its_ALNamespace()
    {
        Register();

        Assert.Equal("System.Utilities", Projection(NestedTwoDeep).GetAttribute("ALNamespace"));
        // One level up, so the joined path is shorter — a constant would answer the same for both.
        Assert.Equal("System", Projection(NestedOneDeep).GetAttribute("ALNamespace"));

        // Reached through the flat top-level array: BC omits the attribute for a codeunit in no
        // namespace, so stating "" would be a value where BC states absence.
        Assert.False(Projection(NoNamespace).HasAttribute("ALNamespace"),
            "a codeunit reached outside the Namespaces tree states no ALNamespace; writing an " +
            "empty string would turn 'declares none' into a fabricated disagreement.");
    }

    /// <summary>
    /// Both masks are carried, and the letter is decoded CASE-SENSITIVELY: uppercase is the
    /// direct bit, lowercase the indirect bit at n+5. "X" is 16 and "x" is 512 — the one
    /// distinction a case-insensitive parse loses, and codeunit 2516 in Microsoft's own System
    /// Application is the codeunit that has it.
    /// </summary>
    [Fact]
    public void Both_inherent_masks_are_carried_and_the_letter_case_selects_direct_or_indirect()
    {
        Register();

        var direct = Projection(DirectExecute);
        Assert.Equal("16", direct.GetAttribute("InherentEntitlements"));
        Assert.Equal("16", direct.GetAttribute("InherentPermissions"));

        // Lowercase: bit 4+5 = bit 9 = 512, NOT 16. This is the assertion a case-insensitive
        // parse fails while every other codeunit in the fixture still agrees.
        var indirect = Projection(IndirectExecute);
        Assert.Equal("512", indirect.GetAttribute("InherentEntitlements"));
        // Declared on one mask only, so the two are read independently rather than from one value.
        Assert.False(indirect.HasAttribute("InherentPermissions"),
            "InherentPermissions is unstated for this codeunit; carrying the entitlement mask " +
            "across would state a value the symbol file does not.");

        // Multi-bit: Read (bit 0 = 1) | Modify (bit 2 = 4) = 5, so the walk is a real bit walk
        // rather than a lookup that only knows Execute.
        Assert.Equal("5", Projection(ReadModifyMask).GetAttribute("InherentPermissions"));
        Assert.False(Projection(ReadModifyMask).HasAttribute("InherentEntitlements"));
    }

    /// <summary>
    /// A codeunit declaring neither mask states neither attribute. BC's emitter omits both — 52
    /// of System Application 28.1's 533 codeunits state neither — so writing a 0 would state
    /// PermissionMask.None where BC states absence, and BC's own constructor already defaults an
    /// absent attribute to None.
    /// </summary>
    [Fact]
    public void A_codeunit_declaring_no_mask_omits_both_attributes_rather_than_writing_zero()
    {
        Register();

        var none = Projection(DeclaresNoMask);
        Assert.False(none.HasAttribute("InherentEntitlements"));
        Assert.False(none.HasAttribute("InherentPermissions"));

        // The omission is a property of THAT codeunit, not of the projection: a sibling in the
        // same fixture states both.
        Assert.Equal("16", Projection(DirectExecute).GetAttribute("InherentPermissions"));
    }
}
