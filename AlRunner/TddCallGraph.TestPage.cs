// #5301: a TestPage writes records from the page runtime, so no AL call in the test names the table operation:
// typing a value into a field (SetValue) runs the table field's OnValidate and inserts, modifies or renames
// the record; OpenNew and New start a new record. The graph records those calls as raises of the same keys a
// record operation raises (TddCallGraph.Triggers.cs), under the table the page or the field is bound to.
// Close, GoToRecord and the other moves write only what a SetValue already made dirty, so they record nothing.
// docs/server-mode.md#tdd.
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
    /// or on one of its controls is never a record operation or a Codeunit.Run, so the caller stops there.</summary>
    private bool AddTestPageOperation(NavCA.SemanticModel model, NavSyntax.MemberAccessExpressionSyntax mae,
        string name, Node caller)
    {
        var symbol = model.GetSymbolInfo(mae.Expression).Symbol;
        string[] operations;
        string? table;
        if (symbol is NavCA.IControlSymbol control)
        {
            if (!name.Equals("SetValue", StringComparison.OrdinalIgnoreCase)) return true;
            operations = SetValueOperations;
            table = TableOfControl(control);
        }
        else if (TypeOf(symbol) is { NavTypeKind: NavCA.NavTypeKind.TestPage } pageType)
        {
            if (!name.Equals("OpenNew", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("New", StringComparison.OrdinalIgnoreCase)) return true;
            operations = NewRecordOperations;
            table = TableOfPage(TestPageTarget(pageType));
        }
        else return false;

        var owner = table ?? AnyObject;
        foreach (var operation in operations)
        {
            var op = RecordOperations[operation];
            Raise(caller, ProcKey(owner, op.Trigger));
            foreach (var ev in op.Events) Raise(caller, ProcKey(owner, ev));
        }
        return true;
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
            if (s is NavCA.IPageTypeSymbol page) return TableOfPage(page);
        return null;
    }

    /// <summary>The page a TestPage variable is declared with. The symbol class that holds it is internal to
    /// the compiler, so it is read by name; when that finds nothing the table is unknown and the operations
    /// count for every table, which only ever annotates more.</summary>
    private static NavCA.ITypeSymbol? TestPageTarget(NavCA.ITypeSymbol testPage)
        => testPage.GetType().GetProperty("Target")?.GetValue(testPage) as NavCA.ITypeSymbol;

    private static string? TableOfPage(NavCA.ITypeSymbol? page)
        => (page as NavCA.IPageTypeSymbol)?.RelatedTable is { Name.Length: > 0 } table ? table.Name : null;
}
