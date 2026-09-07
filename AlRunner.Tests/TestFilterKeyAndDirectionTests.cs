// TestFilterKeyAndDirectionTests — issue #3316.
//
// AL's `SomeTestPage.Filter` is BC's NavTestFilter, and every one of its five members is a
// straight pass-through to the runner's own ITestFilter: ALCurrentKey reads
// `filter.CurrentKey`, ALAscending reads and writes `filter.Ascending`, ALSetCurrentKey calls
// `filter.SetCurrentKeyFields`. So the semantics of the whole surface are ours, and nothing on
// BC's side can compensate for an implementation that stores what it is told and never acts on
// it — which is exactly what both implementations did: the key and the direction went into
// private fields with no consumer anywhere in AlRunner/, and CurrentKey rendered those fields'
// raw NUMBERS where BC renders a key naming its fields.
//
// These are RUNNER-MECHANISM tests. They pin that the two record-backed ITestFilter
// implementations DELEGATE to the NavRecord they are filtering rather than answering from a
// private field, and that the rendered-key parser handles the shapes BC's own rendering
// produces. The BEHAVIOURAL claims — what CurrentKey answers, that SetCurrentKey reorders the
// walk, that Ascending(false) reverses it — are measured against a real BC service tier in the
// al-language corpus, codeunit 60350 "Test TestFilter"
// (StefanMaron/BusinessCentral.AL.Language.Tests#229, tests/al-language/testfilter/
// TestTestFilter.al), which went 7 pass / 5 fail before this change and 12 pass after. Per
// .claude/rules/bc-behavior-tests-go-upstream.md those assertions belong there, not here.
//
// Cecil for the delegation half because the claim is about which member each accessor reaches:
// a behavioural restatement here would be asserting BC behaviour in the wrong repository, and
// a private field is invisible to a behavioural test right up until the moment it diverges.
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestFilterKeyAndDirectionTests
{
    private static TypeDefinition LoadType(System.Type type)
    {
        var asm = AssemblyDefinition.ReadAssembly(type.Assembly.Location);
        var cecilType = asm.MainModule.GetType(type.FullName);
        Assert.NotNull(cecilType);
        return cecilType!;
    }

    /// <summary>A nested type by simple name — RequestPageTestPage's filter is private.</summary>
    private static TypeDefinition NestedType(TypeDefinition outer, string name)
    {
        var t = outer.NestedTypes.FirstOrDefault(x => x.Name == name);
        Assert.NotNull(t);
        return t!;
    }

    private static MethodDefinition Method(TypeDefinition type, string name)
    {
        var m = type.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);
        Assert.NotNull(m);
        return m!;
    }

    private static bool Calls(MethodDefinition m, string memberName)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            && i.Operand is MethodReference mr && mr.Name == memberName);

    /// <summary>
    /// Whether the body reads or writes any field DECLARED BY ITS OWN TYPE. That is the shape
    /// this fix removes: a key or a direction parked in a private field of the filter is one
    /// the rowset never sees. Fields of other types (the NavRecord handle these accessors
    /// delegate through) are not what is being ruled out, so they are excluded by declaring
    /// type rather than by name.
    /// </summary>
    private static bool TouchesOwnField(MethodDefinition m, TypeDefinition owner)
        => m.Body.Instructions.Any(i =>
            (i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Stfld)
            && i.Operand is FieldReference fr
            && fr.DeclaringType.FullName == owner.FullName
            && fr.Name != "_record");

    // ── LiveNavTestPage: the page a TestPage over a real table is backed by ───────────

    // THE FIX, first half. SetCurrentKey must reach the record, or the page goes on walking
    // whatever key it opened on. ALSetCurrentKey is the NavRecord member AL's own
    // Record.SetCurrentKey resolves to, so the page and the record cannot pick different keys.
    [Fact]
    public void LivePage_SetCurrentKeyFields_InstallsTheKeyOnTheRecord()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var m = Method(type, "SetCurrentKeyFields");

        Assert.True(Calls(m, "ALSetCurrentKey"),
            "LiveNavTestPage.SetCurrentKeyFields must install the key on the NavRecord the page "
            + "walks — a key kept on the page is one the rowset never sorts by.");
        Assert.False(TouchesOwnField(m, type),
            "and it must not park the fields in a private field of the page instead, which is "
            + "the write-only store #3316 is about.");
    }

    // A key change moves which row is first, so the cursor has to follow — the same argument
    // SetFilter's RepositionAfterFilterChange already makes. Without it the page reports the
    // new key while still sitting on a row from the old order.
    [Fact]
    public void LivePage_SetCurrentKeyFields_RepositionsTheCursor()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        Assert.True(Calls(Method(type, "SetCurrentKeyFields"), "RepositionAfterFilterChange"),
            "a key change reorders the rowset, so SetCurrentKeyFields must reposition the "
            + "cursor exactly as SetFilter does.");
    }

    // THE FIX, second half. Ascending is a property, so both accessors are asserted: a getter
    // that reads the record while the setter writes a field would report a direction the walk
    // does not have, and the corpus's Ascending() round-trip would still pass.
    [Fact]
    public void LivePage_Ascending_ReadsAndWritesTheRecord()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var getter = Method(type, "get_Ascending");
        var setter = Method(type, "set_Ascending");

        Assert.True(Calls(getter, "get_ALAscending"),
            "the Ascending getter must report the direction the record is actually walking.");
        Assert.True(Calls(setter, "set_ALAscending"),
            "and the setter must change it on the record, not merely remember it.");
        Assert.False(TouchesOwnField(getter, type), "the getter must not answer from a page field.");
        Assert.False(TouchesOwnField(setter, type), "nor the setter store into one.");
        Assert.True(Calls(setter, "RepositionAfterFilterChange"),
            "reversing the order moves the first row, so the cursor must follow.");
    }

    // CurrentKey is what produced the observed '3' and '2, 3'. Delegating to ALCurrentKey is
    // what makes it name fields, and it is the same member AL's Record.CurrentKey() reads — so
    // a page and its record can never disagree about the key the page is on.
    [Fact]
    public void LivePage_CurrentKey_RendersTheRecordsOwnKey()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var getter = Method(type, "get_CurrentKey");

        Assert.True(Calls(getter, "get_ALCurrentKey"),
            "CurrentKey must render the record's own key, which names its fields — joining raw "
            + "field numbers is what #3316 reported.");
        Assert.False(Calls(getter, "Join"),
            "and it must not build the string itself out of field numbers.");
    }

    [Fact]
    public void LivePage_GetCurrentKeyFields_ReadsTheRecordRatherThanARememberedArgument()
    {
        var type = LoadType(typeof(AlRunner.LiveNavTestPage));
        var m = Method(type, "GetCurrentKeyFields");

        Assert.True(Calls(m, "Of"),
            "GetCurrentKeyFields must derive the numbers from the record's current key, so a "
            + "page whose key was never set through this interface still reports the key it "
            + "is actually on.");
        Assert.False(TouchesOwnField(m, type),
            "answering from a remembered argument list is the defect, not the fix.");
    }

    // ── RequestPageTestPage's data-item filter: the same shape, one file over ─────────
    //
    // A [RequestPageHandler] reaches this through `Rep.SomeDataItem.SetCurrentKey(...)`. The
    // filters already wrote through to the data item's NavRecord — which is what
    // NavReport.GetReportParameters serialises — while the key and the direction did not, so a
    // handler's SetCurrentKey looked like it worked and the data item came back in
    // primary-key order. Same defect, same fix; no separate issue was open for it.

    [Fact]
    public void DataItemFilter_SetCurrentKeyFields_InstallsTheKeyOnTheDataItemsRecord()
    {
        var outer = LoadType(typeof(AlRunner.Patches.RequestPageTestPage));
        var type = NestedType(outer, "DataItemTestFilter");
        var m = Method(type, "SetCurrentKeyFields");

        Assert.True(Calls(m, "ALSetCurrentKey"),
            "a handler's SetCurrentKey must reach the data item's own NavRecord — that record "
            + "is what the report body walks and what GetReportParameters serialises.");
        Assert.False(TouchesOwnField(m, type),
            "a key held on the filter is one the report never sees.");
    }

    [Fact]
    public void DataItemFilter_AscendingAndCurrentKey_DelegateToTheDataItemsRecord()
    {
        var outer = LoadType(typeof(AlRunner.Patches.RequestPageTestPage));
        var type = NestedType(outer, "DataItemTestFilter");

        Assert.True(Calls(Method(type, "get_Ascending"), "get_ALAscending"));
        Assert.True(Calls(Method(type, "set_Ascending"), "set_ALAscending"));
        Assert.True(Calls(Method(type, "get_CurrentKey"), "get_ALCurrentKey"),
            "CurrentKey must name the key's fields here too — the raw-number join was the same "
            + "code in both places.");
        Assert.False(Calls(Method(type, "get_CurrentKey"), "Join"));
    }

    // Both implementations must answer GetCurrentKeyFields through the SAME helper. Two copies
    // of a name-resolution walk is how they drift apart, and nothing behavioural would catch it
    // — the request-page path has no corpus test that reads a data item's key back.
    [Fact]
    public void BothImplementations_ResolveKeyFieldsThroughTheOneHelper()
    {
        var live = LoadType(typeof(AlRunner.LiveNavTestPage));
        var outer = LoadType(typeof(AlRunner.Patches.RequestPageTestPage));
        var dataItem = NestedType(outer, "DataItemTestFilter");

        foreach (var m in new[] { Method(live, "GetCurrentKeyFields"), Method(dataItem, "GetCurrentKeyFields") })
            Assert.True(
                m.Body.Instructions.Any(i =>
                    i.OpCode == OpCodes.Call && i.Operand is MethodReference mr
                    && mr.Name == "Of"
                    && mr.DeclaringType.Name == "TestFilterKeyFields"),
                $"{m.DeclaringType.Name}.GetCurrentKeyFields must go through TestFilterKeyFields.Of, "
                + "so the two cannot answer the same question differently.");
    }

    // ── the rendered-key parser ──────────────────────────────────────────────────────
    //
    // BC quotes any key field name that is not a bare identifier, so the fixture table's
    // primary key renders as a quoted "Entry No." while Rank renders bare. Both shapes have to
    // resolve, and the quotes are not part of the name.

    [Fact]
    public void SplitRenderedKey_StripsBcsQuotingAndSeparators()
    {
        Assert.Equal(new[] { "Entry No." }, AlRunner.TestFilterKeyFields.SplitRenderedKey("\"Entry No.\""));
        Assert.Equal(new[] { "Rank" }, AlRunner.TestFilterKeyFields.SplitRenderedKey("Rank"));
        Assert.Equal(new[] { "Grp", "Rank" }, AlRunner.TestFilterKeyFields.SplitRenderedKey("Grp,Rank"));
        Assert.Equal(new[] { "Grp", "Rank" }, AlRunner.TestFilterKeyFields.SplitRenderedKey("Grp, Rank"));
        Assert.Equal(new[] { "Grp", "Entry No." },
            AlRunner.TestFilterKeyFields.SplitRenderedKey("Grp, \"Entry No.\""));
    }

    // The negative direction. A page with no key rendered has no key fields to report, and the
    // empty answer must be empty rather than a one-element array holding "".
    [Fact]
    public void SplitRenderedKey_AnswersEmptyForNoKeyRatherThanABlankName()
    {
        Assert.Empty(AlRunner.TestFilterKeyFields.SplitRenderedKey(null));
        Assert.Empty(AlRunner.TestFilterKeyFields.SplitRenderedKey(""));
        Assert.Empty(AlRunner.TestFilterKeyFields.SplitRenderedKey("   "));
        Assert.Empty(AlRunner.TestFilterKeyFields.SplitRenderedKey(","));
        Assert.Equal(new[] { "Rank" }, AlRunner.TestFilterKeyFields.SplitRenderedKey("Rank,"));
    }

    // ── the base mock keeps its own store, and that is deliberate ────────────────────

    // MockITestPage has no record, so it has no rowset to sort and nothing to delegate to.
    // Its members stay a remembered store — but they must be VIRTUAL, because that is the only
    // reason LiveNavTestPage's overrides are reached at all. Non-virtual members here would
    // make every override above dead code that Cecil still sees.
    [Fact]
    public void BaseMock_ITestFilterKeyMembers_AreVirtualSoTheLivePageCanOverrideThem()
    {
        var type = LoadType(typeof(AlRunner.MockITestPage));

        foreach (var name in new[]
                 {
                     "SetCurrentKeyFields", "GetCurrentKeyFields",
                     "get_Ascending", "set_Ascending", "get_CurrentKey",
                 })
        {
            var m = Method(type, name);
            Assert.True(m.IsVirtual,
                $"MockITestPage.{name} must be virtual — LiveNavTestPage's override is what "
                + "makes the key and the direction reach the record.");
        }
    }
}
