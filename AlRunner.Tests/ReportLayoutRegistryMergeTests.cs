using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The same report layout is registered once per compilation that can see the report,
/// and those passes are not equally informed — a symbols-only pass has no source tree,
/// so it cannot resolve LayoutFile to a path. Last-writer-wins therefore ERASED the
/// resolved path a better-informed pass had already found, and the runtime could no
/// longer load the layout's bytes (report renders produced an empty document).
/// </summary>
public class ReportLayoutRegistryMergeTests
{
    private static AlReportLayoutInfo Rich(int id = 71179675) => new(
        ReportId: id,
        Name: "PageworksLayout",
        LayoutType: "Custom",
        MimeType: "reportlayout/pageworks",
        LayoutFile: "./src/Demo/CustomerListPageworks.pageworks",
        ResolvedPath: "/repo/Pageworks/src/Demo/CustomerListPageworks.pageworks",
        Caption: "Pageworks template",
        Summary: "Customer list");

    private static AlReportLayoutInfo Poor(int id = 71179675) => new(
        ReportId: id,
        Name: "PageworksLayout",
        LayoutType: "Custom",
        MimeType: "reportlayout/pageworks",
        LayoutFile: "./src/Demo/CustomerListPageworks.pageworks",
        ResolvedPath: "",
        Caption: "Pageworks template",
        Summary: "");

    [Fact]
    public void LaterPoorerRegistration_DoesNotEraseTheResolvedPath()
    {
        var merged = AlReportLayoutRegistry.Merge(Rich(), Poor());

        Assert.Equal("/repo/Pageworks/src/Demo/CustomerListPageworks.pageworks", merged.ResolvedPath);
        Assert.Equal("Customer list", merged.Summary);
    }

    [Fact]
    public void BetterInformedRegistration_StillFillsInWhatWasMissing()
    {
        // Order must not matter: the pass that knows the path may arrive second.
        var merged = AlReportLayoutRegistry.Merge(Poor(), Rich());

        Assert.Equal("/repo/Pageworks/src/Demo/CustomerListPageworks.pageworks", merged.ResolvedPath);
        Assert.Equal("Customer list", merged.Summary);
    }

    [Fact]
    public void ARealValueStillOverwritesADifferentRealValue()
    {
        // Merging must not freeze the first value it ever saw — only empties are skipped.
        var relocated = Rich() with { ResolvedPath = "/repo/other/CustomerListPageworks.pageworks" };

        var merged = AlReportLayoutRegistry.Merge(Rich(), relocated);

        Assert.Equal("/repo/other/CustomerListPageworks.pageworks", merged.ResolvedPath);
    }

    [Fact]
    public void MimeTypeAndLayoutTypeSurviveAnUninformedPass()
    {
        var blank = Poor() with { MimeType = "", LayoutType = "" };

        var merged = AlReportLayoutRegistry.Merge(Rich(), blank);

        Assert.Equal("reportlayout/pageworks", merged.MimeType);
        Assert.Equal("Custom", merged.LayoutType);
    }
}
