// RecordPatchesParseOnceTests — RED→GREEN guard for #1903.
//
// RecordPatches.AddSourceDirs hands each .al file's TEXT to eight extractors
// (TryParseTableFile / TryParseTableExtensionFile / TryParsePageFile / TryParseReportFile /
// TryParseQueryFile / TryParseXmlPortFile / TryParseObjectDeclFile / TryParseObjectCaptionFile).
// Each one is a thin foreach over ParseAlObjects(text), and ParseAlObjects used to build a
// brand-new BC syntax tree from `text` on EVERY call — so eight IDENTICAL trees were built
// per file. Measured on a 7,339-file real-world corpus: ~59,000 parses / 29.7s per pass
// instead of 7,339 parses, and --watch pays that cost every cycle (Register() runs the
// one-shot pass once; AddSourceDirs' _registered branch is what a watch cycle re-enters).
//
// The fix: ParseAlObjects now memoizes the single most-recently-built tree, keyed on
// (text, active preprocessor symbols). All eight TryParse*File extractors funnel through it
// on the SAME file text back-to-back (RecordPatches.ParseSourceFileIntoAllExtractors is the
// shared per-file call site both AddSourceDirs and Register() route every file through), so
// the second through eighth calls for a given file are cache hits.
//
// What these tests pin
// ---------------------
//  * MECHANISM (positive) — eight extractors called on the identical text build exactly ONE
//    real syntax tree (RecordPatches.ParseObjectTextCallCount), not eight. A COUNT, never a
//    duration — mirrors the discipline RecordPatchesSourceDirBatchTests established for #1833
//    (PopulateNclMetadataCacheCallCount) and BcCompilerWarmLoaderReuseTests for #1832.
//  * MECHANISM (negative, the "always answer 1" trap) — the memo is keyed on TEXT, not just
//    "how many times has this been called": two DIFFERENT files parsed back-to-back still cost
//    two real parses. An implementation that counted calls instead of comparing text (or that
//    cached the FIRST tree forever) would pass the positive test above while silently feeding
//    every file after the first the WRONG parsed content.
//  * MECHANISM (content) — the memoized path still yields the REAL parsed table, not an empty
//    stub that would make the call count trivially low for the wrong reason.
//  * CORRECTNESS (the #1900 regression this exists to prevent) — the SAME text parsed under
//    two DIFFERENT --define / preprocessor-symbol sets produces two DIFFERENT results. #1900
//    was a parser that silently stopped seeing --define symbols because AlParseOptions was a
//    `static readonly` field frozen before BcCompiler.SetExtraPreprocessorSymbols ran (#1907
//    fixed it by making AlParseOptions a property recomputed per call). A #1903 memo keyed on
//    text ALONE would reintroduce exactly that bug through a different door: the second call
//    would silently return the FIRST call's cached tree instead of re-parsing under the new
//    symbol set. Keying the memo on (text, symbols) — not text alone — is what this test
//    actually exercises.
//
// See RecordPatchesAddSourceDirsParseOnceTests.cs for the companion integration-level test
// that pins the same claim at the actual public entry point #1903 names (AddSourceDirs),
// through the real BC engine. It lives in a SEPARATE file on purpose: ParserStaticsIsolation-
// GuardTests scans whole FILE text for the "TryParse…File" reflection markers below and
// requires every class in a flagged file to join RecordPatchesSerialCollection — the
// integration test needs BcEngineCollection (for BcEngineFixture) instead, so it cannot share
// a file with a class that drives the parsers by reflection.
//
// This file drives the real parser via reflection (TryParseTableFile /
// TryParseTableExtensionFile / …), exactly like AlSourceParserSyntaxTreeTests /
// AlSourceParserCommentTests, so RecordPatchesParseOnceTests joins RecordPatchesSerialCollection
// — see ParserStaticsIsolationGuardTests for why: the parsers publish into process-wide static
// dictionaries and xUnit runs collections in parallel.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class RecordPatchesParseOnceTests : IDisposable
{
    private static readonly Type RecordPatchesType = typeof(RecordPatches);

    public RecordPatchesParseOnceTests()
    {
        // Start every test from a known, empty preprocessor-symbol state so a leftover
        // --define from another test sharing this serialized collection can't leak in.
        BcCompiler.SetExtraPreprocessorSymbols(Array.Empty<string>());
    }

    public void Dispose() => BcCompiler.SetExtraPreprocessorSymbols(Array.Empty<string>());

    private static void InvokeTryParse(string methodName, string text)
    {
        var method = RecordPatchesType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, new object[] { text });
    }

    private static Dictionary<int, ParsedTable> ParsedTables =>
        (Dictionary<int, ParsedTable>)RecordPatchesType
            .GetField("_parsedTables", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static string TableSource(int id, string name) => $$"""
        table {{id}} "{{name}}"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
            }
            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """;

    // ── Mechanism ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EightExtractors_OnTheSameText_BuildOneSyntaxTree_NotEight()
    {
        const int tableId = 61897;
        var text = TableSource(tableId, "Parse Once Test");

        var before = RecordPatches.ParseObjectTextCallCount;

        // Exactly the eight extractors AddSourceDirs / Register run over one file's text, in
        // the same order RecordPatches.ParseSourceFileIntoAllExtractors calls them.
        InvokeTryParse("TryParseTableFile", text);
        InvokeTryParse("TryParseTableExtensionFile", text);
        InvokeTryParse("TryParsePageFile", text);
        InvokeTryParse("TryParseReportFile", text);
        InvokeTryParse("TryParseQueryFile", text);
        InvokeTryParse("TryParseXmlPortFile", text);
        InvokeTryParse("TryParseObjectDeclFile", text);
        InvokeTryParse("TryParseObjectCaptionFile", text);

        var after = RecordPatches.ParseObjectTextCallCount;

        // Before #1903's fix this was 8 — one SyntaxTree.ParseObjectText build per extractor,
        // all building the IDENTICAL tree from the IDENTICAL text.
        Assert.Equal(1, after - before);

        // The memoized path still returns the REAL parsed table — rules out an implementation
        // that made the count small by returning nothing.
        Assert.True(ParsedTables.ContainsKey(tableId), $"table {tableId} was not parsed at all");
        Assert.Contains(ParsedTables[tableId].Fields, f => f.FieldId == 1 && f.FieldName == "No.");
    }

    [Fact]
    public void DifferentTextBetweenCalls_StillCostsOneRealParseEach()
    {
        const int tableIdA = 61898;
        const int tableIdB = 61899;
        var textA = TableSource(tableIdA, "Parse Once A");
        var textB = TableSource(tableIdB, "Parse Once B");

        var before = RecordPatches.ParseObjectTextCallCount;
        InvokeTryParse("TryParseTableFile", textA);
        InvokeTryParse("TryParseTableFile", textB);
        var after = RecordPatches.ParseObjectTextCallCount;

        // Two DIFFERENT files cost two real parses — the single-slot memo must key on the
        // TEXT, not merely remember "a parse already happened". An implementation that cached
        // the first tree forever (or counted calls instead of comparing text) would pass the
        // positive test above while corrupting every file parsed after the first — this is
        // exactly that trap, made visible.
        Assert.Equal(2, after - before);
        Assert.True(ParsedTables.ContainsKey(tableIdA));
        Assert.True(ParsedTables.ContainsKey(tableIdB));
        Assert.Equal("Parse Once A", ParsedTables[tableIdA].TableName);
        Assert.Equal("Parse Once B", ParsedTables[tableIdB].TableName);
    }

    // ── Correctness: the memo must not regress #1900 ────────────────────────────────────

    [Fact]
    public void SameText_DifferentDefineSets_ProducesDifferentFields_NotAStaleCachedTree()
    {
        const int tableId = 61900;
        var text = $$"""
            table {{tableId}} "Parse Once Define Gated"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
            #if PARSEONCE1903TEST
                    field(20; "Active Branch"; Text[10]) { }
            #else
                    field(21; "Dead Branch"; Text[10]) { }
            #endif
                }
                keys
                {
                    key(PK; "No.") { Clustered = true; }
                }
            }
            """;

        // First parse: symbol NOT defined -> the #else branch (field 21) is active.
        BcCompiler.SetExtraPreprocessorSymbols(Array.Empty<string>());
        InvokeTryParse("TryParseTableFile", text);
        var withoutDefine = ParsedTables[tableId];
        Assert.Contains(withoutDefine.Fields, f => f.FieldId == 21);
        Assert.DoesNotContain(withoutDefine.Fields, f => f.FieldId == 20);

        // Second parse: the EXACT SAME text string, but the active preprocessor symbol set
        // has changed -> the #if branch (field 20) must now be active. If the #1903 memo
        // were keyed on text alone, this call would incorrectly serve the FIRST call's
        // cached tree (the #else branch) instead of re-parsing — reproducing #1900 (a parser
        // that silently ignored a changed --define set) through a cache instead of a frozen
        // field.
        BcCompiler.SetExtraPreprocessorSymbols(new[] { "PARSEONCE1903TEST" });
        InvokeTryParse("TryParseTableFile", text);
        var withDefine = ParsedTables[tableId];
        Assert.Contains(withDefine.Fields, f => f.FieldId == 20);
        Assert.DoesNotContain(withDefine.Fields, f => f.FieldId == 21);
    }
}
