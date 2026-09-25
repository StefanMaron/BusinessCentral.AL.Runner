// CoverageObjectSourceLinesTests — #4572: AlSourceLocationMap.ObjectSourceLines is the text the
// runner serves in place of BC's "User AL Code", which BC's Code Coverage (2000000049) line rows
// index by decoded [SourceSpans] line. That numbering is [file preamble][the object], so index L
// must hold the line a span line L names: the preamble's lines first, then the object's own.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// Parses AL with BC's own parser in-process, so it joins BcEngineCollection.
[Collection(BcEngineCollection.Name)]
public sealed class CoverageObjectSourceLinesTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public CoverageObjectSourceLinesTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-coverage-object-source-lines");
        Directory.CreateDirectory(_root);
    }

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string TwoObjectsWithPreamble = """
        namespace Cov.Lines;

        using System.Utilities;

        // leading comment: part of the first object's text, not preamble
        codeunit 63650 "Cov Lines First"
        {
            procedure A(): Integer
            begin
                exit(1);
            end;
        }

        codeunit 63651 "Cov Lines Second"
        {
            procedure B(): Integer
            begin
                exit(2);
            end;
        }
        """;

    private AlSourceLocationMap BuildMap()
    {
        File.WriteAllText(Path.Combine(_root, "Two.Codeunit.al"), TwoObjectsWithPreamble);
        return AlCoverageSourceMap.Build(new[] { _root });
    }

    [SkippableFact]
    public void ObjectSourceLines_LaterObject_IsThePreambleFollowedByThatObjectOnly()
    {
        RequireEngine();
        var lines = BuildMap().ObjectSourceLines("CodeUnit", 63651);

        Assert.NotNull(lines);
        Assert.Equal(new[]
        {
            "namespace Cov.Lines;",
            "",
            "using System.Utilities;",
            "",
            "codeunit 63651 \"Cov Lines Second\"",
            "{",
            "    procedure B(): Integer",
            "    begin",
            "        exit(2);",
            "    end;",
            "}",
        }, lines);
    }

    [SkippableFact]
    public void ObjectSourceLines_FirstObject_CarriesItsLeadingCommentAndStopsAtItsOwnEnd()
    {
        RequireEngine();
        var lines = BuildMap().ObjectSourceLines("CodeUnit", 63650);

        Assert.NotNull(lines);
        Assert.Equal("// leading comment: part of the first object's text, not preamble", lines![4]);
        Assert.Equal("        exit(1);", lines[9]);
        Assert.Equal("}", lines[^1]);
        Assert.Equal(12, lines.Count);
    }

    [SkippableFact]
    public void ObjectSourceLines_IndexAgreesWithLineOffset_ForEveryObject()
    {
        // The two numberings the runner already had (LineOffset, for --coverage) and the new one
        // must name the same file line: text[L] == file[L + LineOffset] past the preamble.
        RequireEngine();
        var map = BuildMap();
        var file = File.ReadAllLines(Path.Combine(_root, "Two.Codeunit.al"));
        foreach (var id in new[] { 63650, 63651 })
        {
            var lines = map.ObjectSourceLines("CodeUnit", id)!;
            var offset = map.LineOffset("CodeUnit", id);
            for (int l = 4; l < lines.Count; l++)
                Assert.Equal(file[l + offset], lines[l]);
        }
    }

    [SkippableFact]
    public void ObjectSourceLines_ObjectNotInTheMap_IsNull()
    {
        RequireEngine();
        Assert.Null(BuildMap().ObjectSourceLines("CodeUnit", 710));
    }
}
