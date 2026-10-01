using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
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

    // #5167: a quoted event name is mangled into the class name; [NavName] carries the AL name.
    [Theory]
    [InlineData(typeof(Codeunit50100.On_Before_Quoted_Scope), "On Before Quoted")]
    [InlineData(typeof(Codeunit50100.Ona45Checka46Value_a40Qtya41_a38_Amt_Scope), "On-Check.Value (Qty) & Amt")]
    [InlineData(typeof(Codeunit50100.OnQuotedPlain_Scope), "OnQuotedPlain")]
    [InlineData(typeof(Codeunit50100.OnBeforeHandle_State_Scope), "OnBeforeHandle_State")]
    public void EventScope_AlName_IsTheNavName_OrTheDecodedName(Type scopeType, string expected)
    {
        Assert.True(BcRuntime.TryGetEventScopeAlName(scopeType, out var eventName));
        Assert.Equal(expected, eventName);
    }

    [Fact]
    public void EventScope_AlName_RefusesAClassThatIsNotAnEventScope()
    {
        Assert.False(BcRuntime.TryGetEventScopeAlName(typeof(Codeunit50100.On_Decoy_Frame), out var eventName));
        Assert.Equal("", eventName);
    }

    [Fact]
    public void RaiseTrackerKey_UsesTheAlNameOfAQuotedEvent()
    {
        Assert.Equal("ev|Codeunit|50100|On Before Quoted",
            AlEventRaiseTracker.EventScopeKey(typeof(Codeunit50100.On_Before_Quoted_Scope)));
        // Unmangled names keep the key they always had: persisted affectedOnly baselines stay valid.
        Assert.Equal("ev|Codeunit|50100|OnQuotedPlain",
            AlEventRaiseTracker.EventScopeKey(typeof(Codeunit50100.OnQuotedPlain_Scope)));
    }

    [Theory]
    [InlineData("On Before Quoted", typeof(Codeunit50100.On_Before_Quoted_Scope))]
    [InlineData("On-Check.Value (Qty) & Amt", typeof(Codeunit50100.Ona45Checka46Value_a40Qtya41_a38_Amt_Scope))]
    [InlineData("OnBeforeHandle", typeof(Codeunit50100.OnBeforeHandle_Scope))]
    [InlineData("OnBeforeHandle_State", typeof(Codeunit50100.OnBeforeHandle_State_Scope))]
    public void SeedLookup_FindsTheScopeClassOfTheAlEventName(string alEventName, Type expected)
    {
        Assert.Same(expected, EventSubscriberPatches.FindEventScopeType(typeof(Codeunit50100), alEventName));
    }

    [Theory]
    [InlineData("On Missing")]
    [InlineData("On Decoy")]          // [NavName] on a class that is not an event scope
    public void SeedLookup_FindsNothingForAnUndeclaredName(string alEventName)
    {
        Assert.Null(EventSubscriberPatches.FindEventScopeType(typeof(Codeunit50100), alEventName));
    }

    // Stand-ins for an emitted publisher: the declaring type's name is what the decoders read.
    // The [NavName] values mirror what BC's compiler emits for the same AL names (#5167).
    private static class Codeunit50100
    {
        public sealed class OnBeforeHandle_State_Scope { }
        public sealed class OnBeforeHandle_Scope { }
        [NavName("On Before Quoted")] public sealed class On_Before_Quoted_Scope { }
        [NavName("On-Check.Value (Qty) & Amt")] public sealed class Ona45Checka46Value_a40Qtya41_a38_Amt_Scope { }
        [NavName("OnQuotedPlain")] public sealed class OnQuotedPlain_Scope { }
        [NavName("On Decoy")] public sealed class On_Decoy_Frame { }
    }
}
