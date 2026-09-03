// BcCompilerIncrementalNamespaceTests — RED/GREEN proof for issue #2507.
//
// #2507: `RecordIncrementalBaseline`'s baseline `ModuleDefinition` for a bundle where (nearly)
// every file declares `namespace X.Y;` — the modern `al new` default — was EMPTY: 0 objects
// across every top-level Codeunits/Tables/Pages/etc. array, even though the SAME compilation's
// `GetDeclaredApplicationObjectSymbols()` correctly reported every declared object. Root cause
// (decompiled `SerializableSymbolModelConverter`): a namespace-declared object is never a direct
// child of the GLOBAL namespace BC's converter starts from — it is nested under
// `ModuleDefinition.Namespaces[...].Codeunits` (recursively, one level per namespace segment).
// `RecordIncrementalBaseline`/`ExcludeObjects`/`MergeModuleDefinition` (BcCompiler.Incremental.cs)
// only ever inspected the top-level arrays, so for ANY app using `namespace` — not a rare edge
// case, the ordinary shape of a modern app — the RAD baseline was silently empty from the moment
// it was first recorded, defeating #1902's incremental-compile fast path for that class of app.
//
// This class proves two things, both required by the issue:
//   1. Mechanism: the recorded baseline's ModuleDefinition, walked RECURSIVELY through
//      `.Namespaces`, contains every declared object of a namespace-heavy, multi-object bundle —
//      not merely "non-zero", the EXACT count, at a scale (30 objects across 3 distinct
//      namespace segments, one nested two levels deep) explicitly chosen because #2486's
//      post-mortem on a related issue found a 1-2 file fixture does not reproduce this class of
//      bug — and the top-level arrays are (correctly, per BC's own converter) near-empty, so a
//      naive "moduleDef.Codeunits.Length > 0" assertion would NOT catch a regression here.
//   2. Observable behaviour: `TryEmitIncremental` on a namespace-declared app with a real
//      cross-namespace object reference (an unmodified caller in one namespace calling a
//      modified callee in ANOTHER namespace — the exact shape that needs the self-referencing
//      RAD loader to correctly represent an untouched sibling) actually takes the fast path,
//      rather than falling back or throwing.
using System.Linq;
using System.Reflection;
using Xunit;
using AlRunner;
using Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerIncrementalNamespaceTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerIncrementalNamespaceTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-incremental-ns-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private static string NsCodeunitSrc(string ns, int id, string name, int returnValue) => $$"""
        namespace {{ns}};

        codeunit {{id}} "{{name}}"
        {
            procedure GetValue(): Integer
            begin
                exit({{returnValue}});
            end;
        }
        """;

    /// <summary>
    /// Reflects out the private baseline `BcCompiler` recorded for `moduleName` (there is no
    /// public accessor — the field is deliberately private implementation detail; see
    /// BcCompiler.Incremental.cs).
    /// </summary>
    private static ModuleDefinition GetRecordedBaselineModuleDef(BcCompiler compiler, string moduleName)
    {
        var field = typeof(BcCompiler).GetField("_radBaselines", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var baselines = (System.Collections.IDictionary)field.GetValue(compiler)!;
        Assert.True(baselines.Contains(moduleName), $"no RAD baseline was recorded for '{moduleName}'");
        var baseline = baselines[moduleName]!;
        var moduleDefField = baseline.GetType().GetField("ModuleDef", BindingFlags.Public | BindingFlags.Instance)!;
        return (ModuleDefinition)moduleDefField.GetValue(baseline)!;
    }

    /// <summary>Recursively counts every id-bearing/id-less application object in a ModuleDefinition tree — mirrors BcCompiler.Incremental.cs's own `EnumerateContainers`.</summary>
    private static int CountObjectsRecursive(IObjectContainerDefinition container)
    {
        int count = 0;
        foreach (var propName in new[] { "Tables", "Codeunits", "Pages", "PageExtensions", "TableExtensions", "Reports",
                     "ReportExtensions", "XmlPorts", "Queries", "EnumTypes", "EnumExtensionTypes", "PermissionSets",
                     "PermissionSetExtensions", "Interfaces", "ControlAddIns", "Profiles", "PageCustomizations", "ProfileExtensions" })
        {
            var prop = typeof(IObjectContainerDefinition).GetProperty(propName)!;
            if (prop.GetValue(container) is Array arr) count += arr.Length;
        }
        if (container.Namespaces != null)
            foreach (var ns in container.Namespaces)
                count += CountObjectsRecursive(ns);
        return count;
    }

    private static int TopLevelCodeunitCount(ModuleDefinition module) => module.Codeunits?.Length ?? 0;

    [SkippableFact]
    public void RecordIncrementalBaseline_NamespaceHeavyApp_ModuleDefPopulatedRecursively_NotInTopLevelArrays()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // 30 codeunits across 3 distinct namespace segments (one nested two levels deep) — "at
        // scale", per #2486's finding that a 1-2 file fixture does not reproduce this class of
        // bug (issue #2507's own guidance).
        var expectedNames = new List<string>();
        int id = 92000;
        for (int i = 0; i < 10; i++)
        {
            var name = $"NsA Codeunit {i}";
            WriteAl($"A{i}.al", NsCodeunitSrc("Pageworks.Assets", id + i, name, i));
            expectedNames.Add(name);
        }
        for (int i = 0; i < 10; i++)
        {
            var name = $"NsB Codeunit {i}";
            WriteAl($"B{i}.al", NsCodeunitSrc("Pageworks.Utils", id + 100 + i, name, i));
            expectedNames.Add(name);
        }
        for (int i = 0; i < 10; i++)
        {
            var name = $"NsC Codeunit {i}";
            // Nested two levels deep: Pageworks.Utils.Internal.
            WriteAl($"C{i}.al", NsCodeunitSrc("Pageworks.Utils.Internal", id + 200 + i, name, i));
            expectedNames.Add(name);
        }
        Assert.Equal(30, expectedNames.Count);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "NsScaleModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        Assert.Equal(30, baselineOut.Sources.Count);

        var moduleDef = GetRecordedBaselineModuleDef(compiler, "NsScaleModule");

        // Documents the mechanism precisely (tdd.md: assert specific values, not just "some"):
        // BC's OWN converter is faithful here — the top-level array is near-empty because every
        // object lives under `.Namespaces`. A fix that flattened namespace objects into the
        // TOP-LEVEL arrays instead (diverging from BC's own shape, which CreateForRad expects)
        // would fail this half of the assertion.
        Assert.True(TopLevelCodeunitCount(moduleDef) < 30,
            $"expected BC's own namespace nesting to keep the top-level Codeunits array small, got {TopLevelCodeunitCount(moduleDef)}");

        // The actual claim under test: walked recursively, every single declared object is there.
        var recursiveCount = CountObjectsRecursive(moduleDef);
        Assert.Equal(30, recursiveCount);

        // BC nests each DOT-separated namespace SEGMENT as its own tree level (confirmed
        // empirically here — NOT one flat "Pageworks.Assets" node): a single top-level
        // "Pageworks" node, with two children "Assets" and "Utils", "Utils" itself having one
        // child "Internal" (the two-levels-deep case).
        Assert.NotNull(moduleDef.Namespaces);
        var pageworksNs = Assert.Single(moduleDef.Namespaces!);
        Assert.Equal("Pageworks", pageworksNs.Name);
        Assert.NotNull(pageworksNs.Namespaces);
        Assert.Equal(2, pageworksNs.Namespaces!.Length); // "Assets" and "Utils"
        var utilsNs = Assert.Single(pageworksNs.Namespaces!, n => n.Name == "Utils");
        Assert.NotNull(utilsNs.Namespaces);
        var internalNs = Assert.Single(utilsNs.Namespaces!);
        Assert.Equal("Internal", internalNs.Name);
        Assert.Equal(10, CountObjectsRecursive(internalNs)); // the "NsC" group, nested two levels deep
    }

    [SkippableFact]
    public void TryEmitIncremental_NamespaceDeclaredApp_CrossNamespaceCall_TakesFastPath_NotFallback()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Caller lives in a DIFFERENT namespace from Callee — the exact shape that needs the
        // self-referencing RAD loader to correctly represent an untouched sibling declared under
        // `.Namespaces`, not the module's own top-level arrays.
        WriteAl("Callee.al", $$"""
            namespace Pageworks.NsCross.Callees;

            codeunit 92300 "NsCross Callee"
            {
                procedure GetValue(): Integer
                begin
                    exit(10);
                end;
            }
            """);
        WriteAl("Caller.al", $$"""
            namespace Pageworks.NsCross.Callers;

            using Pageworks.NsCross.Callees;

            codeunit 92301 "NsCross Caller"
            {
                procedure CallIt(): Integer
                var
                    Callee: Codeunit "NsCross Callee";
                begin
                    exit(Callee.GetValue());
                end;
            }
            """);

        var compiler = new BcCompiler();
        var baselineOut = compiler.Emit(new[] { _root }, "NsCrossModule", trackIncrementalBaseline: true);
        Assert.Empty(baselineOut.Diagnostics);
        var baselineByName = baselineOut.Sources.ToDictionary(s => s.Name, s => s.Code);

        // Only the CALLEE changes — the caller's file (a different namespace) is never touched.
        WriteAl("Callee.al", $$"""
            namespace Pageworks.NsCross.Callees;

            codeunit 92300 "NsCross Callee"
            {
                procedure GetValue(): Integer
                begin
                    exit(20);
                end;
            }
            """);

        var incrOut = compiler.TryEmitIncremental(new[] { _root }, "NsCrossModule", appRootDir: null, out var fallbackReason);
        Assert.True(incrOut != null,
            $"expected the fast path to apply to a namespace-declared cross-namespace call; fell back instead: {fallbackReason}");
        var incrByName = incrOut!.Sources.ToDictionary(s => s.Name, s => s.Code);

        // Caller's C# is untouched — served from cache, byte-identical.
        Assert.Equal(baselineByName["NsCross Caller"], incrByName["NsCross Caller"]);
        // Callee's C# reflects the edit.
        Assert.NotEqual(baselineByName["NsCross Callee"], incrByName["NsCross Callee"]);
        Assert.Contains("20", incrByName["NsCross Callee"]);

        // Correct: matches an independent full rebuild of the same post-edit tree.
        var freshOut = new BcCompiler().Emit(new[] { _root }, "NsCrossModuleFresh");
        var freshByName = freshOut.Sources.ToDictionary(s => s.Name, s => s.Code);
        Assert.Equal(freshByName["NsCross Caller"], incrByName["NsCross Caller"]);
        Assert.Equal(freshByName["NsCross Callee"], incrByName["NsCross Callee"]);
    }
}
