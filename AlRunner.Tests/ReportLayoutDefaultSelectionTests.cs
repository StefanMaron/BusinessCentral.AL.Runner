using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Which declared layout a <c>ReportLayout</c> instance stands for when BC gives no
/// usable name — i.e. the report's DEFAULT layout path (a plain
/// <c>Report.SaveAs(Report::Foo, ...)</c> with no
/// <c>ReportLayoutSelection.SetTempLayoutSelectedName</c>).
///
/// WHY THIS EXISTS
/// Hydration used to give up whenever a report declared more than one layout, on the
/// reasoning that picking one of several would be guessing. The caution was right and
/// the conclusion was wrong: AL *requires* any report using the `rendering` syntax to
/// declare `DefaultRenderingLayout`, so the default case has an exact answer sitting in
/// the source — it never needed to be guessed. Giving up meant the default-layout render
/// got an empty layout stream and produced an EMPTY PDF.
///
/// MEASURED on the Pageworks suite (1012 tests): 15 tests failed with "Expected at least
/// 3 pages, got 0" / "Expected PDF content not found", and every one of them rendered
/// through the default layout while every sibling test naming a layout explicitly passed.
/// Two further tests passed only vacuously — they assert that two renders are
/// byte-identical, which two empty documents trivially are.
/// </summary>
public class ReportLayoutDefaultSelectionTests
{
    private const int PagingReportId = 71179690;

    private static AlReportLayoutInfo Layout(string name, bool isDefault = false) => new(
        ReportId: PagingReportId,
        Name: name,
        LayoutType: "Custom",
        MimeType: "reportlayout/pageworks",
        LayoutFile: $"./src/{name}.pageworks",
        ResolvedPath: $"/repo/src/{name}.pageworks",
        Caption: name,
        Summary: "",
        IsDefault: isDefault);

    /// <summary>
    /// The real shape of PageworksPagingTestReport: three layouts, one of them declared
    /// `DefaultRenderingLayout = PageworksLayout;`.
    /// </summary>
    private static AlReportLayoutInfo[] ThreeLayouts() =>
    [
        Layout("PageworksLayout", isDefault: true),
        Layout("BorderBoundaryLayout"),
        Layout("SplitBorderRowLayout"),
    ];

    /// <summary>
    /// THE REGRESSION. No name from BC + several declared layouts used to yield null,
    /// so nothing was hydrated and the render produced an empty document.
    /// </summary>
    [Fact]
    public void NoName_WithSeveralLayouts_ResolvesToTheDeclaredDefault()
    {
        var chosen = ReportLayoutHydration.Choose(ThreeLayouts(), name: null);

        Assert.NotNull(chosen);
        Assert.Equal("PageworksLayout", chosen!.Name);
    }

    [Fact]
    public void EmptyName_WithSeveralLayouts_ResolvesToTheDeclaredDefault()
    {
        var chosen = ReportLayoutHydration.Choose(ThreeLayouts(), name: "");

        Assert.Equal("PageworksLayout", chosen?.Name);
    }

    /// <summary>
    /// An explicit by-name selection must still win over the default — this is the path
    /// that already worked and must not regress.
    /// </summary>
    [Fact]
    public void ExplicitName_WinsOverTheDeclaredDefault()
    {
        var chosen = ReportLayoutHydration.Choose(ThreeLayouts(), name: "SplitBorderRowLayout");

        Assert.Equal("SplitBorderRowLayout", chosen?.Name);
    }

    [Fact]
    public void ExplicitName_IsMatchedCaseInsensitively()
    {
        var chosen = ReportLayoutHydration.Choose(ThreeLayouts(), name: "splitborderrowlayout");

        Assert.Equal("SplitBorderRowLayout", chosen?.Name);
    }

    /// <summary>
    /// BC sets Name for its own reasons and it does not always correspond to a declared
    /// layout. A failed name match must fall through to the default, never short-circuit
    /// to null — treating a non-matching name as "not ours" previously cost 59 tests.
    /// </summary>
    [Fact]
    public void NonMatchingName_FallsThroughToTheDeclaredDefault()
    {
        var chosen = ReportLayoutHydration.Choose(ThreeLayouts(), name: "SomeTenantOverride");

        Assert.Equal("PageworksLayout", chosen?.Name);
    }

    /// <summary>The single-layout case is unambiguous with or without a default marker.</summary>
    [Fact]
    public void SingleLayout_ResolvesRegardlessOfDefaultMarker()
    {
        var one = new[] { Layout("OnlyLayout") };

        Assert.Equal("OnlyLayout", ReportLayoutHydration.Choose(one, name: null)?.Name);
        Assert.Equal("OnlyLayout", ReportLayoutHydration.Choose(one, name: "Mismatch")?.Name);
    }

    /// <summary>
    /// NEGATIVE CONTROL — the no-guessing rule still holds. Several layouts and no
    /// declared default (a pre-`rendering`-syntax report, or a capture that could not
    /// read the property) must still yield null rather than serve an arbitrary template.
    /// </summary>
    [Fact]
    public void SeveralLayouts_WithNoDeclaredDefault_StillYieldsNull()
    {
        var noDefault = new[] { Layout("A"), Layout("B"), Layout("C") };

        Assert.Null(ReportLayoutHydration.Choose(noDefault, name: null));
    }

    /// <summary>
    /// NEGATIVE CONTROL — AL cannot express two defaults, so seeing two means the capture
    /// is wrong. Ambiguous is ambiguous: yield null instead of picking the first.
    /// </summary>
    [Fact]
    public void TwoLayoutsMarkedDefault_IsAmbiguousAndYieldsNull()
    {
        var twoDefaults = new[]
        {
            Layout("A", isDefault: true),
            Layout("B", isDefault: true),
        };

        Assert.Null(ReportLayoutHydration.Choose(twoDefaults, name: null));
    }

    [Fact]
    public void NoDeclaredLayouts_YieldsNull()
    {
        Assert.Null(ReportLayoutHydration.Choose([], name: null));
        Assert.Null(ReportLayoutHydration.Choose([], name: "Anything"));
    }
}
