// TestPageControlEditableTests — a TestPage control's Editable() is narrowed by the page's open
// mode (#5002). The AL-observable claim is corpus codeunit 68015 "RSV Tests"; these pin the
// runner's own combination and that a Rec-bound field actually consults it.
using System;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageControlEditableTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // OpenView / the View action: the control is read-only
    [InlineData(false, true, false)]   // the control's own Editable = false still wins
    [InlineData(false, false, false)]
    public void Combine_NarrowsTheControlByTheOpenMode(bool controlEditable, bool openMode, bool expected)
        => Assert.Equal(expected, TestPageControlEditable.Combine(() => openMode, controlEditable));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Combine_NoOpenMode_AnswersTheControl(bool controlEditable)
        => Assert.Equal(controlEditable, TestPageControlEditable.Combine(null, controlEditable));

    [Fact]
    public void Combine_DoesNotAskTheOpenModeForAControlThatIsAlreadyReadOnly()
    {
        var asked = false;
        Assert.False(TestPageControlEditable.Combine(() => { asked = true; return true; }, controlEditable: false));
        Assert.False(asked);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RecBoundField_EditableFollowsTheOpenMode(bool openMode, bool expected)
    {
        // Record-only field: no page, so the control's own property answers true and the open
        // mode alone decides. Editable reads neither the record nor the field number.
        var field = new LiveNavTestField(null!, 1) { OpenModeEditable = () => openMode };
        Assert.Equal(expected, field.Editable);
    }

    [Fact]
    public void RecBoundField_WithoutAnOpenMode_StaysEditable()
        => Assert.True(new LiveNavTestField(null!, 1).Editable);

    // #5012: the open mode of a page the test did not open itself.
    [Theory]
    [InlineData(null, true)]    // a handler's page, not a part, not a lookup
    [InlineData(true, true)]    // a part whose host is editable
    [InlineData(false, false)]  // a part whose host was opened with OpenView, or switched by View
    public void OpenMode_NoTestMode_FollowsTheHost(bool? hostOpenMode, bool expected)
        => Assert.Equal(expected, TestPageControlEditable.OpenMode(null, hostOpenMode, lookupMode: false));

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void OpenMode_NoTestMode_LookupModeIsReadOnly(bool? hostOpenMode)
        => Assert.False(TestPageControlEditable.OpenMode(null, hostOpenMode, lookupMode: true));

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, false, false)]
    public void OpenMode_TheTestsModeWins(bool testOpenMode, bool hostOpenMode, bool lookupMode, bool expected)
        => Assert.Equal(expected, TestPageControlEditable.OpenMode(testOpenMode, hostOpenMode, lookupMode));
}
