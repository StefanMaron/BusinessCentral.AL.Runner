// BcCompilerIncrementalCompilationTargetTests — issue #2316.
//
// RUNNER-MECHANISM test. The one-shot Emit hands BC's compiler the app.json `target` (#2725,
// pinned end to end by DotNetCompilationTargetScopeTests), so a Cloud app calling an
// OnPrem-scoped method gets BC's own AL0296. The --watch/--server incremental path
// (TryEmitIncremental) built its CompilationOptions with a hardcoded OnPrem target instead, so
// the same edit made during a watch session compiled green on the fast path, and every
// target-dependent literal it emitted disagreed with a cold build of the same tree.
//
// Not a corpus test: the diagnostic is BC's compiler applying its own rule, and a bundle that
// deliberately fails to compile cannot express a passing corpus test.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalCompilationTargetTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalCompilationTargetTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-target-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteManifest(string target) =>
        File.WriteAllText(Path.Combine(_root, "app.json"), $$"""
            { "id": "c2b3c4d5-e6f7-4a81-9b02-c3d4e5f60718", "name": "IncrTarget", "publisher": "Test",
              "version": "1.0.0.0", "idRanges": [ { "from": 90960, "to": 90969 } ], "runtime": "14.0",
              "target": "{{target}}" }
            """);

    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private const string CleanProbe = """
        codeunit 90960 "Incr Target Probe"
        {
            procedure GetValue(): Text
            begin
                exit('clean');
            end;
        }
        """;

    // Sid() is Scope('OnPrem'): BC's compiler refuses it for a Cloud target with AL0296.
    private const string SidProbe = """
        codeunit 90960 "Incr Target Probe"
        {
            procedure GetValue(): Text
            begin
                exit(Sid('SOMEUSER'));
            end;
        }
        """;

    // RegisterTableConnection compiles for both targets, and the compiler passes the target into
    // the emitted ALRegisterTableConnection call as a literal.
    private const string TableConnectionProbe = """
        codeunit 90960 "Incr Target Probe"
        {
            procedure GetValue(): Text
            begin
                Database.RegisterTableConnection(TableConnectionType::ExternalSQL, 'x', 'y');
                exit('connected');
            end;
        }
        """;

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    [SkippableFact]
    public void CloudTarget_EditAddingAnOnPremScopedCall_IsRefusedWithAL0296_NotTakenOnTheFastPath()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteManifest("Cloud");
        WriteAl("Probe.al", CleanProbe);
        var compiler = new BcCompiler();
        var baseline = compiler.Emit(new[] { _root }, "IncrTargetCloud", appRootDir: _root, trackIncrementalBaseline: true);
        Assert.Empty(baseline.Diagnostics);

        WriteAl("Probe.al", SidProbe);
        var incremental = compiler.TryEmitIncremental(new[] { _root }, "IncrTargetCloud", appRootDir: _root, out var reason);

        Assert.True(incremental == null,
            "the incremental path compiled a Cloud app's call to the OnPrem-scoped Sid() without a diagnostic; "
            + "a cold compile of the same tree refuses it with AL0296.");
        // The fallback reason carries the compiler's message text, not its diagnostic id.
        Assert.Contains("has scope 'OnPrem' and cannot be used for 'Cloud' development", reason, StringComparison.Ordinal);

        // The full compile the caller falls back to refuses it too.
        var full = compiler.Emit(new[] { _root }, "IncrTargetCloud", appRootDir: _root, trackIncrementalBaseline: true);
        Assert.Contains(full.Diagnostics, d => d.Contains("AL0296", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void OnPremTarget_SameEdit_StaysOnTheFastPath()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Positive control: without it, a fast path that refused Sid() for every target would
        // satisfy the Cloud test above.
        WriteManifest("OnPrem");
        WriteAl("Probe.al", CleanProbe);
        var compiler = new BcCompiler();
        var baseline = compiler.Emit(new[] { _root }, "IncrTargetOnPrem", appRootDir: _root, trackIncrementalBaseline: true);
        Assert.Empty(baseline.Diagnostics);

        WriteAl("Probe.al", SidProbe);
        var incremental = compiler.TryEmitIncremental(new[] { _root }, "IncrTargetOnPrem", appRootDir: _root, out var reason);

        Assert.True(incremental != null, $"expected the fast path for an OnPrem app; fell back: {reason}");
        Assert.Contains("Incr Target Probe", ByName(incremental!).Keys);
    }

    [SkippableFact]
    public void CloudTarget_FastPathOutput_CarriesTheSameTargetLiteralAsAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteManifest("Cloud");
        WriteAl("Probe.al", CleanProbe);
        var compiler = new BcCompiler();
        Assert.Empty(compiler.Emit(new[] { _root }, "IncrTargetLiteral", appRootDir: _root, trackIncrementalBaseline: true).Diagnostics);

        WriteAl("Probe.al", TableConnectionProbe);
        var incremental = compiler.TryEmitIncremental(new[] { _root }, "IncrTargetLiteral", appRootDir: _root, out var reason);
        Assert.True(incremental != null, $"expected the fast path; fell back: {reason}");

        var coldCloud = ByName(new BcCompiler().Emit(new[] { _root }, "IncrTargetLiteralColdCloud", appRootDir: _root))["Incr Target Probe"];
        WriteManifest("OnPrem");
        var coldOnPrem = ByName(new BcCompiler().Emit(new[] { _root }, "IncrTargetLiteralColdOnPrem", appRootDir: _root))["Incr Target Probe"];

        // Fixture guard: the probe really emits something target-dependent.
        Assert.NotEqual(coldCloud, coldOnPrem);
        Assert.Equal(coldCloud, ByName(incremental!)["Incr Target Probe"]);
    }
}
