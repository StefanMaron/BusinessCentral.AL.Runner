// The pure parts of --tdd's stub beside a precompiled codeunit (#5037): which codeunit id the stub
// takes, and the text edit that points call sites at it. In-process, no runner and no BC engine; the
// end-to-end claim is TddPrecompiledTests. The claim is about the runner's own bookkeeping, not BC.
using AlRunner;
using Xunit;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Tests;

public sealed class TddPrecompiledStubTests
{
    private static NavSyntax.SyntaxTree Parse(string text, string path = "T.al") =>
        NavSyntax.SyntaxTree.ParseObjectText(text, path: path, encoding: null!,
            BcCompiler.BuildParseOptions(BcCompiler.ReadManifestCompilerInputs(null)), default);

    [Fact]
    public void FreeCodeunitId_SkipsWhatTheTreesAndTheReservationHold_ThenRunsOut()
    {
        var trees = new[]
        {
            Parse("codeunit 65320 \"A\" { }"),
            Parse("codeunit 65321 \"B\" { } table 65322 \"C\" { }"), // a table of that id is not a codeunit
        };
        var ranges = new[] { (65320, 65323) };

        Assert.Equal(65322, TddPrecompiledStub.FreeCodeunitId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65323, TddPrecompiledStub.FreeCodeunitId(trees, ranges, new HashSet<int> { 65322 }));
        Assert.Null(TddPrecompiledStub.FreeCodeunitId(trees, ranges, new HashSet<int> { 65322, 65323 }));
        // A second range is used once the first has none left.
        Assert.Equal(65400, TddPrecompiledStub.FreeCodeunitId(trees, new[] { (65320, 65321), (65400, 65401) }, new HashSet<int>()));
        // No range at all: nothing to allocate from, never a guessed id.
        Assert.Null(TddPrecompiledStub.FreeCodeunitId(trees, Array.Empty<(int, int)>(), new HashSet<int>()));
    }

    /// <summary>#5445: ids are per object type, in both directions - a codeunit does not take a table's id from the
    /// first free table id, and a table does not take a codeunit's.</summary>
    [Fact]
    public void FreeTableId_IsPerObjectType_InBothDirections()
    {
        var trees = new[]
        {
            Parse("codeunit 65320 \"A\" { } codeunit 65321 \"B\" { }"),
            Parse("table 65320 \"C\" { fields { field(1; \"K\"; Integer) { } } }"),
        };
        var ranges = new[] { (65320, 65323) };

        Assert.Equal(65321, TddPrecompiledStub.FreeTableId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65322, TddPrecompiledStub.FreeCodeunitId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65322, TddPrecompiledStub.FreeTableId(trees, ranges, new HashSet<int> { 65321 }));
        Assert.Null(TddPrecompiledStub.FreeTableId(trees, ranges, new HashSet<int> { 65321, 65322, 65323 }));
    }

    /// <summary>#5445: the enum id space is its own, in both directions - a codeunit or a table with id 65321 does not take
    /// it from an enum, and an enum does not take a codeunit's or a table's.</summary>
    [Fact]
    public void FreeEnumId_IsPerObjectType_InBothDirections()
    {
        var trees = new[]
        {
            Parse("codeunit 65320 \"A\" { } codeunit 65321 \"B\" { }"),
            Parse("table 65322 \"T\" { fields { field(1; \"K\"; Integer) { } } }"),
            Parse("enum 65320 \"E\" { }"),
        };
        var ranges = new[] { (65320, 65323) };

        Assert.Equal(65321, TddPrecompiledStub.FreeEnumId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65322, TddPrecompiledStub.FreeCodeunitId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65320, TddPrecompiledStub.FreeTableId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65322, TddPrecompiledStub.FreeEnumId(trees, ranges, new HashSet<int> { 65321 }));
        Assert.Null(TddPrecompiledStub.FreeEnumId(trees, ranges, new HashSet<int> { 65321, 65322, 65323 }));
    }

    /// <summary>#5430: a tableextension's and an enumextension's id spaces are their own, in both directions - a codeunit, a
    /// table or an enum with the number does not take it from the extension, and an extension does not take theirs.</summary>
    [Fact]
    public void FreeTableExtensionId_AndFreeEnumExtensionId_ArePerObjectType_InBothDirections()
    {
        var trees = new[]
        {
            Parse("codeunit 65320 \"A\" { } table 65321 \"T\" { fields { field(1; \"K\"; Integer) { } } } enum 65322 \"E\" { }"),
            Parse("tableextension 65320 \"TX\" extends \"T\" { fields { field(65320; \"F\"; Integer) { } } }"),
            Parse("enumextension 65321 \"EX\" extends \"E\" { value(65321; V) { } }"),
        };
        var ranges = new[] { (65320, 65323) };

        Assert.Equal(65321, TddPrecompiledStub.FreeTableExtensionId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65320, TddPrecompiledStub.FreeEnumExtensionId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65322, TddPrecompiledStub.FreeEnumExtensionId(trees, ranges, new HashSet<int> { 65320, 65321 }));
        Assert.Equal(65323, TddPrecompiledStub.FreeTableExtensionId(trees, ranges, new HashSet<int> { 65321, 65322 }));
        Assert.Null(TddPrecompiledStub.FreeTableExtensionId(trees, ranges, new HashSet<int> { 65321, 65322, 65323 }));
        // The other direction: the extensions hold 65320 (table) and 65321 (enum), which a codeunit, table or enum may still take.
        Assert.Equal(65321, TddPrecompiledStub.FreeCodeunitId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65320, TddPrecompiledStub.FreeTableId(trees, ranges, new HashSet<int>()));
        Assert.Equal(65320, TddPrecompiledStub.FreeEnumId(trees, ranges, new HashSet<int>()));
    }

    /// <summary>#5430: the id of a field or the ordinal of a value is taken from the app's ranges, skipping what the
    /// object already uses and what was already handed out, and runs out rather than guessing.</summary>
    [Fact]
    public void FreeIdInRanges_SkipsUsedAndReserved_ThenRunsOut_AndNeverGuessesOutsideTheRanges()
    {
        var ranges = new[] { (65400, 65402), (65410, 65411) };
        var used = new HashSet<int> { 1, 2, 65400 };

        Assert.Equal(65401, TddPrecompiledStub.FreeIdInRanges(ranges, used, new HashSet<int>()));
        Assert.Equal(65402, TddPrecompiledStub.FreeIdInRanges(ranges, used, new HashSet<int> { 65401 }));
        // The second range is used once the first has none left.
        Assert.Equal(65410, TddPrecompiledStub.FreeIdInRanges(ranges, used, new HashSet<int> { 65401, 65402 }));
        Assert.Null(TddPrecompiledStub.FreeIdInRanges(ranges, used, new HashSet<int> { 65401, 65402, 65410, 65411 }));
        // No range at all: nothing to allocate from. Ids 1 and 2 are free of the object but are not the app's to give.
        Assert.Null(TddPrecompiledStub.FreeIdInRanges(Array.Empty<(int, int)>(), new HashSet<int>(), new HashSet<int>()));
    }

    [Fact]
    public void ReadIdRanges_ReadsTheManifest_AndNothingFromAnUnreadableOne()
    {
        var dir = TestScratch.Dir("al-runner-tdd-precompiled-ranges");
        Directory.CreateDirectory(dir);
        var good = Path.Combine(dir, "good.json");
        File.WriteAllText(good, """{ "idRanges": [ { "from": 10, "to": 20 }, { "from": 30, "to": 40 } ] }""");
        var none = Path.Combine(dir, "none.json");
        File.WriteAllText(none, """{ "name": "x" }""");
        var broken = Path.Combine(dir, "broken.json");
        File.WriteAllText(broken, "{ not json");

        Assert.Equal(new[] { (10, 20), (30, 40) }, TddPrecompiledStub.ReadIdRanges(good));
        Assert.Empty(TddPrecompiledStub.ReadIdRanges(none));
        Assert.Empty(TddPrecompiledStub.ReadIdRanges(broken));
        Assert.Empty(TddPrecompiledStub.ReadIdRanges(Path.Combine(dir, "missing.json")));
        Assert.Empty(TddPrecompiledStub.ReadIdRanges(null));
    }

    /// <summary>
    /// Two stub groups in one file, both with a site in the same codeunit and the first also in another
    /// codeunit: every edit is computed against the text as parsed and applied back to front, so each
    /// qualifier is replaced where it was, each codeunit gets one variable per stub, and the result still
    /// parses. The calls through the other variable are left alone.
    /// </summary>
    [Fact]
    public void Apply_PointsEachSiteAtItsStub_AndLeavesTheOtherCallsAlone()
    {
        const string source = """
            codeunit 1 "First"
            {
                procedure One()
                var
                    P: Codeunit "Pre";
                    R: Integer;
                begin
                    R := P.Missing(1);
                    R := P.Present(2);
                    R := P.Other(true);
                end;
            }

            codeunit 2 "Second"
            {
                procedure Two()
                var
                    P: Codeunit "Pre";
                    R: Integer;
                begin
                    R := P.Missing(3);
                end;
            }
            """;
        var parseOptions = BcCompiler.BuildParseOptions(BcCompiler.ReadManifestCompilerInputs(null));
        var trees = new[] { Parse(source) };
        var root = (NavSyntax.CompilationUnitSyntax)trees[0].GetRoot();
        var calls = root.DescendantNodes().OfType<NavSyntax.MemberAccessExpressionSyntax>().ToList();
        NavSyntax.MemberAccessExpressionSyntax Call(string member, int nth) =>
            calls.Where(m => ((NavSyntax.IdentifierNameSyntax)m.Name).Identifier.ValueText == member).ElementAt(nth);
        TddPrecompiledStub.Site SiteOf(NavSyntax.MemberAccessExpressionSyntax m) =>
            TddPrecompiledStub.SiteOf(trees, trees[0], m)!.Value;

        var missing = new TddPrecompiledStub.Group { StubId = 10, TreeIdx = 0 };
        missing.Procedures.Add("procedure \"Missing\"(Arg1: Integer): Integer\n    begin\n    end;");
        missing.Sites.Add(SiteOf(Call("Missing", 0)));
        missing.Sites.Add(SiteOf(Call("Missing", 1)));
        var other = new TddPrecompiledStub.Group { StubId = 11, TreeIdx = 0 };
        other.Procedures.Add("procedure \"Other\"(Arg1: Boolean): Integer\n    begin\n    end;");
        other.Sites.Add(SiteOf(Call("Other", 0)));

        TddPrecompiledStub.Apply(trees, parseOptions, new[] { missing, other });

        var text = trees[0].GetText().ToString();
        Assert.DoesNotContain(trees[0].GetDiagnostics(), d => d.Severity == Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics.DiagnosticSeverity.Error);
        Assert.Contains("R := TddStub10.Missing(1);", text);
        Assert.Contains("R := TddStub10.Missing(3);", text);
        Assert.Contains("R := TddStub11.Other(true);", text);
        Assert.Contains("R := P.Present(2);", text); // the real member is still reached through the real variable
        Assert.Contains("codeunit 10 \"TDD Stub 10\"", text);
        Assert.Contains("codeunit 11 \"TDD Stub 11\"", text);
        // One variable per stub per containing codeunit: Missing is used in both, Other in the first only.
        Assert.Equal(2, CountOf(text, "TddStub10: Codeunit \"TDD Stub 10\";"));
        Assert.Equal(1, CountOf(text, "TddStub11: Codeunit \"TDD Stub 11\";"));
    }

    private static int CountOf(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
