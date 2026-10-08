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
        Assert.StartsWith("the package Home 1.0.0.0 (b.app) declares it - if the test means that codeunit, add the dependency on it to app.json; if it means a new one, give it another name; an empty codeunit would shadow it", reason);
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

    private const string Name = "Nowhere Declared Points";

    /// <summary>The probe with its notes collected instead of written to stderr.</summary>
    private static (string? Reason, List<string> Notes) Probe(BcCompiler.PackageScanEntry[] packages, string name)
    {
        var notes = new List<string>();
        return (TddGeneration.FindPackageDeclaringCodeunit(packages, name, notes.Add), notes);
    }

    /// <summary>An unreadable package whose symbols text mentions the name may be the object's home: it refuses,
    /// naming the package and the remedy, rather than reading as "does not declare it". Case does not matter.</summary>
    [Fact]
    public void AnUnreadablePackageMentioningTheName_Refuses_WhateverTheCase()
    {
        var packages = new[] { Package("bad.app", "Broken", "\"Codeunits\":[{\"Id\":65390,\"Name\":\"NOWHERE declared Points\","), Package("ok.app", "Fine", Other) };
        var (reason, notes) = Probe(packages, Name);
        Assert.NotNull(reason);
        Assert.StartsWith("the package Broken 1.0.0.0 (bad.app) could not be read (", reason);
        Assert.Contains("so it cannot be ruled out as the home of the codeunit", reason);
        Assert.EndsWith("remove or replace that file", reason);
        Assert.Empty(notes);
    }

    /// <summary>#5450: an unreadable package whose text never mentions the name cannot declare it. It is skipped with
    /// one note naming the file, and the name no longer depends on a file it has nothing to do with.</summary>
    [Fact]
    public void AnUnreadablePackageNeverMentioningTheName_IsSkippedWithANote()
    {
        var packages = new[] { Package("bad.app", "Broken", "\"Codeunits\":["), Package("ok.app", "Fine", Other) };
        var (reason, notes) = Probe(packages, Name);
        Assert.Null(reason);
        var note = Assert.Single(notes);
        Assert.StartsWith("the package Broken 1.0.0.0 (bad.app) could not be read (", note);
        Assert.Contains("never mentions \"Nowhere Declared Points\"", note);
        Assert.Contains("remove or replace that file", note);
        Assert.DoesNotContain("\n", note);
    }

    /// <summary>Skipping the unreadable package must not end the walk: a readable package after it still declares the
    /// name and is the one named (the unreadable one sorts first by name).</summary>
    [Fact]
    public void AnUnreadablePackageThatIsSkipped_DoesNotHideAReadableOneAfterIt()
    {
        var packages = new[] { Package("a.app", "Aaa Broken", "\"Codeunits\":["), Package("b.app", "Home", TddMissingObjectPackageTests.PointsSymbols) };
        var (reason, notes) = Probe(packages, "Package Only Points");
        Assert.StartsWith("the package Home 1.0.0.0 (b.app) declares it", reason);
        Assert.Single(notes);
    }

    /// <summary>The text search sees the name however the file spells its characters: a JSON \uXXXX escape of any
    /// case (System.Text.Json writes &amp; and ' that way), an escaped slash, a raw non-ASCII letter.</summary>
    [Theory]
    [InlineData("Sales & Co Points", "\"Codeunits\":[{\"Name\":\"Sales \\u0026 Co Points\",")]
    [InlineData("Sales & Co Points", "\"Codeunits\":[{\"Name\":\"Sales & Co Points\",")]
    [InlineData("Caf\u00e9 Points", "\"Codeunits\":[{\"Name\":\"Caf\\u00E9 POINTS\",")]
    [InlineData("Caf\u00e9 Points", "\"Codeunits\":[{\"Name\":\"Caf\u00e9 Points\",")]
    [InlineData("In/Out Points", "\"Codeunits\":[{\"Name\":\"In\\/Out Points\",")]
    [InlineData("Plain Points", "\"Codeunits\":[{\"Name\":\"\\u0050lain Points\",")]
    public void AnUnreadablePackage_SpellingTheNameWithEscapes_StillRefuses(string name, string symbols)
    {
        var (reason, notes) = Probe(new[] { Package("bad.app", "Broken", symbols) }, name);
        Assert.StartsWith("the package Broken 1.0.0.0 (bad.app) could not be read (", reason);
        Assert.Empty(notes);
    }

    /// <summary>A name the file could only spell with an escape the text search does not undo (a backslash, a control
    /// character) proves nothing from a miss, so the unreadable package refuses without being searched.</summary>
    [Theory]
    [InlineData("Back\\slash Points")]
    [InlineData("Tab\tPoints")]
    public void AnUnreadablePackage_WithANameTheTextCannotProve_Refuses(string name)
    {
        var (reason, notes) = Probe(new[] { Package("bad.app", "Broken", "\"Codeunits\":[") }, name);
        Assert.StartsWith("the package Broken 1.0.0.0 (bad.app) could not be read (", reason);
        Assert.Empty(notes);
    }

    /// <summary>Text that is not ASCII-compatible once decoded (a NUL: UTF-16 with no byte-order mark reads as
    /// interleaved NULs) hides any name from a text search, so it cannot rule the package out.</summary>
    [Fact]
    public void AnUnreadablePackage_WhoseTextHoldsANul_Refuses()
    {
        var (reason, notes) = Probe(new[] { Package("bad.app", "Broken", "\"Codeunits\":[\0N\0o") }, Name);
        Assert.StartsWith("the package Broken 1.0.0.0 (bad.app) could not be read (", reason);
        Assert.Empty(notes);
    }

    /// <summary>A package whose file cannot be opened at all has no text to search: it refuses (it may be the home), it
    /// is not read as "never mentions the name".</summary>
    [Fact]
    public void APackageWhoseFileIsGone_Refuses()
    {
        var gone = new BcCompiler.PackageScanEntry(Path.Combine(_scratch, "gone.app"), Guid.NewGuid(), "AL Runner Fixtures", "Gone", new Version(1, 0, 0, 0));
        var (reason, notes) = Probe(new[] { gone }, Name);
        Assert.StartsWith("the package Gone 1.0.0.0 (gone.app) could not be read (", reason);
        Assert.Empty(notes);
    }

    /// <summary>A package that declares a TABLE of the name does not declare a codeunit of it: the codeunit is still
    /// generated (#5450; the kind check was unpinned). The same name as a codeunit in the same package is refused.</summary>
    [Fact]
    public void APackageDeclaringATableOfTheName_DoesNotBlockTheCodeunit()
    {
        const string table = "\"Tables\":[{\"Id\":65410,\"Name\":\"Nowhere Declared Points\",\"Fields\":[],\"Properties\":[]}]";
        var packages = new[] { Package("t.app", "Tabular", table) };
        Assert.Null(TddGeneration.FindPackageDeclaringCodeunit(packages, Name));
        const string both = table + ",\"Codeunits\":[{\"Id\":65411,\"Name\":\"Nowhere Declared Points\",\"Methods\":[],\"Properties\":[]}]";
        Assert.NotNull(TddGeneration.FindPackageDeclaringCodeunit(new[] { Package("t2.app", "Tabular", both) }, Name));
    }
}
