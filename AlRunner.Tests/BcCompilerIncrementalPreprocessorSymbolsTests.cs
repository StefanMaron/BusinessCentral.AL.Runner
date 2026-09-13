// BcCompilerIncrementalPreprocessorSymbolsTests — issue #4064.
//
// RUNNER-MECHANISM test. The one-shot Emit parses with CLEANSCHEMA1..25, the --define symbols and
// the app.json `preprocessorSymbols` (#1943). The --watch/--server incremental path
// (TryEmitIncremental) parsed with CLEANSCHEMA1..25 only, so after a baseline the fast path
// compiled the other `#if` branch of an edited file than a cold build of the same tree.
//
// Not a corpus test: which symbols the runner hands its own compiler on its fast path is a
// property of the runner, and a corpus test is always a cold compile.
using System.Reflection;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalPreprocessorSymbolsTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalPreprocessorSymbolsTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-incremental-preprocessor-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteManifest(string symbolsJson) =>
        File.WriteAllText(Path.Combine(_root, "app.json"), $$"""
            { "id": "d3c4b5a6-f7e8-4a91-8b02-d4e5f6071829", "name": "IncrPreproc", "publisher": "Test",
              "version": "1.0.0.0", "idRanges": [ { "from": 90980, "to": 90989 } ], "runtime": "14.0",
              "preprocessorSymbols": {{symbolsJson}} }
            """);

    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private static string Probe(string suffix, string symbol) => $$"""
        codeunit 90980 "Incr Preproc Probe"
        {
            procedure GetValue(): Text
            begin
        #if {{symbol}}
                exit('branch-defined-{{suffix}}');
        #else
                exit('branch-undefined-{{suffix}}');
        #endif
            end;
        }
        """;

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    private static string ProbeCode(BcEmitOutput output) => ByName(output)["Incr Preproc Probe"];

    // --define symbols are process-wide static state; restore whatever was there.
    private static readonly FieldInfo ExtraSymbolsField = typeof(BcCompiler).GetField(
        "_extraPreprocessorSymbols", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static T WithDefines<T>(IReadOnlyList<string>? symbols, Func<T> body)
    {
        var saved = ExtraSymbolsField.GetValue(null);
        ExtraSymbolsField.SetValue(null, symbols);
        try { return body(); }
        finally { ExtraSymbolsField.SetValue(null, saved); }
    }

    [SkippableFact]
    public void ManifestSymbol_FastPathCompilesTheSameBranchAsAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WithDefines<object?>(null, () =>
        {
            WriteManifest("""["INCRFOO"]""");
            WriteAl("Probe.al", Probe("v1", "INCRFOO"));
            var compiler = new BcCompiler();
            var baseline = compiler.Emit(new[] { _root }, "IncrPreprocManifest", appRootDir: _root, trackIncrementalBaseline: true);
            Assert.Empty(baseline.Diagnostics);
            Assert.Contains("branch-defined-v1", ProbeCode(baseline), StringComparison.Ordinal);

            WriteAl("Probe.al", Probe("v2", "INCRFOO"));
            var incremental = compiler.TryEmitIncremental(new[] { _root }, "IncrPreprocManifest", appRootDir: _root, out var reason);
            Assert.True(incremental != null, $"expected the fast path; fell back: {reason}");

            var cold = ProbeCode(new BcCompiler().Emit(new[] { _root }, "IncrPreprocManifestCold", appRootDir: _root));
            Assert.Contains("branch-defined-v2", cold, StringComparison.Ordinal);
            Assert.Contains("branch-defined-v2", ProbeCode(incremental!), StringComparison.Ordinal);
            Assert.Equal(cold, ProbeCode(incremental!));
            return null;
        });
    }

    [SkippableFact]
    public void DefineSymbol_FastPathCompilesTheSameBranchAsAColdBuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WithDefines<object?>(new List<string> { "INCRBAR" }, () =>
        {
            WriteManifest("[]");
            WriteAl("Probe.al", Probe("v1", "INCRBAR"));
            var compiler = new BcCompiler();
            var baseline = compiler.Emit(new[] { _root }, "IncrPreprocDefine", appRootDir: _root, trackIncrementalBaseline: true);
            Assert.Empty(baseline.Diagnostics);
            Assert.Contains("branch-defined-v1", ProbeCode(baseline), StringComparison.Ordinal);

            WriteAl("Probe.al", Probe("v2", "INCRBAR"));
            var incremental = compiler.TryEmitIncremental(new[] { _root }, "IncrPreprocDefine", appRootDir: _root, out var reason);
            Assert.True(incremental != null, $"expected the fast path; fell back: {reason}");

            var cold = ProbeCode(new BcCompiler().Emit(new[] { _root }, "IncrPreprocDefineCold", appRootDir: _root));
            Assert.Contains("branch-defined-v2", cold, StringComparison.Ordinal);
            Assert.Contains("branch-defined-v2", ProbeCode(incremental!), StringComparison.Ordinal);
            Assert.Equal(cold, ProbeCode(incremental!));
            return null;
        });
    }

    [SkippableFact]
    public void UndeclaredSymbol_FastPathStillCompilesTheElseBranch()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Negative control: a fast path that defined every symbol would satisfy the two tests above.
        WithDefines<object?>(null, () =>
        {
            WriteManifest("""["INCRFOO"]""");
            WriteAl("Probe.al", Probe("v1", "INCROTHER"));
            var compiler = new BcCompiler();
            Assert.Empty(compiler.Emit(new[] { _root }, "IncrPreprocUndeclared", appRootDir: _root, trackIncrementalBaseline: true).Diagnostics);

            WriteAl("Probe.al", Probe("v2", "INCROTHER"));
            var incremental = compiler.TryEmitIncremental(new[] { _root }, "IncrPreprocUndeclared", appRootDir: _root, out var reason);
            Assert.True(incremental != null, $"expected the fast path; fell back: {reason}");
            Assert.Contains("branch-undefined-v2", ProbeCode(incremental!), StringComparison.Ordinal);
            return null;
        });
    }

    [SkippableFact]
    public void ManifestSymbolsEditedMidSession_ForcesAFullRebuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WithDefines<object?>(null, () =>
        {
            WriteManifest("""["INCRFOO"]""");
            WriteAl("Probe.al", Probe("v1", "INCRFOO"));
            var compiler = new BcCompiler();
            Assert.Empty(compiler.Emit(new[] { _root }, "IncrPreprocManifestEdit", appRootDir: _root, trackIncrementalBaseline: true).Diagnostics);

            // Every unchanged file in the baseline was parsed with INCRFOO defined; reusing them
            // under a symbol set without it would mix two branch choices in one compilation.
            WriteManifest("[]");
            WriteAl("Probe.al", Probe("v2", "INCRFOO"));
            var incremental = compiler.TryEmitIncremental(new[] { _root }, "IncrPreprocManifestEdit", appRootDir: _root, out var reason);
            Assert.True(incremental == null, "an app.json preprocessorSymbols edit was taken on the incremental fast path");
            Assert.Contains("preprocessor symbols", reason, StringComparison.Ordinal);
            return null;
        });
    }

    [SkippableFact]
    public void DefineSymbolsChangedAfterBaseline_ForcesAFullRebuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteManifest("[]");
        WriteAl("Probe.al", Probe("v1", "INCRBAR"));
        var compiler = new BcCompiler();
        WithDefines<object?>(new List<string> { "INCRBAR" }, () =>
        {
            Assert.Empty(compiler.Emit(new[] { _root }, "IncrPreprocDefineEdit", appRootDir: _root, trackIncrementalBaseline: true).Diagnostics);
            return null;
        });

        WriteAl("Probe.al", Probe("v2", "INCRBAR"));
        string? reason = null;
        var incremental = WithDefines(null, () =>
            compiler.TryEmitIncremental(new[] { _root }, "IncrPreprocDefineEdit", appRootDir: _root, out reason));
        Assert.True(incremental == null, "a changed --define symbol set was taken on the incremental fast path");
        Assert.Contains("preprocessor symbols", reason, StringComparison.Ordinal);
    }
}
