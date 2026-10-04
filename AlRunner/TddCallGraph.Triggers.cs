// #5286: what a table operation or Codeunit.Run starts is not a call the semantic model binds to a
// procedure: Rec.Insert(true) runs the table's OnInsert trigger and raises its database events, and
// Codeunit.Run runs the target's OnRun. The graph records the CALL as a raise of a key (the trigger's or the
// event's ProcKey), and a trigger or a subscriber of a database event is something that key reaches, which is
// how a publisher that no bundle declares is already followed (#5264). docs/server-mode.md#tdd.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using Node = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.MethodOrTriggerDeclarationSyntax;

namespace AlRunner;

internal sealed partial class TddCallGraph
{
    // The triggers this compile declares that a table operation or Codeunit.Run starts, with the key a
    // raise of them is recorded under. A tableextension's trigger runs for its base table, so it is
    // keyed by that table (KeyObjectName).
    private readonly List<(Node Trigger, string Key)> _triggers = new();
    private readonly Dictionary<NavSyntax.ObjectSyntax, string> _extensionBase = new();
    private readonly HashSet<(Node Caller, string Key)> _raised = new();

    /// <summary>What one record method starts: its trigger, the database events around it, and the
    /// argument that says whether the trigger runs (-1: always). An omitted or literal false
    /// RunTrigger runs no trigger; the events count as raised whatever it says.</summary>
    private readonly record struct RecordOperation(string Trigger, string[] Events, int RunTriggerArgument);

    private static readonly Dictionary<string, RecordOperation> RecordOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Insert"] = new("OnInsert", new[] { "OnBeforeInsertEvent", "OnAfterInsertEvent" }, 0),
        ["Modify"] = new("OnModify", new[] { "OnBeforeModifyEvent", "OnAfterModifyEvent" }, 0),
        ["Delete"] = new("OnDelete", new[] { "OnBeforeDeleteEvent", "OnAfterDeleteEvent" }, 0),
        ["DeleteAll"] = new("OnDelete", new[] { "OnBeforeDeleteEvent", "OnAfterDeleteEvent" }, 0),
        ["ModifyAll"] = new("OnModify", new[] { "OnBeforeModifyEvent", "OnAfterModifyEvent" }, 2),
        ["Rename"] = new("OnRename", new[] { "OnBeforeRenameEvent", "OnAfterRenameEvent" }, -1),
        ["Validate"] = new("OnValidate", new[] { "OnBeforeValidateEvent", "OnAfterValidateEvent" }, -1),
    };

    // The names a raise is recorded under, whoever the receiver is: the entry points above.
    private static readonly HashSet<string> EntryPointNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnInsert", "OnModify", "OnDelete", "OnRename", "OnValidate", "OnRun",
        "OnBeforeInsertEvent", "OnAfterInsertEvent", "OnBeforeModifyEvent", "OnAfterModifyEvent",
        "OnBeforeDeleteEvent", "OnAfterDeleteEvent", "OnBeforeRenameEvent", "OnAfterRenameEvent",
        "OnBeforeValidateEvent", "OnAfterValidateEvent",
    };

    private const string AnyObject = "*";

    // The trigger a record operation starts, by the operation's own name; a table extension names its triggers
    // OnBefore<Op>/OnAfter<Op> (and a modify() block OnBeforeValidate/OnAfterValidate), which the same
    // operation starts under the same RunTrigger rule.
    private static readonly string[] OperationTriggers = { "OnInsert", "OnModify", "OnDelete", "OnRename", "OnValidate" };

    /// <summary>The keys that start a trigger a table or table extension declares under
    /// <paramref name="name"/>: an operation trigger (<c>OnInsert</c> ...) for a record operation, and for a
    /// lookup, a drill-down or an assist-edit the key a control's Lookup, Drilldown or AssistEdit raises on the
    /// table of its field (#5309). A name that is none of them is read as started by every operation: an
    /// unrecognised name over-annotates and never misses (#5286).</summary>
    internal static IReadOnlyList<string> StartedBy(string name)
    {
        var op = name;
        foreach (var prefix in new[] { "OnBefore", "OnAfter" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                op = "On" + name[prefix.Length..];
        foreach (var known in OperationTriggers)
            if (known.Equals(op, StringComparison.OrdinalIgnoreCase)) return new[] { known };
        foreach (var pageStarted in new[] { "Lookup", "DrillDown", "AssistEdit" })
            if (name.Contains(pageStarted, StringComparison.OrdinalIgnoreCase)) return new[] { "On" + pageStarted };
        return OperationTriggers;
    }

    /// <summary>The key a raise is recorded under when its receiver names no table or codeunit the graph
    /// can read (a RecordRef, a FieldRef, a codeunit id that is not a literal object reference): every
    /// entry point of that name answers it.</summary>
    private static string WildcardKey(string entryPoint) => ProcKey(AnyObject, entryPoint);

    /// <summary>The wildcard key for <paramref name="key"/>, when it is an entry point's key.</summary>
    private static string? WildcardOf(string key)
    {
        var bar = key.IndexOf('|');
        return bar > 0 && (EntryPointNames.Contains(key[(bar + 1)..]) || IsPageEntryPoint(key[(bar + 1)..])) && key[..bar] != AnyObject
            ? WildcardKey(key[(bar + 1)..])
            : null;
    }

    /// <summary>The keys whose raisers reach something keyed by <paramref name="key"/>: the key itself and,
    /// for an entry point, the wildcard.</summary>
    internal static IEnumerable<string> RaiseKeys(string key)
    {
        yield return key;
        if (WildcardOf(key) is { } wildcard) yield return wildcard;
    }

    private string KeyObjectName(NavSyntax.ObjectSyntax o, Node method)
        => method is not NavSyntax.MethodDeclarationSyntax && _extensionBase.TryGetValue(o, out var baseTable)
            ? baseTable
            : Name(o.Name);

    private void CollectTriggers(NavCA.SemanticModel model, NavCA.SyntaxNode root)
    {
        foreach (var obj in root.DescendantNodesAndSelf().OfType<NavSyntax.ObjectSyntax>())
        {
            if (obj is NavSyntax.TableExtensionSyntax or NavSyntax.PageExtensionSyntax)
                _extensionBase[obj] = ExtensionBase(model, obj);
            if (obj is NavSyntax.PageSyntax or NavSyntax.PageExtensionSyntax)
            {
                CollectPageTriggers(obj);
                continue;
            }
            if (obj is not (NavSyntax.TableSyntax or NavSyntax.TableExtensionSyntax or NavSyntax.CodeunitSyntax)) continue;
            foreach (var node in obj.DescendantNodes().OfType<Node>())
            {
                if (node is NavSyntax.MethodDeclarationSyntax) continue;
                var name = Name(node.Name);
                if (obj is NavSyntax.CodeunitSyntax)
                {
                    if (name.Equals("OnRun", StringComparison.OrdinalIgnoreCase) && KeyOf(node) is { } runKey) _triggers.Add((node, runKey));
                    continue;
                }
                var objectName = KeyObjectName(obj, node);
                foreach (var trigger in StartedBy(name))
                    _triggers.Add((node, ProcKey(objectName, trigger)));
            }
        }
    }

    private static string ExtensionBase(NavCA.SemanticModel model, NavSyntax.ObjectSyntax ext)
    {
        // The symbol knows the table or page whether the extension names it, qualifies it with a namespace or gives its id.
        if (model.GetDeclaredSymbol(ext) is NavCA.IApplicationObjectExtensionTypeSymbol { Target: { } target }
            && target.Name.Length > 0)
            return target.Name;
        // A page extension has no text fallback: the compiler's symbol resolves every extension that compiles.
        return Unquote((ext as NavSyntax.TableExtensionSyntax)?.BaseObject?.ToString().Trim() ?? "");
    }

    /// <summary>Records what the invocation <paramref name="inv"/> starts: a record method's trigger and
    /// database events, or the OnRun of a codeunit run. Only for a call that binds to no declared procedure.</summary>
    private void AddEntryPointCalls(NavCA.SemanticModel model, NavSyntax.InvocationExpressionSyntax inv, Node caller)
    {
        var mae = inv.Expression as NavSyntax.MemberAccessExpressionSyntax;
        var name = mae != null ? Name(mae.Name as NavSyntax.IdentifierNameSyntax)
            : Name(inv.Expression as NavSyntax.IdentifierNameSyntax);
        if (name.Length == 0) return;
        if (mae != null && AddTestPageOperation(model, mae, name, inv.ArgumentList.Arguments.Count, caller)) return;
        if (mae != null && (AddCurrentPageOperation(mae, name, caller) || AddPageRun(model, inv, mae, name, caller))) return;
        if (name.Equals("Run", StringComparison.OrdinalIgnoreCase))
        {
            if (mae != null) AddCodeunitRun(model, inv, mae, caller);
            return;
        }
        if (!RecordOperations.TryGetValue(name, out var op)) return;

        string? table;
        if (mae != null)
        {
            var symbol = model.GetSymbolInfo(mae.Expression).Symbol;
            var type = TypeOf(symbol);
            if (type == null || type.NavTypeKind is NavCA.NavTypeKind.RecordRef or NavCA.NavTypeKind.FieldRef)
                table = null;
            else if (type.NavTypeKind == NavCA.NavTypeKind.Record)
                table = type.Name;
            else
                return;
        }
        else
        {
            table = EnclosingTable(caller);
        }
        var owner = table ?? AnyObject;
        if (RunsTrigger(inv, op.RunTriggerArgument)) Raise(caller, ProcKey(owner, op.Trigger));
        foreach (var ev in op.Events) Raise(caller, ProcKey(owner, ev));
    }

    private void AddCodeunitRun(NavCA.SemanticModel model, NavSyntax.InvocationExpressionSyntax inv,
        NavSyntax.MemberAccessExpressionSyntax mae, Node caller)
    {
        var symbol = model.GetSymbolInfo(mae.Expression).Symbol;
        var type = TypeOf(symbol);
        if (type != null && type.NavTypeKind != NavCA.NavTypeKind.Codeunit) return;
        string owner;
        if (symbol is NavCA.IVariableSymbol or NavCA.IParameterSymbol or NavCA.IFieldSymbol && type != null)
            owner = type.Name;
        else
            owner = CodeunitNamedByFirstArgument(inv) ?? AnyObject;
        Raise(caller, ProcKey(owner, "OnRun"));
    }

    /// <summary>The name in <c>Codeunit::"Name"</c>, the first argument of <c>Codeunit.Run</c>; null for
    /// anything else (an id, a variable).</summary>
    private static string? CodeunitNamedByFirstArgument(NavSyntax.InvocationExpressionSyntax inv)
    {
        if (inv.ArgumentList.Arguments.Count == 0) return null;
        var text = inv.ArgumentList.Arguments[0].ToString().Trim();
        const string scope = "Codeunit::";
        return text.StartsWith(scope, StringComparison.OrdinalIgnoreCase) && text.Length > scope.Length
            ? Unquote(text[scope.Length..].Trim())
            : null;
    }

    private void Raise(Node caller, string key)
    {
        if (_raised.Add((caller, key))) _externalCalls.Add((caller, key));
    }

    private static NavCA.ITypeSymbol? TypeOf(NavCA.ISymbol? symbol) => symbol switch
    {
        NavCA.IVariableSymbol v => v.Type,
        NavCA.IParameterSymbol p => p.ParameterType,
        NavCA.IFieldSymbol f => f.Type,
        NavCA.ITypeSymbol t => t,
        _ => null,
    };

    /// <summary>The table a bare <c>Insert()</c> inside a table or table extension acts on; null elsewhere.</summary>
    private string? EnclosingTable(Node caller)
    {
        for (NavCA.SyntaxNode? n = caller; n != null; n = n.Parent)
            if (n is NavSyntax.ObjectSyntax o)
                return o is NavSyntax.TableSyntax ? Name(o.Name)
                    : o is NavSyntax.TableExtensionSyntax && _extensionBase.TryGetValue(o, out var baseTable) ? baseTable
                    : null;
        return null;
    }

    /// <summary>False only for a RunTrigger argument that is absent or the literal false: anything else
    /// (a variable, an expression) may be true.</summary>
    private static bool RunsTrigger(NavSyntax.InvocationExpressionSyntax inv, int argument)
    {
        if (argument < 0) return true;
        var args = inv.ArgumentList.Arguments;
        if (args.Count <= argument) return false;
        return !(args[argument] is { } a && a.ToString().Trim().Equals("false", StringComparison.OrdinalIgnoreCase));
    }
}
