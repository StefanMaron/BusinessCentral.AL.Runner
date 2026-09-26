// TestFilterUserGroupTests — the runner-internal half of issue #4677.
//
// The BC-behaviour claim — TestPage.Filter.SetFilter writes filter group 0, and GetFilter reads the
// first filter group that filters the field — is measured on a service tier by corpus codeunit
// 60919 "Test TestFilter Filter Groups". These tests pin only the helpers LiveNavTestPage routes
// both TestFilter members through.
using System;
using System.Collections.Generic;
using Xunit;

namespace AlRunner.Tests;

public class TestFilterUserGroupTests
{
    [Fact]
    public void Body_RunsInFilterGroupZero_WhenTheRecordWasLeftInGroupTwo()
    {
        var group = 2;
        var seen = TestFilterUserGroup.Run(() => group, g => group = g, () => group);

        Assert.Equal(0, seen);
    }

    [Fact]
    public void TheCallersGroup_IsRestoredAfterwards()
    {
        var group = 2;
        TestFilterUserGroup.Run(() => group, g => group = g, () => 0);

        Assert.Equal(2, group);
    }

    [Fact]
    public void TheCallersGroup_IsRestored_WhenTheBodyThrows()
    {
        var group = 4;
        Assert.Throws<InvalidOperationException>(() =>
            TestFilterUserGroup.Run<int>(() => group, g => group = g, () => throw new InvalidOperationException("boom")));

        Assert.Equal(4, group);
    }

    [Fact]
    public void TheBodysResult_IsReturned()
    {
        var group = 0;
        Assert.Equal("B", TestFilterUserGroup.Run(() => group, g => group = g, () => "B"));
    }

    [Fact]
    public void RunIn_RunsTheBodyInTheNamedGroup_AndRestores()
    {
        var group = 0;
        var seen = TestFilterUserGroup.RunIn(2, () => group, g => group = g, () => group);

        Assert.Equal(2, seen);
        Assert.Equal(0, group);
    }

    [Fact]
    public void FirstGroupFiltering_AnswersTheEarliestGroupInTheGivenOrder_NotTheLowestNumber()
    {
        var groups = new List<(int, IEnumerable<int>)> { (2, new[] { 5 }), (0, new[] { 5, 7 }) };

        Assert.Equal(2, TestFilterUserGroup.FirstGroupFiltering(groups, 5));
        Assert.Equal(0, TestFilterUserGroup.FirstGroupFiltering(groups, 7));
    }

    [Fact]
    public void FirstGroupFiltering_IsNull_WhenNoGroupFiltersTheField()
    {
        var groups = new List<(int, IEnumerable<int>)> { (2, new[] { 5 }), (0, new[] { 7 }) };

        Assert.Null(TestFilterUserGroup.FirstGroupFiltering(groups, 9));
    }
}
