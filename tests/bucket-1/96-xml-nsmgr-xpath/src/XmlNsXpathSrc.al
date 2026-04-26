/// Helper codeunit exercising namespace-aware XPath: SelectNodes(xpath, XmlNamespaceManager, nodeList)
/// and SelectSingleNode(xpath, XmlNamespaceManager, node) on XmlDocument and XmlElement — issue #1371.
codeunit 309400 "XmlNs XPath Src"
{
    // ── XmlDocument.SelectNodes(xpath, nsmgr, nodeList) ──────────────────────

    /// Returns the count of elements found via namespace-qualified XPath.
    /// With correct prefix registered, must find all matching nodes.
    procedure XmlDoc_SelectNodesNs_CountMatch(nsPrefix: Text; nsUri: Text; elemName: Text): Integer
    var
        doc: XmlDocument;
        root: XmlElement;
        child1: XmlElement;
        child2: XmlElement;
        mgr: XmlNamespaceManager;
        nodeList: XmlNodeList;
    begin
        root := XmlElement.Create(elemName, nsUri);
        child1 := XmlElement.Create(elemName, nsUri);
        child2 := XmlElement.Create(elemName, nsUri);
        root.Add(child1);
        root.Add(child2);
        doc := XmlDocument.Create();
        doc.Add(root);

        mgr.AddNamespace(nsPrefix, nsUri);
        doc.SelectNodes('//' + nsPrefix + ':' + elemName, mgr, nodeList);
        exit(nodeList.Count);
    end;

    // ── XmlDocument.SelectSingleNode(xpath, nsmgr, node) ─────────────────────

    /// Returns the local-name of the first matching element found via namespace-aware XPath.
    procedure XmlDoc_SelectSingleNodeNs_LocalName(nsPrefix: Text; nsUri: Text; elemName: Text): Text
    var
        doc: XmlDocument;
        root: XmlElement;
        child: XmlElement;
        mgr: XmlNamespaceManager;
        foundNode: XmlNode;
        foundElem: XmlElement;
    begin
        root := XmlElement.Create('root', nsUri);
        child := XmlElement.Create(elemName, nsUri);
        root.Add(child);
        doc := XmlDocument.Create();
        doc.Add(root);

        mgr.AddNamespace(nsPrefix, nsUri);
        if doc.SelectSingleNode('//' + nsPrefix + ':' + elemName, mgr, foundNode) then begin
            foundElem := foundNode.AsXmlElement();
            exit(foundElem.LocalName);
        end;
        exit('');
    end;

    /// Returns false when XPath prefix is not registered in the namespace manager.
    procedure XmlDoc_SelectSingleNodeNs_UnknownPrefix_ReturnsFalse(nsUri: Text; elemName: Text): Boolean
    var
        doc: XmlDocument;
        root: XmlElement;
        mgr: XmlNamespaceManager;
        foundNode: XmlNode;
    begin
        root := XmlElement.Create(elemName, nsUri);
        doc := XmlDocument.Create();
        doc.Add(root);

        // 'xx' prefix intentionally not registered
        exit(doc.SelectSingleNode('//xx:' + elemName, mgr, foundNode));
    end;

    // ── XmlElement.SelectNodes(xpath, nsmgr, nodeList) ───────────────────────

    /// Returns the count of children found on an XmlElement via namespace-aware XPath.
    procedure XmlElem_SelectNodesNs_CountChildren(nsPrefix: Text; nsUri: Text; childName: Text): Integer
    var
        root: XmlElement;
        child1: XmlElement;
        child2: XmlElement;
        mgr: XmlNamespaceManager;
        nodeList: XmlNodeList;
    begin
        root := XmlElement.Create('root', nsUri);
        child1 := XmlElement.Create(childName, nsUri);
        child2 := XmlElement.Create(childName, nsUri);
        root.Add(child1);
        root.Add(child2);

        mgr.AddNamespace(nsPrefix, nsUri);
        root.SelectNodes(nsPrefix + ':' + childName, mgr, nodeList);
        exit(nodeList.Count);
    end;

    // ── XmlElement.SelectSingleNode(xpath, nsmgr, node) ──────────────────────

    /// Returns the local-name of the first matching child via namespace-aware XPath on XmlElement.
    procedure XmlElem_SelectSingleNodeNs_LocalName(nsPrefix: Text; nsUri: Text; childName: Text): Text
    var
        root: XmlElement;
        child: XmlElement;
        mgr: XmlNamespaceManager;
        foundNode: XmlNode;
        foundElem: XmlElement;
    begin
        root := XmlElement.Create('root', nsUri);
        child := XmlElement.Create(childName, nsUri);
        root.Add(child);

        mgr.AddNamespace(nsPrefix, nsUri);
        if root.SelectSingleNode(nsPrefix + ':' + childName, mgr, foundNode) then begin
            foundElem := foundNode.AsXmlElement();
            exit(foundElem.LocalName);
        end;
        exit('');
    end;

    // ── XmlDeclaration.SelectNodes with XmlNamespaceManager — must return false ─

    /// XmlDeclaration has no child nodes; SelectNodes must return false without throwing.
    procedure XmlDecl_SelectNodesNs_ReturnsFalse(): Boolean
    var
        decl: XmlDeclaration;
        mgr: XmlNamespaceManager;
        nodeList: XmlNodeList;
    begin
        decl := XmlDeclaration.Create('1.0', 'UTF-8', '');
        mgr.AddNamespace('ns', 'http://example.com');
        exit(decl.SelectNodes('//ns:item', mgr, nodeList));
    end;

    /// XmlDeclaration has no child nodes; SelectSingleNode must return false without throwing.
    procedure XmlDecl_SelectSingleNodeNs_ReturnsFalse(): Boolean
    var
        decl: XmlDeclaration;
        mgr: XmlNamespaceManager;
        foundNode: XmlNode;
    begin
        decl := XmlDeclaration.Create('1.0', 'UTF-8', '');
        mgr.AddNamespace('ns', 'http://example.com');
        exit(decl.SelectSingleNode('//ns:item', mgr, foundNode));
    end;

    /// XmlDeclaration SelectNodes as statement (not in 'if' / 'exit') must not throw.
    procedure XmlDecl_SelectNodesNs_AsStatement_NoThrow(): Boolean
    var
        decl: XmlDeclaration;
        mgr: XmlNamespaceManager;
        nodeList: XmlNodeList;
    begin
        decl := XmlDeclaration.Create('1.0', 'UTF-8', '');
        mgr.AddNamespace('ns', 'http://example.com');
        decl.SelectNodes('//ns:item', mgr, nodeList); // statement — BC emits ThrowError DataError
        exit(true);
    end;

    /// XmlDeclaration SelectSingleNode as statement must not throw.
    procedure XmlDecl_SelectSingleNodeNs_AsStatement_NoThrow(): Boolean
    var
        decl: XmlDeclaration;
        mgr: XmlNamespaceManager;
        foundNode: XmlNode;
    begin
        decl := XmlDeclaration.Create('1.0', 'UTF-8', '');
        mgr.AddNamespace('ns', 'http://example.com');
        decl.SelectSingleNode('//ns:item', mgr, foundNode); // statement — BC emits ThrowError DataError
        exit(true);
    end;
}
