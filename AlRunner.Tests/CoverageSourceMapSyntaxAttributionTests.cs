// #3521: coverage and the debugger attribute a statement to an object through
// AlCoverageSourceMap, which since #3713 reads BC's syntax tree rather than a header regex.
// What was never pinned is the half of the claim that a regex cannot honour: a declaration-
// looking line in a comment, a string, or an inactive #if branch must not claim an object, and
// the branch the COMPILE took must be the branch the map takes.
//
// Map-level facts parse in-process (BcEngineCollection); the Cobertura fact spawns the runner
// and asserts specific file lines with specific hit counts, so a report that merely stopped
// crashing cannot pass.
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CoverageSourceMapSyntaxAttributionTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly BcEngineFixture _engine;
    private readonly string _root = TestScratch.Dir("al-runner-coverage-syntax-attribution");

    public CoverageSourceMapSyntaxAttributionTests(BcEngineFixture engine) => _engine = engine;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    // File lines are load-bearing: the executed statements are `exit(1)` on line 10 and the
    // StrLen call on line 28. Everything that looks like a declaration and is not one sits above
    // line 6, inside line 28's string, or in the branch FOO turns off.
    private static readonly string[] MultiObjectLines =
    {
        "/* block comment",                                              // 1
        "   codeunit 63899 \"Fake In Block\"",                           // 2
        "*/",                                                            // 3
        "// codeunit 63898 \"Fake In Line\"",                            // 4
        "#if FOO",                                                       // 5
        "codeunit 63800 \"Active Foo\"",                                 // 6
        "{",                                                             // 7
        "    procedure A(): Integer",                                    // 8
        "    begin",                                                     // 9
        "        exit(1);",                                              // 10  executed, FOO on
        "    end;",                                                      // 11
        "}",                                                             // 12
        "#else",                                                         // 13
        "codeunit 63801 \"Active Else\"",                                // 14
        "{",                                                             // 15
        "    procedure A(): Integer",                                    // 16
        "    begin",                                                     // 17
        "        exit(2);",                                              // 18  inactive when FOO is on
        "    end;",                                                      // 19
        "}",                                                             // 20
        "#endif",                                                        // 21
        "",                                                              // 22
        "#region second",                                                // 23
        "codeunit 63802 \"After Region\"",                               // 24
        "{",                                                             // 25
        "    procedure B(): Integer",                                    // 26
        "    begin",                                                     // 27
        "        exit(StrLen('codeunit 63897 \"Fake In String\"'));",    // 28  executed
        "    end;",                                                      // 29
        "}",                                                             // 30
        "#endregion",                                                    // 31
        "",
    };

    private string App(string name, bool withSymbol)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var symbols = withSymbol ? ", \"preprocessorSymbols\": [ \"FOO\" ]" : "";
        File.WriteAllText(Path.Combine(dir, "app.json"),
            "{ \"id\": \"5a3f0b11-3521-4a01-8001-000000003521\", \"name\": \"Syntax Attribution 3521\", "
            + "\"publisher\": \"AL Runner\", \"version\": \"1.0.0.0\", \"dependencies\": [], "
            + "\"idRanges\": [ { \"from\": 63800, \"to\": 63899 } ], \"runtime\": \"14.0\"" + symbols + " }");
        File.WriteAllText(Path.Combine(dir, "Multi.al"), string.Join("\n", MultiObjectLines));
        return dir;
    }

    [SkippableFact]
    public void Build_CommentsStringsAndTheInactiveBranch_CannotClaimAnObject()
    {
        RequireEngine();
        var on = App("map-on", withSymbol: true);

        var map = AlCoverageSourceMap.Build(new[] { on }, relativeTo: on);

        // Exactly the declarations the compile sees with FOO defined: not the block or line
        // comment's, not the string's, and not the #else branch's.
        Assert.Equal(
            new[] { ("CodeUnit", 63800), ("CodeUnit", 63802) },
            map.Keys.OrderBy(k => k.Id).ToArray());
    }

    [SkippableFact]
    public void Build_TheManifestSymbolPicksTheBranch_AndWithoutItTheOtherOne()
    {
        RequireEngine();
        var off = App("map-off", withSymbol: false);

        var map = AlCoverageSourceMap.Build(new[] { off }, relativeTo: off);

        Assert.Equal(
            new[] { ("CodeUnit", 63801), ("CodeUnit", 63802) },
            map.Keys.OrderBy(k => k.Id).ToArray());
    }

    private (string Output, int Exit) Spawn(string bundle, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-cache");
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// The same file, end to end: the report holds exactly the two executed statements, on the
    /// file lines they sit on, once each. Line 18 (the inactive branch) and any line a fake
    /// declaration could have produced are absent.
    /// </summary>
    [SkippableFact]
    public void Coverage_FileWithCommentsAnInactiveBranchAndARegion_ReportsTheRealObjectsFileLines()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = App("e2e", withSymbol: true);
        File.WriteAllText(Path.Combine(bundle, "T.Codeunit.al"), """
            codeunit 63810 "Syntax Attribution Tests"
            {
                Subtype = Test;

                [Test]
                procedure CallsBoth()
                var
                    A: Codeunit "Active Foo";
                    B: Codeunit "After Region";
                begin
                    if A.A() <> 1 then
                        Error('A');
                    if B.B() <= 0 then
                        Error('B');
                end;
            }
            """);
        var coveragePath = Path.Combine(_root, "cobertura.xml");

        var (output, exit) = Spawn(bundle, "--coverage", $"--coverage-out \"{coveragePath}\"");

        Assert.Equal(0, exit);
        Assert.True(File.Exists(coveragePath), $"cobertura.xml was not written.\n{output}");
        var cls = XDocument.Load(coveragePath).Descendants("class").First(c =>
            (c.Attribute("filename")?.Value ?? "").Replace('\\', '/').EndsWith("/Multi.al", StringComparison.Ordinal));
        var lines = cls.Descendants("line").ToDictionary(
            l => int.Parse(l.Attribute("number")!.Value), l => int.Parse(l.Attribute("hits")!.Value));

        Assert.Equal(new[] { 10, 28 }, lines.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(1, lines[10]);
        Assert.Equal(1, lines[28]);
    }
}
