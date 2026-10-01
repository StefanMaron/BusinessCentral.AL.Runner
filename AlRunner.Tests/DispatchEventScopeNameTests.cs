using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins <see cref="BcRuntime.TryDecodeEventScopeName"/>, the seam that turns a publisher's
/// generated <c>&lt;EventName&gt;_Scope</c> class into the event name the subscriber registry is
/// keyed by (#5142). The dispatcher used to cut at the FIRST underscore, so
/// <c>OnBeforeHandle_State</c> was looked up as <c>OnBeforeHandle</c>: its own subscribers never
/// ran, and a sibling event of the shorter name received them instead. The BC-side claim
/// (an underscored event reaches its subscribers, and only its own) is corpus codeunit 67024 "Test Event Underscore Name".
/// </summary>
public class DispatchEventScopeNameTests
{
    [Theory]
    [InlineData("OnDoCalc_Scope", "OnDoCalc")]
    [InlineData("OnBeforeHandle_State_Scope", "OnBeforeHandle_State")]
    [InlineData("On_Before_Handle_Scope", "On_Before_Handle")]
    [InlineData("OnCheck_Scope_Value_Scope", "OnCheck_Scope_Value")]
    [InlineData("OnCheck_Scope_Scope", "OnCheck_Scope")]
    public void EventScope_DecodesTheWholeEventName(string scopeName, string expected)
    {
        Assert.True(BcRuntime.TryDecodeEventScopeName(scopeName, out var eventName));
        Assert.Equal(expected, eventName);
    }

    [Theory]
    [InlineData("_Scope")]
    [InlineData("Run_Scope__1684062386")]
    [InlineData("OnDoCalc")]
    [InlineData("OnDoCalc_State")]
    [InlineData("")]
    public void NotAnEventScope_IsRefused(string scopeName)
    {
        Assert.False(BcRuntime.TryDecodeEventScopeName(scopeName, out var eventName));
        Assert.Equal("", eventName);
    }

    [Fact]
    public void RaiseTrackerKey_KeepsTheUnderscoredEventName()
    {
        Assert.Equal("ev|Codeunit|50100|OnBeforeHandle_State",
            AlEventRaiseTracker.EventScopeKey(typeof(Codeunit50100.OnBeforeHandle_State_Scope)));
        Assert.Equal("ev|Codeunit|50100|OnBeforeHandle",
            AlEventRaiseTracker.EventScopeKey(typeof(Codeunit50100.OnBeforeHandle_Scope)));
    }

    // Stand-ins for an emitted publisher: the declaring type's name is what the decoders read.
    private static class Codeunit50100
    {
        public sealed class OnBeforeHandle_State_Scope { }
        public sealed class OnBeforeHandle_Scope { }
    }
}
