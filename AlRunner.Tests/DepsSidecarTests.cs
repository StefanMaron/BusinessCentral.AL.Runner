using AlRunner;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Round-trip tests for the *.symbols.deps.json sidecar (issue #1546).
/// The sidecar tells <see cref="JsonSymbolReferenceLoader"/> which dependencies to
/// advertise via <c>ISymbolReferenceLoader.GetDependencies</c>; without it BC's
/// ReferenceManager cannot link cross-app type references and parameter types
/// resolve to <c>__MissingTypeSymbol__</c>.
/// </summary>
public class DepsSidecarTests
{
    [Fact]
    public void Write_RoundTripsThroughLoader()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deps-sc-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var appId = Guid.NewGuid();
            var sysId = Guid.Parse("8874ed3a-0643-4247-9ced-7a7002f7135d");
            DepsSidecarWriter.Write(
                Path.Combine(dir, "Microsoft_System Application_27.5.46862.49619.symbols.deps.json"),
                "Microsoft", "System Application", new Version(27, 5, 46862, 49619), appId,
                new[] {
                    new DepsSidecarWriter.DepEntry("Microsoft", "System", new Version(27, 0, 0, 0), sysId),
                });

            var loader = new JsonSymbolReferenceLoader(dir);
            Assert.True(loader.HasAny);

            var spec = new SymbolReferenceSpecification(
                "Microsoft", "System Application", new Version(27, 5, 0, 0),
                false, appId, false, System.Collections.Immutable.ImmutableArray<Guid>.Empty);
            var diagnostics = new List<Diagnostic>();
            var deps = loader.GetDependencies(spec, diagnostics).ToList();

            Assert.Single(deps);
            Assert.Equal("Microsoft", deps[0].Publisher);
            Assert.Equal("System", deps[0].Name);
            Assert.Equal(new Version(27, 0, 0, 0), deps[0].Version);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetDependencies_ReturnsEmpty_WhenNoSidecarPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deps-sc-empty-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var loader = new JsonSymbolReferenceLoader(dir);
            var spec = new SymbolReferenceSpecification(
                "Microsoft", "System Application", new Version(27, 5, 0, 0),
                false, Guid.NewGuid(), false, System.Collections.Immutable.ImmutableArray<Guid>.Empty);
            var deps = loader.GetDependencies(spec, new List<Diagnostic>()).ToList();
            Assert.Empty(deps);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetDependencies_TolerantToReversedPubName()
    {
        // BC sometimes queries with publisher/name in reversed order; the loader caches both.
        var dir = Path.Combine(Path.GetTempPath(), "deps-sc-rev-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var appId = Guid.NewGuid();
            DepsSidecarWriter.Write(
                Path.Combine(dir, "Microsoft_X_1.0.0.0.symbols.deps.json"),
                "Microsoft", "X", new Version(1, 0, 0, 0), appId,
                new[] { new DepsSidecarWriter.DepEntry("Microsoft", "Y", new Version(1, 0, 0, 0), Guid.NewGuid()) });

            var loader = new JsonSymbolReferenceLoader(dir);
            // Reversed: ask with publisher="X", name="Microsoft"
            var spec = new SymbolReferenceSpecification(
                "X", "Microsoft", new Version(1, 0, 0, 0),
                false, appId, false, System.Collections.Immutable.ImmutableArray<Guid>.Empty);
            var deps = loader.GetDependencies(spec, new List<Diagnostic>()).ToList();
            Assert.Single(deps);
            Assert.Equal("Y", deps[0].Name);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
