// BcCompilerIncrementalCrossKindSiblingTests — RED/GREEN proof for issue #2479's remaining
// half (same-bundle self-edit inside a namespace-declared app).
//
// #2479 (comment 2026-09-03T14:57:01Z): editing a codeunit inside a real project's TEST app,
// where the touched codeunit references a REPORT declared in a separate .al file of the SAME
// bundle, failed the incremental (RAD) emit with "RAD Emit failed: The name
// '<ReportObjectName>' does not exist in the current context" — even though the report itself
// was never touched. Narrowed down here (bisecting kind and namespace-presence independently, at
// unit scale — no Pageworks/real-project repro needed):
//   - a namespace-declared codeunit referencing an untouched namespace-declared CODEUNIT sibling
//     was already covered (BcCompilerIncrementalNamespaceTests) and works;
//   - the SAME shape but with the untouched sibling of a DIFFERENT id-bearing kind (Table,
//     Report, ...) and BOTH objects namespace-declared reproduces the crash;
//   - neither ingredient alone reproduces it: a flat (non-namespaced) codeunit referencing a
//     flat Table/Report sibling compiles fine (see the existing cross-object-call tests), and a
//     namespaced codeunit referencing a namespaced CODEUNIT sibling also compiles fine.
//
// Root cause (BcCompiler.Incremental.cs's `ExcludeObjectsRecursive`, fixed alongside this file):
// building the self-loader's baseline module cloned every NAMESPACE container by hand (BC's own
// `ModuleDefinition.Clone()` has no `NamespaceDefinition` counterpart), and the per-kind loop
// only ever called `prop.SetValue(clone, ...)` for a kind that actually had something excluded
// THIS cycle. Editing a Codeunit excludes only the Codeunit kind, so every OTHER kind's array
// (Tables, Reports, Pages, ...) was left at its CLR default — null — on every namespace clone.
// BC's binder resolved a reference into that null array far enough to bind SOME symbol, but
// never gave it a real `NavTypeKind`, and `Compilation.Emit` crashed deep inside
// `CodeGenerator.EmitFieldInitializer` with "Unexpected value 'None' of type NavTypeKind" the
// moment codegen tried to emit that variable's scope-class field initializer — the exact crash
// class this file's own header comment already documents for an unresolved DotNet type,
// reproduced here for an unresolved namespace-nested AL sibling instead. A same-kind sibling
// (Codeunit referencing Codeunit) never hit this, because excluding the touched Codeunit already
// forced the Codeunits property to be reassigned on the clone.
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalCrossKindSiblingTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalCrossKindSiblingTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-incremental-crosskind-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private static Dictionary<string, string> ByName(BcEmitOutput output)
        => output.Sources.ToDictionary(s => s.Name, s => s.Code);

    [SkippableFact]
    public void TryEmitIncremental_NamespacedCodeunitReferencesReportSiblingInSeparateFile_EditingOnlyTheCodeunit_TakesFastPath()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("Report.al", """
            namespace Pageworks.Test;

            report 90260 "Incr Report Sibling"
            {
                UsageCategory = None;
                ProcessingOnly = true;

                dataset
                {
                }
            }
            """);
        string CallerSrc(int returnValue) => $$"""
            namespace Pageworks.Test;

            codeunit 90261 "Incr Report Caller"
            {
                procedure Marker(): Integer
                var
                    Rep: Report "Incr Report Sibling";
                begin
                    Rep.UseRequestPage(false);
                    Rep.RunModal();
                    exit({{returnValue}});
                end;
            }
            """;
        WriteAl("Caller.al", CallerSrc(1));

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "CrossKindReportModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);
        Assert.Equal(2, baselineByName.Count);

        // Edit ONLY the codeunit that declares a `Report "Incr Report Sibling"` variable. The
        // report itself is never touched.
        WriteAl("Caller.al", CallerSrc(2));

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "CrossKindReportModule", appRootDir: null, out var fallbackReason);
        Assert.True(incrOut != null,
            $"expected the fast path to apply for a namespaced codeunit-only edit referencing an untouched namespaced report sibling; fell back instead: {fallbackReason}");
        var incrByName = ByName(incrOut!);

        // Caller's C# reflects the edit.
        Assert.Contains("2", incrByName["Incr Report Caller"]);
        // The untouched report's C# is served from cache, byte-identical.
        Assert.Equal(baselineByName["Incr Report Sibling"], incrByName["Incr Report Sibling"]);

        // Correct: matches an independent full rebuild of the same post-edit tree.
        var freshOut = new BcCompiler().Emit(new[] { _root }, "CrossKindReportModuleFresh");
        var freshByName = ByName(freshOut);
        Assert.Equal(freshByName["Incr Report Caller"], incrByName["Incr Report Caller"]);
        Assert.Equal(freshByName["Incr Report Sibling"], incrByName["Incr Report Sibling"]);
    }

    [SkippableFact]
    public void TryEmitIncremental_NamespacedCodeunitReferencesTableSiblingInSeparateFile_EditingOnlyTheCodeunit_TakesFastPath()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        WriteAl("Table.al", """
            namespace Pageworks.Test;

            table 90262 "Incr Table Sibling"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "No."; Code[20]) { }
                }
                keys { key(PK; "No.") { Clustered = true; } }
            }
            """);
        string CallerSrc(int returnValue) => $$"""
            namespace Pageworks.Test;

            codeunit 90263 "Incr Table Caller"
            {
                procedure Marker(): Integer
                var
                    Rec: Record "Incr Table Sibling";
                begin
                    Rec.Init();
                    exit({{returnValue}});
                end;
            }
            """;
        WriteAl("Caller.al", CallerSrc(1));

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "CrossKindTableModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = ByName(baselineOut);
        // Only the codeunit produces runtime C# (the table's shape is metadata, not a
        // separate emitted object here) — assert the codeunit is present rather than a fixed
        // total, matching how BC counts objects vs. emitted sources.
        Assert.Contains("Incr Table Caller", baselineByName.Keys);

        // Edit ONLY the codeunit that declares a `Record "Incr Table Sibling"` variable. The
        // table itself is never touched.
        WriteAl("Caller.al", CallerSrc(2));

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "CrossKindTableModule", appRootDir: null, out var fallbackReason);
        Assert.True(incrOut != null,
            $"expected the fast path to apply for a namespaced codeunit-only edit referencing an untouched namespaced table sibling; fell back instead: {fallbackReason}");
        var incrByName = ByName(incrOut!);

        Assert.Contains("2", incrByName["Incr Table Caller"]);

        // Correct: matches an independent full rebuild of the same post-edit tree.
        var freshOut = new BcCompiler().Emit(new[] { _root }, "CrossKindTableModuleFresh");
        var freshByName = ByName(freshOut);
        Assert.Equal(freshByName["Incr Table Caller"], incrByName["Incr Table Caller"]);
    }
}
