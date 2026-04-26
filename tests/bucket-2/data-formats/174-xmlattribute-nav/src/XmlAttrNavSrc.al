/// Helper codeunit for XmlAttribute navigation and namespace methods:
/// NamespacePrefix, IsNamespaceDeclaration, CreateNamespaceDeclaration,
/// WriteTo, GetParent, GetDocument, Remove, AddAfterSelf, AddBeforeSelf,
/// ReplaceWith, SelectNodes, SelectSingleNode.
codeunit 97400 "XAN Src"
{
    // ── NamespacePrefix ───────────────────────────────────────────────────────

    procedure PlainAttrNamespacePrefix(): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('id', 'val');
        exit(attr.NamespacePrefix());
    end;

    // ── IsNamespaceDeclaration ────────────────────────────────────────────────

    procedure PlainAttrIsNsDecl(): Boolean
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.Create('id', 'val');
        exit(attr.IsNamespaceDeclaration());
    end;

    // ── CreateNamespaceDeclaration ────────────────────────────────────────────

    procedure CreateNsDecl(Prefix: Text; Uri: Text): Boolean
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.CreateNamespaceDeclaration(Prefix, Uri);
        exit(attr.IsNamespaceDeclaration());
    end;

    procedure CreateNsDeclLocalName(Prefix: Text; Uri: Text): Text
    var
        attr: XmlAttribute;
    begin
        attr := XmlAttribute.CreateNamespaceDeclaration(Prefix, Uri);
        exit(attr.LocalName);
    end;

    // ── WriteTo ────────────────────────────────────────────────────────────────

    procedure WriteToText(AttrName: Text; AttrValue: Text): Text
    var
        attr: XmlAttribute;
        result: Text;
    begin
        attr := XmlAttribute.Create(AttrName, AttrValue);
        attr.WriteTo(result);
        exit(result);
    end;

    // ── GetParent ─────────────────────────────────────────────────────────────

    procedure DetachedAttrHasNoParent(): Boolean
    var
        attr: XmlAttribute;
        parent: XmlElement;
    begin
        attr := XmlAttribute.Create('id', 'val');
        exit(attr.GetParent(parent));
    end;

    procedure AttachedAttrGetParentName(): Text
    var
        el: XmlElement;
        attr: XmlAttribute;
        parent: XmlElement;
    begin
        el := XmlElement.Create('root');
        attr := XmlAttribute.Create('color', 'blue');
        el.Add(attr);
        if attr.GetParent(parent) then
            exit(parent.Name);
        exit('');
    end;

    // ── GetDocument ───────────────────────────────────────────────────────────

    procedure DetachedAttrHasNoDocument(): Boolean
    var
        attr: XmlAttribute;
        doc: XmlDocument;
    begin
        attr := XmlAttribute.Create('id', 'val');
        exit(attr.GetDocument(doc));
    end;

    // ── Remove ────────────────────────────────────────────────────────────────

    procedure RemoveAttrFromElement(): Boolean
    var
        el: XmlElement;
        attr: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        attr := XmlAttribute.Create('color', 'blue');
        el.Add(attr);
        attr.Remove();
        // After removal, GetParent should return false
        exit(attr.GetParent(el));
    end;

    // ── AddAfterSelf / AddBeforeSelf ──────────────────────────────────────────

    procedure AddAfterSelfAttrCount(): Integer
    var
        el: XmlElement;
        a1: XmlAttribute;
        a2: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        a1 := XmlAttribute.Create('first', '1');
        el.Add(a1);
        a2 := XmlAttribute.Create('second', '2');
        a1.AddAfterSelf(a2);
        exit(el.Attributes().Count());
    end;

    procedure AddBeforeSelfAttrCount(): Integer
    var
        el: XmlElement;
        a1: XmlAttribute;
        a2: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        a1 := XmlAttribute.Create('first', '1');
        el.Add(a1);
        a2 := XmlAttribute.Create('before', '0');
        a1.AddBeforeSelf(a2);
        exit(el.Attributes().Count());
    end;

    // ── ReplaceWith ───────────────────────────────────────────────────────────

    procedure ReplaceWithChangesName(): Text
    var
        el: XmlElement;
        old: XmlAttribute;
        newAttr: XmlAttribute;
        result: XmlAttribute;
    begin
        el := XmlElement.Create('root');
        old := XmlAttribute.Create('old', 'v');
        el.Add(old);
        newAttr := XmlAttribute.Create('new', 'v');
        old.ReplaceWith(newAttr);
        if el.Attributes().Get('new', result) then
            exit(result.Name);
        exit('');
    end;

    // ── SelectNodes ───────────────────────────────────────────────────────────

    procedure SelectNodesOnAttr(): Boolean
    var
        attr: XmlAttribute;
        nodeList: XmlNodeList;
    begin
        attr := XmlAttribute.Create('id', 'val');
        exit(attr.SelectNodes('.', nodeList));
    end;

    // ── SelectSingleNode ──────────────────────────────────────────────────────

    procedure SelectSingleNodeOnAttr(): Boolean
    var
        attr: XmlAttribute;
        node: XmlNode;
    begin
        attr := XmlAttribute.Create('id', 'val');
        exit(attr.SelectSingleNode('.', node));
    end;
}
