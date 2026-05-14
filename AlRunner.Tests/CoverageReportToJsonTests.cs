using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for CoverageReport.ToJson.
/// Uses [Collection("Pipeline")] because SourceFileMapper is a static singleton;
/// we must run sequentially and Clear() in the constructor to avoid cross-test pollution.
/// Object names are prefixed with "T6_" as an extra guard so these tests don't
/// interfere with names registered by other Pipeline-collection tests.
/// </summary>
[Collection("Pipeline")]
public class CoverageReportToJsonTests
{
    public CoverageReportToJsonTests()
    {
        SourceFileMapper.Clear();
    }

    // -----------------------------------------------------------------------
    // 1. Happy path: two scopes → two different files, partial hits
    // -----------------------------------------------------------------------
    [Fact]
    public void ToJson_BuildsFileEntries_OnePerScopeWithFile()
    {
        SourceFileMapper.Register("T6_CodeunitA", "src/CodeunitA.al");
        SourceFileMapper.Register("T6_CodeunitB", "src/CodeunitB.al");

        var sourceSpans = new Dictionary<(string Scope, int StmtIndex), int>
        {
            [("T6_ScopeA", 0)] = 10,
            [("T6_ScopeA", 1)] = 20,
            [("T6_ScopeB", 0)] = 5,
        };

        var totalStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_ScopeA", 0),
            ("T6_ScopeA", 1),
            ("T6_ScopeB", 0),
        };

        var hitStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_ScopeA", 0), // hit
            // ScopeA stmtIdx 1 not hit
            ("T6_ScopeB", 0), // hit
        };

        var scopeToObject = new Dictionary<string, string>
        {
            ["T6_ScopeA"] = "T6_CodeunitA",
            ["T6_ScopeB"] = "T6_CodeunitB",
        };

        var result = CoverageReport.ToJson(sourceSpans, hitStatements, totalStatements, scopeToObject);

        Assert.Equal(2, result.Count);

        // Files should be sorted by path
        var fileA = result.Find(f => f.File == "src/CodeunitA.al");
        var fileB = result.Find(f => f.File == "src/CodeunitB.al");
        Assert.NotNull(fileA);
        Assert.NotNull(fileB);

        Assert.Equal(2, fileA.TotalStatements);
        Assert.Equal(1, fileA.HitStatements);
        Assert.Equal(2, fileA.Lines.Count);

        Assert.Equal(1, fileB.TotalStatements);
        Assert.Equal(1, fileB.HitStatements);
        Assert.Single(fileB.Lines);
    }

    // -----------------------------------------------------------------------
    // 2. Line deduplication: multiple stmtIndexes on the same AL line sum hits
    // -----------------------------------------------------------------------
    [Fact]
    public void ToJson_LineDeduplication_SumsHitsPerLine()
    {
        SourceFileMapper.Register("T6_MultiStmt", "src/Multi.al");

        // Two statements on line 7 (both hit), one statement on line 8 (not hit)
        var sourceSpans = new Dictionary<(string Scope, int StmtIndex), int>
        {
            [("T6_MultiScope", 0)] = 7,
            [("T6_MultiScope", 1)] = 7,
            [("T6_MultiScope", 2)] = 8,
        };

        var totalStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_MultiScope", 0),
            ("T6_MultiScope", 1),
            ("T6_MultiScope", 2),
        };

        var hitStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_MultiScope", 0),
            ("T6_MultiScope", 1),
            // stmtIdx 2 not hit
        };

        var scopeToObject = new Dictionary<string, string>
        {
            ["T6_MultiScope"] = "T6_MultiStmt",
        };

        var result = CoverageReport.ToJson(sourceSpans, hitStatements, totalStatements, scopeToObject);

        Assert.Single(result);
        var file = result[0];

        Assert.Equal(2, file.Lines.Count);

        var line7 = file.Lines.Find(l => l.Line == 7);
        var line8 = file.Lines.Find(l => l.Line == 8);
        Assert.NotNull(line7);
        Assert.NotNull(line8);

        // Both statements on line 7 were hit → Hits = 2 (summed, not max-1)
        Assert.Equal(2, line7.Hits);
        // Statement on line 8 not hit → Hits = 0
        Assert.Equal(0, line8.Hits);
    }

    // -----------------------------------------------------------------------
    // 3. No hits: all reachable lines still present with Hits = 0
    // -----------------------------------------------------------------------
    [Fact]
    public void ToJson_NoHits_AllLinesPresentWithZero()
    {
        SourceFileMapper.Register("T6_NoHitObj", "src/NoHit.al");

        var sourceSpans = new Dictionary<(string Scope, int StmtIndex), int>
        {
            [("T6_NoHitScope", 0)] = 3,
            [("T6_NoHitScope", 1)] = 7,
            [("T6_NoHitScope", 2)] = 12,
        };

        var totalStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_NoHitScope", 0),
            ("T6_NoHitScope", 1),
            ("T6_NoHitScope", 2),
        };

        var hitStatements = new HashSet<(string Type, int Id)>(); // empty

        var scopeToObject = new Dictionary<string, string>
        {
            ["T6_NoHitScope"] = "T6_NoHitObj",
        };

        var result = CoverageReport.ToJson(sourceSpans, hitStatements, totalStatements, scopeToObject);

        Assert.Single(result);
        var file = result[0];

        Assert.Equal(3, file.TotalStatements);
        Assert.Equal(0, file.HitStatements);
        Assert.Equal(3, file.Lines.Count);

        // Every line should be present with Hits = 0
        Assert.All(file.Lines, l => Assert.Equal(0, l.Hits));
        Assert.Contains(file.Lines, l => l.Line == 3);
        Assert.Contains(file.Lines, l => l.Line == 7);
        Assert.Contains(file.Lines, l => l.Line == 12);
    }

    // -----------------------------------------------------------------------
    // 4. Empty input → empty output
    // -----------------------------------------------------------------------
    [Fact]
    public void ToJson_EmptyInput_ReturnsEmptyList()
    {
        var result = CoverageReport.ToJson(
            new Dictionary<(string Scope, int StmtIndex), int>(),
            new HashSet<(string Type, int Id)>(),
            new HashSet<(string Type, int Id)>(),
            new Dictionary<string, string>());

        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // 5. Non-total statements (var-decls, blank lines) must be skipped
    // -----------------------------------------------------------------------
    [Fact]
    public void ToJson_SkipsNonTotalStatements()
    {
        SourceFileMapper.Register("T6_FilterObj", "src/Filter.al");

        // sourceSpans has 3 entries: stmtIdx 0 and 2 are real stmts; stmtIdx 1 is a var-decl (not in totalStatements)
        var sourceSpans = new Dictionary<(string Scope, int StmtIndex), int>
        {
            [("T6_FilterScope", 0)] = 5,
            [("T6_FilterScope", 1)] = 6, // var-decl: NOT in totalStatements
            [("T6_FilterScope", 2)] = 10,
        };

        var totalStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_FilterScope", 0),
            // stmtIdx 1 intentionally absent (simulates var-decl)
            ("T6_FilterScope", 2),
        };

        var hitStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_FilterScope", 0),
            ("T6_FilterScope", 2),
        };

        var scopeToObject = new Dictionary<string, string>
        {
            ["T6_FilterScope"] = "T6_FilterObj",
        };

        var result = CoverageReport.ToJson(sourceSpans, hitStatements, totalStatements, scopeToObject);

        Assert.Single(result);
        var file = result[0];

        // Only 2 real statements, not 3
        Assert.Equal(2, file.TotalStatements);
        Assert.Equal(2, file.HitStatements);

        // Line 6 (the var-decl) must NOT appear in Lines
        Assert.Equal(2, file.Lines.Count);
        Assert.DoesNotContain(file.Lines, l => l.Line == 6);
        Assert.Contains(file.Lines, l => l.Line == 5);
        Assert.Contains(file.Lines, l => l.Line == 10);
    }

    // -----------------------------------------------------------------------
    // 6. Library/stub scopes with no file mapping → entirely excluded
    // -----------------------------------------------------------------------
    [Fact]
    public void ToJson_SkipsLibraryScopes_NoFileMapping()
    {
        SourceFileMapper.Register("T6_UserObj", "src/User.al");
        // "T6_LibScope" is NOT in scopeToObject → library/stub → must be excluded

        var sourceSpans = new Dictionary<(string Scope, int StmtIndex), int>
        {
            [("T6_UserScope", 0)] = 15,
            [("T6_LibScope", 0)] = 99, // library stub
        };

        var totalStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_UserScope", 0),
            ("T6_LibScope", 0),
        };

        var hitStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_UserScope", 0),
            ("T6_LibScope", 0),
        };

        var scopeToObject = new Dictionary<string, string>
        {
            ["T6_UserScope"] = "T6_UserObj",
            // T6_LibScope deliberately absent
        };

        var result = CoverageReport.ToJson(sourceSpans, hitStatements, totalStatements, scopeToObject);

        Assert.Single(result);
        var file = result[0];
        Assert.Equal("src/User.al", file.File);
        Assert.Equal(1, file.TotalStatements);
        Assert.Equal(1, file.HitStatements);
    }

    // -----------------------------------------------------------------------
    // 7. Null scopeToObject → no statements can be mapped → empty result
    // -----------------------------------------------------------------------
    [Fact]
    public void ToJson_NullScopeToObject_ReturnsEmpty()
    {
        SourceFileMapper.Register("T6_SomeObj", "src/Some.al");

        var sourceSpans = new Dictionary<(string Scope, int StmtIndex), int>
        {
            [("T6_SomeScope", 0)] = 42,
        };

        var totalStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_SomeScope", 0),
        };

        var hitStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_SomeScope", 0),
        };

        // Pass null for scopeToObject — no mapping possible
        var result = CoverageReport.ToJson(sourceSpans, hitStatements, totalStatements, scopeToObject: null);

        Assert.Empty(result);
    }

    // -----------------------------------------------------------------------
    // 8. Deterministic ordering: files by path, lines by number within file
    // -----------------------------------------------------------------------
    [Fact]
    public void ToJson_RecordsSerializeWithCamelCasePropertyNames()
    {
        SourceFileMapper.Register("T6_S_Cam", "src/Cam.al");
        var sourceSpans = new Dictionary<(string Scope, int StmtIndex), int>
        {
            [("T6_S_Cam_Scope", 0)] = 5,
        };
        var hit = new HashSet<(string Type, int Id)> { ("T6_S_Cam_Scope", 0) };
        var total = new HashSet<(string Type, int Id)> { ("T6_S_Cam_Scope", 0) };
        var scopeToObject = new Dictionary<string, string> { ["T6_S_Cam_Scope"] = "T6_S_Cam" };

        var result = CoverageReport.ToJson(sourceSpans, hit, total, scopeToObject);
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        // Schema-mandated camelCase shape
        Assert.Contains("\"file\":", json);
        Assert.Contains("\"lines\":", json);
        Assert.Contains("\"totalStatements\":", json);
        Assert.Contains("\"hitStatements\":", json);
        Assert.Contains("\"line\":", json);
        Assert.Contains("\"hits\":", json);
        // PascalCase MUST NOT appear (would happen with default options + no attributes)
        Assert.DoesNotContain("\"File\":", json);
        Assert.DoesNotContain("\"Lines\":", json);
        Assert.DoesNotContain("\"TotalStatements\":", json);
    }

    [Fact]
    public void ToJson_ScopeMappedButObjectNotRegistered_SkipsScope()
    {
        // scopeToObject has the scope; the AL object name is NOT in SourceFileMapper.
        // GetFile returns null, scope is silently skipped.
        var sourceSpans = new Dictionary<(string Scope, int StmtIndex), int>
        {
            [("T6_GhostScope", 0)] = 1,
        };
        var hit = new HashSet<(string Type, int Id)>();
        var total = new HashSet<(string Type, int Id)> { ("T6_GhostScope", 0) };
        var scopeToObject = new Dictionary<string, string>
        {
            ["T6_GhostScope"] = "T6_NonexistentObject",  // never registered
        };

        var result = CoverageReport.ToJson(sourceSpans, hit, total, scopeToObject);
        Assert.Empty(result);
    }

    [Fact]
    public void ToJson_DeterministicOrdering_FilesByPathLinesByNumber()
    {
        SourceFileMapper.Register("T6_ObjZ", "src/zzz.al");
        SourceFileMapper.Register("T6_ObjA", "src/aaa.al");

        // Intentionally register spans in reverse order to verify sorting
        var sourceSpans = new Dictionary<(string Scope, int StmtIndex), int>
        {
            [("T6_ZScope", 0)] = 30,
            [("T6_ZScope", 1)] = 10,
            [("T6_ZScope", 2)] = 20,
            [("T6_AScope", 0)] = 5,
        };

        var totalStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_ZScope", 0),
            ("T6_ZScope", 1),
            ("T6_ZScope", 2),
            ("T6_AScope", 0),
        };

        var hitStatements = new HashSet<(string Type, int Id)>
        {
            ("T6_ZScope", 0),
            ("T6_AScope", 0),
        };

        var scopeToObject = new Dictionary<string, string>
        {
            ["T6_ZScope"] = "T6_ObjZ",
            ["T6_AScope"] = "T6_ObjA",
        };

        var result = CoverageReport.ToJson(sourceSpans, hitStatements, totalStatements, scopeToObject);

        Assert.Equal(2, result.Count);

        // Files in alphabetical path order: aaa.al before zzz.al
        Assert.Equal("src/aaa.al", result[0].File);
        Assert.Equal("src/zzz.al", result[1].File);

        // Lines within zzz.al in ascending order: 10, 20, 30
        var linesZ = result[1].Lines;
        Assert.Equal(3, linesZ.Count);
        Assert.Equal(10, linesZ[0].Line);
        Assert.Equal(20, linesZ[1].Line);
        Assert.Equal(30, linesZ[2].Line);
    }
}
