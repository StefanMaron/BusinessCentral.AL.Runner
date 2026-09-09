// PageMetadataSourceTraceTests — issue #3750.
//
// A RUNNER-MECHANISM test, not a claim about BC. The claim under test is "which of the runner's
// three routes produced this page's Page Control Field rows", which is not a statement a service
// tier can adjudicate: all three routes produce the same ROW TYPE and AL cannot see the
// difference. That is the whole reason the trace has to exist — see
// .claude/rules/bc-behavior-tests-go-upstream.md for why this one stays here.
//
// WHAT MAKES THIS PROVE SOMETHING
//   The assertion is on the ROUTE, not on a line having been printed. Two properties do that
//   work, and a trace that hardcoded a single route value fails both:
//
//     * one run, two pages, two DIFFERENT routes — an emitted page traced `bc-document` and an
//       AL-text-only page traced `derived`, from a single enumeration;
//     * the negative direction per page — the emitted page must NOT be traced `derived` and the
//       text-only page must NOT be traced `bc-document`.
//
//   A `source=bc-document` constant passes neither, and neither does asserting only that some
//   `[page-metadata]` line appeared.
//
// THE LIMIT THIS TEST CANNOT COVER, and does not pretend to
//   #3590: a document that failed to load is memoised as a null and takes the derivation arm,
//   so it is indistinguishable from "no document existed" — here and in the trace. There is
//   deliberately no test asserting a `failed` route, because there deliberately is no such
//   route. TracePageMetadataSource says the same thing at the site.

using System.Text;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class PageMetadataSourceTraceTests : IDisposable
{
    private const int EmittedPageId = 90361;
    private const int EmittedTableId = 90360;
    private const int TextOnlyPageId = 90371;
    private const int TextOnlyTableId = 90370;

    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public PageMetadataSourceTraceTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-page-metadata-source-trace-tests");
        Directory.CreateDirectory(_root);
        AlObjectMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlObjectMetadataRegistry.Clear();
        // The parser dictionaries and the row memo are static, so a fixture left behind here
        // answers for the next class that declares one of these ids.
        RemoveFromDict("_parsedPages", EmittedPageId);
        RemoveFromDict("_parsedPages", TextOnlyPageId);
        RemoveFromDict("_parsedTables", EmittedTableId);
        RemoveFromDict("_parsedTables", TextOnlyTableId);
        RemoveMetaTableCacheEntry(EmittedTableId);
        RemoveMetaTableCacheEntry(TextOnlyTableId);
        ClearStatic("ClearBcPageControlDocuments");
        InvalidatePageControlFieldRowCache();
        Environment.SetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA_SOURCE", null);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>The page the runner COMPILES. BC's emitter captures a document for it, so it
    /// must take the document route.</summary>
    private const string EmittedAl = """
        table 90360 "PcfTrace Sample"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
                field(2; "Name Field"; Text[50]) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        page 90361 "PcfTrace Fixture"
        {
            PageType = Card;
            SourceTable = "PcfTrace Sample";
            layout
            {
                area(Content)
                {
                    group(G)
                    {
                        field(TraceEntryNo; Rec."Entry No.") { ApplicationArea = All; }
                        field(TraceName; Rec."Name Field") { ApplicationArea = All; }
                    }
                }
            }
        }
        """;

    /// <summary>The control page: parsed from AL text and never emitted, so no document is
    /// captured for it and it must take the derivation. Without this arm the test could not
    /// tell a working trace from one that answers `bc-document` unconditionally.</summary>
    private const string TextOnlyAl = """
        table 90370 "PcfTraceFb Sample"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        page 90371 "PcfTraceFb Fixture"
        {
            PageType = Card;
            SourceTable = "PcfTraceFb Sample";
            layout
            {
                area(Content)
                {
                    group(G)
                    {
                        field(TraceFbEntryNo; Rec."Entry No.") { ApplicationArea = All; }
                    }
                }
            }
        }
        """;

    [SkippableFact]
    public void Trace_NamesTheRoutePerPage_DocumentForACompiledPage_DerivationForATextOnlyOne()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Emit only the first fixture, so exactly one of the two pages has a document. Both
        // then go through the AL parser, which is what puts both into the same enumeration.
        var output = new BcCompiler().Emit(new[] { WriteAl("PcfTrace.al", EmittedAl) }, "PcfTraceModule");
        Assert.True(output.Sources.Count > 0,
            $"expected the fixture to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");
        Assert.True(AlObjectMetadataRegistry.TryGet("Page", EmittedPageId, out var xml)
                    && !string.IsNullOrEmpty(xml),
            $"page {EmittedPageId}'s document was not captured, so this test could not tell the "
            + "two routes apart even if the trace worked.");

        ParseAlTableSource(EmittedAl);
        ParseAlPageSource(EmittedAl);
        ParseAlTableSource(TextOnlyAl);
        ParseAlPageSource(TextOnlyAl);

        var lines = EnumerateWithTrace("1");

        // Positive, page by page: the two pages take DIFFERENT routes in one enumeration.
        Assert.Contains($"[page-metadata] {EmittedPageId} source=bc-document", lines);
        Assert.Contains($"[page-metadata] {TextOnlyPageId} source=derived", lines);

        // Negative, page by page. This pair is what a hardcoded route fails: a trace that
        // always said `bc-document` satisfies the first assertion above and fails the second
        // one here, and a trace that always said `derived` does the mirror image.
        Assert.DoesNotContain($"[page-metadata] {EmittedPageId} source=derived", lines);
        Assert.DoesNotContain($"[page-metadata] {TextOnlyPageId} source=bc-document", lines);

        // Exactly one route per page per enumeration — a page traced twice would make the
        // compiled-vs-precompiled COUNT this trace exists to produce wrong, even with every
        // assertion above green.
        Assert.Equal(1, lines.Count(l => l.StartsWith($"[page-metadata] {EmittedPageId} ", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(l => l.StartsWith($"[page-metadata] {TextOnlyPageId} ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Trace_IsASilentNoOp_UnlessTheFlagIsExactlyOne()
    {
        // The table flag's contract, mirrored: "true" is the value a reader reaches for and it
        // must do nothing, so a measurement taken with it cannot come back a false zero that
        // reads as "no pages were built". No BC engine needed — the gate is read before any
        // route is taken, so an AL-text-only page exercises it.
        ParseAlTableSource(TextOnlyAl);
        ParseAlPageSource(TextOnlyAl);

        // Positive: the flag's ONE working value does emit, so the negatives below are about
        // the gate rather than about the fixture producing no rows.
        Assert.Contains($"[page-metadata] {TextOnlyPageId} source=derived", EnumerateWithTrace("1"));

        foreach (var value in new[] { null, "", "true", "True", "0", "2", "yes", " 1" })
        {
            var lines = EnumerateWithTrace(value);
            Assert.True(lines.All(l => !l.StartsWith("[page-metadata] ", StringComparison.Ordinal)),
                $"AL_RUNNER_TRACE_PAGE_METADATA_SOURCE={value ?? "<unset>"} emitted a trace line. "
                + "Every value other than \"1\" must be a silent no-op.\n"
                + string.Join("\n", lines.Where(l => l.StartsWith("[page-metadata] ", StringComparison.Ordinal))));
        }
    }

    /// <summary>
    /// Run the real row builder with the flag set to <paramref name="flag"/>, capturing
    /// <c>Console.Out</c>. Capturing stdout rather than stderr is itself part of the claim:
    /// the runner re-execs itself, so a stderr diagnostic can vanish, and a test reading the
    /// wrong stream would pass against a trace nobody could observe in a real run.
    /// </summary>
    private static List<string> EnumerateWithTrace(string? flag)
    {
        var previous = Console.Out;
        var captured = new StringWriter(new StringBuilder());
        Environment.SetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA_SOURCE", flag);
        try
        {
            Console.SetOut(captured);
            InvalidatePageControlFieldRowCache();
            var m = typeof(AlRunner.Patches.RecordPatches).GetMethod("EnumerateKnownPageControlFields",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RecordPatches.EnumerateKnownPageControlFields not found.");
            m.Invoke(null, null);
        }
        finally
        {
            Console.SetOut(previous);
            Environment.SetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA_SOURCE", null);
        }
        return captured.ToString()
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
    }

    private string WriteAl(string name, string source)
    {
        var dir = Path.Combine(_root, Path.GetFileNameWithoutExtension(name));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), source);
        return dir;
    }

    private static void ParseAlPageSource(string source)
        => typeof(AlRunner.Patches.RecordPatches)
            .GetMethod("TryParsePageFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, new object[] { source });

    private static void ParseAlTableSource(string source)
    {
        var m = typeof(AlRunner.Patches.RecordPatches).GetMethod("TryParseTableFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("RecordPatches.TryParseTableFile not found.");
        m.Invoke(null, new object?[] { source, null });
    }

    private static void InvalidatePageControlFieldRowCache()
        => typeof(AlRunner.Patches.RecordPatches)
            .GetField("_pageControlFieldRows",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, null);

    private static void ClearStatic(string method)
        => typeof(AlRunner.Patches.RecordPatches)
            .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, null);

    private static void RemoveMetaTableCacheEntry(int tableId)
    {
        var f = typeof(AlRunner.Patches.RecordPatches).GetField("_metaTableCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d && d.Contains(tableId))
            d.Remove(tableId);
    }

    private static void RemoveFromDict(string field, int id)
    {
        var f = typeof(AlRunner.Patches.RecordPatches).GetField(field,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is System.Collections.IDictionary d && d.Contains(id)) d.Remove(id);
    }
}
