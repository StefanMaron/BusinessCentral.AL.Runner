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
        Assert.Empty(trees[0].GetDiagnostics().Where(d => d.Severity == Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics.DiagnosticSeverity.Error));
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
