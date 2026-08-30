// ActionRefTargetParserTests — RED→GREEN guard for #2113.
//
// An `actionref(X_Promoted; X)` in a page's `area(Promoted)` is a delegating REFERENCE, not
// an action. The AL grammar gives it nowhere to put a trigger: `PageActionRefSyntax` carries
// `Name`, `Target` and a property list, and — unlike `PageActionSyntax` — it does NOT derive
// from `PageActionWithTriggersBaseSyntax`. So the `*_OnAction` method BC emits belongs to the
// TARGET action and its member id hashes from the TARGET's name.
//
// `RecordPatches.ParseMemberNames` walked only `PageActionSyntax` nodes, so actionrefs were
// invisible to the parser entirely, and `RunnerPageInstance.RaiseOnAction` searched for a
// trigger whose member id equalled the ACTIONREF's own id. Nothing ever matched, and a
// promoted `Invoke()` was refused with "the page declares no OnAction trigger for this
// action" against an action that plainly declares one — while invoking the same action
// directly worked. Measured on the issue's own reproducer: member id 1270254389 is
// MemberId(79841, "DoStamp_Promoted"), the actionref's id; the emitted trigger's id is
// MemberId(79841, "DoStamp") == 903595246.
//
// These tests pin the PARSER half of the fix — the map that lets the dispatcher follow the
// reference. The end-to-end dispatch claim is proven in
// tests/runner-extras/testpage-promoted-actionref, and its plain-BC half upstream in the
// al-language corpus: StefanMaron/BusinessCentral.AL.Language.Tests#79, commit c98be548,
// eight tests green on real BC 27.5 and 28.3 (handlers/TestPagePromotedActionref*.al).
//
// The target is recorded by NAME rather than by id on purpose: a pageextension's
// `addlast(Promoted)` actionref may point at an action of the BASE page, whose member id
// hashes from the BASE page's object id, not the extension's. Storing an id would pick one of
// the two id spaces at parse time, before it is known which object declares the target.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public class ActionRefTargetParserTests
{
    private static readonly Type RP = typeof(AlRunner.Patches.RecordPatches);

    // This class owns 62440–62449.
    private const int TableId = 62441;
    private const int PageId = 62440;
    private const int PageExtId = 62442;

    private static void Parse(string method, string source) =>
        RP.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
          .Invoke(null, new object[] { source });

    private static System.Collections.IDictionary Dict(string field) =>
        (System.Collections.IDictionary)RP
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static void Cleanup()
    {
        Dict("_parsedTables").Remove(TableId);
        Dict("_parsedPages").Remove(PageId);
        Dict("_parsedPageExtensions").Remove(PageExtId);
    }

    /// <summary>BC's IdSpace.GetMemberId, reached on the real implementation by reflection so
    /// this cannot drift from the hash the runtime resolves actions with.</summary>
    private static int MemberId(int ancestorObjectId, string name)
    {
        var t = RP.Assembly.GetType("AlRunner.Patches.RunnerPageInstance")
            ?? throw new InvalidOperationException("AlRunner.Patches.RunnerPageInstance not found.");
        var m = t.GetMethod("MemberId", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RunnerPageInstance.MemberId not found.");
        return (int)m.Invoke(null, new object[] { ancestorObjectId, name })!;
    }

    private static string? Target(int declaringObjectId, int memberId, bool isExtension) =>
        (string?)RP.GetMethod("TryGetActionRefTarget", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { declaringObjectId, memberId, isExtension });

    private static readonly string TableSource = $$"""
        table {{TableId}} "AR Base Table"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """;

    private static readonly string PageSource = $$"""
        page {{PageId}} "AR List"
        {
            PageType = List;
            SourceTable = "AR Base Table";
            layout { area(content) { repeater(Rows) { field(CodeField; Rec."Code") { } } } }
            actions
            {
                area(Processing)
                {
                    action(FlatTarget) { trigger OnAction() begin end; }
                    group(Grouped)
                    {
                        action("Grouped Target") { trigger OnAction() begin end; }
                    }
                    action(TriggerlessTarget) { RunObject = page "AR List"; }
                }
                area(Promoted)
                {
                    actionref(FlatRef; FlatTarget) { }
                    group(Category_Process)
                    {
                        actionref(GroupedRef; "Grouped Target") { }
                    }
                    actionref(TriggerlessRef; TriggerlessTarget) { }
                }
            }
        }
        """;

    // The reported shape: an actionref directly in the page's own area(Promoted). Keyed by the
    // ACTIONREF's member id (what a TestPage Invoke() arrives with), valued by the TARGET's
    // declared name (what the emitted trigger is named after).
    [Fact]
    public void PageOwnPromotedActionRef_MapsItsMemberIdToTheTargetActionName()
    {
        try
        {
            Parse("TryParseTableFile", TableSource);
            Parse("TryParsePageFile", PageSource);

            Assert.Equal("FlatTarget", Target(PageId, MemberId(PageId, "FlatRef"), isExtension: false));
        }
        finally { Cleanup(); }
    }

    // The same reference nested inside a promoted category group — the layout real promoted
    // pages actually use. A parser that only looked at an area's direct children would find
    // FlatRef and miss this one. The quoted target name also pins that the TRUE declared name
    // survives: "Grouped Target" mangles to `Grouped_Target` on the emitted method, and that
    // mangling is not invertible (#1968), so recording the un-mangled form would resolve to a
    // different member id than BC's.
    [Fact]
    public void PromotedActionRefInsideAGroup_IsFoundAndKeepsItsTargetsSpacedName()
    {
        try
        {
            Parse("TryParseTableFile", TableSource);
            Parse("TryParsePageFile", PageSource);

            Assert.Equal("Grouped Target", Target(PageId, MemberId(PageId, "GroupedRef"), isExtension: false));
        }
        finally { Cleanup(); }
    }

    // A pageextension's actionrefs live in the EXTENSION's own id space, and may point either
    // at one of its own actions or at an action of the BASE page. Both directions are recorded
    // by name; nothing here commits to which object declares the target.
    [Fact]
    public void PageExtensionPromotedActionRefs_AreKeyedInTheExtensionsIdSpaceForBothTargets()
    {
        try
        {
            Parse("TryParseTableFile", TableSource);
            Parse("TryParsePageFile", PageSource);
            Parse("TryParsePageFile", $$"""
                pageextension {{PageExtId}} "AR List Ext" extends "AR List"
                {
                    actions
                    {
                        addlast(Processing)
                        {
                            action(ExtTarget) { trigger OnAction() begin end; }
                        }
                        addlast(Promoted)
                        {
                            actionref(ExtRefToExt; ExtTarget) { }
                            actionref(ExtRefToBase; FlatTarget) { }
                        }
                    }
                }
                """);

            Assert.Equal("ExtTarget",
                Target(PageExtId, MemberId(PageExtId, "ExtRefToExt"), isExtension: true));
            Assert.Equal("FlatTarget",
                Target(PageExtId, MemberId(PageExtId, "ExtRefToBase"), isExtension: true));

            // The extension's refs are NOT reachable in the base page's id space — a page and a
            // pageextension are separate objects with separate id namespaces (#1710), and
            // conflating them is what let one stand in for the other.
            Assert.Null(Target(PageId, MemberId(PageExtId, "ExtRefToExt"), isExtension: false));
            Assert.Null(Target(PageExtId, MemberId(PageId, "ExtRefToExt"), isExtension: true));
        }
        finally { Cleanup(); }
    }

    // Negative, and the reason the loud refusal survives the fix: an ACTION is not an
    // actionref, so nothing about it resolves through this map. A parser that answered a
    // target for every action would let RaiseOnAction "resolve" a triggerless RunObject action
    // to itself and run nothing, quietly — the failure mode loud-failures.md forbids.
    [Fact]
    public void OrdinaryActions_HaveNoActionRefTarget()
    {
        try
        {
            Parse("TryParseTableFile", TableSource);
            Parse("TryParsePageFile", PageSource);

            Assert.Null(Target(PageId, MemberId(PageId, "FlatTarget"), isExtension: false));
            Assert.Null(Target(PageId, MemberId(PageId, "TriggerlessTarget"), isExtension: false));
            Assert.Null(Target(PageId, MemberId(PageId, "NotDeclaredAtAll"), isExtension: false));
        }
        finally { Cleanup(); }
    }

    // A ref pointing at a triggerless action IS still a ref: the map must resolve it, so the
    // refusal that follows can name the target instead of blaming the actionref for carrying
    // no trigger of its own (which is true of every actionref, and says nothing).
    [Fact]
    public void ActionRefToATriggerlessAction_StillResolvesToItsTarget()
    {
        try
        {
            Parse("TryParseTableFile", TableSource);
            Parse("TryParsePageFile", PageSource);

            Assert.Equal("TriggerlessTarget",
                Target(PageId, MemberId(PageId, "TriggerlessRef"), isExtension: false));
        }
        finally { Cleanup(); }
    }
}
