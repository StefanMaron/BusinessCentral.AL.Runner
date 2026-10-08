// #5446: the package probe of the missing-object generation, called directly: which package it names, in which
// order, for a namespaced or differently cased name, and what it does with a package it cannot read.
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class TddMissingObjectPackageProbeTests : IDisposable
{
    private readonly string _scratch;

    public TddMissingObjectPackageProbeTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-missing-object-probe");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private BcCompiler.PackageScanEntry Package(string file, string name, string symbols)
    {
        var path = Path.Combine(_scratch, file);
        File.WriteAllBytes(path, TddMissingObjectPackageTests.BuildPackage(symbols));
        return new BcCompiler.PackageScanEntry(path, Guid.NewGuid(), "AL Runner Fixtures", name, new Version(1, 0, 0, 0));
    }

    private const string Namespaced = """
        "Namespaces":[{"Name":"Fixture.Sub","Codeunits":[{"Id":65401,"Name":"Namespaced Points","Methods":[],"Properties":[]}]}]
        """;
    private const string Other = """
        "Codeunits":[{"Id":65402,"Name":"Other Points","Methods":[],"Properties":[]}]
        """;

    [Fact]
    public void ANameNoPackageDeclares_ReturnsNull()
    {
        var packages = new[] { Package("a.app", "A", TddMissingObjectPackageTests.PointsSymbols), Package("b.app", "B", Other) };
        Assert.Null(TddGeneration.FindPackageDeclaringCodeunit(packages, "Nowhere Declared Points"));
        Assert.Null(TddGeneration.FindPackageDeclaringCodeunit(Array.Empty<BcCompiler.PackageScanEntry>(), "Package Only Points"));
    }

    [Fact]
    public void ANameAPackageDeclares_NamesThatPackageAndNotItsNeighbour()
    {
        var packages = new[] { Package("a.app", "Neighbour", Other), Package("b.app", "Home", TddMissingObjectPackageTests.PointsSymbols) };
        var reason = TddGeneration.FindPackageDeclaringCodeunit(packages, "Package Only Points");
        Assert.StartsWith("the package Home 1.0.0.0 (b.app) declares it - add the dependency on it to app.json", reason);
    }

    [Fact]
    public void TheNameIsMatchedWhateverTheCase_AndInAnyNamespace()
    {
        var packages = new[] { Package("a.app", "Home", Namespaced) };
        Assert.NotNull(TddGeneration.FindPackageDeclaringCodeunit(packages, "namespaced POINTS"));
        Assert.Null(TddGeneration.FindPackageDeclaringCodeunit(packages, "Namespaced Point"));
    }

    [Fact]
    public void TwoPackagesDeclaringIt_TheNamedOneDoesNotFollowTheScanOrder()
    {
        var first = Package("z.app", "Zeta", TddMissingObjectPackageTests.PointsSymbols);
        var second = Package("a.app", "Alpha", TddMissingObjectPackageTests.PointsSymbols);
        var forward = TddGeneration.FindPackageDeclaringCodeunit(new[] { first, second }, "Package Only Points");
        var backward = TddGeneration.FindPackageDeclaringCodeunit(new[] { second, first }, "Package Only Points");
        Assert.Equal(forward, backward);
        Assert.StartsWith("the package Alpha 1.0.0.0 (a.app)", forward);
    }

    /// <summary>An unreadable package may be the object's home: it refuses, naming the package, rather than reading as
    /// "does not declare it".</summary>
    [Fact]
    public void AnUnreadablePackage_RefusesInsteadOfReadingAsAbsent()
    {
        var packages = new[] { Package("bad.app", "Broken", "\"Codeunits\":["), Package("ok.app", "Fine", Other) };
        var reason = TddGeneration.FindPackageDeclaringCodeunit(packages, "Nowhere Declared Points");
        Assert.NotNull(reason);
        Assert.StartsWith("the package Broken 1.0.0.0 (bad.app) could not be read (", reason);
        Assert.Contains("so it cannot be ruled out as the home of the codeunit", reason);
    }
}
