// CodeunitMetadataColumnsFromSymbolFileTests — the AL-OBSERVABLE half of #3788.
//
// WHY THIS FILE EXISTS, AND WHAT IT CAUGHT
//   The runner renders one codeunit's metadata TWICE, from one CodeunitMetaRow:
//
//     RecordPatches.CodeunitMetadataEquivalence.cs   the projection the metadata-equivalence
//                                                    harness compares against BC's emitter
//     RecordPatches.CodeunitMetadataVirtualTable.cs  CodeUnit Metadata (2000000137), which is
//                                                    what AL actually reads
//
//   They are INDEPENDENT CODE and they disagree on spelling: the projection writes BC's NUMERIC
//   mask ("16"), this table writes the AL LETTER string ("X"). So a test driving one proves
//   nothing about the other.
//
//   #3788 first landed with only the projection covered. A reviewer reverted all three `case`
//   arms in BuildCodeunitMetadataValue to Default() — the exact pre-fix behaviour — and 252
//   tests stayed GREEN, because not one of the three new test files referenced the virtual-table
//   rendering at all. The PR body had argued, correctly, that fixing only the projection "would
//   have closed the issue while leaving CodeUnit Metadata still answering BC's default to AL";
//   writing that warning did not produce a test for it. This file is that test.
//
//   Same shape as #3912, where mutating a compiler-side reader left six sibling tests green
//   because they exercised the render rather than the reader. The lesson both times: run the
//   mutation on EACH rendering, rather than reasoning that one covers both.
//
// WHY A RUNNER-SIDE MECHANISM TEST
//   What these columns answer on a real tier is already pinned upstream and green —
//   Record_CodeunitMetadata_Get_ALNamespace_ReportsTheDeclaringFilesNamespace and
//   Record_CodeunitMetadata_Get_InherentPermissionsAndEntitlements_ReadIndependently. Nothing
//   here restates them. What no AL test can reach is the PRECOMPILED-DEPENDENCY route: a corpus
//   codeunit is source-compiled, so its SymbolReference.json is never consulted and the branch
//   this file drives — no BC document registered, values coming from the symbol file — never
//   executes. That is the branch #3788 is about.

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Renders through the LIVE CodeUnit Metadata metatable, so it needs the BC engine in-process —
// the same reason MetadataEquivalenceCodeunitOracleTests sits here. Run it with
// tools/engine-test-bootstrap.sh; unbootstrapped, BcEngineUnbootstrappedGuard fires and the
// failures are the guard, not this file.
[Collection(BcEngineCollection.Name)]
public sealed class CodeunitMetadataColumnsFromSymbolFileTests : IDisposable
{
    private const int NestedNamespace = 61061;
    private const int DirectExecute = 61062;
    private const int IndirectExecute = 61063;
    private const int MixedCaseMask = 61064;
    private const int DeclaresNothing = 61065;

    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public CodeunitMetadataColumnsFromSymbolFileTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-codeunit-metadata-columns");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// A precompiled dependency. Four codeunits sit inside the <c>Namespaces</c> tree, which is
    /// what Microsoft's own packages do — System Application 28.1's top-level <c>Codeunits</c>
    /// array has length 0.
    ///
    /// <para><c>DeclaresNothing</c> sits in the FLAT top-level array on purpose. A namespace is a
    /// property of an object's POSITION in the tree, not of its <c>Properties</c> bag, so a
    /// codeunit nested under <c>System.Utilities</c> answers that namespace however empty its
    /// own properties are. Putting it in the tree and expecting an empty namespace is a fixture
    /// contradicting itself, and this one did until the test was run.</para>
    /// </summary>
    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "6d1f0a47-9b3e-4c52-8a71-2f4e0b95c318",
          "Name": "Codeunit Metadata Column Fixture",
          "Codeunits": [
            { "Id": {{DeclaresNothing}}, "Name": "Col Declares Nothing", "Properties": [] }
          ],
          "Namespaces": [
            {
              "Name": "System",
              "Namespaces": [
                {
                  "Name": "Utilities",
                  "Codeunits": [
                    { "Id": {{NestedNamespace}}, "Name": "Col Nested", "Properties": [] },
                    {
                      "Id": {{DirectExecute}},
                      "Name": "Col Direct Execute",
                      "Properties": [
                        { "Name": "InherentEntitlements", "Value": "X" },
                        { "Name": "InherentPermissions", "Value": "X" }
                      ]
                    },
                    {
                      "Id": {{IndirectExecute}},
                      "Name": "Col Indirect Execute",
                      "Properties": [
                        { "Name": "InherentEntitlements", "Value": "x" }
                      ]
                    },
                    {
                      "Id": {{MixedCaseMask}},
                      "Name": "Col Mixed Case",
                      "Properties": [
                        { "Name": "InherentPermissions", "Value": "rX" }
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
        var appPath = Path.Combine(_root, "codeunit-metadata-columns.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8);
            w.Write(SymbolReference);
        }
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
    }

    /// <summary>Renders one column through the live metatable and the real column builder.</summary>
    private static string? Column(int codeunitId, string fieldName)
        => RecordPatches.TryRenderCodeunitMetadataColumnForTests(codeunitId, fieldName);

    /// <summary>
    /// The column AL reads answers the namespace the symbol file's tree states, for a codeunit
    /// with no BC metadata document — which is every codeunit in a precompiled dependency.
    /// Reverting this <c>case</c> arm to <c>Default()</c> answers the empty string.
    /// </summary>
    [SkippableFact]
    public void ALNamespaceColumn_ForAPrecompiledDependencyCodeunit_AnswersTheSymbolFilesTreePath()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        Register();

        Assert.Equal("System.Utilities", Column(NestedNamespace, "AL Namespace"));

        // The negative control that makes the line above a claim about the COLUMN rather than
        // about a constant: a sibling in the same fixture, same tree position, still answers its
        // own Name — so the row is real and the table is not returning one fixed string.
        Assert.Equal("Col Nested", Column(NestedNamespace, "Name"));
    }

    /// <summary>
    /// Both mask columns answer the AL LETTER spelling — which is this table's own spelling and
    /// NOT the numeric one the equivalence projection writes. That difference is why proving the
    /// projection proves nothing here.
    /// </summary>
    [SkippableFact]
    public void InherentMaskColumns_AnswerTheAlLetterSpelling_NotTheProjectionsNumericOne()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        Register();

        Assert.Equal("X", Column(DirectExecute, "InherentPermissions"));
        Assert.Equal("X", Column(DirectExecute, "InherentEntitlements"));

        // Stated on one mask only: the two columns are read independently rather than one being
        // echoed into the other.
        Assert.Equal("x", Column(IndirectExecute, "InherentEntitlements"));
        Assert.Equal("", Column(IndirectExecute, "InherentPermissions"));

        // Mixed case in one value — indirect Read + direct Execute — which is the form that
        // actually ships on the non-codeunit kinds (40 occurrences of "rX" in 28.1). The letters
        // survive verbatim here because this column IS the letter spelling.
        Assert.Equal("rX", Column(MixedCaseMask, "InherentPermissions"));
    }

    /// <summary>
    /// A codeunit stating none keeps BC's own default, which is the honest answer for "declares
    /// none" — so the fallback did not turn absence into a fabricated value.
    ///
    /// <para>This codeunit is reached through the flat top-level array, so it is outside the
    /// namespace tree as well as empty of properties; see the fixture's note on why those are
    /// two different things.</para>
    /// </summary>
    [SkippableFact]
    public void ACodeunitStatingNothing_KeepsBcsOwnDefault_RatherThanAFabricatedValue()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        Register();

        Assert.Equal("", Column(DeclaresNothing, "AL Namespace"));
        Assert.Equal("", Column(DeclaresNothing, "InherentPermissions"));
        Assert.Equal("", Column(DeclaresNothing, "InherentEntitlements"));

        // ...and the row exists, so the three empties above are a codeunit answering "none"
        // rather than a missing row answering nothing.
        Assert.Equal("Col Declares Nothing", Column(DeclaresNothing, "Name"));
    }

    /// <summary>
    /// A letter the mask table does not name is refused rather than answered as a narrower
    /// permission — the same contract <c>ReadPermissionMaskString</c> holds in the opposite
    /// direction, and the reason is identical: dropping the character would answer a permission
    /// the codeunit does not declare, which no caller could tell from a real one
    /// (<c>loud-failures.md</c>).
    /// </summary>
    [SkippableFact]
    public void AMaskLetterTheRunnerCannotSpell_IsRefused_NotAnsweredAsANarrowerPermission()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var appPath = Path.Combine(_root, "unspellable-mask.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8);
            w.Write($$"""
                {
                  "RuntimeVersion": "15.1",
                  "AppId": "6d1f0a47-9b3e-4c52-8a71-2f4e0b95c319",
                  "Name": "Unspellable Mask Fixture",
                  "Codeunits": [
                    {
                      "Id": {{DirectExecute}},
                      "Name": "Col Unspellable",
                      "Properties": [ { "Name": "InherentPermissions", "Value": "Q" } ]
                    }
                  ]
                }
                """);
        }
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);

        var ex = Assert.Throws<AlRunner.Infrastructure.RunnerOutOfScopeException>(
            () => RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(DirectExecute));

        // The message names the offending character and the mask it came from, so the reader can
        // tell WHICH letter is unknown rather than only that something was.
        Assert.Contains("'Q'", ex.Message);
        Assert.Contains("RIMDX", ex.Message);
    }
}
