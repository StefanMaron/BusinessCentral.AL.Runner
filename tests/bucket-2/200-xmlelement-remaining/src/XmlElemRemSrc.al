/// Helper codeunit for XmlElement remaining methods:
/// AddAfterSelf, AddBeforeSelf, GetDescendantElements, GetDescendantNodes,
/// GetDocument, GetNamespaceOfPrefix, GetPrefixOfNamespace, NamespaceUri,
/// ReplaceNodes, ReplaceWith.
codeunit 97600 "XER Src"
{
    // ── NamespaceUri ──────────────────────────────────────────────────────────

    procedure PlainElemNamespaceUri(): Text
    var
        el: XmlElement;
    begin
        el := XmlElement.Create('root');
        exit(el.NamespaceUri());
    end;

    procedure NamespacedElemUri(): Text
    var
        el: XmlElement;
    begin
        // XmlElement.Create(prefix, namespaceUri, localName)
        el := XmlElement.Create('ns', 'http://example.com', 'root');
        exit(el.NamespaceUri());
    end;

    // ── GetDocument ───────────────────────────────────────────────────────────

    procedure DetachedElemHasNoDocument(): Boolean
    var
        el: XmlElement;
        doc: XmlDocument;
    begin
        el := XmlElement.Create('root');
        exit(el.GetDocument(doc));
    end;

    // ── GetDescendantElements ─────────────────────────────────────────────────

    procedure LeafDescendantElementsCount(): Integer
    var
        el: XmlElement;
        nodes: XmlNodeList;
    begin
        el := XmlElement.Create('root');
        nodes := el.GetDescendantElements();
        exit(nodes.Count());
    end;

    procedure NestedDescendantElementsCount(): Integer
    var
        root: XmlElement;
        child: XmlElement;
        grandchild: XmlElement;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        child := XmlElement.Create('child');
        grandchild := XmlElement.Create('grandchild');
        child.Add(grandchild);
        root.Add(child);
        nodes := root.GetDescendantElements();
        exit(nodes.Count());
    end;

    // ── GetDescendantNodes ────────────────────────────────────────────────────

    procedure LeafDescendantNodesCount(): Integer
    var
        el: XmlElement;
        nodes: XmlNodeList;
    begin
        el := XmlElement.Create('root');
        nodes := el.GetDescendantNodes();
        exit(nodes.Count());
    end;

    // ── GetNamespaceOfPrefix / GetPrefixOfNamespace ───────────────────────────

    procedure NamespaceOfXmlPrefix(): Text
    var
        el: XmlElement;
        result: Text;
    begin
        // 'xml' prefix is always defined (http://www.w3.org/XML/1998/namespace)
        el := XmlElement.Create('root');
        el.GetNamespaceOfPrefix('xml', result);
        exit(result);
    end;

    procedure PrefixOfUnknownNamespace(): Text
    var
        el: XmlElement;
        result: Text;
    begin
        el := XmlElement.Create('root');
        el.GetPrefixOfNamespace('http://unknown.example.com', result);
        exit(result);
    end;

    // ── ReplaceNodes ──────────────────────────────────────────────────────────

    procedure ReplaceNodesChildCount(): Integer
    var
        root: XmlElement;
        child: XmlElement;
        newChild: XmlElement;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        child := XmlElement.Create('old');
        root.Add(child);
        newChild := XmlElement.Create('new');
        root.ReplaceNodes(newChild.AsXmlNode());
        nodes := root.GetChildNodes();
        exit(nodes.Count());
    end;

    // ── ReplaceWith ───────────────────────────────────────────────────────────

    procedure ReplaceWithNewName(): Text
    var
        parent: XmlElement;
        old: XmlElement;
        newEl: XmlElement;
        nodes: XmlNodeList;
        node: XmlNode;
        el: XmlElement;
    begin
        parent := XmlElement.Create('parent');
        old := XmlElement.Create('old');
        parent.Add(old);
        newEl := XmlElement.Create('new');
        old.ReplaceWith(newEl.AsXmlNode());
        nodes := parent.GetChildNodes();
        if nodes.Count() = 1 then begin
            nodes.Get(1, node);
            if node.IsXmlElement() then
                el := node.AsXmlElement();
            exit(el.Name);
        end;
        exit('');
    end;

    // ── AddAfterSelf / AddBeforeSelf ──────────────────────────────────────────

    procedure AddAfterSelfChildCount(): Integer
    var
        parent: XmlElement;
        first: XmlElement;
        second: XmlElement;
        nodes: XmlNodeList;
    begin
        parent := XmlElement.Create('parent');
        first := XmlElement.Create('first');
        parent.Add(first);
        second := XmlElement.Create('second');
        first.AddAfterSelf(second.AsXmlNode());
        nodes := parent.GetChildNodes();
        exit(nodes.Count());
    end;

    procedure AddBeforeSelfChildCount(): Integer
    var
        parent: XmlElement;
        first: XmlElement;
        before: XmlElement;
        nodes: XmlNodeList;
    begin
        parent := XmlElement.Create('parent');
        first := XmlElement.Create('first');
        parent.Add(first);
        before := XmlElement.Create('before');
        first.AddBeforeSelf(before.AsXmlNode());
        nodes := parent.GetChildNodes();
        exit(nodes.Count());
    end;
}
