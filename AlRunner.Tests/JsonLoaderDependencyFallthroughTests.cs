// JsonLoaderDependencyFallthroughTests — the multi-bundle __MissingTypeSymbol__ bug
// (Pageworks "Copilot Capability" AL0133 cluster).
//
// Mechanism: whenever ANY layered sidecar symbols exist, GetSharedReferences wraps the BC
// package scanner in a CompositeSymbolReferenceLoader with JsonSymbolReferenceLoader(s)
// FIRST. The composite's GetDependencies takes the FIRST non-null result. The JSON loader
// used to return Enumerable.Empty<> for modules it had never heard of (e.g. System
// Application), which is non-null — so it WON the race and erased the real dependency
// list of every .app module. BC's ReferenceManager then never connected System
// Application's reference to the platform System module, and every System-typed parameter
// in its public signatures degraded to __MissingTypeSymbol__ (AL0133) — but ONLY in
// multi-bundle runs (single-bundle runs have no sidecars, hence no composite).
//
// RED before the fix: JsonSymbolReferenceLoader.GetDependencies returned an empty list for
// unknown modules → Composite_GetDependencies_FallsThrough... failed (empty instead of the
// scanner's list). GREEN after: unknown module throws FileNotFoundException (same "not
// mine" signal LoadModule uses) → the composite falls through to the next child.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;
using AlRunner;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;

namespace AlRunner.Tests;

public sealed class JsonLoaderDependencyFallthroughTests : IDisposable
{
    private readonly string _emptyDir;

    public JsonLoaderDependencyFallthroughTests()
    {
        _emptyDir = Path.Combine(Path.GetTempPath(), "alrunner-jsonloader-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_emptyDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_emptyDir, recursive: true); } catch { }
    }

    private static SymbolReferenceSpecification Spec(string publisher, string name) =>
        new(publisher, name, new Version(28, 0, 0, 0), exact: false,
            appId: Guid.NewGuid(), isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);

    /// <summary>Stub standing in for BC's package-scanner loader.</summary>
    private sealed class StubLoader : ISymbolReferenceLoader
    {
        public readonly List<SymbolReferenceSpecification> Deps;
        public StubLoader(params SymbolReferenceSpecification[] deps) => Deps = deps.ToList();
        public ModuleDefinition? LoadModule(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics)
            => throw new FileNotFoundException("stub has no modules");
        public IEnumerable<SymbolReferenceSpecification> GetDependencies(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics)
            => Deps;
        public ModuleInfo LoadModuleInfo(SymbolReferenceSpecification reference, IList<Diagnostic> diagnostics, LoadModuleInfoFlags flags)
            => throw new FileNotFoundException("stub has no modules");
    }

    [Fact]
    public void JsonLoader_GetDependencies_UnknownModule_ThrowsFileNotFound()
    {
        // A JSON loader over a dir with no sidecars knows nothing — it must signal
        // "not mine" (throw), never answer "no dependencies" (empty list) for a module
        // it has no knowledge of.
        var loader = new JsonSymbolReferenceLoader(_emptyDir);
        var diags = new List<Diagnostic>();

        Assert.Throws<FileNotFoundException>(
            () => loader.GetDependencies(Spec("Microsoft", "System Application"), diags).ToList());
    }

    [Fact]
    public void Composite_GetDependencies_FallsThroughToScannerForUnknownModule()
    {
        // Composite = [JSON loader (knows nothing), stub scanner (knows System
        // Application depends on platform System)]. The composite must surface the
        // scanner's dependency list — if the JSON loader's answer wins, System
        // Application loses its reference to platform System and its System-typed
        // parameters become __MissingTypeSymbol__ downstream.
        var platformSystem = Spec("Microsoft", "System");
        var scanner = new StubLoader(platformSystem);
        var composite = new CompositeSymbolReferenceLoader(new ISymbolReferenceLoader[]
        {
            new JsonSymbolReferenceLoader(_emptyDir),
            scanner,
        });

        var deps = composite.GetDependencies(Spec("Microsoft", "System Application"), new List<Diagnostic>()).ToList();

        Assert.Single(deps);
        Assert.Equal("System", deps[0].Name);
    }

    [Fact]
    public void JsonLoader_GetDependencies_KnownModule_ReturnsSidecarDeps()
    {
        // Positive control: a module that DOES have a *.symbols.deps.json sidecar keeps
        // answering from it (the #1546 behaviour must not regress).
        var appId = Guid.NewGuid();
        var depId = Guid.NewGuid();
        DepsSidecarWriter.Write(
            Path.Combine(_emptyDir, "Test_MyImpl_1_0_0_0.symbols.deps.json"),
            "Test", "MyImpl", new Version(1, 0, 0, 0), appId,
            new[] { new DepsSidecarWriter.DepEntry("Microsoft", "System Application", new Version(28, 0, 0, 0), depId) });

        var loader = new JsonSymbolReferenceLoader(_emptyDir);
        var deps = loader.GetDependencies(
            new SymbolReferenceSpecification("Test", "MyImpl", new Version(1, 0, 0, 0), false, appId, false, ImmutableArray<Guid>.Empty),
            new List<Diagnostic>()).ToList();

        Assert.Single(deps);
        Assert.Equal("System Application", deps[0].Name);
        Assert.Equal(depId, deps[0].AppId);
    }
}
