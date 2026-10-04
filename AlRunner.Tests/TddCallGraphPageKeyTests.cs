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

    private static readonly string[] Open =
        { "OnInit", "OnOpenPage", "OnFindRecord", "OnNextRecord", "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnNewRecord" };
    private static readonly string[] Move =
        { "OnFindRecord", "OnNextRecord", "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnInsertRecord", "OnModifyRecord" };
    private static readonly string[] AllTriggers =
        {
            "OnInit", "OnOpenPage", "OnClosePage", "OnQueryClosePage", "OnNewRecord", "OnInsertRecord", "OnModifyRecord",
            "OnDeleteRecord", "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnFindRecord", "OnNextRecord",
        };

    /// <summary>What a TestPage method starts, whole: an open starts the open and row triggers, a move the row triggers and
    /// the save of the row it leaves, a New a new row and that save, a Close the close triggers and that save; a reader
    /// none; a method this list does not know every trigger (a later BC's method over-annotates and never misses).</summary>
    [Theory]
    [InlineData("OpenView")]
    [InlineData("OpenEdit")]
    [InlineData("OpenNew")]
    public void TestPageMethodTriggers_AnOpenStartsTheOpenAndRowTriggers(string method)
        => Assert.Equal(Open, TddCallGraph.TestPageMethodTriggers(method));

    [Theory]
    [InlineData("First")]
    [InlineData("Last")]
    [InlineData("Next")]
    [InlineData("Prev")]
    [InlineData("Previous")]
    [InlineData("GoToKey")]
    [InlineData("GoToRecord")]
    [InlineData("FindFirstField")]
    [InlineData("FindNextField")]
    [InlineData("FindPreviousField")]
    [InlineData("Expand")]
    public void TestPageMethodTriggers_AMoveStartsTheRowTriggersAndTheSave(string method)
        => Assert.Equal(Move, TddCallGraph.TestPageMethodTriggers(method));

    [Fact]
    public void TestPageMethodTriggers_NewCloseReadersAndUnknownMethods()
    {
        Assert.Equal(new[] { "OnNewRecord", "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnInsertRecord", "OnModifyRecord" },
            TddCallGraph.TestPageMethodTriggers("New"));
        Assert.Equal(new[] { "OnQueryClosePage", "OnClosePage", "OnInsertRecord", "OnModifyRecord" }, TddCallGraph.TestPageMethodTriggers("Close"));
        foreach (var reader in new[] { "Caption", "Editable", "GetField", "GetValidationError", "IsExpanded", "ValidationErrorCount", "Trap" })
            Assert.Empty(TddCallGraph.TestPageMethodTriggers(reader));
        Assert.Equal(AllTriggers, TddCallGraph.TestPageMethodTriggers("SomethingABcLaterAdds"));
        // The built-in actions a TestPage returns are not in the list: calling one starts every trigger of its page.
        foreach (var builtIn in new[] { "OK", "Cancel", "Yes", "No", "Edit", "View" })
            Assert.Equal(AllTriggers, TddCallGraph.TestPageMethodTriggers(builtIn));
    }

    private static void AssertCall(string[] control, string[] page, bool everyPage, string name, bool typesValue)
    {
        var call = TddCallGraph.ControlCall(name, typesValue);
        Assert.Equal(control, call.Control);
        Assert.Equal(page, call.Page);
        Assert.Equal(everyPage, call.EveryPage);
    }

    [Fact]
    public void ControlCall_NamesTheControlTriggersThePageTriggersAndWhetherEveryPageCounts()
    {
        var none = Array.Empty<string>();
        var save = new[] { "OnInsertRecord", "OnModifyRecord" };
        AssertCall(new[] { "OnValidate" }, save, false, "SetValue", true);
        AssertCall(new[] { "OnValidate" }, save, false, "Value", true);
        AssertCall(new[] { "OnLookup", "OnValidate" }, save, true, "Lookup", false);
        AssertCall(none, new[] { "OnInsertRecord" }, false, "Activate", false);
        AssertCall(new[] { "OnDrillDown" }, none, false, "Drilldown", false);
        AssertCall(new[] { "OnAssistEdit" }, none, false, "AssistEdit", false);
        AssertCall(new[] { "OnLookup", "OnDrillDown", "OnAssistEdit", "OnAction" }, none, false, "Invoke", false);
        AssertCall(none, none, false, "AsInteger", false);
        AssertCall(none, none, false, "Value", false);
    }

    /// <summary>What CurrPage starts in a page's code, whole.</summary>
    [Fact]
    public void CurrentPageTriggers_UpdateSaveAndCloseOnly()
    {
        Assert.Equal(new[] { "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnInsertRecord", "OnModifyRecord" }, TddCallGraph.CurrentPageTriggers("Update"));
        Assert.Equal(new[] { "OnInsertRecord", "OnModifyRecord" }, TddCallGraph.CurrentPageTriggers("SaveRecord"));
        Assert.Equal(new[] { "OnQueryClosePage", "OnClosePage", "OnInsertRecord", "OnModifyRecord" }, TddCallGraph.CurrentPageTriggers("Close"));
        Assert.Empty(TddCallGraph.CurrentPageTriggers("Caption"));
    }
}
