// PackageDeclarationNarrowingTests — #4816: a source-compiled dependency package that declares no
// Application and no dependencies (Microsoft's Test Runner) compiles against the platform app
// "System" alone. Given Base Application too, Test Runner's xmlport "Code Coverage Detailed" is
// ambiguous with Base Application's (AL0275) and 9 of its 47 objects are excluded. Any package
// that declares something keeps every reference, so a propagating dependency still reaches it.

using System.Collections.Immutable;
using Xunit;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner.Tests;

public sealed class PackageDeclarationNarrowingTests
{
    private static NavCA.SymbolReferenceSpecification Spec(string publisher, string name, Guid id) =>
        new(publisher: publisher, name: name, version: new Version(28, 0, 0, 0), exact: false,
            appId: id, isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);

    private static readonly NavCA.SymbolReferenceSpecification[] Floor =
    {
        Spec("Microsoft", "System", new Guid("8874ed3a-0643-4247-9ced-7a7002f7135d")),
        Spec("Microsoft", "System Application", new Guid("63ca2fa4-4f03-4f2b-a480-172fef340d3f")),
        Spec("Microsoft", "Base Application", new Guid("437dbf0e-84ff-417a-965d-ed2bb9650972")),
        Spec("Microsoft", "Application", new Guid("c1335042-3002-4257-bf8a-75c898ccb1b8")),
    };

    private static string[] Narrow(AppManifest package)
    {
        BcCompiler.RecordPackageDeclaration(package);
        return BcCompiler.NarrowToDeclaredReferences(Floor, package.AppId).Select(s => s.Name).ToArray();
    }

    private static AppManifest Package(IReadOnlyList<DependencyRef> dependencies, Version? application = null) =>
        new("Someone", "Package " + Guid.NewGuid().ToString("N"), new Version(1, 0, 0, 0), Guid.NewGuid(),
            dependencies, application, new Version(28, 0, 0, 0));

    [Fact]
    public void PlatformOnlyPackage_CompilesAgainstSystemAlone()
        => Assert.Equal(new[] { "System" }, Narrow(Package(Array.Empty<DependencyRef>())));

    [Fact]
    public void PackageDeclaringApplication_KeepsEveryReference()
        => Assert.Equal(Floor.Select(s => s.Name), Narrow(Package(Array.Empty<DependencyRef>(), new Version(28, 0, 0, 0))));

    [Fact]
    public void PackageDeclaringOnlyAPropagatingIsvDependency_KeepsTheApplicationLayer()
    {
        // The ISV dependency may set propagateDependencies and itself need Base Application; the
        // package then compiles against what it propagates, so nothing is narrowed away.
        var isv = new DependencyRef(Guid.NewGuid(), "Propagating Lib", "Some ISV", new Version(1, 0, 0, 0));
        Assert.Equal(Floor.Select(s => s.Name), Narrow(Package(new[] { isv })));
    }

    [Fact]
    public void UnrecordedApp_IsLeftUnnarrowed()
        => Assert.Equal(Floor.Select(s => s.Name),
            BcCompiler.NarrowToDeclaredReferences(Floor, Guid.NewGuid()).Select(s => s.Name));
}
