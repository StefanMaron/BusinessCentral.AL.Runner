// #5301: a TestPage writes records from the page runtime, so no AL call in the test names the table operation:
// typing a value into a field (SetValue, Value(x) or an assignment to Value) runs the table field's OnValidate and
// inserts, modifies or renames the record; OpenNew, New (a TestPart's too) and Activate start a new record. The graph records those calls as raises of the
// same keys a record operation raises (TddCallGraph.Triggers.cs), under the table the page or the field is bound
// to. Close, GoToRecord and the other moves save only what a SetValue or an OpenNew already made dirty, so they
// record nothing. docs/server-mode.md#tdd.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using Node = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.MethodOrTriggerDeclarationSyntax;

namespace AlRunner;

internal sealed partial class TddCallGraph
{
    // What the page runtime starts for each TestPage call that writes: every operation a typed value can
    // start (the first value of a new record inserts it, a later one modifies it, a key value of an existing
    // record renames it), and an insert for a new record. The page always runs the table's triggers.
    private static readonly string[] SetValueOperations = { "Validate", "Insert", "Modify", "Rename" };
    private static readonly string[] NewRecordOperations = { "Insert" };

    /// <summary>Records what a TestPage call starts, and says whether the call was one: a call on a TestPage
    /// or on one of its controls or actions is never a record operation or a Codeunit.Run, so the caller stops
    /// there. Of a control's members a write starts table code: SetValue, Value with an argument (the same
    /// setter), and Activate, which moves the focus and may insert a draft row; Lookup validates the selected
    /// value too (not measured, counted); Lookup, Drilldown, AssistEdit and Invoke start the field's own
    /// triggers, on the table and on the page; the reads (Value with none, AsInteger, AssertEquals, Caption,
    /// Editable, ...) start none. A page's own code is TddCallGraph.PageTriggers.cs (#5309).</summary>
    private bool AddTestPageOperation(NavCA.SemanticModel model, NavSyntax.MemberAccessExpressionSyntax mae,
        string name, int argumentCount, Node caller)
    {
        var symbol = model.GetSymbolInfo(mae.Expression).Symbol;
        string[] operations;
        string? table;
        if (symbol is NavCA.IControlSymbol control)
        {
            var typesValue = name.Equals("SetValue", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Value", StringComparison.OrdinalIgnoreCase) && argumentCount > 0;
            table = TableOfControl(control);
            AddControlPageOperation(control, name, typesValue, caller);
            if (typesValue) operations = SetValueOperations;
            else if (name.Equals("Activate", StringComparison.OrdinalIgnoreCase)) operations = NewRecordOperations;
            else if (name.Equals("Lookup", StringComparison.OrdinalIgnoreCase))
            {
                RaiseOperations(caller, SetValueOperations, table);
                RaiseTableTrigger(caller, table, "OnLookup");
                return true;
            }
            else
            {
                foreach (var trigger in FieldPageTriggers(name)) RaiseTableTrigger(caller, table, trigger);
                return true;
            }
        }
        else if (symbol is NavCA.IActionSymbol action)
        {
            AddActionPageOperation(action, name, caller);
            return true;
        }
        else if (TypeOf(symbol) is { NavTypeKind: NavCA.NavTypeKind.TestPage or NavCA.NavTypeKind.TestPart } pageType)
        {
            var page = pageType.NavTypeKind == NavCA.NavTypeKind.TestPart ? PageOfTestPart(pageType) : TestPageTarget(pageType);
            AddTestPagePageOperation(page, name, caller);
            if (!name.Equals("OpenNew", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("New", StringComparison.OrdinalIgnoreCase)) return true;
            operations = NewRecordOperations;
            table = TableOfPage(page);
        }
        else if (name.Equals("Invoke", StringComparison.OrdinalIgnoreCase) && AddBuiltInActionInvoke(model, mae, caller)) return true;
        else return false;

        RaiseOperations(caller, operations, table);
        return true;
    }

    /// <summary>The table-field triggers a page control's call starts: Drilldown and AssistEdit their own, Invoke
    /// any of the three; every other member of a control reads.</summary>
    private static IEnumerable<string> FieldPageTriggers(string name)
    {
        if (name.Equals("Drilldown", StringComparison.OrdinalIgnoreCase)) return new[] { "OnDrillDown" };
        if (name.Equals("AssistEdit", StringComparison.OrdinalIgnoreCase)) return new[] { "OnAssistEdit" };
        if (name.Equals("Invoke", StringComparison.OrdinalIgnoreCase)) return new[] { "OnLookup", "OnDrillDown", "OnAssistEdit" };
        return Array.Empty<string>();
    }

    private void RaiseTableTrigger(Node caller, string? table, string trigger)
        => Raise(caller, ProcKey(table ?? AnyObject, trigger));

    private void RaiseOperations(Node caller, string[] operations, string? table)
    {
        var owner = table ?? AnyObject;
        foreach (var operation in operations)
        {
            var op = RecordOperations[operation];
            Raise(caller, ProcKey(owner, op.Trigger));
            foreach (var ev in op.Events) Raise(caller, ProcKey(owner, ev));
        }
    }

    /// <summary>An assignment to a control's Value (<c>P.Qty.Value := '5'</c>) is not an
    /// invocation, so the walk over invocations never sees it: it types the value like SetValue. Returns the
    /// number of assignments that could not be read.</summary>
    private int CollectTestPageAssignments(NavCA.SemanticModel model, NavCA.SyntaxNode root)
    {
        var failed = 0;
        foreach (var statement in root.DescendantNodes().OfType<NavSyntax.AssignmentStatementSyntax>())
        {
            // Value is the one member of a control AL lets you assign to, and only with := (a compound assignment
            // is AL0129), so any assignment whose target is a member of a control is a typed value.
            if (statement.Target is not NavSyntax.MemberAccessExpressionSyntax mae) continue;
            try
            {
                var caller = EnclosingMethod(statement);
                if (caller != null && model.GetSymbolInfo(mae.Expression).Symbol is NavCA.IControlSymbol control)
                {
                    RaiseOperations(caller, SetValueOperations, TableOfControl(control));
                    AddControlPageOperation(control, "Value", typesValue: true, caller);
                }
            }
            catch { failed++; }
        }
        return failed;
    }

    /// <summary>The table a typed value is written to: the one the control's field belongs to (a table
    /// extension's field is its base table's), else the table of the page the control sits on. Null when
    /// neither is known: the operations then count for every table.</summary>
    private static string? TableOfControl(NavCA.IControlSymbol control)
    {
        if (control.RelatedFieldSymbol?.ContainingSymbol is { } owner)
        {
            if (owner is NavCA.IApplicationObjectExtensionTypeSymbol { Target: { } target } && target.Name.Length > 0)
                return target.Name;
            if (owner is NavCA.ITypeSymbol { NavTypeKind: NavCA.NavTypeKind.Record } table && table.Name.Length > 0)
                return table.Name;
        }
        for (NavCA.ISymbol? s = control.ContainingSymbol; s != null; s = s.ContainingSymbol)
        {
            if (s is NavCA.IPageTypeSymbol page) return TableOfPage(page);
            // A control a page extension adds, read from a dependency's symbols: no related field is kept,
            // and the extension's own page is the one it extends.
            if (s is NavCA.IApplicationObjectExtensionTypeSymbol { Target: NavCA.IPageTypeSymbol extended }) return TableOfPage(extended);
        }
        return null;
    }

    /// <summary>The page a TestPage variable is declared with. The symbol class that holds it is internal to
    /// the compiler, so it is read by name; when that finds nothing the table is unknown and the operations
    /// count for every table, which only ever annotates more.</summary>
    private static NavCA.ITypeSymbol? TestPageTarget(NavCA.ITypeSymbol testPage)
        => testPage.GetType().GetProperty("Target")?.GetValue(testPage) as NavCA.ITypeSymbol;

    /// <summary>The page a TestPart (a part control of a TestPage) shows, read the same way.</summary>
    private static NavCA.ITypeSymbol? PageOfTestPart(NavCA.ITypeSymbol testPart)
        => (testPart.GetType().GetProperty("ControlSymbol")?.GetValue(testPart) as NavCA.IControlSymbol)?.RelatedPartSymbol;

    private static string? TableOfPage(NavCA.ITypeSymbol? page)
        => (page as NavCA.IPageTypeSymbol)?.RelatedTable is { Name.Length: > 0 } table ? table.Name : null;
}
