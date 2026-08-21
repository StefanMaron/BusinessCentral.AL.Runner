// BcCompilerIncrementalTests — RED/GREEN proof for issue #1902.
//
// #1902: every --watch cycle called the SAME Emit() a one-shot run uses — full re-parse,
// re-bind, re-codegen of every object, every save. BcCompiler.TryEmitIncremental (see
// BcCompiler.Incremental.cs) is the fix: one edit costs work proportional to that edit, for
// every object kind and every file operation (add/edit/rename/delete/touch-with-identical-
// bytes), via BC's own Compilation.CreateForRad, and the final result must be indistinguishable
// from a full rebuild of the same source tree.
//
// What each test proves (tdd.md: must prove, not just pass):
//   - Proportional: editing/adding/removing ONE object leaves every OTHER object's emitted C#
//     text BYTE-IDENTICAL to what the ORIGINAL full compile produced — not just "still
//     compiles", the actual cached bytes are reused. A no-op fast path that quietly re-emitted
//     everything would still pass a weaker "same object count" assertion; it would NOT pass
//     this one.
//   - Correct: the incremental result's object set and C# matches a completely independent full
//     rebuild of the SAME post-edit source tree, byte for byte.
//   - Multi-cycle: three sequential incremental cycles, each touching a DIFFERENT object, must
//     all be reflected in the final result — proving the baseline MERGE (not just "trust the
//     delta compile's own conversion") described in BcCompiler.Incremental.cs's header comment.
//     Without that merge this class of bug is silent: cycle 2 would compile fine but quietly
//     forget cycle 1's edit the next time anything referencing it needed a rebuild.
//   - Every genuinely disqualifying condition (no baseline yet, app.json changed, a duplicate
//     declaration only the compiler can adjudicate) returns null (never a stale result) and the
//     caller-visible contract — an ordinary Emit(..., trackIncrementalBaseline: true) — still
//     produces the correct answer.
//   - Add/remove/rename (of the file, of the AL object's own name, or both at once) and the six
//     id-less object kinds (interface/controladdin/profile/pagecustomization/profileextension/
//     entitlement) ALL take the fast path now — see BcCompiler.Incremental.cs's header comment
//     for why a rename is not a distinct case from BC's own point of view, and why entitlement
//     needs a different mechanism (no ModuleDefinition representation at all).
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
    public void TryEmitIncremental_FileAdded_TakesFastPath_LeavesExistingByteIdentical_AndMatchesFullRebuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90260, "AddA A", 1));
        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "AddModule", trackIncrementalBaseline: true);
        var baselineByName = ByName(baselineOut);

        // Add a brand new file declaring a NEW object — never touch A.
        WriteAl("B.al", CodeunitSrc(90261, "AddA B", 2));

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "AddModule", appRootDir: null, out var reason);
        Assert.True(incrOut != null, $"expected the fast path to apply to a genuine add; fell back instead: {reason}");
        var incrByName = ByName(incrOut!);
        Assert.Equal(2, incrByName.Count);

        // Proportional: A's C# is byte-identical to the ORIGINAL full compile — untouched.
        Assert.Equal(baselineByName["AddA A"], incrByName["AddA A"]);
        Assert.Contains("2", incrByName["AddA B"]);

        // Correct: matches an independent full rebuild of the same post-add tree.
        var freshOut = new BcCompiler().Emit(new[] { _root }, "AddModuleFresh");
        var freshByName = ByName(freshOut);
        Assert.Equal(freshByName["AddA A"], incrByName["AddA A"]);
        Assert.Equal(freshByName["AddA B"], incrByName["AddA B"]);

        // The baseline this fast cycle recorded is itself usable for a THIRD cycle — proves the
        // add was folded into the merged ModuleDefinition, not just the returned C#.
        WriteAl("A.al", CodeunitSrc(90260, "AddA A", 999));
        var out3 = compiler.TryEmitIncremental(new[] { _root }, "AddModule", appRootDir: null, out var reason3);
        Assert.True(out3 != null, $"cycle 3 fell back: {reason3}");
        var by3 = ByName(out3!);
        Assert.Contains("999", by3["AddA A"]);
        Assert.Equal(incrByName["AddA B"], by3["AddA B"]); // B still untouched two cycles later
    }

    [SkippableFact]
    public void TryEmitIncremental_FileRemoved_TakesFastPath_DropsDeletedObjectAndMatchesFullRebuild()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90262, "RemA A", 1));
        WriteAl("B.al", CodeunitSrc(90263, "RemA B", 2));
        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "RemModule", trackIncrementalBaseline: true);
        var baselineByName = ByName(baselineOut);
        Assert.Equal(2, baselineByName.Count);

        File.Delete(Path.Combine(_root, "B.al"));

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "RemModule", appRootDir: null, out var reason);
        Assert.True(incrOut != null, $"expected the fast path to apply to a genuine delete; fell back instead: {reason}");
        var incrByName = ByName(incrOut!);

        // The deleted object's runtime metadata goes with it — not merely "unchanged", ABSENT.
        Assert.Single(incrByName);
        Assert.False(incrByName.ContainsKey("RemA B"));
        Assert.Equal(baselineByName["RemA A"], incrByName["RemA A"]);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "RemModuleFresh");
        var freshByName = ByName(freshOut);
        Assert.Equal(freshByName.Keys, incrByName.Keys);
        Assert.Equal(freshByName["RemA A"], incrByName["RemA A"]);
    }

    [SkippableFact]
    public void TryEmitIncremental_FileRenamed_SameObjectIdentity_TakesFastPath()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("Old.al", CodeunitSrc(90264, "RenA A", 1));
        WriteAl("Sibling.al", CodeunitSrc(90265, "RenA Sibling", 5));
        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "RenModule", trackIncrementalBaseline: true);
        var baselineByName = ByName(baselineOut);

        // Rename the FILE — same (Kind,Id,Name), just a different path on disk. From BC's own
        // ObjectChangeElement point of view (no path field) this is exactly a content edit.
        File.Move(Path.Combine(_root, "Old.al"), Path.Combine(_root, "New.al"));

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "RenModule", appRootDir: null, out var reason);
        Assert.True(incrOut != null, $"expected a pure file rename to take the fast path; fell back instead: {reason}");
        var incrByName = ByName(incrOut!);
        Assert.Equal(baselineByName["RenA A"], incrByName["RenA A"]);
        Assert.Equal(baselineByName["RenA Sibling"], incrByName["RenA Sibling"]);

        // The rename is correctly tracked under the NEW path — editing it again next cycle must
        // ALSO take the fast path (proves ObjectByPath moved, not just "still compiles once").
        WriteAl("New.al", CodeunitSrc(90264, "RenA A", 111));
        var out2 = compiler.TryEmitIncremental(new[] { _root }, "RenModule", appRootDir: null, out var reason2);
        Assert.True(out2 != null, $"expected the SECOND cycle at the renamed path to take the fast path too; fell back: {reason2}");
        Assert.Contains("111", ByName(out2!)["RenA A"]);
    }

    [SkippableFact]
    public void TryEmitIncremental_ObjectRenamedInPlace_SameFile_NewAlName_TakesFastPath()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90266, "RenB Old Name", 1));
        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "RenObjModule", trackIncrementalBaseline: true);
        Assert.Equal("RenB Old Name", ByName(baselineOut).Keys.Single());

        // Same file, same Id, the AL OBJECT's own name changes — BC's ObjectChangeElement
        // identity for an id-bearing kind is (Kind,Id), Name is not part of it (decompiled and
        // confirmed: NamespaceAgnosticEqualityComparer only falls back to Name when Id is null).
        WriteAl("A.al", CodeunitSrc(90266, "RenB New Name", 1));

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "RenObjModule", appRootDir: null, out var reason);
        Assert.True(incrOut != null, $"expected an in-place AL rename (same Id) to take the fast path; fell back instead: {reason}");
        var incrByName = ByName(incrOut!);
        Assert.Single(incrByName);
        Assert.True(incrByName.ContainsKey("RenB New Name"));
        Assert.False(incrByName.ContainsKey("RenB Old Name"));

        var freshOut = new BcCompiler().Emit(new[] { _root }, "RenObjModuleFresh");
        Assert.Equal(ByName(freshOut)["RenB New Name"], incrByName["RenB New Name"]);
    }

    [SkippableFact]
    public void TryEmitIncremental_GenuinelyNewObject_CollidingIdWithUntouchedBaselineObject_FallsBack()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90267, "DupA A", 1));
        var compiler = new BcCompiler();
        compiler.Emit(new[] { _root }, "DupModule", trackIncrementalBaseline: true);

        // A NEW file claims the SAME Id as an EXISTING, untouched baseline object — a real
        // duplicate declaration only the compiler can adjudicate (the issue's own words), not
        // something to fast-path silently.
        WriteAl("B.al", CodeunitSrc(90267, "DupA B", 2));

        var result = compiler.TryEmitIncremental(new[] { _root }, "DupModule", appRootDir: null, out var reason);
        Assert.Null(result);
        Assert.Contains("duplicate declaration", reason, StringComparison.OrdinalIgnoreCase);

        // The caller's contract still holds: falling back to Emit() surfaces the REAL compiler
        // diagnostic rather than silently picking a winner.
        var fullOut = compiler.Emit(new[] { _root }, "DupModule", trackIncrementalBaseline: true);
        Assert.NotEmpty(fullOut.Diagnostics);
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
    public void TryEmitIncremental_InterfaceEdited_TakesFastPath_HostCodeunitByteIdentical_AndSubsequentCycleStillFast()
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
        var baselineByName = ByName(baselineOut);

        // Edit the interface itself — one of the six id-less kinds. Must now take the fast
        // path: GetDeclaredApplicationObjectSymbols() never returns an interface (confirmed:
        // IApplicationObjectTypeSymbol : ISymbolWithId), so this is classified via
        // SymbolJsonWriter.GetModuleDefinition's Interfaces array instead (see
        // BcCompiler.Incremental.cs's header comment).
        WriteAl("IFace.al", """
            interface "Incr IFace"
            {
                procedure DoIt(): Integer;
                procedure DoItAgain(): Integer;
            }
            """);

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "IfaceModule", appRootDir: null, out var reason);
        Assert.True(incrOut != null, $"expected an interface content edit to take the fast path; fell back instead: {reason}");
        var incrByName = ByName(incrOut!);

        // Proportional: the interface produces no runtime C# at all (pure metadata), and the
        // UNRELATED codeunit's C# is byte-identical to the original full compile.
        Assert.Single(incrByName);
        Assert.Equal(baselineByName["IfaceHost A"], incrByName["IfaceHost A"]);

        // No lingering "already declared" duplicate registration from the interface edit: a
        // SECOND incremental cycle (editing the codeunit this time) must ALSO take the fast
        // path — this is exactly the regression this fix closes (an id-less kind's stale
        // packaged copy silently blocking every later cycle for the same module).
        WriteAl("A.al", CodeunitSrc(90280, "IfaceHost A", 777));
        var out2 = compiler.TryEmitIncremental(new[] { _root }, "IfaceModule", appRootDir: null, out var reason2);
        Assert.True(out2 != null, $"expected the cycle AFTER an interface edit to still take the fast path; fell back: {reason2}");
        Assert.Contains("777", ByName(out2!)["IfaceHost A"]);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "IfaceModuleFresh");
        Assert.Empty(freshOut.Diagnostics);
        Assert.Equal(ByName(freshOut)["IfaceHost A"], ByName(out2!)["IfaceHost A"]);
    }

    [SkippableFact]
    public void TryEmitIncremental_ControlAddInAndProfileAndPageCustomizationAndProfileExtensionEdited_AllTakeFastPath()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("Host.al", CodeunitSrc(90281, "MultiIdless Host", 1));
        WriteAl("Page.al", $$"""
            page 90282 "MultiIdless Page"
            {
                PageType = RoleCenter;
                layout
                {
                    area(Content)
                    {
                    }
                }
            }
            """);
        File.WriteAllText(Path.Combine(_root, "x.js"), "// noop");
        File.WriteAllText(Path.Combine(_root, "y.js"), "// noop");
        WriteAl("Addin.al", """
            controladdin "Incr AddIn"
            {
                Scripts = 'x.js';
                StartupScript = 'x.js';
            }
            """);
        WriteAl("Prof.al", """
            profile "Incr Profile"
            {
                Caption = 'Incr Profile';
                RoleCenter = "MultiIdless Page";
            }
            """);
        WriteAl("Cust.al", """
            pagecustomization "Incr Custom" customizes "MultiIdless Page"
            {
            }
            """);
        WriteAl("ProfExt.al", """
            profileextension "Incr ProfExt" extends "Incr Profile"
            {
            }
            """);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "MultiIdlessModule", appRootDir: _root, trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);

        // Edit each id-less file, one cycle at a time — every one of them must take the fast
        // path, and the untouched host codeunit's C# must stay byte-identical throughout.
        var edits = new (string File, string Content)[]
        {
            ("Addin.al", """
                controladdin "Incr AddIn"
                {
                    Scripts = 'x.js', 'y.js';
                    StartupScript = 'x.js';
                }
                """),
            ("Prof.al", """
                profile "Incr Profile"
                {
                    Caption = 'Incr Profile Renamed';
                    RoleCenter = "MultiIdless Page";
                }
                """),
            ("Cust.al", """
                pagecustomization "Incr Custom" customizes "MultiIdless Page"
                {
                    // touched
                }
                """),
            ("ProfExt.al", """
                profileextension "Incr ProfExt" extends "Incr Profile"
                {
                    // touched
                }
                """),
        };
        foreach (var (file, content) in edits)
        {
            WriteAl(file, content);
            var incrOut = compiler.TryEmitIncremental(new[] { _root }, "MultiIdlessModule", appRootDir: _root, out var reason);
            Assert.True(incrOut != null, $"expected editing '{file}' to take the fast path; fell back instead: {reason}");
            Assert.Equal(baselineByName["MultiIdless Host"], ByName(incrOut!)["MultiIdless Host"]);
        }

        var freshOut = new BcCompiler().Emit(new[] { _root }, "MultiIdlessModuleFresh", appRootDir: _root);
        Assert.Empty(freshOut.Diagnostics);
    }

    [SkippableFact]
    public void TryEmitIncremental_EntitlementTracked_UnchangedAcrossUnrelatedEdit_NoLingeringDuplicateOnSubsequentCycle()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("A.al", CodeunitSrc(90283, "EntHost A", 1));
        // Entitlement has NO SymbolReference.ModuleDefinition representation at all (see
        // BcCompiler.Incremental.cs's header comment) — it can only ever be present in a RAD
        // cycle by being re-included in `syntaxTrees` EVERY cycle, whether it changed or not.
        WriteAl("Ent.al", """
            entitlement "Incr Entitlement"
            {
                Type = PerUserServicePlan;
                Id = 'incr-entitlement';
            }
            """);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "EntModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);

        // Edit the UNRELATED codeunit — the entitlement file itself is never touched.
        WriteAl("A.al", CodeunitSrc(90283, "EntHost A", 42));
        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "EntModule", appRootDir: null, out var reason);
        Assert.True(incrOut != null, $"expected an unrelated edit with an untouched entitlement present to take the fast path; fell back: {reason}");
        var incrByName = ByName(incrOut!);
        Assert.Single(incrByName); // the entitlement itself never emits runtime C#
        Assert.Contains("42", incrByName["EntHost A"]);
        Assert.NotEqual(baselineByName["EntHost A"], incrByName["EntHost A"]);

        // A SECOND cycle proves the always-re-included entitlement never accumulates a stale
        // duplicate registration.
        WriteAl("A.al", CodeunitSrc(90283, "EntHost A", 43));
        var out2 = compiler.TryEmitIncremental(new[] { _root }, "EntModule", appRootDir: null, out var reason2);
        Assert.True(out2 != null, $"expected the cycle AFTER an untouched-entitlement cycle to still take the fast path; fell back: {reason2}");
        Assert.Contains("43", ByName(out2!)["EntHost A"]);

        var freshOut = new BcCompiler().Emit(new[] { _root }, "EntModuleFresh");
        Assert.Empty(freshOut.Diagnostics);
    }
}
