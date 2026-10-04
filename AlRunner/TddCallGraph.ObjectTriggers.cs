// #5322: a report, a query and an xmlport run their own triggers from the platform, so no AL call in the test
// names them. The graph records Run/Execute/SaveAs..., Open and Import/Export as raises of keys under the OBJECT'S
// name, the way #5309 does for a page (TddCallGraph.PageTriggers.cs): a trigger is something its key reaches. A
// request page (a report's or an xmlport's) is a page of that name, so its triggers use the page keys. The
// platform publishes no event around these triggers (AL0280 on every trigger-shaped name tried). What each call
// runs was measured on the runner (BC's own runtime); the corpus settles the report triggers
// (handlers/TestReportRunExecution.al, reportextensiontrigger/TestReportExtensionTriggers.al) and the xmlport
// import and export (xmlport/). docs/server-mode.md#tdd.
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using Node = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.MethodOrTriggerDeclarationSyntax;

namespace AlRunner;

internal sealed partial class TddCallGraph
{
    // The key a trigger no operation is known to start is raised under, by every operation of its kind.
    private const string ReportCode = "OnReportCode", QueryCode = "OnQueryCode", XmlPortCode = "OnXmlPortCode";
    private const string ReportInit = "OnInitReport", QueryOpen = "OnBeforeOpen", XmlPortInit = "OnInitXmlPort";

    private static readonly string[] ReportBodyTriggers =
        {
            "OnPreReport", "OnPostReport", "OnPreDataItem", "OnAfterGetRecord", "OnPostDataItem",
            "OnBeforePreDataItem", "OnAfterPreDataItem", "OnBeforeAfterGetRecord", "OnAfterAfterGetRecord",
            "OnBeforePostDataItem", "OnAfterPostDataItem",
        };

    // An xmlport's body triggers by the direction that runs them (measured: an export ran OnPreXmlItem,
    // OnAfterGetRecord and the BeforePass triggers, an import the AfterInit, Insert, Modify and AfterAssign ones, and
    // both OnPreXmlPort / OnPostXmlPort).
    private static readonly string[] XmlPortRunTriggers = { "OnPreXmlPort", "OnPostXmlPort" };
    private static readonly string[] XmlPortExportTriggers =
        { "OnPreXmlItem", "OnAfterGetRecord", "OnBeforePassVariable", "OnBeforePassField" };
    private static readonly string[] XmlPortImportTriggers =
        {
            "OnAfterInitRecord", "OnBeforeInsertRecord", "OnAfterInsertRecord", "OnBeforeModifyRecord", "OnAfterModifyRecord",
            "OnAfterAssignVariable", "OnAfterAssignField",
        };

    // What an import writes to the table of each table element: the field validation, the insert and, for an
    // existing key, the modify (measured: no delete, no rename).
    private static readonly string[] XmlPortImportTableOperations = { "Validate", "Insert", "Modify" };

    // The members of a Report, Query or XmlPort that start none of the object's code beyond its construction (the
    // first use of the variable runs OnInitReport / OnInitXmlPort), by what was measured; any other name starts
    // everything, so a member a later BC adds over-annotates and never misses.
    private static readonly HashSet<string> ReportReaders = new(StringComparer.OrdinalIgnoreCase)
        { "UseRequestPage", "SetTableView" };
    private static readonly HashSet<string> QueryReaders = new(StringComparer.OrdinalIgnoreCase)
        { "Close", "Read", "TopNumberOfRows", "ColumnName", "ColumnCaption", "ColumnNo", "SetFilter", "SetRange", "GetFilter", "GetFilters", "SecurityFiltering" };
    private static readonly HashSet<string> XmlPortReaders = new(StringComparer.OrdinalIgnoreCase)
        { "SetDestination", "SetSource", "SetTableView", "Filename", "TextEncoding", "CurrentPath", "ImportFile", "UseRequestPage" };

    private readonly Dictionary<string, List<string?>> _xmlPortTables = new(StringComparer.OrdinalIgnoreCase);

    internal enum ObjectFamily { Report, Query, XmlPort }

    /// <summary>True for the key names a report, a query or an xmlport operation raises (beyond the page ones a
    /// request page uses): read at call time, like <see cref="IsPageEntryPoint"/>.</summary>
    private static bool IsObjectFamilyEntryPoint(string name)
        => name.Equals(ReportCode, StringComparison.OrdinalIgnoreCase) || name.Equals(QueryCode, StringComparison.OrdinalIgnoreCase)
            || name.Equals(XmlPortCode, StringComparison.OrdinalIgnoreCase) || name.Equals(ReportInit, StringComparison.OrdinalIgnoreCase)
            || name.Equals(QueryOpen, StringComparison.OrdinalIgnoreCase) || name.Equals(XmlPortInit, StringComparison.OrdinalIgnoreCase)
            || ReportBodyTriggers.Contains(name, StringComparer.OrdinalIgnoreCase)
            || XmlPortRunTriggers.Contains(name, StringComparer.OrdinalIgnoreCase)
            || XmlPortExportTriggers.Contains(name, StringComparer.OrdinalIgnoreCase)
            || XmlPortImportTriggers.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads, for each xmlport of <paramref name="trees"/>, the tables of its table elements (null: named by
    /// an id): what an import of it writes. Run before the invocations are walked, because a call may sit in a file
    /// that precedes the xmlport's.</summary>
    private void CollectXmlPortTables(IEnumerable<NavSyntax.SyntaxTree> trees)
    {
        foreach (var tree in trees)
            foreach (var xmlPort in tree.GetRoot().DescendantNodes().OfType<NavSyntax.XmlPortSyntax>())
            {
                var tables = new List<string?>();
                foreach (var element in xmlPort.DescendantNodes().OfType<NavSyntax.XmlPortTableElementSyntax>())
                {
                    var text = element.SourceTable?.ToString().Trim() ?? "";
                    tables.Add(text.Length == 0 || text.All(char.IsDigit) ? null : Unquote(text));
                }
                _xmlPortTables[Name(xmlPort.Name)] = tables;
            }
    }

    /// <summary>True for a trigger written inside the object's request page (a report's, a report extension's or an
    /// xmlport's): read from the span of the object's own request page, because the trigger's parent chain does not
    /// reach a request page node.</summary>
    private static bool InRequestPage(NavSyntax.ObjectSyntax obj, Node trigger)
    {
        NavCA.SyntaxNode? page = obj switch
        {
            NavSyntax.ReportSyntax r => r.RequestPage,
            NavSyntax.ReportExtensionSyntax e => e.RequestPage,
            NavSyntax.XmlPortSyntax x => x.XmlPortRequestPage,
            _ => null,
        };
        return page != null && page.FullSpan.Contains(trigger.Span);
    }

    private void CollectObjectTriggers(NavSyntax.ObjectSyntax obj)
    {
        foreach (var node in obj.DescendantNodes().OfType<Node>())
        {
            if (node is NavSyntax.MethodDeclarationSyntax) continue;
            var owner = KeyObjectName(obj, node);
            var name = Name(node.Name);
            foreach (var key in ObjectTriggerKeys(obj is NavSyntax.QuerySyntax ? ObjectFamily.Query
                    : obj is NavSyntax.XmlPortSyntax ? ObjectFamily.XmlPort : ObjectFamily.Report,
                    owner, name, InRequestPage(obj, node), ControlNameOf(node)))
                _triggers.Add((node, key));
        }
    }

    /// <summary>The keys that start a trigger of a report, query or xmlport named <paramref name="owner"/>: a request
    /// page trigger answers the keys of a page of that name; any other trigger the key of its own name when this
    /// list knows it, else the one key every operation of its kind raises.</summary>
    internal static IReadOnlyList<string> ObjectTriggerKeys(ObjectFamily family, string owner, string name, bool requestPage, string? control)
    {
        if (requestPage) return TriggerKeys(owner, name, control);
        var known = family switch
        {
            ObjectFamily.Report => name.Equals(ReportInit, StringComparison.OrdinalIgnoreCase)
                || ReportBodyTriggers.Contains(name, StringComparer.OrdinalIgnoreCase),
            ObjectFamily.Query => name.Equals(QueryOpen, StringComparison.OrdinalIgnoreCase),
            _ => name.Equals(XmlPortInit, StringComparison.OrdinalIgnoreCase)
                || XmlPortRunTriggers.Contains(name, StringComparer.OrdinalIgnoreCase)
                || XmlPortExportTriggers.Contains(name, StringComparer.OrdinalIgnoreCase)
                || XmlPortImportTriggers.Contains(name, StringComparer.OrdinalIgnoreCase),
        };
        var fallback = family switch { ObjectFamily.Report => ReportCode, ObjectFamily.Query => QueryCode, _ => XmlPortCode };
        return new[] { ProcKey(owner, known ? name : fallback) };
    }

    /// <summary>The names an operation of <paramref name="family"/> named <paramref name="method"/> raises, and
    /// whether it runs the request page; the construction trigger always.</summary>
    internal static (IReadOnlyList<string> Names, bool RequestPage, bool ImportWrites) ObjectOperation(ObjectFamily family, string method)
    {
        bool Is(string n) => method.Equals(n, StringComparison.OrdinalIgnoreCase);
        switch (family)
        {
            case ObjectFamily.Report:
            {
                var init = new[] { ReportInit };
                if (ReportReaders.Contains(method)) return (init, false, false);
                if (Is("RunRequestPage")) return (init, true, false);
                return (init.Concat(ReportBodyTriggers).Append(ReportCode).ToArray(), true, false);
            }
            case ObjectFamily.Query:
                return QueryReaders.Contains(method) ? (Array.Empty<string>(), false, false)
                    : (new[] { QueryOpen, QueryCode }, false, false);
            default:
            {
                var init = new[] { XmlPortInit };
                if (XmlPortReaders.Contains(method)) return (init, false, false);
                var both = init.Concat(XmlPortRunTriggers);
                if (Is("Export")) return (both.Concat(XmlPortExportTriggers).Append(XmlPortCode).ToArray(), false, false);
                if (Is("Import")) return (both.Concat(XmlPortImportTriggers).Append(XmlPortCode).ToArray(), false, true);
                return (both.Concat(XmlPortExportTriggers).Concat(XmlPortImportTriggers).Append(XmlPortCode).ToArray(), true, true);
            }
        }
    }

    /// <summary>Records what a call on a Report, Query or XmlPort starts, and says whether the call was one: the
    /// object is named by the variable's type, by <c>Report::"X"</c> and its kin in the first argument of the static
    /// form, else every object of the kind counts. A call on <c>CurrReport</c> or <c>CurrXmlPort</c> inside the object
    /// has a type kind of its own and is none of these.</summary>
    private bool AddObjectFamilyOperation(NavCA.SemanticModel model, NavSyntax.InvocationExpressionSyntax inv,
        NavSyntax.MemberAccessExpressionSyntax mae, string method, Node caller)
    {
        ObjectFamily family;
        string? target;
        var symbol = model.GetSymbolInfo(mae.Expression).Symbol;
        if (symbol is NavCA.IVariableSymbol or NavCA.IParameterSymbol or NavCA.IFieldSymbol
            && TypeOf(symbol) is { NavTypeKind: NavCA.NavTypeKind.Report or NavCA.NavTypeKind.Query or NavCA.NavTypeKind.XmlPort } type)
        {
            family = type.NavTypeKind == NavCA.NavTypeKind.Report ? ObjectFamily.Report
                : type.NavTypeKind == NavCA.NavTypeKind.Query ? ObjectFamily.Query : ObjectFamily.XmlPort;
            target = type.Name.Length > 0 ? type.Name : null;
        }
        else if (mae.Expression is NavSyntax.IdentifierNameSyntax id
            && Name(id).ToLowerInvariant() is var keyword && keyword is "report" or "query" or "xmlport")
        {
            family = keyword == "report" ? ObjectFamily.Report : keyword == "query" ? ObjectFamily.Query : ObjectFamily.XmlPort;
            var named = inv.ArgumentList.Arguments.Count > 0 ? model.GetSymbolInfo(inv.ArgumentList.Arguments[0]).Symbol : null;
            target = named switch
            {
                NavCA.IReportTypeSymbol r when family == ObjectFamily.Report && r.Name.Length > 0 => r.Name,
                NavCA.IQueryTypeSymbol q when family == ObjectFamily.Query && q.Name.Length > 0 => q.Name,
                NavCA.IXmlPortTypeSymbol x when family == ObjectFamily.XmlPort && x.Name.Length > 0 => x.Name,
                _ => null,
            };
        }
        else return false;

        var (names, requestPage, importWrites) = ObjectOperation(family, method);
        var owner = target ?? AnyObject;
        foreach (var n in names) Raise(caller, ProcKey(owner, n));
        if (requestPage)
        {
            RaisePage(caller, target, AllOps);
        }
        if (importWrites)
        {
            if (target != null && _xmlPortTables.TryGetValue(target, out var tables))
                foreach (var table in tables.Distinct()) RaiseOperations(caller, XmlPortImportTableOperations, table);
            else RaiseOperations(caller, XmlPortImportTableOperations, null);
        }
        return true;
    }
}
