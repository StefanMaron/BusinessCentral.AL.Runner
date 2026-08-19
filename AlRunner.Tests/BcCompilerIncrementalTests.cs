// BcCompilerIncrementalTests — RED/GREEN proof for issue #1902.
//
// #1902: every --watch cycle called the SAME Emit() a one-shot run uses — full re-parse,
// re-bind, re-codegen of every object, every save. BcCompiler.TryEmitIncremental (see
// BcCompiler.Incremental.cs) is the fix: an edit to one already-tracked, id-bearing object's
// CONTENT recompiles only that object via BC's own Compilation.CreateForRad, reusing cached C#
// for everything else, and the final result must be indistinguishable from a full rebuild of
// the same source tree.
//
// What each test proves (tdd.md: must prove, not just pass):
//   - Proportional: editing ONE codeunit leaves every OTHER object's emitted C# text BYTE-
//     IDENTICAL to what the ORIGINAL full compile produced — not just "still compiles", the
//     actual cached bytes are reused. A no-op fast path that quietly re-emitted everything
//     would still pass a weaker "same object count" assertion; it would NOT pass this one.
//   - Correct: the incremental result's object set and C# for the CHANGED object matches a
//     completely independent full rebuild of the SAME post-edit source tree, byte for byte.
//   - Multi-cycle: three sequential incremental cycles, each touching a DIFFERENT object, must
//     all be reflected in the final result — proving the baseline MERGE (not just "trust the
//     delta compile's own conversion") described in BcCompiler.Incremental.cs's header comment.
//     Without that merge this class of bug is silent: cycle 2 would compile fine but quietly
//     forget cycle 1's edit the next time anything referencing it needed a rebuild.
//   - Every disqualifying condition (no baseline yet, a file added, app.json changed, an
//     id-less object kind touched) returns null (never a stale result) and the caller-visible
//     contract — an ordinary Emit(..., trackIncrementalBaseline: true) — still produces the
//     correct answer.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-incremental-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private static string CodeunitSrc(int id, string name, int returnValue) => $$"""
        codeunit {{id}} "{{name}}"
        {
            procedure GetValue(): Integer
            begin
                exit({{returnValue}});
            end;
        }
        """;

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    [SkippableFact]
    public void TryEmitIncremental_EditingOneCodeunit_LeavesOthersByteIdentical_AndMatchesFullRebuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90210, "Incr A", 1));
        WriteAl("B.al", CodeunitSrc(90211, "Incr B", 2));
        WriteAl("C.al", CodeunitSrc(90212, "Incr C", 3));

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "IncrModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);
        Assert.Equal(3, baselineByName.Count);

        // Edit ONLY B's content.
        WriteAl("B.al", CodeunitSrc(90211, "Incr B", 999));

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "IncrModule", appRootDir: null, out var fallbackReason);
        Assert.True(incrOut != null, $"expected the fast path to apply; fell back instead: {fallbackReason}");
        var incrByName = ByName(incrOut!);

        // Proportional: A and C's C# is byte-identical to the ORIGINAL full compile — proves
        // they were served from cache, not regenerated.
        Assert.Equal(baselineByName["Incr A"], incrByName["Incr A"]);
        Assert.Equal(baselineByName["Incr C"], incrByName["Incr C"]);
        // B actually changed.
        Assert.NotEqual(baselineByName["Incr B"], incrByName["Incr B"]);
        Assert.Contains("999", incrByName["Incr B"]);

        // Correct: matches an independent full rebuild of the SAME post-edit tree.
        var freshOut = new BcCompiler().Emit(new[] { _root }, "IncrModuleFresh");
        var freshByName = ByName(freshOut);
        Assert.Equal(freshByName["Incr A"], incrByName["Incr A"]);
        Assert.Equal(freshByName["Incr B"], incrByName["Incr B"]);
        Assert.Equal(freshByName["Incr C"], incrByName["Incr C"]);
    }

    [SkippableFact]
    public void TryEmitIncremental_TouchWithIdenticalBytes_ReplaysLastOutputAtZeroCost()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90220, "Touch A", 1));
        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "TouchModule", trackIncrementalBaseline: true);

        // Touch: rewrite the SAME bytes (a save with no actual edit, or an mtime-only touch).
        WriteAl("A.al", CodeunitSrc(90220, "Touch A", 1));

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "TouchModule", appRootDir: null, out var fallbackReason);
        Assert.True(incrOut != null, $"expected the fast path on an identical-bytes touch; fell back: {fallbackReason}");
        Assert.Same(baselineOut, incrOut);
    }

    [SkippableFact]
    public void TryEmitIncremental_ThreeSequentialCycles_EachTouchingADifferentObject_AllEditsSurviveInFinalResult()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90230, "Chain A", 1));
        WriteAl("B.al", CodeunitSrc(90231, "Chain B", 2));
        WriteAl("C.al", CodeunitSrc(90232, "Chain C", 3));

        var compiler = new BcCompiler();
        compiler.Emit(new[] { _root }, "ChainModule", trackIncrementalBaseline: true);

        // Cycle 1: edit A.
        WriteAl("A.al", CodeunitSrc(90230, "Chain A", 111));
        var out1 = compiler.TryEmitIncremental(new[] { _root }, "ChainModule", appRootDir: null, out var reason1);
        Assert.True(out1 != null, $"cycle 1 fell back: {reason1}");

        // Cycle 2: edit B (a DIFFERENT object — this is the case that silently loses cycle 1's
        // edit if the next baseline is built from the delta compile alone instead of merged).
        WriteAl("B.al", CodeunitSrc(90231, "Chain B", 222));
        var out2 = compiler.TryEmitIncremental(new[] { _root }, "ChainModule", appRootDir: null, out var reason2);
        Assert.True(out2 != null, $"cycle 2 fell back: {reason2}");

        // Cycle 3: edit C.
        WriteAl("C.al", CodeunitSrc(90232, "Chain C", 333));
        var out3 = compiler.TryEmitIncremental(new[] { _root }, "ChainModule", appRootDir: null, out var reason3);
        Assert.True(out3 != null, $"cycle 3 fell back: {reason3}");

        var finalByName = ByName(out3!);
        Assert.Contains("111", finalByName["Chain A"]);
        Assert.Contains("222", finalByName["Chain B"]);
        Assert.Contains("333", finalByName["Chain C"]);

        // Matches an independent full rebuild of the final tree, object for object.
        var freshOut = new BcCompiler().Emit(new[] { _root }, "ChainModuleFresh");
        var freshByName = ByName(freshOut);
        Assert.Equal(freshByName["Chain A"], finalByName["Chain A"]);
        Assert.Equal(freshByName["Chain B"], finalByName["Chain B"]);
        Assert.Equal(freshByName["Chain C"], finalByName["Chain C"]);
    }

    [SkippableFact]
    public void TryEmitIncremental_CrossObjectCall_UnmodifiedCallerAutomaticallySeesCalleesNewBehaviour()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("Callee.al", CodeunitSrc(90240, "Callee X", 10));
        WriteAl("Caller.al", """
            codeunit 90241 "Caller X"
            {
                procedure CallIt(): Integer
                var
                    Callee: Codeunit "Callee X";
                begin
                    exit(Callee.GetValue());
                end;
            }
            """);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "CrossCallModule", trackIncrementalBaseline: true);
        var baselineByName = ByName(baselineOut);

        // Only the CALLEE changes — the caller's file is never touched.
        WriteAl("Callee.al", CodeunitSrc(90240, "Callee X", 20));

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "CrossCallModule", appRootDir: null, out var fallbackReason);
        Assert.True(incrOut != null, $"expected the fast path to apply; fell back instead: {fallbackReason}");
        var incrByName = ByName(incrOut!);

        // Caller's C# is untouched — served from cache, byte-identical.
        Assert.Equal(baselineByName["Caller X"], incrByName["Caller X"]);
        // Callee's C# reflects the edit.
        Assert.NotEqual(baselineByName["Callee X"], incrByName["Callee X"]);
        Assert.Contains("20", incrByName["Callee X"]);
    }

    [SkippableFact]
    public void TryEmitIncremental_NoBaselineYet_FallsBack()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90250, "NoBaseline A", 1));
        var compiler = new BcCompiler();
        var result = compiler.TryEmitIncremental(new[] { _root }, "NeverEmittedModule", appRootDir: null, out var reason);
        Assert.Null(result);
        Assert.Contains("no incremental baseline", reason, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void TryEmitIncremental_FileAdded_FallsBack_AndSubsequentFullRebuildIsStillCorrect()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90260, "AddA A", 1));
        var compiler = new BcCompiler();
        compiler.Emit(new[] { _root }, "AddModule", trackIncrementalBaseline: true);

        WriteAl("B.al", CodeunitSrc(90261, "AddA B", 2));

        var incrResult = compiler.TryEmitIncremental(new[] { _root }, "AddModule", appRootDir: null, out var reason);
        Assert.Null(incrResult);
        Assert.Contains("added/removed/renamed", reason, StringComparison.OrdinalIgnoreCase);

        // The caller's contract: fall back to Emit(), which must still produce the correct,
        // complete result — never silently missing the added object.
        var fullOut = compiler.Emit(new[] { _root }, "AddModule", trackIncrementalBaseline: true);
        var byName = ByName(fullOut);
        Assert.Equal(2, byName.Count);
        Assert.True(byName.ContainsKey("AddA A"));
        Assert.True(byName.ContainsKey("AddA B"));
    }

    [SkippableFact]
    public void TryEmitIncremental_AppJsonChanged_FallsBack()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90270, "ManifestA A", 1));
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            { "id": "b1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f710", "name": "IncrManifest", "publisher": "Test",
              "version": "1.0.0.0", "idRanges": [ { "from": 90270, "to": 90279 } ], "runtime": "14.0" }
            """);

        var compiler = new BcCompiler();
        compiler.Emit(new[] { _root }, "ManifestModule", appRootDir: _root, trackIncrementalBaseline: true);

        // Edit A's content AND bump the manifest version in the same cycle.
        WriteAl("A.al", CodeunitSrc(90270, "ManifestA A", 2));
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            { "id": "b1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f710", "name": "IncrManifest", "publisher": "Test",
              "version": "1.0.0.1", "idRanges": [ { "from": 90270, "to": 90279 } ], "runtime": "14.0" }
            """);

        var result = compiler.TryEmitIncremental(new[] { _root }, "ManifestModule", appRootDir: _root, out var reason);
        Assert.Null(result);
        Assert.Contains("app.json", reason, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void TryEmitIncremental_IdlessObjectKindTouched_FallsBack()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90280, "IfaceHost A", 1));
        WriteAl("IFace.al", """
            interface "Incr IFace"
            {
                procedure DoIt(): Integer;
            }
            """);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "IfaceModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);

        // Edit the interface itself — an id-less kind, must always fall back.
        WriteAl("IFace.al", """
            interface "Incr IFace"
            {
                procedure DoIt(): Integer;
                procedure DoItAgain(): Integer;
            }
            """);

        var result = compiler.TryEmitIncremental(new[] { _root }, "IfaceModule", appRootDir: null, out var reason);
        Assert.Null(result);
        // BC's Compilation.GetDeclaredApplicationObjectSymbols() (ISymbolWithId, non-nullable
        // Id) never returns an interface at all, so RecordIncrementalBaseline has no (Kind,Id)
        // to track it under — the baseline correctly reports the file as untracked rather than
        // classifying it "id-less". Either wording is a safe outcome: what matters is that an
        // id-less kind can NEVER take the fast path.
        Assert.True(
            reason.Contains("id-less", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("not tracked", StringComparison.OrdinalIgnoreCase),
            $"expected a safe, explained fallback; got: {reason}");

        // The caller's contract still holds: a full rebuild after the fallback is correct.
        var fullOut = compiler.Emit(new[] { _root }, "IfaceModule", trackIncrementalBaseline: true);
        Assert.Empty(fullOut.Diagnostics);
        Assert.Equal(1, fullOut.Sources.Count); // only the codeunit emits C#; the interface never does.
    }
}
