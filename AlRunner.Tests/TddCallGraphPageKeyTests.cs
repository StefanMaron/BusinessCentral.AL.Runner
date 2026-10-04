// The keys a page trigger is registered under and a TestPage call raises (#5309): pure mappings, in-process. They
// are what the single-folder fixture (TddPageTriggersTests) reaches through the compiler, pinned here at the
// ends the fixture cannot reach: a trigger the graph cannot attribute to a control, a call whose page is unknown,
// and the names a trigger is read under.
using Xunit;

namespace AlRunner.Tests;

public sealed class TddCallGraphPageKeyTests
{
    /// <summary>A lifecycle trigger and a control trigger are themselves; a control trigger a page extension's modify block
    /// adds under OnBefore or OnAfter is the control trigger; OnAfterGetRecord is not an OnAfter of anything.</summary>
    [Theory]
    [InlineData("OnOpenPage", "OnOpenPage")]
    [InlineData("onafterGetRecord", "OnAfterGetRecord")]
    [InlineData("OnAfterGetCurrRecord", "OnAfterGetCurrRecord")]
    [InlineData("OnValidate", "OnValidate")]
    [InlineData("OnAfterValidate", "OnValidate")]
    [InlineData("OnBeforeValidate", "OnValidate")]
    [InlineData("OnBeforeAction", "OnAction")]
    [InlineData("OnAfterLookup", "OnLookup")]
    [InlineData("OnPageBackgroundTaskCompleted", null)]
    [InlineData("OnBeforeInsertRecord", null)]
    public void PageTriggerName_IsTheTriggerARaiseUses(string name, string? trigger)
        => Assert.Equal(trigger, TddCallGraph.PageTriggerName(name));

    /// <summary>A lifecycle trigger answers its own key; a control trigger its control's and the coarse one; one in no
    /// readable control the coarse one and the unattributed one; one no operation starts only the key every page
    /// operation raises.</summary>
    [Fact]
    public void TriggerKeys_AnswerTheRaisesThatStartThem()
    {
        Assert.Equal(new[] { "pt card|onopenpage" }, TddCallGraph.TriggerKeys("PT Card", "OnOpenPage", null));
        Assert.Equal(new[] { "pt card|onvalidate", "pt card|onvalidate@qty" }, TddCallGraph.TriggerKeys("PT Card", "OnValidate", "Qty"));
        Assert.Equal(new[] { "pt card|onvalidate", "pt card|onvalidate@qty" }, TddCallGraph.TriggerKeys("PT Card", "OnAfterValidate", "Qty"));
        Assert.Equal(new[] { "pt card|onaction", "pt card|onaction@?" }, TddCallGraph.TriggerKeys("PT Card", "OnAction", null));
        Assert.Equal(new[] { "pt card|onaction", "pt card|onaction@?" }, TddCallGraph.TriggerKeys("PT Card", "OnAction", ""));
        Assert.Equal(new[] { "pt card|onpagecode" }, TddCallGraph.TriggerKeys("PT Card", "OnPageBackgroundTaskCompleted", "Qty"));
    }

    /// <summary>A call that names its control raises that control's key, the unattributed one and the page one; a call
    /// that names no page or no control raises the coarse key every control trigger of the page (of every page) answers.</summary>
    [Fact]
    public void ControlRaiseKeys_NameTheControlWhenTheyCan()
    {
        Assert.Equal(new[] { "pt card|onvalidate@qty", "pt card|onvalidate@?", "pt card|onpagecode" },
            TddCallGraph.ControlRaiseKeys("PT Card", "Qty", "OnValidate"));
        Assert.Equal(new[] { "pt card|onvalidate", "pt card|onpagecode" }, TddCallGraph.ControlRaiseKeys("PT Card", null, "OnValidate"));
        Assert.Equal(new[] { "pt card|onvalidate", "pt card|onpagecode" }, TddCallGraph.ControlRaiseKeys("PT Card", "", "OnValidate"));
        Assert.Equal(new[] { "*|onvalidate", "*|onpagecode" }, TddCallGraph.ControlRaiseKeys(null, "Qty", "OnValidate"));
    }

    /// <summary>A page operation raises each trigger, the platform event around it where there is one, and the key every
    /// page operation raises; a page the call cannot name is every page.</summary>
    [Fact]
    public void PageRaiseKeys_RaiseTheTriggerItsEventAndThePageKey()
    {
        Assert.Equal(new[] { "pt card|onopenpage", "pt card|onopenpageevent", "pt card|onfindrecord", "pt card|onpagecode" },
            TddCallGraph.PageRaiseKeys("PT Card", new[] { "OnOpenPage", "OnFindRecord" }));
        Assert.Equal(new[] { "*|onclosepage", "*|onclosepageevent", "*|onpagecode" }, TddCallGraph.PageRaiseKeys(null, new[] { "OnClosePage" }));
        foreach (var (trigger, ev) in new[]
        {
            ("OnQueryClosePage", "OnQueryClosePageEvent"), ("OnNewRecord", "OnNewRecordEvent"), ("OnInsertRecord", "OnInsertRecordEvent"),
            ("OnModifyRecord", "OnModifyRecordEvent"), ("OnDeleteRecord", "OnDeleteRecordEvent"),
            ("OnAfterGetRecord", "OnAfterGetRecordEvent"), ("OnAfterGetCurrRecord", "OnAfterGetCurrRecordEvent"),
        })
            Assert.Contains("p|" + ev.ToLowerInvariant(), TddCallGraph.PageRaiseKeys("P", new[] { trigger }));
    }

    /// <summary>The control's raise reaches its own trigger, an unattributed one and the coarse registration, and not
    /// another control's; a raise of an unknown page reaches every page's trigger (the wildcard), a fine one does not.</summary>
    [Fact]
    public void ARaise_ReachesTheTriggersItShould()
    {
        var qty = TddCallGraph.TriggerKeys("P", "OnValidate", "Qty");
        var unattributed = TddCallGraph.TriggerKeys("P", "OnValidate", null);
        var qtyRaise = TddCallGraph.ControlRaiseKeys("P", "Qty", "OnValidate");
        var noteRaise = TddCallGraph.ControlRaiseKeys("P", "Note", "OnValidate");
        var coarseRaise = TddCallGraph.ControlRaiseKeys("P", null, "OnValidate");

        // The way ReachClosure reads it: a registered key is answered by its own raise and by the wildcard raise of its name.
        bool Reaches(IEnumerable<string> raise, IEnumerable<string> registered)
            => registered.SelectMany(TddCallGraph.RaiseKeys).Intersect(raise).Any();

        Assert.True(Reaches(qtyRaise, qty));
        Assert.True(Reaches(qtyRaise, unattributed));
        Assert.False(Reaches(noteRaise, qty));
        Assert.True(Reaches(noteRaise, unattributed));
        Assert.True(Reaches(coarseRaise, qty));
        Assert.True(Reaches(TddCallGraph.ControlRaiseKeys(null, "Qty", "OnValidate"), qty));
        Assert.True(Reaches(TddCallGraph.PageRaiseKeys(null, new[] { "OnOpenPage" }), TddCallGraph.TriggerKeys("P", "OnOpenPage", null)));
        Assert.True(Reaches(TddCallGraph.PageRaiseKeys(null, new[] { "OnClosePage" }), new[] { "p|onclosepageevent" }));
        Assert.True(Reaches(TddCallGraph.PageRaiseKeys(null, new[] { "OnOpenPage" }), TddCallGraph.TriggerKeys("P", "OnBackground", null)));
        Assert.False(Reaches(TddCallGraph.PageRaiseKeys("Q", new[] { "OnOpenPage" }), TddCallGraph.TriggerKeys("P", "OnOpenPage", null)));
    }
}
