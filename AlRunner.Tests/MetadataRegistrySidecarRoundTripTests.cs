// MetadataRegistrySidecarRoundTripTests — #3958.
//
// Four registries next to AlObjectMetadataRegistry persist a SaveSidecar/LoadSidecar pair
// on the DependencyLoader path, and none of their round trips was covered. Measured, not
// assumed: with the full 228-test metadata surface green, a writer emitting a constant in
// place of every entry's payload left all 228 passing in each of the four. The same
// mutation against AlObjectMetadataRegistry reds its existing round-trip test, which is
// why that one is absent here and these four are present.
//
// WHY A ROUND TRIP IS THE THING TO ASSERT
//   Emit runs only on a compile-cache MISS, so a property that does not survive the
//   sidecar is silently gone on the next warm run — the registry answers its not-found
//   branch and nothing throws. Each registry's own header says the suite exercising it
//   must be run twice; these tests make the sidecar itself the subject, so one run
//   covers both directions.
//
// WHAT EACH TEST ASSERTS, BEYOND "IT COMES BACK"
//   * concrete values per property, never a count alone — a count survives every
//     payload mutation above;
//   * the id-scoping claim: an id outside `onlyIds` must NOT be written, which is what
//     keeps a dependency's sidecar from carrying a sibling app's entries;
//   * for AlReportLayoutRegistry, the three states of the optional IsDefault marker —
//     true, false, and ABSENT — because a reader collapsing absent-to-false is invisible
//     to a test that only ever writes true.
using System.IO;
using System.Linq;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Serial collection for the four metadata registries whose sidecar round trips are
/// covered here. Each is a process-wide static, so a <c>Clear()</c> landing between
/// another class's <c>Register</c> and its lookup flips that lookup's outcome in both
/// directions — the hazard <see cref="ObjectMetadataRegistrySerialCollection"/> exists
/// for, one registry over. <c>DisableParallelization</c> is what makes membership mean
/// anything.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class MetadataRegistrySidecarSerialCollection
{
    public const string Name = "metadata-registry-sidecars";
}

[Collection(MetadataRegistrySidecarSerialCollection.Name)]
public class PageMetadataRegistrySidecarRoundTripTests : IDisposable
{
    public PageMetadataRegistrySidecarRoundTripTests() => AlPageMetadataRegistry.Clear();
    public void Dispose() => AlPageMetadataRegistry.Clear();

    [Fact]
    public void Sidecar_RoundTripsTheMetadataXml_AndScopesToTheIdsAsked()
    {
        const string mineXml = "<PageDefinition ID=\"70661\"><Controls /></PageDefinition>";
        const string otherXml = "<PageDefinition ID=\"70662\" />";
        AlPageMetadataRegistry.Register(70661, mineXml);
        AlPageMetadataRegistry.Register(70662, otherXml);
        // A sibling app's page, which this dependency's sidecar must NOT carry.
        AlPageMetadataRegistry.Register(79999, "<PageDefinition ID=\"79999\" />");

        var path = Path.Combine(TestScratch.Dir("al-runner-pagemeta-sidecar"), "dep.page-metadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Equal(2, AlPageMetadataRegistry.SaveSidecar(path, new[] { 70661, 70662 }));

        AlPageMetadataRegistry.Clear();
        Assert.Equal(0, AlPageMetadataRegistry.Count);

        Assert.Equal(2, AlPageMetadataRegistry.LoadSidecar(path));

        // The XML itself, not merely that an entry exists: a writer substituting a
        // constant keeps the count and loses the metadata.
        Assert.True(AlPageMetadataRegistry.TryGet(70661, out var back));
        Assert.Equal(mineXml, back);
        Assert.True(AlPageMetadataRegistry.TryGet(70662, out var backOther));
        Assert.Equal(otherXml, backOther);

        // The scoping is its own claim: the sibling app's entry was never written.
        Assert.False(AlPageMetadataRegistry.TryGet(79999, out _));
    }

    [Fact]
    public void Sidecar_CorruptJson_Throws_SoCallersFallThroughToAMiss()
    {
        var path = Path.Combine(TestScratch.Dir("al-runner-pagemeta-sidecar-bad"), "bad.page-metadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, "{ not json");
        Assert.ThrowsAny<Exception>(() => AlPageMetadataRegistry.LoadSidecar(path));

        // Well-formed JSON without the array the loader needs is the likelier corruption,
        // and it must not read as an empty-but-valid sidecar.
        File.WriteAllText(path, "{ \"pages\": 3 }");
        Assert.ThrowsAny<Exception>(() => AlPageMetadataRegistry.LoadSidecar(path));
    }
}

[Collection(MetadataRegistrySidecarSerialCollection.Name)]
public class XmlPortMetadataRegistrySidecarRoundTripTests : IDisposable
{
    public XmlPortMetadataRegistrySidecarRoundTripTests() => AlXmlPortMetadataRegistry.Clear();
    public void Dispose() => AlXmlPortMetadataRegistry.Clear();

    [Fact]
    public void Sidecar_RoundTripsTheSchemaXml_AndScopesToTheIdsAsked()
    {
        const string mineXml = "<XmlPortDefinition ID=\"70663\"><Schema /></XmlPortDefinition>";
        AlXmlPortMetadataRegistry.Register(70663, mineXml);
        AlXmlPortMetadataRegistry.Register(79999, "<XmlPortDefinition ID=\"79999\" />");

        var path = Path.Combine(TestScratch.Dir("al-runner-xmlportmeta-sidecar"), "dep.xmlport-metadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Equal(1, AlXmlPortMetadataRegistry.SaveSidecar(path, new[] { 70663 }));

        AlXmlPortMetadataRegistry.Clear();
        Assert.Equal(0, AlXmlPortMetadataRegistry.Count);

        Assert.Equal(1, AlXmlPortMetadataRegistry.LoadSidecar(path));
        Assert.True(AlXmlPortMetadataRegistry.TryGet(70663, out var back));
        Assert.Equal(mineXml, back);
        Assert.False(AlXmlPortMetadataRegistry.TryGet(79999, out _));
    }

    [Fact]
    public void Sidecar_CorruptJson_Throws_SoCallersFallThroughToAMiss()
    {
        var path = Path.Combine(TestScratch.Dir("al-runner-xmlportmeta-sidecar-bad"), "bad.xmlport-metadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, "{ not json");
        Assert.ThrowsAny<Exception>(() => AlXmlPortMetadataRegistry.LoadSidecar(path));

        File.WriteAllText(path, "{ \"xmlPorts\": 3 }");
        Assert.ThrowsAny<Exception>(() => AlXmlPortMetadataRegistry.LoadSidecar(path));
    }
}

[Collection(MetadataRegistrySidecarSerialCollection.Name)]
public class ReportMetadataRegistrySidecarRoundTripTests : IDisposable
{
    public ReportMetadataRegistrySidecarRoundTripTests() => AlReportMetadataRegistry.Clear();
    public void Dispose() => AlReportMetadataRegistry.Clear();

    [Fact]
    public void ScopedSidecar_RoundTripsTheMetadataXml_AndScopesToTheIdsAsked()
    {
        const string mineXml = "<ReportDefinition ID=\"70664\"><DataItems /></ReportDefinition>";
        AlReportMetadataRegistry.Register(70664, mineXml);
        AlReportMetadataRegistry.Register(79999, "<ReportDefinition ID=\"79999\" />");

        var path = Path.Combine(TestScratch.Dir("al-runner-reportmeta-sidecar"), "dep.report-metadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Equal(1, AlReportMetadataRegistry.SaveSidecar(path, new[] { 70664 }));

        AlReportMetadataRegistry.Clear();
        Assert.Equal(0, AlReportMetadataRegistry.Count);

        Assert.Equal(1, AlReportMetadataRegistry.LoadSidecar(path));
        Assert.True(AlReportMetadataRegistry.TryGet(70664, out var back));
        Assert.Equal(mineXml, back);
        Assert.False(AlReportMetadataRegistry.TryGet(79999, out _));
    }

    /// <summary>
    /// The whole-registry overload is a separate writer with its own body, reached from
    /// the bundle cache rather than the dependency cache. It carries everything, so the
    /// scoping claim inverts: id 79999 MUST come back.
    /// </summary>
    [Fact]
    public void WholeRegistrySidecar_CarriesEveryEntry()
    {
        AlReportMetadataRegistry.Register(70664, "<ReportDefinition ID=\"70664\" />");
        AlReportMetadataRegistry.Register(79999, "<ReportDefinition ID=\"79999\" />");

        var path = Path.Combine(TestScratch.Dir("al-runner-reportmeta-sidecar-all"), "all.report-metadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Equal(2, AlReportMetadataRegistry.SaveSidecar(path));

        AlReportMetadataRegistry.Clear();
        Assert.Equal(2, AlReportMetadataRegistry.LoadSidecar(path));

        Assert.True(AlReportMetadataRegistry.TryGet(70664, out var mine));
        Assert.Equal("<ReportDefinition ID=\"70664\" />", mine);
        Assert.True(AlReportMetadataRegistry.TryGet(79999, out var other));
        Assert.Equal("<ReportDefinition ID=\"79999\" />", other);
    }

    [Fact]
    public void Sidecar_CorruptJson_Throws_SoCallersFallThroughToAMiss()
    {
        var path = Path.Combine(TestScratch.Dir("al-runner-reportmeta-sidecar-bad"), "bad.report-metadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, "{ not json");
        Assert.ThrowsAny<Exception>(() => AlReportMetadataRegistry.LoadSidecar(path));

        File.WriteAllText(path, "{ \"reports\": 3 }");
        Assert.ThrowsAny<Exception>(() => AlReportMetadataRegistry.LoadSidecar(path));
    }
}

[Collection(MetadataRegistrySidecarSerialCollection.Name)]
public class ReportLayoutRegistrySidecarRoundTripTests : IDisposable
{
    public ReportLayoutRegistrySidecarRoundTripTests() => AlReportLayoutRegistry.Clear();
    public void Dispose() => AlReportLayoutRegistry.Clear();

    private static AlReportLayoutInfo Layout(int reportId, string name, bool isDefault) => new(
        ReportId: reportId,
        Name: name,
        LayoutType: "Word",
        MimeType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        LayoutFile: "./src/" + name + ".docx",
        ResolvedPath: "/repo/src/" + name + ".docx",
        Caption: name + " caption",
        Summary: name + " summary",
        IsDefault: isDefault);

    [Fact]
    public void Sidecar_RoundTripsEveryProperty_AndScopesToTheReportIdsAsked()
    {
        AlReportLayoutRegistry.Register(Layout(70665, "MineWord", isDefault: true));
        // A sibling app's layout, which this dependency's sidecar must NOT carry.
        AlReportLayoutRegistry.Register(Layout(79999, "ForeignWord", isDefault: true));

        var path = Path.Combine(TestScratch.Dir("al-runner-reportlayout-sidecar"), "dep.report-layouts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Equal(1, AlReportLayoutRegistry.SaveSidecar(path, new[] { 70665 }));

        AlReportLayoutRegistry.Clear();
        Assert.Equal(0, AlReportLayoutRegistry.Count);

        Assert.Equal(1, AlReportLayoutRegistry.LoadSidecar(path));

        var back = Assert.Single(AlReportLayoutRegistry.Get(70665));
        // Every property, by value. The registry's consumers read all of these: a lost
        // ResolvedPath renders an empty document, a lost LayoutType picks the wrong engine.
        Assert.Equal(70665, back.ReportId);
        Assert.Equal("MineWord", back.Name);
        Assert.Equal("Word", back.LayoutType);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", back.MimeType);
        Assert.Equal("./src/MineWord.docx", back.LayoutFile);
        Assert.Equal("/repo/src/MineWord.docx", back.ResolvedPath);
        Assert.Equal("MineWord caption", back.Caption);
        Assert.Equal("MineWord summary", back.Summary);
        Assert.True(back.IsDefault);

        Assert.Empty(AlReportLayoutRegistry.Get(79999));
    }

    /// <summary>
    /// IsDefault has three states across the sidecar, not two: written true, written
    /// false, and ABSENT (a sidecar predating the property). A reader collapsing
    /// absent-to-false is invisible to a test that only ever writes true, so all three
    /// are asserted — and the false case must come back false rather than defaulting.
    /// </summary>
    [Fact]
    public void Sidecar_RoundTripsIsDefault_InAllThreeOfItsStates()
    {
        AlReportLayoutRegistry.Register(Layout(70666, "TheDefault", isDefault: true));
        AlReportLayoutRegistry.Register(Layout(70666, "NotTheDefault", isDefault: false));

        var path = Path.Combine(TestScratch.Dir("al-runner-reportlayout-isdefault"), "dep.report-layouts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Equal(2, AlReportLayoutRegistry.SaveSidecar(path, new[] { 70666 }));

        AlReportLayoutRegistry.Clear();
        Assert.Equal(2, AlReportLayoutRegistry.LoadSidecar(path));

        var layouts = AlReportLayoutRegistry.Get(70666);
        Assert.True(layouts.Single(l => l.Name == "TheDefault").IsDefault);
        // The written-false state: a writer that omits the marker whenever it is false,
        // or a reader that defaults to true, both land here.
        Assert.False(layouts.Single(l => l.Name == "NotTheDefault").IsDefault);

        // The ABSENT state, which no writer in this repo produces — a hand-copied cache
        // from before IsDefault existed. The reader tolerates it and must answer false.
        var legacy = Path.Combine(TestScratch.Dir("al-runner-reportlayout-legacy"), "legacy.report-layouts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, """
        {"layouts":[{"ReportId":70667,"Name":"LegacyWord","LayoutType":"RDLC","MimeType":"",
        "LayoutFile":"./src/LegacyWord.rdl","ResolvedPath":"","Caption":"","Summary":""}]}
        """);

        AlReportLayoutRegistry.Clear();
        Assert.Equal(1, AlReportLayoutRegistry.LoadSidecar(legacy));
        var legacyBack = Assert.Single(AlReportLayoutRegistry.Get(70667));
        Assert.False(legacyBack.IsDefault);
        // The rest of the entry still arrives — tolerating the missing marker must not
        // turn into tolerating a half-read row.
        Assert.Equal("LegacyWord", legacyBack.Name);
        Assert.Equal("RDLC", legacyBack.LayoutType);
    }

    [Fact]
    public void Sidecar_CorruptJson_Throws_SoCallersFallThroughToAMiss()
    {
        var path = Path.Combine(TestScratch.Dir("al-runner-reportlayout-sidecar-bad"), "bad.report-layouts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, "{ not json");
        Assert.ThrowsAny<Exception>(() => AlReportLayoutRegistry.LoadSidecar(path));

        File.WriteAllText(path, "{ \"layouts\": 3 }");
        Assert.ThrowsAny<Exception>(() => AlReportLayoutRegistry.LoadSidecar(path));
    }
}
