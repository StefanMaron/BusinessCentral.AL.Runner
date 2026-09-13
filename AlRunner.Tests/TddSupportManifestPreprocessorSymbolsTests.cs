// #4071: TddSupport re-reads an excluded object's source to name its [Test] procedures. That
// re-parse must pick the same #if branch the compile picked, so it parses under the manifest the
// compile read — carried on the detail — and never a manifest found by walking up from the file.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddSupportManifestPreprocessorSymbolsTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-tdd-manifest-symbols");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteManifest(string dir, string? preprocessorSymbolsJson)
    {
        Directory.CreateDirectory(dir);
        var symbols = preprocessorSymbolsJson is null ? "" : $", \"preprocessorSymbols\": {preprocessorSymbolsJson}";
        var path = Path.Combine(dir, "app.json");
        File.WriteAllText(path,
            $$"""{ "id": "00000000-0000-0000-0000-000000004071", "name": "T", "publisher": "P", "version": "1.0.0.0"{{symbols}} }""");
        return path;
    }

    private static string WriteTddCodeunit(string dir)
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Tdd.Codeunit.al");
        File.WriteAllText(file, """
            codeunit 94071 "Manifest Symbol Tdd"
            {
                Subtype = Test;

            #if PPX4071
                [Test]
                procedure OnlyDefinedTest()
                begin
                end;
            #else
                [Test]
                procedure OnlyUndefinedTest()
                begin
                end;
            #endif

                [Test]
                procedure Broken()
                begin
                    NoSuchProcedure();
                end;
            }
            """);
        return file;
    }

    private static string[] TestNames(string file, string? compileManifest) =>
        TddSupport.BuildFailedTests(new[]
            {
                new TddExcludedObjectDetail(file, "Tdd.Codeunit", new[] { "error AL0118" }, compileManifest),
            })
            .Select(r => r.Method)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void BuildFailedTests_CompileManifestDefinesSymbol_ReportsTheDefinedBranch()
    {
        var app = Path.Combine(_root, "defined");
        var manifest = WriteManifest(app, "[\"PPX4071\"]");
        // The AL sits under src/, the app.json at the app root: the compile reads it through
        // appRootDir, so the detail names it even though src/ holds none.
        var file = WriteTddCodeunit(Path.Combine(app, "src"));

        Assert.Equal(new[] { "Broken", "OnlyDefinedTest" }, TestNames(file, manifest));
    }

    [Fact]
    public void BuildFailedTests_CompileManifestDoesNotDefineSymbol_ReportsTheElseBranch()
    {
        var app = Path.Combine(_root, "undefined");
        var manifest = WriteManifest(app, preprocessorSymbolsJson: null);
        var file = WriteTddCodeunit(app);

        Assert.Equal(new[] { "Broken", "OnlyUndefinedTest" }, TestNames(file, manifest));
    }

    [Fact]
    public void BuildFailedTests_CompileReadNoManifest_IgnoresAParentFoldersManifest()
    {
        // A parent's app.json defines the symbol, but the compile of this folder read none
        // (#2542: the compile never climbs to ../app.json). The re-parse must agree with it.
        WriteManifest(_root, "[\"PPX4071\"]");
        var file = WriteTddCodeunit(Path.Combine(_root, "no-manifest-folder"));

        Assert.Equal(new[] { "Broken", "OnlyUndefinedTest" }, TestNames(file, compileManifest: null));
    }
}
