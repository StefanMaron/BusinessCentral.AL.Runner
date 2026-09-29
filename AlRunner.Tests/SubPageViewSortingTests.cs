// The runner-side half of #4188: what a part's SubPageView Sorting makes the runner set on the
// part's record. The AL-observable claim (the rows a part shows, in which order) is measured
// upstream by corpus codeunit 67930 "SPS Tests"; this pins the metadata read.
using System.Collections.Generic;
using AlRunner;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

public sealed class SubPageViewSortingTests
{
    private static InfopartPageDefinition Part(ViewDefinitionSorting? sorting)
        => new() { SubFormView = new ViewDefinition { Sorting = sorting } };

    [Fact]
    public void SortOrder_ReadsTheKeyAndADescendingDirection()
    {
        var (keys, ascending) = LiveNavTestPage.SubPageViewSortOrder(Part(new ViewDefinitionSorting
        {
            KeyFields = "Field2,Field1", KeyFieldsSetByView = true, AscendingSetByView = true, Ascending = false,
        }));

        Assert.Equal(new[] { 2, 1 }, keys);
        Assert.False(ascending);
    }

    // order(descending) alone: no key, direction still applied.
    [Fact]
    public void SortOrder_OrderWithoutSorting_SetsOnlyTheDirection()
    {
        var (keys, ascending) = LiveNavTestPage.SubPageViewSortOrder(Part(new ViewDefinitionSorting
        {
            KeyFields = "", AscendingSetByView = true, Ascending = false,
        }));

        Assert.Null(keys);
        Assert.False(ascending);
    }

    // BC's client (PageInfopartBuilder.CreatePagePart) copies Ascending without reading the
    // SetByView flags, unlike NavForm.ApplySourceTableView on the page path.
    [Fact]
    public void SortOrder_IsNotGatedOnTheSetByViewFlags()
    {
        var (keys, ascending) = LiveNavTestPage.SubPageViewSortOrder(Part(new ViewDefinitionSorting
        {
            KeyFields = "Field3", KeyFieldsSetByView = false, AscendingSetByView = false, Ascending = true,
        }));

        Assert.Equal(new[] { 3 }, keys);
        Assert.True(ascending);
    }

    [Fact]
    public void SortOrder_NoViewOrNoSorting_SetsNothing()
    {
        Assert.Equal((null, null), LiveNavTestPage.SubPageViewSortOrder(new InfopartPageDefinition()));
        Assert.Equal((null, null), LiveNavTestPage.SubPageViewSortOrder(Part(null)));
    }
}
