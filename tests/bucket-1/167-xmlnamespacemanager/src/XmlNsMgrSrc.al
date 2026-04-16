/// Helper codeunit exercising XmlNamespaceManager — all 8 methods from issue #726.
codeunit 92000 "XNM Src"
{
    // AddNamespace + LookupNamespace
    procedure AddAndLookup(prefix: Text; uri: Text): Text
    var
        mgr: XmlNamespaceManager;
        result: Text;
    begin
        mgr.AddNamespace(prefix, uri);
        mgr.LookupNamespace(prefix, result);
        exit(result);
    end;

    // LookupPrefix
    procedure LookupPrefix(prefix: Text; uri: Text): Text
    var
        mgr: XmlNamespaceManager;
        result: Text;
    begin
        mgr.AddNamespace(prefix, uri);
        mgr.LookupPrefix(uri, result);
        exit(result);
    end;

    // HasNamespace — true for added, false for unknown
    procedure HasNamespace(prefix: Text; uri: Text): Boolean
    var
        mgr: XmlNamespaceManager;
    begin
        mgr.AddNamespace(prefix, uri);
        exit(mgr.HasNamespace(prefix));
    end;

    // HasNamespace for unknown prefix
    procedure HasNamespaceMissing(prefix: Text): Boolean
    var
        mgr: XmlNamespaceManager;
    begin
        exit(mgr.HasNamespace(prefix));
    end;

    // RemoveNamespace — HasNamespace returns false after removal
    procedure RemoveNamespace(prefix: Text; uri: Text): Boolean
    var
        mgr: XmlNamespaceManager;
    begin
        mgr.AddNamespace(prefix, uri);
        mgr.RemoveNamespace(prefix, uri);
        exit(mgr.HasNamespace(prefix));
    end;

    // PushScope + PopScope — after push/pop the prefix is still accessible (default scope)
    procedure PushPopScope(prefix: Text; uri: Text): Text
    var
        mgr: XmlNamespaceManager;
        result: Text;
    begin
        mgr.AddNamespace(prefix, uri);
        mgr.PushScope();
        mgr.PopScope();
        mgr.LookupNamespace(prefix, result);
        exit(result);
    end;

    // NameTable — must not throw; returns a value (we just check it doesn't error)
    procedure NameTableDoesNotThrow(): Boolean
    var
        mgr: XmlNamespaceManager;
        nt: XmlNameTable;
    begin
        nt := mgr.NameTable();
        exit(true);
    end;

    // Use with SelectNodes namespace-qualified XPath
    procedure SelectWithNs(prefix: Text; uri: Text; elemName: Text): Integer
    var
        doc: XmlDocument;
        root: XmlElement;
        child: XmlElement;
        mgr: XmlNamespaceManager;
        nodeList: XmlNodeList;
    begin
        // Build <root xmlns:ns="uri"><ns:child/></root>
        root := XmlElement.Create(elemName, uri);
        child := XmlElement.Create(elemName, uri);
        root.Add(child);
        doc := XmlDocument.Create();
        doc.Add(root);

        mgr.AddNamespace(prefix, uri);
        doc.SelectNodes('//' + prefix + ':' + elemName, mgr, nodeList);
        exit(nodeList.Count);
    end;
}
