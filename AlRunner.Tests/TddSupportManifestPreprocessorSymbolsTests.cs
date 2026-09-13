// #4071: TddSupport re-reads an excluded object's source to name its [Test] procedures. That
// re-parse must pick the same #if branch the compile picked, which includes the owning app's
// app.json `preprocessorSymbols` — not only CLEANSCHEMA1..25 and --define.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddSupportManifestPreprocessorSymbolsTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-tdd-manifest-symbols");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteApp(string name, string? preprocessorSymbolsJson)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        var symbols = preprocessorSymbolsJson is null ? "" : $", \"preprocessorSymbols\": {preprocessorSymbolsJson}";
        File.WriteAllText(Path.Combine(dir, "app.json"),
            $$"""{ "id": "00000000-0000-0000-0000-000000004071", "name": "{{name}}", "publisher": "P", "version": "1.0.0.0"{{symbols}} }""");
        // Nested one directory below app.json: the owning manifest is found by walking up.
        var file = Path.Combine(dir, "src", "Tdd.Codeunit.al");
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

    private static string[] TestNames(string file) =>
        TddSupport.BuildFailedTests(new[] { new TddExcludedObjectDetail(file, "Tdd.Codeunit", new[] { "error AL0118" }) })
            .Select(r => r.Method)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void BuildFailedTests_ManifestDefinesSymbol_ReportsTheDefinedBranch()
    {
        var file = WriteApp("defined", "[\"PPX4071\"]");

        Assert.Equal(new[] { "Broken", "OnlyDefinedTest" }, TestNames(file));
    }

    [Fact]
    public void BuildFailedTests_ManifestDoesNotDefineSymbol_ReportsTheElseBranch()
    {
        var file = WriteApp("undefined", preprocessorSymbolsJson: null);

        Assert.Equal(new[] { "Broken", "OnlyUndefinedTest" }, TestNames(file));
    }
}
