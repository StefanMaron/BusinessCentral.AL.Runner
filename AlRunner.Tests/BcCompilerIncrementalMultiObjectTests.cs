// BcCompilerIncrementalMultiObjectTests — #5003: the change model tracks one object per file, so a
// file declaring several objects is named by the baseline instead, and touching one falls back to a
// full compile. affectedOnly keys a test's statements there by the object that ran them, which is
// safe only while that holds. docs/server-mode.md#affectedonly-and-files-declaring-several-objects.
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalMultiObjectTests : IDisposable
{
    private const string Module = "MultiObjectModule";
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalMultiObjectTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-multi-object");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Path_(string relative) => Path.Combine(_root, relative);

    private void Write(string relative, string content) => File.WriteAllText(Path_(relative), content);

    private static string Codeunit(int id, string name, int value = 1) => $$"""
        codeunit {{id}} "{{name}}"
        {
            procedure Value(): Integer
            begin
                exit({{value}});
            end;
        }
        """;

    private static string Table(int id, string name) => $$"""
        table {{id}} "{{name}}"
        {
            fields { field(1; PK; Integer) { } }
            keys { key(PK; PK) { Clustered = true; } }
        }
        """;

    private void WriteBundle()
    {
        Write("app.json", """
            { "id": "c5003000-0000-4a11-9111-0000000000b1", "name": "MultiObject", "publisher": "Test",
              "version": "1.0.0.0", "idRanges": [ { "from": 90700, "to": 90799 } ], "runtime": "14.0" }
            """);
        Write("Single.al", Codeunit(90701, "MO Single"));
        // Each of the multi-object files below is a shape the baseline has to name.
        Write("TwoCodeunits.al", Codeunit(90710, "MO Two A") + "\n\n" + Codeunit(90711, "MO Two B", 2));
        Write("CodeunitAndTable.al", Codeunit(90712, "MO CT Unit") + "\n\n" + Table(90713, "MO CT Table"));
        Write("TableAndExtension.al", Table(90714, "MO TE Base") + """


            tableextension 90715 "MO TE Ext" extends "MO TE Base"
            {
                fields { field(90715; Extra; Integer) { } }
            }
            """);
    }

    private BcCompiler Baseline()
    {
        var compiler = new BcCompiler();
        var output = compiler.Emit(new[] { _root }, Module, appRootDir: _root, trackIncrementalBaseline: true);
        Assert.True(output.Diagnostics.Count == 0, string.Join(" | ", output.Diagnostics));
        return compiler;
    }

    private BcEmitOutput? Cycle(BcCompiler compiler, out string reason, out IReadOnlyList<AffectedObjectId>? changed)
        => compiler.TryEmitIncremental(new[] { _root }, Module, _root, out reason, out changed);

    private void Ready() => TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private static readonly string[] MultiObjectFiles =
        { "CodeunitAndTable.al", "TableAndExtension.al", "TwoCodeunits.al" };

    [SkippableFact]
    public void Baseline_NamesEveryMultiObjectFile_AndTracksOnlyTheSingleObjectOnes()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        var multi = compiler.TryGetMultiObjectPaths(Module)!;
        Assert.Equal(MultiObjectFiles, multi.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));

        var tracked = compiler.TryGetTrackedObjectsByPath(Module)!;
        Assert.Equal(new[] { "Single.al" }, tracked.Keys.Select(Path.GetFileName));
        Assert.Equal(new AffectedObjectId("Codeunit", 90701, "MO Single"), tracked[Path_("Single.al")]);
        Assert.Empty(multi.Intersect(tracked.Keys));
    }

    [SkippableFact]
    public void EditingAMultiObjectFile_FallsBackToAFullCompile_AndNoPeekAnswers()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        Write("TwoCodeunits.al", Codeunit(90710, "MO Two A") + "\n\n" + Codeunit(90711, "MO Two B", 3));

        Assert.Null(compiler.PeekChangedObjects(new[] { _root }, Module, _root));
        Assert.Null(compiler.PeekChangedScopes(new[] { _root }, Module, _root));
        var output = Cycle(compiler, out var reason, out _);
        Assert.Null(output);
        Assert.Contains("several objects", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The edit that leaves ONE object in a multi-object file classifies cleanly, so only the
    /// recorded paths stand between it and a fast path that reports one object as changed and
    /// leaves the file's other baseline entries behind.
    /// </summary>
    [SkippableFact]
    public void ShrinkingAMultiObjectFileToOneObject_FallsBackToo()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        Write("TwoCodeunits.al", Codeunit(90710, "MO Two A"));
        Write("MoreFromTwoCodeunits.al", Codeunit(90711, "MO Two B", 2));

        Assert.Null(compiler.PeekChangedObjects(new[] { _root }, Module, _root));
        Assert.Null(compiler.PeekChangedScopes(new[] { _root }, Module, _root));
        var output = Cycle(compiler, out var reason, out _);
        Assert.Null(output);
        Assert.Contains("TwoCodeunits.al", reason, StringComparison.Ordinal);
        Assert.Contains("several objects", reason, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void EditingASingleObjectFile_StaysOnTheFastPath_AndKeepsTheMultiObjectPaths()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();
        var before = compiler.TryGetMultiObjectPaths(Module)!.OrderBy(p => p, StringComparer.Ordinal).ToList();

        Write("Single.al", Codeunit(90701, "MO Single", 5));

        var output = Cycle(compiler, out var reason, out var changed);
        Assert.True(output != null, $"a single-object edit beside multi-object files must stay on the fast path: {reason}");
        Assert.Equal(new[] { new AffectedObjectId("Codeunit", 90701, "MO Single") }, changed);
        Assert.Equal(before, compiler.TryGetMultiObjectPaths(Module)!.OrderBy(p => p, StringComparer.Ordinal).ToList());
        Assert.Equal(new[] { "Single.al" }, compiler.TryGetTrackedObjectsByPath(Module)!.Keys.Select(Path.GetFileName));
    }

    [SkippableFact]
    public void ARecompiledBaseline_NamesTheNewMultiObjectFile()
    {
        Ready();
        WriteBundle();
        var compiler = new BcCompiler();
        compiler.Emit(new[] { _root }, Module, appRootDir: _root, trackIncrementalBaseline: true);
        Write("Single.al", Codeunit(90701, "MO Single") + "\n\n" + Codeunit(90702, "MO Joined"));

        Assert.Null(Cycle(compiler, out _, out _));
        compiler.Emit(new[] { _root }, Module, appRootDir: _root, trackIncrementalBaseline: true);

        Assert.Contains(Path_("Single.al"), compiler.TryGetMultiObjectPaths(Module)!);
        Assert.DoesNotContain(Path_("Single.al"), compiler.TryGetTrackedObjectsByPath(Module)!.Keys);
    }
}
