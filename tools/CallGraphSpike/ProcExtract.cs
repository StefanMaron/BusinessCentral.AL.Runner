using System.Collections.Concurrent;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace CallGraphSpike;

/// <summary>A declared type as far as reachability cares: an object kind and name, or a RecordRef/FieldRef.</summary>
readonly record struct TypeRef(string Kind, string Name);

/// <summary>One call site, kept unresolved until every app is parsed.</summary>
sealed class CallSite
{
    public string? Receiver;          // simple identifier receiver (normalised), null for an unqualified call
    public string? ChainHead;         // leftmost identifier of a chained receiver (a.b.c.M())
    public string? ReceiverCall;      // receiver is a call F(...) / X.F(...): F's name
    public string? ReceiverCallTarget;// X in X.F(...)
    public required string Member;    // normalised member name
    // dynamic run literal resolution, filled at extraction: (kind, name, id) or wildcard kind
    public readonly List<(string Kind, string Name, int Id)> RunTargets = new();
    public readonly List<string> RunWildcards = new();
    public string? LookupPageOfVar;   // Page.Run(0, X): X's name
    public string? FieldArg;          // Validate("Field", ...): the field named by the first argument, when it is a plain name
}

sealed class PProc
{
    public required string Name;          // normalised; triggers get "trigger:<container>:<name>"
    public required string DisplayName;   // as written; triggers as "<container> - <Trigger>" or "<Trigger>"
    public bool IsTrigger, IsTest, IsSubscriber, IsPublisher;
    public readonly List<string> Handlers = new();
    public readonly Dictionary<string, TypeRef> Locals = new();
    public TypeRef? ReturnType;
    public readonly List<CallSite> Calls = new();
    public (string Kind, string Pub, int PubId, string Event)? Subscription;
    public string SubscriptionElement = "";   // 4th EventSubscriber argument (field name for OnBefore/AfterValidateEvent)
}

sealed class PObj
{
    public required string Kind;
    public required int Id;
    public required string Name;
    public required string DisplayName;
    public required string App;
    public required string File;
    public int StartLine, EndLine;
    public string? ExtendsName;
    public bool IsTestCodeunit;
    public string? SourceTable, TableNo, LookupPage, DrillDownPage;
    public readonly List<string> Implements = new();
    public readonly List<string> PartPages = new();
    public readonly List<(string Kind, string Name)> RunObjects = new();
    public readonly Dictionary<string, TypeRef> Globals = new();
    public readonly List<PProc> Procs = new();
    public string Key => Kind == "Interface" ? $"Interface:{Name}@{App}" : $"{Kind}:{Id}@{App}";
}

/// <summary>
/// Procedure-granularity extraction: per procedure/trigger, its declared locals and every call
/// site; per object, its globals (report/query dataitems and page parts included).
/// </summary>
static class ProcExtract
{
    public static List<PObj> ParseApp(string app, string dir)
    {
        var files = Directory.EnumerateFiles(dir, "*.al", SearchOption.AllDirectories).ToArray();
        var bag = new ConcurrentBag<PObj>();
        var opts = new NavCA.ParseOptions(runtimeVersion: null!, preprocessorSymbols: Array.Empty<string>(), documentationMode: NavCA.DocumentationMode.None);
        Parallel.ForEach(files, file =>
        {
            var tree = NavSyntax.SyntaxTree.ParseObjectText(File.ReadAllText(file), path: file, encoding: null!, opts, default);
            foreach (var objNode in tree.GetRoot().ChildNodes())
            {
                var kind = KindOf(objNode.Kind.ToString());
                if (kind == null) continue;
                var o = Build(app, file, tree, objNode, kind);
                if (o != null) bag.Add(o);
            }
        });
        return bag.ToList();
    }

    static string? KindOf(string k) => k switch
    {
        "TableObject" => "Table", "TableExtensionObject" => "TableExtension",
        "PageObject" => "Page", "PageExtensionObject" => "PageExtension",
        "CodeunitObject" => "Codeunit", "ReportObject" => "Report", "ReportExtensionObject" => "ReportExtension",
        "QueryObject" => "Query", "XmlPortObject" => "XmlPort", "EnumType" => "Enum",
        "EnumExtensionType" => "EnumExtension", "Interface" => "Interface", _ => null,
    };

    static string N(string s) => Extract.Norm(s);
    static string K(NavCA.SyntaxNode n) => n.Kind.ToString();

    static string LastId(NavCA.SyntaxNode n)
    {
        if (K(n) == "IdentifierName") return N(n.ToString());
        var ids = n.DescendantNodes().Where(d => K(d) == "IdentifierName").ToList();
        return ids.Count > 0 ? N(ids[^1].ToString()) : N(n.ToString());
    }

    public static TypeRef? TypeOf(NavCA.SyntaxNode typeNode)
    {
        var text = typeNode.ToString().TrimStart();
        var w = text.Split(' ', '\t', '\r', '\n', '[')[0].ToLowerInvariant();
        string? kind = w switch
        {
            "record" => "Table", "codeunit" => "Codeunit", "page" => "Page", "testpage" => "Page", "testpart" => "Page",
            "report" => "Report", "testrequestpage" => "Report", "query" => "Query", "xmlport" => "XmlPort",
            "interface" => "Interface", "recordref" => "RecordRef", "fieldref" => "FieldRef", _ => null,
        };
        if (kind == null) return null;
        if (kind is "RecordRef" or "FieldRef") return new TypeRef(kind, "");
        var r = typeNode.DescendantNodes().FirstOrDefault(d => K(d) is "ObjectReference" or "ObjectNameReference");
        return r == null ? null : new TypeRef(kind, LastId(r));
    }

    static PObj? Build(string app, string file, NavSyntax.SyntaxTree tree, NavCA.SyntaxNode objNode, string kind)
    {
        var children = objNode.ChildNodes().ToList();
        int id = 0;
        var idNode = children.FirstOrDefault(c => K(c) == "ObjectId");
        if (idNode != null) int.TryParse(idNode.ToString().Trim(), out id);
        var nameNode = children.FirstOrDefault(c => K(c) == "IdentifierName");
        if (nameNode == null) return null;
        var span = tree.GetLineSpan(objNode.Span);
        var o = new PObj
        {
            Kind = kind, Id = id, Name = N(nameNode.ToString()), DisplayName = nameNode.ToString().Trim(), App = app, File = file,
            StartLine = span.StartLinePosition.Line + 1, EndLine = span.EndLinePosition.Line + 1,
        };
        if (kind.EndsWith("Extension"))
        {
            var ext = children.FirstOrDefault(c => K(c) == "ObjectReference");
            if (ext != null) o.ExtendsName = LastId(ext);
        }
        foreach (var c in children.Where(c => K(c) == "ObjectNameReference")) o.Implements.Add(LastId(c));

        // object-level properties, globals, dataitems, parts, run objects
        foreach (var d in objNode.DescendantNodes())
        {
            switch (K(d))
            {
                case "Property":
                {
                    var pn = N(d.ChildNodes().FirstOrDefault()?.ToString() ?? "");
                    var val = d.ChildNodes().Skip(1).FirstOrDefault();
                    if (val == null) break;
                    var refNode = val.DescendantNodesAndSelf().FirstOrDefault(x => K(x) == "ObjectReference");
                    switch (pn)
                    {
                        case "subtype" when kind == "Codeunit" && N(val.ToString()) == "test": o.IsTestCodeunit = true; break;
                        case "sourcetable" when refNode != null: o.SourceTable = LastId(refNode); break;
                        case "tableno" when refNode != null: o.TableNo = LastId(refNode); break;
                        case "lookuppageid" when refNode != null: o.LookupPage = LastId(refNode); break;
                        case "drilldownpageid" when refNode != null: o.DrillDownPage = LastId(refNode); break;
                        case "runobject" when refNode != null:
                        {
                            var w = val.ToString().TrimStart().Split(' ')[0].ToLowerInvariant();
                            var rk = w switch { "page" => "Page", "codeunit" => "Codeunit", "report" => "Report", "xmlport" => "XmlPort", "query" => "Query", _ => null };
                            if (rk != null) o.RunObjects.Add((rk, LastId(refNode)));
                            break;
                        }
                    }
                    break;
                }
                case "PagePart":
                {
                    var r = d.ChildNodes().FirstOrDefault(x => K(x) == "ObjectReference");
                    var pn = d.ChildNodes().FirstOrDefault(x => K(x) == "IdentifierName");
                    if (r != null)
                    {
                        o.PartPages.Add(LastId(r));
                        if (pn != null) o.Globals[N(pn.ToString())] = new TypeRef("Page", LastId(r));
                    }
                    break;
                }
                case "ReportDataItem" or "QueryDataItem" or "XmlPortTableElement" or "ReportExtensionDataItem":
                {
                    var r = d.ChildNodes().FirstOrDefault(x => K(x) == "ObjectReference");
                    var dn = d.ChildNodes().FirstOrDefault(x => K(x) == "IdentifierName");
                    if (r != null && dn != null) o.Globals[N(dn.ToString())] = new TypeRef("Table", LastId(r));
                    break;
                }
            }
        }
        // global var sections: VariableDeclarations with no method/trigger ancestor
        foreach (var d in objNode.DescendantNodes().Where(x => K(x) is "VariableDeclaration" or "VariableListDeclaration"))
        {
            if (d.Ancestors().Any(a => K(a) is "MethodDeclaration" or "TriggerDeclaration")) continue;
            AddDecl(d, o.Globals);
        }
        // procedures and triggers
        foreach (var m in objNode.DescendantNodes().Where(x => K(x) is "MethodDeclaration" or "TriggerDeclaration"))
            o.Procs.Add(BuildProc(o, m));
        return o;
    }

    static void AddDecl(NavCA.SyntaxNode decl, Dictionary<string, TypeRef> into)
    {
        var typeNode = decl.ChildNodes().FirstOrDefault(c => K(c).EndsWith("TypeReference"));
        if (typeNode == null) return;
        var t = TypeOf(typeNode);
        if (t == null) return;
        foreach (var nm in decl.ChildNodes().Where(c => K(c) is "IdentifierName" or "VariableDeclarationName"))
            into[N(nm.ToString())] = t.Value;
    }

    static PProc BuildProc(PObj o, NavCA.SyntaxNode m)
    {
        var isTrigger = K(m) == "TriggerDeclaration";
        var nm = m.ChildNodes().FirstOrDefault(c => K(c) == "IdentifierName");
        var raw = nm?.ToString().Trim().Trim('"') ?? "?";
        string display = raw, name = N(raw);
        if (isTrigger)
        {
            // container: the nearest named ancestor inside the object (field, control, action, dataitem)
            var container = m.Ancestors().FirstOrDefault(a => K(a) is "Field" or "PageField" or "PageAction" or "ReportDataItem"
                or "ReportColumn" or "PageLabel" or "PagePart" or "QueryDataItem" or "XmlPortTableElement" or "XmlPortFieldElement"
                or "ReportExtensionDataItem" or "ReportExtensionModifyDataItem" or "PageFieldChange" or "PageActionChange"
                or "FieldModification" or "ControlModification");
            var cname = container?.ChildNodes().FirstOrDefault(c => K(c) == "IdentifierName")?.ToString().Trim().Trim('"');
            display = cname == null ? raw : $"{cname} - {raw}";
            name = "trigger:" + N(display);
        }
        var p = new PProc { Name = name, DisplayName = display, IsTrigger = isTrigger };
        foreach (var attr in m.ChildNodes().Where(c => K(c) == "MemberAttribute"))
        {
            var cs = attr.ChildNodes().ToList();
            var an = cs.Count > 0 ? N(cs[0].ToString()) : "";
            var args = cs.Count > 1 ? cs[1].ChildNodes().ToList() : new List<NavCA.SyntaxNode>();
            switch (an)
            {
                case "test": p.IsTest = true; break;
                case "handlerfunctions":
                    foreach (var a in args)
                        foreach (var h in a.ToString().Trim().Trim('\'').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            p.Handlers.Add(h.ToLowerInvariant());
                    break;
                case "integrationevent" or "businessevent" or "internalevent": p.IsPublisher = true; break;
                case "eventsubscriber" when args.Count >= 3:
                {
                    p.IsSubscriber = true;
                    var objType = N(args[0].ToString().Split("::").Last());
                    var kind = objType switch { "codeunit" => "Codeunit", "table" => "Table", "page" => "Page", "report" => "Report", "query" => "Query", "xmlport" => "XmlPort", _ => "*" };
                    var pubText = args[1].ToString().Trim();
                    var pub = pubText.Contains("::") ? N(pubText[(pubText.IndexOf("::", StringComparison.Ordinal) + 2)..]) : N(pubText);
                    var ev = N(args[2].ToString().Trim().Trim('\''));
                    p.Subscription = int.TryParse(pub, out var pid) ? (kind, "", pid, ev) : (kind, pub, 0, ev);
                    if (args.Count >= 4) p.SubscriptionElement = N(args[3].ToString().Trim().Trim('\''));
                    break;
                }
            }
        }
        foreach (var par in m.DescendantNodes().Where(x => K(x) == "Parameter")) AddDecl(par, p.Locals);
        foreach (var d in m.DescendantNodes().Where(x => K(x) is "VariableDeclaration" or "VariableListDeclaration")) AddDecl(d, p.Locals);
        var ret = m.ChildNodes().FirstOrDefault(c => K(c) == "ReturnValue");
        if (ret != null)
        {
            var tn = ret.ChildNodes().FirstOrDefault(c => K(c).EndsWith("TypeReference"));
            if (tn != null)
            {
                p.ReturnType = TypeOf(tn);
                var rn = ret.ChildNodes().FirstOrDefault(c => K(c) == "IdentifierName");
                if (rn != null && p.ReturnType != null) p.Locals[N(rn.ToString())] = p.ReturnType.Value;
            }
        }
        foreach (var inv in m.DescendantNodes().Where(x => K(x) == "InvocationExpression"))
        {
            var cs = BuildCall(inv);
            if (cs != null) p.Calls.Add(cs);
        }
        return p;
    }

    static readonly HashSet<string> ReportRunners = new() { "run", "runmodal", "execute", "print", "saveas", "saveaspdf", "saveasword", "saveasexcel", "saveashtml", "saveasxml", "runrequestpage" };

    static CallSite? BuildCall(NavCA.SyntaxNode inv)
    {
        var cs = inv.ChildNodes().ToList();
        if (cs.Count == 0) return null;
        var argList = cs.FirstOrDefault(c => K(c) == "ArgumentList");
        var args = argList?.ChildNodes().ToList() ?? new List<NavCA.SyntaxNode>();
        var head = cs[0];
        CallSite call;
        if (K(head) == "IdentifierName")
            call = new CallSite { Member = N(head.ToString()) };
        else if (K(head) == "MemberAccessExpression")
        {
            var mc = head.ChildNodes().ToList();
            if (mc.Count != 2) return null;
            var member = N(mc[1].ToString());
            var recv = mc[0];
            call = new CallSite { Member = member };
            switch (K(recv))
            {
                case "IdentifierName": call.Receiver = N(recv.ToString()); break;
                case "InvocationExpression":
                {
                    var rh = recv.ChildNodes().FirstOrDefault();
                    if (rh != null && K(rh) == "IdentifierName") call.ReceiverCall = N(rh.ToString());
                    else if (rh != null && K(rh) == "MemberAccessExpression")
                    {
                        var rc = rh.ChildNodes().ToList();
                        if (rc.Count == 2 && K(rc[0]) == "IdentifierName") { call.ReceiverCallTarget = N(rc[0].ToString()); call.ReceiverCall = N(rc[1].ToString()); }
                        else call.ChainHead = FirstId(recv);
                    }
                    else call.ChainHead = FirstId(recv);
                    break;
                }
                default: call.ChainHead = FirstId(recv); break;
            }
        }
        else return null;

        if (call.Member == "validate" && args.Count > 0 && K(args[0]) == "IdentifierName") call.FieldArg = N(args[0].ToString());

        // static dynamic-dispatch APIs
        void Check(string kind, int argIndex)
        {
            if (args.Count <= argIndex) return;
            var a = args[argIndex];
            if (K(a) == "OptionAccessExpression")
            {
                var oc = a.ChildNodes().ToList();
                if (oc.Count == 2) { call.RunTargets.Add((kind, LastId(oc[1]), 0)); return; }
            }
            if (K(a) == "LiteralExpression" && int.TryParse(a.ToString().Trim(), out var lit))
            {
                if (lit == 0 && kind == "Page" && args.Count > argIndex + 1 && K(args[argIndex + 1]) == "IdentifierName")
                { call.LookupPageOfVar = N(args[argIndex + 1].ToString()); return; }
                if (lit != 0) { call.RunTargets.Add((kind, "", lit)); return; }
            }
            call.RunWildcards.Add(kind);
        }
        switch (call.Receiver)
        {
            case "codeunit" when call.Member == "run": Check("Codeunit", 0); break;
            case "page" when call.Member is "run" or "runmodal": Check("Page", 0); break;
            case "report" when ReportRunners.Contains(call.Member): Check("Report", 0); break;
            case "xmlport" when call.Member is "run" or "import" or "export": Check("XmlPort", 0); break;
            case "query" when call.Member.StartsWith("saveas"): Check("Query", 0); break;
            case "taskscheduler" when call.Member == "createtask": Check("Codeunit", 0); Check("Codeunit", 1); break;
            case "session" when call.Member == "startsession": Check("Codeunit", 1); break;
            case null when call.Member == "startsession" && call.ChainHead == null && call.ReceiverCall == null: Check("Codeunit", 1); break;
        }
        return call;
    }

    static string? FirstId(NavCA.SyntaxNode n)
    {
        var id = n.DescendantNodesAndSelf().FirstOrDefault(d => K(d) == "IdentifierName");
        return id == null ? null : N(id.ToString());
    }
}
