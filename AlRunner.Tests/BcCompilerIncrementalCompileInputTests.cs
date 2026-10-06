// BcCompilerIncrementalCompileInputTests — #5087: the change model hashed only `*.al`, but BC's
// compile also reads a report's layout file, a ControlAddIn's resources and the Translations
// folder. Edit only one of those and every `.al` hash matched, so TryEmitIncremental replayed the
// previous output with `changedObjects = []`: under affectedOnly no test was selected, and a layout
// the cold compile refuses (AL1081) kept passing.
// Rules and the population: docs/server-mode.md#affectedonly-and-files-the-compile-reads.
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalCompileInputTests : IDisposable
{
    private const string Module = "CompileInputModule";
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalCompileInputTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-compile-input");
        Directory.CreateDirectory(Path.Combine(_root, "Layouts"));
        Directory.CreateDirectory(Path.Combine(_root, "Translations"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void Write(string relative, string content) => File.WriteAllText(Path.Combine(_root, relative), content);

    private void WriteBundle()
    {
        Write("app.json", """
            { "id": "c5087000-0000-4a11-9111-0000000000a1", "name": "CompileInput", "publisher": "Test",
              "version": "1.0.0.0", "idRanges": [ { "from": 90500, "to": 90519 } ], "runtime": "14.0",
              "features": [ "TranslationFile" ] }
            """);
        Write("Tab.al", """
            table 90500 "CI Tab"
            {
                fields { field(1; PK; Integer) { } }
                keys { key(PK; PK) { Clustered = true; } }
            }
            """);
        Write("ReportA.al", Report(90501, "CI Report A", "RDLC", "Layouts/A.rdlc"));
        Write("ReportB.al", Report(90502, "CI Report B", "Word", "Layouts/B.docx"));
        Write("Host.al", """
            codeunit 90503 "CI Host"
            {
                procedure GetValue(): Integer
                begin
                    exit(1);
                end;
            }
            """);
        Write("Addin.al", """
            controladdin "CI Addin"
            {
                Scripts = 'js/a.js';
                StartupScript = 'js/a.js';
            }
            """);
        Directory.CreateDirectory(Path.Combine(_root, "js"));
        Write("js/a.js", "// v1");
        Write("Layouts/A.rdlc", "<Report>v1</Report>");
        Write("Layouts/B.docx", "docx v1");
        Directory.CreateDirectory(Path.Combine(_root, "Sub"));
        Write("Sub/ReportC.al", Report(90504, "CI Report C", "RDLC", "./C.rdlc"));
        Write("Sub/C.rdlc", "<Report>C v1</Report>");
        Write("Translations/CompileInput.da-DK.xlf", "<xliff version=\"1.2\"/>");
        Write("notes.txt", "never read by the compile");
    }

    private static string Report(int id, string name, string type, string file) => $$"""
        report {{id}} "{{name}}"
        {
            DefaultRenderingLayout = L1;
            dataset { dataitem(T; "CI Tab") { column(PK; PK) { } } }
            rendering
            {
                layout(L1)
                {
                    Type = {{type}};
                    LayoutFile = '{{file}}';
                }
            }
        }
        """;

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

    [SkippableFact]
    public void EditedRdlcLayout_IsAChangeToTheReportThatNamesIt_AndToNoOtherObject()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        Write("Layouts/A.rdlc", "<Report>v2</Report>");

        var output = Cycle(compiler, out var reason, out var changed);
        Assert.True(output != null, $"a layout edit must stay on the fast path: {reason}");
        Assert.Equal(new[] { new AffectedObjectId("Report", 90501, "CI Report A") }, changed);
    }

    [SkippableFact]
    public void EditedWordLayout_IsAChangeToItsOwnReport_NotTheOneWithTheRdlc()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        Write("Layouts/B.docx", "docx v2");

        var output = Cycle(compiler, out var reason, out var changed);
        Assert.True(output != null, $"a layout edit must stay on the fast path: {reason}");
        Assert.Equal(new[] { new AffectedObjectId("Report", 90502, "CI Report B") }, changed);
    }

    [SkippableFact]
    public void EditedFileRelativeLayout_IsAChangeToTheReportInThatDirectory()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        Write("Sub/C.rdlc", "<Report>C v2</Report>");

        var output = Cycle(compiler, out var reason, out var changed);
        Assert.True(output != null, $"a layout edit must stay on the fast path: {reason}");
        Assert.Equal(new[] { new AffectedObjectId("Report", 90504, "CI Report C") }, changed);
    }

    // The delta compile re-reads only what it recompiled, so its own reads have to reach the next baseline.
    [SkippableFact]
    public void LayoutOfAReportAddedByAnIncrementalCycle_IsAnInputOfTheNextCycle()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        Write("Layouts/D.rdlc", "<Report>D v1</Report>");
        Write("ReportD.al", Report(90505, "CI Report D", "RDLC", "Layouts/D.rdlc"));
        var added = Cycle(compiler, out var addReason, out _);
        Assert.True(added != null, $"an added report must stay on the fast path: {addReason}");

        Write("Layouts/D.rdlc", "<Report>D v2</Report>");

        var output = Cycle(compiler, out var reason, out var changed);
        Assert.True(output != null, $"a layout edit must stay on the fast path: {reason}");
        Assert.Equal(new[] { new AffectedObjectId("Report", 90505, "CI Report D") }, changed);
    }

    [SkippableFact]
    public void EditedControlAddInResource_IsAChangeToTheControlAddIn()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        Write("js/a.js", "// v2");

        var output = Cycle(compiler, out var reason, out var changed);
        Assert.True(output != null, $"a resource edit must stay on the fast path: {reason}");
        Assert.Equal(new[] { new AffectedObjectId("ControlAddIn", null, "CI Addin") }, changed);
    }

    [SkippableFact]
    public void DeletedLayout_IsNotReplayedAsIfItStillExisted()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        File.Delete(Path.Combine(_root, "Layouts", "A.rdlc"));

        // A cold compile of this tree raises AL1081; the cycle must not hand back last cycle's output.
        var cold = new BcCompiler().Emit(new[] { _root }, Module + "Cold", appRootDir: _root);
        Assert.Contains(cold.Diagnostics, d => d.Contains("AL1081", StringComparison.Ordinal));

        var output = Cycle(compiler, out var reason, out var changed);
        Assert.Null(output);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [SkippableFact]
    public void EditedTranslationFile_NamesNoObject_SoTheCycleFallsBackAndSaysWhy()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        Write("Translations/CompileInput.da-DK.xlf", "<xliff version=\"1.2\"><!-- v2 --></xliff>");

        var output = Cycle(compiler, out var reason, out _);
        Assert.Null(output);
        Assert.Contains("CompileInput.da-DK.xlf", reason, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void EditedFileTheCompileNeverReads_StaysAZeroWorkReplay()
    {
        Ready();
        WriteBundle();
        var compiler = new BcCompiler();
        var baseline = compiler.Emit(new[] { _root }, Module, appRootDir: _root, trackIncrementalBaseline: true);

        Write("notes.txt", "edited, and still never read by the compile");

        var output = Cycle(compiler, out var reason, out var changed);
        Assert.True(output != null, reason);
        Assert.Same(baseline, output);
        Assert.Empty(changed!);
    }

    [SkippableFact]
    public void PeekChangedObjects_AndScopes_SeeAnEditedLayout()
    {
        Ready();
        WriteBundle();
        var compiler = Baseline();

        Write("Layouts/A.rdlc", "<Report>v3</Report>");

        var objects = compiler.PeekChangedObjects(new[] { _root }, Module, _root);
        Assert.Equal(new[] { new AffectedObjectId("Report", 90501, "CI Report A") }, objects);

        var scopes = compiler.PeekChangedScopes(new[] { _root }, Module, _root);
        var scope = Assert.Single(scopes!);
        Assert.Equal(new AffectedObjectId("Report", 90501, "CI Report A"), scope.Object);
        Assert.Null(scope.ScopeName);
    }
}
