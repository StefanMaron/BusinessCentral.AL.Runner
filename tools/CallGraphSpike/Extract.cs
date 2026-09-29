using System.Collections.Concurrent;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace CallGraphSpike;

/// <summary>
/// Parses AL sources with BC's own parser (syntax only, no Compilation) and records, per object,
/// every object it names. At object granularity AL has no implicit cross-object reference that is
/// not spelled out by name somewhere (a typed variable/parameter/return value, a
/// <c>Kind::Name</c> literal, an object-reference property, a dataitem, a part, an extends clause),
/// so name resolution over the syntax is enough; resolving a name to EVERY object of that kind with
/// that name (across apps and namespaces) keeps it an over-approximation.
/// </summary>
static class Extract
{
    public static readonly string[] BaseKinds = { "Table", "Page", "Codeunit", "Report", "Query", "XmlPort", "Enum", "Interface" };

    public sealed class Stats
    {
        public int Files, Objects, ParseErrors;
        public ConcurrentDictionary<string, int> AnyKindParents = new();
    }

    public static List<AlObject> ParseApp(string app, string dir, Stats stats)
    {
        var files = Directory.EnumerateFiles(dir, "*.al", SearchOption.AllDirectories).ToArray();
        var bag = new ConcurrentBag<AlObject>();
        var opts = new NavCA.ParseOptions(runtimeVersion: null!, preprocessorSymbols: Array.Empty<string>(), documentationMode: NavCA.DocumentationMode.None);
        Parallel.ForEach(files, file =>
        {
            Interlocked.Increment(ref stats.Files);
            string src;
            try { src = File.ReadAllText(file); } catch { Interlocked.Increment(ref stats.ParseErrors); return; }
            var tree = NavSyntax.SyntaxTree.ParseObjectText(src, path: file, encoding: null!, opts, default);
            var root = tree.GetRoot();
            foreach (var objNode in root.ChildNodes())
            {
                var kind = KindOf(objNode.Kind.ToString());
                if (kind == null) continue;
                var o = BuildObject(app, file, tree, objNode, kind, stats);
                if (o != null) { bag.Add(o); Interlocked.Increment(ref stats.Objects); }
            }
        });
        return bag.ToList();
    }

    static string? KindOf(string syntaxKind) => syntaxKind switch
    {
        "TableObject" => "Table",
        "TableExtensionObject" => "TableExtension",
        "PageObject" => "Page",
        "PageExtensionObject" => "PageExtension",
        "CodeunitObject" => "Codeunit",
        "ReportObject" => "Report",
        "ReportExtensionObject" => "ReportExtension",
        "QueryObject" => "Query",
        "XmlPortObject" => "XmlPort",
        "EnumType" => "Enum",
        "EnumExtensionType" => "EnumExtension",
        "Interface" => "Interface",
        _ => null,
    };

    public static string Norm(string s) => s.Trim().Trim('"').ToLowerInvariant();

    static string LastIdentifier(NavCA.SyntaxNode n)
    {
        if (n.Kind.ToString() == "IdentifierName") return Norm(n.ToString());
        var ids = n.DescendantNodes().Where(d => d.Kind.ToString() == "IdentifierName").ToList();
        return ids.Count > 0 ? Norm(ids[^1].ToString()) : Norm(n.ToString());
    }

    static AlObject? BuildObject(string app, string file, NavSyntax.SyntaxTree tree, NavCA.SyntaxNode objNode, string kind, Stats stats)
    {
        var children = objNode.ChildNodes().ToList();
        int id = 0;
        var idNode = children.FirstOrDefault(c => c.Kind.ToString() == "ObjectId");
        if (idNode != null) int.TryParse(idNode.ToString().Trim(), out id);
        var nameNode = children.FirstOrDefault(c => c.Kind.ToString() == "IdentifierName");
        if (nameNode == null) return null;
        var span = tree.GetLineSpan(objNode.Span);
        var o = new AlObject
        {
            Kind = kind, Id = id, Name = Norm(nameNode.ToString()), DisplayName = nameNode.ToString().Trim(),
            App = app, File = file,
        };
        o.StartLine = span.StartLinePosition.Line + 1;
        o.EndLine = span.EndLinePosition.Line + 1;
        if (kind.EndsWith("Extension"))
        {
            var ext = children.FirstOrDefault(c => c.Kind.ToString() == "ObjectReference");
            if (ext != null) o.ExtendsName = LastIdentifier(ext);
        }
        foreach (var c in children.Where(c => c.Kind.ToString() == "ObjectNameReference"))
            o.Implements.Add(LastIdentifier(c));
        if (kind == "Codeunit")
        {
            foreach (var p in objNode.DescendantNodes().Where(d => d.Kind.ToString() == "Property"))
            {
                var t = p.ToString().Replace(" ", "");
                if (t.StartsWith("Subtype=Test", StringComparison.OrdinalIgnoreCase)) { o.IsTestCodeunit = true; break; }
            }
        }

        // RecordRef-typed names anywhere in the object (a RecordRef.Open with a non-constant id is a wildcard).
        var recRefNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in objNode.DescendantNodes())
        {
            var k = d.Kind.ToString();
            if (k is "VariableDeclaration" or "Parameter" or "VariableListDeclaration")
            {
                var typeNode = d.ChildNodes().FirstOrDefault(c => c.Kind.ToString().EndsWith("TypeReference"));
                if (typeNode == null) continue;
                var tt = typeNode.ToString().Trim();
                if (tt.StartsWith("RecordRef", StringComparison.OrdinalIgnoreCase))
                    foreach (var nm in d.ChildNodes().Where(c => c.Kind.ToString() is "IdentifierName" or "VariableDeclarationName"))
                        recRefNames.Add(Norm(nm.ToString()));
            }
        }

        var walker = new Walker(o, recRefNames, stats);
        foreach (var c in children) walker.Walk(c, null, inGlobalVar: null);
        return o;
    }

    sealed class Walker
    {
        readonly AlObject _o;
        readonly HashSet<string> _recRefNames;
        readonly Stats _stats;
        public Walker(AlObject o, HashSet<string> recRefNames, Stats stats) { _o = o; _recRefNames = recRefNames; _stats = stats; }

        void AddRef(Proc? proc, List<Ref>? globalVar, Ref r)
        {
            _o.Refs.Add(r);
            proc?.Refs.Add(r);
            globalVar?.Add(r);
        }

        void AddWildcard(Proc? proc, string kind)
        {
            _o.Wildcards[kind] = _o.Wildcards.GetValueOrDefault(kind) + 1;
            if (proc != null) proc.Wildcards[kind] = proc.Wildcards.GetValueOrDefault(kind) + 1;
        }

        public void Walk(NavCA.SyntaxNode n, Proc? proc, List<Ref>? inGlobalVar)
        {
            var k = n.Kind.ToString();
            switch (k)
            {
                case "MethodDeclaration":
                case "TriggerDeclaration":
                {
                    if (proc != null) break; // nested (should not happen)
                    var nm = n.ChildNodes().FirstOrDefault(c => c.Kind.ToString() == "IdentifierName");
                    var p = new Proc { Name = nm == null ? "?" : Norm(nm.ToString()), DisplayName = nm == null ? "?" : nm.ToString().Trim().Trim('"'), IsTrigger = k == "TriggerDeclaration" };
                    foreach (var attr in n.ChildNodes().Where(c => c.Kind.ToString() == "MemberAttribute"))
                        HandleAttribute(attr, p);
                    _o.Procs.Add(p);
                    foreach (var c in n.ChildNodes())
                        if (c.Kind.ToString() != "MemberAttribute") Walk(c, p, null);
                    foreach (var id in n.DescendantNodes().Where(d => d.Kind.ToString() == "IdentifierName"))
                        p.Identifiers.Add(Norm(id.ToString()));
                    return;
                }
                case "MemberAttribute":
                    return; // handled with its method; attribute arguments are not forward references
                case "VariableDeclaration":
                case "VariableListDeclaration":
                    if (proc == null && inGlobalVar == null)
                    {
                        var refs = new List<Ref>();
                        foreach (var c in n.ChildNodes()) Walk(c, null, refs);
                        foreach (var nm in n.ChildNodes().Where(c => c.Kind.ToString() is "IdentifierName" or "VariableDeclarationName"))
                            _o.GlobalVarTypes[Norm(nm.ToString())] = refs;
                        return;
                    }
                    break;
                case "ObjectReference":
                {
                    var parent = n.Parent;
                    var pk = parent?.Kind.ToString() ?? "";
                    string? kind = null;
                    if (pk == "SubtypedDataType") kind = TypeKeywordKind(parent!.ToString());
                    else if (pk == "ObjectReferencePropertyValue") kind = PropertyKind(parent!.Parent?.ChildNodes().FirstOrDefault()?.ToString());
                    else if (pk == "QualifiedObjectReferencePropertyValue") kind = TypeKeywordKind(parent!.ToString());
                    else if (pk == "PermissionValue") return; // permissions carry no code path
                    else if (pk == "PagePart") kind = "Page";
                    else if (pk is "ReportDataItem" or "QueryDataItem" or "XmlPortTableElement" or "ReportExtensionDataItem") kind = "Table";
                    else if (pk.EndsWith("Object") || pk.EndsWith("ExtensionType")) return; // extends clause, handled separately
                    else { kind = "*"; _stats.AnyKindParents.AddOrUpdate(pk, 1, (_, v) => v + 1); }
                    if (kind == null) return;
                    if (kind == "") kind = "*";
                    AddRef(proc, inGlobalVar, MakeRef(kind, n, "type"));
                    return;
                }
                case "ObjectNameReference":
                {
                    var pk = n.Parent?.Kind.ToString() ?? "";
                    if (pk == "EnumDataType") { AddRef(proc, inGlobalVar, new Ref("Enum", LastIdentifier(n), 0, "type")); return; }
                    if (pk.EndsWith("Object") || pk is "EnumType" or "Interface" or "EnumExtensionType") return; // implements list
                    if (pk == "IdentifierAttributeArgument") return;
                    _stats.AnyKindParents.AddOrUpdate("ONR:" + pk, 1, (_, v) => v + 1);
                    AddRef(proc, inGlobalVar, new Ref("*", LastIdentifier(n), 0, "name"));
                    return;
                }
                case "OptionAccessExpression":
                {
                    var cs = n.ChildNodes().ToList();
                    if (cs.Count == 2 && cs[0].Kind.ToString() == "IdentifierName")
                    {
                        var left = Norm(cs[0].ToString());
                        var kind = left switch
                        {
                            "codeunit" => "Codeunit", "database" => "Table", "page" => "Page", "report" => "Report",
                            "query" => "Query", "xmlport" => "XmlPort", "enum" => "Enum", "interface" => "Interface",
                            _ => null,
                        };
                        if (kind != null) { AddRef(proc, inGlobalVar, new Ref(kind, LastIdentifier(cs[1]), 0, "literal")); return; }
                        if (left != "objecttype")
                            AddRef(proc, inGlobalVar, new Ref("EnumOpt", left, 0, "enumvalue")); // "Enum Name"::Value (resolved only if such an enum exists)
                    }
                    break;
                }
                case "InvocationExpression":
                    HandleInvocation(n, proc);
                    break;
                case "TableRelationStatement":
                {
                    // TableRelation = T[."Field"] [where ...]; the head identifier names a table
                    var head = n.ChildNodes().FirstOrDefault(c => c.Kind.ToString() is "IdentifierName" or "QualifiedName");
                    if (head != null)
                    {
                        var hn = head.Kind.ToString() == "QualifiedName" ? Norm(head.ChildNodes().First().ToString()) : Norm(head.ToString());
                        AddRef(proc, inGlobalVar, new Ref("Table", hn, 0, "tablerelation"));
                    }
                    break;
                }
            }
            foreach (var c in n.ChildNodes()) Walk(c, proc, inGlobalVar);
        }

        Ref MakeRef(string kind, NavCA.SyntaxNode objRef, string via)
        {
            var t = objRef.ToString().Trim();
            if (int.TryParse(t, out var id)) return new Ref(kind, "", id, via);
            return new Ref(kind, LastIdentifier(objRef), 0, via);
        }

        static string? TypeKeywordKind(string text)
        {
            var w = text.TrimStart().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            return w switch
            {
                "record" => "Table", "codeunit" => "Codeunit", "page" => "Page", "report" => "Report",
                "query" => "Query", "xmlport" => "XmlPort", "interface" => "Interface",
                "testpage" or "testpart" => "Page", "testrequestpage" => "Report",
                "enum" => "Enum",
                "dotnet" => null,
                _ => "",
            };
        }

        static string PropertyKind(string? propName) => (propName ?? "").Trim().ToLowerInvariant() switch
        {
            "tableno" or "sourcetable" => "Table",
            "lookuppageid" or "drilldownpageid" or "cardpageid" => "Page",
            _ => "",
        };

        void HandleAttribute(NavCA.SyntaxNode attr, Proc p)
        {
            var cs = attr.ChildNodes().ToList();
            var name = cs.Count > 0 ? Norm(cs[0].ToString()) : "";
            var args = cs.Count > 1 ? cs[1].ChildNodes().ToList() : new List<NavCA.SyntaxNode>();
            switch (name)
            {
                case "test": p.IsTest = true; break;
                case "handlerfunctions":
                    foreach (var a in args)
                        foreach (var h in a.ToString().Trim().Trim('\'').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            p.Handlers.Add(h.ToLowerInvariant());
                    break;
                case "eventsubscriber":
                {
                    p.IsSubscriber = true;
                    if (args.Count < 2) break;
                    var objType = Norm(args[0].ToString().Split("::").Last());
                    var kind = objType switch
                    {
                        "codeunit" => "Codeunit", "table" => "Table", "page" => "Page", "report" => "Report",
                        "query" => "Query", "xmlport" => "XmlPort", _ => "*",
                    };
                    var pubText = args[1].ToString().Trim();
                    var pub = pubText.Contains("::") ? Norm(pubText[(pubText.IndexOf("::", StringComparison.Ordinal) + 2)..]) : Norm(pubText);
                    if (int.TryParse(pub, out var pid)) _o.Subscribes.Add(new Ref(kind, "", pid, "subscriber"));
                    else _o.Subscribes.Add(new Ref(kind, pub, 0, "subscriber"));
                    break;
                }
            }
        }

        static readonly HashSet<string> ReportRunners = new(StringComparer.OrdinalIgnoreCase)
        { "run", "runmodal", "execute", "print", "saveas", "saveaspdf", "saveasword", "saveasexcel", "saveashtml", "saveasxml", "runrequestpage" };

        void HandleInvocation(NavCA.SyntaxNode inv, Proc? proc)
        {
            var cs = inv.ChildNodes().ToList();
            if (cs.Count < 2) return;
            var argList = cs.FirstOrDefault(c => c.Kind.ToString() == "ArgumentList");
            var args = argList?.ChildNodes().ToList() ?? new List<NavCA.SyntaxNode>();
            string? target = null, member = null;
            if (cs[0].Kind.ToString() == "MemberAccessExpression")
            {
                var mc = cs[0].ChildNodes().ToList();
                if (mc.Count == 2 && mc[0].Kind.ToString() == "IdentifierName")
                { target = Norm(mc[0].ToString()); member = Norm(mc[1].ToString()); }
            }
            else if (cs[0].Kind.ToString() == "IdentifierName") member = Norm(cs[0].ToString());
            if (member == null) return;

            void Check(string kind, int argIndex)
            {
                if (args.Count <= argIndex) return;
                var a = args[argIndex];
                var ak = a.Kind.ToString();
                if (ak == "OptionAccessExpression") return; // Kind::Name, already a reference
                if (ak == "LiteralExpression")
                {
                    if (int.TryParse(a.ToString().Trim(), out var lit))
                    {
                        if (lit == 0) return; // Page.Run(0, Rec): the record's lookup page, an edge the table already carries
                        _o.Refs.Add(new Ref(kind, "", lit, "literal-id"));
                        proc?.Refs.Add(new Ref(kind, "", lit, "literal-id"));
                        return;
                    }
                }
                AddWildcard(proc, kind);
            }

            switch (target)
            {
                case "codeunit" when member == "run": Check("Codeunit", 0); return;
                case "page" when member is "run" or "runmodal": Check("Page", 0); return;
                case "report" when ReportRunners.Contains(member): Check("Report", 0); return;
                case "xmlport" when member is "run" or "import" or "export": Check("XmlPort", 0); return;
                case "query" when member.StartsWith("saveas"): Check("Query", 0); return;
                case "taskscheduler" when member == "createtask": Check("Codeunit", 0); Check("Codeunit", 1); return;
                case "session" when member == "startsession": Check("Codeunit", 1); return;
            }
            if (target == null && member == "startsession") { Check("Codeunit", 1); return; }
            if (target != null && member == "open" && _recRefNames.Contains(target)) { Check("Table", 0); return; }
        }
    }
}
