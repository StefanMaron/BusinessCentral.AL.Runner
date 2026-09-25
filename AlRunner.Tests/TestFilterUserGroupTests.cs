// TestFilterUserGroupTests — the runner-internal half of issue #4677.
//
// The BC-behaviour claim — TestPage.Filter.SetFilter replaces a group-0 filter an OnOpenPage set
// before leaving FilterGroup(2) active, and does not override a group-2 filter — is measured on a
// service tier by corpus codeunit 60919 "Test TestFilter Filter Groups". These tests pin only the
// scope helper LiveNavTestPage routes both TestFilter members through.
using System;
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
}
