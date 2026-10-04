// #5309: a page's own code runs from the page runtime, so no AL call in the test names it: a TestPage operation
// starts the page's triggers and the platform events around them (OnOpenPage, OnAfterGetRecord, OnClosePage ...),
// a control's SetValue / Lookup / Drilldown / AssistEdit its field's page triggers, and Invoke on an action its
// OnAction. The graph records each as a raise of a key under the PAGE'S name, the way a record operation raises a
// table's (TddCallGraph.Triggers.cs): a page trigger is something that key reaches, and a subscriber of a page
// event is an external subscriber of it. Which operation runs which trigger was measured on the runner's own
// page runtime (BC's) and is settled for BC by the corpus: handlers/TestPageTriggerEvents.al orders the
// triggers and events, pageextensiontrigger/TestPageExtensionPageTriggers_Tests.al runs a page extension's per
// row. docs/server-mode.md#tdd.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using Node = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.MethodOrTriggerDeclarationSyntax;

namespace AlRunner;

internal sealed partial class TddCallGraph
{
    // The key a trigger no operation is known to start is raised under, by every page operation.
    private const string PageCode = "OnPageCode";

    // What each kind of TestPage operation can start. Over-approximations, never under: a row move saves a row a
    // SetValue made dirty, so it can run the page's own OnInsertRecord / OnModifyRecord (OnModifyRecord ran at
    // Close in the probe, and the corpus shows the save at a move and at Close); OnNewRecord is only measured
    // for the open of a new record and a New, and counts there only.
    private static readonly string[] OpenOps =
        { "OnInit", "OnOpenPage", "OnFindRecord", "OnNextRecord", "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnNewRecord" };
    private static readonly string[] MoveOps =
        { "OnFindRecord", "OnNextRecord", "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnInsertRecord", "OnModifyRecord" };
    private static readonly string[] NewRowOps =
        { "OnNewRecord", "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnInsertRecord", "OnModifyRecord" };
    private static readonly string[] CloseOps = { "OnQueryClosePage", "OnClosePage", "OnInsertRecord", "OnModifyRecord" };
    private static readonly string[] TypedOps = { "OnInsertRecord", "OnModifyRecord", "OnNewRecord" };
    private static readonly string[] FocusOps = { "OnNewRecord", "OnInsertRecord", "OnAfterGetRecord", "OnAfterGetCurrRecord" };
    private static readonly string[] AllOps =
        {
            "OnInit", "OnOpenPage", "OnClosePage", "OnQueryClosePage", "OnNewRecord", "OnInsertRecord", "OnModifyRecord",
            "OnDeleteRecord", "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnFindRecord", "OnNextRecord",
        };

    // The platform event a page publishes around a trigger of that name (handlers/TestPageTriggerEvents.al and the
    // other page events BC documents): raised by the operation that runs the trigger, after it.
    private static readonly Dictionary<string, string> PageEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OnOpenPage"] = "OnOpenPageEvent", ["OnClosePage"] = "OnClosePageEvent",
        ["OnQueryClosePage"] = "OnQueryClosePageEvent", ["OnNewRecord"] = "OnNewRecordEvent",
        ["OnInsertRecord"] = "OnInsertRecordEvent", ["OnModifyRecord"] = "OnModifyRecordEvent",
        ["OnDeleteRecord"] = "OnDeleteRecordEvent", ["OnAfterGetRecord"] = "OnAfterGetRecordEvent",
        ["OnAfterGetCurrRecord"] = "OnAfterGetCurrRecordEvent",
    };

    // The names a trigger of a control carries: its raise is keyed by the control as well, so Invoke on one action
    // reaches that action's OnAction and no other (UnknownControl: a trigger the graph could not attribute to a
    // control, which every control raise reaches).
    private static readonly string[] ControlTriggers = { "OnValidate", "OnLookup", "OnDrillDown", "OnAssistEdit", "OnAction" };
    private const string UnknownControl = "@?";

    // A TestPage method that runs no page code: the ones that read the page or return an action to Invoke later.
    private static readonly HashSet<string> TestPageReaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Caption", "Editable", "GetField", "GetValidationError", "IsExpanded", "ValidationErrorCount", "Trap",
        "OK", "Cancel", "Yes", "No", "Edit", "View",
    };

    private static readonly Dictionary<string, string[]> TestPageOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenView"] = OpenOps, ["OpenEdit"] = OpenOps, ["OpenNew"] = OpenOps, ["Close"] = CloseOps, ["New"] = NewRowOps,
        ["First"] = MoveOps, ["Last"] = MoveOps, ["Next"] = MoveOps, ["Prev"] = MoveOps, ["Previous"] = MoveOps,
        ["GoToKey"] = MoveOps, ["GoToRecord"] = MoveOps, ["FindFirstField"] = MoveOps, ["FindNextField"] = MoveOps,
        ["FindPreviousField"] = MoveOps, ["Expand"] = MoveOps,
    };

    /// <summary>True for the key names a page, a TestPage operation or a control raises: the page triggers, the
    /// platform events around them and the control triggers. A raise whose page the graph could not read answers
    /// every page's (<see cref="WildcardOf"/>). Read at call time, not folded into a static set, because the
    /// arrays live in this file and a static initialiser in another file of the partial class may run first.</summary>
    private static bool IsPageEntryPoint(string name)
        => name.Equals(PageCode, StringComparison.OrdinalIgnoreCase)
            || AllOps.Contains(name, StringComparer.OrdinalIgnoreCase)
            || ControlTriggers.Contains(name, StringComparer.OrdinalIgnoreCase)
            || PageEvents.Values.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The trigger a page or page extension trigger is, by the name a raise uses: a lifecycle trigger or
    /// event trigger by its own name, a control trigger with an OnBefore/OnAfter prefix (a page extension's
    /// <c>OnAfterValidate</c> of a modify block) as the control trigger. Null for a name this list does not know.</summary>
    internal static string? PageTriggerName(string name)
    {
        foreach (var known in AllOps.Concat(ControlTriggers))
            if (known.Equals(name, StringComparison.OrdinalIgnoreCase)) return known;
        foreach (var prefix in new[] { "OnBefore", "OnAfter" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                foreach (var known in ControlTriggers)
                    if (known.Equals("On" + name[prefix.Length..], StringComparison.OrdinalIgnoreCase)) return known;
        return null;
    }

    private void CollectPageTriggers(NavSyntax.ObjectSyntax obj)
    {
        foreach (var node in obj.DescendantNodes().OfType<Node>())
        {
            if (node is NavSyntax.MethodDeclarationSyntax) continue;
            var page = KeyObjectName(obj, node);
            foreach (var key in TriggerKeys(page, Name(node.Name), ControlNameOf(node)))
                _triggers.Add((node, key));
        }
    }

    /// <summary>The keys that start a trigger of <paramref name="page"/> named <paramref name="name"/>, sitting in the
    /// control <paramref name="control"/> (null: not in one the graph could read). A trigger no operation is known to
    /// start answers only the key every page operation raises. A control trigger answers a raise keyed by its control,
    /// and the coarse raise a call that names no control makes; one in no readable control answers every control's
    /// raise, so a control the graph misattributes can only over-annotate.</summary>
    internal static IReadOnlyList<string> TriggerKeys(string page, string name, string? control)
    {
        if (PageTriggerName(name) is not { } trigger) return new[] { ProcKey(page, PageCode) };
        if (!ControlTriggers.Contains(trigger, StringComparer.OrdinalIgnoreCase)) return new[] { ProcKey(page, trigger) };
        return new[] { ProcKey(page, trigger), ProcKey(page, trigger + (control is { Length: > 0 } ? "@" + control : UnknownControl)) };
    }

    /// <summary>The keys a call raises for <paramref name="trigger"/> of the control <paramref name="control"/> on
    /// <paramref name="page"/>: that control's, and the ones in no readable control; without a page or a control name,
    /// the coarse key of every control of every page (<see cref="WildcardOf"/>); and the one every page operation
    /// raises.</summary>
    internal static IReadOnlyList<string> ControlRaiseKeys(string? page, string? control, string trigger)
    {
        var owner = page ?? AnyObject;
        return page != null && !string.IsNullOrEmpty(control)
            ? new[] { ProcKey(owner, trigger + "@" + control), ProcKey(owner, trigger + UnknownControl), ProcKey(owner, PageCode) }
            : new[] { ProcKey(owner, trigger), ProcKey(owner, PageCode) };
    }

    /// <summary>The keys a page operation raises for the page triggers <paramref name="triggers"/> of
    /// <paramref name="page"/> (every page when it is null): each trigger, the platform event around it, and the one
    /// key every page operation raises.</summary>
    internal static IReadOnlyList<string> PageRaiseKeys(string? page, IEnumerable<string> triggers)
    {
        var owner = page ?? AnyObject;
        var keys = new List<string>();
        foreach (var trigger in triggers)
        {
            keys.Add(ProcKey(owner, trigger));
            if (PageEvents.TryGetValue(trigger, out var ev)) keys.Add(ProcKey(owner, ev));
        }
        keys.Add(ProcKey(owner, PageCode));
        return keys;
    }

    /// <summary>The field or action a trigger sits in, or the control a page extension's modify block names. Only
    /// these syntaxes are read; a trigger in anything else is not attributed, which reads as reached by every
    /// control raise (over-annotating, never missing). The modify block's class is read by name: a BC that
    /// renames it degrades to that, and the precision tests see it.</summary>
    private static string? ControlNameOf(Node trigger)
    {
        for (NavCA.SyntaxNode? n = trigger.Parent; n != null && n is not NavSyntax.ObjectSyntax; n = n.Parent)
        {
            if (n is NavSyntax.PageFieldSyntax field) return Name(field.Name as NavSyntax.IdentifierNameSyntax) is { Length: > 0 } f ? f : null;
            if (n is NavSyntax.PageActionSyntax action) return Name(action.Name as NavSyntax.IdentifierNameSyntax) is { Length: > 0 } a ? a : null;
            if (n.GetType().Name.EndsWith("ModifyChangeSyntax", StringComparison.Ordinal)
                && n.GetType().GetProperty("Name")?.GetValue(n) is NavSyntax.IdentifierNameSyntax modified)
                return Name(modified) is { Length: > 0 } m ? m : null;
        }
        return null;
    }

    private void RaisePage(Node caller, string? page, IEnumerable<string> triggers)
    {
        foreach (var key in PageRaiseKeys(page, triggers)) Raise(caller, key);
    }

    private void RaiseControlTrigger(Node caller, string? page, string? control, string trigger)
    {
        foreach (var key in ControlRaiseKeys(page, control, trigger)) Raise(caller, key);
    }

    /// <summary>The page a control or an action sits on: the one it is declared on, or the page a page extension
    /// extends. Null when the symbols do not say.</summary>
    private static string? PageOfSymbol(NavCA.ISymbol? symbol)
    {
        for (var s = symbol?.ContainingSymbol; s != null; s = s.ContainingSymbol)
        {
            if (s is NavCA.IApplicationObjectExtensionTypeSymbol { Target: NavCA.IPageTypeSymbol extended })
                return extended.Name.Length > 0 ? extended.Name : null;
            if (s is NavCA.IPageTypeSymbol page) return page.Name.Length > 0 ? page.Name : null;
        }
        return null;
    }

    /// <summary>What a call on a control starts on its page, and on the table of its field. SetValue, Value with an
    /// argument and an assignment type a value (the field's validate, and the row saved when the focus leaves it),
    /// Lookup also validates the selected value (not measured; counted), Drilldown and AssistEdit run their own
    /// trigger only, and Invoke on a field is any of its triggers (a probe saw it run none: counted, an
    /// over-approximation).</summary>
    private void AddControlPageOperation(NavCA.IControlSymbol control, string name, bool typesValue, Node caller)
    {
        var page = PageOfSymbol(control);
        var field = control.Name;
        if (typesValue || name.Equals("Lookup", StringComparison.OrdinalIgnoreCase))
        {
            // A lookup of a field with a table relation opens the related table's lookup page, which the graph cannot
            // name: its triggers run too (probe: a [ModalPageHandler] for it saw the page's OnOpenPage run).
            if (name.Equals("Lookup", StringComparison.OrdinalIgnoreCase))
            {
                RaiseControlTrigger(caller, page, field, "OnLookup");
                RaisePage(caller, null, AllOps);
            }
            RaiseControlTrigger(caller, page, field, "OnValidate");
            RaisePage(caller, page, TypedOps);
        }
        else if (name.Equals("Activate", StringComparison.OrdinalIgnoreCase)) RaisePage(caller, page, FocusOps);
        else if (name.Equals("Drilldown", StringComparison.OrdinalIgnoreCase)) RaiseControlTrigger(caller, page, field, "OnDrillDown");
        else if (name.Equals("AssistEdit", StringComparison.OrdinalIgnoreCase)) RaiseControlTrigger(caller, page, field, "OnAssistEdit");
        else if (name.Equals("Invoke", StringComparison.OrdinalIgnoreCase))
            foreach (var trigger in new[] { "OnLookup", "OnDrillDown", "OnAssistEdit", "OnAction" })
                RaiseControlTrigger(caller, page, field, trigger);
    }

    /// <summary>Invoke on an action starts its OnAction, whatever else it does: a page opened by its
    /// RunObject is not followed.</summary>
    private void AddActionPageOperation(NavCA.IActionSymbol action, string name, Node caller)
    {
        if (name.Equals("Invoke", StringComparison.OrdinalIgnoreCase))
            RaiseControlTrigger(caller, PageOfSymbol(action), action.Name, "OnAction");
    }

    /// <summary>The page itself and every page it hosts as a part, in a group or not: opening or moving a page runs
    /// the open and row triggers of its parts' pages as well (a part's OnOpenPage runs with its parent's, corpus
    /// testpart/TestPartOnOpenPageError.al). A part page cannot host a part (AL0215), so there is no deeper level.</summary>
    private static List<NavCA.ITypeSymbol> WithParts(NavCA.ITypeSymbol page)
    {
        var pages = new List<NavCA.ITypeSymbol> { page };
        void Walk(NavCA.IControlSymbol c)
        {
            if (c.RelatedPartSymbol is { } part && !pages.Any(x => x.Name == part.Name)) pages.Add(part);
            foreach (var child in c.Controls) Walk(child);
        }
        foreach (var control in page.GetMembers().OfType<NavCA.IControlSymbol>()) Walk(control);
        return pages;
    }

    /// <summary>What a TestPage (or TestPart) method starts on its page. A name the lists do not know starts
    /// every trigger of the page: a method a later BC adds over-annotates and never misses.</summary>
    private void AddTestPagePageOperation(NavCA.ITypeSymbol? page, string name, Node caller)
    {
        if (TestPageReaders.Contains(name)) return;
        var ops = TestPageOperations.TryGetValue(name, out var known) ? known : AllOps;
        if (page == null)
        {
            RaisePage(caller, null, ops);
            return;
        }
        foreach (var hosted in WithParts(page))
            RaisePage(caller, hosted.Name.Length > 0 ? hosted.Name : null, ops);
    }

    // What a page's own code asks of its page through CurrPage: a save runs the page's insert or modify trigger
    // (probe: an action that assigns Rec and calls CurrPage.SaveRecord or CurrPage.Update(true) ran OnModifyRecord
    // and its event; corpus handlers/TestPageTriggerEvents.al DirectSave / UpdateSave), Update reads the row again.
    private static readonly string[] SaveOps = { "OnInsertRecord", "OnModifyRecord" };
    private static readonly string[] UpdateOps = { "OnAfterGetRecord", "OnAfterGetCurrRecord", "OnInsertRecord", "OnModifyRecord" };

    /// <summary>The page whose code <paramref name="caller"/> is: the page itself, the page a page extension
    /// extends (also from a procedure of the extension). Null elsewhere.</summary>
    private string? EnclosingPage(Node caller)
    {
        for (NavCA.SyntaxNode? n = caller; n != null; n = n.Parent)
            if (n is NavSyntax.ObjectSyntax o)
                return o is NavSyntax.PageSyntax ? Name(o.Name)
                    : o is NavSyntax.PageExtensionSyntax && _extensionBase.TryGetValue(o, out var basePage) && basePage.Length > 0 ? basePage
                    : null;
        return null;
    }

    /// <summary>Records <c>CurrPage.Update</c>, <c>SaveRecord</c> and <c>Close</c> in a page's code as raises of
    /// the page's own triggers. Says whether the call was one.</summary>
    private bool AddCurrentPageOperation(NavSyntax.MemberAccessExpressionSyntax mae, string name, Node caller)
    {
        if (mae.Expression is not NavSyntax.IdentifierNameSyntax receiver
            || !Name(receiver).Equals("CurrPage", StringComparison.OrdinalIgnoreCase)) return false;
        if (EnclosingPage(caller) is not { } page) return false;
        if (name.Equals("Update", StringComparison.OrdinalIgnoreCase)) RaisePage(caller, page, UpdateOps);
        else if (name.Equals("SaveRecord", StringComparison.OrdinalIgnoreCase)) RaisePage(caller, page, SaveOps);
        else if (name.Equals("Close", StringComparison.OrdinalIgnoreCase)) RaisePage(caller, page, CloseOps);
        return true;
    }

    /// <summary>Records <c>Page.Run</c> / <c>Page.RunModal</c> of a page named by <c>Page::"Name"</c>, and
    /// <c>Run</c> / <c>RunModal</c> on a Page variable: the page opens and closes in the request, so every one of
    /// its triggers (and its parts') can run, whether or not the test drives it through a handler (a handler
    /// that replaces the page runs none: counted, an over-approximation). A page named by an id or an expression
    /// counts as every page. Says whether the call was one.</summary>
    private bool AddPageRun(NavCA.SemanticModel model, NavSyntax.InvocationExpressionSyntax inv,
        NavSyntax.MemberAccessExpressionSyntax mae, string name, Node caller)
    {
        if (!name.Equals("Run", StringComparison.OrdinalIgnoreCase) && !name.Equals("RunModal", StringComparison.OrdinalIgnoreCase))
            return false;
        var symbol = model.GetSymbolInfo(mae.Expression).Symbol;
        NavCA.ITypeSymbol? page;
        if (symbol is NavCA.IVariableSymbol or NavCA.IParameterSymbol or NavCA.IFieldSymbol
            && TypeOf(symbol) is { NavTypeKind: NavCA.NavTypeKind.Page } variableType)
            page = variableType;
        else if (mae.Expression is NavSyntax.IdentifierNameSyntax id && Name(id).Equals("Page", StringComparison.OrdinalIgnoreCase))
            page = inv.ArgumentList.Arguments.Count > 0
                ? model.GetSymbolInfo(inv.ArgumentList.Arguments[0]).Symbol as NavCA.IPageTypeSymbol
                : null;
        else return false;
        if (page == null)
        {
            RaisePage(caller, null, AllOps);
            return true;
        }
        foreach (var hosted in WithParts(page))
            RaisePage(caller, hosted.Name.Length > 0 ? hosted.Name : null, AllOps);
        return true;
    }

    /// <summary>Invoke on a built-in action a TestPage returns (<c>P.OK().Invoke()</c>): OK, Cancel, Yes and No close
    /// the page, so they start its close triggers and the save of the row; Edit and View open a card page the
    /// graph cannot name, so they count for every page.</summary>
    private bool AddBuiltInActionInvoke(NavCA.SemanticModel model, NavSyntax.MemberAccessExpressionSyntax invoke, Node caller)
    {
        if (invoke.Expression is not NavSyntax.InvocationExpressionSyntax { Expression: NavSyntax.MemberAccessExpressionSyntax builtIn }
            || TypeOf(model.GetSymbolInfo(builtIn.Expression).Symbol) is not { NavTypeKind: NavCA.NavTypeKind.TestPage or NavCA.NavTypeKind.TestPart } pageType)
            return false;
        var action = Name(builtIn.Name as NavSyntax.IdentifierNameSyntax);
        if (action.Equals("Edit", StringComparison.OrdinalIgnoreCase) || action.Equals("View", StringComparison.OrdinalIgnoreCase))
        {
            RaisePage(caller, null, AllOps);
            return true;
        }
        var page = pageType.NavTypeKind == NavCA.NavTypeKind.TestPart ? PageOfTestPart(pageType) : TestPageTarget(pageType);
        AddTestPagePageOperation(page, "Close", caller);
        return true;
    }
}
